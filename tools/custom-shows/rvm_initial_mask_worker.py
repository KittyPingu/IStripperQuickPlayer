#!/usr/bin/env python3
"""Create cleaned RVM person masks for one or more SAM2 initialization frames."""
import argparse, json, subprocess
from pathlib import Path

from matanyone2_worker import clean_rvm_mask
from rvm_worker import executable, fast_fp16, load_model, probe
from custom_show_worker import model_size


def send(**values):
    print(json.dumps(values, separators=(",", ":")), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--mask", type=Path)
    parser.add_argument("--frame-ms", type=int)
    parser.add_argument("--request", type=Path)
    parser.add_argument("--alpha-threshold", type=float, default=.4)
    args = parser.parse_args()

    import numpy as np
    import torch
    from PIL import Image

    if args.request is not None:
        request = json.loads(args.request.read_text(encoding="utf-8-sig"))
        source = Path(request["source"]).resolve()
        threshold = float(request.get("alphaThreshold", args.alpha_threshold))
        items = request.get("items") or []
    else:
        if args.source is None or args.mask is None or args.frame_ms is None:
            parser.error("--source, --mask, and --frame-ms are required without --request")
        source = args.source.resolve()
        threshold = args.alpha_threshold
        items = [{"mask": str(args.mask), "frameMs": args.frame_ms}]
    if not items:
        raise RuntimeError("No RVM initialization masks were requested")

    width, height, _, _, duration = probe(source)
    review_width, review_height = model_size(width, height)
    torch, model, device = load_model(args.runtime.resolve(), "quality")
    fp16 = device.type == "cuda" and fast_fp16(
        torch.cuda.get_device_capability(device))
    threshold = max(.1, min(.9, threshold))
    count = len(items)
    expected = review_width * review_height * 3
    for index, item in enumerate(items):
        frame_ms = int(item["frameMs"])
        destination = Path(item["mask"])
        position = frame_ms / 1000
        if position < 0 or position > duration + .1:
            raise RuntimeError(
                f"RVM initialization frame {index + 1}/{count} is outside the source video")
        base = index * 100 / count
        send(stage="rvm-initial-mask", percent=base + 5 / count,
             message=f"Decoding RVM initialization frame {index + 1}/{count}...")
        command = [executable("ffmpeg"), "-v", "error", "-ss", f"{position:.6f}",
            "-i", str(source), "-frames:v", "1", "-vf",
            f"scale={review_width}:{review_height}:flags=bilinear,setsar=1",
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]
        decoded = subprocess.run(command, stdout=subprocess.PIPE,
                                 stderr=subprocess.PIPE)
        if decoded.returncode != 0 or len(decoded.stdout) < expected:
            raise RuntimeError(decoded.stderr.decode(errors="replace").strip() or
                               "RVM initialization frame could not be decoded")
        frame = np.frombuffer(decoded.stdout[:expected], np.uint8).reshape(
            review_height, review_width, 3).copy()
        send(stage="rvm-initial-mask", percent=base + 20 / count,
             message=f"Creating RVM person mask {index + 1}/{count}...")
        tensor = torch.from_numpy(frame).permute(2, 0, 1).unsqueeze(0).to(
            device=device, dtype=torch.float32).div_(255)
        with torch.inference_mode(), torch.autocast(device_type=device.type,
                dtype=torch.float16, enabled=fp16):
            _, alpha, *_ = model(tensor, *([None] * 4), downsample_ratio=1)
        mask = clean_rvm_mask(alpha[0, 0].float().cpu().numpy(),
                              threshold=threshold)
        if not mask.any():
            raise RuntimeError(
                f"RVM could not find a usable person mask for scene {index + 1}/{count}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        Image.fromarray(mask, "L").save(destination)
        send(stage="rvm-initial-mask", percent=(index + 1) * 100 / count,
             message=f"RVM person mask {index + 1}/{count} ready for SAM2")
    send(stage="rvm-initial-mask", percent=100,
         message=f"{count} RVM person mask{'s' if count != 1 else ''} ready for SAM2",
         width=review_width,
         height=review_height, device=device.type,
         precision="FP16" if fp16 else "FP32")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        send(status="error", message=str(error))
        raise
