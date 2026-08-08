#!/usr/bin/env python3
"""QuickPlayer ViTMatte worker. Stdout is NDJSON progress only."""
import argparse, json, subprocess
from pathlib import Path

from rvm_worker import emit, executable, probe
from videomama_worker import encoder, write_preview

MODELS = {
    "s": ("vitmatte-s", "VITMATTE_S_REVISION",
          "6a58ad7646403c1df626fbd746900aec7361ea1d"),
    "b": ("vitmatte-b", "VITMATTE_B_REVISION",
          "bf486d01a7d9e3dbcc8400f7942835caf0eaf76e"),
}


def trimap_from_mask(mask, cv2):
    mask = (mask >= 128).astype("uint8")
    radius = max(3, round(max(mask.shape) / 256))
    kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE,
                                       (radius * 2 + 1, radius * 2 + 1))
    certain = cv2.erode(mask, kernel)
    possible = cv2.dilate(mask, kernel)
    trimap = possible * 128
    trimap[certain != 0] = 255
    return trimap


def alpha_bytes(alpha):
    return (alpha * 255).byte().cpu().numpy()


def process(args):
    import cv2
    import numpy as np
    import torch
    from PIL import Image
    from transformers import VitMatteForImageMatting, VitMatteImageProcessor

    runtime, source, output = args.runtime.resolve(), args.source.resolve(), args.output.resolve()
    model_folder, marker, revision = MODELS[args.model]
    model_path = runtime / model_folder
    if not model_path.is_dir() or not (runtime / marker).is_file() or \
            (runtime / marker).read_text().strip() != revision:
        raise RuntimeError(f"ViTMatte-{args.model.upper()} is not installed; run setup again")
    masks = sorted(args.mask_folder.resolve().glob("*.png"))
    if not masks:
        raise RuntimeError("The corrected SAM2 mask sequence is missing")

    width, height, frame_rate, fps, source_duration = probe(source)
    start = args.start_ms / 1000
    end = source_duration if args.end_ms is None else args.end_ms / 1000
    if start < 0 or end <= start or end > source_duration + .1:
        raise RuntimeError("clip range is outside the source video")
    duration = min(end, source_duration) - start
    expected = max(1, round(duration * fps))
    if abs(len(masks) - expected) > 1:
        raise RuntimeError(
            f"SAM2 returned {len(masks)} masks for approximately {expected} source frames")

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    fp16 = device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 7
    emit("startup", 0, f"Loading ViTMatte-{args.model.upper()} on "
         f"{device.type.upper()}/{('FP16' if fp16 else 'FP32')}...")
    processor = VitMatteImageProcessor.from_pretrained(model_path, local_files_only=True)
    model = VitMatteForImageMatting.from_pretrained(
        model_path, local_files_only=True).eval().to(device)

    output.mkdir(parents=True, exist_ok=True)
    ffmpeg = executable("ffmpeg")
    decode = subprocess.Popen([ffmpeg, "-v", "error", "-ss", f"{start:.6f}",
        "-i", str(source), "-t", f"{duration:.6f}", "-map", "0:v:0", "-vf",
        f"fps={frame_rate},scale={width}:{height}:flags=lanczos,setsar=1",
        "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"], stdout=subprocess.PIPE)
    encode = encoder(ffmpeg, source, output, start, duration, frame_rate, width, height)
    frame_bytes, count, last_source, last_alpha = width * height * 3, 0, None, None

    def read_frame():
        value, received = bytearray(frame_bytes), 0
        view = memoryview(value)
        while received < frame_bytes:
            size = decode.stdout.readinto(view[received:])
            if not size: break
            received += size
        if received == 0: return None
        if received != frame_bytes: raise RuntimeError("source decoder returned a partial frame")
        return np.frombuffer(value, np.uint8).reshape(height, width, 3)

    def infer(frames, trimaps):
        try:
            inputs = processor(images=[Image.fromarray(frame) for frame in frames],
                trimaps=[Image.fromarray(value, "L") for value in trimaps],
                return_tensors="pt")
            inputs = {name: value.to(device, non_blocking=device.type == "cuda")
                      for name, value in inputs.items()}
            with torch.inference_mode(), torch.autocast(device_type=device.type,
                    dtype=torch.float16, enabled=fp16):
                alphas = model(**inputs).alphas[:, 0, :height, :width].clamp_(0, 1)
            return list(alpha_bytes(alphas))
        except torch.OutOfMemoryError:
            if len(frames) == 1: raise
            if device.type == "cuda": torch.cuda.empty_cache()
            split = len(frames) // 2
            emit("inference", count * 100 / len(masks),
                 f"Memory limit; retrying {len(frames)} frames as smaller batches")
            return infer(frames[:split], trimaps[:split]) + \
                infer(frames[split:], trimaps[split:])

    try:
        batch_size = max(1, min(args.batch_size, 12))
        while count < len(masks):
            frames, trimaps = [], []
            for mask_path in masks[count:count + batch_size]:
                frame = read_frame()
                if frame is None: break
                mask = cv2.imread(str(mask_path), cv2.IMREAD_GRAYSCALE)
                if mask is None: raise RuntimeError(f"Could not read {mask_path.name}")
                if mask.shape != (height, width):
                    mask = cv2.resize(mask, (width, height), interpolation=cv2.INTER_NEAREST)
                frames.append(frame); trimaps.append(trimap_from_mask(mask, cv2))
            if not frames: break
            for source_frame, trimap, alpha in zip(frames, trimaps, infer(frames, trimaps)):
                alpha[trimap == 0] = 0
                alpha[trimap == 255] = 255
                rgba = np.empty((height, width, 4), np.uint8)
                rgba[:, :, :3], rgba[:, :, 3] = source_frame, alpha
                output_bytes = memoryview(rgba).cast("B")
                while output_bytes:
                    written = encode.stdin.write(output_bytes)
                    if not written:
                        raise RuntimeError("FFmpeg output encoder closed unexpectedly")
                    output_bytes = output_bytes[written:]
                count += 1
                last_source, last_alpha = source_frame, alpha
                write_preview(output, source_frame, alpha)
                emit("inference", min(99, count * 100 / len(masks)),
                     f"Processed {count}/{len(masks)} frames")
        if count != len(masks):
            raise RuntimeError(f"source decoding ended after {count}/{len(masks)} frames")
        encode.stdin.close(); decode.stdout.close()
        if decode.wait() != 0: raise RuntimeError("source normalization failed")
        if encode.wait() != 0: raise RuntimeError("FFmpeg output encoding failed")
        if last_source is not None: write_preview(output, last_source, last_alpha)
        (output / "result.json").write_text(json.dumps({"width": width,
            "height": height, "frameRate": frame_rate,
            "durationMs": round(count * 1000 / fps)}, indent=2))
        emit("complete", 100,
             f"ViTMatte-{args.model.upper()} foreground and alpha are ready for preview")
    except BaseException:
        if decode.poll() is None: decode.kill()
        if encode.poll() is None: encode.kill()
        raise


def self_test():
    import cv2, numpy as np, torch
    mask = np.zeros((64, 64), np.uint8); mask[16:48, 16:48] = 255
    trimap = trimap_from_mask(mask, cv2)
    assert trimap[32, 32] == 255 and trimap[0, 0] == 0 and 128 in trimap
    with torch.inference_mode(): alpha = torch.tensor([0., .5, 1.])
    assert alpha_bytes(alpha).tolist() == [0, 127, 255]
    print("ViTMatte worker self-test passed")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--mask-folder", type=Path)
    parser.add_argument("--model", choices=("s", "b"), default="s")
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--batch-size", type=int, default=3)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test: self_test()
    elif not all((args.source, args.output, args.runtime, args.mask_folder)):
        parser.error("--source, --output, --runtime, and --mask-folder are required")
    else: process(args)


if __name__ == "__main__":
    try: main()
    except Exception as error:
        emit("error", 0, str(error)); raise
