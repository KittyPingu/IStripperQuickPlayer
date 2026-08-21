#!/usr/bin/env python3
"""Generate review-sized PNG masks with Robust Video Matting."""
import argparse, json, queue, shutil, subprocess, sys, threading
from pathlib import Path

from rvm_worker import executable, load_model, probe, replace_preview
from custom_show_worker import model_size


def send(**values):
    print(json.dumps(values, separators=(",", ":")), flush=True)


def read_exact(stream, size):
    value, received = bytearray(size), 0
    view = memoryview(value)
    while received < size:
        count = stream.readinto(view[received:])
        if not count:
            break
        received += count
    return None if received == 0 else value if received == size else value[:received]


def put_bounded(target, value, errors):
    while True:
        try:
            target.put(value, timeout=.1)
            return
        except queue.Full:
            if not errors.empty():
                raise errors.get()


def finish_decode(process):
    # The fps filter can leave buffered output after the requested mask count.
    # Drain it before closing the read end so FFmpeg can flush and report its
    # real decode status instead of failing with a synthetic broken pipe.
    while process.stdout.read(1024 * 1024):
        pass
    process.stdout.close()
    return process.wait()


def apply_prop_on_frame(frame_index, every_frame):
    return every_frame or frame_index == 0


def contribution_frame(frame, detected, added):
    import numpy as np
    result = np.asarray(frame, dtype=np.uint8).copy()
    cyan = np.asarray(detected, dtype=bool) & ~np.asarray(added, dtype=bool)
    green = np.asarray(added, dtype=bool)
    result[cyan] = (result[cyan].astype(np.uint16) * 2 // 5 +
                    np.array((0, 153, 153), dtype=np.uint16)).astype(np.uint8)
    result[green] = (result[green].astype(np.uint16) // 4 +
                     np.array((0, 191, 0), dtype=np.uint16)).astype(np.uint8)
    return result


def self_test():
    process = subprocess.Popen(
        [sys.executable, "-c",
         "import sys; sys.stdout.buffer.write(b'x' * 1048576)"],
        stdout=subprocess.PIPE)
    if len(read_exact(process.stdout, 1024)) != 1024:
        raise RuntimeError("decode drain self-test could not read its prefix")
    if finish_decode(process) != 0:
        raise RuntimeError("decode drain self-test failed")
    if not apply_prop_on_frame(0, False) or apply_prop_on_frame(1, False) or \
            not apply_prop_on_frame(12, True):
        raise RuntimeError("prop-frame selection self-test failed")
    send(status="ok", message="rvm-mask self-test passed")


def main():
    if sys.argv[1:] == ["--self-test"]:
        self_test()
        return
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--frames", type=Path, required=True)
    parser.add_argument("--masks", type=Path, required=True)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int, required=True)
    parser.add_argument("--alpha-threshold", type=float, default=.5)
    parser.add_argument("--masks-only", action="store_true")
    parser.add_argument("--profile-log", type=Path)
    parser.add_argument("--preview-output", type=Path)
    parser.add_argument("--chunk-size", type=int, default=12)
    parser.add_argument("--prop-model", type=Path)
    parser.add_argument("--prop-every-frame", action="store_true")
    parser.add_argument("--debug-prop-contribution", type=Path)
    args = parser.parse_args()
    if args.prop_every_frame and not args.prop_model:
        parser.error("--prop-every-frame requires --prop-model")
    if args.debug_prop_contribution and not args.prop_every_frame:
        parser.error("--debug-prop-contribution requires --prop-every-frame")

    import numpy as np
    import torch
    from PIL import Image

    source, runtime = args.source.resolve(), args.runtime.resolve()
    width, height, rate, fps, duration = probe(source)
    start, end = args.start_ms / 1000, args.end_ms / 1000
    if start < 0 or end <= start or end > duration + .1:
        raise RuntimeError("clip range is outside the source video")
    review_width, review_height = model_size(width, height)
    expected = max(1, round((min(end, duration) - start) * fps))
    shutil.rmtree(args.masks, ignore_errors=True)
    args.masks.mkdir(parents=True)
    args.alpha_threshold = max(0, min(1, args.alpha_threshold))
    files = []
    decode = None
    if args.masks_only:
        send(status="progress", percent=1,
             message="Streaming source frames into RVM...")
        command = [executable("ffmpeg"), "-hide_banner", "-loglevel", "error",
            "-ss", f"{start:.6f}", "-i", str(source), "-t", f"{end-start:.6f}",
            "-vf", f"fps={rate},scale={review_width}:{review_height}:flags=bilinear,setsar=1",
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]
        decode = subprocess.Popen(command, stdout=subprocess.PIPE)
    else:
        shutil.rmtree(args.frames, ignore_errors=True)
        args.frames.mkdir(parents=True)
        send(status="progress", percent=1, message="Extracting RVM review frames...")
        command = [executable("ffmpeg"), "-hide_banner", "-loglevel", "error", "-y",
            "-ss", f"{start:.6f}", "-i", str(source), "-t", f"{end-start:.6f}",
            "-vf", f"fps={rate},scale={review_width}:{review_height}:flags=bilinear,setsar=1",
            "-q:v", "3", "-start_number", "1", str(args.frames / "%08d.jpg")]
        subprocess.run(command, check=True)
        files = sorted(args.frames.glob("*.jpg"))
        if not files:
            raise RuntimeError("RVM frame extraction produced no frames")

    send(status="progress", percent=10, message="Loading RVM ResNet50...")
    torch, model, device = load_model(runtime, "quality")
    prop = None
    prop_model_id = None
    if args.prop_model:
        send(status="progress", percent=10,
             message="Loading trained prop model...")
        from prop_segmenter import (augment_rvm_mask, load_package,
                                    predict_mask)
        prop_torch, prop_model, prop_device, prop_manifest = load_package(
            args.prop_model, device)
        if prop_torch is not torch or prop_device != device:
            raise RuntimeError(
                "Prop segmenter and RVM must use the same processing device")
        prop = (prop_torch, prop_model, prop_device, prop_manifest,
                predict_mask, augment_rvm_mask)
        prop_model_id = prop_manifest["modelId"]
    bf16 = device.type == "cuda" and torch.cuda.get_device_capability()[0] >= 8
    rec = [None] * 4
    count = expected if args.masks_only else len(files)
    chunk_size = max(1, min(32, args.chunk_size))
    pending = queue.Queue(maxsize=2)
    writer_errors = queue.Queue()
    writer_done = object()

    def write_masks():
        debug_encode = None
        try:
            if args.debug_prop_contribution:
                args.debug_prop_contribution.parent.mkdir(parents=True, exist_ok=True)
                debug_encode = subprocess.Popen([executable("ffmpeg"), "-y", "-v",
                    "warning", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s",
                    f"{review_width}x{review_height}", "-r", rate, "-i", "pipe:0",
                    "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p",
                    str(args.debug_prop_contribution)], stdin=subprocess.PIPE)
            written = 0
            retained_components = added_pixels = 0
            while True:
                item = pending.get()
                if item is writer_done:
                    break
                first_index, frames, masks, prop_stats, prop_debug = item
                retained_components += sum(value[0] for value in prop_stats)
                added_pixels += sum(value[1] for value in prop_stats)
                for offset, (pixels, mask) in enumerate(zip(frames, masks)):
                    index = first_index + offset
                    Image.fromarray(mask, "L").save(
                        args.masks / f"{index + 1:08d}.png", compress_level=1)
                    if debug_encode is not None:
                        detected, added = prop_debug[offset]
                        debug_encode.stdin.write(memoryview(contribution_frame(
                            pixels, detected, added)).cast("B"))
                    written = index + 1
                    if written == 1 or written == count or written % 10 == 0:
                        if args.preview_output:
                            args.preview_output.mkdir(parents=True, exist_ok=True)
                            for name, image, mode in (
                                    ("preview-source.jpg", pixels, "RGB"),
                                    ("preview-composite.jpg", mask, "L")):
                                temporary = args.preview_output / (name + ".tmp")
                                Image.fromarray(image, mode).save(
                                    temporary, "JPEG", quality=88)
                                replace_preview(temporary, args.preview_output / name)
                        send(status="progress", percent=10 + 88 * written / count,
                             message=(f"RVM segmented {written}/{count} frames" +
                                (f"; trained model retained {retained_components} "
                                 f"components ({added_pixels:,} pixels)"
                                 if prop_model_id is not None else "")),
                             frame=written - 1)
            if debug_encode is not None:
                debug_encode.stdin.close()
                if debug_encode.wait() != 0:
                    raise RuntimeError(
                        "FFmpeg prop contribution encoding failed")
        except BaseException as error:
            if debug_encode is not None:
                try: debug_encode.kill()
                except Exception: pass
            writer_errors.put(error)

    writer = threading.Thread(target=write_masks, name="rvm-mask-writer",
                              daemon=True)
    writer.start()
    index = 0
    try:
        with torch.inference_mode(), torch.autocast(device_type=device.type,
                dtype=torch.bfloat16, enabled=bf16):
            while index < count:
                frames = []
                for offset in range(min(chunk_size, count - index)):
                    if args.masks_only:
                        data = read_exact(decode.stdout,
                                          review_width * review_height * 3)
                        if not data:
                            break
                        if len(data) != review_width * review_height * 3:
                            raise RuntimeError(
                                "RVM source decoder returned a partial frame")
                        pixels = np.frombuffer(data, dtype=np.uint8).reshape(
                            review_height, review_width, 3).copy()
                    else:
                        with Image.open(files[index + offset]) as source_image:
                            pixels = np.asarray(source_image.convert(
                                "RGB"), dtype=np.uint8).copy()
                    frames.append(pixels)
                if not frames:
                    break
                # RVM is recurrent across the temporal dimension, so processing a
                # bounded sequence preserves state while amortizing Python, upload,
                # and kernel-launch overhead across the chunk.
                tensor = torch.from_numpy(np.stack(frames)).permute(0, 3, 1, 2) \
                    .unsqueeze(0).to(device=device, dtype=torch.float32).div_(255)
                _, alpha, *rec = model(tensor, *rec,
                    downsample_ratio=min(1.0, 512 / max(review_width, review_height)))
                rvm_alphas = alpha[0, :, 0].float().cpu().numpy()
                masks = (rvm_alphas >= args.alpha_threshold).astype(np.uint8) * 255
                prop_stats = []
                prop_debug = []
                if prop is not None:
                    prop_torch, prop_model, prop_device, prop_manifest, \
                        predict_prop_mask, augment_rvm_mask = prop
                    for offset, pixels in enumerate(frames):
                        if not apply_prop_on_frame(
                                index + offset, args.prop_every_frame):
                            prop_stats.append((0, 0))
                            prop_debug.append((np.zeros_like(masks[offset], dtype=bool),
                                               np.zeros_like(masks[offset], dtype=bool)))
                            continue
                        predicted, _ = predict_prop_mask(
                            prop_torch, prop_model, prop_device, pixels,
                            prop_manifest.get("confidenceThreshold", .5),
                            prop_manifest.get("inputSize", 512), rvm_alphas[offset])
                        person = masks[offset] >= 128
                        combined, components, _ = augment_rvm_mask(
                            predicted, person,
                            prop_manifest.get("proximityRadiusAt512", 24))
                        added = int((combined & ~person).sum())
                        added_mask = combined & ~person
                        prop_debug.append((predicted & person | added_mask,
                                           added_mask))
                        retained = sum(value["retained"] for value in components)
                        masks[offset] = combined.astype(np.uint8) * 255
                        prop_stats.append((retained, added))
                else:
                    prop_stats = [(0, 0)] * len(frames)
                    prop_debug = [(np.zeros_like(mask, dtype=bool),
                                   np.zeros_like(mask, dtype=bool))
                                  for mask in masks]
                put_bounded(pending, (index, frames, masks, prop_stats, prop_debug),
                            writer_errors)
                if prop is not None and not args.prop_every_frame:
                    del prop_model
                    prop = None
                    if device.type == "cuda":
                        torch.cuda.empty_cache()
                index += len(frames)
                if not writer_errors.empty():
                    raise writer_errors.get()
    finally:
        if writer_errors.empty():
            put_bounded(pending, writer_done, writer_errors)
        writer.join(timeout=30)
    if not writer_errors.empty():
        raise writer_errors.get()
    if writer.is_alive():
        raise RuntimeError("RVM mask writer did not shut down cleanly")
    generated = len(list(args.masks.glob("*.png")))
    if decode is not None:
        if finish_decode(decode) != 0:
            raise RuntimeError("RVM source decoding failed")
    if not generated:
        raise RuntimeError("RVM generated no masks")
    send(status="ready", frameCount=generated, fps=fps, width=review_width,
         height=review_height, device=device.type, precision="BF16" if bf16 else "FP32",
         optimized=True, execution="chunked-bounded", chunkSize=chunk_size,
         checkpoint="RVM ResNet50",
         propModel=prop_model_id,
         propEveryFrame=args.prop_every_frame,
         model="rvm", resumed=False, framesFolder=str(args.frames),
         supportsCorrections=False)
    if args.masks_only:
        return
    for line in sys.stdin:
        if not line.strip():
            continue
        if json.loads(line).get("command") == "quit":
            break


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        send(status="error", message=str(error))
        raise
