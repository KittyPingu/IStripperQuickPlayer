#!/usr/bin/env python3
"""Benchmark QuickPlayer's MatAnyone2 tensor path without video decode/encode."""
import argparse, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from matanyone2_worker import load_model
from rvm_worker import fast_fp16

parser = argparse.ArgumentParser()
parser.add_argument("--runtime", type=Path, required=True)
parser.add_argument("--device", choices=("cpu", "cuda"), required=True)
parser.add_argument("--frames", type=int, default=3)
parser.add_argument("--width", type=int, default=640)
parser.add_argument("--height", type=int, default=360)
args = parser.parse_args()

import numpy as np
import torch
if args.device == "cuda" and not torch.cuda.is_available():
    raise SystemExit("CUDA is unavailable")
torch, processor, _ = load_model(args.runtime.resolve())
device = torch.device(args.device)
processor.network.to(device)
processor.device = device
fp16 = device.type == "cuda" and fast_fp16(torch.cuda.get_device_capability(device))
frame = torch.rand((3, args.height, args.width), device=device)
mask = torch.zeros((args.height, args.width), device=device)
mask[args.height // 4:args.height * 3 // 4,
     args.width // 4:args.width * 3 // 4] = 255

started = time.perf_counter()
with torch.inference_mode(), torch.amp.autocast(device_type=device.type, enabled=fp16):
    processor.step(frame, mask, objects=[1])
    for _ in range(11): output = processor.step(frame, first_frame_pred=True)
warmup = time.perf_counter() - started

started = time.perf_counter()
for _ in range(args.frames):
    with torch.inference_mode(), torch.amp.autocast(device_type=device.type, enabled=fp16):
        output = processor.step(frame)
    processor.output_prob_to_mask(output).byte().cpu().numpy()
elapsed = time.perf_counter() - started
print(f"matanyone2,{device.type},{'FP16' if fp16 else 'FP32'},"
      f"{args.width}x{args.height},frames={args.frames},warmup={warmup:.3f},"
      f"seconds={elapsed:.3f},fps={args.frames / elapsed:.3f}")
