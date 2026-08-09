#!/usr/bin/env python3
"""QuickPlayer TransNetV2 worker. Stdout is NDJSON progress only."""
import argparse, hashlib, json, os, subprocess, sys
from pathlib import Path

COMMIT = "85cef72af9a916bdfd7cc94a670c9cdfbf12d1ed"
SOURCE_HASH = "f7c1d437465579a8ec28a5add19853d2cb2755248ea4a4207678210a609428e1"
WEIGHTS_HASH = "46520d66d4bf60414a4d82e0e94a92442ff950e34517a3718b2e54815e642b53"

def emit(stage, percent, message, **extra):
    print(json.dumps({"stage": stage, "percent": percent,
                      "message": message, **extra}), flush=True)

def digest(path):
    value = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()

def frame_rate(source, ffprobe):
    output = subprocess.check_output([ffprobe, "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=avg_frame_rate,r_frame_rate:format=duration",
        "-of", "json", source], text=True)
    data = json.loads(output)
    stream = data["streams"][0]
    rate = stream.get("avg_frame_rate") or stream.get("r_frame_rate") or "25/1"
    numerator, denominator = map(int, rate.split("/"))
    fps = numerator / denominator if numerator and denominator else 25.0
    return fps, float(data.get("format", {}).get("duration") or 0)

def load_model(runtime):
    import torch
    folder = runtime / "transnetv2"
    source = folder / "transnetv2_pytorch.py"
    weights = folder / "transnetv2-pytorch-weights.pth"
    marker = runtime / "TRANSNETV2_COMMIT"
    if not marker.is_file() or marker.read_text().strip() != COMMIT:
        raise RuntimeError("TransNetV2 is not installed. Run Install / Update Processing Tools and choose Yes.")
    if not source.is_file() or digest(source) != SOURCE_HASH:
        raise RuntimeError("TransNetV2 source validation failed; run setup again.")
    if not weights.is_file() or digest(weights) != WEIGHTS_HASH:
        raise RuntimeError("TransNetV2 weights validation failed; run setup again.")
    sys.path.insert(0, str(folder))
    from transnetv2_pytorch import TransNetV2
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = TransNetV2().eval().to(device)
    model.load_state_dict(torch.load(weights, map_location=device, weights_only=True))
    return torch, model, device

def decode(source, ffmpeg, expected_frames, use_cuda):
    import numpy as np
    emit("decode", 5, "Reading video for scene detection...")
    frame_bytes = 48 * 27 * 3
    def run(command):
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        output = bytearray()
        while block := process.stdout.read(frame_bytes * 250):
            output.extend(block)
            if expected_frames:
                count = len(output) // frame_bytes
                emit("decode", min(89, 5 + 84 * count / expected_frames),
                     f"Reading video {count}/{expected_frames} frames")
        error = process.stderr.read().decode(errors="replace").strip()
        return output, error, process.wait()

    cpu = [ffmpeg, "-v", "error", "-i", source, "-map", "0:v:0", "-an",
        "-s", "48x27", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]
    if use_cuda:
        gpu = [ffmpeg, "-v", "error", "-hwaccel", "cuda",
            "-hwaccel_output_format", "cuda", "-i", source, "-map", "0:v:0", "-an",
            "-vf", "scale_cuda=48:28,hwdownload,format=nv12,format=rgb24,crop=48:27",
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]
        output, error, exit_code = run(gpu)
        if exit_code or not output or len(output) % frame_bytes:
            emit("decode", 5, "GPU decode unavailable for this video; using CPU decode...")
            output, error, exit_code = run(cpu)
    else:
        output, error, exit_code = run(cpu)
    if exit_code:
        raise RuntimeError(error or "FFmpeg video decode failed")
    if not output or len(output) % frame_bytes:
        raise RuntimeError("FFmpeg returned incomplete scene-detection frames")
    return np.frombuffer(output, np.uint8).reshape((-1, 27, 48, 3))

def predict(frames, torch, model, device):
    import numpy as np
    before = 25
    after = 25 + 50 - (len(frames) % 50 if len(frames) % 50 else 50)
    padded = np.concatenate(([frames[0]] * before, frames, [frames[-1]] * after))
    predictions = []
    windows = (len(padded) - 100) // 50 + 1
    use_fp16 = device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 7
    with torch.inference_mode():
        for window, start in enumerate(range(0, len(padded) - 99, 50), 1):
            inputs = torch.from_numpy(padded[start:start + 100].copy())[None].to(device)
            try:
                with torch.autocast("cuda", dtype=torch.float16, enabled=use_fp16):
                    logits, _ = model(inputs)
            except RuntimeError:
                if not use_fp16:
                    raise
                use_fp16 = False
                torch.cuda.empty_cache()
                emit("detect", 90 + 9 * (window - 1) / windows,
                     "FP16 unavailable; continuing with FP32 CUDA")
                logits, _ = model(inputs)
            predictions.append(torch.sigmoid(logits)[0, 25:75, 0].cpu().numpy())
            emit("detect", 90 + 9 * window / windows,
                 f"Detecting scenes {min(window * 50, len(frames))}/{len(frames)} frames")
    return np.concatenate(predictions)[:len(frames)]

def dividers(predictions, fps, threshold=0.5):
    import numpy as np
    found, start = [], None
    for index, is_transition in enumerate(predictions > threshold):
        if is_transition and start is None:
            start = index
        if start is not None and (not is_transition or index == len(predictions) - 1):
            end = index if not is_transition else index + 1
            peak = start + int(np.argmax(predictions[start:end]))
            at = round(peak * 1000 / fps)
            if at >= 250 and (not found or at - found[-1] >= 500):
                found.append(at)
            start = None
    return found

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--runtime", required=True)
    args = parser.parse_args()
    ffmpeg = os.environ.get("IQP_FFMPEG", "ffmpeg")
    ffprobe = os.environ.get("IQP_FFPROBE", "ffprobe")
    emit("load", 0, "Loading TransNetV2...")
    torch, model, device = load_model(Path(args.runtime))
    fps, duration = frame_rate(args.source, ffprobe)
    frames = decode(args.source, ffmpeg, round(fps * duration), device.type == "cuda")
    precision = "FP16" if device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 7 else "FP32"
    emit("load", 90, f"Loaded {len(frames)} frames; using {device.type.upper()} {precision}")
    cuts = dividers(predict(frames, torch, model, device), fps)
    duration_ms = round(len(frames) * 1000 / fps)
    cuts = [cut for cut in cuts if cut <= duration_ms - 250]
    emit("complete", 100, f"Detected {len(cuts)} scene changes", dividersMs=cuts)

if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(str(error), file=sys.stderr, flush=True)
        raise SystemExit(1)
