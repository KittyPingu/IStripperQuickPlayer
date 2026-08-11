#!/usr/bin/env python3
"""Calibrate VideoMaMa's fastest safe inference batch for the current GPU."""
import argparse
import json
import os
import time
from pathlib import Path

from videomama_worker import gpu_policy_key, load_videomama


def say(message):
    print(message, flush=True)


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def run_candidate(pipeline, torch, batch, width, height):
    from PIL import Image, ImageDraw

    frame = Image.new("RGB", (width, height), (112, 128, 144))
    guide = Image.new("L", (width, height), 0)
    ImageDraw.Draw(guide).ellipse((width // 4, height // 5,
        width * 3 // 4, height * 4 // 5), fill=255)
    torch.cuda.empty_cache()
    torch.cuda.reset_peak_memory_stats()
    started = time.perf_counter()
    mattes = pipeline.run([frame] * batch, [guide] * batch, seed=42,
        mask_cond_mode="vae", fps=30)
    if len(mattes) != batch:
        raise RuntimeError(f"VideoMaMa returned {len(mattes)} frames for batch {batch}")
    torch.cuda.synchronize()
    elapsed = time.perf_counter() - started
    free, total = torch.cuda.mem_get_info()
    peak_reserved = torch.cuda.max_memory_reserved()
    del mattes
    return {"batchSize": batch, "seconds": elapsed, "fps": batch / elapsed,
        "wholeGpuUsedBytes": total - free, "peakTorchReservedBytes": peak_reserved,
        "totalVramBytes": total, "usedPercent": (total - free) * 100 / total}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--batches", type=int, nargs="+", default=(1, 2, 3, 4, 6, 8, 12))
    parser.add_argument("--vram-stop-percent", type=float, default=94.0)
    args = parser.parse_args()
    if not 50 <= args.vram_stop_percent <= 99:
        parser.error("--vram-stop-percent must be between 50 and 99")

    import torch
    if not torch.cuda.is_available():
        raise RuntimeError("VideoMaMa benchmarking requires an NVIDIA CUDA GPU")
    key = gpu_policy_key(torch)
    properties = torch.cuda.get_device_properties(0)
    say(f"GPU: {properties.name} ({properties.total_memory / 1073741824:.1f} GiB)")
    say("Loading the installed VideoMaMa model...")
    pipeline = load_videomama(args.runtime.resolve(), torch)
    pipeline.unet.enable_forward_chunking(chunk_size=1, dim=1)

    results = []
    for batch in sorted(set(max(1, min(value, 12)) for value in args.batches)):
        say(f"Testing batch {batch} at 1024x576...")
        try:
            result = run_candidate(pipeline, torch, batch, 1024, 576)
            pressure = result["usedPercent"] >= args.vram_stop_percent and batch > 1
            result["safe"] = not pressure
            results.append(result)
            say(f"  {result['fps']:.2f} fps; whole-GPU memory "
                f"{result['wholeGpuUsedBytes'] / 1048576:,.0f}/"
                f"{result['totalVramBytes'] / 1048576:,.0f} MiB "
                f"({result['usedPercent']:.1f}%)" +
                (" - stopping before larger batches" if pressure else ""))
            if pressure:
                break
        except torch.cuda.OutOfMemoryError:
            torch.cuda.empty_cache()
            results.append({"batchSize": batch, "safe": False,
                "outOfMemory": True})
            say(f"  batch {batch} ran out of GPU memory; larger batches were skipped")
            break
        except RuntimeError as error:
            if "out of memory" not in str(error).lower():
                raise
            torch.cuda.empty_cache()
            results.append({"batchSize": batch, "safe": False,
                "outOfMemory": True, "message": str(error)})
            say(f"  batch {batch} ran out of GPU memory; larger batches were skipped")
            break

    safe = [value for value in results if value.get("safe")]
    if not safe:
        raise RuntimeError("Even VideoMaMa batch 1 did not complete successfully")
    best = max(safe, key=lambda value: value["fps"])
    selected = best["batchSize"]
    say(f"Recommended VideoMaMa batch: {selected} ({best['fps']:.2f} fps).")

    policy_path = args.runtime / "videomama-performance-policy-v1.json"
    try:
        policy = json.loads(policy_path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        policy = {"schemaVersion": 1, "entries": {}}
    policy.setdefault("entries", {})[key] = {
        "safeBatchSize": selected,
        "modelResolution": "1024x576",
        "vramStopPercent": args.vram_stop_percent,
        "updatedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "candidates": results
    }
    atomic_json(policy_path, policy)
    atomic_json(args.output, {"videoMaMaPreferredBatchSize": selected})


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        raise SystemExit(130)
