#!/usr/bin/env python3
"""Prepare, train, and benchmark YOLO26 prop-mask candidates."""
import argparse
import hashlib
import json
import math
import os
import shutil
import sys
import tempfile
import time
from pathlib import Path

EXPORT_FORMAT = 1
TRAINING_REVISION = 1
INPUT_SIZE = 1024
VARIANTS = {"yolo26s-sem": "semantic", "yolo26s-seg": "segment"}
NEGATIVE_FRAME_CEILING = .05
NEGATIVE_P95_AREA_CEILING = .001

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "custom-shows"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from prop_segmenter import digest
from prop_segmenter_v2 import filter_prediction, inference_tiles
from prop_segmenter_train_v2 import binary_metrics, evaluate_v1_on_samples, load_arrays


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
    samples = []
    for path in sorted((root / "records").glob("*/*.json")):
        record = json.loads(path.read_text(encoding="utf-8-sig"))
        if record.get("datasetId") != value.get("datasetId"):
            raise RuntimeError(f"Record does not belong to this dataset: {path}")
        sample = record["sample"]; sample["_recordPath"] = str(path.relative_to(root))
        sample["_recordSha256"] = digest(path); samples.append(sample)
    value["samples"] = samples
    return value


def selected_samples(manifest, minimum_resolution):
    sources = {value["id"]: value for value in manifest.get("sources", [])}
    result = []
    for sample in manifest.get("samples", []):
        if sample.get("decision") not in ("positive", "negative"): continue
        if not sample.get("framePath") or not sample.get("propMaskPath"): continue
        if minimum_resolution and min(int(sample.get("width", 0)),
                                      int(sample.get("height", 0))) < minimum_resolution: continue
        source = sources.get(sample.get("sourceId"), {})
        sample = dict(sample); sample["_sealed"] = bool(source.get("sealedHoldout"))
        sample["_exportSplit"] = "holdout" if sample["_sealed"] else sample.get("split", "train")
        result.append(sample)
    return result


def read_gray(path):
    import cv2
    value = cv2.imread(str(path), cv2.IMREAD_UNCHANGED)
    if value is None: raise RuntimeError(f"Could not read {path}")
    if value.ndim == 3: value = value[:, :, 0]
    return value


def square_bounds(mask, alpha):
    import numpy as np
    height, width = mask.shape
    ys, xs = np.where(mask > 0)
    if len(xs):
        object_area = max(1, int((mask > 0).sum()))
        extent = max(int(xs.max() - xs.min() + 1), int(ys.max() - ys.min() + 1))
        side = max(extent * 1.15, math.sqrt(object_area / .08))
        cx, cy = float(xs.mean()), float(ys.mean())
    else:
        ys, xs = np.where(alpha >= .4)
        if len(xs):
            extent = max(int(xs.max() - xs.min() + 1), int(ys.max() - ys.min() + 1))
            side = extent * 1.7; cx, cy = float(xs.mean()), float(ys.mean())
        else: side = max(height, width); cx, cy = width / 2, height / 2
    side = max(32, min(int(math.ceil(side)), max(height, width)))
    return round(cx - side / 2), round(cy - side / 2), side


def crop_square(value, bounds, fill=0):
    import numpy as np
    left, top, side = bounds
    shape = (side, side) + (() if value.ndim == 2 else (value.shape[2],))
    output = np.full(shape, fill, dtype=value.dtype)
    src_left, src_top = max(0, left), max(0, top)
    src_right, src_bottom = min(value.shape[1], left + side), min(value.shape[0], top + side)
    if src_right > src_left and src_bottom > src_top:
        output[src_top - top:src_bottom - top, src_left - left:src_right - left] = \
            value[src_top:src_bottom, src_left:src_right]
    return output


def instance_polygons(mask):
    import cv2
    import numpy as np
    lines, rendered = [], np.zeros(mask.shape, np.uint8)
    height, width = mask.shape
    for object_id in np.unique(mask):
        if object_id == 0: continue
        binary = (mask == object_id).astype(np.uint8)
        contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        for contour in contours:
            if len(contour) < 3 or cv2.contourArea(contour) < 1: continue
            cv2.drawContours(rendered, [contour], -1, 1, thickness=cv2.FILLED)
            points = contour[:, 0, :]
            lines.append("0 " + " ".join(f"{x / width:.8f} {y / height:.8f}" for x, y in points))
    truth = mask > 0; predicted = rendered > 0
    union = int((truth | predicted).sum())
    iou = int((truth & predicted).sum()) / max(1, union)
    return lines, iou


def prepare_dataset(root, output, manifest, samples, variant, size=INPUT_SIZE, destination=None):
    import cv2
    import numpy as np
    destination = Path(destination) if destination else output / "yolo-dataset"
    ready = destination / "READY"
    export_path = destination / "export.json"
    if ready.is_file() and export_path.is_file():
        export = json.loads(export_path.read_text(encoding="utf-8"))
        if export.get("variant") == variant and export.get("inputSize") == size:
            emit("prepare", f"Reusing shared {variant} export ({len(export['samples']):,} crops)")
            return destination, export["samples"]
    if destination.exists(): shutil.rmtree(destination)
    failures, exported = [], []
    for index, sample in enumerate(samples, 1):
        split = sample["_exportSplit"]
        frame = cv2.imread(str(root / sample["framePath"]), cv2.IMREAD_COLOR)
        prop = read_gray(root / sample["propMaskPath"])
        alpha_path = sample.get("rvmAlphaPath") or sample.get("rvmPersonMaskPath")
        if frame is None or not alpha_path: raise RuntimeError(f"Sample {sample['id']} is incomplete")
        alpha = read_gray(root / alpha_path).astype(np.float32)
        alpha /= 65535. if alpha.max(initial=0) > 255 else 255.
        bounds = square_bounds(prop, alpha)
        image_crop = crop_square(frame, bounds, (124, 116, 104))
        prop_crop = crop_square(prop, bounds)
        alpha_crop = crop_square(alpha, bounds)
        interpolation = cv2.INTER_AREA if bounds[2] > size else cv2.INTER_LINEAR
        image_crop = cv2.resize(image_crop, (size, size), interpolation=interpolation)
        prop_crop = cv2.resize(prop_crop, (size, size), interpolation=cv2.INTER_NEAREST)
        alpha_crop = cv2.resize(alpha_crop, (size, size), interpolation=cv2.INTER_LINEAR)
        image_path = destination / "images" / split / f"{sample['id']}.jpg"
        image_path.parent.mkdir(parents=True, exist_ok=True)
        cv2.imwrite(str(image_path), image_crop, [cv2.IMWRITE_JPEG_QUALITY, 95])
        alpha_path_out = destination / "alpha" / split / f"{sample['id']}.png"
        alpha_path_out.parent.mkdir(parents=True, exist_ok=True)
        cv2.imwrite(str(alpha_path_out), np.clip(alpha_crop * 65535, 0, 65535).astype(np.uint16))
        truth_path = destination / "truth" / split / f"{sample['id']}.png"
        truth_path.parent.mkdir(parents=True, exist_ok=True)
        cv2.imwrite(str(truth_path), (prop_crop > 0).astype(np.uint8) * 255)
        polygon_iou = None
        if variant == "yolo26s-sem":
            mask_path = destination / "masks" / split / f"{sample['id']}.png"
            mask_path.parent.mkdir(parents=True, exist_ok=True)
            cv2.imwrite(str(mask_path), (prop_crop > 0).astype(np.uint8))
        else:
            label_path = destination / "labels" / split / f"{sample['id']}.txt"
            label_path.parent.mkdir(parents=True, exist_ok=True)
            if sample.get("decision") == "positive" and sample.get("instanceMaskPath"):
                instances = read_gray(root / sample["instanceMaskPath"])
                instances = crop_square(instances, bounds)
                instances = cv2.resize(instances, (size, size), interpolation=cv2.INTER_NEAREST)
                lines, polygon_iou = instance_polygons(instances)
                if polygon_iou < .98: failures.append({"sampleId": sample["id"], "iou": polygon_iou})
                label_path.write_text("\n".join(lines) + ("\n" if lines else ""), encoding="utf-8")
            else: label_path.write_text("", encoding="utf-8")
        exported.append({"id": sample["id"], "sourceId": sample["sourceId"], "split": split,
            "decision": sample["decision"], "recordSha256": sample["_recordSha256"],
            "crop": list(bounds), "maskFraction": float((prop_crop > 0).mean()),
            "polygonIoU": polygon_iou})
        if index % 250 == 0: emit("prepare", f"Prepared {index:,}/{len(samples):,} YOLO crops",
                                  completed=index, total=len(samples))
    atomic_json(destination / "export.json", {"schemaVersion": EXPORT_FORMAT,
        "variant": variant, "inputSize": size, "samples": exported, "polygonFailures": failures})
    if failures:
        atomic_json(output / "polygon-conversion-failures.json", failures)
        raise RuntimeError(f"{len(failures)} instance masks converted below 0.98 IoU")
    data = {"path": str(destination), "train": "images/train", "val": "images/validation",
            "test": "images/test"}
    if variant == "yolo26s-sem":
        data.update({"masks_dir": "masks", "names": {"0": "background", "1": "foreground_prop"}})
    else: data["names"] = {"0": "foreground_prop"}
    atomic_json(destination / "dataset.yaml", data)
    ready.write_text("ready\n", encoding="ascii")
    return destination, exported


def cleanup_legacy_exports(root, keep_run=None):
    runs = (root / "runs").resolve()
    if not runs.is_dir(): return 0
    removed = 0
    keep = Path(keep_run).resolve() if keep_run else None
    for run in runs.iterdir():
        if not run.is_dir() or (keep is not None and run.resolve() == keep): continue
        generated = run / "yolo-dataset"
        if generated.is_dir() and generated.resolve().parent == run.resolve():
            shutil.rmtree(generated); removed += 1
    if removed: emit("storage", f"Removed generated YOLO datasets from {removed} old runs")
    return removed


def cleanup_stale_shared_exports(root, variant, current):
    cache = (root / "yolo-cache").resolve()
    if not cache.is_dir(): return 0
    current = Path(current).resolve(); prefix = f"{variant}-r"
    removed = 0
    for candidate in cache.iterdir():
        if not candidate.is_dir() or not candidate.name.startswith(prefix) or \
                candidate.resolve() == current: continue
        if candidate.resolve().parent != cache: continue
        shutil.rmtree(candidate); removed += 1
    if removed: emit("storage", f"Removed {removed} stale shared {variant} exports")
    return removed


def candidate_probabilities(model, variant, image_or_path, device):
    import cv2
    import numpy as np
    import torch
    image = image_or_path if isinstance(image_or_path, np.ndarray) else \
        cv2.imread(str(image_or_path), cv2.IMREAD_COLOR)
    if variant == "yolo26s-sem":
        tensor = torch.from_numpy(image[:, :, ::-1].copy()).permute(2, 0, 1).float().div(255).unsqueeze(0).to(device)
        with torch.inference_mode(), torch.autocast(device_type=device.type,
                dtype=torch.bfloat16, enabled=device.type == "cuda"):
            logits = model.model(tensor)
        logits = torch.nn.functional.interpolate(logits.float(), size=image.shape[:2],
            mode="bilinear", align_corners=False)
        return logits.softmax(1)[0, 1].cpu().numpy()
    result = model.predict(image, imgsz=INPUT_SIZE, conf=.001, retina_masks=True,
                           device=str(device), verbose=False)[0]
    probability = np.zeros(image.shape[:2], np.float32)
    if result.masks is not None:
        masks = result.masks.data.float().cpu().numpy()
        confidences = result.boxes.conf.float().cpu().numpy()
        for mask, confidence in zip(masks, confidences):
            if mask.shape != probability.shape:
                mask = cv2.resize(mask, (probability.shape[1], probability.shape[0]),
                                  interpolation=cv2.INTER_LINEAR)
            probability = np.maximum(probability, mask * float(confidence))
    return probability


def aggregate_at_threshold(values, threshold):
    predictions = []
    for probability, target, alpha in values:
        retained, _, _ = filter_prediction(probability, alpha, threshold, 1., 0., 96, INPUT_SIZE)
        predictions.append((retained, target, alpha))
    metrics = binary_metrics(predictions); metrics["threshold"] = threshold
    metrics["constraintsMet"] = metrics["negativeFrameFalsePositiveRate"] <= NEGATIVE_FRAME_CEILING and \
        metrics["negativeP95AddedArea"] <= NEGATIVE_P95_AREA_CEILING
    return metrics


def evaluate(model_path, variant, dataset, split, device):
    import cv2
    import numpy as np
    from ultralytics import YOLO
    images = sorted((dataset / "images" / split).glob("*.jpg"))
    model = YOLO(str(model_path)); model.model.to(device).eval()
    values, elapsed = [], 0.
    for index, image_path in enumerate(images, 1):
        started = time.perf_counter()
        probability = candidate_probabilities(model, variant, image_path, device)
        elapsed += time.perf_counter() - started
        target = read_gray(dataset / "truth" / split / f"{image_path.stem}.png") > 0
        alpha = read_gray(dataset / "alpha" / split / f"{image_path.stem}.png").astype(np.float32) / 65535.
        values.append((probability, target, alpha))
        if index % 100 == 0: emit("evaluation", f"Evaluating {split} {index:,}/{len(images):,}")
    options = [aggregate_at_threshold(values, value / 100) for value in range(5, 96, 5)]
    feasible = [value for value in options if value["constraintsMet"]]
    best = max(feasible or options, key=lambda value: (value["constraintsMet"],
        value["exteriorRecall"], value["exteriorF2"], value["exteriorPrecision"]))
    return best, values, elapsed / max(1, len(images))


def evaluate_sources(model_path, variant, root, samples, split, device):
    """Evaluate tiled model output at each accepted frame's original resolution."""
    import cv2
    import numpy as np
    from ultralytics import YOLO
    selected = [value for value in samples if value["_exportSplit"] == split]
    model = YOLO(str(model_path)); model.model.to(device).eval()
    values, elapsed = [], 0.
    for index, sample in enumerate(selected, 1):
        rgb, target, alpha = load_arrays(root, sample)
        probability = np.zeros(target.shape, np.float32); started = time.perf_counter()
        for left, top, right, bottom in inference_tiles(alpha, INPUT_SIZE):
            height, width = bottom - top, right - left
            tile = np.full((INPUT_SIZE, INPUT_SIZE, 3), (104, 116, 124), np.uint8)
            tile[:height, :width] = rgb[top:bottom, left:right, ::-1]
            tile_probability = candidate_probabilities(model, variant, tile, device)
            probability[top:bottom, left:right] = np.maximum(
                probability[top:bottom, left:right], tile_probability[:height, :width])
        elapsed += time.perf_counter() - started
        values.append((probability, target, alpha))
        if index % 100 == 0:
            emit("evaluation", f"Evaluating source-resolution {split} {index:,}/{len(selected):,}")
    options = [aggregate_at_threshold(values, value / 100) for value in range(5, 96, 5)]
    feasible = [value for value in options if value["constraintsMet"]]
    best = max(feasible or options, key=lambda value: (value["constraintsMet"],
        value["exteriorRecall"], value["exteriorF2"], value["exteriorPrecision"]))
    return best, elapsed / max(1, len(selected))


def save_review(dataset, values, threshold, destination, limit=50):
    import cv2
    import numpy as np
    destination.mkdir(parents=True, exist_ok=True)
    records = {"false-positive": [], "false-negative": []}; ranked = {key: [] for key in records}
    images = sorted((dataset / "images" / "validation").glob("*.jpg"))
    for image_path, (probability, target, alpha) in zip(images, values):
        predicted, _, _ = filter_prediction(probability, alpha, threshold, 1., 0., 96, INPUT_SIZE)
        exterior = alpha < .4
        ranked["false-positive"].append((int((predicted & ~target & exterior).sum()), image_path, predicted, target))
        ranked["false-negative"].append((int((~predicted & target & exterior).sum()), image_path, predicted, target))
    for kind, items in ranked.items():
        for error, image_path, predicted, target in sorted(items, key=lambda value: value[0], reverse=True)[:limit]:
            if error == 0: continue
            image = cv2.imread(str(image_path)); overlay = image.copy()
            overlay[predicted] = overlay[predicted] * .55 + np.array([70, 220, 20]) * .45
            overlay[predicted & ~target] = (45, 45, 235); overlay[~predicted & target] = (255, 120, 40)
            output = destination / f"{image_path.stem}.jpg"; cv2.imwrite(str(output), overlay)
            records[kind].append({"sampleId": image_path.stem, "errorPixelsAtInput": error,
                                  "image": output.name, "families": [], "relationships": []})
    atomic_json(destination / "review.json", records)


def train(args):
    import torch
    import ultralytics
    from ultralytics import YOLO
    root, output = Path(args.dataset), Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    manifest = load_manifest(root); samples = selected_samples(manifest, args.minimum_resolution)
    holdout = [value for value in samples if value["_sealed"]]
    trainable = [value for value in samples if not value["_sealed"]]
    if not holdout or not trainable: raise RuntimeError("YOLO benchmark requires trainable and sealed samples")
    all_samples = trainable + holdout
    # Keep this compatible with snapshots written before shared exports existed.
    snapshot_fingerprint = hashlib.sha256("\n".join(sorted(
        value["_recordSha256"] for value in all_samples)).encode()).hexdigest()
    shared_export = root / "yolo-cache" / \
        f"{args.variant}-r{TRAINING_REVISION}-{INPUT_SIZE}-{snapshot_fingerprint[:16]}"
    dataset, exported = prepare_dataset(root, output, manifest, all_samples, args.variant,
        destination=shared_export)
    cleanup_stale_shared_exports(root, args.variant, dataset)
    cleanup_legacy_exports(root, output if args.resume else None)
    training_sources = {value["sourceId"] for value in exported if value["split"] != "holdout"}
    sealed_sources = {value["sourceId"] for value in exported if value["split"] == "holdout"}
    if training_sources & sealed_sources:
        raise RuntimeError("A source leaked between training and sealed holdout")
    pretrained = output / "pretrained" / (args.variant + ".pt")
    pretrained.parent.mkdir(parents=True, exist_ok=True)
    model = YOLO(str((output / "ultralytics" / "weights" / "last.pt") if args.resume else pretrained))
    pretrained_path = pretrained if pretrained.is_file() else Path(model.ckpt_path or pretrained)
    snapshot = {"schemaVersion": 1, "datasetId": manifest["datasetId"], "variant": args.variant,
        "trainingRevision": TRAINING_REVISION, "exportFormat": EXPORT_FORMAT,
        "inputSize": INPUT_SIZE, "ultralyticsVersion": ultralytics.__version__,
        "pretrainedWeight": pretrained_path.name,
        "pretrainedSha256": digest(pretrained_path) if pretrained_path.is_file() else None,
        "datasetSnapshotSha256": snapshot_fingerprint,
        "sharedExport": str(dataset),
        "counts": {name: sum(value["split"] == name for value in exported)
                   for name in ("train", "validation", "test", "holdout")},
        "positiveCropBands": {
            "target2To15Percent": sum(value["decision"] == "positive" and
                .02 <= value["maskFraction"] <= .15 for value in exported),
            "safety0_5To30Percent": sum(value["decision"] == "positive" and
                .005 <= value["maskFraction"] <= .30 for value in exported),
            "outsideSafety": sum(value["decision"] == "positive" and
                not .005 <= value["maskFraction"] <= .30 for value in exported)}}
    snapshot_path = output / "dataset-snapshot.json"
    if args.resume and snapshot_path.is_file():
        previous = json.loads(snapshot_path.read_text(encoding="utf-8"))
        for key in ("variant", "trainingRevision", "exportFormat", "inputSize",
                    "ultralyticsVersion", "datasetSnapshotSha256"):
            if previous.get(key) != snapshot.get(key):
                raise RuntimeError(f"Resume configuration mismatch: {key}")
    atomic_json(snapshot_path, snapshot)
    emit("setup", f"Training {args.variant} on cuda", architecture=args.variant,
         frameCount=len(exported), holdoutCount=len(holdout))
    def epoch_callback(trainer):
        metrics = trainer.metrics or {}; fitness = float(trainer.fitness or 0.)
        if args.variant == "yolo26s-sem":
            emit("epoch", f"{args.variant} fine-tune", epoch=trainer.epoch + 1,
                 validationMeanIou=float(metrics.get("metrics/mIoU", fitness)),
                 validationPixelAccuracy=float(metrics.get("metrics/pixel_acc", 0.)))
        else:
            emit("epoch", f"{args.variant} fine-tune", epoch=trainer.epoch + 1,
                 validationDice=fitness,
                 validationPrecision=float(metrics.get("metrics/precision(M)", 0.)),
                 validationRecall=float(metrics.get("metrics/recall(M)", 0.)))
    model.add_callback("on_fit_epoch_end", epoch_callback)
    model.train(data=str(dataset / "dataset.yaml"), task=VARIANTS[args.variant],
        epochs=args.epochs, patience=20, imgsz=INPUT_SIZE, batch=-1, device=0, optimizer="AdamW",
        project=str(output), name="ultralytics", exist_ok=True, lr0=.001,
        mosaic=.25, mixup=0., copy_paste=0., fliplr=.5, flipud=0., degrees=3., scale=.25,
        save_period=5, cache="disk", fraction=args.fraction, resume=args.resume, verbose=False)
    weights = output / "ultralytics" / "weights"
    candidates = sorted(weights.glob("epoch*.pt")) + [weights / name for name in ("best.pt", "last.pt")]
    results = []
    device = torch.device("cuda:0")
    for checkpoint in candidates:
        if not checkpoint.is_file(): continue
        validation, values, speed = evaluate(checkpoint, args.variant, dataset, "validation", device)
        results.append({"checkpoint": str(checkpoint), "metrics": validation, "secondsPerCrop": speed,
                        "values": values})
    winner = max(results, key=lambda value: (value["metrics"]["constraintsMet"],
        value["metrics"]["exteriorRecall"], value["metrics"]["exteriorF2"]))
    selected_path = Path(winner["checkpoint"])
    if selected_path.name not in ("best.pt", "last.pt"):
        retained = output / "benchmark" / "selected.pt"
        retained.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(selected_path, retained); winner["checkpoint"] = str(retained)
    validation, source_validation_speed = evaluate_sources(winner["checkpoint"], args.variant,
        root, trainable, "validation", device)
    test, test_speed = evaluate_sources(winner["checkpoint"], args.variant,
        root, trainable, "test", device)
    sealed, sealed_speed = evaluate_sources(winner["checkpoint"], args.variant,
        root, holdout, "holdout", device)
    baseline = evaluate_v1_on_samples(root, [value for value in trainable if value["_exportSplit"] == "validation"], device)
    review = output / "benchmark" / "review"
    save_review(dataset, winner["values"], winner["metrics"]["threshold"], review)
    result = {"schemaVersion": 1, "benchmarkOnly": True, "architecture": args.variant,
        "selectedCheckpoint": winner["checkpoint"], "validation": validation,
        "cropValidation": winner["metrics"],
        "test": test, "sealedHoldout": sealed, "v1Baseline": baseline,
        "inference": {"validationSecondsPerCrop": winner["secondsPerCrop"],
                      "validationSecondsPerSourceFrame": source_validation_speed,
                      "testSecondsPerSourceFrame": test_speed,
                      "holdoutSecondsPerSourceFrame": sealed_speed,
                      "peakVramGiB": torch.cuda.max_memory_allocated(device) / 1024 ** 3},
        "review": str(review)}
    atomic_json(output / "benchmark-result.json", result)
    for checkpoint in weights.glob("epoch*.pt"):
        checkpoint.unlink(missing_ok=True)
    cleanup_legacy_exports(root)
    emit("benchmark", f"{args.variant} benchmark completed", benchmark=str(output / "benchmark-result.json"),
         review=str(review), validation=result["validation"], sealedHoldout=sealed,
         validationRecall=validation["exteriorRecall"],
         validationPrecision=validation["exteriorPrecision"],
         validationRetainedNegativeFalsePositiveRate=validation["negativeFrameFalsePositiveRate"],
         validationThreshold=validation["threshold"])


def self_test():
    import cv2
    import numpy as np
    mask = np.zeros((64, 64), np.uint16); mask[8:24, 10:30] = 1; mask[38:54, 42:58] = 2
    lines, iou = instance_polygons(mask)
    assert len(lines) == 2 and iou >= .98
    alpha = np.zeros((64, 64), np.float32); alpha[12:52, 12:52] = 1
    bounds = square_bounds(mask, alpha); cropped = crop_square(mask, bounds)
    assert cropped.ndim == 2 and cropped.max() == 2
    with tempfile.TemporaryDirectory(prefix="iqp-yolo26-test-") as value:
        root = Path(value); output = root / "run"
        (root / "frames").mkdir(); (root / "masks").mkdir()
        frame = np.full((64, 96, 3), 120, np.uint8)
        prop = np.zeros((64, 96), np.uint8); prop[20:36, 32:48] = 255
        instances = np.zeros_like(prop, np.uint16); instances[20:28, 32:40] = 1
        instances[28:36, 40:48] = 1
        alpha16 = np.zeros_like(prop, np.uint16); alpha16[8:56, 20:72] = 65535
        cv2.imwrite(str(root / "frames" / "positive.png"), frame)
        cv2.imwrite(str(root / "frames" / "negative.png"), frame)
        cv2.imwrite(str(root / "masks" / "prop.png"), prop)
        cv2.imwrite(str(root / "masks" / "empty.png"), np.zeros_like(prop))
        cv2.imwrite(str(root / "masks" / "instances.png"), instances)
        cv2.imwrite(str(root / "masks" / "alpha.png"), alpha16)
        base = {"sourceId": "source-a", "split": "train", "_exportSplit": "train",
                "framePath": "frames/positive.png", "propMaskPath": "masks/prop.png",
                "rvmAlphaPath": "masks/alpha.png", "_recordSha256": "test"}
        positive = dict(base, id="positive", decision="positive",
                        instanceMaskPath="masks/instances.png")
        negative = dict(base, id="negative", decision="negative",
                        framePath="frames/negative.png", propMaskPath="masks/empty.png")
        semantic, _ = prepare_dataset(root, output / "semantic", {}, [positive, negative],
                                      "yolo26s-sem", 64)
        assert read_gray(semantic / "masks/train/positive.png").max() == 1
        assert read_gray(semantic / "masks/train/negative.png").max() == 0
        instance, exported = prepare_dataset(root, output / "instance", {}, [positive, negative],
                                             "yolo26s-seg", 64)
        assert (instance / "labels/train/positive.txt").read_text().strip()
        assert not (instance / "labels/train/negative.txt").read_text().strip()
        assert exported[0]["polygonIoU"] >= .98
    emit("self-test", "YOLO26 benchmark self-test passed")


def smoke_test(args):
    """Exercise the installed Ultralytics/CUDA path without touching user data."""
    import cv2
    import numpy as np
    import torch
    import ultralytics
    from ultralytics import YOLO
    output = Path(args.output); dataset = output / "smoke-dataset"
    if output.exists(): shutil.rmtree(output)
    for split in ("train", "validation", "test", "holdout"):
        for index in range(2):
            stem = f"{split}-{index}"
            image = np.full((128, 128, 3), (55 + index * 30, 90, 135), np.uint8)
            image[35:90, 42:88] = (190, 175, 80)
            target = np.zeros((128, 128), np.uint8)
            if index == 0: target[42:82, 48:82] = 1
            alpha = np.zeros((128, 128), np.uint16); alpha[20:108, 24:104] = 65535
            for folder, value in (("images", image), ("truth", target * 255), ("alpha", alpha)):
                path = dataset / folder / split / f"{stem}.{'jpg' if folder == 'images' else 'png'}"
                path.parent.mkdir(parents=True, exist_ok=True); cv2.imwrite(str(path), value)
            if args.variant == "yolo26s-sem":
                path = dataset / "masks" / split / f"{stem}.png"
                path.parent.mkdir(parents=True, exist_ok=True); cv2.imwrite(str(path), target)
            else:
                path = dataset / "labels" / split / f"{stem}.txt"
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("0 .375 .328125 .640625 .328125 .640625 .640625 .375 .640625\n"
                                if index == 0 else "", encoding="utf-8")
    data = {"path": str(dataset), "train": "images/train", "val": "images/validation",
            "test": "images/test"}
    if args.variant == "yolo26s-sem":
        data.update({"masks_dir": "masks", "names": {"0": "background", "1": "foreground_prop"}})
    else: data["names"] = {"0": "foreground_prop"}
    atomic_json(dataset / "dataset.yaml", data)
    pretrained = output / "pretrained" / (args.variant + ".pt")
    pretrained.parent.mkdir(parents=True, exist_ok=True)
    model = YOLO(str(pretrained))
    model.train(data=str(dataset / "dataset.yaml"), task=VARIANTS[args.variant], epochs=1,
                imgsz=128, batch=2, device=0, project=str(output), name="ultralytics",
                exist_ok=True, workers=0, cache=False, plots=False, verbose=False)
    weights = output / "ultralytics" / "weights"
    if not (weights / "last.pt").is_file() or not torch.cuda.is_available():
        raise RuntimeError("CUDA smoke training did not create its checkpoint")
    result = {"schemaVersion": 1, "benchmarkOnly": True, "smokeTest": True,
              "architecture": args.variant, "ultralyticsVersion": ultralytics.__version__,
              "cuda": torch.cuda.get_device_name(0), "checkpoint": str(weights / "last.pt")}
    atomic_json(output / "benchmark-result.json", result)
    emit("benchmark", f"{args.variant} CUDA smoke test completed",
         benchmark=str(output / "benchmark-result.json"), **result)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset"); parser.add_argument("--output")
    parser.add_argument("--variant", choices=VARIANTS)
    parser.add_argument("--minimum-resolution", type=int, default=0)
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--fraction", type=float, default=1.)
    parser.add_argument("--resume", action="store_true"); parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--smoke-test", action="store_true")
    args = parser.parse_args()
    if args.self_test: self_test(); return
    if args.smoke_test:
        if not args.output or not args.variant: parser.error("output and variant are required for a smoke test")
        smoke_test(args); return
    if not args.dataset or not args.output or not args.variant: parser.error("dataset, output, and variant are required")
    train(args)


if __name__ == "__main__": main()
