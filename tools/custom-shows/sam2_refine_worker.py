#!/usr/bin/env python3
"""Persistent SAM2 video-mask propagation and correction worker."""
import argparse, json, os, shutil, subprocess, sys
from pathlib import Path

from rvm_worker import executable, probe
from videomama_worker import (load_sam2, model_size, sam2_vos_cache_marker,
                              sam2_vos_optimized, sam2_vos_uses_cudagraphs,
                              use_on_demand_sam2_frames)
from sam_mask_worker import automatic_foreground

VOS_MIN_FRAMES = 16000


def send(**values):
    print(json.dumps(values), flush=True)


def extract_frames(source, folder, start, duration, rate, width, height, total):
    command = [executable("ffmpeg"), "-y", "-v", "error", "-ss", f"{start:.6f}",
        "-i", str(source), "-t", f"{duration:.6f}", "-map", "0:v:0", "-vf",
        f"fps={rate},scale={width}:{height}:flags=lanczos,setsar=1", "-q:v", "2",
        str(folder / "%08d.jpg"), "-progress", "pipe:1", "-nostats"]
    process = subprocess.Popen(command, stdout=subprocess.PIPE, text=True)
    for line in process.stdout:
        if line.startswith("frame="):
            count = int(line.partition("=")[2])
            send(status="progress", percent=min(15, count * 15 / total),
                 message=f"Extracted {count}/{total} review frames")
    if process.wait() != 0:
        raise RuntimeError("SAM2 review-frame extraction failed")


def mask_from_logits(object_ids, logits, frame_index, mask_folder):
    import numpy as np
    from PIL import Image
    ids = object_ids.tolist() if hasattr(object_ids, "tolist") else list(object_ids)
    if 1 not in ids:
        raise RuntimeError(f"SAM2 lost the selected foreground at frame {frame_index + 1}")
    mask = (logits[ids.index(1)] > 0).squeeze().byte().cpu().numpy() * 255
    Image.fromarray(mask.astype(np.uint8), "L").save(
        mask_folder / f"{frame_index + 1:08d}.png")


def correction_limits(frame, correction_frames, total):
    return (max((value for value in correction_frames if value < frame), default=0),
            min((value for value in correction_frames if value > frame), default=total - 1))


def compiled_vos_worthwhile(frame_count):
    return frame_count >= VOS_MIN_FRAMES


def use_optimized_vos(torch, device, frame_count):
    forced = os.environ.get("IQP_SAM2_FORCE_VOS")
    if forced is not None:
        return forced == "1" and sam2_vos_optimized(torch, device)
    return sam2_vos_optimized(torch, device) and compiled_vos_worthwhile(frame_count)


def propagation_range(start, total, reverse, max_frames):
    distance = start if reverse else total - start - 1
    if max_frames is not None: distance = min(distance, max_frames)
    return (start - distance, start) if reverse else (start, start + distance)


def interactive_forwards(predictor):
    forwards = []
    for component in (predictor.sam_prompt_encoder, predictor.sam_mask_decoder):
        compiled = component.forward
        eager = getattr(compiled, "_torchdynamo_orig_callable", None)
        if eager is not None:
            forwards.append((component, compiled, eager))
    return forwards


def use_compiled_interactive_forwards(forwards, enabled):
    for component, compiled, eager in forwards:
        component.forward = compiled if enabled else eager


def remember_prompt_preview(state, baselines, frame, obj_id=1):
    obj = state["obj_id_to_idx"][obj_id]
    if frame not in baselines:
        points = state["point_inputs_per_obj"][obj]
        masks = state["mask_inputs_per_obj"][obj]
        baselines[frame] = (frame in points, points.get(frame),
                            frame in masks, masks.get(frame))


def clear_prompt_preview(state, baselines, frame, obj_id=1):
    obj = state["obj_id_to_idx"][obj_id]
    had_points, old_points, had_mask, old_mask = baselines.pop(
        frame, (False, None, False, None))
    points = state["point_inputs_per_obj"][obj]
    masks = state["mask_inputs_per_obj"][obj]
    if had_points: points[frame] = old_points
    else: points.pop(frame, None)
    if had_mask: masks[frame] = old_mask
    else: masks.pop(frame, None)
    for outputs in state["temp_output_dict_per_obj"][obj].values():
        outputs.pop(frame, None)
    outputs = state["output_dict_per_obj"][obj]
    original = outputs["cond_frame_outputs"].get(frame)
    return original if original is not None else outputs["non_cond_frame_outputs"].get(frame)


def propagate(predictor, state, mask_folder, start, total, reverse, progress_start,
              progress_size, message, max_frames=None, mark_step=None):
    maximum = (start + 1 if reverse else total - start) if max_frames is None \
        else max_frames + 1
    count = 0
    range_start, range_end = propagation_range(start, total, reverse, max_frames)
    iterator = iter(predictor.propagate_in_video(
        state, start_frame_idx=start, reverse=reverse,
        max_frame_num_to_track=max_frames))
    while True:
        if mark_step: mark_step()
        try: frame_index, object_ids, logits = next(iterator)
        except StopIteration: break
        mask_from_logits(object_ids, logits, frame_index, mask_folder)
        count += 1
        send(status="progress",
             percent=progress_start + count * progress_size / max(1, maximum),
             message=f"{message} {count}/{maximum} frames", frameIndex=frame_index,
             rangeStart=range_start, rangeEnd=range_end, reverse=reverse)


def process(args):
    import numpy as np
    import torch
    from PIL import Image

    source, runtime = args.source.resolve(), args.runtime.resolve()
    frames, masks = args.frames.resolve(), args.masks.resolve()
    shutil.rmtree(frames, ignore_errors=True)
    shutil.rmtree(masks, ignore_errors=True)
    frames.mkdir(parents=True); masks.mkdir(parents=True)
    width, height, rate, fps, source_duration = probe(source)
    start, end = args.start_ms / 1000, args.end_ms / 1000
    if start < 0 or end <= start or end > source_duration + .1:
        raise RuntimeError("clip range is outside the source video")
    duration = min(end, source_duration) - start
    expected = max(1, round(duration * fps))
    review_width, review_height = model_size(width, height)
    extract_frames(source, frames, start, duration, rate,
                   review_width, review_height, expected)
    frame_files = sorted(frames.glob("*.jpg"))
    if not frame_files: raise RuntimeError("The clip has no review frames")
    total = len(frame_files)
    initial = np.asarray(Image.open(args.mask).convert("L").resize(
        (review_width, review_height), Image.Resampling.NEAREST)) >= 128
    device = "cuda" if torch.cuda.is_available() else "cpu"
    bf16 = device == "cuda" and torch.cuda.get_device_capability()[0] >= 8
    optimized = use_optimized_vos(torch, device, total)
    first_compile = optimized and not sam2_vos_cache_marker(runtime).is_file()
    if optimized:
        send(status="progress", percent=15,
             message="Compiling optimized SAM2 (one-time cache build; later processing "
             "reuses it). This may take several minutes..." if first_compile else
             "Loading cached optimized SAM2; initializing this worker...")
    else:
        send(status="progress", percent=15,
             message=f"Loading SAM2 on {device.upper()}...")
    predictor = load_sam2(runtime, torch, device, optimized=optimized)
    # Later corrections must remain anchors; otherwise propagation immediately
    # recomputes the corrected frame.
    predictor.add_all_frames_to_correct_as_cond = True
    mark_step = torch.compiler.cudagraph_mark_step_begin \
        if optimized and sam2_vos_uses_cudagraphs() else None
    use_on_demand_sam2_frames(frames, torch)
    with torch.inference_mode(), torch.autocast(device_type=device,
            dtype=torch.bfloat16, enabled=bf16):
        if mark_step: mark_step()
        state = predictor.init_state(str(frames), offload_video_to_cpu=True,
                                     offload_state_to_cpu=device != "cuda")
        if mark_step: mark_step()
        predictor.add_new_mask(state, frame_idx=0, obj_id=1, mask=initial)
        correction_frames = {0}
        propagate(predictor, state, masks, 0, total, False, 15, 85,
                  "Tracked", mark_step=mark_step)
        if optimized: sam2_vos_cache_marker(runtime).touch()
        prompt_forwards = interactive_forwards(predictor) if optimized else []
        use_compiled_interactive_forwards(prompt_forwards, False)
        send(status="ready", frameCount=total, fps=fps,
             width=review_width, height=review_height, device=device,
             precision="BF16" if bf16 else "FP32", optimized=optimized,
             checkpoint="SAM2.1 Hiera Base+")
        preview_baselines = {}
        for line in sys.stdin:
            request = json.loads(line)
            if request.get("command") == "quit": break
            command = request.get("command")
            if command not in ("prompt", "auto", "reset", "update"):
                send(status="error", message="Unknown SAM2 editor command")
                continue
            frame = int(request["frame"])
            if not 0 <= frame < total:
                send(status="error", message="Correction frame is outside the clip")
                continue
            if command != "update":
                if command == "reset":
                    original = clear_prompt_preview(state, preview_baselines, frame)
                    if original is None:
                        raise RuntimeError("The original tracked mask is unavailable")
                    _, logits = predictor._get_orig_video_res_output(
                        state, original["pred_masks"].to(device, non_blocking=True))
                    mask_from_logits(state["obj_ids"], logits, frame, masks)
                    send(status="preview", frame=frame, automatic=False)
                    continue
                points = np.asarray(request.get("points", []), np.float32)
                labels = np.asarray(request.get("labels", []), np.int32)
                if len(points) != len(labels) or command == "prompt" and len(points) == 0:
                    send(status="error", message="Add at least one correction click")
                    continue
                remember_prompt_preview(state, preview_baselines, frame)
                if command == "auto":
                    frame_image = np.asarray(Image.open(frame_files[frame]).convert("RGB"))
                    if mark_step: mark_step()
                    automatic, candidate_count = automatic_foreground(
                        predictor, frame_image, points, labels, torch, device, bf16)
                    if mark_step: mark_step()
                    _, object_ids, logits = predictor.add_new_mask(
                        state, frame_idx=frame, obj_id=1, mask=automatic)
                else:
                    if mark_step: mark_step()
                    _, object_ids, logits = predictor.add_new_points_or_box(
                        state, frame_idx=frame, obj_id=1, points=points, labels=labels,
                        clear_old_points=True)
                    candidate_count = None
                mask_from_logits(object_ids, logits, frame, masks)
                send(status="preview", frame=frame,
                     candidates=candidate_count, automatic=command == "auto")
                continue
            # Meta's optimized predictor calls a missing per-object helper when its
            # automatic stale-memory option is enabled. This worker has one combined
            # foreground object, so its working all-object helper is equivalent.
            predictor._clear_non_cond_mem_around_input(state, frame)
            previous, following = correction_limits(frame, correction_frames, total)
            use_compiled_interactive_forwards(prompt_forwards, True)
            try:
                if frame > previous:
                    propagate(predictor, state, masks, frame, total, True, 0, 50,
                              "Updated backward", frame - previous, mark_step)
                propagate(predictor, state, masks, frame, total, False,
                          50 if frame > previous else 0, 50 if frame > previous else 100,
                          "Updated forward", following - frame, mark_step)
            finally:
                use_compiled_interactive_forwards(prompt_forwards, False)
            correction_frames.add(frame)
            preview_baselines.pop(frame, None)
            send(status="ready", frameCount=total, fps=fps,
                 width=review_width, height=review_height, device=device,
                 precision="BF16" if bf16 else "FP32", optimized=optimized,
                 checkpoint="SAM2.1 Hiera Base+")


def self_test():
    class Component: pass
    class Predictor: pass
    def eager(): return "eager"
    def compiled(): return "compiled"
    compiled._torchdynamo_orig_callable = eager
    predictor = Predictor()
    predictor.sam_prompt_encoder = Component()
    predictor.sam_mask_decoder = Component()
    predictor.sam_prompt_encoder.forward = compiled
    predictor.sam_mask_decoder.forward = lambda: "plain"
    forwards = interactive_forwards(predictor)
    use_compiled_interactive_forwards(forwards, False)
    assert predictor.sam_prompt_encoder.forward is eager
    use_compiled_interactive_forwards(forwards, True)
    assert predictor.sam_prompt_encoder.forward is compiled
    assert model_size(1920, 1080) == (1024, 576)
    assert model_size(1080, 1920) == (576, 1024)
    assert correction_limits(75, {0, 50, 100}, 150) == (50, 100)
    assert propagation_range(75, 150, True, 25) == (50, 75)
    assert propagation_range(75, 150, False, 25) == (75, 100)
    original = {"pred_masks": "original"}
    old_points, old_mask, baselines = [1], [2], {}
    state = {"obj_id_to_idx": {1: 0}, "point_inputs_per_obj": {0: {4: old_points}},
             "mask_inputs_per_obj": {0: {4: old_mask}},
             "temp_output_dict_per_obj": {0: {"cond_frame_outputs": {4: "temp"},
                                                    "non_cond_frame_outputs": {}}},
             "output_dict_per_obj": {0: {"cond_frame_outputs": {4: original},
                                               "non_cond_frame_outputs": {}}}}
    remember_prompt_preview(state, baselines, 4)
    state["point_inputs_per_obj"][0][4] = [3]
    state["mask_inputs_per_obj"][0].pop(4)
    assert clear_prompt_preview(state, baselines, 4) is original
    assert state["point_inputs_per_obj"][0][4] is old_points
    assert state["mask_inputs_per_obj"][0][4] is old_mask
    assert 4 not in state["temp_output_dict_per_obj"][0]["cond_frame_outputs"]
    assert not compiled_vos_worthwhile(VOS_MIN_FRAMES - 1)
    assert compiled_vos_worthwhile(VOS_MIN_FRAMES)
    print("SAM2 refinement worker self-test passed")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--mask", type=Path)
    parser.add_argument("--frames", type=Path)
    parser.add_argument("--masks", type=Path)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test: self_test()
    elif not all((args.source, args.runtime, args.mask, args.frames,
                  args.masks, args.end_ms is not None)):
        parser.error("--source, --runtime, --mask, --frames, --masks, and --end-ms are required")
    else: process(args)


if __name__ == "__main__":
    try: main()
    except Exception as error:
        send(status="error", message=str(error)); raise
