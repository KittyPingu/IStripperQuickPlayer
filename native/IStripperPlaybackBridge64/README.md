# iStripper playback-engine notes

This directory contains the x64 bridge used by the original WinForms application
to control the desktop movie owned by `vghd.exe`. It is deliberately specific to
iStripper/vghd **2.4.0.0**. The private ABI described below is not a supported
Totem API and must be rediscovered before enabling another build.

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

The WinForms app already used
[`Deviare`](https://www.nektra.com/products/deviare-api-hook-windows/) to attach
to `vghd.exe`. The added path reuses that agent:

1. Check the file version and all six expected instruction signatures.
2. Load `IStripperPlaybackBridge64.dll` in the x64 vghd process.
3. Pre-hook `MovieManager::pause`, `resume`, and `setPlayRate` and capture their
   `this` pointer from x64 `RCX` when iStripper invokes one of them.
4. Call the corresponding private manager wrappers from the bridge.
5. Read the current frame, total-frame count, and FPS under the movie's Qt
   mutex to expose a content-time position to WinForms.

Relevant locations in the investigated executable (RVAs from the image base):

| Purpose | RVA / field |
| --- | ---: |
| `MovieManager::play` wrapper | `0x280CF0` |
| `MovieManager::pause` wrapper | `0x280D10` |
| `MovieManager::resume` wrapper | `0x280D30` |
| `MovieManager::setPlayRate` wrapper | `0x280F90` |
| Manager to `Movie*` | `+0x08` |
| Movie state (`3` playing, `4` paused) | `+0x4C` |
| Movie play-rate `double` | `+0x60` |
| Movie to `CAnim*` | `+0x88` |
| Movie current frame | `+0x98` |
| Movie `QMutex` | `+0xE0` |
| `CAnim` to animation info | `+0x46580` |
| `CAnim` delta-alpha position | `+0x46570` |
| Info total frames / FPS | `+0x108` / `+0x10C` |
| Info mask encoding | `+0x15C` |
| `Movie::advance` | `0x27DA20` |
| `CAnim` frame wrapper | `0x272780` |

The controls fail closed unless every signature matches. They do not guess at
offsets on an unrecognised version.

## There is no 4x playback limiter

`Movie::setPlayRate` stores the supplied `double`; it does not clamp it to 4x.
`Movie::run` multiplies the animation FPS by that rate and bottoms out at a
one-millisecond sleep. The theoretical scheduler ceiling is therefore roughly
1,000 displayed frames per second, well above the decoder's practical limit.

The installed decoder is the old dynamic FFmpeg 3-era set:

- `avcodec-57.dll` 57.54.100
- `avformat-57.dll`
- `avutil-55.dll`
- `swscale-4.dll`

`VideoFFmpeg::open` calls `avcodec_open2` at RVA `0x286805` without configuring
`AVCodecContext::thread_count`. The bridge replaces that one process-local
function-pointer slot (`0x726770`) and sets FFmpeg's `threads` option before
future codec opens. It validates that the slot still points to the exact
`avcodec_open2` export first and pins the bridge for the remaining vghd process
lifetime so the hook cannot point into an unloaded DLL.

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

The direct catch-up path now does the following while the movie is paused:

1. Lock the `VideoFFmpeg` queue and mark stale entries empty.
2. For a backward target, call the only supported decoder seek, `seek(0)`.
3. Arm a one-shot worker target. The patched load at `0x286D60` makes the
   original worker call `decodeVideo(target)` and label its queue entry with
   that same target.
4. Feed every intervening compressed packet through `avcodec_decode_video2` so
   VP9 reference-frame state stays valid. The patch at `0x28651A` skips only
   `sws_scale` for disposable intermediate frames; the requested frame still
   receives its normal colour conversion.
5. Let CAnim reconstruct its delta-RLE alpha state and compose the target.
6. Let vghd's original instruction at `Movie::advance+0xCA` (`0x27DAEA`) write
   the exact target to `Movie+0x98`.
7. Report completion only after observing that final `Movie+0x98` value.

The temporary `target - 1` value is therefore never treated as completion, and
WinForms does not maintain a separate position clock. Rewind no longer reloads
the clip through `ForceAnim`; colour decoder, mask state, active animation, and
vghd's position counter remain owned and updated by the original objects.

Mask encoding 5 is still delta RLE, so CAnim must apply intervening alpha
records serially. That work proved much cheaper than the blocked colour queue.
In a live 30 FPS test, a rewind from 1:10 to 1:00 reset the colour decoder and
rebuilt 1,818 frames in 8.95 seconds, skipping 1,818 intermediate colour
conversions and dropping 20 stale queue entries. A subsequent ten-second
forward seek decoded 279 frames in 1.47 seconds. Both operations finished with
the original `Movie+0x98` counter at the requested position.

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
- alpha checkpointing or independently decodable mask keyframes for fast
  arbitrary seeks;
- synchronized composition and a click-through transparent desktop renderer.

The bridge now implements the safe intermediate-RGB-conversion optimisation.
The next larger improvement would require building a verified SSV packet index
and seeking FFmpeg to a usable VP9 keyframe, paired with alpha checkpoints.
Either feature still has to restore CAnim's alpha state and vghd's counters
before displaying the target.

## Build

Build the solution as `x64` in Visual Studio. The C++ project writes the bridge
to the WinForms `dependencies` directory, and the WinForms post-build step
copies dependencies to its output directory. The bridge is x64-only because
the supported `vghd.exe` is x64.
