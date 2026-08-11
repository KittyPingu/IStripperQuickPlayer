#!/usr/bin/env python3
"""Stabilo + SAM2 exclusion-mask video stabilization worker for QuickPlayer."""

from __future__ import annotations

import argparse
import collections
import json
import math
import os
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path

import cv2
import numpy as np


STABILO_COMMIT = "52ebd524d26fb940b868dc9d7eeb3e2602f895a3"


def send(**values) -> None:
    print(json.dumps(values, separators=(",", ":")), flush=True)


def rate_value(rate: str) -> float:
    numerator, denominator = rate.split("/", 1)
    value = float(numerator) / float(denominator)
    if not math.isfinite(value) or value <= 0:
        raise RuntimeError("The source has an invalid frame rate")
    return value


def probe(path: Path, ffprobe: str) -> dict:
    result = subprocess.run(
        [ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries",
         "stream=width,height,avg_frame_rate,nb_frames:format=duration", "-of", "json", str(path)],
        capture_output=True, text=True, check=False,
    )
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or "ffprobe failed")
    value = json.loads(result.stdout)
    stream = value["streams"][0]
    duration = float(value["format"]["duration"])
    rate = stream.get("avg_frame_rate", "30/1")
    fps = rate_value(rate)
    frames = int(stream.get("nb_frames") or round(duration * fps))
    return dict(width=int(stream["width"]), height=int(stream["height"]),
                frameRate=rate, fps=fps, duration=duration, frames=max(1, frames))


def read_mask_box(path: Path, width: int, height: int):
    mask = cv2.imread(str(path), cv2.IMREAD_GRAYSCALE)
    if mask is None:
        raise RuntimeError(f"SAM2 exclusion mask is missing: {path.name}")
    points = cv2.findNonZero((mask >= 128).astype(np.uint8))
    if points is None:
        return None, mask
    x, y, w, h = cv2.boundingRect(points)
    scale_x, scale_y = width / mask.shape[1], height / mask.shape[0]
    box = np.asarray([[(x + w / 2) * scale_x, (y + h / 2) * scale_y,
                       max(1, w * scale_x), max(1, h * scale_y)]], np.float32)
    return box, mask


def atomic_jpeg(path: Path, image: np.ndarray) -> None:
    temporary = path.with_name(f"{path.stem}.tmp-{uuid.uuid4().hex}.jpg")
    if not cv2.imwrite(str(temporary), image, [cv2.IMWRITE_JPEG_QUALITY, 88]):
        raise RuntimeError(f"Could not write preview {path}")
    os.replace(temporary, path)


def motion_preview(frame, mask, stabilizer, frame_number: int) -> np.ndarray:
    preview = frame.copy()
    resized_mask = cv2.resize(mask, (frame.shape[1], frame.shape[0]),
                              interpolation=cv2.INTER_NEAREST) >= 128
    tint = np.zeros_like(preview)
    tint[:, :, 2] = 220
    preview[resized_mask] = cv2.addWeighted(
        preview[resized_mask], .55, tint[resized_mask], .45, 0)
    current = getattr(stabilizer, "cur_pts", None)
    reference = getattr(stabilizer, "ref_pts", None)
    inliers = getattr(stabilizer, "cur_inliers", None)
    if current is not None and reference is not None and len(current) == len(reference):
        flags = np.ones(len(current), dtype=bool) if inliers is None else \
            np.asarray(inliers).reshape(-1).astype(bool)
        for source, target in zip(np.asarray(current)[flags][::2],
                                  np.asarray(reference)[flags][::2], strict=False):
            p1 = tuple(np.round(source).astype(int))
            delta = np.clip(target - source, -80, 80)
            p2 = tuple(np.round(source + delta).astype(int))
            cv2.arrowedLine(preview, p1, p2, (40, 255, 80), 1,
                            cv2.LINE_AA, tipLength=.25)
    cv2.rectangle(preview, (8, 8), (405, 63), (0, 0, 0), -1)
    cv2.putText(preview, f"Background matches / motion - frame {frame_number + 1}",
                (18, 32), cv2.FONT_HERSHEY_SIMPLEX, .58, (255, 255, 255), 1,
                cv2.LINE_AA)
    cv2.putText(preview, "Red = SAM2 exclusion; green = accepted background motion",
                (18, 53), cv2.FONT_HERSHEY_SIMPLEX, .42, (210, 210, 210), 1,
                cv2.LINE_AA)
    return preview


def identity(transformation: str) -> np.ndarray:
    return np.eye(3, dtype=np.float64) if transformation == "projective" else \
        np.asarray([[1., 0., 0.], [0., 1., 0.]], np.float64)


def matrix_is_finite(matrix) -> bool:
    return matrix is not None and np.asarray(matrix).shape in ((2, 3), (3, 3)) and \
        np.isfinite(matrix).all()


def matrix_is_sane(matrix, transformation: str, width: int, height: int,
                   previous=None) -> bool:
    if not matrix_is_finite(matrix):
        return False
    full = np.eye(3, dtype=np.float64)
    value = np.asarray(matrix, np.float64)
    full[:value.shape[0], :value.shape[1]] = value
    if abs(full[2, 2]) < 1e-8:
        return False
    full /= full[2, 2]
    corners = np.float32([[[0, 0], [width - 1, 0],
                           [width - 1, height - 1], [0, height - 1]]])
    warped = cv2.perspectiveTransform(corners, full)[0]
    if not np.isfinite(warped).all() or not cv2.isContourConvex(warped):
        return False
    area_ratio = abs(cv2.contourArea(warped)) / max(1, (width - 1) * (height - 1))
    if not .5 <= area_ratio <= 2:
        return False
    if transformation == "projective" and (
            abs(full[2, 0] * width) > .2 or abs(full[2, 1] * height) > .2):
        return False
    if previous is not None:
        prior = np.eye(3, dtype=np.float64)
        prior_value = np.asarray(previous, np.float64)
        prior[:prior_value.shape[0], :prior_value.shape[1]] = prior_value
        prior /= prior[2, 2]
        prior_corners = cv2.perspectiveTransform(corners, prior)[0]
        maximum_step = max(40, math.hypot(width, height) * .10)
        if np.linalg.norm(warped - prior_corners, axis=1).max() > maximum_step:
            return False
    return True


def analyze(args, info: dict, source_preview: Path, output_preview: Path):
    os.environ["STABILO_DISABLE_UPDATE_CHECK"] = "1"
    os.environ.setdefault("TORCH_HOME", str(args.runtime / "torch-hub"))
    sys.path.insert(0, str(args.runtime / "stabilo"))
    from stabilo import Stabilizer, __version__
    if __version__ != "1.4.0" or (args.runtime / "STABILO_COMMIT").read_text().strip() != STABILO_COMMIT:
        raise RuntimeError("The pinned Stabilo 1.4.0 runtime is not installed")

    import torch
    device = "cuda" if torch.cuda.is_available() else "cpu"
    analysis_device = device if args.detector == "xfeat" else "cpu"
    detector = args.detector
    stabilizer = Stabilizer(
        detector_name=detector, matcher_name="bf", filter_type="ratio",
        transformation_type=args.transformation, downsample_ratio=args.downsample,
        max_features=2400, ref_multiplier=1.5, mask_use=True,
        mask_margin_ratio=.10, device=device, viz=True, benchmark=False,
        ransac_method=(cv2.USAC_MAGSAC if args.transformation == "projective"
                       else cv2.RANSAC),
        min_good_match_count_warning=12, min_inliers_match_count_warning=8,
    )
    cap = cv2.VideoCapture(str(args.source))
    if not cap.isOpened():
        raise RuntimeError("OpenCV could not open the source video")
    total = min(info["frames"], len(list(args.masks.glob("*.png"))))
    if total < 1:
        raise RuntimeError("No accepted SAM2 exclusion masks were found")
    reference = max(0, min(total - 1, round(args.reference_ms / 1000 * info["fps"])))
    cap.set(cv2.CAP_PROP_POS_FRAMES, reference)
    ok, reference_frame = cap.read()
    if not ok:
        raise RuntimeError("The selected Stabilo reference frame could not be decoded")
    reference_box, _ = read_mask_box(args.masks / f"{reference + 1:08d}.png",
                                     info["width"], info["height"])
    stabilizer.set_ref_frame(reference_frame, reference_box, box_format="xywh")
    cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
    matrices, resets = [], 0
    last_report = 0.0
    for frame_number in range(total):
        ok, frame = cap.read()
        if not ok:
            raise RuntimeError(f"Source decoding stopped at frame {frame_number + 1}")
        box, mask = read_mask_box(args.masks / f"{frame_number + 1:08d}.png",
                                  info["width"], info["height"])
        if frame_number == reference:
            matrix = identity(args.transformation)
            stabilizer.cur_pts = stabilizer.ref_pts
            stabilizer.cur_inliers = None
        else:
            stabilizer.stabilize(frame, box, box_format="xywh")
            inliers = getattr(stabilizer, "cur_inliers_count", None)
            matrix = getattr(stabilizer, "cur_trans_matrix", None)
            minimum_inliers = 4 if args.transformation == "projective" else 3
            previous = matrices[-1] if matrices else None
            if inliers is None or inliers < minimum_inliers or not matrix_is_sane(
                    matrix, args.transformation, info["width"], info["height"], previous):
                matrix = matrices[-1] if matrices else identity(args.transformation)
        matrices.append(np.asarray(matrix, np.float64).copy())
        now = time.monotonic()
        if frame_number == 0 or frame_number + 1 == total or now - last_report >= .4:
            atomic_jpeg(source_preview,
                        motion_preview(frame, mask, stabilizer, frame_number))
            percent = 55 * (frame_number + 1) / total
            send(status="progress", stage="analysis", percent=percent,
                 message=f"Step 1 of 2 - Stabilo background analysis - Processed {frame_number + 1}/{total} frames",
                 sourcePreview=str(source_preview), outputPreview=str(output_preview),
                 sourceLabel="Stabilo background motion / SAM2 exclusion",
                 outputLabel="Stabilized frame (available during Step 2)")
            last_report = now
    cap.release()
    return matrices, total, reference, detector, analysis_device, resets


def can_nvenc(ffmpeg: str, preset: str) -> bool:
    result = subprocess.run(
        [ffmpeg, "-v", "error", "-f", "lavfi", "-i", "color=size=256x256:duration=0.1",
         "-frames:v", "1", "-an", "-c:v", "h264_nvenc", "-preset", preset,
         "-f", "null", "-"], capture_output=True, check=False)
    return result.returncode == 0


def encoder_process(args, info: dict, nvenc: bool):
    command = [args.ffmpeg, "-y", "-hide_banner", "-nostdin", "-v", "warning",
        "-f", "rawvideo", "-pix_fmt", "bgr24", "-s:v",
        f"{info['width']}x{info['height']}", "-r", info["frameRate"], "-i", "pipe:0",
        "-i", str(args.source), "-map", "0:v:0", "-map", "1:a?",
        "-map_metadata", "1", "-map_chapters", "1"]
    if nvenc:
        command += ["-c:v", "h264_nvenc", "-preset", args.nvenc_preset,
                    "-tune", "hq", "-cq", "19"]
    else:
        command += ["-c:v", "libx264", "-preset", "fast", "-crf", "18"]
    command += ["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
                "-movflags", "+faststart", "-shortest", str(args.output)]
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                               stderr=subprocess.PIPE)
    errors = collections.deque(maxlen=300)
    def read_errors():
        for line in iter(process.stderr.readline, b""):
            errors.append(line.decode("utf-8", "replace").rstrip())
    thread = threading.Thread(target=read_errors, daemon=True)
    thread.start()
    return process, errors, thread


def gpu_warper(border: str):
    import torch
    if not torch.cuda.is_available():
        return None
    import kornia
    padding = "reflection" if border == "reflect" else "zeros"
    def warp(frame, matrix):
        full = np.eye(3, dtype=np.float32)
        array = np.asarray(matrix, np.float32)
        full[:array.shape[0], :array.shape[1]] = array
        image = torch.from_numpy(np.ascontiguousarray(frame)).to(
            device="cuda", non_blocking=True).permute(2, 0, 1).unsqueeze(0).float().div_(255)
        transform = torch.from_numpy(full).to(device="cuda").unsqueeze(0)
        with torch.inference_mode():
            result = kornia.geometry.transform.warp_perspective(
                image, transform, (frame.shape[0], frame.shape[1]),
                mode="bilinear", padding_mode=padding, align_corners=True)
            result.mul_(255).clamp_(0, 255)
            return result.to(torch.uint8).squeeze(0).permute(1, 2, 0).cpu().numpy()
    return warp


def cpu_warp(frame, matrix, transformation: str, border: str):
    mode = cv2.BORDER_REFLECT_101 if border == "reflect" else cv2.BORDER_CONSTANT
    size = (frame.shape[1], frame.shape[0])
    if transformation == "projective":
        return cv2.warpPerspective(frame, matrix, size, flags=cv2.INTER_LINEAR,
                                   borderMode=mode)
    return cv2.warpAffine(frame, matrix, size, flags=cv2.INTER_LINEAR,
                          borderMode=mode)


def render(args, info: dict, matrices, total: int, source_preview: Path,
           output_preview: Path):
    cap = cv2.VideoCapture(str(args.source))
    if not cap.isOpened():
        raise RuntimeError("OpenCV could not reopen the source video")
    nvenc = can_nvenc(args.ffmpeg, args.nvenc_preset)
    process, errors, error_thread = encoder_process(args, info, nvenc)
    warp_gpu = gpu_warper(args.border)
    last_report = 0.0
    try:
        for frame_number, matrix in enumerate(matrices):
            ok, frame = cap.read()
            if not ok:
                raise RuntimeError(f"Source decoding stopped at frame {frame_number + 1}")
            stabilized = warp_gpu(frame, matrix) if warp_gpu is not None else \
                cpu_warp(frame, matrix, args.transformation, args.border)
            try:
                process.stdin.write(stabilized.tobytes())
            except (BrokenPipeError, OSError):
                raise RuntimeError("FFmpeg stopped while encoding: " + "\n".join(errors))
            now = time.monotonic()
            if frame_number == 0 or frame_number + 1 == total or now - last_report >= .4:
                atomic_jpeg(source_preview, frame)
                atomic_jpeg(output_preview, stabilized)
                percent = 55 + 43 * (frame_number + 1) / total
                send(status="progress", stage="render", percent=percent,
                     message=f"Step 2 of 2 - Rendering background-locked video - Processed {frame_number + 1}/{total} frames",
                     sourcePreview=str(source_preview), outputPreview=str(output_preview),
                     sourceLabel="Original frame", outputLabel="Stabilo + SAM2 result")
                last_report = now
        process.stdin.close()
        code = process.wait()
        error_thread.join(timeout=2)
        if code:
            raise RuntimeError("FFmpeg encoding failed: " + "\n".join(errors))
    finally:
        cap.release()
        if process.poll() is None:
            try: process.kill()
            except OSError: pass
    return nvenc, warp_gpu is not None


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--masks", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--reference-ms", type=int, required=True)
    parser.add_argument("--detector", choices=("orb", "xfeat"), default="xfeat")
    parser.add_argument("--transformation", choices=("affine", "projective"), default="affine")
    parser.add_argument("--downsample", type=float, default=.35)
    parser.add_argument("--border", choices=("reflect", "black"), default="reflect")
    parser.add_argument("--nvenc-preset", default="p5")
    parser.add_argument("--source-preview", type=Path, required=True)
    parser.add_argument("--output-preview", type=Path, required=True)
    parser.add_argument("--ffmpeg", default=os.environ.get("IQP_FFMPEG", "ffmpeg"))
    parser.add_argument("--ffprobe", default=os.environ.get("IQP_FFPROBE", "ffprobe"))
    args = parser.parse_args()
    if not 0 < args.downsample <= 1:
        parser.error("--downsample must be in (0, 1]")
    args.source, args.output, args.masks, args.runtime = (
        args.source.resolve(), args.output.resolve(), args.masks.resolve(), args.runtime.resolve())
    args.source_preview, args.output_preview = \
        args.source_preview.resolve(), args.output_preview.resolve()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.source_preview.parent.mkdir(parents=True, exist_ok=True)
    info = probe(args.source, args.ffprobe)
    if not 0 <= args.reference_ms / 1000 <= info["duration"]:
        raise RuntimeError("The selected reference frame is outside the source video")
    send(status="progress", stage="analysis", percent=0,
         message="Step 1 of 2 - Loading Stabilo and the accepted SAM2 exclusions...",
         sourcePreview=str(args.source_preview), outputPreview=str(args.output_preview),
         sourceLabel="Stabilo background motion / SAM2 exclusion",
         outputLabel="Stabilized frame (available during Step 2)")
    matrices, total, reference, detector, analysis_device, resets = analyze(
        args, info, args.source_preview, args.output_preview)
    nvenc, gpu_warp = render(args, info, matrices, total,
                             args.source_preview, args.output_preview)
    output_info = probe(args.output, args.ffprobe)
    difference = abs(output_info["duration"] - info["duration"])
    if difference > max(1, info["duration"] * .01):
        raise RuntimeError("The stabilized output duration does not match the source")
    result = dict(width=output_info["width"], height=output_info["height"],
                  frameRate=output_info["frameRate"],
                  durationMs=round(output_info["duration"] * 1000),
                  encoder="h264_nvenc" if nvenc else "libx264",
                  encoderPreset=args.nvenc_preset if nvenc else "fast",
                  executionMode=(f"Stabilo {detector.upper()} {analysis_device.upper()}; "
                                 f"{'CUDA' if gpu_warp else 'CPU'} warp; {resets} reference resets"))
    send(status="complete", stage="complete", percent=100,
         message="Stabilo + SAM2 stabilization complete.", result=result,
         sourcePreview=str(args.source_preview), outputPreview=str(args.output_preview),
         sourceLabel="Original frame", outputLabel="Stabilo + SAM2 result")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        send(status="error", message=str(error))
        raise
