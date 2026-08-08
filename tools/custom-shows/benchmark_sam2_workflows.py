#!/usr/bin/env python3
"""Benchmark QuickPlayer's real SAM2 initial and correction workflows."""
import argparse, json, os, queue, shutil, subprocess, sys, tempfile, threading, time
from pathlib import Path

import numpy as np
from PIL import Image


MODES = {
    "eager": ("0", "max-autotune-no-cudagraphs"),
    "max-autotune": ("1", "max-autotune"),
    "max-autotune-no-cudagraphs": ("1", "max-autotune-no-cudagraphs"),
}


def stream_errors(stream, target):
    for line in stream:
        target.put(line.rstrip())


def correction_point(mask_path):
    mask = np.asarray(Image.open(mask_path).convert("L")) >= 128
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        raise RuntimeError(f"No foreground in {mask_path}")
    middle = len(xs) // 2
    return [float(xs[middle]), float(ys[middle])]


def run_mode(args, mode, output_root):
    forced, compile_mode = MODES[mode]
    run_root = output_root / mode
    frames, masks = run_root / "frames", run_root / "masks"
    shutil.rmtree(run_root, ignore_errors=True)
    run_root.mkdir(parents=True)
    environment = os.environ.copy()
    environment.update({
        "IQP_FFMPEG": str(args.ffmpeg.resolve()),
        "IQP_FFPROBE": str(args.ffprobe.resolve()),
        "IQP_SAM2_FORCE_VOS": forced,
        "IQP_SAM2_COMPILE_MODE": compile_mode,
    })
    command = [str(args.python.resolve()), str(args.worker.resolve()),
        "--source", str(args.source.resolve()), "--runtime", str(args.runtime.resolve()),
        "--mask", str(args.mask.resolve()), "--frames", str(frames),
        "--masks", str(masks), "--start-ms", str(args.start_ms),
        "--end-ms", str(args.end_ms)]
    errors = queue.Queue()
    started = time.perf_counter()
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, text=True, bufsize=1, env=environment)
    threading.Thread(target=stream_errors, args=(process.stderr, errors), daemon=True).start()
    result = {"mode": mode, "pid": process.pid, "requestedFrames": args.frames,
              "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    load_started = first_mask = initial_ready = prompt_started = preview_ready = None
    update_started = correction_ready = None
    initial_events = correction_events = 0
    correction_frame = args.frames // 2
    last_report = started
    try:
        for raw in process.stdout:
            now = time.perf_counter()
            try:
                event = json.loads(raw)
            except json.JSONDecodeError:
                print(f"[{mode} worker] {raw.rstrip()}", flush=True)
                continue
            status, message = event.get("status"), event.get("message", "")
            if status == "error":
                raise RuntimeError(message)
            if load_started is None and ("Loading" in message or "Compiling" in message):
                load_started = now
            if message.startswith("Tracked"):
                initial_events += 1
                if first_mask is None: first_mask = now
            elif message.startswith("Updated"):
                correction_events += 1
            if now - last_report >= 15:
                phase = "initial" if initial_ready is None else "correction"
                count = initial_events if initial_ready is None else correction_events
                print(f"[{mode}] {phase}: {count} mask events, {now - started:.1f}s elapsed",
                      flush=True)
                last_report = now
            if status == "ready" and initial_ready is None:
                initial_ready = now
                actual_frames = int(event["frameCount"])
                correction_frame = min(correction_frame, actual_frames - 1)
                point = correction_point(masks / f"{correction_frame + 1:08d}.png")
                prompt_started = time.perf_counter()
                process.stdin.write(json.dumps({"command": "prompt", "frame": correction_frame,
                    "points": [point], "labels": [1]}) + "\n")
                process.stdin.flush()
                result.update({"actualFrames": actual_frames, "reviewWidth": event["width"],
                    "reviewHeight": event["height"], "precision": event["precision"],
                    "optimized": event["optimized"], "correctionFrame": correction_frame,
                    "correctionPoint": point})
            elif status == "preview" and preview_ready is None:
                preview_ready = now
                update_started = time.perf_counter()
                process.stdin.write(json.dumps({"command": "update",
                                                "frame": correction_frame}) + "\n")
                process.stdin.flush()
            elif status == "ready" and initial_ready is not None:
                correction_ready = now
                process.stdin.write('{"command":"quit"}\n')
                process.stdin.flush()
                break
        if correction_ready is None:
            detail = "\n".join(list(errors.queue)[-20:])
            raise RuntimeError(f"Worker stopped before correction completed.\n{detail}")
        process.stdin.close()
        exit_code = process.wait(timeout=60)
        if exit_code:
            detail = "\n".join(list(errors.queue)[-20:])
            raise RuntimeError(f"Worker exited {exit_code}.\n{detail}")
        result.update({
            "extractionAndInitialTotalSeconds": round(initial_ready - started, 3),
            "loadToFirstMaskSeconds": round(first_mask - load_started, 3),
            "initialFirstToReadySeconds": round(initial_ready - first_mask, 3),
            "initialMaskEvents": initial_events,
            "initialSteadyFps": round(max(0, initial_events - 1) /
                                      max(.001, initial_ready - first_mask), 3),
            "correctionPreviewSeconds": round(preview_ready - prompt_started, 3),
            "correctionUpdateSeconds": round(correction_ready - update_started, 3),
            "correctionMaskEvents": correction_events,
            "correctionFps": round(correction_events /
                                   max(.001, correction_ready - update_started), 3),
            "totalSeconds": round(correction_ready - started, 3),
        })
        print(json.dumps(result, indent=2), flush=True)
        return result
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--python", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--mask", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--ffprobe", type=Path, required=True)
    parser.add_argument("--frames", type=int, default=1000)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int, default=33333)
    parser.add_argument("--modes", nargs="+", choices=MODES, default=list(MODES))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.frames < 2: parser.error("--frames must be at least 2")
    output = args.output or Path(tempfile.mkdtemp(prefix="iqp-sam2-benchmark-"))
    output.mkdir(parents=True, exist_ok=True)
    results = []
    try:
        for mode in args.modes:
            print(f"Starting {mode} over {args.frames} requested frames...", flush=True)
            results.append(run_mode(args, mode, output))
    finally:
        summary = output / "results.json"
        summary.write_text(json.dumps(results, indent=2), encoding="utf-8")
        print(f"Results: {summary}", flush=True)


if __name__ == "__main__":
    main()
