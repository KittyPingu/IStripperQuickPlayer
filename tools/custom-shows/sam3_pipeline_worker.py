#!/usr/bin/env python3
"""SAM3 keyframe discovery, separate eager SAM2 tracks, optimized ViTMatte S.

SAM3 and SAM2 run in separate environments because their packages conflict.
The fixed recipe is the foreground-preserving result of the November benchmark.
"""
import argparse
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

DEFAULT_CONFIDENCE = .35


def emit(stage, percent, message, **extra):
    print(json.dumps(dict(stage=stage, percent=percent, message=message, **extra)), flush=True)


def save(path, value):
    path.write_text(json.dumps(value, indent=2), encoding="utf-8")


def validate_concepts(concepts):
    if not 1 <= len(concepts) <= 16 or any(not c.strip() or len(c) > 200 or
            any(ord(x) < 32 or ord(x) == 127 or x in ",;" for x in c) for c in concepts):
        raise ValueError("Enter 1–16 SAM3 concepts, up to 200 characters each")


def confidence_values(concepts, values):
    if values is not None and not isinstance(values, (list, tuple)):
        raise ValueError("Enter one SAM3 confidence from 0 to 1 for each concept")
    values = list(values) if values else [DEFAULT_CONFIDENCE] * len(concepts)
    if len(values) != len(concepts) or any(
            isinstance(value, bool) or not isinstance(value, (int, float)) or
            not math.isfinite(value) or not 0 <= value <= 1 for value in values):
        raise ValueError("Enter one SAM3 confidence from 0 to 1 for each concept")
    return values


def select_objects(candidates, minimum_new_fraction=.05):
    """Prioritize people, then retain concepts that add foreground pixels."""
    import numpy as np
    kept, covered = [], None
    for item in sorted(candidates, key=lambda x:
                       (x["concept"].casefold() != "person", -x["score"])):
        mask = item["mask"]
        if covered is None:
            covered = np.zeros_like(mask, dtype=bool)
        area = int(mask.sum())
        new = int((mask & ~covered).sum())
        if area and new >= 16 and new / area >= minimum_new_fraction:
            kept.append(item)
            covered |= mask
    return kept


def run_child(command, lower, upper):
    process = subprocess.Popen(command, stdout=subprocess.PIPE, text=True,
                               encoding="utf-8", errors="replace")
    failure = None
    try:
        for line in process.stdout:
            try:
                record = json.loads(line)
                if not isinstance(record, dict) or "stage" not in record:
                    raise ValueError("Not a progress record")
                percent = float(record.get("percent", 0))
                if record["stage"] == "error":
                    failure = record.get("message")
                record["percent"] = lower + (upper-lower)*max(0, min(100, percent))/100
                print(json.dumps(record), flush=True)
            except (ValueError, TypeError):
                print(line.rstrip(), file=sys.stderr, flush=True)
        if process.wait() != 0:
            raise RuntimeError(failure or "Processing stage failed; see the processing log for details")
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        process.stdout.close()


def prepare(args, scratch):
    from custom_show_worker import model_size
    from rvm_worker import probe, executable
    width, height, rate, fps, duration = probe(args.source)
    end = min(duration*1000, args.end_ms) if args.end_ms is not None else duration*1000
    if args.start_ms < 0 or end <= args.start_ms:
        raise ValueError("SAM3 processing requires a nonempty source range")
    seconds = (end-args.start_ms)/1000
    rw, rh = model_size(width, height)
    frames = scratch / "frames"
    frames.mkdir()
    common = [executable("ffmpeg"), "-hide_banner", "-ss", str(args.start_ms/1000),
              "-i", str(args.source), "-t", str(seconds), "-map", "0:v:0"]
    subprocess.run([*common, "-v", "error", "-vf",
        f"fps={rate},scale={rw}:{rh}:flags=lanczos,setsar=1", "-q:v", "2",
        "-start_number", "0", str(frames / "%08d.jpg")], check=True)
    files = sorted(frames.glob("*.jpg"))
    if not files:
        raise RuntimeError("No video frames were decoded")
    cuts = subprocess.run([*common, "-vf", "select='gt(scene,0.3)',showinfo",
        "-an", "-f", "null", "-"], capture_output=True, text=True, check=True)
    anchors = set(range(0, len(files), max(1, round(5*fps))))
    cut_frames = {round(float(t)*fps) for t in
                  re.findall(r"pts_time:([\d.]+)", cuts.stderr)}
    anchors.update(cut_frames)
    anchors = sorted(t for t in anchors if 0 <= t < len(files))
    chunks = []
    for start, end_frame in zip(anchors, anchors[1:] + [len(files)]):
        chunk = scratch / "chunks" / f"{start:08d}"
        chunk.mkdir(parents=True)
        for index in range(start, end_frame):
            os.link(files[index], chunk / f"{index-start:08d}.jpg")
        chunks.append(dict(start=start, end=end_frame, path=str(chunk)))
    prepared = dict(width=width, height=height, reviewWidth=rw, reviewHeight=rh,
                    rate=rate, fps=fps, frameCount=len(files), chunks=chunks)
    if getattr(args, "selective_backward_repair", False):
        keyframes = [dict(start=chunk["start"], path=chunk["path"]) for chunk in chunks]
        tail = len(files)-1
        if tail not in anchors:
            endpoint = scratch / "tail-keyframe"
            endpoint.mkdir()
            os.link(files[-1], endpoint / "00000000.jpg")
            keyframes.append(dict(start=tail, path=str(endpoint)))
        for index, chunk in enumerate(chunks):
            chunk["repairKeyframe"] = None
            if chunk["end"] in cut_frames:
                continue
            if index+1 < len(chunks):
                chunk["repairKeyframe"] = index+1
                chunk["repairFrame"] = chunk["end"]-chunk["start"]
                os.link(files[chunk["end"]], Path(chunk["path"]) /
                        f"{chunk['repairFrame']:08d}.jpg")
            elif tail not in anchors:
                chunk["repairKeyframe"] = len(keyframes)-1
                chunk["repairFrame"] = tail-chunk["start"]
        prepared["keyframes"] = keyframes
    return prepared


def load_detector(sam3):
    sys.path.insert(0, str(sam3 / "source" / "SAM2Matting"))
    import torch
    from sam3.model_builder import build_sam3_image_model
    from sam3.model.sam3_image_processor import Sam3Processor
    from sam2matting_worker import full_checkpoint_check
    if not torch.cuda.is_available():
        raise RuntimeError("SAM3 foreground processing requires an NVIDIA CUDA GPU")
    emit("SAM3", 0, "Loading SAM3 keyframe detector")
    checkpoint_path = sam3 / "checkpoints/SAM2Matting-SAM3.pt"
    full_checkpoint_check(checkpoint_path, 3509720141,
        "7102d695be6070b39acd67464f93207df725514a688b545ed1267d913d3b9c7d")
    model = build_sam3_image_model(device="cpu", load_from_HF=False,
        bpe_path=str(sam3 / "source/SAM2Matting/sam3/bpe_simple_vocab_16e6.txt.gz"))
    checkpoint = torch.load(checkpoint_path, map_location="cpu", weights_only=True)["model"]
    detector = {k.removeprefix("detector."): v for k, v in checkpoint.items()
                if k.startswith("detector.") and
                not k.startswith("detector.backbone.vision_backbone.sam2_convs.")}
    model.load_state_dict(detector, strict=True)
    del checkpoint, detector
    model = model.cuda().eval()
    return Sam3Processor(model, confidence_threshold=DEFAULT_CONFIDENCE)


def validate_runtime(sam3):
    import torch
    import torchvision
    from PIL import Image
    if sys.version_info[:2] != (3, 10) or torch.__version__ != "2.8.0+cu128" or \
            torchvision.__version__ != "0.23.0+cu128":
        raise RuntimeError("SAM3 requires the pinned Python 3.10 / PyTorch 2.8 CUDA 12.8 environment")
    revision = subprocess.check_output(["git", "-C", str(sam3 / "source/SAM2Matting"),
        "rev-parse", "HEAD"], text=True).strip()
    if revision != "73dd721d77b56749248aefe5e8824d7f61b9d13c":
        raise RuntimeError("SAM3 source revision mismatch; run SAM3 setup again")
    processor = load_detector(sam3)
    with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
        state = processor.set_image(Image.new("RGB", (64, 64)))
        processor.set_text_prompt("person", state)
    emit("complete", 100, "SAM3 checkpoint, text prompting and CUDA inference validated")


def prompt_candidates(processor, state, concepts, confidences):
    candidates = []
    for concept, confidence in zip(concepts, confidences, strict=True):
        processor.confidence_threshold = confidence
        processor.reset_all_prompts(state)
        result = processor.set_text_prompt(concept, state)
        for mask, score in zip(result["masks"].squeeze(1).cpu().numpy(),
                               result["scores"].float().cpu().numpy()):
            candidates.append(dict(concept=concept, score=float(score), mask=mask))
    return candidates


def detect(config):
    import torch
    import numpy as np
    from PIL import Image
    validate_concepts(config["concepts"])
    confidences = confidence_values(config["concepts"], config.get("confidences"))
    processor = load_detector(Path(config["sam3Runtime"]))
    records = []
    chunks = config["prepared"].get("keyframes", config["prepared"]["chunks"])
    with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
        for chunk in chunks:
            with Image.open(Path(chunk["path"]) / "00000000.jpg") as image:
                state = processor.set_image(image.convert("RGB"))
            candidates = prompt_candidates(processor, state, config["concepts"], confidences)
            objects = []
            for index, item in enumerate(select_objects(candidates), 1):
                path = Path(chunk["path"]) / f"seed-{index}.png"
                Image.fromarray(item["mask"].astype(np.uint8)*255).save(path)
                objects.append(dict(id=index, concept=item["concept"],
                                    score=item["score"], mask=str(path)))
            records.append(objects)
            emit("SAM3", len(records)*100/len(chunks),
                 f"Keyframe {len(records)}/{len(chunks)}: {len(objects)} foreground objects")
    if not any(records):
        raise RuntimeError("SAM3 found no foreground objects. Adjust the foreground concepts and retry.")
    save(Path(config["scratch"]) / "detection.json", records)


def track(config):
    import numpy as np
    import torch
    from PIL import Image
    from custom_show_worker import load_sam2, use_on_demand_sam2_frames
    from sam2matting_worker import prune_sam2_tracking_state, sam2_tracking_history
    from sam2_refine_worker import MaskWriter, Profiler
    prepared = config["prepared"]
    records = json.loads((Path(config["scratch"]) / "detection.json").read_text())
    repair_enabled = config.get("selectiveBackwardRepair", True)
    if not isinstance(repair_enabled, bool):
        raise ValueError("Selective backward repair must be true or false")
    if repair_enabled:
        import cv2
        from sam3_tracking_repair import (CombinedCollapseDetector, mapping,
                                         endpoint_disagreement, decide)
    # Reuse QuickPlayer's generated-mask archive/cleanup contract for SAM3 seeds too.
    output = Path(config["output"]).resolve()
    out = output / ".rvm-masks"
    if out.resolve().parent != output or out.is_symlink():
        raise ValueError("Generated-mask folder must stay inside the output folder")
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(exist_ok=False)
    emit("SAM2", 0, "Loading eager SAM2 Base+ object tracker")
    predictor = load_sam2(Path(config["runtime"]), torch, optimized=False,
                         encoder_compiled=False, model_name="base-plus")
    history = sam2_tracking_history(predictor)
    profiler = Profiler(Path(config["scratch"]) / "tracking-profile.jsonl")
    writer = MaskWriter(torch, "cuda", profiler)

    def run_pass(chunk, objects, last_frame, reverse=False, analyze=False,
                 reserve_repair=False):
        count = chunk["end"]-chunk["start"]
        if not objects:
            blank = np.zeros((prepared["reviewHeight"], prepared["reviewWidth"]), np.uint8)
            for index in range(count):
                Image.fromarray(blank).save(out / f"{chunk['start']+index+1:08d}.png")
            return blank > 0, []
        use_on_demand_sam2_frames(Path(chunk["path"]), torch,
            prefetch_depth=3, prefetch_to_device=True)
        state = predictor.init_state(video_path=chunk["path"],
            offload_video_to_cpu=True, offload_state_to_cpu=False)
        detector = CombinedCollapseDetector() if analyze else None
        analysis_size = (max(1, prepared["reviewWidth"]//4),
                         max(1, prepared["reviewHeight"]//4)) if analyze else None
        previous_gray, endpoint = None, None
        try:
            for obj in objects:
                with Image.open(obj["mask"]) as image:
                    mask = np.asarray(image.convert("L")) > 0
                predictor.add_new_mask(state, frame_idx=last_frame if reverse else 0,
                                       obj_id=obj["id"], mask=mask)
            written, last_progress = 0, 0.
            for index, ids, logits in predictor.propagate_in_video(state,
                    start_frame_idx=last_frame if reverse else 0,
                    max_frame_num_to_track=last_frame, reverse=reverse):
                union = (logits > 0).any(dim=0, keepdim=True)
                if index < count:
                    if reverse:
                        with Image.open(out / f"{chunk['start']+index+1:08d}.png") as image:
                            forward = np.asarray(image.convert("L")) > 0
                        union = union | torch.from_numpy(forward).to(union.device)
                    writer.submit([1], union, chunk["start"]+index, out)
                    if detector is not None:
                        gray = cv2.imread(str(Path(chunk["path"]) / f"{index:08d}.jpg"),
                                          cv2.IMREAD_GRAYSCALE)
                        if gray is None:
                            raise RuntimeError("Cannot read a frame for SAM3 repair analysis")
                        gray = cv2.resize(gray, analysis_size, interpolation=cv2.INTER_AREA)
                        coordinates, consistent = mapping(previous_gray, gray) \
                            if previous_gray is not None else (None, None)
                        low = torch.nn.functional.interpolate(union.float(),
                            size=(analysis_size[1], analysis_size[0]), mode="nearest")[0, 0].cpu().numpy() > 0
                        detector.step(low, coordinates, consistent, chunk["start"]+index)
                        previous_gray = gray
                if index == last_frame and not reverse:
                    endpoint = union[0, 0].cpu().numpy()
                prune_sam2_tracking_state(state, index, reverse, history)
                written += 1
                if time.monotonic()-last_progress >= 1:
                    fraction = written/(last_frame+1)
                    if reverse:
                        fraction = .5+.5*fraction
                    elif reserve_repair:
                        fraction *= .5
                    emit("SAM2", (chunk["start"]+count*fraction)*100/prepared["frameCount"],
                         f"{'Backward repair' if reverse else 'Tracking'} {len(objects)} objects; "
                         f"frame {chunk['start']+index+1}/{prepared['frameCount']}")
                    last_progress = time.monotonic()
            if written != last_frame+1:
                raise RuntimeError(f"SAM2 returned {written}/{last_frame+1} frames")
            return endpoint, detector.events if detector is not None else []
        finally:
            predictor.reset_state(state)
            state["images"].executor.shutdown(wait=True)
            del state

    repairs = []
    try:
        with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
            for chunk_index, chunk in enumerate(prepared["chunks"]):
                objects = records[chunk_index]
                count = chunk["end"]-chunk["start"]
                keyframe = chunk.get("repairKeyframe") if repair_enabled and count > 1 else None
                next_objects = records[keyframe] if keyframe is not None else []
                anchor, reliable = None, False
                if keyframe is not None:
                    next_masks = []
                    prefer_person = any(c.casefold() == "person" for c in config["concepts"])
                    for obj in next_objects:
                        with Image.open(obj["mask"]) as image:
                            mask = np.asarray(image.convert("L")) > 0
                        next_masks.append(mask)
                        if (not prefer_person or obj["concept"].casefold() == "person") and \
                                obj["score"] >= .5 and mask.mean() >= .01:
                            reliable = True
                    anchor = np.logical_or.reduce(next_masks) if next_masks else np.zeros(
                        (prepared["reviewHeight"], prepared["reviewWidth"]), bool)
                    reliable = bool(reliable and anchor.mean() < .95)
                eligible = keyframe is not None and reliable
                last_frame = chunk["repairFrame"] if eligible else count-1
                endpoint, events = run_pass(chunk, objects, last_frame,
                    analyze=eligible, reserve_repair=eligible)
                decision = dict(runBackward=False, reasons=["repair disabled" if not repair_enabled
                    else "no reliable next keyframe in the same scene"])
                if eligible:
                    disagreement = endpoint_disagreement(endpoint, anchor)
                    decision = decide(events, disagreement, reliable, True)
                    if decision["runBackward"]:
                        writer.flush()
                        emit("SAM2", (chunk["start"]+.5*count)*100/prepared["frameCount"],
                             "Foreground coverage lost; repairing from the next keyframe")
                        run_pass(chunk, next_objects, last_frame, reverse=True)
                repairs.append(dict(start=chunk["start"], end=chunk["end"],
                    eligible=eligible, **decision))
                emit("SAM2", chunk["end"]*100/prepared["frameCount"], "Object tracks saved")
    finally:
        writer.close()
    if len(list(out.glob("*.png"))) != prepared["frameCount"]:
        raise RuntimeError("Incomplete SAM2 mask sequence")
    save(Path(config["scratch"]) / "tracking.json", dict(
        selectiveBackwardRepair=repair_enabled,
        backwardIntervals=sum(item["runBackward"] for item in repairs), intervals=repairs))


def process(args):
    validate_concepts(args.concept)
    args.confidence = confidence_values(args.concept, args.confidence)
    if not args.source.is_file():
        raise FileNotFoundError("Source video is missing")
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "result.json").unlink(missing_ok=True)
    emit("preparing", 0, "Preparing SAM3 foreground processing")
    worker = str(Path(__file__).resolve())
    with tempfile.TemporaryDirectory(prefix=".sam3-work-", dir=args.output) as folder:
        scratch = Path(folder)
        prepared = prepare(args, scratch)
        config = dict(prepared=prepared, concepts=args.concept,
                      confidences=args.confidence,
                      selectiveBackwardRepair=args.selective_backward_repair, scratch=str(scratch),
                      sam3Runtime=str(args.sam3_runtime), runtime=str(args.runtime),
                      output=str(args.output))
        request = scratch / "request.json"
        save(request, config)
        for stage, python, lower, upper in (
                ("detect", args.sam3_python, 10, 25), ("track", sys.executable, 25, 82)):
            run_child([str(python), worker, "--stage", stage, "--request", str(request)], lower, upper)
        command = [sys.executable, str(Path(worker).with_name("vitmatte_worker.py")),
            "--source", str(args.source), "--output", str(args.output),
            "--runtime", str(args.runtime), "--mask-folder", str(args.output / ".rvm-masks"),
            "--model", "s", "--max-size", "1024", "--batch-size", "3",
            "--execution-mode", "eager", "--channels-last", "on", "--explicit-half", "off",
            "--encoder-preset", args.encoder_preset, "--start-ms", str(args.start_ms)]
        if args.end_ms is not None:
            command += ["--end-ms", str(args.end_ms)]
        run_child(command, 82, 99)
        receipt = dict(algorithm="sam3-person", concepts=args.concept,
            keyframeIntervalSeconds=5, sceneThreshold=.3, confidences=args.confidence,
            selectiveBackwardRepair=args.selective_backward_repair,
            repair=json.loads((scratch / "tracking.json").read_text()),
            objectsPerKeyframe=[len(x) for x in json.loads((scratch / "detection.json").read_text())],
            sam2Model="base-plus", sam2Execution="eager", encoderBatch=1,
            vitMatteModel="s", vitMatteDetail=1024, vitMatteBatch=3,
            channelsLast=True, explicitHalf=False)
        save(args.output / "sam3-pipeline.json", receipt)
    emit("complete", 100, "SAM3 foreground and alpha are ready for preview")


def self_test():
    import numpy as np
    import torch
    from sam3_tracking_repair import self_test as repair_self_test
    repair_self_test()
    validate_concepts(["person", "clothing", "red shoes"])
    assert confidence_values(["person", "hand"], None) == [.35, .35]
    assert confidence_values(["person", "hand"], [.35, .2]) == [.35, .2]
    for invalid in ([.35], [-.1, .2], [1.1, .2], [float("nan"), .2],
                    [float("inf"), .2], [True, .2], 0., False, ".35"):
        try:
            confidence_values(["person", "hand"], invalid)
        except ValueError:
            continue
        raise AssertionError("Invalid confidences accepted")

    class Processor:
        def __init__(self): self.calls = []
        def reset_all_prompts(self, state): pass
        def set_text_prompt(self, concept, state):
            self.calls.append((concept, self.confidence_threshold))
            count = int(self.confidence_threshold <= .4)
            return dict(masks=torch.ones((count, 1, 4, 4), dtype=torch.bool),
                        scores=torch.full((count,), .4))

    processor = Processor()
    candidates = prompt_candidates(processor, {}, ["hand", "clothing"], [.2, .6])
    assert processor.calls == [("hand", .2), ("clothing", .6)]
    assert len(candidates) == 1 and candidates[0]["concept"] == "hand"
    for invalid in ([], [" "], ["x"]*17, ["x"*201], ["person\nclothing"]):
        try:
            validate_concepts(invalid)
        except ValueError:
            continue
        raise AssertionError("Invalid concepts accepted")
    person = np.zeros((16, 16), bool)
    person[:, :8] = True
    other = ~person
    objects = select_objects([dict(concept="clothing", score=.9, mask=person),
        dict(concept="person", score=.8, mask=person),
        dict(concept="person", score=.7, mask=other)])
    assert len(objects) == 2 and all(x["concept"] == "person" for x in objects)
    assert select_objects([]) == []
    logits = torch.tensor([[[[float("nan"), -1., 1.]]], [[[1., -1., -1.]]]])
    assert (logits > 0).any(dim=0, keepdim=True).flatten().tolist() == [True, False, True]
    print("SAM3 pipeline worker self-test passed")


def main():
    parser = argparse.ArgumentParser()
    for name in ("source", "output", "runtime", "sam3-runtime", "sam3-python", "request"):
        parser.add_argument("--"+name, type=Path)
    parser.add_argument("--concept", action="append")
    parser.add_argument("--confidence", action="append", type=float)
    parser.add_argument("--selective-backward-repair", action=argparse.BooleanOptionalAction,
                        default=True)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--encoder-preset", choices=tuple(f"p{i}" for i in range(1, 8)), default="p5")
    parser.add_argument("--stage", choices=("detect", "track"))
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--validate", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    elif args.validate and args.sam3_runtime:
        validate_runtime(args.sam3_runtime)
    elif args.stage and args.request:
        config = json.loads(args.request.read_text(encoding="utf-8"))
        (detect if args.stage == "detect" else track)(config)
    elif all((args.source, args.output, args.runtime, args.sam3_runtime, args.sam3_python)):
        args.concept = args.concept or ["person", "clothing", "hand", "mouth", "skin", "leg"]
        process(args)
    else:
        parser.error("--source, --output, --runtime, --sam3-runtime and --sam3-python are required")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("error", 0, str(error))
        raise
