# Creating Custom Shows

QuickPlayer can convert an ordinary video containing one or more people into a
transparent show. Custom cards live alongside official iStripper cards and use
the same search, filters, favourites, ratings, manual queues, automatic queues,
history, hotkeys, and REST controls. Official shows still play through iStripper;
custom shows use QuickPlayer's RGB/alpha player.

For exact installation commands, pinned revisions, hashes, file schemas, and
licensing details, see the [technical custom-show reference](docs/custom-shows.md).

Each new conversion records its processing method and reproducibility-relevant
parameters in the optional `processing` section of the show's `show.json`.
These saved details are visible when editing the custom show's metadata.

## 1. Install the processing tools

Choose **File > Custom Shows > Install / Update Processing Tools...**. Robust
Video Matting is always installed. The default optional selection installs:

- TransNetV2 automatic clip detection;
- MatAnyone2 and SAM2 interactive masking.

VideoMaMa, ViTMatte, and ProPainter are off by default because they add large
downloads, specialised workflows, or restrictive licences. Selecting or
clearing a box controls what setup installs or updates; it does not uninstall
an already installed tool.

Setup creates an isolated Python environment under
`%LOCALAPPDATA%\IStripperQuickPlayer\rvm-runtime`. Then open
**File > Custom Shows > Settings...**, select the library and Python executable,
and click **Validate setup**. Validation checks Python, PyTorch/CUDA, models,
FFmpeg, write access, and one-frame inference.

An NVIDIA CUDA GPU is strongly recommended. CPU/FP32 fallback works but is much
slower. QuickPlayer uses FP16 where the GPU supports it and falls back safely on
older CUDA hardware.

When any SAM2-backed option is selected, setup installs and verifies SAM2.1
Base+, Small, and Tiny. Existing checkpoints whose SHA-256 still matches are
retained, so rerunning setup does not download them again.

## 2. Create the card and model profile

Choose **File > Custom Shows > Create Show...**, select the source, enter a show
title, and select or create a model profile. Model profiles are reusable: edits
to birth date, height, measurements, hair, ethnicity, city, or country update
every linked custom card. Title, dates, description, tags, rating, hotness, clip
types, and age override remain specific to the show.

Choose whether to copy the original into the show folder or retain an absolute
reference to it. A reference stops working if the source is moved or renamed.
An automatic cover is generated from the source midpoint and embeds the model
and show names; its title colour can be changed in the metadata editor.

## 3. Split the source into clips

Choose **Split into clips...** before processing. The editor always works on the
original source, not a generated RGB/alpha pair. Scrub, play, step one frame at
a time, use slow motion, and drag the gold divider triangles to exact positions.
Each segment can have its own hotness, clip types, and included/skipped state.
Double-click a grid row to seek to that segment's first frame without starting
playback. The include checkbox shows a mixed state only when a multi-selection
contains both included and skipped segments; clicking it always chooses a clear
included or skipped state.

**Accurate (TransNetV2)** is the default detector when it is installed; otherwise
QuickPlayer falls back to **Fast (FFmpeg)**. Automatic detection replaces the
current dividers. Optional **Skip transition ±** creates a non-playable buffer
of the selected number of seconds on both sides of every detected cut. Overlapping
buffers are merged.

Auto-detected playable segments shorter than 10 seconds are skipped by default.
They remain visible in the grid and can be re-enabled manually. Likewise, any
transition buffer remains editable. Skipped segments are not processed and do
not become playable clips.

Accurate detection streams bounded 100-frame windows instead of retaining the
whole decoded source in memory, overlaps FFmpeg decode with inference, and runs
several windows together. The shipped CUDA default is batch 8. On the development
RTX 4080, batch 8 measured 357.6 windows/s versus 98.6 at batch 1; batch 16 was
effectively tied at 357.3 windows/s while using more VRAM. Because decode can then
become the bottleneck, a larger batch is not automatically faster end to end.

**Custom Shows > Settings > Benchmark TransNetV2...** manually tests batch sizes,
eager execution, `max-autotune-no-cudagraphs`, CUDA Graph compilation, and decoder
paths. Compilation is enabled only when cached whole-pipeline throughput improves
by at least 5% and detected divider frame numbers are identical. Its first run can
spend substantial time generating a PyTorch compiler cache; later processing
reuses that cache. Automatic decode is codec-aware: compatible CPU decode is used
for H.264, while HEVC/H.265, VP9, and AV1 use CUDA-first decode with CPU fallback.
The optional `fast_bilinear` scaler remains benchmark-only because it shifted four
divider frames in a 600-second fixture. TensorRT is not used. The benchmark is
never started automatically.

Each included segment is processed independently. This deliberately resets RVM
recurrent state, MatAnyone2 state, and SAM2 tracking at scene boundaries.

## 4. Choose a matting method

- **RVM Quality** uses ResNet50 and is the general default.
- **RVM Fast** uses MobileNetV3. It reduces model work, although decode, transfer,
  and dual-stream encoding can limit the end-to-end speed difference.
- **MatAnyone2** requires an initial person mask for every included clip and is
  useful when an explicit subject selection matters. If you have a powerful
  NVIDIA GPU, try MatAnyone2 first for mask-guided work: it gives direct control
  over the subject while remaining much lighter than VideoMaMa. Its mask editor
  can play, scrub, slow-play, and step through the original clip so the mask can
  be created on the clearest frame, not just the first frame. A middle-frame mask
  is propagated to the end and, after resetting recurrent state, backward to the
  beginning. Choosing a later frame therefore adds a preparation/backward pass.
- **VideoMaMa** is a large, slow, high-quality diffusion option requiring CUDA.
- **ViTMatte S/B** uses editable SAM2 video masks as trimaps; S is lighter and B
  normally gives finer refinement. ViTMatte processing is slow, and the B model
  can take several times as long as S; start with S unless the extra refinement
  is worth a substantially longer conversion.

**Matting detail** controls internal inference resolution while exported RGB and
alpha media retain the source display resolution. Standard 512 px is the default.
If processing exhausts dedicated VRAM or spills heavily into shared memory, use
384 px or 256 px and reduce the processing batch size.

RVM Quality and Fast default to 12-frame chunks. Small values reduce peak memory;
large values can improve throughput but may become dramatically slower after
VRAM exhaustion. ViTMatte and VideoMaMa retain their own smaller recommended
batch values. MatAnyone2 advances recurrently one frame at a time, so batch size
does not apply to it.

## 5. Create and correct SAM2 masks

For a mask-guided method, left-click people or missing foreground and right-click
background or unwanted foreground. The initial editor also supports automatic
mask generation. For MatAnyone2, first use the timeline and playback controls to
choose the best source frame; mask creation is enabled on the exact still frame
after playback or scrubbing stops. During video-mask review, scrub to a bad frame, add correction
clicks, inspect the still-frame result, then choose **Update masks** to propagate
the correction backward and forward.

Correction markers on the timeline return to edited frames and restore their
clicks. Blue means originally generated masks, grey means the range currently
being regenerated, and green means regenerated masks. Undo removes correction
clicks from the history; when none remain, playback is unlocked and updating is
disabled.

Mask setup is resumable. QuickPlayer saves initial masks, selected source frames,
click histories, SAM2 per-frame masks, correction anchors/ranges, and each anchor's
undo history beneath `<custom-library>\.mask-drafts`. Cancelling or failing during
conversion keeps that draft. Starting the same source again with the same method,
SAM2 model, and included clip boundaries restores every clip's mask editors and
avoids repeating the initial SAM2 pass. A successful publish or explicit
**Discard** removes the matching draft.

Choose the **SAM2 mask model** beside the processing algorithm. Base+ is the
default and most robust; Small is balanced; Tiny is fastest. Only verified
installed checkpoints appear. The selected model produces the accepted mask
sequence directly, with no hidden Base+ rerun.

Review frames are cached under
`%LOCALAPPDATA%\IStripperQuickPlayer\sam2-frame-cache`. The default limit is
10 GB; change it in **Custom Shows > Settings**, where current usage and a
**Clear cache** action are also shown. Setting 0 disables persistent caching.
The key includes source identity, clip range, frame rate, dimensions, and
extraction settings. An in-session decoded-frame LRU avoids repeated JPEG decode
and preprocessing when correction passes revisit frames.

QuickPlayer uses `sam2-performance-policy-v1.json` instead of a fixed clip-length
rule. The policy is keyed by GPU, driver, PyTorch/CUDA, SAM2 commit, model, and
compile mode. A bounded GPU feature cache qualifies at 2% measured end-to-end
improvement with at least 4 GiB VRAM headroom. Compiled encoder or VOS execution
still requires at least 10% improvement, and expected initial plus correction
work must exceed break-even by a 20% safety margin. Unknown or invalid policy
data uses eager mode. The dialog distinguishes one-time compiler cache
generation, cached compiled loading, and per-worker/CUDA-graph setup.

Custom Show Settings also exposes conservative 16,000-source-frame compile
cutoffs for MatAnyone2 and each SAM2 model. Clips below a cutoff always use eager
execution; the measured policy gates still apply above it. **Benchmark and
recalculate cutoffs...** is the only action that runs these expensive tests. It
uses deterministic media, can take many minutes, is cancellable, and retains the
current cutoff whenever compilation fails the speed or mask-quality gate.

## 6. Performance findings and recommendations

Reference inference measurements were taken on 7 August 2026 with a Ryzen 7
7800X3D, RTX 4080, PyTorch 2.11/CUDA 12.8, 1920×1080 input, and 512 px RVM
matting detail:

| Algorithm | RTX 4080 | CPU only | CPU slowdown |
| --- | ---: | ---: | ---: |
| RVM Fast / MobileNetV3 | 54.6 FPS | 9.10 FPS | 6.0× |
| RVM Quality / ResNet50 | 68.3 FPS | 3.78 FPS | 18.1× |
| MatAnyone2 | 9.99 FPS | 0.314 FPS | 31.8× |

MatAnyone2 per-clip warm-up was 1.50 seconds on CUDA and 32.88 seconds on CPU.
These figures measure inference plus QuickPlayer's tensor upload/output download;
they exclude source decoding, audio, preview snapshots, and foreground/alpha
encoding, so complete conversion runs below the table's FPS. RVM Fast may not be
noticeably faster when those shared pipeline stages are the bottleneck.

RVM now overlaps decode, pinned-memory upload, recurrent inference, asynchronous
download, preview preparation, and FFmpeg writes in up to three bounded reusable
slots. The depth shrinks automatically to keep pinned memory below the smaller of
2 GiB or 12.5% of physical RAM. Preview JPEGs are capped at 960x540 and generated
by a latest-frame-only worker, so stale previews cannot queue behind inference.
An OOM halves the inference chunk once and carries the safe size across the rest
of a multi-clip show.

Use **Custom Shows > Settings > Benchmark RVM...** to measure full 1,000-frame
1080p and 4K jobs for both RVM models, available chunks, preview overhead, bounded
versus serial execution, and optional compilation. Compilation ships disabled
(cutoff 0) and is accepted only after cached end-to-end gains reach 5% at both
resolutions with equivalent alpha. The global custom-show NVENC effort is `p5`
by default; `p1` is faster and `p7` spends more encoder effort. It applies to
RVM, MatAnyone2, ViTMatte, and VideoMaMa. The manual benchmark writes results
to the form only when **Use results** is chosen, and Settings must then be saved.
Detailed stage timings and resolved chunk/pipeline/encoder values go only to
`processing.log`.

A full 1,000-frame 1080p MatAnyone2 conversion at 512 px detail measured 11.19
FPS in the old serial path and 16.21 FPS in the bounded path with previews on,
a 31.0% whole-job gain. Turning previews off changed the bounded result only to
16.31 FPS. Decode/read contributed about 5-6% of wall time, so GPU decoding is
not enabled: its transfer, codec, fallback, and exact-frame-parity costs would
outweigh the available gain in this pipeline. QuickPlayer instead overlaps
software decode/resize, sequential recurrent inference, and alpha/preview/encode
work in three bounded reusable slots.

FFmpeg now shares one normalized source decode between foreground encoding and
a model-resolution RGB branch for Python inference. Python sends only
model-resolution grayscale alpha, which FFmpeg scales and encodes. This avoids
4K RGB pipe traffic, Python input/alpha resizing, RGBA allocation, and roughly
33 MB of RGBA pipe traffic for every 4K frame. ViTMatte and VideoMaMa use the
same split foreground/alpha output path; VideoMaMa also receives only its
model-resolution RGB, while full-resolution ViTMatte retains full-resolution input.

At 4K output the bounded path measured 9.38 FPS without previews. Reusing the
Standard-detail inference buffers for the capped preview improved preview-on
throughput from 8.29 to 8.88 FPS without changing published RGB or alpha media.
A midpoint mask adds a backward pass and temporary alpha mapping; the measured
4K midpoint case ran at 7.03 FPS and used about 253 MB of temporary mapping.

Development tests also rejected partial MatAnyone2 `torch.compile`, explicit
`model.half()`, and OpenCV resizing as production defaults. Cached partial
compilation was slightly slower over the complete job and still recompiled a
graph; explicit half conflicted with required FP32 paths; output-only OpenCV
resizing improved throughput by less than 5%, while changing inference input
pixels exceeded the alpha-equivalence limit. The shipped defaults therefore keep
CUDA autocast with FP32/CPU fallback and Pillow resizing.

A previous controlled 1,000-frame, 1024×576 SAM2 Base+ test including a full
midpoint correction averaged 241.2 seconds eager, 245.2 seconds with full
`max-autotune`, and 271.9 seconds with cached
`max-autotune-no-cudagraphs`; both compiled modes are therefore rejected for
that workload. The current benchmark repeats eager, supported encoder-only, and
full VOS modes for Base+, Small, and Tiny on 1080p and 4K fixtures, including
cold/cached startup and two correction-equivalent passes. Only qualifying
results enter the adaptive policy. Smaller extracted review frames do not
materially accelerate SAM2 because it still uses a fixed 1024×1024 internal
input.

The current corrected-output benchmark uses 1,000 1080p source frames plus two
correction passes (2,667 propagated frame visits). Median eager totals on the
RTX 4080 were 158.4 seconds for Base+, 151.3 seconds for Small, and 142.0
seconds for Tiny. GPU feature caches of 1, 4, 8, 16, 64, and 256 frames were
screened; the workflow produced only three feature hits, and larger capacities
only consumed more VRAM. Three-run one-frame-cache medians changed throughput by
-0.3%, +1.9%, and +0.5% respectively, so none met the 2% gate.

For Base+, cached 1,000-frame totals were 162.9 seconds for encoder-only
`max-autotune-no-cudagraphs`, 172.1 seconds for encoder-only `max-autotune`,
168.3 seconds for full VOS without CUDA graphs, and 179.8 seconds for full VOS
with `max-autotune`. None met the 10% compilation gate, so eager remains the
automatic choice. Tiny likewise measured 154.5 seconds for encoder-only and
147.6 seconds for full VOS without CUDA graphs versus its 142.0-second eager
median. With both ends of the model range losing end-to-end, Small remains eager
instead of inheriting a speculative compiled choice. The compiler paths remain
benchmarkable for future PyTorch/driver/model combinations.

Mask output uses three bounded slots: thresholded GPU masks copy asynchronously
to pinned CPU buffers while a dedicated writer saves atomic binary PNGs at
compression level 1. Progress is throttled while covered-range events continue
updating the correction timeline. Automatic-mask candidates are retained for
the current frame, so refining clicks does not regenerate them.

Practical recommendations:

- Start RVM at 512 px detail and its 12-frame preferred chunk. Start mask-guided
  methods at their model-specific saved batch recommendation.
- Reduce detail to 384 px or 256 px before reducing output resolution; exported
  dimensions remain unchanged.
- For ViTMatte and VideoMaMa, use batch size 1–3 on 12–16 GB GPUs and try 4–6
  only with clear VRAM headroom. RVM's OOM recovery reduces its chunk automatically.
- Avoid VideoMaMa batches of 8–12 on a 16 GB card. A 12-frame, 1024×576 test
  exhausted 16 GB of dedicated VRAM and spilled about 6.5 GB into shared memory,
  making the first batch extremely slow.
- CPU fallback is functional for RVM but generally impractical for MatAnyone2.
- Do not judge ETA during model loading, compilation, or the first few frames;
  startup overhead is excluded once a stable processing rate is available.

The reproducible RVM and MatAnyone2 scripts are
`tools/custom-shows/benchmark_rvm.py` and `benchmark_matanyone2.py`. The SAM2
workflow benchmark is `tools/custom-shows/benchmark_sam2_workflows.py`. Its
structured extraction, decode/preprocess, image-encoding, propagation,
mask-transfer, PNG-writing, prompt, automatic-mask, correction, RAM/VRAM, and
cache records go to `processing.log`, not the processing dialog.

## 7. Monitor, review, and accept

The resizable processing window shows the current source frame and composited
result, frame progress, elapsed time, and ETA. Cancel early if the mask is poor.
Cancellation and failures do not publish partial cards.

After processing, review every clip and tune its lower alpha threshold. New
clips start at **25**: alpha below that value is transparent. Playback settings
also provide a global full-opacity threshold; alpha at or above it becomes fully
opaque, while values between the two thresholds retain soft edges.

Choose **Accept** when you are satisfied with the previews. QuickPlayer closes
every preview player and waits for its RGB/alpha decoders to release the media
before publishing the finished show atomically beneath the configured custom
library. Retry reruns conversion, Open Log shows diagnostics, and Discard removes
staging.

## 8. Playback, storage, and backup

Each custom card can contain multiple clips. Natural completion and **Next clip**
follow the existing manual/automatic queue rules. Custom and official cards can
be mixed in either direction. Moving or resizing the custom player does not alter
iStripper's separately remembered horizontal position.

Custom media is stored under `<custom-library>\shows\<show-id>` and shared model
profiles under `<custom-library>\performers`. QuickPlayer `.iqpb` backups do not
include these files; back up the entire configured custom-library root separately.
Deleting a custom card moves its show folder to the Recycle Bin and does not
delete referenced source videos or shared model profiles.

## Troubleshooting checklist

- Run **Validate setup** after changing Python, CUDA, or optional tools.
- Lower matting detail and batch size when VRAM is full.
- Keep referenced source filenames and paths unchanged if covers may be rebuilt.
- Use **Open Log** for worker, FFmpeg, checkpoint, or media mismatch errors.
- If an optional algorithm is absent from a selector, install it through the
  setup choices and validate again.
- Do not delete a staging folder while its preview or worker is still open.

See [docs/custom-shows.md](docs/custom-shows.md) for manual setup, exact storage
schemas, portability, licence notices, benchmarks, and deeper diagnostics.
