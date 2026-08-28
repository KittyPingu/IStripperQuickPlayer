# Creating Custom Shows

## RVM ONNX real-time playback

`RVM ONNX (real-time playback)` does not require Python, a vendor segmentation
SDK, or an API key. When a show contains one full-duration clip and the source
passes a real seek test, QuickPlayer copies the video byte-for-byte into the
show without transcoding it. Trimmed or split clips are encoded as RGB-only
H.264/AAC media. QuickPlayer runs Robust Video Matting through DirectML during
playback and generates the alpha mask for each frame. The original
full-resolution RGB remains the displayed image.

Install the optional model from **Custom Shows > Settings > Maintenance >
Install / Update Processing Tools**, or choose **Open Setup** beside the RVM
ONNX controls while creating a show. Setup downloads the official MobileNetV3
and ResNet50 FP32 ONNX models (about 117 MB total), verifies their fixed sizes
and SHA-256 hashes, and performs a test load and inference with each. They are stored under
`%LOCALAPPDATA%\IStripperQuickPlayer\rvm-onnx`; it is not included in the
QuickPlayer application. The model and upstream implementation are provided by
[Robust Video Matting under GPL-3.0](https://github.com/PeterL1n/RobustVideoMatting).

NVIDIA AI Green Screen is no longer a playback option. QuickPlayer
automatically opens older `nvidia-aigs` manifests and queued jobs as RVM ONNX
clips using MobileNetV3, Balanced quality, and temporal memory. Their existing
RGB video is retained, so this migration does not re-encode the clip. The
NVIDIA Video Effects SDK, NGC API key, and NVIDIA foreground settings are not
needed. CUDA may still be used by some optional offline processing tools; that
is separate from real-time RVM playback.

Each clip saves these appearance/performance settings:

- **MobileNetV3** is the recommended faster model. **ResNet50** is substantially
  larger and slower, with the small quality improvement described by the RVM
  project. Model selection is saved independently for each clip.
- **Fast**, **Balanced**, and **Quality** change the internal RVM processing
  resolution. Higher quality preserves more fine detail but requires more GPU
  work.
- **Full-resolution refinement** feeds RVM the complete source frame while
  retaining 512-level internal analysis. Unlike **Quality**, which enlarges a
  smaller generated mask for composition, this lets RVM's guided refinement
  produce the matte at source resolution. It can improve fine hair and edge
  detail, but transfers and refines substantially more pixels and may not keep
  up in real time—particularly with ResNet50 or high-resolution sources.
- **Temporal memory** carries RVM's recurrent state from one frame to the next,
  reducing flicker and improving consistency. Disable it when you prefer every
  frame to be segmented independently.

During playback, right-click an RVM ONNX clip and choose **Real-time Foreground
Settings...**. Changes are applied to the current
frame without restarting the clip; temporal state is reset so subsequent frames
use the new settings cleanly. Seeking, restarting, and starting a new clip also
reset that state. Existing alpha threshold, full-opacity threshold, edge
cleanup, hit testing, click-through, and virtual green-screen controls are
applied after the generated matte.

DirectML uses the available Windows graphics adapter and does not require CUDA.
On supported systems, FFmpeg hardware-decodes directly to a Direct3D 12 video
surface. A GPU shader converts and resizes that surface into RVM's planar input,
DirectML keeps inference and recurrent-state tensors on the same GPU, and the
resulting alpha texture is shared directly with the Direct3D 11 compositor.
No RGB frame crosses the CPU boundary in this path; alpha is read back only
when QuickPlayer needs an alpha-aware mouse hit map. If hardware decoding or GPU
sharing is unavailable, QuickPlayer falls back to its software-decoded DirectML
path.
Performance depends on the source resolution and GPU; start with **Balanced**
and use **Fast** if playback cannot keep up. If the model is missing or an
inference error occurs, playback continues as opaque RGB and QuickPlayer shows
one non-blocking setup warning for the session.

The card filter includes a separate **Real-time** checkbox. Clear it to hide
custom shows containing RVM real-time clips while leaving ordinary custom shows
visible; the main **Custom** checkbox still controls all custom shows.

The filter is based on the clips in each show, not the algorithm that originally
created it. Mixed shows therefore remain visible when **Real-time** is enabled
and are hidden when it is disabled if any playable clip uses RVM ONNX.

Reprocessing an existing show to RVM ONNX reuses each unchanged clip's existing
`foreground.mp4`. QuickPlayer encodes again only when a selected clip's source
range changed or its retained RGB file is missing.

If a copied whole-video show is later divided into clips, those ranges must be
encoded so their boundaries are accurate. QuickPlayer then asks whether to
delete the superseded whole-video copy after the split clips publish. Choosing
No keeps it as the show's retained source; choosing Yes reclaims its library
storage. This prompt never deletes the external source selected by the user.

### Create a real-time show from a URL

Choose **File > Custom Shows > Create Real-time Show from URL...** and paste an
HTTP or HTTPS page containing a video, or a direct video URL. QuickPlayer uses
[yt-dlp](https://github.com/yt-dlp/yt-dlp) to resolve and download one video;
playlists are deliberately disabled. The same setup also installs the managed
Deno runtime that current yt-dlp versions use for YouTube's JavaScript
challenges. If either tool is missing, the URL window can install it before
downloading. Setup is also available as the optional
**URL video downloader** entry under **Install / Update Processing Tools...**.

For a public page, leave **Authentication** set to **None**. If a site requires
the account already signed into Chrome, Edge, Firefox, or Brave, select that
browser so yt-dlp can use its local cookie store. On Windows, fully close
Chrome, Edge, or Brave first, including background processes; Chromium locks
its cookie database while it is running. Newer Chromium cookie encryption can
also prevent yt-dlp from decrypting the database even after the browser closes.
If that happens, retry with **None** for a public page, use a signed-in Firefox
profile, or use the cookie-file option below.

If browser extraction remains unavailable, select **Cookie file (Netscape
format)** and browse to a `cookies.txt` export. For age-restricted YouTube
videos, follow yt-dlp's [YouTube cookie instructions](https://github.com/yt-dlp/yt-dlp/wiki/Extractors#exporting-youtube-cookies): sign in through a private/incognito
window, visit `https://www.youtube.com/robots.txt` in that same tab, export the
YouTube cookies using a conforming local cookie exporter, then close the private
window. The file's first line must be `# HTTP Cookie File` or
`# Netscape HTTP Cookie File`. Treat this file like a password. QuickPlayer
passes a temporary copy to yt-dlp, deletes it after the attempt, and never puts
it in the queue or published show; it does not store or log cookie values.
YouTube warns that using an account through yt-dlp can put that account at risk,
so use authentication only when needed. Some protected sites may still reject
downloads, and DRM-protected streams are not supported.

After downloading, QuickPlayer opens the normal custom-show editor with the
page title filled in and **RVM ONNX (real-time playback)** selected. Choose a
model profile, adjust the title, clips, metadata, RVM model, quality, and
temporal memory, then use **Encode and Preview** or **Queue** as usual. This
workflow creates a new show; it cannot append directly to an existing show.

The temporary download is deleted when the editor closes. Before that happens,
queueing copies the complete source into the queue job. For an unsplit seekable
video, that byte-for-byte copy is also the clip's playback media, avoiding a
second encoded copy. If it cannot be reused directly, publication keeps a
separate source copy alongside the encoded RGB clips. The remote page therefore
does not need to remain available and the show can be reprocessed later.
Deleting the custom show removes its owned media with the rest of the show.

Only download and process videos you have permission to use, and follow the
site's terms. A failed extraction normally means the page is unsupported,
requires a login/cookies, has changed, or uses DRM; the error output in the URL
window contains yt-dlp's explanation. If a site's dedicated extractor reports
that it found no video formats, QuickPlayer automatically retries once with
yt-dlp's generic page extractor. That fallback may expose fewer quality choices
than a working dedicated extractor. XVideos is handled more specifically: when
its current extractor misses the site's player data, QuickPlayer resolves the
player's signed adaptive HLS manifest and gives that back to yt-dlp, which then
selects the best available rendition instead of the generic 360p metadata URL.

### Import a folder as real-time shows

Choose **File > Custom Shows > Folder Import for Real-time...** and select a root
folder. QuickPlayer scans its subfolders for MP4, MOV, MKV, AVI, and WebM files,
then shows a review grid. Each included video becomes one custom show containing
one full-length RGB clip. Choose the RVM model, quality, and temporal settings at
the top of the review window. The optional model files do not need to be
installed to scan or queue the shows.

The sortable grid shows each video's relative path, proposed model, editable
show name, resolution, duration, and status. Individual include checkboxes,
model cells, and show names remain editable. Use the explicit batch toolbar to
select rows, include or exclude the selection, and apply one model to selected
rows; **Apply model to all included** handles the complete batch. **New
model...** opens the normal model-profile editor. If the selected folder name
matches an existing model name after spaces and punctuation are ignored, that
model is assigned automatically; otherwise assign a model before adding the
batch.

Generated show names remove common dates, model/uploader names, Reddit and
OnlyFans/Coomer source labels, post identifiers, resolution/codec labels, and
sequence suffixes. Punctuation is converted to spaces and camel-case words are
split. Repeated generated names receive numeric suffixes; manually entered names
may still be the same. Release and show dates come from a leading `YYYY-MM-DD`
filename date, otherwise the nearest `YYYY-MM` parent folder, otherwise the
file's modified date.

Videos already referenced by a published custom show or present anywhere in the
queue are shown as excluded duplicates. Unreadable videos also remain visible
but cannot be included. Set the shared RVM options, hotness, and
clip types above the grid, then choose **Add to Queue**. The whole valid batch
is added together and the queue is opened but not started.

Folder import does not copy the source immediately. Keep every source video at
the same path and do not modify it until its queue job has completed; moving,
deleting, or changing a source makes that job require attention. You can edit an
individual imported job from the queue before running it.

QuickPlayer can turn an ordinary video containing one or more people into a
transparent show. Custom cards appear beside official iStripper cards and use
the same search, filters, favourites, ratings, queues, history, hotkeys, and
REST controls.

This is the end-user guide. Exact model versions, storage formats, benchmarks,
licences, and implementation details are in the
[technical reference](docs/custom-shows.md).

## Guided custom-show wizard

Choose **File > Custom Shows > Create Show with Wizard...** when you want help
choosing the source, model profile, clip divisions, and processing method. The
normal Create Custom Show editor stays open and editable while a companion
window explains the current step and shows which required details are complete.

The wizard asks whether you want preprocessed or real-time transparency, how
much mask editing you are willing to do, whether speed, exact selection, soft
edges, or scene changes matter most, what class of hardware you have, and
whether you want to review the result interactively or queue it. It then
explains an ideal method and applies its recommended starting values only when
you choose **Apply recommendation**. Later manual changes in the editor are not
overwritten.

If the ideal method is not installed, the wizard can open the appropriate setup
with the needed components preselected or apply the best available fallback.
The recommendation clearly identifies features lost by a fallback, such as
real-time playback, exact subject selection, ViTMatte edge refinement, or
independent scene prompts. The existing editor remains responsible for final
validation, processing, previews, and queue creation.

## Recommended workflow

For most users, choose one of these methods:

| Method | Choose it when | Starting point |
| --- | --- | --- |
| **RVM-MatAnyone** | You want a good automatic workflow without drawing a mask. RVM chooses a complete-person starting mask and MatAnyone 2 produces the final alpha. | **Standard (512 px)** detail. |
| **MatAnyone 2** | You want to choose the exact person or people and manually correct the initial mask before processing. | Scrub to a clear frame, create the mask, then use **Standard (512 px)** detail. |
| **RVM-ViTMatte S** | You want a fully automatic RVM mask for every frame followed by softer ViTMatte edge refinement. | Keep the recommended ViTMatte-S batch; lower it if VRAM is tight. |
| **SAM2Matting** | You want scene-aware SAM2.1 tracking with a separate initial mask for each detected scene. | Start with **RVM → SAM2.1-B+** for automatic person masks. |
| **RVM ONNX (real-time playback)** | You want RGB-only clips and an alpha generated locally during playback. | **Balanced** with temporal memory enabled. |

**RVM-MatAnyone is the recommended automatic starting point.** Use plain
**MatAnyone 2** when subject selection matters, there are several people, or
RVM includes the wrong foreground. Try **RVM-ViTMatte S** when hair and soft-edge
quality justify a slower, more VRAM-intensive run. Use **RVM Quality** when you
want a very fast automatic result and its simpler matte quality is sufficient.
Use **SAM2Matting** when scene changes inside a clip need independent prompts.

Higher **Matting detail** can improve fine edges in RVM and MatAnyone-based
methods, but increases processing time and VRAM use. It does not change the
exported video dimensions. Start at 512 px; try 768 or 1024 only after checking
that the improvement is visible. For RVM-MatAnyone, detail controls the final
MatAnyone pass; its brief RVM initializer remains capped at 512 px.
RVM-ViTMatte S does not use the RVM/MatAnyone **Matting detail** selector. Use
**ViTMatte inference detail** instead; it defaults to 1024 px while published
foreground and alpha media retain the source dimensions.

As a real-world queue reference, a 38-minute 1920x1080 source divided into 18
clips took about **7 minutes with RVM Quality**, **42 minutes with
RVM-MatAnyone**, and **2 hours 30 minutes with RVM-ViTMatte S** on the reference
machine.

To run unattended, enable **Automatically accept result with alpha threshold
25**. This enables **Queue** for RVM-MatAnyone and RVM-ViTMatte S. MatAnyone 2
shows **Mask and Queue** instead: you must create one initial mask for every
included clip before the job can be queued. A queued job also requires a source
video that will remain at the recorded path, valid clip divisions, a model
profile, and all selected processing tools already installed. Open **File >
Custom Shows > Queues...** to start, reorder, pause, stop, retry, edit, or delete
jobs. Closing the queue window does not stop processing.
When a queued show uses a newly created custom model, accepting the queue job
also saves that model immediately so it can be selected by later shows without
waiting for processing to finish.
Use **Remove Completed** to clear all successful history entries after confirming;
published shows are not deleted.

## Install the processing tools

Choose **File > Custom Shows > Install / Update Processing Tools...**.

- Keep **MatAnyone 2 + SAM2** selected to install MatAnyone 2 and its interactive
  mask editor. This is selected by default.
- Select **ViTMatte S + B** to install ViTMatte and the SAM2 components used by
  its editable workflow. The same install also enables **RVM-ViTMatte S**.
- Select **URL video downloader** to install or update the official standalone
  Windows yt-dlp executable and its recommended Deno JavaScript runtime. Setup
  verifies both official downloads with SHA-256, test-runs them, and only then
  replaces each previous working copy. It does not install Python.
- RVM is part of the core processing setup and is used by RVM Quality, RVM Fast,
  RVM-MatAnyone, and RVM-ViTMatte S.
- **SAM2Matting** has a separate setup button beside its backbone selector. It
  installs an isolated Python 3.10 runtime and the SAM2.1-T and SAM2.1-B+
  checkpoints under `%LOCALAPPDATA%\IStripperQuickPlayer\sam2matting-worker\v1`.
- TransNetV2 is the recommended optional clip detector and is selected by
  default. OmniShotCut, EdgeTAM, Stabilo, and ProPainter are specialist tools.

Leave the installer open until it reports completion. It creates an isolated
Python environment under
`%LOCALAPPDATA%\IStripperQuickPlayer\rvm-runtime` and reuses verified files when
run again. If needed, it installs a compatible Python for the current user.

After installation:

1. Open **File > Custom Shows > Settings...**.
2. Confirm the custom-show library folder and Python executable.
3. Click **Validate setup**.

An NVIDIA CUDA GPU is strongly recommended. CPU fallback exists for some
methods, but MatAnyone, ViTMatte, and mask tracking are generally too slow for
normal CPU-only use.

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
age override, gender, hotness, performer count, clip types, cover options, and
an optional **Photo folder**. Attach a folder of JPG, JPEG, PNG, BMP, or GIF
images to make them available through the card's **Photos** button and the
wallpaper feature. QuickPlayer also scans its subfolders. The folder is
referenced rather than copied into the custom-show library, so keep it
available or update the setting if it moves.
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

All three detectors show a **Sensitivity** slider above the grid beside **Show
skipped clips in grid**. Higher values find or retain more possible cuts; lower
values keep only stronger changes. Fast defaults to 65% and TransNetV2 to 50%,
matching their previous fixed behaviour. OmniShotCut defaults to 100%, preserving
its previous result; its confidence scores cluster near 1.0, so its percentage
means the proportion of strongest candidate boundaries retained. For example,
1% keeps roughly the strongest 1%, while 100% keeps them all. Each detector
remembers its own setting.

New detections retain compressed confidence data. While you move the slider,
QuickPlayer rebuilds the grid after a short debounce without decoding the source
or rerunning the detector. If dividers or included/skipped states changed after
detection, live recalculation pauses and QuickPlayer asks on release before
replacing those edits. After acceptance, continued slider movement updates live.
Results created before confidence retention require one new **Auto-detect clips**
run.

The **Auto-skip shorter than** value controls which short detected clips are
excluded; it defaults to 20 seconds and can be changed before detection. The
transition buffer can skip complete transitions and a margin around them. For a
hard cut, the value is applied on both sides: **±1.5 seconds** creates one skipped
3-second segment centred on the cut.
Review the results: skipped clips are not processed or published, and every
included clip restarts the selected matting model at its boundary.

After publishing, right-click a custom clip and choose **Trim Custom Clip...**
to set playback start and end markers against a DirectComposition preview.
Trimming is non-destructive: reopening the editor restores the saved markers,
and **Reset to full clip** makes all of the processed media playable again.

## Processing methods

### Recommended methods

**RVM-MatAnyone** samples the first few frames of each clip with RVM, chooses the
strongest complete-person matte, cleans it, and uses it to initialize MatAnyone
2. The **RVM initializer alpha** slider controls how readily faint RVM alpha is
kept. The 40% default is a balanced choice; reduce it to retain more hair, dark
clothing, or motion-blurred limbs, and raise it to reject weak background haze.
Enable **RVM mask refresh** to keep RVM running during forward propagation and
inject persistent foreground that MatAnyone has missed. A missing region must
remain for three frames before its eroded core is added to MatAnyone's bounded
memory, with a 15-frame cooldown between corrections. This can recover people or
held objects that enter later, at the cost of extra processing time and GPU memory.
**RVM refresh strength** controls how strongly that eroded core overrides the
current MatAnyone alpha (25–100%, default 100%). Lower it for gentler corrections
when RVM includes uncertain foreground.

For **MatAnyone 2** and **RVM-MatAnyone**, **Max memory frames** controls the
recent detailed-memory window (2–30 frames without long-term memory, or 6–14
frames with it). **Use compressed long-term memory** consolidates older entries
up to 4,000 tokens with a 500-token consolidation buffer. The default remains
fourteen frames with long-term memory enabled.
Lower the maximum frames if processing exhausts GPU memory.

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

**SAM2Matting** first divides every included clip into processing scenes. Choose:

- **SAM2.1-T** for the fastest interactive per-scene masks;
- **SAM2.1-B+** for higher-capacity interactive per-scene masks;
- **RVM → SAM2.1-T/B+** to generate one automatic person mask per scene when the
  job runs.

Interactive SAM2.1 modes open the same click/paint mask editor for every scene.
Left-click or paint foreground and right-click or paint unwanted regions. RVM
RVM initialization can be queued without opening mask editors. Processing
now expands the progress window with current-source and matted-foreground
previews; background queue jobs skip preview generation.

### Other methods

- **RVM Quality (ResNet50)** is the built-in automatic whole-person method. It is
  quick and dependable, but its final edge quality may be below MatAnyone or
  ViTMatte.
- **RVM Fast (MobileNetV3)** is lighter than RVM Quality and useful on slower
  hardware or for drafts.
- **RVM ONNX (real-time playback)** uses the MobileNetV3 RVM model during
  playback instead of baking an alpha video. It runs through DirectML and
  requires only the downloaded ONNX model.
- **ViTMatte S** uses editable SAM2 or EdgeTAM masks and is the recommended
  ViTMatte model. Choose it when you want to correct tracking before edge
  refinement.
- **ViTMatte B** uses a larger backbone. It is slower and can consume much more
  VRAM; use it only after S and with a conservative batch.

**VideoMaMa is no longer a processing option.** QuickPlayer retains only enough
legacy manifest support to load and play shows created with older versions.

The form remembers the previous algorithm, mask engine, model, detail, batch,
RVM threshold, and automatic-accept choice. It shows only controls that apply to
the selected method.

For editable-mask methods, **SAM2 Base+** is the most robust mask generator and
the recommended default; Small and Tiny trade some robustness for speed.
**EdgeTAM** is the faster optional alternative when installed. The chosen mask
engine tracks the binary subject mask; MatAnyone or ViTMatte then performs the
selected final processing.

## Create and correct masks

The initial-mask and tracked-mask editors support:

- left-click or left-paint to add foreground;
- right-click or right-paint to remove foreground;
- a brush-size slider and mouse wheel to resize the circular brush;
- **Ctrl+P** to toggle painting and **Ctrl+Z** to undo;
- playback, scrubbing, slow motion, and frame stepping.

During interactive MatAnyone 2 processing, use **Pause and correct mask** to
scrub back to an earlier processed frame, edit its predicted mask, and replay the
correction forward. Overlay playback shows the synchronized RGB and mask.

For ViTMatte, mask generation can be interrupted with **Pause and Correct**. The
timeline always represents the whole clip; after pausing, scrub
back to any generated frame, correct the first tracking error, and choose
**Update masks**. During backward propagation, use **Stop backward propagation**
once the correction has travelled far enough. Continue correcting later drift,
then choose **Use corrected masks** when the complete clip is valid.

Mask work is saved as a resumable draft if processing is cancelled or fails.
Published shows also retain reusable masks: initial masks as PNG and tracked
sequences as lossless one-bit, Zstandard-compressed `.iqpmask`
archives. Reprocessing with **Keep existing masks** avoids masking again and can
reuse masks across MatAnyone 2, RVM-MatAnyone, and ViTMatte where the workflow is
compatible.

## Process, review, or queue

**Process and Preview** runs immediately, shows source and mask/composite
previews, and opens a final review where each clip's minimum alpha can be tuned.
New clips use alpha threshold 25. Choose **Accept** to publish atomically;
cancelling or failing never publishes a partial card.

For RVM ONNX, the immediate action is **Encode and Preview**. A single seekable
whole video is copied without transcoding; split or trimmed clips are encoded.
**Queue** is available without automatic acceptance and without an installed
ONNX model; the model is required only when the resulting show is played.

Enable **Automatically accept result with alpha threshold 25** for a fire-and-
forget run. The number is an alpha cutoff on the 0-255 scale, not 25% of the
image. With automatic acceptance enabled:

- RVM Quality, RVM Fast, RVM-MatAnyone, and RVM-ViTMatte S can be queued directly.
- MatAnyone 2 requires **Mask and Queue** so its initial masks exist first.
- SAM2Matting can be queued directly. Interactive SAM2.1 modes collect all
  required scene masks before saving the job; RVM-initialized SAM2.1 creates
  its prompts automatically when the job runs.
- Manually masked ViTMatte currently runs through the interactive workflow rather
  than the automatic queue.

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
- Prefer **Auto** batch for RVM, ViTMatte, and RVM-ViTMatte. It starts from
  current VRAM and effective inference size, adjusts from live memory use,
  and remembers successful values for similar future runs. Manual values remain
  available when repeatability is required.
- QuickPlayer warns when a requested ViTMatte batch is unlikely to fit and lets
  you use the suggested value or continue deliberately.
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

### Send a custom show to another QuickPlayer user

Choose **Custom Shows > Export Custom Show Package...**, select a show, and save
the generated `.iqpshow.zip` file. The same command is available by
right-clicking a custom card. The package contains the complete published show
folder, all playback media, its cover and retained show files, the linked model
profile, and any available attached photo folder. Referenced original source
videos are not duplicated because they are not required for playback; their
private local paths are reduced to filenames in the exported manifest.

On the receiving computer, choose **Custom Shows > Import Custom Show
Package...**. QuickPlayer validates and stages the ZIP before changing the
library. It reuses a model profile with the same profile ID, iStripper model ID,
or model name; otherwise it creates the supplied model profile automatically.
The imported show appears immediately. Import never overwrites a show with the
same show ID, and an invalid or incomplete package leaves the library unchanged.
Reprocessing may require the recipient to relink the original source video.

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
- For paired-alpha shows, foreground and alpha media are synchronized; do not
  transcode one independently. RVM ONNX real-time clips intentionally contain
  only `foreground.mp4` and must not be given a separate alpha file.

Only process videos you have permission to use. Review the individual model
licences before commercial use. See the [technical reference](docs/custom-shows.md)
for exact licence terms and pinned upstream versions.
