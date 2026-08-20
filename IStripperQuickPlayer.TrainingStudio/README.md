# QuickPlayer Training Studio

The label-painting canvas is presented through a DirectComposition/D3D11 swap
chain. Its render thread coalesces rapid paint updates so WinForms repainting does
not expose partially drawn image and mask frames.
RVM-detected foreground is shown as a read-only pink layer in the labeling editor;
editable object labels remain independently colored above it.
The pink layer is deliberately subtle and can be toggled from the editor toolbar.
Its threshold slider ranges from 0.10 to 0.90 and refreshes after a short debounce.
Training Studio keeps one persistent RVM preview worker warm across frames and
slider changes, avoiding repeated model initialization.
The annotation editor also keeps one SAM2 Base+ worker warm across images;
switching frames reloads only the image and recomputes its required embedding.
As soon as any candidate is displayed, a bounded queue of up to ten eligible
source frames is selected and decoded to draft PNGs in the background. The first
successor is prioritized; accepting, rejecting, or marking the current frame
negative advances into the same prepared queue. Those PNGs are also decoded into
a bounded ten-bitmap memory cache, avoiding image decoding on the UI thread.
RVM is generated only when requested from the editor. The first request decodes a
two-second lead-in and runs it sequentially to establish recurrent temporal state;
later threshold changes reuse the cached final alpha map.

Indexing a video folder preserves all existing dataset work and makes that folder
the persisted active source for subsequent random candidates. Previously indexed
sources remain recorded but are not sampled until their folder is selected again.
Previously scanned folders appear in the Active videos dropdown and can be reused
without another recursive scan.
Changing the active folder immediately loads a candidate from it. Drafts belonging
to other folders remain preserved and reappear when their folder is active again.
The shared `prop_segmenter.py` helper is deployed beside the RVM worker under
`custom-shows`, where both RVM preview and model training can import it.

Training Studio is a local x64 WinForms companion for building and training the
binary foreground-prop model used by compatible automatic RVM initializers.

## Workflow

1. Open or create a dataset folder.
2. Index one or more folders containing videos. Indexing is recursive.
3. Use **Random frame** and decide each candidate as **Accept + label**,
   **No foreground objects**, or **Reject**.
4. For positive frames, create stable prop objects and annotate in point mode
   (left positive, right negative) or enable **Paint** (left paint, right erase).
   Single clicks add SAM2 prompts and a double-click applies them; SAM boxes and
   polygons remain available. Mouse
   wheel zooms and the middle mouse button pans. Drafts save automatically.
5. Optionally create a five-frame burst at offsets -3, -1, 0, +1 and +3
   seconds. Accepting its centre anchor can seed SAM2
   drafts for its neighbours; review then advances through the remaining burst
   automatically, but every frame still requires an explicit decision.
6. Train after the dashboard has a useful mixture of sources and negatives.
   The **Minimum** dropdown can exclude lower-resolution frames using the image's
   shorter dimension, so the same choices work for landscape and portrait media.
   It also filters random frame selection and queued drafts while it is selected.
   **Training size** selects a 512, 768, or 1024 pixel square model input. Larger
   sizes retain more fine detail but reduce automatic batch size and take longer.
   **Negatives** can run one fixed 20%, 25%, 30%, or 35% subset, compare all four,
   or use every eligible negative sample.
   Each run keeps every positive and can train four independent candidates using
   reproducible 20%, 25%, 30%, and 35% negative subsets. Positive crops usually
   remain centred on labelled foreground, while error-review and burst samples
   receive extra sampling weight. Training combines focal, Dice, and
   false-negative-weighted Tversky losses, uses cosine learning-rate decay, and
   evaluates exponential-moving-average weights. Checkpoints and negative-ratio
   winners are selected using threshold-calibrated global Dice, per-image Dice,
   positive recall, and small-object recall. Only the winner is evaluated on the
   untouched test split. Candidate metrics and the selected ratio are saved in
   `package/metrics.json`.
   Every run persists structured progress in `events.ndjson` and diagnostics in
   `training.stderr.log` inside its run folder, including failed runs.
   Cancellation leaves `last.pth`; enable **Resume latest checkpoint** to continue.
7. Inspect **Open error review**. Ranked false positives and false negatives can
   be reopened for correction, or expanded into a five-frame feedback burst.
   Corrected samples return to the ordinary dataset and are included in later
   training runs. Then install the package; installation does not enable it.
8. In QuickPlayer Custom Show settings, open the RVM tab, enable prop augmentation,
   and select the installed model.

The dataset keeps source videos in place. `dataset.json` contains only indexed
video metadata. Each candidate has its own atomic, schema-versioned record under
`records/<shard>/<sampleId>.json`; there is no shared decision ledger. Ordinary
mask edits update only that frame's files after its draft paths have first been
registered. Accepted frames and masks are stored under `samples`, in-progress
work under `drafts`, and training runs under `runs`.

## Verification

From the repository root:

```powershell
dotnet build .\IStripperQuickPlayer.TrainingStudio\IStripperQuickPlayer.TrainingStudio.csproj -c Release -p:Platform=x64 --no-restore
.\IStripperQuickPlayer.TrainingStudio\bin\x64\Release\net10.0-windows10.0.19041.0\IStripperQuickPlayer.TrainingStudio.exe --verify-training-studio
```
