#!/usr/bin/env python3
"""Offline SAM3 keyframe detection / SAM2 tracking experiment; never edits shows.

Run detection with the SAM3 environment, tracking/report with the custom-show
environment. The JSON config records all input paths and benchmark settings.
"""
import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

SCRIPT_STARTED = time.perf_counter()


def save(path, value):
    path.write_text(json.dumps(value, indent=2), encoding="utf-8")


def select_objects(candidates, minimum_new_fraction=.05):
    """Keep distinct people first, then garments that add foreground pixels."""
    import numpy as np
    kept = []
    covered = None
    for item in sorted(candidates, key=lambda x:
                       (x["concept"] != "person", -x["score"])):
        mask = item["mask"]
        if covered is None:
            covered = np.zeros_like(mask, dtype=bool)
        area = int(mask.sum())
        new = int((mask & ~covered).sum())
        if area and new >= 16 and new / area >= minimum_new_fraction:
            kept.append(item)
            covered |= mask
    return kept


def prepare(config):
    from custom_show_worker import model_size
    from rvm_worker import probe
    root = Path(config["output"])
    source = root / "source.mp4"
    started = time.perf_counter()
    if not source.exists():
        subprocess.run([os.environ["IQP_FFMPEG"], "-v", "error", "-ss",
            str(config["sourceStartMs"] / 1000), "-i", config["source"], "-t",
            str(config["durationMs"] / 1000), "-an", "-c:v", "h264_nvenc",
            "-preset", "p5", "-cq", "16", str(source)], check=True)
    source_seconds = time.perf_counter() - started
    extraction_started = time.perf_counter()
    width, height, rate, fps, duration = probe(source)
    frames = root / "frames"
    frames.mkdir(exist_ok=True)
    rw, rh = model_size(width, height)
    subprocess.run([os.environ["IQP_FFMPEG"], "-y", "-v", "error", "-i", str(source),
        "-vf", f"fps={rate},scale={rw}:{rh}:flags=lanczos,setsar=1", "-q:v", "2",
        "-start_number", "0", str(frames / "%08d.jpg")], check=True)
    files = sorted(frames.glob("*.jpg"))
    cuts = subprocess.run([os.environ["IQP_FFMPEG"], "-hide_banner", "-i",
        str(source), "-vf", "select='gt(scene,0.3)',showinfo", "-an", "-f",
        "null", "-"], capture_output=True, text=True, check=True)
    import re
    anchors = set(range(0, len(files), max(1, round(config["intervalSeconds"] * fps))))
    anchors.update(round(float(t) * fps) for t in
                   re.findall(r"pts_time:([\d.]+)", cuts.stderr))
    anchors = sorted(t for t in anchors if 0 <= t < len(files))
    chunks = []
    for start, end in zip(anchors, anchors[1:] + [len(files)]):
        chunk = root / "chunks" / f"{start:08d}"
        chunk.mkdir(parents=True, exist_ok=True)
        for index in range(start, end):
            destination = chunk / f"{index-start:08d}.jpg"
            if not destination.exists():
                os.link(files[index], destination)
        chunks.append({"start": start, "end": end, "path": str(chunk)})
    result = dict(width=width, height=height, reviewWidth=rw, reviewHeight=rh,
                  rate=rate, fps=fps, frameCount=len(files), duration=duration,
                  chunks=chunks, prepareSeconds=time.perf_counter()-started,
                  sourcePreparationSeconds=source_seconds,
                  frameExtractionSeconds=time.perf_counter()-extraction_started)
    save(root / "prepared.json", result)
    print(json.dumps({k: v for k, v in result.items() if k != "chunks"}), flush=True)


def detect(config):
    root = Path(config["output"])
    prepared = json.loads((root / "prepared.json").read_text())
    sam3 = Path(config["sam3Runtime"])
    sys.path.insert(0, str(sam3 / "source" / "SAM2Matting"))
    import numpy as np
    import torch
    from PIL import Image
    from sam3.model_builder import build_sam3_image_model
    from sam3.model.sam3_image_processor import Sam3Processor
    started = time.perf_counter()
    torch.cuda.reset_peak_memory_stats()
    model = build_sam3_image_model(device="cpu", load_from_HF=False,
        bpe_path=str(sam3 / "source/SAM2Matting/sam3/bpe_simple_vocab_16e6.txt.gz"))
    checkpoint = torch.load(sam3 / "checkpoints/SAM2Matting-SAM3.pt",
                            map_location="cpu", weights_only=True)["model"]
    detector = {k.removeprefix("detector."): v for k, v in checkpoint.items()
                if k.startswith("detector.")}
    # This image-only experiment disables the checkpoint's interactive SAM2 adapters.
    detector = {k: v for k, v in detector.items()
                if not k.startswith("backbone.vision_backbone.sam2_convs.")}
    model.load_state_dict(detector, strict=True)
    del checkpoint, detector
    model = model.cuda().eval()
    processor = Sam3Processor(model, confidence_threshold=config["confidence"])
    torch.cuda.synchronize()
    load_seconds = time.perf_counter() - started
    records = []
    with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
        for chunk in prepared["chunks"]:
            stage = time.perf_counter()
            with Image.open(Path(chunk["path"]) / "00000000.jpg") as im:
                state = processor.set_image(im.convert("RGB"))
            torch.cuda.synchronize()
            encode_seconds = time.perf_counter() - stage
            candidates, times = [], {}
            for concept in ("person", "clothing"):
                stage = time.perf_counter()
                processor.reset_all_prompts(state)
                result = processor.set_text_prompt(concept, state)
                masks = result["masks"].squeeze(1).cpu().numpy()
                scores = result["scores"].float().cpu().numpy()
                for mask, score in zip(masks, scores):
                    candidates.append(dict(concept=concept, score=float(score), mask=mask))
                torch.cuda.synchronize()
                times[concept] = time.perf_counter() - stage
            variants = {}
            for variant in ("person", "person-clothing"):
                selected = select_objects([c for c in candidates
                    if variant != "person" or c["concept"] == "person"])
                folder = root / "seeds" / variant / f"{chunk['start']:08d}"
                folder.mkdir(parents=True, exist_ok=True)
                objects = []
                for index, item in enumerate(selected):
                    path = folder / f"{index+1}.png"
                    Image.fromarray(item["mask"].astype(np.uint8)*255).save(path)
                    objects.append(dict(id=index+1, concept=item["concept"],
                        score=item["score"], pixels=int(item["mask"].sum()), mask=str(path)))
                variants[variant] = objects
            record = dict(start=chunk["start"], encodeSeconds=encode_seconds,
                          promptSeconds=times, variants=variants)
            records.append(record)
            print(json.dumps(record), flush=True)
    save(root / "detection.json", dict(loadSeconds=load_seconds,
        elapsedSeconds=time.perf_counter()-started, records=records,
        processSeconds=time.perf_counter()-SCRIPT_STARTED,
        checkpoint="SAM2Matting-SAM3.pt (strict detector weights)",
        peakAllocatedMiB=torch.cuda.max_memory_allocated()/1048576))


def track(config, variant):
    import numpy as np
    import torch
    from PIL import Image
    from custom_show_worker import load_sam2, use_on_demand_sam2_frames
    from sam2matting_worker import prune_sam2_tracking_state, sam2_tracking_history
    from sam2_refine_worker import MaskWriter, Profiler
    root = Path(config["output"])
    prepared = json.loads((root / "prepared.json").read_text())
    detection = json.loads((root / "detection.json").read_text())
    out = root / variant / "masks"
    out.mkdir(parents=True, exist_ok=False)
    started = time.perf_counter()
    torch.cuda.reset_peak_memory_stats()
    execution = config.get("sam2Execution", "eager")
    if execution not in ("eager", "encoder", "vos"):
        raise ValueError(f"Unknown SAM2 execution mode: {execution}")
    backend = config.get("sam2Backend", "inductor")
    if backend not in ("inductor", "cudagraphs"):
        raise ValueError(f"Unknown SAM2 backend: {backend}")
    original_compile = torch.compile
    if backend == "cudagraphs":
        torch._dynamo.config.recompile_limit = 64
        def capture(model, *args, **kwargs):
            kwargs.pop("mode", None)
            kwargs["dynamic"] = False
            return original_compile(model, *args, backend="cudagraphs", **kwargs)
        torch.compile = capture
    try:
        predictor = load_sam2(Path(config["runtime"]), torch, optimized=execution == "vos",
            encoder_compiled=execution == "encoder", model_name=config["sam2Model"],
            compile_mode=config.get("sam2CompileMode", "max-autotune"))
    finally:
        torch.compile = original_compile
    history = sam2_tracking_history(predictor)
    torch.cuda.synchronize()
    load_seconds = time.perf_counter()-started
    chunks = []
    profiler = Profiler(root / variant / "profile.jsonl")
    writer = MaskWriter(torch, "cuda", profiler) if config.get("asyncMasks", False) else None
    encoder_batch = int(config.get("encoderBatch", 1))
    with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
        for chunk, seeds in zip(prepared["chunks"], detection["records"]):
            stage = time.perf_counter()
            objects = seeds["variants"][variant]
            count = chunk["end"]-chunk["start"]
            if not objects:
                for index in range(count):
                    Image.new("L", (prepared["reviewWidth"], prepared["reviewHeight"])).save(
                        out / f"{chunk['start']+index+1:08d}.png")
            else:
                use_on_demand_sam2_frames(Path(chunk["path"]), torch,
                                         prefetch_depth=3, prefetch_to_device=True)
                state = predictor.init_state(video_path=chunk["path"],
                    offload_video_to_cpu=True, offload_state_to_cpu=False)
                original_feature = predictor._get_image_feature
                if encoder_batch > 1:
                    install_encoder_batches(predictor, state, encoder_batch)
                for obj in objects:
                    with Image.open(obj["mask"]) as image:
                        mask = np.asarray(image.convert("L")) > 0
                    predictor.add_new_mask(state, frame_idx=0, obj_id=obj["id"], mask=mask)
                written = 0
                iterator = iter(predictor.propagate_in_video(state))
                while True:
                    if execution == "vos":
                        torch.compiler.cudagraph_mark_step_begin()
                    try:
                        index, ids, logits = next(iterator)
                    except StopIteration:
                        break
                    if writer is not None:
                        # Merge only for output; each object keeps its own tracking memory.
                        writer.submit([1], (logits > 0).any(dim=0, keepdim=True),
                                      chunk["start"]+index, out)
                    else:
                        mask = (logits > 0).any(dim=0).squeeze().cpu().numpy()
                        Image.fromarray(mask.astype(np.uint8)*255).save(
                            out / f"{chunk['start']+index+1:08d}.png")
                    prune_sam2_tracking_state(state, index, False, history)
                    written += 1
                if written != count:
                    raise RuntimeError(f"SAM2 returned {written}/{count} frames")
                predictor.reset_state(state)
                predictor._get_image_feature = original_feature
                state["images"].executor.shutdown(wait=True)
                del state
            torch.cuda.synchronize()
            record = dict(start=chunk["start"], frames=count, objects=len(objects),
                          seconds=time.perf_counter()-stage)
            chunks.append(record)
            print(json.dumps(record), flush=True)
    if writer is not None:
        writer.close()
    if len(list(out.glob("*.png"))) != prepared["frameCount"]:
        raise RuntimeError("Incomplete SAM2 mask sequence")
    save(root / variant / "tracking.json", dict(loadSeconds=load_seconds,
        elapsedSeconds=time.perf_counter()-started, chunks=chunks,
        execution=execution, predictorClass=type(predictor).__name__,
        backend=backend,
        compileMode=config.get("sam2CompileMode", "max-autotune"),
        peakAllocatedMiB=torch.cuda.max_memory_allocated()/1048576))
    profiler.report(encoderBatch=encoder_batch, asyncMasks=writer is not None)


def install_encoder_batches(predictor, state, batch_size):
    """Cache the same SAM2 image features in bounded forward-only batches."""
    import torch
    original = predictor._get_image_feature

    def batched(inference_state, frame_idx, object_count):
        if frame_idx not in inference_state["cached_features"]:
            indices = range(frame_idx, min(frame_idx+batch_size, state["num_frames"]))
            images = torch.stack([state["images"][i] for i in indices]).to(state["device"]).float()
            backbone = predictor.forward_image(images)
            inference_state["cached_features"] = {
                index: (images[offset:offset+1], {
                    key: [value[offset:offset+1] for value in backbone[key]]
                    for key in ("backbone_fpn", "vision_pos_enc")})
                for offset, index in enumerate(indices)}
        return original(inference_state, frame_idx, object_count)

    predictor._get_image_feature = batched


def suite(config):
    root = Path(config["output"])
    worker = Path(__file__).resolve()
    python = str(Path(config["runtime"]) / "venv/Scripts/python.exe")
    prepared = json.loads((root / "prepared.json").read_text())
    results = []
    settings = config["settings"]
    receipts_path = root / "stages.json"
    receipts = json.loads(receipts_path.read_text()) if receipts_path.exists() else {}

    def run(name, command):
        signature = {k:v for k,v in config.items() if k != "qualitySamples"}
        if name in receipts and receipts[name]["command"] == command and \
                receipts[name].get("config") == signature:
            return receipts[name]
        print(f"Starting {name}", flush=True)
        started = time.perf_counter()
        before = subprocess.check_output(["nvidia-smi", "--query-gpu=memory.used,utilization.gpu",
                                          "--format=csv,noheader"], text=True).strip()
        with (root / f"{name}.log").open("w", encoding="utf-8") as log:
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
        seconds = time.perf_counter()-started
        print(f"Finished {name}: {seconds:.2f}s", flush=True)
        receipt = dict(seconds=seconds, gpuBefore=before, command=command, config=signature)
        receipts[name] = receipt
        save(receipts_path, receipts)
        return receipt

    sam3_python = str(Path(config["sam3Runtime"]) / "venv/Scripts/python.exe")
    detector_time = run("detect-timed", [sam3_python, str(worker), "--config",
        str(root / "config.json"), "--stage", "detect"])
    detection = json.loads((root / "detection.json").read_text())
    # Time independent current presets on precisely the same prepared source.
    common = ["--source", str(root / "source.mp4"), "--runtime", config["runtime"],
              "--end-ms", str(config["durationMs"])]
    rvm = run("rvm", [python, str(worker.with_name("rvm_worker.py")), *common,
        "--output", str(root / "rvm"), "--preset", "quality", "--matting-resolution", "512",
        "--execution-mode", "eager", "--sequence-chunk", "12", "--disable-preview"])
    results.append(dict(name="RVM ResNet50", output=str(root / "rvm"), **rvm))
    save(root / "timings.json", results)
    prop = Path(config["runtime"]) / "prop-segmenter/models" / settings["activePropSegmenterModelId"]
    mat_command = [python, str(worker.with_name("matanyone2_worker.py")), *common,
        "--output", str(root / "matanyone"), "--auto-rvm-init", "--max-size", "512",
        "--compile-mode", "eager", "--max-mem-frames", str(settings["lastMatAnyoneMaxMemoryFrames"]),
        "--rvm-alpha-threshold", str(settings["lastRvmInitializerAlphaThresholdPercent"]/100),
        "--rvm-refresh-strength", str(settings["lastRvmMatAnyoneRefreshStrengthPercent"]/100),
        "--disable-previews", "--prop-model", str(prop)]
    if settings["lastMatAnyoneUseLongTermMemory"]:
        mat_command.append("--use-long-term")
    if settings["lastRvmMatAnyoneMaskRefresh"]:
        mat_command.append("--rvm-mask-refresh")
    mat = run("matanyone", mat_command)
    results.append(dict(name="Current RVM-MatAnyone", output=str(root / "matanyone"), **mat))
    save(root / "timings.json", results)
    for variant in ("person", "person-clothing"):
        tracking = run(f"track-{variant}", [python, str(worker), "--config",
            str(root / "config.json"), "--stage", "track", "--variant", variant])
        matting = run(f"matte-{variant}", [python, str(worker.with_name("vitmatte_worker.py")),
            "--source", str(root / "source.mp4"), "--runtime",
            config.get("vitMatteRuntime", config["runtime"]),
            "--end-ms", str(config["durationMs"]),
            "--mask-folder", str(root / variant / "masks"),
            "--output", str(root / variant / "video"), "--model", "s", "--max-size", "1024",
            "--batch-size", str(config.get("vitMatteBatchSize", 2)),
            "--channels-last", config.get("vitMatteChannelsLast", "auto"),
            "--explicit-half", config.get("vitMatteExplicitHalf", "auto"),
            "--execution-mode", "eager", "--no-preview"])
        prompt_cost = sum(r["promptSeconds"]["clothing"] for r in detection["records"])
        detection_seconds = detector_time["seconds"] - (prompt_cost if variant == "person" else 0)
        # Frame extraction is required by the hybrid, unlike the streaming presets.
        # Exclude the common source preparation and include actual JPEG extraction below.
        total = prepared["frameExtractionSeconds"] + detection_seconds + tracking["seconds"] + matting["seconds"]
        results.append(dict(name=f"SAM3 {variant} / SAM2 / ViTMatte S",
            output=str(root / variant / "video"), seconds=total,
            detectionSeconds=detection_seconds, trackingSeconds=tracking["seconds"],
            mattingSeconds=matting["seconds"], frameExtractionSeconds=prepared["frameExtractionSeconds"],
            personOnlyDetectionTimeDerived=variant == "person"))
        save(root / "timings.json", results)


def report(config):
    import html
    import cv2
    import numpy as np
    from PIL import Image, ImageDraw
    root = Path(config["output"])
    prepared = json.loads((root / "prepared.json").read_text())
    results = json.loads((root / "timings.json").read_text())
    # Hand-selected interior patches at 480x270, not full segmentation ground truth.
    samples = config["qualitySamples"]
    frozen = root / "existing" / "alpha.mkv"
    methods = [dict(name=config.get("referenceLabel", "Existing SAM2Matting Tiny"),
                    seconds=config.get("referenceSeconds"),
                    alpha=str(frozen) if frozen.exists() else config["existingAlpha"])]
    methods += [dict(name=r["name"], seconds=r["seconds"],
                     alpha=str(Path(r["output"]) / "alpha.mkv")) for r in results]
    columns, row_height, header_height = len(methods), 294, 50
    canvas = Image.new("RGB", (columns*480, header_height+len(samples)*row_height), (25,25,25))
    draw = ImageDraw.Draw(canvas)
    for column, method in enumerate(methods):
        draw.text((column*480+8, 12), method["name"], fill="white")
        capture = cv2.VideoCapture(method["alpha"])
        if not capture.isOpened():
            raise RuntimeError(f"Cannot open {method['alpha']}")
        details = []
        for row, (seconds, keep, remove) in enumerate(samples):
            frame = round(seconds*prepared["fps"])
            capture.set(cv2.CAP_PROP_POS_FRAMES, frame)
            ok, decoded = capture.read()
            if not ok:
                raise RuntimeError(f"Missing alpha at {seconds}s in {method['alpha']}")
            alpha = cv2.resize(cv2.cvtColor(decoded, cv2.COLOR_BGR2GRAY), (480,270),
                               interpolation=cv2.INTER_AREA)
            tile = Image.fromarray(alpha).convert("RGB")
            tile_draw = ImageDraw.Draw(tile)
            coverage = []
            leakage = []
            for box in keep:
                x0,y0,x1,y1 = box
                coverage.append(float((alpha[y0:y1,x0:x1] >= 120).mean()))
                tile_draw.rectangle(box, outline="lime", width=2)
            for box in remove:
                x0,y0,x1,y1 = box
                leakage.append(float((alpha[y0:y1,x0:x1] >= 120).mean()))
                tile_draw.rectangle(box, outline="red", width=2)
            canvas.paste(tile, (column*480, header_height+row*row_height))
            draw.text((column*480+8, header_height+row*row_height+273),
                f"+{seconds}s; keep: " + ", ".join(f"{v:.0%}" for v in coverage), fill="white")
            details.append(dict(seconds=seconds, keepCoverage=coverage,
                                backgroundLeakage=leakage))
        capture.release()
        method["samples"] = details
        method["meanKeepCoverage"] = float(np.mean([c for r in details for c in r["keepCoverage"]]))
        method["meanBackgroundLeakage"] = float(np.mean([c for r in details for c in r["backgroundLeakage"]]))
    canvas.save(root / "alpha-comparison.jpg", quality=95)
    save(root / "quality.json", dict(threshold=120, samples=samples, methods=methods,
        limitation="Interior patch coverage only; not full-mask accuracy or a hair/edge metric."))
    rows = "".join("<tr><td>"+html.escape(m["name"])+"</td><td>"+
        (f"{m['seconds']:.1f}s / {prepared['frameCount']/m['seconds']:.1f} fps" if m["seconds"] else "Historical; not timed")+
        f"</td><td>{m['meanKeepCoverage']:.1%}</td><td>{m['meanBackgroundLeakage']:.1%}</td></tr>"
        for m in methods)
    previews = ""
    for i, method in enumerate(methods):
        pair = "existing" if i == 0 else Path(results[i-1]["output"]).relative_to(root).as_posix()
        previews += (f'<h2>{html.escape(method["name"])}</h2>'
            f'<p>Full-resolution pair: <a href="{pair}/foreground.mp4">foreground</a> · '
            f'<a href="{pair}/alpha.mkv">alpha</a></p>'
            f'<video controls muted preload="metadata" width="800" src="previews/{i}.mp4"></video>')
    (root / "report.html").write_text("""<!doctype html><meta charset="utf-8">
<title>SAM3 keyframes / SAM2 benchmark</title><style>
body{font:16px system-ui;margin:32px;background:#15171c;color:#eee}td,th{padding:12px;text-align:left;border-bottom:1px solid #555}
table{border-collapse:collapse}img,video{max-width:100%;height:auto}a{color:#9bd}</style>
<h1>SAM3 keyframes → SAM2 tracking</h1><p>First 120 seconds of the supplied clip; 1920×1080 at 60000/1001 fps, 7,193 frames.
SAM3 detects on five-second keyframes plus shot changes. Each interval starts fresh object tracks; masks combine before ViTMatte S at 1024 detail.
RVM and MatAnyone use the existing 512 detail presets. This is a comparison of workflows, not an isolated model comparison.</p>
<table><tr><th>Method</th><th>Processing time / throughput</th><th>Foreground patch retention ↑</th><th>Background patch leakage ↓</th></tr>"""+
        rows+"""</table><p>Green boxes are hand-selected visible-person interior patches; red boxes are background patches.
These checks quantify sampled disappearances, not full-mask accuracy. Inspect the silhouettes for missing limbs and background contamination.</p>
<img src="alpha-comparison.jpg" alt="Alpha masks at six sample times">""" + previews + """
<p>Previews use raw alpha over grey; player threshold, full-opacity mapping, and edge choke are not applied.</p>
<p>One full-clip run per fresh method, with active desktop GPU load. First-job model loading is included; common source preparation is excluded.
Hybrid time is the sum of measured review-frame extraction, detector process, tracking process and matting process times.
Person-only detector cost is derived by subtracting measured clothing prompt calls from the shared detector run.
""" + html.escape(config.get("referenceNote", "Existing alpha is from the original long job, so it has different tracking history and cannot establish a controlled speed comparison.")) + """</p>
<p>Artifacts: <a href="config.json">configuration</a>, <a href="timings.json">timings</a>, <a href="quality.json">patch measurements</a>,
<a href="detection.json">SAM3 detections</a>, <a href="stages.json">exact commands</a>.</p>""", encoding="utf-8")
    print(json.dumps([{k:v for k,v in m.items() if k not in ("samples", "alpha")}
                      for m in methods], indent=2))


def previews(config):
    from fractions import Fraction
    root = Path(config["output"])
    rate = Fraction(json.loads((root / "prepared.json").read_text())["rate"])
    align = f"settb=1/{rate.numerator},setpts=N*{rate.denominator}"
    frozen = root / "existing"
    frozen.mkdir(exist_ok=True)
    for name in ("foreground.mp4", "alpha.mkv"):
        if not (frozen / name).exists():
            subprocess.run([config["ffmpeg"], "-v", "error", "-i",
                str(Path(config["existingAlpha"]).parent / name), "-t",
                str(config["durationMs"]/1000), "-map", "0:v:0", "-c", "copy",
                str(frozen / name)], check=True)
    methods = [frozen]
    methods += [Path(r["output"]) for r in json.loads((root / "timings.json").read_text())]
    folder = root / "previews"
    folder.mkdir(exist_ok=True)
    for index, method in enumerate(methods):
        output = folder / f"{index}.mp4"
        if output.exists():
            continue
        print(f"Compositing preview {index}: {method}", flush=True)
        subprocess.run([config["ffmpeg"], "-v", "error", "-i", str(method / "foreground.mp4"),
            "-i", str(method / "alpha.mkv"), "-f", "lavfi", "-i",
            f"color=c=0x30343b:s=960x540:r={rate}", "-filter_complex",
            f"[0:v]scale=960:540,setsar=1,{align}[rgb];"
            f"[1:v]scale=960:540,format=gray,setsar=1,{align}[a];"
            "[rgb][a]alphamerge[fg];[2:v][fg]overlay=shortest=1:format=auto,format=yuv420p[out]",
            "-map", "[out]", "-t", str(config["durationMs"]/1000), "-an",
            "-c:v", "h264_nvenc", "-preset", "p5", "-cq", "20", str(output)], check=True)


def self_test():
    import numpy as np
    person = np.zeros((20, 20), bool)
    person[2:15, 2:15] = True
    garment = np.zeros_like(person)
    garment[15:20, 10:20] = True
    candidate = lambda concept, mask: dict(concept=concept, mask=mask, score=.9)
    selected = select_objects([candidate("clothing", person.copy()),
        candidate("clothing", garment), candidate("person", person)])
    assert len(selected) == 2 and selected[0]["concept"] == "person"
    assert np.array_equal(np.logical_or.reduce([s["mask"] for s in selected]),
                          person | garment)
    assert not select_objects([candidate("person", np.zeros_like(person))])
    other = np.zeros_like(person); other[2:15, 15:20] = True
    assert len(select_objects([candidate("person", person),
                               candidate("person", other)])) == 2
    import torch
    class Predictor:
        def __init__(self): self.batches = []
        def forward_image(self, image):
            self.batches.append(len(image))
            return {"backbone_fpn": [image+10], "vision_pos_enc": [image+20]}
        def _get_image_feature(self, state, frame, objects):
            return state["cached_features"][frame]
    predictor = Predictor()
    images = [torch.full((3, 2, 2), float(i)) for i in range(7)]
    state = dict(images=images, device="cpu", num_frames=7,
                 cached_features={0: (images[0][None], {"backbone_fpn": [images[0][None]+10]})})
    install_encoder_batches(predictor, state, 4)
    for frame in range(7):
        image, features = predictor._get_image_feature(state, frame, 3)
        assert torch.all(image == frame) and torch.all(features["backbone_fpn"][0] == frame+10)
        assert len(state["cached_features"]) <= 4
    assert predictor.batches == [4, 2]
    import tempfile
    from sam2_refine_worker import MaskWriter, Profiler
    from PIL import Image
    with tempfile.TemporaryDirectory() as temporary:
        writer = MaskWriter(torch, "cpu", Profiler(None))
        logits = torch.full((2, 1, 4, 4), -1.)
        logits[0, 0, 0, 0] = logits[1, 0, 3, 3] = 1
        logits[0, 0, 3, 3] = float("nan")
        writer.submit([1], (logits > 0).any(dim=0, keepdim=True), 0, Path(temporary))
        writer.close()
        with Image.open(Path(temporary) / "00000001.png") as image:
            values = np.asarray(image)
            assert values[0, 0] == values[3, 3] == 255 and np.count_nonzero(values) == 2
    print("SAM3/SAM2 benchmark self-test passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path)
    parser.add_argument("--stage", choices=("prepare", "detect", "track", "suite", "report", "previews"))
    parser.add_argument("--variant", choices=("person", "person-clothing"), default="person-clothing")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not args.config or not args.stage:
        parser.error("--config and --stage are required")
    config = json.loads(args.config.read_text(encoding="utf-8-sig"))
    os.environ.update(IQP_FFMPEG=config["ffmpeg"], IQP_FFPROBE=config["ffprobe"])
    if args.stage == "track":
        track(config, args.variant)
    else:
        {"prepare": prepare, "detect": detect, "suite": suite, "report": report,
         "previews": previews}[args.stage](config)


if __name__ == "__main__":
    main()
