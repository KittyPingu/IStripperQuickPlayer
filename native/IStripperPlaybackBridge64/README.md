# iStripper playback-engine notes

This directory contains the x64 bridge used by the original WinForms application
to control the desktop movie owned by `vghd.exe`. The private ABI is not a
supported Totem API. Version 2.4.0.0 is the analysed baseline, but callable
addresses are now discovered and validated against the loaded executable rather
than compiled as fixed RVAs.

The build investigated here has SHA-256:

`61F70A43CEB1ECD59A9E215F91955E092A11524E67745BB25956AA9E1C9197E3`

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

1. Load the structural layout from `vghd-offsets.ini`.
2. Load `IStripperPlaybackBridge64.dll` in the x64 vghd process.
3. Scan executable code and MSVC RTTI to resolve the Movie/Video functions,
   imported FFmpeg slots, patch sites, and vtables. Each candidate must be
   unique and its internal field relationships must agree.
4. Locate the active `Movie*` directly and call its resolved pause, resume, and
   rate methods. No per-frame or input-simulation capture hook is required.
5. Read the current frame, total-frame count, and FPS under the movie's Qt
   mutex to expose a content-time position to WinForms.

The resolver produced this profile for the investigated executable:

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
| Info mask encoding | `+0x15C` |
| `Movie::advance` | `0x27DA20` |
| `CAnim` frame wrapper | `0x272780` |

Function RVAs are not read back as trusted configuration. They are rediscovered
on every vghd process start, then written under an
`[image_<PE timestamp>_<image size>]` section for diagnostics. The `[layout]`
section contains structural CAnim/SSV offsets that cannot be identified from a
single safe signature. The resolver cross-checks all code-derived Movie and
Video offsets and live objects before use. A changed or ambiguous image fails
closed instead of calling an unverified address.

Direct Movie discovery scans committed private writable regions once for the
resolved Movie vtable, then validates the playing/paused state,
animation pointer, frame range, FPS, and `VideoFFmpeg` vtable before accepting a
candidate. It makes no persistent code change and avoids blocking vghd's
per-frame thread while QuickPlayer is loading.

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
cannot make all work parallel. `VideoFFmpeg::decodeVideo` (`0x287490`) must
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
operation returns false, and `Movie::setPlayRate` does not change its timeline
rate. Bridge v14 still discovers the shared `Movie`/`CAnim` object so the timer
and pause/play work, but reports the decoder kind to QuickPlayer so WMV seek
and speed controls are disabled instead of starting a repeated heap scan or a
CPU-heavy real-time fallback.

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

The bridge now implements intermediate-RGB-conversion skipping, indexed VP9
keyframe seeking, and bounded per-animation alpha checkpoints. A checkpoint
restores the output plane and the complete mutable CAnim alpha block before
composition; the final position is still published by vghd's own
`Movie::advance` counter update.

## Build

Build the solution as `x64` in Visual Studio. The C++ project writes the bridge
and the tracked `vghd-offsets.ini` profile to the WinForms `dependencies`
directory, and the WinForms post-build step copies dependencies to its output
directory. The bridge is x64-only because `vghd.exe` is x64.
