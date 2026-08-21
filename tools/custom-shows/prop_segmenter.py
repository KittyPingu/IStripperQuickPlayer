#!/usr/bin/env python3
"""QuickPlayer prop-segmenter model/package and mask post-processing helpers."""
import hashlib
import json
from collections import deque
from pathlib import Path

ARCHITECTURE = "deeplabv3-resnet50-binary-v1"
ARCHITECTURE_V2 = "rvm-conditioned-convnext-fpn-v2"
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
    schema = manifest.get("schemaVersion")
    architecture = manifest.get("architecture")
    if (schema, architecture) not in ((1, ARCHITECTURE), (2, ARCHITECTURE_V2)):
        raise RuntimeError("Prop-segmenter package contract is unsupported")
    actual = digest(checkpoint)
    if actual.lower() != str(manifest.get("checkpointSha256", "")).lower():
        raise RuntimeError("Prop-segmenter checkpoint hash validation failed")
    device = torch.device(device or ("cuda" if torch.cuda.is_available() else "cpu"))
    if architecture == ARCHITECTURE_V2:
        from prop_segmenter_v2 import build_model as build_v2_model
        model = build_v2_model(torch, pretrained=False).eval().to(device)
    else:
        model = build_model(torch).eval().to(device)
    model.load_state_dict(torch.load(checkpoint, map_location=device, weights_only=True))
    model._prop_manifest = manifest
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


def predict_mask(torch, model, device, image, threshold=.5, size=INPUT_SIZE,
                 rvm_alpha=None):
    manifest = getattr(model, "_prop_manifest", {})
    if manifest.get("architecture") == ARCHITECTURE_V2:
        if rvm_alpha is None:
            raise RuntimeError("The v2 prop segmenter requires an RVM alpha mask")
        from prop_segmenter_v2 import filter_prediction, predict
        probability, presence = predict(torch, model, device, image, rvm_alpha,
            int(manifest.get("input", {}).get("cropSize", size)),
            float(manifest.get("input", {}).get("rvmThreshold", .4)))
        runtime = manifest.get("runtime", {})
        retained, components, radius = filter_prediction(probability, rvm_alpha,
            float(runtime.get("pixelThreshold", threshold)), presence,
            float(runtime.get("presenceThreshold", .5)),
            int(runtime.get("maxComponentDistanceAt768", 96)),
            int(manifest.get("input", {}).get("cropSize", 768)))
        model._prop_last_details = {"presence": presence, "components": components,
                                    "proximityRadius": radius}
        return retained, probability
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


def predict_mask_tensor(torch, model, device, image, threshold=.5,
                        size=INPUT_SIZE, rvm_alpha=None):
    """Predict directly from a CHW GPU frame and keep the mask on the GPU."""
    if image.ndim != 3 or image.shape[0] != 3:
        raise RuntimeError("Prop-segmenter tensor input must be CHW RGB")
    manifest = getattr(model, "_prop_manifest", {})
    if manifest.get("architecture") == ARCHITECTURE_V2:
        if rvm_alpha is None:
            raise RuntimeError("The v2 prop segmenter requires an RVM alpha mask")
        pixels = image.detach().permute(1, 2, 0).to("cpu").numpy()
        if pixels.max(initial=0) <= 1: pixels = pixels * 255
        person = rvm_alpha.detach().float().to("cpu").numpy()
        predicted, _ = predict_mask(torch, model, device,
            pixels.astype("uint8"), threshold, size, person)
        return torch.from_numpy(predicted).to(device)
    image = image.to(device=device, dtype=torch.float32)
    height, width = image.shape[-2:]
    scale = min(size / width, size / height)
    resized_width = max(1, round(width * scale))
    resized_height = max(1, round(height * scale))
    left = (size - resized_width) // 2
    top = (size - resized_height) // 2
    resized = torch.nn.functional.interpolate(image.unsqueeze(0),
        size=(resized_height, resized_width), mode="bilinear",
        align_corners=False)
    mean = torch.tensor(MEAN, device=device, dtype=torch.float32)[None, :, None, None]
    std = torch.tensor(STD, device=device, dtype=torch.float32)[None, :, None, None]
    fill = torch.tensor(tuple(round(value * 255) / 255 for value in MEAN),
                        device=device, dtype=torch.float32)[None, :, None, None]
    canvas = ((fill - mean) / std).expand(1, 3, size, size).clone()
    canvas[:, :, top:top + resized_height, left:left + resized_width] = \
        (resized - mean) / std
    bf16 = device.type == "cuda" and \
        torch.cuda.get_device_capability(device)[0] >= 8
    with torch.inference_mode(), torch.autocast(device_type=device.type,
            dtype=torch.bfloat16, enabled=bf16):
        logits = model(canvas)["out"][:, :, top:top + resized_height,
                                      left:left + resized_width]
    probability = torch.nn.functional.interpolate(logits.float().sigmoid(),
        size=(height, width), mode="bilinear", align_corners=False)[0, 0]
    return probability >= float(threshold)


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


def _filter_components_python(prop, person, radius):
    import numpy as np
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


def filter_components(prop_mask, person_mask, base_radius=24):
    import numpy as np
    prop = np.asarray(prop_mask, dtype=bool)
    person = np.asarray(person_mask, dtype=bool)
    if prop.shape != person.shape or prop.ndim != 2:
        raise RuntimeError("Prop and person masks must have identical 2D dimensions")
    radius = max(1, round(base_radius * min(prop.shape) / 512))
    try:
        import cv2
        count, labels = cv2.connectedComponents(prop.astype(np.uint8),
                                                 connectivity=8)
        kernel = np.ones((radius * 2 + 1, radius * 2 + 1), dtype=np.uint8)
        near = cv2.dilate(person.astype(np.uint8), kernel) != 0
        retained_labels = np.unique(labels[near & prop])
        keep = np.zeros(count, dtype=bool)
        keep[retained_labels[retained_labels > 0]] = True
        retained = keep[labels]
        sizes = np.bincount(labels.ravel(), minlength=count)
        components = [{"pixels": int(sizes[index]),
                       "retained": bool(keep[index])}
                      for index in range(1, count)]
        return retained, components, radius
    except ImportError:
        return _filter_components_python(prop, person, radius)


def augment_rvm_mask(prop_mask, person_mask, base_radius=24):
    import numpy as np
    retained, components, radius = filter_components(prop_mask, person_mask, base_radius)
    return np.asarray(person_mask, dtype=bool) | retained, components, radius


def self_test():
    import tempfile
    import numpy as np
    person = np.zeros((64, 64), bool); person[20:44, 20:44] = True
    prop = np.zeros_like(person); prop[29:35, 44:50] = True; prop[2:6, 2:6] = True
    union, components, radius = augment_rvm_mask(prop, person)
    assert union[30, 47] and not union[3, 3]
    assert sum(value["retained"] for value in components) == 1 and radius == 3
    native = filter_components(prop, person, 24)
    fallback = _filter_components_python(prop, person, 3)
    assert np.array_equal(native[0], fallback[0])
    assert native[1] == fallback[1] and native[2] == fallback[2]
    # Exercise the side-by-side v2 package loader and checkpoint hash contract.
    import torch
    from prop_segmenter_v2 import build_model as build_v2_model
    with tempfile.TemporaryDirectory(prefix="iqp-prop-package-") as value:
        package = Path(value)
        checkpoint = package / "model.pth"
        model = build_v2_model(torch, pretrained=False)
        torch.save(model.state_dict(), checkpoint); del model
        checkpoint_hash = digest(checkpoint)
        manifest = {"schemaVersion": 2, "modelId": "self-test-v2",
            "architecture": ARCHITECTURE_V2, "checkpointSha256": checkpoint_hash,
            "confidenceThreshold": .55, "proximityRadiusAt512": 64,
            "inputSize": 768,
            "input": {"cropSize": 768, "rvmThreshold": .4},
            "runtime": {"pixelThreshold": .55, "presenceThreshold": .5,
                        "maxComponentDistanceAt768": 96}}
        (package / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        _, loaded, loaded_device, loaded_manifest = load_package(package, "cpu")
        assert loaded_device.type == "cpu" and loaded_manifest["schemaVersion"] == 2
        assert loaded.features[0][0].in_channels == 5
        del loaded
        manifest["checkpointSha256"] = "0" * 64
        (package / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        try: load_package(package, "cpu"); raise AssertionError("invalid hash accepted")
        except RuntimeError as error: assert "hash validation" in str(error)
    print(json.dumps({"status": "ok", "message": "prop-segmenter self-test passed"}))


if __name__ == "__main__":
    import sys
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    else:
        raise SystemExit("Use --self-test")
