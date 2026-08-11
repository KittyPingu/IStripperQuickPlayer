#!/usr/bin/env python3
"""Manual end-to-end ViTMatte batch/compile benchmark for QuickPlayer."""
import argparse
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

PROGRESS_PREFIX = "IQP_BENCHMARK_PROGRESS "
PROGRESS = {"completed": 0, "total": 1}


def say(message):
    print(message, flush=True)


def progress(message, finished=False):
    if finished:
        PROGRESS["completed"] += 1
    say(PROGRESS_PREFIX + json.dumps({"completed": PROGRESS["completed"],
        "total": PROGRESS["total"], "message": message}, separators=(",", ":")))


def skip_progress(count, message):
    PROGRESS["completed"] += max(0, count)
    progress(message)


class VramPressureError(RuntimeError):
    pass


def gpu_memory_mib():
    executable = shutil.which("nvidia-smi")
    if executable is None:
        return None
    creation_flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    try:
        output = subprocess.check_output([executable,
            "--query-gpu=memory.total,memory.used", "--format=csv,noheader,nounits"],
            text=True, timeout=5, creationflags=creation_flags)
        total, used = (int(value.strip())
            for value in output.splitlines()[0].split(",")[:2])
        return total, used
    except (OSError, subprocess.SubprocessError, ValueError, IndexError):
        return None


def stop_process_tree(process):
    if process.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            creationflags=subprocess.CREATE_NO_WINDOW)
    elif process.poll() is None:
        process.kill()


def run_worker(args, model, source, masks, output, batch, execution, profile,
               channels_last="off", explicit_half="off", monitor_vram=False):
    description = (f"ViTMatte-{model.upper()} {execution}, batch {batch}: "
        f"{output.name}")
    progress(description)
    shutil.rmtree(output, ignore_errors=True)
    output.mkdir(parents=True)
    command = [sys.executable, str(args.worker), "--runtime", str(args.runtime),
        "--source", str(source), "--mask-folder", str(masks), "--output", str(output),
        "--model", model, "--batch-size", str(batch), "--execution-mode", execution,
        "--compile-cutoff-frames", "1", "--profile", "--profile-output", str(profile),
        "--no-preview", "--channels-last", channels_last,
        "--explicit-half", explicit_half]
    environment = os.environ.copy()
    environment["IQP_FFMPEG"] = str(args.ffmpeg)
    environment["IQP_FFPROBE"] = str(args.ffprobe)
    started = time.perf_counter()
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, bufsize=1, env=environment)
    monitor_stop = threading.Event()
    pressure = {}

    def watch_vram():
        while not monitor_stop.wait(.5):
            memory = gpu_memory_mib()
            if memory is None:
                continue
            total, used = memory
            if total > 0 and used / total >= args.vram_stop_percent / 100:
                pressure.update(total=total, used=used)
                stop_process_tree(process)
                return

    monitor = None
    if monitor_vram and args.vram_stop_percent > 0:
        monitor = threading.Thread(target=watch_vram,
            name="vitmatte-vram-monitor", daemon=True)
        monitor.start()
    tail = []
    try:
        for line in process.stdout:
            line = line.rstrip()
            if line:
                tail.append(line); tail = tail[-30:]
            if time.perf_counter() - started > args.worker_timeout:
                raise TimeoutError(f"exceeded {args.worker_timeout:g}s")
    except TimeoutError as error:
        stop_process_tree(process)
        process.wait()
        # Windows can retain FFmpeg output handles briefly after taskkill returns.
        time.sleep(1)
        raise RuntimeError(f"ViTMatte benchmark worker timed out: {error}")
    finally:
        monitor_stop.set()
        if monitor is not None:
            monitor.join(timeout=2)
    exit_code = process.wait()
    if pressure:
        used, total = pressure["used"], pressure["total"]
        raise VramPressureError(f"whole-GPU dedicated memory reached {used:,}/"
            f"{total:,} MiB ({used * 100 / total:.1f}%); shared-memory spill avoided")
    if exit_code != 0:
        raise RuntimeError("ViTMatte benchmark worker failed:\n" + "\n".join(tail))
    value = json.loads(profile.read_text(encoding="utf-8"))
    value["wallSeconds"] = time.perf_counter() - started
    progress(description + " complete", finished=True)
    return value


def make_masks(mask, folder, frames):
    folder.mkdir(parents=True, exist_ok=True)
    for index in range(frames):
        target = folder / f"{index + 1:08d}.png"
        try:
            os.link(mask, target)
        except OSError:
            shutil.copyfile(mask, target)


def trim_source(args, source, output, frames):
    fps_text = subprocess.check_output([str(args.ffprobe), "-v", "error",
        "-select_streams", "v:0", "-show_entries", "stream=avg_frame_rate",
        "-of", "default=nw=1:nk=1", str(source)], text=True).strip()
    numerator, denominator = (int(value) for value in fps_text.split("/"))
    fps = numerator / denominator
    subprocess.run([str(args.ffmpeg), "-y", "-v", "error", "-i", str(source),
        "-t", f"{frames / fps:.9f}", "-an", "-c:v", "libx264", "-preset", "veryfast",
        "-pix_fmt", "yuv420p", str(output)], check=True)
    return fps


def compare_alpha(args, first, second):
    import numpy as np
    width_height = subprocess.check_output([str(args.ffprobe), "-v", "error",
        "-select_streams", "v:0", "-show_entries", "stream=width,height",
        "-of", "csv=p=0:s=x", str(first)], text=True).strip()
    width, height = (int(value) for value in width_height.split("x"))
    frame_bytes = width * height
    commands = [[str(args.ffmpeg), "-v", "error", "-i", str(path), "-map", "0:v:0",
        "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"] for path in (first, second)]
    processes = [subprocess.Popen(command, stdout=subprocess.PIPE) for command in commands]
    intersection = union = absolute = pixels = 0
    try:
        while True:
            values = [process.stdout.read(frame_bytes) for process in processes]
            if not values[0] and not values[1]: break
            if len(values[0]) != frame_bytes or len(values[1]) != frame_bytes:
                raise RuntimeError("Alpha comparison streams ended at different frames")
            a, b = (np.frombuffer(value, np.uint8) for value in values)
            aa, bb = a >= 128, b >= 128
            intersection += int(np.count_nonzero(aa & bb))
            union += int(np.count_nonzero(aa | bb))
            absolute += int(np.abs(a.astype(np.int16) - b.astype(np.int16)).sum())
            pixels += frame_bytes
    finally:
        for process in processes:
            if process.poll() is None: process.kill()
    return {"iou": intersection / union if union else 1.0,
            "meanAbsoluteError": absolute / pixels if pixels else 0.0}


def median(values):
    ordered = sorted(values)
    return ordered[len(ordered) // 2]


def benchmark_batches(args, model, source, masks, work):
    candidates = (args.small_batches if model == "s" else args.base_batches)
    batch_results = []
    say(f"ViTMatte-{model.upper()}: testing eager batch sizes {candidates} "
        f"({args.batch_runs} runs each)...")
    for candidate_index, batch in enumerate(candidates):
        runs = []
        try:
            for run in range(args.batch_runs):
                result = run_worker(args, model, source, masks,
                    work / f"{model}-batch-{batch}-{run}", batch, "eager",
                    work / f"{model}-batch-{batch}-{run}.json", monitor_vram=True)
                runs.append(result)
                if result["batchSizeUsed"] != batch:
                    raise VramPressureError(f"requested batch {batch} fell back to "
                        f"batch {result['batchSizeUsed']} after a GPU out-of-memory error")
            used_sizes = sorted({value["batchSizeUsed"] for value in runs})
            fps = median([value["fps"] for value in runs])
            seconds = median([value["totalSeconds"] for value in runs])
            peak_vram = max(value["peakVramBytes"] for value in runs)
            stable = used_sizes == [batch]
            batch_results.append({"batchSizeRequested": batch,
                "batchSizeUsed": used_sizes, "medianFps": fps,
                "medianSeconds": seconds, "peakVramBytes": peak_vram,
                "stableAtRequestedSize": stable, "runs": runs})
            suffix = "" if stable else f"; OOM fallback used {used_sizes}"
            say(f"  batch {batch}: median {fps:.2f} fps, "
                f"{peak_vram / 1073741824:.2f} GiB peak VRAM{suffix}")
        except VramPressureError as error:
            skipped = args.batch_runs - len(runs) + \
                (len(candidates) - candidate_index - 1) * args.batch_runs
            skip_progress(skipped, f"ViTMatte-{model.upper()}: skipped unsafe "
                f"batch {batch} and all larger batches")
            batch_results.append({"batchSizeRequested": batch,
                "stableAtRequestedSize": False, "rejectedForVramPressure": True,
                "reason": str(error), "runs": runs})
            say(f"  batch {batch}: stopped ({error}).")
            remaining = tuple(candidates[candidate_index + 1:])
            if remaining:
                say(f"  skipping larger batches {remaining}; their memory demand "
                    "cannot be safer than the rejected batch.")
            break
        except RuntimeError as error:
            say(f"  batch {batch}: unavailable ({str(error).splitlines()[-1]})")
    stable_results = [value for value in batch_results
                      if value["stableAtRequestedSize"]]
    if not stable_results:
        raise RuntimeError(f"No ViTMatte-{model.upper()} batch size completed "
                           "without OOM fallback")
    best = max(stable_results, key=lambda value: value["medianFps"])
    say(f"ViTMatte-{model.upper()}: selected batch {best['batchSizeRequested']} "
        f"at {best['medianFps']:.2f} median fps.")
    return best["batchSizeRequested"], batch_results


def benchmark_model(args, model, source, masks, work):
    best_batch, batch_results = benchmark_batches(args, model, source, masks, work)
    say(f"ViTMatte-{model.upper()}: selected batch {best_batch}; measuring eager and compile...")
    eager = [run_worker(args, model, source, masks, work / f"{model}-eager-{run}",
        best_batch, "eager", work / f"{model}-eager-{run}.json")
        for run in range(args.runs)]
    eager_seconds = median([value["totalSeconds"] for value in eager])
    reference_alpha = work / f"{model}-eager-{args.runs - 1}" / "alpha.mkv"
    variants = []
    for name, channels_last, explicit_half in (
            ("channels-last", "on", "off"), ("explicit-half", "off", "on")):
        try:
            value = run_worker(args, model, source, masks, work / f"{model}-{name}",
                best_batch, "eager", work / f"{model}-{name}.json",
                channels_last, explicit_half)
            equivalent = compare_alpha(args, reference_alpha,
                work / f"{model}-{name}" / "alpha.mkv")
            variant_gain = (eager_seconds - value["totalSeconds"]) / eager_seconds
            accepted_variant = variant_gain >= .05 and equivalent["iou"] >= .9999
            variants.append((value["totalSeconds"], name, channels_last,
                             explicit_half, accepted_variant, equivalent, value))
            say(f"  {name}: {value['totalSeconds']:.2f}s, gain "
                f"{variant_gain * 100:.1f}%, alpha IoU {equivalent['iou']:.6f}" +
                (" (accepted)" if accepted_variant else " (not enabled)"))
        except RuntimeError as error:
            say(f"  {name}: unavailable ({str(error).splitlines()[-1]})")
    accepted_variants = [value for value in variants if value[4]]
    if accepted_variants:
        selected_seconds, variant_name, selected_channels, selected_half, _, _, _ = \
            min(accepted_variants)
        say(f"  selected additional optimization: {variant_name}")
    else:
        selected_channels = selected_half = "off"
        selected_seconds = eager_seconds
    compiled = [run_worker(args, model, source, masks, work / f"{model}-compile-{run}",
        best_batch, "compile", work / f"{model}-compile-{run}.json",
        selected_channels, selected_half)
        for run in range(args.runs)]
    cached_seconds = median([value["totalSeconds"] for value in compiled[1:] or compiled])
    cold_seconds = compiled[0]["totalSeconds"]
    saving = (selected_seconds - cached_seconds) / args.frames
    compile_cost = max(0.0, cold_seconds - cached_seconds)
    gain = ((selected_seconds - cached_seconds) / selected_seconds
            if selected_seconds else 0.0)
    break_even = math.ceil(1.2 * compile_cost / saving) if saving > 0 else args.default_cutoff
    equivalence = compare_alpha(args,
        work / f"{model}-eager-{args.runs - 1}" / "alpha.mkv",
        work / f"{model}-compile-{args.runs - 1}" / "alpha.mkv")
    accepted = gain >= args.minimum_gain / 100 and equivalence["iou"] >= .9999
    say(f"  eager median {eager_seconds:.2f}s; selected eager path "
        f"{selected_seconds:.2f}s; cached compile {cached_seconds:.2f}s; "
        f"compile gain {gain * 100:.1f}%; alpha IoU {equivalence['iou']:.6f}")
    result = {"model": model, "batchSize": best_batch, "eagerSeconds": eager_seconds,
        "compiledColdSeconds": cold_seconds, "compiledCachedSeconds": cached_seconds,
        "selectedEagerSeconds": selected_seconds,
        "gainPercent": gain * 100, "breakEvenFrames": max(1, break_even),
        "compiledEnabled": accepted, "equivalence": equivalence,
        "policyKey": eager[-1]["policyKey"],
        "channelsLast": selected_channels == "on",
        "explicitHalf": selected_half == "on",
        "memoryFormatCandidates": [{"name": value[1], "accepted": value[4],
            "equivalence": value[5], "profile": value[6]} for value in variants],
        "batchCandidates": batch_results}
    policy_path = args.runtime / "vitmatte-performance-policy-v1.json"
    try:
        policy = json.loads(policy_path.read_text(encoding="utf-8"))
    except Exception:
        policy = {"schemaVersion": 1, "entries": {}}
    entry = policy.setdefault("entries", {}).setdefault(result["policyKey"], {})
    entry.update({"safeBatchSize": best_batch, "benchmarkBatchSize": best_batch,
        "compiledEnabled": accepted, "breakEvenFrames": result["breakEvenFrames"],
        "gainPercent": result["gainPercent"], "alphaIou": equivalence["iou"],
        "channelsLast": result["channelsLast"],
        "explicitHalf": result["explicitHalf"],
        "updatedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})
    temporary = policy_path.with_suffix(policy_path.suffix + ".tmp")
    temporary.write_text(json.dumps(policy, indent=2), encoding="utf-8")
    os.replace(temporary, policy_path)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--ffprobe", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--mask", type=Path, required=True)
    parser.add_argument("--models", nargs="+", choices=("s", "b"), default=("s", "b"))
    parser.add_argument("--frames", type=int, default=300)
    parser.add_argument("--runs", type=int, default=2)
    parser.add_argument("--batch-runs", type=int, default=3)
    parser.add_argument("--worker-timeout", type=float, default=600,
        help="Reject one worker run after this many seconds")
    parser.add_argument("--vram-stop-percent", type=float, default=94.0,
        help="Stop an eager batch candidate when whole-GPU dedicated memory reaches "
             "this percentage; 0 disables monitoring")
    parser.add_argument("--batch-only", action="store_true",
        help="Only measure eager batch sizes; do not run compilation/cutoff tests")
    parser.add_argument("--small-batches", type=int, nargs="+",
        default=(1, 2, 3, 4, 6, 8, 12))
    parser.add_argument("--base-batches", type=int, nargs="+",
        default=(1, 2, 3, 4, 6, 8, 12))
    parser.add_argument("--minimum-gain", type=float, default=5.0)
    parser.add_argument("--default-cutoff", type=int, default=16000)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if not 0 <= args.vram_stop_percent <= 100:
        parser.error("--vram-stop-percent must be between 0 and 100")
    batches_by_model = {"s": args.small_batches, "b": args.base_batches}
    if args.batch_only:
        PROGRESS["total"] = sum(len(batches_by_model[model]) * args.batch_runs
                                for model in args.models)
    else:
        PROGRESS["total"] = sum(len(batches_by_model[model]) * args.batch_runs +
            args.runs * 2 + 2 for model in args.models)
    with tempfile.TemporaryDirectory(prefix="iqp-vitmatte-benchmark-",
                                     ignore_cleanup_errors=True) as value:
        work = Path(value)
        source = work / "source.mp4"
        trim_source(args, args.source.resolve(), source, args.frames)
        masks = work / "masks"
        make_masks(args.mask.resolve(), masks, args.frames)
        if args.batch_only:
            results = []
            for model in args.models:
                best_batch, candidates = benchmark_batches(
                    args, model, source, masks, work)
                results.append({"model": model, "batchSize": best_batch,
                                "batchCandidates": candidates})
        else:
            results = [benchmark_model(args, model, source, masks, work)
                       for model in args.models]
        # Publish the report before cleanup so a delayed Windows media handle
        # cannot discard otherwise complete benchmark results.
        args.report.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.report.with_suffix(args.report.suffix + ".tmp")
        temporary.write_text(
            json.dumps({"schemaVersion": 1, "results": results}, indent=2),
            encoding="utf-8")
        os.replace(temporary, args.report)
    say("ViTMatte benchmarks complete.")


if __name__ == "__main__":
    try: main()
    except KeyboardInterrupt: raise SystemExit(130)
