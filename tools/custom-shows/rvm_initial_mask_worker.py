#!/usr/bin/env python3
"""Create a cleaned RVM person mask for one SAM2 initialization frame."""
import argparse, json, subprocess
from pathlib import Path

from matanyone2_worker import clean_rvm_mask
from rvm_worker import executable, fast_fp16, load_model, probe
from videomama_worker import model_size


def send(**values):
    print(json.dumps(values, separators=(",", ":")), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--mask", type=Path, required=True)
    parser.add_argument("--frame-ms", type=int, required=True)
    parser.add_argument("--alpha-threshold", type=float, default=.4)
    args = parser.parse_args()

    import numpy as np
    import torch
    from PIL import Image

    source = args.source.resolve()
    width, height, _, _, duration = probe(source)
    position = args.frame_ms / 1000
    if position < 0 or position > duration + .1:
        raise RuntimeError("RVM initialization frame is outside the source video")
    review_width, review_height = model_size(width, height)
    send(stage="rvm-initial-mask", percent=5,
         message="Decoding the SAM2 initialization frame...")
    command = [executable("ffmpeg"), "-v", "error", "-ss", f"{position:.6f}",
        "-i", str(source), "-frames:v", "1", "-vf",
        f"scale={review_width}:{review_height}:flags=bilinear,setsar=1",
        "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]
    decoded = subprocess.run(command, stdout=subprocess.PIPE,
                             stderr=subprocess.PIPE)
    expected = review_width * review_height * 3
    if decoded.returncode != 0 or len(decoded.stdout) < expected:
        raise RuntimeError(decoded.stderr.decode(errors="replace").strip() or
                           "RVM initialization frame could not be decoded")
    frame = np.frombuffer(decoded.stdout[:expected], np.uint8).reshape(
        review_height, review_width, 3).copy()

    send(stage="rvm-initial-mask", percent=20,
         message="Creating the RVM person mask...")
    torch, model, device = load_model(args.runtime.resolve(), "quality")
    fp16 = device.type == "cuda" and fast_fp16(
        torch.cuda.get_device_capability(device))
    tensor = torch.from_numpy(frame).permute(2, 0, 1).unsqueeze(0).to(
        device=device, dtype=torch.float32).div_(255)
    with torch.inference_mode(), torch.autocast(device_type=device.type,
            dtype=torch.float16, enabled=fp16):
        _, alpha, *_ = model(tensor, *([None] * 4), downsample_ratio=1)
    threshold = max(.1, min(.9, args.alpha_threshold))
    mask = clean_rvm_mask(alpha[0, 0].float().cpu().numpy(),
                          threshold=threshold)
    if not mask.any():
        raise RuntimeError("RVM could not find a usable person mask on this frame")
    args.mask.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(mask, "L").save(args.mask)
    send(stage="rvm-initial-mask", percent=100,
         message="RVM person mask ready for SAM2", width=review_width,
         height=review_height, device=device.type,
         precision="FP16" if fp16 else "FP32")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        send(status="error", message=str(error))
        raise
