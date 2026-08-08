#!/usr/bin/env python3
"""Benchmark QuickPlayer's RVM tensor path without video decode/encode."""
import argparse, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from rvm_worker import fast_fp16, load_model

parser = argparse.ArgumentParser()
parser.add_argument("--runtime", type=Path, required=True)
parser.add_argument("--device", choices=("cpu", "cuda"), required=True)
parser.add_argument("--preset", choices=("fast", "quality"), required=True)
parser.add_argument("--frames", type=int, default=6)
parser.add_argument("--width", type=int, default=1920)
parser.add_argument("--height", type=int, default=1080)
parser.add_argument("--detail", type=int, default=512)
args = parser.parse_args()

import torch
if args.device == "cuda" and not torch.cuda.is_available():
    raise SystemExit("CUDA is unavailable")
torch, model, _ = load_model(args.runtime.resolve(), args.preset)
device = torch.device(args.device)
model.to(device)
fp16 = device.type == "cuda" and fast_fp16(torch.cuda.get_device_capability(device))
dtype = torch.float16 if fp16 else torch.float32
downsample = min(args.detail / max(args.width, args.height), 1)
host = torch.randint(0, 256, (1, args.height, args.width, 3), dtype=torch.uint8)

def run(state):
    frame = host.to(device=device, dtype=dtype).permute(0, 3, 1, 2).div_(255)
    with torch.inference_mode(), torch.autocast(device_type=device.type,
                                                dtype=torch.float16, enabled=fp16):
        foreground, alpha, *state = model(frame, *state, downsample_ratio=downsample)
    torch.cat((foreground, alpha), 1).clamp_(0, 1).mul_(255).byte().cpu().numpy()
    return state

state = [None] * 4
state = run(state)
started = time.perf_counter()
for _ in range(args.frames): state = run(state)
elapsed = time.perf_counter() - started
print(f"{args.preset},{device.type},{'FP16' if fp16 else 'FP32'},"
      f"{args.width}x{args.height},detail={args.detail},frames={args.frames},"
      f"seconds={elapsed:.3f},fps={args.frames / elapsed:.3f}")
