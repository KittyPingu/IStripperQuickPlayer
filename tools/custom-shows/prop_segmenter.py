#!/usr/bin/env python3
"""QuickPlayer prop-segmenter model/package and mask post-processing helpers."""
import hashlib
import json
from collections import deque
from pathlib import Path

ARCHITECTURE = "deeplabv3-resnet50-binary-v1"
INPUT_SIZE = 512
MEAN = (0.485, 0.456, 0.406)
STD = (0.229, 0.224, 0.225)


def digest(path):
    value = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def build_model(torch, pretrained=False):
    from torchvision.models.segmentation import (
        DeepLabV3_ResNet50_Weights, deeplabv3_resnet50)
    if pretrained:
        model = deeplabv3_resnet50(
            weights=DeepLabV3_ResNet50_Weights.COCO_WITH_VOC_LABELS_V1,
            aux_loss=True)
        model.classifier[4] = torch.nn.Conv2d(256, 1, 1)
        model.aux_classifier[4] = torch.nn.Conv2d(256, 1, 1)
        torch.nn.init.normal_(model.classifier[4].weight, std=.01)
        torch.nn.init.zeros_(model.classifier[4].bias)
        torch.nn.init.normal_(model.aux_classifier[4].weight, std=.01)
        torch.nn.init.zeros_(model.aux_classifier[4].bias)
        return model
    return deeplabv3_resnet50(weights=None, weights_backbone=None,
                              num_classes=1, aux_loss=True)


def load_package(package, device=None):
    import torch
    package = Path(package).resolve()
    manifest_path, checkpoint = package / "manifest.json", package / "model.pth"
    if not manifest_path.is_file() or not checkpoint.is_file():
        raise RuntimeError("Prop-segmenter package is incomplete")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if manifest.get("schemaVersion") != 1 or manifest.get("architecture") != ARCHITECTURE:
        raise RuntimeError("Prop-segmenter package contract is unsupported")
    actual = digest(checkpoint)
    if actual.lower() != str(manifest.get("checkpointSha256", "")).lower():
        raise RuntimeError("Prop-segmenter checkpoint hash validation failed")
    device = torch.device(device or ("cuda" if torch.cuda.is_available() else "cpu"))
    model = build_model(torch).eval().to(device)
    model.load_state_dict(torch.load(checkpoint, map_location=device, weights_only=True))
    return torch, model, device, manifest


def prepare_image(image, size=INPUT_SIZE):
    import numpy as np
    from PIL import Image
    if not isinstance(image, Image.Image):
        image = Image.fromarray(np.asarray(image, dtype=np.uint8), "RGB")
    image = image.convert("RGB")
    width, height = image.size
    scale = min(size / width, size / height)
    resized = (max(1, round(width * scale)), max(1, round(height * scale)))
    left, top = (size - resized[0]) // 2, (size - resized[1]) // 2
    fill = tuple(round(value * 255) for value in MEAN)
    canvas = Image.new("RGB", (size, size), fill)
    canvas.paste(image.resize(resized, Image.Resampling.BILINEAR), (left, top))
    pixels = np.asarray(canvas, dtype=np.float32).transpose(2, 0, 1) / 255
    pixels = (pixels - np.asarray(MEAN, dtype=np.float32)[:, None, None]) / \
        np.asarray(STD, dtype=np.float32)[:, None, None]
    return pixels, (left, top, resized[0], resized[1]), (width, height)


def predict_mask(torch, model, device, image, threshold=.5, size=INPUT_SIZE):
    import numpy as np
    from PIL import Image
    pixels, region, original = prepare_image(image, size)
    tensor = torch.from_numpy(pixels).unsqueeze(0).to(device)
    bf16 = device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 8
    with torch.inference_mode(), torch.autocast(device_type=device.type,
            dtype=torch.bfloat16, enabled=bf16):
        logits = model(tensor)["out"][0, 0].float().cpu().numpy()
    left, top, width, height = region
    probability = 1 / (1 + np.exp(-np.clip(logits[top:top + height, left:left + width], -30, 30)))
    probability = np.asarray(Image.fromarray(probability.astype(np.float32), "F").resize(
        original, Image.Resampling.BILINEAR), dtype=np.float32)
    return probability >= float(threshold), probability


def _dilate(mask, radius):
    import numpy as np
    from PIL import Image, ImageFilter
    if radius <= 0:
        return np.asarray(mask, dtype=bool)
    # MaxFilter is limited to practical odd kernels. Repeated passes preserve
    # the intended radius for large frames without a scipy dependency.
    value = Image.fromarray(np.asarray(mask, dtype=np.uint8) * 255, "L")
    remaining = int(radius)
    while remaining:
        step = min(remaining, 31)
        value = value.filter(ImageFilter.MaxFilter(step * 2 + 1))
        remaining -= step
    return np.asarray(value, dtype=np.uint8) >= 128


def filter_components(prop_mask, person_mask, base_radius=24):
    import numpy as np
    prop = np.asarray(prop_mask, dtype=bool)
    person = np.asarray(person_mask, dtype=bool)
    if prop.shape != person.shape or prop.ndim != 2:
        raise RuntimeError("Prop and person masks must have identical 2D dimensions")
    radius = max(1, round(base_radius * min(prop.shape) / 512))
    near = _dilate(person, radius)
    visited = np.zeros(prop.shape, dtype=bool)
    retained = np.zeros(prop.shape, dtype=bool)
    components = []
    height, width = prop.shape
    for start_y, start_x in zip(*np.where(prop & ~visited)):
        if visited[start_y, start_x]:
            continue
        pending, pixels, keep = deque([(int(start_y), int(start_x))]), [], False
        visited[start_y, start_x] = True
        while pending:
            y, x = pending.popleft(); pixels.append((y, x)); keep |= bool(near[y, x])
            for ny, nx in ((y - 1, x - 1), (y - 1, x), (y - 1, x + 1),
                           (y, x - 1), (y, x + 1),
                           (y + 1, x - 1), (y + 1, x), (y + 1, x + 1)):
                if 0 <= ny < height and 0 <= nx < width and prop[ny, nx] and not visited[ny, nx]:
                    visited[ny, nx] = True; pending.append((ny, nx))
        if keep:
            ys, xs = zip(*pixels); retained[ys, xs] = True
        components.append({"pixels": len(pixels), "retained": bool(keep)})
    return retained, components, radius


def augment_rvm_mask(prop_mask, person_mask, base_radius=24):
    import numpy as np
    retained, components, radius = filter_components(prop_mask, person_mask, base_radius)
    return np.asarray(person_mask, dtype=bool) | retained, components, radius


def self_test():
    import numpy as np
    person = np.zeros((64, 64), bool); person[20:44, 20:44] = True
    prop = np.zeros_like(person); prop[29:35, 44:50] = True; prop[2:6, 2:6] = True
    union, components, radius = augment_rvm_mask(prop, person)
    assert union[30, 47] and not union[3, 3]
    assert sum(value["retained"] for value in components) == 1 and radius == 3
    print(json.dumps({"status": "ok", "message": "prop-segmenter self-test passed"}))


if __name__ == "__main__":
    import sys
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    else:
        raise SystemExit("Use --self-test")
