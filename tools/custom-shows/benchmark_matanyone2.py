#!/usr/bin/env python3
"""Benchmark QuickPlayer's MatAnyone2 tensor path or complete video pipeline."""
import argparse
import json
import math
import os
import shutil
import statistics
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from matanyone2_worker import (COMPILE_POLICY, apply_partial_compile, load_model,
                               restore_eager)
from rvm_worker import fast_fp16

PROGRESS_PREFIX = "IQP_BENCHMARK_PROGRESS "


def progress(completed, total, message):
    print(PROGRESS_PREFIX + json.dumps({"completed": completed,
        "total": max(1, total), "message": message}, separators=(",", ":")),
        flush=True)


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1,
                       max(0, math.ceil(len(ordered) * fraction) - 1))]


def tensor_benchmark(args):
    if args.device == "cpu":
        os.environ["CUDA_VISIBLE_DEVICES"] = "-1"
    import torch
    if args.device == "cuda" and not torch.cuda.is_available():
        raise SystemExit("CUDA is unavailable")
    device = torch.device(args.device)
    torch_module, InferenceCore, model, _ = load_model(args.runtime.resolve(), device)
    originals = None
    if args.precision_mode == "half" and device.type == "cuda":
        model.half()
    if args.compile_mode == "partial":
        if device.type != "cuda":
            raise SystemExit("Partial compilation requires CUDA")
        originals = apply_partial_compile(torch_module, model, args.runtime.resolve())
    processor = InferenceCore(model, cfg=model.cfg, device=device)
    fp16 = device.type == "cuda" and fast_fp16(torch.cuda.get_device_capability(device))
    frame = torch.rand((3, args.height, args.width), device=device)
    mask = torch.zeros((args.height, args.width), device=device)
    mask[args.height // 4:args.height * 3 // 4,
         args.width // 4:args.width * 3 // 4] = 255
    if device.type == "cuda":
        torch.cuda.reset_peak_memory_stats(device)

    started = time.perf_counter()
    with torch.inference_mode(), torch.amp.autocast(
            device_type=device.type, enabled=fp16):
        processor.step(frame, mask, objects=[1])
        for _ in range(11):
            output = processor.step(frame, first_frame_pred=True)
    if device.type == "cuda":
        torch.cuda.synchronize(device)
    warmup = time.perf_counter() - started

    samples = []
    for _ in range(args.frames):
        started = time.perf_counter()
        with torch.inference_mode(), torch.amp.autocast(
                device_type=device.type, enabled=fp16):
            output = processor.step(frame)
        processor.output_prob_to_mask(output).mul(255).byte().cpu().numpy()
        if device.type == "cuda":
            torch.cuda.synchronize(device)
        samples.append(time.perf_counter() - started)
    elapsed = sum(samples)
    result = {
        "kind": "tensor", "device": device.type,
        "precision": "FP16" if fp16 else "FP32",
        "compile": args.compile_mode, "precisionMode": args.precision_mode,
        "size": f"{args.width}x{args.height}",
        "frames": args.frames, "warmupSeconds": warmup,
        "seconds": elapsed, "fps": args.frames / elapsed,
        "meanMs": statistics.fmean(samples) * 1000,
        "medianMs": statistics.median(samples) * 1000,
        "p95Ms": percentile(samples, .95) * 1000,
        "peakCudaAllocatedBytes": torch.cuda.max_memory_allocated(device)
            if device.type == "cuda" else 0,
    }
    if originals is not None:
        restore_eager(model, originals)
    print(json.dumps(result, indent=2))


def make_4k_fixture(source, destination, ffmpeg, frames, fps):
    subprocess.run([ffmpeg, "-y", "-v", "error", "-i", str(source),
        "-frames:v", str(frames), "-vf",
        "scale=3840:2160:force_original_aspect_ratio=increase,crop=3840:2160,setsar=1",
        "-an", "-c:v", "h264_nvenc", "-preset", "p4", "-cq", "20",
        "-r", str(fps), str(destination)], check=True)


def run_worker(args, source, output, pipeline, compile_mode, previews,
               mask_frame_ms, raw_alpha):
    worker = Path(__file__).with_name("matanyone2_worker.py")
    command = [sys.executable, str(worker), "--source", str(source),
        "--mask", str(args.mask), "--output", str(output), "--runtime",
        str(args.runtime), "--start-ms", str(args.start_ms), "--max-size",
        str(args.max_size), "--max-frames", str(args.frames),
        "--pipeline-mode", pipeline, "--compile-mode", compile_mode,
        "--mask-frame-ms", str(mask_frame_ms), "--resize-backend",
        args.resize_backend, "--precision-mode", args.precision_mode]
    if not previews:
        command.append("--disable-previews")
    if raw_alpha is not None:
        command.extend(("--raw-alpha-output", str(raw_alpha)))
    environment = os.environ.copy()
    environment["IQP_FFMPEG"] = str(args.ffmpeg)
    environment["IQP_FFPROBE"] = str(args.ffprobe)
    if compile_mode == "partial":
        environment["TORCH_LOGS"] = "recompiles"
    started = time.perf_counter()
    process = subprocess.run(command, text=True, capture_output=True,
                             env=environment)
    elapsed = time.perf_counter() - started
    if process.returncode:
        raise RuntimeError(f"Worker failed ({process.returncode}):\n{process.stdout}\n{process.stderr}")
    profiles = []
    for line in process.stderr.splitlines():
        try:
            value = json.loads(line)
            if value.get("matanyone2") == "profile":
                profiles.append(value)
        except (ValueError, AttributeError):
            pass
    if not profiles:
        raise RuntimeError("Worker returned no profiling record")
    profile = profiles[-1]
    if profile.get("variant", {}).get("compile") != compile_mode:
        raise RuntimeError(f"Requested {compile_mode} but worker completed as "
                           f"{profile.get('variant', {}).get('compile')}")
    recompiles = sum("recompil" in line.lower() for line in process.stderr.splitlines())
    return {"wallSeconds": elapsed, "profile": profile,
            "recompileLogLines": recompiles}


def alpha_difference(eager_path, compiled_path, shape):
    import numpy as np
    eager = np.memmap(eager_path, mode="r", dtype=np.uint8, shape=shape)
    compiled = np.memmap(compiled_path, mode="r", dtype=np.uint8, shape=shape)
    difference = np.abs(eager.astype(np.int16) - compiled.astype(np.int16)).reshape(-1)
    result = {
        "mean255": float(difference.mean()),
        "p99_255": float(np.percentile(difference, 99)),
        "maximum255": int(difference.max()),
    }
    del eager, compiled
    return result


def full_pipeline_benchmark(args):
    from rvm_worker import probe
    source = args.source.resolve()
    width, height, _, fps, _ = probe(source)
    mask_frames = [("first", args.start_ms)]
    if args.include_middle:
        mask_frames.append(("middle", args.start_ms + round(args.frames * 500 / fps)))
    variants = []
    for pipeline in args.pipelines:
        for compile_mode in args.compile_modes:
            for previews in args.preview_modes:
                variants.append((pipeline, compile_mode, previews))

    with tempfile.TemporaryDirectory(prefix="iqp-matanyone-benchmark-") as temp_value:
        temp = Path(temp_value)
        sources = [] if args.only_4k else [
            ("1080p" if height <= 1080 else f"{width}x{height}", source)]
        if args.generate_4k:
            fixture = temp / "fixture-4k.mp4"
            make_4k_fixture(source, fixture, args.ffmpeg, args.frames, round(fps))
            sources.append(("4k", fixture))
        results = []
        raw_results = {}
        source_shapes = {}
        total_runs = len(sources) * len(mask_frames) * len(variants) * args.runs
        completed_runs = 0
        for source_label, current_source in sources:
            process_width, process_height = __import__(
                "matanyone2_worker").processing_size(
                    3840 if source_label == "4k" else width,
                    2160 if source_label == "4k" else height, args.max_size)
            source_shapes[source_label] = (args.frames, process_height, process_width)
            for mask_label, mask_frame_ms in mask_frames:
                for pipeline, compile_mode, previews in variants:
                    samples = []
                    for run in range(args.runs):
                        description = (f"MatAnyone2 {source_label}: {compile_mode}, "
                            f"{pipeline} pipeline, run {run + 1}/{args.runs}")
                        progress(completed_runs, total_runs, description)
                        output = temp / (f"{source_label}-{mask_label}-{pipeline}-"
                                         f"{compile_mode}-{previews}-{run}")
                        output.mkdir()
                        capture_alpha = (run == args.runs - 1 and pipeline == "bounded"
                                         and previews and compile_mode in ("eager", "partial"))
                        raw = output / "raw-alpha.u8" if capture_alpha else None
                        value = run_worker(args, current_source, output, pipeline,
                                           compile_mode, previews,
                                           mask_frame_ms, raw)
                        samples.append(value)
                        completed_runs += 1
                        progress(completed_runs, total_runs, description + " complete")
                        for media in ("foreground.mp4", "alpha.mkv"):
                            (output / media).unlink(missing_ok=True)
                        if raw is not None:
                            raw_results[(source_label, mask_label, pipeline,
                                         compile_mode, previews)] = raw
                    walls = [sample["wallSeconds"] for sample in samples]
                    results.append({
                        "source": source_label, "mask": mask_label,
                        "pipeline": pipeline, "compile": compile_mode,
                        "previews": previews, "resizeBackend": args.resize_backend,
                        "precisionMode": args.precision_mode, "runs": args.runs,
                        "medianWallSeconds": statistics.median(walls),
                        "medianFps": args.frames / statistics.median(walls),
                        "samples": samples,
                    })
        quality = []
        for result in results:
            if result["compile"] != "partial":
                continue
            eager_key = (result["source"], result["mask"], result["pipeline"],
                         "eager", result["previews"])
            partial_key = (result["source"], result["mask"], result["pipeline"],
                           "partial", result["previews"])
            if eager_key in raw_results and partial_key in raw_results:
                quality.append({
                    "source": result["source"], "mask": result["mask"],
                    "pipeline": result["pipeline"], "previews": result["previews"],
                    **alpha_difference(raw_results[eager_key], raw_results[partial_key],
                                       source_shapes[result["source"]]),
                })

        accepted = False
        break_even = 0
        eager = next((item for item in results if item["source"] != "4k" and
                      item["mask"] == "first" and item["pipeline"] == "bounded" and
                      item["compile"] == "eager" and item["previews"]), None)
        partial = next((item for item in results if item["source"] != "4k" and
                        item["mask"] == "first" and item["pipeline"] == "bounded" and
                        item["compile"] == "partial" and item["previews"]), None)
        paired_speedups = []
        for partial_result in (item for item in results
                               if item["compile"] == "partial"):
            eager_result = next((item for item in results
                if item["source"] == partial_result["source"] and
                item["mask"] == partial_result["mask"] and
                item["pipeline"] == partial_result["pipeline"] and
                item["previews"] == partial_result["previews"] and
                item["compile"] == "eager"), None)
            if eager_result:
                paired_speedups.append(1 - partial_result["medianWallSeconds"] /
                                       eager_result["medianWallSeconds"])
        no_recompiles = all(sample.get("recompileLogLines", 0) == 0
            for item in results if item["compile"] == "partial"
            for sample in item["samples"])
        if eager and partial and quality:
            saving = ((eager["medianWallSeconds"] - partial["medianWallSeconds"])
                      / args.frames)
            compile_seconds = max(0, partial["samples"][0]["wallSeconds"] -
                                  statistics.median(sample["wallSeconds"]
                                      for sample in partial["samples"][1:] or partial["samples"]))
            break_even = max(0, int(1.2 * compile_seconds / saving)) if saving > 0 else 0
            speedup = 1 - partial["medianWallSeconds"] / eager["medianWallSeconds"]
            accepted = (speedup >= .10 and
                        all(value >= -.05 for value in paired_speedups) and
                        no_recompiles and
                        all(item["mean255"] <= .5 and item["p99_255"] <= 2
                            for item in quality))
        serial = next((item for item in results if item["source"] != "4k" and
                       item["mask"] == "first" and item["pipeline"] == "serial" and
                       item["compile"] == "eager" and item["previews"]), None)
        bounded = next((item for item in results if item["source"] != "4k" and
                        item["mask"] == "first" and item["pipeline"] == "bounded" and
                        item["compile"] == "eager" and item["previews"]), None)
        pipeline_speedup = (1 - bounded["medianWallSeconds"] /
                            serial["medianWallSeconds"]) if serial and bounded else None
        report = {"frames": args.frames, "runs": args.runs,
                  "results": results, "quality": quality,
                  "pipelineSpeedup": pipeline_speedup,
                  "pipelineTargetMet": pipeline_speedup is not None and pipeline_speedup >= .20,
                  "partialCompilePairedSpeedups": paired_speedups,
                  "partialCompileNoLaterRecompiles": no_recompiles,
                  "partialCompileAccepted": accepted,
                  "breakEvenFrames": break_even}
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2))
        if args.retain_raw_alpha:
            retained = next((path for key, path in raw_results.items()
                             if key[1:] == ("first", "bounded", "eager", True)), None)
            if retained is None:
                raise RuntimeError("No bounded eager preview alpha was captured")
            args.retain_raw_alpha.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(retained, args.retain_raw_alpha)
        print(json.dumps(report, indent=2))
        if args.write_policy:
            policy = {"enabled": accepted, "detailSizes": [args.max_size],
                      "breakEvenFrames": break_even,
                      "generatedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                      "benchmarkReport": str(args.report.resolve())}
            (args.runtime / COMPILE_POLICY).write_text(json.dumps(policy, indent=2))


def parse_bool(value):
    lowered = value.lower()
    if lowered in ("true", "yes", "on", "1"):
        return True
    if lowered in ("false", "no", "off", "0"):
        return False
    raise argparse.ArgumentTypeError("expected true or false")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--device", choices=("cpu", "cuda"), default="cuda")
    parser.add_argument("--frames", type=int, default=1000)
    parser.add_argument("--width", type=int, default=910)
    parser.add_argument("--height", type=int, default=512)
    parser.add_argument("--compile-mode", choices=("eager", "partial"), default="eager")
    parser.add_argument("--precision-mode", choices=("autocast", "half"),
                        default="autocast")
    parser.add_argument("--source", type=Path)
    parser.add_argument("--mask", type=Path)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--max-size", type=int, default=512)
    parser.add_argument("--resize-backend",
                        choices=("pillow", "opencv-output", "opencv"),
                        default="pillow")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--pipelines", nargs="+", choices=("serial", "bounded"),
                        default=("serial", "bounded"))
    parser.add_argument("--compile-modes", nargs="+", choices=("eager", "partial"),
                        default=("eager", "partial"))
    parser.add_argument("--preview-modes", nargs="+", type=parse_bool,
                        default=(True, False))
    parser.add_argument("--include-middle", action="store_true")
    parser.add_argument("--generate-4k", action="store_true")
    parser.add_argument("--only-4k", action="store_true",
                        help="benchmark only the generated 4K fixture")
    parser.add_argument("--ffmpeg", type=Path, default=Path(__file__).parents[2] /
                        "IstripperQuickPlayer" / "dependencies" / "ffmpeg.exe")
    parser.add_argument("--ffprobe", type=Path, default=Path(__file__).parents[2] /
                        "IstripperQuickPlayer" / "dependencies" / "ffprobe.exe")
    parser.add_argument("--report", type=Path,
                        default=Path("matanyone2-benchmark.json"))
    parser.add_argument("--retain-raw-alpha", type=Path)
    parser.add_argument("--write-policy", action="store_true")
    args = parser.parse_args()
    if args.only_4k and not args.generate_4k:
        parser.error("--only-4k requires --generate-4k")
    if args.source:
        if not args.mask:
            parser.error("--mask is required for a full-pipeline benchmark")
        full_pipeline_benchmark(args)
    else:
        tensor_benchmark(args)


if __name__ == "__main__":
    main()
