#!/usr/bin/env python3
"""Persistent point/paint mask session for pinned SAM2Matting SAM2.1 variants."""

import argparse
import contextlib
import json
import os
import sys
from pathlib import Path

SOURCE_REVISION = "73dd721d77b56749248aefe5e8824d7f61b9d13c"
MODELS = {
    "sam2.1-tiny": ("configs/sam2matting-sam2.1tiny.yaml",
                    "SAM2Matting-SAM2.1Tiny.pt"),
    "sam2.1-base-plus": ("configs/sam2matting-sam2.1base+.yaml",
                         "SAM2Matting-SAM2.1Base+.pt"),
}


def send(**value):
    print(json.dumps(value), flush=True)


def save_result(image, combined, points, labels, mask_path, preview_path):
    import numpy as np
    from PIL import Image, ImageDraw

    Image.fromarray((combined * 255).astype(np.uint8), "L").save(mask_path)
    overlay = image.astype(np.float32)
    overlay[combined] = overlay[combined] * .45 + np.array([40, 210, 80]) * .55
    painted = Image.fromarray(np.clip(overlay, 0, 255).astype(np.uint8))
    draw = ImageDraw.Draw(painted)
    for (x, y), label in zip(points, labels):
        color = "lime" if label else "red"
        draw.ellipse((x - 7, y - 7, x + 7, y + 7), fill=color,
                     outline="white", width=2)
    painted.save(preview_path, quality=92)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--mask", type=Path, required=True)
    parser.add_argument("--preview", type=Path, required=True)
    parser.add_argument("--tracker", choices=tuple(MODELS), required=True)
    args = parser.parse_args()
    runtime = args.runtime.resolve()
    environment = json.loads((runtime / "environment.json").read_text(encoding="utf-8-sig"))
    if environment.get("sourceRevision") != SOURCE_REVISION:
        raise RuntimeError("Setup required: incompatible SAM2Matting source revision")
    source = runtime / "source" / "SAM2Matting"
    sys.path.insert(0, str(source))
    os.chdir(source)

    import numpy as np
    import torch
    from PIL import Image
    from sam2.build_sam import build_sam2matting_video_predictor
    from sam2.sam2_image_predictor import SAM2ImagePredictor

    config, checkpoint_name = MODELS[args.tracker]
    checkpoint = runtime / "checkpoints" / checkpoint_name
    with contextlib.redirect_stdout(sys.stderr):
        model = build_sam2matting_video_predictor(
            config, str(checkpoint), device="cuda", hydra_overrides_extra=[])
    predictor = SAM2ImagePredictor(model)
    device = "cuda"
    bf16 = True

    def load_image(path):
        value = np.array(Image.open(path).convert("RGB"), copy=True)
        predictor.set_image(value)
        return value

    mask_path = args.mask
    preview_path = args.preview
    image = load_image(args.image)
    send(status="ready", width=int(image.shape[1]), height=int(image.shape[0]),
         device=device, precision="BF16", checkpoint=args.tracker)
    for line in sys.stdin:
        request = json.loads(line)
        if request.get("command") == "load":
            image = load_image(Path(request.get("image", args.image)))
            mask_path = Path(request.get("mask", mask_path))
            preview_path = Path(request.get("preview", preview_path))
            send(status="loaded", width=int(image.shape[1]), height=int(image.shape[0]))
            continue
        if request.get("command") == "auto":
            send(status="error", message="Automatic masking is disabled for SAM2Matting.")
            continue
        points = np.asarray(request.get("points", []), dtype=np.float32)
        labels = np.asarray(request.get("labels", []), dtype=np.int32)
        seed_mask = None
        seed_path = request.get("seedMask")
        if seed_path and Path(seed_path).is_file():
            seed = Image.open(seed_path).convert("L").resize(
                (256, 256), Image.Resampling.NEAREST)
            seed = np.asarray(seed, dtype=np.uint8)
            seed_mask = np.where(seed >= 128, 8.0, -8.0).astype(np.float32)[None]
        positives = points[labels == 1]
        negatives = points[labels == 0]
        if not len(positives) and seed_mask is None:
            send(status="error", message="Left-click at least one foreground object.")
            continue
        if seed_mask is not None:
            prompt_points = points if len(points) else None
            prompt_labels = labels if len(points) else None
            with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
                masks, scores, logits = predictor.predict(
                    point_coords=prompt_points, point_labels=prompt_labels,
                    mask_input=seed_mask, multimask_output=True)
                best = int(np.argmax(scores))
                refined, _, _ = predictor.predict(
                    point_coords=prompt_points, point_labels=prompt_labels,
                    mask_input=logits[best][None], multimask_output=False)
            combined = refined[0] > 0
        else:
            combined = np.zeros(image.shape[:2], dtype=bool)
            for positive in positives:
                prompt_points = np.concatenate(([positive], negatives))
                prompt_labels = np.concatenate(
                    ([1], np.zeros(len(negatives), dtype=np.int32)))
                with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
                    masks, scores, logits = predictor.predict(
                        point_coords=prompt_points, point_labels=prompt_labels,
                        multimask_output=True)
                    best = int(np.argmax(scores))
                    refined, _, _ = predictor.predict(
                        point_coords=prompt_points, point_labels=prompt_labels,
                        mask_input=logits[best][None], multimask_output=False)
                combined |= refined[0] > 0
        mask_path = Path(request.get("mask", mask_path))
        preview_path = Path(request.get("preview", preview_path))
        save_result(image, combined, points, labels, mask_path, preview_path)
        send(status="mask", pixels=int(combined.sum()))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        send(status="error", message=str(error))
        raise
