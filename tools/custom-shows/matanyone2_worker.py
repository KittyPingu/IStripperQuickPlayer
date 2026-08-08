#!/usr/bin/env python3
"""QuickPlayer MatAnyone 2 worker. Stdout is NDJSON progress only."""
import argparse, json, os, subprocess, sys, tempfile, time
from pathlib import Path

from rvm_worker import digest, emit, executable, fast_fp16, probe, replace_preview

COMMIT = "0079197acd6d16a741f71558809c06c586c579e0"
WEIGHTS_HASH = "5e9821e4087231427376b437c85bb6e072b41e582314f06fd524f75bc4af5914"

def processing_size(width, height, max_size):
    if max_size <= 0 or min(width, height) <= max_size:
        return width, height
    scale = max_size / min(width, height)
    return int(width * scale), int(height * scale)

def mask_frame_index(mask_frame_ms, start_ms, fps, total):
    return max(0, min(total - 1,
        round(max(0, mask_frame_ms - start_ms) * fps / 1000)))

def load_model(runtime):
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
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = get_matanyone2_model(str(weights), device)
    return torch, InferenceCore, model, device

def extract_earlier_frames(ffmpeg, source, destination, start, frame_rate,
                           width, height, frame_count):
    if frame_count <= 0:
        return
    emit("preparing", 0,
         f"Preparing {frame_count + 1} frames for backward propagation...")
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
    if not (destination / f"{frame_count:08d}.jpg").is_file():
        raise RuntimeError("The selected MatAnyone mask frame could not be decoded")

def process(args):
    import numpy as np
    from PIL import Image

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
    selected_ms = args.start_ms if args.mask_frame_ms is None else args.mask_frame_ms
    selected_index = mask_frame_index(selected_ms, args.start_ms, fps, total)
    ffmpeg = executable("ffmpeg")

    with tempfile.TemporaryDirectory(prefix=".matanyone-", dir=output) as work_value:
        work = Path(work_value)
        prepared = work / "source"
        backward_alpha = work / "alpha"
        prepared.mkdir()
        backward_alpha.mkdir()
        if selected_index:
            extract_earlier_frames(ffmpeg, source, prepared, start, frame_rate,
                                   process_width, process_height, selected_index)

        progress_base = 5 if selected_index else 0
        emit("startup", progress_base, "Loading MatAnyone 2...")
        torch, InferenceCore, model, device = load_model(args.runtime.resolve())
        fp16 = device.type == "cuda" and fast_fp16(torch.cuda.get_device_capability(device))
        emit("startup", progress_base, f"MatAnyone 2 inference at {process_width}x{process_height}; "
             f"output remains {width}x{height}; initial mask frame {selected_index + 1}/{total}")

        def new_processor():
            return InferenceCore(model, cfg=model.cfg, device=device)

        def tensor(frame):
            if (frame.shape[1], frame.shape[0]) != (process_width, process_height):
                frame = np.asarray(Image.fromarray(frame, "RGB").resize(
                    (process_width, process_height), Image.Resampling.BILINEAR))
            return torch.from_numpy(frame.copy()).permute(2, 0, 1).to(
                device=device, dtype=torch.float32).div_(255)

        mask = Image.open(args.mask).convert("L").resize(
            (process_width, process_height), Image.Resampling.NEAREST)
        mask_tensor = torch.from_numpy(np.asarray(mask, dtype=np.uint8).copy()).float().to(device)

        def warm_up(processor, frame):
            frame_tensor = tensor(frame)
            with torch.inference_mode(), torch.amp.autocast(
                    device_type=device.type, enabled=fp16):
                processor.step(frame_tensor, mask_tensor, objects=[1])
                for _ in range(11):
                    output_prob = processor.step(frame_tensor, first_frame_pred=True)
            return output_prob

        def alpha_from_prob(processor, output_prob):
            return processor.output_prob_to_mask(output_prob).clamp(0, 1).mul(
                255).byte().cpu().numpy()

        last_preview = 0
        last_frame = last_alpha = None
        checker_cache = {}

        def save_preview(frame, alpha, force=False):
            nonlocal last_preview
            now = time.monotonic()
            if not force and now - last_preview < .5:
                return
            preview_height, preview_width = frame.shape[:2]
            if alpha.shape != (preview_height, preview_width):
                alpha = np.asarray(Image.fromarray(alpha, "L").resize(
                    (preview_width, preview_height), Image.Resampling.BILINEAR))
            shape = (preview_height, preview_width)
            checker = checker_cache.get(shape)
            if checker is None:
                yy, xx = np.indices(shape)
                checker = np.where((((xx // 24) + (yy // 24)) & 1)[..., None],
                                   190, 125).astype(np.uint8)
                checker_cache[shape] = checker
            opacity = alpha[..., None].astype(np.float32) / 255
            composite = np.rint(frame * opacity + checker * (1 - opacity)).astype(np.uint8)
            for name, data in (("preview-source.jpg", frame),
                               ("preview-composite.jpg", composite)):
                temporary = output / (name + ".tmp")
                Image.fromarray(data, "RGB").save(temporary, "JPEG", quality=86)
                replace_preview(temporary, output / name)
            last_preview = now

        work_total = total + selected_index
        completed_work = 0
        def work_percent():
            return min(99, progress_base + completed_work *
                       (99 - progress_base) / work_total)
        if selected_index:
            backward = new_processor()
            selected_frame = np.asarray(Image.open(
                prepared / f"{selected_index:08d}.jpg").convert("RGB"))
            emit("inference", progress_base,
                 "Warming up MatAnyone 2 on the selected middle frame...")
            warm_up(backward, selected_frame)
            for index in range(selected_index - 1, -1, -1):
                frame_path = prepared / f"{index:08d}.jpg"
                frame = np.asarray(Image.open(frame_path).convert("RGB"))
                with torch.inference_mode(), torch.amp.autocast(
                        device_type=device.type, enabled=fp16):
                    output_prob = backward.step(tensor(frame))
                alpha = alpha_from_prob(backward, output_prob)
                Image.fromarray(alpha, "L").save(backward_alpha / f"{index:08d}.png")
                save_preview(frame, alpha)
                frame_path.unlink(missing_ok=True)
                completed_work += 1
                emit("inference", work_percent(),
                     f"Propagated backward {selected_index - index}/{selected_index} frames")
            del backward

        decode = encode = None
        count = 0
        try:
            decode = subprocess.Popen([ffmpeg, "-v", "error", "-ss", f"{start:.6f}",
                "-i", str(source), "-t", f"{duration:.6f}", "-map", "0:v:0",
                "-vf", f"fps={frame_rate},scale={width}:{height}:flags=lanczos,setsar=1",
                "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"], stdout=subprocess.PIPE)
            encoders = subprocess.check_output([ffmpeg, "-hide_banner", "-encoders"],
                                               text=True, errors="replace")
            nvenc = width >= 256 and height >= 128 and "h264_nvenc" in encoders and subprocess.run(
                [ffmpeg, "-v", "error", "-f", "lavfi", "-i",
                 "color=size=256x256:duration=0.1", "-frames:v", "1", "-c:v", "h264_nvenc",
                 "-f", "null", "-"], stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL).returncode == 0
            video_codec = ["-c:v", "h264_nvenc", "-preset", "p7", "-tune", "hq", "-cq", "19"] \
                if nvenc else ["-c:v", "libx264", "-preset", "slow", "-crf", "18"]
            alpha_codec = ["-c:v", "h264_nvenc", "-preset", "p7", "-tune", "hq", "-cq", "10"] \
                if nvenc else ["-c:v", "libx264", "-preset", "medium", "-crf", "10"]
            encode = subprocess.Popen([ffmpeg, "-y", "-v", "warning", "-f", "rawvideo",
                "-pix_fmt", "rgba", "-s", f"{width}x{height}", "-r", frame_rate,
                "-i", "pipe:0", "-ss", f"{start:.6f}", "-t", f"{duration:.6f}",
                "-i", str(source), "-filter_complex",
                "[0:v]split=2[rgb][rgba];[rgb]format=yuv420p[vout];[rgba]alphaextract,format=yuv420p[aout]",
                "-map", "[vout]", "-map", "1:a:0?", *video_codec, "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", str(output / "foreground.mp4"),
                "-map", "[aout]", *alpha_codec, "-pix_fmt", "yuv420p",
                str(output / "alpha.mkv")], stdin=subprocess.PIPE)
            frame_bytes = width * height * 3

            def read_frame():
                value = bytearray(frame_bytes)
                view, received = memoryview(value), 0
                while received < frame_bytes:
                    size = decode.stdout.readinto(view[received:])
                    if not size:
                        break
                    received += size
                if not received:
                    return None
                if received != frame_bytes:
                    raise RuntimeError("source decoder returned a partial frame")
                return np.frombuffer(value, np.uint8).reshape(height, width, 3)

            def write_alpha(frame, alpha):
                nonlocal last_frame, last_alpha
                if alpha.shape != (height, width):
                    alpha = np.asarray(Image.fromarray(alpha, "L").resize(
                        (width, height), Image.Resampling.BILINEAR))
                last_frame, last_alpha = frame, alpha
                save_preview(frame, alpha)
                rgba = np.empty((height, width, 4), dtype=np.uint8)
                rgba[:, :, :3] = frame
                rgba[:, :, 3] = alpha
                output_bytes = memoryview(rgba).cast("B")
                while output_bytes:
                    written = encode.stdin.write(output_bytes)
                    if not written:
                        raise RuntimeError("FFmpeg output encoder closed unexpectedly")
                    output_bytes = output_bytes[written:]

            forward = new_processor()
            while (frame := read_frame()) is not None:
                index = count
                if index < selected_index:
                    alpha_path = backward_alpha / f"{index:08d}.png"
                    if not alpha_path.is_file():
                        raise RuntimeError(f"Backward alpha frame {index + 1} is missing")
                    alpha = np.asarray(Image.open(alpha_path).convert("L"))
                    alpha_path.unlink(missing_ok=True)
                elif index == selected_index:
                    emit("inference", work_percent(),
                         "Resetting MatAnyone 2 for forward propagation...")
                    alpha = alpha_from_prob(forward, warm_up(forward, frame))
                else:
                    with torch.inference_mode(), torch.amp.autocast(
                            device_type=device.type, enabled=fp16):
                        output_prob = forward.step(tensor(frame))
                    alpha = alpha_from_prob(forward, output_prob)
                write_alpha(frame, alpha)
                count += 1
                completed_work += 1
                emit("inference", work_percent(),
                     f"Processed {count}/{total} frames")
            if last_frame is not None:
                save_preview(last_frame, last_alpha, force=True)
            encode.stdin.close()
            decode.stdout.close()
            if decode.wait() != 0:
                raise RuntimeError("source normalization failed")
            if encode.wait() != 0:
                raise RuntimeError("FFmpeg output encoding failed")
        except BaseException:
            if decode is not None:
                try: decode.kill()
                except Exception: pass
            if encode is not None:
                try: encode.kill()
                except Exception: pass
            raise

    (output / "result.json").write_text(json.dumps({"width": width, "height": height,
        "frameRate": frame_rate, "durationMs": round(count * 1000 / fps)}, indent=2))
    emit("complete", 100, "MatAnyone 2 foreground and alpha are ready for preview")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--mask", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--mask-frame-ms", type=int)
    parser.add_argument("--max-size", type=int,
                        choices=(0, 256, 384, 512, 768, 1024), default=512)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        assert processing_size(3840, 2160, 512) == (910, 512)
        assert processing_size(1920, 1080, 0) == (1920, 1080)
        assert mask_frame_index(5_000, 1_000, 25, 200) == 100
        assert mask_frame_index(0, 1_000, 25, 200) == 0
        assert mask_frame_index(99_000, 1_000, 25, 200) == 199
        print("MatAnyone 2 worker self-test passed")
    elif not all((args.source, args.mask, args.output, args.runtime)):
        parser.error("--source, --mask, --output, and --runtime are required")
    else:
        process(args)

if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", 0, str(error))
        raise
