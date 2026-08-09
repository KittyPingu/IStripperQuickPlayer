# Creating Custom Shows

QuickPlayer can convert an ordinary video containing one or more people into a
transparent show. Custom cards live alongside official iStripper cards and use
the same search, filters, favourites, ratings, manual queues, automatic queues,
history, hotkeys, and REST controls. Official shows still play through iStripper;
custom shows use QuickPlayer's RGB/alpha player.

For exact installation commands, pinned revisions, hashes, file schemas, and
licensing details, see the [technical custom-show reference](docs/custom-shows.md).

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

Batch size defaults to 3. Small values reduce peak memory; large values can
improve throughput but may become dramatically slower after VRAM exhaustion.
MatAnyone2 advances recurrently one frame at a time, so batch size does not apply
to it.

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

Compiled SAM2 VOS is only worthwhile for long clips. Its first use can spend
several minutes compiling and recording shapes; the dialog identifies this as a
one-time cache build. Shorter clips use eager prediction to avoid that startup
cost. Keep the compilation cache if you do not want to pay the cost again.

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

A full 1,000-frame 1080p MatAnyone2 conversion at 512 px detail measured 11.19
FPS in the old serial path and 16.21 FPS in the bounded path with previews on,
a 31.0% whole-job gain. Turning previews off changed the bounded result only to
16.31 FPS. Decode/read contributed about 5-6% of wall time, so GPU decoding is
not enabled: its transfer, codec, fallback, and exact-frame-parity costs would
outweigh the available gain in this pipeline. QuickPlayer instead overlaps
software decode/resize, sequential recurrent inference, and alpha/preview/encode
work in three bounded reusable slots.

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

A controlled 1,000-frame, 1024×576 SAM2 test including a full midpoint
correction averaged 241.2 seconds eager, 245.2 seconds with full `max-autotune`,
and 271.9 seconds with cached `max-autotune-no-cudagraphs`. QuickPlayer therefore
uses eager propagation below 16,000 frames. Full compilation is deferred until
the first longer clip; it took about nine minutes once on the reference machine,
after which the compiler cache was reused. `max-autotune-no-cudagraphs` is not the
default because it was slower in the measured initial-and-correction workload.

Practical recommendations:

- Start with 512 px detail and batch size 3.
- Reduce detail to 384 px or 256 px before reducing output resolution; exported
  dimensions remain unchanged.
- Use batch size 1–3 on 12–16 GB GPUs. Try 4–6 only with clear VRAM headroom.
- Avoid VideoMaMa batches of 8–12 on a 16 GB card. A 12-frame, 1024×576 test
  exhausted 16 GB of dedicated VRAM and spilled about 6.5 GB into shared memory,
  making the first batch extremely slow.
- CPU fallback is functional for RVM but generally impractical for MatAnyone2.
- Do not judge ETA during model loading, compilation, or the first few frames;
  startup overhead is excluded once a stable processing rate is available.

The reproducible RVM and MatAnyone2 scripts are
`tools/custom-shows/benchmark_rvm.py` and `benchmark_matanyone2.py`. The SAM2
workflow benchmark is `tools/custom-shows/benchmark_sam2_workflows.py`.

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
