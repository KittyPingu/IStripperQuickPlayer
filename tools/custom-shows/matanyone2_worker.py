#!/usr/bin/env python3
"""QuickPlayer MatAnyone 2 worker. Stdout is NDJSON progress only."""
import argparse
from collections import deque
import ctypes
import gc
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
from custom_show_worker import (alpha_encoder, output_codecs,
                                source_and_foreground_encoder)

COMMIT = "0079197acd6d16a741f71558809c06c586c579e0"
WEIGHTS_HASH = "5e9821e4087231427376b437c85bb6e072b41e582314f06fd524f75bc4af5914"
COMPILE_POLICY = "matanyone2-compile-policy.json"
COMPILE_READY = "matanyone2-partial-compile-ready.json"
PIPELINE_SLOTS = 3
QUEUE_DEPTH = 2
PREVIEW_INTERVAL = .5
PREVIEW_MAXIMUM = (960, 540)
RVM_REFRESH_MISSING_RATIO = .01
RVM_REFRESH_PERSISTENCE = 3
RVM_REFRESH_COOLDOWN = 15
PROP_REFRESH_PERSISTENCE = 3
PROP_REFRESH_COOLDOWN = 15
LONG_TERM_MAX_TOKENS = 4000
LONG_TERM_BUFFER_TOKENS = 500
INTERACTIVE_CHECKPOINT_INTERVAL = 250


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


def interactive_anchor_index(frame_ms, start_ms, fps, total,
                             selected_index, current_index):
    index = mask_frame_index(frame_ms, start_ms, fps, total)
    if index < selected_index or index > current_index:
        raise ValueError("correction frame is outside the available MatAnyone history")
    return index


def interactive_checkpoint_frame(frame_index, selected_index,
                                 interval=INTERACTIVE_CHECKPOINT_INTERVAL):
    return frame_index >= selected_index and \
        (frame_index - selected_index + 1) % interval == 0


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


def rvm_initializer_offsets(total, fps, maximum_seconds=30):
    offsets = []
    for seconds in range(maximum_seconds + 1):
        frame = round(seconds * fps)
        if frame >= total:
            break
        if not offsets or frame != offsets[-1][1]:
            offsets.append((seconds, frame))
    return offsets


def rvm_refresh_candidate(missing_pixels, rvm_pixels, total_pixels,
                          minimum_at_512=100):
    minimum_pixels = max(16, round(
        total_pixels * minimum_at_512 / (512 * 512)))
    return (missing_pixels > minimum_pixels and rvm_pixels > 0 and
            missing_pixels / rvm_pixels > RVM_REFRESH_MISSING_RATIO)


def refresh_due(streak, frame, last_refresh, persistence, cooldown):
    return streak >= persistence and frame - last_refresh >= cooldown


def valid_memory_configuration(max_mem_frames, use_long_term):
    return (2 <= max_mem_frames <= 30 and
            (not use_long_term or 6 <= max_mem_frames <= 14))


def resolved_frame_count(expected, decoded):
    if decoded == expected:
        return decoded
    # Some MP4 edit lists advertise one more frame than FFmpeg can decode. A
    # clean decoder exit confirms this is an end-of-stream metadata mismatch,
    # not a partial frame or decoder failure (both are rejected earlier).
    if decoded == expected - 1:
        return decoded
    raise RuntimeError(
        f"source decoder returned {decoded} of {expected} expected frames")


def corrected_rvm_refresh_mask(torch, matanyone_alpha, rvm_foreground,
                               refresh_strength=1, prop_foreground=None):
    if matanyone_alpha.ndim != 2 or rvm_foreground.ndim != 2:
        raise RuntimeError("RVM refresh requires two-dimensional alpha masks")
    inverse = (~rvm_foreground).float()[None, None]
    rvm_core = (1 - torch.nn.functional.max_pool2d(
        inverse, kernel_size=5, stride=1, padding=2))[0, 0]
    corrected = torch.maximum(matanyone_alpha,
                              rvm_core * refresh_strength)
    if prop_foreground is not None:
        corrected = torch.maximum(
            corrected, prop_foreground.float() * refresh_strength)
    return corrected.mul(255)


def augment_refresh_foreground(prop_mask, rvm_foreground, proximity_radius):
    import numpy as np
    from prop_segmenter import augment_rvm_mask
    rvm = np.asarray(rvm_foreground, dtype=bool)
    combined, components, radius = augment_rvm_mask(
        prop_mask, rvm, proximity_radius)
    added_pixels = int((combined & ~rvm).sum())
    return combined, components, radius, added_pixels


def prop_contribution_frame(frame, detected, added, injected=False):
    """Tint retained model support cyan and pixels injected this frame green."""
    import numpy as np
    result = np.asarray(frame, dtype=np.uint8).copy()
    detected = np.asarray(detected, dtype=bool)
    added = np.asarray(added, dtype=bool)
    cyan = detected & ~added
    result[cyan] = (result[cyan].astype(np.uint16) * 2 // 5 +
                    np.array((0, 153, 153), dtype=np.uint16)).astype(np.uint8)
    result[added] = (result[added].astype(np.uint16) // 4 +
                     np.array((0, 191, 0), dtype=np.uint16)).astype(np.uint8)
    if injected:
        thickness = max(2, min(result.shape[:2]) // 128)
        result[:thickness, :] = (0, 255, 0)
        result[-thickness:, :] = (0, 255, 0)
        result[:, :thickness] = (0, 255, 0)
        result[:, -thickness:] = (0, 255, 0)
    return result


def prop_contribution_encoder(ffmpeg, destination, frame_rate, width, height):
    return subprocess.Popen([ffmpeg, "-y", "-v", "warning", "-f", "rawvideo",
        "-pix_fmt", "rgb24", "-s", f"{width}x{height}", "-r", frame_rate,
        "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "veryfast",
        "-crf", "18", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
        "-pix_fmt", "yuv420p", str(destination)], stdin=subprocess.PIPE)


def automatic_rvm_mask(source, runtime, start, frame_rate, fps, total,
                       width, height, sample_count, destination, profiler,
                       threshold=.40, prop_package=None):
    import numpy as np
    from PIL import Image
    frame_bytes = width * height * 3
    torch, model, device = load_rvm_model(runtime, "quality")
    fp16 = device.type == "cuda" and fast_fp16(torch.cuda.get_device_capability(device))
    offsets = rvm_initializer_offsets(total, fps)
    try:
        for attempt, (offset_seconds, offset_frame) in enumerate(offsets, 1):
            count_limit = min(sample_count, total - offset_frame)
            if offset_seconds == 0:
                message = f"Running RVM on the first {count_limit} frames..."
            else:
                message = (f"No person found; retrying RVM at +{offset_seconds}s "
                           "(maximum +30s)...")
            emit("initializing", min(3.5, offset_seconds * 3.5 / 30), message)
            command = [executable("ffmpeg"), "-v", "error", "-ss",
                f"{start + offset_seconds:.6f}", "-i", str(source), "-map", "0:v:0",
                "-vf", f"fps={frame_rate},scale={width}:{height}:flags=bilinear,setsar=1",
                "-frames:v", str(count_limit), "-f", "rawvideo", "-pix_fmt", "rgb24",
                "pipe:1"]
            decode_started = time.perf_counter()
            decoded = subprocess.run(command, stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE)
            if decoded.returncode != 0:
                raise RuntimeError(decoded.stderr.decode(errors="replace").strip() or
                                   "RVM initialization frames could not be decoded")
            count = len(decoded.stdout) // frame_bytes
            if count <= 0:
                if offset_seconds == 0:
                    raise RuntimeError("RVM initialization decoded no frames")
                break
            frames = np.frombuffer(decoded.stdout[:count * frame_bytes],
                                   np.uint8).reshape(count, height, width, 3).copy()
            profiler.add("rvm_initialization_decode",
                         time.perf_counter() - decode_started)
            inference_started = time.perf_counter()
            tensor = torch.from_numpy(frames).permute(0, 3, 1, 2).unsqueeze(0).to(
                device=device, dtype=torch.float32).div_(255)
            with torch.inference_mode(), torch.autocast(device_type=device.type,
                    dtype=torch.float16, enabled=fp16):
                _, alpha, *_ = model(tensor, *([None] * 4), downsample_ratio=1)
            alphas = alpha[0, :, 0].float().cpu().numpy()
            scores = [rvm_frame_score(value, threshold) for value in alphas]
            selected = max(range(count), key=lambda index: scores[index])
            selected_alpha = alphas[selected].copy()
            temporal_indexes = [max(0, selected - 1), selected, min(count - 1, selected + 1)]
            temporal_alphas = [alphas[index].copy() for index in temporal_indexes]
            cleaned = clean_rvm_mask(selected_alpha, threshold=threshold)
            profiler.add("rvm_initialization",
                         time.perf_counter() - inference_started)
            del tensor, alpha, alphas
            if not cleaned.any():
                continue
            if prop_package is not None:
                from prop_segmenter import (augment_rvm_mask, load_package,
                                            predict_mask)
                prop_torch, prop_model, prop_device, prop_manifest = load_package(
                    prop_package, device)
                if prop_manifest.get("architecture") == "rvm-conditioned-convnext-fpn-v2":
                    from prop_segmenter_v2 import confirm_temporal_masks
                    predictions, probabilities = [], []
                    for frame_index, frame_alpha in zip(temporal_indexes, temporal_alphas):
                        frame_prediction, frame_probability = predict_mask(
                            prop_torch, prop_model, prop_device, frames[frame_index],
                            prop_manifest.get("confidenceThreshold", .5),
                            prop_manifest.get("inputSize", 768), frame_alpha)
                        predictions.append(frame_prediction); probabilities.append(frame_probability)
                    high_threshold = min(.95, prop_manifest.get("confidenceThreshold", .5) + .20)
                    predicted = confirm_temporal_masks(predictions,
                        probabilities[1] >= high_threshold,
                        prop_manifest.get("runtime", {}).get("temporalRequired", 2))
                else:
                    predicted, _ = predict_mask(prop_torch, prop_model, prop_device,
                        frames[selected], prop_manifest.get("confidenceThreshold", .5),
                        prop_manifest.get("inputSize", 512), selected_alpha)
                combined, components, radius = augment_rvm_mask(predicted,
                    cleaned >= 128, prop_manifest.get("proximityRadiusAt512", 24))
                cleaned = combined.astype(np.uint8) * 255
                log_record("prop_initializer", modelId=prop_manifest["modelId"],
                           checkpointSha256=prop_manifest["checkpointSha256"],
                           components=components, proximityRadius=radius)
                emit("initializing", 3.8,
                     f"Retained {sum(value['retained'] for value in components)}/"
                     f"{len(components)} nearby prop components...")
                del prop_model
                if prop_device.type == "cuda":
                    prop_torch.cuda.empty_cache()
            selected_frame = min(total - 1, offset_frame + selected)
            Image.fromarray(cleaned, "L").save(destination)
            preview_output = destination.parent
            for name, image, mode in (("preview-source.jpg", frames[selected], "RGB"),
                                      ("preview-composite.jpg", cleaned, "L")):
                temporary = preview_output / (name + ".tmp")
                Image.fromarray(image, mode).save(temporary, "JPEG", quality=88)
                replace_preview(temporary, preview_output / name)
            log_record("rvm_initializer", attempts=attempt, sampledFrames=count,
                       selectedFrame=selected_frame,
                       searchOffsetSeconds=offset_seconds, threshold=threshold,
                       dilationPixels=3,
                       scores=[round(value, 2) for value in scores])
            emit("initializing", 4,
                 f"RVM selected a person at +{offset_seconds}s; starting MatAnyone 2...")
            return selected_frame
        raise RuntimeError(
            "Initial mask could not detect a person within the first 30 seconds "
            "of the clip")
    finally:
        del model
        if device.type == "cuda":
            torch.cuda.empty_cache()


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
        self.prop_detected = None
        self.prop_added = None
        self.prop_injected = False

    def reset(self):
        self.index = -1
        self.frame = None
        self.preview_frame = None
        self.alpha_gpu = None
        self.download_done = None
        self.gpu_timings.clear()
        self.direct_alpha = None
        self.prop_detected = None
        self.prop_added = None
        self.prop_injected = False


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

    v2_sparse_discovery = False
    if args.prop_model is not None:
        try:
            prop_contract = json.loads((args.prop_model / "manifest.json").read_text(
                encoding="utf-8-sig"))
            v2_sparse_discovery = prop_contract.get("architecture") == \
                "rvm-conditioned-convnext-fpn-v2"
        except (OSError, ValueError, TypeError):
            pass

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
    interactive = args.interactive_control.resolve() \
        if args.interactive_control else None
    if interactive is not None:
        interactive.mkdir(parents=True, exist_ok=True)
        for name in ("pause.request", "resume.request", "corrected-mask.png",
                     "paused-mask.png", "correction-frame-ms.txt",
                     "paused-frame.png", "mask-request.json",
                     "mask-request.processing.json"):
            (interactive / name).unlink(missing_ok=True)
        for pattern in ("requested-mask-*", "requested-frame-*"):
            for path in interactive.glob(pattern):
                path.unlink(missing_ok=True)
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
                fps, total, initializer_width, initializer_height, min(8, total),
                mask_path, profiler, args.rvm_alpha_threshold, args.prop_model)
        correction_start_ms = round((start + selected_index / fps) * 1000)
        correction_anchors = {}
        reset_anchors = set()
        checkpoint_folder = work / "interactive-checkpoints"
        checkpoint_files = {}
        anchor_folder = args.anchor_folder.resolve() if args.anchor_folder else interactive
        if interactive is not None:
            checkpoint_folder.mkdir()
        if anchor_folder is not None:
            for path in anchor_folder.glob("correction-*.png"):
                try:
                    frame_ms = int(path.stem.removeprefix("correction-"))
                    index = interactive_anchor_index(frame_ms, args.start_ms, fps,
                        total, selected_index, total - 1)
                    correction_anchors[index] = path
                except ValueError:
                    if interactive is not None:
                        path.unlink(missing_ok=True)
            for path in anchor_folder.glob("reset-*.png"):
                try:
                    frame_ms = int(path.stem.removeprefix("reset-"))
                    index = interactive_anchor_index(frame_ms, args.start_ms, fps,
                        total, selected_index, total - 1)
                    correction_anchors[index] = path
                    reset_anchors.add(index)
                except ValueError:
                    if interactive is not None:
                        path.unlink(missing_ok=True)
        prepared = work / "source"
        prepared.mkdir()
        extract_earlier_frames(ffmpeg, source, prepared, start, frame_rate,
                               process_width, process_height, selected_index, profiler)
        prepared_bytes = sum(path.stat().st_size for path in prepared.glob("*.jpg"))
        profiler.set_temp_bytes(prepared_bytes)
        interactive_alpha = None
        interactive_alpha_path = work / "interactive-alpha.u8"
        interactive_frames = work / "interactive-frames"
        interactive_alpha_ready = [False] * max(0, total - selected_index)
        if interactive is not None:
            interactive_frames.mkdir()
            history_bytes = ((total - selected_index) * process_width *
                             process_height)
            if shutil.disk_usage(work).free < history_bytes + 64 * 1024 * 1024:
                raise RuntimeError("Insufficient temporary disk space for interactive "
                                   f"mask history: need {history_bytes / (1024 ** 2):.0f} MB")
            interactive_alpha = np.memmap(interactive_alpha_path, mode="w+",
                dtype=np.uint8, shape=(total - selected_index,
                                      process_height, process_width))
            profiler.set_temp_bytes(prepared_bytes + history_bytes)

        progress_base = 5 if selected_index else 0
        emit("startup", progress_base, "Loading MatAnyone 2...")
        load_started = time.perf_counter()
        device_override = None if args.device == "auto" else args.device
        torch, InferenceCore, model, device = load_model(runtime, device_override)
        model.cfg.use_long_term = args.use_long_term
        if args.use_long_term:
            model.cfg.long_term.max_mem_frames = args.max_mem_frames
            model.cfg.long_term.max_num_tokens = LONG_TERM_MAX_TOKENS
            model.cfg.long_term.buffer_tokens = LONG_TERM_BUFFER_TOKENS
        else:
            model.cfg.max_mem_frames = args.max_mem_frames
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

        def save_processor_checkpoint(frame_index, processor):
            if interactive is None or not interactive_checkpoint_frame(
                    frame_index, selected_index):
                return
            path = checkpoint_folder / f"{frame_index:08d}.pt"
            temporary = path.with_suffix(".tmp")
            torch.save({
                "curr_ti": processor.curr_ti,
                "last_mem_ti": processor.last_mem_ti,
                "object_manager": processor.object_manager,
                "memory": processor.memory,
                "last_mask": processor.last_mask,
                "last_pix_feat": processor.last_pix_feat,
                "last_msk_value": processor.last_msk_value,
            }, temporary)
            os.replace(temporary, path)
            checkpoint_files[frame_index] = path
            log_record("interactive_checkpoint", frame=frame_index,
                       bytes=path.stat().st_size)

        def restore_processor_checkpoint(frame_index):
            state = torch.load(checkpoint_files[frame_index], map_location=device,
                               mmap=True, weights_only=False)
            processor = new_processor()
            processor.curr_ti = state["curr_ti"]
            processor.last_mem_ti = state["last_mem_ti"]
            processor.object_manager = state["object_manager"]
            processor.memory = state["memory"]
            processor.memory.object_manager = processor.object_manager
            processor.last_mask = state["last_mask"]
            processor.last_pix_feat = state["last_pix_feat"]
            processor.last_msk_value = state["last_msk_value"]
            return processor

        def invalidate_checkpoints(frame_index):
            for index in [value for value in checkpoint_files if value >= frame_index]:
                checkpoint_files.pop(index).unlink(missing_ok=True)

        mask = Image.open(mask_path).convert("L").resize(
            (process_width, process_height), Image.Resampling.NEAREST)
        mask_tensor = torch.from_numpy(np.asarray(mask, dtype=np.uint8).copy()).float().to(device)
        transfer_stream = torch.cuda.Stream(device=device) if device.type == "cuda" else None
        rvm_refresh_model = None
        rvm_refresh_state = [None] * 4
        prop_refresh = None
        rvm_missing_streak = 0
        prop_missing_streak = 0
        last_rvm_refresh = -RVM_REFRESH_COOLDOWN
        last_prop_refresh = -PROP_REFRESH_COOLDOWN
        prop_recent_candidates = deque(maxlen=3)
        last_prop_evaluation = -2

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

        def update_rvm(frame_tensor):
            nonlocal rvm_refresh_state
            if rvm_refresh_model is None:
                return None
            with torch.inference_mode(), torch.amp.autocast(
                    device_type=device.type, enabled=fp16):
                _, rvm_alpha, *rvm_refresh_state = rvm_refresh_model(
                    frame_tensor.unsqueeze(0).unsqueeze(0),
                    *rvm_refresh_state, downsample_ratio=1)
            return rvm_alpha[0, 0, 0].float().clamp(0, 1)

        def predict_prop_refresh(frame_tensor, rvm_alpha):
            if prop_refresh is None:
                return None, {}
            prop_torch, prop_model, prop_device, prop_manifest, \
                predict_prop_mask = prop_refresh
            rvm_foreground = rvm_alpha > args.rvm_alpha_threshold
            prop_started = time.perf_counter()
            predicted_gpu = predict_prop_mask(
                prop_torch, prop_model, prop_device, frame_tensor,
                prop_manifest.get("confidenceThreshold", .5),
                prop_manifest.get("inputSize", 512), rvm_alpha)
            predicted, rvm_cpu = torch.stack(
                (predicted_gpu, rvm_foreground)).to(torch.uint8).cpu().numpy()
            predicted = predicted != 0
            rvm_cpu = rvm_cpu != 0
            profiler.add("prop_inference_download",
                         time.perf_counter() - prop_started)
            post_started = time.perf_counter()
            combined, components, radius, added_pixels = \
                augment_refresh_foreground(
                    predicted, rvm_cpu,
                    prop_manifest.get("proximityRadiusAt512", 24))
            retained = torch.from_numpy(combined & ~rvm_cpu).to(device)
            profiler.add("prop_component_filter_upload",
                         time.perf_counter() - post_started)
            profiler.add("prop_refresh", time.perf_counter() - prop_started)
            details = getattr(prop_model, "_prop_last_details", {})
            high_threshold = min(.95, float(prop_manifest.get(
                "confidenceThreshold", .5)) + .20)
            return retained, {
                "propModelId": prop_manifest["modelId"],
                "propComponents": len(components),
                "propRetainedComponents": sum(
                    value["retained"] for value in components),
                "propAddedPixels": added_pixels,
                "propProximityRadius": radius,
                "propHighConfidence": float(details.get(
                    "maximumContactProbability", 0)) >= high_threshold,
            }

        def step(slot, processor, first_frame=False, allow_rvm_refresh=False,
                 first_mask_tensor=None):
            nonlocal rvm_missing_streak, prop_missing_streak
            nonlocal last_rvm_refresh, last_prop_refresh
            nonlocal last_prop_evaluation
            frame_tensor = upload(slot)
            try:
                if device.type == "cuda":
                    begin, end_event = torch.cuda.Event(True), torch.cuda.Event(True)
                    begin.record()
                started = time.perf_counter()
                with torch.inference_mode(), torch.amp.autocast(
                        device_type=device.type, enabled=fp16):
                    if first_frame:
                        seed_mask = (mask_tensor if first_mask_tensor is None
                                     else first_mask_tensor)
                        output_prob = processor.step(
                            frame_tensor, seed_mask, objects=[1])
                        if first_mask_tensor is None and compile_enabled:
                            for _ in range(11):
                                output_prob = processor.step(
                                    frame_tensor, first_frame_pred=True)
                    else:
                        output_prob = processor.step(frame_tensor)
                    rvm_alpha = update_rvm(frame_tensor)
                    if rvm_alpha is not None and allow_rvm_refresh:
                        ma_alpha = processor.output_prob_to_mask(
                            output_prob).float().clamp(0, 1)
                        rvm_foreground = rvm_alpha > args.rvm_alpha_threshold
                        rvm_missing_pixels = int((rvm_foreground &
                            (ma_alpha <= .20)).sum().item())
                        rvm_pixels = int(rvm_foreground.sum().item())
                        if rvm_refresh_candidate(rvm_missing_pixels, rvm_pixels,
                                                 rvm_foreground.numel()):
                            rvm_missing_streak += 1
                        else:
                            rvm_missing_streak = 0
                        prop_foreground, prop_values = (None, {})
                        prop_missing_pixels = prop_pixels = 0
                        sparse_interval = None
                        if prop_refresh is not None and prop_refresh[3].get("architecture") == \
                                "rvm-conditioned-convnext-fpn-v2":
                            seconds = float(prop_refresh[3].get("runtime", {}).get(
                                "discoveryIntervalSeconds", 2.0))
                            sparse_interval = max(1, round(fps * seconds))
                        sparse_phase = slot.index % sparse_interval if sparse_interval else None
                        prop_evaluated = (sparse_interval is None and args.prop_every_frame) or (
                            sparse_interval is not None and sparse_phase in
                                (sparse_interval - 1, 0, 1))
                        if prop_evaluated:
                            prop_foreground, prop_values = predict_prop_refresh(
                                frame_tensor, rvm_alpha)
                            if prop_foreground is not None:
                                prop_missing_pixels = int((prop_foreground &
                                    (ma_alpha <= .20)).sum().item())
                                prop_pixels = int(prop_foreground.sum().item())
                                prop_candidate = rvm_refresh_candidate(
                                        prop_missing_pixels, prop_pixels,
                                        prop_foreground.numel(), 16)
                                prop_high_confidence = bool(prop_values.get(
                                    "propHighConfidence")) and prop_candidate
                                if sparse_interval is not None:
                                    if slot.index != last_prop_evaluation + 1:
                                        prop_recent_candidates.clear()
                                    prop_recent_candidates.append(prop_candidate)
                                    last_prop_evaluation = slot.index
                                    prop_missing_streak = 2 if prop_high_confidence or \
                                        prop_candidate and sum(prop_recent_candidates) >= 2 else 0
                                elif prop_candidate:
                                    prop_missing_streak += 1
                                else:
                                    prop_missing_streak = 0
                        rvm_trigger = args.rvm_mask_refresh and refresh_due(
                            rvm_missing_streak, slot.index, last_rvm_refresh,
                            RVM_REFRESH_PERSISTENCE, RVM_REFRESH_COOLDOWN)
                        prop_trigger = prop_evaluated and refresh_due(
                            prop_missing_streak, slot.index, last_prop_refresh,
                            2 if sparse_interval is not None else PROP_REFRESH_PERSISTENCE,
                            PROP_REFRESH_COOLDOWN)
                        if (args.debug_prop_contribution and
                                prop_foreground is not None):
                            slot.prop_detected = prop_foreground.to(
                                torch.uint8).cpu().numpy() != 0
                            if prop_trigger:
                                slot.prop_added = (prop_foreground &
                                    (ma_alpha <= .20)).to(
                                        torch.uint8).cpu().numpy() != 0
                            else:
                                slot.prop_added = np.zeros(
                                    slot.prop_detected.shape, dtype=bool)
                            slot.prop_injected = prop_trigger
                        if rvm_trigger or prop_trigger:
                            if (rvm_trigger and prop_refresh is not None and
                                    not prop_evaluated):
                                prop_foreground, prop_values = \
                                    predict_prop_refresh(frame_tensor, rvm_alpha)
                                prop_pixels = 0 if prop_foreground is None else \
                                    int(prop_foreground.sum().item())
                            injected_prop = prop_foreground if (
                                prop_trigger or rvm_trigger and
                                not prop_evaluated) else None
                            injected_rvm = rvm_foreground if rvm_trigger else \
                                torch.zeros_like(rvm_foreground)
                            corrected = corrected_rvm_refresh_mask(
                                torch, ma_alpha, injected_rvm,
                                args.rvm_refresh_strength, injected_prop)
                            output_prob = processor.step(frame_tensor,
                                corrected, objects=[1])
                            log_record("rvm_mask_refresh",
                                frame=slot.index,
                                rvmTriggered=rvm_trigger,
                                propTriggered=prop_trigger,
                                missingPixels=rvm_missing_pixels,
                                rvmPixels=rvm_pixels,
                                missingRatio=round(
                                    rvm_missing_pixels / max(rvm_pixels, 1), 6),
                                propMissingPixels=prop_missing_pixels,
                                propPixels=prop_pixels,
                                **prop_values)
                            if rvm_trigger:
                                last_rvm_refresh = slot.index
                                rvm_missing_streak = 0
                            if injected_prop is not None and prop_pixels:
                                last_prop_refresh = slot.index
                                prop_missing_streak = 0
                                prop_recent_candidates.clear()
                if device.type == "cuda":
                    end_event.record()
                    slot.gpu_timings.append((
                        "warmup" if first_frame else "processor_step", begin, end_event))
                else:
                    profiler.add("warmup" if first_frame else "processor_step",
                                 time.perf_counter() - started)
                download_alpha(slot, processor, output_prob)
                return frame_tensor
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

        def correction_mask_tensor(corrected_path):
            with Image.open(corrected_path) as corrected_image:
                corrected = corrected_image.convert("L").resize(
                    (process_width, process_height), Image.Resampling.NEAREST)
            return torch.from_numpy(np.asarray(
                corrected, dtype=np.uint8).copy()).float().to(device)

        def apply_correction_mask(slot, processor, frame_tensor, corrected_path,
                                  replace_memory=False):
            corrected_tensor = correction_mask_tensor(corrected_path)
            if slot.download_done is not None:
                slot.download_done.synchronize()
            if replace_memory:
                processor.clear_memory()
            else:
                processor.clear_non_permanent_memory()
            with torch.inference_mode(), torch.amp.autocast(
                    device_type=device.type, enabled=fp16):
                output_prob = processor.step(frame_tensor, corrected_tensor,
                                             objects=[1], force_permanent=True)
            download_alpha(slot, processor, output_prob)
            log_record("interactive_correction", frame=slot.index)

        def apply_interactive_correction(slot, processor, frame_tensor):
            nonlocal forward
            if interactive is None or not (interactive / "pause.request").is_file():
                return
            if slot.download_done is not None:
                slot.download_done.synchronize()
            paused_mask = interactive / "paused-mask.png"
            temporary = interactive / "paused-mask.tmp.png"
            Image.fromarray(slot.alpha_cpu.numpy(), "L").resize(
                (width, height), Image.Resampling.NEAREST).save(temporary, "PNG")
            os.replace(temporary, paused_mask)
            paused_frame = interactive / "paused-frame.png"
            temporary_frame = interactive / "paused-frame.tmp.png"
            Image.fromarray(slot.input_cpu.numpy(), "RGB").resize(
                (width, height), Image.Resampling.BILINEAR).save(
                    temporary_frame, "PNG")
            os.replace(temporary_frame, paused_frame)
            (interactive / "pause.request").unlink(missing_ok=True)
            frame_ms = round((start + slot.index / fps) * 1000)
            print(json.dumps({"stage": "paused", "percent": min(99,
                progress_base + completed_work * (99 - progress_base) / work_total),
                "message": f"MatAnyone 2 paused at frame {slot.index + 1}/{total}",
                "correctionFrameMs": frame_ms,
                "correctionStartMs": correction_start_ms,
                "correctionMask": str(paused_mask),
                "correctionFrame": str(paused_frame)}), flush=True)
            corrected_path = interactive / "corrected-mask.png"
            resume_path = interactive / "resume.request"
            correction_frame_path = interactive / "correction-frame-ms.txt"
            request_path = interactive / "mask-request.json"
            processing_request_path = interactive / "mask-request.processing.json"
            pending_request = None
            while not corrected_path.is_file() and not resume_path.is_file():
                if pending_request is None and request_path.is_file():
                    try:
                        os.replace(request_path, processing_request_path)
                        pending_request = json.loads(
                            processing_request_path.read_text())
                    except FileNotFoundError:
                        pass
                    finally:
                        processing_request_path.unlink(missing_ok=True)
                if pending_request is not None:
                    requested_ms = int(pending_request["frameMs"])
                    requested_index = interactive_anchor_index(requested_ms,
                        args.start_ms, fps, total, selected_index, slot.index)
                    history_index = requested_index - selected_index
                    requested_alpha = None
                    if requested_index == slot.index:
                        requested_alpha = slot.alpha_cpu.numpy()
                    elif interactive_alpha_ready[history_index]:
                        requested_alpha = np.array(
                            interactive_alpha[history_index], copy=True)
                    if requested_alpha is not None:
                        request_id = str(pending_request["id"])
                        if len(request_id) != 32 or any(
                                value not in "0123456789abcdef" for value in request_id):
                            raise ValueError("invalid interactive mask request")
                        requested_mask = interactive / f"requested-mask-{request_id}.png"
                        requested_temporary = interactive / \
                            f"requested-mask-{request_id}.tmp.png"
                        Image.fromarray(requested_alpha, "L").resize(
                            (width, height), Image.Resampling.NEAREST).save(
                                requested_temporary, "PNG")
                        os.replace(requested_temporary, requested_mask)
                        requested_frame = interactive / \
                            f"requested-frame-{request_id}.png"
                        requested_frame_temporary = interactive / \
                            f"requested-frame-{request_id}.tmp.png"
                        if requested_index == slot.index:
                            requested_rgb = Image.fromarray(
                                slot.input_cpu.numpy(), "RGB")
                        else:
                            requested_rgb = Image.open(
                                interactive_frames / f"{requested_index:08d}.jpg")
                        with requested_rgb:
                            requested_rgb.resize((width, height),
                                Image.Resampling.BILINEAR).save(
                                    requested_frame_temporary, "PNG")
                        os.replace(requested_frame_temporary, requested_frame)
                        (interactive / f"requested-mask-{request_id}.ready").write_text(
                            str(requested_ms))
                        pending_request = None
                time.sleep(.05)
            if corrected_path.is_file():
                selected_frame_ms = frame_ms
                if correction_frame_path.is_file():
                    selected_frame_ms = int(correction_frame_path.read_text().strip())
                target_index = interactive_anchor_index(selected_frame_ms,
                    args.start_ms, fps, total, selected_index, slot.index)
                anchor_path = interactive / f"correction-{selected_frame_ms}.png"
                os.replace(corrected_path, anchor_path)
                correction_anchors[target_index] = anchor_path
                invalidate_checkpoints(target_index)
                if target_index < slot.index:
                    emit("replaying", min(99, progress_base + completed_work *
                         (99 - progress_base) / work_total),
                         f"Replaying MatAnyone 2 memory through frame {slot.index + 1}...")
                    forward = None
                    processor = None
                    gc.collect()
                    if device.type == "cuda":
                        torch.cuda.empty_cache()
                    forward, replayed_alpha = replay_forward_to(
                        slot.index, target_index)
                    slot.alpha_cpu.copy_(torch.from_numpy(replayed_alpha))
                    slot.download_done = None
                    slot.alpha_gpu = None
                else:
                    apply_correction_mask(slot, processor, frame_tensor, anchor_path)
                    save_processor_checkpoint(slot.index, processor)
            corrected_path.unlink(missing_ok=True)
            resume_path.unlink(missing_ok=True)
            paused_mask.unlink(missing_ok=True)
            paused_frame.unlink(missing_ok=True)
            correction_frame_path.unlink(missing_ok=True)
            emit("inference", min(99, progress_base + completed_work *
                 (99 - progress_base) / work_total),
                 "MatAnyone 2 resumed after mask review")

        work_total = total + selected_index
        completed_work = 0
        progress_lock = threading.Lock()

        def work_done(message, stage="inference"):
            nonlocal completed_work
            with progress_lock:
                completed_work += 1
                percent = min(99, progress_base + completed_work *
                              (99 - progress_base) / work_total)
            emit(stage, percent, message)

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
            emit("backward", progress_base,
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
                work_done(
                    f"Propagated backward {selected_index - slot.index}/{selected_index} frames",
                    "backward")

            BoundedPipeline(torch, device, process_width, process_height, profiler,
                            prepare_backward, infer_backward, consume_backward,
                            args.pipeline_mode).run(backward_items())
            alpha_map.flush()
            del backward

        if args.rvm_mask_refresh or v2_sparse_discovery:
            emit("startup", progress_base,
                 "Loading RVM for persistent foreground refresh...")
            rvm_torch, rvm_refresh_model, rvm_device = load_rvm_model(
                runtime, "quality")
            if rvm_torch is not torch or rvm_device != device:
                raise RuntimeError("RVM and MatAnyone must use the same processing device")
            if args.prop_model is not None:
                emit("startup", progress_base,
                     "Loading trained prop model for RVM refreshes...")
                from prop_segmenter import load_package, predict_mask_tensor
                prop_torch, prop_model, prop_device, prop_manifest = load_package(
                    args.prop_model, device)
                if prop_torch is not torch or prop_device != device:
                    raise RuntimeError(
                        "Prop segmenter and MatAnyone must use the same processing device")
                prop_refresh = (prop_torch, prop_model, prop_device,
                                prop_manifest, predict_mask_tensor)

        decode = encode = debug_encode = None
        count = 0
        try:
            video_codec, alpha_codec, encoder_name = output_codecs(
                ffmpeg, width, height, args.force_software_encode,
                args.encoder_preset)
            decode = source_and_foreground_encoder(ffmpeg, source, output, start,
                duration, frame_rate, width, height, total, video_codec,
                process_width, process_height, "bilinear")
            if interactive is None:
                encode = alpha_encoder(ffmpeg, output, frame_rate, width, height,
                    total, process_width, process_height, alpha_codec,
                    scale="bilinear")
            if args.debug_prop_contribution:
                debug_encode = prop_contribution_encoder(ffmpeg,
                    output / "prop-contribution.mp4", frame_rate,
                    process_width, process_height)
            frame_bytes = process_width * process_height * 3

            def read_frame(pipe):
                value = bytearray(frame_bytes)
                view, received = memoryview(value), 0
                while received < frame_bytes:
                    size = pipe.readinto(view[received:])
                    if not size:
                        break
                    received += size
                if not received:
                    return None
                if received != frame_bytes:
                    raise RuntimeError("source decoder returned a partial frame")
                return np.frombuffer(value, np.uint8).reshape(
                    process_height, process_width, 3)

            def forward_items():
                for index in range(total):
                    started = time.perf_counter()
                    frame = read_frame(decode.stdout)
                    profiler.add("decode_read", time.perf_counter() - started)
                    if frame is None:
                        return
                    yield index, frame

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

            def replay_forward_to(current_index, correction_index):
                nonlocal rvm_refresh_state, rvm_missing_streak
                nonlocal prop_missing_streak, last_rvm_refresh, last_prop_refresh
                nonlocal last_prop_evaluation
                checkpoint_index = max((index for index in checkpoint_files
                                        if index < current_index), default=None)
                replay_start = selected_index if checkpoint_index is None else \
                    checkpoint_index + 1
                replay = subprocess.Popen([ffmpeg, "-v", "error", "-ss",
                    f"{start:.6f}", "-i", str(source), "-vf",
                    (f"fps={frame_rate},scale={width}:{height}:flags=lanczos,"
                     f"setsar=1,format=rgb24,scale={process_width}:"
                     f"{process_height}:flags=bilinear"), "-frames:v",
                    str(current_index + 1), "-f", "rawvideo", "-pix_fmt",
                    "rgb24", "pipe:1"], stdout=subprocess.PIPE)
                replay_processor = new_processor() if checkpoint_index is None else \
                    restore_processor_checkpoint(checkpoint_index)
                rvm_refresh_state = [None] * 4
                rvm_missing_streak = 0
                prop_missing_streak = 0
                last_rvm_refresh = -RVM_REFRESH_COOLDOWN
                last_prop_refresh = -PROP_REFRESH_COOLDOWN
                last_prop_evaluation = -2
                prop_recent_candidates.clear()
                replay_slot = FrameSlot(torch, process_height, process_width, device)
                replay_total = current_index - replay_start + 1
                replay_started = time.perf_counter()
                last_replay_progress = 0.0
                try:
                    for index in range(current_index + 1):
                        frame = read_frame(replay.stdout)
                        if frame is None:
                            raise RuntimeError("correction replay ended early")
                        if index < replay_start:
                            continue
                        replay_slot.reset()
                        replay_slot.index = index
                        replay_slot.input_cpu.copy_(torch.from_numpy(frame.copy()))
                        replay_tensor = step(replay_slot, replay_processor,
                            first_frame=index == selected_index,
                            allow_rvm_refresh=index > selected_index)
                        if index in correction_anchors:
                            apply_correction_mask(replay_slot, replay_processor,
                                replay_tensor, correction_anchors[index])
                        if replay_slot.download_done is not None:
                            replay_slot.download_done.synchronize()
                        history_index = index - selected_index
                        replayed_alpha = replay_slot.alpha_cpu.numpy()
                        interactive_alpha[history_index] = replayed_alpha
                        interactive_alpha_ready[history_index] = True
                        if raw_alpha is not None:
                            raw_alpha[index] = replayed_alpha
                        if index >= correction_index:
                            save_preview(frame, replayed_alpha,
                                         force=index == correction_index)
                        save_processor_checkpoint(index, replay_processor)
                        replay_done = index - replay_start + 1
                        now = time.perf_counter()
                        if (index == correction_index or
                                now - last_replay_progress >= .5 or
                                replay_done == replay_total):
                            replay_fps = replay_done / max(
                                now - replay_started, .001)
                            if index >= correction_index:
                                corrected_done = index - correction_index + 1
                                corrected_total = current_index - correction_index + 1
                                replay_message = (f"Replaying corrected MatAnyone 2 "
                                    f"frames {corrected_done}/{corrected_total} "
                                    f"({replay_fps:.1f} FPS)")
                            else:
                                replay_message = ("Restoring MatAnyone 2 memory "
                                    f"before correction ({replay_done}/{replay_total})")
                            emit("replaying", min(99, progress_base +
                                completed_work * (99 - progress_base) / work_total),
                                replay_message)
                            last_replay_progress = now
                    replay.stdout.close()
                    if replay.wait() != 0:
                        raise RuntimeError("correction replay decoder failed")
                    return replay_processor, np.array(
                        interactive_alpha[current_index - selected_index], copy=True)
                except BaseException:
                    replay.kill()
                    raise

            def infer_forward(slot):
                if slot.index < selected_index:
                    started = time.perf_counter()
                    slot.direct_alpha = np.array(alpha_map[slot.index], copy=True)
                    profiler.add("backward_cache_io", time.perf_counter() - started)
                    if rvm_refresh_model is not None:
                        update_rvm(upload(slot))
                elif slot.index == selected_index:
                    emit("inference", min(99, progress_base + completed_work *
                         (99 - progress_base) / work_total),
                         "Resetting MatAnyone 2 for forward propagation...")
                    frame_tensor = step(slot, forward, first_frame=True)
                    if slot.index in correction_anchors:
                        apply_correction_mask(slot, forward, frame_tensor,
                                              correction_anchors[slot.index])
                    save_processor_checkpoint(slot.index, forward)
                    apply_interactive_correction(slot, forward, frame_tensor)
                    if compile_enabled and not (runtime / COMPILE_READY).is_file():
                        (runtime / COMPILE_READY).write_text(json.dumps({
                            "detail": args.max_size,
                            "createdUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
                        }))
                else:
                    reset = slot.index in reset_anchors
                    if reset:
                        forward.clear_memory()
                    reset_mask = (correction_mask_tensor(
                        correction_anchors[slot.index]) if reset else None)
                    frame_tensor = step(slot, forward, first_frame=reset,
                        allow_rvm_refresh=not reset,
                        first_mask_tensor=reset_mask)
                    if slot.index in correction_anchors and not reset:
                        apply_correction_mask(slot, forward, frame_tensor,
                            correction_anchors[slot.index],
                            replace_memory=args.anchor_folder is not None)
                    save_processor_checkpoint(slot.index, forward)
                    apply_interactive_correction(slot, forward, frame_tensor)

            def consume_forward(slot):
                nonlocal count
                internal_alpha = (slot.direct_alpha if slot.direct_alpha is not None
                                  else slot.alpha_cpu.numpy())
                if raw_alpha is not None:
                    raw_alpha[slot.index] = internal_alpha
                if interactive_alpha is not None and slot.index >= selected_index:
                    history_index = slot.index - selected_index
                    interactive_alpha[history_index] = internal_alpha
                    Image.fromarray(slot.input_cpu.numpy(), "RGB").save(
                        interactive_frames / f"{slot.index:08d}.jpg", "JPEG",
                        quality=90)
                    interactive_alpha_ready[history_index] = True
                save_preview(slot.preview_frame, internal_alpha,
                             force=slot.index == total - 1)
                started = time.perf_counter()
                if encode is not None:
                    output_bytes = memoryview(internal_alpha).cast("B")
                    while output_bytes:
                        written = encode.stdin.write(output_bytes)
                        if not written:
                            raise RuntimeError("FFmpeg output encoder closed unexpectedly")
                        output_bytes = output_bytes[written:]
                profiler.add("encoder_write", time.perf_counter() - started)
                if debug_encode is not None:
                    detected = slot.prop_detected if slot.prop_detected is not None \
                        else np.zeros(internal_alpha.shape, dtype=bool)
                    added = slot.prop_added if slot.prop_added is not None \
                        else np.zeros(internal_alpha.shape, dtype=bool)
                    debug_frame = prop_contribution_frame(
                        slot.input_cpu.numpy(), detected, added,
                        slot.prop_injected)
                    debug_encode.stdin.write(memoryview(debug_frame).cast("B"))
                count += 1
                work_done(f"Processed {count}/{total} frames")

            BoundedPipeline(torch, device, process_width, process_height, profiler,
                            prepare_forward, infer_forward, consume_forward,
                            args.pipeline_mode).run(forward_items())
            finalize_started = time.perf_counter()
            if encode is not None:
                encode.stdin.close()
            if debug_encode is not None:
                debug_encode.stdin.close()
            decode.stdout.close()
            if decode.wait() != 0:
                raise RuntimeError("source normalization/foreground encoding failed")
            if encode is not None and encode.wait() != 0:
                raise RuntimeError("FFmpeg alpha encoding failed")
            if debug_encode is not None and debug_encode.wait() != 0:
                raise RuntimeError("FFmpeg prop contribution encoding failed")
            if interactive is not None:
                encode = alpha_encoder(ffmpeg, output, frame_rate, width, height,
                    total, process_width, process_height, alpha_codec,
                    scale="bilinear")
                for index in range(total):
                    alpha = alpha_map[index] if index < selected_index else \
                        interactive_alpha[index - selected_index]
                    output_bytes = memoryview(alpha).cast("B")
                    while output_bytes:
                        written = encode.stdin.write(output_bytes)
                        if not written:
                            raise RuntimeError(
                                "FFmpeg output encoder closed unexpectedly")
                        output_bytes = output_bytes[written:]
                encode.stdin.close()
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
            if debug_encode is not None:
                try: debug_encode.kill()
                except Exception: pass
            raise
        finally:
            if interactive_alpha is not None:
                try:
                    interactive_alpha.flush()
                    interactive_alpha._mmap.close()
                except Exception:
                    pass
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

    expected_total = total
    total = resolved_frame_count(expected_total, count)
    if total != expected_total:
        log_record("decoder_frame_count_adjustment", expectedFrames=expected_total,
                   decodedFrames=total, reason="clean EOF one frame early")
        if args.raw_alpha_output:
            with args.raw_alpha_output.open("r+b") as raw_alpha_file:
                raw_alpha_file.truncate(total * process_width * process_height)
    initial_mask_frame_ms = round((start + selected_index / fps) * 1000)
    (output / "result.json").write_text(json.dumps({"width": width, "height": height,
        "frameRate": frame_rate, "durationMs": round(count * 1000 / fps),
        "initialMaskFrameMs": initial_mask_frame_ms,
        "propContribution": "prop-contribution.mp4"
            if args.debug_prop_contribution else None}, indent=2))
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
        "rvmMaskRefresh": args.rvm_mask_refresh,
        "propMaskRefresh": (args.rvm_mask_refresh or v2_sparse_discovery) and
            args.prop_model is not None,
        "propEveryFrame": args.prop_every_frame and not v2_sparse_discovery,
        "debugPropContribution": args.debug_prop_contribution,
        "rvmRefreshStrength": args.rvm_refresh_strength,
        "maxMemFrames": args.max_mem_frames,
        "useLongTerm": args.use_long_term,
        "longTermMaxTokens": LONG_TERM_MAX_TOKENS if args.use_long_term else 0,
        "longTermBufferTokens": LONG_TERM_BUFFER_TOKENS if args.use_long_term else 0,
    }, compile_stats)
    if interactive is not None:
        shutil.rmtree(interactive, ignore_errors=True)
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
               precisionMode=args.precision_mode,
               maxMemFrames=args.max_mem_frames,
               useLongTerm=args.use_long_term,
               rvmRefreshStrength=args.rvm_refresh_strength,
               propEveryFrame=args.prop_every_frame,
               debugPropContribution=args.debug_prop_contribution,
               longTermMaxTokens=LONG_TERM_MAX_TOKENS if args.use_long_term else 0,
               longTermBufferTokens=LONG_TERM_BUFFER_TOKENS if args.use_long_term else 0)
    def clear_partial_outputs():
        for name in ("foreground.mp4", "alpha.mkv", "result.json",
                     "prop-contribution.mp4"):
            try: (args.output.resolve() / name).unlink(missing_ok=True)
            except OSError: pass

    while True:
        try:
            _process_once(args, enabled)
            return
        except RuntimeError as error:
            if not enabled or not str(error).startswith(
                    "compiled MatAnyone execution failed:"):
                raise
            enabled = False
            log_record("compile_fallback", error=str(error))
            emit("startup", 0,
                 "MatAnyone 2 optimization failed; retrying safely in eager mode...")
            clear_partial_outputs()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--mask", type=Path)
    parser.add_argument("--auto-rvm-init", action="store_true")
    parser.add_argument("--rvm-alpha-threshold", type=float, default=.40)
    parser.add_argument("--prop-model", type=Path)
    parser.add_argument("--rvm-mask-refresh", action="store_true")
    parser.add_argument("--prop-every-frame", action="store_true")
    parser.add_argument("--debug-prop-contribution", action="store_true")
    parser.add_argument("--rvm-refresh-strength", type=float, default=1)
    parser.add_argument("--max-mem-frames", type=int, default=5)
    parser.add_argument("--use-long-term", action="store_true")
    parser.add_argument("--interactive-control", type=Path)
    parser.add_argument("--anchor-folder", type=Path, help=argparse.SUPPRESS)
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
        assert interactive_anchor_index(3_000, 1_000, 25, 200, 20, 100) == 50
        assert interactive_checkpoint_frame(249, 0)
        assert interactive_checkpoint_frame(519, 20)
        assert not interactive_checkpoint_frame(518, 20)
        try:
            interactive_anchor_index(1_000, 1_000, 25, 200, 20, 100)
            raise AssertionError("correction before initial mask should be rejected")
        except ValueError:
            pass
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
        assert [seconds for seconds, _ in rvm_initializer_offsets(1200, 30)] == \
            list(range(31))
        assert rvm_initializer_offsets(75, 30) == [(0, 0), (1, 30), (2, 60)]
        assert rvm_refresh_candidate(101, 1000, 512 * 512)
        assert not rvm_refresh_candidate(100, 1000, 512 * 512)
        assert not rvm_refresh_candidate(101, 20_000, 512 * 512)
        assert refresh_due(3, 20, 5, 3, 15)
        assert not refresh_due(3, 19, 5, 3, 15)
        assert not refresh_due(2, 20, -15, 3, 15)
        assert valid_memory_configuration(2, False)
        assert valid_memory_configuration(5, False)
        assert valid_memory_configuration(6, True)
        assert valid_memory_configuration(14, True)
        assert not valid_memory_configuration(5, True)
        assert not valid_memory_configuration(15, True)
        assert not valid_memory_configuration(31, False)
        assert resolved_frame_count(5386, 5386) == 5386
        assert resolved_frame_count(5386, 5385) == 5385
        try:
            resolved_frame_count(5386, 5384)
            raise AssertionError("multi-frame truncation should be rejected")
        except RuntimeError:
            pass
        assert LONG_TERM_MAX_TOKENS > LONG_TERM_BUFFER_TOKENS + 128
        refresh_alpha = torch.zeros((12, 12))
        refresh_foreground = torch.zeros((12, 12), dtype=torch.bool)
        refresh_foreground[2:10, 2:10] = True
        refresh_mask = corrected_rvm_refresh_mask(
            torch, refresh_alpha, refresh_foreground)
        assert refresh_mask.ndim == 2 and refresh_mask.shape == (12, 12)
        assert refresh_mask[6, 6] == 255 and refresh_mask[2, 2] == 0
        gentle_refresh = corrected_rvm_refresh_mask(
            torch, refresh_alpha, refresh_foreground, .75)
        assert gentle_refresh[6, 6] == 191.25
        prop = np.zeros((64, 64), dtype=bool)
        person = np.zeros_like(prop)
        person[20:44, 20:44] = True
        prop[29:35, 44:50] = True
        prop[2:6, 2:6] = True
        augmented, components, radius, added = augment_refresh_foreground(
            prop, person, 24)
        assert augmented[30, 47] and not augmented[3, 3]
        assert sum(value["retained"] for value in components) == 1
        assert radius == 3 and added == 36
        debug_source = np.zeros((8, 8, 3), dtype=np.uint8)
        debug_detected = np.zeros((8, 8), dtype=bool)
        debug_added = np.zeros((8, 8), dtype=bool)
        debug_detected[3, 3] = True
        debug_added[4, 4] = True
        debug_frame = prop_contribution_frame(
            debug_source, debug_detected, debug_added, True)
        assert tuple(debug_frame[3, 3]) == (0, 153, 153)
        assert tuple(debug_frame[4, 4]) == (0, 191, 0)
        assert tuple(debug_frame[0, 0]) == (0, 255, 0)
        print("MatAnyone 2 worker self-test passed")
    elif not all((args.source, args.output, args.runtime)) or \
            not args.auto_rvm_init and args.mask is None:
        parser.error("--source, --output, --runtime, and either --mask or --auto-rvm-init are required")
    else:
        if not .10 <= args.rvm_alpha_threshold <= .90:
            parser.error("--rvm-alpha-threshold must be between 0.10 and 0.90")
        if not .25 <= args.rvm_refresh_strength <= 1:
            parser.error("--rvm-refresh-strength must be between 0.25 and 1.00")
        if args.prop_every_frame and (
                not args.rvm_mask_refresh or args.prop_model is None):
            parser.error("--prop-every-frame requires --rvm-mask-refresh and --prop-model")
        if args.debug_prop_contribution and not args.prop_every_frame:
            parser.error("--debug-prop-contribution requires --prop-every-frame")
        if not valid_memory_configuration(args.max_mem_frames,
                                          args.use_long_term):
            parser.error("--max-mem-frames must be 2-30, or 6-14 with --use-long-term")
        if args.device == "cpu":
            os.environ["CUDA_VISIBLE_DEVICES"] = "-1"
        process(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", 0, str(error))
        raise
