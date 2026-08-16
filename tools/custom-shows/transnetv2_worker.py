#!/usr/bin/env python3
"""Streaming/batched QuickPlayer TransNetV2 worker. Stdout is NDJSON only."""
import argparse
import base64
import collections
import gc
import gzip
import hashlib
import json
import math
import os
import platform
import queue
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path
from types import SimpleNamespace

COMMIT = "85cef72af9a916bdfd7cc94a670c9cdfbf12d1ed"
SOURCE_HASH = "f7c1d437465579a8ec28a5add19853d2cb2755248ea4a4207678210a609428e1"
WEIGHTS_HASH = "46520d66d4bf60414a4d82e0e94a92442ff950e34517a3718b2e54815e642b53"
FRAME_BYTES = 48 * 27 * 3
WINDOW = 100
STRIDE = 50
CENTRE_START = 25


def emit(stage, percent, message, **extra):
    print(json.dumps({"stage": stage, "percent": percent,
                      "message": message, **extra}), flush=True)


def digest(path):
    value = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def source_info(source, ffprobe):
    output = subprocess.check_output([ffprobe, "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=avg_frame_rate,r_frame_rate,codec_name,width,height,pix_fmt:format=duration",
        "-of", "json", source], text=True)
    data = json.loads(output)
    stream = data["streams"][0]
    rate = stream.get("avg_frame_rate") or stream.get("r_frame_rate") or "25/1"
    numerator, denominator = map(int, rate.split("/"))
    fps = numerator / denominator if numerator and denominator else 25.0
    return {"fps": fps, "duration": float(data.get("format", {}).get("duration") or 0),
        "codec": stream.get("codec_name") or "unknown", "width": int(stream.get("width") or 0),
        "height": int(stream.get("height") or 0), "pixFmt": stream.get("pix_fmt") or "unknown"}


def normalize_range(args, info):
    source_duration = info["duration"]
    args.start_ms = max(0, args.start_ms)
    if args.end_ms is not None and args.end_ms > args.start_ms:
        info["duration"] = max(0, min(source_duration - args.start_ms / 1000,
                                      (args.end_ms - args.start_ms) / 1000))
        # An unbounded caller used to send Int64.MaxValue here. Besides being
        # unnecessary, that overflows FFmpeg's duration parser before it can
        # decode a frame. Omit -t whenever the requested range reaches EOF.
        if args.end_ms >= round(source_duration * 1000):
            args.end_ms = None
    else:
        args.end_ms = None
        info["duration"] = max(0, source_duration - args.start_ms / 1000)


def load_model(runtime):
    import torch
    folder = runtime / "transnetv2"
    source = folder / "transnetv2_pytorch.py"
    weights = folder / "transnetv2-pytorch-weights.pth"
    marker = runtime / "TRANSNETV2_COMMIT"
    if not marker.is_file() or marker.read_text().strip() != COMMIT:
        raise RuntimeError("TransNetV2 is not installed. Run Install / Update Processing Tools and choose Yes.")
    if not source.is_file() or digest(source) != SOURCE_HASH:
        raise RuntimeError("TransNetV2 source validation failed; run setup again.")
    if not weights.is_file() or digest(weights) != WEIGHTS_HASH:
        raise RuntimeError("TransNetV2 weights validation failed; run setup again.")
    sys.path.insert(0, str(folder))
    from transnetv2_pytorch import TransNetV2
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = TransNetV2().eval()
    state = torch.load(weights, map_location="cpu", weights_only=True)
    model.load_state_dict(state)
    model = model.to(device)
    if device.type == "cuda":
        torch.backends.cudnn.benchmark = True
        torch.set_float32_matmul_precision("high")
    return torch, model, device


def policy_key(torch, device, batch_size, precision):
    if device.type == "cuda":
        name = torch.cuda.get_device_name(device)
        try:
            driver = subprocess.check_output(["nvidia-smi", "--query-gpu=driver_version",
                "--format=csv,noheader"], text=True, timeout=5).splitlines()[0].strip()
        except Exception:
            driver = "unknown"
    else:
        name, driver = platform.processor() or "cpu", "none"
    return "|".join((str(name), str(driver), torch.__version__,
        str(torch.version.cuda), COMMIT, str(batch_size), precision))


def read_policy(runtime):
    path = runtime / "transnetv2-performance-policy-v1.json"
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if value.get("schemaVersion") == 1 else {"schemaVersion": 1, "entries": {}}
    except Exception:
        return {"schemaVersion": 1, "entries": {}}


def is_fp16_unsupported(error):
    text = str(error).lower()
    return any(value in text for value in (
        "not implemented for 'half'", "not implemented for half",
        "unsupported dtype", "does not support float16", "expected scalar type float"))


def select_precision_and_batch(torch, model, device, requested):
    batch = max(1, min(64, requested))
    use_fp16 = device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 7
    while True:
        sample = None
        try:
            sample = torch.zeros((batch, WINDOW, 27, 48, 3),
                                 dtype=torch.uint8, device=device)
            with torch.inference_mode(), torch.autocast("cuda", dtype=torch.float16,
                    enabled=use_fp16):
                model(sample)
            if device.type == "cuda": torch.cuda.synchronize(device)
            return batch, use_fp16
        except torch.cuda.OutOfMemoryError:
            if batch == 1: raise
            batch = max(1, batch // 2)
            torch.cuda.empty_cache()
            emit("load", 2, f"Batch did not fit GPU memory; continuing with batch {batch}")
        except RuntimeError as error:
            if not use_fp16 or not is_fp16_unsupported(error): raise
            use_fp16 = False
            torch.cuda.empty_cache()
            emit("load", 2, "FP16 is unsupported by this GPU/model; using FP32")
        finally:
            if sample is not None: del sample


def resolve_execution(args, runtime, torch, device, batch, precision, expected_frames):
    key = policy_key(torch, device, batch, precision)
    entry = read_policy(runtime).get("entries", {}).get(key, {})
    requested = args.execution_mode
    if requested == "auto":
        if (entry.get("compiledEnabled") and expected_frames >= args.compile_cutoff_frames
                and expected_frames >= int(entry.get("breakEvenFrames", sys.maxsize))):
            requested = entry.get("compileMode", "compile-no-cudagraphs")
        else:
            requested = "eager"
    return requested, key, entry


def compile_model(model, mode, runtime):
    if mode == "eager": return model
    os.environ.setdefault("TORCHINDUCTOR_CACHE_DIR", str(runtime / "torchinductor-transnetv2"))
    compile_mode = "max-autotune" if mode == "compile-cudagraphs" else "max-autotune-no-cudagraphs"
    emit("compile", 3, "One-time TransNetV2 compilation/cache generation...",
         oneTime=True, compileMode=mode)
    import torch
    return torch.compile(model, mode=compile_mode, fullgraph=False)


def resolve_decode_mode(args, policy_entry, cuda_available, info):
    if args.decode_mode != "auto": return args.decode_mode
    codec = str(info.get("codec") or "unknown").lower()
    bucket = resolution_bucket(info)
    resolution_modes = policy_entry.get("decodeModesByResolution", {}).get(codec, {})
    resolution_exact = policy_entry.get("decodeExactByResolution", {}).get(codec, {})
    candidate = resolution_modes.get(bucket)
    if (resolution_exact.get(bucket) and
            candidate in ("legacy", "cpu", "cpu-fast")):
        return candidate
    candidate = policy_entry.get("decodeModes", {}).get(codec)
    if (policy_entry.get("decodeExact", {}).get(codec) and
            candidate in ("legacy", "cpu", "cpu-fast")):
        return candidate
    if not cuda_available: return "cpu"
    # Development benchmarks found software H.264 decode substantially faster
    # while HEVC, AV1 and VP9 all benefited from NVDEC. Compatible CPU scaling
    # matched legacy divider frames on the tested real H.264 sources.
    return "cpu" if codec in ("h264", "avc1") else "legacy"


def resolution_bucket(info):
    width = max(0, int(info.get("width") or 0))
    height = max(0, int(info.get("height") or 0))
    # Include DCI 4K and common ultrawide variants without treating 1440p as 4K.
    return "4k" if (width * height >= 7_000_000 or
                     max(width, height) >= 3800 and min(width, height) >= 1600) else "standard"


def decode_command(ffmpeg, source, mode, start_ms=0, end_ms=None):
    common = [ffmpeg, "-v", "error"]
    if start_ms > 0:
        common += ["-ss", f"{start_ms / 1000:.6f}"]
    duration_ms = None if end_ms is None else max(1, end_ms - start_ms)
    bounded = [] if duration_ms is None else ["-t", f"{duration_ms / 1000:.6f}"]
    if mode == "legacy":
        return common + ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda",
            "-i", source] + bounded + ["-map", "0:v:0", "-an", "-vf",
            "scale_cuda=48:28,hwdownload,format=nv12,format=rgb24,crop=48:27",
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]
    if mode == "cpu-fast":
        return common + ["-i", source] + bounded + ["-map", "0:v:0", "-an", "-vf",
            "scale=48:27:flags=fast_bilinear", "-pix_fmt", "rgb24",
            "-f", "rawvideo", "pipe:1"]
    return common + ["-i", source] + bounded + ["-map", "0:v:0", "-an", "-s", "48x27",
        "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]


class DecodeFailure(RuntimeError):
    pass


class WindowProducer:
    def __init__(self, torch, command, batch_size, expected_frames, stop):
        self.torch, self.command, self.batch_size = torch, command, batch_size
        self.expected_frames, self.stop = expected_frames, stop
        self.full, self.free = queue.Queue(2), queue.Queue()
        self.error, self.decoded_frames = None, 0
        self.producer_seconds = self.read_seconds = 0.0
        self.assembly_seconds = self.queue_wait_seconds = 0.0
        self.process = None
        for index in range(3):
            cpu = torch.empty((batch_size, WINDOW, 27, 48, 3), dtype=torch.uint8,
                              pin_memory=torch.cuda.is_available())
            self.free.put(SimpleNamespace(index=index, cpu=cpu, gpu=None, valid=0))
        self.thread = threading.Thread(target=self._run, name="TransNet decode", daemon=True)

    def start(self): self.thread.start()

    def cancel(self):
        self.stop.set()
        try:
            if self.process and self.process.poll() is None: self.process.terminate()
        except Exception: pass

    def _put_full(self, value):
        started = time.perf_counter()
        while not self.stop.is_set():
            try:
                self.full.put(value, timeout=.1)
                self.queue_wait_seconds += time.perf_counter() - started
                return True
            except queue.Full:
                pass
        return False

    def _get_free(self):
        started = time.perf_counter()
        while not self.stop.is_set():
            try:
                value = self.free.get(timeout=.1)
                self.queue_wait_seconds += time.perf_counter() - started
                return value
            except queue.Empty: pass
        raise InterruptedError("TransNetV2 detection cancelled")

    def _run(self):
        stderr_tail = collections.deque(maxlen=40)
        stderr_thread = None
        try:
            started = time.perf_counter()
            self.process = subprocess.Popen(self.command, stdout=subprocess.PIPE,
                stderr=subprocess.PIPE, bufsize=0)
            def drain_error():
                for line in iter(self.process.stderr.readline, b""):
                    stderr_tail.append(line.decode(errors="replace").rstrip())
            stderr_thread = threading.Thread(target=drain_error, daemon=True)
            stderr_thread.start()
            decode_error = None
            try:
                self._produce(self.process.stdout)
            except DecodeFailure as error:
                decode_error = error
            exit_code = self.process.wait()
            if stderr_thread: stderr_thread.join(timeout=2)
            self.producer_seconds = time.perf_counter() - started
            if exit_code:
                raise DecodeFailure("\n".join(stderr_tail) or "FFmpeg video decode failed")
            if decode_error:
                raise decode_error
        except BaseException as error:
            self.error = error
        finally:
            if self.stop.is_set():
                try: self.full.put_nowait(None)
                except queue.Full: pass
            else:
                self._put_full(None)

    def _produce(self, stream):
        import numpy as np
        frames = collections.deque()
        base_index = count = next_window = 0
        first = last = None
        carry = bytearray()
        slot = self._get_free(); fill = 0; last_window = None

        def frame_at(index):
            if index < 0: return first
            if index >= count: return last
            return frames[index - base_index]

        def submit(window):
            nonlocal slot, fill, last_window
            assembly_started = time.perf_counter()
            slot.cpu[fill].numpy()[:] = window
            self.assembly_seconds += time.perf_counter() - assembly_started
            last_window = window; fill += 1
            if fill == self.batch_size:
                slot.valid = fill
                if not self._put_full(slot):
                    raise InterruptedError("TransNetV2 detection cancelled")
                slot = self._get_free(); fill = 0

        def assemble_available(eof=False):
            nonlocal next_window, base_index
            target = math.ceil(count / STRIDE) if eof else None
            while ((eof and next_window < target) or
                   (not eof and count - 1 >= next_window * STRIDE + 74)):
                start = next_window * STRIDE - 25
                assembly_started = time.perf_counter()
                window = np.stack([frame_at(index) for index in range(start, start + WINDOW)])
                self.assembly_seconds += time.perf_counter() - assembly_started
                submit(window); next_window += 1
                keep_from = max(0, next_window * STRIDE - 25)
                while frames and base_index < keep_from:
                    frames.popleft(); base_index += 1
        while not self.stop.is_set():
            read_started = time.perf_counter()
            block = stream.read(FRAME_BYTES * 500)
            self.read_seconds += time.perf_counter() - read_started
            if not block: break
            carry.extend(block)
            complete = len(carry) // FRAME_BYTES
            if not complete: continue
            used = complete * FRAME_BYTES
            values = np.frombuffer(carry[:used], np.uint8).reshape((-1, 27, 48, 3)).copy()
            del carry[:used]
            for value in values:
                if first is None: first = value
                last = value; frames.append(value); count += 1
            assemble_available()
        if self.stop.is_set(): raise InterruptedError("TransNetV2 detection cancelled")
        if carry: raise DecodeFailure("FFmpeg returned incomplete scene-detection frames")
        if not count: raise DecodeFailure("FFmpeg returned no scene-detection frames")
        assemble_available(eof=True)
        if fill:
            for index in range(fill, self.batch_size): slot.cpu[index].numpy()[:] = last_window
            slot.valid = fill
            if not self._put_full(slot):
                raise InterruptedError("TransNetV2 detection cancelled")
        else:
            self.free.put(slot)
        self.decoded_frames = count


def run_pipeline(args, torch, model, device, command, batch_size, use_fp16,
                 expected_frames, execution_mode):
    import numpy as np
    stop = threading.Event()
    producer = WindowProducer(torch, command, batch_size, expected_frames, stop)
    previous_handlers = {}
    def cancel(_signum, _frame): producer.cancel()
    for value in (getattr(signal, "SIGINT", None), getattr(signal, "SIGTERM", None)):
        if value is not None:
            try: previous_handlers[value] = signal.signal(value, cancel)
            except Exception: pass
    predictions, inference_ms, transfer_ms = [], 0.0, 0.0
    processed_frames = 0
    transfer_stream = torch.cuda.Stream(device=device) if device.type == "cuda" else None
    last_progress_time = 0.0
    producer.start()
    pending = None

    def schedule(slot):
        if device.type != "cuda": return (slot, None, None, None)
        if slot.gpu is None:
            slot.gpu = torch.empty_like(slot.cpu, device=device)
        start_event, end_event = torch.cuda.Event(True), torch.cuda.Event(True)
        with torch.cuda.stream(transfer_stream):
            start_event.record(transfer_stream)
            slot.gpu.copy_(slot.cpu, non_blocking=True)
            end_event.record(transfer_stream)
        return (slot, end_event, start_event, end_event)

    def infer(item):
        nonlocal inference_ms, transfer_ms, last_progress_time, processed_frames
        slot, ready, transfer_start, transfer_end = item
        if device.type == "cuda":
            torch.cuda.current_stream(device).wait_event(ready)
            inference_start, inference_end = torch.cuda.Event(True), torch.cuda.Event(True)
            inference_start.record()
            inputs = slot.gpu
        else:
            started = time.perf_counter(); inputs = slot.cpu
        with torch.inference_mode(), torch.autocast("cuda", dtype=torch.float16,
                enabled=use_fp16):
            logits, _ = model(inputs)
        values = logits[:slot.valid, CENTRE_START:CENTRE_START + STRIDE, 0]
        if device.type == "cuda":
            inference_end.record(); values = values.float().cpu().numpy().reshape(-1)
            torch.cuda.synchronize(device)
            inference_ms += inference_start.elapsed_time(inference_end)
            transfer_ms += transfer_start.elapsed_time(transfer_end)
        else:
            values = values.float().numpy().reshape(-1)
            inference_ms += (time.perf_counter() - started) * 1000
        predictions.append(values)
        processed_frames += len(values)
        processed = processed_frames
        now = time.perf_counter()
        if (now - last_progress_time >= .15 or processed == STRIDE or
                (expected_frames and processed >= expected_frames)):
            percent = 5 + 94 * processed / max(1, expected_frames or processed)
            emit("detect", min(99, percent),
                 f"Detected {min(processed, expected_frames or processed)}/{expected_frames or processed} frames",
                 processedFrames=processed)
            last_progress_time = now
        producer.free.put(slot)

    try:
        while True:
            slot = producer.full.get()
            if slot is None: break
            if stop.is_set():
                raise InterruptedError("TransNetV2 detection cancelled")
            next_item = schedule(slot)
            if pending is not None: infer(pending)
            pending = next_item
        if pending is not None: infer(pending)
        producer.thread.join()
        if producer.error: raise producer.error
        values = np.concatenate(predictions)[:producer.decoded_frames]
        return values, {"producerSeconds": producer.producer_seconds,
            "decodeReadSeconds": producer.read_seconds,
            "assemblySeconds": producer.assembly_seconds,
            "queueWaitSeconds": producer.queue_wait_seconds,
            "inferenceSeconds": inference_ms / 1000,
            "transferSeconds": transfer_ms / 1000,
            "frames": producer.decoded_frames, "executionMode": execution_mode}
    finally:
        producer.cancel(); producer.thread.join(timeout=5)
        for value, handler in previous_handlers.items():
            try: signal.signal(value, handler)
            except Exception: pass


def dividers(predictions, fps, threshold_probability=.5):
    import numpy as np
    found, start = [], None
    threshold_logit = math.log(threshold_probability / (1 - threshold_probability))
    for index, is_transition in enumerate(predictions > threshold_logit):
        if is_transition and start is None: start = index
        if start is not None and (not is_transition or index == len(predictions) - 1):
            end = index if not is_transition else index + 1
            peak = start + int(np.argmax(predictions[start:end]))
            at = round(peak * 1000 / fps)
            if at >= 250 and (not found or at - found[-1] >= 500): found.append(at)
            start = None
    return found


def append_profile(path, value):
    if not path: return
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "a", encoding="utf-8") as stream:
        stream.write(json.dumps(value) + "\n")


def memory_profile(torch, device):
    value = {}
    if device.type == "cuda":
        value["peakVramBytes"] = int(torch.cuda.max_memory_allocated(device))
        value["peakVramReservedBytes"] = int(torch.cuda.max_memory_reserved(device))
    try:
        import psutil
        memory = psutil.Process().memory_info()
        value["workingSetBytes"] = int(memory.rss)
        peak = getattr(memory, "peak_wset", None)
        if peak is not None: value["peakWorkingSetBytes"] = int(peak)
    except Exception:
        pass
    return value


def self_test():
    import numpy as np
    predictions = np.array([-2, 1, 3, 1, -2, -1, 2, 4, -1], np.float32)
    assert dividers(predictions, 10) == [700]
    assert dividers(np.array([-.5, -.5, -.5, -.3, -.5]), 10, .4) == [300]
    automatic = SimpleNamespace(decode_mode="auto")
    assert resolution_bucket({"width": 3840, "height": 2160}) == "4k"
    assert resolution_bucket({"width": 2160, "height": 3840}) == "4k"
    assert resolution_bucket({"width": 2560, "height": 1440}) == "standard"
    resolution_policy = {
        "decodeModesByResolution": {"h264": {"4k": "legacy"}},
        "decodeExactByResolution": {"h264": {"4k": True}}}
    assert resolve_decode_mode(automatic, resolution_policy, True,
        {"codec": "h264", "width": 3840, "height": 2160}) == "legacy"
    resolution_policy["decodeExactByResolution"]["h264"]["4k"] = False
    assert resolve_decode_mode(automatic, resolution_policy, True,
        {"codec": "h264", "width": 3840, "height": 2160}) == "cpu"
    assert resolve_decode_mode(automatic, {}, True, {"codec": "h264"}) == "cpu"
    for codec in ("hevc", "vp9", "av1"):
        assert resolve_decode_mode(automatic, {}, True, {"codec": codec}) == "legacy"
    assert resolve_decode_mode(automatic, {}, False, {"codec": "hevc"}) == "cpu"
    unbounded = SimpleNamespace(start_ms=0, end_ms=2**63 - 1)
    unbounded_info = {"duration": 4379.442}
    normalize_range(unbounded, unbounded_info)
    assert unbounded.end_ms is None and unbounded_info["duration"] == 4379.442
    bounded = SimpleNamespace(start_ms=1000, end_ms=3000)
    bounded_info = {"duration": 10.0}
    normalize_range(bounded, bounded_info)
    assert bounded.end_ms == 3000 and bounded_info["duration"] == 2.0
    print("TransNetV2 worker self-test passed")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source")
    parser.add_argument("--runtime")
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--compile-cutoff-frames", type=int, default=16000)
    parser.add_argument("--execution-mode", choices=("auto", "eager",
        "compile-no-cudagraphs", "compile-cudagraphs"), default="auto")
    parser.add_argument("--decode-mode", choices=("auto", "legacy", "cpu", "cpu-fast"),
                        default="auto")
    parser.add_argument("--profile-log", type=Path)
    parser.add_argument("--threshold-probability", type=float, default=.5)
    parser.add_argument("--start-ms", type=int, default=0)
    parser.add_argument("--end-ms", type=int)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test: self_test(); return
    if not args.source or not args.runtime: parser.error("--source and --runtime are required")
    if not 0 < args.threshold_probability < 1:
        parser.error("--threshold-probability must be between 0 and 1")
    ffmpeg = os.environ.get("IQP_FFMPEG", "ffmpeg")
    ffprobe = os.environ.get("IQP_FFPROBE", "ffprobe")
    runtime = Path(args.runtime)
    started = time.perf_counter()
    emit("load", 0, "Loading TransNetV2...")
    model_load_started = time.perf_counter()
    torch, eager_model, device = load_model(runtime)
    model_load_seconds = time.perf_counter() - model_load_started
    probe_started = time.perf_counter()
    info = source_info(args.source, ffprobe)
    normalize_range(args, info)
    probe_seconds = time.perf_counter() - probe_started
    expected = round(info["fps"] * info["duration"])
    batch = max(1, min(64, args.batch_size))
    use_fp16 = (device.type == "cuda" and
                 torch.cuda.get_device_capability(device)[0] >= 7)
    precision = "FP16" if use_fp16 else "FP32"
    emit("load", 5, "Preparing streaming scene detection...",
         batchSize=batch, precision=precision)
    execution, key, policy_entry = resolve_execution(
        args, runtime, torch, device, batch, precision, expected)
    model = eager_model
    compile_seconds = 0.0
    try:
        compile_started = time.perf_counter()
        model = compile_model(model, execution, runtime)
        if execution != "eager":
            compiled_batch, compiled_fp16 = select_precision_and_batch(
                torch, model, device, batch)
            if compiled_batch != batch or compiled_fp16 != use_fp16:
                raise RuntimeError("compiled preflight changed the fixed batch or precision")
        compile_seconds = time.perf_counter() - compile_started
    except Exception as error:
        compile_seconds = time.perf_counter() - compile_started
        reason = str(error).splitlines()[0][:300]
        emit("load", 4, f"Compiled TransNetV2 unavailable; using eager ({reason})")
        model, execution = eager_model, "eager"
    decode_mode = resolve_decode_mode(
        args, policy_entry, device.type == "cuda", info)
    emit("detect", 5, f"Streaming {decode_mode} decode with batch {batch} on "
         f"{device.type.upper()} {precision}", batchSize=batch, decodeMode=decode_mode,
         executionMode=execution)
    if device.type == "cuda": torch.cuda.reset_peak_memory_stats(device)

    def process_with_runtime_fallback(mode):
        nonlocal batch, use_fp16, precision, execution, model
        while True:
            try:
                return run_pipeline(args, torch, model, device,
                    decode_command(ffmpeg, args.source, mode,
                                   args.start_ms, args.end_ms), batch, use_fp16,
                    expected, execution)
            except torch.cuda.OutOfMemoryError:
                if device.type != "cuda" or batch <= 1: raise
                batch = max(1, batch // 2)
                model, execution = eager_model, "eager"
                gc.collect(); torch.cuda.empty_cache()
                emit("load", 5,
                     f"GPU memory was insufficient; restarting with batch {batch}",
                     batchSize=batch)
            except RuntimeError as error:
                if not use_fp16 or not is_fp16_unsupported(error): raise
                use_fp16 = False; precision = "FP32"
                model, execution = eager_model, "eager"
                gc.collect(); torch.cuda.empty_cache()
                emit("load", 5,
                     "FP16 is unsupported by this GPU/model; restarting with FP32",
                     precision=precision)

    try:
        logits, profile = process_with_runtime_fallback(decode_mode)
    except DecodeFailure:
        if decode_mode != "legacy": raise
        emit("decode", 5, "CUDA decode unavailable; restarting with compatible CPU decode")
        decode_mode = "cpu"
        logits, profile = process_with_runtime_fallback(decode_mode)
    cuts = dividers(logits, info["fps"], args.threshold_probability)
    duration_ms = round(len(logits) * 1000 / info["fps"])
    cuts = [cut for cut in cuts if cut <= duration_ms - 250]
    total_seconds = time.perf_counter() - started
    key = policy_key(torch, device, batch, precision)
    profile.update({"profile": "transnetv2-v1", "totalSeconds": total_seconds,
        "processingFps": len(logits) / max(total_seconds, 1e-9),
        "modelLoadSeconds": model_load_seconds, "probeSeconds": probe_seconds,
        "compileOrCacheSeconds": compile_seconds,
        "batchSizeRequested": args.batch_size, "batchSizeUsed": batch,
        "precision": precision, "device": str(device), "decodeMode": decode_mode,
        "decodeResolutionBucket": resolution_bucket(info),
        "policyKey": key, "source": info, "dividerCount": len(cuts)})
    profile.update(memory_profile(torch, device))
    append_profile(args.profile_log, profile)
    score_data = base64.b64encode(gzip.compress(
        logits.astype("<f4", copy=False).tobytes(), compresslevel=9)).decode("ascii")
    emit("complete", 100, f"Detected {len(cuts)} scene changes", dividersMs=cuts,
         sensitivityDataFormat="transnet-logits-gzip-f32-v1",
         sensitivityData=score_data, detectionFrameRate=info["fps"],
         profile=profile)


if __name__ == "__main__":
    try: main()
    except (KeyboardInterrupt, InterruptedError): raise SystemExit(130)
    except Exception as error:
        print(str(error), file=sys.stderr, flush=True)
        raise SystemExit(1)
