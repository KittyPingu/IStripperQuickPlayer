#!/usr/bin/env python3
"""Manual TransNetV2 benchmark and exact-compatibility policy generator."""
import argparse
import importlib.util
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


def say(message):
    print(message, flush=True)


def load_worker(path):
    spec = importlib.util.spec_from_file_location("iqp_transnetv2_worker", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def timed_model(torch, model, sample, use_fp16, iterations=12, warmup=3):
    values = []
    output = None
    with torch.inference_mode():
        for index in range(warmup + iterations):
            if sample.is_cuda:
                torch.cuda.synchronize()
                started = torch.cuda.Event(True); ended = torch.cuda.Event(True)
                started.record()
            else:
                wall = time.perf_counter()
            with torch.autocast("cuda", dtype=torch.float16, enabled=use_fp16):
                output = model(sample)[0]
            if sample.is_cuda:
                ended.record(); torch.cuda.synchronize()
                elapsed = started.elapsed_time(ended) / 1000
            else:
                elapsed = time.perf_counter() - wall
            if index >= warmup: values.append(elapsed)
    return statistics.median(values), output.detach().float().cpu()


def batch_sweep(worker, torch, model, device, use_fp16):
    results = []
    say("Batch sweep (fixed 100-frame windows)...")
    for batch in (1, 4, 8, 16, 32, 64):
        sample = None
        try:
            if device.type == "cuda": torch.cuda.reset_peak_memory_stats(device)
            sample = torch.randint(0, 256, (batch, 100, 27, 48, 3),
                                   dtype=torch.uint8, device=device)
            seconds, _ = timed_model(torch, model, sample, use_fp16,
                                     iterations=10, warmup=2)
            windows = batch / seconds
            memory = (torch.cuda.max_memory_allocated() / 1024 ** 3
                      if device.type == "cuda" else 0)
            results.append({"batch": batch, "seconds": seconds,
                            "windowsPerSecond": windows, "peakVramGb": memory})
            say(f"  batch {batch:>2}: {windows:7.1f} windows/s, {memory:.2f} GiB peak")
        except (torch.cuda.OutOfMemoryError, RuntimeError) as error:
            if not isinstance(error, torch.cuda.OutOfMemoryError) and "out of memory" not in str(error).lower():
                raise
            say(f"  batch {batch:>2}: did not fit GPU memory")
            if device.type == "cuda": torch.cuda.empty_cache()
        finally:
            del sample
    if not results: raise RuntimeError("No TransNetV2 batch size completed")
    fastest = max(item["windowsPerSecond"] for item in results)
    # Prefer the smallest batch within 2% of the fastest to leave VRAM for QuickPlayer/iStripper.
    selected = min((item for item in results
                    if item["windowsPerSecond"] >= fastest * .98),
                   key=lambda item: item["batch"])
    say(f"Selected batch {selected['batch']} (smallest within 2% of peak throughput).")
    return selected["batch"], results


def compile_sweep(worker, torch, eager, device, batch, use_fp16, runtime):
    sample = torch.randint(0, 256, (batch, 100, 27, 48, 3),
                           dtype=torch.uint8, device=device)
    eager_seconds, eager_output = timed_model(torch, eager, sample, use_fp16)
    eager_predictions = eager_output[:, 25:75, 0].numpy().reshape(-1)
    eager_dividers = worker.dividers(eager_predictions, 30.0)
    results = []
    if device.type != "cuda" or not hasattr(torch, "compile"):
        return None, eager_seconds, results
    os.environ.setdefault("TORCHINDUCTOR_CACHE_DIR",
                          str(runtime / "torchinductor-transnetv2"))
    modes = (("compile-no-cudagraphs", "max-autotune-no-cudagraphs"),
             ("compile-cudagraphs", "max-autotune"))
    for public_mode, torch_mode in modes:
        say(f"Testing {public_mode}; first use may perform one-time compilation...")
        try:
            started = time.perf_counter()
            compiled = torch.compile(eager, mode=torch_mode, fullgraph=False)
            with torch.inference_mode(), torch.autocast("cuda", dtype=torch.float16,
                                                        enabled=use_fp16):
                compiled(sample)
            torch.cuda.synchronize()
            cold = time.perf_counter() - started
            steady, output = timed_model(torch, compiled, sample, use_fp16)
            predictions = output[:, 25:75, 0].numpy().reshape(-1)
            exact = worker.dividers(predictions, 30.0) == eager_dividers
            gain = (eager_seconds - steady) / eager_seconds * 100
            result = {"mode": public_mode, "coldSeconds": cold,
                      "steadySeconds": steady, "inferenceGainPercent": gain,
                      "dividerExact": exact}
            results.append(result)
            say(f"  cached steady-state {gain:+.1f}% vs eager; divider frames exact: {exact}")
        except Exception as error:
            results.append({"mode": public_mode, "error": str(error)[:500],
                            "dividerExact": False})
            say(f"  unavailable: {str(error).splitlines()[0]}")
            torch.cuda.empty_cache()
    viable = [item for item in results if item.get("dividerExact") and
              item.get("steadySeconds", eager_seconds) < eager_seconds]
    return (min(viable, key=lambda item: item["steadySeconds"])
            if viable else None), eager_seconds, results


def create_fixture(ffmpeg, folder, label, size, duration):
    target = folder / f"transnet-fixture-{label}.mp4"
    # Repeated hard visual changes plus motion exercise both scene and non-scene frames.
    vf = ("drawbox=x=0:y=0:w=iw:h=ih:color=red:t=fill:"
          "enable='between(t,4,7)+between(t,12,15)+between(t,20,23)',"
          "drawbox=x=0:y=0:w=iw:h=ih:color=blue:t=fill:"
          "enable='between(t,8,11)+between(t,16,19)'")
    command = [ffmpeg, "-v", "error", "-f", "lavfi", "-i",
               f"testsrc2=size={size}:rate=30:duration={duration}", "-vf", vf,
               "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18",
               "-pix_fmt", "yuv420p", "-y", str(target)]
    subprocess.run(command, check=True)
    return target


def run_worker(python, worker_path, runtime, ffmpeg, ffprobe, source, batch, decode):
    command = [python, str(worker_path), "--source", str(source), "--runtime", str(runtime),
               "--batch-size", str(batch), "--execution-mode", "eager",
               "--decode-mode", decode]
    environment = os.environ.copy()
    environment["IQP_FFMPEG"] = str(ffmpeg)
    environment["IQP_FFPROBE"] = str(ffprobe)
    started = time.perf_counter()
    process = subprocess.run(command, capture_output=True, text=True, env=environment)
    if process.returncode:
        raise RuntimeError(process.stderr.strip() or f"worker exit {process.returncode}")
    complete = None
    for line in process.stdout.splitlines():
        try:
            value = json.loads(line)
            if value.get("stage") == "complete": complete = value
        except json.JSONDecodeError:
            pass
    if complete is None: raise RuntimeError("TransNetV2 benchmark worker returned no result")
    return {"mode": decode, "wallSeconds": time.perf_counter() - started,
            "dividersMs": complete["dividersMs"], "profile": complete.get("profile", {})}


def benchmark_decoders(args, fixture, label, batch):
    results = []
    for mode in ("legacy", "cpu", "cpu-fast"):
        try:
            say(f"Testing {label} {mode} decode and divider frames...")
            result = run_worker(sys.executable, args.worker, args.runtime,
                                args.ffmpeg, args.ffprobe, fixture, batch, mode)
            results.append(result)
            say(f"  {result['wallSeconds']:.2f}s; {len(result['dividersMs'])} dividers")
        except Exception as error:
            results.append({"mode": mode, "error": str(error)[:500]})
            say(f"  unavailable: {str(error).splitlines()[0]}")
    # The compatible CPU scaler is the reference used by normal H.264 detection.
    baseline = next((item for item in results
                     if item["mode"] == "cpu" and "dividersMs" in item), None)
    if baseline is None:
        raise RuntimeError(f"Compatible CPU decoding failed for the {label} fixture")
    for item in results:
        item["dividerExact"] = item.get("dividersMs") == baseline["dividersMs"]
    # Fast-bilinear remains diagnostic-only: it has shifted divider frames on
    # longer compatibility footage even when a short fixture happens to match.
    exact = [item for item in results
             if item.get("dividerExact") and item["mode"] != "cpu-fast"]
    def decode_seconds(item):
        profile = item.get("profile", {})
        return float(profile.get("decodeReadSeconds",
                                 profile.get("producerSeconds", item["wallSeconds"])))
    chosen = min(exact, key=decode_seconds)["mode"]
    say(f"Fastest exact-compatible {label} decoder: {chosen}.")
    return chosen, results, baseline


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--ffprobe", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--batch-default", type=int, default=8)
    parser.add_argument("--cutoff-default", type=int, default=16000)
    args = parser.parse_args()
    worker = load_worker(args.worker)
    torch, model, device = worker.load_model(args.runtime)
    requested = max(1, min(64, args.batch_default))
    _, use_fp16 = worker.select_precision_and_batch(torch, model, device, requested)
    precision = "FP16" if use_fp16 else "FP32"
    say(f"Device: {device}; precision: {precision}")
    batch, batches = batch_sweep(worker, torch, model, device, use_fp16)
    best_compile, eager_batch_seconds, compile_results = compile_sweep(
        worker, torch, model, device, batch, use_fp16, args.runtime)

    temporary = Path(tempfile.mkdtemp(prefix="iqp-transnet-benchmark-"))
    try:
        say("Generating deterministic standard and 4K decoder/divider fixtures...")
        standard_fixture = create_fixture(str(args.ffmpeg), temporary,
                                          "standard", "1280x720", 24)
        four_k_fixture = create_fixture(str(args.ffmpeg), temporary,
                                        "4k", "3840x2160", 24)
        standard_decode, standard_results, standard_baseline = benchmark_decoders(
            args, standard_fixture, "standard", batch)
        four_k_decode, four_k_results, _ = benchmark_decoders(
            args, four_k_fixture, "4K", batch)

        profile = standard_baseline.get("profile", {})
        frames = max(1, int(profile.get("frames", 1800)))
        decode_per_frame = float(profile.get("decodeReadSeconds",
                                              profile.get("producerSeconds", 0))) / frames
        eager_per_frame = eager_batch_seconds / (batch * 50)
        compile_enabled = False
        compile_mode = None
        break_even = max(1, args.cutoff_default)
        projected_gain = 0.0
        if best_compile:
            compiled_per_frame = best_compile["steadySeconds"] / (batch * 50)
            before = max(decode_per_frame, eager_per_frame)
            after = max(decode_per_frame, compiled_per_frame)
            projected_gain = ((before - after) / before * 100) if before else 0
            saving = max(0, eager_per_frame - compiled_per_frame)
            if saving:
                break_even = max(1, math.ceil(1.2 * best_compile["coldSeconds"] / saving))
            compile_enabled = projected_gain >= 5.0
            compile_mode = best_compile["mode"]
        say(f"Projected whole-pipeline compiled gain: {projected_gain:.1f}%.")
        if not compile_enabled:
            say("Compilation/CUDA Graphs remain disabled: the measured end-to-end gate was not met.")

        key = worker.policy_key(torch, device, batch, precision)
        policy_path = args.runtime / "transnetv2-performance-policy-v1.json"
        policy = worker.read_policy(args.runtime)
        policy.setdefault("entries", {})[key] = {
            "compiledEnabled": compile_enabled,
            "compileMode": compile_mode,
            "breakEvenFrames": break_even,
            "decodeMode": standard_decode,
            "dividerExact": True,
            "decodeModes": {"h264": standard_decode},
            "decodeExact": {"h264": True},
            "decodeModesByResolution": {
                "h264": {"standard": standard_decode, "4k": four_k_decode}},
            "decodeExactByResolution": {
                "h264": {"standard": True, "4k": True}},
            "projectedGainPercent": projected_gain,
            "measuredUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "batchResults": batches,
            "compileResults": compile_results,
            "decodeResults": standard_results,
            "decodeResultsByResolution": {
                "standard": standard_results, "4k": four_k_results},
        }
        atomic_json(policy_path, policy)
        atomic_json(args.output, {
            "transNetPreferredBatchSize": batch,
            "transNetCompileCutoffFrames": (break_even if compile_enabled
                                               else max(1, args.cutoff_default)),
            "transNetDecodeMode": "auto"
        })
        say(f"Saved exact-compatible performance policy: {policy_path}")
    finally:
        shutil.rmtree(temporary, ignore_errors=True)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        raise SystemExit(130)
    except Exception as error:
        print(f"Benchmark failed: {error}", file=sys.stderr, flush=True)
        raise SystemExit(1)
