# iStripper playback-engine notes

This directory contains the x64 bridge used by the original WinForms application
to control the desktop movie owned by `vghd.exe`. The private ABI is not a
supported Totem API. Version 2.4.0.0 is the analysed baseline. Bridge v34
discovers and validates every vghd-owned function, vtable, hook site, and
object-layout field against the loaded executable rather than compiling or
loading fixed values.

The 2.4.0.0 baseline has SHA-256:

`61F70A43CEB1ECD59A9E215F91955E092A11524E67745BB25956AA9E1C9197E3`

The same resolver has also been run successfully against downgraded iStripper
2.3.0.3 (`C2C24A3DAEC4C2F2258A5B1364808D683D3A52F77F3DD9FBED4F628DFD227693`).
Its code RVAs moved while the resolver independently recovered the complete
layout.

## How the transparent desktop movie is built

The installed movie files are proprietary SSV containers: current `.vghd` files
start with `HD3`, while older/demo material uses `HD2`. Strings and control flow
in the 2.4.0.0 binary identify these components:

- `SsvFile.cpp` reads the container.
- `VideoFFmpeg.cpp` decodes the colour video and owns its media timeline.
- `Shape.cpp` contains `CShape::Apply_RLEAlphaLayer7`,
  `CShape::Apply_RLEAlphaLayer7b`, and `CShape::Apply_AlphaLayer`.
- `AlphaMix.cpp` contains `frameAlphaMix` and `frameCopy`.
- `Movie.cpp` coordinates the decoded frame and mask.
- `MovieOpenGLWidget.cpp` receives an alpha-bearing `QImage`, uploads it as an
  OpenGL texture, and renders it with blending into the transparent desktop
  overlay. `MovieRasterWidget.cpp` is the non-OpenGL fallback.

The useful mental model is therefore **one animation timeline with colour data
and a frame-indexed alpha shape**, not two independently playing videos that
have to be kept in sync:

```text
HD2/HD3 SSV container
        |
        +-- VideoFFmpeg ------ colour frame
        |        |
        |        +------------ decoded PCM --> CBpkSound --> QAudioOutput
        |
        +-- CShape ----------- RLE alpha for the same frame
                    \
                     +-- AlphaMix --> alpha-bearing QImage
                                             |
                                    MovieOpenGLWidget
                                             |
                              transparent Qt desktop window
```

This is why a safe restart or seek must reset colour-decoder and shape state
together. Moving only the FFmpeg stream or only the displayed frame index can
desynchronise the silhouette from the image.

Qt documents the underlying top-level OpenGL rendering model in
[`QOpenGLWindow`](https://doc.qt.io/qt-6/qopenglwindow.html) and the texture/FBO
composition model in
[`QOpenGLWidget`](https://doc.qt.io/qt-6.8/qopenglwidget.html). The exact
container and alpha-layer implementation above comes from local analysis of
the installed vghd binary, not from those public Qt documents.

## Playback control path

The WinForms app already uses
[`Deviare`](https://www.nektra.com/products/deviare-api-hook-windows/) to attach
to `vghd.exe`. The added path reuses that agent:

1. Load `IStripperPlaybackBridge64.dll` in the x64 vghd process.
2. Read the running executable's file version and PE identity.
3. Scan executable code and MSVC RTTI to resolve the Movie/Video functions,
   imported FFmpeg slots, patch sites, and vtables. Each candidate must be
   unique and its internal field relationships must agree.
4. Capture the active `Movie*` from the resolved `Movie::advance(this)` call
   during a clip transition, then call its pause, resume, and rate methods.
   Attaching after a clip has already started falls back to validated memory
   discovery.
5. Read the current frame, total-frame count, and FPS under the movie's Qt
   mutex to expose a content-time position to WinForms.
6. Resolve the active `VideoFFmpeg` object's `CBpkSound` and `QAudioOutput`
   ownership so pause and seek operations control the same audio stream that
   vghd opened for the clip.

The resolver produced this profile for the 2.4.0.0 baseline:

| Purpose | RVA / field |
| --- | ---: |
| `Movie::pause` / `resume` | `0x27C7D0` / `0x27C840` |
| `Movie::setPlayRate` | `0x27D720` |
| Movie vtable | `0x55AF50` |
| Movie state (`3` playing, `4` paused) | `+0x4C` |
| Movie to `CAnim*` | `+0x88` |
| Movie current frame | `+0x98` |
| Movie `QMutex` | `+0xE0` |
| `CAnim` to animation info | `+0x46580` |
| `CAnim` delta-alpha position | `+0x46570` |
| Info total frames / FPS | `+0x108` / `+0x10C` |
| `Movie::advance` | `0x27DA20` |
| `CAnim` frame wrapper | `0x272780` |
| `CBpkSound` vtable / close helper | `0x55C558` / `0x281FB0` |
| `VideoFFmpeg` / `VideoWmvCore` to `CBpkSound*` | `+0x28` |
| `CBpkSound` to `QAudioOutput*` | `+0x10` |
| `CBpkSound` to output `QIODevice*` | `+0x18` |
| `CBpkSound` pending `QByteArray` | `+0x20` |

Nothing is read back from the INI as trusted configuration. On every vghd
process start, the bridge derives all vghd-owned code and object fields from
instruction relationships and RTTI, cross-checks their layout invariants, and
then writes the result to:

`%LOCALAPPDATA%\IStripperQuickPlayer\vghd-offsets.ini`

Each section is keyed by the actual iStripper file version, PE timestamp, and
image size, for example
`[vghd_2.3.0.3_6A463E88_00760000]`. It includes resolver-stage masks so a
future incompatible build still leaves a useful partial diagnostic. An edited,
stale, or copied INI cannot supply an address to the bridge. A changed or
ambiguous image is rescanned and fails closed instead of calling an unverified
address.

The only pinned structure fields belong to the separately version-checked
FFmpeg 3.1 ABI: the `AVFormatContext` stream count/list, the `AVStream` codec
context and index entries/count, and the `AVCodecContext` media type. Per the
current compatibility policy, those remain valid while `avformat_version()` is
exactly 57.47.101. They are still named and written to the diagnostic INI.
All CAnim, SSV, Movie, VideoFFmpeg, VideoWmvCore, queue-container, mutex,
sound-object, counter, import-slot, and hook-site fields are derived
dynamically.

`IStripperDiscoverMovie` is the compatibility fallback. It scans committed
private writable regions for the resolved Movie vtable, then validates the
playing/paused state, animation pointer, frame range, FPS, and active
video-decoder vtable before accepting a candidate. That broad scan can take
seconds because video and frame buffers are writable private memory too.

Normal clip changes use the verified `Movie::advance` capture hook, with
validated memory discovery retained as the late-attach fallback. Seeking stays
disabled until the native decoder reports synchronized state: FFmpeg must have
an initialized frame queue, repeated progress in the complete restorable alpha
state, and a captured checkpoint; legacy WMV must repeatedly advance while
both colour and alpha queues remain populated.

## There is no 4x playback limiter

`Movie::setPlayRate` stores the supplied `double`; it does not clamp it to 4x.
`Movie::run` multiplies the animation FPS by that rate and bottoms out at a
one-millisecond sleep. The theoretical scheduler ceiling is therefore roughly
1,000 displayed frames per second, well above the decoder's practical limit.

The installed decoder is the old dynamic FFmpeg 3-era set:

- `avcodec-57.dll` 57.54.100
- `avformat-57.dll` 57.47.101
- `avutil-55.dll`
- `swscale-4.dll`

`VideoFFmpeg::open` calls `avcodec_open2` without configuring
`AVCodecContext::thread_count`. The bridge finds the process-local import slot
that points to the exact `avcodec_open2` export and sets FFmpeg's `threads`
option before future codec opens. It pins the bridge for the remaining vghd
process lifetime so the hook cannot point into an unloaded DLL.

This improves clips whose VP9 bitstream supports useful frame threading, but it
cannot make all work parallel. `VideoFFmpeg::decodeVideo` (baseline
`0x287490`) must
still feed dependent packets through `avcodec_decode_video2`, and the alpha
decoder remains serial.

## Seek behaviour and position-counter synchronisation

`CAnim::seek(int)` delegates to `VideoFFmpeg::seek(int)`, but this vghd build
rejects every non-zero frame. Only `seek(0)` reaches FFmpeg's
`av_seek_frame`. The adjacent public manager operation initially suspected to
be a position setter is identified by vghd's Qt metadata as
`MovieManager::setPrefinishMark`; it schedules a one-shot near-end event and is
not safe for seeking.

Older cards use `VideoWmvCore` instead of `VideoFFmpeg`. Its virtual seek
operation returns false, and changing only `Movie::setPlayRate` starves its
bounded sample queues. Bridge v15 bypasses that wrapper and controls the
existing Windows Media asynchronous reader owned by `CSsvReader`.

For a legacy seek, the bridge:

1. Pauses the Movie, gates new sample callbacks, and stops `IWMReader`,
   waiting for vghd's status event.
2. Clears `CBpkSound`'s pending PCM and restarts its `QAudioOutput` while the
   sample callback is stopped, then leaves the fresh output suspended until
   Movie resumes at the target.
3. Calls vghd's queue-clear helper so all five colour/alpha sample queues are
   discarded together.
4. Resets the reader's sample counter to the target frame and starts
   `IWMReader` at the corresponding 100-nanosecond media time.
5. Leaves `IWMReaderAdvanced` on its normal reader clock. Earlier builds
   switched every seek to the user-provided clock and raced vghd's
   `WMT_STARTED` callback: its final `DeliverTime(0)` could overwrite the
   bridge's later horizon and strand the bounded queues.
6. Waits until both completed colour and alpha queues contain data. A
   colour-only restart is not exposed to `Movie`, because doing so can display
   an unpaired mask or leave the scheduler waiting on the next sample.
7. Sets `Movie.currentFrame` to `target - 1` and resumes the original advance
   path. vghd's own `Movie::advance` publishes the target position. The bridge
   does not report completion to QuickPlayer until two further frames have
   advanced, so the timer and playback controls remain disabled until the
   restarted reader is demonstrably moving.

Legacy speed changes use that same user-clock mode to keep the synchronized
queues supplied, while `Movie::setPlayRate` controls presentation speed.
Subsequent changes between 0.25x and 4x only update the Movie scheduler and do
not restart the reader. The reader itself remains at 1x because its non-1
`Start` rate is unsupported for these files.

The WMV path is resolved from the current executable rather than compiled
addresses. For the investigated image the diagnostic profile contains:

| `VideoWmvCore` / `CSsvReader` item | RVA / field |
| --- | ---: |
| `VideoWmvCore` vtable | `0x555C50` |
| `CSsvReader` vtable | `0x5559F8` |
| clear all sample queues | `0x268740` |
| peek colour queue | `0x2686F0` |
| `VideoWmvCore` to `CSsvReader*` | `+0x38` |
| `CSsvReader` to `IWMReader*` / `IWMReaderAdvanced*` | `+0x40` / `+0x48` |
| status event / last callback result | `+0x30` / `+0x38` |
| sample counter / paused flag | `+0x18` / `+0x10` |
| shared queue mutex | `+0x68` |
| completed colour / alpha queues | `+0x80` / `+0x90` |

The resolver derives those vtables, functions, COM-interface fields, event,
counter, and queue fields from RTTI and verified instruction relationships on
each vghd start. They are written to the per-image INI section for diagnostics;
failure to resolve a unique, internally consistent path disables the bridge.

There is a second, private route: `Movie::advance` normally calls the CAnim
frame wrapper with `Movie.currentFrame + 1`, and that wrapper accepts a larger
absolute target. Simply priming `Movie.currentFrame` to `target - 1` did not
work, however. The fundamental blocker was `VideoFFmpeg`'s bounded producer
queue, not a 4x limit or raw VP9 throughput:

| `VideoFFmpeg` item | RVA / field |
| --- | ---: |
| vtable | `0x55CD30` |
| decoder worker `run` | `0x286D30` |
| worker's next-frame load | `0x286D60` |
| exact-frame queue getter | `0x287360` |
| `seek` | `0x286F70` |
| `decodeVideo` | `0x287490` |
| compressed-frame helper call | `0x2874FB` |
| `sws_scale` call | `0x28651A` |
| frame queue / queue mutex | `+0x58` / `+0x60` |
| next decoder frame | `+0x78` |

The getter searches for the exact requested frame but does not discard earlier
entries. When CAnim suddenly asks for a distant target, the 20-slot queue is
already full of ordinary sequential frames. The worker cannot enqueue more,
while CAnim waits for a target that the worker can never reach. The apparent
"slow decode" and timeouts were mostly this pipeline stall.

The direct seek path now does the following while the movie is paused:

1. Lock the `VideoFFmpeg` queue and mark stale entries empty.
2. For a backward or sufficiently distant forward target, read FFmpeg's
   populated `AVStream` index, choose the nearest indexed VP9 keyframe at or
   before the target, call
   `av_seek_frame(..., AVSEEK_FLAG_BACKWARD)` directly, and flush the codec.
   This bypasses vghd's frame-zero-only `VideoFFmpeg::seek` wrapper; no separate
   SSV packet index is needed. Nearby forward targets continue from the current
   decoder position.
3. Restore the nearest earlier alpha checkpoint for the active CAnim, or reset
   its alpha state to frame zero when no checkpoint exists. QuickPlayer captures
   the complete mutable alpha block and output plane every five seconds during
   playback. Checkpoints are scoped to one animation and capped at 128 MiB.
4. Arm a one-shot worker target. The dynamically resolved worker load
   (`0x286D60` in the baseline build) makes the original worker call
   `decodeVideo(target)` and label its queue entry with that same target.
5. Feed compressed packets from that keyframe through `avcodec_decode_video2`
   so VP9 reference-frame state stays valid. The resolved scaler call
   (`0x28651A` in the baseline build) skips `sws_scale` only for disposable
   intermediate frames; the requested frame still receives its normal colour
   conversion.
6. Let CAnim compose the reconstructed alpha state with the target colour frame.
7. Let vghd's original `Movie::advance` instruction write the exact target to
   the dynamically discovered current-frame field (`Movie+0x98` in the
   baseline).
8. Report completion only after observing that final current-frame value.
9. Suspend the clip's `QAudioOutput` and suppress `CBpkSound::write` while
   rapidly rebuilding VP9 references. Before restarting the output, clear
   `CBpkSound`'s internal pending `QByteArray` as well as Qt's output queue;
   otherwise pre-seek PCM can be written after the target and make sound lag or
   distort. The guarded write hook stores the newly returned `QIODevice*` back
   into `CBpkSound` and discards catch-up PCM. Normal writes resume only after
   the target colour and alpha frame has been published.

The temporary `target - 1` value is therefore never treated as completion, and
WinForms does not maintain a separate position clock. Rewind no longer reloads
the clip through `ForceAnim`; colour decoder, mask state, active animation, and
vghd's position counter remain owned and updated by the original objects.
The index reader is also fail-closed: it requires avformat 57.47.101, the
verified FFmpeg 3.1 `AVStream` layout (`index_entries` at `+0x1C8`, count at
`+0x1D0`), one entry per animation frame, and a keyframe at frame zero.

Delta-RLE masks still require CAnim to apply records between the restored
checkpoint and the requested target. The cache is populated opportunistically,
so an unvisited part of a clip still falls back to frame zero. Each operation
finishes with the original `Movie+0x98` counter at the requested position.

`Movie::pause` and `Movie::resume` do not control `CBpkSound` themselves, so
bridge v21 suspends and resumes the associated `QAudioOutput` with the Movie.
`Movie::setPlayRate` likewise changes only the picture scheduler. The bridge's
guarded PCM hook linearly resamples each decoded stereo block to the selected
duration, preserving audio/video timing and decoder back-pressure from 0.25x
through 4x. This changes pitch rather than performing time-stretching. Each
rate transition restarts the output and refreshes `CBpkSound`'s device pointer
so already queued samples from the previous rate cannot drift into the new
timeline.

## Why the bridge does not clone vghd's decoder

The colour decoder is already standard FFmpeg/libvpx. Replacing all of
`VideoFFmpeg` would not remove VP9 frame dependencies, and copying decompiled
proprietary implementation is unnecessary. The small injected shim changes the
missing FFmpeg thread configuration and uses verified vghd entry points while
leaving container ownership, audio, mask state, and rendering with vghd.

A genuinely independent, clean-room player would need all of the following:

- an HD2/HD3 SSV demuxer and timing/index parser;
- FFmpeg/libvpx colour and audio decode;
- every RLE alpha variant plus shape metadata;
- independently decodable mask keyframes for cold seeks without a checkpoint;
- synchronized composition and a click-through transparent desktop renderer.

The bridge implements intermediate-RGB-conversion skipping, indexed VP9
keyframe seeking, bounded per-animation alpha checkpoints, synchronized
FFmpeg audio flushing, and synchronized legacy WMV reader restarts. A modern
checkpoint restores the output plane and the complete mutable CAnim alpha block
before composition. Capture and seek wait for the existing `Movie::advance`
hook to report no frame in flight, then lock and recheck the Movie mutex;
both decoder paths finish by letting vghd's own `Movie::advance` publish the
final position.

## Player locking

Lock Player no longer hooks every `CallWindowProcW` call in vghd. Bridge v19
subclasses only vghd's Qt movie windows, returns `HTTRANSPARENT` for
`WM_NCHITTEST`, and watches through window location changes so late-initialized
and additional performer windows are captured. The subclass remains installed
for that vghd process and unlock only disables its hit-test override; this
avoids dismantling and rebuilding a Qt-managed window-procedure chain.
The WinEvent hook runs on a dedicated message-loop thread rather than the
short-lived injection thread. The bridge is pinned for the remaining vghd
process lifetime before installing any process-local window or decoder hook.

## Build

Build the solution as `x64` in Visual Studio. The C++ project writes only the
bridge to the WinForms `dependencies` directory; the WinForms post-build step
copies dependencies to its output directory. The runtime creates its diagnostic
INI under `%LOCALAPPDATA%\IStripperQuickPlayer`. The bridge is x64-only because
`vghd.exe` is x64.
