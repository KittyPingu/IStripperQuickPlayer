# Creating Custom Shows

QuickPlayer can turn an ordinary video containing one or more people into a
transparent show. Custom cards appear alongside official iStripper cards and
work with the same library, search, filters, favourites, ratings, queues,
history, hotkeys, and REST controls.

This is the user guide. For pinned versions, storage schemas, processing
internals, benchmark results, licensing, and developer diagnostics, see the
[technical custom-show reference](docs/custom-shows.md).

## 1. Install the processing tools

Choose **File > Custom Shows > Install / Update Processing Tools...**.

Robust Video Matting (RVM) is always installed. The default optional selection
also installs TransNetV2 clip detection and MatAnyone2/SAM2 interactive masking.
VideoMaMa, ViTMatte, and ProPainter are optional larger or specialised tools.

Setup creates an isolated Python environment under
`%LOCALAPPDATA%\IStripperQuickPlayer\rvm-runtime`. Existing verified model files
are reused when setup is run again.

After setup:

1. Open **File > Custom Shows > Settings...**.
2. Select the custom-show library folder and Python executable if necessary.
3. Click **Validate setup**.

An NVIDIA CUDA GPU is strongly recommended. RVM can fall back to CPU, but the
mask-guided methods are generally too slow there for normal use.

## 2. Create the card and model profile

Choose **File > Custom Shows > Create Show...**, select a source video, enter a
title, and choose or create a model profile.

Model profiles are shared. Changing a model's birth date, height, measurements,
hair, ethnicity, city, or country updates every linked custom card. The show
title, dates, description, tags, rating, hotness, clip types, and age override
belong to the individual show.

You can either copy the source into the show folder or retain a reference to its
current path. A referenced source must not be moved or renamed if you later want
to regenerate its automatic cover. The generated cover includes the model and
show names, and its title colour can be changed when editing metadata.

## 3. Split the source into clips

Choose **Split into clips...** before processing. The editor works on the
original video. You can:

- play, scrub, use slow motion, or step one frame at a time;
- add dividers and drag their gold triangle markers;
- assign hotness and clip types to each segment;
- include or skip individual segments;
- select several rows and edit them together.

Double-click a segment to seek to its start without autoplaying. Double-click
its **End** cell to seek to its final frame.

**Accurate (TransNetV2)** is the default automatic detector when installed;
otherwise use **Fast (FFmpeg)**. You can create a skipped buffer around each
detected transition and adjust the dividers afterwards. Auto-detected segments
shorter than 10 seconds are skipped by default but can be re-enabled.

Skipped segments are neither processed nor published as playable clips. Every
included clip is processed independently so state does not carry across scene
cuts.

## 4. Choose a matting method

| Method | Best use |
| --- | --- |
| **RVM Quality** | General-purpose automatic person matting. |
| **RVM Fast** | Lighter RVM model; useful when model inference is the bottleneck. |
| **MatAnyone2** | Interactive subject selection on a powerful NVIDIA GPU. |
| **VideoMaMa** | Very slow, high-quality experimental processing with high VRAM use. |
| **ViTMatte S** | Editable SAM2 masks with lighter matte refinement. |
| **ViTMatte B** | Higher-cost refinement; often several times slower than S. |

For a capable NVIDIA GPU, MatAnyone2 is a good first choice when you want direct
control over which person or people are retained.

**Matting detail** controls the internal processing resolution, not the exported
video size. Start with **Standard (512 px)**. If VRAM fills or Windows starts
using shared GPU memory heavily, reduce it to 384 px or 256 px and lower the
processing batch/chunk size.

## 5. Create and correct masks

Mask-guided methods ask you to identify the foreground:

- left-click a person or missing foreground;
- right-click background or unwanted foreground;
- use **Undo click** to work backwards through the click history;
- use automatic masking when it provides a useful starting point.

MatAnyone2 lets you play or scrub to a clear frame before creating its initial
mask. A mask made in the middle of a clip is propagated both forward and
backward.

ViTMatte and VideoMaMa use SAM2 masks across the clip. During mask review, scrub
to a frame where tracking has drifted, add correction clicks, inspect the still
preview, then choose **Update masks**. Timeline markers return to corrected
frames and restore their clicks:

- blue: originally generated masks;
- grey: currently regenerating;
- green: regenerated after correction.

Mask work is saved as a draft. If processing is cancelled or fails, starting the
same source again with the same method and clip boundaries restores the saved
masks, correction frames, clicks, and undo histories. Successful publication or
explicit discard removes the matching draft.

SAM2 Base+ is the most robust model, Small is balanced, and Tiny is fastest.
Only installed and verified models appear in the selector.

## 6. Process and review

The processing window shows the latest source frame and composited result, along
with processed frames, processing FPS, elapsed time, and an ETA once the rate is
stable. Model loading and the first few frames may make an early estimate
unreliable.

Cancel if the mask is clearly unsuitable. A cancelled or failed conversion does
not publish a partial card.

After processing, preview every clip and tune its lower alpha threshold. New
clips start at **25**; pixels below it become transparent. Playback settings also
contain a global full-opacity threshold for turning sufficiently opaque pixels
fully solid while retaining soft edges between the two thresholds.

Choose **Accept** when satisfied. QuickPlayer closes its preview players, waits
for the media files to be released, and publishes the show atomically. You do
not need to close previews manually. **Retry**, **Open Log**, and **Discard** are
available when further work is needed.

## 7. Playback, storage, and backup

A custom card may contain several clips. Natural completion and **Next clip**
follow the existing manual or automatic queue rules, and custom and official
cards can be mixed in either direction.

Custom shows are stored under `<custom-library>\shows`, while shared model
profiles are under `<custom-library>\performers`. QuickPlayer `.iqpb` backups do
not contain this library, so back up the configured custom-library folder
separately.

Deleting a custom card moves its show folder to the Recycle Bin. It does not
delete a referenced source video or a shared model profile.

## 8. Performance and maintenance

- Start RVM at 512 px detail and its recommended chunk size.
- Start ViTMatte and VideoMaMa with small batches on 12-16 GB GPUs.
- Reduce matting detail before reducing the exported video resolution.
- Avoid other heavy GPU applications while processing or benchmarking.
- Use the manual benchmark buttons only when you want to recalibrate settings.
  They generate deterministic temporary test videos and do not use personal
  videos or rely on a path in your Downloads or custom library.
- The SAM2 frame cache can be sized or cleared in Custom Show Settings. Setting
  its limit to 0 disables persistent caching.

The benchmark buttons do not change saved settings until you explicitly choose
**Use results** and then save Settings. Compilation may have a substantial
one-time setup cost; subsequent compatible runs reuse the compiler cache.

## Troubleshooting

- Run **Validate setup** after changing Python, CUDA, or optional tools.
- Lower matting detail and batch/chunk size after an out-of-memory failure.
- Keep referenced sources at their recorded paths if covers may be regenerated.
- Use **Open Log** for worker, FFmpeg, checkpoint, or media mismatch details.
- If an algorithm is missing, install it through the processing-tool choices and
  validate the setup again.
- Do not manually remove active staging or mask-draft folders while processing or
  preview windows are open.

See the [technical custom-show reference](docs/custom-shows.md) for exact setup,
formats, benchmark methodology and results, pipeline design, portability,
licences, and deeper diagnostics.
