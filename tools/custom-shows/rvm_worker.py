#!/usr/bin/env python3
"""QuickPlayer Robust Video Matting worker. Stdout is NDJSON progress only."""
import argparse
import ctypes
import hashlib
import json
import math
import os
import queue
import shutil
import statistics
import subprocess
import sys
import threading
import time
from pathlib import Path

RVM_COMMIT = "53d74c6826735f01f4406b5ca9075eee27bec094"
WEIGHTS = {
    "fast": ("mobilenetv3", "rvm_mobilenetv3.pth", "3c7c1d92033f7c38d6577c481d13a195d7d80a159b960f4f3119ac7b534cf4f8"),
    "quality": ("resnet50", "rvm_resnet50.pth", "c191a807251164c073dce5fa408e7a816070d539b882b2a3150330a9fec112ce"),
}
SENTINEL = object()


def emit(stage, percent=0, message=""):
    print(json.dumps({"stage": stage, "percent": percent, "message": message}), flush=True)


def profile_record(kind, **values):
    print("PROFILE " + json.dumps({"kind": kind, **values}, sort_keys=True),
          file=sys.stderr, flush=True)


def replace_preview(temporary, destination):
    for delay in (0, .02, .05):
        if delay:
            time.sleep(delay)
        try:
            os.replace(temporary, destination)
            return
        except PermissionError:
            pass
    try:
        Path(temporary).unlink(missing_ok=True)
    except OSError:
        pass


def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def executable(name):
    configured = os.environ.get("IQP_" + name.upper())
    if configured and Path(configured).is_file():
        return configured
    found = shutil.which(name)
    if not found:
        raise RuntimeError(f"{name} was not found")
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
    rotation = next((abs(int(item.get("rotation", 0)))
                     for item in stream.get("side_data_list", [])), 0)
    if rotation in (90, 270):
        width, height = height, width
    width -= width % 2
    height -= height % 2
    rate = stream.get("avg_frame_rate") or stream.get("r_frame_rate") or "25/1"
    numerator, denominator = map(int, rate.split("/"))
    if not numerator or not denominator:
        numerator, denominator, rate = 25, 1, "25/1"
    duration = float(data.get("format", {}).get("duration") or 0)
    if width <= 0 or height <= 0 or duration <= 0:
        raise RuntimeError("invalid source video metadata")
    return width, height, f"{numerator}/{denominator}", numerator / denominator, duration


def physical_memory_bytes():
    try:
        class MemoryStatus(ctypes.Structure):
            _fields_ = [("length", ctypes.c_ulong), ("load", ctypes.c_ulong),
                ("total", ctypes.c_ulonglong), ("available", ctypes.c_ulonglong),
                ("page_total", ctypes.c_ulonglong), ("page_available", ctypes.c_ulonglong),
                ("virtual_total", ctypes.c_ulonglong), ("virtual_available", ctypes.c_ulonglong),
                ("extended_available", ctypes.c_ulonglong)]
        status = MemoryStatus()
        status.length = ctypes.sizeof(status)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return int(status.total)
    except Exception:
        pass
    return 8 * 1024 ** 3


def peak_rss_bytes():
    try:
        class Counters(ctypes.Structure):
            _fields_ = [("cb", ctypes.c_ulong), ("page_faults", ctypes.c_ulong),
                ("peak_working_set", ctypes.c_size_t), ("working_set", ctypes.c_size_t),
                ("quota_peak_paged", ctypes.c_size_t), ("quota_paged", ctypes.c_size_t),
                ("quota_peak_nonpaged", ctypes.c_size_t), ("quota_nonpaged", ctypes.c_size_t),
                ("pagefile", ctypes.c_size_t), ("peak_pagefile", ctypes.c_size_t)]
        counters = Counters()
        counters.cb = ctypes.sizeof(counters)
        get_counters = ctypes.WinDLL("kernel32", use_last_error=True) \
            .K32GetProcessMemoryInfo
        get_counters.argtypes = [ctypes.c_void_p, ctypes.POINTER(Counters),
                                 ctypes.c_ulong]
        get_counters.restype = ctypes.c_int
        if get_counters(ctypes.c_void_p(-1), ctypes.byref(counters), counters.cb):
            return int(counters.peak_working_set)
    except Exception:
        pass
    return 0


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
    started = time.perf_counter()
    model = MattingNetwork(architecture).eval().to(device)
    model.load_state_dict(torch.load(checkpoint, map_location=device, weights_only=True))
    profile_record("rvm_model_load", preset=preset, device=device.type,
                   seconds=time.perf_counter() - started)
    return torch, model, device


class StageTimings:
    def __init__(self):
        self.values = {}
        self.lock = threading.Lock()

    def add(self, stage, seconds):
        if seconds is None or seconds < 0:
            return
        with self.lock:
            self.values.setdefault(stage, []).append(float(seconds))

    def emit(self, **context):
        with self.lock:
            values = {name: list(samples) for name, samples in self.values.items()}
        for name, samples in sorted(values.items()):
            ordered = sorted(samples)
            p95 = ordered[min(len(ordered) - 1,
                              max(0, math.ceil(len(ordered) * .95) - 1))]
            profile_record("rvm_stage", stage=name, samples=len(samples),
                totalSeconds=sum(samples), meanSeconds=statistics.fmean(samples),
                medianSeconds=statistics.median(samples), p95Seconds=p95, **context)


class LatestPreviewWriter:
    def __init__(self, output, enabled, timings):
        self.output = output
        self.enabled = enabled
        self.timings = timings
        self.condition = threading.Condition()
        self.pending = None
        self.stopping = False
        self.error = None
        self.thread = None
        if enabled:
            self.thread = threading.Thread(target=self._run,
                name="RVM preview writer", daemon=True)
            self.thread.start()

    def submit(self, source, alpha):
        if not self.enabled:
            return
        with self.condition:
            if self.error:
                raise self.error
            self.pending = (source.copy(), alpha.copy())
            self.condition.notify()

    def _run(self):
        try:
            from PIL import Image
            import numpy as np
            while True:
                with self.condition:
                    while self.pending is None and not self.stopping:
                        self.condition.wait()
                    if self.pending is None and self.stopping:
                        return
                    source, alpha = self.pending
                    self.pending = None
                    self.condition.notify_all()
                started = time.perf_counter()
                height, width = source.shape[:2]
                scale = min(960 / width, 540 / height, 1)
                preview_size = (max(1, round(width * scale)),
                                max(1, round(height * scale)))
                source_image = Image.fromarray(source, "RGB")
                alpha_image = Image.fromarray(alpha, "L")
                if preview_size != (width, height):
                    source_image = source_image.resize(preview_size, Image.Resampling.BILINEAR)
                    alpha_image = alpha_image.resize(preview_size, Image.Resampling.BILINEAR)
                source_small = np.asarray(source_image, dtype=np.uint8)
                alpha_small = np.asarray(alpha_image, dtype=np.uint8)
                preview_height, preview_width = source_small.shape[:2]
                yy, xx = np.indices((preview_height, preview_width))
                checker = np.where((((xx // 16) + (yy // 16)) & 1)[..., None],
                                   190, 125).astype(np.uint8)
                opacity = alpha_small[:, :, None].astype(np.float32) / 255
                composite = np.rint(source_small * opacity +
                                     checker * (1 - opacity)).astype(np.uint8)
                for name, image in (("preview-source.jpg", source_small),
                                    ("preview-composite.jpg", composite)):
                    temporary = self.output / (name + ".tmp")
                    Image.fromarray(image, "RGB").save(temporary, "JPEG", quality=86)
                    replace_preview(temporary, self.output / name)
                self.timings.add("preview", time.perf_counter() - started)
        except BaseException as error:
            with self.condition:
                self.error = error
                self.pending = None
                self.condition.notify_all()

    def close(self):
        if not self.enabled:
            return
        with self.condition:
            while self.pending is not None and self.error is None:
                self.condition.wait(.1)
            self.stopping = True
            self.condition.notify_all()
        self.thread.join()
        if self.error:
            raise self.error


class FrameSlot:
    def __init__(self, torch, chunk, height, width, device, dtype):
        pinned = device.type == "cuda"
        self.host = torch.empty((chunk, height, width, 3), dtype=torch.uint8,
                                pin_memory=pinned)
        self.output = torch.empty((chunk, height, width), dtype=torch.uint8,
                                  pin_memory=pinned)
        self.gpu = torch.empty((1, chunk, 3, height, width), device=device,
                               dtype=dtype) if pinned else None
        self.count = 0
        self.final = False
        self.upload_start = self.upload_end = self.download_end = None
        self.inference_events = []
        self.download_events = []


class RvmExecution:
    def __init__(self, args, loaded, expected_frames):
        self.args = args
        self.torch, self.model, self.device = loaded
        self.safe_chunk = args.sequence_chunk
        self.requested_chunk = args.sequence_chunk
        self.expected_frames = expected_frames
        self.compiled_model = None
        self.compile_attempted = False
        self.compile_materialized = False
        self.compile_marker = None
        self.compile_disabled_reason = None
        self.resolved_mode = "eager"
        self.encoder = ""
        self.encoder_preset = ""
        self.pipeline_depth = 1

    @property
    def compile_allowed(self):
        if self.device.type != "cuda" or self.args.matting_resolution != 512:
            return False
        if self.args.execution_mode == "eager" or self.args.compile_cutoff_frames <= 0:
            return self.args.execution_mode == "compile"
        return self.args.execution_mode == "compile" or self.expected_frames >= math.ceil(
            self.args.compile_cutoff_frames * 1.2)

    def compiled(self, report, width, height):
        if not self.compile_allowed or self.compile_disabled_reason:
            return None
        if self.compiled_model is not None:
            return self.compiled_model
        if self.compile_attempted:
            return None
        self.compile_attempted = True
        cache = self.args.runtime / "torchinductor-rvm"
        self.compile_marker = cache / (f"ready-{self.args.preset}-{width}x{height}-"
                                       f"c{self.requested_chunk}.json")
        cached = self.compile_marker.is_file()
        report("compile", 0, "Loading cached compiled RVM..." if cached else
               "Compiling RVM once; later processing reuses this cache...")
        started = time.perf_counter()
        try:
            cache.mkdir(parents=True, exist_ok=True)
            self.compiled_model = self.torch.compile(self.model,
                mode="max-autotune-no-cudagraphs", dynamic=False, fullgraph=False)
            self.resolved_mode = "compiled"
            profile_record("rvm_compile_prepare", cached=cached,
                           seconds=time.perf_counter() - started)
            return self.compiled_model
        except BaseException as error:
            self.compile_disabled_reason = str(error)
            self.resolved_mode = "eager-fallback"
            profile_record("rvm_compile_fallback", error=str(error),
                           seconds=time.perf_counter() - started)
            if self.args.execution_mode == "compile":
                raise
            report("inference", 0, "RVM compilation failed; continuing in eager mode...")
            return None

    def disable_compile(self, reason):
        self.compiled_model = None
        self.compile_disabled_reason = reason
        if self.compile_marker is not None:
            self.compile_marker.unlink(missing_ok=True)
        if self.resolved_mode == "compiled":
            self.resolved_mode = "eager-fallback"

    def metadata(self):
        return {
            "requestedSequenceChunk": self.requested_chunk,
            "effectiveSequenceChunk": self.safe_chunk,
            "executionMode": self.resolved_mode,
            "pipelineDepth": self.pipeline_depth,
            "encoder": self.encoder,
            "encoderPreset": self.encoder_preset,
        }


def validate(runtime):
    from tempfile import TemporaryDirectory
    from unittest.mock import patch
    with TemporaryDirectory() as folder:
        temporary = Path(folder) / "preview.tmp"
        temporary.write_bytes(b"preview")
        with patch("os.replace", side_effect=PermissionError), patch("time.sleep"):
            replace_preview(temporary, Path(folder) / "preview.jpg")
        if temporary.exists():
            raise RuntimeError("preview lock handling validation failed")
    if aggregate_percent(10, 90, 100, 50) != 55:
        raise RuntimeError("multi-clip progress validation failed")
    if min(3, max(1, int(min(2 * 1024 ** 3, 32 * 1024 ** 3 * .125) //
                         (12 * 1920 * 1080 * 7)))) != 3:
        raise RuntimeError("pipeline depth calculation validation failed")
    if os.name == "nt" and peak_rss_bytes() <= 0:
        raise RuntimeError("process memory profiling validation failed")
    print("STATUS:Checking Python and packages...", flush=True)
    if not (3, 11) <= sys.version_info[:2] < (3, 15):
        raise RuntimeError("Python 3.11-3.14 is required")
    import torch
    import torchvision
    import numpy as np
    if torch.__version__.split("+")[0] != "2.11.0":
        raise RuntimeError(f"PyTorch 2.11.0 required; found {torch.__version__}")
    if torchvision.__version__.split("+")[0] != "0.26.0":
        raise RuntimeError(f"torchvision 0.26.0 required; found {torchvision.__version__}")
    if torch.version.cuda != "12.8":
        raise RuntimeError(f"CUDA 12.8 wheel required; found {torch.version.cuda}")
    print("STATUS:Checking FFmpeg and ffprobe...", flush=True)
    ffmpeg, ffprobe = executable("ffmpeg"), executable("ffprobe")
    subprocess.check_output([ffmpeg, "-version"], text=True)
    subprocess.check_output([ffprobe, "-version"], text=True)
    print("STATUS:Checking RVM and model checkpoints...", flush=True)
    head = subprocess.check_output(["git", "-C", str(runtime / "rvm"),
        "rev-parse", "HEAD"], text=True).strip()
    if head != RVM_COMMIT:
        raise RuntimeError(f"RVM checkout is {head}, expected {RVM_COMMIT}")
    for _, filename, expected in WEIGHTS.values():
        path = runtime / "checkpoints" / filename
        if not path.is_file() or digest(path) != expected:
            raise RuntimeError(f"checkpoint SHA-256 validation failed: {filename}")
    torch_mod, model, device = load_model(runtime, "fast")
    print(f"STATUS:Loading RVM on {device.type.upper()}...", flush=True)
    print("STATUS:Running two-frame sequence inference...", flush=True)
    with torch_mod.inference_mode():
        model(torch_mod.zeros((1, 2, 3, 64, 64), device=device), downsample_ratio=1)
    print(f"OK: Python {sys.version_info.major}.{sys.version_info.minor}; torch {torch.__version__}; "
          f"torchvision {torchvision.__version__}; numpy {np.__version__}; CUDA {torch.version.cuda}; "
          f"{Path(ffmpeg).name}; {Path(ffprobe).name}; RVM {RVM_COMMIT}; sequence inference")


def queue_put(target, value, stop):
    while not stop.is_set():
        try:
            target.put(value, timeout=.1)
            return True
        except queue.Full:
            pass
    return False


def queue_get(source, stop, errors):
    while not stop.is_set():
        try:
            return source.get(timeout=.1)
        except queue.Empty:
            try:
                raise errors.get_nowait()
            except queue.Empty:
                pass
    try:
        raise errors.get_nowait()
    except queue.Empty:
        raise RuntimeError("RVM processing stopped")


def process(args, execution, report=emit):
    import numpy as np
    torch, model, device = execution.torch, execution.model, execution.device
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
    report("startup", 0, "Preparing video decoding and encoding...")
    encoders = subprocess.check_output([ffmpeg, "-hide_banner", "-encoders"],
                                       text=True, errors="replace")
    nvenc = width >= 256 and height >= 128 and "h264_nvenc" in encoders and subprocess.run(
        [ffmpeg, "-v", "error", "-f", "lavfi", "-i",
         "color=size=256x256:duration=0.1", "-frames:v", "1", "-c:v", "h264_nvenc",
         "-preset", args.encoder_preset, "-f", "null", "-"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
    video_codec = ["-c:v", "h264_nvenc", "-preset", args.encoder_preset,
        "-tune", "hq", "-cq", "19"] if nvenc else [
        "-c:v", "libx264", "-preset", "slow", "-crf", "18"]
    alpha_codec = ["-c:v", "h264_nvenc", "-preset", args.encoder_preset,
        "-tune", "hq", "-cq", "10"] if nvenc else [
        "-c:v", "libx264", "-preset", "medium", "-crf", "10"]
    execution.encoder = "h264_nvenc" if nvenc else "libx264"
    execution.encoder_preset = args.encoder_preset if nvenc else "slow/medium"
    downsample = 1 if args.matting_resolution == 0 else min(
        args.matting_resolution / max(width, height), 1)
    model_width = max(1, int(width * downsample))
    model_height = max(1, int(height * downsample))
    normalized = (f"[0:v:0]fps={frame_rate},scale={width}:{height}:flags=lanczos,"
                  "setsar=1,split=2[video][python]")
    if (model_width, model_height) == (width, height):
        normalized += ";[python]format=rgb24[raw]"
    else:
        normalized += (f";[python]format=rgb24,scale={model_width}:{model_height}:"
                       "flags=bilinear[raw]")
    decode = subprocess.Popen([ffmpeg, "-y", "-v", "warning", "-ss", f"{start:.6f}",
        "-i", str(source), "-filter_complex", normalized,
        "-map", "[raw]", "-t", f"{duration:.6f}", "-frames:v", str(total),
        "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1",
        "-map", "[video]", "-map", "0:a:0?", "-t", f"{duration:.6f}",
        "-frames:v", str(total), *video_codec, "-pix_fmt", "yuv420p",
        "-r", frame_rate, "-fps_mode", "cfr", "-c:a", "aac",
        str(output / "foreground.mp4")], stdout=subprocess.PIPE)
    try:
        encode = subprocess.Popen([ffmpeg, "-y", "-v", "warning", "-f", "rawvideo",
            "-pix_fmt", "gray", "-s", f"{model_width}x{model_height}",
            "-r", frame_rate, "-i", "pipe:0", "-vf",
            f"scale={width}:{height}:flags=bilinear,format=yuv420p",
            "-frames:v", str(total),
            *alpha_codec, "-pix_fmt", "yuv420p", "-r", frame_rate,
            "-fps_mode", "cfr", str(output / "alpha.mkv")], stdin=subprocess.PIPE)
    except BaseException:
        decode.kill()
        raise

    frame_bytes = model_width * model_height * 3
    slot_bytes = args.sequence_chunk * model_width * model_height * 4
    memory_limit = min(2 * 1024 ** 3, int(physical_memory_bytes() * .125))
    slot_count = 1 if args.pipeline == "serial" else min(3,
        max(1, memory_limit // max(1, slot_bytes)))
    execution.pipeline_depth = max(execution.pipeline_depth, slot_count)
    fp16 = device.type == "cuda" and fast_fp16(torch.cuda.get_device_capability(device))
    tensor_dtype = torch.float16 if fp16 else torch.float32
    if device.type == "cuda":
        torch.backends.cudnn.benchmark = not args.verify_raw_hash
        torch.backends.cudnn.deterministic = args.verify_raw_hash
        torch.set_float32_matmul_precision("high")
        torch.cuda.reset_peak_memory_stats(device)
    timings = StageTimings()
    preview = None
    try:
        preview = LatestPreviewWriter(preview_output, not args.disable_preview, timings)
        free_slots = queue.Queue(maxsize=slot_count)
        decoded_slots = queue.Queue(maxsize=max(1, slot_count - 1))
        output_slots = queue.Queue(maxsize=max(1, slot_count - 1))
        errors = queue.Queue()
        stop = threading.Event()
        slots = [FrameSlot(torch, args.sequence_chunk, model_height, model_width,
                           device, tensor_dtype) for _ in range(slot_count)]
        for slot in slots:
            free_slots.put(slot)
        upload_stream = torch.cuda.Stream(device=device) if device.type == "cuda" else None
        download_stream = torch.cuda.Stream(device=device) if device.type == "cuda" else None
    except BaseException:
        try:
            if preview is not None:
                preview.close()
        finally:
            decode.kill()
            encode.kill()
        raise
    started_processing = time.perf_counter()
    encoded_count = 0
    last_preview_time = 0.0
    raw_alpha_hash = hashlib.sha256() if args.verify_raw_hash else None

    def fail(error):
        if errors.empty():
            errors.put(error)
        stop.set()

    def decoder_worker():
        try:
            carry = b""
            while not stop.is_set():
                wait_started = time.perf_counter()
                slot = queue_get(free_slots, stop, errors)
                timings.add("decode_slot_wait", time.perf_counter() - wait_started)
                read_started = time.perf_counter()
                raw = memoryview(slot.host.numpy()).cast("B")
                received = len(carry)
                if carry:
                    raw[:received] = carry
                    carry = b""
                while received < len(raw) and not stop.is_set():
                    size = decode.stdout.readinto(raw[received:])
                    if not size:
                        break
                    received += size
                timings.add("decode_read", time.perf_counter() - read_started)
                if not received:
                    free_slots.put(slot)
                    queue_put(decoded_slots, SENTINEL, stop)
                    return
                if received % frame_bytes:
                    raise RuntimeError("source decoder returned a partial frame")
                slot.count = received // frame_bytes
                if received == len(raw):
                    carry = decode.stdout.read(1)
                slot.final = not carry
                if not queue_put(decoded_slots, slot, stop):
                    return
                if slot.final:
                    queue_put(decoded_slots, SENTINEL, stop)
                    return
        except BaseException as error:
            fail(error)
            try:
                decoded_slots.put_nowait(SENTINEL)
            except queue.Full:
                pass

    def encoder_write(data):
        view = memoryview(data).cast("B")
        started = time.perf_counter()
        while view and not stop.is_set():
            written = encode.stdin.write(view)
            if not written:
                raise RuntimeError("FFmpeg output encoder closed unexpectedly")
            view = view[written:]
        timings.add("encoder_write", time.perf_counter() - started)

    def output_worker():
        nonlocal encoded_count, last_preview_time
        try:
            while not stop.is_set():
                queue_started = time.perf_counter()
                slot = queue_get(output_slots, stop, errors)
                timings.add("output_queue_wait", time.perf_counter() - queue_started)
                if slot is SENTINEL:
                    return
                wait_started = time.perf_counter()
                if device.type == "cuda":
                    slot.download_end.synchronize()
                    timings.add("h2d", slot.upload_start.elapsed_time(slot.upload_end) / 1000)
                    for first, last in slot.inference_events:
                        timings.add("inference", first.elapsed_time(last) / 1000)
                    for first, last in slot.download_events:
                        timings.add("d2h", first.elapsed_time(last) / 1000)
                timings.add("output_wait", time.perf_counter() - wait_started)
                alpha_array = slot.output[:slot.count].numpy()
                if raw_alpha_hash is not None:
                    raw_alpha_hash.update(alpha_array.tobytes())
                encoder_write(alpha_array)
                now = time.monotonic()
                if preview.enabled and (slot.final or now - last_preview_time >= .5):
                    preview.submit(slot.host[slot.count - 1].numpy(),
                                   slot.output[slot.count - 1].numpy())
                    last_preview_time = now
                encoded_count += slot.count
                elapsed = max(.001, time.perf_counter() - started_processing)
                report("inference", min(99, encoded_count * 100 / total),
                       f"Processed {encoded_count}/{total} frames ({encoded_count / elapsed:.1f} FPS)")
                slot.inference_events.clear()
                slot.download_events.clear()
                slot.count = 0
                slot.final = False
                if not queue_put(free_slots, slot, stop):
                    return
        except BaseException as error:
            fail(error)

    decoder_thread = threading.Thread(target=decoder_worker,
        name="RVM decoder", daemon=True)
    output_thread = threading.Thread(target=output_worker,
        name="RVM encoder", daemon=True)
    decoder_thread.start()
    output_thread.start()
    recurrent = [None] * 4
    bootstrap = True
    report("inference", 0, f"Starting alpha inference on {device.type.upper()} in "
           f"{'FP16' if fp16 else 'FP32'}; chunk {execution.safe_chunk}; "
           f"pipeline depth {slot_count}; {execution.encoder} {execution.encoder_preset}...")

    def upload(slot):
        if device.type != "cuda":
            return
        slot.upload_start = torch.cuda.Event(enable_timing=True)
        slot.upload_end = torch.cuda.Event(enable_timing=True)
        with torch.cuda.stream(upload_stream):
            slot.upload_start.record(upload_stream)
            target = slot.gpu[:, :slot.count]
            target.copy_(slot.host[:slot.count].permute(0, 3, 1, 2).unsqueeze(0),
                         non_blocking=True)
            target.div_(255)
            slot.upload_end.record(upload_stream)

    def run_slice(slot, offset, length, state, use_compiled):
        try:
            if args.simulate_oom_above and length > args.simulate_oom_above:
                raise torch.OutOfMemoryError("simulated RVM chunk OOM")
            if device.type == "cuda":
                torch.cuda.current_stream(device).wait_event(slot.upload_end)
                tensor = slot.gpu[:, offset:offset + length]
            else:
                transfer_started = time.perf_counter()
                tensor = slot.host[offset:offset + length].to(dtype=tensor_dtype) \
                    .permute(0, 3, 1, 2).unsqueeze(0).div_(255)
                timings.add("h2d", time.perf_counter() - transfer_started)
            inference_started = time.perf_counter()
            cuda_start = torch.cuda.Event(enable_timing=True) if device.type == "cuda" else None
            cuda_end = torch.cuda.Event(enable_timing=True) if device.type == "cuda" else None
            if cuda_start:
                cuda_start.record()
            selected_model = use_compiled or model
            first_compiled_call = use_compiled is not None and \
                not execution.compile_materialized
            compile_started = time.perf_counter() if first_compiled_call else None
            with torch.inference_mode(), torch.autocast(device_type=device.type,
                    dtype=torch.float16, enabled=fp16):
                _foreground, alpha, *next_state = selected_model(
                    tensor, *state, downsample_ratio=1)
                alpha_bytes = alpha.clamp_(0, 1).mul_(255).byte()[0, :, 0].contiguous()
            if first_compiled_call:
                if device.type == "cuda":
                    torch.cuda.synchronize(device)
                execution.compile_materialized = True
                if execution.compile_marker is not None:
                    marker_temporary = execution.compile_marker.with_suffix(".tmp")
                    marker_temporary.write_text(json.dumps({
                        "preset": args.preset, "width": width, "height": height,
                        "chunk": execution.requested_chunk,
                        "createdUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
                    }))
                    os.replace(marker_temporary, execution.compile_marker)
                profile_record("rvm_compile_materialize",
                    seconds=time.perf_counter() - compile_started)
                report("inference", min(99, encoded_count * 100 / total),
                       "Compiled RVM is ready; processing frames...")
            if cuda_end:
                cuda_end.record()
                slot.inference_events.append((cuda_start, cuda_end))
                download_start = torch.cuda.Event(enable_timing=True)
                download_end = torch.cuda.Event(enable_timing=True)
                with torch.cuda.stream(download_stream):
                    download_stream.wait_event(cuda_end)
                    download_start.record(download_stream)
                    slot.output[offset:offset + length].copy_(alpha_bytes,
                                                              non_blocking=True)
                    download_end.record(download_stream)
                    alpha_bytes.record_stream(download_stream)
                slot.download_events.append((download_start, download_end))
                slot.download_end = download_end
            else:
                timings.add("inference", time.perf_counter() - inference_started)
                download_started = time.perf_counter()
                slot.output[offset:offset + length].copy_(alpha_bytes)
                timings.add("d2h", time.perf_counter() - download_started)
            return next_state
        except torch.OutOfMemoryError:
            raise
        except BaseException as error:
            if use_compiled is not None and args.execution_mode != "compile":
                execution.disable_compile(str(error))
                profile_record("rvm_compile_runtime_fallback", error=str(error))
                report("inference", min(99, encoded_count * 100 / total),
                       "Compiled RVM failed; continuing in eager mode...")
                return run_slice(slot, offset, length, state, None)
            raise

    def infer_slot(slot, state):
        nonlocal bootstrap
        offset = 0
        while offset < slot.count:
            length = min(execution.safe_chunk, slot.count - offset)
            compiled = None
            if not bootstrap and length == execution.requested_chunk and \
                    execution.safe_chunk == execution.requested_chunk:
                compiled = execution.compiled(report, model_width, model_height)
            try:
                state = run_slice(slot, offset, length, state, compiled)
                bootstrap = False
                offset += length
            except torch.OutOfMemoryError:
                if length <= 1:
                    raise
                execution.safe_chunk = max(1, length // 2)
                execution.disable_compile("CUDA out of memory")
                if device.type == "cuda":
                    torch.cuda.empty_cache()
                report("inference", min(99, encoded_count * 100 / total),
                       f"Memory limit; using {execution.safe_chunk}-frame chunks for the rest of this show")
                profile_record("rvm_oom_recovery", requested=length,
                               effective=execution.safe_chunk)
        if device.type != "cuda":
            slot.download_end = None
        return state

    try:
        queue_started = time.perf_counter()
        current = queue_get(decoded_slots, stop, errors)
        timings.add("decode_queue_wait", time.perf_counter() - queue_started)
        if current is SENTINEL:
            raise RuntimeError("source decoder returned no frames")
        upload(current)
        while current is not SENTINEL:
            recurrent = infer_slot(current, recurrent)
            if slot_count == 1:
                queue_started = time.perf_counter()
                if not queue_put(output_slots, current, stop):
                    raise RuntimeError("RVM output pipeline stopped")
                timings.add("output_queue_put_wait", time.perf_counter() - queue_started)
                queue_started = time.perf_counter()
                next_slot = queue_get(decoded_slots, stop, errors)
                timings.add("decode_queue_wait", time.perf_counter() - queue_started)
                if next_slot is not SENTINEL:
                    upload(next_slot)
            else:
                queue_started = time.perf_counter()
                next_slot = queue_get(decoded_slots, stop, errors)
                timings.add("decode_queue_wait", time.perf_counter() - queue_started)
                if next_slot is not SENTINEL:
                    upload(next_slot)
                queue_started = time.perf_counter()
                if not queue_put(output_slots, current, stop):
                    raise RuntimeError("RVM output pipeline stopped")
                timings.add("output_queue_put_wait", time.perf_counter() - queue_started)
            current = next_slot
        queue_put(output_slots, SENTINEL, stop)
        decoder_thread.join()
        output_thread.join()
        if not errors.empty():
            raise errors.get()
        preview.close()
        encode.stdin.close()
        decode.stdout.close()
        if decode.wait() != 0:
            raise RuntimeError("source normalization failed")
        if encode.wait() != 0:
            raise RuntimeError("FFmpeg output encoding failed")
    except BaseException:
        stop.set()
        try:
            decode.kill()
        except Exception:
            pass
        try:
            encode.kill()
        except Exception:
            pass
        try:
            output_slots.put_nowait(SENTINEL)
        except queue.Full:
            pass
        decoder_thread.join(timeout=5)
        output_thread.join(timeout=5)
        try:
            preview.close()
        except Exception:
            pass
        raise

    elapsed = max(.001, time.perf_counter() - started_processing)
    context = {"preset": args.preset, "width": width, "height": height,
               "modelWidth": model_width, "modelHeight": model_height,
               "frames": encoded_count}
    timings.emit(**context)
    profile_record("rvm_summary", **context, seconds=elapsed,
        fps=encoded_count / elapsed, requestedChunk=execution.requested_chunk,
        effectiveChunk=execution.safe_chunk, pipelineDepth=slot_count,
        pinnedBytes=slot_count * slot_bytes,
        peakVramBytes=torch.cuda.max_memory_allocated(device)
            if device.type == "cuda" else 0,
        peakRamBytes=peak_rss_bytes(), executionMode=execution.resolved_mode,
        encoder=execution.encoder, encoderPreset=execution.encoder_preset,
        previewEnabled=preview.enabled,
        rawAlphaSha256=raw_alpha_hash.hexdigest() if raw_alpha_hash is not None else None,
        foregroundMode="source")
    result = {"width": width, "height": height, "frameRate": frame_rate,
        "durationMs": round(encoded_count * 1000 / fps), "foregroundMode": "source",
        **execution.metadata()}
    (output / "result.json").write_text(json.dumps(result, indent=2))
    report("complete", 100, "Source foreground and alpha are ready for preview")
    return result


def expected_frames(args, jobs):
    _, _, _, fps, source_duration = probe(args.source.resolve())
    if jobs:
        duration = sum(max(1, int(job["endMs"]) - int(job["startMs"]))
                       for job in jobs) / 1000
    else:
        end = source_duration if args.end_ms is None else args.end_ms / 1000
        duration = max(0, min(end, source_duration) - args.start_ms / 1000)
    return max(1, round(duration * fps))


def process_jobs(args, execution):
    jobs = [json.loads(value) for value in args.job]
    durations = [max(1, int(job["endMs"]) - int(job["startMs"])) for job in jobs]
    total_duration, completed = sum(durations), 0
    results = []
    for index, (job, duration) in enumerate(zip(jobs, durations)):
        clip = argparse.Namespace(**vars(args))
        clip.output = Path(job["output"])
        clip.start_ms, clip.end_ms = int(job["startMs"]), int(job["endMs"])
        clip.preview_output = args.output

        def report(stage, percent=0, message=""):
            emit(stage, aggregate_percent(completed, duration, total_duration, percent),
                 f"Clip {index + 1}/{len(jobs)}: {message}")

        results.append(process(clip, execution, report))
        completed += duration
        aggregate = dict(results[0])
        aggregate.update(execution.metadata())
        aggregate["clips"] = results
        args.output.mkdir(parents=True, exist_ok=True)
        temporary = args.output / "result.json.tmp"
        temporary.write_text(json.dumps(aggregate, indent=2))
        os.replace(temporary, args.output / "result.json")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--preset", choices=WEIGHTS, default="quality")
    parser.add_argument("--matting-resolution", type=int,
                        choices=(0, 256, 384, 512, 768, 1024), default=512)
    parser.add_argument("--sequence-chunk", type=int, choices=range(1, 25), default=12)
    parser.add_argument("--encoder-preset", choices=tuple(f"p{i}" for i in range(1, 8)),
                        default="p5")
    parser.add_argument("--compile-cutoff-frames", type=int, default=0)
    parser.add_argument("--execution-mode", choices=("auto", "eager", "compile"),
                        default="auto")
    parser.add_argument("--pipeline", choices=("bounded", "serial"), default="bounded")
    parser.add_argument("--disable-preview", action="store_true")
    parser.add_argument("--verify-raw-hash", action="store_true")
    parser.add_argument("--simulate-oom-above", type=int, default=0,
                        help=argparse.SUPPRESS)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--job", action="append", default=[])
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--validate", action="store_true")
    args = parser.parse_args()
    args.runtime = args.runtime.resolve()
    os.environ["TORCHINDUCTOR_CACHE_DIR"] = str(args.runtime / "torchinductor-rvm")
    if args.validate:
        validate(args.runtime)
        return
    if not args.source or not args.output:
        parser.error("--source and --output are required")
    args.output = args.output.resolve()
    jobs = [json.loads(value) for value in args.job]
    frames = expected_frames(args, jobs)
    emit("startup", 0, "Loading RVM model...")
    loaded = load_model(args.runtime, args.preset)
    execution = RvmExecution(args, loaded, frames)
    if args.job:
        process_jobs(args, execution)
    else:
        process(args, execution)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", 0, str(error))
        raise
