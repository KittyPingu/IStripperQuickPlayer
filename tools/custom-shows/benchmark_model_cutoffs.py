#!/usr/bin/env python3
"""Manually benchmark and recalculate QuickPlayer's model compile cutoffs."""
import argparse
import json
import math
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

PROGRESS_PREFIX = "IQP_BENCHMARK_PROGRESS "
PHASES = 4
PHASE_UNITS = {1: 1, 2: 1, 3: 1, 4: 1}


def say(message):
    print(message, flush=True)


def progress(phase, completed, total, message):
    inner = max(0.0, min(1.0, completed / max(1, total)))
    overall_total = sum(PHASE_UNITS.values())
    overall_completed = sum(PHASE_UNITS[index]
        for index in range(1, phase)) + PHASE_UNITS[phase] * inner
    payload = {"phase": phase, "phases": PHASES, "completed": completed,
        "total": max(1, total), "message": message,
        "overallCompleted": overall_completed, "overallTotal": overall_total}
    say(PROGRESS_PREFIX + json.dumps(payload, separators=(",", ":")))


def run(command, label, phase=1):
    say(label)
    progress(phase, 0, 1, label.removesuffix("..."))
    process = subprocess.Popen(command, stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT, text=True, bufsize=1)
    tail = []
    for line in process.stdout:
        line = line.rstrip()
        if line:
            if line.startswith(PROGRESS_PREFIX):
                try:
                    child = json.loads(line[len(PROGRESS_PREFIX):])
                    progress(phase, int(child.get("completed", 0)),
                        int(child.get("total", 1)), child.get("message", label))
                except (TypeError, ValueError):
                    say(line)
            else:
                say(line)
            tail.append(line)
            tail = tail[-30:]
    if process.wait() != 0:
        raise RuntimeError(f"{label} failed:\n" + "\n".join(tail))
    progress(phase, 1, 1, label.removesuffix("...") + " complete")


def make_fixture(ffmpeg, folder, frames=300, fps=30):
    from PIL import Image, ImageDraw
    folder.mkdir(parents=True, exist_ok=True)
    source, mask = folder / "source.mp4", folder / "mask.png"
    if not source.is_file():
        run([str(ffmpeg), "-y", "-v", "error", "-f", "lavfi", "-i",
            f"testsrc2=size=1920x1080:rate={fps}:duration={frames / fps}",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            str(source)], "Generating the deterministic 1080p benchmark fixture...", 1)
    image = Image.new("L", (1920, 1080), 0)
    ImageDraw.Draw(image).ellipse((560, 170, 1360, 1030), fill=255)
    image.save(mask)
    return source, mask


def installed_sam_models(runtime):
    files = {"base-plus": "sam2.1_hiera_base_plus.pt",
             "small": "sam2.1_hiera_small.pt", "tiny": "sam2.1_hiera_tiny.pt"}
    return [model for model, name in files.items()
            if (runtime / "checkpoints" / name).is_file()]


def matanyone_cutoff(args, source, mask, work):
    marker = args.runtime / "MATANYONE2_COMMIT"
    if not marker.is_file():
        say("MatAnyone2 is not installed; retaining its current cutoff.")
        progress(2, 1, 1, "MatAnyone2 is not installed; skipped")
        return args.matanyone_default
    report = work / "matanyone2-report.json"
    benchmark = Path(__file__).with_name("benchmark_matanyone2.py")
    run([sys.executable, str(benchmark), "--runtime", str(args.runtime),
        "--source", str(source), "--mask", str(mask), "--frames", "300",
        "--runs", "2", "--pipelines", "bounded", "--compile-modes", "eager",
        "partial", "--preview-modes", "true", "--ffmpeg", str(args.ffmpeg),
        "--ffprobe", str(args.ffprobe), "--report", str(report), "--write-policy"],
        "Benchmarking MatAnyone2 eager and partially compiled execution...", 2)
    value = json.loads(report.read_text(encoding="utf-8"))
    if not value.get("partialCompileAccepted"):
        say("MatAnyone2 compilation did not pass the speed/quality gate; retaining the current cutoff.")
        return args.matanyone_default
    cutoff = max(1, int(value.get("breakEvenFrames", args.matanyone_default)))
    say(f"Measured MatAnyone2 compile cutoff: {cutoff:,} frames")
    return cutoff


def sam_cutoffs(args, source, mask, work, defaults):
    models = installed_sam_models(args.runtime)
    if not models:
        say("SAM2 checkpoints are not installed; retaining the current SAM2 cutoffs.")
        progress(3, 1, 1, "SAM2 is not installed; skipped")
        return defaults
    benchmark = Path(__file__).with_name("benchmark_sam2_workflows.py")
    output = work / "sam2"
    run([sys.executable, str(benchmark), "--python", sys.executable,
        "--worker", str(args.sam_worker), "--runtime", str(args.runtime),
        "--ffmpeg", str(args.ffmpeg), "--ffprobe", str(args.ffprobe),
        "--source", str(source), "--mask", str(mask), "--frames", "300",
        "--start-ms", "0", "--end-ms", "10000", "--models", *models,
        "--modes", "eager", "encoder-no-cudagraphs", "vos-no-cudagraphs",
        "--repetitions", "2", "--write-policy", "--fixed-correction-point",
        "960", "540", "--output", str(output)],
        "Benchmarking installed SAM2 models and compilation modes...", 3)
    policy_path = args.runtime / "sam2-performance-policy-v1.json"
    policy = json.loads(policy_path.read_text(encoding="utf-8"))
    for model in models:
        matching = [entry for key, entry in policy.get("entries", {}).items()
                    if key.endswith("|" + model)]
        if not matching:
            continue
        entry = matching[-1]
        factor = max(1.0, float(entry.get("expectedWorkMultiplier", 3.0)))
        candidates = [float(entry[mode]["breakEvenFrames"])
                      for mode in ("encoder", "vos")
                      if entry.get(mode, {}).get("enabled")]
        if not candidates:
            tested = [entry[mode] for mode in ("encoder", "vos")
                      if isinstance(entry.get(mode), dict)]
            fastest = max(tested, key=lambda value:
                float(value.get("steadyCorrectionSpeedup",
                    value.get("warmSpeedup", -math.inf))), default=None)
            if fastest is None:
                detail = "no compiled result was available"
            else:
                gain = float(fastest.get("steadyCorrectionSpeedup",
                    fastest.get("warmSpeedup", 0))) * 100
                quality = "mask equivalence passed" if fastest.get("maskEquivalent") \
                    else "mask equivalence failed"
                detail = f"best steady correction gain {gain:.1f}%; {quality}"
            say(f"SAM2 {model} compilation disabled ({detail}); eager mode will be used.")
            continue
        # The policy is measured in propagated work; the UI cutoff is source frames.
        cutoff = max(1, math.ceil(min(candidates) * 1.2 / factor))
        defaults[model] = cutoff
        say(f"Measured SAM2 {model} compile cutoff: {cutoff:,} source frames")
    return defaults


def vitmatte_cutoffs(args, source, mask, work, defaults, batches):
    installed = []
    for model, marker, folder in (("s", "VITMATTE_S_REVISION", "vitmatte-s"),
                                  ("b", "VITMATTE_B_REVISION", "vitmatte-b")):
        if (args.runtime / marker).is_file() and (args.runtime / folder).is_dir():
            installed.append(model)
    if not installed:
        say("ViTMatte is not installed; retaining the current ViTMatte cutoffs.")
        progress(4, 1, 1, "ViTMatte is not installed; skipped")
        return defaults, batches
    benchmark = Path(__file__).with_name("benchmark_vitmatte.py")
    report = work / "vitmatte-report.json"
    run([sys.executable, str(benchmark), "--runtime", str(args.runtime),
        "--worker", str(args.vitmatte_worker), "--ffmpeg", str(args.ffmpeg),
        "--ffprobe", str(args.ffprobe), "--source", str(source), "--mask", str(mask),
        "--models", *installed, "--frames", "300", "--runs", "2",
        "--report", str(report)],
        "Benchmarking installed ViTMatte models, batch sizes, and compilation...", 4)
    values = json.loads(report.read_text(encoding="utf-8"))
    for result in values.get("results", []):
        model = result.get("model")
        if model not in defaults:
            continue
        batches[model] = max(1, min(12,
            int(result.get("batchSize", batches[model]))))
        say(f"Measured ViTMatte-{model.upper()} preferred batch: {batches[model]}")
        if not result.get("compiledEnabled"):
            say(f"ViTMatte-{model.upper()} compilation did not pass its gate; "
                "retaining the current cutoff.")
            continue
        defaults[model] = max(1, int(result.get("breakEvenFrames", defaults[model])))
        say(f"Measured ViTMatte-{model.upper()} compile cutoff: "
            f"{defaults[model]:,} frames")
    return defaults, batches


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--ffprobe", type=Path, required=True)
    parser.add_argument("--sam-worker", type=Path, required=True)
    parser.add_argument("--vitmatte-worker", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--matanyone-default", type=int, default=16000)
    parser.add_argument("--sam-base-default", type=int, default=16000)
    parser.add_argument("--sam-small-default", type=int, default=16000)
    parser.add_argument("--sam-tiny-default", type=int, default=16000)
    parser.add_argument("--vitmatte-small-default", type=int, default=16000)
    parser.add_argument("--vitmatte-base-default", type=int, default=16000)
    parser.add_argument("--vitmatte-small-batch-default", type=int, default=2)
    parser.add_argument("--vitmatte-base-batch-default", type=int, default=1)
    args = parser.parse_args()
    args.runtime = args.runtime.resolve()
    global PHASE_UNITS
    sam_models = installed_sam_models(args.runtime)
    vitmatte_models = sum((args.runtime / marker).is_file() and
        (args.runtime / folder).is_dir() for marker, folder in (
            ("VITMATTE_S_REVISION", "vitmatte-s"),
            ("VITMATTE_B_REVISION", "vitmatte-b")))
    PHASE_UNITS = {1: 1,
        2: 4 if (args.runtime / "MATANYONE2_COMMIT").is_file() else 1,
        3: max(1, len(sam_models) * 3 * 2),
        4: max(1, vitmatte_models * 27)}
    defaults = {"base-plus": args.sam_base_default,
                "small": args.sam_small_default, "tiny": args.sam_tiny_default}
    vitmatte_defaults = {"s": args.vitmatte_small_default,
                         "b": args.vitmatte_base_default}
    vitmatte_batches = {"s": args.vitmatte_small_batch_default,
                        "b": args.vitmatte_base_batch_default}
    with tempfile.TemporaryDirectory(prefix="iqp-cutoff-benchmark-") as value:
        work = Path(value)
        progress(1, 0, 1, "Preparing deterministic 1080p benchmark fixture")
        source, mask = make_fixture(args.ffmpeg.resolve(), work / "fixture")
        progress(1, 1, 1, "Benchmark fixture ready")
        matanyone = matanyone_cutoff(args, source, mask, work)
        defaults = sam_cutoffs(args, source, mask, work, defaults)
        vitmatte_defaults, vitmatte_batches = vitmatte_cutoffs(
            args, source, mask, work, vitmatte_defaults, vitmatte_batches)
    result = {"matAnyone2CompileCutoffFrames": matanyone,
        "sam2BasePlusCompileCutoffFrames": defaults["base-plus"],
        "sam2SmallCompileCutoffFrames": defaults["small"],
        "sam2TinyCompileCutoffFrames": defaults["tiny"],
        "vitMatteSmallCompileCutoffFrames": vitmatte_defaults["s"],
        "vitMatteBaseCompileCutoffFrames": vitmatte_defaults["b"],
        "vitMatteSmallPreferredBatchSize": vitmatte_batches["s"],
        "vitMatteBasePreferredBatchSize": vitmatte_batches["b"]}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix(args.output.suffix + ".tmp")
    temporary.write_text(json.dumps(result, indent=2), encoding="utf-8")
    temporary.replace(args.output)
    say("All requested cutoff benchmarks are complete.")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        raise SystemExit(130)
