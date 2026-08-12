#!/usr/bin/env python3
"""Run RVM over every frame, then refine its masks with ViTMatte-S."""
import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

from rvm_worker import emit


def relay(process, start, span, phase):
    for line in process.stdout:
        line = line.strip()
        if not line:
            continue
        try:
            value = json.loads(line)
            percent = float(value.get("percent", 0))
            if value.get("status") in ("ready", "complete"):
                percent = 100
            message = value.get("message", "")
            if not message and value.get("status") == "ready":
                message = "RVM mask sequence complete"
            emit(phase, start + span * max(0, min(100, percent)) / 100,
                 message)
        except (ValueError, TypeError, json.JSONDecodeError):
            print(line, file=sys.stderr, flush=True)
    return process.wait()


def run(command, start, span, phase):
    process = subprocess.Popen(command, stdout=subprocess.PIPE,
        text=True, encoding="utf-8", errors="replace")
    result = relay(process, start, span, phase)
    if result:
        raise RuntimeError(f"{phase} worker failed with exit code {result}")


def process(args):
    args.output.mkdir(parents=True, exist_ok=True)
    work = args.output.resolve() / ".rvm-vitmatte"
    frames = work / "frames"
    generated_masks = args.output.resolve() / ".rvm-masks"
    masks = args.mask_folder.resolve() if args.mask_folder else generated_masks
    shutil.rmtree(work, ignore_errors=True)
    work.mkdir(parents=True)
    try:
        if not args.mask_folder:
            rvm = Path(__file__).with_name("rvm_mask_worker.py")
            rvm_command = [sys.executable, str(rvm), "--source", str(args.source),
                "--runtime", str(args.runtime), "--frames", str(frames),
                "--masks", str(masks), "--start-ms", str(args.start_ms),
                "--end-ms", str(args.end_ms), "--masks-only",
                "--alpha-threshold", str(args.rvm_alpha_threshold)]
            emit("rvm-masks", 0, "Generating a complete RVM mask sequence...")
            run(rvm_command, 0, 30, "rvm-masks")
        elif not masks.is_dir() or not any(masks.glob("*.png")):
            raise RuntimeError("The retained RVM mask sequence is missing")

        vitmatte = Path(__file__).with_name("vitmatte_worker.py")
        command = [sys.executable, str(vitmatte), "--source", str(args.source),
            "--output", str(args.output), "--runtime", str(args.runtime),
            "--mask-folder", str(masks), "--model", "s",
            "--start-ms", str(args.start_ms), "--end-ms", str(args.end_ms),
            "--batch-size", str(args.batch_size), "--encoder-preset",
            args.encoder_preset, "--compile-cutoff-frames",
            str(args.compile_cutoff_frames)]
        emit("vitmatte", 30, "Refining the RVM masks with ViTMatte-S...")
        run(command, 30, 70, "vitmatte")
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int, required=True)
    parser.add_argument("--mask-folder", type=Path)
    parser.add_argument("--batch-size", type=int, default=2)
    parser.add_argument("--rvm-alpha-threshold", type=float, default=.5)
    parser.add_argument("--encoder-preset",
                        choices=tuple(f"p{i}" for i in range(1, 8)), default="p5")
    parser.add_argument("--compile-cutoff-frames", type=int, default=16000)
    args = parser.parse_args()
    process(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", 0, str(error))
        raise
