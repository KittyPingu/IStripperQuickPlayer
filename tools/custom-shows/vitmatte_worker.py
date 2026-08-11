#!/usr/bin/env python3
"""QuickPlayer ViTMatte worker. Stdout is NDJSON progress only."""
import argparse
import json
import math
import os
import queue
import statistics
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path

from rvm_worker import emit, executable, probe
from videomama_worker import (alpha_encoder, output_codecs,
                              source_and_foreground_encoder, write_preview)

MODELS = {
    "s": ("vitmatte-s", "VITMATTE_S_REVISION",
          "6a58ad7646403c1df626fbd746900aec7361ea1d"),
    "b": ("vitmatte-b", "VITMATTE_B_REVISION",
          "bf486d01a7d9e3dbcc8400f7942835caf0eaf76e"),
}
POLICY_NAME = "vitmatte-performance-policy-v1.json"
PIPELINE_DEPTH = 2


def safe_batch_for_memory(total_bytes, model, width, height, requested):
    gib = total_bytes / 1024 ** 3
    if model == "b": maximum = 1 if gib < 24 else 2 if gib < 40 else 4
    else: maximum = 2 if gib < 20 else 4 if gib < 32 else 8
    if width * height > 1920 * 1080:
        maximum = max(1, maximum // 2)
    return max(1, min(requested, maximum))
PREVIEW_INTERVAL = .5
PREVIEW_MAXIMUM = (960, 540)


class Timings:
    def __init__(self):
        self.values = {}

    def add(self, name, seconds):
        self.values.setdefault(name, []).append(float(seconds))

    def summary(self):
        result = {}
        for name, values in self.values.items():
            ordered = sorted(values)
            result[name] = {
                "count": len(values),
                "totalSeconds": sum(values),
                "meanMs": statistics.fmean(values) * 1000,
                "medianMs": statistics.median(values) * 1000,
                "p95Ms": ordered[min(len(ordered) - 1,
                    max(0, math.ceil(len(ordered) * .95) - 1))] * 1000,
            }
        return result


def trimap_from_mask(mask, cv2, kernel_cache=None):
    mask = (mask >= 128).astype("uint8")
    radius = max(3, round(max(mask.shape) / 256))
    key = (radius,)
    kernel = kernel_cache.get(key) if kernel_cache is not None else None
    if kernel is None:
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE,
                                           (radius * 2 + 1, radius * 2 + 1))
        if kernel_cache is not None:
            kernel_cache[key] = kernel
    certain = cv2.erode(mask, kernel)
    possible = cv2.dilate(mask, kernel)
    trimap = possible * 128
    trimap[certain != 0] = 255
    return trimap


def alpha_bytes(alpha):
    return (alpha * 255).byte().cpu().numpy()


def policy_identity(torch, model, revision, padded_height, padded_width, fp16, batch_size):
    if torch.cuda.is_available():
        gpu = torch.cuda.get_device_name(0)
        try:
            driver = str(torch._C._cuda_getDriverVersion())
        except Exception:
            try:
                driver = subprocess.check_output(["nvidia-smi",
                    "--query-gpu=driver_version", "--format=csv,noheader"],
                    text=True, timeout=5).splitlines()[0].strip()
            except Exception:
                driver = "unknown"
    else:
        gpu, driver = "cpu", "none"
    return "|".join(("v1", gpu, driver, torch.__version__,
        str(torch.version.cuda), revision, model, f"{padded_width}x{padded_height}",
        "fp16" if fp16 else "fp32", f"batch{batch_size}"))


def load_policy(path):
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if value.get("schemaVersion") == 1 else {"schemaVersion": 1, "entries": {}}
    except Exception:
        return {"schemaVersion": 1, "entries": {}}


def save_policy(path, policy):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(policy, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def update_policy(path, key, **values):
    try:
        policy = load_policy(path)
        entry = policy.setdefault("entries", {}).setdefault(key, {})
        entry.update(values)
        entry["updatedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        save_policy(path, policy)
    except Exception as error:
        print(json.dumps({"profile": "vitmatte-policy-warning", "message": str(error)}),
              file=sys.stderr, flush=True)


def put_bounded(target, item, stop):
    while not stop.is_set():
        try:
            target.put(item, timeout=.1)
            return True
        except queue.Full:
            pass
    return False


def read_exact(stream, size):
    value, received = bytearray(size), 0
    view = memoryview(value)
    while received < size:
        count = stream.readinto(view[received:])
        if not count:
            break
        received += count
    if received == 0:
        return None
    if received != size:
        raise RuntimeError("source decoder returned a partial frame")
    return value


def read_exact_into(stream, array):
    view, received = memoryview(array).cast("B"), 0
    while received < len(view):
        count = stream.readinto(view[received:])
        if not count:
            break
        received += count
    if received == 0:
        return False
    if received != len(view):
        raise RuntimeError("source decoder returned a partial frame")
    return True


def preview_frame(output, source, alpha, cv2):
    height, width = alpha.shape
    scale = min(1.0, PREVIEW_MAXIMUM[0] / width, PREVIEW_MAXIMUM[1] / height)
    if scale < 1:
        size = (max(1, round(width * scale)), max(1, round(height * scale)))
        source = cv2.resize(source, size, interpolation=cv2.INTER_AREA)
        alpha = cv2.resize(alpha, size, interpolation=cv2.INTER_AREA)
    write_preview(output, source, alpha)


@dataclass
class InputBatch:
    frames: list
    trimaps: list
    tensor: object


@dataclass
class OutputBatch:
    frames: list
    trimaps: list
    alpha: object
    event: object = None
    gpu_alpha: object = None
    gpu_events: object = None


def process(args):
    import cv2
    import numpy as np
    import torch
    from transformers import VitMatteForImageMatting, VitMatteImageProcessor

    started = time.perf_counter()
    timings = Timings()
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
    if device.type == "cuda":
        torch.backends.cudnn.benchmark = True
        torch.backends.cuda.matmul.allow_tf32 = True
        torch.backends.cudnn.allow_tf32 = True
        torch.set_float32_matmul_precision("high")
    emit("startup", 0, f"Loading ViTMatte-{args.model.upper()} on "
         f"{device.type.upper()}/{('FP16' if fp16 else 'FP32')}...")
    load_started = time.perf_counter()
    processor = VitMatteImageProcessor.from_pretrained(model_path, local_files_only=True)
    eager_model = VitMatteForImageMatting.from_pretrained(
        model_path, local_files_only=True).eval().to(device)
    timings.add("modelLoad", time.perf_counter() - load_started)

    output.mkdir(parents=True, exist_ok=True)
    ffmpeg = executable("ffmpeg")
    video_codec, alpha_codec, encoder_name = output_codecs(
        ffmpeg, width, height, preset=args.encoder_preset)
    decode = source_and_foreground_encoder(ffmpeg, source, output, start, duration,
        frame_rate, width, height, len(masks), video_codec)
    encode = alpha_encoder(ffmpeg, output, frame_rate, width, height, len(masks),
        width, height, alpha_codec)
    frame_bytes = width * height * 3
    requested_batch = max(1, args.batch_size)
    safe_batch = safe_batch_for_memory(
        torch.cuda.get_device_properties(device).total_memory,
        args.model, width, height, requested_batch) if device.type == "cuda" \
        else requested_batch
    active_batch = {"size": safe_batch}
    if safe_batch < requested_batch:
        emit("startup", 0, f"ViTMatte-{args.model.upper()} batch reduced from "
             f"{requested_batch} to {safe_batch} for this GPU and resolution")
    input_queue, output_queue = queue.Queue(PIPELINE_DEPTH), queue.Queue(PIPELINE_DEPTH)
    errors, stop = queue.Queue(), threading.Event()
    input_done = output_done = object()
    kernel_cache = {}
    counters = {"read": 0, "written": 0, "last_progress": 0.0, "last_preview": 0.0}
    last_result = {"source": None, "alpha": None}
    policy_path = runtime / POLICY_NAME
    # Both official ViTMatte processors are pinned to a 32-pixel divisor.
    divisor = 32
    padded_height = math.ceil(height / divisor) * divisor
    padded_width = math.ceil(width / divisor) * divisor
    initial_key = policy_identity(torch, args.model, revision,
                                  padded_height, padded_width, fp16, requested_batch)
    initial_entry = load_policy(policy_path).get("entries", {}).get(initial_key, {})
    active_batch["size"] = max(1, min(active_batch["size"],
        int(initial_entry.get("safeBatchSize", requested_batch) or requested_batch)))
    policy_key = {"value": initial_key}
    selected_mode = {"value": "eager"}
    compiled = {"model": None, "batch": None, "failed": False}
    memory_optimizations = {"configured": False, "channelsLast": False,
                            "explicitHalf": False}
    transfer_stream = torch.cuda.Stream() if device.type == "cuda" else None
    pinned_alpha_pools = {}
    source_pool = queue.SimpleQueue()

    def fail(error):
        if errors.empty():
            errors.put(error)
        stop.set()

    def input_worker():
        try:
            offset = 0
            while offset < len(masks) and not stop.is_set():
                size = min(active_batch["size"], len(masks) - offset)
                frames, trimaps = [], []
                for mask_path in masks[offset:offset + size]:
                    stage = time.perf_counter()
                    try:
                        frame = source_pool.get_nowait()
                    except queue.Empty:
                        frame = np.empty((height, width, 3), np.uint8)
                    complete = read_exact_into(decode.stdout, frame)
                    timings.add("decodeRead", time.perf_counter() - stage)
                    if not complete:
                        source_pool.put(frame)
                        break
                    stage = time.perf_counter()
                    mask = cv2.imread(str(mask_path), cv2.IMREAD_GRAYSCALE)
                    if mask is None:
                        raise RuntimeError(f"Could not read {mask_path.name}")
                    if mask.shape != (height, width):
                        mask = cv2.resize(mask, (width, height), interpolation=cv2.INTER_NEAREST)
                    trimap = trimap_from_mask(mask, cv2, kernel_cache)
                    timings.add("maskReadTrimap", time.perf_counter() - stage)
                    frames.append(frame); trimaps.append(trimap)
                if not frames:
                    break
                stage = time.perf_counter()
                # The processor already converts PIL inputs to NumPy internally; passing the
                # arrays directly removes two full-frame copies per image.
                tensor = processor(images=frames, trimaps=trimaps,
                    return_tensors="pt")["pixel_values"]
                if device.type == "cuda":
                    tensor = tensor.pin_memory()
                timings.add("preprocess", time.perf_counter() - stage)
                if not put_bounded(input_queue, InputBatch(frames, trimaps, tensor), stop):
                    return
                offset += len(frames)
                counters["read"] = offset
        except BaseException as error:
            fail(error)
        finally:
            put_bounded(input_queue, input_done, stop) if not stop.is_set() else None

    def write_all(data):
        value = memoryview(data).cast("B")
        while value:
            written = encode.stdin.write(value)
            if not written:
                raise RuntimeError("FFmpeg output encoder closed unexpectedly")
            value = value[written:]

    def output_worker():
        try:
            while not stop.is_set():
                try:
                    item = output_queue.get(timeout=.1)
                except queue.Empty:
                    continue
                if item is output_done:
                    return
                stage = time.perf_counter()
                if item.event is not None:
                    item.event.synchronize()
                if item.gpu_events is not None:
                    upload_events, inference_events, conversion_events = item.gpu_events
                    timings.add("gpuUpload",
                        upload_events[0].elapsed_time(upload_events[1]) / 1000)
                    timings.add("gpuInference",
                        inference_events[0].elapsed_time(inference_events[1]) / 1000)
                    timings.add("gpuAlphaConversion",
                        conversion_events[0].elapsed_time(conversion_events[1]) / 1000)
                alphas = item.alpha.numpy()
                timings.add("alphaDownload", time.perf_counter() - stage)
                for source_frame, trimap, alpha in zip(item.frames, item.trimaps, alphas):
                    stage = time.perf_counter()
                    alpha[trimap == 0] = 0
                    alpha[trimap == 255] = 255
                    timings.add("alphaConstraint", time.perf_counter() - stage)
                    stage = time.perf_counter()
                    write_all(alpha)
                    timings.add("encoderWrite", time.perf_counter() - stage)
                    counters["written"] += 1
                    count = counters["written"]
                    if count == len(masks):
                        last_result["source"], last_result["alpha"] = \
                            source_frame.copy(), alpha.copy()
                    now = time.monotonic()
                    if not args.no_preview and (count == 1 or
                            now - counters["last_preview"] >= PREVIEW_INTERVAL):
                        stage = time.perf_counter()
                        preview_frame(output, source_frame, alpha, cv2)
                        timings.add("preview", time.perf_counter() - stage)
                        counters["last_preview"] = now
                    if count == 1 or count == len(masks) or \
                            now - counters["last_progress"] >= .25:
                        emit("inference", min(99, count * 100 / len(masks)),
                             f"Processed {count}/{len(masks)} frames")
                        counters["last_progress"] = now
                    source_pool.put(source_frame)
                if device.type == "cuda":
                    pinned_alpha_pools.setdefault(tuple(item.alpha.shape),
                        queue.SimpleQueue()).put(item.alpha)
        except BaseException as error:
            fail(error)

    def configure_runner(tensor):
        padded_height, padded_width = tensor.shape[-2:]
        key = policy_identity(torch, args.model, revision, padded_height, padded_width,
                              fp16, requested_batch)
        policy_key["value"] = key
        entry = load_policy(policy_path).get("entries", {}).get(key, {})
        if not memory_optimizations["configured"]:
            channels_last = args.channels_last == "on" or (
                args.channels_last == "auto" and bool(entry.get("channelsLast", False)))
            explicit_half = args.explicit_half == "on" or (
                args.explicit_half == "auto" and bool(entry.get("explicitHalf", False)))
            if explicit_half and fp16:
                eager_model.half()
                memory_optimizations["explicitHalf"] = True
            if channels_last:
                eager_model.to(memory_format=torch.channels_last)
                memory_optimizations["channelsLast"] = True
            memory_optimizations["configured"] = True
        wants_compile = args.execution_mode == "compile" or (
            args.execution_mode == "auto" and len(masks) >= max(
                args.compile_cutoff_frames, int(entry.get("breakEvenFrames", 1))) and
            bool(entry.get("compiledEnabled", False)))
        if not wants_compile or not hasattr(torch, "compile") or device.type != "cuda":
            return eager_model
        cache = runtime / "torchinductor-vitmatte"
        cache.mkdir(parents=True, exist_ok=True)
        os.environ.setdefault("TORCHINDUCTOR_CACHE_DIR", str(cache))
        selected_mode["value"] = "compiled"
        emit("startup", 0, "Loading the benchmark-approved compiled ViTMatte path. "
             "The first use can build a one-time cache; later runs reuse it...")
        compiled["batch"] = active_batch["size"]
        compiled["model"] = torch.compile(eager_model,
            mode="max-autotune", fullgraph=False)
        return compiled["model"]

    runner = {"model": None}

    def run_inference(tensor, actual):
        if runner["model"] is None:
            runner["model"] = configure_runner(tensor)
        model = runner["model"]
        compile_batch = compiled["batch"] if model is compiled["model"] else None
        if compile_batch is not None and tensor.shape[0] < compile_batch:
            repeats = compile_batch - tensor.shape[0]
            tensor = torch.cat((tensor, tensor[-1:].expand(repeats, -1, -1, -1)), dim=0)
        stage = time.perf_counter()
        upload_events = inference_events = conversion_events = None
        if device.type == "cuda":
            gpu_start, gpu_end = torch.cuda.Event(True), torch.cuda.Event(True)
            gpu_start.record()
        uploaded = tensor.to(device, non_blocking=device.type == "cuda")
        if memory_optimizations["channelsLast"]:
            uploaded = uploaded.contiguous(memory_format=torch.channels_last)
        if device.type == "cuda":
            gpu_end.record()
            upload_events = (gpu_start, gpu_end)
        timings.add("uploadWall", time.perf_counter() - stage)
        stage = time.perf_counter()
        if device.type == "cuda":
            gpu_start, gpu_end = torch.cuda.Event(True), torch.cuda.Event(True)
            gpu_start.record()
        try:
            with torch.inference_mode(), torch.autocast(device_type=device.type,
                    dtype=torch.float16, enabled=fp16):
                result = model(pixel_values=uploaded).alphas[:, 0, :height, :width]
        except torch.OutOfMemoryError:
            raise
        except Exception as error:
            if model is not compiled["model"] and not (
                    memory_optimizations["channelsLast"] or
                    memory_optimizations["explicitHalf"]):
                raise
            if model is compiled["model"]:
                compiled["failed"] = True
                selected_mode["value"] = "eager-fallback"
            failed_variant = ("compiled" if model is compiled["model"] else
                "ViTMatte memory-layout optimization")
            runner["model"] = eager_model
            emit("inference", counters["written"] * 100 / len(masks),
                 f"{failed_variant} failed ({error}); continuing with the standard eager path")
            if policy_key["value"]:
                update_policy(policy_path, policy_key["value"], compiledEnabled=False,
                              channelsLast=False, explicitHalf=False,
                              optimizationFailure=str(error))
            eager_model.float().to(memory_format=torch.contiguous_format)
            memory_optimizations["channelsLast"] = False
            memory_optimizations["explicitHalf"] = False
            if device.type == "cuda":
                torch.cuda.empty_cache()
            return run_inference(tensor[:actual], actual)
        if device.type == "cuda":
            gpu_end.record()
            inference_events = (gpu_start, gpu_end)
            conversion_start, conversion_end = torch.cuda.Event(True), torch.cuda.Event(True)
            conversion_start.record()
        result = result.clamp(0, 1).mul(255).to(torch.uint8)[:actual]
        if device.type == "cuda":
            conversion_end.record()
            conversion_events = (conversion_start, conversion_end)
        else:
            timings.add("inferenceWall", time.perf_counter() - stage)
        if device.type == "cuda":
            shape = tuple(result.shape)
            pool = pinned_alpha_pools.setdefault(shape, queue.SimpleQueue())
            try:
                pinned = pool.get_nowait()
            except queue.Empty:
                pinned = torch.empty(shape, dtype=torch.uint8,
                                     device="cpu", pin_memory=True)
            event = torch.cuda.Event()
            transfer_stream.wait_stream(torch.cuda.current_stream())
            with torch.cuda.stream(transfer_stream):
                pinned.copy_(result, non_blocking=True)
                event.record(transfer_stream)
            return pinned, event, result, (upload_events, inference_events,
                                           conversion_events)
        return result.cpu(), None, None, None

    input_thread = threading.Thread(target=input_worker, name="vitmatte-input", daemon=True)
    output_thread = threading.Thread(target=output_worker, name="vitmatte-output", daemon=True)
    input_thread.start(); output_thread.start()
    try:
        while not stop.is_set():
            if not errors.empty():
                raise errors.get()
            try:
                item = input_queue.get(timeout=.1)
            except queue.Empty:
                continue
            if item is input_done:
                break
            offset = 0
            while offset < len(item.frames):
                size = min(active_batch["size"], len(item.frames) - offset)
                tensor = item.tensor[offset:offset + size]
                try:
                    pinned, event, gpu_alpha, gpu_events = run_inference(tensor, size)
                except torch.OutOfMemoryError:
                    if size == 1:
                        raise
                    active_batch["size"] = max(1, size // 2)
                    if device.type == "cuda":
                        torch.cuda.empty_cache()
                    if compiled["model"] is not None:
                        runner["model"] = eager_model
                        selected_mode["value"] = "eager-oom-fallback"
                    emit("inference", counters["written"] * 100 / len(masks),
                         f"GPU memory limit; permanently reducing this ViTMatte run to "
                         f"{active_batch['size']} frame batches")
                    if policy_key["value"]:
                        update_policy(policy_path, policy_key["value"],
                                      safeBatchSize=active_batch["size"])
                    continue
                frames = item.frames[offset:offset + size]
                trimaps = item.trimaps[offset:offset + size]
                if not put_bounded(output_queue,
                        OutputBatch(frames, trimaps, pinned, event, gpu_alpha,
                                    gpu_events), stop):
                    break
                offset += size
        if not errors.empty():
            raise errors.get()
        put_bounded(output_queue, output_done, stop)
        output_thread.join()
        if not errors.empty():
            raise errors.get()
        stop.set(); input_thread.join(timeout=5)
        if counters["written"] != len(masks):
            raise RuntimeError(
                f"source decoding ended after {counters['written']}/{len(masks)} frames")
        encode.stdin.close(); decode.stdout.close()
        if decode.wait() != 0:
            raise RuntimeError("source normalization/foreground encoding failed")
        if encode.wait() != 0:
            raise RuntimeError("FFmpeg alpha encoding failed")
        if not args.no_preview and last_result["source"] is not None:
            preview_frame(output, last_result["source"], last_result["alpha"], cv2)
        (output / "result.json").write_text(json.dumps({"width": width,
            "height": height, "frameRate": frame_rate,
            "durationMs": round(counters["written"] * 1000 / fps),
            "requestedSequenceChunk": requested_batch,
            "effectiveSequenceChunk": active_batch["size"],
            "executionMode": selected_mode["value"]}, indent=2))
        total = time.perf_counter() - started
        peak_vram = torch.cuda.max_memory_allocated() if device.type == "cuda" else 0
        try:
            import psutil
            memory = psutil.Process().memory_info()
            peak_ram = int(getattr(memory, "peak_wset", memory.rss))
        except Exception:
            peak_ram = 0
        profile = {"profile": "vitmatte-v1", "model": args.model,
            "executionMode": selected_mode["value"], "frames": counters["written"],
            "width": width, "height": height, "batchSizeRequested": requested_batch,
            "batchSizeUsed": active_batch["size"], "totalSeconds": total,
            "fps": counters["written"] / total if total else 0,
            "peakVramBytes": peak_vram, "peakRamBytes": peak_ram,
            "temporaryDiskBytes": 0, "timings": timings.summary(),
            "policyKey": policy_key["value"],
            "encoder": encoder_name,
            "encoderPreset": args.encoder_preset if encoder_name == "h264_nvenc" else "slow/medium",
            "channelsLast": memory_optimizations["channelsLast"],
            "explicitHalf": memory_optimizations["explicitHalf"]}
        print(json.dumps(profile), file=sys.stderr, flush=True)
        if args.profile_output:
            args.profile_output.parent.mkdir(parents=True, exist_ok=True)
            temporary = args.profile_output.with_suffix(args.profile_output.suffix + ".tmp")
            temporary.write_text(json.dumps(profile, indent=2), encoding="utf-8")
            os.replace(temporary, args.profile_output)
        emit("complete", 100,
             f"ViTMatte-{args.model.upper()} foreground and alpha are ready for preview")
    except BaseException:
        stop.set()
        try: input_queue.put_nowait(input_done)
        except queue.Full: pass
        try: output_queue.put_nowait(output_done)
        except queue.Full: pass
        if decode.poll() is None: decode.kill()
        if encode.poll() is None: encode.kill()
        input_thread.join(timeout=3); output_thread.join(timeout=3)
        raise


def self_test():
    import cv2
    import numpy as np
    import torch
    mask = np.zeros((64, 64), np.uint8); mask[16:48, 16:48] = 255
    cache = {}
    trimap = trimap_from_mask(mask, cv2, cache)
    assert trimap[32, 32] == 255 and trimap[0, 0] == 0 and 128 in trimap
    assert len(cache) == 1
    assert safe_batch_for_memory(16 * 1024 ** 3, "b", 1920, 1080, 12) == 1
    assert safe_batch_for_memory(16 * 1024 ** 3, "s", 1920, 1080, 12) == 2
    assert safe_batch_for_memory(24 * 1024 ** 3, "b", 3840, 2160, 12) == 1
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
    parser.add_argument("--encoder-preset", choices=tuple(f"p{i}" for i in range(1, 8)),
                        default="p5")
    parser.add_argument("--compile-cutoff-frames", type=int, default=16000)
    parser.add_argument("--execution-mode", choices=("auto", "eager", "compile"),
                        default="auto")
    parser.add_argument("--profile", action="store_true")
    parser.add_argument("--profile-output", type=Path)
    parser.add_argument("--no-preview", action="store_true")
    parser.add_argument("--channels-last", choices=("auto", "on", "off"), default="auto")
    parser.add_argument("--explicit-half", choices=("auto", "on", "off"), default="auto")
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
