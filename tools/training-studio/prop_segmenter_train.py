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


class PropDataset:
    def __init__(self, root, samples, training):
        self.root, self.samples, self.training = Path(root), samples, training

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
            target = max(64, round(INPUT_SIZE * scale))
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
            canvas = Image.new("RGB", (max(INPUT_SIZE, image.width), max(INPUT_SIZE, image.height)), fill)
            canvas_mask = Image.new("L", canvas.size, 0)
            left, top = (canvas.width - image.width) // 2, (canvas.height - image.height) // 2
            canvas.paste(image, (left, top)); canvas_mask.paste(mask, (left, top))
            max_x, max_y = canvas.width - INPUT_SIZE, canvas.height - INPUT_SIZE
            x, y = random.randint(0, max_x), random.randint(0, max_y)
            image = canvas.crop((x, y, x + INPUT_SIZE, y + INPUT_SIZE))
            mask = canvas_mask.crop((x, y, x + INPUT_SIZE, y + INPUT_SIZE))
            pixels = np.asarray(image, dtype=np.float32).transpose(2, 0, 1) / 255
            pixels = (pixels - np.asarray(MEAN, np.float32)[:, None, None]) / \
                np.asarray(STD, np.float32)[:, None, None]
        else:
            pixels, region, _ = prepare_image(image)
            left, top, width, height = region
            canvas_mask = Image.new("L", (INPUT_SIZE, INPUT_SIZE), 0)
            canvas_mask.paste(mask.resize((width, height), Image.Resampling.NEAREST), (left, top))
            mask = canvas_mask
        target = (np.asarray(mask, dtype=np.uint8) >= 128).astype(np.float32)[None]
        return torch.from_numpy(pixels), torch.from_numpy(target), sample["id"]


def dice_loss(logits, target):
    probability = logits.sigmoid()
    numerator = 2 * (probability * target).sum(dim=(1, 2, 3)) + 1
    denominator = probability.sum(dim=(1, 2, 3)) + target.sum(dim=(1, 2, 3)) + 1
    return (1 - numerator / denominator).mean()


def segmentation_loss(torch, output, target, pos_weight):
    bce = torch.nn.functional.binary_cross_entropy_with_logits(
        output["out"], target, pos_weight=pos_weight)
    total = bce + dice_loss(output["out"], target)
    if "aux" in output:
        aux = torch.nn.functional.binary_cross_entropy_with_logits(
            output["aux"], target, pos_weight=pos_weight) + dice_loss(output["aux"], target)
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


def evaluate(torch, model, loader, device, thresholds=(.5,)):
    totals = {threshold: [0, 0, 0, 0] for threshold in thresholds}
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
    return {threshold: scores(value) for threshold, value in totals.items()}


def save_error_review(torch, model, loader, device, threshold, destination, limit=12):
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


def train(args):
    import torch
    from torch.utils.data import DataLoader
    root, output = args.dataset.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    manifest = load_manifest(root)
    splits = {name: accepted_samples(manifest, name)
              for name in ("train", "validation", "test")}
    if not splits["train"] or not splits["validation"] or not splits["test"]:
        raise RuntimeError("Train, validation, and test splits must each contain accepted frames")
    random.seed(args.seed); torch.manual_seed(args.seed)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    if device.type == "cuda":
        memory = torch.cuda.get_device_properties(device).total_memory
        batch = 4 if memory >= 20 * 1024 ** 3 else 2 if memory >= 12 * 1024 ** 3 else 1
    else:
        batch = 1
    accumulation = max(1, math.ceil(8 / batch))
    loaders = {
        "train": DataLoader(PropDataset(root, splits["train"], True), batch_size=batch,
                            shuffle=True, num_workers=min(4, os.cpu_count() or 1), pin_memory=device.type == "cuda"),
        "validation": DataLoader(PropDataset(root, splits["validation"], False), batch_size=batch,
                                 shuffle=False, num_workers=1),
        "test": DataLoader(PropDataset(root, splits["test"], False), batch_size=batch,
                           shuffle=False, num_workers=1)}
    emit("setup", f"Loading DeepLabV3-ResNet50 on {device}", batchSize=batch,
         gradientAccumulation=accumulation)
    model = build_model(torch, pretrained=True).to(device)
    pos_weight = torch.tensor([positive_weight(root, splits["train"])], device=device)
    best_dice, stale, start_epoch = -1., 0, 0
    last_path, best_path = output / "last.pth", output / "best.pth"
    optimizer = torch.optim.AdamW([
        {"params": model.backbone.parameters(), "lr": 1e-5},
        {"params": model.classifier.parameters(), "lr": 1e-4},
        {"params": model.aux_classifier.parameters(), "lr": 1e-4}], weight_decay=1e-4)
    scaler = torch.amp.GradScaler("cuda", enabled=device.type == "cuda")
    if args.resume and last_path.is_file():
        state = torch.load(last_path, map_location=device, weights_only=False)
        model.load_state_dict(state["model"]); optimizer.load_state_dict(state["optimizer"])
        scaler.load_state_dict(state["scaler"]); start_epoch = state["epoch"] + 1
        best_dice, stale = state["bestDice"], state["stale"]
        emit("resume", f"Resumed at epoch {start_epoch + 1}")
    total_epochs = args.warmup_epochs + args.epochs
    for epoch in range(start_epoch, total_epochs):
        frozen = epoch < args.warmup_epochs
        for parameter in model.backbone.parameters(): parameter.requires_grad = not frozen
        if frozen:
            for group in optimizer.param_groups: group["lr"] = 0 if group is optimizer.param_groups[0] else 1e-3
        else:
            optimizer.param_groups[0]["lr"] = 1e-5
            optimizer.param_groups[1]["lr"] = optimizer.param_groups[2]["lr"] = 1e-4
        model.train(); optimizer.zero_grad(set_to_none=True); running = 0.
        for step, (images, target, _) in enumerate(loaders["train"]):
            images, target = images.to(device, non_blocking=True), target.to(device, non_blocking=True)
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=device.type == "cuda"):
                loss = segmentation_loss(torch, model(images), target, pos_weight) / accumulation
            scaler.scale(loss).backward(); running += float(loss.item()) * accumulation
            if (step + 1) % accumulation == 0 or step + 1 == len(loaders["train"]):
                scaler.step(optimizer); scaler.update(); optimizer.zero_grad(set_to_none=True)
        validation = evaluate(torch, model, loaders["validation"], device)[.5]
        improved = validation["dice"] > best_dice
        if improved:
            best_dice, stale = validation["dice"], 0
            torch.save(model.state_dict(), best_path)
        else:
            stale += 1
        torch.save({"model": model.state_dict(), "optimizer": optimizer.state_dict(),
                    "scaler": scaler.state_dict(), "epoch": epoch, "bestDice": best_dice,
                    "stale": stale}, last_path)
        emit("epoch", "backbone frozen" if frozen else "full fine-tune",
             epoch=epoch + 1, trainingLoss=running / max(1, len(loaders["train"])),
             validationDice=validation["dice"], best=improved)
        if not frozen and stale >= args.patience:
            emit("early-stopping", f"No validation improvement for {stale} epochs")
            break
    model.load_state_dict(torch.load(best_path, map_location=device, weights_only=True))
    thresholds = tuple(round(value / 100, 2) for value in range(10, 91, 5))
    validation_all = evaluate(torch, model, loaders["validation"], device, thresholds)
    threshold = max(thresholds, key=lambda value: validation_all[value]["dice"])
    test_metrics = evaluate(torch, model, loaders["test"], device, (threshold,))[threshold]
    package = output / "package"; package.mkdir(exist_ok=True)
    checkpoint = package / "model.pth"; shutil.copy2(best_path, checkpoint)
    checkpoint_hash = digest(checkpoint)
    model_id = f"prop-r50-{time.strftime('%Y%m%d-%H%M%S', time.gmtime())}-{checkpoint_hash[:8]}"
    review = save_error_review(torch, model, loaders["test"], device, threshold, package / "review")
    metrics = {"validation": validation_all[threshold], "test": test_metrics,
               "threshold": threshold, "counts": {key: len(value) for key, value in splits.items()},
               "review": {key: len(value) for key, value in review.items()}}
    atomic_json(package / "metrics.json", metrics)
    files = {str(path.relative_to(package)).replace("\\", "/"): digest(path)
             for path in package.rglob("*") if path.is_file() and path.name != "manifest.json"}
    atomic_json(package / "manifest.json", {
        "schemaVersion": 1, "modelId": model_id, "architecture": ARCHITECTURE,
        "category": "foreground_prop", "inputSize": INPUT_SIZE,
        "mean": MEAN, "std": STD, "confidenceThreshold": threshold,
        "proximityRadiusAt512": 24, "checkpointSha256": checkpoint_hash,
        "preprocessing": {"resize": "aspect-preserving-letterbox", "inputSize": INPUT_SIZE,
                          "mean": MEAN, "std": STD},
        "postprocessing": {"contract": "rvm-proximity-union-v1", "confidenceThreshold": threshold,
                           "proximityRadiusAt512": 24, "componentConnectivity": 8},
        "datasetId": manifest["datasetId"], "runId": output.name,
        "createdUtc": time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
        "pythonVersion": sys.version.split()[0], "torchVersion": torch.__version__,
        "torchvisionVersion": __import__("torchvision").__version__,
        "cudaVersion": torch.version.cuda, "files": files})
    emit("review", "Saved ranked false-positive and false-negative review images",
         review=str(package / "review"))
    emit("complete", "Training, threshold calibration, and test evaluation complete",
         package=str(package), modelId=model_id, testDice=test_metrics["dice"])


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
        optimizer = torch.optim.AdamW(model.parameters(), lr=1e-3)
        image = torch.randn(2, 3, 8, 8); target = torch.zeros(2, 1, 8, 8)
        loss = torch.nn.functional.binary_cross_entropy_with_logits(model(image), target)
        loss.backward(); optimizer.step()
        checkpoint = root / "resume.pth"
        torch.save({"model": model.state_dict(), "optimizer": optimizer.state_dict(), "epoch": 0}, checkpoint)
        resumed = torch.nn.Conv2d(3, 1, 1)
        state = torch.load(checkpoint, weights_only=False); resumed.load_state_dict(state["model"])
        assert state["epoch"] == 0 and digest(checkpoint) == hashlib.sha256(checkpoint.read_bytes()).hexdigest()
        values = {threshold: scores(confusion(torch.tensor([.1, .9]),
                  torch.tensor([0., 1.]), threshold)) for threshold in (.25, .5, .75)}
        assert max(values, key=lambda item: values[item]["dice"]) in values
        class ReviewModel(torch.nn.Module):
            def forward(self, inputs):
                return {"out": inputs[:, :1] * 4}
        images = torch.zeros(2, 3, 8, 8); images[:, 0] = -1
        images[0, 0, :4, :4] = 1
        targets = torch.zeros(2, 1, 8, 8); targets[0, 0, 4:, 4:] = 1
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
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test(); return
    if args.dataset is None or args.output is None:
        parser.error("--dataset and --output are required")
    train(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", str(error)); raise
