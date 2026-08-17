#!/usr/bin/env python3
"""Shared QuickPlayer SAM2 and foreground/alpha encoding helpers."""
import concurrent.futures, os, subprocess
import threading, time
from collections import OrderedDict
from pathlib import Path

from rvm_worker import emit, executable, probe, replace_preview

SAM2_COMMIT = "2b90b9f5ceec907a1c18123530e92e794ad901a4"
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
        raise RuntimeError(f"The required model file is missing or invalid: {path.name}")


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


def model_size(width, height):
    import math
    scale = min(1.0, 1024 / max(width, height),
                math.sqrt((1024 * 576) / (width * height)))
    return max(8, round(width * scale / 8) * 8), max(8, round(height * scale / 8) * 8)


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


