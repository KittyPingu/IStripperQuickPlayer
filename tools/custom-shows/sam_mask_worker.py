#!/usr/bin/env python3
"""Persistent SAM2 point-prompt worker for QuickPlayer's initial-mask editor."""
import argparse, json, sys
from pathlib import Path

SAM2_COMMIT = "2b90b9f5ceec907a1c18123530e92e794ad901a4"
SAM2_BYTES = 323606802

def send(**value):
    print(json.dumps(value), flush=True)

def automatic_foreground(model, image, points, labels, torch, device, bf16):
    """Pick stable automatic masks at positive clicks, or the largest useful mask."""
    import numpy as np
    from sam2.automatic_mask_generator import SAM2AutomaticMaskGenerator
    generator = SAM2AutomaticMaskGenerator(model, points_per_side=16,
        points_per_batch=64, pred_iou_thresh=.82, stability_score_thresh=.90,
        min_mask_region_area=256)
    with torch.inference_mode(), torch.autocast(device_type=device,
            dtype=torch.bfloat16, enabled=bf16):
        candidates = generator.generate(image)
    if not candidates:
        raise RuntimeError("SAM2 did not find an automatic mask on this frame.")
    area = image.shape[0] * image.shape[1]
    positives = points[labels == 1]
    negatives = points[labels == 0]
    usable = [item for item in candidates if item["area"] < area * .9 and
        not any(item["segmentation"][min(image.shape[0] - 1, max(0, round(y))),
            min(image.shape[1] - 1, max(0, round(x)))] for x, y in negatives)]
    if not usable:
        usable = candidates
    selected = []
    for x, y in positives:
        px = min(image.shape[1] - 1, max(0, round(x)))
        py = min(image.shape[0] - 1, max(0, round(y)))
        covering = [item for item in usable if item["segmentation"][py, px]]
        if covering:
            selected.append(max(covering, key=lambda item:
                (item["predicted_iou"], item["stability_score"], item["area"])))
    if not selected:
        selected = [max(usable, key=lambda item:
            (item["area"], item["predicted_iou"]))]
    combined = np.logical_or.reduce([item["segmentation"] for item in selected])
    return combined, len(candidates)

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
    args = parser.parse_args()
    runtime = args.runtime.resolve()
    weights = runtime / "checkpoints" / "sam2.1_hiera_base_plus.pt"
    marker = runtime / "SAM2_COMMIT"
    if not marker.is_file() or marker.read_text().strip() != SAM2_COMMIT:
        raise RuntimeError("SAM2 is not installed. Run Install / Update Processing Tools again.")
    if not weights.is_file() or weights.stat().st_size != SAM2_BYTES:
        raise RuntimeError("SAM2 base-plus weights validation failed; run setup again.")
    import numpy as np
    import torch
    from PIL import Image
    from sam2.build_sam import build_sam2
    from sam2.sam2_image_predictor import SAM2ImagePredictor
    device = "cuda" if torch.cuda.is_available() else "cpu"
    bf16 = device == "cuda" and torch.cuda.get_device_capability()[0] >= 8
    model = build_sam2("configs/sam2.1/sam2.1_hiera_b+.yaml",
                       str(weights), device=device)
    predictor = SAM2ImagePredictor(model)
    def load_image(path):
        value = np.array(Image.open(path).convert("RGB"), copy=True)
        predictor.set_image(value)
        return value
    image = load_image(args.image)
    send(status="ready", width=int(image.shape[1]), height=int(image.shape[0]),
         device=device, precision="BF16" if bf16 else "FP32",
         checkpoint="SAM2.1 Hiera Base+")
    for line in sys.stdin:
        request = json.loads(line)
        if request.get("command") == "load":
            image = load_image(Path(request.get("image", args.image)))
            send(status="loaded", width=int(image.shape[1]),
                 height=int(image.shape[0]))
            continue
        points = np.asarray(request.get("points", []), dtype=np.float32)
        labels = np.asarray(request.get("labels", []), dtype=np.int32)
        if request.get("command") == "auto":
            combined, candidate_count = automatic_foreground(
                model, image, points, labels, torch, device, bf16)
            save_result(image, combined, points, labels, args.mask, args.preview)
            send(status="mask", pixels=int(combined.sum()),
                 candidates=candidate_count, automatic=True)
            continue
        positives = points[labels == 1]
        negatives = points[labels == 0]
        if not len(positives):
            send(status="error", message="Left-click at least one person.")
            continue
        combined = np.zeros(image.shape[:2], dtype=bool)
        for positive in positives:
            prompt_points = np.concatenate(([positive], negatives))
            prompt_labels = np.concatenate(([1], np.zeros(len(negatives), dtype=np.int32)))
            with torch.inference_mode(), torch.autocast(device_type=device,
                    dtype=torch.bfloat16, enabled=bf16):
                masks, scores, logits = predictor.predict(point_coords=prompt_points,
                    point_labels=prompt_labels, multimask_output=True)
            best = int(np.argmax(scores))
            with torch.inference_mode(), torch.autocast(device_type=device,
                    dtype=torch.bfloat16, enabled=bf16):
                refined, _, _ = predictor.predict(point_coords=prompt_points,
                    point_labels=prompt_labels, mask_input=logits[best][None],
                    multimask_output=False)
            combined |= refined[0] > 0
        save_result(image, combined, points, labels, args.mask, args.preview)
        send(status="mask", pixels=int(combined.sum()))

if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        send(status="error", message=str(error))
        raise SystemExit(1)
