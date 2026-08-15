"""Pinned QuickPlayer adapter for FudanCVL/SAM2Matting.

Stdout is reserved for NDJSON protocol/progress messages.  Diagnostics go to
stderr.  The adapter intentionally uses eager BF16 and the upstream SDPA path.
"""

from __future__ import annotations

import argparse
import contextlib
import gc
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from fractions import Fraction
from pathlib import Path

SOURCE_REVISION = "73dd721d77b56749248aefe5e8824d7f61b9d13c"
CHECKPOINT_REVISION = "4315db9c60d27fde396b09765748a0ca6c97bed5"
CHECKPOINTS = {
    "sam2.1-tiny": (
        "SAM2Matting-SAM2.1Tiny.pt",
        215_569_778,
        "5b9321e3b51bc20f5b84c208746cc083dd3053dd701590f2e88dc8640afcc39d",
        "configs/sam2matting-sam2.1tiny.yaml",
    ),
    "sam2.1-base-plus": (
        "SAM2Matting-SAM2.1Base+.pt",
        383_180_506,
        "1f0eb2eda3e8bc9101eafc0b30b8b8fcae1ff83d8fd3adc18e2f3b410fdaae60",
        "configs/sam2matting-sam2.1base+.yaml",
    ),
    "sam3": (
        "SAM2Matting-SAM3.pt",
        3_509_720_141,
        "7102d695be6070b39acd67464f93207df725514a688b545ed1267d913d3b9c7d",
        None,
    ),
}


def emit(stage, percent, message, **extra):
    print(json.dumps({"stage": stage, "percent": float(percent),
                      "message": message, **extra}), flush=True)


def fail(message):
    emit("error", 0, str(message))


def run_process(args, capture=True):
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    return subprocess.run(args, check=True, text=True,
                          capture_output=capture, creationflags=creationflags)


def ffmpeg():
    return os.environ.get("IQP_FFMPEG", "ffmpeg")


def ffprobe():
    return os.environ.get("IQP_FFPROBE", "ffprobe")


def cancelled(request):
    path = request.get("cancelPath")
    if path and os.path.exists(path):
        raise KeyboardInterrupt("SAM2Matting job cancelled")


def marker(runtime):
    path = runtime / "environment.json"
    if not path.is_file():
        raise RuntimeError("Setup required: environment.json is missing")
    # Windows PowerShell 5.1 writes UTF-8 JSON with a BOM; accept both forms.
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if (value.get("environmentSpecVersion") != "sam2matting-v1" or
            value.get("sourceRevision") != SOURCE_REVISION or
            value.get("checkpointRevision") != CHECKPOINT_REVISION):
        raise RuntimeError("Setup required: the SAM2Matting environment revision is incompatible")
    return value


def cheap_checkpoint_check(runtime, tracker, expected_hash):
    if tracker not in CHECKPOINTS:
        raise RuntimeError(f"Unsupported SAM2Matting tracker: {tracker}")
    name, size, digest, _ = CHECKPOINTS[tracker]
    path = runtime / "checkpoints" / name
    if not path.is_file() or path.stat().st_size != size or expected_hash != digest:
        raise RuntimeError(f"Setup required: {name} is missing or incompatible")
    return path


def normalize_concepts(values):
    normalized = []
    seen = set()
    for line in values or []:
        value = str(line).strip()
        folded = value.casefold()
        if not value or folded in seen:
            continue
        if len(value) > 200:
            raise RuntimeError("A foreground concept cannot exceed 200 characters")
        seen.add(folded)
        normalized.append(value)
    if not normalized:
        raise RuntimeError("Enter at least one foreground concept for SAM3")
    if len(normalized) > 64:
        raise RuntimeError("A SAM3 job cannot contain more than 64 concepts")
    return normalized


def validate_scene_contract(request, fps):
    def canonical_frame(milliseconds):
        value = Fraction(int(milliseconds), 1000) * fps
        return (value.numerator * 2 + value.denominator) // (2 * value.denominator)

    scenes = request.get("scenes") or []
    seen = set()
    for clip in request.get("clips") or []:
        values = sorted((item for item in scenes
                         if item["clipId"].casefold() == clip["id"].casefold()),
                        key=lambda item: item["startFrame"])
        expected_start = canonical_frame(clip["startMs"])
        expected_end = max(expected_start + 1, canonical_frame(clip["endMs"]))
        if not values or values[0]["startFrame"] != expected_start or \
                values[-1]["endFrameExclusive"] != expected_end:
            raise RuntimeError("Saved scenes do not cover every canonical clip frame")
        for index, scene in enumerate(values):
            if scene["id"].casefold() in seen or \
                    scene["endFrameExclusive"] <= scene["startFrame"] or \
                    index and values[index - 1]["endFrameExclusive"] != scene["startFrame"]:
                raise RuntimeError("Saved scenes are duplicated, overlapping, or non-contiguous")
            seen.add(scene["id"].casefold())
    if len(seen) != len(scenes):
        raise RuntimeError("A saved scene references an unknown clip")


def full_checkpoint_check(path, expected_size, expected_hash):
    if path.stat().st_size != expected_size:
        raise RuntimeError(f"Checkpoint size mismatch: {path.name}")
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest().lower() != expected_hash:
        raise RuntimeError(f"Checkpoint SHA-256 mismatch: {path.name}")


def probe_source(source):
    result = run_process([
        ffprobe(), "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate,time_base",
        "-show_entries", "format=duration", "-of", "json", source,
    ])
    data = json.loads(result.stdout)
    stream = data["streams"][0]
    rate = stream.get("avg_frame_rate")
    if not rate or rate == "0/0":
        rate = stream["r_frame_rate"]
    fps = Fraction(rate)
    if fps <= 0:
        raise RuntimeError("The input frame rate is invalid")
    return {
        "width": int(stream["width"]), "height": int(stream["height"]),
        "frameRate": f"{fps.numerator}/{fps.denominator}",
        "fps": fps, "timeBase": stream.get("time_base") or "",
        "duration": float(data["format"]["duration"]),
    }


def extract_scene(source, directory, start_frame, frame_count, fps):
    directory.mkdir(parents=True, exist_ok=True)
    start_seconds = float(Fraction(start_frame, 1) / fps)
    filter_rate = f"{fps.numerator}/{fps.denominator}"
    run_process([
        ffmpeg(), "-y", "-v", "error", "-ss", f"{start_seconds:.9f}",
        "-i", source, "-an", "-vf", f"fps={filter_rate}",
        "-frames:v", str(frame_count), "-pix_fmt", "rgb24",
        str(directory / "%08d.png"),
    ], capture=True)
    files = sorted(directory.glob("*.png"))
    if len(files) != frame_count:
        raise RuntimeError(
            f"Scene extraction returned {len(files)} of {frame_count} canonical frames")
    return files


def load_variant(runtime, tracker):
    import torch

    source = runtime / "source" / "SAM2Matting"
    if not source.is_dir():
        raise RuntimeError("Setup required: pinned Fudan source is missing")
    os.chdir(source)
    sys.path.insert(0, str(source))
    checkpoint = runtime / "checkpoints" / CHECKPOINTS[tracker][0]
    emit("checkpoint-loading", 1, f"Loading {tracker} checkpoint")
    if tracker == "sam3":
        from sam3.model_builder import build_sam3_video_predictor
        with contextlib.redirect_stdout(sys.stderr):
            predictor = build_sam3_video_predictor(
                gpus_to_use=[0], checkpoint_path=str(checkpoint),
                strict_state_dict_loading=False,
                bpe_path=str(source / "sam3" / "bpe_simple_vocab_16e6.txt.gz"),
            )
    else:
        from sam2.build_sam import build_sam2matting_video_predictor
        with contextlib.redirect_stdout(sys.stderr):
            predictor = build_sam2matting_video_predictor(
                CHECKPOINTS[tracker][3], str(checkpoint), device="cuda",
                hydra_overrides_extra=[],
            )
    emit("checkpoint-loading", 5, f"Loaded {tracker} in eager BF16 mode")
    return predictor


def alpha_array(value):
    import numpy as np
    import torch

    if torch.is_tensor(value):
        value = value.detach().float().cpu().numpy()
    value = np.asarray(value, dtype=np.float32)
    while value.ndim > 2:
        value = value[0]
    return np.clip(value, 0.0, 1.0)


def process_sam2_scene(predictor, request, scene, scene_dir, union):
    import numpy as np
    import torch
    from PIL import Image

    prompt = next((item for item in request["prompts"]
                   if item["sceneId"].lower() == scene["id"].lower()), None)
    if prompt is None:
        raise RuntimeError(f"Scene {scene['id']} has no initial mask")
    local_prompt = int(prompt["promptFrame"] - scene["startFrame"])
    mask = np.asarray(Image.open(prompt["initialMaskPath"]).convert("L")) > 0
    state = predictor.init_state(video_path=str(scene_dir))
    try:
        predictor.reset_state(state)
        predictor.add_new_mask(
            inference_state=state, frame_idx=local_prompt, obj_id=1,
            mask=torch.from_numpy(mask),
        )
        count = union.shape[0]
        for index, _, _, alpha, _ in predictor.propagate_in_video(
                state, start_frame_idx=local_prompt,
                max_frame_num_to_track=count - local_prompt, reverse=False):
            cancelled(request)
            union[index] = np.maximum(union[index], alpha_array(alpha))
            if index % 10 == 0:
                emit("tracking", 0, f"Forward tracking frame {index + 1}/{count}")
        if local_prompt > 0:
            for index, _, _, alpha, _ in predictor.propagate_in_video(
                    state, start_frame_idx=local_prompt - 1,
                    max_frame_num_to_track=local_prompt, reverse=True):
                cancelled(request)
                union[index] = np.maximum(union[index], alpha_array(alpha))
                if index % 10 == 0:
                    emit("tracking", 0, f"Reverse tracking frame {index + 1}/{count}")
    finally:
        try:
            predictor.reset_state(state)
        finally:
            del state


def ensure_frame_cache(model, state, frame_idx):
    if frame_idx not in state["feature_cache"]:
        model._prepare_backbone_feats(state, frame_idx, reverse=False)


def sam3_alpha(model, state, frame_idx, mask_hw):
    import numpy as np
    import torch
    import torch.nn.functional as functional

    ensure_frame_cache(model, state, frame_idx)
    image, cache = state["feature_cache"][frame_idx]
    if image.dim() == 3:
        image = image.unsqueeze(0)
    image = image.to("cuda")
    high_res_features = list(cache["tracker_backbone_out"]["backbone_fpn"])
    if torch.is_tensor(mask_hw):
        mask = mask_hw.detach().to(device="cuda", dtype=torch.float32)
    else:
        mask = torch.as_tensor(np.asarray(mask_hw), device="cuda",
                               dtype=torch.float32)
    while mask.dim() > 2:
        mask = mask[0]
    mask = mask > 0
    mask_288 = functional.interpolate(
        mask[None, None], size=(288, 288), mode="bilinear",
        align_corners=False, antialias=True,
    )
    alpha, _, _ = model.tracker._forward_alpha_heads(
        input=image, backbone_features=None, point_inputs=None,
        mask_inputs=(mask_288 > 0).float(), unknown_region_inputs=None,
        high_res_features=high_res_features, image=None, trimap_input=None,
    )
    alpha = functional.interpolate(
        alpha.float(), size=(state["orig_height"], state["orig_width"]),
        mode="bilinear", align_corners=False,
    )
    return alpha.squeeze().detach().float().cpu().numpy().clip(0, 1)


def process_sam3_scene(predictor, request, scene_dir, union):
    import numpy as np

    response = predictor.handle_request({
        "type": "start_session", "resource_path": str(scene_dir),
    })
    session_id = response["session_id"]
    try:
        for concept_index, concept in enumerate(request["concepts"]):
            cancelled(request)
            emit("concept-detection", 5,
                 f"Detecting concept {concept_index + 1}/{len(request['concepts'])}: {concept}")
            state = predictor._get_session(session_id)["state"]
            predictor.model.add_prompt(
                inference_state=state, frame_idx=0, text_str=concept)
            for response in predictor.handle_stream_request({
                    "type": "propagate_in_video", "session_id": session_id,
                    "propagation_direction": "forward"}):
                cancelled(request)
                index = int(response["frame_index"])
                outputs = response["outputs"]
                masks = outputs.get("out_binary_masks", [])
                for mask in masks:
                    cancelled(request)
                    alpha = sam3_alpha(predictor.model, state, index, mask)
                    union[index] = np.maximum(union[index], alpha)
                if index % 10 == 0:
                    emit("tracking", 0,
                         f"Concept {concept_index + 1}/{len(request['concepts'])}, frame {index + 1}/{union.shape[0]}")
    finally:
        predictor.handle_request({"type": "close_session", "session_id": session_id})


def encode_alpha(raw_path, destination, width, height, fps, count):
    run_process([
        ffmpeg(), "-y", "-v", "error", "-f", "rawvideo",
        "-pixel_format", "gray16le", "-video_size", f"{width}x{height}",
        "-framerate", f"{fps.numerator}/{fps.denominator}", "-i", str(raw_path),
        "-frames:v", str(count), "-an", "-c:v", "ffv1", "-level", "3",
        "-pix_fmt", "gray16le", "-color_range", "pc", str(destination),
    ])


def encode_foreground(source, destination, start_frame, fps, count):
    # Use the same canonical frame origin as scene extraction and alpha.  This
    # avoids independently rounding a millisecond clip boundary on VFR input.
    start = max(0.0, float(Fraction(start_frame, 1) / fps))
    duration = count / float(fps)
    run_process([
        ffmpeg(), "-y", "-v", "error", "-ss", f"{start:.9f}", "-i", source,
        "-t", f"{duration:.9f}", "-map", "0:v:0", "-map", "0:a:0?",
        "-vf", f"fps={fps.numerator}/{fps.denominator}", "-frames:v", str(count),
        "-c:v", "libx264", "-preset", "medium", "-crf", "18",
        "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
        "-movflags", "+faststart", str(destination),
    ])


def probe_output(path):
    result = run_process([
        ffprobe(), "-v", "error", "-count_frames", "-select_streams", "v:0",
        "-show_entries", "stream=width,height,avg_frame_rate,time_base,codec_name,pix_fmt,nb_read_frames",
        "-of", "json", str(path),
    ])
    stream = json.loads(result.stdout)["streams"][0]
    return {
        "width": int(stream["width"]), "height": int(stream["height"]),
        "frameRate": stream["avg_frame_rate"], "timeBase": stream["time_base"],
        "codec": stream["codec_name"], "pixelFormat": stream["pix_fmt"],
        "frames": int(stream["nb_read_frames"]),
    }


def write_union(union, raw_stream):
    import numpy as np

    for index in range(union.shape[0]):
        quantized = np.rint(np.clip(union[index], 0, 1) * 65535.0).astype("<u2")
        raw_stream.write(quantized.tobytes(order="C"))


def run_job(request, loaded=None):
    import numpy as np
    import torch

    if request.get("protocolVersion") != 1 or request.get("type") != "run":
        raise RuntimeError("Unsupported SAM2Matting protocol contract")
    if request.get("sourceRevision") != SOURCE_REVISION or request.get(
            "checkpointRevision") != CHECKPOINT_REVISION:
        raise RuntimeError("Setup required: queued source/checkpoint revision is incompatible")
    runtime = Path(request["runtimePath"])
    environment = marker(runtime)
    tracker = request["tracker"]
    checkpoint = cheap_checkpoint_check(runtime, tracker,
                                        request.get("checkpointSha256"))
    if tracker == "sam3" and not request.get("concepts"):
        raise RuntimeError("SAM3 requires at least one foreground concept")
    if tracker != "sam3" and request.get("concepts"):
        raise RuntimeError("SAM2.1 trackers do not accept text concepts")
    source = request["sourcePath"]
    media = probe_source(source)
    validate_scene_contract(request, media["fps"])
    if tracker == "sam3":
        normalized = normalize_concepts(request.get("concepts"))
        if normalized != request.get("concepts") or request.get("prompts"):
            raise RuntimeError("SAM3 concepts are not normalized or the job contains scene masks")
    output = Path(request["outputPath"])
    output.mkdir(parents=True, exist_ok=True)
    emit("preflight", 0, "Validated saved SAM2Matting job contract")
    predictor = loaded if loaded is not None else load_variant(runtime, tracker)
    scenes_by_clip = {}
    for scene in request["scenes"]:
        scenes_by_clip.setdefault(scene["clipId"].lower(), []).append(scene)
    prompt_ids = [item["sceneId"].lower() for item in request.get("prompts", [])]
    if tracker != "sam3" and (len(prompt_ids) != len(set(prompt_ids)) or
                              set(prompt_ids) != {scene["id"].lower()
                                                  for scene in request["scenes"]}):
        raise RuntimeError("Every SAM2.1 scene requires exactly one saved initial mask")
    results = []
    work = output / ".sam2matting-work"
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)
    try:
        total_scenes = max(1, len(request["scenes"]))
        completed_scenes = 0
        for clip in request["clips"]:
            cancelled(request)
            clip_scenes = sorted(scenes_by_clip.get(clip["id"].lower(), []),
                                 key=lambda item: item["startFrame"])
            if not clip_scenes:
                raise RuntimeError(f"Clip {clip['id']} has no processing scenes")
            clip_dir = output / "clips" / clip["id"]
            clip_dir.mkdir(parents=True, exist_ok=True)
            raw_path = work / f"{clip['id']}.gray16le"
            decoded_count = 0
            with raw_path.open("wb") as raw:
                for scene in clip_scenes:
                    cancelled(request)
                    count = int(scene["endFrameExclusive"] - scene["startFrame"])
                    scene_dir = work / f"scene-{scene['id']}"
                    emit("scene-analysis", 5 + 75 * completed_scenes / total_scenes,
                         f"Extracting scene {completed_scenes + 1}/{total_scenes}")
                    extract_scene(source, scene_dir, int(scene["startFrame"]), count,
                                  media["fps"])
                    union_path = work / f"union-{scene['id']}.float32"
                    union = np.memmap(union_path, mode="w+", dtype=np.float32,
                                      shape=(count, media["height"], media["width"]))
                    union[:] = 0.0
                    with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
                        if tracker == "sam3":
                            process_sam3_scene(predictor, request, scene_dir, union)
                        else:
                            process_sam2_scene(predictor, request, scene, scene_dir, union)
                    emit("matting", 5 + 75 * (completed_scenes + .8) / total_scenes,
                         f"Writing progressive alpha for scene {completed_scenes + 1}/{total_scenes}")
                    write_union(union, raw)
                    union.flush()
                    del union
                    union_path.unlink(missing_ok=True)
                    shutil.rmtree(scene_dir, ignore_errors=True)
                    decoded_count += count
                    completed_scenes += 1
            emit("encoding", 82, f"Encoding clip {len(results) + 1}/{len(request['clips'])}")
            alpha_path = clip_dir / "alpha.mkv"
            foreground_path = clip_dir / "foreground.mp4"
            encode_alpha(raw_path, alpha_path, media["width"], media["height"],
                         media["fps"], decoded_count)
            encode_foreground(source, foreground_path,
                              int(clip_scenes[0]["startFrame"]),
                              media["fps"], decoded_count)
            alpha_info = probe_output(alpha_path)
            foreground_info = probe_output(foreground_path)
            if (alpha_info["frames"] != decoded_count or
                    foreground_info["frames"] != decoded_count or
                    alpha_info["width"] != foreground_info["width"] or
                    alpha_info["height"] != foreground_info["height"] or
                    Fraction(alpha_info["frameRate"]) != Fraction(foreground_info["frameRate"]) or
                    alpha_info["codec"] != "ffv1" or alpha_info["pixelFormat"] != "gray16le"):
                raise RuntimeError("Foreground/alpha validation failed")
            duration_ms = round(decoded_count / float(media["fps"]) * 1000)
            results.append({
                "clipId": clip["id"], "width": media["width"],
                "height": media["height"], "frameRate": media["frameRate"],
                "timeBase": alpha_info["timeBase"], "durationMs": duration_ms,
                "decodedFrameCount": decoded_count,
                "foregroundFrameCount": foreground_info["frames"],
                "alphaFrameCount": alpha_info["frames"],
                "foregroundCodec": foreground_info["codec"],
                "alphaCodec": alpha_info["codec"],
                "alphaPixelFormat": alpha_info["pixelFormat"],
                "tracker": tracker, "executionMode": "eager-bf16-sdpa",
                "encoder": "libx264", "encoderPreset": "medium",
                "firstTimestamp": 0,
                "lastTimestamp": max(0, decoded_count - 1),
            })
            raw_path.unlink(missing_ok=True)
        first = results[0]
        contract = {**first, "clips": results, "tracker": tracker,
                    "executionMode": "eager-bf16-sdpa"}
        temporary = output / "result.json.tmp"
        temporary.write_text(json.dumps(contract, indent=2), encoding="utf-8")
        os.replace(temporary, output / "result.json")
        emit("validation", 100, "SAM2Matting output passed strict media validation")
        return contract
    finally:
        shutil.rmtree(work, ignore_errors=True)
        if loaded is None:
            del predictor
            gc.collect()
            if torch.cuda.is_available():
                torch.cuda.empty_cache()


class PersistentHost:
    def __init__(self, runtime):
        self.runtime = Path(runtime)
        self.tracker = None
        self.predictor = None
        self.cancel_paths = {}

    def unload(self):
        import torch
        self.predictor = None
        self.tracker = None
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    def handle(self, command):
        kind = command.get("type")
        if command.get("protocolVersion") != 1:
            raise RuntimeError("Unsupported protocol version")
        if kind == "loadVariant":
            tracker = command["tracker"]
            if tracker != self.tracker:
                self.unload()
                self.predictor = load_variant(self.runtime, tracker)
                self.tracker = tracker
            return {"type": "variantLoaded", "tracker": tracker}
        if kind == "run":
            request = json.loads(Path(command["requestPath"]).read_text(encoding="utf-8"))
            if request["tracker"] != self.tracker:
                self.handle({"protocolVersion": 1, "type": "loadVariant",
                             "tracker": request["tracker"]})
            result = run_job(request, self.predictor)
            return {"type": "completed", "jobId": request["jobId"], "result": result}
        if kind == "cancel":
            path = self.cancel_paths.get(command.get("jobId"))
            if path:
                Path(path).write_text("cancel", encoding="utf-8")
            return {"type": "cancelRequested", "jobId": command.get("jobId")}
        if kind in {"openMaskSession", "loadMaskFrame", "predictMask", "closeMaskSession"}:
            raise RuntimeError("Mask-session commands require the interactive host adapter")
        if kind == "shutdown":
            self.unload()
            return {"type": "shutdown", "shutdown": True}
        raise RuntimeError(f"Unknown command: {kind}")


def host_main(runtime):
    host = PersistentHost(runtime)
    for line in sys.stdin:
        try:
            command = json.loads(line)
            response = host.handle(command)
            print(json.dumps(response), flush=True)
            if response.get("shutdown"):
                break
        except Exception as error:
            print(json.dumps({"type": "error", "message": str(error)}), flush=True)


def validate(runtime, full_hash=False):
    runtime = Path(runtime)
    marker(runtime)
    for tracker, (name, size, digest, _) in CHECKPOINTS.items():
        path = cheap_checkpoint_check(runtime, tracker, digest)
        if full_hash:
            emit("setup", 0, f"Hashing {name}")
            full_checkpoint_check(path, size, digest)
    source = runtime / "source" / "SAM2Matting"
    required = [source / "sam2" / "build_sam.py",
                source / "sam3" / "model_builder.py",
                source / "sam3" / "bpe_simple_vocab_16e6.txt.gz"]
    if not all(path.is_file() for path in required):
        raise RuntimeError("Pinned Fudan source contract is incomplete")
    if full_hash:
        import torch

        for tracker in CHECKPOINTS:
            emit("setup", 0, f"Validating checkpoint key contract for {tracker}")
            predictor = load_variant(runtime, tracker)
            checkpoint = torch.load(
                runtime / "checkpoints" / CHECKPOINTS[tracker][0],
                map_location="cpu", weights_only=True)
            state_dict = checkpoint.get("model", checkpoint)
            target = predictor.model if tracker == "sam3" else predictor
            missing, unexpected = target.load_state_dict(state_dict, strict=False)
            if missing or unexpected:
                raise RuntimeError(
                    f"Checkpoint key contract changed for {tracker}: "
                    f"missing={sorted(missing)!r}, unexpected={sorted(unexpected)!r}")
            del checkpoint, state_dict, target, predictor
            gc.collect()
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        from sam3.perflib.connected_components import connected_components
        sample = torch.zeros((1, 1, 8, 8), dtype=torch.bool, device="cuda")
        sample[:, :, 2:6, 2:6] = True
        labels, counts = connected_components(sample)
        if int(counts.max().item()) != 16 or int(labels.max().item()) < 1:
            raise RuntimeError("Native Windows connected-components validation failed")
    print(json.dumps({"status": "ok", "sourceRevision": SOURCE_REVISION,
                      "checkpointRevision": CHECKPOINT_REVISION}), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request")
    parser.add_argument("--host", action="store_true")
    parser.add_argument("--runtime")
    parser.add_argument("--validate", action="store_true")
    parser.add_argument("--full-hash", action="store_true")
    args = parser.parse_args()
    if args.validate:
        validate(args.runtime, args.full_hash)
    elif args.host:
        host_main(args.runtime)
    elif args.request:
        request = json.loads(Path(args.request).read_text(encoding="utf-8"))
        run_job(request)
    else:
        parser.error("one of --request, --host, or --validate is required")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        emit("cleanup", 0, "SAM2Matting job cancelled")
        raise SystemExit(2)
    except Exception as error:
        fail(error)
        traceback.print_exc(file=sys.stderr)
        raise SystemExit(1)
