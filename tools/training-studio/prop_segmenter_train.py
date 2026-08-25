#!/usr/bin/env python3
"""Train, evaluate, and package QuickPlayer's binary prop segmenter."""
import argparse
import gzip
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

TRAINING_REVISION = 9
CLASSIFICATION_REVISION = 1
CATEGORIES = ("dildo", "butt-plug", "strap-on", "choker", "necklace", "ears",
              "chains", "restraints", "other", "gloves", "mask", "diaper",
              "fluids", "chastity", "unclassified")
TRAINABLE_CATEGORIES = CATEGORIES[:-1]
TARGET_CATEGORIES = {"dildo", "butt-plug"}
SPECIALIZED_ARCHITECTURE = "deeplabv3-resnet50-v1.1-dildo-butt-plug"
CLASSIFICATION_STATES = ("held", "inserted")
STATE_CATEGORIES = {"dildo", "butt-plug"}
HARD_NEGATIVE_WEIGHT = 2.0
HARD_NEGATIVE_FRACTION = .15
MAX_HARD_NEGATIVES = 125
HARD_NEGATIVE_THRESHOLD_OFFSET = .15
HARD_NEGATIVE_MINIMUM_THRESHOLD = .45
HARD_POSITIVE_WEIGHT = 2.0
HARD_POSITIVE_FRACTION = .20
MAX_HARD_POSITIVES = 250
HARD_POSITIVE_MINING_REVISION = 2
HARD_NEGATIVE_MINING_REVISION = 4
EXTERIOR_RECALL_WEIGHT = .35
RETAINED_NEGATIVE_FPR_CEILING = .05
CHECKPOINT_SCORE_TOLERANCE = .015
REV5_MODEL_ID = "prop-r50-20260821-001652-b0430521"
REV5_CHECKPOINT_SHA256 = "b0430521c679194f2789f6188843dade9eefe687ea014154b2e2118d9d4d0325"

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


def format_duration(seconds):
    seconds = max(0, int(seconds)); hours, seconds = divmod(seconds, 3600)
    minutes, seconds = divmod(seconds, 60)
    return f"{hours:d}:{minutes:02d}:{seconds:02d}" if hours else f"{minutes:02d}:{seconds:02d}"


def write_console_progress(message, finish=False):
    width = 118
    sys.stdout.write("\r" + message[:width].ljust(width) + ("\n" if finish else ""))
    sys.stdout.flush()


def atomic_json(path, value):
    path = Path(path); path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp-" + os.urandom(6).hex())
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def atomic_torch_save(torch, value, path):
    path = Path(path); path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp-" + os.urandom(6).hex())
    try:
        torch.save(value, temporary)
        os.replace(temporary, path)
    finally:
        try:
            if temporary.exists(): temporary.unlink()
        except OSError:
            pass


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


def partition_samples(manifest, minimum_resolution):
    """Keep every sealed source out of model selection and expose it only at the end."""
    sealed_sources = {source["id"] for source in manifest.get("sources", [])
                      if source.get("sealedHoldout")}
    accepted = [sample for sample in manifest.get("samples", [])
                if sample.get("decision") in ("positive", "negative") and
                sample.get("framePath") and sample.get("propMaskPath")]
    accepted = resolution_samples(accepted, minimum_resolution)
    available = {split: [sample for sample in accepted
                         if sample.get("split") == split and
                         sample.get("sourceId") not in sealed_sources]
                 for split in ("train", "validation", "test")}
    available["sealedHoldout"] = [sample for sample in accepted
                                  if sample.get("sourceId") in sealed_sources]
    return available, sealed_sources


def latest_v1_package(root, output, required_model_id=None, required_checkpoint_sha256=None):
    candidates = sorted((Path(root) / "runs").glob("*/package/manifest.json"),
                        key=lambda path: path.stat().st_mtime, reverse=True)
    for path in candidates:
        checkpoint = path.parent / "model.pth"
        if Path(output) in path.parents or not checkpoint.is_file(): continue
        try:
            manifest = json.loads(path.read_text(encoding="utf-8"))
            if manifest.get("architecture") != ARCHITECTURE: continue
            if manifest.get("promotionEligible") is False: continue
            if required_model_id and manifest.get("modelId") != required_model_id: continue
            expected = manifest.get("checkpointSha256")
            if expected and digest(checkpoint) != expected: continue
            if required_checkpoint_sha256 and digest(checkpoint) != required_checkpoint_sha256: continue
            return path.parent, manifest
        except (OSError, ValueError, TypeError):
            continue
    return None, None


def mining_dataset_fingerprint(samples):
    rows = [f"{sample.get('id')}:{sample.get('_recordSha256', sample.get('recordSha256', ''))}:"
            f"{sample.get('decision')}" for sample in samples]
    return hashlib.sha256("\n".join(sorted(rows)).encode()).hexdigest()


def mining_cache_path(root, kind, revision, checkpoint_sha256, dataset_fingerprint,
                      input_size):
    name = f"{kind}-r{revision}-{checkpoint_sha256[:16]}-{dataset_fingerprint[:16]}-{input_size}.json"
    return Path(root) / "mining-cache" / name


def load_mining_selection(root, output, kind, revision, checkpoint_sha256,
                          model_id, samples, input_size, snapshot_field=None):
    fingerprint = mining_dataset_fingerprint(samples)
    path = mining_cache_path(root, kind, revision, checkpoint_sha256,
                             fingerprint, input_size)
    if path.is_file():
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
            if (value.get("modelCheckpointSha256") == checkpoint_sha256 and
                    value.get("datasetFingerprint") == fingerprint and
                    value.get("inputSize") == input_size and
                    value.get("algorithmRevision") == revision):
                return set(value.get("sampleIds", [])), "cache", path
        except (OSError, ValueError, TypeError):
            pass
    if snapshot_field:
        snapshots = sorted((Path(root) / "runs").glob("*/dataset-snapshot.json"),
                           key=lambda candidate: candidate.stat().st_mtime, reverse=True)
        for snapshot_path in snapshots:
            if Path(output) in snapshot_path.parents: continue
            try:
                snapshot = json.loads(snapshot_path.read_text(encoding="utf-8"))
                entries = snapshot.get("splits", {}).get("train", [])
                if (snapshot.get("warmStartModelId") != model_id or
                        snapshot.get("warmStartCheckpointSha256") != checkpoint_sha256 or
                        snapshot.get("inputSize") != input_size or
                        mining_dataset_fingerprint(entries) != fingerprint):
                    continue
                selected = {entry["id"] for entry in entries if entry.get(snapshot_field)}
                if selected: return selected, "snapshot", path
            except (OSError, ValueError, TypeError, KeyError):
                continue
    return set(), None, path


def save_mining_selection(path, kind, revision, checkpoint_sha256, model_id,
                          samples, input_size, selected, **metadata):
    atomic_json(path, {"schemaVersion": 1, "kind": kind,
        "algorithmRevision": revision, "modelId": model_id,
        "modelCheckpointSha256": checkpoint_sha256,
        "datasetFingerprint": mining_dataset_fingerprint(samples),
        "inputSize": input_size, "sampleIds": sorted(selected), **metadata})


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


def read_classification_ids(path, width, height):
    import numpy as np
    with gzip.open(path, "rb") as stream:
        raw = stream.read()
    expected = width * height * 4
    if len(raw) != expected:
        raise RuntimeError(f"Classification mask has {len(raw):,} bytes; expected {expected:,}: {path}")
    return np.frombuffer(raw, dtype="<i4").reshape(height, width).copy()


def annotate_classifications(root, output, available, input_size):
    """Validate every positive classification and attach object-aware training metadata."""
    import numpy as np
    from PIL import Image
    samples = [sample for values in available.values() for sample in values]
    positives = [sample for sample in samples if sample.get("decision") == "positive"]
    category_objects = {name: 0 for name in CATEGORIES}
    category_pixels = {name: 0 for name in CATEGORIES}
    state_objects = {name: 0 for name in CLASSIFICATION_STATES}
    errors = []
    for index, sample in enumerate(positives, 1):
        current_errors = []
        classification_path = sample.get("classificationPath")
        classification_mask_path = sample.get("classificationMaskPath")
        if not classification_path or not classification_mask_path or not sample.get("classifiedUtc"):
            errors.append({"sampleId": sample["id"], "errors": ["classification is incomplete"]})
            continue
        annotation_path = Path(root) / classification_path
        ids_path = Path(root) / classification_mask_path
        try:
            annotation = json.loads(annotation_path.read_text(encoding="utf-8-sig"))
            if annotation.get("schemaVersion") != 1:
                raise RuntimeError("unsupported classification schema")
            width, height = int(sample["width"]), int(sample["height"])
            ids = read_classification_ids(ids_path, width, height)
            prop = np.asarray(Image.open(Path(root) / sample["propMaskPath"]).convert("L"),
                              dtype=np.uint8) >= 128
            if ids.shape != prop.shape: current_errors.append("classification dimensions differ from prop mask")
            else:
                if np.any((ids > 0) & ~prop): current_errors.append("classified pixels outside prop mask")
                if np.any(prop & (ids == 0)): current_errors.append("unassigned prop pixels")
            objects = annotation.get("objects", [])
            by_id = {int(value.get("id", 0)): value for value in objects}
            used = {int(value) for value in np.unique(ids) if value > 0}
            if 0 in by_id or len(by_id) != len(objects): current_errors.append("invalid or duplicate object IDs")
            if used - set(by_id): current_errors.append("classification mask references unknown objects")
            focus_objects = []
            categories, states = set(), set()
            for object_id in sorted(used & set(by_id)):
                value = by_id[object_id]; category = value.get("category")
                object_states = tuple(dict.fromkeys(value.get("states") or []))
                if category not in CATEGORIES:
                    current_errors.append(f"object {object_id} has unknown category {category!r}"); continue
                if any(state not in CLASSIFICATION_STATES for state in object_states):
                    current_errors.append(f"object {object_id} has an unknown state")
                if object_states and category not in STATE_CATEGORIES:
                    current_errors.append(f"object {object_id} has states but category is {category}")
                region = ids == object_id; ys, xs = np.where(region); pixels = int(region.sum())
                if not pixels: continue
                bounds = [int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1]
                fraction = pixels * min(input_size / width, input_size / height) ** 2 / input_size ** 2
                focus_objects.append({"id": object_id, "category": category,
                    "states": list(object_states), "bounds": bounds, "pixels": pixels,
                    "fractionAtInput": fraction})
                categories.add(category); states.update(object_states)
                category_objects[category] += 1; category_pixels[category] += pixels
                for state in object_states:
                    if state in state_objects: state_objects[state] += 1
            sample["_classificationObjects"] = focus_objects
            sample["_classificationCategories"] = sorted(categories)
            sample["_classificationStates"] = sorted(states)
            sample["_classificationRevision"] = CLASSIFICATION_REVISION
            sample["_classificationArtifacts"] = {
                "classificationPath": {"path": str(classification_path).replace("\\", "/"),
                    "size": annotation_path.stat().st_size, "sha256": digest(annotation_path)},
                "classificationMaskPath": {"path": str(classification_mask_path).replace("\\", "/"),
                    "size": ids_path.stat().st_size, "sha256": digest(ids_path)}}
        except Exception as error:
            current_errors.append(str(error))
        if current_errors: errors.append({"sampleId": sample["id"], "errors": current_errors})
        if index % 100 == 0:
            emit("classification-audit", f"Audited {index:,}/{len(positives):,} classified images",
                 completed=index, total=len(positives))
    if errors:
        atomic_json(Path(output) / "classification-audit-errors.json", {
            "schemaVersion": 1, "trainingRevision": TRAINING_REVISION, "errors": errors})
        raise RuntimeError(f"Classification audit failed for {len(errors):,} images")
    training_category_objects = {name: 0 for name in CATEGORIES}
    for sample in available.get("train", []):
        for value in sample.get("_classificationObjects", []):
            training_category_objects[value["category"]] += 1
    counts = [training_category_objects[name] for name in TRAINABLE_CATEGORIES
              if training_category_objects[name]]
    median = sorted(counts)[len(counts) // 2] if counts else 1
    category_weights = {name: min(2.5, max(.75, math.sqrt(
                            median / max(1, training_category_objects[name]))))
                        for name in TRAINABLE_CATEGORIES}
    for sample in samples:
        if sample.get("decision") != "positive":
            sample["_classificationObjects"] = []; sample["_classificationCategories"] = []
            sample["_classificationStates"] = []; sample["_categoryBalanceWeight"] = 1.; continue
        represented = [name for name in sample.get("_classificationCategories", [])
                       if name in category_weights]
        sample["_categoryBalanceWeight"] = max(
            (category_weights[name] for name in represented), default=1.)
    summary = {"revision": CLASSIFICATION_REVISION, "complete": True,
        "images": len(positives), "objects": sum(category_objects.values()),
        "categoryObjects": category_objects, "categoryPixels": category_pixels,
        "stateObjects": state_objects, "categorySamplingWeights": category_weights,
        "objectFocusedCropProbability": .30}
    emit("classification-audit",
         f"Validated {len(positives):,} images and {summary['objects']:,} classified objects",
         **summary)
    return summary


def materialize_specialized_targets(root, output, available, input_size):
    """Build deterministic dildo/butt-plug targets and ignore unclassified pixels."""
    import numpy as np
    from PIL import Image
    destination = Path(output) / "target-masks"
    destination.mkdir(parents=True, exist_ok=True)
    samples = [sample for values in available.values() for sample in values]
    target_objects = {name: 0 for name in sorted(TARGET_CATEGORIES)}
    target_pixels = {name: 0 for name in sorted(TARGET_CATEGORIES)}
    target_frames = {name: 0 for name in sorted(TARGET_CATEGORIES)}
    split_counts = {}
    ignored_pixels = 0
    for split, split_samples in available.items():
        counts = {"positive": 0, "negative": 0, "categoryHardNegative": 0}
        for sample in split_samples:
            source_decision = sample.get("decision")
            sample["_sourceDecision"] = source_decision
            sample["_sourceMaskFraction"] = float(sample.get("_maskFraction", 0.))
            if source_decision != "positive":
                sample["_maskFraction"] = 0.
                sample["_positivePixels"] = 0
                sample["_categoryBalanceWeight"] = 1.
                counts["negative"] += 1
                continue
            ids = read_classification_ids(Path(root) / sample["classificationMaskPath"],
                                          int(sample["width"]), int(sample["height"]))
            objects = sample.get("_classificationObjects", [])
            selected_ids = [value["id"] for value in objects
                            if value["category"] in TARGET_CATEGORIES]
            ignored_ids = [value["id"] for value in objects
                           if value["category"] == "unclassified"]
            target = np.isin(ids, selected_ids) if selected_ids else np.zeros(ids.shape, dtype=bool)
            ignored = np.isin(ids, ignored_ids) if ignored_ids else np.zeros(ids.shape, dtype=bool)
            encoded = np.zeros(ids.shape, dtype=np.uint8)
            encoded[ignored] = 128
            encoded[target] = 255
            filename = hashlib.sha256(str(sample["id"]).encode("utf-8")).hexdigest() + ".png"
            path = destination / filename
            temporary = path.with_name(path.name + ".tmp-" + os.urandom(6).hex())
            try:
                Image.fromarray(encoded).save(temporary, format="PNG", compress_level=6)
                os.replace(temporary, path)
            finally:
                try:
                    if temporary.exists(): temporary.unlink()
                except OSError:
                    pass
            target_count = int(target.sum())
            scale = min(input_size / ids.shape[1], input_size / ids.shape[0])
            sample["_targetMaskPath"] = str(path)
            sample["_positivePixels"] = target_count
            sample["_maskFraction"] = target_count * scale * scale / (input_size * input_size)
            sample["decision"] = "positive" if target_count else "negative"
            counts[sample["decision"]] += 1
            if not target_count: counts["categoryHardNegative"] += 1
            ignored_pixels += int(ignored.sum())
            represented = set()
            for value in objects:
                category = value["category"]
                if category not in TARGET_CATEGORIES: continue
                object_pixels = int((ids == value["id"]).sum())
                target_objects[category] += 1
                target_pixels[category] += object_pixels
                represented.add(category)
            for category in represented: target_frames[category] += 1
        split_counts[split] = counts
    training_counts = {name: sum(name in sample.get("_classificationCategories", [])
                                 for sample in available.get("train", []))
                       for name in TARGET_CATEGORIES}
    largest = max(training_counts.values(), default=1)
    category_weights = {name: min(3., max(.75, math.sqrt(largest / max(1, count))))
                        for name, count in training_counts.items()}
    for sample in samples:
        represented = [name for name in sample.get("_classificationCategories", [])
                       if name in TARGET_CATEGORIES]
        sample["_categoryBalanceWeight"] = max(
            (category_weights[name] for name in represented), default=1.)
    summary = {"mode": "binary-specialized", "foregroundCategories": sorted(TARGET_CATEGORIES),
               "ignoredCategories": ["unclassified"], "backgroundCategories":
               [name for name in TRAINABLE_CATEGORIES if name not in TARGET_CATEGORIES],
               "targetObjects": target_objects, "targetPixels": target_pixels,
               "targetFrames": target_frames, "trainingCategorySamplingWeights": category_weights,
               "ignoredPixels": ignored_pixels, "splitCounts": split_counts}
    emit("target-preparation", "Prepared dildo/butt-plug foreground targets; unclassified pixels ignored",
         **summary)
    return summary


def mine_hard_negatives(torch, DataLoader, root, output, samples, input_size, batch, device,
                        prior_package=None, prior_manifest=None, progress_callback=None):
    """Rank source-diverse harmful and sub-threshold RVM-adjacent negative responses."""
    import cv2
    import numpy as np
    if prior_package is None:
        prior_package, prior_manifest = latest_v1_package(root, output)
    negatives = [sample for sample in samples if sample.get("decision") == "negative"]
    if prior_package is None or not negatives: return 0, None, None, set()
    deployed_threshold = float(prior_manifest.get("confidenceThreshold", .5))
    threshold = max(HARD_NEGATIVE_MINIMUM_THRESHOLD,
                    deployed_threshold - HARD_NEGATIVE_THRESHOLD_OFFSET)
    model = build_model(torch, pretrained=False).to(device)
    model.load_state_dict(torch.load(Path(prior_package) / "model.pth", map_location=device,
                                     weights_only=True))
    loader = DataLoader(PropDataset(root, negatives, False, input_size), batch_size=batch,
                        shuffle=False, num_workers=0)
    lookup = {sample["id"]: sample for sample in negatives}
    per_source = {}
    model.eval()
    bf16 = device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 8
    with torch.inference_mode():
        for batch_index, (images, targets, sample_ids) in enumerate(loader):
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=bf16):
                probabilities = model(images.to(device))["out"].float().sigmoid().cpu()
            for index, sample_id in enumerate(sample_ids):
                sample = lookup[str(sample_id)]
                probability = probabilities[index, 0].numpy()
                valid = targets[index, 0].numpy() >= 0
                prediction = (probability >= threshold) & valid
                retained = prediction
                person = None
                if sample.get("rvmPersonMaskPath"):
                    try:
                        person = load_evaluation_mask(root, sample["rvmPersonMaskPath"], input_size)
                        retained, _, _ = filter_components(prediction, person, 24)
                        retained &= ~person
                    except (FileNotFoundError, OSError, ValueError):
                        person = None
                pixels = int(retained.sum())
                if person is not None:
                    radius = max(1, round(24 * min(person.shape) / 512))
                    kernel = np.ones((radius * 2 + 1, radius * 2 + 1), np.uint8)
                    adjacent = cv2.dilate(person.astype(np.uint8), kernel) != 0
                    candidates = probability[adjacent & ~person & valid]
                else:
                    candidates = probability[valid]
                if candidates.size:
                    top_count = min(128, candidates.size)
                    top = np.partition(candidates, candidates.size - top_count)[-top_count:]
                    near_miss_score = float(candidates.max()) + .25 * float(top.mean())
                else:
                    near_miss_score = 0.
                harmful_score = .35 * min(1., pixels / 256) + \
                    (.15 * float(probability[retained].mean()) if pixels else 0.)
                value = (near_miss_score + harmful_score, pixels, str(sample_id))
                source = sample.get("sourceId", sample["id"])
                previous = per_source.get(source)
                if previous is None or value > previous: per_source[source] = value
            if progress_callback is not None:
                progress_callback(batch_index + 1, len(loader))
    limit = min(MAX_HARD_NEGATIVES, max(40, round(len(negatives) * HARD_NEGATIVE_FRACTION)))
    selected = {sample_id for _, _, sample_id in
                sorted(per_source.values(), reverse=True)[:limit]}
    for sample_id in selected: lookup[sample_id]["_hardNegative"] = True
    del model
    if device.type == "cuda": torch.cuda.empty_cache()
    return len(selected), prior_manifest.get("modelId"), threshold, selected


def mine_hard_positives(torch, DataLoader, root, samples, input_size, batch, device,
                        prior_package, prior_manifest, progress_callback=None):
    """Promote low-recall positives, deduplicated by source video."""
    import numpy as np
    positives = [sample for sample in samples if sample.get("decision") == "positive"]
    if prior_package is None or not positives: return 0
    threshold = float(prior_manifest.get("confidenceThreshold", .5))
    model = build_model(torch, pretrained=False).to(device)
    model.load_state_dict(torch.load(Path(prior_package) / "model.pth", map_location=device,
                                     weights_only=True))
    loader = DataLoader(PropDataset(root, positives, False, input_size), batch_size=batch,
                        shuffle=False, num_workers=0)
    lookup = {sample["id"]: sample for sample in positives}
    per_source = {}
    model.eval()
    bf16 = device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 8
    with torch.inference_mode():
        for batch_index, (images, targets, sample_ids) in enumerate(loader):
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=bf16):
                probabilities = model(images.to(device))["out"].float().sigmoid().cpu().numpy()
            truths = targets.numpy() >= .5
            for index, sample_id in enumerate(sample_ids):
                sample = lookup[str(sample_id)]
                truth = truths[index, 0]
                needed = truth
                person = None
                if sample.get("rvmPersonMaskPath"):
                    try:
                        person = load_evaluation_mask(root, sample["rvmPersonMaskPath"], input_size)
                        needed = truth & ~person
                    except (FileNotFoundError, OSError, ValueError):
                        person = None
                needed_pixels = int(needed.sum())
                if not needed_pixels: continue
                prediction = probabilities[index, 0] >= threshold
                if person is not None:
                    prediction, _, _ = filter_components(prediction, person, 24)
                    prediction &= ~person
                recall = int((prediction & needed).sum()) / needed_pixels
                mean_probability = float(probabilities[index, 0][needed].mean())
                size_bonus = 1.25 if needed_pixels / needed.size <= .005 else 1.
                score = size_bonus * ((1 - recall) + .25 * (1 - mean_probability))
                source = sample.get("sourceId", sample["id"])
                previous = per_source.get(source)
                value = (score, -recall, str(sample_id))
                if previous is None or value > previous: per_source[source] = value
            if progress_callback is not None:
                progress_callback(batch_index + 1, len(loader))
    limit = min(MAX_HARD_POSITIVES, max(60, round(len(positives) * HARD_POSITIVE_FRACTION)))
    selected = {sample_id for _, _, sample_id in
                sorted(per_source.values(), reverse=True)[:limit]}
    for sample_id in selected: lookup[sample_id]["_hardPositive"] = True
    del model
    if device.type == "cuda": torch.cuda.empty_cache()
    return len(selected), selected


def split_profile(samples):
    positives = [sample for sample in samples if sample.get("decision") == "positive"]
    negatives = [sample for sample in samples if sample.get("decision") == "negative"]
    buckets = {name: 0 for name in ("under-0.5pct", "0.5-2pct", "2-5pct", "over-5pct")}
    for sample in positives:
        buckets[mask_size_bucket(float(sample.get("_maskFraction", 0.)))] += 1
    category_counts = {name: sum(name in sample.get("_classificationCategories", [])
                                 for sample in positives) for name in CATEGORIES}
    state_counts = {name: sum(name in sample.get("_classificationStates", [])
                              for sample in positives) for name in CLASSIFICATION_STATES}
    return {"count": len(samples), "sourceCount": len({sample.get("sourceId") for sample in samples}),
            "positiveCount": len(positives), "negativeCount": len(negatives),
            "rvmPersonMaskCount": sum(bool(sample.get("rvmPersonMaskPath")) for sample in samples),
            "negativeRatio": len(negatives) / max(1, len(samples)), "positiveSizeBuckets": buckets,
            "classifiedPositiveFramesByCategory": category_counts,
            "classifiedPositiveFramesByState": state_counts}


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
            hard = HARD_NEGATIVE_WEIGHT if sample.get("_hardNegative") else \
                HARD_POSITIVE_WEIGHT if sample.get("_hardPositive") else \
                2. if sample.get("feedbackPriority") or sample.get("burstId") else 1.
            fraction = float(sample.get("_maskFraction", 0.))
            size_weight = 2. if decision == "positive" and fraction <= .005 else \
                1.35 if decision == "positive" and fraction <= .02 else 1.
            category_weight = float(sample.get("_categoryBalanceWeight", 1.))
            weights[index] = hard * size_weight * category_weight / \
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
        self.geometry_mode = "balanced"

    def __len__(self):
        return len(self.samples)

    def __getitem__(self, index):
        import numpy as np
        import torch
        from PIL import Image, ImageEnhance, ImageFilter
        sample = self.samples[index]
        image = Image.open(self.root / sample["framePath"]).convert("RGB")
        mask_path = Path(sample.get("_targetMaskPath", sample["propMaskPath"]))
        if not mask_path.is_absolute(): mask_path = self.root / mask_path
        encoded_mask = Image.open(mask_path).convert("L")
        target_mask = encoded_mask.point(lambda value: 255 if value >= 192 else 0)
        ignore_mask = encoded_mask.point(lambda value: 255 if 64 <= value < 192 else 0)
        if self.training:
            focus_candidates = [value for value in sample.get("_classificationObjects", [])
                                if value.get("category") in TARGET_CATEGORIES]
            focus = random.choice(focus_candidates) if focus_candidates else None
            focus_bounds = tuple(focus["bounds"]) if focus else None
            focus_fraction = float(focus.get("fractionAtInput", sample.get("_maskFraction", 0.))) \
                if focus else float(sample.get("_maskFraction", 0.))
            person = Image.new("L", target_mask.size, 0)
            person_available = False
            person_path = sample.get("rvmPersonMaskPath")
            if person_path:
                try:
                    person = Image.open(self.root / person_path).convert("L")
                    if person.size != target_mask.size:
                        person = person.resize(target_mask.size, Image.Resampling.NEAREST)
                    person_available = True
                except (FileNotFoundError, OSError, ValueError):
                    person = Image.new("L", target_mask.size, 0)
            availability = Image.new("L", target_mask.size, 255 if person_available else 0)
            mask = Image.merge("RGBA", (target_mask, person, availability, ignore_mask))
            if random.random() < .5:
                if focus_bounds:
                    left, top, right, bottom = focus_bounds
                    focus_bounds = (image.width - right, top, image.width - left, bottom)
                image = image.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
                mask = mask.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
            image = augment_appearance(image, np)
            geometry = random.random()
            letterbox_limit, focus_limit = ((.80, .90)
                if self.geometry_mode == "runtime" else (.50, .80))
            if geometry < letterbox_limit:
                image, mask = letterbox_pair(image, mask, self.input_size)
            else:
                native_foreground = focus_bounds or prop_mask_bounds(mask)
                if native_foreground and geometry < focus_limit and \
                        (focus_bounds is not None or float(sample.get("_maskFraction", 0.)) <= .02):
                    image, mask = focused_crop_pair(image, mask, self.input_size,
                        focus_fraction, focus_bounds)
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
                    canvas_mask = Image.new(mask.mode, canvas.size, 0)
                    left, top = (canvas.width - image.width) // 2, (canvas.height - image.height) // 2
                    canvas.paste(image, (left, top)); canvas_mask.paste(mask, (left, top))
                    max_x, max_y = canvas.width - self.input_size, canvas.height - self.input_size
                    foreground = prop_mask_bounds(canvas_mask)
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
            canvas_mask.paste(encoded_mask.resize((width, height), Image.Resampling.NEAREST),
                              (left, top))
            mask = canvas_mask
        mask_pixels = np.asarray(mask, dtype=np.uint8)
        if self.training:
            target = (mask_pixels[..., 0] >= 128).astype(np.float32)[None]
            person = mask_pixels[..., 1] >= 128
            person_available = mask_pixels[..., 2] >= 128
            exterior = ((target[0] >= .5) & ~person & person_available).astype(np.float32)[None]
            target[mask_pixels[..., 3][None] >= 128] = -1.
            return (torch.from_numpy(pixels), torch.from_numpy(target),
                    torch.from_numpy(exterior), sample["id"])
        target = (mask_pixels >= 192).astype(np.float32)[None]
        target[((mask_pixels >= 64) & (mask_pixels < 192))[None]] = -1.
        return torch.from_numpy(pixels), torch.from_numpy(target), sample["id"]


def letterbox_pair(image, mask, input_size):
    from PIL import Image
    scale = min(input_size / image.width, input_size / image.height)
    size = (max(1, round(image.width * scale)), max(1, round(image.height * scale)))
    left, top = (input_size - size[0]) // 2, (input_size - size[1]) // 2
    fill = tuple(round(value * 255) for value in MEAN)
    canvas = Image.new("RGB", (input_size, input_size), fill)
    canvas_mask = Image.new(mask.mode, (input_size, input_size), 0)
    canvas.paste(image.resize(size, Image.Resampling.BILINEAR), (left, top))
    canvas_mask.paste(mask.resize(size, Image.Resampling.NEAREST), (left, top))
    return canvas, canvas_mask


def focused_crop_pair(image, mask, input_size, mask_fraction, bounds=None):
    """Zoom a small prop to a useful training scale while retaining context."""
    from PIL import Image
    bounds = bounds or prop_mask_bounds(mask)
    if not bounds: return letterbox_pair(image, mask, input_size)
    width, height = bounds[2] - bounds[0], bounds[3] - bounds[1]
    target_span = random.uniform(.14, .28) if mask_fraction <= .005 else random.uniform(.18, .36)
    side = max(width, height, math.ceil(max(width, height) / target_span))
    side = max(1, min(side, min(image.size)))
    centre_x = (bounds[0] + bounds[2]) / 2
    centre_y = (bounds[1] + bounds[3]) / 2
    free_x, free_y = max(0, side - width), max(0, side - height)
    centre_x += random.uniform(-.15 * free_x, .15 * free_x)
    centre_y += random.uniform(-.15 * free_y, .15 * free_y)
    left = max(0, min(image.width - side, round(centre_x - side / 2)))
    top = max(0, min(image.height - side, round(centre_y - side / 2)))
    box = (left, top, left + side, top + side)
    return (image.crop(box).resize((input_size, input_size), Image.Resampling.BILINEAR),
            mask.crop(box).resize((input_size, input_size), Image.Resampling.NEAREST))


def prop_mask_bounds(mask):
    channel = mask.getchannel("R") if mask.mode in ("RGB", "RGBA") else mask
    return channel.point(lambda value: 255 if value >= 192 else 0).getbbox()


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


def dice_loss(logits, target, valid=None):
    valid = target.new_ones(target.shape) if valid is None else valid
    probability = logits.sigmoid()
    numerator = 2 * (probability * target * valid).sum(dim=(1, 2, 3)) + 1
    denominator = (probability * valid).sum(dim=(1, 2, 3)) + \
        (target * valid).sum(dim=(1, 2, 3)) + 1
    return (1 - numerator / denominator).mean()


def tversky_loss(logits, target, alpha=.5, beta=.5, valid=None):
    valid = target.new_ones(target.shape) if valid is None else valid
    probability = logits.sigmoid()
    true_positive = (probability * target * valid).sum(dim=(1, 2, 3))
    false_positive = (probability * (1 - target) * valid).sum(dim=(1, 2, 3))
    false_negative = ((1 - probability) * target * valid).sum(dim=(1, 2, 3))
    return (1 - (true_positive + 1) /
            (true_positive + alpha * false_positive + beta * false_negative + 1)).mean()


def focal_bce(torch, logits, target, pos_weight, gamma=2., valid=None):
    valid = torch.ones_like(target) if valid is None else valid
    loss = torch.nn.functional.binary_cross_entropy_with_logits(
        logits, target, pos_weight=pos_weight, reduction="none")
    probability = logits.sigmoid()
    correct_probability = probability * target + (1 - probability) * (1 - target)
    weighted = (1 - correct_probability).pow(gamma) * loss * valid
    return weighted.sum() / valid.sum().clamp_min(1)


def boundary_loss(torch, logits, target, valid=None):
    import torch.nn.functional as functional
    valid = torch.ones_like(target) if valid is None else valid
    probability = logits.sigmoid() * valid
    target = target * valid
    predicted = functional.max_pool2d(probability, 3, 1, 1) - \
        (-functional.max_pool2d(-probability, 3, 1, 1))
    truth = functional.max_pool2d(target, 3, 1, 1) - \
        (-functional.max_pool2d(-target, 3, 1, 1))
    numerator = 2 * (predicted * truth * valid).sum(dim=(1, 2, 3)) + 1
    denominator = (predicted * valid).sum(dim=(1, 2, 3)) + \
        (truth * valid).sum(dim=(1, 2, 3)) + 1
    return (1 - numerator / denominator).mean()


def exterior_recall_loss(logits, exterior_target):
    probability = logits.sigmoid()
    positive = exterior_target.sum(dim=(1, 2, 3))
    detected = (probability * exterior_target).sum(dim=(1, 2, 3))
    available = positive > 0
    if not available.any():
        return logits.sum() * 0
    return (1 - (detected[available] + 1) / (positive[available] + 1)).mean()


def segmentation_loss(torch, output, target, pos_weight, exterior_target=None):
    valid = (target >= 0).to(target.dtype)
    target = target.clamp(0, 1)
    total = (focal_bce(torch, output["out"], target, pos_weight, valid=valid) +
             dice_loss(output["out"], target, valid) +
             .5 * tversky_loss(output["out"], target, valid=valid) +
             .2 * boundary_loss(torch, output["out"], target, valid))
    if exterior_target is not None:
        total += EXTERIOR_RECALL_WEIGHT * exterior_recall_loss(
            output["out"], exterior_target)
    if "aux" in output:
        aux = (focal_bce(torch, output["aux"], target, pos_weight, valid=valid) +
               dice_loss(output["aux"], target, valid) +
               .5 * tversky_loss(output["aux"], target, valid=valid) +
               .1 * boundary_loss(torch, output["aux"], target, valid))
        if exterior_target is not None:
            aux += .5 * EXTERIOR_RECALL_WEIGHT * exterior_recall_loss(
                output["aux"], exterior_target)
        total += .4 * aux
    return total


def confusion(probability, target, threshold, valid=None):
    prediction = probability >= threshold; truth = target >= .5
    valid = target >= 0 if valid is None else valid
    return (int((prediction & truth & valid).sum()), int((prediction & ~truth & valid).sum()),
            int((~prediction & truth & valid).sum()), int((~prediction & ~truth & valid).sum()))


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
    retained_fpr = downstream.get("retainedNegativeFrameFalsePositiveRate", 1.) \
        if downstream.get("retainedNegativeCoverage", 0.) >= .8 \
        else value["negativeFrameFalsePositiveRate"]
    retained_negative_score = 1 - retained_fpr
    return (.5 * raw + .3 * exterior["dice"] + .1 * exterior["precision"] +
            .1 * retained_negative_score)


def retained_negative_fpr(value):
    downstream = value.get("rvmUnion", {})
    if downstream.get("retainedNegativeCoverage", 0.) >= .8:
        return downstream.get("retainedNegativeFrameFalsePositiveRate", 1.)
    return value.get("negativeFrameFalsePositiveRate", 1.)


def promotion_result(candidate, baseline):
    candidate_exterior = candidate.get("rvmUnion", {}).get("exteriorProp", {})
    baseline_exterior = baseline.get("rvmUnion", {}).get("exteriorProp", {})
    result = {
        "recallGain": candidate_exterior.get("recall", 0.) -
            baseline_exterior.get("recall", 0.),
        "diceGain": candidate_exterior.get("dice", 0.) -
            baseline_exterior.get("dice", 0.),
        "retainedNegativeFalsePositiveRate": retained_negative_fpr(candidate),
        "rvmUnionDiceGain": candidate.get("rvmUnion", {}).get(
            "finalForeground", {}).get("dice", 0.) - baseline.get(
            "rvmUnion", {}).get("finalForeground", {}).get("dice", 0.)}
    result["eligible"] = (result["recallGain"] >= .05 and
        result["retainedNegativeFalsePositiveRate"] <= RETAINED_NEGATIVE_FPR_CEILING and
        result["rvmUnionDiceGain"] > 0)
    return result


def deployment_threshold(values, scorer=selection_score):
    eligible = [threshold for threshold, value in values.items()
                if retained_negative_fpr(value) <= RETAINED_NEGATIVE_FPR_CEILING]
    pool = eligible or list(values)
    threshold = max(pool, key=lambda current: scorer(values[current]))
    return threshold, bool(eligible)


def deployment_checkpoint(options):
    eligible = [option for option in options
                if retained_negative_fpr(option["validation"]) <=
                RETAINED_NEGATIVE_FPR_CEILING]
    pool = eligible or list(options)
    best_score = max(option["selectionScore"] for option in pool)
    near_best = [option for option in pool if option["selectionScore"] >=
                 best_score - CHECKPOINT_SCORE_TOLERANCE]
    selected = min(near_best, key=lambda option:
                   (retained_negative_fpr(option["validation"]),
                    -option["selectionScore"]))
    return selected, bool(eligible)


def conservative_score(value):
    downstream = value.get("rvmUnion", {})
    if downstream.get("coverage", 0.) < .8:
        return .7 * value["precision"] + .3 * value["dice"]
    exterior = downstream["exteriorProp"]
    retained_fpr = downstream.get("retainedNegativeFrameFalsePositiveRate", 1.) \
        if downstream.get("retainedNegativeCoverage", 0.) >= .8 \
        else value["negativeFrameFalsePositiveRate"]
    clean_negatives = 1 - retained_fpr
    return (.45 * exterior["dice"] + .25 * exterior["precision"] +
            .2 * clean_negatives + .1 * value["smallObjectRecall"])


def checkpoint_objectives(values):
    scorers = {
        "balanced": lambda value: value["selectionScore"],
        "raw-dice": lambda value: value["dice"],
        "exterior-prop": lambda value: value.get("rvmUnion", {}).get(
            "exteriorProp", {}).get("dice", value["dice"]),
        "conservative": conservative_score}
    result = {}
    for name, scorer in scorers.items():
        threshold = max(values, key=lambda current: scorer(values[current]))
        result[name] = {"threshold": threshold, "score": scorer(values[threshold]),
                        "validation": values[threshold]}
    return result


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


def load_classification_regions(root, sample, input_size):
    """Letterbox precise classified object pixels for category/state recall reporting."""
    import numpy as np
    from PIL import Image
    objects = sample.get("_classificationObjects", [])
    relative = sample.get("classificationMaskPath")
    if not objects or not relative: return {"categories": {}, "states": {}}
    ids = read_classification_ids(Path(root) / relative,
        int(sample["width"]), int(sample["height"]))
    scale = min(input_size / ids.shape[1], input_size / ids.shape[0])
    size = (max(1, round(ids.shape[1] * scale)), max(1, round(ids.shape[0] * scale)))
    left, top = (input_size - size[0]) // 2, (input_size - size[1]) // 2
    grouped = {"categories": {}, "states": {}}
    for kind, names in (("categories", CATEGORIES), ("states", CLASSIFICATION_STATES)):
        for name in names:
            object_ids = [value["id"] for value in objects if
                (value["category"] == name if kind == "categories" else name in value["states"])]
            if not object_ids: continue
            native = np.isin(ids, object_ids).astype(np.uint8) * 255
            canvas = Image.new("L", (input_size, input_size), 0)
            canvas.paste(Image.fromarray(native).resize(size, Image.Resampling.NEAREST), (left, top))
            grouped[kind][name] = np.asarray(canvas, dtype=np.uint8) >= 128
    return grouped


def add_confusion(destination, value):
    for index, current in enumerate(value): destination[index] += current


def mask_size_bucket(fraction):
    if fraction <= .005: return "under-0.5pct"
    if fraction <= .02: return "0.5-2pct"
    if fraction <= .05: return "2-5pct"
    return "over-5pct"


def evaluate(torch, model, loader, device, thresholds=(.5,), root=None, samples=None,
             progress_callback=None, classification_breakdown=False):
    totals = {threshold: [0, 0, 0, 0] for threshold in thresholds}
    per_image = {threshold: {"dice": [], "recall": [], "smallRecall": [], "boundary": [],
                             "negativeFrames": 0, "negativeFramesWithPrediction": 0,
                             "sourceTotals": {}, "sizeBuckets": {}, "rvmAvailable": 0,
                             "rvmExterior": [0, 0, 0, 0], "rvmUnion": [0, 0, 0, 0],
                             "rvmNegativeFrames": 0, "rvmNegativeFramesWithRetained": 0,
                             "rvmNegativeFractions": [], "classificationCategoryRecall": {},
                             "classificationStateRecall": {}}
                 for threshold in thresholds}
    sample_lookup = {sample["id"]: sample for sample in (samples or [])}
    total_samples = len(sample_lookup)
    model.eval()
    bf16 = device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 8
    with torch.inference_mode():
        for batch_index, (images, target, sample_ids) in enumerate(loader):
            images, target = images.to(device), target.to(device)
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=bf16):
                probability = model(images)["out"].float().sigmoid()
            evaluation_artifacts = []
            for sample_id in sample_ids:
                sample = sample_lookup.get(str(sample_id))
                if root is None or not sample or not sample.get("rvmPersonMaskPath"):
                    evaluation_artifacts.append(None); continue
                try:
                    evaluation_artifacts.append(
                        load_evaluation_mask(root, sample["rvmPersonMaskPath"], images.shape[-1]))
                except (FileNotFoundError, OSError, ValueError):
                    evaluation_artifacts.append(None)
            classification_artifacts = []
            for sample_id in sample_ids:
                sample = sample_lookup.get(str(sample_id))
                if not classification_breakdown or root is None or not sample:
                    classification_artifacts.append(None); continue
                try: classification_artifacts.append(
                    load_classification_regions(root, sample, images.shape[-1]))
                except (FileNotFoundError, OSError, ValueError):
                    classification_artifacts.append(None)
            for threshold in thresholds:
                current = confusion(probability, target, threshold)
                add_confusion(totals[threshold], current)
                for index in range(target.shape[0]):
                    truth = target[index] >= .5
                    valid = target[index] >= 0
                    truth_pixels = int(truth.sum())
                    raw_predicted = probability[index] >= threshold
                    predicted = raw_predicted & valid
                    sample = sample_lookup.get(str(sample_ids[index]))
                    source_id = sample.get("sourceId", sample.get("id")) if sample else str(sample_ids[index])
                    source_totals = per_image[threshold]["sourceTotals"].setdefault(source_id, [0, 0, 0, 0])
                    add_confusion(source_totals, confusion(predicted, truth, .5, valid))
                    if classification_artifacts[index] is not None:
                        predicted_np = raw_predicted[0].detach().cpu().numpy() \
                            if raw_predicted.ndim == 3 else raw_predicted.detach().cpu().numpy()
                        for kind, destination_name in (("categories", "classificationCategoryRecall"),
                                                       ("states", "classificationStateRecall")):
                            destination = per_image[threshold][destination_name]
                            for name, region in classification_artifacts[index][kind].items():
                                current_recall = destination.setdefault(name, [0, 0, 0])
                                current_recall[0] += int((predicted_np & region).sum())
                                current_recall[1] += int(region.sum())
                                current_recall[2] += 1
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
                            person = evaluation_artifacts[index]
                            prediction_np = predicted[0].detach().cpu().numpy() if predicted.ndim == 3 \
                                else predicted.detach().cpu().numpy()
                            truth_np = truth[0].detach().cpu().numpy() if truth.ndim == 3 \
                                else truth.detach().cpu().numpy()
                            valid_np = valid[0].detach().cpu().numpy() if valid.ndim == 3 \
                                else valid.detach().cpu().numpy()
                            retained, _, _ = filter_components(prediction_np, person, 24)
                            exterior = retained & ~person & valid_np
                            needed = truth_np & ~person
                            add_confusion(per_image[threshold]["rvmExterior"],
                                          confusion(exterior, needed, .5, valid_np))
                            add_confusion(per_image[threshold]["rvmUnion"],
                                          confusion(person | retained, person | truth_np, .5, valid_np))
                            per_image[threshold]["rvmAvailable"] += 1
                            if truth_pixels == 0:
                                harmful_pixels = int(exterior.sum())
                                per_image[threshold]["rvmNegativeFrames"] += 1
                                per_image[threshold]["rvmNegativeFramesWithRetained"] += \
                                    int(harmful_pixels > 0)
                                per_image[threshold]["rvmNegativeFractions"].append(
                                    harmful_pixels / exterior.size)
                        except (FileNotFoundError, OSError, ValueError):
                            pass
            if progress_callback is not None:
                progress_callback(batch_index + 1, len(loader))
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
        if classification_breakdown:
            result[threshold]["classificationRecall"] = {
                "byCategory": {name: {"recall": value[0] / max(1, value[1]),
                    "pixels": value[1], "frames": value[2]}
                    for name, value in current["classificationCategoryRecall"].items()},
                "byState": {name: {"recall": value[0] / max(1, value[1]),
                    "pixels": value[1], "frames": value[2]}
                    for name, value in current["classificationStateRecall"].items()}}
        if total_samples:
            fractions = sorted(current["rvmNegativeFractions"])
            percentile95 = fractions[min(len(fractions) - 1,
                math.ceil(.95 * len(fractions)) - 1)] if fractions else 0.
            result[threshold]["rvmUnion"] = {
                "coverage": current["rvmAvailable"] / total_samples,
                "sampleCount": current["rvmAvailable"],
                "exteriorProp": scores(current["rvmExterior"]),
                "finalForeground": scores(current["rvmUnion"]),
                "retainedNegativeFrameCount": current["rvmNegativeFrames"],
                "retainedNegativeCoverage": current["rvmNegativeFrames"] /
                    max(1, current["negativeFrames"]),
                "retainedNegativeFrameFalsePositiveRate":
                    current["rvmNegativeFramesWithRetained"] /
                    max(1, current["rvmNegativeFrames"]),
                "retainedNegativeMeanFalsePositiveFraction":
                    sum(fractions) / max(1, len(fractions)),
                "retainedNegativeP95FalsePositiveFraction": percentile95,
                "retainedNegativeMaxFalsePositiveFraction": fractions[-1] if fractions else 0.}
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
                valid = targets[index, 0].numpy() >= 0
                prediction &= valid
                false_positive = prediction & ~truth & valid
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
                valid = targets[index, 0].numpy() >= 0
                prediction &= valid
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
            artifacts = dict(sample.get("_classificationArtifacts", {}))
            for name in artifact_names:
                relative = sample.get(name)
                if not relative: continue
                path = root / relative
                if not path.is_file(): continue
                stat = path.stat()
                artifacts[name] = {"path": str(relative).replace("\\", "/"),
                                   "size": stat.st_size, "sha256": digest(path)}
            target_path = Path(sample["_targetMaskPath"]) if sample.get("_targetMaskPath") else None
            if target_path and target_path.is_file():
                stat = target_path.stat()
                artifacts["specializedTargetMask"] = {"path": str(target_path),
                    "size": stat.st_size, "sha256": digest(target_path)}
            entries.append({
                "id": sample["id"], "decision": sample["decision"],
                "sourceDecision": sample.get("_sourceDecision", sample["decision"]),
                "sourceId": sample.get("sourceId"), "timestampMs": sample.get("timestampMs"),
                "burstId": sample.get("burstId"),
                "feedbackPriority": bool(sample.get("feedbackPriority")),
                "historicalHardNegative": bool(sample.get("_hardNegative")),
                "historicalHardPositive": bool(sample.get("_hardPositive")),
                "classificationRevision": sample.get("_classificationRevision"),
                "classificationCategories": sample.get("_classificationCategories", []),
                "classificationStates": sample.get("_classificationStates", []),
                "classificationObjects": sample.get("_classificationObjects", []),
                "categoryBalanceWeight": sample.get("_categoryBalanceWeight", 1.),
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
        "schemaVersion": 1, "trainingRevision": TRAINING_REVISION,
        "createdUtc": time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
        "datasetId": manifest["datasetId"], "inputSize": args.input_size,
        "minimumResolution": args.minimum_resolution, "seed": args.seed,
        "classification": getattr(args, "classification_summary", None),
        "warmStartModelId": getattr(args, "warm_start_model_id", None),
        "warmStartCheckpointSha256": getattr(args, "warm_start_checkpoint_sha256", None),
        "splits": snapshot_samples}
    path = output / "dataset-snapshot.json"
    if path.is_file():
        previous = json.loads(path.read_text(encoding="utf-8"))
        comparable_keys = ("schemaVersion", "trainingRevision", "datasetId", "inputSize",
                           "minimumResolution", "seed", "warmStartModelId",
                           "warmStartCheckpointSha256", "classification", "splits")
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
                    selection_name, epoch_size, epoch_negative_count,
                    warm_start_checkpoint=None, warm_start_model_id=None):
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
    model = build_model(torch, pretrained=warm_start_checkpoint is None).to(device)
    if warm_start_checkpoint is not None:
        model.load_state_dict(torch.load(warm_start_checkpoint, map_location=device,
                                         weights_only=True))
        emit("warm-start", f"Initialized {selection_name} from {warm_start_model_id}",
             modelId=warm_start_model_id, checkpoint=str(warm_start_checkpoint))
    pos_weight = torch.tensor([positive_weight(train_samples, sampler_weights)], device=device)
    best_score, stale, start_epoch = -1., 0, 0
    last_path = candidate / "last.pth"
    checkpoint_paths = {
        "balanced": candidate / "best.pth",
        "raw-dice": candidate / "best-raw-dice.pth",
        "exterior-prop": candidate / "best-exterior-prop.pth",
        "conservative": candidate / "best-conservative.pth"}
    best_objectives = {name: -1. for name in checkpoint_paths}
    best_records = {}
    backbone_lr = 3e-6 if warm_start_checkpoint is not None else 1e-5
    head_lr = 3e-5 if warm_start_checkpoint is not None else 1e-4
    optimizer = torch.optim.AdamW([
        {"params": model.backbone.parameters(), "lr": backbone_lr},
        {"params": model.classifier.parameters(), "lr": head_lr},
        {"params": model.aux_classifier.parameters(), "lr": head_lr}], weight_decay=1e-4)
    scaler = torch.amp.GradScaler("cuda", enabled=device.type == "cuda")
    resume_state = None
    if args.resume and last_path.is_file():
        state = torch.load(last_path, map_location=device, weights_only=False)
        resume_state = state
        model.load_state_dict(state["model"]); optimizer.load_state_dict(state["optimizer"])
        scaler.load_state_dict(state["scaler"]); start_epoch = state["epoch"] + 1
        best_score = state.get("bestScore", state.get("bestDice", -1.)); stale = state["stale"]
        best_objectives.update(state.get("bestObjectives", {"balanced": best_score}))
        best_records.update(state.get("bestRecords", {}))
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
    runtime_start = max(args.warmup_epochs, total_epochs - args.runtime_epochs)
    checkpoint_thresholds = tuple(round(value / 100, 2) for value in range(15, 86, 10))
    for epoch in range(start_epoch, total_epochs):
        runtime_phase = epoch >= runtime_start
        if epoch == runtime_start:
            stale = 0
            emit("curriculum", f"Switched to runtime-matched geometry for the final "
                 f"{total_epochs - runtime_start} epochs", epoch=epoch + 1,
                 letterboxFraction=.80)
        loaders["train"].dataset.geometry_mode = "runtime" if runtime_phase else "balanced"
        frozen = epoch < args.warmup_epochs and warm_start_checkpoint is None
        for parameter in model.backbone.parameters(): parameter.requires_grad = not frozen
        if frozen:
            for group in optimizer.param_groups: group["lr"] = 0 if group is optimizer.param_groups[0] else 1e-3
        else:
            progress = (epoch - args.warmup_epochs) / max(1, args.epochs - 1)
            multiplier = .05 + .95 * .5 * (1 + math.cos(math.pi * progress))
            phase_multiplier = .5 if runtime_phase else 1.
            optimizer.param_groups[0]["lr"] = backbone_lr * multiplier * phase_multiplier
            optimizer.param_groups[1]["lr"] = optimizer.param_groups[2]["lr"] = \
                head_lr * multiplier * phase_multiplier
        model.train(); freeze_batch_norm(torch, model)
        optimizer.zero_grad(set_to_none=True); running = 0.
        epoch_started = time.monotonic()
        if args.console_progress:
            write_console_progress(f"Epoch {epoch + 1}/{total_epochs}  "
                f"[------------------------------]   0%  batch 0/{len(loaders['train'])}")
        for step, (images, target, exterior_target, _) in enumerate(loaders["train"]):
            images = images.to(device, non_blocking=True)
            target = target.to(device, non_blocking=True)
            exterior_target = exterior_target.to(device, non_blocking=True)
            with torch.autocast(device_type=device.type, dtype=torch.bfloat16, enabled=device.type == "cuda"):
                loss = segmentation_loss(torch, model(images), target, pos_weight,
                                         exterior_target) / accumulation
            scaler.scale(loss).backward(); running += float(loss.item()) * accumulation
            if (step + 1) % accumulation == 0 or step + 1 == len(loaders["train"]):
                scaler.step(optimizer); scaler.update(); optimizer.zero_grad(set_to_none=True)
                ema.update_parameters(model)
            if args.console_progress:
                completed, total = step + 1, len(loaders["train"])
                fraction = completed / max(1, total); filled = min(30, int(fraction * 30))
                elapsed = time.monotonic() - epoch_started
                remaining = elapsed * (total - completed) / completed
                write_console_progress(f"Epoch {epoch + 1}/{total_epochs}  "
                    f"[{'=' * filled}{'-' * (30 - filled)}] {fraction:4.0%}  "
                    f"batch {completed}/{total}  ETA {format_duration(remaining)}")
        validation_started = time.monotonic()
        def validation_progress(completed, total):
            if not args.console_progress: return
            fraction = completed / max(1, total); filled = min(30, int(fraction * 30))
            elapsed = time.monotonic() - validation_started
            remaining = elapsed * (total - completed) / completed
            write_console_progress(f"Epoch {epoch + 1}/{total_epochs}  validating "
                f"[{'=' * filled}{'-' * (30 - filled)}] {fraction:4.0%}  "
                f"batch {completed}/{total}  ETA {format_duration(remaining)}")
        validation_all = evaluate(torch, ema.module, loaders["validation"], device,
                                  checkpoint_thresholds, root, validation_samples,
                                  validation_progress)
        if args.console_progress:
            write_console_progress(f"Epoch {epoch + 1}/{total_epochs}  "
                "[==============================] 100%  validation complete", finish=True)
        objectives = checkpoint_objectives(validation_all)
        validation_threshold = objectives["balanced"]["threshold"]
        validation = objectives["balanced"]["validation"]
        improved_variants = []
        for name, objective in objectives.items():
            if objective["score"] > best_objectives[name]:
                best_objectives[name] = objective["score"]
                best_records[name] = {"epoch": epoch + 1, "threshold": objective["threshold"],
                                      "objectiveScore": objective["score"],
                                      "validation": objective["validation"]}
                atomic_torch_save(torch, ema.module.state_dict(), checkpoint_paths[name])
                improved_variants.append(name)
        improved = "balanced" in improved_variants
        if improved: best_score = best_objectives["balanced"]
        if improved_variants: stale = 0
        else: stale += 1
        atomic_torch_save(torch, {"model": model.state_dict(), "optimizer": optimizer.state_dict(),
                    "ema": ema.state_dict(), "scaler": scaler.state_dict(), "epoch": epoch,
                    "bestScore": best_score, "bestDice": validation["dice"],
                    "bestObjectives": best_objectives, "bestRecords": best_records,
                    "stale": stale, "samplerGenerator": sampler_generator.get_state(),
                    "pythonRandomState": random.getstate(),
                    "torchRandomState": torch.get_rng_state(),
                    "cudaRandomState": torch.cuda.get_rng_state_all()
                        if device.type == "cuda" else None}, last_path)
        emit("epoch", "runtime-geometry fine-tune" if runtime_phase else
             "backbone frozen" if frozen else "balanced-geometry fine-tune",
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
             validationExteriorPropDice=validation.get("rvmUnion", {}).get(
                "exteriorProp", {}).get("dice"),
             validationRetainedNegativeFalsePositiveRate=validation.get(
                "rvmUnion", {}).get("retainedNegativeFrameFalsePositiveRate"),
             improvedCheckpoints=improved_variants,
             validationThreshold=validation_threshold, best=improved,
             negativeRatio=negative_ratio)
        if runtime_phase and epoch + 1 >= total_epochs and stale >= args.patience:
            emit("early-stopping", f"No checkpoint objective improvement for {stale} epochs")
            break
    if not checkpoint_paths["balanced"].is_file():
        raise RuntimeError(f"No checkpoint was produced for {negative_ratio:.0%} negatives")
    thresholds = tuple(round(value / 100, 2) for value in range(10, 91, 5))
    swept = []
    for name, path in checkpoint_paths.items():
        if not path.is_file(): continue
        model.load_state_dict(torch.load(path, map_location=device, weights_only=True))
        validation_all = evaluate(torch, model, loaders["validation"], device, thresholds,
                                  root, validation_samples)
        threshold, ceiling_met = deployment_threshold(validation_all)
        swept.append({"name": name, "threshold": threshold,
                      "selectionScore": validation_all[threshold]["selectionScore"],
                      "validation": validation_all[threshold], "checkpoint": str(path),
                      "retainedNegativeCeilingMet": ceiling_met,
                      "retainedAt": best_records.get(name)})
        emit("checkpoint-sweep", f"{name}: validation Dice "
             f"{validation_all[threshold]['dice']:.3f} at {threshold:.2f}",
             checkpoint=name, threshold=threshold, retainedNegativeCeilingMet=ceiling_met,
             validation=validation_all[threshold])
    selected, ceiling_met = deployment_checkpoint(swept)
    threshold = selected["threshold"]
    result = {"targetNegativeRatio": negative_ratio,
              "negativeSelection": selection_name,
              "actualNegativeRatio": epoch_negative_count / max(1, epoch_size),
              "negativeAvailable": sum(sample.get("decision") == "negative"
                                       for sample in train_samples),
              "hardExampleCount": sum(bool(sample.get("feedbackPriority") or sample.get("burstId") or
                                             sample.get("_hardNegative"))
                                      for sample in train_samples),
              "historicalHardNegativeCount": sum(bool(sample.get("_hardNegative"))
                                                   for sample in train_samples),
              "historicalHardPositiveCount": sum(bool(sample.get("_hardPositive"))
                                                   for sample in train_samples),
              "warmStartModelId": warm_start_model_id,
              "trainCount": len(train_samples), "epochSize": epoch_size,
              "positiveWeight": float(pos_weight.item()), "threshold": threshold,
              "retainedNegativeCeiling": RETAINED_NEGATIVE_FPR_CEILING,
              "retainedNegativeCeilingMet": ceiling_met,
              "selectedCheckpointVariant": selected["name"],
              "checkpointVariants": [{key: value for key, value in item.items()
                                      if key != "checkpoint"} for item in swept],
              "validation": selected["validation"]}
    emit("candidate-complete", f"{negative_ratio:.0%} negatives: selected "
         f"{selected['name']} checkpoint with validation Dice "
         f"{result['validation']['dice']:.3f}", **result)
    return result, Path(selected["checkpoint"])


def train(args):
    import torch
    from torch.utils.data import DataLoader
    root, output = args.dataset.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    manifest = load_manifest(root)
    available, sealed_sources = partition_samples(manifest, args.minimum_resolution)
    if any(not available[name] for name in ("train", "validation", "test")):
        raise RuntimeError("Train, validation, and test splits must each contain accepted frames "
                           "at the selected minimum resolution")
    if not available["sealedHoldout"]:
        raise RuntimeError("v1.1 requires the source-isolated sealed holdout")
    leaked = {sample.get("sourceId") for name in ("train", "validation", "test")
              for sample in available[name]} & sealed_sources
    if leaked: raise RuntimeError("A sealed source leaked into model development splits")
    for samples in available.values():
        annotate_mask_statistics(root, samples, args.input_size)
    args.classification_summary = annotate_classifications(
        root, output, available, args.input_size)
    args.target_summary = materialize_specialized_targets(
        root, output, available, args.input_size)
    args.classification_summary["targetDefinition"] = args.target_summary
    split_profiles = {key: split_profile(value) for key, value in available.items()}
    emit("split-profile", "Recorded source-isolated split balance for this run",
         profiles=split_profiles)
    if args.audit_only:
        args.warm_start_model_id = REV5_MODEL_ID
        args.warm_start_checkpoint_sha256 = REV5_CHECKPOINT_SHA256
        snapshot_path = write_dataset_snapshot(root, output, manifest, available, args)
        emit("complete", "Classification audit and Rev 9 specialized dataset snapshot completed",
             snapshot=str(snapshot_path), baselineModelId=REV5_MODEL_ID,
             classification=args.classification_summary)
        return
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    if device.type == "cuda":
        memory = torch.cuda.get_device_properties(device).total_memory
        base_batch = 4 if memory >= 20 * 1024 ** 3 else 2 if memory >= 12 * 1024 ** 3 else 1
        batch = max(1, math.floor(base_batch * (512 / args.input_size) ** 2))
    else:
        batch = 1
    prior_package, prior_manifest = latest_v1_package(
        root, output, REV5_MODEL_ID, REV5_CHECKPOINT_SHA256)
    if args.warm_start_latest and prior_package is None:
        raise RuntimeError("No completed v1 package is available for the v1.1 warm start")
    warm_checkpoint = prior_package / "model.pth" if args.warm_start_latest else None
    warm_model_id = prior_manifest.get("modelId") if args.warm_start_latest else None
    args.warm_start_model_id = warm_model_id
    args.warm_start_checkpoint_sha256 = digest(warm_checkpoint) if warm_checkpoint else None
    mining_checkpoint_sha256 = digest(prior_package / "model.pth") if prior_package else ""
    def phase_progress(label):
        started = time.monotonic()
        def update(completed, total):
            if not args.console_progress: return
            fraction = completed / max(1, total); filled = min(30, int(fraction * 30))
            elapsed = time.monotonic() - started
            remaining = elapsed * (total - completed) / completed
            write_console_progress(f"{label}  [{'=' * filled}{'-' * (30 - filled)}] "
                f"{fraction:4.0%}  {completed}/{total}  ETA {format_duration(remaining)}")
        return update
    emit("hard-example-mining", "Scoring training mistakes with the current v1 model",
         sourceModelId=prior_manifest.get("modelId") if prior_manifest else None)
    train_lookup = {sample["id"]: sample for sample in available["train"]}
    hard_negative_model = warm_model_id
    mining_threshold = max(HARD_NEGATIVE_MINIMUM_THRESHOLD,
        float(prior_manifest.get("confidenceThreshold", .5)) - HARD_NEGATIVE_THRESHOLD_OFFSET)
    hard_negative_ids, hard_negative_source, hard_negative_cache = load_mining_selection(
        root, output, "hard-negative", HARD_NEGATIVE_MINING_REVISION,
        mining_checkpoint_sha256, prior_manifest.get("modelId"), available["train"], args.input_size)
    if hard_negative_ids:
        for sample_id in hard_negative_ids & train_lookup.keys():
            train_lookup[sample_id]["_hardNegative"] = True
        historical_hard_negatives = len(hard_negative_ids & train_lookup.keys())
    else:
        historical_hard_negatives, hard_negative_model, mining_threshold, hard_negative_ids = \
            mine_hard_negatives(torch, DataLoader, root, output, available["train"],
                args.input_size, batch, device, prior_package, prior_manifest,
                phase_progress("Mining near-miss negatives"))
        if args.console_progress:
            write_console_progress("Near-miss negative mining complete", finish=True)
        save_mining_selection(hard_negative_cache, "hard-negative",
            HARD_NEGATIVE_MINING_REVISION, mining_checkpoint_sha256,
            prior_manifest.get("modelId"), available["train"], args.input_size, hard_negative_ids,
            miningThreshold=mining_threshold,
            deployedThreshold=float(prior_manifest.get("confidenceThreshold", .5)),
            selectionPolicy="source-diverse-rvm-adjacent-top-response")
        hard_negative_source = "new"
    hard_positive_ids, hard_positive_source, hard_positive_cache = load_mining_selection(
        root, output, "hard-positive", HARD_POSITIVE_MINING_REVISION,
        mining_checkpoint_sha256, prior_manifest.get("modelId"), available["train"], args.input_size,
        "historicalHardPositive")
    if hard_positive_ids:
        for sample_id in hard_positive_ids & train_lookup.keys():
            train_lookup[sample_id]["_hardPositive"] = True
        historical_hard_positives = len(hard_positive_ids & train_lookup.keys())
        if hard_positive_source == "snapshot":
            save_mining_selection(hard_positive_cache, "hard-positive",
                HARD_POSITIVE_MINING_REVISION, mining_checkpoint_sha256,
                prior_manifest.get("modelId"), available["train"], args.input_size, hard_positive_ids)
    else:
        historical_hard_positives, hard_positive_ids = mine_hard_positives(
            torch, DataLoader, root, available["train"], args.input_size, batch, device,
            prior_package, prior_manifest, phase_progress("Mining missed positives"))
        if args.console_progress: write_console_progress("Hard-positive mining complete", finish=True)
        save_mining_selection(hard_positive_cache, "hard-positive",
            HARD_POSITIVE_MINING_REVISION, mining_checkpoint_sha256,
            prior_manifest.get("modelId"), available["train"], args.input_size, hard_positive_ids)
        hard_positive_source = "new"
    emit("hard-example-mining",
         f"Promoted {historical_hard_positives:,} missed positives and "
         f"{historical_hard_negatives:,} near-miss negatives at {mining_threshold:.2f}",
         hardPositiveCount=historical_hard_positives,
         hardPositiveSource=hard_positive_source,
         hardNegativeCount=historical_hard_negatives,
         hardNegativeSource=hard_negative_source, miningThreshold=mining_threshold,
         sourceModelId=hard_negative_model)
    snapshot_path = write_dataset_snapshot(root, output, manifest, available, args)
    accumulation = max(1, math.ceil(8 / batch))
    emit("setup", f"Loading DeepLabV3-ResNet50 on {device}",
         batchSize=batch, gradientAccumulation=accumulation,
         negativeSelection=args.negative_selection, minimumResolution=args.minimum_resolution,
         inputSize=args.input_size)
    baseline = None
    if prior_package is not None:
        baseline_model = build_model(torch, pretrained=False).to(device)
        baseline_model.load_state_dict(torch.load(prior_package / "model.pth", map_location=device,
                                                   weights_only=True))
        baseline_thresholds = tuple(round(value / 100, 2) for value in range(10, 91, 5))
        baseline_validation_all = evaluate(torch, baseline_model, DataLoader(
            PropDataset(root, available["validation"], False, args.input_size), batch_size=batch,
            shuffle=False, num_workers=1), device, baseline_thresholds,
            root, available["validation"], phase_progress("Baseline validation"),
            classification_breakdown=True)
        if args.console_progress: write_console_progress("Baseline validation complete", finish=True)
        baseline_threshold, baseline_ceiling = deployment_threshold(baseline_validation_all)
        baseline = {"modelId": prior_manifest.get("modelId"),
                    "threshold": baseline_threshold,
                    "validation": baseline_validation_all[baseline_threshold],
                    "validationCeilingMet": baseline_ceiling}
        for split in ("test", "sealedHoldout"):
            loader = DataLoader(PropDataset(root, available[split], False, args.input_size),
                                batch_size=batch, shuffle=False, num_workers=1)
            baseline[split] = evaluate(torch, baseline_model, loader, device,
                                       (baseline_threshold,), root,
                                       available[split], phase_progress(
                                           f"Baseline {split}"),
                                       classification_breakdown=True)[baseline_threshold]
            if args.console_progress:
                write_console_progress(f"Baseline {split} complete", finish=True)
        atomic_json(output / "v1-baseline.json", baseline)
        emit("baseline", f"Recorded {baseline['modelId']} at threshold "
             f"{baseline_threshold:.2f} on validation, test, and sealed holdout",
             baseline=str(output / "v1-baseline.json"), **baseline)
        del baseline_model
        if device.type == "cuda": torch.cuda.empty_cache()
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
            epoch_size, negative_selected, warm_checkpoint, warm_model_id)
        result["checkpoint"] = str(checkpoint_path)
        candidates.append(result)
        if device.type == "cuda": torch.cuda.empty_cache()
    candidate_options = [{"name": candidate["negativeSelection"],
                          "selectionScore": candidate["validation"]["selectionScore"],
                          "validation": candidate["validation"], "candidate": candidate}
                         for candidate in candidates]
    selected_candidate, candidate_ceiling_met = deployment_checkpoint(candidate_options)
    winner = selected_candidate["candidate"]
    emit("candidate-selection", f"Selected {winner['negativeSelection']} negatives with "
         f"retained-negative FP {retained_negative_fpr(winner['validation']):.1%}",
         negativeSelection=winner["negativeSelection"],
         retainedNegativeCeiling=RETAINED_NEGATIVE_FPR_CEILING,
         retainedNegativeCeilingMet=candidate_ceiling_met,
         validation=winner["validation"])
    best_path = Path(winner.pop("checkpoint"))
    for candidate in candidates: candidate.pop("checkpoint", None)
    threshold = winner["threshold"]
    model = build_model(torch, pretrained=False).to(device)
    model.load_state_dict(torch.load(best_path, map_location=device, weights_only=True))
    test_loader = DataLoader(PropDataset(root, available["test"], False, args.input_size), batch_size=batch,
                             shuffle=False, num_workers=1)
    test_metrics = evaluate(torch, model, test_loader, device, (threshold,),
                            root, available["test"], phase_progress("Final test"),
                            classification_breakdown=True)[threshold]
    if args.console_progress: write_console_progress("Final test complete", finish=True)
    holdout_loader = DataLoader(PropDataset(root, available["sealedHoldout"], False,
                                            args.input_size), batch_size=batch,
                                shuffle=False, num_workers=1)
    holdout_metrics = evaluate(torch, model, holdout_loader, device, (threshold,),
                               root, available["sealedHoldout"],
                               phase_progress("Sealed holdout"),
                               classification_breakdown=True)[threshold]
    if args.console_progress: write_console_progress("Sealed holdout complete", finish=True)
    validation_loader = DataLoader(
        PropDataset(root, available["validation"], False, args.input_size), batch_size=batch,
        shuffle=False, num_workers=1)
    validation_metrics = evaluate(torch, model, validation_loader, device, (threshold,),
        root, available["validation"], phase_progress("Final validation breakdown"),
        classification_breakdown=True)[threshold]
    winner["validation"] = validation_metrics
    package = output / "package"; package.mkdir(exist_ok=True)
    checkpoint = package / "model.pth"; shutil.copy2(best_path, checkpoint)
    shutil.copy2(snapshot_path, package / "dataset-snapshot.json")
    checkpoint_hash = digest(checkpoint)
    model_id = f"prop-r50-{time.strftime('%Y%m%d-%H%M%S', time.gmtime())}-{checkpoint_hash[:8]}"
    review = save_error_review(torch, model, validation_loader, device, threshold,
                               package / "review", samples=available["validation"])
    baseline_holdout = baseline.get("sealedHoldout", {}) if baseline else {}
    promotion = promotion_result(holdout_metrics, baseline_holdout)
    promotion["benchmarkCandidateEligible"] = promotion["eligible"]
    promotion["eligible"] = False
    promotion["reason"] = "Specialized dildo/butt-plug benchmark; not a general foreground replacement"
    metrics = {"validation": winner["validation"], "test": test_metrics,
               "sealedHoldout": holdout_metrics, "v1Baseline": baseline,
               "promotion": promotion,
               "threshold": threshold, "selectedNegativeRatio": winner["targetNegativeRatio"],
               "selectedNegativeMode": winner["negativeSelection"],
               "minimumResolution": args.minimum_resolution,
               "inputSize": args.input_size,
               "trainingRevision": TRAINING_REVISION,
               "classification": args.classification_summary,
               "testSealed": False, "sealedSourceCount": len(sealed_sources),
               "reviewSplit": "validation",
               "sampling": {"sourceBalanced": True, "allNegativesEligible": True,
                            "smallMaskUpweighting": True,
                            "categoryBalanced": True,
                            "categorySamplingWeights": args.target_summary[
                                "trainingCategorySamplingWeights"],
                            "foregroundCategories": sorted(TARGET_CATEGORIES),
                            "ignoredCategories": ["unclassified"],
                            "objectFocusedCrops": True,
                            "objectFocusedCropProbability": .30,
                            "historicalHardNegativeMining": True,
                            "historicalHardNegativeCount": historical_hard_negatives,
                            "hardNegativeMiningRevision": HARD_NEGATIVE_MINING_REVISION,
                            "hardNegativeMiningThreshold": mining_threshold,
                            "hardNegativeSelectionSource": hard_negative_source,
                            "historicalHardPositiveMining": True,
                            "historicalHardPositiveCount": historical_hard_positives,
                            "hardPositiveMiningRevision": HARD_POSITIVE_MINING_REVISION,
                            "hardPositiveSelectionSource": hard_positive_source,
                            "hardNegativeSourceModelId": hard_negative_model,
                            "hardNegativeWeight": HARD_NEGATIVE_WEIGHT,
                            "hardNegativeFraction": HARD_NEGATIVE_FRACTION,
                            "hardNegativeLimit": MAX_HARD_NEGATIVES},
               "warmStart": {"enabled": bool(warm_checkpoint), "modelId": warm_model_id,
                             "checkpointSha256": digest(warm_checkpoint)
                                if warm_checkpoint else None},
               "geometryAugmentation": {"letterbox": .50, "foregroundCrop": .30,
                                        "randomContextCrop": .20,
                                        "smallObjectFocusedZoom": True,
                                        "runtimeFineTuneEpochs": args.runtime_epochs,
                                        "runtimeLetterbox": .80},
               "checkpointSelection": {"variants": ["balanced", "raw-dice",
                    "exterior-prop", "conservative"], "validationSweep": True,
                    "finalRvmUnionUsedForSelection": False,
                    "retainedNegativeFalsePositiveCeiling": RETAINED_NEGATIVE_FPR_CEILING,
                    "nearBestScoreTolerance": CHECKPOINT_SCORE_TOLERANCE},
               "loss": {"positiveWeightCap": 10, "tverskyAlpha": .5,
                        "tverskyBeta": .5, "boundaryWeight": .2,
                        "exteriorRecallWeight": EXTERIOR_RECALL_WEIGHT},
               "balanceCandidates": candidates,
               "splitProfiles": split_profiles,
               "counts": {key: len(value) for key, value in available.items()},
               "review": {key: len(value) for key, value in review.items()}}
    atomic_json(package / "metrics.json", metrics)
    atomic_json(output / "promotion-result.json", promotion)
    files = {str(path.relative_to(package)).replace("\\", "/"): digest(path)
             for path in package.rglob("*") if path.is_file() and path.name != "manifest.json"}
    atomic_json(package / "manifest.json", {
        "schemaVersion": 1, "modelId": model_id, "architecture": SPECIALIZED_ARCHITECTURE,
        "trainingRevision": TRAINING_REVISION,
        "category": "dildo_or_butt_plug", "inputSize": args.input_size,
        "mean": MEAN, "std": STD, "confidenceThreshold": threshold,
        "proximityRadiusAt512": 24, "checkpointSha256": checkpoint_hash,
        "preprocessing": {"resize": "aspect-preserving-letterbox", "inputSize": args.input_size,
                          "mean": MEAN, "std": STD},
        "postprocessing": {"contract": "rvm-proximity-union-v1", "confidenceThreshold": threshold,
                           "proximityRadiusAt512": 24, "componentConnectivity": 8},
        "datasetId": manifest["datasetId"], "runId": output.name,
        "minimumTrainingResolution": args.minimum_resolution,
        "negativeSelection": winner["negativeSelection"],
        "checkpointVariant": winner["selectedCheckpointVariant"],
        "warmStartModelId": warm_model_id,
        "promotionEligible": promotion["eligible"],
        "createdUtc": time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
        "pythonVersion": sys.version.split()[0], "torchVersion": torch.__version__,
        "torchvisionVersion": __import__("torchvision").__version__,
        "cudaVersion": torch.version.cuda, "files": files})
    emit("review", "Saved diverse validation false-positive and false-negative review images",
         review=str(package / "review"))
    emit("complete", "Training, threshold calibration, and test evaluation complete",
         package=str(package), modelId=model_id, testDice=test_metrics["dice"],
         sealedHoldout=holdout_metrics, promotion=promotion,
         selectedNegativeRatio=winner["targetNegativeRatio"],
         selectedNegativeMode=winner["negativeSelection"],
         selectedCheckpointVariant=winner["selectedCheckpointVariant"])


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
        partition_manifest = {"sources": [
            {"id": "ordinary", "sealedHoldout": False},
            {"id": "sealed", "sealedHoldout": True}], "samples": [
            {"id": "train", "decision": "positive", "split": "train",
             "sourceId": "ordinary", "framePath": "frame.png", "propMaskPath": "mask.png",
             "width": 1920, "height": 1080},
            {"id": "leak", "decision": "positive", "split": "train",
             "sourceId": "sealed", "framePath": "frame.png", "propMaskPath": "mask.png",
             "width": 1920, "height": 1080}]}
        partitioned, sealed_sources = partition_samples(partition_manifest, 720)
        assert sealed_sources == {"sealed"}
        assert [sample["id"] for sample in partitioned["train"]] == ["train"]
        assert [sample["id"] for sample in partitioned["sealedHoldout"]] == ["leak"]
        selection_root = root / "selection"
        good_package = selection_root / "runs" / "good" / "package"
        failed_package = selection_root / "runs" / "failed" / "package"
        good_package.mkdir(parents=True); failed_package.mkdir(parents=True)
        (good_package / "model.pth").write_bytes(b"good")
        (failed_package / "model.pth").write_bytes(b"failed")
        atomic_json(good_package / "manifest.json", {"architecture": ARCHITECTURE,
            "modelId": "good-model", "confidenceThreshold": .7})
        atomic_json(failed_package / "manifest.json", {"architecture": ARCHITECTURE,
            "modelId": "failed-model", "promotionEligible": False})
        os.utime(failed_package / "manifest.json", (time.time() + 1, time.time() + 1))
        selected_package, selected_manifest = latest_v1_package(selection_root,
                                                                 selection_root / "new")
        assert selected_package == good_package and selected_manifest["modelId"] == "good-model"
        cache_samples = [{"id": "a", "decision": "positive", "_recordSha256": "1"},
                         {"id": "b", "decision": "negative", "_recordSha256": "2"}]
        cache_path = mining_cache_path(selection_root, "hard-negative", 2, "a" * 64,
            mining_dataset_fingerprint(cache_samples), 768)
        save_mining_selection(cache_path, "hard-negative", 2, "a" * 64, "good-model",
                              cache_samples, 768, {"b"}, miningThreshold=.55)
        cached, source, _ = load_mining_selection(selection_root, selection_root / "new",
            "hard-negative", 2, "a" * 64, "good-model", cache_samples, 768)
        assert cached == {"b"} and source == "cache"
        atomic_json(selection_root / "runs" / "snapshot" / "dataset-snapshot.json", {
            "warmStartModelId": "good-model", "warmStartCheckpointSha256": "a" * 64,
            "inputSize": 768, "splits": {"train": [
                {"id": "a", "decision": "positive", "recordSha256": "1",
                 "historicalHardPositive": True},
                {"id": "b", "decision": "negative", "recordSha256": "2"}]}})
        snapshot_ids, source, _ = load_mining_selection(selection_root,
            selection_root / "new", "hard-positive", 1, "a" * 64, "good-model",
            cache_samples, 768, "historicalHardPositive")
        assert snapshot_ids == {"a"} and source == "snapshot"
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
        planned[1]["_categoryBalanceWeight"] = 2.
        category_weights = source_balanced_weights(planned, .30)
        assert category_weights[1] > weights[1]
        planned[1].pop("_categoryBalanceWeight")
        planned[7]["_hardNegative"] = True
        hard_weights = source_balanced_weights(planned, .30)
        assert hard_weights[7] > hard_weights[11]
        planned[0]["_hardPositive"] = True
        hard_positive_weights = source_balanced_weights(planned, .30)
        assert hard_positive_weights[0] > hard_positive_weights[1]
        profile = split_profile(planned)
        assert profile["positiveCount"] == 7 and profile["negativeCount"] == 20
        optimizer = torch.optim.AdamW(model.parameters(), lr=1e-3)
        image = torch.randn(2, 3, 8, 8); target = torch.zeros(2, 1, 8, 8)
        loss = torch.nn.functional.binary_cross_entropy_with_logits(model(image), target)
        loss.backward(); optimizer.step()
        checkpoint = root / "resume.pth"
        atomic_torch_save(torch,
            {"model": model.state_dict(), "optimizer": optimizer.state_dict(), "epoch": 0}, checkpoint)
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
        images[1, 0, 2:6, 2:6] = 1
        targets = torch.zeros(2, 1, 8, 8); targets[0, 0, 4:, 4:] = 1
        from PIL import Image
        import numpy as np
        person = np.zeros((8, 8), dtype=np.uint8); person[:4, :4] = 255
        desired = person.copy(); desired[4:, 4:] = 255
        Image.fromarray(person).save(root / "person.png")
        Image.fromarray(desired).save(root / "desired.png")
        Image.fromarray(np.zeros((8, 8, 3), dtype=np.uint8)).save(root / "frame.png")
        source_mask = (targets[0, 0].numpy() * 255).astype(np.uint8)
        source_mask[:2, 4:] = 255
        source_mask[4:, :2] = 255
        Image.fromarray(source_mask).save(root / "mask.png")
        classification_ids = np.zeros((8, 8), dtype=np.int32)
        classification_ids[4:, 4:] = 1
        classification_ids[:2, 4:] = 2
        classification_ids[4:, :2] = 3
        with gzip.open(root / "classification-mask.i32.gz", "wb") as stream:
            stream.write(classification_ids.astype("<i4").tobytes())
        atomic_json(root / "classification.json", {"schemaVersion": 1,
            "reviewedUtc": "2026-08-24T00:00:00Z", "objects": [
                {"id": 1, "category": "dildo", "states": ["held"], "colorArgb": 0},
                {"id": 2, "category": "unclassified", "states": [], "colorArgb": 0},
                {"id": 3, "category": "strap-on", "states": [], "colorArgb": 0}]})
        classified_sample = {"id": "sample-a", "decision": "positive",
            "sourceId": "same-source", "timestampMs": 1000, "width": 8, "height": 8,
            "classifiedUtc": "2026-08-24T00:00:00Z", "framePath": "frame.png",
            "propMaskPath": "mask.png", "classificationPath": "classification.json",
            "classificationMaskPath": "classification-mask.i32.gz",
            "rvmPersonMaskPath": "person.png", "desiredForegroundPath": "desired.png"}
        classification_summary = annotate_classifications(root, root / "classification-audit",
            {"train": [classified_sample], "validation": [], "test": [], "sealedHoldout": []}, 8)
        assert classification_summary["objects"] == 3
        assert classified_sample["_classificationObjects"][0]["bounds"] == [4, 4, 8, 8]
        target_summary = materialize_specialized_targets(root, root / "specialized",
            {"train": [classified_sample], "validation": [], "test": [], "sealedHoldout": []}, 8)
        encoded_target = np.asarray(Image.open(classified_sample["_targetMaskPath"]), dtype=np.uint8)
        assert np.all(encoded_target[4:, 4:] == 255)
        assert np.all(encoded_target[:2, 4:] == 128)
        assert np.all(encoded_target[4:, :2] == 0)
        assert classified_sample["decision"] == "positive"
        assert target_summary["targetObjects"]["dildo"] == 1
        assert target_summary["splitCounts"]["train"]["positive"] == 1
        evaluation_samples = [
            classified_sample,
            {"id": "sample-b", "decision": "negative", "sourceId": "same-source",
             "timestampMs": 2000, "rvmPersonMaskPath": "person.png",
             "desiredForegroundPath": "person.png"}]
        review_metrics = evaluate(torch, ReviewModel(),
            [(images, targets, ("sample-a", "sample-b"))], torch.device("cpu"), (.5,),
            root, evaluation_samples, classification_breakdown=True)[.5]
        assert all(name in review_metrics for name in
                   ("macroDice", "positiveRecall", "smallObjectRecall", "boundaryF1",
                    "perVideoDice", "negativeFrameFalsePositiveRate", "selectionScore"))
        assert review_metrics["rvmUnion"]["coverage"] == 1
        assert review_metrics["rvmUnion"]["retainedNegativeCoverage"] == 1
        assert review_metrics["rvmUnion"]["retainedNegativeFrameFalsePositiveRate"] == 1
        assert review_metrics["classificationRecall"]["byCategory"]["dildo"]["pixels"] == 16
        assert review_metrics["classificationRecall"]["byState"]["held"]["recall"] == 0
        objectives = checkpoint_objectives({.5: review_metrics})
        assert set(objectives) == {"balanced", "raw-dice", "exterior-prop", "conservative"}
        dirty = json.loads(json.dumps(review_metrics))
        clean = json.loads(json.dumps(review_metrics))
        dirty["selectionScore"] = .9
        dirty["rvmUnion"]["retainedNegativeFrameFalsePositiveRate"] = .2
        clean["selectionScore"] = .89
        clean["rvmUnion"]["retainedNegativeFrameFalsePositiveRate"] = .05
        threshold, ceiling_met = deployment_threshold({.5: dirty, .75: clean})
        assert ceiling_met and threshold == .75
        selected, ceiling_met = deployment_checkpoint([
            {"name": "dirty", "selectionScore": .9, "validation": dirty},
            {"name": "clean", "selectionScore": .89, "validation": clean}])
        assert ceiling_met and selected["name"] == "clean"
        baseline_promotion = {"rvmUnion": {"exteriorProp": {"recall": .30, "dice": .40},
            "finalForeground": {"dice": .99}, "retainedNegativeCoverage": 1.,
            "retainedNegativeFrameFalsePositiveRate": .04}}
        improved_promotion = {"rvmUnion": {"exteriorProp": {"recall": .36, "dice": .45},
            "finalForeground": {"dice": .991}, "retainedNegativeCoverage": 1.,
            "retainedNegativeFrameFalsePositiveRate": .05}}
        assert promotion_result(improved_promotion, baseline_promotion)["eligible"]
        ignored_target = torch.tensor([[[[1., -1.], [0., 0.]]]])
        ignored_probability = torch.tensor([[[[.9, .9], [.1, .1]]]])
        assert confusion(ignored_probability, ignored_target, .5) == (1, 0, 0, 2)
        ignored_loss = segmentation_loss(torch, {"out": torch.zeros_like(ignored_target)},
                                         ignored_target, torch.tensor([2.]))
        assert torch.isfinite(ignored_loss)
        combined_loss = segmentation_loss(torch, {"out": images[:, :1] * 4}, targets,
                                          torch.tensor([2.]), targets)
        assert torch.isfinite(combined_loss)
        random.seed(1729)
        _, trained_target, trained_exterior, _ = PropDataset(root, [{
            "id": "training-sample", "framePath": "frame.png", "propMaskPath": "mask.png",
            "rvmPersonMaskPath": "person.png", "_maskFraction": .25}], True, 8)[0]
        assert torch.equal(trained_target, trained_exterior)
        review = save_error_review(torch, ReviewModel(),
            [(images, targets, ("sample-a", "sample-b"))], torch.device("cpu"), .5,
            root / "review", limit=2, samples=evaluation_samples)
        assert review["false-positive"] and review["false-negative"]
        assert len(review["false-positive"]) == len(review["false-negative"]) == 1
        assert "errorPixelsAtInput" in review["false-positive"][0]
        assert (root / "review" / "review.json").is_file()
        focus_image = Image.fromarray(np.zeros((100, 100, 3), dtype=np.uint8))
        focus_mask = np.zeros((100, 100), dtype=np.uint8); focus_mask[49:51, 49:51] = 255
        random.seed(1729)
        _, zoomed_mask = focused_crop_pair(focus_image, Image.fromarray(focus_mask), 64, .0004,
                                           (49, 49, 51, 51))
        zoomed_bounds = zoomed_mask.getbbox()
        assert zoomed_bounds and zoomed_bounds[2] - zoomed_bounds[0] >= 7
        snapshot_sample = {"id": "sample-a", "decision": "positive", "sourceId": "source",
                           "timestampMs": 1000, "framePath": "frame.png",
                           "propMaskPath": "mask.png", "_maskFraction": .25}
        snapshot_path = write_dataset_snapshot(root, root / "run",
            {"datasetId": "test-dataset"}, {"train": [snapshot_sample],
            "validation": [], "test": []}, argparse.Namespace(input_size=8,
            minimum_resolution=0, seed=1729))
        snapshot = json.loads(snapshot_path.read_text(encoding="utf-8"))
        assert snapshot["trainingRevision"] == TRAINING_REVISION
        assert snapshot["splits"]["train"][0]["artifacts"]["framePath"]["sha256"] == \
            digest(root / "frame.png")
    emit("self-test", "prop-segmenter training self-test passed", status="ok")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--warm-start-latest", action="store_true")
    parser.add_argument("--warmup-epochs", type=int, default=5)
    parser.add_argument("--epochs", type=int, default=40)
    parser.add_argument("--runtime-epochs", type=int, default=8)
    parser.add_argument("--console-progress", action="store_true")
    parser.add_argument("--patience", type=int, default=12)
    parser.add_argument("--seed", type=int, default=1729)
    parser.add_argument("--minimum-resolution", type=int, default=720)
    parser.add_argument("--input-size", type=int, choices=(512, 768, 1024), default=768)
    parser.add_argument("--negative-selection", choices=("compare", "20", "25", "30", "35", "all"),
                        default="compare")
    parser.add_argument("--regenerate-review", action="store_true")
    parser.add_argument("--audit-only", action="store_true")
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
