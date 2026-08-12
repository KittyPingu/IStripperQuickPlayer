#!/usr/bin/env python3
"""QuickPlayer MatAnyone 2 worker. Stdout is NDJSON progress only."""
import argparse
from collections import deque
import ctypes
import json
import math
import os
import queue
import shutil
import statistics
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

from rvm_worker import (digest, emit, executable, fast_fp16, load_model as load_rvm_model,
                        probe, replace_preview)
from videomama_worker import (alpha_encoder, output_codecs,
                              source_and_foreground_encoder)

COMMIT = "0079197acd6d16a741f71558809c06c586c579e0"
WEIGHTS_HASH = "5e9821e4087231427376b437c85bb6e072b41e582314f06fd524f75bc4af5914"
COMPILE_POLICY = "matanyone2-compile-policy.json"
COMPILE_READY = "matanyone2-partial-compile-ready.json"
PIPELINE_SLOTS = 3
QUEUE_DEPTH = 2
PREVIEW_INTERVAL = .5
PREVIEW_MAXIMUM = (960, 540)


def log_record(kind, **values):
    print(json.dumps({"matanyone2": kind, **values}, separators=(",", ":")),
          file=sys.stderr, flush=True)


class StageProfiler:
    def __init__(self, enabled=True):
        self.enabled = enabled
        self.started = time.perf_counter()
        self.values = {}
        self.peak_rss = process_rss()
        self.temp_bytes = 0

    def add(self, stage, seconds):
        if not self.enabled:
            return
        self.values.setdefault(stage, []).append(max(0.0, float(seconds)))
        self.peak_rss = max(self.peak_rss, process_rss())

    def set_temp_bytes(self, value):
        self.temp_bytes = max(self.temp_bytes, int(value))

    def report(self, torch=None, device=None, frames=0, variant=None,
               compile_stats=None):
        elapsed = time.perf_counter() - self.started
        stages = {}
        for name, samples in sorted(self.values.items()):
            ordered = sorted(samples)
            p95 = ordered[min(len(ordered) - 1,
                              max(0, math.ceil(len(ordered) * .95) - 1))]
            stages[name] = {
                "count": len(samples),
                "totalSeconds": round(sum(samples), 6),
                "meanMs": round(statistics.fmean(samples) * 1000, 3),
                "medianMs": round(statistics.median(samples) * 1000, 3),
                "p95Ms": round(p95 * 1000, 3),
            }
        peak_allocated = peak_reserved = 0
        if torch is not None and device is not None and device.type == "cuda":
            peak_allocated = torch.cuda.max_memory_allocated(device)
            peak_reserved = torch.cuda.max_memory_reserved(device)
        log_record("profile", variant=variant or {}, frames=frames,
                   totalSeconds=round(elapsed, 6),
                   fps=round(frames / elapsed, 4) if elapsed and frames else 0,
                   peakRssBytes=self.peak_rss, peakTempBytes=self.temp_bytes,
                   peakCudaAllocatedBytes=peak_allocated,
                   peakCudaReservedBytes=peak_reserved,
                   compileStats=compile_stats or {}, stages=stages)


def process_rss():
    if os.name != "nt":
        try:
            import resource
            return int(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024)
        except Exception:
            return 0
    class Counter(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_ulong), ("PageFaultCount", ctypes.c_ulong),
                    ("PeakWorkingSetSize", ctypes.c_size_t),
                    ("WorkingSetSize", ctypes.c_size_t),
                    ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                    ("PagefileUsage", ctypes.c_size_t),
                    ("PeakPagefileUsage", ctypes.c_size_t)]
    try:
        counter = Counter()
        counter.cb = ctypes.sizeof(counter)
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        kernel.GetCurrentProcess.restype = ctypes.c_void_p
        psapi.GetProcessMemoryInfo.argtypes = (
            ctypes.c_void_p, ctypes.POINTER(Counter), ctypes.c_ulong)
        psapi.GetProcessMemoryInfo.restype = ctypes.c_int
        if psapi.GetProcessMemoryInfo(kernel.GetCurrentProcess(),
                                      ctypes.byref(counter), counter.cb):
            return int(counter.PeakWorkingSetSize)
    except Exception:
        pass
    return 0


def processing_size(width, height, max_size):
    if max_size <= 0 or min(width, height) <= max_size:
        return width, height
    scale = max_size / min(width, height)
    return int(width * scale), int(height * scale)


def preview_size(width, height):
    scale = min(PREVIEW_MAXIMUM[0] / width, PREVIEW_MAXIMUM[1] / height, 1)
    return max(1, round(width * scale)), max(1, round(height * scale))


def mask_frame_index(mask_frame_ms, start_ms, fps, total):
    return max(0, min(total - 1,
        round(max(0, mask_frame_ms - start_ms) * fps / 1000)))


def binary_dilate(mask, iterations=3):
    import numpy as np
    result = mask.astype(bool, copy=True)
    for _ in range(iterations):
        padded = np.pad(result, 1, mode="constant")
        result = np.logical_or.reduce([
            padded[y:y + result.shape[0], x:x + result.shape[1]]
            for y in range(3) for x in range(3)])
    return result


def mask_components(mask):
    import numpy as np
    height, width = mask.shape
    visited = np.zeros(mask.shape, dtype=bool)
    for start_y, start_x in zip(*np.nonzero(mask)):
        if visited[start_y, start_x]:
            continue
        pending = deque([(int(start_y), int(start_x))])
        visited[start_y, start_x] = True
        component = []
        touches_edge = False
        while pending:
            y, x = pending.popleft()
            component.append((y, x))
            touches_edge |= y == 0 or x == 0 or y == height - 1 or x == width - 1
            for next_y, next_x in ((y - 1, x), (y + 1, x),
                                   (y, x - 1), (y, x + 1)):
                if (0 <= next_y < height and 0 <= next_x < width and
                        mask[next_y, next_x] and not visited[next_y, next_x]):
                    visited[next_y, next_x] = True
                    pending.append((next_y, next_x))
        yield component, touches_edge


def clean_rvm_mask(alpha, threshold=.40, dilation=3):
    import numpy as np
    foreground = np.asarray(alpha) >= threshold
    pixels = foreground.size
    minimum_island = max(16, pixels // 2000)
    cleaned = np.zeros_like(foreground)
    for component, _ in mask_components(foreground):
        if len(component) >= minimum_island:
            ys, xs = zip(*component)
            cleaned[ys, xs] = True
    maximum_hole = max(64, pixels // 500)
    background = ~cleaned
    for component, touches_edge in mask_components(background):
        if not touches_edge and len(component) <= maximum_hole:
            ys, xs = zip(*component)
            cleaned[ys, xs] = True
    return binary_dilate(cleaned, dilation).astype(np.uint8) * 255


def rvm_frame_score(alpha, threshold=.40):
    import numpy as np
    value = np.asarray(alpha, dtype=np.float32)
    foreground = value >= threshold
    area = int(foreground.sum())
    if area < foreground.size * .005 or area > foreground.size * .90:
        return -1.0
    confident = int((value >= .75).sum())
    border = int(foreground[0].sum() + foreground[-1].sum() +
                 foreground[:, 0].sum() + foreground[:, -1].sum())
    return area + confident * .20 - border * 2


def automatic_rvm_mask(source, runtime, start, frame_rate, width, height,
                       sample_count, destination, profiler, threshold=.40):
    import numpy as np
    from PIL import Image
    emit("initializing", 0, f"Running RVM on the first {sample_count} frames...")
    frame_bytes = width * height * 3
    command = [executable("ffmpeg"), "-v", "error", "-ss", f"{start:.6f}",
        "-i", str(source), "-map", "0:v:0", "-vf",
        f"fps={frame_rate},scale={width}:{height}:flags=bilinear,setsar=1",
        "-frames:v", str(sample_count), "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]
    started = time.perf_counter()
    decoded = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if decoded.returncode != 0:
        raise RuntimeError(decoded.stderr.decode(errors="replace").strip() or
                           "RVM initialization frames could not be decoded")
    count = len(decoded.stdout) // frame_bytes
    if count <= 0:
        raise RuntimeError("RVM initialization decoded no frames")
    frames = np.frombuffer(decoded.stdout[:count * frame_bytes], np.uint8).reshape(
        count, height, width, 3).copy()
    profiler.add("rvm_initialization_decode", time.perf_counter() - started)
    started = time.perf_counter()
    torch, model, device = load_rvm_model(runtime, "quality")
    fp16 = device.type == "cuda" and fast_fp16(torch.cuda.get_device_capability(device))
    tensor = torch.from_numpy(frames).permute(0, 3, 1, 2).unsqueeze(0).to(
        device=device, dtype=torch.float32).div_(255)
    with torch.inference_mode(), torch.autocast(device_type=device.type,
            dtype=torch.float16, enabled=fp16):
        _, alpha, *_ = model(tensor, *([None] * 4), downsample_ratio=1)
    alphas = alpha[0, :, 0].float().cpu().numpy()
    scores = [rvm_frame_score(value, threshold) for value in alphas]
    selected = max(range(count), key=lambda index: scores[index])
    cleaned = clean_rvm_mask(alphas[selected], threshold=threshold)
    if not cleaned.any():
        raise RuntimeError("RVM could not find a usable person mask in the opening frames")
    Image.fromarray(cleaned, "L").save(destination)
    profiler.add("rvm_initialization", time.perf_counter() - started)
    log_record("rvm_initializer", sampledFrames=count, selectedFrame=selected,
               threshold=threshold, dilationPixels=3,
               scores=[round(value, 2) for value in scores])
    del tensor, alpha, alphas, model
    if device.type == "cuda":
        torch.cuda.empty_cache()
    emit("initializing", 4,
         f"RVM selected frame {selected + 1}/{count}; starting MatAnyone 2...")
    return selected


def load_model(runtime, device_override=None):
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
    device = torch.device(device_override) if device_override else torch.device(
        "cuda" if torch.cuda.is_available() else "cpu")
    model = get_matanyone2_model(str(weights), device)
    return torch, InferenceCore, model, device


def compile_policy(runtime, mode, max_size, frames, cutoff_frames=0):
    if mode == "eager":
        return False, 0
    if mode == "partial":
        return True, 0
    try:
        policy = optimization_policy(runtime)
        sizes = {int(value) for value in policy.get("detailSizes", [512])}
        measured = max(0, int(policy.get("breakEvenFrames", 0)))
        break_even = max(0, int(cutoff_frames)) or measured
        return (bool(policy.get("enabled")) and max_size in sizes and
                frames >= break_even), break_even
    except (OSError, ValueError, TypeError):
        return False, 0


def optimization_policy(runtime):
    for path in (runtime / COMPILE_POLICY,
                 Path(__file__).with_name(COMPILE_POLICY)):
        try:
            return json.loads(path.read_text())
        except (OSError, ValueError, TypeError):
            pass
    return {}


def apply_partial_compile(torch, model, runtime):
    cache = runtime / "torchinductor-matanyone2"
    cache.mkdir(parents=True, exist_ok=True)
    os.environ["TORCHINDUCTOR_CACHE_DIR"] = str(cache)
    originals = (model.pixel_encoder, model.pix_feat_proj, model.key_proj)
    model.pixel_encoder = torch.compile(model.pixel_encoder,
        mode="max-autotune-no-cudagraphs", fullgraph=False)
    model.pix_feat_proj = torch.compile(model.pix_feat_proj,
        mode="max-autotune-no-cudagraphs", fullgraph=False)
    model.key_proj = torch.compile(model.key_proj,
        mode="max-autotune-no-cudagraphs", fullgraph=False)
    return originals


def restore_eager(model, originals):
    model.pixel_encoder, model.pix_feat_proj, model.key_proj = originals


def extract_earlier_frames(ffmpeg, source, destination, start, frame_rate,
                           width, height, frame_count, profiler):
    if frame_count <= 0:
        return
    emit("preparing", 0,
         f"Preparing {frame_count + 1} frames for backward propagation...")
    started = time.perf_counter()
    process = subprocess.Popen([ffmpeg, "-y", "-v", "error", "-ss", f"{start:.6f}",
        "-i", str(source), "-map", "0:v:0", "-vf",
        f"fps={frame_rate},scale={width}:{height}:flags=lanczos,setsar=1",
        "-frames:v", str(frame_count + 1), "-start_number", "0", "-q:v", "2",
        str(destination / "%08d.jpg"), "-progress", "pipe:1", "-nostats"],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    for line in process.stdout:
        if line.startswith("frame="):
            try:
                current = min(frame_count + 1, int(line.partition("=")[2]))
                emit("preparing", current * 5 / (frame_count + 1),
                     f"Prepared {current}/{frame_count + 1} source frames")
            except ValueError:
                pass
    error = process.stderr.read()
    if process.wait() != 0:
        raise RuntimeError(error.strip() or "Could not prepare frames for backward propagation")
    profiler.add("backward_source_cache_io", time.perf_counter() - started)
    if not (destination / f"{frame_count:08d}.jpg").is_file():
        raise RuntimeError("The selected MatAnyone mask frame could not be decoded")


class FrameSlot:
    def __init__(self, torch, process_height, process_width, device):
        self.index = -1
        self.frame = None
        self.preview_frame = None
        self.input_cpu = torch.empty((process_height, process_width, 3),
            dtype=torch.uint8, pin_memory=device.type == "cuda")
        self.input_gpu = torch.empty((3, process_height, process_width),
            dtype=torch.float32, device=device)
        self.alpha_cpu = torch.empty((process_height, process_width),
            dtype=torch.uint8, pin_memory=device.type == "cuda")
        self.alpha_gpu = None
        self.download_done = None
        self.gpu_timings = []
        self.direct_alpha = None

    def reset(self):
        self.index = -1
        self.frame = None
        self.preview_frame = None
        self.alpha_gpu = None
        self.download_done = None
        self.gpu_timings.clear()
        self.direct_alpha = None


class BoundedPipeline:
    def __init__(self, torch, device, process_width, process_height,
                 profiler, prepare, infer, consume, mode="bounded"):
        self.torch = torch
        self.device = device
        self.profiler = profiler
        self.prepare = prepare
        self.infer = infer
        self.consume = consume
        self.mode = mode
        self.slots = [FrameSlot(torch, process_height, process_width, device)
                      for _ in range(PIPELINE_SLOTS if mode == "bounded" else 1)]
        self.free = queue.Queue(QUEUE_DEPTH + 1)
        self.ready = queue.Queue(QUEUE_DEPTH)
        self.finished = queue.Queue(QUEUE_DEPTH)
        for slot in self.slots:
            self.free.put(slot)
        self.stop = threading.Event()
        self.error = queue.Queue()

    def _put(self, target, value):
        while not self.stop.is_set():
            try:
                target.put(value, timeout=.1)
                return True
            except queue.Full:
                pass
        return False

    def _input(self, items):
        try:
            for item in items:
                if self.stop.is_set():
                    break
                while not self.stop.is_set():
                    try:
                        slot = self.free.get(timeout=.1)
                        break
                    except queue.Empty:
                        pass
                else:
                    break
                slot.reset()
                self.prepare(slot, item)
                if not self._put(self.ready, slot):
                    break
            self._put(self.ready, None)
        except BaseException as error:
            self.error.put(error)
            self.stop.set()

    def _output(self):
        try:
            while not self.stop.is_set():
                try:
                    slot = self.finished.get(timeout=.1)
                except queue.Empty:
                    continue
                if slot is None:
                    return
                if slot.download_done is not None:
                    slot.download_done.synchronize()
                for name, start, end in slot.gpu_timings:
                    self.profiler.add(name, start.elapsed_time(end) / 1000)
                self.consume(slot)
                slot.reset()
                self._put(self.free, slot)
        except BaseException as error:
            self.error.put(error)
            self.stop.set()

    def run(self, items):
        if self.mode == "serial":
            slot = self.slots[0]
            for item in items:
                slot.reset()
                self.prepare(slot, item)
                self.infer(slot)
                if slot.download_done is not None:
                    slot.download_done.synchronize()
                for name, start, end in slot.gpu_timings:
                    self.profiler.add(name, start.elapsed_time(end) / 1000)
                self.consume(slot)
            return
        input_thread = threading.Thread(target=self._input, args=(items,),
                                        name="matanyone-input", daemon=True)
        output_thread = threading.Thread(target=self._output,
                                         name="matanyone-output", daemon=True)
        input_thread.start()
        output_thread.start()
        try:
            while not self.stop.is_set():
                try:
                    slot = self.ready.get(timeout=.1)
                except queue.Empty:
                    if not self.error.empty():
                        break
                    continue
                if slot is None:
                    self._put(self.finished, None)
                    break
                self.infer(slot)
                if not self._put(self.finished, slot):
                    break
        except BaseException as error:
            self.error.put(error)
            self.stop.set()
        finally:
            input_thread.join(timeout=5)
            output_thread.join(timeout=30)
        if not self.error.empty():
            raise self.error.get()
        if input_thread.is_alive() or output_thread.is_alive():
            self.stop.set()
            raise RuntimeError("MatAnyone pipeline did not shut down cleanly")


def _process_once(args, compile_enabled):
    import numpy as np
    from PIL import Image

    cv2 = None
    if args.resize_backend in ("opencv", "opencv-output"):
        try:
            import cv2 as cv2_module
            cv2 = cv2_module
        except ImportError:
            log_record("resize_fallback", requested="opencv", actual="pillow")
            args.resize_backend = "pillow"

    def resize_array(value, size, output_stage=False):
        if cv2 is not None and (args.resize_backend == "opencv" or output_stage):
            return cv2.resize(value, size, interpolation=cv2.INTER_LINEAR)
        mode = "RGB" if value.ndim == 3 else "L"
        return np.asarray(Image.fromarray(value, mode).resize(
            size, Image.Resampling.BILINEAR))

    profiler = StageProfiler()
    source, output = args.source.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    width, height, frame_rate, fps, source_duration = probe(source)
    process_width, process_height = processing_size(width, height, args.max_size)
    start = args.start_ms / 1000
    end = source_duration if args.end_ms is None else args.end_ms / 1000
    if start < 0 or end <= start or end > source_duration + .1:
        raise RuntimeError("clip range is outside the source video")
    duration = min(end, source_duration) - start
    total = max(1, round(duration * fps))
    if args.max_frames:
        total = min(total, args.max_frames)
        duration = total / fps
    selected_ms = args.start_ms if args.mask_frame_ms is None else args.mask_frame_ms
    selected_index = mask_frame_index(selected_ms, args.start_ms, fps, total)
    ffmpeg = executable("ffmpeg")
    runtime = args.runtime.resolve()
    raw_alpha = None
    if args.raw_alpha_output:
        args.raw_alpha_output.parent.mkdir(parents=True, exist_ok=True)
        raw_alpha = np.memmap(args.raw_alpha_output, mode="w+", dtype=np.uint8,
                              shape=(total, process_height, process_width))

    with tempfile.TemporaryDirectory(prefix=".matanyone-", dir=output) as work_value:
        work = Path(work_value)
        mask_path = args.mask
        if args.auto_rvm_init:
            # Keep the cleaned initializer beside the processed media so the
            # host can retain it for later MatAnyone reprocessing.
            mask_path = output / "initial-mask.png"
            initializer_size = min(512, args.max_size) if args.max_size > 0 else 512
            initializer_width, initializer_height = processing_size(
                width, height, initializer_size)
            selected_index = automatic_rvm_mask(source, runtime, start, frame_rate,
                initializer_width, initializer_height, min(8, total), mask_path,
                profiler, args.rvm_alpha_threshold)
        prepared = work / "source"
        prepared.mkdir()
        extract_earlier_frames(ffmpeg, source, prepared, start, frame_rate,
                               process_width, process_height, selected_index, profiler)
        prepared_bytes = sum(path.stat().st_size for path in prepared.glob("*.jpg"))
        profiler.set_temp_bytes(prepared_bytes)

        progress_base = 5 if selected_index else 0
        emit("startup", progress_base, "Loading MatAnyone 2...")
        load_started = time.perf_counter()
        device_override = None if args.device == "auto" else args.device
        torch, InferenceCore, model, device = load_model(runtime, device_override)
        profiler.add("model_load", time.perf_counter() - load_started)
        fp16 = device.type == "cuda" and fast_fp16(torch.cuda.get_device_capability(device))
        if args.precision_mode == "half" and device.type == "cuda":
            model.half()
        elif args.precision_mode == "half":
            log_record("precision_fallback", requested="half", actual="float32")
        if device.type == "cuda":
            torch.backends.cudnn.benchmark = True
            torch.set_float32_matmul_precision("high")
            torch.cuda.reset_peak_memory_stats(device)
        originals = None
        if compile_enabled and device.type == "cuda":
            ready = (runtime / COMPILE_READY).is_file()
            emit("compile", progress_base,
                 "Loading cached MatAnyone 2 optimization..." if ready else
                 "Compiling MatAnyone 2 image features (one-time for this detail size)...")
            compile_started = time.perf_counter()
            try:
                originals = apply_partial_compile(torch, model, runtime)
            except BaseException as error:
                raise RuntimeError(
                    f"compiled MatAnyone execution failed: {error}") from error
            profiler.add("compile_setup", time.perf_counter() - compile_started)
        emit("startup", progress_base, f"MatAnyone 2 inference at {process_width}x{process_height}; "
             f"output remains {width}x{height}; initial mask frame {selected_index + 1}/{total}")

        def new_processor():
            return InferenceCore(model, cfg=model.cfg, device=device)

        mask = Image.open(mask_path).convert("L").resize(
            (process_width, process_height), Image.Resampling.NEAREST)
        mask_tensor = torch.from_numpy(np.asarray(mask, dtype=np.uint8).copy()).float().to(device)
        transfer_stream = torch.cuda.Stream(device=device) if device.type == "cuda" else None

        def upload(slot):
            if device.type == "cuda":
                begin, end_event = torch.cuda.Event(True), torch.cuda.Event(True)
                begin.record()
                slot.input_gpu.copy_(slot.input_cpu.permute(2, 0, 1), non_blocking=True)
                slot.input_gpu.div_(255)
                end_event.record()
                slot.gpu_timings.append(("pinned_upload", begin, end_event))
            else:
                started = time.perf_counter()
                slot.input_gpu.copy_(slot.input_cpu.permute(2, 0, 1))
                slot.input_gpu.div_(255)
                profiler.add("pinned_upload", time.perf_counter() - started)
            return slot.input_gpu

        def download_alpha(slot, processor, output_prob):
            if device.type == "cuda":
                alpha_start, alpha_end = torch.cuda.Event(True), torch.cuda.Event(True)
                alpha_start.record()
                slot.alpha_gpu = processor.output_prob_to_mask(output_prob).clamp(
                    0, 1).mul(255).to(torch.uint8).contiguous()
                alpha_end.record()
                copy_done = torch.cuda.Event()
                with torch.cuda.stream(transfer_stream):
                    transfer_stream.wait_event(alpha_end)
                    copy_start = torch.cuda.Event(True)
                    copy_end = torch.cuda.Event(True)
                    copy_start.record(transfer_stream)
                    slot.alpha_cpu.copy_(slot.alpha_gpu, non_blocking=True)
                    copy_end.record(transfer_stream)
                    copy_done.record(transfer_stream)
                slot.download_done = copy_done
                slot.gpu_timings.append(("alpha_conversion", alpha_start, alpha_end))
                slot.gpu_timings.append(("alpha_download", copy_start, copy_end))
            else:
                started = time.perf_counter()
                alpha = processor.output_prob_to_mask(output_prob).clamp(
                    0, 1).mul(255).to(torch.uint8)
                slot.alpha_cpu.copy_(alpha)
                profiler.add("alpha_conversion_download", time.perf_counter() - started)

        def step(slot, processor, first_frame=False):
            frame_tensor = upload(slot)
            try:
                if device.type == "cuda":
                    begin, end_event = torch.cuda.Event(True), torch.cuda.Event(True)
                    begin.record()
                started = time.perf_counter()
                with torch.inference_mode(), torch.amp.autocast(
                        device_type=device.type, enabled=fp16):
                    if first_frame:
                        processor.step(frame_tensor, mask_tensor, objects=[1])
                        for _ in range(11):
                            output_prob = processor.step(frame_tensor, first_frame_pred=True)
                    else:
                        output_prob = processor.step(frame_tensor)
                if device.type == "cuda":
                    end_event.record()
                    slot.gpu_timings.append((
                        "warmup" if first_frame else "processor_step", begin, end_event))
                else:
                    profiler.add("warmup" if first_frame else "processor_step",
                                 time.perf_counter() - started)
                download_alpha(slot, processor, output_prob)
            except BaseException as error:
                if compile_enabled:
                    raise RuntimeError(f"compiled MatAnyone execution failed: {error}") from error
                raise

        last_preview = 0.0
        checker_cache = {}
        preview_width, preview_height = preview_size(width, height)

        def save_preview(frame, alpha, force=False):
            nonlocal last_preview
            if args.disable_previews:
                return
            now = time.monotonic()
            if not force and now - last_preview < PREVIEW_INTERVAL:
                return
            started = time.perf_counter()
            preview_frame = frame
            if (frame.shape[1], frame.shape[0]) != (preview_width, preview_height):
                preview_frame = resize_array(frame, (preview_width, preview_height), True)
            if alpha.shape != (preview_height, preview_width):
                alpha = resize_array(alpha, (preview_width, preview_height), True)
            shape = (preview_height, preview_width)
            checker = checker_cache.get(shape)
            if checker is None:
                yy, xx = np.indices(shape)
                checker = np.where((((xx // 24) + (yy // 24)) & 1)[..., None],
                                   190, 125).astype(np.uint8)
                checker_cache[shape] = checker
            opacity = alpha[..., None].astype(np.float32) / 255
            composite = np.rint(preview_frame * opacity + checker *
                                (1 - opacity)).astype(np.uint8)
            for name, data in (("preview-source.jpg", preview_frame),
                               ("preview-composite.jpg", composite)):
                temporary = output / (name + ".tmp")
                Image.fromarray(data, "RGB").save(temporary, "JPEG", quality=86)
                replace_preview(temporary, output / name)
            last_preview = now
            profiler.add("preview_generation", time.perf_counter() - started)

        work_total = total + selected_index
        completed_work = 0
        progress_lock = threading.Lock()

        def work_done(message):
            nonlocal completed_work
            with progress_lock:
                completed_work += 1
                percent = min(99, progress_base + completed_work *
                              (99 - progress_base) / work_total)
            emit("inference", percent, message)

        alpha_map = None
        alpha_map_path = work / "backward-alpha.u8"
        if selected_index:
            required = selected_index * process_width * process_height
            free = shutil.disk_usage(work).free
            if args.temporary_space_limit_bytes is not None:
                free = min(free, args.temporary_space_limit_bytes)
            if free < required + 64 * 1024 * 1024:
                raise RuntimeError(f"Insufficient temporary disk space for backward alpha cache: "
                                   f"need {required / (1024 ** 2):.0f} MB")
            alpha_map = np.memmap(alpha_map_path, mode="w+", dtype=np.uint8,
                                  shape=(selected_index, process_height, process_width))
            profiler.set_temp_bytes(prepared_bytes + required)
            backward = new_processor()
            selected_frame = np.asarray(Image.open(
                prepared / f"{selected_index:08d}.jpg").convert("RGB"))
            warm_slot = FrameSlot(torch, process_height, process_width, device)
            warm_slot.input_cpu.copy_(torch.from_numpy(selected_frame.copy()))
            emit("inference", progress_base,
                 "Warming up MatAnyone 2 on the selected middle frame...")
            step(warm_slot, backward, first_frame=True)
            if warm_slot.download_done is not None:
                warm_slot.download_done.synchronize()
            for name, begin, finish in warm_slot.gpu_timings:
                profiler.add(name, begin.elapsed_time(finish) / 1000)
            if compile_enabled and not (runtime / COMPILE_READY).is_file():
                (runtime / COMPILE_READY).write_text(json.dumps({
                    "detail": args.max_size, "createdUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
                }))

            def backward_items():
                for index in range(selected_index - 1, -1, -1):
                    yield index

            def prepare_backward(slot, index):
                started = time.perf_counter()
                frame_path = prepared / f"{index:08d}.jpg"
                frame = np.asarray(Image.open(frame_path).convert("RGB"))
                profiler.add("decode_read", time.perf_counter() - started)
                slot.index, slot.frame = index, frame
                started = time.perf_counter()
                slot.input_cpu.copy_(torch.from_numpy(frame.copy()))
                profiler.add("resize", time.perf_counter() - started)
                frame_path.unlink(missing_ok=True)

            def infer_backward(slot):
                step(slot, backward)

            def consume_backward(slot):
                started = time.perf_counter()
                alpha_map[slot.index] = slot.alpha_cpu.numpy()
                profiler.add("backward_cache_io", time.perf_counter() - started)
                save_preview(slot.frame, slot.alpha_cpu.numpy())
                work_done(f"Propagated backward {selected_index - slot.index}/{selected_index} frames")

            BoundedPipeline(torch, device, process_width, process_height, profiler,
                            prepare_backward, infer_backward, consume_backward,
                            args.pipeline_mode).run(backward_items())
            alpha_map.flush()
            del backward

        decode = encode = None
        count = 0
        try:
            video_codec, alpha_codec, encoder_name = output_codecs(
                ffmpeg, width, height, args.force_software_encode,
                args.encoder_preset)
            decode = source_and_foreground_encoder(ffmpeg, source, output, start,
                duration, frame_rate, width, height, total, video_codec,
                process_width, process_height, "bilinear")
            encode = alpha_encoder(ffmpeg, output, frame_rate, width, height, total,
                process_width, process_height, alpha_codec, scale="bilinear")
            frame_bytes = process_width * process_height * 3

            def forward_items():
                for index in range(total):
                    started = time.perf_counter()
                    value = bytearray(frame_bytes)
                    view, received = memoryview(value), 0
                    while received < frame_bytes:
                        size = decode.stdout.readinto(view[received:])
                        if not size:
                            break
                        received += size
                    profiler.add("decode_read", time.perf_counter() - started)
                    if not received:
                        return
                    if received != frame_bytes:
                        raise RuntimeError("source decoder returned a partial frame")
                    yield index, np.frombuffer(value, np.uint8).reshape(
                        process_height, process_width, 3)

            def prepare_forward(slot, item):
                index, frame = item
                slot.index = index
                started = time.perf_counter()
                needs_resize = (frame.shape[1], frame.shape[0]) != \
                    (process_width, process_height)
                if not needs_resize:
                    resized = frame
                else:
                    resized = resize_array(frame, (process_width, process_height))
                # At Standard detail and above, the inference RGB is already very
                # close to the capped preview size. Reuse it so the output worker
                # does not downscale the full-resolution source and an already
                # upscaled alpha a second time solely for UI snapshots.
                if (process_width >= preview_width * .9 and
                        process_height >= preview_height * .9):
                    slot.preview_frame = resized
                else:
                    slot.preview_frame = frame
                slot.input_cpu.copy_(torch.from_numpy(resized.copy()))
                profiler.add("resize" if needs_resize else "input_copy",
                             time.perf_counter() - started)

            forward = new_processor()

            def infer_forward(slot):
                if slot.index < selected_index:
                    started = time.perf_counter()
                    slot.direct_alpha = np.array(alpha_map[slot.index], copy=True)
                    profiler.add("backward_cache_io", time.perf_counter() - started)
                elif slot.index == selected_index:
                    emit("inference", min(99, progress_base + completed_work *
                         (99 - progress_base) / work_total),
                         "Resetting MatAnyone 2 for forward propagation...")
                    step(slot, forward, first_frame=True)
                    if compile_enabled and not (runtime / COMPILE_READY).is_file():
                        (runtime / COMPILE_READY).write_text(json.dumps({
                            "detail": args.max_size,
                            "createdUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
                        }))
                else:
                    step(slot, forward)

            def consume_forward(slot):
                nonlocal count
                internal_alpha = (slot.direct_alpha if slot.direct_alpha is not None
                                  else slot.alpha_cpu.numpy())
                if raw_alpha is not None:
                    raw_alpha[slot.index] = internal_alpha
                save_preview(slot.preview_frame, internal_alpha,
                             force=slot.index == total - 1)
                started = time.perf_counter()
                output_bytes = memoryview(internal_alpha).cast("B")
                while output_bytes:
                    written = encode.stdin.write(output_bytes)
                    if not written:
                        raise RuntimeError("FFmpeg output encoder closed unexpectedly")
                    output_bytes = output_bytes[written:]
                profiler.add("encoder_write", time.perf_counter() - started)
                count += 1
                work_done(f"Processed {count}/{total} frames")

            BoundedPipeline(torch, device, process_width, process_height, profiler,
                            prepare_forward, infer_forward, consume_forward,
                            args.pipeline_mode).run(forward_items())
            finalize_started = time.perf_counter()
            encode.stdin.close()
            decode.stdout.close()
            if decode.wait() != 0:
                raise RuntimeError("source normalization/foreground encoding failed")
            if encode.wait() != 0:
                raise RuntimeError("FFmpeg alpha encoding failed")
            profiler.add("encoder_finalize", time.perf_counter() - finalize_started)
        except BaseException:
            if decode is not None:
                try: decode.kill()
                except Exception: pass
            if encode is not None:
                try: encode.kill()
                except Exception: pass
            raise
        finally:
            if alpha_map is not None:
                try:
                    alpha_map.flush()
                    del alpha_map
                except Exception:
                    pass
            try:
                alpha_map_path.unlink(missing_ok=True)
            except OSError:
                pass
            if originals is not None:
                restore_eager(model, originals)

    if raw_alpha is not None:
        raw_alpha.flush()
        del raw_alpha

    if count != total:
        raise RuntimeError(f"source decoder returned {count} of {total} expected frames")
    initial_mask_frame_ms = round((start + selected_index / fps) * 1000)
    (output / "result.json").write_text(json.dumps({"width": width, "height": height,
        "frameRate": frame_rate, "durationMs": round(count * 1000 / fps),
        "initialMaskFrameMs": initial_mask_frame_ms}, indent=2))
    compile_stats = {}
    if compile_enabled:
        try:
            counters = torch._dynamo.utils.counters
            compile_stats = {
                category: {str(key): int(value) for key, value in values.items()}
                for category, values in counters.items()
                if category in ("frames", "stats", "inductor", "aot_autograd")
            }
        except (AttributeError, TypeError, ValueError):
            pass
    profiler.report(torch, device, count, {
        "pipeline": args.pipeline_mode,
        "compile": "partial" if compile_enabled else "eager",
        "previews": not args.disable_previews,
        "detail": args.max_size,
        "resizeBackend": args.resize_backend,
        "precisionMode": args.precision_mode,
        "encoder": encoder_name,
        "encoderPreset": args.encoder_preset if encoder_name == "h264_nvenc" else "slow/medium",
        "sourceResize": "ffmpeg-bilinear",
        "alphaResize": "ffmpeg-bilinear",
        "selectedFrame": selected_index,
    }, compile_stats)
    emit("complete", 100, "MatAnyone 2 foreground and alpha are ready for preview")


def process(args):
    width, height, _, fps, source_duration = probe(args.source.resolve())
    end = source_duration if args.end_ms is None else args.end_ms / 1000
    total = max(1, round((end - args.start_ms / 1000) * fps))
    if args.max_frames:
        total = min(total, args.max_frames)
    policy = optimization_policy(args.runtime.resolve())
    if args.resize_backend == "auto":
        args.resize_backend = policy.get("resizeBackend", "pillow")
    if args.precision_mode == "auto":
        args.precision_mode = policy.get("precisionMode", "autocast")
    enabled, break_even = compile_policy(args.runtime.resolve(), args.compile_mode,
                                         args.max_size, total,
                                         args.compile_cutoff_frames)
    log_record("configuration", sourceSize=f"{width}x{height}", frames=total,
               pipeline=args.pipeline_mode, compileRequested=args.compile_mode,
               compileEnabled=enabled, compileBreakEvenFrames=break_even,
               previews=not args.disable_previews, resizeBackend=args.resize_backend,
               precisionMode=args.precision_mode)
    try:
        _process_once(args, enabled)
    except RuntimeError as error:
        if not enabled or not str(error).startswith("compiled MatAnyone execution failed:"):
            raise
        log_record("compile_fallback", error=str(error))
        emit("startup", 0, "MatAnyone 2 optimization failed; retrying safely in eager mode...")
        for name in ("foreground.mp4", "alpha.mkv", "result.json"):
            try: (args.output.resolve() / name).unlink(missing_ok=True)
            except OSError: pass
        _process_once(args, False)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--mask", type=Path)
    parser.add_argument("--auto-rvm-init", action="store_true")
    parser.add_argument("--rvm-alpha-threshold", type=float, default=.40)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--mask-frame-ms", type=int)
    parser.add_argument("--max-size", type=int,
                        choices=(0, 256, 384, 512, 768, 1024), default=512)
    parser.add_argument("--pipeline-mode", choices=("serial", "bounded"),
                        default="bounded", help=argparse.SUPPRESS)
    parser.add_argument("--compile-mode", choices=("auto", "eager", "partial"),
                        default="auto", help=argparse.SUPPRESS)
    parser.add_argument("--compile-cutoff-frames", type=int, default=0,
                        help=argparse.SUPPRESS)
    parser.add_argument("--device", choices=("auto", "cpu", "cuda"),
                        default="auto", help=argparse.SUPPRESS)
    parser.add_argument("--disable-previews", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--resize-backend",
                        choices=("auto", "pillow", "opencv-output", "opencv"),
                        default="auto", help=argparse.SUPPRESS)
    parser.add_argument("--precision-mode", choices=("auto", "autocast", "half"),
                        default="auto", help=argparse.SUPPRESS)
    parser.add_argument("--max-frames", type=int, help=argparse.SUPPRESS)
    parser.add_argument("--raw-alpha-output", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--force-software-encode", action="store_true",
                        help=argparse.SUPPRESS)
    parser.add_argument("--encoder-preset", choices=tuple(f"p{i}" for i in range(1, 8)),
                        default="p5")
    parser.add_argument("--temporary-space-limit-bytes", type=int,
                        help=argparse.SUPPRESS)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        assert processing_size(3840, 2160, 512) == (910, 512)
        assert processing_size(1920, 1080, 0) == (1920, 1080)
        assert preview_size(3840, 2160) == (960, 540)
        assert preview_size(720, 1280) == (304, 540)
        assert mask_frame_index(5_000, 1_000, 25, 200) == 100
        assert mask_frame_index(0, 1_000, 25, 200) == 0
        assert mask_frame_index(99_000, 1_000, 25, 200) == 199
        import numpy as np
        import torch
        order = []
        profiler = StageProfiler(False)
        device = torch.device("cpu")

        def prepare(slot, index):
            slot.index = index
            slot.frame = np.full((2, 2, 3), index, np.uint8)

        def infer(slot):
            slot.direct_alpha = np.full((2, 2), slot.index, np.uint8)

        def consume(slot):
            assert int(slot.direct_alpha[0, 0]) == slot.index
            order.append(slot.index)

        BoundedPipeline(torch, device, 2, 2, profiler, prepare, infer,
                        consume).run(range(7))
        assert order == list(range(7))
        alpha = np.zeros((24, 24), np.float32)
        alpha[4:20, 6:18] = .9
        alpha[10, 10] = 0
        alpha[1, 1] = 1
        cleaned = clean_rvm_mask(alpha)
        assert cleaned.dtype == np.uint8 and cleaned[10, 10] == 255
        assert cleaned[1, 1] == 0 and set(np.unique(cleaned)) <= {0, 255}
        assert rvm_frame_score(alpha) > rvm_frame_score(np.zeros_like(alpha))
        print("MatAnyone 2 worker self-test passed")
    elif not all((args.source, args.output, args.runtime)) or \
            not args.auto_rvm_init and args.mask is None:
        parser.error("--source, --output, --runtime, and either --mask or --auto-rvm-init are required")
    else:
        if not .10 <= args.rvm_alpha_threshold <= .90:
            parser.error("--rvm-alpha-threshold must be between 0.10 and 0.90")
        if args.device == "cpu":
            os.environ["CUDA_VISIBLE_DEVICES"] = "-1"
        process(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", 0, str(error))
        raise
