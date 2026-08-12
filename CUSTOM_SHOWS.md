# Creating Custom Shows

QuickPlayer can turn an ordinary video containing one or more people into a
transparent show. Custom cards appear beside official iStripper cards and use
the same search, filters, favourites, ratings, queues, history, hotkeys, and
REST controls.

This is the end-user guide. Exact model versions, storage formats, benchmarks,
licences, and implementation details are in the
[technical reference](docs/custom-shows.md).

## Recommended workflow

For most users, choose one of these three methods:

| Method | Choose it when | Starting point |
| --- | --- | --- |
| **RVM-MatAnyone** | You want a good automatic workflow without drawing a mask. RVM chooses a complete-person starting mask and MatAnyone 2 produces the final alpha. | **Standard (512 px)** detail. |
| **MatAnyone 2** | You want to choose the exact person or people and manually correct the initial mask before processing. | Scrub to a clear frame, create the mask, then use **Standard (512 px)** detail. |
| **RVM-ViTMatte S** | You want a fully automatic RVM mask for every frame followed by softer ViTMatte edge refinement. | Keep the recommended ViTMatte-S batch; lower it if VRAM is tight. |

**RVM-MatAnyone is the recommended automatic starting point.** Use plain
**MatAnyone 2** when subject selection matters, there are several people, or
RVM includes the wrong foreground. Try **RVM-ViTMatte S** when hair and soft-edge
quality justify a slower, more VRAM-intensive run.

Higher **Matting detail** can improve fine edges in RVM and MatAnyone-based
methods, but increases processing time and VRAM use. It does not change the
exported video dimensions. Start at 512 px; try 768 or 1024 only after checking
that the improvement is visible. For RVM-MatAnyone, detail controls the final
MatAnyone pass; its brief RVM initializer remains capped at 512 px.
RVM-ViTMatte S does not use the Matting detail selector: ViTMatte works at the
source dimensions, and **Processing batch size** is its main VRAM control.

To run unattended, enable **Automatically accept result with alpha threshold
25**. This enables **Queue** for RVM-MatAnyone and RVM-ViTMatte S. MatAnyone 2
shows **Mask and Queue** instead: you must create one initial mask for every
included clip before the job can be queued. A queued job also requires a source
video that will remain at the recorded path, valid clip divisions, a model
profile, and all selected processing tools already installed. Open **File >
Custom Shows > Queues...** to start, reorder, pause, stop, retry, edit, or delete
jobs. Closing the queue window does not stop processing.

## Install the processing tools

Choose **File > Custom Shows > Install / Update Processing Tools...**.

- Keep **MatAnyone 2 + SAM2** selected to install MatAnyone 2 and its interactive
  mask editor. This is selected by default.
- Select **ViTMatte S + B** to install ViTMatte and the SAM2 components used by
  its editable workflow. The same install also enables **RVM-ViTMatte S**.
- RVM is part of the core processing setup and is used by RVM Quality, RVM Fast,
  RVM-MatAnyone, and RVM-ViTMatte S.
- TransNetV2 is the recommended optional clip detector and is selected by
  default. VideoMaMa, OmniShotCut, EdgeTAM, Stabilo, and ProPainter are optional
  specialist tools.

Leave the installer open until it reports completion. It creates an isolated
Python environment under
`%LOCALAPPDATA%\IStripperQuickPlayer\rvm-runtime` and reuses verified files when
run again. If needed, it installs a compatible Python for the current user.

After installation:

1. Open **File > Custom Shows > Settings...**.
2. Confirm the custom-show library folder and Python executable.
3. Click **Validate setup**.

An NVIDIA CUDA GPU is strongly recommended. CPU fallback exists for some
methods, but MatAnyone, ViTMatte, VideoMaMa, and mask tracking are generally too
slow for normal CPU-only use.

## Create or extend a show

Choose **File > Custom Shows > Create Show...**, select a source video, enter a
title, and choose or create a model profile. The source remains an external
reference; QuickPlayer does not copy or delete it.

Use **Add To Existing Show** to select an existing custom show. Its metadata is
loaded into the form, and newly processed clips are appended to its existing
clips. Right-click an existing custom card and choose **Reprocess Custom Show...**
to replace its processed media while optionally retaining clip divisions and
masks.

The collapsed **Metadata (optional)** section contains description, tags, dates,
age override, gender, hotness, performer count, clip types, and cover options.
Model profiles are reusable. Selecting an official iStripper model uses that
model's existing metadata and statistics; custom model metadata is editable and
shared by every linked custom show.

## Split and classify clips

Choose **Split into clips...** when the source contains scene changes, titles,
or sections that should not play. Clicking or scrubbing the timeline selects and
brings the related grid row into view. Use **Show skipped clips in grid** to hide
or reveal excluded sections.

You can add or move dividers, assign hotness and clip types, and include or skip
segments. Automatic detectors are:

- **Fast (FFmpeg)**: always available; lightweight visual cut detection.
- **Accurate (TransNetV2)**: recommended general detector when installed.
- **Modern/Quality (OmniShotCut)**: optional CUDA detector that identifies hard
  cuts, sudden jumps, fades, dissolves, wipes, pushes, slides, zooms, and doorway
  transitions.

The **Auto-skip shorter than** value controls which short detected clips are
excluded; it defaults to 10 seconds and can be changed before detection. The
transition buffer can skip complete transitions and a margin around them.
Review the results: skipped clips are not processed or published, and every
included clip restarts the selected matting model at its boundary.

## Processing methods

### Recommended methods

**RVM-MatAnyone** samples the first few frames of each clip with RVM, chooses the
strongest complete-person matte, cleans it, and uses it to initialize MatAnyone
2. The **RVM initializer alpha** slider controls how readily faint RVM alpha is
kept. The 40% default is a balanced choice; reduce it to retain more hair, dark
clothing, or motion-blurred limbs, and raise it to reject weak background haze.

**MatAnyone 2** opens an initial-mask editor for each included clip. Scrub to a
clear frame, left-click or paint foreground, and right-click or paint unwanted
foreground/background. A mask selected in the middle of a clip propagates both
backward and forward. This is the best choice when you need deliberate subject
selection.

**RVM-ViTMatte S** first makes a binary RVM mask for every frame and then uses
ViTMatte S to produce soft alpha edges. It is automatic and retains the generated
mask sequence for reprocessing. It normally uses more VRAM and runs more slowly
than RVM-MatAnyone. Start with the recommended batch size; larger batches are not
always faster and can force Windows into very slow shared GPU memory.

### Other methods

- **RVM Quality (ResNet50)** is the built-in automatic whole-person method. It is
  quick and dependable, but its final edge quality may be below MatAnyone or
  ViTMatte.
- **RVM Fast (MobileNetV3)** is lighter than RVM Quality and useful on slower
  hardware or for drafts.
- **ViTMatte S** uses editable SAM2 or EdgeTAM masks and is the recommended
  ViTMatte model. Choose it when you want to correct tracking before edge
  refinement.
- **ViTMatte B** uses a larger backbone. It is slower and can consume much more
  VRAM; use it only after S and with a conservative batch.
- **VideoMaMa High Quality** uses editable masks and diffusion-based processing.
  It is experimental, CUDA-only, very slow, and requires substantial VRAM and
  temporary disk space.

The form remembers the previous algorithm, mask engine, model, detail, batch,
RVM threshold, and automatic-accept choice. It shows only controls that apply to
the selected method.

For editable-mask methods, **SAM2 Base+** is the most robust mask generator and
the recommended default; Small and Tiny trade some robustness for speed.
**EdgeTAM** is the faster optional alternative when installed. The chosen mask
engine tracks the binary subject mask; MatAnyone, ViTMatte, or VideoMaMa then
performs the selected final processing.

## Create and correct masks

The initial-mask and tracked-mask editors support:

- left-click or left-paint to add foreground;
- right-click or right-paint to remove foreground;
- a brush-size slider and mouse wheel to resize the circular brush;
- **Ctrl+P** to toggle painting and **Ctrl+Z** to undo;
- playback, scrubbing, slow motion, and frame stepping.

For ViTMatte and VideoMaMa, mask generation can be interrupted with **Pause and
Correct**. The timeline always represents the whole clip; after pausing, scrub
back to any generated frame, correct the first tracking error, and choose
**Update masks**. During backward propagation, use **Stop backward propagation**
once the correction has travelled far enough. Continue correcting later drift,
then choose **Use corrected masks** when the complete clip is valid.

Mask work is saved as a resumable draft if processing is cancelled or fails.
Published shows also retain reusable masks: initial masks as PNG and tracked
sequences as lossless one-bit, XOR-delta, Zstandard-compressed `.iqpmask`
archives. Reprocessing with **Keep existing masks** avoids masking again and can
reuse masks across MatAnyone 2, RVM-MatAnyone, VideoMaMa, and ViTMatte where the
workflow is compatible.

## Process, review, or queue

**Process and Preview** runs immediately, shows source and mask/composite
previews, and opens a final review where each clip's minimum alpha can be tuned.
New clips use alpha threshold 25. Choose **Accept** to publish atomically;
cancelling or failing never publishes a partial card.

Enable **Automatically accept result with alpha threshold 25** for a fire-and-
forget run. The number is an alpha cutoff on the 0-255 scale, not 25% of the
image. With automatic acceptance enabled:

- RVM Quality, RVM Fast, RVM-MatAnyone, and RVM-ViTMatte S can be queued directly.
- MatAnyone 2 requires **Mask and Queue** so its initial masks exist first.
- VideoMaMa and manually masked ViTMatte currently run through the interactive
  workflow rather than the automatic queue.

The queue runs one show at a time. It verifies that referenced sources and target
shows have not changed, preserves conflicts as **Needs attention**, and retains
completed entries as history until you delete them. Pause can finish the current
show or cancel it back to Pending; Stop also returns it to Pending for a complete
restart. Closing QuickPlayer asks before cancelling an active job.

Before appending to or replacing a show, QuickPlayer moves playback away from
that show—or stops playback when no other valid card is available—then waits for
its decoder files to close. Temporary Windows file locks are retried. If the
folder still cannot be replaced, the completed staging output is retained and
**Retry** attempts publication again without rerunning matting.

## Reprocessing and adding clips

Right-click a custom card and choose **Reprocess Custom Show...**. Keep existing
clip divisions and masks unless you need to correct them. This lets you compare,
for example, MatAnyone 2 with RVM-MatAnyone or ViTMatte without recreating all
mask work. The replacement is validated before the existing show is swapped.

**Edit Custom Show Metadata...** does not need the referenced source for ordinary
changes. QuickPlayer preserves the existing cover unless you select a new custom
cover or change an automatic-cover input such as its title, model, or title
colour. Regenerating an automatic cover does require the source.

To add a different source video as more clips on the same card, begin a new
Create Show workflow and select **Add To Existing Show**. The selected show's
metadata is populated; split and process the new source normally, and its
included clips are appended.

## Performance and VRAM guidance

- Begin MatAnyone/RVM processing at **Standard (512 px)**. Lower detail if VRAM
  fills; raise it only for a visible edge-quality improvement.
- Use the recommended ViTMatte-S batch. ViTMatte-B and VideoMaMa should normally
  start at batch 1 on 12-16 GB GPUs.
- QuickPlayer warns when a requested ViTMatte or VideoMaMa batch is unlikely to
  fit and lets you use the suggested value or continue deliberately.
- If Task Manager shows dedicated VRAM full and shared GPU memory growing,
  cancel and lower detail or batch size. Shared-memory spill can slow the whole
  desktop dramatically.
- **CPU priority** and **GPU priority** in Custom Show Settings apply to newly
  started processing workers. Both default to **Normal**. Lowering them may
  favour playback and desktop responsiveness, but can reduce processing speed.
- Benchmarks are optional. They measure safe batches and compiled/eager paths on
  the current GPU; settings change only after you accept and save the results.

## Playback, storage, and backup

Accepted custom cards become visible immediately and can mix with official cards
in manual and automatic playback queues. A custom card may contain several clips;
clip completion follows the normal QuickPlayer next-item rules.

Shows are stored under `<custom-library>\shows`, model profiles under
`<custom-library>\performers`, resumable drafts under
`<custom-library>\.mask-drafts`, and processing queue state under
`<custom-library>\queue`. QuickPlayer `.iqpb` backups do not contain this
library, so back up the complete custom-library folder separately.

Deleting a custom show moves its show folder to the Recycle Bin. It does not
delete its referenced source or shared model profile.

## Source preparation tools

- **Stabilize Video (FFmpeg)** performs conventional two-pass stabilization.
- **Camera / Background Lock (Stabilo + SAM2)** tracks a selected background and
  compensates camera motion.
- **Remove Video Object / Watermark** offers fast FFmpeg delogo or optional
  ProPainter AI inpainting. ProPainter defaults to 25% processing resolution
  because 50% uses four times as many pixels and can exhaust VRAM. Scrub to a
  representative frame before selecting the rectangle; the rectangle applies
  for the complete video.

These tools create a new source video. Select that output when creating the
custom show.

## Troubleshooting

- Run **Validate setup** after changing Python, CUDA, drivers, or optional tools.
- If an algorithm is absent, rerun **Install / Update Processing Tools** with its
  checkbox selected.
- Lower detail or batch size after an out-of-memory error.
- Keep referenced source videos at their recorded paths for reprocessing.
- Use **Open Log** for worker, FFmpeg, model, or media mismatch diagnostics.
- Do not delete active staging, queue, or mask-draft folders while work is open.
- Foreground and alpha media are a synchronized pair; do not transcode one
  independently.

Only process videos you have permission to use. Review the individual model
licences before commercial use. See the [technical reference](docs/custom-shows.md)
for exact licence terms and pinned upstream versions.
