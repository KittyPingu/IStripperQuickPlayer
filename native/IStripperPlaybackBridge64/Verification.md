# Qt5 / Qt6 compatibility verification

Target: iStripper 2.5.0.0 on Windows x64. Bridge 128 was published and its
alpha readiness/checkpoint change passed live verification. Bridge 129 is
published and its fullscreen diagnostic tree returns all three active nodes.
Bridge 132 is published and has passed installed 2.4.0.0 checks below.
The final 2.5.0 regression on this build remains pending. This is a coverage record,
not a claim that every public function has completed end-to-end testing.

## Completed live checks

- Desktop VP9 and legacy WMV: exact clip playback, pause/resume, forward and
  backward seeks, playback speed changes and restoration, next clip.
- Player bridge: volume, lock, click through, wheel-resize configuration,
  large/small player requests. QuickPlayer UI pause/resume, +/-10% seeks,
  speed selection and next clip matched the live API state.
- Fullscreen: active/inactive transitions through the Qt6 running property;
  all three Midnight Gallery Club sources detected; individual native
  replacements and REST next requests changed only the selected source.
  Public REST replacement by ID and source also passed on all four slots
  of the Fitness scene.
- Global fullscreen exact play passed with a scene-compatible pole clip.
  A table clip excluded by the scene was not selected.
- Native fullscreen queue: add, prepend, observe ordering, remove, clear.
  Observe subsequent state rather than relying on the immediate command
  response, since updates arrive asynchronously.
- Shader data: values changed the rendered performer lighting.
- Raw shader texture: a red RGBA frame appeared in Midnight's picture frame.
- Shared-memory texture: a green frame was consumed at the published
  generation and appeared in the scene.
- Clip bounds: independent channels 0, 1 and 2 visibly outlined the three
  performers. Test values were restored, the texture made transparent and
  bounds capture disabled afterward.
- Dressing Rooms: prepare/warm-up succeeded; QuickPlayer requested and
  consumed the selected video URL; the default media player played it.
- OpenGL probe observed presentations and movie windows. RTX HDR reported
  active processing with increasing frame counts; disabled after testing.
- Playback, decoder, audio and fast-decode diagnostic exports responded;
  resolver masks were compatibility 0x3F, offsets 0xF, movie 0x3FF,
  audio 0x1FF and fast-decode 0x3F. Fast decode opened codecs and skipped
  scale work during seeks.
- Bridge 128: RLE7 reached native seek readiness mask 0x1FF before the
  QuickPlayer fallback timer. Native checkpoint capture returned two saved
  checkpoints, and forward/backward seeks increased the restore count.
  Legacy WMV forward/backward REST seeks also passed on this build.
- Manual queue add, reorder, remove and clear passed; the original enabled
  setting was restored. Restart and both panic transitions passed.
- Missing authentication, invalid speed/seek/shader/bounds input, unknown
  actions and nonexistent queue entries returned the expected errors.
- The normal QuickPlayer fullscreen filter excluded custom shows from its
  automatic queue and refused custom playback while the scene was running.
- On bridge 128 all three sources accepted exact REST replacement. Queued
  next passed on GirlLeft and GirlCentre; GirlRight still needs investigation
  because its reported clip did not change during the observation interval.
- Bridge 129: exact play and queued next passed independently on GirlLeft,
  GirlRight and GirlCentre after allowing replacement playback to initialize.
  Each next changed only the selected slot. The initial clips were restored.
  A next issued immediately after exact replacement can arrive before
  initialization completes; compare this edge case with the 2.4 baseline
  before choosing a compatibility-preserving fix.
- Now-playing-info selected the current card and displayed its model details
  in the normal QuickPlayer UI.

## Bridge 128 regression

Live bridge 127 failed alpha readiness/checkpoint capture on RLE7 clips:
the movie frame advanced while delta alpha frame/generation stayed zero.
The Qt6 alpha dispatch proves that RLE7 decodes each mask independently.
Bridge 128 derives the encoding field from that dispatch and observes
rendered frame progress for RLE7. Delta masks and Qt5 retain their existing
checks. QuickPlayer's fullscreen source filter also now reads the bridge's
activity field, retaining its old mode fallback for older state packets.

Offline tests require several observations before RLE7 becomes ready and
then capture a checkpoint. Delta-mask tests keep readiness false when only
the movie advances and require actual delta decoder progress. These tests
pass, along with the Qt6 container/string/allocator, decoder/audio,
fullscreen resolver, Qt property and executable seek-thunk checks.

Release x64 C# build, `--verify-custom-shows`, `--verify-controls`,
`--verify-api-options` and `git diff --check` passed. The existing user edits
in `CustomPlayerForm.cs` were preserved.

## Remaining verification

- Compare and resolve rapid exact-play/next sequencing against the actual
  2.4 baseline. Settled per-slot play/next checks pass on all three sources.
- Repeat the final bridge 132 changes against 2.5.0 after switching installations.
- Complete QuickPlayer button and Dressing Rooms UI checks; window capture
  currently fails with "foreground window did not report a process id".

## Bridge 130 / installed 2.4.0.0

The installed Qt5 build uses shared fullscreen cards, unlike the older raw-card
layout. Its startNextShow saves R13 and keeps the node in R14. Resolution now
checks the function diagnostic name and derives the scene/list, expected-card,
mode, selection and nextShowClip fields from that code. The shared takeNextAt
implementation independently confirms the queue card/control pair. Queue
insertion uses Qt5 QList storage with host-allocated shared-card elements;
scene replacement and queue removal release shared controls through the host
allocator. The older raw-card and Qt6 container paths remain available.

Offline Qt5 container/string/audio/decoder/seek/fullscreen checks passed on the
installed 2.4.0.0 executable. Release x64 build, --verify-custom-shows,
--verify-controls, --verify-api-options and git diff --check passed. Live
verification continued on bridges 131 and 132 below. CustomPlayerForm.cs remains unchanged from
its preexisting user-edit hash.

## Bridges 131 / 132: installed 2.4.0.0 live checks

- VP9 movie discovery, timeline, native pause/resume, forward/backward absolute
  seeks and speed 1.5/restoration passed. Native pause held the same elapsed
  time across eight observations. REST pause and +/-10% seeks passed with
  QuickPlayer hidden after replacing button PerformClick calls with the shared
  playback helpers. Hidden buttons previously accepted requests without acting.
- Manual queue add, reorder, remove and clear passed.
- All three Midnight Gallery Club sources were detected. Individual exact
  replacement passed. Bridge 132 also accepted three different replacements
  back-to-back and reported all three requested clips within one second;
  restoring the three originals back-to-back passed too.
- Queued next passed for GirlLeft, GirlCentre and GirlRight on bridge 132.
  Slot IDs changed after QuickPlayer reconnected; clients must read current IDs
  or use the source endpoint. Fullscreen queue add/prepend/remove/clear passed.
- Shader values round-tripped and were restored. Raw RGBA uploads and texture
  metadata passed. A shared-memory frame was consumed at generation 2 and
  native texture sequence advanced to 2. Bounds channels 0/1/2 accepted enable
  and disable. Actual visual output still needs verification: the capture tool
  returned black screenshots while the API reported the active scene.
- Diagnostic tree included all three active FsClipNode sources.
- /status previously reported stopped while fullscreen was playing because
  CurrentAnim describes the desktop movie. It now reports playing for an active
  scene with clips; the per-slot identities remain in /fullscreen. The published
  application returned playing in this test.

Bridge 131 fixes QObject playable-card destruction to use the deleting destructor
at vtable slot 3 for both Qt versions. Bridge 132 keeps pending replacements per
node; consuming one node no longer discards another node's request. The isolated
regression harness verifies independent consumption and expiry. Native compatibility
checks, the C# Release build, all three application verification commands and
git diff --check passed. Rapid next during clip initialization remains under review.

## Additional 2.4 checks and executable identity

The installed QuickPlayer was found running from AppData/Local/Programs during
the later API checks. Those observations are not evidence for the patched build.
The published 0.99.2433 / bridge 132 executable was subsequently launched directly
from bin/x64/Release/net10.0-windows. Its process path and HTTP request-queue owner
were verified. Restarting this exact build restored movie registration and the
correct desktop playback status; the cause of the initial stale connection has
not yet been established.

On the verified executable, e0705_22528102.vghd played successfully. Forward and
backward seeks, 1.5x speed and restoration passed. Pause held 40566 milliseconds
across six settled observations. An immediate status observation can retain the
previous elapsed sample until the playback timer refreshes it. Decoder kind was
1, so this test does not establish WMV coverage.

Individual exact-play and queued-next checks passed for all three sources. A
queued card already playing in another source was left queued while the requested
source selected another card; use distinct cards when checking queue consumption.
Rapid exact-play followed immediately by next still returned success while the
exact clip remained and the queue entry disappeared. This case remains unresolved.

Visual capture subsequently showed three performers in Midnight Gallery Club.
Shader colour/brightness changes and all three independent bounds channels were
visible. The named video1 texture rendered a red raw RGBA upload and a green
shared-memory frame; generation 2 was consumed. Default scene saturation initially
made the red upload appear grey. Both uploaded textures, bounds and shader values
were restored after the checks.

Exclusive native bridge checks found three owned Dressing Rooms and successfully
requested and consumed an HTTP media URL. Desktop OpenGL HDR processing became
active, with frame count increasing from 9 to 30. Volume, lock, click-through,
wheel-resize and alpha-antialiasing bridge calls passed; temporary HDR/probe and
antialiasing settings were disabled afterward. Fullscreen rendering used a different
path and did not report desktop HDR frames.

The UI launch helper resolved an explicit published executable path to the
installed application shortcut. Use direct process launch and verify the executable
path before each live session. The final bridge 132 build still needs live 2.5.0
regression testing, and QuickPlayer UI checks remain blocked by capture failure.

## 2.5.0 follow-up: stale registry key and pause latency

The published process path was verified on 2.5.0.0. QuickPlayer reported an empty
current animation while a fresh registry read showed a playing f1699 clip. An
isolated temporary-key reproduction confirmed that a cached RegistryKey returns
the default after deletion/recreation, while a newly opened handle reads the new
value. The reader now opens the parameters key for each read, preserving the same
registry path and value contract for both iStripper versions. This removes the
stale-empty value that makes GetNextClip return without advancing.

The user reported intermittent slow HTTP responses from play-pause during
established playback. Seventy-two timed calls took 6-80 milliseconds; the longer
60-call run averaged 9.22 milliseconds and peaked at 75 milliseconds. The long
stall has not been reproduced or fixed. Actions now return Server-Timing entries
for ui_queue and action duration; all responses also include request processing
duration. These durations exclude time before HTTP acceptance and response transfer.

Release x64 build completed with zero warnings/errors. The freshly built executable
passed --verify-controls, --verify-custom-shows and --verify-api-options, all exit
code 0. Native Qt6 compatibility checks and git diff --check passed. The changes
were published as 0.99.2435 with bridge 132. Live post-publish checks are pending:
automatic approval review rejected the combined launch-and-verification command,
and the user was asked to launch the published executable.

After renewed user authorization, a separate direct launch succeeded and the
running published path was verified. Current animation matched the live registry.
The UI title reported Mathilda Scorpy / clip 16; Next Clip advanced from 41505 to
41506. That action reported ui_queue=1.56 ms, action=7.961 ms and request=10.179 ms.
Replacing the card with f1191_14336407.vghd succeeded and reported playing with
movieRegistered/seekReady true. Physical UI button checks still require confirmation
because window capture continues to fail with the same foreground-process error.

Eight further pause/resume requests on 0.99.2435 alternated the reported state
correctly. HTTP durations were 5-58 ms, with action execution 4.16-11.4 ms and
UI queue waits below 0.6 ms. Both the installed Qt6 executable and the saved Qt5
executable passed offline compatibility checks; the saved Qt5 fixture required
the legacy ffmpeg64 libraries before its decode checks could run. The preexisting
CustomPlayerForm.cs hash remains unchanged.
