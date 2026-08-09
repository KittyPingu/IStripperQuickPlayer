#!/usr/bin/env python3
"""QuickPlayer VideoMaMa worker. Stdout is NDJSON progress only."""
import argparse, gc, json, os, shutil, subprocess, sys, time
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
                              cache_stats=None):
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
            def __len__(self): return len(paths)
            def __getitem__(self, index):
                if index in self.cache:
                    image = self.cache.pop(index)
                    self.cache[index] = image
                    if cache_stats is not None:
                        cache_stats["hits"] = cache_stats.get("hits", 0) + 1
                    return image
                if cache_stats is not None:
                    cache_stats["misses"] = cache_stats.get("misses", 0) + 1
                with Image.open(paths[index]) as opened:
                    decoded_at = time.perf_counter()
                    decoded = opened.convert("RGB")
                    decoded_seconds = time.perf_counter() - decoded_at
                    prepared_at = time.perf_counter()
                    pixels = np.array(decoded.resize((image_size, image_size)))
                if pixels.dtype != np.uint8:
                    raise RuntimeError(f"Unknown image dtype: {pixels.dtype}")
                image = torch.from_numpy(pixels / 255.0).permute(2, 0, 1)
                image.sub_(mean).div_(std)
                if cache_stats is not None:
                    cache_stats["decodePreprocessCount"] = \
                        cache_stats.get("decodePreprocessCount", 0) + 1
                    cache_stats["jpegDecodeSeconds"] = \
                        cache_stats.get("jpegDecodeSeconds", 0.0) + decoded_seconds
                    cache_stats["preprocessSeconds"] = \
                        cache_stats.get("preprocessSeconds", 0.0) + \
                        time.perf_counter() - prepared_at
                if offload_video_to_cpu and compute_device is not None and \
                        str(compute_device).startswith("cuda"):
                    image = image.pin_memory()
                elif not offload_video_to_cpu:
                    image = image.to(compute_device)
                if cache_limit_bytes > 0:
                    size = image.nelement() * image.element_size()
                    while self.cache and self.cache_bytes + size > cache_limit_bytes:
                        _, removed = self.cache.popitem(last=False)
                        self.cache_bytes -= removed.nelement() * removed.element_size()
                    if size <= cache_limit_bytes:
                        self.cache[index] = image
                        self.cache_bytes += size
                        if cache_stats is not None:
                            cache_stats["bytes"] = self.cache_bytes
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


def encoder(ffmpeg, source, output, start, duration, frame_rate, width, height):
    encoders = subprocess.check_output([ffmpeg, "-hide_banner", "-encoders"],
        text=True, errors="replace")
    nvenc = "h264_nvenc" in encoders and subprocess.run([ffmpeg, "-v", "error",
        "-f", "lavfi", "-i", "color=size=256x256:duration=0.1", "-frames:v", "1",
        "-c:v", "h264_nvenc", "-f", "null", "-"], stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL).returncode == 0
    video_codec = ["-c:v", "h264_nvenc", "-preset", "p7", "-tune", "hq", "-cq", "19"] \
        if nvenc else ["-c:v", "libx264", "-preset", "slow", "-crf", "18"]
    alpha_codec = ["-c:v", "h264_nvenc", "-preset", "p7", "-tune", "hq", "-cq", "10"] \
        if nvenc else ["-c:v", "libx264", "-preset", "medium", "-crf", "10"]
    return subprocess.Popen([ffmpeg, "-y", "-v", "warning", "-f", "rawvideo",
        "-pix_fmt", "rgba", "-s", f"{width}x{height}", "-r", frame_rate, "-i", "pipe:0",
        "-ss", f"{start:.6f}", "-t", f"{duration:.6f}", "-i", str(source),
        "-filter_complex", "[0:v]split=2[rgb][rgba];[rgb]format=yuv420p[vout];[rgba]alphaextract,format=yuv420p[aout]",
        "-map", "[vout]", "-map", "1:a:0?", *video_codec, "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-shortest", str(output / "foreground.mp4"),
        "-map", "[aout]", *alpha_codec, "-pix_fmt", "yuv420p",
        str(output / "alpha.mkv")], stdin=subprocess.PIPE)


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
        batch_size = max(1, min(args.batch_size, 12))
        emit("startup", 35, f"VideoMaMa model loaded; first {batch_size}-frame batch is running...")
        decode = subprocess.Popen([ffmpeg, "-v", "error", "-ss", f"{start:.6f}",
            "-i", str(source), "-t", f"{duration:.6f}", "-map", "0:v:0", "-vf",
            f"fps={frame_rate},scale={width}:{height}:flags=lanczos,setsar=1",
            "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"], stdout=subprocess.PIPE)
        encode = encoder(ffmpeg, source, output, start, duration, frame_rate, width, height)
        frame_bytes, count, last_preview = width * height * 3, 0, 0

        def read_source():
            value, received = bytearray(frame_bytes), 0
            view = memoryview(value)
            while received < frame_bytes:
                size = decode.stdout.readinto(view[received:])
                if not size: break
                received += size
            if received != frame_bytes:
                raise RuntimeError("source decoder ended before VideoMaMa output")
            return np.frombuffer(value, np.uint8).reshape(height, width, 3)

        def preview(source_frame, alpha, force=False):
            nonlocal last_preview
            now = time.monotonic()
            if not force and now - last_preview < .5: return
            write_preview(output, source_frame, alpha)
            last_preview = now

        last_source = last_alpha = None
        try:
            for offset in range(0, total, batch_size):
                chunk_files = frame_files[offset:offset + batch_size]
                cond = [Image.open(path).convert("RGB") for path in chunk_files]
                guides = [Image.open(masks / (path.stem + ".png")).convert("L").resize(
                    (model_width, model_height), Image.Resampling.NEAREST)
                    for path in chunk_files]
                mattes = pipeline.run(cond, guides, seed=42, mask_cond_mode="vae",
                                      fps=max(1, round(fps)))
                for matte in mattes:
                    source_frame = read_source()
                    alpha = np.asarray(matte.convert("L").resize(
                        (width, height), Image.Resampling.LANCZOS), dtype=np.uint8)
                    rgba = np.empty((height, width, 4), dtype=np.uint8)
                    rgba[:, :, :3], rgba[:, :, 3] = source_frame, alpha
                    output_bytes = memoryview(rgba).cast("B")
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
            if last_source is not None: preview(last_source, last_alpha, force=True)
            encode.stdin.close(); decode.stdout.close()
            if decode.wait() != 0: raise RuntimeError("source normalization failed")
            if encode.wait() != 0: raise RuntimeError("FFmpeg output encoding failed")
        except BaseException:
            decode.kill(); encode.kill(); raise
        (output / "result.json").write_text(json.dumps({"width": width, "height": height,
            "frameRate": frame_rate, "durationMs": round(count * 1000 / fps)}, indent=2))
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
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        assert model_size(1920, 1080) == (1024, 576)
        assert model_size(1080, 1920) == (576, 1024)
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
