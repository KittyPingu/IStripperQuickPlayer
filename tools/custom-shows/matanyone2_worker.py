#!/usr/bin/env python3
"""QuickPlayer MatAnyone 2 worker. Stdout is NDJSON progress only."""
import argparse, json, os, subprocess, sys, time
from pathlib import Path

from rvm_worker import digest, emit, executable, fast_fp16, probe, replace_preview

COMMIT = "0079197acd6d16a741f71558809c06c586c579e0"
WEIGHTS_HASH = "5e9821e4087231427376b437c85bb6e072b41e582314f06fd524f75bc4af5914"

def processing_size(width, height, max_size):
    if max_size <= 0 or min(width, height) <= max_size:
        return width, height
    scale = max_size / min(width, height)
    return int(width * scale), int(height * scale)

def load_model(runtime):
    import torch
    root = runtime / "matanyone2"
    weights = runtime / "checkpoints" / "matanyone2.pth"
    marker = runtime / "MATANYONE2_COMMIT"
    if not marker.is_file() or marker.read_text().strip() != COMMIT:
        raise RuntimeError("MatAnyone 2 is not installed. Run Install / Update Processing Tools and select it.")
    if not weights.is_file() or digest(weights) != WEIGHTS_HASH:
        raise RuntimeError("MatAnyone 2 weights validation failed; run setup again.")
    sys.path.insert(0, str(root))
    from hydra.core.global_hydra import GlobalHydra
    GlobalHydra.instance().clear()
    from matanyone2.utils.get_default_model import get_matanyone2_model
    from matanyone2.inference.inference_core import InferenceCore
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = get_matanyone2_model(str(weights), device)
    return torch, InferenceCore(model, cfg=model.cfg, device=device), device

def process(args):
    import numpy as np
    from PIL import Image
    emit("startup", 0, "Loading MatAnyone 2...")
    torch, processor, device = load_model(args.runtime.resolve())
    fp16 = device.type == "cuda" and fast_fp16(torch.cuda.get_device_capability(device))
    source, output = args.source.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    width, height, frame_rate, fps, source_duration = probe(source)
    process_width, process_height = processing_size(width, height, args.max_size)
    emit("startup", 0, f"MatAnyone 2 inference at {process_width}x{process_height}; "
         f"output remains {width}x{height}")
    start = args.start_ms / 1000
    end = source_duration if args.end_ms is None else args.end_ms / 1000
    if start < 0 or end <= start or end > source_duration + .1:
        raise RuntimeError("clip range is outside the source video")
    duration = min(end, source_duration) - start
    total = max(1, round(duration * fps))
    ffmpeg = executable("ffmpeg")
    decode = subprocess.Popen([ffmpeg, "-v", "error", "-ss", f"{start:.6f}",
        "-i", str(source), "-t", f"{duration:.6f}", "-map", "0:v:0",
        "-vf", f"fps={frame_rate},scale={width}:{height}:flags=lanczos,setsar=1",
        "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"], stdout=subprocess.PIPE)
    encoders = subprocess.check_output([ffmpeg, "-hide_banner", "-encoders"], text=True, errors="replace")
    nvenc = width >= 256 and height >= 128 and "h264_nvenc" in encoders and subprocess.run([ffmpeg, "-v", "error", "-f", "lavfi", "-i",
        "color=size=256x256:duration=0.1", "-frames:v", "1", "-c:v", "h264_nvenc", "-f", "null", "-"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
    video_codec = ["-c:v", "h264_nvenc", "-preset", "p7", "-tune", "hq", "-cq", "19"] if nvenc else ["-c:v", "libx264", "-preset", "slow", "-crf", "18"]
    alpha_codec = ["-c:v", "h264_nvenc", "-preset", "p7", "-tune", "hq", "-cq", "10"] if nvenc else ["-c:v", "libx264", "-preset", "medium", "-crf", "10"]
    encode = subprocess.Popen([ffmpeg, "-y", "-v", "warning", "-f", "rawvideo", "-pix_fmt", "rgba",
        "-s", f"{width}x{height}", "-r", frame_rate, "-i", "pipe:0",
        "-ss", f"{start:.6f}", "-t", f"{duration:.6f}", "-i", str(source),
        "-filter_complex", "[0:v]split=2[rgb][rgba];[rgb]format=yuv420p[vout];[rgba]alphaextract,format=yuv420p[aout]",
        "-map", "[vout]", "-map", "1:a:0?", *video_codec, "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", str(output / "foreground.mp4"),
        "-map", "[aout]", *alpha_codec, "-pix_fmt", "yuv420p", str(output / "alpha.mkv")], stdin=subprocess.PIPE)
    frame_bytes = width * height * 3
    last_preview = 0
    last_frame = last_alpha = None
    yy, xx = np.indices((height, width))
    checker = np.where((((xx // 24) + (yy // 24)) & 1)[..., None], 190, 125).astype(np.uint8)

    def save_preview(frame, alpha, force=False):
        nonlocal last_preview
        now = time.monotonic()
        if not force and now - last_preview < .5: return
        opacity = alpha[..., None].astype(np.float32) / 255
        composite = np.rint(frame * opacity + checker * (1 - opacity)).astype(np.uint8)
        for name, data in (("preview-source.jpg", frame),
                           ("preview-composite.jpg", composite)):
            temporary = output / (name + ".tmp")
            Image.fromarray(data, "RGB").save(temporary, "JPEG", quality=86)
            replace_preview(temporary, output / name)
        last_preview = now

    def read_frame():
        value = bytearray(frame_bytes)
        view, received = memoryview(value), 0
        while received < frame_bytes:
            size = decode.stdout.readinto(view[received:])
            if not size: break
            received += size
        if not received: return None
        if received != frame_bytes: raise RuntimeError("source decoder returned a partial frame")
        return np.frombuffer(value, np.uint8).reshape(height, width, 3)

    def tensor(frame):
        if (process_width, process_height) != (width, height):
            frame = np.asarray(Image.fromarray(frame, "RGB").resize(
                (process_width, process_height), Image.Resampling.BILINEAR))
        return torch.from_numpy(frame.copy()).permute(2, 0, 1).to(
            device=device, dtype=torch.float32).div_(255)

    def write(frame, output_prob):
        nonlocal last_frame, last_alpha
        alpha = processor.output_prob_to_mask(output_prob).clamp(0, 1).mul(255).byte().cpu().numpy()
        if alpha.shape != (height, width):
            alpha = np.asarray(Image.fromarray(alpha, "L").resize(
                (width, height), Image.Resampling.BILINEAR))
        last_frame, last_alpha = frame, alpha
        save_preview(frame, alpha)
        rgba = np.empty((height, width, 4), dtype=np.uint8)
        rgba[:, :, :3] = frame
        rgba[:, :, 3] = alpha
        output_bytes = memoryview(rgba).cast("B")
        while output_bytes:
            written = encode.stdin.write(output_bytes)
            if not written: raise RuntimeError("FFmpeg output encoder closed unexpectedly")
            output_bytes = output_bytes[written:]

    try:
        first = read_frame()
        if first is None: raise RuntimeError("The source video has no frames")
        mask = Image.open(args.mask).convert("L").resize(
            (process_width, process_height), Image.Resampling.NEAREST)
        mask_tensor = torch.from_numpy(np.asarray(mask, dtype=np.uint8).copy()).float().to(device)
        first_tensor = tensor(first)
        emit("inference", 0, "Warming up MatAnyone 2 on the initial mask...")
        with torch.inference_mode(), torch.amp.autocast(device_type=device.type, enabled=fp16):
            processor.step(first_tensor, mask_tensor, objects=[1])
            for _ in range(11):
                output_prob = processor.step(first_tensor, first_frame_pred=True)
        write(first, output_prob)
        count = 1
        emit("inference", count * 100 / total, f"Processed {count}/{total} frames")
        while (frame := read_frame()) is not None:
            with torch.inference_mode(), torch.amp.autocast(device_type=device.type, enabled=fp16):
                output_prob = processor.step(tensor(frame))
            write(frame, output_prob)
            count += 1
            emit("inference", min(99, count * 100 / total), f"Processed {count}/{total} frames")
        if last_frame is not None: save_preview(last_frame, last_alpha, force=True)
        encode.stdin.close(); decode.stdout.close()
        if decode.wait() != 0: raise RuntimeError("source normalization failed")
        if encode.wait() != 0: raise RuntimeError("FFmpeg output encoding failed")
    except BaseException:
        decode.kill(); encode.kill(); raise
    (output / "result.json").write_text(json.dumps({"width": width, "height": height,
        "frameRate": frame_rate, "durationMs": round(count * 1000 / fps)}, indent=2))
    emit("complete", 100, "MatAnyone 2 foreground and alpha are ready for preview")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--mask", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--max-size", type=int,
                        choices=(0, 256, 384, 512, 768, 1024), default=512)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        assert processing_size(3840, 2160, 512) == (910, 512)
        assert processing_size(1920, 1080, 0) == (1920, 1080)
        print("MatAnyone 2 worker self-test passed")
    elif not all((args.source, args.mask, args.output, args.runtime)):
        parser.error("--source, --mask, --output, and --runtime are required")
    else:
        process(args)

if __name__ == "__main__":
    try: main()
    except Exception as error:
        emit("error", 0, str(error)); raise
