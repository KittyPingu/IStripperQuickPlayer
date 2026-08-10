#!/usr/bin/env python3
"""Manual full-pipeline benchmark for QuickPlayer's RVM worker."""
import argparse
import json
import math
import os
import shutil
import statistics
import subprocess
import sys
import tempfile
from pathlib import Path

CHUNKS = (3, 6, 8, 12, 16, 24)
RESOLUTIONS = ((1920, 1080), (3840, 2160))


def say(message):
    print(message, flush=True)


def command(args, label, capture=False):
    say(label)
    process = subprocess.Popen(args, stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT, text=True, bufsize=1)
    lines = []
    for line in process.stdout:
        line = line.rstrip()
        if line:
            if not line.startswith("{") or '"stage": "inference"' not in line:
                say(line)
            lines.append(line)
    if process.wait() != 0:
        raise RuntimeError(f"{label} failed (exit {process.returncode}):\n" +
                           "\n".join(lines[-40:]))
    return lines if capture else None


def make_fixture(ffmpeg, folder, width, height, frames, fps):
    path = folder / f"rvm-{width}x{height}-{frames}.mp4"
    if path.is_file():
        return path
    command([str(ffmpeg), "-y", "-v", "error", "-f", "lavfi", "-i",
        f"testsrc2=size={width}x{height}:rate={fps}:duration={frames / fps}",
        "-f", "lavfi", "-i", f"sine=frequency=440:duration={frames / fps}",
        "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "veryfast",
        "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", str(path)],
        f"Generating deterministic {width}x{height} fixture...")
    return path


def parse_summary(lines):
    summaries = []
    for line in lines:
        marker = line.find("PROFILE {")
        if marker < 0:
            continue
        try:
            value = json.loads(line[marker + len("PROFILE "):])
            if value.get("kind") == "rvm_summary":
                summaries.append(value)
        except json.JSONDecodeError:
            pass
    if not summaries:
        raise RuntimeError("RVM worker did not emit a profiling summary")
    return summaries[-1]


def run_worker(args, source, output, preset, chunk, pipeline="bounded",
               execution="eager", preview=True, label="RVM benchmark", keep=False,
               encoder_preset=None, verify_raw=False):
    shutil.rmtree(output, ignore_errors=True)
    output.mkdir(parents=True)
    environment = os.environ.copy()
    environment["IQP_FFMPEG"] = str(args.ffmpeg)
    environment["IQP_FFPROBE"] = str(args.ffprobe)
    worker = [sys.executable, str(args.worker), "--source", str(source),
        "--output", str(output), "--runtime", str(args.runtime), "--preset", preset,
        "--matting-resolution", "512", "--sequence-chunk", str(chunk),
        "--encoder-preset", encoder_preset or args.encoder_preset, "--pipeline", pipeline,
        "--execution-mode", execution]
    if execution == "compile":
        worker += ["--compile-cutoff-frames", "1"]
    if not preview:
        worker.append("--disable-preview")
    if verify_raw:
        worker.append("--verify-raw-hash")
    say(label)
    process = subprocess.Popen(worker, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, bufsize=1, env=environment)
    lines = []
    # Workers write little output apart from progress; draining stdout first is safe because
    # profiling stderr remains comfortably below the pipe capacity for one run.
    for line in process.stdout:
        line = line.rstrip()
        lines.append(line)
        if line and ('"stage": "inference"' not in line or '"percent": 99' in line):
            say(line)
    if process.wait() != 0:
        raise RuntimeError(f"{label} failed (exit {process.returncode}):\n" +
                           "\n".join(lines[-40:]))
    for line in lines:
        if line.startswith("PROFILE ") and '"kind": "rvm_summary"' in line:
            say(line)
    summary = parse_summary(lines)
    if not keep:
        shutil.rmtree(output, ignore_errors=True)
    return summary


def median(values, key):
    return statistics.median(value[key] for value in values)


def sample_alpha(ffmpeg, ffprobe, path, width, height, frame_count=10):
    duration = float(subprocess.check_output([str(ffprobe), "-v", "error",
        "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", str(path)],
        text=True).strip())
    samples = []
    for index in range(frame_count):
        timestamp = max(0, duration * index / max(1, frame_count - 1) - .001)
        data = subprocess.check_output([str(ffmpeg), "-v", "error", "-ss",
            f"{timestamp:.6f}", "-i", str(path), "-frames:v", "1", "-f", "rawvideo",
            "-pix_fmt", "gray", "pipe:1"])
        if len(data) != width * height:
            raise RuntimeError("Could not decode an alpha verification frame")
        samples.append(data)
    return samples


def alpha_difference(first, second):
    import numpy as np
    errors = []
    for left, right in zip(first, second):
        a = np.frombuffer(left, dtype=np.uint8).astype(np.int16)
        b = np.frombuffer(right, dtype=np.uint8).astype(np.int16)
        errors.append(np.abs(a - b))
    combined = np.concatenate(errors)
    return float(combined.mean()) / 255, float(np.percentile(combined, 99)) / 255


def foreground_ssim(ffmpeg, first, second):
    process = subprocess.run([str(ffmpeg), "-v", "info", "-i", str(first),
        "-i", str(second), "-lavfi", "ssim", "-f", "null", "-"],
        stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
    if process.returncode != 0:
        raise RuntimeError("FFmpeg SSIM comparison failed: " + process.stderr[-1000:])
    marker = "All:"
    line = next((line for line in reversed(process.stderr.splitlines())
                 if marker in line), "")
    if not line:
        raise RuntimeError("FFmpeg did not report foreground SSIM")
    return float(line.split(marker, 1)[1].split()[0])


def choose_chunk(results, total_vram):
    by_resolution = {}
    for resolution in RESOLUTIONS:
        candidates = {chunk: median(results[(resolution, chunk)], "fps")
                      for chunk in CHUNKS if (resolution, chunk) in results}
        by_resolution[resolution] = candidates
    eligible = []
    for chunk in CHUNKS:
        if not all(chunk in by_resolution[resolution] for resolution in RESOLUTIONS):
            continue
        if any(by_resolution[resolution][chunk] <
               max(by_resolution[resolution].values()) * .95 for resolution in RESOLUTIONS):
            continue
        peaks = [max(value["peakVramBytes"] for value in results[(resolution, chunk)])
                 for resolution in RESOLUTIONS]
        if total_vram and max(peaks) > total_vram - 2 * 1024 ** 3:
            continue
        score = statistics.geometric_mean(
            by_resolution[resolution][chunk] for resolution in RESOLUTIONS)
        eligible.append((score, -chunk, chunk))
    if not eligible:
        return 12
    return max(eligible)[2]


def benchmark_model(args, preset, fixtures, work, total_vram):
    say(f"\n=== RVM {preset.upper()} chunk sweep ===")
    chunk_results = {}
    for resolution, source in fixtures.items():
        for chunk in CHUNKS:
            values = []
            for run in range(args.runs):
                values.append(run_worker(args, source,
                    work / f"{preset}-{resolution[0]}-c{chunk}-r{run}", preset, chunk,
                    label=f"{preset} {resolution[0]}x{resolution[1]}, chunk {chunk}, run {run + 1}/{args.runs}"))
            chunk_results[(resolution, chunk)] = values
            say(f"  median {median(values, 'fps'):.2f} FPS; "
                f"peak VRAM {max(value['peakVramBytes'] for value in values) / 1024 ** 3:.2f} GiB")
    selected = choose_chunk(chunk_results, total_vram)
    say(f"Selected {preset} chunk: {selected}")

    say(f"\n=== RVM {preset.upper()} serial/bounded and preview checks ===")
    for resolution, source in fixtures.items():
        variants = {}
        for pipeline, preview in (("serial", True), ("bounded", True),
                                  ("bounded", False)):
            key = f"{pipeline}-preview-{preview}"
            variants[key] = [run_worker(args, source,
                work / f"{preset}-{resolution[0]}-{key}-r{run}", preset, selected,
                pipeline=pipeline, preview=preview, verify_raw=True,
                label=f"{preset} {resolution[0]} {key}, run {run + 1}/{args.runs}")
                for run in range(args.runs)]
        serial = median(variants["serial-preview-True"], "fps")
        bounded = median(variants["bounded-preview-True"], "fps")
        preview_off = median(variants["bounded-preview-False"], "fps")
        say(f"  {resolution[0]}: serial {serial:.2f}, bounded {bounded:.2f}, "
            f"preview off {preview_off:.2f} FPS")
        if bounded < serial * .98:
            raise RuntimeError(f"Bounded {preset} pipeline regressed by more than 2% at {resolution[0]}px")
        if variants["serial-preview-True"][0].get("rawAlphaSha256") != \
                variants["bounded-preview-True"][0].get("rawAlphaSha256"):
            raise RuntimeError(
                f"Bounded {preset} output differs from serial output at {resolution[0]}px")

    say(f"\n=== RVM {preset.upper()} NVENC p5/p7 comparison ===")
    for resolution, source in fixtures.items():
        variants = {}
        for encoder_preset in ("p5", "p7"):
            variants[encoder_preset] = [run_worker(args, source,
                work / f"{preset}-{resolution[0]}-{encoder_preset}-r{run}",
                preset, selected, keep=run == 0, encoder_preset=encoder_preset,
                label=f"{preset} {resolution[0]} NVENC {encoder_preset}, "
                      f"run {run + 1}/{args.runs}") for run in range(args.runs)]
        if variants["p5"][0]["encoder"] != "h264_nvenc":
            say(f"  {resolution[0]}: NVENC unavailable; p5/p7 comparison skipped.")
        else:
            p5_folder = work / f"{preset}-{resolution[0]}-p5-r0"
            p7_folder = work / f"{preset}-{resolution[0]}-p7-r0"
            p5_seconds = median(variants["p5"], "seconds")
            p7_seconds = median(variants["p7"], "seconds")
            p5_size = sum((p5_folder / name).stat().st_size
                          for name in ("foreground.mp4", "alpha.mkv"))
            p7_size = sum((p7_folder / name).stat().st_size
                          for name in ("foreground.mp4", "alpha.mkv"))
            ssim = foreground_ssim(args.ffmpeg, p5_folder / "foreground.mp4",
                                   p7_folder / "foreground.mp4")
            p5_alpha = sample_alpha(args.ffmpeg, args.ffprobe,
                                    p5_folder / "alpha.mkv", *resolution)
            p7_alpha = sample_alpha(args.ffmpeg, args.ffprobe,
                                    p7_folder / "alpha.mkv", *resolution)
            mean_error, p99_error = alpha_difference(p5_alpha, p7_alpha)
            say(f"  {resolution[0]}: p5 {p5_seconds:.2f}s/{p5_size / 1024**2:.1f} MiB; "
                f"p7 {p7_seconds:.2f}s/{p7_size / 1024**2:.1f} MiB; "
                f"foreground SSIM {ssim:.6f}; alpha mean/p99 "
                f"{mean_error:.6f}/{p99_error:.6f}")
        shutil.rmtree(work / f"{preset}-{resolution[0]}-p5-r0", ignore_errors=True)
        shutil.rmtree(work / f"{preset}-{resolution[0]}-p7-r0", ignore_errors=True)

    say(f"\n=== RVM {preset.upper()} compile check ===")
    compile_values = {}
    cold_overheads = []
    quality_ok = True
    cache = args.runtime / "torchinductor-rvm"
    shutil.rmtree(cache, ignore_errors=True)
    for resolution, source in fixtures.items():
        eager = []
        for run in range(args.runs):
            eager.append(run_worker(args, source,
                work / f"{preset}-{resolution[0]}-eager-r{run}", preset, selected,
                execution="eager", preview=True, keep=run == 0,
                label=f"{preset} {resolution[0]} eager, run {run + 1}/{args.runs}"))
        cold = run_worker(args, source,
            work / f"{preset}-{resolution[0]}-compile-cold", preset, selected,
            execution="compile", preview=True,
            label=f"{preset} {resolution[0]} compiled cold/cache-generation run")
        compiled = []
        for run in range(args.runs):
            compiled.append(run_worker(args, source,
                work / f"{preset}-{resolution[0]}-compile-r{run}", preset, selected,
                execution="compile", preview=True, keep=run == 0,
                label=f"{preset} {resolution[0]} compiled cached, run {run + 1}/{args.runs}"))
        eager_seconds = median(eager, "seconds")
        compiled_seconds = median(compiled, "seconds")
        gain = (eager_seconds - compiled_seconds) / eager_seconds
        compile_values[resolution] = (gain, eager_seconds, compiled_seconds)
        cold_overheads.append(max(0, cold["seconds"] - compiled_seconds))
        say(f"  {resolution[0]}: eager {eager_seconds:.2f}s; compiled cached "
            f"{compiled_seconds:.2f}s; gain {gain * 100:.2f}%")
        eager_output = work / f"{preset}-{resolution[0]}-eager-r0"
        compiled_output = work / f"{preset}-{resolution[0]}-compile-r0"
        eager_alpha = sample_alpha(args.ffmpeg, args.ffprobe,
            eager_output / "alpha.mkv", *resolution)
        compiled_alpha = sample_alpha(args.ffmpeg, args.ffprobe,
            compiled_output / "alpha.mkv", *resolution)
        mean_error, p99_error = alpha_difference(eager_alpha, compiled_alpha)
        say(f"  decoded alpha error: mean {mean_error:.6f}; p99 {p99_error:.6f}")
        quality_ok &= mean_error <= .5 / 255 and p99_error <= 2 / 255
        shutil.rmtree(eager_output, ignore_errors=True)
        shutil.rmtree(compiled_output, ignore_errors=True)
    accepted = quality_ok and all(value[0] >= .05 for value in compile_values.values())
    cutoff = 0
    if accepted:
        savings = [(eager - compiled) / args.frames
                   for _, eager, compiled in compile_values.values()]
        per_frame = min(savings)
        cutoff = max(1, math.ceil(1.2 * max(cold_overheads) / per_frame))
        say(f"Compilation accepted; calculated cutoff {cutoff:,} frames.")
    else:
        say("Compilation did not pass the 5% speed and alpha-quality gates; it remains disabled.")
    return selected, cutoff


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--ffprobe", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--frames", type=int, default=1000)
    parser.add_argument("--fps", type=int, default=30)
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--quality-chunk-default", type=int, default=12)
    parser.add_argument("--fast-chunk-default", type=int, default=12)
    parser.add_argument("--quality-cutoff-default", type=int, default=0)
    parser.add_argument("--fast-cutoff-default", type=int, default=0)
    parser.add_argument("--encoder-preset", choices=tuple(f"p{i}" for i in range(1, 8)),
                        default="p5")
    args = parser.parse_args()
    args.runtime = args.runtime.resolve()
    os.environ["IQP_FFMPEG"] = str(args.ffmpeg.resolve())
    os.environ["IQP_FFPROBE"] = str(args.ffprobe.resolve())
    try:
        import torch
        total_vram = torch.cuda.get_device_properties(0).total_memory \
            if torch.cuda.is_available() else 0
    except Exception:
        total_vram = 0
    with tempfile.TemporaryDirectory(prefix="iqp-rvm-benchmark-") as temporary:
        work = Path(temporary)
        fixtures = {resolution: make_fixture(args.ffmpeg, work, *resolution,
                                              args.frames, args.fps)
                    for resolution in RESOLUTIONS}
        values = {}
        for preset in ("quality", "fast"):
            try:
                values[preset] = benchmark_model(args, preset, fixtures, work, total_vram)
            except Exception as error:
                say(f"RVM {preset} benchmark failed: {error}")
                values[preset] = ((args.quality_chunk_default if preset == "quality"
                                   else args.fast_chunk_default), 0)
        result = {
            "rvmQualityPreferredChunk": values["quality"][0],
            "rvmFastPreferredChunk": values["fast"][0],
            "rvmQualityCompileCutoffFrames": values["quality"][1],
            "rvmFastCompileCutoffFrames": values["fast"][1],
            "rvmNvencPreset": args.encoder_preset,
        }
        args.output.parent.mkdir(parents=True, exist_ok=True)
        temporary_output = args.output.with_suffix(args.output.suffix + ".tmp")
        temporary_output.write_text(json.dumps(result, indent=2), encoding="utf-8")
        os.replace(temporary_output, args.output)
        say("RVM benchmark results written successfully.")


if __name__ == "__main__":
    main()
