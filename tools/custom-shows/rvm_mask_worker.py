#!/usr/bin/env python3
"""Generate review-sized PNG masks with Robust Video Matting."""
import argparse, json, shutil, subprocess, sys
from pathlib import Path

from rvm_worker import executable, load_model, probe, replace_preview
from videomama_worker import model_size


def send(**values):
    print(json.dumps(values, separators=(",", ":")), flush=True)


def read_exact(stream, size):
    value, received = bytearray(size), 0
    view = memoryview(value)
    while received < size:
        count = stream.readinto(view[received:])
        if not count:
            break
        received += count
    return None if received == 0 else value if received == size else value[:received]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--frames", type=Path, required=True)
    parser.add_argument("--masks", type=Path, required=True)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int, required=True)
    parser.add_argument("--alpha-threshold", type=float, default=.5)
    parser.add_argument("--masks-only", action="store_true")
    parser.add_argument("--profile-log", type=Path)
    parser.add_argument("--preview-output", type=Path)
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
    shutil.rmtree(args.masks, ignore_errors=True)
    args.masks.mkdir(parents=True)
    args.alpha_threshold = max(0, min(1, args.alpha_threshold))
    files = []
    decode = None
    if args.masks_only:
        send(status="progress", percent=1,
             message="Streaming source frames into RVM...")
        command = [executable("ffmpeg"), "-hide_banner", "-loglevel", "error",
            "-ss", f"{start:.6f}", "-i", str(source), "-t", f"{end-start:.6f}",
            "-vf", f"fps={rate},scale={review_width}:{review_height}:flags=bilinear,setsar=1",
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]
        decode = subprocess.Popen(command, stdout=subprocess.PIPE)
    else:
        shutil.rmtree(args.frames, ignore_errors=True)
        args.frames.mkdir(parents=True)
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
        count = expected if args.masks_only else len(files)
        index = 0
        while True:
            if args.masks_only:
                data = read_exact(decode.stdout, review_width * review_height * 3)
                if not data:
                    break
                if len(data) != review_width * review_height * 3:
                    raise RuntimeError("RVM source decoder returned a partial frame")
                pixels = np.frombuffer(data, dtype=np.uint8).reshape(
                    review_height, review_width, 3).copy()
            else:
                if index >= len(files):
                    break
                pixels = np.asarray(Image.open(files[index]).convert("RGB"),
                                    dtype=np.uint8).copy()
            tensor = torch.from_numpy(pixels).permute(2, 0, 1).unsqueeze(0) \
                .to(device=device, dtype=torch.float32).div_(255)
            # RVM returns full review-resolution alpha while its encoder works at
            # roughly 512 px, matching the fast custom-show RVM path.
            _, alpha, *rec = model(tensor, *rec,
                downsample_ratio=min(1.0, 512 / max(review_width, review_height)))
            mask = (alpha[0, 0].float().cpu().numpy() >=
                    args.alpha_threshold).astype(np.uint8) * 255
            Image.fromarray(mask, "L").save(args.masks / f"{index + 1:08d}.png",
                                             compress_level=1)
            index += 1
            if index == 1 or index == count or index % 10 == 0:
                if args.preview_output:
                    args.preview_output.mkdir(parents=True, exist_ok=True)
                    for name, image, mode in (
                            ("preview-source.jpg", pixels, "RGB"),
                            ("preview-composite.jpg", mask, "L")):
                        temporary = args.preview_output / (name + ".tmp")
                        Image.fromarray(image, mode).save(
                            temporary, "JPEG", quality=88)
                        replace_preview(temporary, args.preview_output / name)
                send(status="progress", percent=10 + 88 * index / count,
                     message=f"RVM segmented {index}/{count} frames",
                     frame=index - 1)
    generated = len(list(args.masks.glob("*.png")))
    if decode is not None:
        decode.stdout.close()
        if decode.wait() != 0:
            raise RuntimeError("RVM source decoding failed")
    if not generated:
        raise RuntimeError("RVM generated no masks")
    send(status="ready", frameCount=generated, fps=fps, width=review_width,
         height=review_height, device=device.type, precision="BF16" if bf16 else "FP32",
         optimized=False, execution="eager", checkpoint="RVM ResNet50",
         model="rvm", resumed=False, framesFolder=str(args.frames),
         supportsCorrections=False)
    if args.masks_only:
        return
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
