#!/usr/bin/env python3
"""Train, evaluate, and package QuickPlayer's binary prop segmenter."""
import argparse
import hashlib
import io
import json
import math
import os
import random
import shutil
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "custom-shows"))
try:
    from prop_segmenter import (ARCHITECTURE, INPUT_SIZE, MEAN, STD,
                                build_model, digest, filter_components, prepare_image)
except ImportError:
    # Published Training Studio links the shared helper beside this worker.
    from prop_segmenter import (ARCHITECTURE, INPUT_SIZE, MEAN, STD,
                                build_model, digest, filter_components, prepare_image)


def emit(stage, message="", **values):
    print(json.dumps({"stage": stage, "message": message, **values},
                     separators=(",", ":")), flush=True)


def atomic_json(path, value):
    path = Path(path); path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp-" + os.urandom(6).hex())
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def load_manifest(root):
    value = json.loads((root / "dataset.json").read_text(encoding="utf-8-sig"))
    if value.get("schemaVersion") != 1:
        raise RuntimeError("Unsupported training dataset schema")
    records = sorted((root / "records").glob("*/*.json"))
    if records:
        samples = []
        for path in records:
            record = json.loads(path.read_text(encoding="utf-8-sig"))
            if record.get("schemaVersion") != 1 or record.get("datasetId") != value.get("datasetId"):
                raise RuntimeError(f"Training sample record does not belong to this dataset: {path}")
            sample = record["sample"]
            sample["_recordPath"] = str(path.relative_to(root)).replace("\\", "/")
            sample["_recordSha256"] = digest(path)
            samples.append(sample)
        value["samples"] = samples
    elif (root / "samples.json").is_file():
        samples_path = root / "samples.json"
        ledger = json.loads(samples_path.read_text(encoding="utf-8-sig"))
        if ledger.get("schemaVersion") != 1 or ledger.get("datasetId") != value.get("datasetId"):
            raise RuntimeError("Training sample ledger does not belong to this dataset")
        value["samples"] = ledger.get("samples", [])
    return value


def accepted_samples(manifest, split):
    return [sample for sample in manifest.get("samples", [])
            if sample.get("decision") in ("positive", "negative") and
            sample.get("split") == split and sample.get("framePath") and
            sample.get("propMaskPath")]


def resolution_samples(samples, minimum_resolution):
    if minimum_resolution <= 0:
        return list(samples)
    return [sample for sample in samples
            if min(int(sample.get("width", 0)), int(sample.get("height", 0))) >= minimum_resolution]


def annotate_mask_statistics(root, samples, input_size):
    import numpy as np
    from PIL import Image
    for sample in samples:
        if sample.get("decision") != "positive":
            sample["_maskFraction"] = 0.
            sample["_positivePixels"] = 0
            continue
        mask = np.asarray(Image.open(root / sample["propMaskPath"]).convert("L"), dtype=np.uint8)
        positive = int((mask >= 128).sum())
        scale = min(input_size / mask.shape[1], input_size / mask.shape[0])
        sample["_positivePixels"] = positive
        sample["_maskFraction"] = positive * scale * scale / (input_size * input_size)


def sampling_plan(samples, negative_ratio):
    positives = [sample for sample in samples if sample.get("decision") == "positive"]
    negatives = [sample for sample in samples if sample.get("decision") == "negative"]
    if not positives or not negatives:
        return list(samples), len(samples), len(negatives)
    expected_negatives = max(1, round(len(positives) * negative_ratio / (1 - negative_ratio)))
    epoch_size = len(positives) + expected_negatives
    return positives + negatives, epoch_size, expected_negatives


def source_balanced_weights(samples, negative_ratio):
    from collections import defaultdict
    groups = defaultdict(list)
    for index, sample in enumerate(samples):
        groups[(sample.get("decision"), sample.get("sourceId", sample.get("id")))].append(index)
    class_sources = defaultdict(set)
    for decision, source_id in groups: class_sources[decision].add(source_id)
    class_target = {"positive": 1 - negative_ratio, "negative": negative_ratio}
    weights = [0.] * len(samples)
    for (decision, source_id), indexes in groups.items():
        for index in indexes:
            sample = samples[index]
            hard = 2. if sample.get("feedbackPriority") or sample.get("burstId") else 1.
            fraction = float(sample.get("_maskFraction", 0.))
            size_weight = 2. if decision == "positive" and fraction <= .005 else \
                1.35 if decision == "positive" and fraction <= .02 else 1.
            weights[index] = hard * size_weight / \
                (max(1, len(class_sources[decision])) * len(indexes))
    for decision, target in class_target.items():
        total = sum(weight for weight, sample in zip(weights, samples)
                    if sample.get("decision") == decision)
        if total:
            for index, sample in enumerate(samples):
                if sample.get("decision") == decision:
                    weights[index] *= target / total
    return weights


class PropDataset:
    def __init__(self, root, samples, training, input_size=INPUT_SIZE):
        self.root, self.samples, self.training, self.input_size = \
            Path(root), samples, training, input_size

    def __len__(self):
        return len(self.samples)

    def __getitem__(self, index):
        import numpy as np
        import torch
        from PIL import Image, ImageEnhance, ImageFilter
        sample = self.samples[index]
        image = Image.open(self.root / sample["framePath"]).convert("RGB")
        mask = Image.open(self.root / sample["propMaskPath"]).convert("L")
        if self.training:
            if random.random() < .5:
                image = image.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
                mask = mask.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
            image = augment_appearance(image, np)
            geometry = random.random()
            if geometry < .5:
                image, mask = letterbox_pair(image, mask, self.input_size)
            else:
                scale = random.uniform(.75, 1.5)
                target = max(64, round(self.input_size * scale))
                ratio = target / min(image.size)
                size = (max(1, round(image.width * ratio)), max(1, round(image.height * ratio)))
                image = image.resize(size, Image.Resampling.BILINEAR)
                mask = mask.resize(size, Image.Resampling.NEAREST)
                fill = tuple(round(value * 255) for value in MEAN)
                canvas = Image.new("RGB", (max(self.input_size, image.width),
                                           max(self.input_size, image.height)), fill)
                canvas_mask = Image.new("L", canvas.size, 0)
                left, top = (canvas.width - image.width) // 2, (canvas.height - image.height) // 2
                canvas.paste(image, (left, top)); canvas_mask.paste(mask, (left, top))
                max_x, max_y = canvas.width - self.input_size, canvas.height - self.input_size
                foreground = canvas_mask.getbbox()
                if foreground and geometry < .8:
                    centre_x = (foreground[0] + foreground[2]) // 2
                    centre_y = (foreground[1] + foreground[3]) // 2
                    jitter = round(self.input_size * .2)
                    x = max(0, min(max_x, centre_x - self.input_size // 2 +
                                  random.randint(-jitter, jitter)))
                    y = max(0, min(max_y, centre_y - self.input_size // 2 +
                                  random.randint(-jitter, jitter)))
                else:
                    x, y = random.randint(0, max_x), random.randint(0, max_y)
                image = canvas.crop((x, y, x + self.input_size, y + self.input_size))
                mask = canvas_mask.crop((x, y, x + self.input_size, y + self.input_size))
            if random.random() < .08:
                image = image.filter(ImageFilter.GaussianBlur(random.uniform(.2, 1.2)))
            pixels = np.asarray(image, dtype=np.float32).transpose(2, 0, 1) / 255
            if random.random() < .08:
                pixels = np.clip(pixels + np.random.normal(0, random.uniform(.005, .025),
                    pixels.shape).astype(np.float32), 0, 1)
            pixels = (pixels - np.asarray(MEAN, np.float32)[:, None, None]) / \
                np.asarray(STD, np.float32)[:, None, None]
        else:
            pixels, region, _ = prepare_image(image, self.input_size)
            left, top, width, height = region
            canvas_mask = Image.new("L", (self.input_size, self.input_size), 0)
            canvas_mask.paste(mask.resize((width, height), Image.Resampling.NEAREST), (left, top))
            mask = canvas_mask
        target = (np.asarray(mask, dtype=np.uint8) >= 128).astype(np.float32)[None]
        return torch.from_numpy(pixels), torch.from_numpy(target), sample["id"]


def letterbox_pair(image, mask, input_size):
    from PIL import Image
    scale = min(input_size / image.width, input_size / image.height)
    size = (max(1, round(image.width * scale)), max(1, round(image.height * scale)))
    left, top = (input_size - size[0]) // 2, (input_size - size[1]) // 2
    fill = tuple(round(value * 255) for value in MEAN)
    canvas = Image.new("RGB", (input_size, input_size), fill)
    canvas_mask = Image.new("L", (input_size, input_size), 0)
    canvas.paste(image.resize(size, Image.Resampling.BILINEAR), (left, top))
    canvas_mask.paste(mask.resize(size, Image.Resampling.NEAREST), (left, top))
    return canvas, canvas_mask


def augment_appearance(image, np):
    from PIL import Image, ImageEnhance, ImageFilter
    if random.random() < .65:
        image = ImageEnhance.Brightness(image).enhance(random.uniform(.75, 1.25))
        image = ImageEnhance.Contrast(image).enhance(random.uniform(.75, 1.25))
        image = ImageEnhance.Color(image).enhance(random.uniform(.65, 1.35))
    if random.random() < .25:
        hsv = np.asarray(image.convert("HSV"), dtype=np.uint8).copy()
        hsv[..., 0] = (hsv[..., 0].astype(np.int16) + random.randint(-12, 12)) % 256
        image = Image.fromarray(hsv, "HSV").convert("RGB")
    if random.random() < .08:
        kernel = [0.] * 25
        horizontal = random.random() < .5
        for index in range(5): kernel[2 * 5 + index if horizontal else index * 5 + 2] = .2
        image = image.filter(ImageFilter.Kernel((5, 5), kernel, scale=1))
    if random.random() < .08:
        stream = io.BytesIO(); image.save(stream, "JPEG", quality=random.randint(45, 90))
        stream.seek(0); image = Image.open(stream).convert("RGB")
    return image


def dice_loss(logits, target):
    probability = logits.sigmoid()
    numerator = 2 * (probability * target).sum(dim=(1, 2, 3)) + 1
    denominator = probability.sum(dim=(1, 2, 3)) + target.sum(dim=(1, 2, 3)) + 1
    return (1 - numerator / denominator).mean()


def tversky_loss(logits, target, alpha=.5, beta=.5):
    probability = logits.sigmoid()
    true_positive = (probability * target).sum(dim=(1, 2, 3))
    false_positive = (probability * (1 - target)).sum(dim=(1, 2, 3))
    false_negative = ((1 - probability) * target).sum(dim=(1, 2, 3))
    return (1 - (true_positive + 1) /
            (true_positive + alpha * false_positive + beta * false_negative + 1)).mean()


def focal_bce(torch, logits, target, pos_weight, gamma=2.):
    loss = torch.nn.functional.binary_cross_entropy_with_logits(
        logits, target, pos_weight=pos_weight, reduction="none")
    probability = logits.sigmoid()
    correct_probability = probability * target + (1 - probability) * (1 - target)
    return ((1 - correct_probability).pow(gamma) * loss).mean()


def boundary_loss(torch, logits, target):
    import torch.nn.functional as functional
    probability = logits.sigmoid()
    predicted = functional.max_pool2d(probability, 3, 1, 1) - \
        (-functional.max_pool2d(-probability, 3, 1, 1))
    truth = functional.max_pool2d(target, 3, 1, 1) - \
        (-functional.max_pool2d(-target, 3, 1, 1))
    numerator = 2 * (predicted * truth).sum(dim=(1, 2, 3)) + 1
    denominator = predicted.sum(dim=(1, 2, 3)) + truth.sum(dim=(1, 2, 3)) + 1
    return (1 - numerator / denominator).mean()


def segmentation_loss(torch, output, target, pos_weight):
    total = (focal_bce(torch, output["out"], target, pos_weight) +
             dice_loss(output["out"], target) +
             .5 * tversky_loss(output["out"], target) +
             .2 * boundary_loss(torch, output["out"], target))
    if "aux" in output:
        aux = (focal_bce(torch, output["aux"], target, pos_weight) +
               dice_loss(output["aux"], target) +
               .5 * tversky_loss(output["aux"], target) +
               .1 * boundary_loss(torch, output["aux"], target))
        total += .4 * aux
    return total


def confusion(probability, target, threshold):
    prediction = probability >= threshold; truth = target >= .5
    return (int((prediction & truth).sum()), int((prediction & ~truth).sum()),
            int((~prediction & truth).sum()), int((~prediction & ~truth).sum()))


def scores(values):
    tp, fp, fn, tn = values
    return {"iou": tp / max(1, tp + fp + fn),
            "dice": 2 * tp / max(1, 2 * tp + fp + fn),
            "precision": tp / max(1, tp + fp),
            "recall": tp / max(1, tp + fn)}


def selection_score(value):
    raw = (.4 * value["dice"] + .15 * value["macroDice"] +
           .15 * value["positiveRecall"] + .1 * value["smallObjectRecall"] +
           .2 * value["boundaryF1"])
    downstream = value.get("rvmUnion")
    if not downstream or downstream.get("coverage", 0.) < .8:
        return raw
    exterior = downstream["exteriorProp"]
    final = downstream["finalForeground"]
    return .55 * raw + .2 * exterior["dice"] + .1 * exterior["recall"] + .15 * final["dice"]


def boundary_f1(torch, prediction, truth):
    import torch.nn.functional as functional
    prediction = prediction.float()[None, None]
    truth = truth.float()[None, None]
    prediction_boundary = prediction.bool() & ~(-functional.max_pool2d(-prediction, 3, 1, 1)).bool()
    truth_boundary = truth.bool() & ~(-functional.max_pool2d(-truth, 3, 1, 1)).bool()
    if not truth_boundary.any(): return 1. if not prediction_boundary.any() else 0.
    prediction_near = functional.max_pool2d(prediction_boundary.float(), 5, 1, 2).bool()
    truth_near = functional.max_pool2d(truth_boundary.float(), 5, 1, 2).bool()
    precision = float((prediction_boundary & truth_near).sum()) / max(1, int(prediction_boundary.sum()))
    recall = float((truth_boundary & prediction_near).sum()) / max(1, int(truth_boundary.sum()))
    return 2 * precision * recall / max(1e-8, precision + recall)


def load_evaluation_mask(root, relative, input_size):
    import numpy as np
    from PIL import Image
    mask = Image.open(Path(root) / relative).convert("L")
    scale = min(input_size / mask.width, input_size / mask.height)
    size = (max(1, round(mask.width * scale)), max(1, round(mask.height * scale)))
    left, top = (input_size - size[0]) // 2, (input_size - size[1]) // 2
    canvas = Image.new("L", (input_size, input_size), 0)
    canvas.paste(mask.resize(size, Image.Resampling.NEAREST), (left, top))
    return np.asarray(canvas, dtype=np.uint8) >= 128


def add_confusion(destination, value):
    for index, current in enumerate(value): destination[index] += current


def mask_size_bucket(fraction):
    if fraction <= .005: return "under-0.5pct"
    if fraction <= .02: return "0.5-2pct"
    if fraction <= .05: return "2-5pct"
    return "over-5pct"


def evaluate(torch, model, loader, device, thresholds=(.5,), root=None, samples=None):
    totals = {threshold: [0, 0, 0, 0] for threshold in thresholds}
    per_image = {threshold: {"dice": [], "recall": [], "smallRecall": [], "boundary": [],
                             "negativeFrames": 0, "negativeFramesWithPrediction": 0,
                             "sourceTotals": {}, "sizeBuckets": {}, "rvmAvailable": 0,
                             "rvmExterior": [0, 0, 0, 0], "rvmUnion": [0, 0, 0, 0]}
                 for threshold in thresholds}
    sample_lookup = {sample["id"]: sample for sample in (samples or [])}
    total_samples = len(sample_lookup)
    model.eval()
    bf16 = device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 8
    with torch.inference_mode():
        for images, target, sample_ids in loader:
            images, target = images.to(device), target.to(device)
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=bf16):
                probability = model(images)["out"].float().sigmoid()
            evaluation_artifacts = []
            for sample_id in sample_ids:
                sample = sample_lookup.get(str(sample_id))
                if root is None or not sample or not sample.get("rvmPersonMaskPath") or \
                        not sample.get("desiredForegroundPath"):
                    evaluation_artifacts.append(None); continue
                try:
                    evaluation_artifacts.append((
                        load_evaluation_mask(root, sample["rvmPersonMaskPath"], images.shape[-1]),
                        load_evaluation_mask(root, sample["desiredForegroundPath"], images.shape[-1])))
                except (FileNotFoundError, OSError, ValueError):
                    evaluation_artifacts.append(None)
            for threshold in thresholds:
                current = confusion(probability, target, threshold)
                add_confusion(totals[threshold], current)
                for index in range(target.shape[0]):
                    truth = target[index] >= .5
                    truth_pixels = int(truth.sum())
                    predicted = probability[index] >= threshold
                    sample = sample_lookup.get(str(sample_ids[index]))
                    source_id = sample.get("sourceId", sample.get("id")) if sample else str(sample_ids[index])
                    source_totals = per_image[threshold]["sourceTotals"].setdefault(source_id, [0, 0, 0, 0])
                    add_confusion(source_totals, confusion(predicted, truth, .5))
                    if truth_pixels == 0:
                        per_image[threshold]["negativeFrames"] += 1
                        per_image[threshold]["negativeFramesWithPrediction"] += int(bool(predicted.any()))
                    else:
                        tp = int((predicted & truth).sum())
                        fp = int((predicted & ~truth).sum())
                        fn = truth_pixels - tp
                        per_image[threshold]["dice"].append(2 * tp / max(1, 2 * tp + fp + fn))
                        recall = tp / truth_pixels
                        per_image[threshold]["recall"].append(recall)
                        per_image[threshold]["boundary"].append(
                            boundary_f1(torch, predicted[0] if predicted.ndim == 3 else predicted,
                                        truth[0] if truth.ndim == 3 else truth))
                        fraction = truth_pixels / truth.numel()
                        bucket = per_image[threshold]["sizeBuckets"].setdefault(
                            mask_size_bucket(fraction), {"dice": [], "recall": []})
                        bucket["dice"].append(2 * tp / max(1, 2 * tp + fp + fn))
                        bucket["recall"].append(recall)
                        if fraction <= .02: per_image[threshold]["smallRecall"].append(recall)
                    if evaluation_artifacts[index] is not None:
                        try:
                            person, desired = evaluation_artifacts[index]
                            prediction_np = predicted[0].detach().cpu().numpy() if predicted.ndim == 3 \
                                else predicted.detach().cpu().numpy()
                            truth_np = truth[0].detach().cpu().numpy() if truth.ndim == 3 \
                                else truth.detach().cpu().numpy()
                            retained, _, _ = filter_components(prediction_np, person, 24)
                            exterior = retained & ~person
                            needed = truth_np & ~person
                            add_confusion(per_image[threshold]["rvmExterior"],
                                          confusion(exterior, needed, .5))
                            add_confusion(per_image[threshold]["rvmUnion"],
                                          confusion(person | retained, desired, .5))
                            per_image[threshold]["rvmAvailable"] += 1
                        except (FileNotFoundError, OSError, ValueError):
                            pass
    result = {}
    for threshold, value in totals.items():
        result[threshold] = scores(value)
        current = per_image[threshold]
        result[threshold]["macroDice"] = sum(current["dice"]) / max(1, len(current["dice"]))
        result[threshold]["positiveRecall"] = sum(current["recall"]) / max(1, len(current["recall"]))
        result[threshold]["smallObjectRecall"] = (sum(current["smallRecall"]) /
            max(1, len(current["smallRecall"]))) if current["smallRecall"] else result[threshold]["positiveRecall"]
        result[threshold]["boundaryF1"] = sum(current["boundary"]) / max(1, len(current["boundary"]))
        source_scores = [scores(value)["dice"] for value in current["sourceTotals"].values()
                         if value[0] + value[2] > 0]
        result[threshold]["perVideoDice"] = sum(source_scores) / max(1, len(source_scores))
        result[threshold]["negativeFrameFalsePositiveRate"] = \
            current["negativeFramesWithPrediction"] / max(1, current["negativeFrames"])
        result[threshold]["negativeFrameCount"] = current["negativeFrames"]
        result[threshold]["sizeBuckets"] = {
            name: {"count": len(value["dice"]),
                   "dice": sum(value["dice"]) / max(1, len(value["dice"])),
                   "recall": sum(value["recall"]) / max(1, len(value["recall"]))}
            for name, value in current["sizeBuckets"].items()}
        if total_samples:
            result[threshold]["rvmUnion"] = {
                "coverage": current["rvmAvailable"] / total_samples,
                "sampleCount": current["rvmAvailable"],
                "exteriorProp": scores(current["rvmExterior"]),
                "finalForeground": scores(current["rvmUnion"])}
        result[threshold]["rawSelectionScore"] = (
            .4 * result[threshold]["dice"] + .15 * result[threshold]["macroDice"] +
            .15 * result[threshold]["positiveRecall"] +
            .1 * result[threshold]["smallObjectRecall"] +
            .2 * result[threshold]["boundaryF1"])
        result[threshold]["selectionScore"] = selection_score(result[threshold])
    return result


def save_error_review(torch, model, loader, device, threshold, destination, limit=50, samples=None):
    """Save diverse validation errors, deduplicated by source and ten-second window."""
    import numpy as np
    from PIL import Image
    destination.mkdir(parents=True, exist_ok=True)
    sample_lookup = {sample["id"]: sample for sample in (samples or [])}
    candidates = {"false-positive": {}, "false-negative": {}}
    model.eval()
    bf16 = device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 8
    with torch.inference_mode():
        for images, targets, sample_ids in loader:
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=bf16):
                probabilities = model(images.to(device))["out"].float().sigmoid().cpu()
            for index, sample_id in enumerate(sample_ids):
                prediction = probabilities[index, 0].numpy() >= threshold
                truth = targets[index, 0].numpy() >= .5
                false_positive = prediction & ~truth
                false_negative = ~prediction & truth
                sample = sample_lookup.get(str(sample_id), {})
                source_id = sample.get("sourceId", str(sample_id))
                window = sample.get("burstId") or int(sample.get("timestampMs", 0)) // 10_000
                group = (source_id, window)
                for kind, mask, denominator in (
                        ("false-positive", false_positive, max(1, int(prediction.sum()))),
                        ("false-negative", false_negative, max(1, int(truth.sum())))):
                    area = int(mask.sum())
                    if area:
                        fraction = area / denominator
                        score = math.sqrt(area) * (.5 + fraction)
                        value = {"score": score, "area": area, "fraction": fraction,
                                 "sampleId": str(sample_id), "sourceId": source_id,
                                 "timestampMs": int(sample.get("timestampMs", 0))}
                        existing = candidates[kind].get(group)
                        if existing is None or (score, area) > (existing["score"], existing["area"]):
                            candidates[kind][group] = value
    selected = {kind: sorted(values.values(), key=lambda item: (item["score"], item["area"]),
                             reverse=True)[:limit]
                for kind, values in candidates.items()}
    selected_ids = {value["sampleId"] for values in selected.values() for value in values}
    overlays = {}
    with torch.inference_mode():
        for images, targets, sample_ids in loader:
            wanted = [index for index, sample_id in enumerate(sample_ids) if str(sample_id) in selected_ids]
            if not wanted: continue
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=bf16):
                probabilities = model(images.to(device))["out"].float().sigmoid().cpu()
            for index in wanted:
                sample_id = str(sample_ids[index]); prediction = probabilities[index, 0].numpy() >= threshold
                truth = targets[index, 0].numpy() >= .5
                pixels = images[index].numpy().transpose(1, 2, 0)
                pixels = np.clip((pixels * np.asarray(STD) + np.asarray(MEAN)) * 255,
                                 0, 255).astype(np.uint8)
                overlay = pixels.copy()
                for mask, colour in ((prediction & truth, (0, 220, 80)),
                                     (prediction & ~truth, (255, 45, 45)),
                                     (~prediction & truth, (25, 135, 255))):
                    overlay[mask] = (.4 * overlay[mask] + .6 * np.asarray(colour)).astype(np.uint8)
                overlays[sample_id] = overlay
    summary = {}
    for kind, values in selected.items():
        summary[kind] = []
        for rank, value in enumerate(values, 1):
            sample_id, area = value["sampleId"], value["area"]
            filename = f"{kind}-{rank:02d}-{sample_id}-{area}px.png"
            Image.fromarray(overlays[sample_id]).save(destination / filename, compress_level=6)
            summary[kind].append({"sampleId": sample_id, "errorPixelsAtInput": area,
                                  "errorPixelsAt512": area,
                                  "errorFraction": value["fraction"], "sourceId": value["sourceId"],
                                  "timestampMs": value["timestampMs"], "image": filename})
    atomic_json(destination / "review.json", summary)
    return summary


def positive_weight(samples, sampler_weights=None):
    # Statistics are measured after inference-style letterboxing so the cap is
    # calibrated to the pixel distribution the model actually sees.
    if sampler_weights is None:
        sampler_weights = [1 / max(1, len(samples))] * len(samples)
    total_weight = sum(sampler_weights)
    positive_fraction = sum(weight * float(sample.get("_maskFraction", 0.))
                            for weight, sample in zip(sampler_weights, samples)) / \
        max(1e-8, total_weight)
    return min(10., max(1., (1 - positive_fraction) / max(1e-8, positive_fraction)))


def write_dataset_snapshot(root, output, manifest, available, args):
    """Pin every input artifact used by this run without copying the dataset."""
    artifact_names = ("framePath", "propMaskPath", "rvmPersonMaskPath", "desiredForegroundPath")
    snapshot_samples = {}
    total = sum(len(samples) for samples in available.values())
    completed = 0
    for split, samples in available.items():
        entries = []
        for sample in samples:
            artifacts = {}
            for name in artifact_names:
                relative = sample.get(name)
                if not relative: continue
                path = root / relative
                if not path.is_file(): continue
                stat = path.stat()
                artifacts[name] = {"path": str(relative).replace("\\", "/"),
                                   "size": stat.st_size, "sha256": digest(path)}
            entries.append({
                "id": sample["id"], "decision": sample["decision"],
                "sourceId": sample.get("sourceId"), "timestampMs": sample.get("timestampMs"),
                "burstId": sample.get("burstId"),
                "feedbackPriority": bool(sample.get("feedbackPriority")),
                "recordPath": sample.get("_recordPath"),
                "recordSha256": sample.get("_recordSha256"),
                "maskFractionAtInput": sample.get("_maskFraction", 0.),
                "artifacts": artifacts})
            completed += 1
            if completed % 250 == 0:
                emit("snapshot", f"Fingerprinting training data {completed:,}/{total:,}",
                     completed=completed, total=total)
        snapshot_samples[split] = entries
    snapshot = {
        "schemaVersion": 1, "trainingRevision": 3,
        "createdUtc": time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
        "datasetId": manifest["datasetId"], "inputSize": args.input_size,
        "minimumResolution": args.minimum_resolution, "seed": args.seed,
        "splits": snapshot_samples}
    path = output / "dataset-snapshot.json"
    if path.is_file():
        previous = json.loads(path.read_text(encoding="utf-8"))
        comparable_keys = ("schemaVersion", "trainingRevision", "datasetId", "inputSize",
                           "minimumResolution", "seed", "splits")
        if any(previous.get(key) != snapshot.get(key) for key in comparable_keys):
            raise RuntimeError("The dataset changed since this run started; start a new training run "
                               "instead of resuming this checkpoint")
        emit("snapshot", f"Verified {total:,} samples against the saved run snapshot",
             snapshot=str(path))
        return path
    atomic_json(path, snapshot)
    emit("snapshot", f"Pinned {total:,} eligible samples for this run", snapshot=str(path))
    return path


def freeze_batch_norm(torch, model):
    # DeepLab's final ResNet feature map is 1x1. A singleton final batch cannot
    # calculate BatchNorm statistics, so retain the pretrained running values
    # while the convolutional and classifier weights continue fine-tuning.
    for module in model.modules():
        if isinstance(module, torch.nn.modules.batchnorm._BatchNorm):
            module.eval()


def train_candidate(torch, DataLoader, args, root, output, device, batch,
                    accumulation, train_samples, validation_samples, negative_ratio,
                    selection_name, epoch_size, epoch_negative_count):
    candidate = output / "candidates" / f"negative-{selection_name}"
    candidate.mkdir(parents=True, exist_ok=True)
    sampler_weights = source_balanced_weights(train_samples, negative_ratio)
    sampler_generator = torch.Generator().manual_seed(args.seed)
    sampler = torch.utils.data.WeightedRandomSampler(
        sampler_weights, epoch_size, replacement=True, generator=sampler_generator)
    loaders = {
        "train": DataLoader(PropDataset(root, train_samples, True, args.input_size), batch_size=batch,
                            sampler=sampler, num_workers=min(4, os.cpu_count() or 1),
                            pin_memory=device.type == "cuda"),
        "validation": DataLoader(PropDataset(root, validation_samples, False, args.input_size), batch_size=batch,
                                 shuffle=False, num_workers=1)}
    random.seed(args.seed); torch.manual_seed(args.seed)
    model = build_model(torch, pretrained=True).to(device)
    pos_weight = torch.tensor([positive_weight(train_samples, sampler_weights)], device=device)
    best_score, stale, start_epoch = -1., 0, 0
    last_path, best_path = candidate / "last.pth", candidate / "best.pth"
    optimizer = torch.optim.AdamW([
        {"params": model.backbone.parameters(), "lr": 1e-5},
        {"params": model.classifier.parameters(), "lr": 1e-4},
        {"params": model.aux_classifier.parameters(), "lr": 1e-4}], weight_decay=1e-4)
    scaler = torch.amp.GradScaler("cuda", enabled=device.type == "cuda")
    resume_state = None
    if args.resume and last_path.is_file():
        state = torch.load(last_path, map_location=device, weights_only=False)
        resume_state = state
        model.load_state_dict(state["model"]); optimizer.load_state_dict(state["optimizer"])
        scaler.load_state_dict(state["scaler"]); start_epoch = state["epoch"] + 1
        best_score = state.get("bestScore", state.get("bestDice", -1.)); stale = state["stale"]
        if "samplerGenerator" in state:
            sampler_generator.set_state(state["samplerGenerator"])
        if "pythonRandomState" in state:
            random.setstate(state["pythonRandomState"])
        if "torchRandomState" in state:
            torch.set_rng_state(state["torchRandomState"])
        if device.type == "cuda" and "cudaRandomState" in state:
            torch.cuda.set_rng_state_all(state["cudaRandomState"])
        emit("resume", f"Resumed {negative_ratio:.0%} negatives at epoch {start_epoch + 1}",
             negativeRatio=negative_ratio)
    ema = torch.optim.swa_utils.AveragedModel(model,
        multi_avg_fn=torch.optim.swa_utils.get_ema_multi_avg_fn(.99))
    if resume_state and "ema" in resume_state: ema.load_state_dict(resume_state["ema"])
    total_epochs = args.warmup_epochs + args.epochs
    checkpoint_thresholds = tuple(round(value / 100, 2) for value in range(15, 86, 10))
    for epoch in range(start_epoch, total_epochs):
        frozen = epoch < args.warmup_epochs
        for parameter in model.backbone.parameters(): parameter.requires_grad = not frozen
        if frozen:
            for group in optimizer.param_groups: group["lr"] = 0 if group is optimizer.param_groups[0] else 1e-3
        else:
            progress = (epoch - args.warmup_epochs) / max(1, args.epochs - 1)
            multiplier = .05 + .95 * .5 * (1 + math.cos(math.pi * progress))
            optimizer.param_groups[0]["lr"] = 1e-5 * multiplier
            optimizer.param_groups[1]["lr"] = optimizer.param_groups[2]["lr"] = 1e-4 * multiplier
        model.train(); freeze_batch_norm(torch, model)
        optimizer.zero_grad(set_to_none=True); running = 0.
        for step, (images, target, _) in enumerate(loaders["train"]):
            images, target = images.to(device, non_blocking=True), target.to(device, non_blocking=True)
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=device.type == "cuda"):
                loss = segmentation_loss(torch, model(images), target, pos_weight) / accumulation
            scaler.scale(loss).backward(); running += float(loss.item()) * accumulation
            if (step + 1) % accumulation == 0 or step + 1 == len(loaders["train"]):
                scaler.step(optimizer); scaler.update(); optimizer.zero_grad(set_to_none=True)
                ema.update_parameters(model)
        validation_all = evaluate(torch, ema.module, loaders["validation"], device,
                                  checkpoint_thresholds, root, validation_samples)
        validation_threshold = max(checkpoint_thresholds,
            key=lambda value: validation_all[value]["selectionScore"])
        validation = validation_all[validation_threshold]
        improved = validation["selectionScore"] > best_score
        if improved:
            best_score, stale = validation["selectionScore"], 0
            torch.save(ema.module.state_dict(), best_path)
        else:
            stale += 1
        torch.save({"model": model.state_dict(), "optimizer": optimizer.state_dict(),
                    "ema": ema.state_dict(), "scaler": scaler.state_dict(), "epoch": epoch,
                    "bestScore": best_score, "bestDice": validation["dice"],
                    "stale": stale, "samplerGenerator": sampler_generator.get_state(),
                    "pythonRandomState": random.getstate(),
                    "torchRandomState": torch.get_rng_state(),
                    "cudaRandomState": torch.cuda.get_rng_state_all()
                        if device.type == "cuda" else None}, last_path)
        emit("epoch", "backbone frozen" if frozen else "full fine-tune",
             epoch=epoch + 1, trainingLoss=running / max(1, len(loaders["train"])),
             validationDice=validation["dice"], validationPrecision=validation["precision"],
             validationRecall=validation["recall"],
             validationMacroDice=validation["macroDice"],
             validationPerVideoDice=validation["perVideoDice"],
             validationNegativeFrameFalsePositiveRate=
                validation["negativeFrameFalsePositiveRate"],
             validationSmallObjectRecall=validation["smallObjectRecall"],
             validationSelectionScore=validation["selectionScore"],
             validationRvmUnionDice=validation.get("rvmUnion", {}).get(
                "finalForeground", {}).get("dice"),
             validationThreshold=validation_threshold, best=improved,
             negativeRatio=negative_ratio)
        if not frozen and stale >= args.patience:
            emit("early-stopping", f"No validation improvement for {stale} epochs")
            break
    if not best_path.is_file():
        raise RuntimeError(f"No checkpoint was produced for {negative_ratio:.0%} negatives")
    model.load_state_dict(torch.load(best_path, map_location=device, weights_only=True))
    thresholds = tuple(round(value / 100, 2) for value in range(10, 91, 5))
    validation_all = evaluate(torch, model, loaders["validation"], device, thresholds,
                              root, validation_samples)
    threshold = max(thresholds, key=lambda value: validation_all[value]["selectionScore"])
    result = {"targetNegativeRatio": negative_ratio,
              "negativeSelection": selection_name,
              "actualNegativeRatio": epoch_negative_count / max(1, epoch_size),
              "negativeAvailable": sum(sample.get("decision") == "negative"
                                       for sample in train_samples),
              "hardExampleCount": sum(bool(sample.get("feedbackPriority") or sample.get("burstId"))
                                      for sample in train_samples),
              "trainCount": len(train_samples), "epochSize": epoch_size,
              "positiveWeight": float(pos_weight.item()), "threshold": threshold,
              "validation": validation_all[threshold]}
    emit("candidate-complete", f"{negative_ratio:.0%} negatives: validation Dice "
         f"{result['validation']['dice']:.3f}", **result)
    return result, best_path


def train(args):
    import torch
    from torch.utils.data import DataLoader
    root, output = args.dataset.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    manifest = load_manifest(root)
    available = {name: resolution_samples(accepted_samples(manifest, name), args.minimum_resolution)
                 for name in ("train", "validation", "test")}
    if any(not available[name] for name in available):
        raise RuntimeError("Train, validation, and test splits must each contain accepted frames "
                           "at the selected minimum resolution")
    for samples in available.values():
        annotate_mask_statistics(root, samples, args.input_size)
    snapshot_path = write_dataset_snapshot(root, output, manifest, available, args)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    if device.type == "cuda":
        memory = torch.cuda.get_device_properties(device).total_memory
        base_batch = 4 if memory >= 20 * 1024 ** 3 else 2 if memory >= 12 * 1024 ** 3 else 1
        batch = max(1, math.floor(base_batch * (512 / args.input_size) ** 2))
    else:
        batch = 1
    accumulation = max(1, math.ceil(8 / batch))
    emit("setup", f"Loading DeepLabV3-ResNet50 on {device}",
         batchSize=batch, gradientAccumulation=accumulation,
         negativeSelection=args.negative_selection, minimumResolution=args.minimum_resolution,
         inputSize=args.input_size)
    if args.negative_selection == "compare":
        requested = [(f"{round(ratio * 100):02d}", ratio) for ratio in (.20, .25, .30, .35)]
    elif args.negative_selection == "all":
        actual = sum(sample.get("decision") == "negative" for sample in available["train"]) / \
                 max(1, len(available["train"]))
        requested = [("all", actual)]
    else:
        requested = [(args.negative_selection.zfill(2), int(args.negative_selection) / 100)]
    candidates = []
    for selection_name, negative_ratio in requested:
        if selection_name == "all":
            train_samples = list(available["train"])
            negative_available = negative_selected = sum(
                sample.get("decision") == "negative" for sample in train_samples)
            epoch_size = len(train_samples)
            description = f"ALL negatives ({negative_ratio:.1%} of training samples)"
        else:
            train_samples, epoch_size, negative_selected = sampling_plan(
                available["train"], negative_ratio)
            negative_available = sum(sample.get("decision") == "negative"
                                     for sample in train_samples)
            description = f"{negative_ratio:.0%} negatives"
        emit("candidate", f"Training candidate with {description}",
             negativeRatio=negative_ratio, negativeAvailable=negative_available,
             negativeSelected=negative_selected, trainCount=len(train_samples),
             epochSize=epoch_size,
             negativeSelection=selection_name)
        result, checkpoint_path = train_candidate(
            torch, DataLoader, args, root, output, device, batch, accumulation,
            train_samples, available["validation"], negative_ratio, selection_name,
            epoch_size, negative_selected)
        result["checkpoint"] = str(checkpoint_path)
        candidates.append(result)
        if device.type == "cuda": torch.cuda.empty_cache()
    winner = max(candidates, key=lambda value: value["validation"]["selectionScore"])
    best_path = Path(winner.pop("checkpoint"))
    for candidate in candidates: candidate.pop("checkpoint", None)
    threshold = winner["threshold"]
    model = build_model(torch, pretrained=False).to(device)
    model.load_state_dict(torch.load(best_path, map_location=device, weights_only=True))
    test_loader = DataLoader(PropDataset(root, available["test"], False, args.input_size), batch_size=batch,
                             shuffle=False, num_workers=1)
    test_metrics = evaluate(torch, model, test_loader, device, (threshold,),
                            root, available["test"])[threshold]
    validation_loader = DataLoader(
        PropDataset(root, available["validation"], False, args.input_size), batch_size=batch,
        shuffle=False, num_workers=1)
    package = output / "package"; package.mkdir(exist_ok=True)
    checkpoint = package / "model.pth"; shutil.copy2(best_path, checkpoint)
    shutil.copy2(snapshot_path, package / "dataset-snapshot.json")
    checkpoint_hash = digest(checkpoint)
    model_id = f"prop-r50-{time.strftime('%Y%m%d-%H%M%S', time.gmtime())}-{checkpoint_hash[:8]}"
    review = save_error_review(torch, model, validation_loader, device, threshold,
                               package / "review", samples=available["validation"])
    metrics = {"validation": winner["validation"], "test": test_metrics,
               "threshold": threshold, "selectedNegativeRatio": winner["targetNegativeRatio"],
               "selectedNegativeMode": winner["negativeSelection"],
               "minimumResolution": args.minimum_resolution,
               "inputSize": args.input_size,
               "trainingRevision": 3,
               "testSealed": True, "reviewSplit": "validation",
               "sampling": {"sourceBalanced": True, "allNegativesEligible": True,
                            "smallMaskUpweighting": True},
               "geometryAugmentation": {"letterbox": .50, "foregroundCrop": .30,
                                        "randomContextCrop": .20},
               "loss": {"positiveWeightCap": 10, "tverskyAlpha": .5,
                        "tverskyBeta": .5, "boundaryWeight": .2},
               "balanceCandidates": candidates,
               "counts": {key: len(value) for key, value in available.items()},
               "review": {key: len(value) for key, value in review.items()}}
    atomic_json(package / "metrics.json", metrics)
    files = {str(path.relative_to(package)).replace("\\", "/"): digest(path)
             for path in package.rglob("*") if path.is_file() and path.name != "manifest.json"}
    atomic_json(package / "manifest.json", {
        "schemaVersion": 1, "modelId": model_id, "architecture": ARCHITECTURE,
        "trainingRevision": 3,
        "category": "foreground_prop", "inputSize": args.input_size,
        "mean": MEAN, "std": STD, "confidenceThreshold": threshold,
        "proximityRadiusAt512": 24, "checkpointSha256": checkpoint_hash,
        "preprocessing": {"resize": "aspect-preserving-letterbox", "inputSize": args.input_size,
                          "mean": MEAN, "std": STD},
        "postprocessing": {"contract": "rvm-proximity-union-v1", "confidenceThreshold": threshold,
                           "proximityRadiusAt512": 24, "componentConnectivity": 8},
        "datasetId": manifest["datasetId"], "runId": output.name,
        "minimumTrainingResolution": args.minimum_resolution,
        "negativeSelection": winner["negativeSelection"],
        "createdUtc": time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
        "pythonVersion": sys.version.split()[0], "torchVersion": torch.__version__,
        "torchvisionVersion": __import__("torchvision").__version__,
        "cudaVersion": torch.version.cuda, "files": files})
    emit("review", "Saved diverse validation false-positive and false-negative review images",
         review=str(package / "review"))
    emit("complete", "Training, threshold calibration, and test evaluation complete",
         package=str(package), modelId=model_id, testDice=test_metrics["dice"],
         selectedNegativeRatio=winner["targetNegativeRatio"],
         selectedNegativeMode=winner["negativeSelection"])


def regenerate_review(args):
    import torch
    from torch.utils.data import DataLoader
    root, output = args.dataset.resolve(), args.output.resolve()
    package = output / "package"
    checkpoint, metrics_path = package / "model.pth", package / "metrics.json"
    manifest_path = package / "manifest.json"
    if not checkpoint.is_file() or not metrics_path.is_file() or not manifest_path.is_file():
        raise RuntimeError("The selected run does not contain a completed model package")
    metrics = json.loads(metrics_path.read_text(encoding="utf-8"))
    package_manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    input_size = int(metrics.get("inputSize", package_manifest.get("inputSize", 512)))
    minimum_resolution = int(metrics.get("minimumResolution", 0))
    threshold = float(metrics["threshold"])
    samples = resolution_samples(accepted_samples(load_manifest(root), "validation"), minimum_resolution)
    if not samples:
        raise RuntimeError("No eligible validation samples remain for error review")
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    batch = max(1, math.floor((4 if device.type == "cuda" else 1) * (512 / input_size) ** 2))
    emit("review", f"Regenerating error review from the saved model on {device}",
         validationCount=len(samples), inputSize=input_size, threshold=threshold)
    model = build_model(torch, pretrained=False).to(device)
    model.load_state_dict(torch.load(checkpoint, map_location=device, weights_only=True))
    loader = DataLoader(PropDataset(root, samples, False, input_size), batch_size=batch,
                        shuffle=False, num_workers=1)
    review_folder = package / "review"
    if review_folder.exists(): shutil.rmtree(review_folder)
    review = save_error_review(torch, model, loader, device, threshold, review_folder,
                               samples=samples)
    metrics["review"] = {key: len(value) for key, value in review.items()}
    metrics["reviewSplit"] = "validation"
    atomic_json(metrics_path, metrics)
    package_manifest["files"] = {
        str(path.relative_to(package)).replace("\\", "/"): digest(path)
        for path in package.rglob("*") if path.is_file() and path.name != "manifest.json"
    }
    atomic_json(manifest_path, package_manifest)
    emit("complete", "Error review regenerated without retraining", review=str(review_folder),
         falsePositive=len(review["false-positive"]),
         falseNegative=len(review["false-negative"]))


def self_test():
    import torch
    with tempfile.TemporaryDirectory(prefix="iqp-prop-train-test-") as value:
        root = Path(value); model = torch.nn.Conv2d(3, 1, 1)
        atomic_json(root / "dataset.json", {"schemaVersion": 1, "datasetId": "test-dataset"})
        atomic_json(root / "records" / "ab" / "abcdef.json", {
            "schemaVersion": 1, "datasetId": "test-dataset",
            "sample": {"id": "abcdef", "decision": "negative", "split": "train",
                       "framePath": "frame.png", "propMaskPath": "mask.png"}})
        loaded_manifest = load_manifest(root)
        assert len(accepted_samples(loaded_manifest, "train")) == 1
        sized = [{"width": 1920, "height": 1080}, {"width": 1280, "height": 720},
                 {"width": 1080, "height": 1920}]
        assert len(resolution_samples(sized, 1080)) == 2
        samples = ([{"id": f"p{index}", "decision": "positive",
                     "sourceId": f"positive-source-{index % 2}",
                     "_maskFraction": .004 if index == 0 else .03}
                    for index in range(7)] +
                   [{"id": f"n{index}", "decision": "negative",
                     "sourceId": f"negative-source-{index % 4}", "_maskFraction": 0.}
                    for index in range(20)])
        planned, epoch_size, expected_negatives = sampling_plan(samples, .30)
        assert len(planned) == 27 and epoch_size == 10 and expected_negatives == 3
        weights = source_balanced_weights(planned, .30)
        assert all(weight > 0 for weight in weights)
        assert abs(sum(weight for weight, sample in zip(weights, planned)
                       if sample["decision"] == "positive") - .7) < 1e-8
        assert abs(sum(weight for weight, sample in zip(weights, planned)
                       if sample["decision"] == "negative") - .3) < 1e-8
        assert weights[0] > weights[1]
        optimizer = torch.optim.AdamW(model.parameters(), lr=1e-3)
        image = torch.randn(2, 3, 8, 8); target = torch.zeros(2, 1, 8, 8)
        loss = torch.nn.functional.binary_cross_entropy_with_logits(model(image), target)
        loss.backward(); optimizer.step()
        checkpoint = root / "resume.pth"
        torch.save({"model": model.state_dict(), "optimizer": optimizer.state_dict(), "epoch": 0}, checkpoint)
        resumed = torch.nn.Conv2d(3, 1, 1)
        state = torch.load(checkpoint, weights_only=False); resumed.load_state_dict(state["model"])
        assert state["epoch"] == 0 and digest(checkpoint) == hashlib.sha256(checkpoint.read_bytes()).hexdigest()
        normalized = torch.nn.Sequential(torch.nn.Conv2d(3, 3, 1),
                                         torch.nn.BatchNorm2d(3))
        normalized.train(); freeze_batch_norm(torch, normalized)
        assert normalized[0].training and not normalized[1].training
        assert normalized(torch.randn(1, 3, 1, 1)).shape == (1, 3, 1, 1)
        values = {threshold: scores(confusion(torch.tensor([.1, .9]),
                  torch.tensor([0., 1.]), threshold)) for threshold in (.25, .5, .75)}
        assert max(values, key=lambda item: values[item]["dice"]) in values
        class ReviewModel(torch.nn.Module):
            def forward(self, inputs):
                return {"out": inputs[:, :1] * 4}
        images = torch.zeros(2, 3, 8, 8); images[:, 0] = -1
        images[0, 0, :4, :4] = 1
        targets = torch.zeros(2, 1, 8, 8); targets[0, 0, 4:, 4:] = 1
        from PIL import Image
        import numpy as np
        person = np.zeros((8, 8), dtype=np.uint8); person[:4, :4] = 255
        desired = person.copy(); desired[4:, 4:] = 255
        Image.fromarray(person).save(root / "person.png")
        Image.fromarray(desired).save(root / "desired.png")
        Image.fromarray(np.zeros((8, 8, 3), dtype=np.uint8)).save(root / "frame.png")
        Image.fromarray((targets[0, 0].numpy() * 255).astype(np.uint8)).save(root / "mask.png")
        evaluation_samples = [
            {"id": "sample-a", "decision": "positive", "sourceId": "same-source",
             "timestampMs": 1000, "rvmPersonMaskPath": "person.png",
             "desiredForegroundPath": "desired.png"},
            {"id": "sample-b", "decision": "negative", "sourceId": "same-source",
             "timestampMs": 2000, "rvmPersonMaskPath": "person.png",
             "desiredForegroundPath": "person.png"}]
        review_metrics = evaluate(torch, ReviewModel(),
            [(images, targets, ("sample-a", "sample-b"))], torch.device("cpu"), (.5,),
            root, evaluation_samples)[.5]
        assert all(name in review_metrics for name in
                   ("macroDice", "positiveRecall", "smallObjectRecall", "boundaryF1",
                    "perVideoDice", "negativeFrameFalsePositiveRate", "selectionScore"))
        assert review_metrics["rvmUnion"]["coverage"] == 1
        combined_loss = segmentation_loss(torch, {"out": images[:, :1] * 4}, targets,
                                          torch.tensor([2.]))
        assert torch.isfinite(combined_loss)
        review = save_error_review(torch, ReviewModel(),
            [(images, targets, ("sample-a", "sample-b"))], torch.device("cpu"), .5,
            root / "review", limit=2, samples=evaluation_samples)
        assert review["false-positive"] and review["false-negative"]
        assert len(review["false-positive"]) == len(review["false-negative"]) == 1
        assert "errorPixelsAtInput" in review["false-positive"][0]
        assert (root / "review" / "review.json").is_file()
        snapshot_sample = {"id": "sample-a", "decision": "positive", "sourceId": "source",
                           "timestampMs": 1000, "framePath": "frame.png",
                           "propMaskPath": "mask.png", "_maskFraction": .25}
        snapshot_path = write_dataset_snapshot(root, root / "run",
            {"datasetId": "test-dataset"}, {"train": [snapshot_sample],
            "validation": [], "test": []}, argparse.Namespace(input_size=8,
            minimum_resolution=0, seed=1729))
        snapshot = json.loads(snapshot_path.read_text(encoding="utf-8"))
        assert snapshot["trainingRevision"] == 3
        assert snapshot["splits"]["train"][0]["artifacts"]["framePath"]["sha256"] == \
            digest(root / "frame.png")
    emit("self-test", "prop-segmenter training self-test passed", status="ok")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--warmup-epochs", type=int, default=5)
    parser.add_argument("--epochs", type=int, default=40)
    parser.add_argument("--patience", type=int, default=8)
    parser.add_argument("--seed", type=int, default=1729)
    parser.add_argument("--minimum-resolution", type=int, default=0)
    parser.add_argument("--input-size", type=int, choices=(512, 768, 1024), default=512)
    parser.add_argument("--negative-selection", choices=("compare", "20", "25", "30", "35", "all"),
                        default="compare")
    parser.add_argument("--regenerate-review", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test(); return
    if args.dataset is None or args.output is None:
        parser.error("--dataset and --output are required")
    if args.regenerate_review:
        regenerate_review(args); return
    train(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", str(error)); raise
