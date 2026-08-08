#!/usr/bin/env python3
"""Persistent SAM2 video-mask propagation and correction worker."""
import argparse, json, shutil, subprocess, sys
from pathlib import Path

from rvm_worker import executable, probe
from videomama_worker import load_sam2, model_size, use_on_demand_sam2_frames
from sam_mask_worker import automatic_foreground


def send(**values):
    print(json.dumps(values), flush=True)


def extract_frames(source, folder, start, duration, rate, width, height, total):
    command = [executable("ffmpeg"), "-y", "-v", "error", "-ss", f"{start:.6f}",
        "-i", str(source), "-t", f"{duration:.6f}", "-map", "0:v:0", "-vf",
        f"fps={rate},scale={width}:{height}:flags=lanczos,setsar=1", "-q:v", "2",
        str(folder / "%08d.jpg"), "-progress", "pipe:1", "-nostats"]
    process = subprocess.Popen(command, stdout=subprocess.PIPE, text=True)
    for line in process.stdout:
        if line.startswith("frame="):
            count = int(line.partition("=")[2])
            send(status="progress", percent=min(15, count * 15 / total),
                 message=f"Extracted {count}/{total} review frames")
    if process.wait() != 0:
        raise RuntimeError("SAM2 review-frame extraction failed")


def mask_from_logits(object_ids, logits, frame_index, mask_folder):
    import numpy as np
    from PIL import Image
    ids = object_ids.tolist() if hasattr(object_ids, "tolist") else list(object_ids)
    if 1 not in ids:
        raise RuntimeError(f"SAM2 lost the selected foreground at frame {frame_index + 1}")
    mask = (logits[ids.index(1)] > 0).squeeze().byte().cpu().numpy() * 255
    Image.fromarray(mask.astype(np.uint8), "L").save(
        mask_folder / f"{frame_index + 1:08d}.png")


def correction_limits(frame, correction_frames, total):
    return (max((value for value in correction_frames if value < frame), default=0),
            min((value for value in correction_frames if value > frame), default=total - 1))


def propagate(predictor, state, mask_folder, start, total, reverse, progress_start,
              progress_size, message, max_frames=None):
    maximum = (start + 1 if reverse else total - start) if max_frames is None \
        else max_frames + 1
    count = 0
    for frame_index, object_ids, logits in predictor.propagate_in_video(
            state, start_frame_idx=start, reverse=reverse,
            max_frame_num_to_track=max_frames):
        mask_from_logits(object_ids, logits, frame_index, mask_folder)
        count += 1
        send(status="progress",
             percent=progress_start + count * progress_size / max(1, maximum),
             message=f"{message} {count}/{maximum} frames")


def process(args):
    import numpy as np
    import torch
    from PIL import Image

    source, runtime = args.source.resolve(), args.runtime.resolve()
    frames, masks = args.frames.resolve(), args.masks.resolve()
    shutil.rmtree(frames, ignore_errors=True)
    shutil.rmtree(masks, ignore_errors=True)
    frames.mkdir(parents=True); masks.mkdir(parents=True)
    width, height, rate, fps, source_duration = probe(source)
    start, end = args.start_ms / 1000, args.end_ms / 1000
    if start < 0 or end <= start or end > source_duration + .1:
        raise RuntimeError("clip range is outside the source video")
    duration = min(end, source_duration) - start
    expected = max(1, round(duration * fps))
    review_width, review_height = model_size(width, height)
    extract_frames(source, frames, start, duration, rate,
                   review_width, review_height, expected)
    frame_files = sorted(frames.glob("*.jpg"))
    if not frame_files: raise RuntimeError("The clip has no review frames")
    total = len(frame_files)
    initial = np.asarray(Image.open(args.mask).convert("L").resize(
        (review_width, review_height), Image.Resampling.NEAREST)) >= 128
    device = "cuda" if torch.cuda.is_available() else "cpu"
    bf16 = device == "cuda" and torch.cuda.get_device_capability()[0] >= 8
    optimized = device == "cuda" and sys.platform != "win32"
    send(status="progress", percent=15,
         message=f"Loading SAM2 on {device.upper()}...")
    predictor = load_sam2(runtime, torch, device, optimized=True)
    use_on_demand_sam2_frames(frames, torch)
    with torch.inference_mode(), torch.autocast(device_type=device,
            dtype=torch.bfloat16, enabled=bf16):
        state = predictor.init_state(str(frames), offload_video_to_cpu=True,
                                     offload_state_to_cpu=device != "cuda")
        predictor.add_new_mask(state, frame_idx=0, obj_id=1, mask=initial)
        correction_frames = {0}
        propagate(predictor, state, masks, 0, total, False, 15, 85,
                  "Tracked")
        send(status="ready", frameCount=total, fps=fps,
             width=review_width, height=review_height, device=device,
             precision="BF16" if bf16 else "FP32", optimized=optimized,
             checkpoint="SAM2.1 Hiera Base+")
        for line in sys.stdin:
            request = json.loads(line)
            if request.get("command") == "quit": break
            command = request.get("command")
            if command not in ("prompt", "auto", "update"):
                send(status="error", message="Unknown SAM2 editor command")
                continue
            frame = int(request["frame"])
            if not 0 <= frame < total:
                send(status="error", message="Correction frame is outside the clip")
                continue
            if command != "update":
                points = np.asarray(request.get("points", []), np.float32)
                labels = np.asarray(request.get("labels", []), np.int32)
                if len(points) != len(labels) or command == "prompt" and len(points) == 0:
                    send(status="error", message="Add at least one correction click")
                    continue
                if command == "auto":
                    frame_image = np.asarray(Image.open(frame_files[frame]).convert("RGB"))
                    automatic, candidate_count = automatic_foreground(
                        predictor, frame_image, points, labels, torch, device, bf16)
                    _, object_ids, logits = predictor.add_new_mask(
                        state, frame_idx=frame, obj_id=1, mask=automatic)
                else:
                    _, object_ids, logits = predictor.add_new_points_or_box(
                        state, frame_idx=frame, obj_id=1, points=points, labels=labels,
                        clear_old_points=True)
                    candidate_count = None
                mask_from_logits(object_ids, logits, frame, masks)
                send(status="preview", frame=frame,
                     candidates=candidate_count, automatic=command == "auto")
                continue
            previous, following = correction_limits(frame, correction_frames, total)
            if frame > previous:
                propagate(predictor, state, masks, frame, total, True, 0, 50,
                          "Updated backward", frame - previous)
            propagate(predictor, state, masks, frame, total, False,
                      50 if frame > previous else 0, 50 if frame > previous else 100,
                      "Updated forward", following - frame)
            correction_frames.add(frame)
            send(status="ready", frameCount=total, fps=fps,
                 width=review_width, height=review_height, device=device,
                 precision="BF16" if bf16 else "FP32", optimized=optimized,
                 checkpoint="SAM2.1 Hiera Base+")


def self_test():
    assert model_size(1920, 1080) == (1024, 576)
    assert model_size(1080, 1920) == (576, 1024)
    assert correction_limits(75, {0, 50, 100}, 150) == (50, 100)
    print("SAM2 refinement worker self-test passed")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--mask", type=Path)
    parser.add_argument("--frames", type=Path)
    parser.add_argument("--masks", type=Path)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test: self_test()
    elif not all((args.source, args.runtime, args.mask, args.frames,
                  args.masks, args.end_ms is not None)):
        parser.error("--source, --runtime, --mask, --frames, --masks, and --end-ms are required")
    else: process(args)


if __name__ == "__main__":
    try: main()
    except Exception as error:
        send(status="error", message=str(error)); raise
