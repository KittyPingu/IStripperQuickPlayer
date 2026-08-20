#!/usr/bin/env python3
"""Train, evaluate, and package QuickPlayer's binary prop segmenter."""
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

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "custom-shows"))
try:
    from prop_segmenter import (ARCHITECTURE, INPUT_SIZE, MEAN, STD,
                                build_model, digest, prepare_image)
except ImportError:
    # Published Training Studio links the shared helper beside this worker.
    from prop_segmenter import (ARCHITECTURE, INPUT_SIZE, MEAN, STD,
                                build_model, digest, prepare_image)


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
            samples.append(record["sample"])
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


def balanced_samples(samples, seed, split, negative_ratio=.30):
    positives = [sample for sample in samples
                 if sample.get("decision") == "positive"]
    negatives = [sample for sample in samples
                 if sample.get("decision") == "negative"]
    if not positives:
        return list(samples), len(negatives), len(negatives)
    negative_limit = max(1, round(
        len(positives) * negative_ratio / (1 - negative_ratio)))
    if len(negatives) <= negative_limit:
        return list(samples), len(negatives), len(negatives)
    ordered = sorted(negatives, key=lambda sample: sample.get("id", ""))
    random.Random(f"{seed}:{split}").shuffle(ordered)
    selected = ordered[:negative_limit]
    return positives + selected, len(negatives), len(selected)


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
            scale = random.uniform(.75, 1.5)
            target = max(64, round(self.input_size * scale))
            ratio = target / min(image.size)
            size = (max(1, round(image.width * ratio)), max(1, round(image.height * ratio)))
            image = image.resize(size, Image.Resampling.BILINEAR)
            mask = mask.resize(size, Image.Resampling.NEAREST)
            if random.random() < .5:
                image = image.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
                mask = mask.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
            if random.random() < .3:
                image = ImageEnhance.Brightness(image).enhance(random.uniform(.85, 1.15))
                image = ImageEnhance.Contrast(image).enhance(random.uniform(.85, 1.15))
                image = ImageEnhance.Color(image).enhance(random.uniform(.85, 1.15))
            if random.random() < .05:
                image = image.filter(ImageFilter.GaussianBlur(random.uniform(.2, 1.2)))
            fill = tuple(round(value * 255) for value in MEAN)
            canvas = Image.new("RGB", (max(self.input_size, image.width),
                                       max(self.input_size, image.height)), fill)
            canvas_mask = Image.new("L", canvas.size, 0)
            left, top = (canvas.width - image.width) // 2, (canvas.height - image.height) // 2
            canvas.paste(image, (left, top)); canvas_mask.paste(mask, (left, top))
            max_x, max_y = canvas.width - self.input_size, canvas.height - self.input_size
            foreground = canvas_mask.getbbox()
            if foreground and random.random() < .75:
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
            pixels = np.asarray(image, dtype=np.float32).transpose(2, 0, 1) / 255
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


def dice_loss(logits, target):
    probability = logits.sigmoid()
    numerator = 2 * (probability * target).sum(dim=(1, 2, 3)) + 1
    denominator = probability.sum(dim=(1, 2, 3)) + target.sum(dim=(1, 2, 3)) + 1
    return (1 - numerator / denominator).mean()


def tversky_loss(logits, target, alpha=.3, beta=.7):
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


def segmentation_loss(torch, output, target, pos_weight):
    total = (focal_bce(torch, output["out"], target, pos_weight) +
             dice_loss(output["out"], target) +
             .5 * tversky_loss(output["out"], target))
    if "aux" in output:
        aux = (focal_bce(torch, output["aux"], target, pos_weight) +
               dice_loss(output["aux"], target) +
               .5 * tversky_loss(output["aux"], target))
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
    return (.4 * value["dice"] + .15 * value["macroDice"] +
            .15 * value["positiveRecall"] + .1 * value["smallObjectRecall"] +
            .2 * value["boundaryF1"])


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


def evaluate(torch, model, loader, device, thresholds=(.5,)):
    totals = {threshold: [0, 0, 0, 0] for threshold in thresholds}
    per_image = {threshold: {"dice": [], "recall": [], "smallRecall": [], "boundary": []}
                 for threshold in thresholds}
    model.eval()
    bf16 = device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 8
    with torch.inference_mode():
        for images, target, _ in loader:
            images, target = images.to(device), target.to(device)
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=bf16):
                probability = model(images)["out"].float().sigmoid()
            for threshold in thresholds:
                current = confusion(probability, target, threshold)
                totals[threshold] = [a + b for a, b in zip(totals[threshold], current)]
                for index in range(target.shape[0]):
                    truth = target[index] >= .5
                    truth_pixels = int(truth.sum())
                    if truth_pixels == 0: continue
                    predicted = probability[index] >= threshold
                    tp = int((predicted & truth).sum())
                    fp = int((predicted & ~truth).sum())
                    fn = truth_pixels - tp
                    per_image[threshold]["dice"].append(2 * tp / max(1, 2 * tp + fp + fn))
                    recall = tp / truth_pixels
                    per_image[threshold]["recall"].append(recall)
                    per_image[threshold]["boundary"].append(
                        boundary_f1(torch, predicted[0] if predicted.ndim == 3 else predicted,
                                    truth[0] if truth.ndim == 3 else truth))
                    if truth_pixels <= truth.numel() * .02:
                        per_image[threshold]["smallRecall"].append(recall)
    result = {}
    for threshold, value in totals.items():
        result[threshold] = scores(value)
        current = per_image[threshold]
        result[threshold]["macroDice"] = sum(current["dice"]) / max(1, len(current["dice"]))
        result[threshold]["positiveRecall"] = sum(current["recall"]) / max(1, len(current["recall"]))
        result[threshold]["smallObjectRecall"] = (sum(current["smallRecall"]) /
            max(1, len(current["smallRecall"]))) if current["smallRecall"] else result[threshold]["positiveRecall"]
        result[threshold]["boundaryF1"] = sum(current["boundary"]) / max(1, len(current["boundary"]))
        result[threshold]["selectionScore"] = selection_score(result[threshold])
    return result


def save_error_review(torch, model, loader, device, threshold, destination, limit=50):
    """Save the test frames with the largest false-positive/false-negative areas."""
    import numpy as np
    from PIL import Image
    destination.mkdir(parents=True, exist_ok=True)
    candidates = {"false-positive": [], "false-negative": []}
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
                pixels = images[index].numpy().transpose(1, 2, 0)
                pixels = np.clip((pixels * np.asarray(STD) + np.asarray(MEAN)) * 255, 0, 255).astype(np.uint8)
                overlay = pixels.copy()
                for mask, colour in ((prediction & truth, (0, 220, 80)),
                                     (false_positive, (255, 45, 45)),
                                     (false_negative, (25, 135, 255))):
                    overlay[mask] = (.4 * overlay[mask] + .6 * np.asarray(colour)).astype(np.uint8)
                for kind, area in (("false-positive", int(false_positive.sum())),
                                   ("false-negative", int(false_negative.sum()))):
                    if area:
                        candidates[kind].append((area, str(sample_id), overlay.copy()))
                        candidates[kind] = sorted(candidates[kind], key=lambda item: item[0], reverse=True)[:limit]
    summary = {}
    for kind, values in candidates.items():
        summary[kind] = []
        for rank, (area, sample_id, overlay) in enumerate(values, 1):
            filename = f"{kind}-{rank:02d}-{sample_id}-{area}px.png"
            Image.fromarray(overlay).save(destination / filename, compress_level=6)
            summary[kind].append({"sampleId": sample_id, "errorPixelsAt512": area,
                                  "image": filename})
    atomic_json(destination / "review.json", summary)
    return summary


def positive_weight(root, samples):
    from PIL import Image
    import numpy as np
    positive = total = 0
    for sample in samples:
        value = np.asarray(Image.open(root / sample["propMaskPath"]).convert("L"), dtype=np.uint8)
        positive += int((value >= 128).sum()); total += value.size
    return min(20., max(1., (total - positive) / max(1, positive)))


def freeze_batch_norm(torch, model):
    # DeepLab's final ResNet feature map is 1x1. A singleton final batch cannot
    # calculate BatchNorm statistics, so retain the pretrained running values
    # while the convolutional and classifier weights continue fine-tuning.
    for module in model.modules():
        if isinstance(module, torch.nn.modules.batchnorm._BatchNorm):
            module.eval()


def train_candidate(torch, DataLoader, args, root, output, device, batch,
                    accumulation, train_samples, validation_samples, negative_ratio,
                    selection_name):
    candidate = output / "candidates" / f"negative-{selection_name}"
    candidate.mkdir(parents=True, exist_ok=True)
    hard_weights = [3. if sample.get("feedbackPriority") or sample.get("burstId") else 1.
                    for sample in train_samples]
    sampler_generator = torch.Generator().manual_seed(args.seed)
    sampler = torch.utils.data.WeightedRandomSampler(
        hard_weights, len(train_samples), replacement=True, generator=sampler_generator)
    loaders = {
        "train": DataLoader(PropDataset(root, train_samples, True, args.input_size), batch_size=batch,
                            sampler=sampler, num_workers=min(4, os.cpu_count() or 1),
                            pin_memory=device.type == "cuda"),
        "validation": DataLoader(PropDataset(root, validation_samples, False, args.input_size), batch_size=batch,
                                 shuffle=False, num_workers=1)}
    random.seed(args.seed); torch.manual_seed(args.seed)
    model = build_model(torch, pretrained=True).to(device)
    pos_weight = torch.tensor([positive_weight(root, train_samples)], device=device)
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
        validation_all = evaluate(torch, ema.module, loaders["validation"], device, checkpoint_thresholds)
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
                    "stale": stale}, last_path)
        emit("epoch", "backbone frozen" if frozen else "full fine-tune",
             epoch=epoch + 1, trainingLoss=running / max(1, len(loaders["train"])),
             validationDice=validation["dice"], validationRecall=validation["recall"],
             validationMacroDice=validation["macroDice"],
             validationSmallObjectRecall=validation["smallObjectRecall"],
             validationSelectionScore=validation["selectionScore"],
             validationThreshold=validation_threshold, best=improved,
             negativeRatio=negative_ratio)
        if not frozen and stale >= args.patience:
            emit("early-stopping", f"No validation improvement for {stale} epochs")
            break
    if not best_path.is_file():
        raise RuntimeError(f"No checkpoint was produced for {negative_ratio:.0%} negatives")
    model.load_state_dict(torch.load(best_path, map_location=device, weights_only=True))
    thresholds = tuple(round(value / 100, 2) for value in range(10, 91, 5))
    validation_all = evaluate(torch, model, loaders["validation"], device, thresholds)
    threshold = max(thresholds, key=lambda value: validation_all[value]["selectionScore"])
    result = {"targetNegativeRatio": negative_ratio,
              "negativeSelection": selection_name,
              "actualNegativeRatio": sum(sample.get("decision") == "negative" for sample in train_samples) /
                                     max(1, len(train_samples)),
              "hardExampleCount": sum(weight > 1 for weight in hard_weights),
              "trainCount": len(train_samples), "threshold": threshold,
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
            description = f"ALL negatives ({negative_ratio:.1%} of training samples)"
        else:
            train_samples, negative_available, negative_selected = balanced_samples(
                available["train"], args.seed, "train", negative_ratio)
            description = f"{negative_ratio:.0%} negatives"
        emit("candidate", f"Training candidate with {description}",
             negativeRatio=negative_ratio, negativeAvailable=negative_available,
             negativeSelected=negative_selected, trainCount=len(train_samples),
             negativeSelection=selection_name)
        result, checkpoint_path = train_candidate(
            torch, DataLoader, args, root, output, device, batch, accumulation,
            train_samples, available["validation"], negative_ratio, selection_name)
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
    test_metrics = evaluate(torch, model, test_loader, device, (threshold,))[threshold]
    package = output / "package"; package.mkdir(exist_ok=True)
    checkpoint = package / "model.pth"; shutil.copy2(best_path, checkpoint)
    checkpoint_hash = digest(checkpoint)
    model_id = f"prop-r50-{time.strftime('%Y%m%d-%H%M%S', time.gmtime())}-{checkpoint_hash[:8]}"
    review = save_error_review(torch, model, test_loader, device, threshold, package / "review")
    metrics = {"validation": winner["validation"], "test": test_metrics,
               "threshold": threshold, "selectedNegativeRatio": winner["targetNegativeRatio"],
               "selectedNegativeMode": winner["negativeSelection"],
               "minimumResolution": args.minimum_resolution,
               "inputSize": args.input_size,
               "balanceCandidates": candidates,
               "counts": {key: len(value) for key, value in available.items()},
               "review": {key: len(value) for key, value in review.items()}}
    atomic_json(package / "metrics.json", metrics)
    files = {str(path.relative_to(package)).replace("\\", "/"): digest(path)
             for path in package.rglob("*") if path.is_file() and path.name != "manifest.json"}
    atomic_json(package / "manifest.json", {
        "schemaVersion": 1, "modelId": model_id, "architecture": ARCHITECTURE,
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
    emit("review", "Saved ranked false-positive and false-negative review images",
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
    samples = resolution_samples(accepted_samples(load_manifest(root), "test"), minimum_resolution)
    if not samples:
        raise RuntimeError("No eligible test samples remain for error review")
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    batch = max(1, math.floor((4 if device.type == "cuda" else 1) * (512 / input_size) ** 2))
    emit("review", f"Regenerating error review from the saved model on {device}",
         testCount=len(samples), inputSize=input_size, threshold=threshold)
    model = build_model(torch, pretrained=False).to(device)
    model.load_state_dict(torch.load(checkpoint, map_location=device, weights_only=True))
    loader = DataLoader(PropDataset(root, samples, False, input_size), batch_size=batch,
                        shuffle=False, num_workers=1)
    review_folder = package / "review"
    if review_folder.exists(): shutil.rmtree(review_folder)
    review = save_error_review(torch, model, loader, device, threshold, review_folder)
    metrics["review"] = {key: len(value) for key, value in review.items()}
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
        samples = ([{"id": f"p{index}", "decision": "positive"}
                    for index in range(7)] +
                   [{"id": f"n{index}", "decision": "negative"}
                    for index in range(20)])
        balanced, available, selected = balanced_samples(samples, 1729, "train")
        repeated, _, _ = balanced_samples(samples, 1729, "train")
        assert available == 20 and selected == 3 and len(balanced) == 10
        assert [sample["id"] for sample in balanced] == \
               [sample["id"] for sample in repeated]
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
        review_metrics = evaluate(torch, ReviewModel(),
            [(images, targets, ("sample-a", "sample-b"))], torch.device("cpu"), (.5,))[.5]
        assert all(name in review_metrics for name in
                   ("macroDice", "positiveRecall", "smallObjectRecall", "boundaryF1", "selectionScore"))
        combined_loss = segmentation_loss(torch, {"out": images[:, :1] * 4}, targets,
                                          torch.tensor([2.]))
        assert torch.isfinite(combined_loss)
        review = save_error_review(torch, ReviewModel(),
            [(images, targets, ("sample-a", "sample-b"))], torch.device("cpu"), .5,
            root / "review", limit=2)
        assert review["false-positive"] and review["false-negative"]
        assert (root / "review" / "review.json").is_file()
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
