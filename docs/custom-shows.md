# Custom shows

## Real-time RVM segmentation

The `rvm-onnx` processing method only splits/re-encodes full-colour H.264/AAC
clips and is queueable without an installed model. Its clip media has
`mode: "rvm-onnx"`, omits `alpha`, and carries per-clip RVM model, quality, and
temporal-memory settings. Missing media mode remains the schema-compatible
`paired-alpha` default, and mixed-media shows are valid.

Playback runs Robust Video Matting through DirectML. Its generated mask feeds
the existing Direct3D compositor while full-resolution RGB remains the rendered
image. Load or run failure supplies constant alpha 255 without interrupting
audio, transport, preload, or handoff.

Each real-time clip stores `rvmOnnx.model` (`mobilenetv3` or `resnet50`),
`rvmOnnx.quality` (`fast`, `balanced`, `quality`, or `full-resolution`), and
`rvmOnnx.temporal`. Fast, Balanced, and Quality target a 256, 384, or 512-pixel
short edge. Full-resolution keeps the source dimensions while RVM's internal
downsample ratio still targets 512 pixels. Temporal mode carries recurrent
state between frames and resets on clip start, seek, restart, or a live setting
change.

The preferred playback path asks FFmpeg for a D3D12 hardware-decoded frame. The
native `IStripperRvmGpuBridge64` shader converts and scales its YUV surface into
a planar GPU tensor, ONNX Runtime DirectML binds input, recurrent state, and
outputs on the same D3D12 device, and the alpha and full-resolution display
textures are shared into the D3D11 compositor. Full-frame RGB does not cross
the CPU boundary. Alpha readback is requested only when an alpha-aware
mouse hit map is required. Hardware decode, adapter, or sharing failure falls
back to software-decoded frames with the managed DirectML session; inference
failure then falls back to opaque RGB for uninterrupted transport and audio.

Custom shows are normal QuickPlayer library cards backed either by synchronized
foreground/alpha videos or by an RGB-only RVM real-time clip. They can be
searched, sorted, filtered, rated, favourited, dragged, saved in manual queues,
selected by automatic queues, and controlled through REST alongside iStripper
cards. The **Real-time** card filter independently includes or hides any custom
show containing an RVM real-time clip. iStripper still plays official shows;
QuickPlayer's transparent player is used only for IDs beginning `custom:`.

### Retired NVIDIA renderer migration

The NVIDIA Video Effects SDK renderer is retired. Manifest and queue loading
migrates its former `nvidia-aigs` identifiers in memory before strict
deserialization:

- `processing.algorithm` becomes `rvm-onnx` and `precisionPolicy` becomes
  `onnx-directml-fp32`.
- `media.mode` becomes `rvm-onnx`.
- Missing per-clip settings are created as MobileNetV3, `balanced`, with
  temporal memory enabled.
- The retired `nvidia` settings object is removed.

The RGB-only foreground media is already suitable for RVM playback, so loading
or reprocessing a migrated show does not require a conversion encode unless its
source range changed or its media is missing. The retired SDK root setting is
ignored and removed from loaded configuration. No proprietary NVIDIA SDK DLL,
feature package, model, NGC login, or API key participates in playback or setup.
References to NVIDIA elsewhere in this document concern CUDA acceleration for
optional offline Python tools, not NVIDIA AI Green Screen.

The collapsed Metadata section can attach a local **Photo folder** containing
JPG, JPEG, PNG, BMP, or GIF images. QuickPlayer scans the folder and its
subfolders for the card's Photos viewer, and makes landscape images available
to the normal wallpaper feature. This stores a reference to the folder rather
than copying the images into the custom-show library.

Accepting a queue job that uses a newly created custom model writes that model
profile to the shared performer library immediately. Other show editors can
therefore select the same model while the first job is still pending or
processing.

For a concise, task-oriented guide with recommended choices, begin with
[Creating Custom Shows](../CUSTOM_SHOWS.md). This document is the exact setup,
format, performance, licensing, and troubleshooting reference.

For most new shows, start with **RVM-MatAnyone** for automatic subject selection,
**MatAnyone 2** when you want to choose and correct the initial subject mask, or
**RVM-ViTMatte S** for automatic full-frame masks followed by softer-edge
refinement. Use **RVM Quality** when you want a very fast automatic result and
its simpler matte quality is sufficient. Start RVM and MatAnyone methods at
**Standard (512 px)** detail.
RVM-ViTMatte S has a separate **ViTMatte inference detail** setting that defaults
to 1024 px. Published media retains the source dimensions, and the unrelated
RVM/MatAnyone Matting detail selector is hidden. See the end-user guide before
using the more specialised ViTMatte B workflow.
**SAM2Matting** is the scene-aware alternative: SAM2.1 Tiny/Base+ use an initial
mask for each detected scene.

One measured queued 1920x1080 source was 38 minutes long and divided into 18
clips. On the reference machine it took approximately **7 minutes with RVM
Quality**, **42 minutes with RVM-MatAnyone**, and **2 hours 30 minutes with
RVM-ViTMatte S**.

Only process videos you have permission to use. The RVM, MatAnyone, ViTMatte,
and SAM2.1 workflows matte one or more visible people and link each show to one
primary reusable model profile. SAM2 correction clicks refine prompted masks.

## Requirements

- Windows x64 and QuickPlayer's FFmpeg 8 dependency bundle.
- Python 3.11–3.14. Setup prefers the newest compatible installed version.
- An NVIDIA CUDA GPU supported by the CUDA 12.8 PyTorch wheels is strongly recommended. CPU/FP32 fallback is supported; an RTX 4080 is the reference configuration.
- Git on `PATH` for the one-time setup.
- Enough free space for the source, staging outputs, and H.264 foreground media
  plus alpha media for non-real-time methods.

QuickPlayer pins:

- [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) commit `53d74c6826735f01f4406b5ca9075eee27bec094`.
- PyTorch `2.11.0`, torchvision `0.26.0`, CUDA `12.8` wheels, and NumPy `2.3.2`.
- Triton for Windows `3.6.0.post26` for compiled SAM2 video propagation.
- RVM MobileNetV3 SHA-256 `3C7C1D92033F7C38D6577C481D13A195D7D80A159B960F4F3119AC7B534CF4F8`.
- RVM ResNet50 SHA-256 `C191A807251164C073DCE5FA408E7A816070D539B882B2A3150330A9FEC112CE`.
- Optional [TransNetV2](https://github.com/soCzech/TransNetV2) PyTorch source commit `85cef72af9a916bdfd7cc94a670c9cdfbf12d1ed` and converted-weight SHA-256 `46520D66D4BF60414A4D82E0E94A92442FF950E34517A3718B2E54815E642B53`.
- Optional [OmniShotCut](https://github.com/UVA-Computer-Vision-Lab/OmniShotCut) commit `23ad6fb41b296fb9258b0e7825125a914573b906`, official checkpoint revision `7f646c4ff4bb843e18c013481fb5d9ed2b068c6b`, and checkpoint SHA-256 `5948EA78E00626C0E6C5E742E64873EF872CF4A5071D2A0841AED51C3E686CFA`.
- Optional [MatAnyone2](https://github.com/pq-yang/MatAnyone2) commit `0079197acd6d16a741f71558809c06c586c579e0` and checkpoint SHA-256 `5E9821E4087231427376B437C85BB6E072B41E582314F06FD524F75BC4AF5914`.
- Every mask-guided algorithm uses [SAM2](https://github.com/facebookresearch/sam2) commit `2b90b9f5ceec907a1c18123530e92e794ad901a4`; setup verifies the Hiera Base+, Small, and Tiny checkpoints.
- Optional [ViTMatte](https://github.com/hustvl/ViTMatte) S revision `6a58ad7646403c1df626fbd746900aec7361ea1d` and B revision `bf486d01a7d9e3dbcc8400f7942835caf0eaf76e`; setup SHA-256 verifies both official Hugging Face model repositories.
- Optional [SAM2Matting](https://github.com/FudanCVL/SAM2Matting) source revision `73dd721d77b56749248aefe5e8824d7f61b9d13c`. Its separate Python 3.10 environment verifies the SAM2.1 Tiny and SAM2.1 Base+ checkpoints by exact size and SHA-256 before use.
- Optional [streaming ProPainter](https://github.com/osmr/propainter) commit `c8983a445720450bf2fd976cab0adb1cad19547d` and PyTorchCV `0.0.74`. Its RAFT, flow-completion, and ProPainter files are verified against upstream SHA-1 values `d74fed4b7511a95ca9713b69ff19a132210a1016`, `a865ddc0281f27ff0814939a8c0101e936678312`, and `5f3cc1e7083fb5c49a7e5c574a06d41c763550a5`.

## Exact setup

The simplest method is **File → Custom Shows → Install / Update Processing
Tools…**. TransNetV2 and MatAnyone 2 + SAM2 are selected by default; ViTMatte
and ProPainter are initially cleared. Select the tools to install or
update in the choices dialog, then leave
the setup window open until it finishes. Selecting any mask-guided method
installs SAM2 for interactive source-frame selection. ViTMatte uses SAM2 for the
editable tracked-mask workflow. QuickPlayer selects the isolated Python automatically. If no
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

Add `-InstallOmniShotCut` to install and verify the optional CUDA-only modern
transition detector. Setup checks the pinned source revision, official
checkpoint hash, CUDA model loading, and a synthetic 100-frame inference:

```powershell
.\custom-shows\setup.ps1 -InstallOmniShotCut
```

Add `-InstallMatAnyone2` to install and verify MatAnyone2 together with SAM2;
switches can be combined:

```powershell
.\custom-shows\setup.ps1 -InstallTransNetV2 -InstallMatAnyone2
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

QuickPlayer only lists TransNetV2, OmniShotCut, MatAnyone2, ViTMatte, and
ProPainter in operational selectors when their installation markers, source
folders, workers, and required model files are present. The setup choices remain
visible so missing tools can be installed.

### SAM2Matting setup and processing contract

Select **SAM2Matting** in the Create/Reprocess form and use **Install / repair...**
beside **Backbone tracker**. This launches the dedicated setup script. From a
published or build output folder it can also be run directly:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\custom-shows\setup-sam2matting.ps1
```

SAM2Matting intentionally uses a separate Python 3.10 environment at
`%LOCALAPPDATA%\IStripperQuickPlayer\sam2matting-worker\v1`. The normal
`rvm-runtime` Python is not interchangeable. Setup pins the source and checkpoint
revisions, installs the locked dependencies, downloads both checkpoints, and
validates the environment. Use `-Repair` to rebuild an incompatible runtime.

The available backbone/prompt combinations are:

| Selector | Tracker | Prompt mode | Intended use |
| --- | --- | --- | --- |
| **SAM2.1-T** | `sam2.1-tiny` | `initial-mask` | Fastest interactive mask for every scene. |
| **SAM2.1-B+** | `sam2.1-base-plus` | `initial-mask` | Higher-capacity interactive mask for every scene. |
| **RVM → SAM2.1-T** | `sam2.1-tiny` | `rvm-initial-mask` | Automatic person mask created for every scene when processing starts. |
| **RVM → SAM2.1-B+** | `sam2.1-base-plus` | `rvm-initial-mask` | Higher-capacity tracking from automatic RVM person masks. |

QuickPlayer builds a deterministic scene plan inside the confirmed included
clips using the configured FFmpeg, TransNetV2, or OmniShotCut detector. Interactive
SAM2.1 modes require exactly one saved initial mask per scene. The RVM modes defer
those masks until the job runs.

SAM2Matting is always queue-capable. Interactive SAM2.1 jobs open the click/paint
mask editor for every scene before the job is committed; RVM-initialized SAM2.1
can be committed immediately. The queue validates the exact option,
scene-plan, source, checkpoint, and prompt contract before processing. Immediate
**Process** runs show current-source and foreground-on-green previews in the
progress form. Background queue jobs deliberately do not generate those JPEG
previews.

The worker uses eager BF16 and CUDA, processes each scene independently, and
streams linear 8-bit alpha into H.264 `yuv420p`/`yuvj420p` media. SAM2.1 tracks
forward from its prompt and then backward when needed. Published foreground and alpha outputs must
have identical decoded frame counts, dimensions, and timing.

VideoMaMa is not installed, offered, or accepted for new processing or queue
jobs. Its identifier remains valid only when loading legacy published manifests,
so shows created by older QuickPlayer versions remain playable.

## Automatic processing queue

Select **Automatically accept result with alpha threshold 25** to enable **Queue**
for RVM Quality/Fast, RVM-MatAnyone, and RVM-ViTMatte S. SAM2Matting is queueable
regardless of this checkbox because its prompt requirements are resolved before
or during the job. MatAnyone 2 instead shows
**Mask and Queue** and collects its initial mask for each included clip before the
job is added. Open **Custom Shows → Queues...** to reorder, edit, delete, retry,
run, pause, stop, and monitor jobs. The queue runs one show at a time and does not
open the normal processing or result-review windows.

RVM ONNX is always queueable without automatic acceptance or an installed ONNX
model. Its queue job only trims/re-encodes RGB media; playback performs the
matting later. Recursive **Folder Import for Real-time...** uses this path and
adds one full-duration clip per included video without starting the queue.

### Recursive real-time folder import

The importer scans `.mp4`, `.mov`, `.mkv`, `.avi`, and `.webm` files below the
selected root in deterministic path order. It does not traverse reparse points,
continues past inaccessible directories, and probes resolution and duration in
the background. Invalid files remain visible but disabled. A canonical case-
insensitive source-path match against a published reference source or any queue
entry is also visible but disabled as a duplicate.

The review grid is sortable by its data columns. Individual include, model, and
show-name cells are editable without losing the multi-row selection. The batch
toolbar includes selection, include/exclude, and model-assignment actions, while
**Apply model to all included** intentionally ignores selection. A normalized
root-folder/model-name match supplies the initial model when possible.

Default titles remove extensions, leading dates, uploader/source boilerplate,
community and post identifiers, resolution/codec tokens, and trailing sequence
markers before splitting camel case and punctuation. Empty or identifier-only
names fall back to a dated parent folder and then **Imported Video**; generated
duplicates receive numeric suffixes. Dates prefer a leading `YYYY-MM-DD`, then
the nearest `YYYY-MM` parent, then the source modification time.

Adding the batch validates that every included row has a title and model, then
inserts all jobs through the atomic queue batch operation. Each job is an RVM
ONNX show with one `0..duration` RGB-only clip and the selected shared RVM,
hotness, and clip-type settings. The importer never copies the source, so it
must remain unchanged at its recorded path until that job completes.

### URL import with yt-dlp

**Create Real-time Show from URL...** accepts only normalized `http` and
`https` URLs without embedded credentials. It invokes the official standalone
Windows `yt-dlp.exe` through `ProcessStartInfo.ArgumentList`; the URL is never
concatenated into a shell command. Downloads use `--no-playlist`, write an info
JSON sidecar, ask the bundled FFmpeg to merge the best available video and
audio, and accept either an optional `--cookies-from-browser` value for Chrome,
Edge, Firefox, or Brave or `--cookies` with a transient copy of a user-selected
Netscape cookie file. The transient file is outside the job/download directory,
is deleted after the attempt, and is never persisted in queue or show data.
Known Chromium cookie-database lock failures are replaced with an actionable
close-browser-or-use-cookie-file message. DPAPI decryption failures are mapped
to public/no-cookie, Firefox, and Netscape-cookie-file alternatives; QuickPlayer
does not attempt to bypass Chromium's cookie protection.

When the first yt-dlp attempt fails specifically with `No video formats found`,
the importer retries the same URL and authentication once with
`--force-generic-extractor`. Other failures are not retried. This delegates HTML
media discovery to yt-dlp rather than adding site-specific extraction code; the
generic result may offer a lower-quality media URL than the site's dedicated
extractor normally would.

Before that generic retry, an XVideos URL gets one domain-scoped adaptive-stream
attempt. QuickPlayer reads the page's encoded video ID, HLS CDN ID, and
`X-View-Data` value, requests the site's `html5player/getvideo` endpoint with
the original page as referrer, and accepts only an HTTPS HLS URL below an
`xvideos-cdn.com` host. The signed manifest is passed back to yt-dlp with the
original referrer, so its ordinary format selector chooses the highest available
variant. Failure at any stage falls through to the generic page retry. The
short-lived CDN URL is not stored as show provenance; the original page URL is.

The managed executable is stored at
`%LOCALAPPDATA%\IStripperQuickPlayer\yt-dlp\yt-dlp.exe`. The managed Deno
runtime is stored beside it as `deno.exe`, and each download explicitly passes
`--js-runtimes deno:<managed-path>`. The official standalone yt-dlp executable
already contains its EJS challenge scripts. Setup downloads
`yt-dlp.exe` and `SHA2-256SUMS` from the latest official GitHub release, matches
the executable's SHA-256 entry, runs `--version`, and atomically replaces the
working executable only after validation. It also downloads Deno's official
x64 Windows archive and its per-asset SHA-256 file, verifies and extracts the
archive, requires Deno 2.3.0 or newer, runs `deno --version`, and replaces the
working runtime only after validation. The URL importer can run this setup
directly; **Install / Update Processing Tools...** also offers it independently
of the Python processing environment and RVM ONNX model setup. The standalone
executable's bundled dependency licensing is described in the
[yt-dlp documentation](https://github.com/yt-dlp/yt-dlp#license).

Each attempt downloads below `<library>/.url-downloads/<uuid>` and probes the
result with QuickPlayer's FFmpeg decoder before opening the editor. Stale
temporary attempts older than one day are removed. A successful editor is
forced to `rvm-onnx`; its title defaults to yt-dlp's page metadata, while all
normal model-profile, clip, metadata, and RVM controls remain available.

URL jobs set `RetainSourceInShow`, save the canonical page URL as provenance,
and synchronously copy the download to `queue/jobs/<job-id>/source.<ext>` when
the job is committed. Only then may the importer delete its temporary folder.
The queue fingerprints and validates that owned source exactly like an external
source. Before publication it copies the file to `shows/<show-id>/source.<ext>`
and writes `source.mode: "copy"`; deleting the show therefore deletes the
retained source too. URL import is limited to new shows so an appended show's
source ownership cannot become ambiguous.

RVM-MatAnyone requires the MatAnyone 2 installation. RVM-ViTMatte S requires the
ViTMatte installation. All selected tools must validate before a job is queued,
and the referenced source must remain at the same path until processing finishes.
The manually corrected ViTMatte workflow is interactive and is not currently
eligible for the automatic queue.

Queue state and job-owned covers, masks, and retained URL downloads are stored
under `<custom-library>\queue`. Normal source videos remain referenced; URL
imports are copied into their job before the temporary download is removed.
Both are checked for size/timestamp changes before processing. Append and
reprocess jobs also verify that their target `show.json`
has not changed. Conflicts are marked **Needs attention** rather than overwriting
newer data. Completed entries remain as history until deleted; deleting history
never deletes the published show. **Remove Completed** clears all successful
history entries together after confirmation, without deleting published shows.

Closing the Queue window leaves processing active. Pause can either finish the
current show before pausing or cancel it back to Pending for a full restart. Stop
also returns the active job to Pending. Closing QuickPlayer while a job runs asks
for confirmation and, if confirmed, safely cancels it back to Pending.

For append and reprocess jobs, publication first advances playback away from the
target show, or stops playback if no other valid card is available, and waits for
the previous custom decoder to close. The atomic directory replacement retries
transient Windows sharing/permission errors for ten seconds. If publication still
fails, the fully processed staging folder remains owned by the queue; **Retry**
revalidates the source and target manifest and retries only publication rather
than running matting again. The queue displays **Waiting to publish** during this
phase instead of presenting processing as complete prematurely.

In QuickPlayer, open **File → Custom Shows → Settings**:

1. Select the custom library folder. The default is `%LOCALAPPDATA%\IStripperQuickPlayer\custom-shows`.
2. Select `<runtime>\venv\Scripts\python.exe`.
3. Click **Validate setup**. Validation checks Python 3.11–3.14, exact PyTorch/torchvision and CUDA-wheel versions, the RVM commit marker, checkpoint hash, FFmpeg, ffprobe, library write access, and a one-frame inference on the available device.

If QuickPlayer reports that an FFmpeg library is missing, repair/reinstall QuickPlayer so its FFmpeg 8 files (`ffmpeg.exe`, `ffprobe.exe`, `avcodec-62.dll`, `avformat-62.dll`, `avutil-60.dll`, `swresample-6.dll`, and `swscale-9.dll`) are beside `IStripperQuickPlayer.exe`. Do not substitute an incompatible FFmpeg major version.

## Stabilizing a source video

Open **File → Custom Shows → Stabilize Video (FFmpeg)** to run the bundled
two-pass VidStab workflow on an MP4, MOV, AVI, MKV, or WebM source. The first pass
measures camera motion; the second smooths the path and writes a new MP4. Light,
standard, and strong smoothing windows are available, along with static/adaptive
auto-zoom or uncropped output. Static auto-zoom and standard strength are the
defaults. The global output NVENC preset is used when hardware encoding is
available, with automatic `libx264` fallback.

Both phases report frame progress, elapsed time, throughput, and an estimated
remaining time. Processing writes to a staging MP4 and only replaces the chosen
destination after both passes and output validation succeed. Cancellation removes
the partial staging output, retains the source and any previous completed output,
and writes `<output>.stabilization.log` diagnostics. This standalone operation is
the initial speed/quality test; it is not yet part of custom-show creation.

The processing window identifies the workflow as two steps and labels each active
step. During Step 1, the original preview advances and overlays VidStab's accepted
tracking fields and motion vectors; the output pane explains that no stabilized
frame exists yet. During Step 2, synchronized original and stabilized preview
frames update approximately every two seconds of video. FPS is calculated
independently for each step.

After processing, QuickPlayer opens a synchronized side-by-side reviewer for the
original and stabilized videos. Both panes are produced by one playback graph so
they cannot drift independently. Use the shared QuickPlayer time bar, play/pause,
five-second seek buttons, or frame-step buttons to inspect the result. The reviewer
is muted and can be reopened with **Review input vs output** in the stabilization
window while both files remain available.

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

ProPainter is substantially more memory-intensive than RVM. The dialog therefore
defaults to **25% processing resolution**, a **40-frame temporal window**, and
**FP16**. This conservative default is particularly important for 4K sources:
50% processes four times as many pixels as 25% and can exhaust dedicated VRAM or
spill into much slower shared GPU memory. The output dimensions remain those of
the source; processing at a lower resolution trades inpaint detail for speed and
lower VRAM. Increase the resolution only when there is measured headroom, or
reduce the window to 20–30 frames if CUDA still runs out of memory. Frames are
decoded into a bounded rolling buffer, processed on CUDA, and
encoded immediately (NVENC when available), so long videos no longer create a
full lossless-PNG copy or need to fit in RAM. The dialog reports completed/total
frames and derives its remaining-time estimate from that progress. CPU/FP32
fallback is functional but normally too slow for full videos. Cancellation
terminates the worker tree, deletes its partial MP4, and leaves both the source and
any previous completed output intact. A `<output>.processing.log` file records
worker diagnostics.

QuickPlayer automatically caps ProPainter's temporal window for the selected
resolution and available VRAM. At 4K, the 25% default has the same working pixel
count as 1080p at 50%, normally allowing the requested 40-frame window on a 16 GB
GPU. This prevents Windows shared-GPU-memory spill from turning a high-quality run
into a system-wide memory stall.

## Creating a show

Choose **File → Custom Shows → Create Show**. Source video, show title, and model
profile are required. The form is divided into **Show**, collapsed **Metadata
(optional)**, **Video sections**, and **Processing** groups. Expand Metadata to set
the description, tags, dates, age override, gender, hotness, performer count,
clip types, and cover options.
QuickPlayer records the source video as an absolute reference and never deletes it.
The Processing group shows only the controls used by the selected algorithm.

Choose **Add To Existing Show** to load an existing custom show's metadata. The
included clips produced from the new source are appended to that show rather than
creating a second card.

Before processing, choose **Split into clips...** to open the clip editor on the
original source. Scrub or play the preview, place dividers, and drag only their
gold triangle handles below the timeline; scrubbing across divider lines no
longer moves them. Select each resulting segment to set its hotness and clip types.
Choose **Fast (FFmpeg)** for lightweight built-in scene-score detection,
**Accurate (TransNetV2)** for neural cut detection, or optional CUDA-only
**Modern/Quality (OmniShotCut)** for typed hard cuts, sudden jumps, fades,
dissolves, wipes, pushes, slides, zooms, and doorway transitions. The last
choice is persisted; an unavailable choice falls back in order to TransNetV2,
OmniShotCut, then FFmpeg. Click **Auto-detect clips** to replace current
dividers. OmniShotCut discards `Padding`, keeps `General`, skips complete gradual
transition ranges, and splits at the start of `Hard_Cut` and `Sudden_Jump`
ranges. Enable **Skip transition ±** to expand gradual ranges and add symmetric
buffers around every detected instantaneous cut; overlapping skipped ranges are
merged. For example, a 1.5-second value creates a 3-second skipped segment
centred on a hard cut.
All three detectors expose a detector-specific **Sensitivity** slider above the
grid beside **Show skipped clips in grid**. For Fast FFmpeg and TransNetV2,
higher values lower the acceptance threshold and produce more candidate cuts;
lower values require a stronger change. Fast defaults to 65% (the former fixed
scene threshold of 0.35), while TransNetV2 defaults to 50% (probability 0.5).
OmniShotCut defaults to 100%, preserving its previous accept-all result. Because
OmniShotCut confidence values cluster very close to 1.0, its slider ranks the
retained candidates by confidence instead: 1% keeps roughly the strongest 1%,
50% keeps the strongest half, and 100% keeps all candidates. Removing a boundary
merges its range into neighbouring content. All three values are remembered
separately and recorded in new detection metadata.
New detections retain compressed raw scores or scored boundaries in the
clip-detection metadata. Moving the sensitivity slider rebuilds the grid from
that data after a short debounce, without decoding the source or rerunning a
neural model. If dividers or included/skipped states were edited after the last
calculation, live recalculation pauses and QuickPlayer asks on slider release
before discarding those changes. Accepting the warning enables live updates for
continued movement. Older detections without retained data require one new
**Auto-detect clips** run before local sensitivity adjustment is available.
The **Auto-skip shorter than** value controls which detected playable segments
are skipped and defaults to 20 seconds. **Show skipped clips in grid** hides or
reveals excluded segments. Clicking or scrubbing the timeline selects the related
grid row and scrolls it into view. Detection labels remain visible on each segment and survive manual
inclusion and metadata edits.
**Import clip setup...** accepts an existing custom show's `show.json`. It copies
the source ranges, included/skipped states, hotness, clip types, and detection
labels into the editor, gives every imported segment a new ID, removes generated
media references, and adjusts the final range to the current source duration.
The saved and current durations must match within one frame, and the imported
ranges must remain contiguous and valid.
TransNet inference runs on CUDA when available and FFmpeg
uses CUDA decode/scale where the source codec supports it, with automatic CPU
decode fallback. Clear **Include this segment as a playable
clip** to mark a segment as skipped; it remains editable in the manifest but is
not exposed to queues or playback. The values on the
main creation form remain overall card metadata. Every included segment is
processed independently into its own foreground/alpha pair. This resets RVM,
MatAnyone2 recurrent state, and SAM2 tracking at every clip boundary; skipped
segments are not processed.

OmniShotCut decodes directly through bundled FFmpeg at the checkpoint's
128×96 input size. Its bounded worker decodes into three reusable pinned frame
and normalized-input slots, carries the 20-frame overlap without rebuilding a
window from individual allocations, and uploads from pinned memory
asynchronously. Decode/preprocessing for the next window runs while CUDA is
processing the current window. The upstream 100-frame window, validation
normalization, valid-region split, and two-frame boundary deduplication remain
unchanged; label/range tensors are transferred from CUDA together.

The automatic decoder is deliberately codec and resolution aware rather than
assuming that GPU decode is always quicker. Deterministic 1080p and 4K tests on
the development RTX 4080 produced identical typed ranges for the compatible
CPU and CUDA paths. CPU decode was faster for H.264 and VP9 and effectively tied
for HEVC. CUDA decode/scale materially helped 4K AV1 (about 3.31 seconds versus
4.77 seconds for the complete 300-frame job), so auto uses it for 4K AV1 and
uses CPU decode otherwise. A failed CUDA path retries on CPU. The bounded H.264
pipeline improved the warm 600-frame test from 3.06 to 2.70 seconds while
preserving identical output. cuDNN autotuning is intentionally disabled: it
added about 4.6 seconds to the first fixed-shape inference without a useful
whole-job gain.

Batching four independent windows reduced model-only time by about 30% in a
6,000-frame test, but decode became the bottleneck and complete-job time stayed
effectively unchanged (4.39 versus 4.38 seconds), while peak model VRAM rose
from about 0.41 GB to 1.16 GB. Production therefore retains one-window
inference; hidden serial/bounded, decoder, and window-batch switches remain for
repeatable development profiling. OmniShotCut runs eager FP32 in this version.
Frame indices are converted with the probed rational frame rate;
variable-frame-rate sources use the average rate and log a warning. Progress
and final typed ranges use newline-delimited JSON. Detailed model-load,
decode/read, overlap-copy, preprocessing, queue-wait, transfer, inference,
result-transfer, RAM, pinned-memory, and VRAM timings are appended to
`<custom-library>\.logs\omnishotcut.ndjson`.

TransNetV2 detection uses a bounded streaming pipeline rather than decoding the
complete source into RAM. It preserves the original 100-frame window, 50-frame
stride, 25-frame padding, raw-logit threshold, peak selection, and divider-frame
semantics while overlapping FFmpeg decode, pinned-memory upload, and batched
inference. The shipped CUDA batch is 8. On the RTX 4080 development machine, the
fixed-window FP16 sweep measured 98.6 windows/s at batch 1, 348.4 at batch 4,
357.6 at batch 8, and 357.3 at batch 16; batch 8 was selected because it matched
peak throughput with less VRAM. A 300-second decode test measured 6.09 seconds for
the compatible CPU scaler, 4.57 seconds for fast-bilinear CPU scaling, and 8.56
seconds for the existing NVDEC path, but fast-bilinear shifted four dividers by
one frame in the 600-second compatibility fixture. It therefore remains
benchmark-only. Auto is the default decode setting. Its policy is
resolution-aware: the manual benchmark tests standard and 4K fixtures
separately, uses compatible CPU divider frames as the reference, and selects
the fastest CPU or NVDEC path whose divider list matches exactly in each bucket.

Codec-specific 1080p tests found CUDA-first decode faster for HEVC/H.265
(1.60 versus 3.03 seconds), AV1 (2.30 versus 11.10 seconds), and VP9 (1.85 versus
2.24 seconds). CPU decode was substantially faster for the real H.264 test
(42.41 versus 63.60 seconds), with identical divider frames on two real sources.
Auto consequently uses compatible CPU decode for H.264 and CUDA-first decode for
HEVC, AV1, and VP9. CUDA failure still restarts on compatible CPU decode.

Custom Show Settings exposes the preferred TransNetV2 window batch, compilation
cutoff, and decode policy. **Benchmark TransNetV2...** is an explicit, cancellable
GPU-heavy test; QuickPlayer never runs it during setup or normal detection. It
benchmarks eager, `max-autotune-no-cudagraphs`, and CUDA-Graph `max-autotune`,
records the one-time compilation cost and cached throughput, checks exact divider
frames, and writes `<runtime>\transnetv2-performance-policy-v1.json` atomically.
A compiled mode is used only after at least 5% projected whole-pipeline gain and
after its measured break-even frame count. A compiler failure falls back to eager.
Manual benchmark results can override the codec-specific choice only after the
fixture returns identical divider frames. Detailed normal runs append to
`<custom-library>\.logs\transnetv2.ndjson`. TensorRT remains deferred and is not
installed or used.

The **Quality** preset uses RVM ResNet50 and is the default. **Fast** uses
MobileNetV3. QuickPlayer normalizes display rotation, variable frame rate, square
pixels, and odd dimensions while a bounded pipeline overlaps FFmpeg decoding,
pinned-memory transfers, recurrent RVM inference, output transfers, preview
generation, and FFmpeg encoding. Published frames remain strictly ordered and
each included clip starts with fresh recurrent state.

The **RVM-MatAnyone** preset is fully automatic. For every included clip it runs
RVM ResNet50 over up to eight frames, selects the strongest usable complete-person
matte, thresholds alpha at 0.40, removes tiny disconnected regions, fills small
enclosed holes, and dilates the mask by three pixels. If no usable person is
found, it retries once per second up to 30 seconds into the clip. MatAnyone 2 then
uses the selected frame and cleaned binary mask. Since detected scenes are
processed as separate clips, this
initialization and MatAnyone recurrent state restart after every retained scene
cut. MatAnyone 2 produces the final alpha.
For this preset, the selected **Matting detail** applies to MatAnyone's
frame-by-frame processing, not to the RVM initializer. The initializer is capped
at 512 px on the short side. RVM is unloaded before MatAnyone starts unless mask
refresh is enabled. Thus,
selecting **Very High (1024 px)** can use most of a 16 GB GPU even though the
brief RVM stage remains at 512 px. Lowering Matting detail reduces MatAnyone's
processing time and VRAM use; it does not change the dimensions of the published
foreground or alpha video. **RVM initializer alpha** controls the automatic
binary-mask cutoff from 10% to 90% and defaults to 40%. Lower values retain more
hair, motion-blurred limbs, dark clothing, and faint edges; higher values require
greater RVM confidence but can remove weak background haze.

With **RVM mask refresh** disabled, RVM is released before MatAnyone loads. When
enabled, RVM remains active during forward propagation. A region is considered
missing when RVM places it above the configured initializer-alpha threshold while
MatAnyone remains at or below 0.20. More than 1% of RVM foreground, plus a
resolution-scaled absolute minimum, must remain missing for three frames. After a
15-frame cooldown, QuickPlayer forms a conservative core with a 5×5 erosion,
combines it with the current MatAnyone alpha, and injects the result as an ordinary
MatAnyone memory update. **RVM refresh strength** scales the core from 25% to
100%. If an installed trained prop segmenter is enabled for the job, QuickPlayer
runs it when an RVM refresh triggers, retains its components near the current RVM
foreground, and includes those pixels in the same memory update. Enable **Run the
trained prop model on every frame** to evaluate it continuously instead. Its
missing-foreground persistence and 15-frame cooldown are independent of RVM's,
so either model can trigger a memory update without delaying the other. The
refresh log records which model triggered, component counts, and added-pixel
counts. This can add later people or held objects, but retains MatAnyone, RVM, and
the trained model in VRAM. RVM inference runs on every forward frame; prop
inference does so only when the additional option is selected.

When per-frame prop inference is enabled, the optional **Debug contribution**
setting writes `prop-contribution.mp4` beside each processed clip. Cyan marks
retained prop-model detections that did not cause an injection on that frame;
green marks pixels supplied to the active RVM/MatAnyone mask, and RVM-MatAnyone
draws a green border on model-injection frames. This diagnostic is disabled by
default and is not used by playback.

For MatAnyone 2 and RVM-MatAnyone, **Max memory frames** accepts 2–30 without
long-term memory or 6–14 with it. Compressed long-term memory is enabled by
default with fourteen detailed frames, a 4,000-token limit, and a 500-token
consolidation buffer. These limits bound memory growth but do not guarantee that
every detail/resolution fits a particular GPU. Above Standard detail, lower Max
memory frames first if dedicated VRAM fills or processing spills into shared GPU
memory.

The optional **MatAnyone 2** algorithm opens an initial-mask editor for every
included clip before processing. Play, scrub, use 0.25x slow motion, or step a
frame at a time through the original clip to choose a clear mask frame. After
playback or scrubbing stops, QuickPlayer loads the exact still frame while keeping
SAM2 base-plus resident, then updates the mask interactively: left-click
every person to include and right-click unwanted people or background areas to
exclude. For exact initial-mask edits, enable **Paint mask**, adjust the brush-size
slider or mouse wheel, then left-drag to add foreground or right-drag to erase it.
**Ctrl+P** toggles paint mode; **Ctrl+Z** or **Undo stroke** restores the mask
before the previous stroke. **Auto mask frame** runs SAM2's
automatic image-mask generator; positive
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

During interactive **Process and Preview** for plain MatAnyone 2, **Pause and
correct mask** waits for the current inference frame and opens its exact RGB frame
with the predicted alpha already loaded. The editor can scrub backward from the
pause point to the original mask frame, step frame-by-frame, or play synchronized
RGB/alpha pairs with the green overlay. It cannot inspect frames that have not yet
been processed. Accepting a mask on the paused frame updates memory directly;
accepting one on an earlier frame replays MatAnyone forward from that correction
to the pause point while the processing preview and replay counter advance.

Interactive alpha history is a temporary memory-mapped byte array at MatAnyone's
processing resolution; matching inference RGB frames are retained as temporary
JPEGs for correction review. The worker also serializes mutable MatAnyone state to
temporary Torch checkpoint files every 250 frames. An earlier correction restores
the nearest checkpoint with memory-mapped Torch loading instead of replaying from the beginning,
invalidates later checkpoints, then rebuilds them while replaying. At the anchor,
QuickPlayer clears non-permanent working and compressed long-term entries and adds
the edited mask as permanent memory, so later frames propagate from the
authoritative correction. The history, RGB cache, and checkpoints live under the
clip's staging work directory and are removed with that temporary work.

For **ViTMatte S** and **ViTMatte B**, QuickPlayer opens a resizable review editor
for the initial SAM2 or EdgeTAM propagation before matting.
The **RVM → SAM2** mask-generation option uses RVM to create the initial
person-oriented mask at the start of each clip, then hands that mask to SAM2.
It remains fully editable with clicks, painting, pausing, scrubbing, and
forward/backward correction propagation. The RVM initializer alpha control sets
the threshold used to create the initial binary person mask.
The editor displays sampled tracked overlays while the initial pass runs. Its
timeline always represents the whole clip. Choose **Pause and Correct** to stop
after the mask currently being calculated. Once paused, scrub, play, or step
through any already-generated frame, including frames earlier than the pause
point, and correct the first place where tracking went wrong. Frames beyond the
generated range remain visible on the timeline but cannot be reviewed until their
masks have been generated.

Left-click missing person pixels and right-click background or unwanted pixels.
For exact local edits, enable **Paint mask**, choose a 2–200 px brush size, then
left-drag to add foreground or right-drag to erase it. The pointer becomes a
circular outline scaled to the preview so it shows the actual brush diameter
instead of a crosshair; it is green when adding and red while erasing. The mouse
wheel changes brush size, **Ctrl+P** toggles paint mode, and **Ctrl+Z** or **Undo
stroke** undoes one complete stroke or click. Painting is available both while
creating an initial mask and while correcting a generated mask.

Clicks and paint update only the selected frame's preview. Choose **Update masks**
to accept that frame as a correction anchor and propagate it backward and then
forward. The timeline marks saved anchors and highlights ranges as their masks are
updated. During the backward phase, choose **Stop backward propagation** once the
corrected tracking has reached far enough; masks already updated are retained and
forward propagation begins immediately. The forward phase can be paused again for
another correction. **Continue generation** resumes without adding a correction,
whereas **Use corrected masks** becomes available only after the complete clip has
valid masks. **Auto mask frame** can replace the current frame prompt with
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
clicks and their complete undo history are restored. ViTMatte also reuses every
generated per-frame mask, correction marker, correction range, and
per-anchor click/undo history, so the expensive initial SAM2 tracking pass is not
repeated. Changing the source file or clip layout creates a separate draft.
Successful publication and the explicit **Discard** actions remove the matching
draft; ordinary processing cancellation preserves it.

Normal processing consults `<runtime>\sam2-performance-policy-v1.json`, keyed by
GPU, driver, PyTorch/CUDA, SAM2 commit, checkpoint, and compile mode. Eager mode
is the safe default. Meta's supported `compile_image_encoder=True` encoder path
or full `vos_optimized=True` path is selected only after the benchmark records at
least 10% cached correction-propagation improvement and the predicted initial
pass plus two correction-equivalent passes exceeds the measured fixed-startup
break-even by 20%. Completed local sessions
refine that correction-work estimate. The bounded GPU feature cache qualifies at
2% end-to-end improvement because it has no compilation startup cost, but still
requires equivalent masks and at least 4 GiB VRAM headroom. Compiler failure
invalidates the attempted choice and falls back to eager. The UI separately identifies one-time cache
generation, cached compiled loading, and per-worker/CUDA-graph setup. Accepted
compile artefacts remain in `<runtime>\torchinductor-cache`.

Custom Show Settings exposes the minimum source-frame cutoff for MatAnyone2,
SAM2 Base+/Small/Tiny, and ViTMatte S/B. The shipped value is a conservative
16,000 frames
for each model. A compiled path must pass both this user-visible cutoff and its
hardware-specific performance-policy gate. Use **Benchmark and recalculate
cutoffs...** to test the installed models, calibrate ViTMatte batch sizes, and
update the fields; QuickPlayer
never starts these GPU-heavy benchmarks automatically. The run can take many
minutes, shows live output, supports cancellation, and preserves the current
value when a compiled variant is not faster and mask-equivalent.
The same run writes separate preferred batch sizes for ViTMatte S and B. New
Create Show dialogs apply the selected model's saved recommendation when that
ViTMatte algorithm is chosen; the per-show batch selector can still override it.
The shipped RTX 4080 defaults are based on three-run, 300-frame, 1920x1080
end-to-end eager sweeps: batch 2 for ViTMatte S (7.38 median FPS, 6.51 GiB peak
VRAM) and batch 1 for ViTMatte B (4.34 median FPS, 6.71 GiB peak VRAM). S batch
3 was slightly slower at 7.31 FPS while using 9.71 GiB, and S batch 4 plus B
batch 2 or higher entered severe shared-memory pressure. These are starting
values rather than universal limits; use the manual benchmark for other GPUs.

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
statistics are appended to `processing.log`. Covered-range events remain
throttled, and the editor samples completed mask files at roughly 2.5 previews per
second, keeping the live preview and correction timeline responsive without
adding per-frame UI or disk synchronization.

ViTMatte turns the corrected SAM2 masks into trimaps and predicts soft alpha per
frame. S is the smaller/faster model; B uses the larger backbone for higher
quality. Both are slow, and B can take several times as long as S, so S is the
recommended starting point. Both support CUDA FP16 with FP32/CPU fallback and automatic batch
splitting on CUDA out-of-memory. A three-stage bounded pipeline overlaps
source/mask preparation, GPU inference, asynchronous pinned-memory alpha
download, FFmpeg output, and the capped 960x540 preview. NumPy frames go
directly to the official Hugging Face processor, trimap morphology kernels are
reused, and preview/progress work is throttled. Corrected review masks remain resumable draft data
until successful publication or explicit discard; the generated RGB/alpha clips
are retained in the published show.

**RVM-ViTMatte S/B** is the fully automatic alternative. It first runs RVM
ResNet50 over every frame in each included clip to create a complete binary mask
sequence, then uses that sequence as ViTMatte trimap input. It does not open the
SAM2 correction editor. The **RVM initializer alpha** control sets the RVM
foreground cutoff. With an enabled trained prop model, the first RVM mask is
augmented with nearby predicted prop components. **Run the trained prop model on
every frame** keeps that model warm and applies the same proximity-filtered union
to every RVM mask before ViTMatte. Because ViTMatte refines each frame
independently, this path has no persistence counter, cooldown, or memory refresh.
Its progress reports retained components and added pixels. After successful
processing, QuickPlayer compresses every
generated sequence as `masks/<clip-id>/tracked-masks.iqpmask` inside the published
show and deletes only the temporary PNG copies. Reprocessing with **Keep existing
masks** extracts and reuses those archives when prop augmentation is not active;
an active prop model regenerates the masks so the requested injection is applied.

ViTMatte permanently reduces its batch size after the first CUDA out-of-memory
failure instead of retrying the same oversized batch throughout the clip. The
safe size is remembered per GPU, driver, PyTorch/CUDA version, model, padded
dimensions, precision, and requested batch size. The manual cutoff benchmark
tests batches 1, 2, 3, 4, 6, 8, and 12, measures whole-job throughput and peak
VRAM, and stores the best safe size in `vitmatte-performance-policy-v1.json`.
During the upward sweep, whole-GPU dedicated-memory usage is monitored; reaching
the safety limit terminates that candidate immediately and skips every larger
batch to avoid slow Windows shared-memory spill. It also compares
eager execution with full-model `max-autotune` compilation. Compiled execution
is used only when that machine's cached run is at least 5% faster, binary alpha
IoU is at least 0.9999, and the clip exceeds both the measured break-even and
the user-visible cutoff. A compiler/runtime problem invalidates the entry and
continues with eager inference. One-time artefacts are stored under
`<runtime>\torchinductor-vitmatte`. Structured stage timings, throughput,
selected batch, execution mode, and peak VRAM are written to `processing.log`.
The same manual run tests channels-last tensors and explicit FP16 weights
separately; either is enabled only with at least 5% additional whole-job gain
and the same alpha-equivalence gate. Autocast FP16 remains the default.

**Matting detail** controls RVM and MatAnyone2's internal inference resolution
per show: Very Low (256 px), Low (384 px), Standard (512 px, recommended), High
(768 px), Very High (1024 px), or Full resolution. Higher detail can improve
fine edges but increases processing time and GPU memory use. If dedicated VRAM
fills or Task Manager shows shared-GPU-memory spill, cancel and retry at a lower
detail setting. It never changes the full-resolution dimensions of the
foreground or alpha output. RVM's official HD baseline is
approximately 480 px internally (`downsample_ratio=0.25` at 1920 px wide).
The RVM-MatAnyone preset is the exception to the shared-resolution wording: its
automatic RVM initializer is always capped at 512 px, while the selected detail
controls the final MatAnyone pass.

Enable **Automatically accept result with alpha threshold 25** to skip the final
preview/retry dialog after successful processing. This option is available for
every processing algorithm and assigns the existing 0–255 playback alpha cutoff
of 25 to every included clip before publishing. It is especially useful for
unattended RVM and RVM-MatAnyone jobs. This means an alpha value of 25 (about 10%
opacity), not a requirement that foreground cover 25% of the image. Leave it
unchecked when you want to preview each clip and tune its cutoff before accepting.

QuickPlayer processes configurable temporal chunks through RVM while retaining
recurrent state. Quality and Fast each default to 12 frames, following the
official RVM conversion guidance. Values 1, 2, 3, 4, 6, 8, 12, 16, and 24 are
available. If a chunk exceeds GPU memory, QuickPlayer halves it and remembers the
successful size for every remaining chunk and clip in that job instead of
repeatedly retrying the oversized allocation. The same batch selector controls
ViTMatte inference. **Auto** is the default for genuinely batched workflows: RVM,
ViTMatte, and RVM-ViTMatte. It starts conservatively from the algorithm, effective
inference dimensions, current free VRAM, GPU identity, and VRAM capacity. It increments
after healthy measurements and decrements on a CUDA memory limit. Successful
effective values are stored in the runtime performance policy and reused as the
initial suggestion for matching later runs.

**CPU priority** and **GPU priority** in Custom Show Settings apply to newly
started processing workers and both default to **Normal**. Lower priorities can
leave more resources for playback and desktop interaction, but may reduce
processing throughput. They do not change a worker that is already running.

### RVM pipeline performance

RVM uses the normalized source stream directly for `foreground.mp4`, asks
FFmpeg to provide Python with only the selected matting-detail resolution, and
sends only the single-channel alpha result back for FFmpeg to upscale. This
avoids piping full-resolution RGB input or constructing, downloading, and
piping full-resolution RGBA output. It also means RVM's reconstructed foreground
colour is not retained; the original
source supplies foreground colour, matching the MatAnyone2 and ViTMatte output
path and trading some possible edge-colour cleanup for substantially lower
transfer and CPU overhead.

RVM uses up to three reusable slots with queues limited to two items. The slot
count is reduced automatically so pinned RGB input and alpha output memory stays
below the smaller of 2 GiB or 12.5% of physical RAM. CUDA uploads and downloads
use dedicated streams and events; source previews come from the decoded CPU
buffer, and a latest-frame-only worker creates previews no more than twice per
second at up to 960x540. A final preview is drained before review begins. CPU
and FP32 fallbacks use the same ordered pipeline.

**Custom Shows > Settings** stores separate preferred chunks for Quality and
Fast and a global custom-show NVENC effort preset from `p1` (fastest) through
`p7` (most encoding effort); `p5` is the default. It applies to RVM, MatAnyone2,
and ViTMatte. The existing CQ values are unchanged, and libx264 remains the
automatic fallback when NVENC is unavailable. Use
**Benchmark RVM...** to run the cancellable 1,000-frame 1080p/4K full-pipeline
sweep. Results are copied into the form only after **Use results**, and the
Settings dialog must still be accepted to save them.

RVM compilation is disabled by default (cutoff 0). The manual benchmark tests
cached `max-autotune-no-cudagraphs` at Standard 512 px detail and accepts it only
when both resolutions improve by at least 5%, no tested case regresses by more
than 2%, and alpha remains equivalent. Automatic use also requires the included
frame count to exceed the measured break-even point by 20%. The first recurrent
chunk and final partial chunk stay eager. One-time compiler artefacts live under
`<runtime>\torchinductor-rvm`; a compiler or runtime failure falls back to eager.

Detailed `PROFILE` records in `processing.log` cover model/compile time,
decode/read and queue waits, H2D, inference, D2H, encoder blocking, preview work,
total FPS, RAM, pinned memory, peak VRAM, effective chunk, pipeline depth,
encoder, and NVENC preset. These diagnostics do not add traffic to the processing
dialog.

### MatAnyone2 pipeline performance

MatAnyone2's recurrent inference is necessarily sequential, but QuickPlayer
overlaps the work around it. A three-slot bounded pipeline reads and resizes the
next source frame while the GPU propagates the current matte and an output worker
downloads alpha, updates the preview, and writes the previous low-resolution
grayscale mask to FFmpeg. FFmpeg shares the normalized source decode with
foreground encoding, sends a model-resolution RGB branch to Python, and scales
alpha to the published resolution. Python no longer reads 4K RGB merely to
downscale it or creates and pipes a full-resolution RGBA frame. Input and output queues are limited to two entries, so
long clips do not accumulate frames in RAM or VRAM and output order remains
strictly chronological. Middle-frame propagation stores backward alpha in one
preallocated temporary memory-mapped array instead of thousands of PNG files.
Interactive processing additionally stores forward alpha history in a second
memory-mapped array, matching review RGB frames, and restartable processor
checkpoints every 250 frames. These files support backward scrubbing and
correction replay without retaining the decoded clip in RAM or restarting from
frame zero. The temporary mappings and checkpoints are removed after success,
cancellation, or failure.

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

A focused 100-frame 4K validation of the split FFmpeg path reduced median source
delivery from 43.99 ms to 0.77 ms and the Python resize/copy from 40.49 ms to
0.66 ms. The active section improved from approximately 9.2 to 17.6 FPS including
pipeline ramp and previews; recurrent inference then became the limiting stage
at a 37.07 ms median. Decoded foreground was pixel-identical (SSIM 1.0), while
decoded alpha measured 0.999984 luma SSIM and 65.84 dB luma PSNR against the
former Pillow-input path.

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
creation form. The metadata section optionally selects separate installed fonts
for the model name and show title; existing shows default to Segoe Script and
Segoe UI. Generated and selected covers use the official-style 2:3 portrait
ratio (`600×900`) so custom cards retain the normal library size and alignment.
User-supplied covers are used as the background and receive the same model-name
and show-title text overlay as automatically generated covers.

## Models and metadata

Use **File → Custom Shows → Manage Models** to create or edit reusable profiles. Editing a profile updates every linked custom card on the next immediate custom-library refresh.

Profile measurements are stored in centimetres: height, bust, waist, and hips. QuickPlayer converts them to its legacy display/filter values so metric/imperial display, sorting, and filters continue to work. Birth date is stored without a time. Model age is calculated at the show's release date. If birth date is unknown, enable the show-specific age-at-release override.

Show fields are title, description, tags, release date, optional recording/show
date, optional age-at-release override, gender, hotness, number of performers,
and one or more clip types. The title is displayed through the existing `outfit`
field and the profile name through `modelName`. Personal rating and favourite
remain separate QuickPlayer user data. The Gender filter supports Male, Female,
Transgender, Femboy, Sissy, and Non-Binary. Official iStripper cards are treated
as Female for filtering, except VirtuaGuy cards, which are treated as Male.

Right-click a custom card and choose **Edit Custom Show Metadata…**. Official iStripper metadata remains read-only.
Changing ordinary metadata preserves the existing cover. An automatic cover is
regenerated only when its title, linked model, title colour, or either cover font
changes; selecting
a new custom cover replaces it. Therefore an unrelated metadata edit does not
require the referenced original source to still exist.

### Reprocessing a show

Right-click a custom card and choose **Reprocess Custom Show…** to run the
original source through a different installed algorithm without creating a
second card. **Keep existing clip divisions** is enabled by default. Clear it
to unlock divider editing before processing. **Keep existing masks** reuses the
retained initial and tracked masks, so changing among ViTMatte S, ViTMatte B,
and MatAnyone 2 does not require drawing the same mask again. Clear it to generate
and review new masks.

While **Keep existing clip divisions** remains enabled, **Clips to reprocess**
lists every playable clip and initially checks all of them. Uncheck clips whose
existing foreground, alpha, and retained masks should remain untouched. For
example, leave only clip 4 checked to create new masks and process only clip 4
with the newly selected method. The selector is hidden after the clip layout is
changed because the old and new clips can no longer be matched safely. Partial
reprocessing keeps the show's existing processing provenance; a full reprocess
updates it to the newly selected method and settings.

Interactive MatAnyone retains the initial mask selected for every included
clip. RVM-MatAnyone retains the cleaned binary initializer generated by RVM and
its selected frame. A later RVM-MatAnyone reprocessing pass uses that retained
mask directly and does not rerun RVM initialization unless **Keep existing
masks** is cleared.

QuickPlayer retains masks for newly processed and newly reprocessed shows.
Shows created by older versions still retain their clip divisions, but their
temporary mask drafts were not stored; the first reprocessing pass therefore
asks for masks again and retains them for later passes. Reprocessing validates
the complete staged replacement before swapping it into the library. The old
show is restored if that swap cannot complete.

Mask review is also saved while a new show is still incomplete. Choose
**Custom Shows → Restore Incomplete Setup…** to reopen its metadata, complete
clip layout, initial masks, correction anchors, and generated mask frames. Fully
generated earlier clips are reused immediately. If generation stopped partway
through a clip, QuickPlayer resumes from its last contiguous saved mask; it does
not throw away the completed prefix or restart the earlier clips. The draft is
removed only after the show publishes successfully or the user explicitly
discards the processing result.

Before ViTMatte processing begins, QuickPlayer compares the selected batch with
a conservative recommendation based on the NVIDIA GPU's total and
currently free VRAM. ViTMatte also accounts for the S/B model and source
resolution. If the batch is too large, QuickPlayer shows the detected memory and
offers to select the safer batch; cancelling returns to the form without starting
mask review or processing. A GPU-specific policy lets the worker reuse the
measured safe batch while retaining the conservative generic cap on unbenchmarked hardware.
ViTMatte retains its out-of-memory batch reduction as a final safeguard.

## Playback and queues

An unsplit custom card has the stable playback ID `custom:<show-id>`. Split clips
use `custom:<show-id>:<clip-id>`. Completion advances through the remaining
sections and then follows the existing manual/automatic queue or next-card
rules. Each clip participates independently in clip filters and history while
the library still presents one card for the show.

Before custom playback, QuickPlayer pauses iStripper when its bridge is available and hides the iStripper movie window. It remains hidden across consecutive custom shows. QuickPlayer restores/resumes it on manual close, failure, shutdown, or before an official queued show.

For paired-alpha media, the transparent player keeps RGB and alpha decoders
synchronized and rejects dimension, frame-rate, duration, timestamp, or end-of-
stream mismatches. For RVM media, it generates alpha from each RGB frame and
resets recurrent state after discontinuities. The GPU shader premultiplies RGB
by the resulting alpha. Play/pause, timeline seek, restart, ±10%, 0.25–4× speed
(audio muted outside 1×), volume, next, lock, alpha-aware hit testing, click-
through, moving, mouse-wheel resizing, small/large mode, global hotkeys, panic,
queues, taskbar actions, and REST use the existing QuickPlayer controls. Custom
playback does not depend on iStripper account entitlement.

## Folder format and portability

```text
<custom-library>/
  performers/<performer-id>.json
  shows/<show-id>/
    show.json
    cover.jpg
    source.mp4               # retained URL source; extension may differ
    processing.log
    masks/                  # retained reprocessing inputs, when available
      retained-masks.json
      <clip-id>/
        initial-mask.png
        tracked-masks.iqpmask # 1-bit chunks compressed with Zstandard
    clips/
      <clip-id>/
        foreground.mp4
        alpha.mkv           # paired-alpha methods only; omitted for RVM ONNX
        initial-mask.png     # retained prompt/initializer when applicable
        result.json
```

IDs are lowercase 32-character UUIDs. `show.json` uses schema version 3. Media
paths are relative to the show folder and may not escape it. Referenced original
source paths may be absolute because they are not playback media. URL imports
instead use a relative `source.mode: "copy"` path and may include the normalized
page address in optional `source.url` provenance. Invalid/duplicate profiles or
shows are skipped and recorded in the model reload diagnostic without blocking
valid shows. Older shared-media shows must be reprocessed.

Every show has a `clips` array. Each entry contains a UUID `id`, contiguous source
`startMs`/`endMs`, `hotness`, `clipTypes`, and per-clip media metadata when
included. Ranges start at zero and cover the complete source without gaps or
overlaps. Skipped entries have no required media and never become playable clips.
`media.mode` defaults to `paired-alpha`; `rvm-onnx` permits an omitted alpha and
requires the clip's `rvmOnnx` settings. Media mode is authoritative, so a show
may contain both paired-alpha and RVM clips after partial reprocessing.
Legacy `nvidia-aigs` values are normalized to `rvm-onnx` during JSON loading and
must not be emitted by newly written manifests or queue jobs.
An optional `detectionLabels` array records the typed reason for each generated
range. An optional root `clipDetection` object records the detector, pinned tool
revision, overlap, transition-buffer duration, configured short-clip minimum, and whether a
divider was manually changed. In OmniShotCut output, `intraLabel` classifies the
range itself and `interLabel` classifies its relationship to the preceding
range. Unknown worker or manifest labels are rejected rather than guessed.

New conversions also include an optional `processing` object in `show.json`.
It records the algorithm, matting-detail resolution, processing batch size,
SAM2 model (when applicable), execution and precision policies, MatAnyone2
recurrent-refinement count, processing time, QuickPlayer version, and each
installed processing tool's exact commit/model revision. It also records each
included clip's initial-mask frame and whether SAM2 mask tracking was used.
This provenance is read-only in **Edit Custom Show Metadata**. Older shows with
no `processing` object remain valid; their original settings cannot be inferred.
For RVM ONNX, the provenance algorithm is `rvm-onnx`, execution policy is
`realtime-playback`, and precision policy is `onnx-directml-fp32`; the per-clip
media mode remains authoritative in mixed shows.

Tracked masks are retained losslessly in `IQPMASK1` archives. Each binary mask is
packed to one bit per pixel and each 300-frame chunk is compressed with
Zstandard. Every chunk includes a SHA-256 integrity value. Current archives use
plain packed frames because they compressed better in representative testing;
QuickPlayer remains able to read older XOR-delta archives. Reprocessing expands
the archive into temporary `0`/`255` PNG masks and removes those temporary files
afterward. The decoded masks are pixel-identical to the originals.

To move a library, close QuickPlayer, copy the entire configured custom-library root, then select the new root in **Custom Shows → Settings**. Relative show media remains portable; referenced originals do not need to move unless you want to reprocess them.

Custom media and profiles are intentionally not stored in `.iqpb` QuickPlayer backups. Back up the configured custom-library root separately.

The optional machine diagnostic below verifies D3D12 hardware decoding,
DirectML GPU inference, D3D11 texture sharing, and alpha readback against a real
video. It is not part of the hardware-independent custom-show verifier:

```powershell
.\IStripperQuickPlayer.exe --verify-rvm-gpu 'C:\path\to\video.mp4'
```

The URL diagnostic installs or validates the managed yt-dlp executable. With an
optional URL it also downloads one video, queues and encodes it as RVM RGB
media, publishes it into a temporary library with its retained source, validates
the manifest, and removes the temporary library:

```powershell
.\IStripperQuickPlayer.exe --verify-url-downloader
.\IStripperQuickPlayer.exe --verify-url-downloader 'https://example.com/video-page'
```

Deleting a custom card moves only `shows/<show-id>` to the Windows Recycle Bin. A referenced original and the shared performer profile are not deleted.

## REST v1

Library, queue, and status objects include `source` (`istripper` or `custom`) and nullable `showId`. Custom status reports `animationPath: "custom:<show-id>"`. Existing play, queue, seek, speed, next, lock, and panic routes accept custom card tags; there are no separate custom-show REST endpoints.

## Reference benchmark

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

## Troubleshooting

- **Unsupported Python**: rerun setup with Python 3.11–3.14, then reselect its venv executable.
- **CUDA unavailable**: processing automatically uses CPU/FP32 and libx264. This is functional but much slower, especially for MatAnyone2. To restore GPU processing, install a compatible NVIDIA driver and confirm `& <python> -c "import torch; print(torch.cuda.is_available())"` prints `True`.
- **Checkpoint validation failed**: delete only the named file in `<runtime>\checkpoints` and rerun setup. Do not bypass the hash check.
- **MatAnyone2/SAM unavailable**: rerun processing-tools setup with MatAnyone2 checked. It is one option because SAM is required to create the initial mask.
- **ViTMatte unavailable**: rerun setup with ViTMatte checked. S and B remain hidden until their models and SAM2 are installed.
- **RVM commit validation failed**: rerun setup; `<runtime>\RVM_COMMIT` and the detached checkout must match the pinned commit.
- **FFmpeg/libavcodec 62 missing**: repair the QuickPlayer FFmpeg 8 dependency bundle. Run **Validate setup** again.
- **RVM ONNX model missing**: use **Install / Update Processing Tools** or
  **Setup RVM ONNX...**. Existing RGB-only clips continue as opaque video until
  the selected model validates.
- **RVM GPU diagnostic fails**: hardware decode or Direct3D sharing may be
  unavailable on that adapter/codec. Normal playback first tries the managed
  DirectML fallback, so a failed zero-copy diagnostic does not by itself make
  the show unplayable.
- **Poor mask edges**: retry with Quality, use a higher-quality source, reduce motion blur, improve subject/background contrast, and avoid heavy source compression.
- **Foreground/alpha mismatch**: reprocess the show. Do not independently transcode either published media file.
- **No audio**: the source may have no supported audio stream. Audio is intentionally muted whenever speed is not exactly 1×.
- **Card skipped on reload**: inspect `%LOCALAPPDATA%\IStripperQuickPlayer\model-reload-diagnostic.txt` and the show's `processing.log`.
- **Referenced source missing**: playback still uses the published foreground/alpha. Restore the source only if you need to reprocess.

## Licensing

Robust Video Matting is GPL-3.0; its repository and weights are fetched directly
from the upstream project. OmniShotCut is MIT. MatAnyone2 and ProPainter use the
S-Lab License 1.0 and are limited to non-commercial use unless their authors grant
permission. ViTMatte is MIT; SAM and SAM2 are Apache 2.0. The custom player uses
FFmpeg 8, FFmpeg.AutoGen, NAudio, Direct3D 11, and DirectComposition.
