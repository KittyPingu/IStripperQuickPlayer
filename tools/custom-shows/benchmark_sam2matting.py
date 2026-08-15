#!/usr/bin/env python3
"""Repeatable cold-job benchmark for the pinned SAM2Matting adapter."""

import argparse
import json
import time
from pathlib import Path

from sam2matting_worker import run_job


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", type=Path, required=True)
    args = parser.parse_args()
    request = json.loads(args.request.read_text(encoding="utf-8"))
    import torch

    torch.cuda.reset_peak_memory_stats()
    started = time.perf_counter()
    result = run_job(request)
    elapsed = time.perf_counter() - started
    frames = sum(item["decodedFrameCount"] for item in result["clips"])
    print(json.dumps({
        "tracker": request["tracker"], "frames": frames,
        "elapsedSeconds": elapsed, "framesPerSecond": frames / max(elapsed, 1e-9),
        "peakAllocatedMiB": torch.cuda.max_memory_allocated() / 1048576,
        "peakReservedMiB": torch.cuda.max_memory_reserved() / 1048576,
        "executionMode": result["executionMode"],
    }, indent=2))


if __name__ == "__main__":
    main()
