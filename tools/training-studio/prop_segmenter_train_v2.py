#!/usr/bin/env python3
"""Train, screen, evaluate, and package RVM-conditioned prop recovery v2."""
import argparse
import hashlib
import json
import math
import os
import random
import shutil
import sys
import tempfile
import time
from pathlib import Path

TRAINING_REVISION = 6
SCREEN_EPOCHS = 12
MAX_EPOCHS = 40
WARMUP_EPOCHS = 3
EARLY_STOPPING_PATIENCE = 8
NEGATIVE_FRAME_CEILING = .05
NEGATIVE_P95_AREA_CEILING = .001
SCREEN_RECALL_GAIN = .05
PROMOTION_RECALL_GAIN = .08
PROMOTION_SMALL_RECALL_GAIN = .10

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "custom-shows"))
from prop_segmenter import digest
from prop_segmenter_v2 import (ARCHITECTURE, DISTANCE_CLIP_AT_INPUT, INPUT_SIZE,
    MAX_TILES, MEAN, MIN_CONTEXT_AT_INPUT, ROI_EXPANSION, STD, association_band, build_model,
    conditioned_array, expanded_roi, filter_prediction, predict)


def emit(stage, message="", **values):
    print(json.dumps({"stage": stage, "message": message, **values},
                     separators=(",", ":")), flush=True)


def atomic_json(path, value):
    path = Path(path); path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp-" + os.urandom(6).hex())
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def atomic_torch_save(torch, value, path):
    path = Path(path); path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp-" + os.urandom(6).hex())
    try:
        torch.save(value, temporary); os.replace(temporary, path)
    finally:
        try: temporary.unlink(missing_ok=True)
        except OSError: pass


def load_manifest(root):
    value = json.loads((root / "dataset.json").read_text(encoding="utf-8-sig"))
    if value.get("schemaVersion") != 1: raise RuntimeError("Unsupported training dataset schema")
    samples = []
    for path in sorted((root / "records").glob("*/*.json")):
        record = json.loads(path.read_text(encoding="utf-8-sig"))
        if record.get("schemaVersion") != 1 or record.get("datasetId") != value.get("datasetId"):
            raise RuntimeError(f"Training sample record does not belong to this dataset: {path}")
        sample = record["sample"]
        sample["_recordPath"] = str(path.relative_to(root)).replace("\\", "/")
        sample["_recordSha256"] = digest(path)
        samples.append(sample)
    value["samples"] = samples
    return value


def split_samples(manifest, split, sealed=False):
    sources = {source["id"]: source for source in manifest.get("sources", [])}
    result = []
    for sample in manifest.get("samples", []):
        source = sources.get(sample.get("sourceId"), {})
        is_sealed = bool(source.get("sealedHoldout"))
        if is_sealed != sealed or (not sealed and sample.get("split") != split): continue
        if sample.get("decision") not in ("positive", "negative"): continue
        if not sample.get("framePath") or not sample.get("propMaskPath"): continue
        result.append(sample)
    return result


def eligible_samples(samples, minimum_resolution):
    return [sample for sample in samples if minimum_resolution <= 0 or
            min(int(sample.get("width", 0)), int(sample.get("height", 0))) >= minimum_resolution]


def sample_paths(root, sample):
    frame = root / sample["framePath"]
    prop = root / sample["propMaskPath"]
    alpha_value = sample.get("rvmAlphaPath") or sample.get("rvmPersonMaskPath")
    if not alpha_value: raise RuntimeError(f"Sample {sample['id']} has no RVM artifact")
    return frame, prop, root / alpha_value


def load_arrays(root, sample):
    import numpy as np
    from PIL import Image
    frame, prop, alpha = sample_paths(root, sample)
    rgb = np.asarray(Image.open(frame).convert("RGB"), dtype=np.uint8)
    target = np.asarray(Image.open(prop).convert("L"), dtype=np.uint8) >= 128
    raw = np.asarray(Image.open(alpha), dtype=np.float32)
    if raw.ndim == 3: raw = raw[..., 0]
    divisor = 65535. if raw.max(initial=0) > 255 else 255.
    raw = np.clip(raw / divisor, 0, 1)
    if raw.shape != target.shape:
        raw = np.asarray(Image.fromarray(raw, "F").resize(
            (target.shape[1], target.shape[0]), Image.Resampling.BILINEAR), dtype=np.float32)
    return rgb, target, raw


def crop_square(rgb, target, alpha, center, side, output_size, training=False):
    import numpy as np
    from PIL import Image
    height, width = target.shape
    side = max(32, min(int(side), max(height, width)))
    cx, cy = center
    requested_left = round(cx - side / 2); requested_top = round(cy - side / 2)
    left, top = max(0, requested_left), max(0, requested_top)
    right, bottom = min(width, requested_left + side), min(height, requested_top + side)
    destination_left, destination_top = left - requested_left, top - requested_top
    rgb_square = np.empty((side, side, 3), np.uint8)
    rgb_square[:] = tuple(round(value * 255) for value in MEAN)
    target_square = np.zeros((side, side), bool)
    alpha_square = np.zeros((side, side), np.float32)
    if right > left and bottom > top:
        destination_right = destination_left + right - left
        destination_bottom = destination_top + bottom - top
        rgb_square[destination_top:destination_bottom, destination_left:destination_right] = \
            rgb[top:bottom, left:right]
        target_square[destination_top:destination_bottom, destination_left:destination_right] = \
            target[top:bottom, left:right]
        alpha_square[destination_top:destination_bottom, destination_left:destination_right] = \
            alpha[top:bottom, left:right]
    image = Image.fromarray(rgb_square, "RGB").resize(
        (output_size, output_size), Image.Resampling.BILINEAR)
    target = np.asarray(Image.fromarray(target_square.astype(np.uint8) * 255, "L").resize(
        (output_size, output_size), Image.Resampling.NEAREST), dtype=np.uint8) >= 128
    alpha = np.asarray(Image.fromarray(alpha_square, "F").resize(
        (output_size, output_size), Image.Resampling.BILINEAR), dtype=np.float32)
    if training:
        image = augment_appearance(image)
        if random.random() < .5:
            image = image.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
            target = np.fliplr(target).copy(); alpha = np.fliplr(alpha).copy()
        alpha = perturb_alpha(alpha)
    return np.asarray(image, dtype=np.uint8), target, alpha


def positive_crop(rgb, target, alpha, size):
    import numpy as np
    ys, xs = np.where(target)
    if not len(xs): return context_crop(rgb, target, alpha, size, False)
    extent = max(int(xs.max() - xs.min() + 1), int(ys.max() - ys.min() + 1))
    desired = random.uniform(48, 192)
    side = max(192, round(extent * size / desired))
    center = (float(xs.mean()) + random.uniform(-.1, .1) * side,
              float(ys.mean()) + random.uniform(-.1, .1) * side)
    return crop_square(rgb, target, alpha, center, side, size, True)


def context_crop(rgb, target, alpha, size, training=True, prefer_empty=False):
    import numpy as np
    foreground = alpha >= random.uniform(.35, .55)
    ys, xs = np.where(foreground)
    attempts = 20 if prefer_empty and target.any() else 1
    best = None
    for _ in range(attempts):
        if len(xs):
            point = random.randrange(len(xs)); center = (float(xs[point]), float(ys[point]))
        else: center = (target.shape[1] / 2, target.shape[0] / 2)
        side = random.uniform(size * .75, size * 1.5) if training else max(target.shape)
        left, top = round(center[0] - side / 2), round(center[1] - side / 2)
        right, bottom = min(target.shape[1], round(left + side)), \
            min(target.shape[0], round(top + side))
        count = int(target[max(0, top):bottom, max(0, left):right].sum())
        if best is None or count < best[0]: best = (count, center, side)
        if count == 0: break
    _, center, side = best
    return crop_square(rgb, target, alpha, center, side, size, training)


def augment_appearance(image):
    from PIL import ImageEnhance, ImageFilter
    if random.random() < .8:
        image = ImageEnhance.Brightness(image).enhance(random.uniform(.85, 1.15))
        image = ImageEnhance.Contrast(image).enhance(random.uniform(.8, 1.2))
        image = ImageEnhance.Color(image).enhance(random.uniform(.75, 1.25))
    if random.random() < .2: image = image.filter(ImageFilter.GaussianBlur(random.uniform(.2, 1.2)))
    if random.random() < .2:
        import io
        buffer = io.BytesIO(); image.save(buffer, "JPEG", quality=random.randint(65, 94))
        buffer.seek(0); from PIL import Image; image = Image.open(buffer).convert("RGB")
    return image


def perturb_alpha(alpha):
    import cv2
    import numpy as np
    value = np.asarray(alpha, dtype=np.float32)
    if random.random() < .75:
        binary = value >= random.uniform(.30, .60)
        radius = random.randint(-8, 8)
        if radius:
            kernel = np.ones((abs(radius) * 2 + 1,) * 2, np.uint8)
            binary = (cv2.dilate if radius > 0 else cv2.erode)(binary.astype(np.uint8), kernel) != 0
        value = np.clip(value * .6 + binary.astype(np.float32) * .4, 0, 1)
    return value


def evaluation_crop(rgb, target, alpha, size):
    import numpy as np
    from PIL import Image
    left, top, right, bottom = expanded_roi(alpha, .4, ROI_EXPANSION, MIN_CONTEXT_AT_INPUT)
    rgb, target, alpha = rgb[top:bottom, left:right], target[top:bottom, left:right], alpha[top:bottom, left:right]
    scale = min(size / max(1, rgb.shape[1]), size / max(1, rgb.shape[0]))
    resized = (max(1, round(rgb.shape[1] * scale)), max(1, round(rgb.shape[0] * scale)))
    canvas = np.empty((size, size, 3), np.uint8); canvas[:] = tuple(round(x * 255) for x in MEAN)
    prop_canvas = np.zeros((size, size), bool); alpha_canvas = np.zeros((size, size), np.float32)
    image = np.asarray(Image.fromarray(rgb, "RGB").resize(resized, Image.Resampling.BILINEAR))
    prop = np.asarray(Image.fromarray(target.astype(np.uint8) * 255, "L").resize(
        resized, Image.Resampling.NEAREST)) >= 128
    mask = np.asarray(Image.fromarray(alpha.astype(np.float32), "F").resize(
        resized, Image.Resampling.BILINEAR), dtype=np.float32)
    canvas[:resized[1], :resized[0]] = image
    prop_canvas[:resized[1], :resized[0]] = prop
    alpha_canvas[:resized[1], :resized[0]] = mask
    return canvas, prop_canvas, alpha_canvas


def training_plan(samples):
    positives = [sample for sample in samples if sample.get("decision") == "positive"]
    negatives = [sample for sample in samples if sample.get("decision") == "negative"]
    if not positives or not negatives: raise RuntimeError("v2 training needs positive and negative frames")
    random.shuffle(positives); random.shuffle(negatives)
    plan = [(sample, "positive") for sample in positives]
    plan += [(random.choice(positives), "near-negative")
             for _ in range(max(1, len(positives) // 2))]
    plan += [(random.choice(negatives), "empty-negative")
             for _ in range(max(1, len(positives) // 2))]
    random.shuffle(plan)
    return plan


def annotate_mask_fractions(root, samples):
    import numpy as np
    from PIL import Image
    for sample in samples:
        if sample.get("decision") != "positive": sample["_maskFraction"] = 0.; continue
        mask = np.asarray(Image.open(root / sample["propMaskPath"]).convert("L"), dtype=np.uint8)
        sample["_maskFraction"] = float((mask >= 128).mean())


def crop_sampler(torch, plan):
    from collections import Counter, defaultdict
    mode_target = {"positive": .5, "near-negative": .25, "empty-negative": .25}
    source_counts = Counter((mode, sample.get("sourceId")) for sample, mode in plan)
    raw_weights = []
    for sample, mode in plan:
        fraction = float(sample.get("_maskFraction", 0))
        size = 2. if mode == "positive" and fraction <= .005 else \
            1.35 if mode == "positive" and fraction <= .02 else 1.
        raw_weights.append(size / max(1, source_counts[(mode, sample.get("sourceId"))]))
    mode_sums = defaultdict(float)
    for raw, (_, mode) in zip(raw_weights, plan): mode_sums[mode] += raw
    weights = [mode_target[mode] * raw / max(1e-9, mode_sums[mode])
               for raw, (_, mode) in zip(raw_weights, plan)]
    return torch.utils.data.WeightedRandomSampler(weights, len(plan), replacement=True)


class CropDataset:
    def __init__(self, root, entries, size=INPUT_SIZE):
        self.root, self.entries, self.size = Path(root), entries, size

    def __len__(self): return len(self.entries)

    def __getitem__(self, index):
        import torch
        sample, mode = self.entries[index]
        rgb, target, alpha = load_arrays(self.root, sample)
        if mode == "positive": rgb, target, alpha = positive_crop(rgb, target, alpha, self.size)
        else: rgb, target, alpha = context_crop(rgb, target, alpha, self.size, True,
                                                mode == "near-negative")
        inputs = conditioned_array(rgb, alpha)
        exterior = target & ~(alpha >= .4)
        return (torch.from_numpy(inputs), torch.from_numpy(target[None].astype("float32")),
                torch.from_numpy(exterior[None].astype("float32")),
                torch.tensor(float(target.any())), sample["id"])


class EvaluationDataset:
    def __init__(self, root, samples, size=INPUT_SIZE):
        self.root, self.samples, self.size = Path(root), samples, size
    def __len__(self): return len(self.samples)
    def __getitem__(self, index):
        import torch
        sample = self.samples[index]
        rgb, target, alpha = evaluation_crop(*load_arrays(self.root, sample), self.size)
        return (torch.from_numpy(conditioned_array(rgb, alpha)),
                torch.from_numpy(target[None].astype("float32")),
                torch.from_numpy(alpha[None].astype("float32")), sample["id"])


def edge_weight(torch, target):
    dilated = torch.nn.functional.max_pool2d(target, 7, stride=1, padding=3)
    eroded = -torch.nn.functional.max_pool2d(-target, 7, stride=1, padding=3)
    return 1 + (dilated - eroded).clamp(0, 1)


def training_loss(torch, output, target, exterior, presence_target):
    logits, presence = output["out"], output["presence"]
    probability = logits.sigmoid()
    focal = torch.nn.functional.binary_cross_entropy_with_logits(logits, target,
        reduction="none", pos_weight=torch.tensor([8.], device=logits.device)[None, :, None, None])
    pt = probability * target + (1 - probability) * (1 - target)
    weights = edge_weight(torch, target) * (1 + exterior * 2)
    focal = (((1 - pt) ** 2) * focal * weights).mean()
    axes = (1, 2, 3)
    tp = (probability * target).sum(axes)
    fp = (probability * (1 - target)).sum(axes)
    fn = ((1 - probability) * target).sum(axes)
    positive = target.sum(axes) > 0
    tversky = torch.where(positive, 1 - (tp + 1) / (tp + .3 * fp + .7 * fn + 1), 0).sum() / \
        positive.sum().clamp_min(1)
    presence_loss = torch.nn.functional.binary_cross_entropy_with_logits(presence, presence_target)
    return focal + .75 * tversky + .2 * presence_loss


def collect_predictions(torch, model, loader, device):
    values = []
    model.eval(); bf16 = device.type == "cuda"
    with torch.inference_mode():
        for inputs, targets, alphas, sample_ids in loader:
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=bf16):
                output = model(inputs.to(device))
            probabilities = output["out"].float().sigmoid().cpu().numpy()[:, 0]
            presences = output["presence"].float().sigmoid().cpu().numpy()
            for index, sample_id in enumerate(sample_ids):
                values.append((str(sample_id), probabilities[index].astype("float16"),
                    targets[index, 0].numpy() >= .5, alphas[index, 0].numpy().astype("float16"),
                    float(presences[index])))
    return values


def binary_frame_statistics(predicted, target, alpha):
    import numpy as np
    person = alpha >= .4; desired = person | target
    exterior_target = target & ~person; exterior_prediction = predicted & ~person
    tp = int((exterior_prediction & exterior_target).sum())
    fp = int((exterior_prediction & ~exterior_target).sum())
    fn = int((~exterior_prediction & exterior_target).sum())
    positive = bool(exterior_target.any()); negative = bool(not target.any())
    coverage = tp / max(1, int(exterior_target.sum()))
    area = int(exterior_prediction.sum()) / exterior_prediction.size if negative else None
    small = bool(positive and target.mean() < .005)
    union = []
    for output in (person, person | predicted):
        union.append((int((output & desired).sum()), int((output & ~desired).sum()),
                      int((~output & desired).sum())))
    return {"tp": tp, "fp": fp, "fn": fn, "positive": positive,
        "recovered": positive and coverage >= .5,
        "small": small,
        "smallRecovered": small and coverage >= .5,
        "negative": negative, "falseArea": area,
        "materialFalse": negative and area > max(16 / exterior_prediction.size, .0001),
        "baselineUnion": union[0], "augmentedUnion": union[1]}


def aggregate_binary_statistics(statistics):
    import numpy as np
    tp = sum(value["tp"] for value in statistics)
    fp = sum(value["fp"] for value in statistics)
    fn = sum(value["fn"] for value in statistics)
    positive_frames = sum(value["positive"] for value in statistics)
    recovered = sum(value["recovered"] for value in statistics)
    small_frames = sum(value["small"] for value in statistics)
    small_recovered = sum(value["smallRecovered"] for value in statistics)
    negative_frames = sum(value["negative"] for value in statistics)
    material_false = sum(value["materialFalse"] for value in statistics)
    false_areas = [value["falseArea"] for value in statistics if value["falseArea"] is not None]
    baseline_union = [sum(value["baselineUnion"][index] for value in statistics) for index in range(3)]
    augmented_union = [sum(value["augmentedUnion"][index] for value in statistics) for index in range(3)]
    precision = tp / max(1, tp + fp); recall = tp / max(1, tp + fn)
    dice = 2 * tp / max(1, 2 * tp + fp + fn)
    f2 = 5 * precision * recall / max(1e-12, 4 * precision + recall)
    p95 = float(np.percentile(false_areas, 95)) if false_areas else 0.
    union_dice = lambda totals: 2 * totals[0] / max(1, 2 * totals[0] + totals[1] + totals[2])
    baseline_union_dice = union_dice(baseline_union)
    augmented_union_dice = union_dice(augmented_union)
    return {"exteriorDice": dice, "exteriorPrecision": precision, "exteriorRecall": recall,
            "exteriorF2": f2, "positiveRecoveryRate": recovered / max(1, positive_frames),
            "smallObjectRecovery": small_recovered / max(1, small_frames),
            "negativeFrameFalsePositiveRate": material_false / max(1, negative_frames),
            "negativeP95AddedArea": p95, "positiveFrames": positive_frames,
            "negativeFrames": negative_frames, "smallPositiveFrames": small_frames,
            "rvmUnionBaselineDice": baseline_union_dice,
            "rvmUnionAugmentedDice": augmented_union_dice,
            "rvmUnionDiceGain": augmented_union_dice - baseline_union_dice}


def binary_metrics(values):
    return aggregate_binary_statistics([binary_frame_statistics(*value) for value in values])


def metrics_for(values, pixel_threshold, presence_threshold):
    predictions = []
    for _, probability, target, alpha, presence in values:
        predicted, _, _ = filter_prediction(probability, alpha, pixel_threshold, presence,
            presence_threshold, 96, INPUT_SIZE)
        predictions.append((predicted, target, alpha))
    return binary_metrics(predictions)


def calibrate(values):
    import numpy as np
    options = []
    bands = [association_band(alpha, 96, INPUT_SIZE)[0]
             for _, _, _, alpha, _ in values]
    for pixel in [value / 100 for value in range(10, 91, 5)]:
        filtered = []
        for (_, probability, target, alpha, presence_score), near in zip(values, bands):
            predicted, _, _ = filter_prediction(probability, alpha, pixel, 1., 0., 96,
                INPUT_SIZE, near)
            filtered.append((binary_frame_statistics(predicted, target, alpha),
                             binary_frame_statistics(np.zeros_like(predicted), target, alpha),
                             presence_score))
        for presence in (.1, .2, .3, .4, .5, .6, .7, .8, .9):
            metrics = aggregate_binary_statistics([active if score >= presence else inactive
                for active, inactive, score in filtered])
            metrics.update(pixelThreshold=pixel, presenceThreshold=presence)
            metrics["constraintsMet"] = metrics["negativeFrameFalsePositiveRate"] <= NEGATIVE_FRAME_CEILING and \
                metrics["negativeP95AddedArea"] <= NEGATIVE_P95_AREA_CEILING
            options.append(metrics)
    feasible = [value for value in options if value["constraintsMet"]]
    selected = max(feasible or options, key=lambda value: (
        value["constraintsMet"], value["exteriorF2"], value["exteriorRecall"],
        value["exteriorPrecision"]))
    return selected, options


def grouped_metrics(values, samples, pixel, presence, field):
    lookup = {sample["id"]: sample for sample in samples}
    groups = {}
    for item in values:
        for name in lookup.get(item[0], {}).get(field, []) or []:
            groups.setdefault(name, []).append(item)
    return {name: metrics_for(items, pixel, presence) for name, items in groups.items()}


def temporal_consistency(values, samples, pixel, presence):
    import numpy as np
    lookup = {sample["id"]: sample for sample in samples}
    groups = {}
    for item in values:
        burst = lookup.get(item[0], {}).get("burstId")
        if burst: groups.setdefault(burst, []).append(item)
    variations = []
    for items in groups.values():
        if len(items) < 2: continue
        areas = []
        for _, probability, _, alpha, presence_score in items:
            prediction, _, _ = filter_prediction(probability, alpha, pixel, presence_score,
                presence, 96, INPUT_SIZE)
            areas.append(float(prediction.mean()))
        mean = float(np.mean(areas))
        variations.append(float(np.std(areas) / max(mean, 1e-6)))
    return {"burstCount": len(variations),
            "medianAreaCoefficientOfVariation": float(np.median(variations)) if variations else None}


def save_error_review(root, samples, values, pixel, presence, destination, limit=50):
    import numpy as np
    from PIL import Image
    destination = Path(destination); destination.mkdir(parents=True, exist_ok=True)
    lookup = {sample["id"]: sample for sample in samples}
    ranked = {"false-positive": [], "false-negative": []}
    rendered = {}
    for sample_id, probability, target, alpha, presence_score in values:
        predicted, _, _ = filter_prediction(probability, alpha, pixel, presence_score,
            presence, 96, INPUT_SIZE)
        exterior = ~(alpha >= .4)
        false_positive = predicted & ~target & exterior
        false_negative = ~predicted & target & exterior
        ranked["false-positive"].append((int(false_positive.sum()), sample_id,
                                         false_positive, false_negative, predicted, target))
        ranked["false-negative"].append((int(false_negative.sum()), sample_id,
                                         false_positive, false_negative, predicted, target))
    records = {"false-positive": [], "false-negative": []}
    for kind, items in ranked.items():
        for error, sample_id, false_positive, false_negative, predicted, target in \
                sorted(items, reverse=True)[:limit]:
            if error <= 0: continue
            key = sample_id
            if key not in rendered:
                sample = lookup[sample_id]
                rgb, _, _ = evaluation_crop(*load_arrays(root, sample), INPUT_SIZE)
                overlay = rgb.astype(np.float32)
                overlay[predicted] = overlay[predicted] * .55 + np.array([20, 220, 70]) * .45
                overlay[false_positive] = np.array([235, 45, 45])
                overlay[false_negative] = np.array([40, 120, 255])
                path = destination / f"{sample_id}.jpg"
                Image.fromarray(np.clip(overlay, 0, 255).astype(np.uint8)).save(path, quality=92)
                rendered[key] = path.name
            sample = lookup[sample_id]
            records[kind].append({"sampleId": sample_id, "errorPixelsAtInput": error,
                "image": rendered[key], "families": sample.get("propFamilies", []),
                "relationships": sample.get("propRelationships", [])})
    atomic_json(destination / "review.json", records)
    return records


def benchmark_inference(torch, model, loader, device, maximum_batches=10):
    import time as clock
    model.eval()
    if device.type == "cuda": torch.cuda.reset_peak_memory_stats(device); torch.cuda.synchronize(device)
    frames = 0; started = clock.perf_counter()
    with torch.inference_mode():
        for index, (inputs, _, _, _) in enumerate(loader):
            if index >= maximum_batches: break
            inputs = inputs.to(device)
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=device.type == "cuda"):
                model(inputs)
            frames += len(inputs)
    if device.type == "cuda": torch.cuda.synchronize(device)
    elapsed = clock.perf_counter() - started
    peak = torch.cuda.max_memory_allocated(device) / 1024 ** 3 if device.type == "cuda" else 0
    return {"averageSecondsPerFrame": elapsed / max(1, frames), "peakVramGiB": peak,
            "measuredFrames": frames}


def runtime_evaluate(torch, model, root, samples, device, pixel, presence,
                     benchmark_frames=20):
    import numpy as np
    import time as clock
    values = []; measured = 0; elapsed = 0.
    if device.type == "cuda": torch.cuda.reset_peak_memory_stats(device)
    model.eval()
    for index, sample in enumerate(samples, 1):
        rgb, target, alpha = load_arrays(root, sample)
        if measured < benchmark_frames and device.type == "cuda": torch.cuda.synchronize(device)
        started = clock.perf_counter()
        probability, presence_score = predict(torch, model, device, rgb, alpha, INPUT_SIZE, .4)
        if measured < benchmark_frames:
            if device.type == "cuda": torch.cuda.synchronize(device)
            elapsed += clock.perf_counter() - started; measured += 1
        predicted, _, _ = filter_prediction(probability, alpha, pixel, presence_score,
            presence, 96, INPUT_SIZE)
        values.append((predicted, target, alpha))
        if index % 100 == 0:
            emit("evaluation", f"Source-resolution v2 evaluation {index:,}/{len(samples):,}")
    peak = torch.cuda.max_memory_allocated(device) / 1024 ** 3 if device.type == "cuda" else 0
    return binary_metrics(values), {
        "averageSecondsPerFrame": elapsed / max(1, measured),
        "peakVramGiB": peak, "measuredFrames": measured}


def mine_hard_negative_tiles(torch, model, root, plan, size, device, limit=125):
    """Promote the highest-scoring near-RVM/empty crops, not whole frames."""
    from torch.utils.data import DataLoader
    entries = [entry for entry in plan if entry[1] != "positive"]
    loader = DataLoader(CropDataset(root, entries, size), batch_size=1, shuffle=False,
                        num_workers=0)
    ranked = []; model.eval()
    with torch.inference_mode():
        for index, (inputs, target, _, _, _) in enumerate(loader):
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16,
                    enabled=device.type == "cuda"):
                output = model(inputs.to(device))
            probability = output["out"].float().sigmoid().cpu()
            presence = float(output["presence"].float().sigmoid().cpu()[0])
            false_probability = probability * (1 - target)
            ranked.append((float(false_probability.max()) * presence, entries[index]))
    return [entry for _, entry in sorted(ranked, key=lambda value: value[0], reverse=True)[:limit]]


def save_hard_negative_set(path, entries):
    atomic_json(path, [{"sampleId": sample["id"], "mode": mode}
                       for sample, mode in entries])


def load_hard_negative_set(path, samples):
    if not path.is_file(): return None
    lookup = {sample["id"]: sample for sample in samples}
    values = json.loads(path.read_text(encoding="utf-8"))
    return [(lookup[value["sampleId"]], value["mode"]) for value in values
            if value.get("sampleId") in lookup and value.get("mode") in
                ("near-negative", "empty-negative")]


def latest_v1_baseline(root):
    for manifest_path in sorted((root / "runs").glob("*/package/manifest.json"),
                                key=lambda path: path.stat().st_mtime, reverse=True):
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            metrics_path = manifest_path.parent / "metrics.json"
            if manifest.get("architecture") != "deeplabv3-resnet50-binary-v1" or not metrics_path.is_file():
                continue
            metrics = json.loads(metrics_path.read_text(encoding="utf-8"))
            validation = metrics.get("validation", {})
            exterior = validation.get("rvmUnion", {}).get("exteriorProp", {})
            return {"modelId": manifest.get("modelId"),
                    "exteriorRecall": float(exterior.get("recall", validation.get("exteriorPropRecall", 0))),
                    "exteriorDice": float(exterior.get("dice", validation.get("exteriorPropDice", 0))),
                    "smallObjectRecovery": float(validation.get("smallObjectRecall", 0))}
        except (OSError, ValueError, TypeError): continue
    return {"modelId": None, "exteriorRecall": 0., "exteriorDice": 0., "smallObjectRecovery": 0.}


def latest_v1_package(root):
    for manifest_path in sorted((root / "runs").glob("*/package/manifest.json"),
                                key=lambda path: path.stat().st_mtime, reverse=True):
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            if manifest.get("architecture") == "deeplabv3-resnet50-binary-v1":
                return manifest_path.parent
        except (OSError, ValueError, TypeError): pass
    return None


def evaluate_v1_on_samples(root, samples, device):
    package = latest_v1_package(root)
    if package is None or not samples: return None
    from prop_segmenter import load_package, predict_mask
    torch, model, model_device, manifest = load_package(package, device)
    values = []
    try:
        for index, sample in enumerate(samples, 1):
            rgb, target, alpha = load_arrays(root, sample)
            _, probability = predict_mask(torch, model, model_device, rgb,
                manifest.get("confidenceThreshold", .5), manifest.get("inputSize", 512))
            near = association_band(alpha, manifest.get("proximityRadiusAt512", 24), 512)[0]
            values.append((probability.astype("float16"), target, alpha, near))
            if index % 100 == 0:
                emit("baseline", f"Evaluating v1 baseline {index:,}/{len(samples):,}")
        options = []
        thresholds = sorted(set([value / 10 for value in range(1, 10)] +
            [float(manifest.get("confidenceThreshold", .5))]))
        for threshold in thresholds:
            predictions = []
            for probability, target, alpha, near in values:
                retained, _, _ = filter_prediction(probability, alpha, threshold, 1., 0.,
                    manifest.get("proximityRadiusAt512", 24), 512, near)
                predictions.append((retained, target, alpha))
            metrics = binary_metrics(predictions); metrics["pixelThreshold"] = threshold
            metrics["constraintsMet"] = \
                metrics["negativeFrameFalsePositiveRate"] <= NEGATIVE_FRAME_CEILING and \
                metrics["negativeP95AddedArea"] <= NEGATIVE_P95_AREA_CEILING
            options.append(metrics)
        feasible = [value for value in options if value["constraintsMet"]]
        metrics = max(feasible or options, key=lambda value: (
            value["constraintsMet"], value["exteriorF2"], value["exteriorRecall"]))
        metrics["modelId"] = manifest.get("modelId")
        return metrics
    finally:
        del model
        if model_device.type == "cuda": torch.cuda.empty_cache()


def dataset_snapshot(root, output, manifest, splits, args):
    files = {}
    samples = sum(splits.values(), [])
    for index, sample in enumerate(samples, 1):
        for key in ("_recordPath", "framePath", "propMaskPath", "rvmAlphaPath", "rvmPersonMaskPath"):
            relative = sample.get(key)
            if relative and relative not in files and (root / relative).is_file(): files[relative] = digest(root / relative)
        if index % 250 == 0: emit("snapshot", f"Fingerprinting v2 data {index:,}/{len(samples):,}")
    configuration = {key: str(value) if isinstance(value, Path) else value
                     for key, value in vars(args).items()}
    configuration.pop("resume", None)
    value = {"schemaVersion": 2, "trainingRevision": TRAINING_REVISION,
             "datasetId": manifest["datasetId"], "createdUtc": time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
             "configuration": configuration, "files": files,
             "samples": {name: [sample["id"] for sample in values] for name, values in splits.items()}}
    path = output / "dataset-snapshot.json"
    if args.resume and path.is_file():
        previous = json.loads(path.read_text(encoding="utf-8"))
        if previous.get("datasetId") != value["datasetId"] or \
                previous.get("files") != value["files"] or \
                previous.get("samples") != value["samples"] or \
                previous.get("configuration") != value["configuration"]:
            raise RuntimeError("The pinned v2 data/configuration snapshot changed; start a new run instead of resuming")
        return path
    atomic_json(path, value); return path


def train(args):
    import torch
    from torch.utils.data import DataLoader
    random.seed(args.seed); torch.manual_seed(args.seed)
    root, output = args.dataset.resolve(), args.output.resolve(); output.mkdir(parents=True, exist_ok=True)
    manifest = load_manifest(root)
    splits = {name: eligible_samples(split_samples(manifest, name), args.minimum_resolution)
              for name in ("train", "validation", "test")}
    holdout = eligible_samples(split_samples(manifest, "test", sealed=True), args.minimum_resolution)
    if not splits["train"] or not splits["validation"] or not splits["test"]:
        raise RuntimeError("Train, validation and regression-test splits need accepted v2 samples")
    missing_alpha = [sample["id"] for sample in sum(splits.values(), []) + holdout
                     if not sample.get("rvmAlphaPath") or not (root / sample["rvmAlphaPath"]).is_file()]
    if missing_alpha:
        raise RuntimeError(f"{len(missing_alpha):,} samples have no source-sized v2 RVM alpha; "
                           "generate missing v2 artifacts in Training Studio first")
    sources = {sample["sourceId"] for sample in holdout}
    holdout_positives = sum(sample.get("decision") == "positive" for sample in holdout)
    holdout_negatives = sum(sample.get("decision") == "negative" for sample in holdout)
    holdout_balance = holdout_positives / max(1, len(holdout))
    holdout_ready = len(holdout) >= 300 and len(sources) >= 60 and .4 <= holdout_balance <= .6
    emit("holdout", f"Sealed v2 holdout contains {len(holdout):,} frames from {len(sources):,} videos",
         frameCount=len(holdout), sourceCount=len(sources), positive=holdout_positives,
         negative=holdout_negatives, ready=holdout_ready)
    snapshot = dataset_snapshot(root, output, manifest, {**splits, "holdout": holdout}, args)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    memory = torch.cuda.get_device_properties(device).total_memory if device.type == "cuda" else 0
    batch = 2 if memory >= 14 * 1024 ** 3 else 1
    accumulation = max(1, math.ceil(8 / batch))
    recorded_baseline = latest_v1_baseline(root)
    baseline = evaluate_v1_on_samples(root, splits["validation"], device) or recorded_baseline
    sealed_baseline = evaluate_v1_on_samples(root, holdout, device) if holdout else None
    emit("baseline", "Recorded paired v1 comparison metrics",
         validation=baseline, sealedHoldout=sealed_baseline)
    emit("setup", f"Loading RVM-conditioned ConvNeXt/FPN on {device}", batchSize=batch,
         gradientAccumulation=accumulation, inputSize=args.input_size, screeningEpochs=SCREEN_EPOCHS)
    model = build_model(torch, pretrained=True).to(device)
    encoder_parameters = list(model.features.parameters())
    head_parameters = [parameter for name, parameter in model.named_parameters() if not name.startswith("features.")]
    optimizer = torch.optim.AdamW([
        {"params": encoder_parameters, "lr": 3e-5}, {"params": head_parameters, "lr": 3e-4}],
        weight_decay=1e-4)
    annotate_mask_fractions(root, splits["train"])
    plan = training_plan(splits["train"])
    train_loader = DataLoader(CropDataset(root, plan, args.input_size), batch_size=batch,
        sampler=crop_sampler(torch, plan), num_workers=1, pin_memory=device.type == "cuda")
    validation_loader = DataLoader(EvaluationDataset(root, splits["validation"], args.input_size),
        batch_size=batch, shuffle=False, num_workers=1)
    start_epoch = 0; best = None; stale = 0
    resume_path = output / "last.pth"
    if args.resume and resume_path.is_file():
        state = torch.load(resume_path, map_location=device, weights_only=False)
        model.load_state_dict(state["model"]); optimizer.load_state_dict(state["optimizer"])
        start_epoch = int(state["epoch"]) + 1; best = state.get("best"); stale = int(state.get("stale", 0))
        emit("resume", f"Resuming v2 at epoch {start_epoch + 1}")
    best_path = output / "best-v2.pth"; hard_negative_count = 0
    hard_negative_path = output / "hard-negative-tiles.json"
    if start_epoch >= SCREEN_EPOCHS:
        hard_entries = load_hard_negative_set(hard_negative_path, splits["train"])
        if hard_entries is None:
            hard_entries = mine_hard_negative_tiles(torch, model, root, plan,
                args.input_size, device)
            save_hard_negative_set(hard_negative_path, hard_entries)
        hard_negative_count = len(hard_entries); plan.extend(hard_entries)
        train_loader = DataLoader(CropDataset(root, plan, args.input_size), batch_size=batch,
            sampler=crop_sampler(torch, plan), num_workers=1, pin_memory=device.type == "cuda")
        emit("hard-negative-mining", f"Restored {hard_negative_count:,} difficult tiles for resume",
             count=hard_negative_count, unit="crop")
    for epoch in range(start_epoch, MAX_EPOCHS):
        frozen = epoch < WARMUP_EPOCHS
        for parameter in model.features.parameters(): parameter.requires_grad = not frozen
        model.train(); total = 0.; optimizer.zero_grad(set_to_none=True)
        for step, (inputs, target, exterior, presence, _) in enumerate(train_loader, 1):
            inputs, target = inputs.to(device, non_blocking=True), target.to(device, non_blocking=True)
            exterior, presence = exterior.to(device, non_blocking=True), presence.to(device, non_blocking=True)
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=device.type == "cuda"):
                loss = training_loss(torch, model(inputs), target, exterior, presence) / accumulation
            loss.backward(); total += float(loss.detach()) * accumulation
            if step % accumulation == 0 or step == len(train_loader):
                torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0); optimizer.step(); optimizer.zero_grad(set_to_none=True)
        values = collect_predictions(torch, model, validation_loader, device)
        selected, sweep = calibrate(values)
        score = selected["exteriorF2"] if selected["constraintsMet"] else -1 + selected["exteriorF2"]
        improved = best is None or score > best["score"] + 1e-5
        if improved:
            best = {"score": score, "epoch": epoch + 1, "metrics": selected}
            atomic_torch_save(torch, model.state_dict(), best_path); stale = 0
        else: stale += 1
        atomic_torch_save(torch, {"model": model.state_dict(), "optimizer": optimizer.state_dict(),
            "epoch": epoch, "best": best, "stale": stale}, resume_path)
        emit("epoch", "backbone frozen" if frozen else "full fine-tune", epoch=epoch + 1,
            validationDice=selected["exteriorDice"], validationPrecision=selected["exteriorPrecision"],
            validationRecall=selected["exteriorRecall"], validationExteriorPropDice=selected["exteriorDice"],
            validationRetainedNegativeFalsePositiveRate=selected["negativeFrameFalsePositiveRate"],
            validationThreshold=selected["pixelThreshold"], validationPresenceThreshold=selected["presenceThreshold"],
            validationExteriorF2=selected["exteriorF2"], negativeP95AddedArea=selected["negativeP95AddedArea"],
            constraintsMet=selected["constraintsMet"], trainingLoss=total / max(1, len(train_loader)))
        if epoch + 1 == SCREEN_EPOCHS:
            gain = best["metrics"]["exteriorRecall"] - baseline["exteriorRecall"]
            passed = best["metrics"]["constraintsMet"] and gain >= SCREEN_RECALL_GAIN
            emit("screening", f"12-epoch screen {'passed' if passed else 'failed'}: exterior recall gain {gain:+.3f}",
                 passed=passed, recallGain=gain, baseline=baseline, validation=best["metrics"])
            if not passed:
                atomic_json(output / "screening-result.json", {"passed": False, "baseline": baseline,
                    "best": best, "reason": "v2 did not gain five recall points at the false-positive ceiling"})
                model.load_state_dict(torch.load(best_path, map_location=device, weights_only=True))
                review_values = collect_predictions(torch, model, validation_loader, device)
                review_path = output / "package" / "review"
                save_error_review(root, splits["validation"], review_values,
                    best["metrics"]["pixelThreshold"], best["metrics"]["presenceThreshold"], review_path)
                emit("review", "Saved v2 screening error review", review=str(review_path))
                return
            hard_entries = mine_hard_negative_tiles(torch, model, root, plan,
                args.input_size, device)
            hard_negative_count = len(hard_entries)
            save_hard_negative_set(hard_negative_path, hard_entries)
            plan.extend(hard_entries)
            train_loader = DataLoader(CropDataset(root, plan, args.input_size), batch_size=batch,
                sampler=crop_sampler(torch, plan), num_workers=1, pin_memory=device.type == "cuda")
            emit("hard-negative-mining",
                f"Promoted {hard_negative_count:,} difficult near-RVM/empty tiles",
                count=hard_negative_count, unit="crop")
        if epoch + 1 >= SCREEN_EPOCHS and stale >= EARLY_STOPPING_PATIENCE:
            emit("early-stopping", f"No constrained exterior-F2 improvement for {stale} epochs"); break
    model.load_state_dict(torch.load(best_path, map_location=device, weights_only=True))
    pixel = best["metrics"]["pixelThreshold"]; presence = best["metrics"]["presenceThreshold"]
    validation_values = collect_predictions(torch, model, validation_loader, device)
    regression, benchmark = runtime_evaluate(torch, model, root, splits["test"], device,
        pixel, presence)
    sealed, sealed_benchmark = runtime_evaluate(torch, model, root, holdout, device,
        pixel, presence) if holdout else (None, None)
    if sealed_benchmark is not None:
        benchmark = {"averageSecondsPerFrame": max(benchmark["averageSecondsPerFrame"],
                     sealed_benchmark["averageSecondsPerFrame"]),
                     "peakVramGiB": max(benchmark["peakVramGiB"],
                     sealed_benchmark["peakVramGiB"]),
                     "measuredFrames": benchmark["measuredFrames"] +
                     sealed_benchmark["measuredFrames"]}
    review_path = output / "package" / "review"
    review = save_error_review(root, splits["validation"], validation_values,
        pixel, presence, review_path)
    emit("review", "Saved v2 exterior false-positive and false-negative review", review=str(review_path))
    promotion_baseline = sealed_baseline or baseline
    promotion = bool(holdout_ready and sealed and
        sealed["exteriorRecall"] >= promotion_baseline["exteriorRecall"] + PROMOTION_RECALL_GAIN and
        sealed["smallObjectRecovery"] >= promotion_baseline["smallObjectRecovery"] + PROMOTION_SMALL_RECALL_GAIN and
        sealed["exteriorDice"] >= promotion_baseline["exteriorDice"] and
        sealed["negativeFrameFalsePositiveRate"] <= NEGATIVE_FRAME_CEILING and
        benchmark["averageSecondsPerFrame"] < 1 and benchmark["peakVramGiB"] < 14)
    metrics = {"trainingRevision": TRAINING_REVISION, "architecture": ARCHITECTURE,
        "validation": best["metrics"], "regressionTest": regression, "sealedHoldout": sealed,
        "baseline": baseline, "sealedHoldoutBaseline": sealed_baseline,
        "promotionCriteriaMet": promotion,
        "inferenceBenchmark": benchmark,
        "byFamily": grouped_metrics(validation_values, splits["validation"], pixel, presence,
                                    "propFamilies"),
        "byRelationship": grouped_metrics(validation_values, splits["validation"], pixel, presence,
                                          "propRelationships"),
        "temporalConsistency": temporal_consistency(validation_values, splits["validation"],
                                                    pixel, presence),
        "review": {name: len(items) for name, items in review.items()},
        "hardNegativeTiles": hard_negative_count,
        "counts": {**{name: len(value) for name, value in splits.items()}, "sealedHoldout": len(holdout)},
        "cropSampling": {"positive": .5, "nearRvmNegative": .25, "emptyNegative": .25},
        "loss": {"focalGamma": 2, "tverskyAlpha": .3, "tverskyBeta": .7,
                 "exteriorWeight": 3, "presenceWeight": .2}}
    atomic_json(output / "metrics.json", metrics)
    if not promotion:
        emit("acceptance-failed", "v2 completed but was not packaged because sealed promotion criteria were not met",
             holdoutReady=holdout_ready, metrics=metrics); return
    package = output / "package"; package.mkdir(exist_ok=True)
    shutil.copy2(best_path, package / "model.pth"); shutil.copy2(snapshot, package / "dataset-snapshot.json")
    atomic_json(package / "metrics.json", metrics)
    checkpoint_hash = digest(package / "model.pth")
    model_id = f"prop-rvm-fpn-{time.strftime('%Y%m%d-%H%M%S', time.gmtime())}-{checkpoint_hash[:8]}"
    files = {str(path.relative_to(package)).replace("\\", "/"): digest(path)
             for path in package.rglob("*") if path.is_file() and path.name != "manifest.json"}
    manifest_value = {"schemaVersion": 2, "modelId": model_id, "architecture": ARCHITECTURE,
        "trainingRevision": TRAINING_REVISION, "category": "foreground_prop", "inputSize": args.input_size,
        "confidenceThreshold": pixel, "proximityRadiusAt512": 64,
        "checkpointSha256": checkpoint_hash,
        "input": {"cropSize": args.input_size, "channels": ["red", "green", "blue", "rvmAlpha", "rvmSignedDistance"],
                  "mean": MEAN, "std": STD, "rvmThreshold": .4,
                  "signedDistanceClipAt768": DISTANCE_CLIP_AT_INPUT,
                  "roiExpansion": ROI_EXPANSION, "minimumContextAt768": MIN_CONTEXT_AT_INPUT,
                  "maximumTiles": MAX_TILES},
        "output": {"kind": "visible-prop-probability", "presenceHead": True},
        "runtime": {"contract": "rvm-conditioned-temporal-union-v2", "pixelThreshold": pixel,
                    "presenceThreshold": presence, "maxComponentDistanceAt768": 96,
                    "temporalWindow": 3, "temporalRequired": 2,
                    "discoveryIntervalSeconds": 2.0},
        "datasetId": manifest["datasetId"], "runId": output.name,
        "minimumTrainingResolution": args.minimum_resolution,
        "createdUtc": time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
        "pythonVersion": sys.version.split()[0], "torchVersion": torch.__version__,
        "torchvisionVersion": __import__("torchvision").__version__, "cudaVersion": torch.version.cuda,
        "files": files}
    atomic_json(package / "manifest.json", manifest_value)
    emit("complete", "RVM-conditioned v2 passed screening and sealed promotion criteria",
         package=str(package), modelId=model_id, testDice=regression["exteriorDice"])


def self_test():
    import numpy as np
    import torch
    from PIL import Image
    with tempfile.TemporaryDirectory(prefix="iqp-prop-v2-test-") as value:
        root = Path(value); (root / "records" / "ab").mkdir(parents=True)
        Image.fromarray(np.zeros((64, 96, 3), np.uint8)).save(root / "frame.png")
        prop = np.zeros((64, 96), np.uint8); prop[20:30, 55:70] = 255
        alpha = np.zeros((64, 96), np.uint16); alpha[12:52, 24:58] = 65535
        Image.fromarray(prop).save(root / "prop.png"); Image.fromarray(alpha).save(root / "alpha.png")
        atomic_json(root / "dataset.json", {"schemaVersion": 1, "datasetId": "test",
            "sources": [{"id": "source", "sealedHoldout": False}]})
        sample = {"id": "abcdef", "sourceId": "source", "decision": "positive", "split": "train",
                  "width": 96, "height": 64, "framePath": "frame.png", "propMaskPath": "prop.png",
                  "rvmAlphaPath": "alpha.png"}
        atomic_json(root / "records" / "ab" / "abcdef.json",
                    {"schemaVersion": 1, "datasetId": "test", "sample": sample})
        loaded = load_manifest(root); assert len(split_samples(loaded, "train")) == 1
        rgb, target, loaded_alpha = load_arrays(root, sample)
        assert rgb.shape == (64, 96, 3) and target.sum() == 150 and loaded_alpha.max() == 1
        cropped = crop_square(rgb, target, loaded_alpha, (0, 0), 96, 64, False)
        assert cropped[0].shape == (64, 64, 3) and cropped[1].shape == (64, 64)
        inputs = conditioned_array(rgb, loaded_alpha); assert inputs.shape == (5, 64, 96)
        model = build_model(torch, pretrained=False)
        assert model.features[0][0].in_channels == 5
        assert torch.count_nonzero(model.features[0][0].weight[:, 3:]) == 0
        output = model(torch.from_numpy(inputs).unsqueeze(0))
        assert output["out"].shape == (1, 1, 64, 96) and output["presence"].shape == (1,)
        target_tensor = torch.from_numpy(target[None, None].astype("float32"))
        loss = training_loss(torch, output, target_tensor, target_tensor,
                             torch.ones(1)); assert torch.isfinite(loss)
        empty = torch.zeros_like(target_tensor)
        negative_loss = training_loss(torch, output, empty, empty,
                                      torch.zeros(1)); assert torch.isfinite(negative_loss)
        synthetic = [("p", target.astype(np.float32), target, loaded_alpha, .9),
                     ("n", np.zeros_like(target, np.float32), np.zeros_like(target), loaded_alpha, .1)]
        selected, _ = calibrate(synthetic)
        assert "exteriorF2" in selected and "constraintsMet" in selected
        json.dumps(selected)
        hard_path = root / "hard.json"
        save_hard_negative_set(hard_path, [(sample, "near-negative")])
        restored = load_hard_negative_set(hard_path, [sample])
        assert restored and restored[0][0]["id"] == sample["id"]
        confirmed = __import__("prop_segmenter_v2").confirm_temporal_masks(
            [target, target, np.zeros_like(target)])
        assert confirmed.sum() == target.sum()
    emit("self-test", "prop-segmenter v2 training self-test passed")


def main():
    if sys.argv[1:] == ["--self-test"]: self_test(); return
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--minimum-resolution", type=int, default=0)
    parser.add_argument("--input-size", type=int, default=INPUT_SIZE)
    parser.add_argument("--seed", type=int, default=20260821)
    parser.add_argument("--resume", action="store_true")
    args = parser.parse_args()
    if args.input_size != INPUT_SIZE: raise RuntimeError(f"v2 input size is pinned to {INPUT_SIZE}")
    train(args)


if __name__ == "__main__":
    try: main()
    except Exception as error:
        emit("error", str(error)); raise
