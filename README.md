# iStripper QuickPlayer

[![Latest release](https://img.shields.io/github/v/release/KittyPingu/IStripperQuickPlayer)](https://github.com/KittyPingu/IStripperQuickPlayer/releases/latest)
[![Build installer](https://github.com/KittyPingu/IStripperQuickPlayer/actions/workflows/release.yml/badge.svg)](https://github.com/KittyPingu/IStripperQuickPlayer/actions/workflows/release.yml)
[Project website](https://kittypingu.github.io/IStripperQuickPlayer/)

A Windows companion for browsing your local iStripper library, launching the
card or clip you want, and controlling compatible desktop playback.

![QuickPlayer library and playback controls](docs/images/quickplayer-overview.jpg)

QuickPlayer uses the cards already installed by iStripper. It does not download
or include shows.

## What QuickPlayer adds

### Browse your library visually

- Browse local cards as a resizable cover-art library.
- Search model names, show titles, descriptions and tags.
- Sort by model, age, rating, statistics, purchase date or release date.
- Add your own favourites, ratings and tags.
- Show the static and animated card overlays available to your iStripper
  account.
- Save and reload detailed card filters.
- Optionally restrict automatic playback to the cards and clips remaining
  after your filters are applied.
- Reload the library after buying or downloading new cards.

### Choose exactly what plays

- Select a card to see its available clips.
- Filter clips by explicitness, type and minimum file size.
- Start an individual clip or play the next clip.
- Use **Show Model** to filter the library to the performer currently showing,
  making it easy to browse only that model's shows and clips.
- Jump from **Now Playing** to the matching card.
- See the active clip highlighted in green when it is visible in the list.
- Randomize playback and optionally avoid recently played clips.

### Plan what plays next

The collapsible strip at the bottom of QuickPlayer shows two queues:

- drag a card into **Manual** to queue a clip from that card, or drag a clip
  row to queue that exact clip;
- drag queue entries to reorder them, or drag an entry out of the strip to
  remove it;
- drag the strip's top edge to resize it, and drag the centre divider to give
  either queue more room;
- save or load the manual queue as an `.iqpq` file from **File > Queue**;
- manual entries play first and are not changed when the library filter
  changes;
- **Automatic** is rebuilt from the cards currently visible after filtering.

![Manual and automatic play-next queues](docs/images/quickplayer-queue.jpg)

Automatic queuing is disabled when **Enforce Card Filter** is off, while the
manual queue remains available. Queue support and automatic queue length are
configured under **Settings > Playback & queue**. The queue's height and
manual/automatic split are remembered for the next launch.

The same menu can return completed manual entries to the end of the queue or
choose manual entries randomly. **Smart automatic queue** can:

- restrict the first choices to favourites;
- exclude recently played cards for a configurable cooldown, optionally
  applying that cooldown to every card from the same model;
- favour cards played least recently, favourites and higher personal ratings,
  or newer purchases;
- occasionally ignore those scores and make a random choice;
- prefer clips that have never been played; and
- avoid placing consecutive cards from the same model.

Enabled favour rules contribute to a combined score. When strict favourites
or cooldown rules leave too few cards to fill the requested automatic queue,
QuickPlayer progressively admits model-cooldown matches, non-favourites and
finally card-cooldown matches. It still respects the current card and clip
filters and stops when the available unique cards are exhausted.

### Show iStripper card overlays

QuickPlayer provides animated **New**, **Recent**, and **Favourite** overlays,
and reads iStripper's card and person overlay assignments, catalogue, and
account entitlements. Official overlays are limited to those available to the
current iStripper account, and an official assignment takes priority over every
QuickPlayer default.

Under **Settings > Library & search**:

- **Draw Card Overlays** enables or disables overlays throughout the card
  library and play-next queues.
- **DirectComposition overlays (GPU)** is enabled by default. It renders the
  model-list card scene and animated overlay surfaces through Direct3D,
  Direct2D and DirectComposition. Shared overlay frames are reused by cards
  showing the same animation. QuickPlayer falls back to its software drawing
  path if GPU initialization or rendering fails.
- **Overlay defaults** previews available overlays and assigns them to the
  newest releases, most recent purchases, cards at or above a personal-rating
  threshold, or particular card types. Rules can be reordered; the first
  matching rule wins.

### Control desktop playback

For eligible iStripper accounts, QuickPlayer can attach controls to the active
desktop animation:

- pause and resume;
- click or drag the timeline to seek;
- move backward or forward by 10% of the clip;
- restart the clip;
- select playback speeds from 0.25x to 4x;
- use configurable system-wide hotkeys while another app has focus.

> [!NOTE]
> Playback controls can take a few seconds to become available after a clip
> starts while QuickPlayer waits for its colour and alpha decoders to
> synchronize.

Modern clips use keyframe seeking and cached alpha checkpoints to keep the
colour video and its transparency mask synchronized. Legacy WMV clips are also
supported, with the compatibility notes described below.

### Fit playback into your desktop

- Make animation windows click-through with **Lock Player**.
- Create and apply a wallpaper from the current card.
- Choose wallpaper monitors, brightness, blur and desktop-icon behaviour.
- Keep QuickPlayer in the notification area with **Minimize to Tray**.
- Use either the light or dark interface.

### Backup your settings

A single `.iqpb` backup contains your settings, filters, ratings, tags,
favourites and playback history.

### Playback history

QuickPlayer records a local playback history and can avoid the 100 most recent
clips when alternatives are available. View it from
**Settings > Playback & queue > Playback History**.

![Playback history](docs/images/quickplayer-history.jpg)

## Install and get started

QuickPlayer requires 64-bit Windows and an existing iStripper installation.

1. Download the latest `IStripperQuickPlayer-*-Setup.exe` from
   [GitHub Releases](https://github.com/KittyPingu/IStripperQuickPlayer/releases/latest).
2. Run the installer, then start iStripper and QuickPlayer.
3. Select a card to see its clips.
4. Select a clip to play it, or use **Show Model**.
5. Use **File > Reload Models** after adding cards to iStripper.

The File menu also contains playlist/filter import, queue save/load, backup,
restore and update commands.

<img src="docs/images/quickplayer-file-menu.jpg" alt="QuickPlayer File menu" width="190">

## Search your cards

Search is case-insensitive and supports phrases, fields, negation, `AND`, `OR`
and parentheses.

| Search | Meaning |
| --- | --- |
| `kitty` | Find `kitty` anywhere in the indexed card details. |
| `"pool side"` | Find that exact phrase. |
| `rae AND pole` | Require both terms. |
| `(rae AND pole) OR kitty` | Return either group of matches. |
| `model:anna AND tag:duo` | Search particular fields. |
| `tag:blue AND !model:anna` | Exclude a term or field match. |

Available fields are `model`, `card`, `title`, `description` and `tag`.
Aliases such as `performer`, `name`, `show`, `outfit`, `desc` and `tags` also
work. Use explicit `AND` between separate requirements.

## Playback controls and hotkeys

Playback controls appear when:

- **Settings > Playback & queue > Enable playback control** is selected;
- iStripper reports a Platinum-or-higher account level; and
- QuickPlayer safely recognizes the decoder used by the active desktop clip.

The default hotkeys are:

| Action | Default |
| --- | --- |
| Next clip | `Ctrl+Alt+N` |
| Next card | `Ctrl+Alt+C` |
| Pause / play | `Ctrl+Alt+P` |
| Back 10% | `Ctrl+Alt+Left` |
| Forward 10% | `Ctrl+Alt+Right` |
| Restart clip | `Ctrl+Alt+Home` |
| Toggle player lock | `Ctrl+Alt+L` |

Change or disable any shortcut from
**Settings > Hotkeys**. These are global Windows hotkeys, so they continue to
work while another application has focus.

## Custom shows

QuickPlayer can turn an ordinary video containing one or more people into a
transparent custom show. Custom shows use the normal card renderer and can be
searched, sorted, filtered, favourited, rated, dragged into manual queues, and
selected by automatic queues alongside official iStripper cards. Official
shows continue to play through iStripper; custom shows use QuickPlayer's own
RGB/alpha player.

### Set up custom-show processing

Custom-show processing requires Git, FFmpeg (included), and Python 3.11–3.14.
An NVIDIA CUDA GPU is strongly recommended, but CPU/FP32 fallback is supported. Choose:

**File > Custom Shows > Install / Update Processing Tools…**

QuickPlayer creates an isolated environment under
`%LOCALAPPDATA%\IStripperQuickPlayer\rvm-runtime`, installs pinned PyTorch
2.11.0/torchvision 0.26.0 CUDA 12.8 packages, checks out the pinned
[Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) commit,
and downloads and SHA-256 verifies both official model checkpoints. Existing
compatible Python installations are reused; the setup installs Python 3.12
through Windows Package Manager only when none is available.

The setup choices dialog can also install pinned
[TransNetV2](https://github.com/soCzech/TransNetV2) scene detection and
[MatAnyone2](https://github.com/pq-yang/MatAnyone2) with
[SAM2](https://github.com/facebookresearch/sam2) interactive initial masking,
and [VideoMaMa](https://github.com/cvlab-kaist/VideoMaMa) as a high-quality
diffusion option. It can also install
[ViTMatte](https://github.com/hustvl/ViTMatte) S and B; both use the same
editable SAM2 mask sequence as a trimap for per-frame alpha refinement. Optional
[streaming ProPainter](https://github.com/osmr/propainter) powers the separate static
video-object/watermark removal tool. Every mask-guided algorithm uses the same
SAM2 base-plus model for initial selection; VideoMaMa and ViTMatte additionally
use it for editable full-clip tracking. VideoMaMa installs about 8 GB of
inference weights and requires NVIDIA CUDA.
All options reuse the same isolated
Python/PyTorch environment. Rerunning setup keeps verified source and weight
files instead of downloading them again. MatAnyone2 is non-commercial unless
its authors grant separate permission; VideoMaMa is CC BY-NC 4.0 and its model
also uses the Stability AI Community License; ProPainter is non-commercial only.
Uninstalled optional algorithms
and scene detectors are not shown in the creation and clip-editor selectors.

Open **File > Custom Shows > Settings…** to select the custom-library folder
and Python executable, then click **Validate setup**. Validation checks Python,
the available processing device, package versions, RVM/checkpoint integrity,
FFmpeg, folder write access, and a one-frame inference.

Manual setup commands and troubleshooting are in the
[complete custom-show guide](docs/custom-shows.md).

To clean a video before creating a show, open
**File > Custom Shows > Remove Video Object / Watermark...**. Choose the source,
drag a rectangle around one static area, select processing resolution and temporal
window size, then create a new MP4. QuickPlayer preserves the original and copies
its audio to the cleaned result. Fast FFmpeg delogo is the default for small static
watermarks and reports progress immediately. Optional AI High Quality ProPainter
passes frames through a bounded rolling buffer with completed-frame progress and
ETA instead of dumping them to PNG first; output uses NVENC when available. Use
this only on video you own or are authorised to modify. ProPainter is GPU-intensive;
QuickPlayer caps its temporal window to avoid shared-memory spill.

### Create and convert a show

Choose **File > Custom Shows > Create Show…**, select a source video, enter a
show title, and select or create a reusable model profile. The form also
supports descriptions, tags, dates, age, measurements, rating, hotness, clip
types, performer count, source copy/reference mode, and custom cover art.
Automatically generated covers embed the model name and show title in an
official-style bottom treatment; the creation form lets you choose the show-title colour.

Choose **Split into clips...** before processing to scrub the original video,
place dividers, drag their gold triangle handles below the timeline, and assign hotness and clip types to each
section. Choose **Fast (FFmpeg)** for lightweight scene-score detection or
**Accurate (TransNetV2)** for neural detection, then click **Auto-detect
clips**. Clear **Include this segment as a playable clip** to
keep a section out of playback. The
main form's values remain the overall card metadata. Every included section is
processed into its own RGB/alpha pair, so recurrent matting state resets at each
scene boundary. Skipped sections are not processed.

Processing options are:

- **Quality** uses RVM ResNet50; **Fast** uses the officially recommended
  MobileNetV3 model.
- **MatAnyone 2** opens a first-frame mask editor for every included clip.
  Left-click each person to include and right-click unwanted/background areas
  to refine SAM2's mask; accept each preview before conversion starts.
- **VideoMaMa High Quality** uses the same per-clip initial mask, propagates it
  through that clip with SAM2, then refines configurable frame batches with VideoMaMa.
  It is substantially slower and larger than MatAnyone2 and requires CUDA.
- **ViTMatte S / B** first propagates the selected person through the clip with
  SAM2, then uses the corrected masks as trimaps. S is lighter and faster; B is
  larger and normally produces finer mattes.
- For every SAM2-based option, review the tracked overlay before matting. Scrub,
  play, or step frame-by-frame; left-click missing foreground and right-click
  unwanted foreground. Clicks preview the corrected current frame immediately;
  **Update masks** then propagates backward and forward. **Auto mask frame** runs
  SAM2 automatic mask generation, and 0.25× slow motion is available during review.
- **Matting detail** controls RVM's internal long-edge resolution: Standard
  512 px, High 768 px, Very High 1024 px, or Full resolution. Higher settings
  can improve fine edges but take longer and use more GPU memory.
- Output resolution always matches the source's displayed resolution, apart
  from rotation normalization and reducing odd dimensions by at most one
  pixel. Matting detail does not resize the exported show.

The resizable processing dialog reports frames, elapsed time, estimated time
remaining, the current original input frame on the left, and its alpha-composited
checkerboard result on the right. Use these larger previews to cancel early when
the matte is unsuitable. QuickPlayer
prefers NVENC for the foreground and falls back to libx264. Each included clip produces:

- `clips/<clip-id>/foreground.mp4`: H.264/YUV420P with AAC source audio when present;
- `clips/<clip-id>/alpha.mkv`: near-lossless H.264 transparency (CQ/CRF 10);
- `clips/<clip-id>/initial-mask.png`: the accepted initial SAM2 mask, when used; and
- `cover.jpg`: generated once from the source midpoint unless supplied manually.

RVM's published FPS figures measure model tensor throughput only. End-to-end
conversion is slower because it also decodes and transfers full-resolution
frames and encodes both foreground and alpha streams.
QuickPlayer feeds RVM in configurable temporal chunks while preserving recurrent
state between chunks. **Processing batch size** is shared by RVM, VideoMaMa, and ViTMatte;
the default is 3 frames and the creation form offers 1, 2, 3, 4, 6, 8, or 12.
RVM automatically splits a batch and retries if GPU memory is insufficient.
MatAnyone2 is recurrent and must advance one frame at a time, so this setting
does not apply to it.

For VideoMaMa, start at 3. Smaller batches reduce peak VRAM and shared-memory
spill at the cost of more batch boundaries; larger batches can improve temporal
continuity but rapidly increase memory use. On a 16 GB RTX 4080, 12 frames at
1024×576 exhausted dedicated VRAM and spilled about 6.5 GB into shared memory,
making the first batch extremely slow. Use 1–3 on 12–16 GB cards, try 4–6 only
when Task Manager shows comfortable dedicated-VRAM headroom, and reserve 8–12
for cards with substantially more VRAM. QuickPlayer also enables feed-forward
chunking for VideoMaMa to reduce peak activation memory without changing the
matte result. Use **Fast** and
**Standard** for the best throughput.
QuickPlayer uses FP16 on CUDA compute capability 7.0 or newer and automatically
falls back to FP32 on consumer Pascal cards such as the GTX 1080 Ti. Systems
without CUDA fall back to CPU/FP32 and libx264, which is functional but substantially slower.

### Local CPU/GPU benchmark

Measured on 7 August 2026 with this machine's Ryzen 7 7800X3D, RTX 4080,
PyTorch 2.11/CUDA 12.8, 1920×1080 input, and 512px RVM matting detail:

| Algorithm | RTX 4080 | CPU only | CPU slowdown |
| --- | ---: | ---: | ---: |
| RVM Fast / MobileNetV3 | 54.6 FPS | 9.10 FPS | 6.0× |
| RVM Quality / ResNet50 | 68.3 FPS | 3.78 FPS | 18.1× |
| MatAnyone2 | 9.99 FPS | 0.314 FPS | 31.8× |

MatAnyone2 per-clip warm-up measured 1.50 seconds on CUDA and 32.88 seconds
on CPU (21.9× slower). The reproducible benchmark scripts are
`tools/custom-shows/benchmark_rvm.py` and `benchmark_matanyone2.py`. These
figures cover model inference plus QuickPlayer's tensor upload/output download,
but exclude video decoding, audio, and RGB/alpha encoding; end-to-end conversion
will therefore run below the listed FPS.

When processing finishes, preview the transparent result and choose Accept,
Retry, Open Log, or Discard. Accepted shows are published atomically beneath
`%LOCALAPPDATA%\IStripperQuickPlayer\custom-shows\shows`; failed or cancelled
jobs never appear as partial library cards. Use **Manage Models…** for shared
profiles, right-click a custom card to edit its metadata, and use **Open
Folder** to inspect or back up the custom library.

## REST API

QuickPlayer provides an authenticated local API for playback, library, and
queue integrations. See the [REST API documentation](docs/api/README.md) for
authentication, endpoints, and examples.

QuickPlayer can be started in --api-only mode, in which case it will load no UI 
and consume minimal resources

## Settings

Settings are grouped into **Playback & queue**, **Library & search** and
**Appearance**, with frequently used **Hotkeys** and **Lock Player**
kept at the top level.

<img src="docs/images/quickplayer-settings-menu.jpg" alt="QuickPlayer Settings menu" width="161">
<img src="docs/images/quickplayer-playback-queue-menu.jpg" alt="QuickPlayer Playback queue settings" width="201">

### Settings worth knowing

- **Avoid recently played clips** is enabled by default.
- **Enable play next queue** shows the collapsible manual and automatic queue
  strip; **Automatic queue length** controls how far it looks ahead.
- **Smart automatic queue** combines optional eligibility, scoring, clip and
  model-rotation rules, relaxing strict eligibility only when necessary to
  fill the queue.
- **Draw Card Overlays** and **DirectComposition overlays (GPU)** are enabled
  by default.
- **Enable alpha checkpoint cache** speeds up repeated long seeks in modern
  clips and is enabled by default.
- **Alpha checkpoint cache size** defaults to 256 MB. Reducing the limit
  removes older cache entries until the cache fits.
- **Lock Player** makes every detected animation window click-through and also
  handles animation windows created later.
- **Wallpaper** controls automatic wallpaper generation and per-monitor
  options.
- **Player size by clip type** sets independent percentages for standing,
  table, pole, swing and cage clips in small and large playback modes. A
  value of `0` leaves that size under iStripper's normal control.
- **Enable playback control** completely disables playback attachment when
  turned off.

## Playback compatibility and limitations

- QuickPlayer controls one active desktop movie. It does not synchronize
  several simultaneous performer windows.
- Controls fail closed if the running x64 `vghd.exe` cannot be matched safely.
  iStripper 2.4.0.0 is the baseline and 2.3.0.3 has also been tested. Other
  versions are scanned and validated dynamically.
- Seek controls stay disabled while a new clip's colour and alpha decoders
  synchronize. Legacy WMV clips wait for at least 3.5 seconds of playback; the
  general readiness fallback is five seconds.
- The first long seek in a modern clip can be slower while its serial
  delta-alpha state is rebuilt. Later seeks benefit from saved checkpoints.
- Many legacy WMV files have no usable ASF keyframe index. QuickPlayer must
  restart those readers at frame zero and decode forward, so a long seek can
  take longer than the file size suggests.
- Legacy WMV speed is limited to 3x because sustained 4x playback can overrun
  iStripper's older colour/alpha compositor. Modern clips retain 4x.
- Clip audio is muted when speed is not 1x. Pitch-preserving audio stretching
  is not implemented.
- Buttons and the timeline are temporarily disabled while a seek is running.
  Seeking into the final second advances to the next clip.
- Playback control uses an analysed private iStripper interface rather than an
  official Totem API. A future incompatible version may leave the controls
  unavailable until support is updated.

## Backup, restore and updates

Use **File > Backup QuickPlayer Data** to create an `.iqpb` file. The backup
does not include downloaded cards, media files, generated wallpapers or the
alpha checkpoint cache; iStripper continues to manage the card library itself.

**File > Restore QuickPlayer Data** replaces the current QuickPlayer data and
restarts the app after confirmation.

Release builds check GitHub for updates. **File > Check for Updates** can
download the matching Setup executable, verify its SHA-256 digest, launch the
installer and close QuickPlayer.

## Troubleshooting

**Cards or cover images are missing**

Use **File > Reload Models** after iStripper has finished syncing or
downloading the cards.

**The playback controls do not appear**

Check the three playback requirements above, make sure a desktop animation is
running, and allow its decoders time to synchronize.

**A first seek is slower than later seeks**

Leave the alpha checkpoint cache enabled. QuickPlayer builds checkpoints while
modern clips play and reuses them on later seeks.

**Card overlay icons**

Rating stars, favourites, hotness, and special-card markers are drawn by
QuickPlayer and do not require an icon font.

## Building from source

The application targets .NET 10 for Windows and is built for x64. The native
playback bridge also requires the Visual Studio C++ build tools.

```powershell
dotnet build .\IstripperQuickPlayer\IstripperQuickPlayer.csproj `
  -c Release -p:Platform=x64
```

See the [native playback bridge notes](native/IStripperPlaybackBridge64/README.md)
for decoder, seeking and compatibility details.

Pushes and pull requests build a self-contained installer artifact. Pushing a
tag named `vX.Y.Z` creates or updates the matching GitHub release.

---

iStripper QuickPlayer is an unofficial community project and is not affiliated
with Totem Entertainment.
