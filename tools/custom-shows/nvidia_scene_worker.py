#!/usr/bin/env python3
"""Hardware-flow shot scores and conservative blur candidates; stdout is NDJSON.

NVOFA supplies motion and matching costs. Colour reprojection error and failed
forward/backward matches measure how much of the image cannot be explained by
motion. Scores are heuristics, not probabilities. The editor owns thresholding
and stores all candidates so sensitivity changes need no further GPU work.
"""
import argparse
import base64
from collections import deque
import gzip
import json
import queue
import re
import subprocess
import sys
import threading
import time

import cv2
import numpy as np

from nvidia_optical_flow import NvidiaOpticalFlow
from rvm_worker import executable, probe

REVISION = "nvidia-flow-v3"
DATA_FORMAT = "nvidia-flow-gzip-json-v2"
PTS = re.compile(r"\bn:\s*(\d+).*?\bpts_time:([\d.eE+\-]+)")


def emit(percent, **values):
    print(json.dumps({"percent": percent, **values}), flush=True)


def decode(source, width, height, start_ms, duration, hardware=False):
    """Stream frames with their actual presentation times, including VFR inputs."""
    command = [executable("ffmpeg"), "-nostdin", "-hide_banner", "-nostats",
        "-v", "info", "-ss", str(start_ms / 1000)]
    if hardware:
        command += ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]
    filters = (f"scale_cuda={width}:{height}:format=nv12,hwdownload,format=nv12,format=bgr24"
        if hardware else f"scale={width}:{height}:flags=area,format=bgr24")
    command += ["-i", source,
        "-t", str(duration), "-map", "0:v:0", "-an", "-sn", "-dn",
        "-vf", filters + ",showinfo",
        "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1"]
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    timestamps, errors = queue.Queue(), deque(maxlen=30)

    def drain():
        try:
            for raw in process.stderr:
                line = raw.decode("utf-8", errors="replace")
                match = PTS.search(line)
                if match:
                    timestamps.put((int(match[1]), float(match[2])))
                elif "showinfo" not in line:
                    errors.append(line.strip())
        finally:
            timestamps.put(None)

    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    size, index, last_time = width * height * 3, 0, -1
    try:
        while True:
            data = process.stdout.read(size)
            if not data:
                break
            if len(data) != size:
                raise RuntimeError("FFmpeg returned a truncated video frame")
            stamp = timestamps.get()
            if stamp is None or stamp[0] != index or not np.isfinite(stamp[1]):
                raise RuntimeError("FFmpeg did not return matching frame timestamps")
            time_ms = round(stamp[1] * 1000)
            if time_ms < last_time:
                raise RuntimeError("Video presentation timestamps are not monotonic")
            last_time = time_ms
            index += 1
            if 0 <= time_ms < duration * 1000:
                yield time_ms, np.frombuffer(data, np.uint8).reshape(height, width, 3)
        process.wait()
        reader.join()
        if process.returncode:
            raise RuntimeError("FFmpeg decode failed: " + "\n".join(errors))
    finally:
        if process.poll() is None:
            process.kill()
        process.wait()
        reader.join()
        process.stdout.close()
        process.stderr.close()


def gray(image):
    return cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)


def exposure_normalized(image):
    """Compare scene structure without treating exposure ramps as dissolves."""
    mean, deviation = cv2.meanStdDev(image)
    return np.clip((image.astype(np.float32) - mean.ravel()) *
        (45 / np.maximum(10, deviation.ravel())) + 128, 0, 255).astype(np.uint8)


def visible_change(first, second):
    """Identity alignment is already sufficient if almost nothing changed.

    This uses the same colour-error scale as the flow score, with maximum
    reliability weight, so tiny adjacent changes need no hardware round trip.
    Gradual transitions are still tested independently over their long window.
    """
    error = cv2.transform(cv2.absdiff(first, second).astype(np.float32),
        np.array([[1/3, 1/3, 1/3]], np.float32))
    evidence = np.clip((error - 10) / 45, 0, 1)
    coverage = np.mean(cv2.resize(evidence, (8, 6), interpolation=cv2.INTER_AREA) > .2)
    return float(np.sqrt(evidence.mean() * coverage))


class MotionScorer:
    def __init__(self, engine, width, height):
        self.engine = engine
        self.y, self.x = np.mgrid[:height, :width].astype(np.float32)

    def score(self, first, second):
        forward, backward, first_cost, second_cost = self.engine.compute(gray(first), gray(second))
        scores = []
        for source, target, flow, reverse, cost in (
                (first, second, forward, backward, first_cost),
                (second, first, backward, forward, second_cost)):
            mx, my = self.x + flow[..., 0], self.y + flow[..., 1]
            valid = np.isfinite(mx) & np.isfinite(my) & (mx >= 0) & (my >= 0) & \
                (mx < source.shape[1] - 1) & (my < source.shape[0] - 1)
            warped = cv2.remap(target, mx, my, cv2.INTER_LINEAR)
            error = cv2.transform(cv2.absdiff(source, warped).astype(np.float32),
                np.array([[1/3, 1/3, 1/3]], np.float32))
            back = cv2.remap(reverse, mx, my, cv2.INTER_LINEAR)
            mismatch = ((flow[..., 0] + back[..., 0]) ** 2 +
                (flow[..., 1] + back[..., 1]) ** 2) > 1 + .01 * (
                flow[..., 0] ** 2 + flow[..., 1] ** 2 +
                back[..., 0] ** 2 + back[..., 1] ** 2)
            unreliable = mismatch | (cost > 40)
            if valid.mean() < .2:
                # Do not let wildly out-of-bounds estimates conceal a cut.
                error = np.mean(np.abs(source.astype(np.float32) - target), axis=2)
                valid = np.ones_like(valid)
                unreliable = valid
            evidence = np.clip((error - 10) / 45, 0, 1) * (.6 + .4 * unreliable)
            # A moving limb or a disocclusion should not look like a global cut.
            tiles = cv2.resize((evidence * valid).astype(np.float32), (8, 6),
                interpolation=cv2.INTER_AREA)
            coverage = float(np.mean(tiles > .2))
            scores.append(float(np.sqrt(evidence[valid].mean() * coverage)))
        return min(scores)


def flash_like(before, middle, after):
    """Only reject an isolated exposure pulse when the surrounding images match."""
    a, b, c = (gray(image).astype(np.float32) for image in (before, middle, after))
    offset = float(np.median(b - (a + c) / 2))
    residual = float(np.median(np.abs(b - (a + c) / 2 - offset)))
    return abs(offset) > 25 and (residual < 12 or float(b.mean()) > 235)


def cut_candidates(times, scores):
    # Collapse adjacent high responses to the best frame, independently of the
    # user's threshold. No time-gap rule that would erase genuinely short shots.
    return [{"timeMs": times[i], "score": round(score, 6)}
        for i, score in enumerate(scores) if score >= .05 and
        (i == 0 or score > scores[i - 1]) and
        (i == len(scores) - 1 or score >= scores[i + 1])]


def blur_candidates(times, sharpness, contrast, cuts, duration_ms):
    """Sustained loss of detail relative to nearby frames in the same likely shot.

    A uniformly soft shot has no sharp reference and is deliberately not called
    a blur transition. Flat black/white frames are also not blur evidence.
    """
    output = []
    shot_edges = [0] + [i for i, score in enumerate(cuts) if score >= .5] + [len(times)]
    for start, end in zip(shot_edges, shot_edges[1:]):
        if end <= start:
            continue
        maxima, right = deque(), start
        active, strengths = None, []
        for i in range(start, end):
            while right < end and times[right] <= times[i] + 1000:
                while maxima and sharpness[maxima[-1]] <= sharpness[right]:
                    maxima.pop()
                maxima.append(right)
                right += 1
            while maxima and times[maxima[0]] < times[i] - 1000:
                maxima.popleft()
            reference = sharpness[maxima[0]]
            drop = 1 - sharpness[i] / max(1, reference)
            strength = float(np.clip((drop - .5) / .45, 0, 1)) \
                if contrast[i] >= 25 and reference >= 20 else 0
            if strength >= .01:
                if active is None:
                    active = times[i]
                strengths.append(strength)
            if (strength < .01 or i == end - 1) and active is not None:
                stop = times[i] if strength < .01 else \
                    (times[end] if end < len(times) else duration_ms)
                if stop - active >= 160:
                    output.append({"startMs": active, "endMs": stop,
                        "score": round(float(np.median(strengths)), 6)})
                active, strengths = None, []
    return output


def dissolve_candidate(history, scorer):
    """A middle frame must resemble a blend of two motion-incompatible views.

    Adjacent optical flow can explain a slow dissolve. Compare views two seconds
    apart instead, then require intermediate blend weights over a real interval.
    Exposure normalization and hardware motion compensation reject lighting and
    pans. This is a conservative heuristic, not a learned transition classifier.
    """
    first_time, first = history[0]
    last_time, last = history[-1]
    if last_time - first_time < 1800:
        return None
    middle_time = (first_time + last_time) / 2
    middle = min(history, key=lambda item: abs(item[0] - middle_time))[1]
    a, c, b = [cv2.resize(frame, (48, 32),
        interpolation=cv2.INTER_AREA).astype(np.float32) for frame in (first, middle, last)]
    delta = b - a
    energy = float(np.mean(delta ** 2))
    if energy < 100:
        return None
    weight = float(np.mean((c - a) * delta) / energy)
    baseline = min(float(np.mean((c - a) ** 2)), float(np.mean((c - b) ** 2)))
    gain = 1 - float(np.mean((c - a - weight * delta) ** 2)) / max(1, baseline)
    if not .15 < weight < .85 or gain < .35:
        return None
    motion = scorer.score(first, last)
    if motion < .15:
        return None
    # A moving person can fit a coarse linear blend across a long window.
    # Require unexplained structure on BOTH sides of the intermediate image;
    # ordinary motion usually matches one or both half-window comparisons.
    entering, leaving = scorer.score(first, middle), scorer.score(middle, last)
    if min(entering, leaving) < .08 or entering + leaving < .25:
        return None
    weights = []
    for stamp, frame in history:
        value = cv2.resize(frame, (48, 32),
            interpolation=cv2.INTER_AREA).astype(np.float32)
        weights.append((stamp, float(np.mean((value - a) * delta) / energy)))
    active = [stamp for stamp, value in weights if .1 < value < .9]
    if not active or active[-1] - active[0] < 160:
        return None
    # The 10–90% mixture establishes that a dissolve exists, but is only its
    # strongest middle. Include faint tails and bracket their sampled endpoints
    # so the kept clips do not retain partially blended frames. This coverage is
    # independent of confidence/sensitivity and the editor's extra buffer. A
    # 200 ms guard on each side covers endpoint contamination and sampling error.
    extent = [i for i, (_, value) in enumerate(weights) if .02 < value < .98]
    start = weights[max(0, extent[0] - 1)][0]
    end = weights[min(len(weights) - 1, extent[-1] + 1)][0]
    return {"startMs": max(0, start - 200), "endMs": end + 200,
        "score": round(min(1, float(np.sqrt(motion * gain)) * 1.2), 6),
        "kind": "Dissolve"}


def analyse(frames, engine, width, height, duration_ms, progress=None):
    scorer = MotionScorer(engine, width, height)
    history = deque(maxlen=2)
    context, dissolves, last_context_time = deque(), [], -1000
    times, scores, sharpness, contrast = [], [], [], []
    for time_ms, frame in frames:
        score = 0
        if history and visible_change(history[-1], frame) >= .05:
            score = scorer.score(history[-1], frame)
        if len(history) == 2 and score >= .1 and scores[-1] >= .1 and \
                flash_like(history[0], history[1], frame) and \
                scorer.score(history[0], frame) < .1:
            scores[-1], score = 0, 0
        luma = gray(frame)
        times.append(time_ms)
        scores.append(score)
        sharpness.append(float(cv2.Laplacian(luma, cv2.CV_32F).var()))
        contrast.append(float(luma.var()))
        history.append(frame)
        # Ten samples/second for gradual transitions; retain original frame PTS
        # for hard cuts. No full-video image cache and no second decode pass.
        if time_ms - last_context_time >= 95:
            last_context_time = time_ms
            context.append((time_ms, exposure_normalized(frame)))
            while context and time_ms - context[0][0] > 2100:
                context.popleft()
            candidate = dissolve_candidate(context, scorer)
            if candidate:
                candidate["endMs"] = min(duration_ms, candidate["endMs"])
                if dissolves and candidate["startMs"] <= dissolves[-1]["endMs"]:
                    if candidate["score"] > dissolves[-1]["score"]:
                        dissolves[-1] = candidate
                else:
                    dissolves.append(candidate)
        if progress:
            progress(min(99, int(time_ms / max(1, duration_ms) * 100)))
    if not times:
        raise RuntimeError("No video frames were decoded in the selected range")
    candidates = cut_candidates(times, scores)
    return {"durationMs": duration_ms,
        "cuts": [item for item in candidates if 250 < item["timeMs"] < duration_ms - 250],
        "blurs": blur_candidates(times, sharpness, contrast, scores, duration_ms) + dissolves}


def self_test():
    rng = np.random.default_rng(145)
    a = cv2.GaussianBlur(rng.integers(25, 210, (192, 256, 3), np.uint8), (3, 3), 0)
    a[:, :128] //= 2  # Preserve broad scene structure through the blur.
    b = cv2.GaussianBlur(rng.integers(25, 210, a.shape, np.uint8), (3, 3), 0)
    # Distinct texture plus colour, while translation is entirely explainable.
    b[..., 1] = 220
    with NvidiaOpticalFlow(256, 192) as engine:
        scorer = MotionScorer(engine, 256, 192)
        still = scorer.score(a, a)
        pan = scorer.score(a, np.roll(a, 6, axis=1))
        cut = scorer.score(a, b)
        patch = a.copy()
        patch[60:100, 70:115] = 0
        local = scorer.score(a, patch)
        assert still < .01 and pan < .1 and cut > .5 and local < .25, (still, pan, cut, local)
        def run(images):
            return analyse(((i * 50, frame) for i, frame in enumerate(images)),
                engine, 256, 192, len(images) * 50)
        hard = run([a] * 10 + [b] * 10)
        assert len(hard["cuts"]) == 1 and hard["cuts"][0]["timeMs"] == 500, hard
        assert not hard["blurs"], hard
        flash = run([a] * 10 + [cv2.add(a, (65, 65, 65, 0))] + [a] * 10)
        assert not flash["cuts"], flash
        blurred = cv2.GaussianBlur(a, (25, 25), 5)
        blur = run([a] * 10 + [blurred] * 6 + [a] * 10)
        assert blur["blurs"] and blur["blurs"][0]["startMs"] == 500, blur
        assert blur["blurs"][0]["endMs"] == 800, blur
        flat = run([np.zeros_like(a)] * 20)
        assert not flat["cuts"] and not flat["blurs"], flat
        # Exercise the long comparison window: no individual dissolve frame is
        # a hard cut. The accepted range must contain its midpoint.
        other = cv2.flip(a, -1).copy()
        other[:, :128] = cv2.add(other[:, :128], (80, 35, 10, 0))
        gradual = run([a] * 30 + [cv2.addWeighted(a, 1-i/20, other, i/20, 0)
            for i in range(1, 21)] + [other] * 30)
        assert any(item.get("kind") == "Dissolve" and
            item["startMs"] <= 2000 <= item["endMs"] for item in gradual["blurs"]), gradual
        assert any(item.get("kind") == "Dissolve" and
            item["startMs"] <= 1500 and item["endMs"] >= 2500
            for item in gradual["blurs"]), gradual
        exposure = run([cv2.convertScaleAbs(a, alpha=1+i/200, beta=i/2)
            for i in range(80)])
        assert not exposure["cuts"] and not exposure["blurs"], exposure
        local_sequence = run([a] * 10 + [patch] * 10)
        assert not local_sequence["cuts"], local_sequence
        assert scorer.score(np.zeros_like(a), np.full_like(a, 255)) > .5
        print(json.dumps({"selfTest": "passed", "device": engine.device_name,
            "still": still, "pan": pan, "cut": cut, "localMotion": local,
            "blur": blur["blurs"]}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source")
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not args.source:
        parser.error("--source is required")
    width, height, _, fps, duration = probe(args.source)
    start_ms = max(0, args.start_ms)
    end_ms = min(round(duration * 1000), args.end_ms if args.end_ms is not None
        else round(duration * 1000))
    if end_ms <= start_ms:
        raise ValueError("The selected detection range is empty")
    scale = min(1, 192 / max(width, height))
    width, height = max(96, round(width * scale / 2) * 2), max(96, round(height * scale / 2) * 2)
    duration_ms = end_ms - start_ms
    last_percent = -1

    def progress(percent):
        nonlocal last_percent
        if percent > last_percent:
            emit(percent)
            last_percent = percent

    with NvidiaOpticalFlow(width, height) as engine:
        emit(0, device=engine.device_name, backend="nvidia-nvofa", revision=REVISION)
        def decode_available():
            decoded = False
            try:
                for item in decode(args.source, width, height, start_ms, duration_ms / 1000, True):
                    if not decoded:
                        emit(0, decoder="nvidia-nvdec", message="NVIDIA video decoding and scaling active")
                    decoded = True
                    yield item
            except RuntimeError:
                if decoded:
                    raise
                # Codecs unsupported by NVDEC still use NVOFA for motion.
                emit(0, decoder="cpu", message="NVDEC unavailable for this input; using CPU decoding")
                yield from decode(args.source, width, height, start_ms, duration_ms / 1000)
        frames = decode_available()
        started = time.perf_counter()
        try:
            data = analyse(frames, engine, width, height, duration_ms, progress)
        finally:
            frames.close()
    packed = base64.b64encode(gzip.compress(json.dumps(data,
        separators=(",", ":")).encode("utf-8"))).decode("ascii")
    emit(100, sensitivityDataFormat=DATA_FORMAT, sensitivityData=packed,
        detectionFrameRate=fps, revision=REVISION, analysisSeconds=round(time.perf_counter()-started, 3))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(1)
