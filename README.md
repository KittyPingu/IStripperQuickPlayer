# iStripper QuickPlayer

[![Latest release](https://img.shields.io/github/v/release/KittyPingu/IStripperQuickPlayer)](https://github.com/KittyPingu/IStripperQuickPlayer/releases/latest)
[![Build installer](https://github.com/KittyPingu/IStripperQuickPlayer/actions/workflows/release.yml/badge.svg)](https://github.com/KittyPingu/IStripperQuickPlayer/actions/workflows/release.yml)
[Project website](https://kittypingu.github.io/IStripperQuickPlayer/)

A Windows companion for browsing your local iStripper library, launching the
card or clip you want, and controlling compatible desktop playback.

This project is licensed under GNU AGPL-3.0. The optional Training Studio
YOLO26 benchmark uses Ultralytics code, pretrained weights, and fine-tuned
models under Ultralytics' AGPL-3.0 terms. YOLO benchmark outputs are research
artifacts and are not installable QuickPlayer runtime models.

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

Enabled favour rules contribute to a combined score. **Favour favourites and
higher ratings** adds a newly randomized contribution whenever the automatic
queue is rebuilt. Its rating-based upper bound ranges from 0 to 1, plus 0.5
for a favourite. Unrated cards use a neutral 2.5-star rating (5 on iStripper's
internal 0-10 half-star scale). A menu slider applies a
normalized rating influence exponent from 0.25 to 4.00 before calculating the
upper bound. The normalization keeps 0, 2.5 and 5 stars fixed at the bottom,
middle and top of the rating range. At 1.00 the curve is linear; higher values
push below-average ratings down and above-average ratings up, while lower
values flatten their differences. For example at 1.00, an unrated
non-favourite contributes from 0 to 0.50, a 4-star non-favourite from 0 to
0.80, and a favourite adds 0.50 to that upper bound. This lets lower-rated
cards sometimes precede higher-rated cards while favouring the latter over
many queue rebuilds.

The total Smart Queue score is the sum of the enabled scoring rules:

| Smart Queue rule | Score contribution | How it is calculated |
| --- | ---: | --- |
| Personal rating | Random 0 to 1.00 | The normalized rating sets the random upper bound; the exponent changes the curve around 2.5 stars. |
| Favourite | Adds 0.50 to the rating/favourite random upper bound | Separate from the rating curve; a 5-star favourite therefore contributes a random 0 to 1.50. |
| Newer purchase | 0 to 1.00 | Newest candidate gets 1, oldest gets 0, and candidates between them are ranked linearly. |
| Least recently played | 0 to 1.00 | Never/least-recently played candidate gets 1, most recent gets 0, and candidates between them are ranked linearly. |
| **Maximum combined score** | **3.50** | A 5-star favourite receiving its random maximum, which is also newest and least recently played. |

For example, at exponent 1.00 a 4-star favourite has a randomized
rating/favourite contribution from 0 to 1.30. If it receives 0.90 on that
rebuild, has a newer-purchase rank score of 0.75, and a least-recent score of
1.00, its total is `0.90 + 0.75 + 1.00 = 2.65`. The highest total normally
goes first. **Chance of random choice** can instead ignore the totals for an
individual selection step.

The remaining Smart Queue controls do not add to the score: **Favourites
only** and **Cooldown** affect eligibility, **Prefer never-played clips**
chooses a clip after its card is selected, and **Avoid adjacent cards from
same model** rearranges the resulting queue.

When strict favourites or cooldown rules leave too few cards to fill the
requested automatic queue, QuickPlayer progressively admits model-cooldown
matches, non-favourites and finally card-cooldown matches. It still respects
the current card and clip filters and stops when the available unique cards
are exhausted.

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

The installer also adds the **Midnight Gallery Club** fullscreen scene to
iStripper and installs its companion video/lighting example under
`ShaderVideoStreamer` in the QuickPlayer application folder.

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
| `ethnicity:latina` | Match a model's ethnicity. |
| `hair:brunette` | Match a model's hair colour. |
| `tag:blue AND !model:anna` | Exclude a term or field match. |

Available fields are `model`, `card`, `title`, `description`, `tag`, `ethnicity`
and `hair`. Aliases such as `performer`, `name`, `show`, `outfit`, `desc`, `tags`,
`hair-color` and `hair-colour` also work. Field matching is case-insensitive and
partial, so `hair:brun` matches `Brunette`. Use explicit `AND` between separate
requirements.

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

QuickPlayer can convert an ordinary video into a transparent, first-class
library card with its own metadata, clips, RGB/alpha media, and queue behavior.
Processing can use RVM or optional mask-guided tools, with interactive clip
boundaries, per-clip metadata, SAM2 correction, and post-processing alpha tuning.

Start with the dedicated [Creating Custom Shows guide](CUSTOM_SHOWS.md). It
covers setup choices, model profiles, automatic scene detection and skipped
transition buffers, matting methods, mask correction, performance tuning,
review, playback, storage, backup, and troubleshooting. The
[technical reference](docs/custom-shows.md) contains exact commands, pinned
versions, schemas, licences, and diagnostics.

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
