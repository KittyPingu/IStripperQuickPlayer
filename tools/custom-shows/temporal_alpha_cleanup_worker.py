#!/usr/bin/env python3
"""Automatic, memory-propagated temporal alpha stabilization for QuickPlayer."""
import argparse
import json
import math
import os
import re
import subprocess
import sys
import tempfile
from collections import OrderedDict
from contextlib import ExitStack
from pathlib import Path

from rvm_worker import emit, executable, probe


PREVIEW_MAXIMUM = 768
MODEL_DETAIL = 512
CUT_MEAN_DIFFERENCE = 46.0
ALPHA_VARIATION = 12
ANALYSIS_CACHE_REVISION = 3
FLOW_CACHE_BYTES = 128 * 1024 * 1024


class MotionAlignment:
    """Align alpha evidence using source-image motion, without modifying alpha.

    Adjacent flows are composed in the current frame's coordinates. Each alpha
    is sampled only once, avoiding repeated resampling of soft edges. The cache
    is bounded independently of clip length and never pairs frames across cuts.
    """

    def __init__(self, gray, cuts, window, cv2, np, engine=None):
        self.gray, self.cuts, self.cv2, self.np = gray, cuts, cv2, np
        self.height, self.width = gray.shape[1:]
        self.grid = np.stack(np.meshgrid(np.arange(self.width, dtype=np.float32),
            np.arange(self.height, dtype=np.float32)), axis=-1)
        from nvidia_optical_flow import NvidiaOpticalFlow
        self.engine = engine if engine is not None else NvidiaOpticalFlow(self.width, self.height)
        # Two float32 XY fields and two float32 validity maps per cached pair.
        pair_bytes = self.width * self.height * 24
        self.cache_limit = max(1, min(window * 2, FLOW_CACHE_BYTES // pair_bytes))
        self.pairs = OrderedDict()

    def sample(self, value, coordinates):
        return self.cv2.remap(value, coordinates, None, self.cv2.INTER_LINEAR,
            borderMode=self.cv2.BORDER_CONSTANT, borderValue=0)

    def inside(self, coordinates):
        np = self.np
        x, y = coordinates[..., 0], coordinates[..., 1]
        return (np.isfinite(x) & np.isfinite(y) &
            (x >= 0) & (x <= self.width - 1) & (y >= 0) & (y <= self.height - 1))

    def reliable(self, source, target, flow, reverse, cost):
        np, cv2 = self.np, self.cv2
        coordinates = self.grid + flow
        inverse = self.sample(reverse, coordinates)
        # Forward/backward round-trip error, in preview-image pixels. Reject
        # uncertain correspondence; do not shrink its displacement toward zero.
        error = ((flow[..., 0] + inverse[..., 0]) ** 2 +
                 (flow[..., 1] + inverse[..., 1]) ** 2)
        tolerance = 1.0 + .01 * ((flow[..., 0] ** 2 + flow[..., 1] ** 2) +
                                 (inverse[..., 0] ** 2 + inverse[..., 1] ** 2))
        matched = self.sample(target, coordinates)
        photo_error = np.abs(source.astype(np.float32) - matched.astype(np.float32))
        # Flat patches cannot establish correspondence, even if both flows are 0.
        image = source.astype(np.float32)
        mean = cv2.boxFilter(image, -1, (7, 7))
        variance = cv2.boxFilter(image * image, -1, (7, 7)) - mean * mean
        return (self.inside(coordinates) & np.isfinite(error) &
                (error <= tolerance) & (photo_error <= 20) &
                (variance >= 4) & (cost <= 40)).astype(np.float32)

    def pair(self, first):
        if first + 1 in self.cuts or not 0 <= first < len(self.gray) - 1:
            raise ValueError("Optical flow cannot cross a camera cut")
        if first in self.pairs:
            self.pairs.move_to_end(first)
            return self.pairs[first]
        # Evict before allocating another pair, including for large windows.
        while len(self.pairs) >= self.cache_limit:
            self.pairs.popitem(last=False)
        np = self.np
        source = np.ascontiguousarray(self.gray[first])
        target = np.ascontiguousarray(self.gray[first + 1])
        forward, backward, forward_cost, backward_cost = self.engine.compute(source, target)
        pair = (forward, backward, self.reliable(source, target, forward, backward, forward_cost),
                self.reliable(target, source, backward, forward, backward_cost))
        self.pairs[first] = pair
        return pair

    def close(self):
        self.pairs.clear()
        self.gray = None
        self.engine.close()

    def alpha_range(self, alpha, frame, lower, upper):
        np = self.np
        minimum = alpha[frame].astype(np.float32)
        maximum = minimum.copy()
        support = np.ones(minimum.shape, np.uint8)
        source = self.gray[frame].astype(np.float32)
        for step, stop in ((-1, lower - 1), (1, upper)):
            coordinates = self.grid.copy()
            valid = np.ones(minimum.shape, bool)
            for neighbor in range(frame + step, stop, step):
                forward, backward, forward_valid, backward_valid = self.pair(
                    min(neighbor, neighbor - step))
                flow, confidence = (backward, backward_valid) if step < 0 else \
                    (forward, forward_valid)
                valid &= self.sample(confidence, coordinates) >= .999
                coordinates += self.sample(flow, coordinates)
                valid &= self.inside(coordinates)
                # Check the composed path against the target image too. A series
                # of plausible local matches can still drift over a long window.
                matched = self.sample(self.gray[neighbor], coordinates)
                valid &= np.abs(source - matched.astype(np.float32)) <= 20
                warped = self.sample(alpha[neighbor].astype(np.float32), coordinates)
                np.minimum(minimum, warped, out=minimum, where=valid)
                np.maximum(maximum, warped, out=maximum, where=valid)
                support += valid
        # At least two neighboring observations must corroborate the comparison.
        return minimum, maximum, support >= 3


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


def paired_gray_alpha_decoder(ffmpeg, foreground, alpha_path, rate,
                              width, height):
    filters = (
        f"[0:v]fps={rate},scale={width}:{height}:flags=area,format=gray,"
        "setsar=1,setpts=PTS-STARTPTS[fg];"
        f"[1:v]fps={rate},scale={width}:{height}:flags=area,format=gray,"
        "setsar=1,setpts=PTS-STARTPTS[a];"
        "[fg][a]alphamerge=shortest=1:repeatlast=0,format=ya8[out]")
    return subprocess.Popen([ffmpeg, "-v", "error", "-i", str(foreground),
        "-i", str(alpha_path), "-filter_complex", filters, "-map", "[out]",
        "-an", "-f", "rawvideo", "-pix_fmt", "ya8", "pipe:1"],
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


def frame_components(alpha, threshold, cv2, np):
    mask = alpha >= max(1, threshold)
    count, labels, stats, centroids = cv2.connectedComponentsWithStats(
        mask.astype(np.uint8), connectivity=8)
    minimum = max(6, mask.size // 75_000)
    records = []
    for label in range(1, count):
        area = int(stats[label, cv2.CC_STAT_AREA])
        if area < minimum:
            continue
        x = int(stats[label, cv2.CC_STAT_LEFT])
        y = int(stats[label, cv2.CC_STAT_TOP])
        width = int(stats[label, cv2.CC_STAT_WIDTH])
        height = int(stats[label, cv2.CC_STAT_HEIGHT])
        component_alpha = alpha[labels == label]
        records.append({"label": label, "x": x, "y": y, "width": width,
            "height": height, "area": area,
            "confidence": int(component_alpha.sum()) / 255,
            "cx": float(centroids[label, 0]), "cy": float(centroids[label, 1])})
    if len(records) > 128:
        records = sorted(records, key=lambda item: item["area"], reverse=True)[:128]
    return labels, records


def component_match_score(component, observations, frame):
    previous = observations[-1]
    gap = frame - previous["frame"]
    if gap <= 0:
        return None
    predicted_x, predicted_y = previous["cx"], previous["cy"]
    if len(observations) > 1:
        earlier = observations[-2]
        elapsed = max(1, previous["frame"] - earlier["frame"])
        predicted_x += (previous["cx"] - earlier["cx"]) * gap / elapsed
        predicted_y += (previous["cy"] - earlier["cy"]) * gap / elapsed
    distance = math.hypot(component["cx"] - predicted_x,
                          component["cy"] - predicted_y)
    motion_limit = max(12.0, .8 * max(previous["width"], previous["height"]) +
                       4.0 * gap)
    area_ratio = component["area"] / max(1, previous["area"])
    if distance > motion_limit or not .12 <= area_ratio <= 8:
        return None
    size_change = abs(math.log(max(area_ratio, 1e-6)))
    return distance / motion_limit + size_change * .22


def detect_components(alpha, threshold, cv2, np):
    return [frame_components(alpha[frame], threshold, cv2, np)[1]
            for frame in range(alpha.shape[0])]


def track_components(alpha, cuts, window, threshold, cv2, np,
                     detected=None):
    by_frame = [[] for _ in range(alpha.shape[0])]
    tracks = {}
    next_track = 0
    for shot_start, shot_end in zip(cuts, cuts[1:]):
        active = set()
        for frame in range(shot_start, shot_end):
            source = detected[frame] if detected is not None else \
                frame_components(alpha[frame], threshold, cv2, np)[1]
            current = [dict(component) for component in source]
            candidates = []
            for component_index, component in enumerate(current):
                for track_id in active:
                    observations = tracks[track_id]
                    if frame - observations[-1]["frame"] > window + 1:
                        continue
                    score = component_match_score(component, observations, frame)
                    if score is not None:
                        candidates.append((score, component_index, track_id))
            assigned_components, assigned_tracks = set(), set()
            for _, component_index, track_id in sorted(candidates):
                if component_index in assigned_components or track_id in assigned_tracks:
                    continue
                component = current[component_index]
                component["frame"] = frame
                component["track"] = track_id
                tracks[track_id].append(component)
                assigned_components.add(component_index)
                assigned_tracks.add(track_id)
            for component_index, component in enumerate(current):
                if component_index in assigned_components:
                    continue
                component["frame"] = frame
                component["track"] = next_track
                tracks[next_track] = [component]
                active.add(next_track)
                next_track += 1
            active = {track_id for track_id in active
                if frame - tracks[track_id][-1]["frame"] <= window + 1}
            by_frame[frame] = current
    return by_frame, tracks


def persistent_component(component, frame, cuts, window, tracks):
    lower, upper = temporal_bounds(cuts, frame, window)
    observations = tracks[component["track"]]
    present = sum(lower <= item["frame"] < upper for item in observations)
    return present >= (upper - lower) // 2 + 1


def alpha_score(alpha, frame, cuts, window, tracking_strength,
                components, tracks):
    persistent = transient = 0.0
    untracked = 0.0
    for component in components[frame]:
        untracked += component["confidence"]
        if persistent_component(component, frame, cuts, window, tracks):
            persistent += component["area"] + component["confidence"] * .25
        else:
            transient += component["area"]
    tracked = persistent - transient * 2
    return untracked * (1 - tracking_strength) + tracked * tracking_strength


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


def choose_anchors(alpha, cuts, window, tracking_strength, components, tracks):
    span = max(3, window * 2 + 1)
    anchors = []
    for segment_start, segment_end in zip(cuts, cuts[1:]):
        for start in range(segment_start, segment_end, span):
            end = min(segment_end, start + span)
            best = max(range(start, end),
                key=lambda index: alpha_score(alpha, index, cuts, window,
                    tracking_strength, components, tracks))
            if not anchors or best != anchors[-1]:
                anchors.append(best)
        if segment_start and segment_start not in anchors:
            anchors.append(segment_start)
    return sorted(set(anchors))


def instability_weight(alpha, frame, cuts, window, threshold, strength,
                       tracking_strength, components, tracks, cv2, np, motion=None):
    lower, upper = temporal_bounds(cuts, frame, window)
    if upper - lower < 3:
        return np.zeros(alpha.shape[1:], np.float32)
    if motion is not None:
        minimum, maximum, supported = motion.alpha_range(alpha, frame, lower, upper)
    else:
        values = np.asarray(alpha[lower:upper], dtype=np.float32)
        maximum, minimum = values.max(axis=0), values.min(axis=0)
        supported = np.ones(alpha.shape[1:], bool)
    unstable = ((maximum - minimum >= ALPHA_VARIATION) &
                (maximum >= max(1, threshold)) & supported)
    unstable = cv2.dilate(unstable.astype(np.uint8),
                          np.ones((3, 3), np.uint8)).astype(np.float32)
    unstable = cv2.GaussianBlur(unstable, (3, 3), .65)
    # Dilation/feathering must not reintroduce correction into rejected matches.
    unstable *= supported
    labels, _ = frame_components(alpha[frame], threshold, cv2, np)
    protected = np.zeros(alpha.shape[1:], np.uint8)
    for component in components[frame]:
        if persistent_component(component, frame, cuts, window, tracks):
            protected[labels == component["label"]] = 1
    if protected.any():
        protected = cv2.erode(protected, np.ones((3, 3), np.uint8))
        unstable *= 1 - protected.astype(np.float32) * tracking_strength
    return np.clip(unstable * strength, 0, 1)


def blend(original, stabilized, weight, np):
    return np.rint(original.astype(np.float32) * (1 - weight) +
                   stabilized.astype(np.float32) * weight).clip(0, 255).astype(np.uint8)


def analyse_inputs(foreground, alpha_path, rate, total, preview_width,
                   preview_height, cache_path, gray_path, ffmpeg, cv2, np):
    alpha = np.memmap(cache_path, mode="w+", dtype=np.uint8,
                      shape=(total, preview_height, preview_width))
    images = np.memmap(gray_path, mode="w+", dtype=np.uint8, shape=alpha.shape)
    paired_decode = None
    cuts, previous, count = [0], None, 0
    try:
        paired_decode = paired_gray_alpha_decoder(ffmpeg, foreground, alpha_path,
            rate, preview_width, preview_height)
        while count < total:
            paired_data = read_exact(paired_decode.stdout,
                preview_width * preview_height * 2)
            if paired_data is None:
                break
            paired = np.frombuffer(paired_data, np.uint8).reshape(
                preview_height, preview_width, 2)
            gray = paired[:, :, 0]
            images[count] = gray
            alpha[count] = paired[:, :, 1]
            if previous is not None and camera_cut(previous, gray, cv2, np):
                cuts.append(count)
            previous = gray.copy()
            count += 1
        paired_decode.stdout.close()
        paired_error = paired_decode.stderr.read().decode(errors="replace")
        if paired_decode.wait():
            raise RuntimeError(paired_error.strip() or
                               "Paired input analysis decoder failed")
        if count < 1 or count not in (total, total - 1):
            raise RuntimeError(f"Input analysis decoded {count}/{total} frames")
        if count != total:
            alpha.flush()
            images.flush()
            alpha._mmap.close()
            images._mmap.close()
            for path in (cache_path, gray_path):
                with path.open("r+b") as stream:
                    stream.truncate(count * preview_width * preview_height)
            alpha = np.memmap(cache_path, mode="r+", dtype=np.uint8,
                shape=(count, preview_height, preview_width))
            images = np.memmap(gray_path, mode="r+", dtype=np.uint8, shape=alpha.shape)
        cuts.append(count)
        return alpha, images, sorted(set(cuts)), count
    except BaseException:
        try:
            if paired_decode is not None:
                if paired_decode.poll() is None: paired_decode.kill()
                paired_decode.wait()
        except OSError:
            pass
        alpha._mmap.close()
        images._mmap.close()
        raise


def source_stamp(path):
    value = path.stat()
    return {"path": str(path), "size": value.st_size,
            "modifiedNs": value.st_mtime_ns}


def analysis_identity(foreground, alpha_path, width, height, rate, total):
    return {"revision": ANALYSIS_CACHE_REVISION,
            "foreground": source_stamp(foreground),
            "alpha": source_stamp(alpha_path), "width": width,
            "height": height, "frameRate": rate, "expectedFrames": total}


def write_json_atomic(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, separators=(",", ":")),
                         encoding="utf-8")
    os.replace(temporary, path)


def load_cached_analysis(folder, identity, preview_width, preview_height, np):
    metadata_path = folder / "analysis.json"
    raw_path = folder / "input-alpha.gray8"
    gray_path = folder / "input-source.gray8"
    try:
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        count = int(metadata["frames"])
        cuts = [int(value) for value in metadata["cuts"]]
        expected = count * preview_width * preview_height
        if metadata.get("identity") != identity or count < 1 or \
                not cuts or cuts[0] != 0 or cuts[-1] != count or \
                cuts != sorted(set(cuts)) or \
                raw_path.stat().st_size != expected or gray_path.stat().st_size != expected:
            return None
        return (np.memmap(raw_path, mode="r", dtype=np.uint8,
                          shape=(count, preview_height, preview_width)),
                np.memmap(gray_path, mode="r", dtype=np.uint8,
                          shape=(count, preview_height, preview_width)),
                cuts, count)
    except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError):
        return None


def cached_analysis(folder, identity, foreground, alpha_path, rate, total,
                    preview_width, preview_height, ffmpeg, cv2, np):
    cached = load_cached_analysis(folder, identity, preview_width,
                                  preview_height, np)
    if cached is not None:
        emit("temporal-stabilization", 1,
             "Reusing cached source frames, alpha analysis and camera cuts...")
        return cached
    folder.mkdir(parents=True, exist_ok=True)
    staged = folder / "input-alpha.building"
    staged_gray = folder / "input-source.building"
    staged.unlink(missing_ok=True)
    staged_gray.unlink(missing_ok=True)
    # Metadata is the commit marker for the pair of raw caches.
    (folder / "analysis.json").unlink(missing_ok=True)
    try:
        alpha, images, cuts, count = analyse_inputs(foreground, alpha_path, rate, total,
            preview_width, preview_height, staged, staged_gray, ffmpeg, cv2, np)
        alpha.flush()
        images.flush()
        del alpha, images
        raw_path = folder / "input-alpha.gray8"
        os.replace(staged, raw_path)
        gray_path = folder / "input-source.gray8"
        os.replace(staged_gray, gray_path)
        write_json_atomic(folder / "analysis.json",
                          {"identity": identity, "frames": count,
                           "cuts": cuts})
        for old in folder.glob("components-*.json"):
            old.unlink(missing_ok=True)
        return (np.memmap(raw_path, mode="r", dtype=np.uint8,
                          shape=(count, preview_height, preview_width)),
                np.memmap(gray_path, mode="r", dtype=np.uint8,
                          shape=(count, preview_height, preview_width)),
                cuts, count)
    finally:
        staged.unlink(missing_ok=True)
        staged_gray.unlink(missing_ok=True)


def cached_components(folder, identity, alpha, threshold, cv2, np):
    path = folder / f"components-{threshold}.json"
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        frames = value["frames"]
        if value.get("identity") == identity and len(frames) == alpha.shape[0]:
            emit("temporal-stabilization", 2,
                 "Reusing cached alpha component detections...")
            return frames
    except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError):
        pass
    frames = detect_components(alpha, threshold, cv2, np)
    write_json_atomic(path, {"identity": identity, "frames": frames})
    return frames


def anchor_mask(alpha, frame, cuts, window, threshold, tracking_strength,
                components, tracks, cv2, np):
    labels, _ = frame_components(alpha[frame], threshold, cv2, np)
    keep = [component for component in components[frame]
        if persistent_component(component, frame, cuts, window, tracks)]
    if not keep and components[frame]:
        keep = [max(components[frame], key=lambda item: item["area"])]
    candidate = np.zeros(alpha.shape[1:], np.uint8)
    for component in keep:
        candidate[labels == component["label"]] = alpha[frame][
            labels == component["label"]]
    tracked = clean_anchor(candidate, threshold, cv2, np)
    if tracking_strength >= 1:
        return tracked
    untracked = clean_anchor(alpha[frame], threshold, cv2, np)
    return blend(untracked, tracked, tracking_strength, np)


def write_anchor_files(folder, alpha, anchors, cuts, window, threshold, fps,
                       tracking_strength, components, tracks, cv2, np):
    from PIL import Image
    folder.mkdir(parents=True, exist_ok=True)
    first = anchors[0]
    Image.fromarray(anchor_mask(alpha, first, cuts, window, threshold,
        tracking_strength, components, tracks, cv2, np), "L").save(
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
            tracking_strength, components, tracks, cv2, np), "L").save(
            folder / f"{prefix}-{frame_ms}.png")
    return first


def preview_metadata(folder, width, height, rate, total):
    folder.mkdir(parents=True, exist_ok=True)
    temporary = folder / "preview.tmp"
    temporary.write_text(json.dumps({"width": width, "height": height,
        "frameRate": rate, "totalFrames": total,
        "decisionFile": "decision-flags.bitplanes"}), encoding="utf-8")
    os.replace(temporary, folder / "preview.json")


def append_stabilized_frames(raw_path, weight_path, model_size, original,
                             start, end, preview_folder, cuts, window,
                             threshold, strength, tracking_strength,
                             components, tracks, cv2, np, motion=None):
    model_width, model_height = model_size
    model = np.memmap(raw_path, mode="r", dtype=np.uint8,
        shape=(original.shape[0], model_height, model_width))
    weights = open(weight_path, "ab")
    output = open(preview_folder / "output-alpha.gray8", "ab") \
        if preview_folder is not None else None
    decisions = open(preview_folder / "decision-flags.bitplanes", "ab") \
        if preview_folder is not None else None
    try:
        for frame in range(start, end):
            stabilized = model[frame]
            if stabilized.shape != original[frame].shape:
                stabilized = cv2.resize(stabilized,
                    (original.shape[2], original.shape[1]),
                    interpolation=cv2.INTER_LINEAR)
            weight = instability_weight(original, frame, cuts, window,
                threshold, strength, tracking_strength, components, tracks,
                cv2, np, motion)
            compact_weight = np.rint(weight * 255).astype(np.uint8)
            weights.write(memoryview(compact_weight).cast("B"))
            if output is None or decisions is None:
                continue
            weight = compact_weight.astype(np.float32) / 255
            cleaned = blend(original[frame], stabilized, weight, np)
            difference = cleaned.astype(np.int16) - original[frame].astype(np.int16)
            output.write(memoryview(cleaned).cast("B"))
            decisions.write(memoryview(np.packbits(
                (difference < -1).reshape(-1), bitorder="little")).cast("B"))
            decisions.write(memoryview(np.packbits(
                (difference > 1).reshape(-1), bitorder="little")).cast("B"))
        weights.flush()
        if output is not None: output.flush()
        if decisions is not None: decisions.flush()
    finally:
        weights.close()
        if output is not None: output.close()
        if decisions is not None: decisions.close()
        model._mmap.close()


def run_model(args, source, runtime, model_output, raw_path, weight_path, anchors,
              first_anchor, fps, total, model_size, original, preview_folder,
              cuts, components, tracks, cv2, np, motion=None):
    worker = Path(__file__).with_name("matanyone2_worker.py")
    command = [sys.executable, str(worker), "--source", str(source),
        "--output", str(model_output), "--runtime", str(runtime),
        "--mask", str(anchors / "initial-mask.png"), "--mask-frame-ms",
        str(round(first_anchor * 1000 / fps)), "--anchor-folder", str(anchors),
        "--max-size", str(MODEL_DETAIL), "--max-mem-frames",
        str(max(2, min(9, args.window * 2 + 1))), "--compile-mode", "eager",
        "--disable-previews", "--raw-alpha-output", str(raw_path)]
    process = subprocess.Popen(command, stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT, text=True, bufsize=1, env=os.environ.copy())
    weight_path.unlink(missing_ok=True)
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
                    append_stabilized_frames(raw_path, weight_path, model_size,
                        original, preview_count, available, preview_folder,
                        cuts, args.window, args.alpha_threshold,
                        args.strength / 100, args.tracking_strength / 100,
                        components, tracks, cv2, np, motion)
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
            append_stabilized_frames(raw_path, weight_path, model_size,
                original, preview_count, total, preview_folder, cuts,
                args.window, args.alpha_threshold, args.strength / 100,
                args.tracking_strength / 100, components, tracks, cv2, np, motion)
    except BaseException:
        if process.poll() is None:
            process.kill()
        process.wait()
        raise


def encode_blended_output(original_path, model_path, weight_path, destination,
                          rate, width, height, original_preview, ffmpeg, cv2, np):
    expected_weights = original_preview.size
    if not weight_path.is_file() or weight_path.stat().st_size != expected_weights:
        raise RuntimeError("The temporary instability-weight cache is incomplete")
    weights = np.memmap(weight_path, mode="r", dtype=np.uint8,
                        shape=original_preview.shape)
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
            weight = weights[count].astype(np.float32) / 255
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
    finally:
        del weights


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

    with tempfile.TemporaryDirectory(prefix="iqp-auto-alpha-") as temporary_value, \
            ExitStack() as resources:
        temporary = Path(temporary_value)
        original_cache = temporary / "input-alpha.gray8"
        gray_cache = temporary / "input-source.gray8"
        engine = None
        if args.motion_alignment == "nvidia":
            from nvidia_optical_flow import NvidiaOpticalFlow
            engine = resources.enter_context(NvidiaOpticalFlow(preview_width, preview_height))
            emit("temporal-stabilization", 0,
                 f"NVIDIA hardware optical flow ready: {engine.device_name}; "
                 f"{engine.grid}x{engine.grid} flow grid")
        emit("temporal-stabilization", 0, "Analysing alpha stability and camera cuts...")
        identity = analysis_identity(foreground, alpha_path, preview_width,
                                     preview_height, rate, total)
        if args.analysis_cache:
            original, images, cuts, total = cached_analysis(args.analysis_cache, identity,
                foreground, alpha_path, rate, total, preview_width,
                preview_height, ffmpeg, cv2, np)
        else:
            original, images, cuts, total = analyse_inputs(foreground, alpha_path, rate,
                total, preview_width, preview_height, original_cache, gray_cache,
                ffmpeg, cv2, np)
        resources.callback(original._mmap.close)
        resources.callback(images._mmap.close)
        detected = cached_components(args.analysis_cache, identity, original,
            args.alpha_threshold, cv2, np) if args.analysis_cache else None
        motion = MotionAlignment(images, cuts, args.window, cv2, np, engine) if engine else None
        if motion is not None:
            resources.callback(motion.close)
        emit("temporal-stabilization", 2,
             "Tracking moving alpha components across camera shots...")
        components, tracks = track_components(original, cuts, args.window,
            args.alpha_threshold, cv2, np, detected)
        tracking_strength = args.tracking_strength / 100
        anchors = choose_anchors(original, cuts, args.window, tracking_strength,
                                 components, tracks)
        anchor_folder = temporary / "anchors"
        first_anchor = write_anchor_files(anchor_folder, original, anchors, cuts,
            args.window, args.alpha_threshold, fps, tracking_strength,
            components, tracks, cv2, np)
        if args.preview_cache:
            preview_metadata(args.preview_cache, preview_width, preview_height,
                             rate, total)
            for name in ("output-alpha.gray8", "decision-flags.bitplanes"):
                open(args.preview_cache / name, "wb").close()
        model_output = temporary / "model-output"
        raw_path = temporary / "model-alpha.u8"
        weight_path = temporary / "instability-weights.gray8"
        emit("temporal-stabilization", 4,
             f"Selected {len(anchors)} automatic memory anchors across "
             f"{len(cuts) - 1} camera shots")
        run_model(args, foreground, args.runtime, model_output, raw_path,
            weight_path,
            anchor_folder, first_anchor, fps, total, (model_width, model_height),
            original, args.preview_cache, cuts, components, tracks, cv2, np, motion)
        encode_blended_output(alpha_path, model_output / "alpha.mkv", weight_path,
            destination, rate, width, height, original, ffmpeg, cv2, np)
        original.flush()
        del original
    emit("temporal-stabilization", 100,
         f"Automatic alpha stabilization completed using {len(anchors)} memory anchors" +
         (" and NVIDIA motion alignment" if args.motion_alignment == "nvidia" else ""))


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
    components, tracks = track_components(alpha, cuts, 4, 120, cv2, np)
    weight = instability_weight(alpha, 4, cuts, 4, 120, 1,
                                1, components, tracks, cv2, np)
    assert weight[3, 3] == 1 and weight[0, 0] < .2
    protected = instability_weight(alpha, 0, cuts, 4, 120, 1,
                                   1, components, tracks, cv2, np)
    unprotected = instability_weight(alpha, 0, cuts, 4, 120, 1,
                                     0, components, tracks, cv2, np)
    assert protected[3, 3] == 0 and unprotected[3, 3] == 1
    anchors = choose_anchors(alpha, cuts, 2, 1, components, tracks)
    assert anchors and 4 not in anchors
    automatic = np.zeros((9, 40, 40), np.uint8)
    automatic[:, 16:28, 16:28] = 220
    for frame in range(5):
        automatic[frame, 2:6, 2 + frame:6 + frame] = 220
    automatic[:4, 32:36, 32:36] = 220
    moving_components, moving_tracks = track_components(
        automatic, cuts, 4, 120, cv2, np)
    moving_ids = {component["track"] for frame in range(5)
        for component in moving_components[frame]
        if component["cy"] < 10 and component["cx"] < 14}
    assert len(moving_ids) == 1
    selected = choose_anchors(automatic, cuts, 4, 1,
                              moving_components, moving_tracks)[0]
    automatic_mask = anchor_mask(automatic, selected, cuts, 4, 120, 1,
        moving_components, moving_tracks, cv2, np)
    untracked_mask = anchor_mask(automatic, 0, cuts, 4, 120, 0,
        moving_components, moving_tracks, cv2, np)
    assert selected < 5 and automatic_mask[3, 3 + selected] == 255
    assert automatic_mask[33, 33] == 0 and untracked_mask[33, 33] == 255
    cleaned = clean_anchor(alpha[0], 120, cv2, np)
    assert cleaned[3, 3] == 255 and cleaned[0, 0] == 0
    original = np.array([[0, 200]], np.uint8)
    stabilized = np.array([[200, 0]], np.uint8)
    mixed = blend(original, stabilized, np.array([[.5, .25]], np.float32), np)
    assert tuple(mixed[0]) == (100, 150)
    with tempfile.TemporaryDirectory(prefix="iqp-alpha-cache-test-") as folder_value:
        folder = Path(folder_value)
        identity = {"revision": ANALYSIS_CACHE_REVISION, "test": True}
        raw = folder / "input-alpha.gray8"
        raw.write_bytes(alpha.tobytes())
        gray_path = folder / "input-source.gray8"
        gray_path.write_bytes(alpha.tobytes())
        write_json_atomic(folder / "analysis.json",
            {"identity": identity, "frames": alpha.shape[0], "cuts": cuts})
        loaded = load_cached_analysis(folder, identity, 8, 8, np)
        assert loaded is not None and loaded[2] == cuts and \
            np.array_equal(loaded[0], alpha)
        assert np.array_equal(loaded[1], alpha)
        detected = cached_components(folder, identity, loaded[0], 120, cv2, np)
        cached = cached_components(folder, identity, loaded[0], 120, cv2, np)
        assert cached == detected and len(cached) == alpha.shape[0]
        raw_model = folder / "model-alpha.u8"
        raw_model.write_bytes(alpha.tobytes())
        weight_cache = folder / "instability-weights.gray8"
        append_stabilized_frames(raw_model, weight_cache, (8, 8), alpha,
            0, alpha.shape[0], None, cuts, 4, 120, 1, 1,
            components, tracks, cv2, np)
        compact = np.fromfile(weight_cache, dtype=np.uint8).reshape(alpha.shape)
        expected = np.rint(instability_weight(alpha, 4, cuts, 4, 120, 1,
            1, components, tracks, cv2, np) * 255).astype(np.uint8)
        assert weight_cache.stat().st_size == alpha.size and \
            np.array_equal(compact[4], expected)
        del loaded
        gray_path.write_bytes(b"incomplete")
        assert load_cached_analysis(folder, identity, 8, 8, np) is None
    test_motion_alignment(cv2, np)
    print("Automatic temporal alpha stabilization worker self-test passed")


def test_motion_alignment(cv2, np):
    class Translation:
        def compute(self, source, target):
            flow = np.zeros((*source.shape, 2), np.float32)
            flow[..., 0] = 4
            costs = np.zeros(source.shape, np.uint8)
            return flow, -flow, costs, costs

        def close(self):
            pass

    texture = np.random.default_rng(5).integers(0, 256, (96, 160), dtype=np.uint8)
    images = np.stack([np.roll(texture, 4 * frame, axis=1) for frame in range(9)])
    alpha = np.zeros(images.shape, np.uint8)
    for frame in range(9):
        alpha[frame, 24:72, 24 + 4 * frame:72 + 4 * frame] = 220
    cuts = [0, 9]
    components, tracks = track_components(alpha, cuts, 3, 120, cv2, np)
    motion = MotionAlignment(images, cuts, 3, cv2, np, Translation())
    unaligned = instability_weight(alpha, 4, cuts, 3, 120, 1, 0,
                                  components, tracks, cv2, np)
    aligned = instability_weight(alpha, 4, cuts, 3, 120, 1, 0,
                                 components, tracks, cv2, np, motion)
    assert unaligned.sum() > 100 and aligned.sum() == 0, "Movement was classified as flicker"
    # The source still moves smoothly, but its matte has a one-frame dropout.
    alpha[4, 40:56, 52:68] = 0
    aligned = instability_weight(alpha, 4, cuts, 3, 120, 1, 0,
                                 components, tracks, cv2, np, motion)
    assert aligned[48, 60] > .9, "Aligned matte dropout was missed"
    motion.close()
    # Newly visible/occluding content must retain the current matte.
    images[4, 36:60, 48:72] = 0
    motion = MotionAlignment(images, cuts, 3, cv2, np, Translation())
    occluded = instability_weight(alpha, 4, cuts, 3, 120, 1, 0,
                                  components, tracks, cv2, np, motion)
    assert occluded[48, 60] == 0, "Unreliable correspondence was blended"
    # High costs and inconsistent reverse vectors must also reject history.
    flow = np.zeros((96, 160, 2), np.float32)
    high = np.full((96, 160), 255, np.uint8)
    assert not motion.reliable(texture, texture, flow, flow, high).any()
    wrong = flow.copy(); wrong[..., 0] = 8
    assert not motion.reliable(texture, texture, flow, wrong, high * 0).any()
    motion.close()
    # Each shot is stable despite a large alpha change at the cut.
    alpha[:4] = 0; alpha[4:] = 220
    cuts = [0, 4, 9]
    components, tracks = track_components(alpha, cuts, 3, 120, cv2, np)
    motion = MotionAlignment(images, cuts, 3, cv2, np, Translation())
    for frame in (3, 4):
        weight = instability_weight(alpha, frame, cuts, 3, 120, 1, 0,
                                    components, tracks, cv2, np, motion)
        assert not weight.any(), "Temporal evidence crossed a camera cut"
    assert 3 not in motion.pairs and len(motion.pairs) <= motion.cache_limit
    motion.close()
    # Out-of-frame samples must not become transparent evidence at the border.
    cuts = [0, 9]
    alpha[:] = 220
    motion = MotionAlignment(images, cuts, 3, cv2, np, Translation())
    weight = instability_weight(alpha, 4, cuts, 3, 120, 1, 0,
                                components, tracks, cv2, np, motion)
    assert not weight.any(), "Out-of-frame alpha contaminated the current frame"
    motion.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path)
    parser.add_argument("--foreground", type=Path)
    parser.add_argument("--alpha", type=Path)
    parser.add_argument("--destination", type=Path)
    parser.add_argument("--preview-cache", type=Path)
    parser.add_argument("--analysis-cache", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--window", type=int, default=3)
    parser.add_argument("--strength", type=int, default=100)
    parser.add_argument("--tracking-strength", type=int, default=100)
    parser.add_argument("--alpha-threshold", type=int, default=120)
    parser.add_argument("--motion-alignment", choices=("nvidia", "off"), default="nvidia",
                        help="NVIDIA hardware alignment, or unaligned comparison for diagnostics")
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
    if not 1 <= args.window <= 30:
        parser.error("--window must be 1-30 frames")
    if not 25 <= args.strength <= 100:
        parser.error("--strength must be 25-100 percent")
    if not 0 <= args.tracking_strength <= 100:
        parser.error("--tracking-strength must be 0-100 percent")
    if not 0 <= args.alpha_threshold <= 255:
        parser.error("--alpha-threshold must be 0-255")
    args.output = args.output.resolve() if args.output else None
    args.foreground = args.foreground.resolve() if args.foreground else None
    args.alpha = args.alpha.resolve() if args.alpha else None
    args.destination = args.destination.resolve() if args.destination else None
    args.preview_cache = args.preview_cache.resolve() if args.preview_cache else None
    args.analysis_cache = args.analysis_cache.resolve() if args.analysis_cache else None
    args.runtime = args.runtime.resolve()
    process(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", 0, str(error))
        raise SystemExit(1)
