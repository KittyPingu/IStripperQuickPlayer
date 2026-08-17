"""Pinned QuickPlayer adapter for FudanCVL/SAM2Matting.

Stdout is reserved for NDJSON protocol/progress messages.  Diagnostics go to
stderr.  The adapter intentionally uses eager BF16 and the upstream SDPA path.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import contextlib
import gc
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from fractions import Fraction
from pathlib import Path

SOURCE_REVISION = "73dd721d77b56749248aefe5e8824d7f61b9d13c"
CHECKPOINT_REVISION = "4315db9c60d27fde396b09765748a0ca6c97bed5"
ALPHA_ENCODING_POLICY = "h264-yuv420p-linear"
CHECKPOINTS = {
    "sam2.1-tiny": (
        "SAM2Matting-SAM2.1Tiny.pt",
        215_569_778,
        "5b9321e3b51bc20f5b84c208746cc083dd3053dd701590f2e88dc8640afcc39d",
        "configs/sam2matting-sam2.1tiny.yaml",
    ),
    "sam2.1-base-plus": (
        "SAM2Matting-SAM2.1Base+.pt",
        383_180_506,
        "1f0eb2eda3e8bc9101eafc0b30b8b8fcae1ff83d8fd3adc18e2f3b410fdaae60",
        "configs/sam2matting-sam2.1base+.yaml",
    ),
    "sam3": (
        "SAM2Matting-SAM3.pt",
        3_509_720_141,
        "7102d695be6070b39acd67464f93207df725514a688b545ed1267d913d3b9c7d",
        None,
    ),
}

# Keep each SAM3 core block bounded. At 4K this yields 64 new frames per block;
# later blocks prepend a small tracking context, while the float16 max-union
# remains smaller than the previous non-overlapped float32 backing file. At
# lower resolutions the cap also prevents the upstream loader from retaining a
# long video on the GPU. Persisted logical scene boundaries remain unchanged.
SAM3_MAX_UNION_BYTES = 2 * 1024 * 1024 * 1024
SAM3_MAX_CHUNK_FRAMES = 128
SAM3_CONTEXT_FRAMES = 16
SAM3_UNION_DTYPE = "float16"
SAM3_WORKING_EDGE = 1008
_NVENC_AVAILABLE = None


class CudaFrameView:
    """Expose a CPU-backed Fudan frame loader as a one-frame CUDA cache."""

    def __init__(self, frames, device):
        self.frames = frames
        self.device = device
        self.cached_index = None
        self.cached_frame = None

    def __len__(self):
        return len(self.frames)

    def __getitem__(self, index):
        if index != self.cached_index:
            self.cached_frame = self.frames[index].float().to(
                self.device, non_blocking=True)
            self.cached_index = index
        return self.cached_frame

    def close(self):
        self.cached_frame = None
        self.cached_index = None
        close = getattr(self.frames, "close", None)
        if close is not None:
            close()


class Sam2AlphaTransferQueue:
    """Overlap full-resolution alpha download with the next tracking frame."""

    def __init__(self, union, max_pending=3):
        import torch

        self.torch = torch
        self.union = union
        self.max_pending = max_pending
        self.stream = torch.cuda.Stream()
        self.pending = []
        self.free_buffers = []
        self.writer = concurrent.futures.ThreadPoolExecutor(
            max_workers=1, thread_name_prefix="sam2matting-frame-write")
        self.pending_writes = []

    @staticmethod
    def _two_dimensional(value):
        while value.ndim > 2:
            value = value[0]
        return value

    def submit(self, frame_index, alpha):
        torch = self.torch
        self._reclaim_writes(wait=False)
        if len(self.pending) >= self.max_pending:
            self.drain_one()
        source = self._two_dimensional(alpha.detach())
        target = (self.free_buffers.pop()
                  if self.free_buffers else
                  torch.empty(source.shape, dtype=torch.float32,
                              device="cpu", pin_memory=True))
        ready = torch.cuda.Event()
        ready.record(torch.cuda.current_stream())
        complete = torch.cuda.Event()
        with torch.cuda.stream(self.stream):
            self.stream.wait_event(ready)
            target.copy_(source, non_blocking=True)
            complete.record(self.stream)
        self.pending.append((frame_index, source, target, ready, complete))

    def drain_one(self):
        frame_index, source, target, ready, complete = self.pending.pop(0)
        complete.synchronize()
        del source, ready
        future = self.writer.submit(self._write, frame_index, target)
        self.pending_writes.append((future, target))
        if len(self.pending_writes) >= self.max_pending:
            self._reclaim_writes(wait=True)

    def _write(self, frame_index, target):
        import numpy as np

        values = target.numpy()
        np.clip(values, 0.0, 1.0, out=values)
        if hasattr(self.union, "write_frame"):
            self.union.write_frame(frame_index, values)
        else:
            # SAM2 receives one saved union mask as one object, so every
            # canonical frame is emitted exactly once.
            self.union[frame_index] = values

    def _reclaim_writes(self, wait):
        while self.pending_writes:
            future, target = self.pending_writes[0]
            if not wait and not future.done():
                break
            future.result()
            self.pending_writes.pop(0)
            self.free_buffers.append(target)
            if wait:
                break

    def finish(self):
        while self.pending:
            self.drain_one()
        while self.pending_writes:
            self._reclaim_writes(wait=True)

    def close(self):
        self.finish()
        self.writer.shutdown(wait=True, cancel_futures=False)
        self.free_buffers.clear()


class Sam2AlphaRawSink:
    """Write canonical SAM2 alpha frames directly into the final raw stream."""

    def __init__(self, stream, base_frame, frame_count, width, height):
        self.stream = stream
        self.base_frame = int(base_frame)
        self.shape = (int(frame_count), int(height), int(width))
        self.frame_bytes = int(width) * int(height)

    def write_frame(self, frame_index, values):
        import numpy as np

        encoded = np.rint(values * 255.0).astype(np.uint8, copy=False)
        self.stream.seek((self.base_frame + int(frame_index)) * self.frame_bytes)
        self.stream.write(encoded.tobytes(order="C"))


def emit(stage, percent, message, **extra):
    print(json.dumps({"stage": stage, "percent": float(percent),
                      "message": message, **extra}), flush=True)


def fail(message):
    emit("error", 0, str(message))


def run_process(args, capture=True):
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    return subprocess.run(args, check=True, text=True,
                          capture_output=capture, creationflags=creationflags)


def run_ffmpeg_progress(command, frame_count, on_frame=None,
                        check_cancelled=None):
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    process = subprocess.Popen(
        command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        encoding="utf-8", errors="replace", creationflags=creationflags)
    try:
        assert process.stdout is not None
        for line in process.stdout:
            if check_cancelled is not None:
                check_cancelled()
            key, separator, value = line.strip().partition("=")
            if separator and key == "frame" and on_frame is not None:
                try:
                    on_frame(min(frame_count, max(0, int(value))))
                except ValueError:
                    pass
        stderr = process.stderr.read() if process.stderr is not None else ""
        if process.wait() != 0:
            raise RuntimeError(stderr.strip() or "FFmpeg processing failed")
    except BaseException:
        if process.poll() is None:
            process.kill()
            process.wait()
        raise
    if on_frame is not None:
        on_frame(frame_count)


def ffmpeg():
    return os.environ.get("IQP_FFMPEG", "ffmpeg")


def ffprobe():
    return os.environ.get("IQP_FFPROBE", "ffprobe")


def cancelled(request):
    path = request.get("cancelPath")
    if path and os.path.exists(path):
        raise KeyboardInterrupt("SAM2Matting job cancelled")


def marker(runtime):
    path = runtime / "environment.json"
    if not path.is_file():
        raise RuntimeError("Setup required: environment.json is missing")
    # Windows PowerShell 5.1 writes UTF-8 JSON with a BOM; accept both forms.
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if (value.get("environmentSpecVersion") != "sam2matting-v1" or
            value.get("sourceRevision") != SOURCE_REVISION or
            value.get("checkpointRevision") != CHECKPOINT_REVISION):
        raise RuntimeError("Setup required: the SAM2Matting environment revision is incompatible")
    return value


def cheap_checkpoint_check(runtime, tracker, expected_hash):
    if tracker not in CHECKPOINTS:
        raise RuntimeError(f"Unsupported SAM2Matting tracker: {tracker}")
    name, size, digest, _ = CHECKPOINTS[tracker]
    path = runtime / "checkpoints" / name
    if not path.is_file() or path.stat().st_size != size or expected_hash != digest:
        raise RuntimeError(f"Setup required: {name} is missing or incompatible")
    return path


def normalize_concepts(values):
    normalized = []
    seen = set()
    for line in values or []:
        value = str(line).strip()
        folded = value.casefold()
        if not value or folded in seen:
            continue
        if len(value) > 200:
            raise RuntimeError("A foreground concept cannot exceed 200 characters")
        seen.add(folded)
        normalized.append(value)
    if not normalized:
        raise RuntimeError("Enter at least one foreground concept for SAM3")
    if len(normalized) > 64:
        raise RuntimeError("A SAM3 job cannot contain more than 64 concepts")
    return normalized


def validate_scene_contract(request, fps):
    def canonical_frame(milliseconds):
        value = Fraction(int(milliseconds), 1000) * fps
        return (value.numerator * 2 + value.denominator) // (2 * value.denominator)

    scenes = request.get("scenes") or []
    seen = set()
    for clip in request.get("clips") or []:
        values = sorted((item for item in scenes
                         if item["clipId"].casefold() == clip["id"].casefold()),
                        key=lambda item: item["startFrame"])
        expected_start = canonical_frame(clip["startMs"])
        expected_end = max(expected_start + 1, canonical_frame(clip["endMs"]))
        if not values or values[0]["startFrame"] != expected_start or \
                values[-1]["endFrameExclusive"] != expected_end:
            raise RuntimeError("Saved scenes do not cover every canonical clip frame")
        for index, scene in enumerate(values):
            if scene["id"].casefold() in seen or \
                    scene["endFrameExclusive"] <= scene["startFrame"] or \
                    index and values[index - 1]["endFrameExclusive"] != scene["startFrame"]:
                raise RuntimeError("Saved scenes are duplicated, overlapping, or non-contiguous")
            seen.add(scene["id"].casefold())
    if len(seen) != len(scenes):
        raise RuntimeError("A saved scene references an unknown clip")


def frame_rates_match(first, second):
    """Allow the tiny rational rounding imposed by Matroska's 1 ms time base."""
    first = Fraction(first)
    second = Fraction(second)
    difference = abs(float(first - second))
    return difference <= max(float(first), float(second)) * 0.00001


def sam3_working_size(width, height):
    scale = min(1.0, SAM3_WORKING_EDGE / max(1, int(width), int(height)))
    working_width = max(2, int(round(int(width) * scale / 2)) * 2)
    working_height = max(2, int(round(int(height) * scale / 2)) * 2)
    return working_width, working_height


def sam3_chunk_size(width, height):
    bytes_per_frame = max(1, int(width) * int(height) * 4)
    return max(1, min(SAM3_MAX_CHUNK_FRAMES,
                      SAM3_MAX_UNION_BYTES // bytes_per_frame))


def tracker_scene_chunk_size(tracker, width, height, scene_count):
    return (sam3_chunk_size(width, height)
            if tracker == "sam3" else int(scene_count))


def frame_chunks(frame_count, chunk_size, context_frames=0):
    if frame_count <= 0 or chunk_size <= 0:
        raise ValueError("Frame and chunk counts must be positive")
    if context_frames < 0 or context_frames >= chunk_size:
        raise ValueError("Context frames must be smaller than the chunk size")
    chunks = []
    core_offset = 0
    while core_offset < frame_count:
        context = min(context_frames, core_offset)
        core_count = min(chunk_size, frame_count - core_offset)
        chunks.append((core_offset - context, context + core_count,
                       context, core_count))
        core_offset += core_count
    return chunks


def prepare_union(path, shape):
    import numpy as np

    union = np.memmap(path, mode="w+", dtype=SAM3_UNION_DTYPE, shape=shape)
    union[:] = 0
    union.flush()
    mapping = getattr(union, "_mmap", None)
    if mapping is not None:
        mapping.close()


def prepare_sam3_chunk(source, directory, union_path, start_frame, frame_count,
                       width, height, fps, on_frame=None,
                       check_cancelled=None):
    extract_scene(source, directory, start_frame, frame_count, fps, on_frame,
                  check_cancelled, (width, height))
    if check_cancelled is not None:
        check_cancelled()
    prepare_union(union_path, (frame_count, height, width))


def full_checkpoint_check(path, expected_size, expected_hash):
    if path.stat().st_size != expected_size:
        raise RuntimeError(f"Checkpoint size mismatch: {path.name}")
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest().lower() != expected_hash:
        raise RuntimeError(f"Checkpoint SHA-256 mismatch: {path.name}")


def probe_source(source):
    result = run_process([
        ffprobe(), "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate,time_base",
        "-show_entries", "format=duration", "-of", "json", source,
    ])
    data = json.loads(result.stdout)
    stream = data["streams"][0]
    rate = stream.get("avg_frame_rate")
    if not rate or rate == "0/0":
        rate = stream["r_frame_rate"]
    fps = Fraction(rate)
    if fps <= 0:
        raise RuntimeError("The input frame rate is invalid")
    return {
        "width": int(stream["width"]), "height": int(stream["height"]),
        "frameRate": f"{fps.numerator}/{fps.denominator}",
        "fps": fps, "timeBase": stream.get("time_base") or "",
        "duration": float(data["format"]["duration"]),
    }


def extract_scene(source, directory, start_frame, frame_count, fps,
                  on_frame=None, check_cancelled=None, output_size=None):
    directory.mkdir(parents=True, exist_ok=True)
    start_seconds = float(Fraction(start_frame, 1) / fps)
    filter_rate = f"{fps.numerator}/{fps.denominator}"
    filters = [f"fps={filter_rate}"]
    if output_size is not None:
        if isinstance(output_size, (tuple, list)):
            output_width = max(1, int(output_size[0]))
            output_height = max(1, int(output_size[1]))
        else:
            output_width = output_height = max(1, int(output_size))
        filters.append(
            f"scale={output_width}:{output_height}:flags=bicubic")
    command = [
        ffmpeg(), "-y", "-v", "error", "-progress", "pipe:1", "-nostats",
        "-ss", f"{start_seconds:.9f}",
        "-i", source, "-an", "-vf", ",".join(filters),
        "-frames:v", str(frame_count), "-pix_fmt", "rgb24",
        "-compression_level", "1",
        str(directory / "%08d.png"),
    ]
    run_ffmpeg_progress(command, frame_count, on_frame, check_cancelled)
    files = sorted(directory.glob("*.png"))
    if len(files) != frame_count:
        raise RuntimeError(
            f"Scene extraction returned {len(files)} of {frame_count} canonical frames")
    return files


def install_float32_sam2_image_loader():
    """Use float32 plus bounded parallel decoding for Fudan's CPU frame cache."""
    import numpy as np
    import torch
    from PIL import Image
    from sam2.utils import misc

    if getattr(misc._load_img_as_tensor, "_quickplayer_float32", False):
        return

    def load_img_as_float32(img_path, image_size):
        with Image.open(img_path) as source:
            video_width, video_height = source.size
            resized = source.convert("RGB").resize((image_size, image_size))
            image = np.array(resized, dtype=np.float32, copy=True)
        image *= np.float32(1.0 / 255.0)
        tensor = torch.from_numpy(image).permute(2, 0, 1)
        return tensor, video_height, video_width

    load_img_as_float32._quickplayer_float32 = True
    misc._load_img_as_tensor = load_img_as_float32

    class ParallelVideoFrameLoader:
        """Keep only a small rolling CPU-frame prefetch around the current frame."""

        PREFETCH_FRAMES = 6

        def __init__(self, img_paths, image_size, offload_video_to_cpu,
                     img_mean, img_std, compute_device):
            self.img_paths = img_paths
            self.image_size = image_size
            self.offload_video_to_cpu = offload_video_to_cpu
            self.img_mean = img_mean
            self.img_std = img_std
            self.compute_device = compute_device
            self._length = len(img_paths)
            self._cache = {}
            self.video_height = None
            self.video_width = None
            self._executor = concurrent.futures.ThreadPoolExecutor(
                max_workers=min(3, max(1, len(img_paths))),
                thread_name_prefix="sam2matting-frame-load")
            self._futures = {}
            self._last_index = None
            first, height, width = self._load(0)
            self._cache[0] = first
            self.video_height = height
            self.video_width = width
            self._schedule(0, 1)

        def _load(self, index):
            image, height, width = misc._load_img_as_tensor(
                self.img_paths[index], self.image_size)
            image.sub_(self.img_mean).div_(self.img_std)
            if not self.offload_video_to_cpu:
                image = image.to(self.compute_device, non_blocking=True)
            return image, height, width

        def __getitem__(self, index):
            index = int(index)
            if index < 0 or index >= self._length:
                raise IndexError(index)
            direction = (-1 if self._last_index is not None and
                         index < self._last_index else 1)
            try:
                image = self._cache.pop(index, None)
                if image is None:
                    future = self._futures.pop(index, None)
                    result = (future.result() if future is not None
                              else self._load(index))
                    image, height, width = result
                else:
                    height, width = self.video_height, self.video_width
                self._cache.clear()
                self._cache[index] = image
                self.video_height = height
                self.video_width = width
                self._last_index = index
                self._schedule(index, direction)
                return image
            except Exception as error:
                raise RuntimeError("Failure in parallel frame loader") from error

        def _schedule(self, index, direction):
            wanted = {
                candidate for offset in range(1, self.PREFETCH_FRAMES + 1)
                if 0 <= (candidate := index + direction * offset) < self._length
            }
            for candidate, future in list(self._futures.items()):
                if candidate not in wanted:
                    future.cancel()
                    self._futures.pop(candidate, None)
            for candidate in wanted:
                if candidate not in self._futures:
                    self._futures[candidate] = self._executor.submit(
                        self._load, candidate)

        def __len__(self):
            return self._length

        def close(self):
            self._executor.shutdown(wait=True, cancel_futures=True)
            self._futures.clear()
            self._cache.clear()

    misc.AsyncVideoFrameLoader = ParallelVideoFrameLoader


def load_variant(runtime, tracker):
    import torch

    source = runtime / "source" / "SAM2Matting"
    if not source.is_dir():
        raise RuntimeError("Setup required: pinned Fudan source is missing")
    os.chdir(source)
    sys.path.insert(0, str(source))
    checkpoint = runtime / "checkpoints" / CHECKPOINTS[tracker][0]
    emit("checkpoint-loading", 1, f"Loading {tracker} checkpoint")
    if tracker == "sam3":
        from sam3.model_builder import build_sam3_video_predictor
        with contextlib.redirect_stdout(sys.stderr):
            predictor = build_sam3_video_predictor(
                gpus_to_use=[0], checkpoint_path=str(checkpoint),
                strict_state_dict_loading=False,
                bpe_path=str(source / "sam3" / "bpe_simple_vocab_16e6.txt.gz"),
            )
    else:
        from sam2.build_sam import build_sam2matting_video_predictor
        install_float32_sam2_image_loader()
        with contextlib.redirect_stdout(sys.stderr):
            predictor = build_sam2matting_video_predictor(
                CHECKPOINTS[tracker][3], str(checkpoint), device="cuda",
                hydra_overrides_extra=[],
            )
    emit("checkpoint-loading", 5, f"Loaded {tracker} in eager BF16 mode")
    return predictor


def alpha_array(value):
    import numpy as np
    import torch

    if torch.is_tensor(value):
        value = value.detach().float().cpu().numpy()
    value = np.asarray(value, dtype=np.float32)
    while value.ndim > 2:
        value = value[0]
    return np.clip(value, 0.0, 1.0)


def release_sam2_frame_alpha(state, frame_index):
    """Drop Fudan's full-resolution alpha after QuickPlayer has consumed it."""
    for collection_name in ("output_dict_per_obj", "temp_output_dict_per_obj"):
        for output in state.get(collection_name, {}).values():
            for storage_key in ("cond_frame_outputs", "non_cond_frame_outputs"):
                frame = output.get(storage_key, {}).get(frame_index)
                if frame is not None:
                    frame["alpha"] = None


def sam2_tracking_history(predictor):
    """Number of non-conditioning frames the next SAM2 step can reference."""
    memory = max(0, int(getattr(predictor, "num_maskmem", 1)) - 1)
    stride = max(1, int(getattr(
        predictor, "memory_temporal_stride_for_eval", 1)))
    pointers = max(0, int(getattr(
        predictor, "max_obj_ptrs_in_encoder", 1)) - 1)
    return max(1, memory * stride, pointers)


def prune_sam2_tracking_state(state, frame_index, reverse, history,
                              forward_bridge=None):
    """Drop propagated states that cannot be referenced by a future step."""
    bridge_start, bridge_end = forward_bridge or (0, 0)
    for obj_index, output in state.get("output_dict_per_obj", {}).items():
        non_cond = output.get("non_cond_frame_outputs", {})
        if reverse:
            stale = [value for value in non_cond
                     if value > frame_index + history]
        else:
            stale = [value for value in non_cond
                     if value < frame_index - history and
                     not bridge_start <= value < bridge_end]
        for value in stale:
            non_cond.pop(value, None)
            state.get("frames_tracked_per_obj", {}).get(
                obj_index, {}).pop(value, None)


def process_sam2_scene(predictor, request, scene, scene_dir, union,
                       completed_units=0, total_units=1, scene_number=1,
                       scene_total=1, completed_frames=0, total_frames=1,
                       output_width=None, output_height=None, preview_dir=None):
    import numpy as np
    import torch
    from PIL import Image

    prompt = next((item for item in request["prompts"]
                   if item["sceneId"].lower() == scene["id"].lower()), None)
    if prompt is None:
        raise RuntimeError(f"Scene {scene['id']} has no initial mask")
    local_prompt = int(prompt["promptFrame"] - scene["startFrame"])
    mask = np.asarray(Image.open(prompt["initialMaskPath"]).convert("L")) > 0
    # Fudan's default retains every resized source frame on CUDA. A long 4K
    # scene can therefore exhaust a 16 GB card before tracking state and alpha
    # heads are included. Keep the source-frame tensor cache in system memory
    # and load it asynchronously. Tracker state stays on the GPU for throughput,
    # but is pruned to the exact temporal history later steps can reference.
    state = predictor.init_state(
        video_path=str(scene_dir), offload_video_to_cpu=True,
        offload_state_to_cpu=False, async_loading_frames=True)
    if output_width is not None and output_height is not None:
        # Extracted tracking images are already model-sized, but all prompt
        # coordinates and progressive alpha remain in canonical source space.
        state["video_width"] = int(output_width)
        state["video_height"] = int(output_height)
    # Fudan's modified matting predictor bypasses the base predictor's device
    # transfer and reads inference_state["images"] directly in its alpha head.
    # Present a one-frame CUDA cache so tracking and matting share the current
    # device tensor without uploading the entire scene.
    state["images"] = CudaFrameView(state["images"], state["device"])
    transfers = Sam2AlphaTransferQueue(union)
    try:
        predictor.reset_state(state)
        predictor.add_new_mask(
            inference_state=state, frame_idx=local_prompt, obj_id=1,
            mask=torch.from_numpy(mask),
        )
        count = union.shape[0]
        forward_count = count - local_prompt
        history = sam2_tracking_history(predictor)
        # A later reverse pass initially consults the first few forward states.
        # Preserve only that dependency bridge while the forward pass advances.
        forward_bridge = (local_prompt + 1,
                          min(count, local_prompt + history + 1))
        for index, _, _, alpha, _ in predictor.propagate_in_video(
                state, start_frame_idx=local_prompt,
                max_frame_num_to_track=count - local_prompt, reverse=False):
            cancelled(request)
            show_preview = preview_dir is not None and (
                index % 32 == 0 or index + 1 == count)
            source_preview = composite_preview = None
            if show_preview:
                source_preview, composite_preview = write_detection_preview(
                    scene_dir, index, alpha_array(alpha), preview_dir)
            transfers.submit(index, alpha)
            release_sam2_frame_alpha(state, index)
            prune_sam2_tracking_state(
                state, index, False, history, forward_bridge)
            del alpha
            if index % 10 == 0 or show_preview:
                tracked = index - local_prompt + 1
                emit("tracking", 5 + 92 *
                     (completed_units + tracked) / max(1, total_units),
                     f"Scene {scene_number}/{scene_total} • forward frame "
                     f"{index + 1}/{count} • overall "
                     f"{min(total_frames, completed_frames + tracked)}/"
                     f"{total_frames} frames",
                     previewSource=source_preview,
                     previewComposite=composite_preview,
                     previewSourceLabel="Current source frame",
                     previewCompositeLabel="Matted foreground on green")
        if local_prompt > 0:
            for index, _, _, alpha, _ in predictor.propagate_in_video(
                    state, start_frame_idx=local_prompt - 1,
                    max_frame_num_to_track=local_prompt, reverse=True):
                cancelled(request)
                show_preview = preview_dir is not None and (
                    index % 32 == 0 or index == 0)
                source_preview = composite_preview = None
                if show_preview:
                    source_preview, composite_preview = write_detection_preview(
                        scene_dir, index, alpha_array(alpha), preview_dir)
                transfers.submit(index, alpha)
                release_sam2_frame_alpha(state, index)
                prune_sam2_tracking_state(state, index, True, history)
                del alpha
                if index % 10 == 0 or show_preview:
                    tracked = forward_count + local_prompt - index
                    emit("tracking", 5 + 92 *
                         (completed_units + tracked) / max(1, total_units),
                         f"Scene {scene_number}/{scene_total} • reverse frame "
                         f"{local_prompt - index}/{local_prompt} • overall "
                         f"{min(total_frames, completed_frames + tracked)}/"
                         f"{total_frames} frames",
                         previewSource=source_preview,
                         previewComposite=composite_preview,
                         previewSourceLabel="Current source frame",
                         previewCompositeLabel="Matted foreground on green")
        transfers.finish()
    finally:
        try:
            transfers.close()
            predictor.reset_state(state)
        finally:
            images = state.get("images")
            close = getattr(images, "close", None)
            if close is not None:
                close()
            del state


def ensure_frame_cache(model, state, frame_idx):
    if frame_idx not in state["feature_cache"]:
        model._prepare_backbone_feats(state, frame_idx, reverse=False)


def sam3_alpha(model, state, frame_idx, mask_hw):
    import numpy as np
    import torch
    import torch.nn.functional as functional

    ensure_frame_cache(model, state, frame_idx)
    image, cache = state["feature_cache"][frame_idx]
    if image.dim() == 3:
        image = image.unsqueeze(0)
    image = image.to("cuda")
    high_res_features = list(cache["tracker_backbone_out"]["backbone_fpn"])
    if torch.is_tensor(mask_hw):
        mask = mask_hw.detach().to(device="cuda", dtype=torch.float32)
    else:
        mask = torch.as_tensor(np.asarray(mask_hw), device="cuda",
                               dtype=torch.float32)
    while mask.dim() > 2:
        mask = mask[0]
    # SAM3 returns binary masks, but PyTorch's bilinear interpolation has no
    # Bool kernel. Resize a float boundary mask and threshold it again for the
    # matting head's binary ROI input.
    mask = (mask > 0).float()
    mask_288 = functional.interpolate(
        mask[None, None], size=(288, 288), mode="bilinear",
        align_corners=False, antialias=True,
    )
    alpha, _, _ = model.tracker._forward_alpha_heads(
        input=image, backbone_features=None, point_inputs=None,
        mask_inputs=(mask_288 > 0).float(), unknown_region_inputs=None,
        high_res_features=high_res_features, image=None, trimap_input=None,
    )
    alpha = functional.interpolate(
        alpha.float(), size=(state["orig_height"], state["orig_width"]),
        mode="bilinear", align_corners=False,
    )
    return alpha.squeeze().detach().float().cpu().numpy().clip(0, 1)


def write_detection_preview(scene_dir, frame_idx, alpha, preview_dir):
    import numpy as np
    from PIL import Image

    source_path = scene_dir / f"{frame_idx + 1:08d}.png"
    with Image.open(source_path) as opened:
        source = opened.convert("RGB")
    source.thumbnail((640, 640), Image.Resampling.LANCZOS)
    alpha_image = Image.fromarray(
        np.rint(np.clip(alpha, 0, 1) * 255).astype(np.uint8), mode="L")
    alpha_image = alpha_image.resize(source.size, Image.Resampling.BILINEAR)
    source_array = np.asarray(source, dtype=np.float32)
    alpha_array_2d = np.asarray(alpha_image, dtype=np.float32) / 255.0
    green = np.zeros_like(source_array)
    green[..., 1] = 177
    composite = Image.fromarray(np.rint(
        source_array * alpha_array_2d[..., None] +
        green * (1 - alpha_array_2d[..., None])).astype(np.uint8), mode="RGB")
    preview_dir.mkdir(parents=True, exist_ok=True)
    source_preview = preview_dir / "sam3-source.jpg"
    composite_preview = preview_dir / "sam3-composite.jpg"
    source_temp = preview_dir / "sam3-source.tmp.jpg"
    composite_temp = preview_dir / "sam3-composite.tmp.jpg"
    source.save(source_temp, format="JPEG", quality=88)
    composite.save(composite_temp, format="JPEG", quality=88)
    os.replace(source_temp, source_preview)
    os.replace(composite_temp, composite_preview)
    return str(source_preview), str(composite_preview)


def process_sam3_scene(predictor, request, scene_dir, union,
                       completed_units=0, total_units=1, preview_dir=None,
                       context_frames=0, core_count=None, scene_number=1,
                       scene_total=1, completed_frames=0, total_frames=1):
    import numpy as np

    if core_count is None:
        core_count = union.shape[0] - context_frames
    for concept_index, concept in enumerate(request["concepts"]):
        response = predictor.handle_request({
            "type": "start_session", "resource_path": str(scene_dir),
        })
        session_id = response["session_id"]
        try:
            cancelled(request)
            concept_units = completed_units + concept_index * union.shape[0]
            emit("concept-detection",
                 5 + 92 * concept_units / max(1, total_units),
                 f"Detecting concept {concept_index + 1}/{len(request['concepts'])}: {concept}")
            state = predictor._get_session(session_id)["state"]
            predictor.model.add_prompt(
                inference_state=state, frame_idx=0, text_str=concept)
            for response in predictor.handle_stream_request({
                    "type": "propagate_in_video", "session_id": session_id,
                    "propagation_direction": "forward"}):
                cancelled(request)
                index = int(response["frame_index"])
                outputs = response["outputs"]
                masks = outputs.get("out_binary_masks", [])
                for mask in masks:
                    cancelled(request)
                    alpha = sam3_alpha(predictor.model, state, index, mask)
                    union[index] = np.maximum(union[index], alpha)
                core_index = index - context_frames
                in_core = 0 <= core_index < core_count
                show_preview = preview_dir is not None and in_core and (
                    core_index % 32 == 0 or core_index + 1 == core_count)
                source_preview = composite_preview = None
                if show_preview:
                    source_preview, composite_preview = write_detection_preview(
                        scene_dir, index, union[index], preview_dir)
                if in_core and (core_index % 10 == 0 or show_preview):
                    processed = (completed_units + concept_index *
                                 union.shape[0] + index + 1)
                    emit("tracking", 5 + 92 * processed / max(1, total_units),
                         f"Scene {scene_number}/{scene_total} • concept "
                         f"{concept_index + 1}/{len(request['concepts'])} • "
                         f"output frame {core_index + 1}/{core_count} • overall "
                         f"{min(total_frames, completed_frames + core_index + 1)}/"
                         f"{total_frames}" +
                         (f" • warmed with {context_frames} prior frames"
                          if context_frames else ""),
                         previewSource=source_preview,
                         previewComposite=composite_preview,
                         previewSourceLabel="Current source frame",
                         previewCompositeLabel="Detected foreground on green")
        finally:
            predictor.handle_request({"type": "close_session", "session_id": session_id})


def encode_alpha(raw_path, destination, width, height, fps, count,
                 on_frame=None, check_cancelled=None,
                 raw_width=None, raw_height=None):
    codec = alpha_codec_args(width, height)
    input_width = int(raw_width or width)
    input_height = int(raw_height or height)
    scaling = ([] if (input_width, input_height) == (int(width), int(height))
               else ["-vf", f"scale={int(width)}:{int(height)}:flags=bilinear"])
    run_ffmpeg_progress([
        ffmpeg(), "-y", "-v", "error", "-progress", "pipe:1", "-nostats",
        "-f", "rawvideo",
        "-pixel_format", "gray", "-video_size", f"{input_width}x{input_height}",
        "-framerate", f"{fps.numerator}/{fps.denominator}", "-i", str(raw_path),
        "-frames:v", str(count), "-an", *scaling, *codec,
        "-pix_fmt", "yuv420p", "-color_range", "pc", str(destination),
    ], count, on_frame, check_cancelled)


class FfmpegAlphaStreamSink:
    """Stream ascending 8-bit alpha frames into a playback H.264 segment."""

    def __init__(self, destination, width, height, fps, frame_count):
        self.destination = Path(destination)
        self.frame_count = int(frame_count)
        self.next_frame = 0
        self.process = subprocess.Popen([
            ffmpeg(), "-y", "-v", "error", "-f", "rawvideo",
            "-pixel_format", "gray", "-video_size", f"{width}x{height}",
            "-framerate", f"{fps.numerator}/{fps.denominator}",
            "-i", "pipe:0", "-frames:v", str(frame_count), "-an",
            *alpha_codec_args(width, height),
            "-pix_fmt", "yuv420p",
            "-color_range", "pc", str(self.destination),
        ], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
           stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)

    def write_frame(self, frame_index, values):
        import numpy as np

        if int(frame_index) != self.next_frame:
            raise RuntimeError(
                f"H.264 alpha stream expected frame {self.next_frame}, "
                f"received {frame_index}")
        encoded = np.rint(values * 255.0).astype(np.uint8, copy=False)
        self.process.stdin.write(encoded.tobytes(order="C"))
        self.next_frame += 1

    def finish(self):
        if self.process.stdin is not None:
            self.process.stdin.close()
            self.process.stdin = None
        diagnostics = self.process.stderr.read().decode("utf-8", errors="replace")
        code = self.process.wait()
        if code != 0 or self.next_frame != self.frame_count:
            raise RuntimeError(
                f"H.264 alpha stream failed after {self.next_frame}/"
                f"{self.frame_count} frames: {diagnostics.strip()}")

    def abort(self):
        if self.process.poll() is None:
            self.process.kill()
        self.process.wait()
        self.destination.unlink(missing_ok=True)


class Sam2SceneAlphaSink:
    """Stream the forward range and bound raw storage to an interior prefix."""

    def __init__(self, work, scene_id, prompt_frame, frame_count,
                 width, height, fps):
        self.prompt_frame = int(prompt_frame)
        self.shape = (int(frame_count), int(height), int(width))
        self.fps = fps
        self.width = int(width)
        self.height = int(height)
        self.forward_path = Path(work) / f"alpha-{scene_id}-forward.mkv"
        self.forward = FfmpegAlphaStreamSink(
            self.forward_path, width, height, fps,
            frame_count - self.prompt_frame)
        self.reverse_path = Path(work) / f"alpha-{scene_id}-reverse.mkv"
        self.reverse_raw_path = Path(work) / f"alpha-{scene_id}-reverse.gray8"
        self.reverse_stream = None
        self.reverse = None
        if self.prompt_frame > 0:
            self.reverse_stream = self.reverse_raw_path.open("w+b")
            self.reverse = Sam2AlphaRawSink(
                self.reverse_stream, 0, self.prompt_frame, width, height)

    def write_frame(self, frame_index, values):
        frame_index = int(frame_index)
        if frame_index >= self.prompt_frame:
            self.forward.write_frame(frame_index - self.prompt_frame, values)
        else:
            self.reverse.write_frame(frame_index, values)

    def finish(self, check_cancelled=None):
        self.forward.finish()
        segments = []
        if self.reverse is not None:
            self.reverse_stream.flush()
            self.reverse_stream.close()
            self.reverse_stream = None
            encode_alpha(
                self.reverse_raw_path, self.reverse_path,
                self.width, self.height, self.fps, self.prompt_frame,
                check_cancelled=check_cancelled)
            self.reverse_raw_path.unlink(missing_ok=True)
            segments.append(self.reverse_path)
        segments.append(self.forward_path)
        return segments

    def abort(self):
        self.forward.abort()
        if self.reverse_stream is not None:
            self.reverse_stream.close()
            self.reverse_stream = None
        self.reverse_raw_path.unlink(missing_ok=True)
        self.reverse_path.unlink(missing_ok=True)


def concat_alpha_segments(segments, destination, list_path):
    if not segments:
        raise RuntimeError("No H.264 alpha segments were produced")
    if len(segments) == 1:
        os.replace(segments[0], destination)
        return
    lines = []
    for path in segments:
        escaped = str(Path(path).resolve()).replace("'", "'\\''")
        lines.append(f"file '{escaped}'")
    Path(list_path).write_text("\n".join(lines) + "\n", encoding="utf-8")
    run_process([
        ffmpeg(), "-y", "-v", "error", "-f", "concat", "-safe", "0",
        "-i", str(list_path), "-map", "0:v:0", "-c", "copy",
        str(destination),
    ])
    for path in segments:
        Path(path).unlink(missing_ok=True)
    Path(list_path).unlink(missing_ok=True)


def nvenc_available():
    global _NVENC_AVAILABLE
    if _NVENC_AVAILABLE is not None:
        return _NVENC_AVAILABLE
    try:
        encoders = run_process([
            ffmpeg(), "-hide_banner", "-encoders",
        ]).stdout
        if "h264_nvenc" not in encoders:
            _NVENC_AVAILABLE = False
            return False
        test = subprocess.run([
            ffmpeg(), "-y", "-v", "error", "-f", "lavfi", "-i",
            "color=size=256x256:duration=0.05", "-frames:v", "1",
            "-c:v", "h264_nvenc", "-preset", "p5", "-f", "null", "-",
        ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
           creationflags=subprocess.CREATE_NO_WINDOW, timeout=15)
        _NVENC_AVAILABLE = test.returncode == 0
    except (OSError, subprocess.SubprocessError):
        _NVENC_AVAILABLE = False
    return _NVENC_AVAILABLE


def alpha_codec_args(width, height):
    # NVENC rejects very small frames even when the encoder itself is present.
    if int(width) >= 256 and int(height) >= 128 and nvenc_available():
        return ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq",
                "-cq", "10"]
    return ["-c:v", "libx264", "-preset", "medium", "-crf", "10"]


def encode_foreground(source, destination, start_frame, fps, count,
                      on_frame=None, check_cancelled=None):
    # Use the same canonical frame origin as scene extraction and alpha.  This
    # avoids independently rounding a millisecond clip boundary on VFR input.
    start = max(0.0, float(Fraction(start_frame, 1) / fps))
    duration = count / float(fps)
    if nvenc_available():
        codec = ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq",
                 "-cq", "19"]
        encoder, preset = "h264_nvenc", "p5"
    else:
        codec = ["-c:v", "libx264", "-preset", "medium", "-crf", "18"]
        encoder, preset = "libx264", "medium"
    run_ffmpeg_progress([
        ffmpeg(), "-y", "-v", "error", "-progress", "pipe:1", "-nostats",
        "-ss", f"{start:.9f}", "-i", source,
        "-t", f"{duration:.9f}", "-map", "0:v:0", "-map", "0:a:0?",
        "-vf", f"fps={fps.numerator}/{fps.denominator}", "-frames:v", str(count),
        *codec,
        "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
        "-movflags", "+faststart", str(destination),
    ], count, on_frame, check_cancelled)
    return encoder, preset


def probe_output(path):
    result = run_process([
        ffprobe(), "-v", "error", "-count_packets", "-select_streams", "v:0",
        "-show_entries", "stream=width,height,avg_frame_rate,time_base,codec_name,pix_fmt,color_range,nb_frames,nb_read_packets",
        "-of", "json", str(path),
    ])
    stream = json.loads(result.stdout)["streams"][0]
    frame_value = stream.get("nb_read_packets")
    if frame_value in (None, "N/A"):
        frame_value = stream.get("nb_frames")
    if frame_value in (None, "N/A"):
        # Retain a compatibility fallback for unusual containers that expose
        # neither packet nor header counts. QuickPlayer's own MP4/Matroska
        # outputs take the fast packet-count path.
        fallback = run_process([
            ffprobe(), "-v", "error", "-count_frames", "-select_streams", "v:0",
            "-show_entries", "stream=nb_read_frames", "-of", "json", str(path),
        ])
        frame_value = json.loads(fallback.stdout)["streams"][0]["nb_read_frames"]
    return {
        "width": int(stream["width"]), "height": int(stream["height"]),
        "frameRate": stream["avg_frame_rate"], "timeBase": stream["time_base"],
        "codec": stream["codec_name"], "pixelFormat": stream["pix_fmt"],
        "colorRange": stream.get("color_range"),
        "frames": int(frame_value),
    }


def write_union(union, raw_stream, check_cancelled=None, start_index=0):
    import numpy as np

    for index in range(start_index, union.shape[0]):
        if check_cancelled is not None:
            check_cancelled()
        alpha = np.asarray(union[index], dtype=np.float32)
        quantized = np.rint(np.clip(alpha, 0, 1) * 255.0).astype(np.uint8)
        raw_stream.write(quantized.tobytes(order="C"))


def write_and_release_union(union, raw_stream, union_path,
                            check_cancelled=None, start_index=0):
    try:
        write_union(union, raw_stream, check_cancelled, start_index)
        raw_stream.flush()
    finally:
        union.flush()
        mapping = getattr(union, "_mmap", None)
        if mapping is not None:
            mapping.close()
        union_path.unlink(missing_ok=True)


def run_job(request, loaded=None):
    import numpy as np
    import torch

    if request.get("protocolVersion") != 1 or request.get("type") != "run":
        raise RuntimeError("Unsupported SAM2Matting protocol contract")
    if request.get("sourceRevision") != SOURCE_REVISION or request.get(
            "checkpointRevision") != CHECKPOINT_REVISION:
        raise RuntimeError("Setup required: queued source/checkpoint revision is incompatible")
    runtime = Path(request["runtimePath"])
    environment = marker(runtime)
    tracker = request["tracker"]
    if request.get("alphaEncodingPolicy") != ALPHA_ENCODING_POLICY:
        raise RuntimeError(
            "SAM2Matting requires the current H.264 8-bit alpha output policy")
    checkpoint = cheap_checkpoint_check(runtime, tracker,
                                        request.get("checkpointSha256"))
    if tracker == "sam3" and not request.get("concepts"):
        raise RuntimeError("SAM3 requires at least one foreground concept")
    if tracker != "sam3" and request.get("concepts"):
        raise RuntimeError("SAM2.1 trackers do not accept text concepts")
    source = request["sourcePath"]
    media = probe_source(source)
    validate_scene_contract(request, media["fps"])
    if tracker == "sam3":
        normalized = normalize_concepts(request.get("concepts"))
        if normalized != request.get("concepts") or request.get("prompts"):
            raise RuntimeError("SAM3 concepts are not normalized or the job contains scene masks")
        sam3_width, sam3_height = sam3_working_size(
            media["width"], media["height"])
    else:
        sam3_width = sam3_height = None
    output = Path(request["outputPath"])
    output.mkdir(parents=True, exist_ok=True)
    emit("preflight", 0, "Validated saved SAM2Matting job contract")
    predictor = loaded if loaded is not None else load_variant(runtime, tracker)
    sam2_frame_size = (None if tracker == "sam3" else
                       int(getattr(predictor, "image_size", 1024)))
    scenes_by_clip = {}
    for scene in request["scenes"]:
        scenes_by_clip.setdefault(scene["clipId"].lower(), []).append(scene)
    prompt_ids = [item["sceneId"].lower() for item in request.get("prompts", [])]
    if tracker != "sam3" and (len(prompt_ids) != len(set(prompt_ids)) or
                              set(prompt_ids) != {scene["id"].lower()
                                                  for scene in request["scenes"]}):
        raise RuntimeError("Every SAM2.1 scene requires exactly one saved initial mask")
    results = []
    work = output / ".sam2matting-work"
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)
    try:
        total_scenes = max(1, len(request["scenes"]))
        total_frames = sum(int(scene["endFrameExclusive"] - scene["startFrame"])
                           for scene in request["scenes"])
        concept_passes = (len(request["concepts"])
                          if tracker == "sam3" else 1)
        # Count overlap work as well as output frames so ETA covers the complete
        # job rather than becoming optimistic at every bounded-session restart.
        if tracker == "sam3":
            chunk_size = sam3_chunk_size(sam3_width, sam3_height)
            session_frames = sum(sum(chunk[1] for chunk in frame_chunks(
                int(scene["endFrameExclusive"] - scene["startFrame"]),
                chunk_size, min(SAM3_CONTEXT_FRAMES, chunk_size - 1)))
                for scene in request["scenes"])
        else:
            session_frames = total_frames
        total_units = (session_frames * (concept_passes + 1) +
                       total_frames * 2)
        completed_scenes = 0
        completed_frames = 0
        completed_units = 0
        for clip in request["clips"]:
            cancelled(request)
            clip_scenes = sorted(scenes_by_clip.get(clip["id"].lower(), []),
                                 key=lambda item: item["startFrame"])
            if not clip_scenes:
                raise RuntimeError(f"Clip {clip['id']} has no processing scenes")
            clip_dir = output / "clips" / clip["id"]
            clip_dir.mkdir(parents=True, exist_ok=True)
            raw_path = work / f"{clip['id']}.gray8"
            decoded_count = 0
            alpha_segments = []
            writer = concurrent.futures.ThreadPoolExecutor(
                max_workers=1, thread_name_prefix="sam2matting-alpha-write")
            pending_write = None
            try:
                raw_context = (raw_path.open("w+b") if tracker == "sam3"
                               else contextlib.nullcontext(None))
                with raw_context as raw:
                    carried_prefetch = None
                    carried_prefetch_staging = None
                    carried_prefetch_progress = None
                    for scene_index, scene in enumerate(clip_scenes):
                        cancelled(request)
                        scene_count = int(scene["endFrameExclusive"] - scene["startFrame"])
                        chunk_size = tracker_scene_chunk_size(
                            tracker,
                            sam3_width if tracker == "sam3" else media["width"],
                            sam3_height if tracker == "sam3" else media["height"],
                            scene_count)
                        context_frames = (min(SAM3_CONTEXT_FRAMES,
                                              chunk_size - 1)
                                          if tracker == "sam3" and
                                          scene_count > chunk_size else 0)
                        chunks = frame_chunks(scene_count, chunk_size,
                                              context_frames)
                        has_next_scene = scene_index + 1 < len(clip_scenes)
                        prefetch_stop = threading.Event()
                        extractor = (concurrent.futures.ThreadPoolExecutor(
                            max_workers=1, thread_name_prefix="sam2matting-extract")
                            if len(chunks) > 1 or has_next_scene else None)
                        prefetched = carried_prefetch
                        prefetched_staging = carried_prefetch_staging
                        prefetched_progress = carried_prefetch_progress
                        carried_prefetch = None
                        carried_prefetch_staging = None
                        carried_prefetch_progress = None

                        def paths_for(index):
                            suffix = (f"-chunk-{index + 1:04d}"
                                      if len(chunks) > 1 else "")
                            return (suffix,
                                    work / f"scene-{scene['id']}{suffix}",
                                    work / f"union-{scene['id']}{suffix}.float16")

                        def check_prefetch_cancelled():
                            if prefetch_stop.is_set():
                                raise InterruptedError(
                                    "SAM2Matting extraction prefetch stopped")
                            cancelled(request)

                        def check_carry_cancelled():
                            cancelled(request)

                        try:
                            for chunk_index, (input_offset, input_count,
                                              context, count) in enumerate(chunks):
                                cancelled(request)
                                suffix, scene_dir, union_path = paths_for(chunk_index)

                                def extraction_progress(chunk_frames):
                                    core_frames = min(count, max(
                                        0, chunk_frames - context))
                                    current_frames = completed_frames + core_frames
                                    current_units = completed_units + chunk_frames
                                    percent = (5 + 92 * current_units /
                                               max(1, total_units))
                                    chunk_text = (f", chunk {chunk_index + 1}/"
                                                  f"{len(chunks)}"
                                                  if len(chunks) > 1 else "")
                                    emit("scene-extraction", percent,
                                         f"Extracting scene {completed_scenes + 1}/"
                                         f"{total_scenes}{chunk_text} • "
                                         f"{current_frames}/{total_frames} frames")

                                extraction_progress(0)
                                if prefetched is None:
                                    if tracker == "sam3":
                                        prepare_sam3_chunk(
                                            source, scene_dir, union_path,
                                            int(scene["startFrame"]) + input_offset,
                                            input_count, sam3_width,
                                            sam3_height, media["fps"],
                                            extraction_progress,
                                            lambda: cancelled(request))
                                    else:
                                        extract_scene(
                                            source, scene_dir,
                                            int(scene["startFrame"]) + input_offset,
                                            input_count, media["fps"],
                                            extraction_progress,
                                            lambda: cancelled(request),
                                            sam2_frame_size)
                                else:
                                    while True:
                                        try:
                                            prefetched.result(timeout=0.25)
                                            break
                                        except concurrent.futures.TimeoutError:
                                            extraction_progress(
                                                prefetched_progress["frames"]
                                                if prefetched_progress is not None
                                                else 0)
                                    prefetched = None
                                    if prefetched_staging is not None:
                                        shutil.rmtree(scene_dir,
                                                      ignore_errors=True)
                                        os.replace(prefetched_staging, scene_dir)
                                        prefetched_staging = None
                                    extraction_progress(input_count)
                                extracted_count = (len(list(scene_dir.glob("*.png")))
                                                   if scene_dir.is_dir() else 0)
                                if extracted_count != input_count:
                                    raise RuntimeError(
                                        f"Prefetched scene {scene['id']} is incomplete: "
                                        f"found {extracted_count} of {input_count} "
                                        f"PNG frames in {scene_dir}")

                                if extractor is not None and chunk_index + 1 < len(chunks):
                                    next_offset, next_input_count, _, _ = \
                                        chunks[chunk_index + 1]
                                    _, next_dir, next_union_path = paths_for(
                                        chunk_index + 1)
                                    prefetched_progress = {"frames": 0}
                                    prefetched = extractor.submit(
                                        prepare_sam3_chunk, source, next_dir,
                                        next_union_path,
                                        int(scene["startFrame"]) + next_offset,
                                        next_input_count, sam3_width,
                                        sam3_height, media["fps"],
                                        lambda frames, state=prefetched_progress:
                                            state.__setitem__("frames", frames),
                                        check_prefetch_cancelled)
                                    prefetched_staging = None
                                elif extractor is not None and has_next_scene:
                                    next_scene = clip_scenes[scene_index + 1]
                                    next_scene_count = int(
                                        next_scene["endFrameExclusive"] -
                                        next_scene["startFrame"])
                                    next_chunk_size = tracker_scene_chunk_size(
                                        tracker,
                                        sam3_width if tracker == "sam3" else media["width"],
                                        sam3_height if tracker == "sam3" else media["height"],
                                        next_scene_count)
                                    next_context_frames = (min(
                                        SAM3_CONTEXT_FRAMES,
                                        next_chunk_size - 1)
                                        if tracker == "sam3" and
                                        next_scene_count > next_chunk_size else 0)
                                    next_chunks = frame_chunks(
                                        next_scene_count, next_chunk_size,
                                        next_context_frames)
                                    next_offset, next_input_count, _, _ = \
                                        next_chunks[0]
                                    next_suffix = ("-chunk-0001"
                                                   if len(next_chunks) > 1 else "")
                                    next_dir = work / \
                                        f"scene-{next_scene['id']}{next_suffix}"
                                    next_staging_dir = Path(
                                        str(next_dir) + ".prefetch")
                                    shutil.rmtree(next_staging_dir,
                                                  ignore_errors=True)
                                    next_union_path = work / \
                                        f"union-{next_scene['id']}{next_suffix}.float16"
                                    next_progress = {"frames": 0}
                                    if tracker == "sam3":
                                        carried_prefetch = extractor.submit(
                                            prepare_sam3_chunk, source,
                                            next_staging_dir,
                                            next_union_path,
                                            int(next_scene["startFrame"]) + next_offset,
                                            next_input_count, sam3_width,
                                            sam3_height, media["fps"],
                                            lambda frames, state=next_progress:
                                                state.__setitem__("frames", frames),
                                            check_carry_cancelled)
                                    else:
                                        carried_prefetch = extractor.submit(
                                            extract_scene, source,
                                            next_staging_dir,
                                            int(next_scene["startFrame"]) + next_offset,
                                            next_input_count, media["fps"],
                                            lambda frames, state=next_progress:
                                                state.__setitem__("frames", frames),
                                            check_carry_cancelled,
                                            sam2_frame_size)
                                    carried_prefetch_staging = next_staging_dir
                                    carried_prefetch_progress = next_progress

                                if tracker == "sam3":
                                    union = np.memmap(
                                        union_path, mode="r+",
                                        dtype=SAM3_UNION_DTYPE,
                                        shape=(input_count, sam3_height,
                                               sam3_width))
                                else:
                                    prompt = next(
                                        (item for item in request["prompts"]
                                         if item["sceneId"].lower() ==
                                         scene["id"].lower()), None)
                                    if prompt is None:
                                        raise RuntimeError(
                                            f"Scene {scene['id']} has no initial mask")
                                    local_prompt = int(
                                        prompt["promptFrame"] -
                                        scene["startFrame"])
                                    union_path = None
                                    union = Sam2SceneAlphaSink(
                                        work, f"{scene['id']}{suffix}",
                                        local_prompt, input_count,
                                        media["width"], media["height"],
                                        media["fps"])
                                write_submitted = False
                                sam2_sink_finished = False
                                try:
                                    with torch.inference_mode(), torch.autocast(
                                            "cuda", dtype=torch.bfloat16):
                                        if tracker == "sam3":
                                            tracking_base_units = (completed_units +
                                                                   input_count)
                                            process_sam3_scene(
                                                predictor, request, scene_dir, union,
                                                tracking_base_units, total_units,
                                                work / "previews" if request.get(
                                                    "generatePreviews") else None,
                                                context, count,
                                                completed_scenes + 1, total_scenes,
                                                completed_frames, total_frames)
                                        else:
                                            process_sam2_scene(
                                                predictor, request, scene, scene_dir,
                                                union, completed_units + input_count,
                                                 total_units, completed_scenes + 1,
                                                 total_scenes, completed_frames,
                                                 total_frames, media["width"],
                                                 media["height"],
                                                 work / "previews" if request.get(
                                                     "generatePreviews") else None)
                                    chunk_text = (f", chunk {chunk_index + 1}/"
                                                  f"{len(chunks)}"
                                                  if len(chunks) > 1 else "")
                                    completed_chunk_units = (completed_units + input_count *
                                                             (concept_passes + 1))
                                    if tracker == "sam3":
                                        emit("matting", 5 + 92 *
                                             completed_chunk_units /
                                             max(1, total_units),
                                             f"Writing progressive alpha for scene "
                                             f"{completed_scenes + 1}/{total_scenes}"
                                             f"{chunk_text}")
                                        if pending_write is not None:
                                            pending_write.result()
                                            pending_write = None
                                        pending_write = writer.submit(
                                            write_and_release_union, union, raw,
                                            union_path,
                                            lambda: cancelled(request), context)
                                    else:
                                        alpha_segments.extend(union.finish(
                                            lambda: cancelled(request)))
                                        sam2_sink_finished = True
                                        emit("matting", 5 + 92 *
                                             completed_chunk_units /
                                             max(1, total_units),
                                             f"Progressive alpha complete for scene "
                                             f"{completed_scenes + 1}/{total_scenes}")
                                    write_submitted = True
                                finally:
                                    if tracker == "sam3" and not write_submitted:
                                        union.flush()
                                        mapping = getattr(union, "_mmap", None)
                                        if mapping is not None:
                                            mapping.close()
                                        union_path.unlink(missing_ok=True)
                                        shutil.rmtree(scene_dir, ignore_errors=True)
                                    elif (tracker != "sam3" and
                                          not sam2_sink_finished):
                                        union.abort()
                                decoded_count += count
                                completed_frames += count
                                completed_units += input_count * (concept_passes + 1)
                        finally:
                            prefetch_stop.set()
                            if extractor is not None:
                                extractor.shutdown(wait=True, cancel_futures=True)
                        # Inference no longer needs this scene and any next-scene
                        # extraction is now fully closed. Keep directory cleanup on
                        # the main thread and outside the prefetch lifetime.
                        shutil.rmtree(scene_dir, ignore_errors=True)
                        completed_scenes += 1
                    if pending_write is not None:
                        pending_write.result()
                        pending_write = None
            finally:
                writer.shutdown(wait=True, cancel_futures=True)
            alpha_path = clip_dir / "alpha.mkv"
            foreground_path = clip_dir / "foreground.mp4"

            def alpha_encoding_progress(frames):
                emit("encoding", 5 + 92 *
                     (completed_units + frames) / max(1, total_units),
                     f"Encoding H.264 alpha • {frames}/{decoded_count} frames")

            def foreground_encoding_progress(frames):
                emit("encoding", 5 + 92 *
                     (completed_units + frames) / max(1, total_units),
                     f"Encoding foreground video • {frames}/{decoded_count} frames")

            if tracker == "sam3":
                encode_alpha(
                    raw_path, alpha_path, media["width"], media["height"],
                    media["fps"], decoded_count, alpha_encoding_progress,
                    lambda: cancelled(request), sam3_width, sam3_height)
            else:
                emit("encoding", 5 + 92 * completed_units /
                     max(1, total_units),
                     "Finalizing streamed H.264 alpha")
                concat_alpha_segments(
                    alpha_segments, alpha_path,
                    work / f"{clip['id']}-alpha-concat.txt")
            completed_units += decoded_count
            foreground_encoder, foreground_preset = encode_foreground(
                source, foreground_path,
                int(clip_scenes[0]["startFrame"]),
                media["fps"], decoded_count,
                foreground_encoding_progress,
                lambda: cancelled(request))
            completed_units += decoded_count
            emit("validation", 5 + 92 * completed_units /
                 max(1, total_units), "Validating foreground and alpha timing")
            alpha_info = probe_output(alpha_path)
            foreground_info = probe_output(foreground_path)
            if (alpha_info["frames"] != decoded_count or
                    foreground_info["frames"] != decoded_count or
                    alpha_info["width"] != foreground_info["width"] or
                    alpha_info["height"] != foreground_info["height"] or
                    not frame_rates_match(alpha_info["frameRate"],
                                          foreground_info["frameRate"]) or
                    alpha_info["codec"] != "h264" or
                    alpha_info["pixelFormat"] not in ("yuv420p", "yuvj420p") or
                    alpha_info["colorRange"] != "pc"):
                raise RuntimeError("Foreground/alpha validation failed")
            duration_ms = round(decoded_count / float(media["fps"]) * 1000)
            results.append({
                "clipId": clip["id"], "width": media["width"],
                "height": media["height"], "frameRate": media["frameRate"],
                "timeBase": alpha_info["timeBase"], "durationMs": duration_ms,
                "decodedFrameCount": decoded_count,
                "foregroundFrameCount": foreground_info["frames"],
                "alphaFrameCount": alpha_info["frames"],
                "foregroundCodec": foreground_info["codec"],
                "alphaCodec": alpha_info["codec"],
                "alphaPixelFormat": alpha_info["pixelFormat"],
                "tracker": tracker,
                "executionMode": "eager-bf16-sdpa-bounded",
                "encoder": foreground_encoder,
                "encoderPreset": foreground_preset,
                "firstTimestamp": 0,
                "lastTimestamp": max(0, decoded_count - 1),
            })
            raw_path.unlink(missing_ok=True)
        first = results[0]
        contract = {**first, "clips": results, "tracker": tracker,
                    "executionMode": "eager-bf16-sdpa-bounded"}
        temporary = output / "result.json.tmp"
        temporary.write_text(json.dumps(contract, indent=2), encoding="utf-8")
        os.replace(temporary, output / "result.json")
        emit("validation", 100, "SAM2Matting output passed strict media validation")
        return contract
    finally:
        shutil.rmtree(work, ignore_errors=True)
        if loaded is None:
            del predictor
            gc.collect()
            if torch.cuda.is_available():
                torch.cuda.empty_cache()


class PersistentHost:
    def __init__(self, runtime):
        self.runtime = Path(runtime)
        self.tracker = None
        self.predictor = None
        self.cancel_paths = {}

    def unload(self):
        import torch
        self.predictor = None
        self.tracker = None
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    def handle(self, command):
        kind = command.get("type")
        if command.get("protocolVersion") != 1:
            raise RuntimeError("Unsupported protocol version")
        if kind == "loadVariant":
            tracker = command["tracker"]
            if tracker != self.tracker:
                self.unload()
                self.predictor = load_variant(self.runtime, tracker)
                self.tracker = tracker
            return {"type": "variantLoaded", "tracker": tracker}
        if kind == "run":
            request = json.loads(Path(command["requestPath"]).read_text(encoding="utf-8"))
            if request["tracker"] != self.tracker:
                self.handle({"protocolVersion": 1, "type": "loadVariant",
                             "tracker": request["tracker"]})
            result = run_job(request, self.predictor)
            return {"type": "completed", "jobId": request["jobId"], "result": result}
        if kind == "cancel":
            path = self.cancel_paths.get(command.get("jobId"))
            if path:
                Path(path).write_text("cancel", encoding="utf-8")
            return {"type": "cancelRequested", "jobId": command.get("jobId")}
        if kind in {"openMaskSession", "loadMaskFrame", "predictMask", "closeMaskSession"}:
            raise RuntimeError("Mask-session commands require the interactive host adapter")
        if kind == "shutdown":
            self.unload()
            return {"type": "shutdown", "shutdown": True}
        raise RuntimeError(f"Unknown command: {kind}")


def host_main(runtime):
    host = PersistentHost(runtime)
    pending = ""
    for line in sys.stdin:
        pending += line
        try:
            command = json.loads(pending)
        except json.JSONDecodeError:
            # QuickPlayer versions before the compact-NDJSON fix used the
            # indented manifest serializer. Accumulate those physical lines so
            # an already-open editor can still restart this worker and retry.
            if len(pending) > 1024 * 1024:
                print(json.dumps({"type": "error",
                                  "message": "Worker command exceeds 1 MiB"}),
                      flush=True)
                pending = ""
            continue
        try:
            pending = ""
            response = host.handle(command)
            print(json.dumps(response), flush=True)
            if response.get("shutdown"):
                break
        except Exception as error:
            traceback.print_exc(file=sys.stderr)
            print(json.dumps({"type": "error", "message": str(error)}), flush=True)


def validate(runtime, full_hash=False):
    runtime = Path(runtime)
    marker(runtime)
    for tracker, (name, size, digest, _) in CHECKPOINTS.items():
        path = cheap_checkpoint_check(runtime, tracker, digest)
        if full_hash:
            emit("setup", 0, f"Hashing {name}")
            full_checkpoint_check(path, size, digest)
    source = runtime / "source" / "SAM2Matting"
    required = [source / "sam2" / "build_sam.py",
                source / "sam3" / "model_builder.py",
                source / "sam3" / "bpe_simple_vocab_16e6.txt.gz"]
    if not all(path.is_file() for path in required):
        raise RuntimeError("Pinned Fudan source contract is incomplete")
    if full_hash:
        import torch

        for tracker in CHECKPOINTS:
            emit("setup", 0, f"Validating checkpoint key contract for {tracker}")
            predictor = load_variant(runtime, tracker)
            checkpoint = torch.load(
                runtime / "checkpoints" / CHECKPOINTS[tracker][0],
                map_location="cpu", weights_only=True)
            state_dict = checkpoint.get("model", checkpoint)
            target = predictor.model if tracker == "sam3" else predictor
            missing, unexpected = target.load_state_dict(state_dict, strict=False)
            if missing or unexpected:
                raise RuntimeError(
                    f"Checkpoint key contract changed for {tracker}: "
                    f"missing={sorted(missing)!r}, unexpected={sorted(unexpected)!r}")
            del checkpoint, state_dict, target, predictor
            gc.collect()
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        from sam3.perflib.connected_components import connected_components
        sample = torch.zeros((1, 1, 8, 8), dtype=torch.bool, device="cuda")
        sample[:, :, 2:6, 2:6] = True
        labels, counts = connected_components(sample)
        if int(counts.max().item()) != 16 or int(labels.max().item()) < 1:
            raise RuntimeError("Native Windows connected-components validation failed")
    print(json.dumps({"status": "ok", "sourceRevision": SOURCE_REVISION,
                      "checkpointRevision": CHECKPOINT_REVISION}), flush=True)


def self_test():
    class Predictor:
        num_maskmem = 7
        memory_temporal_stride_for_eval = 1
        max_obj_ptrs_in_encoder = 16

    if sam2_tracking_history(Predictor()) != 15:
        raise RuntimeError("SAM2 tracking-history calculation failed")
    if not frame_rates_match("19001/317", "60000/1001") or \
            frame_rates_match("25/1", "30/1"):
        raise RuntimeError("SAM2 output frame-rate tolerance failed")
    state = {
        "output_dict_per_obj": {0: {
            "cond_frame_outputs": {0: {}},
            "non_cond_frame_outputs": {
                index: {"alpha": None} for index in range(100)},
        }},
        "frames_tracked_per_obj": {0: {index: {} for index in range(100)}},
    }
    prune_sam2_tracking_state(state, 99, False, 15, (1, 16))
    remaining = state["output_dict_per_obj"][0]["non_cond_frame_outputs"]
    if set(remaining) != set(range(1, 16)) | set(range(84, 100)):
        raise RuntimeError("Forward SAM2 state pruning failed")
    prune_sam2_tracking_state(state, 0, True, 15)
    if set(remaining) != set(range(1, 16)):
        raise RuntimeError("Reverse SAM2 state pruning failed")
    print(json.dumps({"status": "ok", "selfTest": "sam2-bounded-state"}),
          flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request")
    parser.add_argument("--host", action="store_true")
    parser.add_argument("--runtime")
    parser.add_argument("--validate", action="store_true")
    parser.add_argument("--full-hash", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    elif args.validate:
        validate(args.runtime, args.full_hash)
    elif args.host:
        host_main(args.runtime)
    elif args.request:
        request = json.loads(Path(args.request).read_text(encoding="utf-8"))
        run_job(request)
    else:
        parser.error("one of --request, --host, or --validate is required")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        emit("cleanup", 0, "SAM2Matting job cancelled")
        raise SystemExit(2)
    except Exception as error:
        fail(error)
        traceback.print_exc(file=sys.stderr)
        raise SystemExit(1)
