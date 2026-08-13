#!/usr/bin/env python3
"""Persistent SAM2 propagation/correction worker. Stdout is NDJSON UI protocol."""
import argparse, concurrent.futures, contextlib, hashlib, json, math, os, queue, shutil
import subprocess, sys
import tempfile, threading, time, uuid
from collections import defaultdict
from pathlib import Path

from rvm_worker import executable, probe
from videomama_worker import (SAM2_COMMIT, SAM2_MODELS, load_sam2, model_size,
                              sam2_model, sam2_vos_cache_marker,
                              sam2_vos_optimized, sam2_vos_uses_cudagraphs,
                              use_on_demand_sam2_frames)
from sam_mask_worker import automatic_candidates, select_automatic_foreground

CACHE_SCHEMA = 1
POLICY_SCHEMA = 1
PROGRESS_INTERVAL = .15


def send(**values):
    print(json.dumps(values, separators=(",", ":")), flush=True)


class Profiler:
    def __init__(self, path):
        self.path = path
        self.started = time.perf_counter()
        self.stages = defaultdict(list)
        self.counters = defaultdict(int)
        self.gpu_stages = []

    def add(self, name, seconds):
        self.stages[name].append(float(seconds))

    def count(self, name, value=1):
        self.counters[name] += value

    def add_gpu(self, name, begin, end):
        self.gpu_stages.append((name, begin, end))

    def report(self, **metadata):
        if not self.path:
            return
        for name, begin, end in self.gpu_stages:
            try:
                end.synchronize()
                self.add(name, begin.elapsed_time(end) / 1000)
            except RuntimeError:
                self.count("gpuTimingFailures")
        self.gpu_stages.clear()
        values = {}
        for name, samples in self.stages.items():
            ordered = sorted(samples)
            values[name] = {
                "count": len(samples), "totalSeconds": round(sum(samples), 6),
                "meanSeconds": round(sum(samples) / len(samples), 6),
                "medianSeconds": round(ordered[len(ordered) // 2], 6),
                "p95Seconds": round(ordered[min(len(ordered) - 1,
                    math.ceil(len(ordered) * .95) - 1)], 6),
            }
        record = {"type": "sam2_profile", "schemaVersion": 1,
            "createdUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "totalSeconds": round(time.perf_counter() - self.started, 6),
            "stages": values, "counters": dict(self.counters), **metadata}
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.path.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(record, separators=(",", ":")) + "\n")


def atomic_json(path, value):
    temporary = path.with_name(path.name + ".tmp-" + uuid.uuid4().hex)
    temporary.write_text(json.dumps(value, separators=(",", ":")), encoding="utf-8")
    os.replace(temporary, path)


def frame_cache_key(source, start, duration, rate, width, height,
                    extraction_mode="standard"):
    stat = source.stat()
    value = {"schema": CACHE_SCHEMA, "source": str(source).casefold(),
        "size": stat.st_size, "mtimeNs": stat.st_mtime_ns,
        "start": round(start, 6), "duration": round(duration, 6), "rate": rate,
        "width": width, "height": height, "extractionMode": extraction_mode}
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest(), value


def directory_size(path):
    return sum(item.stat().st_size for item in path.rglob("*") if item.is_file())


def process_alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def active_lock(entry):
    return entry / f".active-{os.getpid()}"


def replace_directory_with_retry(source, target, attempts=20):
    """Publish a cache directory despite transient Windows handle scanning."""
    for attempt in range(attempts):
        try:
            os.replace(source, target)
            return
        except PermissionError:
            if attempt + 1 == attempts:
                raise
            time.sleep(.1)


def entry_is_active(entry):
    active = False
    for lock in entry.glob(".active*"):
        try:
            pid = int(lock.read_text().strip())
            if process_alive(pid): active = True
            else: lock.unlink()
        except (OSError, ValueError):
            try: lock.unlink()
            except OSError: pass
    return active


def prune_frame_cache(root, limit, exclude=None):
    if limit <= 0 or not root.is_dir():
        return
    entries, protected_size = [], 0
    for entry in root.iterdir():
        if not entry.is_dir() or entry.name.startswith(".staging-"):
            continue
        if entry == exclude:
            protected_size += directory_size(entry)
            continue
        if entry_is_active(entry): continue
        manifest = entry / "manifest.json"
        try:
            data = json.loads(manifest.read_text(encoding="utf-8"))
            accessed = float(data.get("lastAccessUnix", entry.stat().st_mtime))
        except (OSError, ValueError):
            accessed = entry.stat().st_mtime
        entries.append([accessed, directory_size(entry), entry])
    total = protected_size + sum(value[1] for value in entries)
    for _, size, entry in sorted(entries):
        if total <= limit: break
        shutil.rmtree(entry, ignore_errors=True)
        total -= size


def extract_frames(source, folder, start, duration, rate, width, height, total,
                   profiler, extraction_mode="standard"):
    required = max(64 * 1024 ** 2, round(width * height * total * .75))
    free = shutil.disk_usage(folder).free
    if free < required:
        raise RuntimeError("Insufficient disk space for SAM2 review frames "
                           f"(need approximately {required / 1024 ** 3:.2f} GB)")
    scale = {"bicubic": "bicubic", "bilinear": "fast_bilinear"}.get(
        extraction_mode, "lanczos")
    jpeg_quality = "3" if extraction_mode == "jpeg3" else "2"
    input_options = ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"] \
        if extraction_mode == "nvdec" else []
    filters = (f"scale_cuda={width}:{height}:interp_algo=lanczos,hwdownload,"
        f"format=nv12,fps={rate},setsar=1") if extraction_mode == "nvdec" else \
        f"fps={rate},scale={width}:{height}:flags={scale},setsar=1"
    command = [executable("ffmpeg"), "-y", "-v", "error", "-ss", f"{start:.6f}",
        *input_options, "-i", str(source), "-t", f"{duration:.6f}", "-map", "0:v:0", "-vf",
        filters, "-q:v", jpeg_quality,
        str(folder / "%08d.jpg"), "-progress", "pipe:1", "-nostats"]
    started = time.perf_counter()
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True)
    last = 0.0
    for line in process.stdout:
        if line.startswith("frame="):
            count = int(line.partition("=")[2])
            now = time.monotonic()
            if count == 1 or count >= total or now - last >= PROGRESS_INTERVAL:
                send(status="progress", percent=min(15, count * 15 / total),
                     message=f"Extracted {count}/{total} review frames")
                last = now
    error = process.stderr.read().strip()
    if process.wait() != 0:
        raise RuntimeError(error or "SAM2 review-frame extraction failed")
    profiler.add("extraction", time.perf_counter() - started)


def prepare_frames(args, source, start, duration, rate, width, height, total,
                   profiler):
    scratch = args.frames.resolve()
    if args.cache_limit_bytes <= 0 or args.cache_root is None:
        shutil.rmtree(scratch, ignore_errors=True)
        scratch.mkdir(parents=True)
        extract_frames(source, scratch, start, duration, rate, width, height, total,
                       profiler, args.extraction_mode)
        return scratch, False, None
    root = args.cache_root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    key, identity = frame_cache_key(source, start, duration, rate, width, height,
                                    args.extraction_mode)
    entry, manifest_path = root / key, root / key / "manifest.json"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        frames = entry / "frames"
        files = list(frames.glob("*.jpg"))
        if (manifest.get("schemaVersion") == CACHE_SCHEMA and
                manifest.get("identity") == identity and
                manifest.get("frameCount") == len(files) and files):
            manifest["lastAccessUnix"] = time.time()
            atomic_json(manifest_path, manifest)
            active_lock(entry).write_text(str(os.getpid()))
            prune_frame_cache(root, args.cache_limit_bytes, entry)
            profiler.count("frameCacheHits")
            return frames, True, entry
    except (OSError, ValueError):
        pass
    if entry.is_dir():
        if entry_is_active(entry):
            raise RuntimeError("An incomplete SAM2 frame-cache entry is currently in use")
        shutil.rmtree(entry, ignore_errors=True)
    profiler.count("frameCacheMisses")
    staging = root / (".staging-" + key + "-" + uuid.uuid4().hex)
    frames = staging / "frames"
    frames.mkdir(parents=True)
    try:
        extract_frames(source, frames, start, duration, rate, width, height, total,
                       profiler, args.extraction_mode)
        count = len(list(frames.glob("*.jpg")))
        if count == 0:
            raise RuntimeError("The clip has no review frames")
        atomic_json(staging / "manifest.json", {"schemaVersion": CACHE_SCHEMA,
            "identity": identity, "frameCount": count,
            "lastAccessUnix": time.time()})
        active_lock(staging).write_text(str(os.getpid()))
        try: replace_directory_with_retry(staging, entry)
        except OSError:
            if not entry.is_dir(): raise
            shutil.rmtree(staging, ignore_errors=True)
            active_lock(entry).write_text(str(os.getpid()))
        prune_frame_cache(root, args.cache_limit_bytes, entry)
        return entry / "frames", False, entry
    except BaseException:
        shutil.rmtree(staging, ignore_errors=True)
        raise


class MaskWriter:
    def __init__(self, torch, device, profiler, slots=3):
        self.torch, self.device, self.profiler = torch, device, profiler
        self.available, self.pending = queue.Queue(), queue.Queue(maxsize=slots)
        self.error = None
        self.completed_frame = None
        self.transfer = torch.cuda.Stream(device=device) if device == "cuda" else None
        for _ in range(slots): self.available.put({"cpu": None})
        self.thread = threading.Thread(target=self._run, name="sam2-mask-writer",
                                       daemon=True)
        self.thread.start()

    def _raise(self):
        if self.error is not None:
            raise RuntimeError(f"SAM2 mask writer failed: {self.error}")

    def submit(self, object_ids, logits, frame, folder, gpu_timings=None):
        self._raise()
        ids = object_ids.tolist() if hasattr(object_ids, "tolist") else list(object_ids)
        if 1 not in ids:
            raise RuntimeError(f"SAM2 lost the selected foreground at frame {frame + 1}")
        slot = self.available.get()
        try:
            # Keep independent storage until the transfer completes. SAM2 reuses
            # output tensors aggressively during propagation, so retaining only
            # a view of its logits can corrupt the queued host copy.
            mask = (logits[ids.index(1)] > 0).squeeze().to(
                self.torch.uint8).mul_(255).contiguous().clone()
            shape = tuple(mask.shape)
            if self.device == "cuda":
                if slot["cpu"] is None or tuple(slot["cpu"].shape) != shape:
                    slot["cpu"] = self.torch.empty(shape, dtype=self.torch.uint8,
                                                   pin_memory=True)
                begin = self.torch.cuda.Event(enable_timing=True)
                end = self.torch.cuda.Event(enable_timing=True)
                producer_done = self.torch.cuda.Event()
                producer_done.record(self.torch.cuda.current_stream())
                with self.torch.cuda.stream(self.transfer):
                    self.transfer.wait_event(producer_done)
                    begin.record(self.transfer)
                    slot["cpu"].copy_(mask, non_blocking=True)
                    end.record(self.transfer)
                mask.record_stream(self.transfer)
                slot.update(event=end, begin=begin, producer=producer_done, gpu=mask,
                            timings=gpu_timings or [])
            else:
                slot["cpu"] = mask.cpu()
                slot.update(event=None, begin=None, gpu=None, timings=[])
            slot.update(frame=frame, path=folder / f"{frame + 1:08d}.png")
            self.pending.put(slot)
        except BaseException:
            self.available.put(slot)
            raise

    def _run(self):
        from PIL import Image
        while True:
            item = self.pending.get()
            if item is None:
                self.pending.task_done(); break
            try:
                if self.error is None:
                    started = time.perf_counter()
                    if item["event"] is not None:
                        item["event"].synchronize()
                        self.profiler.add("mask_download_gpu",
                            item["begin"].elapsed_time(item["event"]) / 1000)
                        for name, begin, end in item["timings"]:
                            end.synchronize()
                            self.profiler.add(name, begin.elapsed_time(end) / 1000)
                    array = item["cpu"].numpy()
                    temporary = item["path"].with_name(
                        item["path"].name + ".tmp-" + uuid.uuid4().hex)
                    Image.fromarray(array, "L").save(temporary, "PNG", compress_level=1)
                    os.replace(temporary, item["path"])
                    self.completed_frame = item["frame"]
                    self.profiler.add("mask_png_write", time.perf_counter() - started)
            except BaseException as error:
                self.error = error
            finally:
                item["gpu"] = item["event"] = item["begin"] = item["producer"] = None
                item["timings"] = []
                self.available.put(item)
                self.pending.task_done()

    def flush(self):
        self.pending.join(); self._raise()

    def close(self):
        failure = None
        try: self.flush()
        except BaseException as error: failure = error
        self.pending.put(None); self.pending.join(); self.thread.join()
        if failure is not None: raise failure
        self._raise()


def write_preview_mask(torch, object_ids, logits, frame, folder):
    """Publish an interactive preview before acknowledging its command.

    Propagation benefits from the pipelined MaskWriter, but a paint/click preview
    is a strict request/response operation. Writing it synchronously prevents a
    later preview from observing a recycled transfer slot or an older queued
    image when strokes are submitted in rapid succession.
    """
    from PIL import Image
    ids = object_ids.tolist() if hasattr(object_ids, "tolist") else list(object_ids)
    if 1 not in ids:
        raise RuntimeError(f"SAM2 lost the selected foreground at frame {frame + 1}")
    mask = (logits[ids.index(1)] > 0).squeeze().to(torch.uint8).mul_(255)
    array = mask.detach().cpu().numpy().copy()
    path = folder / f"{frame + 1:08d}.png"
    temporary = path.with_name(path.name + ".preview-" + uuid.uuid4().hex)
    Image.fromarray(array, "L").save(temporary, "PNG", compress_level=1)
    os.replace(temporary, path)


def correction_limits(frame, correction_frames, total):
    return (max((value for value in correction_frames if value < frame), default=0),
            min((value for value in correction_frames if value > frame), default=total - 1))


def propagation_range(start, total, reverse, max_frames):
    distance = start if reverse else total - start - 1
    if max_frames is not None: distance = min(distance, max_frames)
    return (start - distance, start) if reverse else (start, start + distance)


def interactive_forwards(predictor):
    forwards = []
    for component in (predictor.sam_prompt_encoder, predictor.sam_mask_decoder):
        compiled = component.forward
        eager = getattr(compiled, "_torchdynamo_orig_callable", None)
        if eager is not None: forwards.append((component, compiled, eager))
    return forwards


def use_compiled_interactive_forwards(forwards, enabled):
    for component, compiled, eager in forwards:
        component.forward = compiled if enabled else eager


def remember_prompt_preview(state, baselines, frame, obj_id=1):
    obj = state["obj_id_to_idx"][obj_id]
    if frame not in baselines:
        points, masks = state["point_inputs_per_obj"][obj], state["mask_inputs_per_obj"][obj]
        baselines[frame] = (frame in points, points.get(frame), frame in masks,
                            masks.get(frame))


def clear_prompt_preview(state, baselines, frame, obj_id=1):
    obj = state["obj_id_to_idx"][obj_id]
    had_points, old_points, had_mask, old_mask = baselines.pop(
        frame, (False, None, False, None))
    points, masks = state["point_inputs_per_obj"][obj], state["mask_inputs_per_obj"][obj]
    if had_points: points[frame] = old_points
    else: points.pop(frame, None)
    if had_mask: masks[frame] = old_mask
    else: masks.pop(frame, None)
    for outputs in state["temp_output_dict_per_obj"][obj].values(): outputs.pop(frame, None)
    outputs = state["output_dict_per_obj"][obj]
    original = outputs["cond_frame_outputs"].get(frame)
    return original if original is not None else outputs["non_cond_frame_outputs"].get(frame)


def policy_key(torch, device, model):
    gpu = torch.cuda.get_device_name() if device == "cuda" else "cpu"
    driver = torch.cuda.driver_version() if device == "cuda" and \
        hasattr(torch.cuda, "driver_version") else "unknown"
    return "|".join((gpu, str(driver), torch.__version__, str(torch.version.cuda),
                     SAM2_COMMIT, model))


def load_policy(runtime):
    path = runtime / "sam2-performance-policy-v1.json"
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if value.get("schemaVersion") == POLICY_SCHEMA else {}
    except (OSError, ValueError): return {}


def choose_extraction(args, runtime, torch, device):
    if args.extraction_mode != "auto": return args.extraction_mode
    entry = load_policy(runtime).get("entries", {}).get(
        policy_key(torch, device, args.model), {})
    result = entry.get("extraction", {})
    mode = result.get("mode", "standard")
    return mode if result.get("enabled") and mode in {
        "bicubic", "bilinear", "jpeg3", "nvdec"} else "standard"


def choose_execution(args, runtime, torch, device, frames):
    default_compile = os.environ.get("IQP_SAM2_COMPILE_MODE", "max-autotune")
    if args.execution != "auto":
        return args.execution, 3.0, default_compile, args.gpu_feature_cache_frames
    if device != "cuda": return "eager", 3.0, default_compile, 0
    policy = load_policy(runtime)
    entry = policy.get("entries", {}).get(policy_key(torch, device, args.model), {})
    factor = max(1.0, float(entry.get("expectedWorkMultiplier", 3.0)))
    expected = frames * factor
    choices = []
    for mode in ("encoder", "vos"):
        result = entry.get(mode, {})
        speedup = float(result.get("warmSpeedup", 0))
        break_even = float(result.get("breakEvenFrames", math.inf))
        cutoff_met = args.compile_cutoff_frames <= 0 or \
            frames >= args.compile_cutoff_frames
        if result.get("enabled") and speedup >= .10 and cutoff_met and \
                expected >= break_even * 1.2:
            choices.append((speedup, mode,
                result.get("compileMode", default_compile)))
    if not choices:
        mode, compile_mode = "eager", default_compile
    else:
        _, mode, compile_mode = max(choices)
    feature = entry.get("gpuFeatureCache", {})
    feature_frames = int(feature.get("frames", 0)) if feature.get("enabled") and \
        float(feature.get("warmSpeedup", 0)) >= .02 else 0
    return mode, factor, compile_mode, max(0, feature_frames)


def update_work_multiplier(runtime, torch, device, model, actual):
    """Refine the expected correction workload without inventing speed results."""
    path = runtime / "sam2-performance-policy-v1.json"
    policy = load_policy(runtime)
    if not policy: return
    entries = policy.setdefault("entries", {})
    entry = entries.setdefault(policy_key(torch, device, model), {})
    previous = float(entry.get("expectedWorkMultiplier", 3.0))
    entry["expectedWorkMultiplier"] = round(max(1.0, min(8.0,
        previous * .75 + actual * .25)), 4)
    try: atomic_json(path, policy)
    except OSError: pass


def invalidate_policy_mode(runtime, torch, device, model, execution, message):
    if execution == "eager": return
    path = runtime / "sam2-performance-policy-v1.json"
    policy = load_policy(runtime)
    entry = policy.get("entries", {}).get(policy_key(torch, device, model))
    if not isinstance(entry, dict) or not isinstance(entry.get(execution), dict): return
    entry[execution]["enabled"] = False
    entry[execution]["invalidatedUtc"] = time.strftime(
        "%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    entry[execution]["failure"] = str(message)[:500]
    try: atomic_json(path, policy)
    except OSError: pass


def clear_stale_memory(predictor, state, frame):
    clear = getattr(predictor, "_clear_non_cond_mem_around_input", None)
    if clear is not None:
        clear(state, frame)
        return
    # The compiled VOS class omits this helper, although it uses the same state.
    from sam2.sam2_video_predictor import SAM2VideoPredictor
    SAM2VideoPredictor._clear_non_cond_mem_around_input(predictor, state, frame)


def clear_prompts_in_frame(predictor, state, frame, need_output=True):
    """Call the supported SAM2 prompt-removal API on eager and compiled VOS."""
    clear = getattr(predictor, "clear_all_prompts_in_frame", None)
    if clear is not None:
        return clear(state, frame, 1, need_output=need_output)
    from sam2.sam2_video_predictor import SAM2VideoPredictor
    return SAM2VideoPredictor.clear_all_prompts_in_frame(
        predictor, state, frame, 1, need_output=need_output)


def load_resume_state(path, masks, total, fps, width, height):
    if path is None or not path.is_file():
        return None
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        if value.get("schemaVersion") != 1 or int(value.get("frameCount", -1)) != total:
            return None
        if abs(float(value.get("fps", 0)) - fps) > .001 or \
                int(value.get("reviewWidth", -1)) != width or \
                int(value.get("reviewHeight", -1)) != height:
            return None
        anchors = value.get("anchors", [])
        if not isinstance(anchors, list): return None
        for anchor in anchors:
            frame = int(anchor.get("frame", -1))
            if frame < 0 or frame >= total or anchor.get("mode") not in (
                    "prompt", "auto", "paint"):
                return None
            points, labels = anchor.get("points", []), anchor.get("labels", [])
            if not isinstance(points, list) or not isinstance(labels, list) or \
                    len(points) != len(labels) or any(value not in (0, 1)
                                                       for value in labels) or \
                    any(not isinstance(point, list) or len(point) != 2
                        for point in points):
                return None
        # A draft is deliberately useful before generation has completed.  Only
        # require the contiguous prefix that can safely be resumed; stale preview
        # files after a gap are overwritten when propagation continues.
        available_end = -1
        for frame in range(total):
            if not (masks / f"{frame + 1:08d}.png").is_file():
                break
            available_end = frame
        if available_end < 0:
            return None
        if any(int(item["frame"]) > available_end for item in anchors):
            return None
        from PIL import Image
        for frame in {0, available_end, *(int(item["frame"]) for item in anchors)}:
            with Image.open(masks / f"{frame + 1:08d}.png") as image:
                if image.size != (width, height): return None
        value["_availableEnd"] = available_end
        return value
    except (OSError, ValueError, TypeError, KeyError):
        return None


def physical_memory_bytes():
    if hasattr(os, "sysconf"):
        return os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES")
    try:
        import ctypes
        class MemoryStatus(ctypes.Structure):
            _fields_ = [("length", ctypes.c_ulong), ("memoryLoad", ctypes.c_ulong),
                ("totalPhysical", ctypes.c_ulonglong),
                ("availablePhysical", ctypes.c_ulonglong),
                ("totalPageFile", ctypes.c_ulonglong),
                ("availablePageFile", ctypes.c_ulonglong),
                ("totalVirtual", ctypes.c_ulonglong),
                ("availableVirtual", ctypes.c_ulonglong),
                ("availableExtendedVirtual", ctypes.c_ulonglong)]
        status = MemoryStatus(); status.length = ctypes.sizeof(status)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return int(status.totalPhysical)
    except (AttributeError, OSError):
        pass
    return 8 * 1024 ** 3


def add_feature_profiler(predictor, profiler, torch, device, enabled):
    if not enabled: return
    original = predictor.forward_image
    original_feature = predictor._get_image_feature
    def profiled(image):
        wall = time.perf_counter()
        if device == "cuda":
            begin = torch.cuda.Event(enable_timing=True)
            end = torch.cuda.Event(enable_timing=True)
            begin.record()
        result = original(image)
        if device == "cuda":
            end.record(); profiler.add_gpu("image_encoding_gpu", begin, end)
        profiler.add("image_encoding_dispatch", time.perf_counter() - wall)
        return result
    predictor.forward_image = profiled
    def profiled_feature(inference_state, frame_idx, batch_size):
        wall = time.perf_counter()
        result = original_feature(inference_state, frame_idx, batch_size)
        profiler.add("frame_upload_feature_dispatch", time.perf_counter() - wall)
        return result
    predictor._get_image_feature = profiled_feature


def install_gpu_feature_cache(predictor, state, torch, maximum, profiler):
    if maximum <= 0: return
    from collections import OrderedDict
    original, retained = predictor._get_image_feature, OrderedDict()
    headroom = 4 * 1024 ** 3
    def cached(inference_state, frame_idx, batch_size):
        if frame_idx in retained:
            value = retained.pop(frame_idx); retained[frame_idx] = value
            inference_state["cached_features"] = {frame_idx: value}
            profiler.count("gpuFeatureCacheHits")
            return original(inference_state, frame_idx, batch_size)
        profiler.count("gpuFeatureCacheMisses")
        result = original(inference_state, frame_idx, batch_size)
        value = inference_state["cached_features"].get(frame_idx)
        if value is not None:
            total = torch.cuda.get_device_properties(0).total_memory
            while retained and (len(retained) >= maximum or
                    total - torch.cuda.memory_allocated() < headroom):
                retained.popitem(last=False)
            if total - torch.cuda.memory_allocated() >= headroom:
                retained[frame_idx] = value
                profiler.count("gpuFeatureCacheStores")
        return result
    predictor._get_image_feature = cached


def install_edgetam_embedding_batches(predictor, state, torch, profiler,
                                      batch_size=4, maximum=8):
    """Encode one future batch concurrently with recurrent temporal tracking."""
    from collections import OrderedDict
    original = predictor._get_image_feature
    # init_state has already warmed frame zero; retain it instead of encoding it
    # again as part of the first batch.
    retained = OrderedDict(state.get("cached_features", {}))
    frame_count = int(state["num_frames"])
    last_frame = None
    batch_disabled = False
    async_disabled = os.environ.get("IQP_EDGETAM_ASYNC_EMBEDDINGS", "1") == "0"
    headroom = 3 * 1024 ** 3
    prefetch_stream = torch.cuda.Stream(device=state["device"])
    executor = concurrent.futures.ThreadPoolExecutor(
        max_workers=1, thread_name_prefix="EdgeTAM-embedding-prefetch")
    pending = None

    def one_frame(value, offset, encoded_size):
        if torch.is_tensor(value):
            return value[offset:offset + 1] if value.ndim and \
                value.shape[0] == encoded_size else value
        if isinstance(value, list):
            return [one_frame(item, offset, encoded_size) for item in value]
        if isinstance(value, tuple):
            return tuple(one_frame(item, offset, encoded_size) for item in value)
        if isinstance(value, dict):
            return {key: one_frame(item, offset, encoded_size)
                    for key, item in value.items()}
        return value

    def publish_cache(inference_state):
        inference_state["cached_features"] = dict(retained)

    def batch_indices(start, direction):
        result = []
        for distance in range(batch_size):
            candidate = start + direction * distance
            if 0 <= candidate < frame_count and candidate not in retained:
                result.append(candidate)
        return result

    def encode(inference_state, indices, stream=None):
        images = [inference_state["images"][index] for index in indices]
        image_batch = torch.stack(images).to(inference_state["device"],
                                               non_blocking=True).float()
        real_size = len(indices)
        if real_size < batch_size:
            padding = image_batch[-1:].expand(batch_size - real_size, -1, -1, -1)
            image_batch = torch.cat((image_batch, padding), dim=0)
        context = torch.cuda.stream(stream) if stream is not None else \
            contextlib.nullcontext()
        started = time.perf_counter()
        with torch.inference_mode(), torch.autocast(device_type="cuda",
                dtype=torch.bfloat16), context:
            backbone_batch = predictor.forward_image(image_batch)
            completed = torch.cuda.Event()
            completed.record(stream) if stream is not None else completed.record()
        profiler.add("embedding_batch_dispatch", time.perf_counter() - started)
        profiler.count("embeddingBatches")
        profiler.count("embeddingFramesEncoded", real_size)
        return indices, image_batch, backbone_batch, completed

    def record_stream(value, stream):
        if torch.is_tensor(value):
            value.record_stream(stream)
        elif isinstance(value, (list, tuple)):
            for item in value: record_stream(item, stream)
        elif isinstance(value, dict):
            for item in value.values(): record_stream(item, stream)

    def retain_encoded(result):
        indices, image_batch, backbone_batch, completed = result
        current_stream = torch.cuda.current_stream(state["device"])
        current_stream.wait_event(completed)
        record_stream(image_batch, current_stream)
        record_stream(backbone_batch, current_stream)
        for offset, index in enumerate(indices):
            image = image_batch[offset:offset + 1]
            backbone = one_frame(backbone_batch, offset, batch_size)
            retained[index] = (image, backbone)
        while len(retained) > maximum:
            retained.popitem(last=False)

    def schedule(inference_state, start, direction):
        nonlocal pending
        if async_disabled or pending is not None:
            return
        indices = batch_indices(start, direction)
        if not indices:
            return
        free, _ = torch.cuda.mem_get_info()
        if free < headroom:
            profiler.count("embeddingAsyncLowMemorySkips")
            return
        future = executor.submit(encode, inference_state, indices, prefetch_stream)
        pending = (tuple(indices), direction, future)
        profiler.count("embeddingAsyncBatchesScheduled")

    def batched(inference_state, frame_idx, object_batch_size):
        nonlocal last_frame, batch_disabled, async_disabled, pending
        direction = -1 if last_frame is not None and frame_idx < last_frame else 1
        if pending is not None and pending[1] != direction:
            pending[2].cancel()
            pending = None
            profiler.count("embeddingAsyncDirectionChanges")
        if frame_idx in retained:
            value = retained.pop(frame_idx)
            retained[frame_idx] = value
            publish_cache(inference_state)
            profiler.count("embeddingBatchCacheHits")
            last_frame = frame_idx
            schedule(inference_state, frame_idx + direction, direction)
            return original(inference_state, frame_idx, object_batch_size)
        if batch_disabled:
            return original(inference_state, frame_idx, object_batch_size)

        try:
            if pending is not None and frame_idx in pending[0]:
                waited = time.perf_counter()
                future = pending[2]
                pending = None
                retain_encoded(future.result())
                wait_seconds = time.perf_counter() - waited
                profiler.add("embedding_async_wait", wait_seconds)
                profiler.count("embeddingAsyncBatchesConsumed")
            if frame_idx not in retained:
                free, _ = torch.cuda.mem_get_info()
                if free < headroom:
                    profiler.count("embeddingBatchLowMemoryFallbacks")
                    last_frame = frame_idx
                    return original(inference_state, frame_idx, object_batch_size)
                indices = batch_indices(frame_idx, direction) or [frame_idx]
                retain_encoded(encode(inference_state, indices))
            publish_cache(inference_state)
            last_frame = frame_idx
            schedule(inference_state, frame_idx + direction * batch_size, direction)
            return original(inference_state, frame_idx, object_batch_size)
        except RuntimeError:
            # Retry synchronously after a concurrent-stream failure. If that also
            # fails, the existing single-frame implementation remains available.
            if pending is not None:
                pending[2].cancel()
                pending = None
            if not async_disabled:
                async_disabled = True
                profiler.count("embeddingAsyncFallbacks")
                try:
                    indices = batch_indices(frame_idx, direction) or [frame_idx]
                    retain_encoded(encode(inference_state, indices))
                    publish_cache(inference_state)
                    last_frame = frame_idx
                    return original(inference_state, frame_idx, object_batch_size)
                except RuntimeError:
                    pass
            retained.clear()
            inference_state["cached_features"] = {}
            batch_disabled = True
            profiler.count("embeddingBatchFallbacks")
            torch.cuda.empty_cache()
            return original(inference_state, frame_idx, object_batch_size)

    predictor._get_image_feature = batched


def peak_process_memory_bytes():
    try:
        import resource
        value = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
        return int(value if sys.platform == "darwin" else value * 1024)
    except ImportError:
        pass
    try:
        import ctypes
        from ctypes import wintypes
        class Counters(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t),
                ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t),
                ("PeakPagefileUsage", ctypes.c_size_t)]
        counters = Counters(); counters.cb = ctypes.sizeof(counters)
        process = ctypes.windll.kernel32.GetCurrentProcess()
        if ctypes.windll.psapi.GetProcessMemoryInfo(
                process, ctypes.byref(counters), counters.cb):
            return int(counters.PeakWorkingSetSize)
    except (AttributeError, OSError):
        pass
    return 0


class ProgressThrottle:
    def __init__(self): self.last_time, self.last_percent = 0.0, -1.0
    def emit(self, percent, message, frame, range_start, range_end, reverse,
             preview_frame=None, force=False):
        now = time.monotonic()
        if not force and now - self.last_time < PROGRESS_INTERVAL and \
                percent < self.last_percent + .5:
            return
        self.last_time, self.last_percent = now, percent
        values = dict(status="progress", percent=percent, message=message,
            frameIndex=frame,
            completedStart=min(frame, range_end if reverse else range_start),
            completedEnd=max(frame, range_end if reverse else range_start),
            rangeStart=range_start, rangeEnd=range_end, reverse=reverse)
        if preview_frame is not None:
            values["previewFrameIndex"] = preview_frame
        send(**values)


class StopRequested(Exception):
    pass


class CommandInbox:
    """Read editor commands while model propagation owns the main thread."""
    _eof = object()

    def __init__(self):
        self.pending, self.deferred = queue.Queue(), []
        self.thread = threading.Thread(target=self._read, name="sam2-command-reader",
                                       daemon=True)
        self.thread.start()

    def _read(self):
        try:
            for line in sys.stdin:
                if not line.strip():
                    continue
                try: self.pending.put(json.loads(line))
                except json.JSONDecodeError:
                    self.pending.put({"command": "invalid"})
        finally:
            self.pending.put(self._eof)

    def poll_control(self, allow_pause, allow_stop_backward=False):
        while True:
            try: request = self.pending.get_nowait()
            except queue.Empty: return None
            if request is self._eof or request.get("command") == "quit":
                raise StopRequested()
            if request.get("command") == "pause":
                if allow_pause: return "pause"
                continue
            if request.get("command") == "stop-backward":
                if allow_stop_backward: return "stop-backward"
                continue
            self.deferred.append(request)

    def next(self):
        request = self.deferred.pop(0) if self.deferred else self.pending.get()
        return {"command": "quit"} if request is self._eof else request


def propagate(predictor, state, writer, folder, start, total, reverse,
              progress_start, progress_size, message, profiler, max_frames=None,
              mark_step=None, skip_start=False, commands=None, allow_pause=False,
              allow_stop_backward=False):
    maximum = (start + 1 if reverse else total - start) if max_frames is None \
        else max_frames + 1
    written, seen, last_frame = 0, 0, start
    range_start, range_end = propagation_range(start, total, reverse, max_frames)
    throttle = ProgressThrottle()
    writer.completed_frame = None
    iterator = iter(predictor.propagate_in_video(state, start_frame_idx=start,
        reverse=reverse, max_frame_num_to_track=max_frames))
    while True:
        if mark_step: mark_step()
        timings = []
        if writer.device == "cuda":
            begin, end = writer.torch.cuda.Event(True), writer.torch.cuda.Event(True)
            begin.record()
        wall = time.perf_counter()
        try: frame, object_ids, logits = next(iterator)
        except StopIteration: break
        profiler.add("propagation_dispatch", time.perf_counter() - wall)
        if writer.device == "cuda":
            end.record(); timings.append(("propagation_gpu", begin, end))
        seen += 1
        if skip_start and frame == start:
            continue
        writer.submit(object_ids, logits, frame, folder, timings)
        written += 1; last_frame = frame
        percent = progress_start + seen * progress_size / max(1, maximum)
        throttle.emit(percent, f"{message} {seen}/{maximum} frames", frame,
                      range_start, range_end, reverse, writer.completed_frame,
                      force=seen == 1 or seen == maximum)
        if commands is not None and commands.poll_control(
                allow_pause and frame != range_end,
                allow_stop_backward and reverse and frame != range_end):
            return written, last_frame, True
    return written, last_frame, False


def process(args):
    import numpy as np
    import torch
    from PIL import Image

    profiler = Profiler(args.profile_log.resolve() if args.profile_log else None)
    source, runtime, masks = args.source.resolve(), args.runtime.resolve(), args.masks.resolve()
    device = "cuda" if torch.cuda.is_available() else "cpu"
    args.extraction_mode = choose_extraction(args, runtime, torch, device)
    width, height, rate, fps, source_duration = probe(source)
    start, end = args.start_ms / 1000, args.end_ms / 1000
    if start < 0 or end <= start or end > source_duration + .1:
        raise RuntimeError("clip range is outside the source video")
    if not start <= args.initial_frame_ms / 1000 <= end:
        raise RuntimeError("initial mask frame is outside the clip range")
    duration = min(end, source_duration) - start
    expected = max(1, round(duration * fps))
    review_width, review_height = model_size(width, height)
    frames, cache_hit, cache_entry = prepare_frames(args, source, start, duration,
        rate, review_width, review_height, expected, profiler)
    args.active_cache_entry = cache_entry
    frame_files = sorted(frames.glob("*.jpg"), key=lambda path: int(path.stem))
    if not frame_files: raise RuntimeError("The clip has no review frames")
    total = len(frame_files)
    initial_frame = max(0, min(total - 1, round(
        (args.initial_frame_ms / 1000 - start) * fps)))
    resume = load_resume_state(args.resume_state.resolve()
        if args.resume_state else None, masks, total, fps,
        review_width, review_height)
    if resume is None:
        shutil.rmtree(masks, ignore_errors=True)
        masks.mkdir(parents=True)
    initial = np.asarray(Image.open(args.mask).convert("L").resize(
        (review_width, review_height), Image.Resampling.NEAREST)) >= 128
    bf16 = device == "cuda" and torch.cuda.get_device_capability()[0] >= 8
    if device == "cuda":
        torch.backends.cuda.matmul.allow_tf32 = True
        torch.backends.cudnn.allow_tf32 = True
        torch.backends.cudnn.benchmark = True
        torch.set_float32_matmul_precision("high")
        torch.cuda.reset_peak_memory_stats()
    if args.engine == "sam2":
        execution, expected_factor, compile_mode, feature_cache_frames = \
            choose_execution(args, runtime, torch, device, total)
    else:
        can_compile = device == "cuda" and args.compile_cutoff_frames > 0 and \
            total >= args.compile_cutoff_frames and sam2_vos_optimized(torch, device)
        execution, expected_factor, compile_mode, feature_cache_frames = \
            ("encoder" if can_compile else "eager", 1.0, "max-autotune", 0)
    args.resolved_execution = execution
    args.resolved_device = device
    args.ready_sent = False
    optimized = execution == "vos" and sam2_vos_optimized(torch, device)
    encoder_compiled = execution == "encoder" and sam2_vos_optimized(torch, device)
    if execution != "eager" and not (optimized or encoder_compiled): execution = "eager"
    marker = (runtime / "torchinductor-cache" /
        f"edgetam-{execution}-{compile_mode}.ready") if args.engine == "edgetam" else \
        sam2_vos_cache_marker(runtime, args.model, execution, compile_mode)
    first_compile = execution != "eager" and not marker.is_file()
    if first_compile:
        label = "EdgeTAM" if args.engine == "edgetam" else f"SAM2 {args.model}"
        send(status="progress", percent=15, message=
             f"Compiling {label} {execution} optimization (one-time cache generation; subsequent sessions reuse it)...")
    elif execution != "eager":
        label = "EdgeTAM" if args.engine == "edgetam" else f"SAM2 {args.model}"
        send(status="progress", percent=15, message=
             f"Loading cached {label} {execution} optimization; initializing this worker...")
    else:
        label = "EdgeTAM" if args.engine == "edgetam" else f"SAM2 {args.model}"
        send(status="progress", percent=15, message=f"Loading {label} on {device.upper()}...")
    loaded = time.perf_counter()
    if args.engine == "edgetam":
        repository = runtime / "edgetam"
        checkpoint = repository / "checkpoints" / "edgetam.pt"
        if not (runtime / "EDGETAM_COMMIT").is_file() or not checkpoint.is_file():
            raise RuntimeError("EdgeTAM is not installed; run Processing Tools setup")
        sys.path.insert(0, str(repository))
        from sam2.build_sam import build_sam2_video_predictor
        overrides = ["++model.compile_image_encoder=True"] if encoder_compiled else []
        predictor = build_sam2_video_predictor("configs/edgetam.yaml",
            str(checkpoint), device=device, hydra_overrides_extra=overrides)
        model_info = {"label": "EdgeTAM"}
    else:
        model_info, _ = sam2_model(runtime, args.model)
        predictor = load_sam2(runtime, torch, device, optimized=optimized,
                              model_name=args.model, encoder_compiled=encoder_compiled,
                              compile_mode=compile_mode)
    profiler.add("model_load", time.perf_counter() - loaded)
    add_feature_profiler(predictor, profiler, torch, device, args.detailed_profile)
    predictor.add_all_frames_to_correct_as_cond = True
    mark_step = torch.compiler.cudagraph_mark_step_begin if optimized and \
        sam2_vos_uses_cudagraphs(compile_mode) else None
    physical = physical_memory_bytes()
    memory_cache = min(512 * 1024 ** 2, int(physical * .05))
    cache_stats = {}
    edge_embedding_batch = 4 if args.engine == "edgetam" and device == "cuda" else 1
    use_on_demand_sam2_frames(frames, torch, memory_cache, cache_stats,
        prefetch_depth=max(2, edge_embedding_batch + 2),
        prefetch_to_device=device == "cuda")
    writer = MaskWriter(torch, device, profiler)
    correction_frames = {initial_frame}
    if resume is not None:
        correction_frames.update(int(value["frame"])
                                 for value in resume.get("anchors", []))
    propagated_frames = 0
    try:
        with torch.inference_mode(), torch.autocast(device_type=device,
                dtype=torch.bfloat16, enabled=bf16):
            if mark_step: mark_step()
            state = predictor.init_state(str(frames), offload_video_to_cpu=True,
                                         offload_state_to_cpu=device != "cuda")
            if device == "cuda" and args.engine == "edgetam":
                install_edgetam_embedding_batches(predictor, state, torch, profiler,
                    batch_size=edge_embedding_batch,
                    maximum=max(8, feature_cache_frames))
            elif device == "cuda":
                install_gpu_feature_cache(predictor, state, torch,
                                          feature_cache_frames, profiler)
            if mark_step: mark_step()
            prompted = time.perf_counter()
            predictor.add_new_mask(state, frame_idx=initial_frame, obj_id=1,
                                   mask=initial)
            profiler.add("initial_prompt", time.perf_counter() - prompted)
            commands = getattr(args, "command_inbox", None)
            if commands is None:
                commands = args.command_inbox = CommandInbox()
            metadata = dict(frameCount=total, fps=fps,
                width=review_width, height=review_height, device=device,
                precision="BF16" if bf16 else "FP32", optimized=optimized,
                execution=execution, checkpoint=model_info["label"], model=args.model,
                gpuFeatureCacheFrames=feature_cache_frames,
                embeddingBatchSize=edge_embedding_batch,
                frameCacheHit=cache_hit, framesFolder=str(frames),
                supportsCorrections=True, engine=args.engine)
            metadata["initialFrame"] = initial_frame
            available_end, continuation = total - 1, None
            if resume is None:
                backward_fraction = initial_frame / max(1, total - 1)
                backward_size = 70 * backward_fraction
                if initial_frame > 0:
                    send(status="generation", phase="backward", rangeStart=0,
                         rangeEnd=initial_frame, **metadata)
                    written, _, _ = propagate(predictor, state, writer, masks,
                        initial_frame, total, True, 15, backward_size,
                        "Tracked backward", profiler, initial_frame, mark_step,
                        commands=commands, allow_pause=False)
                    propagated_frames += written
                    writer.flush()
                send(status="generation", phase="forward",
                     rangeStart=initial_frame, rangeEnd=total - 1, **metadata)
                written, last_frame, paused = propagate(predictor, state, writer,
                    masks, initial_frame, total, False, 15 + backward_size,
                    70 - backward_size, "Tracked forward", profiler,
                    mark_step=mark_step, commands=commands, allow_pause=True)
                propagated_frames += written
                writer.flush()
                available_end = last_frame
                if paused:
                    continuation = dict(target=total - 1, range_start=0,
                                        anchor=None, removing=False)
            else:
                predictor.add_new_mask(state, frame_idx=initial_frame,
                                       obj_id=1, mask=initial)
                for anchor in sorted(resume.get("anchors", []),
                                     key=lambda value: int(value["frame"])):
                    frame = int(anchor["frame"])
                    saved = np.asarray(Image.open(
                        masks / f"{frame + 1:08d}.png").convert("L")) >= 128
                    predictor.add_new_mask(state, frame_idx=frame, obj_id=1,
                                           mask=saved)
                available_end = max(0, min(total - 1,
                    int(resume.get("_availableEnd", total - 1))))
                if available_end < total - 1:
                    continuation = dict(target=total - 1, range_start=0,
                                        anchor=None, removing=False)
            if execution != "eager": marker.parent.mkdir(parents=True, exist_ok=True); marker.touch()
            prompt_forwards = interactive_forwards(predictor) if optimized else []
            use_compiled_interactive_forwards(prompt_forwards, False)
            ready = dict(status="ready", resumed=resume is not None, **metadata)
            if continuation is None:
                send(**ready)
            else:
                send(status="paused", pauseFrame=available_end,
                     completedStart=0, completedEnd=available_end,
                     availableEnd=available_end, **metadata)
            args.ready_sent = True
            preview_baselines, preview_mask_baselines, auto_cache = {}, {}, {}

            def remember_preview_mask(frame):
                if frame in preview_mask_baselines:
                    return
                path = masks / f"{frame + 1:08d}.png"
                preview_mask_baselines[frame] = path.read_bytes() \
                    if path.is_file() else None

            def restore_preview_mask(frame):
                path = masks / f"{frame + 1:08d}.png"
                saved = preview_mask_baselines.pop(frame, None)
                if saved is None:
                    try: path.unlink()
                    except FileNotFoundError: pass
                    return
                temporary = path.with_name(path.name + ".reset")
                temporary.write_bytes(saved)
                os.replace(temporary, path)
            while True:
                request = commands.next()
                if request.get("command") == "quit": break
                command = request.get("command")
                if command == "pause":
                    send(**ready); continue
                if command == "continue":
                    if continuation is None:
                        send(**ready); continue
                    target = continuation["target"]
                    cursor = continuation.get("cursor", available_end)
                    send(status="generation", phase="forward",
                         rangeStart=cursor, rangeEnd=target, **metadata)
                    written, last_frame, paused = propagate(predictor, state, writer,
                        masks, cursor, total, False, 0, 100,
                        "Updated forward" if continuation["anchor"] is not None
                        else "Tracked", profiler, target - cursor, mark_step,
                        skip_start=True, commands=commands, allow_pause=True)
                    propagated_frames += written
                    writer.flush()
                    if continuation.get("extends_review", True):
                        available_end = max(available_end, last_frame)
                    terminal = dict(pauseFrame=available_end,
                        completedStart=continuation["range_start"],
                        completedEnd=last_frame, availableEnd=available_end)
                    if continuation["anchor"] is not None:
                        terminal["anchorFrame"] = continuation["anchor"]
                        terminal["removing"] = continuation["removing"]
                    if paused:
                        continuation["cursor"] = last_frame
                        send(status="paused", **terminal, **metadata)
                    else:
                        parent = continuation.get("resume")
                        continuation = parent
                        if continuation is None:
                            available_end = total - 1
                            terminal["availableEnd"] = available_end
                            send(**ready, **terminal)
                        else:
                            terminal["pauseFrame"] = available_end
                            terminal["availableEnd"] = available_end
                            send(status="paused", **terminal, **metadata)
                    continue
                if command not in ("prompt", "auto", "mask", "reset", "update",
                                   "remove-preview", "remove"):
                    send(status="error", message="Unknown SAM2 editor command"); continue
                frame = int(request["frame"])
                if not 0 <= frame <= available_end:
                    send(status="error", message="Correction frame is outside the clip"); continue
                if command not in ("update", "remove"):
                    remember_preview_mask(frame)
                    if command == "remove-preview":
                        remember_prompt_preview(state, preview_baselines, frame)
                        result = clear_prompts_in_frame(predictor, state, frame,
                                                       need_output=True)
                        if frame == initial_frame:
                            result = predictor.add_new_mask(
                                state, frame_idx=initial_frame, obj_id=1,
                                mask=initial)
                        _, object_ids, logits = result
                        write_preview_mask(torch, object_ids, logits, frame, masks)
                        send(status="preview", frame=frame, automatic=False,
                             removed=True); continue
                    if command == "reset":
                        original = clear_prompt_preview(state, preview_baselines, frame)
                        if original is None: raise RuntimeError("The original tracked mask is unavailable")
                        # Restore the exact saved PNG rather than converting the
                        # internal low-resolution tensor again.  Apart from being
                        # lossless, this avoids shape/size failures in SAM2 after
                        # a long sequence of mixed click and paint previews.
                        restore_preview_mask(frame)
                        send(status="preview", frame=frame, automatic=False); continue
                    remember_prompt_preview(state, preview_baselines, frame)
                    # Propagation leaves its final (often the pause) frame in the
                    # predictor's single-frame visual cache. Seeking must never
                    # reuse that embedding for a correction on another frame.
                    # Force SAM2 to encode the explicitly requested frame.
                    state["cached_features"] = {}
                    if command == "mask":
                        painted_path = Path(str(request.get("mask", ""))).resolve()
                        if not painted_path.is_file():
                            send(status="error", message="The painted mask is missing"); continue
                        with Image.open(painted_path) as painted_image:
                            if painted_image.size != (review_width, review_height):
                                send(status="error", message="The painted mask size is invalid"); continue
                            painted = np.asarray(painted_image.convert("L")) >= 128
                        prompted = time.perf_counter()
                        _, object_ids, logits = predictor.add_new_mask(
                            state, frame_idx=frame, obj_id=1, mask=painted)
                        candidate_count = None
                    else:
                        points = np.asarray(request.get("points", []), np.float32)
                        labels = np.asarray(request.get("labels", []), np.int32)
                        if len(points) != len(labels) or command == "prompt" and len(points) == 0:
                            send(status="error", message="Add at least one correction click"); continue
                    if command == "auto":
                        if auto_cache.get("frame") == frame:
                            candidates = auto_cache["candidates"]
                            profiler.count("automaticMaskCacheHits")
                        else:
                            auto_image = np.asarray(Image.open(
                                frame_files[frame]).convert("RGB"))
                            generated = time.perf_counter()
                            candidates = automatic_candidates(predictor, auto_image,
                                torch, device, bf16)
                            profiler.add("automatic_mask_generation",
                                         time.perf_counter() - generated)
                            auto_cache = {"frame": frame, "imageShape": auto_image.shape,
                                          "candidates": candidates}
                            profiler.count("automaticMaskCacheMisses")
                        selected = time.perf_counter()
                        automatic, candidate_count = select_automatic_foreground(
                            candidates, auto_cache["imageShape"], points, labels)
                        profiler.add("automatic_mask_selection",
                                     time.perf_counter() - selected)
                        prompted = time.perf_counter()
                        _, object_ids, logits = predictor.add_new_mask(
                            state, frame_idx=frame, obj_id=1, mask=automatic)
                    elif command == "prompt":
                        prompted = time.perf_counter()
                        _, object_ids, logits = predictor.add_new_points_or_box(
                            state, frame_idx=frame, obj_id=1, points=points, labels=labels,
                            clear_old_points=True)
                        candidate_count = None
                    profiler.add("correction_prompt", time.perf_counter() - prompted)
                    if command == "mask":
                        # Painting is a direct bitmap edit. SAM2 retains that mask
                        # as its propagation prompt, but its immediate decoded
                        # logits are not guaranteed to reproduce the input pixel
                        # for pixel and can lag behind repeated mask prompts.
                        # Publish the exact bitmap the user just painted.
                        path = masks / f"{frame + 1:08d}.png"
                        temporary = path.with_name(
                            path.name + ".paint-" + uuid.uuid4().hex)
                        shutil.copyfile(painted_path, temporary)
                        os.replace(temporary, path)
                    else:
                        write_preview_mask(torch, object_ids, logits, frame, masks)
                    send(status="preview", frame=frame, candidates=candidate_count,
                         automatic=command == "auto", painted=command == "mask"); continue
                removing = command == "remove"
                if removing:
                    preview_baselines.pop(frame, None)
                    preview_mask_baselines.pop(frame, None)
                    clear_prompts_in_frame(predictor, state, frame,
                                           need_output=False)
                    correction_frames.discard(frame)
                    if frame == initial_frame:
                        predictor.add_new_mask(state, frame_idx=initial_frame, obj_id=1,
                                               mask=initial)
                        correction_frames.add(initial_frame)
                clear_stale_memory(predictor, state, frame)
                # Corrections made while initial generation is paused must only
                # rebuild already-generated masks. The outstanding continuation
                # beyond available_end remains a separate operation.
                previous, following = correction_limits(
                    frame, correction_frames, available_end + 1)
                saved_frontier = available_end
                saved_continuation = continuation
                completed_start = previous
                use_compiled_interactive_forwards(prompt_forwards, True)
                try:
                    if frame > previous:
                        send(status="generation", phase="backward",
                             rangeStart=previous, rangeEnd=frame,
                             canStopBackward=True, **metadata)
                        written, backward_last, backward_stopped = propagate(
                            predictor, state, writer, masks,
                            frame, total, True, 0, 50, "Updated backward", profiler,
                            frame - previous, mark_step, commands=commands,
                            allow_pause=False, allow_stop_backward=True)
                        propagated_frames += written
                        writer.flush()
                        if backward_stopped:
                            completed_start = backward_last
                    send(status="generation", phase="forward", rangeStart=frame,
                         rangeEnd=following, **metadata)
                    written, last_frame, paused = propagate(predictor, state, writer,
                        masks, frame, total, False,
                        50 if frame > previous else 0, 50 if frame > previous else 100,
                        "Updated forward", profiler, following - frame, mark_step,
                        skip_start=frame > previous, commands=commands,
                        allow_pause=True)
                    propagated_frames += written
                    writer.flush()
                finally:
                    use_compiled_interactive_forwards(prompt_forwards, False)
                if not removing: correction_frames.add(frame)
                preview_baselines.pop(frame, None)
                preview_mask_baselines.pop(frame, None)
                available_end = saved_frontier
                terminal = dict(anchorFrame=frame, removing=removing,
                    completedStart=completed_start, completedEnd=last_frame,
                    availableEnd=available_end)
                if paused:
                    continuation = dict(target=following, range_start=completed_start,
                                        anchor=frame, removing=removing,
                                        cursor=last_frame, extends_review=False,
                                        resume=saved_continuation)
                    send(status="paused", **terminal, **metadata)
                else:
                    continuation = saved_continuation
                    if continuation is None:
                        available_end = total - 1
                        terminal["availableEnd"] = available_end
                        send(**ready, **terminal)
                    else:
                        terminal["pauseFrame"] = available_end
                        send(status="paused", **terminal, **metadata)
    finally:
        try: writer.close()
        finally:
            if cache_entry is not None:
                try: active_lock(cache_entry).unlink()
                except OSError: pass
            peak = torch.cuda.max_memory_allocated() if device == "cuda" else 0
            total_vram = torch.cuda.get_device_properties(0).total_memory \
                if device == "cuda" else 0
            profiler.report(model=args.model, execution=execution, device=device,
                compileMode=compile_mode,
                gpuFeatureCacheFrames=feature_cache_frames,
                embeddingBatchSize=edge_embedding_batch,
                precision="BF16" if bf16 else "FP32", frames=total,
                propagatedFrames=propagated_frames, expectedWorkMultiplier=expected_factor,
                frameCacheHit=cache_hit, frameCacheStats=cache_stats,
                peakVramBytes=peak, peakRamBytes=peak_process_memory_bytes(),
                totalVramBytes=total_vram,
                frameCacheBytes=directory_size(cache_entry) if cache_entry else 0,
                maskBytes=directory_size(masks), reviewWidth=review_width,
                reviewHeight=review_height, extractionMode=args.extraction_mode)
            if getattr(args, "ready_sent", False):
                update_work_multiplier(runtime, torch, device, args.model,
                                       propagated_frames / max(1, total))


def self_test():
    assert model_size(1920, 1080) == (1024, 576)
    assert correction_limits(75, {0, 50, 100}, 150) == (50, 100)
    assert propagation_range(75, 150, True, 25) == (50, 75)
    assert propagation_range(75, 150, False, 25) == (75, 100)
    assert set(SAM2_MODELS) == {"base-plus", "small", "tiny"}
    inbox = CommandInbox.__new__(CommandInbox)
    inbox.pending, inbox.deferred = queue.Queue(), []
    inbox.pending.put({"command": "stop-backward"})
    assert inbox.poll_control(False, True) == "stop-backward"
    inbox.pending.put({"command": "stop-backward"})
    assert inbox.poll_control(True, False) is None
    inbox.pending.put({"command": "pause"})
    assert inbox.poll_control(True) == "pause"
    try:
        import torch
        from PIL import Image
        with tempfile.TemporaryDirectory(prefix="sam2-writer-test-") as value:
            folder = Path(value)
            devices = ["cpu"] + (["cuda"] if torch.cuda.is_available() else [])
            for device_index, device in enumerate(devices):
                writer = MaskWriter(torch, device, Profiler(None))
                for frame in range(6):
                    logits = torch.tensor([[[1., -1.], [-1., 1.]]], device=device)
                    ids = torch.tensor([1], device=device)
                    writer.submit(ids, logits, device_index * 6 + frame, folder)
                writer.close()
            for saved_path in folder.glob("*.png"):
                with Image.open(saved_path) as saved:
                    values = {index for index, count in enumerate(
                        saved.histogram()) if count}
                    assert values <= {0, 255}
            assert not list(folder.glob("*.tmp-*"))
            # Consecutive interactive previews must publish the latest request,
            # independent of the propagation writer's three transfer slots.
            ids = torch.tensor([1])
            preview = folder / "00000001.png"
            for value in (-1., 1., -1., 1.):
                write_preview_mask(torch, ids,
                    torch.full((1, 2, 2), value), 0, folder)
            with Image.open(preview) as saved:
                assert set(saved.getdata()) == {255}
            state_path = folder / "paint-state.json"
            state_path.write_text(json.dumps(dict(schemaVersion=1, frameCount=6,
                fps=25, reviewWidth=2, reviewHeight=2,
                anchors=[dict(frame=2, mode="paint", points=[], labels=[])])),
                encoding="utf-8")
            assert load_resume_state(state_path, folder, 6, 25, 2, 2) is not None
            resume_folder = folder / "resume-masks"; resume_folder.mkdir()
            for frame in range(12):
                Image.new("L", (2, 2), 255 if frame % 2 else 0).save(
                    resume_folder / f"{frame + 1:08d}.png")
            resume = folder / "resume.json"
            atomic_json(resume, {"schemaVersion": 1, "frameCount": 12,
                "fps": 25.0, "reviewWidth": 2, "reviewHeight": 2,
                "anchors": [{"frame": 4, "mode": "prompt"}]})
            assert load_resume_state(resume, resume_folder, 12, 25.0, 2, 2) is not None
            assert load_resume_state(resume, resume_folder, 11, 25.0, 2, 2) is None
            (resume_folder / "00000009.png").unlink()
            partial = load_resume_state(resume, resume_folder, 12, 25.0, 2, 2)
            assert partial is not None and partial["_availableEnd"] == 7
    except ImportError:
        pass
    print("SAM2 refinement worker self-test passed")


def run_with_adaptive_fallback(args):
    try:
        process(args)
    except Exception as error:
        cache_entry = getattr(args, "active_cache_entry", None)
        if cache_entry is not None:
            try: active_lock(cache_entry).unlink()
            except OSError: pass
        execution = getattr(args, "resolved_execution", "eager")
        if args.execution != "auto" or execution == "eager" or \
                getattr(args, "ready_sent", False):
            raise
        import torch
        device = getattr(args, "resolved_device",
            "cuda" if torch.cuda.is_available() else "cpu")
        invalidate_policy_mode(args.runtime.resolve(), torch, device, args.model,
                               execution, error)
        send(status="progress", percent=15,
             message=f"SAM2 {execution} optimization failed; invalidated its policy entry and retrying in eager mode...")
        args.execution = "eager"
        process(args)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path); parser.add_argument("--runtime", type=Path)
    parser.add_argument("--mask", type=Path); parser.add_argument("--frames", type=Path)
    parser.add_argument("--masks", type=Path); parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int); parser.add_argument("--model",
        choices=tuple(SAM2_MODELS), default="base-plus")
    parser.add_argument("--initial-frame-ms", type=int, default=0)
    parser.add_argument("--engine", choices=("sam2", "edgetam"), default="sam2")
    parser.add_argument("--resume-state", type=Path)
    parser.add_argument("--execution", choices=("auto", "eager", "encoder", "vos"),
                        default="auto")
    parser.add_argument("--compile-cutoff-frames", type=int, default=0)
    parser.add_argument("--cache-root", type=Path)
    parser.add_argument("--cache-limit-bytes", type=int, default=0)
    parser.add_argument("--profile-log", type=Path)
    parser.add_argument("--detailed-profile", action="store_true")
    parser.add_argument("--gpu-feature-cache-frames", type=int, default=0)
    parser.add_argument("--extraction-mode",
        choices=("auto", "standard", "bicubic", "bilinear", "jpeg3", "nvdec"),
        default="auto")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.gpu_feature_cache_frames < 0:
        parser.error("--gpu-feature-cache-frames cannot be negative")
    if args.self_test: self_test()
    elif not all((args.source, args.runtime, args.mask, args.frames,
                  args.masks, args.end_ms is not None)):
        parser.error("--source, --runtime, --mask, --frames, --masks, and --end-ms are required")
    else: run_with_adaptive_fallback(args)


if __name__ == "__main__":
    try: main()
    except Exception as error:
        send(status="error", message=str(error)); raise
