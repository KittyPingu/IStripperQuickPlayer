# iStripper playback-engine notes

This directory contains the x64 bridge used by the original WinForms application
to control the desktop movie owned by `vghd.exe`. The private ABI is not a
supported Totem API. Version 2.4.0.0 is the analysed Qt5 baseline. Bridge v145
discovers and validates every vghd-owned function, vtable, hook site, and
object-layout field against the loaded executable rather than compiling or
loading fixed values.

The 2.4.0.0 baseline has SHA-256:

`61F70A43CEB1ECD59A9E215F91955E092A11524E67745BB25956AA9E1C9197E3`

The same resolver has also been run successfully against downgraded iStripper
2.3.0.3 (`C2C24A3DAEC4C2F2258A5B1364808D683D3A52F77F3DD9FBED4F628DFD227693`).
Its code RVAs moved while the resolver independently recovered the complete
layout.

## Desktop coordination

QuickPlayer reduces desktop playback through `DesktopPlaybackCoordinator`.
Requests reserve queue entries; native confirmation consumes them. Registry
notifications are asynchronous observations and cannot confirm playback or block
the host on a WinForms decision. `Movie::playing()` identifies playback instances;
`LiveActor::clipEndedNaturally()` applies the prepared selection locally at the
natural boundary. The bridge validates these signal names against the executable
metadata before enabling capture, using the installed Qt5 or Qt6 exports.

`IStripperBeginDesktopAttachment` clears selections and request correlation for a
new client. `IStripperPrepareDesktopSelection` and
`IStripperRequestDesktopSelection` accept the bounded packets in `DesktopPlayback.h`.
`IStripperGetDesktopSnapshot` returns playback, request correlation, readiness and
placement together. Clip identity comes from the decoded HD2/HD3 animation header;
validated Qt string layouts are a fallback. Window placement is unknown while
there is no uniquely associated visible movie window. Coordinates are physical
desktop coordinates, including negative origins, with the window's actual DPI.
Resizing and display recovery use iStripper's own actor methods to retain its pose
anchor. No timer restores saved coordinates during dragging.

The client gives ordinary commands a two-second budget, including command queue
waits. Seek preparation retains a thirty-second transport budget and the existing
explicit operation deadline. A transport timeout discards the connection and
never resends the uncertain command. Native frame capture handles attachment and
normal transitions. A bounded recovery scan is attempted only if late attachment
has no captured clip. Existing validated movies bypass discovery, and fallback
scans have a one-second budget.

Run the Release application with `--verify-desktop-playback` for reducer and
stalled-pipe checks. `--verify-desktop-live` injects the current bridge and exercises
same-clip replay, pause/resume, and a prepared natural transition; it changes live
playback and temporarily uses 4x speed, then restores 1x. Restart iStripper to
replace a pinned bridge before running this check.

The October 2026 v134 verification passed against installed Qt6 iStripper 2.5.0.0,
including a natural transition and a 144-DPI monitor snapshot. Synthetic Qt5 and
Qt6 container/string checks pass. No Qt5 binary was available for this run;
historical Qt5 resolver results below are not a live acceptance pass for v135.
Dragging/hanging poses, display removal, and comparative CPU/UI latency benchmarks
still require desktop acceptance before publishing.

The v135 discovery regression and running-app status checks passed without
reconnection. Its accelerated completion check on a 348-second clip did not
observe a transition within the 90-second verification budget.

## Qt6 compatibility

iStripper 2.5.0 uses Qt6. The bridge selects separate validated instruction
patterns for its movie, decoder, WMV, and fullscreen layouts while retaining
the Qt5 resolver for 2.4.x. Qt6 strings and arrays contain three words; lists
of strings, decoder frames, and fullscreen queue records store values inline.
Fullscreen cards use Qt shared ownership. Their control blocks use the host's
allocator and deallocator, and the bridge stays pinned while its deleter can
be called. Movie locks use Qt6 `QRecursiveMutex`; decoder queues use
`QBasicMutex`. PCM output uses `QAudioSink`, including its public volume
method. Dressing Room URL capture covers both loaded FFmpeg 57 and 61 engines.
Fullscreen activity comes from the live Qt6 running property rather than its persisted
desktop mode. The Qt5 path retains the registry mode; older state packets
without an activity field also retain that fallback in the API.
Qt6 RLE7 masks are decoded independently and leave the delta decoder's counters
at zero. Seek readiness observes rendered frame progress for that encoding;
delta masks and the Qt5 path keep their existing decoder progress checks.

The newer 2.4.0.0 Qt5 executable also uses shared fullscreen cards. The resolver
detects that ownership independently of the Qt major version, retaining the
older Qt5 raw-card path and Qt5 QList storage. Pending fullscreen replacements
are retained per node so requests for different slots can be issued together.
REST pause and relative seeks call the same playback helpers as the UI, including
when QuickPlayer is hidden. REST status recognizes active fullscreen clips;
individual clip identities are available through the fullscreen endpoint.

Build the isolated compatibility test with `VerifyCompatibility=true` on the
native project, using the same Release/x64 settings and Windows SDK override
as the bridge. Run `obj\verification\PlaybackBridgeCompatibilityTests.exe`
with the absolute path to the installed `vghd.exe`. It maps the executable
without running it, validates both list layouts, exercises the installed Qt
string/array exports, resolves playback/audio/seek/fullscreen layouts, and
executes the Qt6 seek thunks against synthetic frames. It does not inject
into the running iStripper process. Exit code zero is required. Run it again
after switching installations to verify the actual 2.4.x binary as well.

Live verification remains separate: restart iStripper to unload a previously
pinned bridge before replacing the DLL, then test playback, seeking, audio,
fullscreen slot replacement and queue edits through QuickPlayer and its API.

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

QuickPlayer uses the Windows remote-thread API to load the bridge into
`vghd.exe`. A bounded named-pipe channel invokes its exported controls.
[MinHook](https://github.com/TsudaKageyu/minhook) intercepts the three registry
values QuickPlayer observes, dynamically resolved fullscreen lifecycle
functions, and Qt's exported OpenGL shader-program bind method. Registry
callbacks fail open after two seconds if QuickPlayer is unavailable, so the
injected bridge cannot leave iStripper waiting on the UI process.

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

Fullscreen shader data and textures do not use a fixed vghd RVA or object
offset. At startup the bridge resolves `QOpenGLShaderProgram::bind`,
`uniformLocation`, and `setUniformValue` from the loaded `Qt5Gui.dll` or
`Qt6OpenGL.dll` export
table, and resolves core texture calls from `opengl32.dll`. The current
context's `glActiveTexture` entry point is obtained dynamically through
`wglGetProcAddress` on the render thread. The bind hook sets
`u_QuickPlayerData`, `u_QuickPlayerSequence`, the clip-bounds capture/publish
uniforms, and the named
`u_QuickPlayerTexture_<name>`, `u_QuickPlayerTextureSize_<name>`, and
`u_QuickPlayerTextureSequence_<name>` uniforms only for linked programs that
declare them. Texture objects and uploads are context-specific and run only on
the OpenGL render thread. Missing or incompatible Qt/OpenGL exports make
fullscreen hook setup fail closed.

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
its completed colour queue remains populated.

## There is no 4x playback limiter

`Movie::setPlayRate` stores the supplied `double`; it does not clamp it to 4x.
`Movie::run` multiplies the animation FPS by that rate and bottoms out at a
one-millisecond sleep. The theoretical scheduler ceiling is therefore roughly
1,000 displayed frames per second, well above the decoder's practical limit.
QuickPlayer rejects 4x only for legacy WMV: sustained 4x playback can overrun
vghd's old colour/alpha compositor and cause an access violation. Modern
FFmpeg clips retain the 4x option.

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
bounded sample queues. The bridge bypasses that wrapper and controls the
existing Windows Media asynchronous reader owned by `CSsvReader`.

For a legacy seek, the bridge:

1. Pauses the Movie and stops `IWMReader`, waiting for vghd's status event.
2. Guards the WMV `OnSample` raw-PCM write call so catch-up audio is discarded.
   At the target hand-off it clears `CBpkSound`'s pending PCM. WMV retains its
   existing `QAudioOutput` and device because stopping and restarting that Qt
   object from the WMF callback thread changes its configured buffer size and
   prevents PCM from draining at the source rate.
3. Calls vghd's queue-clear helper so all five colour/audio sample queues are
   discarded together, then clears `VideoWmvCore`'s dynamically resolved
   held-frame flag. Otherwise its next `getFrame` discards the first new colour
   frame while `CAnim` applies the target frame's SSV mask.
4. Restarts the reader at frame zero under `IWMReaderAdvanced`'s user-provided
   clock. Many legacy containers are not indexed: a non-zero `IWMReader::Start`
   can report success but produce no further colour samples.
5. Waits for vghd's `WMT_STARTED` callback to finish, then delivers only
   through the target plus two verification frames. `VideoWmvCore::getFrame`
   drains decoded predecessors through vghd's normal bounded-queue path without
   letting the asynchronous reader decode the rest of the clip into memory.
6. Waits until the completed colour queue contains data. Audio is optional and
   is consumed independently; requiring its queue caused silent clips never to
   become seekable and could strand audio clips during restart.
7. Sets `Movie.currentFrame` to `target - 1` and resumes the original advance
   path. vghd's own `Movie::advance` publishes the target position. After two
   further frames advance, the bridge calls vghd's resolved destructor for all
   five catch-up sample queues and clears `VideoWmvCore`'s held-frame flag
   before the hand-off. At normal speed, a pinned native worker then restarts
   the same reader at the target with its user clock disabled, returning colour
   and audio to WMF's normal real-time pacing before audio is released. Faster
   playback keeps at least 250 ms of wall-clock headroom by growing the
   user-clock horizon with the selected rate, while audio remains muted. The
   final partial delivery bypasses batching and advances the reader clock one
   second past nominal duration so legacy ASF files emit EOF. The timer and
   playback controls remain disabled until that hand-off completes.

Legacy speed changes use that same user-clock mode to keep the synchronized
queues supplied, while `Movie::setPlayRate` controls presentation speed.
Subsequent changes between 0.25x and 3x only update the Movie scheduler and do
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
| `VideoWmvCore` held-frame flag | `+0x40` |
| `CSsvReader` to `IWMReader*` / `IWMReaderAdvanced*` | `+0x40` / `+0x48` |
| status event / last callback result | `+0x30` / `+0x38` |
| sample counter / paused flag | `+0x18` / `+0x10` |
| shared queue mutex | `+0x68` |
| completed colour / audio queues | `+0x80` / `+0x90` |

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
   playback. Checkpoints are scoped to one animation and capped at 128 MiB in
   memory. When **Enable alpha checkpoint cache** is selected, bridge v38+
   normalizes the two embedded scratch pointers, compresses each checkpoint
   with Windows XPRESS Huffman on a thread-pool worker, and writes it atomically
   under `%LOCALAPPDATA%\IStripperQuickPlayer\alpha-cache`.
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
checkpoint and the requested target. Persistent checkpoints are loaded on
demand for later instances of the same clip and the disk cache discards its
oldest files above the configured limit (256 MiB by default). The cache is
populated opportunistically, so a
never-visited part of a clip still falls back to frame zero. Each operation
finishes with the original `Movie+0x98` counter at the requested position.

`Movie::pause` and `Movie::resume` do not control `CBpkSound` themselves, so
bridge v21 suspends and resumes the associated `QAudioOutput` with the Movie.
`Movie::setPlayRate` likewise changes only the picture scheduler. The bridge
therefore suppresses PCM writes and suspends the output whenever playback is
not 1x. Returning to 1x restarts the output, refreshes `CBpkSound`'s device
pointer, and discards samples queued at the previous timeline position.

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

The v136 regression confirms that an outgoing movie frame cannot replace a movie
already confirmed by Movie::playing. Confirmation disarms attachment capture;
the capture and confirmation paths serialize ownership changes under the desktop
lock. This prevents input hit-testing from borrowing an outgoing alpha mask.

The v137 regression checks native opaque/transparent window hit-testing without
a decoder. Unlocked standard rendering uses Windows' layered-window hit-test;
HDR and locked rendering retain decoded-alpha handling. The watcher uses physical
coordinates on mixed-DPI displays. Managed custom-player checks reproduce a DPI
message shrinking a configured player to its startup bounds and verify that DPI
updates retain the physical video bounds. Custom handoffs use the last validated
native placement when no custom placement exists. Live gesture and custom-video
acceptance for these fixes remains pending. The live v137 input check failed when
Qt recreated its native HWND with `WS_EX_TRANSPARENT` while unlocked. The v138
fixture reproduces that case; standard unlocked hit-testing clears this style
before asking User32 to test the layered alpha. A reported native clip temporarily
disappeared and reappeared after a transition; that rendering symptom remains
unresolved.

The v139 hit-test only changes a candidate window containing the physical pointer;
the fixture uses the watcher's per-monitor DPI context. The live custom handoff
recorded 144-to-288 DPI with a stale 22-by-22 suggested rectangle and retained its
configured 1425-by-802 bounds after the message. Subsequent unlocked gesture checks
and the transient native disappearance still need live verification.

The custom-to-native v139 reproduction restored an 8114-by-4284 native HWND;
the existing size hotkeys recalculated it to normal geometry. The v140 live
repetitions showed that forcing `doLarge`/`doSmall` can change playback mode and
does not consistently repair geometry. V141 synchronizes the desktop mode only
when needed, then invokes `updateHeightWithoutAnimation` before configured
sizing. The method is validated against the loaded Qt5/Qt6 meta-object, and the
installed Qt6 compatibility fixture confirms it. No saved coordinates or placement
timer are added. Restoration runs in the background with the ordinary transport
budget; superseded attachments/custom handoffs skip follow-up sizing. The v141
desktop repetition was blocked by active fullscreen playback, so live handoff
acceptance remains pending.

### Desktop startup recovery (v143)

Attachment preserves a validated active movie when resetting transient controls.
Late-attach discovery retains its address cursor across one-second scan budgets;
failed attempts back off from two seconds to thirty seconds and stop after a
confirmed identity. Normal transitions do not trigger these scans. Compatibility
pattern searches use `memchr` to skip nonmatching prefixes without changing the
Qt5/Qt6 validation rules.

QuickPlayer creates the event pipe before scheduling its reader, and cancellation
closes the pipe even if the thread pool has not scheduled that reader. Startup no
longer prefetches Dressing Rooms data: that optional native-object scan exceeded
the two-second command budget and caused repeated disconnects. Explicit Dressing
Rooms requests retain their on-demand discovery.

The October 10 live Qt6 cold-attachment check detected an already-playing clip
about 5.4 seconds after QuickPlayer launch (about 0.8 seconds after its first REST
response), with one attachment, versus about 15.8 seconds with repeated
attachments before removing the prefetch. Natural completion selected the
prepared clip and confirmed its queue entry; playback speed was restored to 1x.
These are local observations, not a controlled CPU or latency benchmark. Offline
checks cover stalled thread-pool startup, pipe reuse, invalid versus retained
movies, scan resumption, and pattern boundaries. Live Qt5 verification remains
unavailable.


## Prompt desktop handoff (v144)

At natural completion, the bridge writes the already prepared `ForceAnim` request
and queues `Live::checkForcedActor()` on iStripper's own thread. This wakes its
forced-playback consumer without waiting for the next timer or re-entering the
finishing actor. The Live singleton is resolved while preparing the selection,
so completion does not scan memory or wait for QuickPlayer. Missing Qt exports
or an invalid cached singleton retain the existing registry/timer fallback.
Late preparations for an already completed instance are dispatched immediately,
rather than being accepted for a boundary that has already passed. Confirmed
snapshots are published before optional checkpoint and speed commands.
Duplicate completion signals are ignored per playback instance. Queue entries
still require movie confirmation before consumption.

The installed Qt6 fixture checks the forced-playback slot and queued invocation
export, and the native regression checks duplicate completion. Live timing is
measured from cached REST status, so it bounds observable gaps rather than
measuring the first rendered frame. Qt5 uses the same legacy QGenericArgument
queued-invocation ABI; a live Qt5 run remains unavailable on this machine.

Final v144 live acceptance covered native-to-custom and native-to-native natural
completion, with 4x speed used to shorten playback and restored to 1x afterward.
Both started the exact prepared QuickPlayer item, updated the active queue card,
and showed no blank state in approximately 50 ms REST polling. Attachment stayed
at 1 for the native handoff. An earlier intermediate build showed an approximately
8 s gap, so these two samples do not establish a maximum load latency for all
clips. Release build, desktop/transport, controls, API options, custom-show and
installed Qt6 native compatibility checks passed; `git diff --check` passed.


## Early seek and alpha hit detection (v145)

Desktop seeking rechecks current native decoder readiness before pausing. Native
preparation also rejects an unready movie before taking its mutex, and rechecks
while holding it before modifying decoder or audio state. The legacy rate-scan
fallback now applies only to the FFmpeg unsupported-operation result; readiness
and decoder failures are surfaced instead of triggering a scan.

Unlocked hit detection no longer treats `WindowFromPoint` as proof of an opaque
model pixel. The bridge publishes an immutable alpha mask, up to 256 by 256,
from the native render/advance callbacks, once per movie frame. It uses a zero-wait
movie lock while producing the mask. The mouse hook reads the latest mask without
taking that lock. Cache identity includes movie and animation, and new playback
clears the mask. Physical window bounds map the mirrored alpha plane to desktop
coordinates; positions outside the window are excluded. The compact mask retains
the existing alpha threshold of 128; fine edges have its sampling resolution.

Regression checks cover transparent pixels inside a fully opaque layered window,
mirrored coordinates, negative desktop origins, outside bounds, missing masks,
unready native preparation and permitted legacy fallback results. The synthetic
movie fixture now allocates through the resolved state member as well as the
animation, frame and mutex members.

Live Qt6 checks rejected a seek at clip elapsed 133 ms while unready, then completed
a ready 40 s seek in approximately 300 ms with subsequent playback progress. A
ready seek to 60 s on a 222 s clip completed in approximately 117 ms. Non-invasive
inspection confirmed a populated mask at the current movie frame. These tests
exercise the shared seek path through REST; direct timebar and desktop pointer
interaction await user verification. Live Qt5 remains unavailable.
