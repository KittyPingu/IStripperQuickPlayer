#!/usr/bin/env python3
"""Persistent RVM preview worker for Training Studio label editing."""
import json
import subprocess
import sys
from pathlib import Path

from PIL import Image

from custom_show_worker import model_size
from matanyone2_worker import clean_rvm_mask
from rvm_worker import executable, fast_fp16, load_model, probe


def send(**values):
    print(json.dumps(values, separators=(",", ":")), flush=True)


def self_test():
    request = parse_request({"source": "x.mp4", "mask": "x.png",
                             "alpha": "alpha.png", "frameMs": 1234,
                             "alphaThreshold": .65, "outputWidth": 1920,
                             "outputHeight": 1080})
    assert request[3] == 1234 and abs(request[4] - .65) < .0001
    assert request[2].name == "alpha.png" and request[5:] == (1920, 1080)
    send(status="ok", message="rvm-preview self-test passed")


def parse_request(value):
    source = Path(value["source"]).resolve()
    destination = Path(value["mask"]).resolve()
    alpha_value = value.get("alpha")
    alpha_destination = Path(alpha_value).resolve() if alpha_value else None
    frame_ms = int(value["frameMs"])
    threshold = max(.1, min(.9, float(value.get("alphaThreshold", .4))))
    output_width = max(0, int(value.get("outputWidth", 0)))
    output_height = max(0, int(value.get("outputHeight", 0)))
    if bool(output_width) != bool(output_height):
        raise RuntimeError("RVM output width and height must be supplied together")
    return source, destination, alpha_destination, frame_ms, threshold, output_width, output_height


def infer(value, torch, model, device, fp16, cache):
    import numpy as np
    source, destination, alpha_destination, frame_ms, threshold, output_width, output_height = parse_request(value)
    width, height, _, fps, duration = probe(source)
    if frame_ms < 0 or frame_ms / 1000 > duration + .1:
        raise RuntimeError("RVM preview frame is outside the source video")
    review_width, review_height = model_size(width, height)
    key = (str(source), frame_ms)
    alpha = cache.get("alpha") if cache.get("key") == key else None
    reused_alpha = alpha is not None
    if alpha is None:
        target = frame_ms / 1000
        start = max(0.0, target - 2.0)
        warm_frames = max(1, round((target - start) * fps) + 1)
        frame_bytes = review_width * review_height * 3
        command = [executable("ffmpeg"), "-v", "error", "-ss", f"{start:.6f}",
        "-i", str(source), "-frames:v", str(warm_frames), "-vf",
        f"scale={review_width}:{review_height}:flags=bilinear,setsar=1",
        "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]
        decoded = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        count = len(decoded.stdout) // frame_bytes
        if decoded.returncode != 0 or count < 1:
            raise RuntimeError(decoded.stderr.decode(errors="replace").strip() or
                               "RVM preview lead-in could not be decoded")
        recurrent = [None] * 4
        with torch.inference_mode(), torch.autocast(device_type=device.type,
                dtype=torch.float16, enabled=fp16):
            for index in range(count):
                offset = index * frame_bytes
                frame = np.frombuffer(decoded.stdout[offset:offset + frame_bytes], np.uint8).reshape(
                    review_height, review_width, 3).copy()
                tensor = torch.from_numpy(frame).permute(2, 0, 1).unsqueeze(0).to(
                    device=device, dtype=torch.float32).div_(255)
                _, current_alpha, *recurrent = model(tensor, *recurrent, downsample_ratio=1)
        alpha = current_alpha[0, 0].float().cpu().numpy()
        cache["key"], cache["alpha"] = key, alpha
    mask = clean_rvm_mask(alpha, threshold=threshold)
    if not mask.any() and alpha_destination is None:
        raise RuntimeError("RVM could not find foreground at this threshold")
    destination.parent.mkdir(parents=True, exist_ok=True)
    mask_image = Image.fromarray(mask, "L")
    alpha_image = Image.fromarray(alpha.astype(np.float32), "F")
    if output_width and output_height:
        mask_image = mask_image.resize((output_width, output_height), Image.Resampling.NEAREST)
        alpha_image = alpha_image.resize((output_width, output_height), Image.Resampling.BILINEAR)
    mask_image.save(destination)
    if alpha_destination is not None:
        alpha_destination.parent.mkdir(parents=True, exist_ok=True)
        alpha16 = np.clip(np.asarray(alpha_image, dtype=np.float32) * 65535, 0, 65535).astype(np.uint16)
        Image.fromarray(alpha16).save(alpha_destination)
    return (destination, threshold, device.type, review_width, review_height,
            reused_alpha, alpha_destination)


def main():
    if sys.argv[1:] == ["--self-test"]:
        self_test(); return
    if len(sys.argv) != 3 or sys.argv[1] != "--runtime":
        raise RuntimeError("Usage: rvm_preview_worker.py --runtime PATH")
    runtime = Path(sys.argv[2]).resolve()
    torch, model, device = load_model(runtime, "quality")
    fp16 = device.type == "cuda" and fast_fp16(torch.cuda.get_device_capability(device))
    send(status="ready", device=device.type, precision="FP16" if fp16 else "FP32")
    cache = {}
    for line in sys.stdin:
        try:
            value = json.loads(line)
            if value.get("command") == "quit": break
            destination, threshold, device_name, width, height, cached, alpha_destination = infer(
                value, torch, model, device, fp16, cache)
            send(status="mask", mask=str(destination), alphaThreshold=threshold,
                 device=device_name, width=width, height=height, temporalLeadInSeconds=2,
                 alphaCached=cached, alpha=str(alpha_destination) if alpha_destination else None)
        except Exception as error:
            send(status="error", message=str(error))


if __name__ == "__main__":
    try: main()
    except Exception as error:
        send(status="error", message=str(error)); raise
