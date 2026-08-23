#!/usr/bin/env python3
"""Automatic, memory-propagated temporal alpha stabilization for QuickPlayer."""
import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

from rvm_worker import emit, executable, probe


PREVIEW_MAXIMUM = 768
MODEL_DETAIL = 512
CUT_MEAN_DIFFERENCE = 46.0
ALPHA_VARIATION = 12


def read_exact(stream, size):
    value = bytearray(size)
    view = memoryview(value)
    offset = 0
    while offset < size:
        count = stream.readinto(view[offset:])
        if not count:
            if offset == 0:
                return None
            raise RuntimeError("Video decoding ended partway through a frame")
        offset += count
    return value


def preview_size(width, height):
    scale = min(1.0, PREVIEW_MAXIMUM / max(width, height))
    return max(16, round(width * scale)), max(16, round(height * scale))


def camera_cut(previous, current, cv2, np):
    difference = np.abs(previous.astype(np.int16) - current.astype(np.int16))
    mean = float(difference.mean())
    if mean >= CUT_MEAN_DIFFERENCE:
        return True
    if mean < 30:
        return False
    previous_hist = cv2.calcHist([previous], [0], None, [32], [0, 256])
    current_hist = cv2.calcHist([current], [0], None, [32], [0, 256])
    distance = cv2.compareHist(previous_hist, current_hist,
                               cv2.HISTCMP_BHATTACHARYYA)
    return mean >= 34 and distance >= .38


def decoder(ffmpeg, source, filters, pixel_format):
    return subprocess.Popen([ffmpeg, "-v", "error", "-i", str(source),
        "-vf", filters, "-f", "rawvideo", "-pix_fmt", pixel_format, "pipe:1"],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE)


def encoder(ffmpeg, destination, rate, width, height):
    return subprocess.Popen([ffmpeg, "-y", "-v", "error", "-f", "rawvideo",
        "-pix_fmt", "gray", "-s", f"{width}x{height}", "-r", rate,
        "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "medium",
        "-crf", "10", "-pix_fmt", "yuv420p", "-r", rate,
        "-fps_mode", "cfr", str(destination)], stdin=subprocess.PIPE,
        stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)


def segment_for_frame(cuts, frame):
    for start, end in zip(cuts, cuts[1:]):
        if start <= frame < end:
            return start, end
    return 0, cuts[-1]


def temporal_bounds(cuts, frame, window):
    start, end = segment_for_frame(cuts, frame)
    length = min(end - start, window * 2 + 1)
    lower = min(max(start, frame - window), end - length)
    return lower, lower + length


def temporal_consensus(alpha, frame, cuts, window, threshold, cv2, np):
    lower, upper = temporal_bounds(cuts, frame, window)
    masks = np.asarray(alpha[lower:upper]) >= max(1, threshold)
    kernel = np.ones((3, 3), np.uint8)
    support = np.zeros(masks.shape[1:], np.uint16)
    for mask in masks:
        support += cv2.dilate(mask.astype(np.uint8), kernel)
    persistent = support >= (len(masks) // 2 + 1)
    return cv2.dilate(persistent.astype(np.uint8), kernel).astype(bool)


def alpha_score(alpha, frame, cuts, window, threshold, cv2, np):
    visible = alpha[frame] >= max(1, threshold)
    persistent = temporal_consensus(alpha, frame, cuts, window, threshold,
                                    cv2, np)
    supported = visible & persistent
    unsupported = visible & ~persistent
    confident = alpha[frame] >= min(255, max(1, threshold) + 32)
    return (int(supported.sum()) + int((confident & persistent).sum()) // 4 -
            int(unsupported.sum()) * 2)


def clean_anchor(alpha, threshold, cv2, np):
    mask = alpha >= max(1, threshold)
    count, labels, stats, _ = cv2.connectedComponentsWithStats(
        mask.astype(np.uint8), connectivity=8)
    minimum = max(4, mask.size // 100_000)
    keep = np.zeros(count, bool)
    if count > 1:
        keep[1:] = stats[1:, cv2.CC_STAT_AREA] >= minimum
    cleaned = keep[labels].astype(np.uint8) * 255
    return cv2.morphologyEx(cleaned, cv2.MORPH_CLOSE,
                            np.ones((3, 3), np.uint8))


def choose_anchors(alpha, cuts, window, threshold, cv2, np):
    span = max(3, window * 2 + 1)
    anchors = []
    for segment_start, segment_end in zip(cuts, cuts[1:]):
        for start in range(segment_start, segment_end, span):
            end = min(segment_end, start + span)
            best = max(range(start, end),
                key=lambda index: alpha_score(alpha, index, cuts, window,
                                              threshold, cv2, np))
            if not anchors or best != anchors[-1]:
                anchors.append(best)
        if segment_start and segment_start not in anchors:
            anchors.append(segment_start)
    return sorted(set(anchors))


def instability_weight(alpha, frame, cuts, window, threshold, strength,
                       cv2, np):
    lower, upper = temporal_bounds(cuts, frame, window)
    if upper - lower < 3:
        return np.zeros(alpha.shape[1:], np.float32)
    values = np.asarray(alpha[lower:upper])
    maximum = values.max(axis=0)
    minimum = values.min(axis=0)
    unstable = ((maximum.astype(np.int16) - minimum.astype(np.int16) >=
                 ALPHA_VARIATION) & (maximum >= max(1, threshold)))
    unstable = cv2.dilate(unstable.astype(np.uint8),
                          np.ones((3, 3), np.uint8)).astype(np.float32)
    unstable = cv2.GaussianBlur(unstable, (3, 3), .65)
    return np.clip(unstable * strength, 0, 1)


def blend(original, stabilized, weight, np):
    return np.rint(original.astype(np.float32) * (1 - weight) +
                   stabilized.astype(np.float32) * weight).clip(0, 255).astype(np.uint8)


def analyse_inputs(foreground, alpha_path, rate, total, preview_width,
                   preview_height, cache_path, ffmpeg, cv2, np):
    alpha = np.memmap(cache_path, mode="w+", dtype=np.uint8,
                      shape=(total, preview_height, preview_width))
    gray_decode = decoder(ffmpeg, foreground,
        f"fps={rate},scale={preview_width}:{preview_height}:flags=area,format=gray",
        "gray")
    alpha_decode = decoder(ffmpeg, alpha_path,
        f"fps={rate},scale={preview_width}:{preview_height}:flags=area,format=gray",
        "gray")
    cuts, previous, count = [0], None, 0
    try:
        while count < total:
            gray_data = read_exact(gray_decode.stdout, preview_width * preview_height)
            alpha_data = read_exact(alpha_decode.stdout, preview_width * preview_height)
            if gray_data is None and alpha_data is None:
                break
            if gray_data is None or alpha_data is None:
                raise RuntimeError("Foreground and alpha decoding ended on different frames")
            gray = np.frombuffer(gray_data, np.uint8).reshape(
                preview_height, preview_width)
            alpha[count] = np.frombuffer(alpha_data, np.uint8).reshape(
                preview_height, preview_width)
            if previous is not None and camera_cut(previous, gray, cv2, np):
                cuts.append(count)
            previous = gray.copy()
            count += 1
        gray_decode.stdout.close(); alpha_decode.stdout.close()
        gray_error = gray_decode.stderr.read().decode(errors="replace")
        alpha_error = alpha_decode.stderr.read().decode(errors="replace")
        if gray_decode.wait() or alpha_decode.wait():
            raise RuntimeError(gray_error.strip() or alpha_error.strip() or
                               "Input analysis decoder failed")
        if count not in (total, total - 1):
            raise RuntimeError(f"Input analysis decoded {count}/{total} frames")
        if count != total:
            alpha.flush()
            del alpha
            with cache_path.open("r+b") as stream:
                stream.truncate(count * preview_width * preview_height)
            alpha = np.memmap(cache_path, mode="r+", dtype=np.uint8,
                shape=(count, preview_height, preview_width))
        cuts.append(count)
        return alpha, sorted(set(cuts)), count
    except BaseException:
        for process in (gray_decode, alpha_decode):
            try:
                if process.poll() is None: process.kill()
                process.wait()
            except OSError:
                pass
        raise


def anchor_mask(alpha, frame, cuts, window, threshold, cv2, np):
    persistent = temporal_consensus(alpha, frame, cuts, window, threshold,
                                    cv2, np)
    candidate = np.where(persistent, alpha[frame], 0).astype(np.uint8)
    return clean_anchor(candidate, threshold, cv2, np)


def write_anchor_files(folder, alpha, anchors, cuts, window, threshold, fps,
                       cv2, np):
    from PIL import Image
    folder.mkdir(parents=True, exist_ok=True)
    first = anchors[0]
    Image.fromarray(anchor_mask(alpha, first, cuts, window, threshold, cv2, np),
                    "L").save(
        folder / "initial-mask.png")
    cut_frames = set(cuts[1:-1])
    used_times = set()
    for frame in anchors[1:]:
        frame_ms = round(frame * 1000 / fps)
        while frame_ms in used_times:
            frame_ms += 1
        used_times.add(frame_ms)
        prefix = "reset" if frame in cut_frames else "correction"
        Image.fromarray(anchor_mask(alpha, frame, cuts, window, threshold,
                                    cv2, np), "L").save(
            folder / f"{prefix}-{frame_ms}.png")
    return first


def preview_metadata(folder, width, height, rate, total):
    folder.mkdir(parents=True, exist_ok=True)
    temporary = folder / "preview.tmp"
    temporary.write_text(json.dumps({"width": width, "height": height,
        "frameRate": rate, "totalFrames": total,
        "decisionFile": "decision-flags.bitplanes"}), encoding="utf-8")
    os.replace(temporary, folder / "preview.json")


def append_preview_frames(raw_path, model_size, original, start, end,
                          preview_folder, cuts, window, threshold, strength,
                          cv2, np):
    if preview_folder is None:
        return
    model_width, model_height = model_size
    model = np.memmap(raw_path, mode="r", dtype=np.uint8,
        shape=(original.shape[0], model_height, model_width))
    with open(preview_folder / "output-alpha.gray8", "ab") as output, \
            open(preview_folder / "decision-flags.bitplanes", "ab") as decisions:
        for frame in range(start, end):
            stabilized = model[frame]
            if stabilized.shape != original[frame].shape:
                stabilized = cv2.resize(stabilized,
                    (original.shape[2], original.shape[1]),
                    interpolation=cv2.INTER_LINEAR)
            weight = instability_weight(original, frame, cuts, window,
                                          threshold, strength, cv2, np)
            cleaned = blend(original[frame], stabilized, weight, np)
            difference = cleaned.astype(np.int16) - original[frame].astype(np.int16)
            output.write(memoryview(cleaned).cast("B"))
            decisions.write(memoryview(np.packbits(
                (difference < -1).reshape(-1), bitorder="little")).cast("B"))
            decisions.write(memoryview(np.packbits(
                (difference > 1).reshape(-1), bitorder="little")).cast("B"))
        output.flush(); decisions.flush()
    del model


def run_model(args, source, runtime, model_output, raw_path, anchors,
              first_anchor, fps, total, model_size, original, preview_folder,
              cuts, cv2, np):
    worker = Path(__file__).with_name("matanyone2_worker.py")
    command = [sys.executable, str(worker), "--source", str(source),
        "--output", str(model_output), "--runtime", str(runtime),
        "--mask", str(anchors / "initial-mask.png"), "--mask-frame-ms",
        str(round(first_anchor * 1000 / fps)), "--anchor-folder", str(anchors),
        "--max-size", str(MODEL_DETAIL), "--max-mem-frames",
        str(max(2, min(30, args.window * 2 + 1))), "--compile-mode", "eager",
        "--disable-previews", "--raw-alpha-output", str(raw_path)]
    process = subprocess.Popen(command, stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT, text=True, bufsize=1, env=os.environ.copy())
    preview_count = 0
    reported_percent = 0.0
    last_plain_message = ""
    try:
        while True:
            line = process.stdout.readline()
            if not line:
                if process.poll() is not None:
                    break
                continue
            try:
                value = json.loads(line)
            except ValueError:
                if line.strip():
                    last_plain_message = line.strip()
                continue
            message = str(value.get("message", ""))
            match = re.search(r"Processed (\d+)/(\d+) frames", message)
            if match and raw_path.is_file():
                available = min(total, int(match.group(1)))
                if available > preview_count:
                    append_preview_frames(raw_path, model_size, original,
                        preview_count, available, preview_folder, cuts,
                        args.window, args.alpha_threshold, args.strength / 100,
                        cv2, np)
                    preview_count = available
            reported_percent = max(reported_percent,
                min(100.0, float(value.get("percent", 0))))
            emit("temporal-stabilization", 5 + reported_percent * .85,
                 message or "Propagating stable alpha memory...")
        code = process.wait()
        if code:
            raise RuntimeError(last_plain_message or
                f"MatAnyone 2 stabilization failed (exit code {code})")
        if preview_count < total:
            append_preview_frames(raw_path, model_size, original, preview_count,
                total, preview_folder, cuts, args.window, args.alpha_threshold,
                args.strength / 100, cv2, np)
    except BaseException:
        if process.poll() is None:
            process.kill()
        process.wait()
        raise


def encode_blended_output(args, original_path, model_path, destination,
                          rate, width, height, original_preview, cuts,
                          ffmpeg, cv2, np):
    original = decoder(ffmpeg, original_path, f"fps={rate},format=gray", "gray")
    model = decoder(ffmpeg, model_path, f"fps={rate},format=gray", "gray")
    temporary = destination.with_name(destination.stem + ".stabilizing.mkv")
    temporary.unlink(missing_ok=True)
    encode = encoder(ffmpeg, temporary, rate, width, height)
    count = 0
    try:
        while count < original_preview.shape[0]:
            source_data = read_exact(original.stdout, width * height)
            model_data = read_exact(model.stdout, width * height)
            if source_data is None or model_data is None:
                break
            source_alpha = np.frombuffer(source_data, np.uint8).reshape(height, width)
            model_alpha = np.frombuffer(model_data, np.uint8).reshape(height, width)
            weight = instability_weight(original_preview, count, cuts,
                args.window, args.alpha_threshold, args.strength / 100, cv2, np)
            if weight.shape != source_alpha.shape:
                weight = cv2.resize(weight, (width, height),
                                    interpolation=cv2.INTER_LINEAR)
            cleaned = blend(source_alpha, model_alpha, weight, np)
            if encode.stdin.write(memoryview(cleaned).cast("B")) != width * height:
                raise RuntimeError("Stabilized alpha encoder ended early")
            count += 1
            if count % 30 == 0:
                emit("temporal-stabilization", 90 + 9 * count /
                     original_preview.shape[0],
                     f"Saving stabilized alpha {count}/{original_preview.shape[0]}")
        encode.stdin.close(); encode.stdin = None
        original.stdout.close(); model.stdout.close()
        errors = [original.stderr.read().decode(errors="replace"),
                  model.stderr.read().decode(errors="replace"),
                  encode.stderr.read().decode(errors="replace")]
        codes = original.wait(), model.wait(), encode.wait()
        if any(codes) or count != original_preview.shape[0]:
            raise RuntimeError(next((value.strip() for value in errors if value.strip()),
                               "Stabilized alpha encoding failed"))
        os.replace(temporary, destination)
    except BaseException:
        for child in (original, model, encode):
            try:
                if child.poll() is None: child.kill()
                child.wait()
            except OSError:
                pass
        temporary.unlink(missing_ok=True)
        raise


def process(args):
    import cv2
    import numpy as np
    from matanyone2_worker import processing_size

    foreground = args.foreground or args.output / "foreground.mp4"
    alpha_path = args.alpha or args.output / "alpha.mkv"
    destination = args.destination or alpha_path
    if not foreground.is_file() or not alpha_path.is_file():
        raise RuntimeError("Foreground or alpha media is missing")
    width, height, rate, fps, duration = probe(foreground)
    alpha_width, alpha_height, alpha_rate, _, _ = probe(alpha_path)
    if (alpha_width, alpha_height, alpha_rate) != (width, height, rate):
        raise RuntimeError("Foreground and alpha dimensions or frame rates differ")
    total = max(1, round(duration * fps))
    preview_width, preview_height = preview_size(width, height)
    model_width, model_height = processing_size(width, height, MODEL_DETAIL)
    ffmpeg = executable("ffmpeg")
    destination.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="iqp-auto-alpha-") as temporary_value:
        temporary = Path(temporary_value)
        original_cache = temporary / "input-alpha.gray8"
        emit("temporal-stabilization", 0, "Analysing alpha stability and camera cuts...")
        original, cuts, total = analyse_inputs(foreground, alpha_path, rate,
            total, preview_width, preview_height, original_cache, ffmpeg, cv2, np)
        anchors = choose_anchors(original, cuts, args.window,
                                 args.alpha_threshold, cv2, np)
        anchor_folder = temporary / "anchors"
        first_anchor = write_anchor_files(anchor_folder, original, anchors, cuts,
            args.window, args.alpha_threshold, fps, cv2, np)
        if args.preview_cache:
            preview_metadata(args.preview_cache, preview_width, preview_height,
                             rate, total)
            for name in ("output-alpha.gray8", "decision-flags.bitplanes"):
                open(args.preview_cache / name, "wb").close()
        model_output = temporary / "model-output"
        raw_path = temporary / "model-alpha.u8"
        emit("temporal-stabilization", 4,
             f"Selected {len(anchors)} automatic memory anchors across "
             f"{len(cuts) - 1} camera shots")
        run_model(args, foreground, args.runtime, model_output, raw_path,
            anchor_folder, first_anchor, fps, total, (model_width, model_height),
            original, args.preview_cache, cuts, cv2, np)
        encode_blended_output(args, alpha_path, model_output / "alpha.mkv",
            destination, rate, width, height, original, cuts, ffmpeg, cv2, np)
        original.flush()
        del original
    emit("temporal-stabilization", 100,
         f"Automatic alpha stabilization completed using {len(anchors)} memory anchors")


def self_test():
    import cv2
    import numpy as np

    background = np.zeros((8, 8), np.uint8)
    foreground = np.full((8, 8), 255, np.uint8)
    assert camera_cut(background, foreground, cv2, np)
    assert not camera_cut(background, background, cv2, np)
    alpha = np.zeros((9, 8, 8), np.uint8)
    alpha[:, 2:6, 2:6] = 220
    alpha[4, 2:6, 2:6] = 0
    cuts = [0, 9]
    weight = instability_weight(alpha, 4, cuts, 4, 120, 1, cv2, np)
    assert weight[3, 3] == 1 and weight[0, 0] < .2
    anchors = choose_anchors(alpha, cuts, 2, 120, cv2, np)
    assert anchors and 4 not in anchors
    consensus_alpha = np.zeros((9, 32, 32), np.uint8)
    consensus_alpha[:, 10:22, 10:22] = 220
    consensus_alpha[4, 10:22, 10:22] = 0
    consensus_alpha[1:5, 2:6, 2:6] = 220
    persistent = anchor_mask(consensus_alpha, 1, cuts, 4, 120, cv2, np)
    assert persistent[12, 12] == 255 and persistent[3, 3] == 0
    automatic = np.zeros((9, 32, 32), np.uint8)
    automatic[:, 10:22, 10:22] = 220
    automatic[:5, 2:6, 2:6] = 220       # real detail, absent for four frames
    automatic[:4, 26:30, 26:30] = 220   # false detail, present for four frames
    selected = choose_anchors(automatic, cuts, 4, 120, cv2, np)[0]
    automatic_mask = anchor_mask(automatic, selected, cuts, 4, 120, cv2, np)
    assert selected < 5 and automatic_mask[3, 3] == 255
    assert automatic_mask[27, 27] == 0
    cleaned = clean_anchor(alpha[0], 120, cv2, np)
    assert cleaned[3, 3] == 255 and cleaned[0, 0] == 0
    original = np.array([[0, 200]], np.uint8)
    stabilized = np.array([[200, 0]], np.uint8)
    mixed = blend(original, stabilized, np.array([[.5, .25]], np.float32), np)
    assert tuple(mixed[0]) == (100, 150)
    print("Automatic temporal alpha stabilization worker self-test passed")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path)
    parser.add_argument("--foreground", type=Path)
    parser.add_argument("--alpha", type=Path)
    parser.add_argument("--destination", type=Path)
    parser.add_argument("--preview-cache", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--window", type=int, default=3)
    parser.add_argument("--strength", type=int, default=100)
    parser.add_argument("--alpha-threshold", type=int, default=120)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if args.output is None and (args.foreground is None or args.alpha is None or
                                args.destination is None):
        parser.error("--output or --foreground/--alpha/--destination is required")
    if args.runtime is None or not args.runtime.is_dir():
        parser.error("--runtime must identify the installed processing runtime")
    if not 1 <= args.window <= 12:
        parser.error("--window must be 1-12 frames")
    if not 25 <= args.strength <= 100:
        parser.error("--strength must be 25-100 percent")
    if not 0 <= args.alpha_threshold <= 255:
        parser.error("--alpha-threshold must be 0-255")
    args.output = args.output.resolve() if args.output else None
    args.foreground = args.foreground.resolve() if args.foreground else None
    args.alpha = args.alpha.resolve() if args.alpha else None
    args.destination = args.destination.resolve() if args.destination else None
    args.preview_cache = args.preview_cache.resolve() if args.preview_cache else None
    args.runtime = args.runtime.resolve()
    process(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", 0, str(error))
        raise SystemExit(1)
