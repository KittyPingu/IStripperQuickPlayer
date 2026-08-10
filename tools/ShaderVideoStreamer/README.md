# Shader video and lighting controller

This small Windows example streams one video into QuickPlayer's named
shared-memory texture channel and controls lighting in a fullscreen scene.

The compact UI contains only:

- video filename and **Browse**;
- **Start**, **Stop**, **Loop**, and a seekable playback timeline;
- video brightness and black-and-white amount;
- whole-scene brightness, with the video composited above it;
- brightness, purpleness, and **Next card** for each of three performers.

The loop and appearance/lighting slider values are saved under the current
Windows user and restored when the controller is opened again.

The three next-card buttons call the source-specific fullscreen routes
`GirlLeft`, `GirlCentre`, and `GirlRight`. A scene using different clip
source IDs can change the `GirlSources` array in
[`VideoStreamerForm.cs`](VideoStreamerForm.cs).

## Build and run

Publish the self-contained Windows executable:

```powershell
dotnet publish .\ShaderVideoStreamer.csproj -c Release -r win-x64 `
  --self-contained true -o .\bin\Release\net10.0-windows\win-x64\publish
```

Launch with no arguments for the UI:

```powershell
.\bin\Release\net10.0-windows\win-x64\publish\ShaderVideoStreamer.exe
```

A video path starts console mode directly:

```powershell
.\ShaderVideoStreamer.exe "C:\Videos\example.mp4"
```

Console mode also supports `--name`, `--max-dimension`, `--fps`,
`--duration`, `--no-loop`, `--base-url`, and `--token`. The default
texture is `video1`; the maximum decode dimension is 4096.

QuickPlayer and its iStripper playback bridge must be running before starting
the streamer. Video frames are published as RGBA8. Audio is not streamed.

## Shader controls

The UI sends four floats to `PUT fullscreen/shader-data`. Each float carries
integer bytes exactly representable by a 32-bit float:

| Component | Low byte | Second byte | Third byte |
| --- | --- | --- | --- |
| `.x` | Left brightness | Left purpleness | reserved |
| `.y` | Centre brightness | Centre purpleness | reserved |
| `.z` | Right brightness | Right purpleness | reserved |
| `.w` | Video brightness | B&W amount | Scene brightness + marker |

GLSL unpacks a pair with `mod(packed, 256.0)` and
`floor(packed / 256.0)`. The performer values are normalized from 0 to 1.
Video brightness maps from 0% through 200%; B&W maps from full colour through
greyscale. Scene brightness maps from 0% through 100%, with encoded byte zero
reserved to mean that the extended controls are absent. The video texture is
composited after scene brightness and is therefore unaffected by that slider.

[`VideoTextureExample.fsh`](VideoTextureExample.fsh) demonstrates the video
texture, three scene-light pools, and video appearance controls.
[`PerformerPurpleLight.fsh`](PerformerPurpleLight.fsh) applies the matching
control pair directly to one clip sprite through its `lightIndex` uniform.

For the default video texture name, declare:

```glsl
uniform sampler2D u_QuickPlayerTexture_video1;
uniform vec2 u_QuickPlayerTextureSize_video1;
uniform float u_QuickPlayerTextureSequence_video1;
uniform vec4 u_QuickPlayerData;
```

The Midnight Gallery Club test scene uses the same controls, projectively maps
`video1` into its left wall frame, displays clip names, and shows the official
iStripper logo for ten seconds of every minute.
