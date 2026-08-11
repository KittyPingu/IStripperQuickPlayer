#!/usr/bin/env python3
"""Bounded-memory QuickPlayer ProPainter runner. Stdout is NDJSON progress only."""

import argparse
from fractions import Fraction
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import traceback

STREAMING_COMMIT = "c8983a445720450bf2fd976cab0adb1cad19547d"
MODEL_FILES = (
    "raft_things-0000-d74fed4b.pth",
    "propainter_rfc-0000-a865ddc0.pth",
    "propainter-0000-5f3cc1e7.pth",
)


def emit(stage: str, percent: float, message: str) -> None:
    print(json.dumps({"stage": stage, "percent": percent, "message": message}), flush=True)


def validate(runtime: Path) -> tuple[Path, Path]:
    root = runtime / "propainter-streaming"
    weights = runtime / "propainter-streaming-weights"
    marker = runtime / "PROPAINTER_STREAMING_COMMIT"
    required = [root / "propainter" / "propainter_video.py",
                *(weights / name for name in MODEL_FILES)]
    if not marker.is_file() or marker.read_text(encoding="utf-8").strip() != STREAMING_COMMIT:
        raise RuntimeError("Streaming ProPainter validation failed; run setup again")
    missing = [path.name for path in required if not path.is_file()]
    if missing:
        raise RuntimeError("Streaming ProPainter is incomplete: " + ", ".join(missing))
    return root, weights


def video_info(ffprobe: str, source: Path) -> tuple[str, int]:
    command = [ffprobe, "-v", "error", "-select_streams", "v:0",
               "-show_entries", "stream=avg_frame_rate,nb_frames,duration:format=duration",
               "-of", "json", str(source)]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or "Could not inspect the source video")
    data = json.loads(result.stdout)
    stream = data["streams"][0]
    rate = stream.get("avg_frame_rate", "")
    if not re.fullmatch(r"\d+(?:/\d+)?", rate) or rate in {"0", "0/0"}:
        raise RuntimeError("Could not read the source frame rate")
    frames = int(stream.get("nb_frames") or 0)
    if frames <= 0:
        duration = float(stream.get("duration") or data.get("format", {}).get("duration") or 0)
        frames = round(duration * float(Fraction(rate)))
    if frames <= 0:
        raise RuntimeError("Could not determine the source frame count")
    return rate, frames


def supports_nvenc(ffmpeg: str) -> bool:
    test = subprocess.run([ffmpeg, "-v", "error", "-f", "lavfi", "-i",
                           "color=size=320x180:rate=1", "-frames:v", "1", "-c:v",
                           "h264_nvenc", "-f", "null", "-"], capture_output=True)
    return test.returncode == 0


def fast_process(source: Path, mask: Path, output: Path, part: Path,
                 preview_source: Path | None, preview_output: Path | None,
                 ffmpeg: str, total_frames: int) -> None:
    import cv2
    import numpy as np

    image = cv2.imread(str(mask), cv2.IMREAD_GRAYSCALE)
    if image is None:
        raise RuntimeError("The removal mask could not be read")
    rows, columns = np.where(image > 0)
    if not len(rows):
        raise RuntimeError("The removal mask is empty")
    height, width = image.shape
    x = max(1, int(columns.min()))
    y = max(1, int(rows.min()))
    right = min(width - 1, int(columns.max()) + 1)
    bottom = min(height - 1, int(rows.max()) + 1)
    area_width, area_height = right - x, bottom - y
    if area_width < 2 or area_height < 2:
        raise RuntimeError("The selected removal area is too small")

    nvenc = supports_nvenc(ffmpeg)
    codec = (["-c:v", "h264_nvenc", "-preset", "p6", "-tune", "hq",
              "-rc", "vbr", "-cq", "16", "-b:v", "0"] if nvenc else
             ["-c:v", "libx264", "-preset", "medium", "-crf", "16"])
    emit("removing", 2, "Starting fast watermark removal with " +
         ("NVENC..." if nvenc else "CPU H.264 output..."))
    clean = f"delogo=x={x}:y={y}:w={area_width}:h={area_height}:show=0"
    command = [ffmpeg, "-y", "-v", "error", "-i", str(source)]
    if preview_source and preview_output:
        preview_source.parent.mkdir(parents=True, exist_ok=True)
        preview_output.parent.mkdir(parents=True, exist_ok=True)
        command += ["-filter_complex",
                    f"[0:v]split=2[source_preview_input][clean_input];"
                    f"[clean_input]{clean},split=2[encoded][output_preview_input];"
                    f"[source_preview_input]fps=1[source_thumb];"
                    f"[output_preview_input]fps=1[output_thumb]",
                    "-map", "[encoded]", "-map", "0:a?", *codec,
                    "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart",
                    str(part), "-map", "[source_thumb]", "-an", "-c:v", "mjpeg",
                    "-q:v", "3", "-update", "1", "-atomic_writing", "1",
                    str(preview_source), "-map", "[output_thumb]", "-an",
                    "-c:v", "mjpeg", "-q:v", "3", "-update", "1",
                    "-atomic_writing", "1", str(preview_output)]
    else:
        command += ["-vf", clean, "-map", "0:v:0", "-map", "0:a?", *codec,
                    "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart",
                    str(part)]
    command += ["-progress", "pipe:1", "-nostats"]
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, bufsize=1)
    assert process.stdout is not None
    for line in process.stdout:
        key, _, value = line.strip().partition("=")
        if key == "frame" and value.isdigit():
            frame = min(int(value), total_frames)
            emit("removing", 2 + frame / total_frames * 97,
                 f"Removed object from {frame:,}/{total_frames:,} frames")
    error = process.stderr.read() if process.stderr else ""
    if process.wait() != 0:
        raise RuntimeError(error.strip() or "FFmpeg watermark removal failed")
    os.replace(part, output)
    emit("complete", 100, f"Saved {output.name}")


def safe_window(requested: int, width: int, height: int,
                resize_ratio: float, vram_gb: float = 16) -> int:
    scaled_pixels = width * height * resize_ratio * resize_ratio
    capacity = 40 * (960 * 540) / max(1, scaled_pixels) * max(0.5, vram_gb / 16)
    limit = max(12, min(80, int(capacity) // 2 * 2))
    requested = max(12, min(80, requested))
    requested += requested % 2
    return min(requested, limit)


def process(args: argparse.Namespace) -> None:
    runtime = Path(args.runtime).resolve()
    source, mask, output = (Path(value).resolve() for value in
                            (args.source, args.mask, args.output))
    preview_source = Path(args.preview_source).resolve() if args.preview_source else None
    preview_output = Path(args.preview_output).resolve() if args.preview_output else None
    if bool(preview_source) != bool(preview_output):
        raise RuntimeError("Both processing preview paths must be provided together")
    if not source.is_file() or source.suffix.lower() not in {".mp4", ".mov", ".avi"}:
        raise RuntimeError("ProPainter accepts MP4, MOV, or AVI source videos")
    if not mask.is_file():
        raise RuntimeError("The removal mask is missing")
    if source == output:
        raise RuntimeError("Output must not overwrite the source video")
    if not 0 < args.resize_ratio <= 1:
        raise RuntimeError("Processing resolution must be between 0 and 1")

    output.parent.mkdir(parents=True, exist_ok=True)
    video_part = output.with_name(output.stem + ".video.part.mp4")
    final_part = output.with_name(output.stem + ".part.mp4")
    video_part.unlink(missing_ok=True)
    final_part.unlink(missing_ok=True)
    ffmpeg = os.environ.get("IQP_FFMPEG", "ffmpeg")
    ffprobe = os.environ.get("IQP_FFPROBE", "ffprobe")
    rate, total_frames = video_info(ffprobe, source)
    if args.method == "fast":
        try:
            fast_process(source, mask, output, final_part, preview_source, preview_output,
                         ffmpeg, total_frames)
        finally:
            final_part.unlink(missing_ok=True)
        return

    streaming_root, model_root = validate(runtime)
    sys.path.insert(0, str(streaming_root))
    import cv2
    import numpy as np
    import torch
    from pytorchcv.models.common.stream import BufferedSequencer
    from pytorchcv.models.propainter import propainter
    from pytorchcv.models.propainter_rfc import propainter_rfc
    from pytorchcv.models.propainter_stream import ProPainterIterator
    from pytorchcv.models.raft import raft_things
    from propainter.propainter_video import (FrameSequencer, MaskSequencer,
                                             ProPainterSIMSequencer)

    class VideoFrames(BufferedSequencer):
        def __init__(self) -> None:
            super().__init__(data=range(total_frames))
            self.capture = cv2.VideoCapture(str(source))
            ok, first = self.capture.read()
            if not ok:
                raise RuntimeError("OpenCV could not decode the source video")
            self.first = first
            self.decoded = 0
            self.reported_first_buffer = False
            self.height, self.width = first.shape[:2]

        def _calc_data_items(self, chunks):
            indices = chunks[0]
            if indices.start != self.decoded:
                raise RuntimeError("The streaming decoder received a non-sequential request")
            frames = []
            for index in indices:
                if index == 0:
                    frame = self.first
                else:
                    ok, frame = self.capture.read()
                    if not ok:
                        raise RuntimeError(f"Source decoding ended at frame {index:,}")
                frames.append(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))
                self.decoded += 1
            if not self.reported_first_buffer:
                emit("inpainting", 10,
                     f"Prepared the first {self.decoded} source frames; computing first output...")
                self.reported_first_buffer = True
            return np.stack(frames)

        def _expand_buffer_by(self, data_chunk):
            self.buffer = np.concatenate([self.buffer, data_chunk])

        def close(self) -> None:
            self.capture.release()

    class StaticMasks(BufferedSequencer):
        def __init__(self, image: np.ndarray) -> None:
            super().__init__(data=range(total_frames))
            self.image = image

        def _calc_data_items(self, chunks):
            return np.repeat(self.image[None], len(chunks[0]), axis=0)

        def _expand_buffer_by(self, data_chunk):
            self.buffer = np.concatenate([self.buffer, data_chunk])

    use_cuda = torch.cuda.is_available()
    fp16 = bool(args.fp16 and use_cuda)
    device = "CUDA/FP16" if fp16 else ("CUDA/FP32" if use_cuda else "CPU/FP32")
    raw_frames = VideoFrames()
    preview_capture = cv2.VideoCapture(str(source)) if preview_source else None
    if preview_capture is not None and not preview_capture.isOpened():
        raw_frames.close()
        raise RuntimeError("OpenCV could not open the source preview stream")
    removal_mask = cv2.imread(str(mask), cv2.IMREAD_GRAYSCALE)
    if removal_mask is None:
        raw_frames.close()
        if preview_capture is not None:
            preview_capture.release()
        raise RuntimeError("The removal mask could not be read")
    if removal_mask.shape != (raw_frames.height, raw_frames.width):
        removal_mask = cv2.resize(removal_mask, (raw_frames.width, raw_frames.height),
                                  interpolation=cv2.INTER_NEAREST)
    removal_mask = (removal_mask > 0).astype(np.uint8)
    raw_masks = StaticMasks(removal_mask)
    vram_gb = (torch.cuda.get_device_properties(0).total_memory / 1024 ** 3
               if use_cuda else 16)
    window = safe_window(args.subvideo_length, raw_frames.width, raw_frames.height,
                         args.resize_ratio, vram_gb)

    encode = None
    try:
        emit("startup", 2, f"Loading optical-flow model on {device}...")
        raft = raft_things(pretrained=True, root=str(model_root),
                           in_normalize=False, iters=20).eval()
        emit("startup", 5, "Loading flow-completion model...")
        flow_completion = propainter_rfc(pretrained=True, root=str(model_root)).eval()
        emit("startup", 8, "Loading ProPainter inpainting model...")
        painter = propainter(pretrained=True, root=str(model_root)).eval()
        if use_cuda:
            raft, flow_completion, painter = (model.cuda() for model in
                                               (raft, flow_completion, painter))
        if window != args.subvideo_length:
            emit("startup", 9, f"Reduced the temporal window from "
                 f"{args.subvideo_length} to {window} frames for "
                 f"{raw_frames.width}x{raw_frames.height} processing.")

        frames = FrameSequencer(data=raw_frames, image_resize_ratio=args.resize_ratio,
                                use_cuda=use_cuda)
        masks = MaskSequencer(data=raw_masks, image_resize_ratio=args.resize_ratio,
                              mask_dilation=4, use_cuda=use_cuda)
        iterator = ProPainterIterator(frames=frames, masks=masks, raft_model=raft,
                                      pprfc_model=flow_completion, pp_model=painter,
                                      use_cuda=use_cuda, pp_window_size=window)
        iterator.main_sequencer = ProPainterSIMSequencer(
            inp_frames=iterator.inp_frame_sequencer, raw_frames=raw_frames,
            raw_masks=raw_masks, rescaler=frames.rescaler)

        nvenc = use_cuda and supports_nvenc(ffmpeg)
        codec = (["-c:v", "h264_nvenc", "-preset", "p6", "-tune", "hq",
                  "-rc", "vbr", "-cq", "16", "-b:v", "0"] if nvenc else
                 ["-c:v", "libx264", "-preset", "medium", "-crf", "16"])
        encode = subprocess.Popen(
            [ffmpeg, "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
             "-video_size", f"{raw_frames.width}x{raw_frames.height}", "-framerate", rate,
             "-i", "pipe:0", "-an", *codec, "-pix_fmt", "yuv420p", str(video_part)],
            stdin=subprocess.PIPE, stderr=subprocess.PIPE)
        assert encode.stdin is not None
        emit("inpainting", 10,
             f"Processing {total_frames:,} frames on {device}; " +
             ("NVENC output..." if nvenc else "CPU H.264 output..."))
        completed = 0
        with torch.inference_mode(), torch.autocast(
                device_type="cuda", dtype=torch.float16, enabled=fp16):
            for output_frames in iterator:
                encode.stdin.write(np.ascontiguousarray(output_frames).tobytes())
                completed += len(output_frames)
                if preview_source and preview_output and preview_capture is not None:
                    source_frame = None
                    for _ in output_frames:
                        ok, source_frame = preview_capture.read()
                        if not ok:
                            raise RuntimeError("Source preview decoding ended early")
                    assert source_frame is not None
                    preview_source.parent.mkdir(parents=True, exist_ok=True)
                    preview_output.parent.mkdir(parents=True, exist_ok=True)
                    temporary_source = preview_source.with_name(
                        preview_source.stem + ".tmp.jpg")
                    temporary_output = preview_output.with_name(
                        preview_output.stem + ".tmp.jpg")
                    cv2.imwrite(str(temporary_source), source_frame,
                                [cv2.IMWRITE_JPEG_QUALITY, 88])
                    cv2.imwrite(str(temporary_output), cv2.cvtColor(
                        output_frames[-1], cv2.COLOR_RGB2BGR),
                        [cv2.IMWRITE_JPEG_QUALITY, 88])
                    os.replace(temporary_source, preview_source)
                    os.replace(temporary_output, preview_output)
                emit("inpainting", 10 + min(completed, total_frames) / total_frames * 84,
                     f"Removed object from {min(completed, total_frames):,}/{total_frames:,} frames")
        encode.stdin.close()
        error = encode.stderr.read().decode(errors="replace") if encode.stderr else ""
        if encode.wait() != 0:
            raise RuntimeError(error.strip() or "FFmpeg video encoding failed")

        emit("audio", 97, "Copying source audio and finalizing...")
        mux = subprocess.run([ffmpeg, "-y", "-v", "error", "-i", str(video_part),
                              "-i", str(source), "-map", "0:v:0", "-map", "1:a?",
                              "-c:v", "copy", "-c:a", "aac", "-shortest",
                              "-movflags", "+faststart", str(final_part)],
                             capture_output=True, text=True)
        if mux.returncode:
            raise RuntimeError(mux.stderr.strip() or "FFmpeg audio mux failed")
        os.replace(final_part, output)
        emit("complete", 100, f"Saved {output.name}")
    finally:
        raw_frames.close()
        if preview_capture is not None:
            preview_capture.release()
        if encode is not None and encode.poll() is None:
            try:
                if encode.stdin:
                    encode.stdin.close()
                encode.terminate()
                encode.wait(timeout=5)
            except Exception:
                encode.kill()
                encode.wait()
        for temporary in (video_part, final_part):
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass


def self_test() -> None:
    assert float(Fraction("30000/1001")) > 29.9
    assert MODEL_FILES[0].startswith("raft_things-")
    assert safe_window(40, 1920, 1080, 1, 16) == 12
    assert safe_window(40, 1920, 1080, .5, 16) == 40
    print("ProPainter worker self-test passed")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime")
    parser.add_argument("--source")
    parser.add_argument("--mask")
    parser.add_argument("--output")
    parser.add_argument("--resize-ratio", type=float, default=0.5)
    parser.add_argument("--subvideo-length", type=int, default=40)
    parser.add_argument("--fp16", action="store_true")
    parser.add_argument("--method", choices=("fast", "propainter"), default="propainter")
    parser.add_argument("--preview-source")
    parser.add_argument("--preview-output")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not all((args.runtime, args.source, args.mask, args.output)):
        parser.error("--runtime, --source, --mask, and --output are required")
    try:
        process(args)
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
