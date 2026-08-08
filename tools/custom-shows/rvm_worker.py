#!/usr/bin/env python3
"""QuickPlayer Robust Video Matting worker. Stdout is NDJSON progress only."""
import argparse, hashlib, json, os, shutil, subprocess, sys, time
from pathlib import Path

RVM_COMMIT = "53d74c6826735f01f4406b5ca9075eee27bec094"
WEIGHTS = {
    "fast": ("mobilenetv3", "rvm_mobilenetv3.pth", "3c7c1d92033f7c38d6577c481d13a195d7d80a159b960f4f3119ac7b534cf4f8"),
    "quality": ("resnet50", "rvm_resnet50.pth", "c191a807251164c073dce5fa408e7a816070d539b882b2a3150330a9fec112ce"),
}

def emit(stage, percent=0, message=""):
    print(json.dumps({"stage": stage, "percent": percent, "message": message}), flush=True)

def replace_preview(temporary, destination):
    for delay in (0, .02, .05):
        if delay: time.sleep(delay)
        try:
            os.replace(temporary, destination)
            return
        except PermissionError:
            pass
    try: Path(temporary).unlink(missing_ok=True)
    except OSError: pass

def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""): h.update(block)
    return h.hexdigest()

def executable(name):
    configured = os.environ.get("IQP_" + name.upper())
    if configured and Path(configured).is_file(): return configured
    found = shutil.which(name)
    if not found: raise RuntimeError(f"{name} was not found")
    return found

def fast_fp16(capability):
    return capability[0] >= 7

def aggregate_percent(completed, duration, total, percent):
    return 100 * (completed + duration * max(0, min(100, percent)) / 100) / total

def probe(source):
    command = [executable("ffprobe"), "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate:stream_side_data=rotation:format=duration",
        "-of", "json", str(source)]
    data = json.loads(subprocess.check_output(command, text=True))
    stream = data["streams"][0]
    width, height = int(stream["width"]), int(stream["height"])
    rotation = next((abs(int(x.get("rotation", 0))) for x in stream.get("side_data_list", [])), 0)
    if rotation in (90, 270): width, height = height, width
    width -= width % 2; height -= height % 2
    rate = stream.get("avg_frame_rate") or stream.get("r_frame_rate") or "25/1"
    n, d = map(int, rate.split("/"))
    if not n or not d: n, d, rate = 25, 1, "25/1"
    duration = float(data.get("format", {}).get("duration") or 0)
    if width <= 0 or height <= 0 or duration <= 0: raise RuntimeError("invalid source video metadata")
    return width, height, f"{n}/{d}", n / d, duration

def load_model(runtime, preset):
    import torch
    rvm = runtime / "rvm"
    commit_file = runtime / "RVM_COMMIT"
    if not commit_file.is_file() or commit_file.read_text().strip() != RVM_COMMIT:
        raise RuntimeError("RVM commit validation failed")
    architecture, filename, expected = WEIGHTS[preset]
    checkpoint = runtime / "checkpoints" / filename
    if not checkpoint.is_file() or digest(checkpoint) != expected:
        raise RuntimeError(f"checkpoint SHA-256 validation failed: {filename}")
    sys.path.insert(0, str(rvm))
    from model import MattingNetwork
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = MattingNetwork(architecture).eval().to(device)
    model.load_state_dict(torch.load(checkpoint, map_location=device, weights_only=True))
    return torch, model, device

def validate(runtime):
    from tempfile import TemporaryDirectory
    from unittest.mock import patch
    with TemporaryDirectory() as folder:
        temporary = Path(folder) / "preview.tmp"
        temporary.write_bytes(b"preview")
        with patch("os.replace", side_effect=PermissionError), patch("time.sleep"):
            replace_preview(temporary, Path(folder) / "preview.jpg")
        if temporary.exists(): raise RuntimeError("preview lock handling validation failed")
    if aggregate_percent(10, 90, 100, 50) != 55:
        raise RuntimeError("multi-clip progress validation failed")
    print("STATUS:Checking Python and packages...", flush=True)
    if not (3, 11) <= sys.version_info[:2] < (3, 15): raise RuntimeError("Python 3.11-3.14 is required")
    import torch, torchvision, numpy as np
    if torch.__version__.split("+")[0] != "2.11.0": raise RuntimeError(f"PyTorch 2.11.0 required; found {torch.__version__}")
    if torchvision.__version__.split("+")[0] != "0.26.0": raise RuntimeError(f"torchvision 0.26.0 required; found {torchvision.__version__}")
    if torch.version.cuda != "12.8": raise RuntimeError(f"CUDA 12.8 wheel required; found {torch.version.cuda}")
    print("STATUS:Checking FFmpeg and ffprobe...", flush=True)
    ffmpeg, ffprobe = executable("ffmpeg"), executable("ffprobe")
    subprocess.check_output([ffmpeg, "-version"], text=True)
    subprocess.check_output([ffprobe, "-version"], text=True)
    print("STATUS:Checking RVM and model checkpoints...", flush=True)
    head = subprocess.check_output(["git", "-C", str(runtime / "rvm"), "rev-parse", "HEAD"], text=True).strip()
    if head != RVM_COMMIT: raise RuntimeError(f"RVM checkout is {head}, expected {RVM_COMMIT}")
    for _, filename, expected in WEIGHTS.values():
        path = runtime / "checkpoints" / filename
        if not path.is_file() or digest(path) != expected: raise RuntimeError(f"checkpoint SHA-256 validation failed: {filename}")
    torch_mod, model, device = load_model(runtime, "fast")
    print(f"STATUS:Loading RVM on {device.type.upper()}...", flush=True)
    print("STATUS:Running two-frame sequence inference...", flush=True)
    with torch_mod.inference_mode():
        model(torch_mod.zeros((1, 2, 3, 64, 64), device=device), downsample_ratio=1)
    print(f"OK: Python {sys.version_info.major}.{sys.version_info.minor}; torch {torch.__version__}; torchvision {torchvision.__version__}; numpy {np.__version__}; CUDA {torch.version.cuda}; {Path(ffmpeg).name}; {Path(ffprobe).name}; RVM {RVM_COMMIT}; sequence inference")

def process(args, loaded=None, report=emit):
    if loaded is None:
        report("startup", 0, "Loading RVM model...")
        loaded = load_model(args.runtime, args.preset)
    import numpy as np
    from PIL import Image
    torch, model, device = loaded
    source, output = args.source.resolve(), args.output.resolve()
    preview_output = getattr(args, "preview_output", output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    preview_output.mkdir(parents=True, exist_ok=True)
    report("startup", 0, "Inspecting the source video...")
    width, height, frame_rate, fps, source_duration = probe(source)
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
    report("startup", 0, "Preparing video decoding and encoding...")
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
    recurrent = [None] * 4
    downsample = 1 if args.matting_resolution == 0 else min(args.matting_resolution / max(width, height), 1)
    frame_bytes = width * height * 3
    count = 0
    last_preview = 0
    last_rgba = last_source = None
    yy, xx = np.indices((height, width))
    checker = np.where((((xx // 24) + (yy // 24)) & 1)[..., None], 190, 125).astype(np.uint8)
    fp16 = device.type == "cuda" and fast_fp16(torch.cuda.get_device_capability(device))
    tensor_dtype = torch.float16 if fp16 else torch.float32
    if device.type == "cuda": torch.backends.cudnn.benchmark = True
    report("inference", 0, f"Starting foreground inference on {device.type.upper()} in {'FP16' if fp16 else 'FP32'}...")

    def save_preview(rgba, source_frame, force=False):
        nonlocal last_preview
        now = time.monotonic()
        if not force and now - last_preview < .5: return
        alpha = rgba[-1, :, :, 3:4].astype(np.float32) / 255
        composite = np.rint(rgba[-1, :, :, :3] * alpha + checker * (1 - alpha)).astype(np.uint8)
        for name, data in (("preview-source.jpg", source_frame),
                           ("preview-composite.jpg", composite)):
            temporary = preview_output / (name + ".tmp")
            Image.fromarray(data, "RGB").save(temporary, "JPEG", quality=86)
            replace_preview(temporary, preview_output / name)
        last_preview = now

    def infer_and_write(tensor, state):
        nonlocal last_rgba, last_source
        try:
            with torch.inference_mode(), torch.autocast(device_type=device.type,
                                                        dtype=torch.float16, enabled=fp16):
                foreground, alpha, *next_state = model(tensor, *state, downsample_ratio=downsample)
            rgba = torch.cat((foreground, alpha), 2).clamp_(0, 1).mul_(255).byte()[0].permute(0, 2, 3, 1).contiguous().cpu().numpy()
            last_rgba = rgba
            last_source = tensor[0, -1].clamp(0, 1).mul(255).byte().permute(1, 2, 0).contiguous().cpu().numpy()
            save_preview(rgba, last_source)
            output_bytes = memoryview(rgba).cast("B")
            while output_bytes:
                written = encode.stdin.write(output_bytes)
                if not written: raise RuntimeError("FFmpeg output encoder closed unexpectedly")
                output_bytes = output_bytes[written:]
            return next_state
        except torch.OutOfMemoryError:
            frames = tensor.size(1)
            if frames == 1: raise
            if device.type == "cuda": torch.cuda.empty_cache()
            split = frames // 2
            report("inference", min(99, count * 100 / total), f"Memory limit; retrying {frames} frames as smaller batches")
            state = infer_and_write(tensor[:, :split], state)
            return infer_and_write(tensor[:, split:], state)

    try:
        while True:
            host = torch.empty((args.sequence_chunk, height, width, 3),
                               dtype=torch.uint8, pin_memory=device.type == "cuda")
            raw = memoryview(host.numpy()).cast("B")
            received = 0
            while received < len(raw):
                size = decode.stdout.readinto(raw[received:])
                if not size: break
                received += size
            if not received: break
            if received % frame_bytes: raise RuntimeError("source decoder returned a partial frame")
            frame_count = received // frame_bytes
            tensor = host[:frame_count].to(device=device, dtype=tensor_dtype,
                non_blocking=device.type == "cuda").permute(0, 3, 1, 2).unsqueeze(0).div_(255)
            recurrent = infer_and_write(tensor, recurrent)
            count += frame_count
            report("inference", min(99, count * 100 / total), f"Processed {count}/{total} frames")
        if last_rgba is not None: save_preview(last_rgba, last_source, force=True)
        encode.stdin.close(); decode.stdout.close()
        if decode.wait() != 0: raise RuntimeError("source normalization failed")
        if encode.wait() != 0: raise RuntimeError("FFmpeg output encoding failed")
    except BaseException:
        decode.kill(); encode.kill(); raise
    result = {"width": width, "height": height, "frameRate": frame_rate,
        "durationMs": round(count * 1000 / fps)}
    (output / "result.json").write_text(json.dumps(result, indent=2))
    report("complete", 100, "Foreground and alpha are ready for preview")

def process_jobs(args):
    jobs = [json.loads(value) for value in args.job]
    durations = [max(1, int(job["endMs"]) - int(job["startMs"])) for job in jobs]
    total, completed = sum(durations), 0
    emit("startup", 0, "Loading RVM model...")
    loaded = load_model(args.runtime, args.preset)
    for index, (job, duration) in enumerate(zip(jobs, durations)):
        clip = argparse.Namespace(**vars(args))
        clip.output = Path(job["output"])
        clip.start_ms, clip.end_ms = int(job["startMs"]), int(job["endMs"])
        clip.preview_output = args.output
        def report(stage, percent=0, message=""):
            emit(stage, aggregate_percent(completed, duration, total, percent),
                 f"Clip {index + 1}/{len(jobs)}: {message}")
        process(clip, loaded, report)
        completed += duration

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path); parser.add_argument("--output", type=Path)
    parser.add_argument("--preset", choices=WEIGHTS, default="quality")
    parser.add_argument("--matting-resolution", type=int, choices=(0, 512, 768, 1024), default=512)
    parser.add_argument("--sequence-chunk", type=int, choices=range(1, 25), default=3)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--job", action="append", default=[])
    parser.add_argument("--runtime", type=Path, required=True); parser.add_argument("--validate", action="store_true")
    args = parser.parse_args()
    if args.validate: validate(args.runtime.resolve())
    elif not args.source or not args.output: parser.error("--source and --output are required")
    elif args.job: process_jobs(args)
    else: process(args)

if __name__ == "__main__":
    try: main()
    except Exception as error:
        emit("error", 0, str(error)); raise
