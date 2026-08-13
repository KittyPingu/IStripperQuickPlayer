#!/usr/bin/env python3
"""QuickPlayer VideoMaMa worker. Stdout is NDJSON progress only."""
import argparse, concurrent.futures, gc, json, os, shutil, subprocess, sys
import threading, time
from collections import OrderedDict
from pathlib import Path

from rvm_worker import emit, executable, probe, replace_preview

VIDEOMAMA_COMMIT = "d5cce3e0ffe3b6429c147e658bb28bcfb576374c"
SAM2_COMMIT = "2b90b9f5ceec907a1c18123530e92e794ad901a4"
MODEL_BYTES = 6098728544
SVD_IMAGE_BYTES = 1264217240
SVD_VAE_BYTES = 195531910
SAM2_MODELS = {
    "base-plus": {
        "file": "sam2.1_hiera_base_plus.pt",
        "bytes": 323606802,
        "config": "configs/sam2.1/sam2.1_hiera_b+.yaml",
        "label": "SAM2.1 Hiera Base+",
    },
    "small": {
        "file": "sam2.1_hiera_small.pt",
        "bytes": 184416285,
        "config": "configs/sam2.1/sam2.1_hiera_s.yaml",
        "label": "SAM2.1 Hiera Small",
    },
    "tiny": {
        "file": "sam2.1_hiera_tiny.pt",
        "bytes": 156008466,
        "config": "configs/sam2.1/sam2.1_hiera_t.yaml",
        "label": "SAM2.1 Hiera Tiny",
    },
}
SAM2_VOS_CACHE_MARKER = "SAM2_VOS_TORCH_2_11_TRITON_3_6_READY"
SAM2_VOS_COMPILE_MODE = os.environ.get(
    "IQP_SAM2_COMPILE_MODE", "max-autotune")


def require_file(path, size):
    if not path.is_file() or path.stat().st_size != size:
        raise RuntimeError(f"VideoMaMa installation is incomplete: {path.name}")


def sam2_vos_optimized(torch, device):
    if device != "cuda": return False
    from torch.utils._triton import has_triton
    return has_triton()


def sam2_model(runtime, name="base-plus"):
    if name not in SAM2_MODELS:
        raise RuntimeError(f"Unknown SAM2 model: {name}")
    model = SAM2_MODELS[name]
    checkpoint = runtime / "checkpoints" / model["file"]
    require_file(checkpoint, model["bytes"])
    return model, checkpoint


def sam2_vos_cache_marker(runtime, model_name="base-plus", execution="vos",
                          compile_mode=None):
    mode = (compile_mode or SAM2_VOS_COMPILE_MODE).upper().replace("-", "_")
    model = model_name.upper().replace("-", "_")
    return runtime / "torchinductor-cache" / \
        f"{SAM2_VOS_CACHE_MARKER}_{model}_{execution.upper()}_{mode}"


def sam2_vos_uses_cudagraphs(mode=None):
    mode = mode or SAM2_VOS_COMPILE_MODE
    return mode in ("max-autotune", "reduce-overhead")


def load_sam2(runtime, torch, device="cuda", optimized=True,
              model_name="base-plus", encoder_compiled=False,
              compile_mode=None):
    os.environ.setdefault("TORCHINDUCTOR_CACHE_DIR",
                          str(runtime / "torchinductor-cache"))
    if (runtime / "SAM2_COMMIT").read_text().strip() != SAM2_COMMIT:
        raise RuntimeError("SAM2 commit validation failed; run setup again")
    model, checkpoint = sam2_model(runtime, model_name)
    from sam2.build_sam import build_sam2_video_predictor
    use_optimized = optimized and sam2_vos_optimized(torch, device)
    use_encoder_compile = encoder_compiled and not use_optimized and \
        sam2_vos_optimized(torch, device)
    selected_compile_mode = compile_mode or SAM2_VOS_COMPILE_MODE
    original_compile = torch.compile
    if (use_optimized or use_encoder_compile) and \
            selected_compile_mode != "max-autotune":
        def configured_compile(model, *args, **kwargs):
            if kwargs.get("mode") == "max-autotune":
                kwargs["mode"] = selected_compile_mode
            return original_compile(model, *args, **kwargs)
        torch.compile = configured_compile
    try:
        overrides = ["++model.compile_image_encoder=True"] \
            if use_encoder_compile else []
        return build_sam2_video_predictor(
            model["config"], str(checkpoint), device=device,
            vos_optimized=use_optimized, hydra_overrides_extra=overrides)
    finally:
        torch.compile = original_compile


def use_on_demand_sam2_frames(frame_folder, torch, cache_limit_bytes=0,
                              cache_stats=None, prefetch_depth=2,
                              prefetch_to_device=False):
    import sam2.sam2_video_predictor as predictor_module
    import numpy as np
    from PIL import Image
    frame_folder = frame_folder.resolve()
    original_loader = predictor_module.load_video_frames

    def load_video_frames(video_path, image_size, offload_video_to_cpu,
                          img_mean=(0.485, 0.456, 0.406),
                          img_std=(0.229, 0.224, 0.225),
                          async_loading_frames=False, compute_device=None):
        if Path(video_path).resolve() != frame_folder:
            return original_loader(video_path, image_size, offload_video_to_cpu,
                img_mean, img_std, async_loading_frames, compute_device)
        paths = sorted(frame_folder.glob("*.jpg"), key=lambda path: int(path.stem))
        mean = torch.tensor(img_mean, dtype=torch.float32)[:, None, None]
        std = torch.tensor(img_std, dtype=torch.float32)[:, None, None]

        class Frames:
            def __init__(self):
                self.cache = OrderedDict()
                self.cache_bytes = 0
                self.lock = threading.RLock()
                self.futures = {}
                self.last_index = None
                self.direction = 1
                self.prefetch_depth = max(0, int(prefetch_depth))
                self.executor = concurrent.futures.ThreadPoolExecutor(
                    max_workers=1, thread_name_prefix="SAM-frame-prefetch") \
                    if self.prefetch_depth else None
                self.upload_stream = torch.cuda.Stream(device=compute_device) \
                    if prefetch_to_device and compute_device is not None and \
                    str(compute_device).startswith("cuda") else None
            def __len__(self): return len(paths)

            def _count(self, name, value=1):
                if cache_stats is not None:
                    with self.lock:
                        cache_stats[name] = cache_stats.get(name, 0) + value

            def _prepare(self, index):
                with Image.open(paths[index]) as opened:
                    decoded_at = time.perf_counter()
                    decoded = opened.convert("RGB")
                    decoded_seconds = time.perf_counter() - decoded_at
                    prepared_at = time.perf_counter()
                    pixels = np.array(decoded.resize((image_size, image_size)))
                if pixels.dtype != np.uint8:
                    raise RuntimeError(f"Unknown image dtype: {pixels.dtype}")
                image = torch.from_numpy(pixels).permute(2, 0, 1).to(torch.float32)
                image.div_(255.0).sub_(mean).div_(std)
                self._count("decodePreprocessCount")
                self._count("jpegDecodeSeconds", decoded_seconds)
                self._count("preprocessSeconds", time.perf_counter() - prepared_at)
                if self.upload_stream is not None:
                    image = image.pin_memory()
                    uploaded_at = time.perf_counter()
                    with torch.cuda.device(compute_device), \
                            torch.cuda.stream(self.upload_stream):
                        uploaded = image.to(compute_device, non_blocking=True)
                        completed = torch.cuda.Event()
                        completed.record(self.upload_stream)
                    completed.synchronize()
                    image = uploaded
                    self._count("gpuUploadCount")
                    self._count("gpuUploadBytes", image.nelement() * image.element_size())
                    self._count("gpuUploadSeconds", time.perf_counter() - uploaded_at)
                elif offload_video_to_cpu and compute_device is not None and \
                        str(compute_device).startswith("cuda"):
                    image = image.pin_memory()
                elif not offload_video_to_cpu:
                    image = image.to(compute_device)
                return image

            def _remember(self, index, image):
                effective_limit = min(cache_limit_bytes, 128 * 1024 ** 2) \
                    if self.upload_stream is not None else cache_limit_bytes
                if effective_limit <= 0:
                    return
                size = image.nelement() * image.element_size()
                with self.lock:
                    while self.cache and self.cache_bytes + size > effective_limit:
                        _, removed = self.cache.popitem(last=False)
                        self.cache_bytes -= removed.nelement() * removed.element_size()
                    if size <= effective_limit:
                        self.cache[index] = image
                        self.cache_bytes += size
                        if cache_stats is not None:
                            cache_stats["bytes"] = self.cache_bytes

            def _schedule(self, index):
                if self.executor is None:
                    return
                if self.last_index is not None and index != self.last_index:
                    self.direction = 1 if index > self.last_index else -1
                self.last_index = index
                wanted = []
                for distance in range(1, self.prefetch_depth + 1):
                    candidate = index + self.direction * distance
                    if 0 <= candidate < len(paths):
                        wanted.append(candidate)
                with self.lock:
                    for stale in list(self.futures):
                        if stale not in wanted:
                            self.futures.pop(stale).cancel()
                    for candidate in wanted:
                        if candidate not in self.cache and candidate not in self.futures:
                            self.futures[candidate] = self.executor.submit(
                                self._prepare, candidate)
                            self._count("prefetchScheduled")

            def __getitem__(self, index):
                with self.lock:
                    if index in self.cache:
                        image = self.cache.pop(index)
                        self.cache[index] = image
                    else:
                        image = None
                    future = self.futures.pop(index, None)
                if image is not None:
                    if cache_stats is not None:
                        self._count("hits")
                    self._schedule(index)
                    return image
                self._count("misses")
                if future is not None:
                    waited = time.perf_counter()
                    image = future.result()
                    self._count("prefetchHits")
                    self._count("prefetchWaitSeconds", time.perf_counter() - waited)
                else:
                    self._count("synchronousLoads")
                    image = self._prepare(index)
                self._remember(index, image)
                self._schedule(index)
                return image

        with Image.open(paths[0]) as first:
            width, height = first.size
        return Frames(), height, width

    predictor_module.load_video_frames = load_video_frames


def load_videomama(runtime, torch):
    if (runtime / "VIDEOMAMA_COMMIT").read_text().strip() != VIDEOMAMA_COMMIT:
        raise RuntimeError("VideoMaMa commit validation failed; run setup again")
    source = runtime / "videomama"
    base = runtime / "videomama-base"
    model = runtime / "videomama-model"
    require_file(base / "image_encoder" / "model.fp16.safetensors", SVD_IMAGE_BYTES)
    require_file(base / "vae" / "diffusion_pytorch_model.fp16.safetensors", SVD_VAE_BYTES)
    require_file(model / "unet" / "diffusion_pytorch_model.safetensors", MODEL_BYTES)
    sys.path.insert(0, str(source))
    from pipeline_svd_mask import VideoInferencePipeline
    return VideoInferencePipeline(str(base), str(model), device="cuda",
                                  weight_dtype=torch.float16)


def model_size(width, height):
    import math
    scale = min(1.0, 1024 / max(width, height),
                math.sqrt((1024 * 576) / (width * height)))
    return max(8, round(width * scale / 8) * 8), max(8, round(height * scale / 8) * 8)


def gpu_policy_key(torch):
    properties = torch.cuda.get_device_properties(0)
    return f"{properties.name}|{properties.total_memory}"


def calibrated_batch_size(runtime, torch):
    try:
        policy = json.loads((runtime / "videomama-performance-policy-v1.json").read_text())
        value = policy.get("entries", {}).get(gpu_policy_key(torch), {}) \
            .get("safeBatchSize")
        return max(1, min(int(value), 12))
    except (OSError, ValueError, TypeError):
        return None


def safe_batch_size(runtime, torch, requested):
    """Keep enough headroom for Windows composition and both NVENC sessions."""
    calibrated = calibrated_batch_size(runtime, torch)
    maximum = calibrated if calibrated is not None else safe_batch_for_memory(
        torch.cuda.get_device_properties(0).total_memory, requested)
    return max(1, min(requested, maximum))


def safe_batch_for_memory(total, requested):
    gib = total / 1024 ** 3
    maximum = 1 if gib < 20 else 2 if gib < 32 else 4
    return max(1, min(requested, maximum))


def auto_batch_policy_key(torch, width, height):
    return f"{gpu_policy_key(torch)}|videomama|{width}x{height}"


def load_auto_batch(runtime, torch, width, height):
    try:
        policy = json.loads((runtime / "videomama-performance-policy-v1.json").read_text())
        value = policy.get("entries", {}).get(
            auto_batch_policy_key(torch, width, height), {}).get("safeBatchSize")
        return max(1, min(int(value), 12))
    except (OSError, ValueError, TypeError):
        return None


def save_auto_batch(runtime, torch, width, height, batch):
    path = runtime / "videomama-performance-policy-v1.json"
    try:
        try:
            policy = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError, TypeError):
            policy = {"schemaVersion": 1, "entries": {}}
        entry = policy.setdefault("entries", {}).setdefault(
            auto_batch_policy_key(torch, width, height), {})
        entry["safeBatchSize"] = int(batch)
        entry["updatedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        temporary = path.with_suffix(path.suffix + ".tmp")
        temporary.write_text(json.dumps(policy, indent=2), encoding="utf-8")
        os.replace(temporary, path)
    except OSError as error:
        print(f"Auto batch policy warning: {error}", file=sys.stderr, flush=True)


def write_preview(output, source, alpha):
    import numpy as np
    from PIL import Image
    height, width = alpha.shape
    yy, xx = np.indices((height, width))
    checker = np.where((((xx // 24) + (yy // 24)) & 1)[..., None],
                       190, 125).astype(np.uint8)
    opacity = alpha[..., None].astype(np.float32) / 255
    composite = np.rint(source * opacity + checker * (1 - opacity)).astype(np.uint8)
    for name, data in (("preview-source.jpg", source),
                       ("preview-composite.jpg", composite)):
        temporary = output / (name + ".tmp")
        Image.fromarray(data, "RGB").save(temporary, "JPEG", quality=86)
        replace_preview(temporary, output / name)


def extract_frames(ffmpeg, source, folder, start, duration, frame_rate,
                   width, height, total):
    command = [ffmpeg, "-y", "-v", "error", "-ss", f"{start:.6f}", "-i", str(source),
        "-t", f"{duration:.6f}", "-map", "0:v:0", "-vf",
        f"fps={frame_rate},scale={width}:{height}:flags=lanczos,setsar=1",
        "-q:v", "2", str(folder / "%08d.jpg"), "-progress", "pipe:1", "-nostats"]
    process = subprocess.Popen(command, stdout=subprocess.PIPE, text=True)
    for line in process.stdout:
        if line.startswith("frame="):
            count = int(line.partition("=")[2])
            emit("extract", min(15, count * 15 / total),
                 f"Extracted {count}/{total} source frames")
    if process.wait() != 0:
        raise RuntimeError("VideoMaMa source-frame extraction failed")


def propagate_masks(runtime, frame_files, mask_path, mask_folder, output, total, torch):
    import numpy as np
    from PIL import Image
    first_size = Image.open(frame_files[0]).size
    initial = np.asarray(Image.open(mask_path).convert("L").resize(
        first_size, Image.Resampling.NEAREST)) >= 128
    write_preview(output,
        np.asarray(Image.open(frame_files[0]).convert("RGB")), initial.astype(np.uint8) * 255)
    emit("tracking", 15, "Loading SAM2 mask tracking...")
    predictor = load_sam2(runtime, torch)
    use_on_demand_sam2_frames(frame_files[0].parent, torch)
    mark_step = torch.compiler.cudagraph_mark_step_begin \
        if sam2_vos_uses_cudagraphs() else None
    if mark_step: mark_step()
    state = predictor.init_state(str(frame_files[0].parent),
        offload_video_to_cpu=True, offload_state_to_cpu=True)
    if mark_step: mark_step()
    predictor.add_new_mask(state, frame_idx=0, obj_id=1, mask=initial)
    optimized = sam2_vos_optimized(torch, "cuda")
    first_compile = optimized and not sam2_vos_cache_marker(runtime).is_file()
    if optimized:
        emit("tracking", 15, "Compiling optimized SAM2 (one-time cache build; "
             "later processing reuses it). This may take several minutes..." if first_compile
             else "Loading cached optimized SAM2; recording this worker's CUDA Graphs...")
    count, last_preview = 0, 0
    iterator = iter(predictor.propagate_in_video(state))
    while True:
        if mark_step: mark_step()
        try: frame_index, object_ids, logits = next(iterator)
        except StopIteration: break
        ids = object_ids.tolist() if hasattr(object_ids, "tolist") else list(object_ids)
        if 1 not in ids:
            raise RuntimeError(f"SAM2 lost the selected foreground at frame {frame_index + 1}")
        mask = (logits[ids.index(1)] > 0).squeeze().byte().cpu().numpy() * 255
        Image.fromarray(mask, "L").save(mask_folder / f"{frame_index + 1:08d}.png")
        count += 1
        now = time.monotonic()
        if count == 1 or now - last_preview >= .5:
            source_frame = np.asarray(Image.open(frame_files[frame_index]).convert("RGB"))
            write_preview(output, source_frame, mask)
            last_preview = now
        emit("tracking", 15 + count * 20 / total,
             f"Tracked {count}/{total} foreground masks")
    del state, predictor
    gc.collect(); torch.cuda.empty_cache()
    if count != len(frame_files):
        raise RuntimeError("SAM2 returned a different number of masks than source frames")
    if optimized: sam2_vos_cache_marker(runtime).touch()


def output_codecs(ffmpeg, width, height, force_software=False, preset="p5"):
    encoders = subprocess.check_output([ffmpeg, "-hide_banner", "-encoders"],
        text=True, errors="replace")
    nvenc = not force_software and width >= 256 and height >= 128 and \
        "h264_nvenc" in encoders and subprocess.run([ffmpeg, "-v", "error",
        "-f", "lavfi", "-i", "color=size=256x256:duration=0.1", "-frames:v", "1",
        "-c:v", "h264_nvenc", "-f", "null", "-"], stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL).returncode == 0
    video_codec = ["-c:v", "h264_nvenc", "-preset", preset, "-tune", "hq", "-cq", "19"] \
        if nvenc else ["-c:v", "libx264", "-preset", "slow", "-crf", "18"]
    alpha_codec = ["-c:v", "h264_nvenc", "-preset", preset, "-tune", "hq", "-cq", "10"] \
        if nvenc else ["-c:v", "libx264", "-preset", "medium", "-crf", "10"]
    return video_codec, alpha_codec, "h264_nvenc" if nvenc else "libx264"


def source_and_foreground_encoder(ffmpeg, source, output, start, duration,
                                  frame_rate, width, height, total, video_codec,
                                  raw_width=None, raw_height=None,
                                  raw_scale="bilinear"):
    """Decode source RGB once, sharing it with Python and foreground encoding."""
    raw_width = width if raw_width is None else raw_width
    raw_height = height if raw_height is None else raw_height
    normalized = (f"[0:v:0]fps={frame_rate},scale={width}:{height}:flags=lanczos,"
                  "setsar=1,split=2[video][python]")
    if (raw_width, raw_height) == (width, height):
        normalized += ";[python]null[raw]"
    else:
        # Match the former Python path: convert the normalized frame to RGB,
        # then resize that RGB image before inference.
        normalized += (f";[python]format=rgb24,scale={raw_width}:{raw_height}:"
                       f"flags={raw_scale}[raw]")
    return subprocess.Popen([ffmpeg, "-y", "-v", "warning", "-ss", f"{start:.6f}",
        "-i", str(source), "-filter_complex", normalized,
        "-map", "[raw]", "-t", f"{duration:.6f}", "-frames:v", str(total),
        "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1",
        "-map", "[video]", "-map", "0:a:0?", "-t", f"{duration:.6f}",
        "-frames:v", str(total), *video_codec, "-pix_fmt", "yuv420p",
        "-r", frame_rate, "-fps_mode", "cfr", "-c:a", "aac",
        str(output / "foreground.mp4")], stdout=subprocess.PIPE)


def alpha_encoder(ffmpeg, output, frame_rate, width, height, total,
                  alpha_width, alpha_height, alpha_codec, scale="bilinear"):
    """Encode low-resolution gray masks, scaling them inside FFmpeg."""
    filters = []
    if (alpha_width, alpha_height) != (width, height):
        filters.append(f"scale={width}:{height}:flags={scale}")
    filters.append("format=yuv420p")
    return subprocess.Popen([ffmpeg, "-y", "-v", "warning", "-f", "rawvideo",
        "-pix_fmt", "gray", "-s", f"{alpha_width}x{alpha_height}",
        "-r", frame_rate, "-i", "pipe:0", "-vf", ",".join(filters),
        "-frames:v", str(total), *alpha_codec, "-pix_fmt", "yuv420p",
        "-r", frame_rate, "-fps_mode", "cfr", str(output / "alpha.mkv")],
        stdin=subprocess.PIPE)


def process(args):
    import numpy as np
    import torch
    from PIL import Image
    if not torch.cuda.is_available():
        raise RuntimeError("VideoMaMa requires an NVIDIA CUDA GPU")
    runtime, source, output = args.runtime.resolve(), args.source.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    width, height, frame_rate, fps, source_duration = probe(source)
    start = args.start_ms / 1000
    end = source_duration if args.end_ms is None else args.end_ms / 1000
    if start < 0 or end <= start or end > source_duration + .1:
        raise RuntimeError("clip range is outside the source video")
    duration = min(end, source_duration) - start
    expected = max(1, round(duration * fps))
    work = output / ".videomama-work"
    frames = work / "frames"
    masks = args.mask_folder.resolve()
    shutil.rmtree(work, ignore_errors=True)
    frames.mkdir(parents=True)
    ffmpeg = executable("ffmpeg")
    model_width, model_height = model_size(width, height)
    try:
        extract_frames(ffmpeg, source, frames, start, duration, frame_rate,
                       model_width, model_height, expected)
        frame_files = sorted(frames.glob("*.jpg"))
        if not frame_files: raise RuntimeError("The source video has no frames")
        total = len(frame_files)
        mask_files = sorted(masks.glob("*.png"))
        if len(mask_files) != total:
            raise RuntimeError(
                f"SAM2 mask count ({len(mask_files)}) does not match source frames ({total})")
        emit("startup", 35, "Loading VideoMaMa diffusion model...")
        pipeline = load_videomama(runtime, torch)
        pipeline.unet.enable_forward_chunking(chunk_size=1, dim=1)
        auto_batch = args.batch_size == 0
        requested_batch = 0 if auto_batch else max(1, min(args.batch_size, 12))
        free_memory, total_memory = torch.cuda.mem_get_info()
        if auto_batch:
            learned = load_auto_batch(runtime, torch, model_width, model_height)
            heuristic = safe_batch_for_memory(min(free_memory, total_memory), 12)
            healthy_headroom = free_memory >= max(2 * 1024 ** 3,
                                                   total_memory * .20)
            batch_size = learned if learned and healthy_headroom \
                else heuristic
            emit("startup", 35, f"Auto batch starts at {batch_size} for "
                 f"{model_width}x{model_height} VideoMaMa inference")
        else:
            batch_size = safe_batch_size(runtime, torch, requested_batch)
        if not auto_batch and batch_size < requested_batch:
            emit("startup", 35, f"VideoMaMa batch reduced from {requested_batch} to "
                 f"{batch_size} to preserve GPU memory headroom")
        emit("startup", 35, f"VideoMaMa model loaded; first {batch_size}-frame batch is running...")
        video_codec, alpha_codec, encoder_name = output_codecs(
            ffmpeg, width, height, preset=args.encoder_preset)
        emit("startup", 35, f"Encoding with {encoder_name} {args.encoder_preset if encoder_name == 'h264_nvenc' else 'fallback'}")
        decode = source_and_foreground_encoder(ffmpeg, source, output, start,
            duration, frame_rate, width, height, total, video_codec,
            model_width, model_height)
        encode = alpha_encoder(ffmpeg, output, frame_rate, width, height, total,
            model_width, model_height, alpha_codec, scale="lanczos")
        frame_bytes, count, last_preview = model_width * model_height * 3, 0, 0

        def read_source():
            value, received = bytearray(frame_bytes), 0
            view = memoryview(value)
            while received < frame_bytes:
                size = decode.stdout.readinto(view[received:])
                if not size: break
                received += size
            if received != frame_bytes:
                raise RuntimeError("source decoder ended before VideoMaMa output")
            return np.frombuffer(value, np.uint8).reshape(
                model_height, model_width, 3)

        def preview(source_frame, alpha, force=False):
            nonlocal last_preview
            now = time.monotonic()
            if not force and now - last_preview < .5: return
            if source_frame.shape[:2] != alpha.shape:
                source_frame = np.asarray(Image.fromarray(source_frame, "RGB").resize(
                    (alpha.shape[1], alpha.shape[0]), Image.Resampling.BILINEAR))
            write_preview(output, source_frame, alpha)
            last_preview = now

        last_source = last_alpha = None
        try:
            offset, stable_batches = 0, 0
            while offset < total:
                chunk_files = frame_files[offset:offset + batch_size]
                cond = [Image.open(path).convert("RGB") for path in chunk_files]
                guides = [Image.open(masks / (path.stem + ".png")).convert("L").resize(
                    (model_width, model_height), Image.Resampling.NEAREST)
                    for path in chunk_files]
                try:
                    mattes = pipeline.run(cond, guides, seed=42, mask_cond_mode="vae",
                                          fps=max(1, round(fps)))
                except torch.OutOfMemoryError:
                    if len(chunk_files) <= 1:
                        raise
                    batch_size = max(1, len(chunk_files) - 1) if auto_batch else \
                        max(1, len(chunk_files) // 2)
                    stable_batches = 0
                    torch.cuda.empty_cache()
                    emit("inference", 35 + count * 64 / total,
                         f"GPU memory limit; batch reduced to {batch_size}")
                    continue
                for matte in mattes:
                    source_frame = read_source()
                    alpha = np.asarray(matte.convert("L").resize(
                        (model_width, model_height), Image.Resampling.LANCZOS),
                        dtype=np.uint8)
                    output_bytes = memoryview(alpha).cast("B")
                    while output_bytes:
                        written = encode.stdin.write(output_bytes)
                        if not written:
                            raise RuntimeError("FFmpeg output encoder closed unexpectedly")
                        output_bytes = output_bytes[written:]
                    count += 1
                    last_source, last_alpha = source_frame, alpha
                    preview(source_frame, alpha)
                    emit("inference", 35 + count * 64 / total,
                         f"Processed {count}/{total} frames")
                offset += len(chunk_files)
                if auto_batch and len(chunk_files) == batch_size:
                    stable_batches += 1
                    free_memory, total_memory = torch.cuda.mem_get_info()
                    if stable_batches >= 2 and free_memory > max(2 * 1024 ** 3,
                            total_memory * .25) and batch_size < 12:
                        batch_size += 1
                        stable_batches = 0
                        emit("inference", 35 + count * 64 / total,
                             f"Auto batch increased to {batch_size} after healthy "
                             "GPU memory measurements")
            if last_source is not None: preview(last_source, last_alpha, force=True)
            encode.stdin.close(); decode.stdout.close()
            if decode.wait() != 0:
                raise RuntimeError("source normalization/foreground encoding failed")
            if encode.wait() != 0: raise RuntimeError("FFmpeg alpha encoding failed")
        except BaseException:
            decode.kill(); encode.kill(); raise
        (output / "result.json").write_text(json.dumps({"width": width, "height": height,
            "frameRate": frame_rate, "durationMs": round(count * 1000 / fps),
            "requestedSequenceChunk": requested_batch,
            "effectiveSequenceChunk": batch_size}, indent=2))
        if auto_batch:
            save_auto_batch(runtime, torch, model_width, model_height, batch_size)
        emit("complete", 100, "VideoMaMa foreground and alpha are ready for preview")
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--mask-folder", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--batch-size", type=int, default=3)
    parser.add_argument("--encoder-preset", choices=tuple(f"p{i}" for i in range(1, 8)),
                        default="p5")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        assert model_size(1920, 1080) == (1024, 576)
        assert model_size(1080, 1920) == (576, 1024)
        assert safe_batch_for_memory(16 * 1024 ** 3, 12) == 1
        assert safe_batch_for_memory(24 * 1024 ** 3, 12) == 2
        assert safe_batch_for_memory(48 * 1024 ** 3, 12) == 4
        assert sam2_vos_uses_cudagraphs("max-autotune")
        assert not sam2_vos_uses_cudagraphs("max-autotune-no-cudagraphs")
        print("VideoMaMa worker self-test passed")
    elif not all((args.source, args.mask_folder, args.output, args.runtime)):
        parser.error("--source, --mask-folder, --output and --runtime are required")
    else:
        process(args)


if __name__ == "__main__":
    try: main()
    except Exception as error:
        emit("error", 0, str(error)); raise
