#!/usr/bin/env python3
"""Benchmark QuickPlayer's complete SAM2 initial + two-correction workflow."""
import argparse, json, math, os, queue, shutil, statistics, subprocess, tempfile
import threading, time
from pathlib import Path

from sam2_refine_worker import POLICY_SCHEMA, atomic_json, policy_key

PROGRESS_PREFIX = "IQP_BENCHMARK_PROGRESS "


def progress(completed, total, message):
    print(PROGRESS_PREFIX + json.dumps({"completed": completed,
        "total": max(1, total), "message": message}, separators=(",", ":")),
        flush=True)


MODES = {
    "eager": ("eager", "max-autotune-no-cudagraphs", 0),
    "feature-cache-1": ("eager", "max-autotune-no-cudagraphs", 1),
    "feature-cache-4": ("eager", "max-autotune-no-cudagraphs", 4),
    "feature-cache-8": ("eager", "max-autotune-no-cudagraphs", 8),
    "feature-cache-16": ("eager", "max-autotune-no-cudagraphs", 16),
    "feature-cache-64": ("eager", "max-autotune-no-cudagraphs", 64),
    "feature-cache-256": ("eager", "max-autotune-no-cudagraphs", 256),
    "encoder-max-autotune": ("encoder", "max-autotune", 0),
    "encoder-no-cudagraphs": ("encoder", "max-autotune-no-cudagraphs", 0),
    "vos-max-autotune": ("vos", "max-autotune", 0),
    "vos-no-cudagraphs": ("vos", "max-autotune-no-cudagraphs", 0),
}


def stream_errors(stream, target):
    for line in stream: target.put(line.rstrip())


def correction_point(mask_path):
    import numpy as np
    from PIL import Image
    mask = np.asarray(Image.open(mask_path).convert("L")) >= 128
    ys, xs = np.nonzero(mask)
    if len(xs) == 0: raise RuntimeError(f"No foreground in {mask_path}")
    middle = len(xs) // 2
    return [float(xs[middle]), float(ys[middle])]


def parse_profile(path):
    if not path.is_file(): return {}
    records = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()
               if line.strip()]
    return records[-1] if records else {}


def compare_masks(reference, candidate):
    import numpy as np
    from PIL import Image
    intersections = unions = changed = pixels = 0
    for left in sorted(reference.glob("*.png")):
        right = candidate / left.name
        if not right.is_file(): return {"iou": 0, "changedFraction": 1}
        a = np.asarray(Image.open(left).convert("L")) >= 128
        b = np.asarray(Image.open(right).convert("L")) >= 128
        intersections += np.logical_and(a, b).sum()
        unions += np.logical_or(a, b).sum()
        changed += np.not_equal(a, b).sum(); pixels += a.size
    return {"iou": float(intersections / max(1, unions)),
            "changedFraction": float(changed / max(1, pixels))}


def generate_fixtures(args, root):
    import numpy as np
    from PIL import Image
    fixtures = []
    for name, width, height in (("1080p", 1920, 1080), ("4k", 3840, 2160)):
        folder = root / name; folder.mkdir(parents=True, exist_ok=True)
        video, mask = folder / "source.mp4", folder / "mask.png"
        duration = args.frames / args.fps
        if not video.is_file() or not mask.is_file():
            subprocess.run([str(args.ffmpeg), "-y", "-v", "error", "-f", "lavfi",
                "-i", f"testsrc2=size={width}x{height}:rate={args.fps}:duration={duration}",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                str(video)], check=True)
            Image.new("L", (width, height), 0).save(mask)
            image = Image.open(mask); array = np.asarray(image).copy()
            array[height // 5:height * 4 // 5, width // 3:width * 2 // 3] = 255
            Image.fromarray(array, "L").save(mask)
        fixtures.append({"name": name, "source": video, "mask": mask,
                         "startMs": 0, "endMs": round(duration * 1000)})
    return fixtures


def load_fixtures(args, root):
    if args.generate_fixtures: return generate_fixtures(args, root / "fixtures")
    if args.fixtures:
        values = json.loads(args.fixtures.read_text(encoding="utf-8"))
        return [{**value, "source": Path(value["source"]), "mask": Path(value["mask"])}
                for value in values]
    if not args.source or not args.mask:
        raise RuntimeError("Provide --source/--mask, --fixtures, or --generate-fixtures")
    return [{"name": "source", "source": args.source, "mask": args.mask,
             "startMs": args.start_ms, "endMs": args.end_ms}]


def run_once(args, fixture, model, mode, repetition, output_root, extraction_mode):
    execution, compile_mode, feature_cache_frames = MODES[mode]
    run_root = output_root / fixture["name"] / model / extraction_mode / mode / \
        f"run-{repetition + 1}"
    frames, masks = run_root / "frames", run_root / "masks"
    shutil.rmtree(run_root, ignore_errors=True); run_root.mkdir(parents=True)
    profile = run_root / "processing.log"
    environment = os.environ.copy()
    environment.update({"IQP_FFMPEG": str(args.ffmpeg.resolve()),
        "IQP_FFPROBE": str(args.ffprobe.resolve()),
        "IQP_SAM2_COMPILE_MODE": compile_mode})
    if args.cold_first and repetition == 0 and execution != "eager":
        environment["TORCHINDUCTOR_CACHE_DIR"] = str(run_root / "cold-inductor-cache")
    command = [str(args.python.resolve()), str(args.worker.resolve()),
        "--source", str(Path(fixture["source"]).resolve()),
        "--runtime", str(args.runtime.resolve()), "--mask", str(Path(fixture["mask"]).resolve()),
        "--frames", str(frames), "--masks", str(masks),
        "--start-ms", str(fixture["startMs"]), "--end-ms", str(fixture["endMs"]),
        "--model", model, "--execution", execution, "--profile-log", str(profile),
        "--detailed-profile", "--gpu-feature-cache-frames", str(feature_cache_frames),
        "--extraction-mode", extraction_mode]
    if args.frame_cache_gb > 0:
        command.extend(["--cache-root", str(output_root / "frame-cache" / model),
            "--cache-limit-bytes", str(round(args.frame_cache_gb * 1024 ** 3))])
    errors, started = queue.Queue(), time.perf_counter()
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, text=True, bufsize=1, env=environment)
    threading.Thread(target=stream_errors, args=(process.stderr, errors), daemon=True).start()
    ready_times, preview_seconds, correction_seconds, correction_index = [], [], [], 0
    correction_frames = []
    prompt_started = update_started = None
    last_report = started
    try:
        for raw in process.stdout:
            now = time.perf_counter()
            try: event = json.loads(raw)
            except json.JSONDecodeError: continue
            if event.get("status") == "error": raise RuntimeError(event.get("message"))
            if now - last_report >= 15:
                print(f"[{fixture['name']}/{model}/{mode}/run {repetition + 1}] "
                      f"{event.get('message', event.get('status'))}; {now-started:.1f}s", flush=True)
                last_report = now
            if event.get("status") == "ready":
                ready_times.append(now)
                if len(ready_times) > 1 and correction_seconds[len(ready_times) - 2] < 0:
                    correction_seconds[len(ready_times) - 2] += now
                if len(ready_times) == 1:
                    total = int(event["frameCount"])
                    correction_frames = [max(1, total // 3), max(1, total * 2 // 3)]
                if correction_index >= len(correction_frames):
                    process.stdin.write('{"command":"quit"}\n'); process.stdin.flush(); break
                frame = correction_frames[correction_index]
                point = list(args.fixed_correction_point) if args.fixed_correction_point \
                    else correction_point(masks / f"{frame + 1:08d}.png")
                prompt_started = time.perf_counter()
                process.stdin.write(json.dumps({"command": "prompt", "frame": frame,
                    "points": [point], "labels": [1]}) + "\n"); process.stdin.flush()
            elif event.get("status") == "preview":
                preview_seconds.append(now - prompt_started)
                update_started = time.perf_counter()
                process.stdin.write(json.dumps({"command": "update",
                    "frame": correction_frames[correction_index]}) + "\n")
                process.stdin.flush(); correction_index += 1
                correction_seconds.append(-update_started)
        process.stdin.close(); exit_code = process.wait(timeout=120)
        if exit_code:
            raise RuntimeError("\n".join(list(errors.queue)[-30:]))
        worker_profile = parse_profile(profile)
        result = {"fixture": fixture["name"], "model": model, "mode": mode,
            "execution": execution, "compileMode": compile_mode, "repetition": repetition + 1,
            "extractionMode": extraction_mode,
            "initialSeconds": ready_times[0] - started,
            "correctionSeconds": correction_seconds,
            "promptSeconds": preview_seconds,
            "totalSeconds": time.perf_counter() - started,
            "maskFolder": str(masks), "profile": worker_profile}
        print(json.dumps({key: value for key, value in result.items()
                          if key != "profile"}, indent=2), flush=True)
        return result
    finally:
        if process.poll() is None: process.kill(); process.wait()


def median(values): return statistics.median(values) if values else math.inf


def correction_rate(runs):
    rates = []
    for item in runs:
        profile = item.get("profile", {})
        propagated = max(0, int(profile.get("propagatedFrames", 0)) -
                         int(profile.get("frames", 0)))
        seconds = sum(max(0.0, float(value))
                      for value in item.get("correctionSeconds", []))
        if propagated > 0 and seconds > 0:
            rates.append(seconds / propagated)
    return statistics.median(rates) if rates else math.inf


def write_policy(args, results):
    import torch
    entries = {}
    for model in args.models:
        eager = [item for item in results if item["model"] == model and
                 item["mode"] == "eager" and item["extractionMode"] == "standard"]
        if not eager: continue
        eager_total = statistics.median(item["totalSeconds"] for item in eager)
        eager_correction_rate = correction_rate(eager)
        entry = {"expectedWorkMultiplier": 3.0}
        standard_uncached = [item for item in eager if not
            item["profile"].get("frameCacheHit", False)]
        extraction_variants = []
        if standard_uncached:
            standard_startup = statistics.median(
                item["initialSeconds"] for item in standard_uncached)
            for extraction in ("bicubic", "bilinear", "jpeg3", "nvdec"):
                runs = [item for item in results if item["model"] == model and
                    item["mode"] == "eager" and item["extractionMode"] == extraction and
                    not item["profile"].get("frameCacheHit", False)]
                if not runs: continue
                startup = statistics.median(item["initialSeconds"] for item in runs)
                speedup = (standard_startup - startup) / standard_startup
                equivalent = all(item.get("maskEquivalence", {}).get("iou", 0) >= .9999
                    and item.get("maskEquivalence", {}).get("changedFraction", 1) <= .0001
                    for item in runs)
                extraction_variants.append((speedup, extraction, equivalent))
        if extraction_variants:
            speedup, extraction, equivalent = max(extraction_variants)
            entry["extraction"] = {"enabled": speedup >= .10 and equivalent,
                "mode": extraction, "uncachedStartupSpeedup": speedup,
                "maskEquivalent": equivalent}
        feature_variants = []
        for feature_mode, (_, _, frames) in MODES.items():
            if frames <= 0: continue
            feature_runs = [item for item in results if item["model"] == model and
                            item["mode"] == feature_mode and
                            item["extractionMode"] == "standard"]
            # A cache-size screen is intentionally allowed to use one run, but a
            # noisy single timing must never become a production policy entry.
            if len(feature_runs) < 3: continue
            feature_total = statistics.median(
                item["totalSeconds"] for item in feature_runs)
            speedup = (eager_total - feature_total) / eager_total
            equivalent = all(item.get("maskEquivalence", {}).get("iou", 1) >= .9999
                and item.get("maskEquivalence", {}).get("changedFraction", 0) <= .0001
                for item in feature_runs)
            headroom = all(item["profile"].get("totalVramBytes", 0) -
                item["profile"].get("peakVramBytes", 2 ** 63) >= 4 * 1024 ** 3
                for item in feature_runs)
            feature_variants.append((speedup, {"enabled": speedup >= .02 and
                equivalent and headroom, "warmSpeedup": speedup, "frames": frames,
                "maskEquivalent": equivalent, "fourGiBHeadroom": headroom}))
        if feature_variants:
            eligible = [value for value in feature_variants if value[1]["enabled"]]
            entry["gpuFeatureCache"] = max(eligible or feature_variants,
                key=lambda value: value[0])[1]
        for execution in ("encoder", "vos"):
            variants = []
            for mode in MODES:
                if MODES[mode][0] != execution or MODES[mode][2] > 0: continue
                runs = [item for item in results if item["model"] == model and item["mode"] == mode]
                runs = [item for item in runs if item["extractionMode"] == "standard"]
                if not runs: continue
                warm = runs[1:] or runs
                total = statistics.median(item["totalSeconds"] for item in warm)
                benchmark_speedup = (eager_total - total) / eager_total
                equivalent = all(item.get("maskEquivalence", {}).get("iou", 1) >= .9999
                    and item.get("maskEquivalence", {}).get("changedFraction", 0) <= .0001
                    for item in runs)
                compiled_rate = correction_rate(warm)
                speedup = ((eager_correction_rate - compiled_rate) /
                    eager_correction_rate if math.isfinite(eager_correction_rate) and
                    eager_correction_rate > 0 and math.isfinite(compiled_rate) else -1.0)
                startup = max(0, statistics.median(item["initialSeconds"] for item in warm) -
                              statistics.median(item["initialSeconds"] for item in eager))
                saving = eager_correction_rate - compiled_rate
                break_even = math.ceil(startup / saving) if saving > 0 else 2 ** 31 - 1
                variants.append((speedup, {"enabled": speedup >= .10 and equivalent,
                    "warmSpeedup": speedup, "steadyCorrectionSpeedup": speedup,
                    "benchmarkEndToEndSpeedup": benchmark_speedup,
                    "cachedStartupOverheadSeconds": startup,
                    "breakEvenFrames": break_even,
                    "maskEquivalent": equivalent,
                    "compileMode": MODES[mode][1], "measuredUtc":
                    time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}))
            if variants: entry[execution] = max(variants, key=lambda value: value[0])[1]
        entries[policy_key(torch, "cuda" if torch.cuda.is_available() else "cpu", model)] = entry
    path = args.runtime / "sam2-performance-policy-v1.json"
    atomic_json(path, {"schemaVersion": POLICY_SCHEMA, "entries": entries})
    print(f"Performance policy: {path}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--python", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--ffprobe", type=Path, required=True)
    parser.add_argument("--source", type=Path); parser.add_argument("--mask", type=Path)
    parser.add_argument("--fixtures", type=Path); parser.add_argument("--generate-fixtures",
                        action="store_true")
    parser.add_argument("--frames", type=int, default=1000); parser.add_argument("--fps", type=int, default=30)
    parser.add_argument("--start-ms", type=int, default=0); parser.add_argument("--end-ms", type=int, default=33333)
    parser.add_argument("--models", nargs="+", choices=("base-plus", "small", "tiny"),
                        default=["base-plus", "small", "tiny"])
    parser.add_argument("--modes", nargs="+", choices=tuple(MODES), default=list(MODES))
    parser.add_argument("--repetitions", type=int, default=3)
    parser.add_argument("--cold-first", action="store_true")
    parser.add_argument("--write-policy", action="store_true")
    parser.add_argument("--frame-cache-gb", type=float, default=10)
    parser.add_argument("--extraction-modes", nargs="+",
        choices=("standard", "bicubic", "bilinear", "jpeg3", "nvdec"),
        default=["standard"])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--fixed-correction-point", nargs=2, type=float,
        metavar=("X", "Y"), help="use the same review-frame correction point in every run")
    parser.add_argument("--resume", action="store_true",
        help="reuse fixtures/results and skip completed variant repetitions")
    args = parser.parse_args()
    if args.frames < 2 or args.repetitions < 1 or args.frame_cache_gb < 0:
        parser.error("invalid frame/repetition/cache size")
    if args.write_policy and args.fixed_correction_point is None:
        parser.error("--write-policy requires --fixed-correction-point so every variant uses identical prompts")
    output = args.output or Path(tempfile.mkdtemp(prefix="iqp-sam2-benchmark-"))
    output.mkdir(parents=True, exist_ok=True)
    if "standard" in args.extraction_modes:
        args.extraction_modes = ["standard"] + [value for value in
            args.extraction_modes if value != "standard"]
    fixtures = load_fixtures(args, output)
    results_path = output / "results.json"
    results = json.loads(results_path.read_text(encoding="utf-8")) \
        if args.resume and results_path.is_file() else []
    completed = {(item["fixture"], item["model"], item["extractionMode"],
                  item["mode"], item["repetition"]) for item in results}
    total_runs = len(fixtures) * len(args.models) * len(args.extraction_modes) * \
        len(args.modes) * args.repetitions
    completed_runs = len(completed)
    for fixture in fixtures:
        for model in args.models:
            existing_reference = next((item for item in results
                if item["fixture"] == fixture["name"] and item["model"] == model and
                item["extractionMode"] == "standard" and item["mode"] == "eager" and
                item["repetition"] == 1), None)
            reference = Path(existing_reference["maskFolder"]) \
                if existing_reference else None
            for extraction_mode in args.extraction_modes:
                for mode in args.modes:
                    for repetition in range(args.repetitions):
                        key = (fixture["name"], model, extraction_mode, mode,
                               repetition + 1)
                        if key in completed:
                            print(f"Skipping completed {key}", flush=True)
                            continue
                        description = (f"SAM2 {model}, {mode}, {extraction_mode}, "
                            f"run {repetition + 1}/{args.repetitions}")
                        progress(completed_runs, total_runs, description)
                        result = run_once(args, fixture, model, mode, repetition,
                                          output, extraction_mode)
                        masks = Path(result["maskFolder"])
                        if extraction_mode == "standard" and mode == "eager" and \
                                repetition == 0:
                            reference = masks
                        elif reference is not None:
                            result["maskEquivalence"] = compare_masks(reference, masks)
                        results.append(result)
                        atomic_json(results_path, results)
                        completed_runs += 1
                        progress(completed_runs, total_runs, description + " complete")
    if args.write_policy: write_policy(args, results)
    print(f"Results: {results_path}")


if __name__ == "__main__": main()
