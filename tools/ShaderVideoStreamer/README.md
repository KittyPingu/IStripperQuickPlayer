# Shader video streamer example

This example decodes a video with OpenCV, converts each frame to RGBA8, and
publishes it to QuickPlayer's named shared-memory texture channel. It preserves
the source frame rate, loops by default, drops decoded frames when necessary to
stay near real time, and proportionally resizes videos whose largest dimension
exceeds the selected maximum dimension. The UI and `--max-dimension` default
to 4096 pixels, so UHD and DCI 4K sources retain their native dimensions. The streamer
declares the resulting width and height when it discovers its shared-memory
channel, so the mapping is only as large as the video output requires.

Publish the self-contained Windows executable:

```powershell
dotnet publish .\ShaderVideoStreamer.csproj -c Release -r win-x64 `
  --self-contained true -o .\bin\Release\net10.0-windows\win-x64\publish
```

Launch with no arguments for the Windows video picker and live controls:

```powershell
.\bin\Release\net10.0-windows\win-x64\publish\ShaderVideoStreamer.exe
```

For automation, supply a path to run in console mode and stream as `video1`:

```powershell
.\bin\Release\net10.0-windows\win-x64\publish\ShaderVideoStreamer.exe `
  "C:\Videos\example.mp4"
```

Choose another deterministic texture name or override the frame rate:

```powershell
.\ShaderVideoStreamer.exe "C:\Videos\example.mp4" `
  --name backgroundVideo --fps 30
```

Add `--duration 10` for a bounded test run.

The example uses `u_QuickPlayerData` as `(centreX, centreY, width, height)`.
The centre is normalized and defaults to `(0.5, 0.5)`; width and height are
scene pixels and default to `512` by `256`. Use `--x`, `--y`,
`--display-width`, and `--display-height` to override them. In GLSL, the fourth
component can be addressed as either `.w` or its colour alias `.a`. While the
streamer has keyboard focus, the arrow keys move the panel, Shift+Left/Right
resize its width, Shift+Down/Up resize its height, and `Q` stops. Movement and
resize steps are editable in the UI; holding Ctrl uses one-tenth of the selected
step for especially fine adjustments.

For the default name, declare:

```glsl
uniform sampler2D u_QuickPlayerTexture_video1;
uniform vec2 u_QuickPlayerTextureSize_video1;
uniform float u_QuickPlayerTextureSequence_video1;
```

[`VideoTextureExample.fsh`](VideoTextureExample.fsh) is a complete fragment
shader example. QuickPlayer must be running with iStripper attached before the
streamer starts. The example publishes video only; audio remains with the
source player and is not sent to iStripper.
