#!/usr/bin/env python3
"""Bounded streaming OmniShotCut inference for QuickPlayer.

The upstream convenience API decodes a complete video and repeatedly allocates
and stacks 100 individual frames. This worker preserves the official window,
overlap, normalization, and merge semantics while decoding into reusable pinned
slots and overlapping decode/preprocessing and asynchronous CUDA upload with
the preceding model invocation.
"""

from __future__ import annotations

import argparse
import base64
from collections import deque
from fractions import Fraction
import gzip
import json
import os
from pathlib import Path
import queue
import signal
import subprocess
import sys
import threading
import time
from types import SimpleNamespace

import numpy as np
import torch


INTRA_LABELS = {
    0: "General", 1: "Dissolve", 2: "Wipes", 3: "Push", 4: "Slide",
    5: "Zoom", 6: "Fade", 7: "Doorway", 8: "Padding",
}
INTER_LABELS = {
    0: "New_Start", 1: "Hard_Cut", 2: "Transition_Source",
    3: "Transition", 4: "Sudden_Jump", 5: "Padding",
}
OVERLAP_FRAMES = 20
MODEL_REVISION = "23ad6fb41b296fb9258b0e7825125a914573b906"
NORMALIZE_MEAN = (0.485, 0.456, 0.406)
NORMALIZE_STD = (0.229, 0.224, 0.225)


def emit(**values):
    print(json.dumps(values, separators=(",", ":")), flush=True)


def append_profile(path: str | None, values: dict):
    if not path:
        return
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("a", encoding="utf-8") as stream:
        stream.write(json.dumps(values, separators=(",", ":")) + "\n")


def parse_rate(value: str) -> Fraction:
    try:
        rate = Fraction(value)
        if rate > 0:
            return rate
    except (ValueError, ZeroDivisionError):
        pass
    return Fraction(25, 1)


def source_info(source: str, ffprobe: str) -> dict:
    output = subprocess.check_output(
        [ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries",
         "stream=avg_frame_rate,r_frame_rate,nb_frames,codec_name,width,height,pix_fmt:format=duration",
         "-of", "json", source], text=True, stderr=subprocess.STDOUT)
    data = json.loads(output)
    streams = data.get("streams") or []
    if not streams:
        raise RuntimeError("The source has no video stream.")
    stream = streams[0]
    average = parse_rate(stream.get("avg_frame_rate") or
                         stream.get("r_frame_rate") or "25/1")
    nominal = parse_rate(stream.get("r_frame_rate") or str(average))
    duration = float((data.get("format") or {}).get("duration") or 0)
    frames = int(stream.get("nb_frames") or 0)
    if frames <= 0 and duration > 0:
        frames = max(1, round(duration * float(average)))
    return {
        "rate": average,
        "nominal_rate": nominal,
        "duration": duration,
        "expected_frames": frames,
        "vfr_approximation": average != nominal,
        "codec": str(stream.get("codec_name") or "unknown").lower(),
        "width": int(stream.get("width") or 0),
        "height": int(stream.get("height") or 0),
        "pix_fmt": stream.get("pix_fmt") or "unknown",
    }


def milliseconds(frame: int, rate: Fraction) -> int:
    numerator = frame * rate.denominator * 1000
    return (numerator + rate.numerator // 2) // rate.numerator


def merge_predictions(full: list[dict], current: list[dict], tolerance: int = 2):
    for item in sorted(current, key=lambda value: value["end_frame"]):
        if full and abs(item["end_frame"] - full[-1]["end_frame"]) <= tolerance:
            continue
        full.append(item)


def select_by_sensitivity(boundaries: list[dict], sensitivity_percent: int):
    """Keep the strongest requested percentage; model confidences saturate near 1."""
    if not boundaries:
        return []
    sensitivity_percent = max(1, min(100, sensitivity_percent))
    count = max(1, min(len(boundaries),
                       int(np.ceil(len(boundaries) * sensitivity_percent / 100))))
    selected = {value[1]["end_frame"] for value in sorted(
        enumerate(boundaries), key=lambda value: (-value[1].get("confidence", 1),
                                                  value[0]))[:count]}
    return [value for value in boundaries if value["end_frame"] in selected]


def load_model(runtime: Path):
    repository = runtime / "omnishotcut"
    checkpoint = runtime / "checkpoints" / "OmniShotCut_ckpt.pth"
    marker = runtime / "OMNISHOTCUT_COMMIT"
    if not repository.is_dir() or not checkpoint.is_file():
        raise RuntimeError("OmniShotCut is not installed. Run Install / Update Processing Tools.")
    if not marker.is_file() or marker.read_text(encoding="utf-8").strip() != MODEL_REVISION:
        raise RuntimeError("The installed OmniShotCut revision is not supported. Run setup again.")
    if not torch.cuda.is_available():
        raise RuntimeError("OmniShotCut currently requires an NVIDIA CUDA GPU.")
    sys.path.insert(0, str(repository))
    from omnishotcut.architecture.backbone import build_backbone
    from omnishotcut.architecture.transformer import build_transformer
    from omnishotcut.architecture.model import OmniShotCut
    from omnishotcut.datasets.transforms import Video_Augmentation_Transform

    state = torch.load(checkpoint, map_location="cpu", weights_only=False)
    if "args" not in state or "model" not in state:
        raise RuntimeError("The OmniShotCut checkpoint is invalid.")
    model_args = state["args"]
    model = OmniShotCut(
        build_backbone(model_args), build_transformer(model_args),
        num_intra_relation_classes=model_args.num_intra_relation_classes,
        num_inter_relation_classes=model_args.num_inter_relation_classes,
        num_frames=model_args.max_process_window_length,
        num_queries=model_args.num_queries, aux_loss=model_args.aux_loss)
    model.load_state_dict(state["model"], strict=True)
    model.to("cuda").eval()
    return model, model_args, Video_Augmentation_Transform(set_type="val")


def stderr_reader(stream, lines: deque[str]):
    try:
        for line in iter(stream.readline, b""):
            lines.append(line.decode("utf-8", errors="replace").rstrip())
    finally:
        stream.close()


def resolve_decode_mode(requested: str, info: dict) -> str:
    if requested != "auto":
        return requested
    # OmniShotCut's 128x96 input changes the trade-off from TransNetV2. Our
    # 1080p/4K equivalence sweep found CPU faster for H.264 and VP9, tied for
    # HEVC, and NVDEC materially faster only for 4K AV1. Unsupported hardware
    # paths retry with the compatible CPU scaler.
    source_pixels = int(info.get("width") or 0) * int(info.get("height") or 0)
    return "legacy" if info["codec"] == "av1" and source_pixels >= 7_000_000 else "cpu"


def decode_command(ffmpeg: str, source: str, mode: str, width: int, height: int,
                   start_ms: int = 0, end_ms: int | None = None):
    common = [ffmpeg, "-hide_banner", "-loglevel", "error"]
    if start_ms > 0:
        common += ["-ss", f"{start_ms / 1000:.6f}"]
    duration_ms = None if end_ms is None else max(1, end_ms - start_ms)
    bounded = [] if duration_ms is None else ["-t", f"{duration_ms / 1000:.6f}"]
    if mode == "legacy":
        return common + ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda",
            "-i", source] + bounded + ["-map", "0:v:0", "-an", "-sn", "-dn", "-vf",
            f"scale_cuda={width}:{height},hwdownload,format=nv12,format=rgb24",
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]
    scaler = (f"scale={width}:{height}:flags=fast_bilinear" if mode == "cpu-fast"
              else f"scale={width}:{height}")
    return common + ["-i", source] + bounded + ["-map", "0:v:0", "-an", "-sn", "-dn",
        "-vf", scaler, "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]


class DecodeFailure(RuntimeError):
    pass


def read_exact_into(stream, target: np.ndarray) -> bool:
    view = memoryview(target).cast("B")
    offset = 0
    while offset < len(view):
        count = stream.readinto(view[offset:])
        if not count:
            if offset:
                raise DecodeFailure("FFmpeg returned an incomplete RGB frame.")
            return False
        offset += count
    return True


def normalize_into(frames: torch.Tensor, target: torch.Tensor,
                   mean: torch.Tensor, std: torch.Tensor):
    """Reproduce the upstream validation transform without per-frame tensors."""
    target[0].copy_(frames.permute(0, 3, 1, 2))
    target.div_(255.0).sub_(mean).div_(std)


class WindowProducer:
    """Decode into three reusable pinned slots and prepare official model input."""

    def __init__(self, command, chunk_size: int, width: int, height: int,
                 expected_frames: int, stop: threading.Event, slot_count: int):
        self.command, self.chunk_size = command, chunk_size
        self.width, self.height = width, height
        self.expected_frames, self.stop = expected_frames, stop
        self.full, self.free = queue.Queue(max(2, slot_count - 1)), queue.Queue()
        self.error = None
        self.process = None
        self.decoded_frames = 0
        self.producer_seconds = self.read_seconds = 0.0
        self.overlap_copy_seconds = self.preprocess_seconds = 0.0
        self.queue_wait_seconds = 0.0
        self.pinned_bytes = 0
        mean = torch.tensor(NORMALIZE_MEAN, dtype=torch.float32).view(1, 1, 3, 1, 1)
        std = torch.tensor(NORMALIZE_STD, dtype=torch.float32).view(1, 1, 3, 1, 1)
        self.mean, self.std = mean, std
        for index in range(slot_count):
            frames = torch.empty((chunk_size + 1, height, width, 3),
                                 dtype=torch.uint8, pin_memory=True)
            inputs = torch.empty((1, chunk_size, 3, height, width),
                                 dtype=torch.float32, pin_memory=True)
            self.pinned_bytes += frames.numel() * frames.element_size()
            self.pinned_bytes += inputs.numel() * inputs.element_size()
            self.free.put(SimpleNamespace(index=index, frames=frames, inputs=inputs,
                gpu=None, valid_len=0, window_start=0, valid_start=0, valid_end=0,
                final=False))
        self.thread = threading.Thread(target=self._run, name="OmniShotCut decode",
                                       daemon=True)

    def start(self):
        self.thread.start()

    def cancel(self):
        self.stop.set()
        try:
            if self.process and self.process.poll() is None:
                self.process.terminate()
        except Exception:
            pass

    def _wait_queue(self, operation):
        started = time.perf_counter()
        while not self.stop.is_set():
            try:
                result = operation()
                self.queue_wait_seconds += time.perf_counter() - started
                return result
            except (queue.Empty, queue.Full):
                pass
        raise InterruptedError("OmniShotCut detection cancelled")

    def _get_free(self):
        return self._wait_queue(lambda: self.free.get(timeout=.1))

    def _put_full(self, value):
        self._wait_queue(lambda: self.full.put(value, timeout=.1))

    def _prepare(self, slot):
        started = time.perf_counter()
        normalize_into(slot.frames[:self.chunk_size], slot.inputs,
                       self.mean, self.std)
        self.preprocess_seconds += time.perf_counter() - started

    def _run(self):
        errors: deque[str] = deque(maxlen=80)
        error_thread = None
        try:
            started = time.perf_counter()
            self.process = subprocess.Popen(self.command, stdout=subprocess.PIPE,
                stderr=subprocess.PIPE, bufsize=0,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            error_thread = threading.Thread(target=stderr_reader,
                args=(self.process.stderr, errors), daemon=True)
            error_thread.start()
            self._produce(self.process.stdout)
            self.process.stdout.close()
            exit_code = self.process.wait()
            error_thread.join(timeout=2)
            self.producer_seconds = time.perf_counter() - started
            if exit_code:
                raise DecodeFailure("FFmpeg decode failed: " + "\n".join(errors))
        except BaseException as error:
            self.error = error
        finally:
            if self.stop.is_set():
                try:
                    self.full.put_nowait(None)
                except queue.Full:
                    pass
            else:
                try:
                    self._put_full(None)
                except InterruptedError:
                    pass

    def _produce(self, stream):
        stride = self.chunk_size - OVERLAP_FRAMES
        carry_frames = self.chunk_size + 1 - stride
        slot = self._get_free()
        fill = 0
        window_start = 0
        eof = False
        while not self.stop.is_set():
            while fill < self.chunk_size + 1:
                started = time.perf_counter()
                present = read_exact_into(stream, slot.frames[fill].numpy())
                self.read_seconds += time.perf_counter() - started
                if not present:
                    eof = True
                    break
                fill += 1
                self.decoded_frames += 1
            if fill == 0:
                self.free.put(slot)
                break

            final_window = eof and fill <= self.chunk_size
            valid_len = min(self.chunk_size, fill)
            if valid_len < self.chunk_size:
                slot.frames[valid_len:self.chunk_size].zero_()
            slot.valid_len = valid_len
            slot.window_start = window_start
            slot.valid_start = (window_start if window_start == 0 else
                                window_start + OVERLAP_FRAMES // 2)
            slot.valid_end = (window_start + valid_len if final_window else
                              window_start + self.chunk_size -
                              (OVERLAP_FRAMES - OVERLAP_FRAMES // 2))
            slot.final = final_window

            if not final_window:
                next_slot = self._get_free()
                copied = time.perf_counter()
                next_slot.frames[:carry_frames].copy_(
                    slot.frames[stride:self.chunk_size + 1])
                self.overlap_copy_seconds += time.perf_counter() - copied
                next_fill = carry_frames
            self._prepare(slot)
            self._put_full(slot)
            if final_window:
                break
            slot, fill = next_slot, next_fill
            window_start += stride
        if self.stop.is_set():
            raise InterruptedError("OmniShotCut detection cancelled")


def predictions_for(outputs):
    intra_probs = outputs["intra_clip_logits"].softmax(-1)[:, :, :-1]
    inter_probs = outputs["inter_clip_logits"].softmax(-1)[:, :, :-1]
    range_probs = outputs["pred_shot_logits"].softmax(-1)[:, :, :-1]
    intra_conf, intra = intra_probs.max(-1)
    inter_conf, inter = inter_probs.max(-1)
    range_conf, ranges = range_probs.max(-1)
    return torch.stack((intra, inter, ranges, intra_conf, inter_conf,
                        range_conf), dim=2)


def collect_predictions(boundaries: list[dict], predictions: np.ndarray,
                        window_start: int, valid_start: int, valid_end: int,
                        valid_len: int, sensitivity_percent: int = 100):
    current: list[dict] = []
    local_start = 0
    minimum_confidence = 1 - max(1, min(100, sensitivity_percent)) / 100
    for (intra_index, inter_index, end_index, intra_confidence,
         inter_confidence, range_confidence) in predictions:
        intra_index = int(intra_index)
        inter_index = int(inter_index)
        local_end = min(int(end_index), valid_len)
        if local_start >= local_end:
            continue
        global_end = window_start + local_end
        if valid_start < global_end <= valid_end:
            if intra_index not in INTRA_LABELS or inter_index not in INTER_LABELS:
                raise RuntimeError("OmniShotCut returned an unknown classification label.")
            class_confidence = (float(intra_confidence)
                                if intra_index not in (0, 8)
                                else float(inter_confidence))
            confidence = min(class_confidence, float(range_confidence))
            if confidence < minimum_confidence:
                local_start = local_end
                if local_end >= valid_len:
                    break
                continue
            current.append({"end_frame": global_end,
                            "intra": INTRA_LABELS[intra_index],
                            "inter": INTER_LABELS[inter_index],
                            "confidence": confidence,
                            "range_confidence": float(range_confidence)})
        local_start = local_end
        if local_end >= valid_len:
            break
    merge_predictions(boundaries, current)


def progress(info: dict, covered: int, last_percent: int):
    expected = max(info["expected_frames"], covered, 1)
    percent = min(99, max(last_percent, round(covered * 99 / expected)))
    if percent > last_percent:
        emit(stage="detecting", percent=percent, processedFrames=covered,
             totalFrames=expected, message=f"Analysed {covered}/{expected} frames")
    return percent


def run_bounded(model, command, info: dict, chunk_size: int,
                width: int, height: int, requested_batch: int,
                sensitivity_percent: int):
    stop = threading.Event()
    requested_batch = max(1, min(16, requested_batch))
    effective_batch = requested_batch
    slot_count = requested_batch * 2 + 1
    producer = WindowProducer(command, chunk_size, width, height,
                              info["expected_frames"], stop, slot_count)
    previous_handlers = {}

    def cancel(_signum, _frame):
        producer.cancel()

    for value in (getattr(signal, "SIGINT", None), getattr(signal, "SIGTERM", None)):
        if value is not None:
            try:
                previous_handlers[value] = signal.signal(value, cancel)
            except Exception:
                pass

    transfer_stream = torch.cuda.Stream()
    boundaries: list[dict] = []
    transfer_ms = inference_ms = 0.0
    result_transfer_seconds = 0.0
    last_percent = 1
    window_count = 0
    producer.start()
    gpu_inputs = [torch.empty((requested_batch, chunk_size, 3, height, width),
                              dtype=torch.float32, device="cuda") for _ in range(2)]

    def schedule(group, gpu_input):
        begin, end = torch.cuda.Event(True), torch.cuda.Event(True)
        with torch.cuda.stream(transfer_stream):
            begin.record(transfer_stream)
            for index, slot in enumerate(group):
                gpu_input[index:index + 1].copy_(slot.inputs, non_blocking=True)
            end.record(transfer_stream)
        return group, gpu_input, end, begin, end

    def infer(item):
        nonlocal transfer_ms, inference_ms, result_transfer_seconds
        nonlocal last_percent, window_count, effective_batch
        group, gpu_input, ready, transfer_start, transfer_end = item
        count = len(group)
        torch.cuda.current_stream().wait_event(ready)
        inference_start, inference_end = torch.cuda.Event(True), torch.cuda.Event(True)
        inference_start.record()
        try:
            with torch.inference_mode():
                values = predictions_for(model(gpu_input[:count]))
            inference_end.record()
            result_started = time.perf_counter()
            predictions = values.cpu().numpy()
            torch.cuda.synchronize()
        except torch.cuda.OutOfMemoryError:
            if count == 1:
                raise
            torch.cuda.empty_cache()
            effective_batch = max(1, min(effective_batch, count // 2))
            emit(stage="detecting", percent=max(2, last_percent),
                 message=f"Window batch did not fit GPU memory; continuing with batch {effective_batch}")
            for start in range(0, count, effective_batch):
                subset = group[start:start + effective_batch]
                infer((subset, gpu_input[start:start + len(subset)], ready,
                       None, None))
            return
        result_transfer_seconds += time.perf_counter() - result_started
        if transfer_start is not None:
            transfer_ms += transfer_start.elapsed_time(transfer_end)
        inference_ms += inference_start.elapsed_time(inference_end)
        for slot, prediction in zip(group, predictions):
            collect_predictions(boundaries, prediction, slot.window_start,
                                slot.valid_start, slot.valid_end, slot.valid_len,
                                sensitivity_percent)
            window_count += 1
            last_percent = progress(info, min(slot.valid_end,
                max(info["expected_frames"], producer.decoded_frames)), last_percent)
            producer.free.put(slot)

    try:
        finished = False
        pending = None
        next_gpu = 0
        while not finished:
            group = []
            while len(group) < effective_batch:
                slot = producer.full.get()
                if slot is None:
                    finished = True
                    break
                if stop.is_set():
                    raise InterruptedError("OmniShotCut detection cancelled")
                group.append(slot)
            if group:
                item = schedule(group, gpu_inputs[next_gpu])
                next_gpu = 1 - next_gpu
                if pending is not None:
                    infer(pending)
                pending = item
        if pending is not None:
            infer(pending)
        producer.thread.join()
        if producer.error:
            raise producer.error
        return boundaries, producer.decoded_frames, {
            "windows": window_count,
            "producerSeconds": producer.producer_seconds,
            "decodeReadSeconds": producer.read_seconds,
            "overlapCopySeconds": producer.overlap_copy_seconds,
            "preprocessSeconds": producer.preprocess_seconds,
            "queueWaitSeconds": producer.queue_wait_seconds,
            "transferSeconds": transfer_ms / 1000,
            "inferenceSeconds": inference_ms / 1000,
            "resultTransferSeconds": result_transfer_seconds,
            "pinnedBytes": producer.pinned_bytes,
            "pipelineDepth": slot_count,
            "requestedWindowBatch": requested_batch,
            "effectiveWindowBatch": effective_batch,
        }
    finally:
        producer.cancel()
        producer.thread.join(timeout=5)
        for value, handler in previous_handlers.items():
            try:
                signal.signal(value, handler)
            except Exception:
                pass


def read_frame(stream, byte_count: int):
    data = bytearray(byte_count)
    view = memoryview(data)
    offset = 0
    while offset < byte_count:
        count = stream.readinto(view[offset:])
        if not count:
            if offset:
                raise DecodeFailure("FFmpeg returned an incomplete RGB frame.")
            return None
        offset += count
    return np.frombuffer(data, dtype=np.uint8)


def run_serial(model, transform, command, info: dict, chunk_size: int,
               width: int, height: int, sensitivity_percent: int):
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    errors: deque[str] = deque(maxlen=80)
    error_thread = threading.Thread(target=stderr_reader,
                                    args=(process.stderr, errors), daemon=True)
    error_thread.start()
    boundaries: list[dict] = []
    buffer: list[np.ndarray] = []
    window_start = total_frames = window_count = 0
    decode_seconds = preprocess_seconds = inference_seconds = result_seconds = 0.0
    last_percent = 1
    eof = False
    stride = chunk_size - OVERLAP_FRAMES
    try:
        while True:
            decode_started = time.perf_counter()
            while len(buffer) < chunk_size + 1 and not eof:
                frame = read_frame(process.stdout, width * height * 3)
                if frame is None:
                    eof = True
                    break
                buffer.append(frame)
                total_frames += 1
            decode_seconds += time.perf_counter() - decode_started
            if not buffer:
                break
            final = eof and len(buffer) <= chunk_size
            valid_len = min(chunk_size, len(buffer))
            valid_start = window_start if window_start == 0 else window_start + OVERLAP_FRAMES // 2
            valid_end = (window_start + valid_len if final else
                         window_start + chunk_size - OVERLAP_FRAMES // 2)
            preprocess_started = time.perf_counter()
            video = np.stack(buffer[:valid_len]).reshape(valid_len, height, width, 3)
            if valid_len < chunk_size:
                video = np.concatenate((video, np.zeros(
                    (chunk_size - valid_len, height, width, 3), dtype=np.uint8)), axis=0)
            tensor = transform(video).unsqueeze(0)
            preprocess_seconds += time.perf_counter() - preprocess_started
            inference_started = time.perf_counter()
            with torch.inference_mode():
                outputs = model(tensor.to("cuda"))
                values = predictions_for(outputs)[0]
            transfer_started = time.perf_counter()
            predictions = values.cpu().numpy()
            torch.cuda.synchronize()
            result_seconds += time.perf_counter() - transfer_started
            inference_seconds += time.perf_counter() - inference_started
            collect_predictions(boundaries, predictions, window_start,
                                valid_start, valid_end, valid_len,
                                sensitivity_percent)
            window_count += 1
            last_percent = progress(info, min(valid_end,
                max(info["expected_frames"], total_frames)), last_percent)
            if final:
                break
            buffer = buffer[stride:]
            window_start += stride
        process.stdout.close()
        exit_code = process.wait()
        error_thread.join(timeout=2)
        if exit_code:
            raise DecodeFailure("FFmpeg decode failed: " + "\n".join(errors))
        return boundaries, total_frames, {
            "windows": window_count,
            "producerSeconds": decode_seconds,
            "decodeReadSeconds": decode_seconds,
            "overlapCopySeconds": 0.0,
            "preprocessSeconds": preprocess_seconds,
            "queueWaitSeconds": 0.0,
            "transferSeconds": 0.0,
            "inferenceSeconds": inference_seconds,
            "resultTransferSeconds": result_seconds,
            "pinnedBytes": 0,
            "pipelineDepth": 1,
            "requestedWindowBatch": 1,
            "effectiveWindowBatch": 1,
        }
    finally:
        if process.poll() is None:
            process.kill()
        try:
            process.stdout.close()
        except Exception:
            pass


def memory_profile():
    result = {
        "peakVramBytes": int(torch.cuda.max_memory_allocated()),
        "peakVramReservedBytes": int(torch.cuda.max_memory_reserved()),
    }
    try:
        import psutil
        memory = psutil.Process().memory_info()
        result["workingSetBytes"] = int(memory.rss)
        if getattr(memory, "peak_wset", None) is not None:
            result["peakWorkingSetBytes"] = int(memory.peak_wset)
    except Exception:
        pass
    return result


def ranges_from_boundaries(boundaries: list[dict], total_frames: int,
                           rate: Fraction):
    ranges = []
    start_frame = 0
    for boundary in boundaries:
        end_frame = min(int(boundary["end_frame"]), total_frames)
        if end_frame <= start_frame:
            continue
        ranges.append({
            "startFrame": start_frame, "endFrame": end_frame,
            "startMs": milliseconds(start_frame, rate),
            "endMs": milliseconds(end_frame, rate),
            "intraLabel": boundary["intra"], "interLabel": boundary["inter"],
        })
        start_frame = end_frame
    if start_frame < total_frames:
        print("Warning: model output did not cover the final decoded frames; retaining them as General.",
              file=sys.stderr, flush=True)
        ranges.append({
            "startFrame": start_frame, "endFrame": total_frames,
            "startMs": milliseconds(start_frame, rate),
            "endMs": milliseconds(total_frames, rate),
            "intraLabel": "General",
            "interLabel": "New_Start" if not ranges else "Transition_Source",
        })
    return ranges


def run(args):
    started = time.perf_counter()
    runtime = Path(args.runtime).resolve()
    ffmpeg = os.environ.get("IQP_FFMPEG", "ffmpeg")
    ffprobe = os.environ.get("IQP_FFPROBE", "ffprobe")
    info = source_info(args.source, ffprobe)
    args.start_ms = max(0, args.start_ms)
    if args.end_ms is not None and args.end_ms > args.start_ms:
        bounded_duration = min(info["duration"] - args.start_ms / 1000,
                               (args.end_ms - args.start_ms) / 1000)
    else:
        args.end_ms = None
        bounded_duration = max(0, info["duration"] - args.start_ms / 1000)
    info["duration"] = bounded_duration
    info["expected_frames"] = max(1, round(bounded_duration * float(info["rate"])))
    if info["vfr_approximation"]:
        print("Warning: variable-frame-rate timestamps are approximated using avg_frame_rate.",
              file=sys.stderr, flush=True)

    emit(stage="loading", percent=1, message="Loading OmniShotCut on CUDA...")
    load_started = time.perf_counter()
    model, model_args, transform = load_model(runtime)
    model_seconds = time.perf_counter() - load_started
    width = int(model_args.process_width)
    height = int(model_args.process_height)
    chunk_size = int(model_args.max_process_window_length)
    if chunk_size <= OVERLAP_FRAMES:
        raise RuntimeError("The OmniShotCut checkpoint has an invalid window length.")

    requested_decode = args.decode_mode
    decode_mode = resolve_decode_mode(requested_decode, info)
    fallback = False

    def execute(mode):
        command = decode_command(ffmpeg, args.source, mode, width, height,
                                 args.start_ms, args.end_ms)
        if args.pipeline_mode == "serial":
            return run_serial(model, transform, command, info, chunk_size, width,
                              height, 100)
        return run_bounded(model, command, info, chunk_size, width, height,
                           args.window_batch, 100)

    try:
        boundaries, total_frames, metrics = execute(decode_mode)
    except DecodeFailure:
        if requested_decode != "auto" or decode_mode == "cpu":
            raise
        fallback = True
        print(f"Warning: {decode_mode} decode failed; retrying with compatible CPU decode.",
              file=sys.stderr, flush=True)
        emit(stage="detecting", percent=2,
             message="CUDA decode unavailable; retrying on CPU...")
        decode_mode = "cpu"
        boundaries, total_frames, metrics = execute(decode_mode)

    if total_frames == 0:
        raise RuntimeError("FFmpeg decoded no video frames.")
    retained_boundaries = list(boundaries)
    boundaries = select_by_sensitivity(retained_boundaries,
                                       args.sensitivity_percent)
    ranges = ranges_from_boundaries(boundaries, total_frames, info["rate"])
    retained = {"totalFrames": total_frames, "boundaries": [{
        "endFrame": int(value["end_frame"]), "intra": value["intra"],
        "inter": value["inter"], "confidence": float(value.get("confidence", 1))}
        for value in retained_boundaries]}
    sensitivity_data = base64.b64encode(gzip.compress(json.dumps(
        retained, separators=(",", ":")).encode("utf-8"), compresslevel=9)).decode("ascii")
    total_seconds = time.perf_counter() - started
    profile = {
        "kind": "omnishotcut-profile",
        "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "source": str(Path(args.source).resolve()),
        "revision": MODEL_REVISION,
        "device": torch.cuda.get_device_name(0),
        "codec": info["codec"],
        "sourceWidth": info["width"], "sourceHeight": info["height"],
        "frames": total_frames,
        "fps": total_frames / total_seconds if total_seconds else 0,
        "modelLoadSeconds": model_seconds,
        "totalSeconds": total_seconds,
        "pipelineMode": args.pipeline_mode,
        "requestedDecodeMode": requested_decode,
        "resolvedDecodeMode": decode_mode,
        "decodeFallback": fallback,
        "overlapFrames": OVERLAP_FRAMES,
        "sensitivityPercent": args.sensitivity_percent,
        "processWidth": width, "processHeight": height,
        "vfrApproximation": info["vfr_approximation"],
        **metrics,
        **memory_profile(),
    }
    append_profile(args.profile_log, profile)
    emit(stage="complete", percent=100, ranges=ranges, totalFrames=total_frames,
         sensitivityDataFormat="omnishotcut-gzip-json-v1",
         sensitivityData=sensitivity_data, detectionFrameRate=float(info["rate"]),
         frameRate=f"{info['rate'].numerator}/{info['rate'].denominator}",
         revision=MODEL_REVISION, overlapFrames=OVERLAP_FRAMES,
         device=profile["device"], pipelineMode=args.pipeline_mode,
         decodeMode=decode_mode, timing={
             "modelLoadSeconds": model_seconds,
             "decodeReadSeconds": metrics["decodeReadSeconds"],
             "preprocessSeconds": metrics["preprocessSeconds"],
             "transferSeconds": metrics["transferSeconds"],
             "inferenceSeconds": metrics["inferenceSeconds"],
             "totalSeconds": total_seconds,
         })


def self_test():
    assert milliseconds(30, Fraction(30, 1)) == 1000
    values = []
    merge_predictions(values, [{"end_frame": 20}, {"end_frame": 40}])
    merge_predictions(values, [{"end_frame": 41}, {"end_frame": 80}])
    assert [item["end_frame"] for item in values] == [20, 40, 80]
    sensitivity_values = [
        {"end_frame": 10, "confidence": .2},
        {"end_frame": 20, "confidence": .99},
        {"end_frame": 30, "confidence": .8},
    ]
    assert [value["end_frame"] for value in
            select_by_sensitivity(sensitivity_values, 1)] == [20]
    assert select_by_sensitivity(sensitivity_values, 100) == sensitivity_values
    source = torch.arange(4 * 3 * 5 * 3, dtype=torch.uint8).reshape(4, 3, 5, 3)
    actual = torch.empty((1, 4, 3, 3, 5), dtype=torch.float32)
    mean = torch.tensor(NORMALIZE_MEAN).view(1, 1, 3, 1, 1)
    std = torch.tensor(NORMALIZE_STD).view(1, 1, 3, 1, 1)
    normalize_into(source, actual, mean, std)
    expected = source.float().div(255).permute(0, 3, 1, 2).contiguous()
    expected = torch.stack([(frame - mean[0, 0]) / std[0, 0]
                            for frame in expected]).unsqueeze(0)
    assert torch.equal(actual, expected)
    automatic = {"codec": "h264"}
    assert resolve_decode_mode("auto", automatic) == "cpu"
    assert resolve_decode_mode("auto", {"codec": "hevc", "width": 3840,
                                        "height": 2160}) == "cpu"
    assert resolve_decode_mode("auto", {"codec": "av1", "width": 3840,
                                        "height": 2160}) == "legacy"
    emit(stage="self-test", percent=100, ok=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source")
    parser.add_argument("--runtime")
    parser.add_argument("--profile-log")
    parser.add_argument("--pipeline-mode", choices=("bounded", "serial"),
                        default="bounded", help=argparse.SUPPRESS)
    parser.add_argument("--decode-mode", choices=("auto", "legacy", "cpu", "cpu-fast"),
                        default="auto", help=argparse.SUPPRESS)
    parser.add_argument("--window-batch", type=int, default=1,
                        help=argparse.SUPPRESS)
    parser.add_argument("--sensitivity-percent", type=int, default=100)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not args.source or not args.runtime:
        parser.error("--source and --runtime are required")
    if not 1 <= args.sensitivity_percent <= 100:
        parser.error("--sensitivity-percent must be between 1 and 100")
    run(args)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        raise SystemExit(130)
    except Exception as error:
        print(str(error), file=sys.stderr, flush=True)
        raise SystemExit(1)
