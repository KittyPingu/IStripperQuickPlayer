# Custom shows

Custom shows are normal QuickPlayer library cards backed by a local foreground video and alpha video. They can be searched, sorted, filtered, rated, favourited, dragged, saved in manual queues, selected by automatic queues, and controlled through REST alongside iStripper cards. iStripper still plays official shows; QuickPlayer's transparent player is used only for IDs beginning `custom:`.

For the task-oriented workflow, begin with [Creating Custom Shows](../CUSTOM_SHOWS.md).
This document is the exact setup, format, licensing, and troubleshooting reference.

Only process videos you have permission to use. V1 mattes people (one or more visible people) and links each show to one primary reusable model profile. It does not segment arbitrary objects; SAM2 correction clicks refine person masks.

## Requirements

- Windows x64 and QuickPlayer's FFmpeg 8 dependency bundle.
- Python 3.11–3.14. Setup prefers the newest compatible installed version.
- An NVIDIA CUDA GPU supported by the CUDA 12.8 PyTorch wheels is strongly recommended. CPU/FP32 fallback is supported; an RTX 4080 is the reference configuration.
- Git on `PATH` for the one-time setup.
- Enough free space for the source, staging outputs, and H.264 foreground/alpha media.

QuickPlayer pins:

- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) commit `53d74c6826735f01f4406b5ca9075eee27bec094`.
- PyTorch `2.11.0`, torchvision `0.26.0`, CUDA `12.8` wheels, and NumPy `2.3.2`.
- Triton for Windows `3.6.0.post26` for compiled SAM2 video propagation.
- RVM MobileNetV3 SHA-256 `3C7C1D92033F7C38D6577C481D13A195D7D80A159B960F4F3119AC7B534CF4F8`.
- RVM ResNet50 SHA-256 `C191A807251164C073DCE5FA408E7A816070D539B882B2A3150330A9FEC112CE`.
- Optional [TransNetV2](https://github.com/soCzech/TransNetV2) PyTorch source commit `85cef72af9a916bdfd7cc94a670c9cdfbf12d1ed` and converted-weight SHA-256 `46520D66D4BF60414A4D82E0E94A92442FF950E34517A3718B2E54815E642B53`.
- Optional [MatAnyone2](https://github.com/pq-yang/MatAnyone2) commit `0079197acd6d16a741f71558809c06c586c579e0` and checkpoint SHA-256 `5E9821E4087231427376B437C85BB6E072B41E582314F06FD524F75BC4AF5914`.
- Optional [VideoMaMa](https://github.com/cvlab-kaist/VideoMaMa) commit `d5cce3e0ffe3b6429c147e658bb28bcfb576374c`, model revision `e289a7acc8403c4fbe4dea2a1de5a9749ebc9bf5`, and inference UNet SHA-256 `F2442BF16EDEDAD25C1C272AE7535B6411C43CEE5C27B012BB6F7FDA72D07B8C`.
- VideoMaMa's SVD inference components use revision `9e43909513c6714f1bc78bcb44d96e733cd242aa`; every mask-guided algorithm uses [SAM2](https://github.com/facebookresearch/sam2) commit `2b90b9f5ceec907a1c18123530e92e794ad901a4`. Setup verifies Hiera Base+ SHA-256 `A2345AEDE8715AB1D5D31B4A509FB160C5A4AF1970F199D9054CCFB746C004C5`, Small `6D1AA6F30DE5C92224F8172114DE081D104BBD23DD9DC5C58996F0CAD5DC4D38`, and Tiny `7402E0D864FA82708A20FBD15BC84245C2F26DFF0EB43A4B5B93452DEB34BE69`.
- Optional [ViTMatte](https://github.com/hustvl/ViTMatte) S revision `6a58ad7646403c1df626fbd746900aec7361ea1d` and B revision `bf486d01a7d9e3dbcc8400f7942835caf0eaf76e`; setup SHA-256 verifies both official Hugging Face model repositories.
- Optional [streaming ProPainter](https://github.com/osmr/propainter) commit `c8983a445720450bf2fd976cab0adb1cad19547d` and PyTorchCV `0.0.74`. Its RAFT, flow-completion, and ProPainter files are verified against upstream SHA-1 values `d74fed4b7511a95ca9713b69ff19a132210a1016`, `a865ddc0281f27ff0814939a8c0101e936678312`, and `5f3cc1e7083fb5c49a7e5c574a06d41c763550a5`.

## Exact setup

The simplest method is **File → Custom Shows → Install / Update Processing
Tools…**. TransNetV2 and MatAnyone2 are selected by default; VideoMaMa,
ViTMatte, and ProPainter are initially cleared. Select the tools to install or
update in the choices dialog, then leave
the setup window open until it finishes. Selecting any mask-guided method
installs SAM2 for interactive source-frame selection. VideoMaMa and ViTMatte also
use SAM2 for the editable tracked-mask workflow. QuickPlayer selects the isolated Python automatically. If no
compatible Python is installed and Windows Package Manager is available, setup
installs Python 3.12 for the current user first.

Open PowerShell in the QuickPlayer installation or build output folder and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\custom-shows\setup.ps1
```

Add `-InstallTransNetV2` to install and verify the optional PyTorch scene detector:

```powershell
.\custom-shows\setup.ps1 -InstallTransNetV2
```

Add `-InstallMatAnyone2` to install and verify MatAnyone2 together with SAM2;
switches can be combined:

```powershell
.\custom-shows\setup.ps1 -InstallTransNetV2 -InstallMatAnyone2
```

Add `-InstallVideoMaMa` for the CUDA-only high-quality backend. This downloads
roughly 8 GB of verified inference weights and also installs SAM2:

```powershell
.\custom-shows\setup.ps1 -InstallVideoMaMa
```

Add `-InstallViTMatte` to install both ViTMatte S and B plus SAM2:

```powershell
.\custom-shows\setup.ps1 -InstallViTMatte
```

Add `-InstallProPainter` to install the optional static video-object removal
tool and its three verified checkpoints:

```powershell
.\custom-shows\setup.ps1 -InstallProPainter
```

The default isolated runtime is `%LOCALAPPDATA%\IStripperQuickPlayer\rvm-runtime`. To choose another location or Python launcher:

```powershell
.\custom-shows\setup.ps1 -RuntimeRoot 'D:\QuickPlayer-RVM' -PythonLauncher 'C:\Windows\py.exe'
```

The script creates `venv`, checks out exact commits, downloads verified weights, and rejects mismatched checkpoints. Optional TransNetV2 is installed under `<runtime>\transnetv2`; MatAnyone2 uses `<runtime>\matanyone2`; VideoMaMa uses `<runtime>\videomama*`; shared SAM2 uses `<runtime>\sam2`; streaming ProPainter uses `<runtime>\propainter-streaming` and `<runtime>\propainter-streaming-weights`. TensorFlow and VideoMaMa training dependencies are not installed. Rerunning setup retains every existing source or weight file whose hash already matches. Legacy `<runtime>\segment-anything` files are no longer used and may be deleted. It prints download progress and the Python executable to select in QuickPlayer.

QuickPlayer only lists TransNetV2, MatAnyone2, VideoMaMa, ViTMatte, and ProPainter in operational
selectors when their installation markers, source folders, workers, and required
model files are present. The setup choices remain visible so missing tools can be installed.

In QuickPlayer, open **File → Custom Shows → Settings**:

1. Select the custom library folder. The default is `%LOCALAPPDATA%\IStripperQuickPlayer\custom-shows`.
2. Select `<runtime>\venv\Scripts\python.exe`.
3. Click **Validate setup**. Validation checks Python 3.11–3.14, exact PyTorch/torchvision and CUDA-wheel versions, the RVM commit marker, checkpoint hash, FFmpeg, ffprobe, library write access, and a one-frame inference on the available device.

If QuickPlayer reports that an FFmpeg library is missing, repair/reinstall QuickPlayer so its FFmpeg 8 files (`ffmpeg.exe`, `ffprobe.exe`, `avcodec-62.dll`, `avformat-62.dll`, `avutil-60.dll`, `swresample-6.dll`, and `swscale-9.dll`) are beside `IStripperQuickPlayer.exe`. Do not substitute an incompatible FFmpeg major version.

## Removing a static video object or watermark

Only remove material from video you own or are authorised to modify. Open
**File → Custom Shows → Remove Video Object / Watermark**.
Choose an MP4, MOV, or AVI source and a new MP4 output path. QuickPlayer extracts
a representative frame; drag one rectangle around the static area to remove.
The red/yellow overlay previews the mask before processing. The source is never
overwritten, and source audio is encoded as AAC into the cleaned output.

**Fast (FFmpeg delogo)** is the default and is intended for small static logos or
watermarks. It interpolates from the pixels around the selected rectangle, reports
frame progress immediately, and normally runs around playback speed or faster.
Install optional ProPainter to enable **AI High Quality**, which is appropriate
when the selected area needs temporal AI reconstruction rather than interpolation.

ProPainter is substantially more memory-intensive than RVM. Start with **50%
processing resolution**, a **40-frame temporal window**, and **FP16** on a 12–16
GB NVIDIA GPU. The output dimensions remain those of the source; processing at a
lower resolution trades inpaint detail for speed and lower VRAM. Reduce the window
to 20–30 frames if CUDA runs out of memory, or raise it only when there is measured
headroom. Frames are decoded into a bounded rolling buffer, processed on CUDA, and
encoded immediately (NVENC when available), so long videos no longer create a
full lossless-PNG copy or need to fit in RAM. The dialog reports completed/total
frames and derives its remaining-time estimate from that progress. CPU/FP32
fallback is functional but normally too slow for full videos. Cancellation
terminates the worker tree, deletes its partial MP4, and leaves both the source and
any previous completed output intact. A `<output>.processing.log` file records
worker diagnostics.

QuickPlayer automatically caps ProPainter's temporal window for the selected
resolution and available VRAM. On a 16 GB GPU, the defaults are 12 frames at full
HD processing, 16 at 75%, and 40 at 50%; this prevents Windows shared-GPU-memory
spill from turning a high-quality run into a system-wide memory stall.

## Creating a show

Choose **File → Custom Shows → Create Show**. Source video, show title, and model profile are required. Choose **Copy original** to place the original under the show folder; otherwise the absolute original path is recorded as a reference and is never deleted by QuickPlayer.

Before processing, choose **Split into clips...** to open the clip editor on the
original source. Scrub or play the preview, place dividers, and drag only their
gold triangle handles below the timeline; scrubbing across divider lines no
longer moves them. Select each resulting segment to set its hotness and clip types.
Choose **Fast (FFmpeg)** for lightweight built-in scene-score detection or
**Accurate (TransNetV2)** for neural detection. Accurate is selected by default
when installed. Click **Auto-detect clips** to replace current dividers. Enable
**Skip transition ±** to turn the selected number of seconds on both sides of
each detected cut into a skipped segment; overlapping transition buffers are
merged. Auto-detected playable segments shorter than 10 seconds are also
skipped by default. Both kinds remain visible and can be re-enabled manually.
TransNet inference runs on CUDA when available and FFmpeg
uses CUDA decode/scale where the source codec supports it, with automatic CPU
decode fallback. Clear **Include this segment as a playable
clip** to mark a segment as skipped; it remains editable in the manifest but is
not exposed to queues or playback. The values on the
main creation form remain overall card metadata. Every included segment is
processed independently into its own foreground/alpha pair. This resets RVM or
MatAnyone2 recurrent state and VideoMaMa/SAM2 tracking at every clip boundary; skipped segments are not
processed.

The **Quality** preset uses RVM ResNet50 and is the default. **Fast** uses MobileNetV3. QuickPlayer normalizes display rotation, variable frame rate, square pixels, and odd dimensions while FFmpeg streams frames to RVM. RVM then streams RGBA into one FFmpeg encoder process:

The optional **MatAnyone 2** algorithm opens an initial-mask editor for every
included clip before processing. Play, scrub, use 0.25x slow motion, or step a
frame at a time through the original clip to choose a clear mask frame. After
playback or scrubbing stops, QuickPlayer loads the exact still frame while keeping
SAM2 base-plus resident, then updates the mask interactively: left-click
every person to include and right-click unwanted people or background areas to
exclude. **Auto mask frame** runs SAM2's automatic image-mask generator; positive
clicks guide which generated masks are combined, while no clicks selects the
largest useful candidate for further refinement. MatAnyone propagates a first-frame
mask forward normally. For a middle or last frame it propagates forward to the end,
resets its recurrent processor, and propagates backward to the beginning. Earlier
frames are prepared at the selected **Matting detail** resolution on disk, so this
does not retain the complete decoded video in RAM; selecting a later mask frame does
add preparation and backward-propagation time. **Matting detail** controls
MatAnyone's internal resolution;
the processing batch-size control does not apply because its recurrent pass is
sequential. This bidirectional behavior follows the
[ComfyUI-MatAnyone `mask_frame` workflow](https://github.com/FuouM/ComfyUI-MatAnyone?tab=readme-ov-file#workflow)
and its [middle-frame two-pass implementation](https://github.com/FuouM/ComfyUI-MatAnyone/blob/main/mat_anyone2.py#L100-L166),
rather than the official MatAnyone 2 command-line interface's first-frame-only
workflow.

The optional **VideoMaMa High Quality** algorithm reuses that initial mask,
tracks it across each clip with SAM2, and refines the resulting masks in
configurable VideoMaMa groups. Its internal frame size preserves aspect ratio while
staying within approximately 1024×576 pixels; the alpha is resized back to the
source display dimensions before encoding. It requires NVIDIA CUDA, about 8 GB
of installed weights, substantial temporary disk space for normalized clip
frames, and is expected to be much slower than RVM or MatAnyone2.

For **VideoMaMa**, **ViTMatte S**, and **ViTMatte B**, QuickPlayer runs the
initial SAM2 propagation before matting and opens a resizable review editor.
Scrub, play, or step one frame at a time through the tracked overlay. Left-click
missing person pixels and right-click background or unwanted pixels. Clicks update
only that frame's preview; **Update masks** then propagates the accepted prompt
backward and forward. **Auto mask frame** can replace the current frame prompt with
an automatically generated candidate before updating. Later corrections stop at
adjacent accepted correction frames instead of needlessly recalculating beyond
them. Playback includes normal speed, 0.25× slow motion, and frame stepping. Repeat
on later failures, then choose **Use corrected masks**. Choose Base+ (default and
most robust), Small (balanced), or Tiny (fastest) in **SAM2 mask model**. Only
installed, size-verified checkpoints are listed and the selected model directly
produces the accepted masks. CUDA uses BF16 autocast, TF32 matmul/cuDNN, and cuDNN
benchmarking where supported; unsupported CUDA and CPU retain FP32 fallback.

Initial and per-frame masks are saved as resumable drafts under
`<custom-library>\.mask-drafts`. If conversion is cancelled or fails after mask
setup, starting the same source again with the same algorithm, SAM2 model, and
included clip boundaries reopens each editor at its saved mask frame. Initial
clicks and their complete undo history are restored. VideoMaMa and ViTMatte also
reuse every generated per-frame mask, correction marker, correction range, and
per-anchor click/undo history, so the expensive initial SAM2 tracking pass is not
repeated. Changing the source file or clip layout creates a separate draft.
Successful publication and the explicit **Discard** actions remove the matching
draft; ordinary processing cancellation preserves it.

Normal processing consults `<runtime>\sam2-performance-policy-v1.json`, keyed by
GPU, driver, PyTorch/CUDA, SAM2 commit, checkpoint, and compile mode. Eager mode
is the safe default. Meta's supported `compile_image_encoder=True` encoder path
or full `vos_optimized=True` path is selected only after the benchmark records at
least 10% cached end-to-end improvement and the predicted initial pass plus two
correction-equivalent passes exceeds break-even by 20%. Completed local sessions
refine that correction-work estimate. The bounded GPU feature cache qualifies at
2% end-to-end improvement because it has no compilation startup cost, but still
requires equivalent masks and at least 4 GiB VRAM headroom. Compiler failure
invalidates the attempted choice and falls back to eager. The UI separately identifies one-time cache
generation, cached compiled loading, and per-worker/CUDA-graph setup. Accepted
compile artefacts remain in `<runtime>\torchinductor-cache`.

Custom Show Settings exposes the minimum source-frame cutoff for MatAnyone2 and
SAM2 Base+, Small, and Tiny. The shipped value is a conservative 16,000 frames
for each model. A compiled path must pass both this user-visible cutoff and its
hardware-specific performance-policy gate. Use **Benchmark and recalculate
cutoffs...** to test the installed models and update the fields; QuickPlayer
never starts these GPU-heavy benchmarks automatically. The run can take many
minutes, shows live output, supports cancellation, and preserves the current
value when a compiled variant is not faster and mask-equivalent.

A historical 1,000-frame, 1024x576 Base+ test with a full midpoint correction
averaged 241.2 seconds eager, 245.2 seconds full `max-autotune`, and 271.9 seconds
cached `max-autotune-no-cudagraphs`, so neither compiled mode qualifies for that
workload. `benchmark_sam2_workflows.py` now repeats eager, encoder-only, and full
VOS modes for all three models on 1080p and 4K sources, measures cold/cached
startup and two corrections, verifies mask equivalence, and writes qualifying
policy entries. Smaller extracted review frames are not a speed control: SAM2
still resizes input internally to 1024x1024.

On the current RTX 4080/PyTorch 2.11 setup, the corrected-output 1,000-frame
1080p benchmark plus two correction passes measured median eager totals of
158.4 seconds (Base+), 151.3 seconds (Small), and 142.0 seconds (Tiny). Cache
capacities 1, 4, 8, 16, 64, and 256 were screened. Only three feature lookups
were reused in the complete workflow; the one-frame cache changed the respective
three-run medians by -0.3%, +1.9%, and +0.5%, so no model qualified at the 2%
threshold. Larger caches added no hits and increased VRAM.

Base+ compiled totals for the same workload were 162.9 seconds (encoder-only
`max-autotune-no-cudagraphs`), 172.1 seconds (encoder-only `max-autotune`),
168.3 seconds (full VOS `max-autotune-no-cudagraphs`), and 179.8 seconds (full
VOS `max-autotune`). All are slower than the 158.4-second eager median and are
therefore disabled by policy. One-time compilation ranged from roughly two to
five minutes in these populated-cache tests; even cached worker initialization
remained materially slower than eager. Small and Tiny do not inherit Base+'s
results. Tiny boundary tests measured 154.5 seconds encoder-only and 147.6
seconds full VOS without CUDA graphs versus 142.0 seconds eager. Since neither
the largest nor smallest model benefits end-to-end, Small safely remains eager
without paying a speculative compilation cost.

Review JPEGs use a persistent, validated LRU cache under
`%LOCALAPPDATA%\IStripperQuickPlayer\sam2-frame-cache`, default 10 GB. Configure
0–100 GB, inspect usage, or clear inactive entries in Custom Show Settings; 0
disables it. Active worker entries cannot be evicted. A bounded decoded-frame LRU
uses the smaller of 512 MiB or 5% of physical RAM. Tracking state remains on the
GPU. Three pinned mask-output slots overlap asynchronous CUDA downloads and
atomic level-1 PNG writes with subsequent inference. Automatic-mask candidates
are retained while refining clicks on one frame. Detailed timings and cache hit
statistics are appended to `processing.log`, while throttled covered-range events
keep the correction timeline responsive without flooding it.

ViTMatte turns the corrected SAM2 masks into trimaps and predicts soft alpha per
frame. S is the smaller/faster model; B uses the larger backbone for higher
quality. Both are slow, and B can take several times as long as S, so S is the
recommended starting point. Both support CUDA FP16 with FP32/CPU fallback and automatic batch
splitting on CUDA out-of-memory. Corrected review masks remain resumable draft data
until successful publication or explicit discard; the generated RGB/alpha clips
are retained in the published show.

**Matting detail** controls RVM and MatAnyone2's internal inference resolution
per show: Very Low (256 px), Low (384 px), Standard (512 px, recommended), High
(768 px), Very High (1024 px), or Full resolution. Higher detail can improve
fine edges but increases processing time and GPU memory use. If dedicated VRAM
fills or Task Manager shows shared-GPU-memory spill, cancel and retry at a lower
detail setting. It never changes the full-resolution dimensions of the
foreground or alpha output. RVM's official HD baseline is
approximately 480 px internally (`downsample_ratio=0.25` at 1920 px wide).

QuickPlayer processes configurable temporal chunks through RVM while retaining
recurrent state. The default is 3 frames, with 1, 2, 3, 4, 6, 8, and 12 available
in the creation form. If a chunk exceeds GPU memory, it is retried automatically
as smaller sequential chunks without restarting the show.
The same batch selector controls ViTMatte inference and VideoMaMa groups.

### MatAnyone2 pipeline performance

MatAnyone2's recurrent inference is necessarily sequential, but QuickPlayer
overlaps the work around it. A three-slot bounded pipeline reads and resizes the
next source frame while the GPU propagates the current matte and an output worker
downloads/upscales alpha, creates RGBA, updates the preview, and writes the
previous frame to FFmpeg. Input and output queues are limited to two entries, so
long clips do not accumulate frames in RAM or VRAM and output order remains
strictly chronological. Middle-frame propagation stores backward alpha in one
preallocated temporary memory-mapped array instead of thousands of PNG files.
The mapping is removed after success, cancellation, or failure.

On the reference Ryzen 7 7800X3D/RTX 4080, a 1,000-frame 1920x1080 clip at
Standard (512 px) detail improved from 11.19 FPS in the former serial path to
16.21 FPS with previews enabled: a 31.0% whole-job improvement. Disabling
previews increased the bounded result only from 16.21 to 16.31 FPS, so the
resizable input/composite preview is not a material processing bottleneck.
Profiling attributed only about 5-6% of wall time to FFmpeg decode/read, below
the threshold at which NVDEC was worth its extra codec, transfer, fallback, and
frame-parity complexity. QuickPlayer therefore keeps the more portable software
decode path.

At 4K output, the initial bounded implementation measured 8.29 FPS with previews
and 9.38 FPS without them. QuickPlayer now composes the capped 960x540 preview
directly from the retained 910x512 inference RGB/alpha at Standard detail instead
of downscaling the already-upscaled 4K buffers again. Three repeated runs improved
preview-on throughput to 8.88 FPS (112.6 seconds for 1,000 frames), a further 6.7%
whole-job gain, while leaving inference and published media unchanged. A 4K
midpoint-mask run measured 7.03 FPS before this preview-only refinement and used
about 253 MB of temporary alpha mapping.

The development worker also tested OpenCV resizing, explicit `model.half()`, and
partial `torch.compile` of MatAnyone2's fixed-shape image-feature modules. OpenCV
output-only resizing improved the whole job by less than 5%; changing inference
input resizing altered alpha beyond the acceptance limit. Explicit half precision
is incompatible with MatAnyone2's intentional FP32 paths. Cached partial compile
was slightly slower end-to-end and still recompiled a graph in each worker, despite
making the recurrent step faster. These variants are therefore not enabled in
normal processing. CUDA autocast FP16 with FP32/CPU fallback and Pillow resizing
remain the quality-preserving defaults. Detailed structured stage records are
written only to `processing.log`; they do not add UI traffic.

- `clips/<clip-id>/foreground.mp4`: H.264/YUV420P with AAC source audio when present. NVENC quality encoding is preferred; libx264 is the fallback.
- `clips/<clip-id>/alpha.mkv`: near-lossless H.264/YUV420P alpha at CQ/CRF 10. This is substantially smaller than the previous FFV1 alpha while retaining clean matte edges.

Progress is newline-delimited JSON from the worker. The processing dialog is
resizable and shows the latest original input frame on the left and the
foreground/alpha result composited over a checkerboard on the right, allowing a
poor matte to be cancelled early. Snapshot files are written atomically and
removed after processing. Cancel terminates the Python/FFmpeg process tree. Work happens below `<library>\.staging`; failed or cancelled jobs never become library cards. `processing.log` contains worker and FFmpeg diagnostics.

After processing, QuickPlayer opens its transparent player for each clip. New
clips begin with a lower alpha threshold of 25; tune the slider before accepting
to remove weak background pixels while retaining soft subject edges. Choose
**Accept** to publish; QuickPlayer closes all preview players and awaits decoder
shutdown before moving any media. Choose **Retry** to process again, **Open Log**
for diagnostics, or **Discard** to remove staging. A cover is generated from the middle of the
source unless a custom image was selected. Generated covers embed the model name
in white script and the uppercase show title in the colour selected on the
creation form. Generated and selected covers use the official-style 2:3 portrait
ratio (`600×900`) so custom cards retain the normal library size and alignment.
User-supplied covers are not given the text overlay.

## Models and metadata

Use **File → Custom Shows → Manage Models** to create or edit reusable profiles. Editing a profile updates every linked custom card on the next immediate custom-library refresh.

Profile measurements are stored in centimetres: height, bust, waist, and hips. QuickPlayer converts them to its legacy display/filter values so metric/imperial display, sorting, and filters continue to work. Birth date is stored without a time. Model age is calculated at the show's release date. If birth date is unknown, enable the show-specific age-at-release override.

Show fields are title, description, tags, release date, optional recording/show date, optional official-style rating from 0–5, hotness, exclusive flag, number of performers, and one or more clip types. The title is displayed through the existing `outfit` field and the profile name through `modelName`. Personal rating and favourite remain QuickPlayer user data, separate from the optional official-style rating.

Right-click a custom card and choose **Edit Custom Show Metadata…**. Official iStripper metadata remains read-only.

## Playback and queues

An unsplit custom card has the stable playback ID `custom:<show-id>`. Split clips
use `custom:<show-id>:<clip-id>`. Completion advances through the remaining
sections and then follows the existing manual/automatic queue or next-card
rules. Each clip participates independently in clip filters and history while
the library still presents one card for the show.

Before custom playback, QuickPlayer pauses iStripper when its bridge is available and hides the iStripper movie window. It remains hidden across consecutive custom shows. QuickPlayer restores/resumes it on manual close, failure, shutdown, or before an official queued show.

The transparent player keeps RGB and alpha decoders synchronized and rejects dimension, frame-rate, duration, timestamp, or end-of-stream mismatches. The GPU shader premultiplies RGB by alpha. Play/pause, timeline seek, restart, ±10%, 0.25–4× speed (audio muted outside 1×), volume, next, lock, alpha-aware hit testing, click-through, moving, mouse-wheel resizing, small/large mode, global hotkeys, panic, queues, taskbar actions, and REST use the existing QuickPlayer controls. Custom playback does not depend on iStripper account entitlement.

## Folder format and portability

```text
<custom-library>/
  performers/<performer-id>.json
  shows/<show-id>/
    show.json
    cover.jpg
    processing.log
    clips/
      <clip-id>/
        foreground.mp4
        alpha.mkv
        initial-mask.png     # MatAnyone2 clips only
        result.json
    source/                 # only for Copy original
```

IDs are lowercase 32-character UUIDs. `show.json` uses schema version 2. Media paths are relative to the show folder and may not escape it. Referenced original source paths may be absolute because they are not playback media. Invalid/duplicate profiles or shows are skipped and recorded in the model reload diagnostic without blocking valid shows. Version 1 shared-media shows must be reprocessed.

Every show has a `clips` array. Each entry contains a UUID `id`, contiguous source
`startMs`/`endMs`, `hotness`, `clipTypes`, and per-clip media metadata when
included. Ranges start at zero and cover the complete source without gaps or
overlaps. Skipped entries have no required media and never become playable clips.

New conversions also include an optional `processing` object in `show.json`.
It records the algorithm, matting-detail resolution, processing batch size,
SAM2 model (when applicable), execution and precision policies, MatAnyone2
recurrent-refinement count, processing time, QuickPlayer version, and each
installed processing tool's exact commit/model revision. It also records each
included clip's initial-mask frame and whether SAM2 mask tracking was used.
This provenance is read-only in **Edit Custom Show Metadata**. Older shows with
no `processing` object remain valid; their original settings cannot be inferred.

To move a library, close QuickPlayer, copy the entire configured custom-library root, then select the new root in **Custom Shows → Settings**. Relative show media remains portable; referenced originals do not need to move unless you want to reprocess them.

Custom media and profiles are intentionally not stored in `.iqpb` QuickPlayer backups. Back up the configured custom-library root separately.

Deleting a custom card moves only `shows/<show-id>` to the Windows Recycle Bin. A referenced original and the shared performer profile are not deleted.

## REST v1

Library, queue, and status objects include `source` (`istripper` or `custom`) and nullable `showId`. Custom status reports `animationPath: "custom:<show-id>"`. Existing play, queue, seek, speed, next, lock, and panic routes accept custom card tags; there are no separate custom-show REST endpoints.

## Troubleshooting

- **Unsupported Python**: rerun setup with Python 3.11–3.14, then reselect its venv executable.
- **CUDA unavailable**: processing automatically uses CPU/FP32 and libx264. This is functional but much slower, especially for MatAnyone2. To restore GPU processing, install a compatible NVIDIA driver and confirm `& <python> -c "import torch; print(torch.cuda.is_available())"` prints `True`.

### Reference benchmark

On the reference Ryzen 7 7800X3D/RTX 4080 machine at 1920×1080, RVM detail
512, and PyTorch 2.11/CUDA 12.8, the QuickPlayer tensor path measured:

| Algorithm | RTX 4080 | CPU only | CPU slowdown |
| --- | ---: | ---: | ---: |
| RVM Fast / MobileNetV3 | 54.6 FPS | 9.10 FPS | 6.0× |
| RVM Quality / ResNet50 | 68.3 FPS | 3.78 FPS | 18.1× |
| MatAnyone2 | 9.99 FPS | 0.314 FPS | 31.8× |

MatAnyone2 warm-up per clip measured 1.50 seconds on CUDA versus 32.88 seconds
on CPU. These numbers include tensor upload and output download but exclude
FFmpeg video decoding and RGB/alpha encoding. Re-run them with
`tools/custom-shows/benchmark_rvm.py` and `benchmark_matanyone2.py`.

The newer full-pipeline MatAnyone2 benchmark includes model loading and warm-up,
source decode, resizing, recurrent inference, alpha download/upscale, previews,
and foreground/alpha encoding. Its median bounded result was 61.69 seconds for
1,000 1080p frames at Standard detail with previews enabled (16.21 FPS). This is
the more representative planning figure for complete QuickPlayer conversions;
the tensor-path number above remains useful when comparing devices in isolation.
- **Checkpoint validation failed**: delete only the named file in `<runtime>\checkpoints` and rerun setup. Do not bypass the hash check.
- **MatAnyone2/SAM unavailable**: rerun processing-tools setup with MatAnyone2 checked. It is one option because SAM is required to create the initial mask.
- **VideoMaMa unavailable**: rerun setup with VideoMaMa checked. It remains hidden until VideoMaMa, SAM2, and all required model files are installed. CUDA is mandatory.
- **ViTMatte unavailable**: rerun setup with ViTMatte checked. S and B remain hidden until their models and SAM2 are installed.
- **RVM commit validation failed**: rerun setup; `<runtime>\RVM_COMMIT` and the detached checkout must match the pinned commit.
- **FFmpeg/libavcodec 62 missing**: repair the QuickPlayer FFmpeg 8 dependency bundle. Run **Validate setup** again.
- **Poor mask edges**: retry with Quality, use a higher-quality source, reduce motion blur, improve subject/background contrast, and avoid heavy source compression.
- **Foreground/alpha mismatch**: reprocess the show. Do not independently transcode either published media file.
- **No audio**: the source may have no supported audio stream. Audio is intentionally muted whenever speed is not exactly 1×.
- **Card skipped on reload**: inspect `%LOCALAPPDATA%\IStripperQuickPlayer\model-reload-diagnostic.txt` and the show's `processing.log`.
- **Referenced source missing**: playback still uses the published foreground/alpha. Restore the source only if you need to reprocess.

## Licensing

Robust Video Matting is GPL-3.0; its repository and weights are fetched directly from the upstream project. MatAnyone2 and ProPainter use the S-Lab License 1.0 and are limited to non-commercial use unless their authors grant permission. VideoMaMa code is CC BY-NC 4.0 and its checkpoint is subject to the Stability AI Community License. ViTMatte is MIT; SAM and SAM2 are Apache 2.0. The custom player uses FFmpeg 8, FFmpeg.AutoGen, NAudio, Direct3D 11, and DirectComposition. QuickPlayer distributions must retain the bundled FFmpeg/GPL library licence and version notices. No proprietary HD3/SSV parser, decryption, or rights code is used by custom shows.
