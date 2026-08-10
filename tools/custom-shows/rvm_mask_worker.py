#!/usr/bin/env python3
"""Generate review-sized PNG masks with Robust Video Matting."""
import argparse, json, shutil, subprocess, sys
from pathlib import Path

from rvm_worker import executable, load_model, probe
from sam2_refine_worker import model_size


def send(**values):
    print(json.dumps(values, separators=(",", ":")), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--frames", type=Path, required=True)
    parser.add_argument("--masks", type=Path, required=True)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int, required=True)
    parser.add_argument("--profile-log", type=Path)
    args = parser.parse_args()

    import numpy as np
    import torch
    from PIL import Image

    source, runtime = args.source.resolve(), args.runtime.resolve()
    width, height, rate, fps, duration = probe(source)
    start, end = args.start_ms / 1000, args.end_ms / 1000
    if start < 0 or end <= start or end > duration + .1:
        raise RuntimeError("clip range is outside the source video")
    review_width, review_height = model_size(width, height)
    expected = max(1, round((min(end, duration) - start) * fps))
    shutil.rmtree(args.frames, ignore_errors=True)
    shutil.rmtree(args.masks, ignore_errors=True)
    args.frames.mkdir(parents=True)
    args.masks.mkdir(parents=True)
    send(status="progress", percent=1, message="Extracting RVM review frames...")
    command = [executable("ffmpeg"), "-hide_banner", "-loglevel", "error", "-y",
        "-ss", f"{start:.6f}", "-i", str(source), "-t", f"{end-start:.6f}",
        "-vf", f"fps={rate},scale={review_width}:{review_height}:flags=bilinear,setsar=1",
        "-q:v", "3", "-start_number", "1", str(args.frames / "%08d.jpg")]
    subprocess.run(command, check=True)
    files = sorted(args.frames.glob("*.jpg"))
    if not files:
        raise RuntimeError("RVM frame extraction produced no frames")

    send(status="progress", percent=10, message="Loading RVM ResNet50...")
    torch, model, device = load_model(runtime, "quality")
    bf16 = device.type == "cuda" and torch.cuda.get_device_capability()[0] >= 8
    rec = [None] * 4
    with torch.inference_mode(), torch.autocast(device_type=device.type,
            dtype=torch.bfloat16, enabled=bf16):
        for index, path in enumerate(files):
            pixels = np.asarray(Image.open(path).convert("RGB"), dtype=np.uint8).copy()
            tensor = torch.from_numpy(pixels).permute(2, 0, 1).unsqueeze(0) \
                .to(device=device, dtype=torch.float32).div_(255)
            # RVM returns full review-resolution alpha while its encoder works at
            # roughly 512 px, matching the fast custom-show RVM path.
            _, alpha, *rec = model(tensor, *rec,
                downsample_ratio=min(1.0, 512 / max(review_width, review_height)))
            mask = (alpha[0, 0].float().cpu().numpy() >= .5).astype(np.uint8) * 255
            Image.fromarray(mask, "L").save(args.masks / f"{index + 1:08d}.png",
                                             compress_level=1)
            if index == 0 or index + 1 == len(files) or index % 10 == 0:
                send(status="progress", percent=10 + 88 * (index + 1) / len(files),
                     message=f"RVM segmented {index + 1}/{len(files)} frames",
                     frame=index)
    send(status="ready", frameCount=len(files), fps=fps, width=review_width,
         height=review_height, device=device.type, precision="BF16" if bf16 else "FP32",
         optimized=False, execution="eager", checkpoint="RVM ResNet50",
         model="rvm", resumed=False, framesFolder=str(args.frames),
         supportsCorrections=False)
    for line in sys.stdin:
        if not line.strip():
            continue
        if json.loads(line).get("command") == "quit":
            break


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        send(status="error", message=str(error))
        raise
