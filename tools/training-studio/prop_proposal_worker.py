#!/usr/bin/env python3
"""Generate a Training Studio v2 prop proposal with a green review overlay."""
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "custom-shows"))
from prop_segmenter import load_package, predict_mask


def send(**value): print(json.dumps(value, separators=(",", ":")), flush=True)


def green_overlay(image, mask):
    import numpy as np
    pixels = np.asarray(image, dtype=np.float32).copy()
    selected = np.asarray(mask, dtype=bool)
    pixels[selected] = pixels[selected] * .4 + np.array([20, 235, 70]) * .6
    return np.clip(pixels, 0, 255).astype(np.uint8)


def self_test():
    import numpy as np
    image = np.zeros((4, 4, 3), np.uint8); mask = np.zeros((4, 4), bool); mask[1, 1] = True
    overlay = green_overlay(image, mask)
    assert overlay[1, 1, 1] > 130 and overlay[1, 1, 0] < 30 and not overlay[0, 0].any()
    send(status="ok", message="prop-proposal self-test passed")


def generate(torch, model, device, manifest, image_path, alpha_path, mask_path, preview_path):
    import numpy as np
    from PIL import Image
    image_path, alpha_path = Path(image_path), Path(alpha_path)
    mask_path, preview_path = Path(mask_path), Path(preview_path)
    image = np.asarray(Image.open(image_path).convert("RGB"), dtype=np.uint8)
    alpha = np.asarray(Image.open(alpha_path), dtype=np.float32)
    if alpha.ndim == 3: alpha = alpha[..., 0]
    divisor = 65535. if alpha.max(initial=0) > 255 else 255.
    alpha = np.clip(alpha / divisor, 0, 1)
    prediction, probability = predict_mask(torch, model, device, image,
        manifest["confidenceThreshold"], manifest["inputSize"], alpha)
    mask_path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(prediction.astype(np.uint8) * 255, "L").save(mask_path)
    Image.fromarray(green_overlay(image, prediction), "RGB").save(preview_path, quality=92)
    details = getattr(model, "_prop_last_details", {})
    selected = probability[prediction]
    confidence = float(selected.mean()) if selected.size else float(probability.max(initial=0))
    pixel_threshold = float(manifest["runtime"]["pixelThreshold"])
    uncertain = abs(confidence - pixel_threshold) <= .10 or abs(float(details.get("presence", 0)) -
        float(manifest["runtime"]["presenceThreshold"])) <= .10
    return {"status": "mask", "mask": str(mask_path), "preview": str(preview_path),
        "pixels": int(prediction.sum()), "confidence": confidence,
        "presence": float(details.get("presence", 0)),
        "activeLearningBucket": "uncertain" if uncertain else
            "confident" if prediction.any() else "random",
        "modelId": manifest["modelId"]}


def main():
    if sys.argv[1:] == ["--self-test"]: self_test(); return
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--session", action="store_true")
    parser.add_argument("--image", type=Path)
    parser.add_argument("--alpha", type=Path)
    parser.add_argument("--mask", type=Path)
    parser.add_argument("--preview", type=Path)
    args = parser.parse_args()
    torch, model, device, manifest = load_package(args.package)
    if manifest.get("schemaVersion") != 2:
        raise RuntimeError("Training proposals require an RVM-conditioned v2 package")
    if args.session:
        send(status="ready", modelId=manifest["modelId"])
        for line in sys.stdin:
            try:
                request = json.loads(line)
                if request.get("command") == "quit": return
                send(**generate(torch, model, device, manifest, request["image"], request["alpha"],
                                request["mask"], request["preview"]))
            except Exception as error: send(status="error", message=str(error))
        return
    if not all((args.image, args.alpha, args.mask, args.preview)):
        raise RuntimeError("image, alpha, mask and preview are required outside session mode")
    send(**generate(torch, model, device, manifest, args.image, args.alpha, args.mask, args.preview))


if __name__ == "__main__":
    try: main()
    except Exception as error:
        send(status="error", message=str(error)); raise
