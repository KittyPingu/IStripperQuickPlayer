# Shader texture latency test

This console tool continuously renders differently coloured elapsed-time
counters into reusable RGBA8 buffers and publishes them as `texture1` through
`textureN`. It reports achieved set/texture rates, missed target intervals, and
HTTP average/p50/p95/p99/maximum latency.

Run the published executable until `Ctrl+C`:

```powershell
.\ShaderTextureLatencyTest.exe
```

Run a 30-second test at a 10 ms target interval:

```powershell
.\ShaderTextureLatencyTest.exe --interval-ms 10 --duration 30
```

Test the three default 1024 by 512 textures through their independent
shared-memory channels:

```powershell
.\ShaderTextureLatencyTest.exe --shared-memory --interval-ms 10 `
  --width 1024 --height 512 --duration 30
```

Shared-memory mode reports publication and consumption rates, local copy time,
missed scheduler intervals, and the number of published frames awaiting an
acknowledgement from QuickPlayer.

The defaults are three 1024 by 512 textures, a 10 ms interval, and full opacity.
Use `--textures 1` through `--textures 16` to change the texture count. The tool
reads the normal QuickPlayer API token from Local AppData and sets
`u_QuickPlayerData.x` to the requested `--opacity` before testing.
Widths and heights from 32 through 4096 are accepted. In shared-memory mode the
tool declares the requested test dimensions during channel discovery; omitting
capacity parameters in your own client still gives the API's 1024 by 1024
default channel.
