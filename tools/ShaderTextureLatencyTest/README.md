# Shader texture latency test

This console tool continuously renders an elapsed-time counter and frame number
into a reusable RGBA8 buffer and publishes it through QuickPlayer's
`PUT /api/v1/fullscreen/shader-texture` endpoint. It reports achieved send rate,
missed target intervals, and HTTP average/p50/p95/p99/maximum latency.

Run the published executable until `Ctrl+C`:

```powershell
.\ShaderTextureLatencyTest.exe
```

Run a 30-second test at a 10 ms target interval:

```powershell
.\ShaderTextureLatencyTest.exe --interval-ms 10 --duration 30
```

Test the alternative shared-memory channel at 1024 by 512:

```powershell
.\ShaderTextureLatencyTest.exe --shared-memory --interval-ms 10 `
  --width 1024 --height 512 --duration 30
```

Shared-memory mode reports publication and consumption rates, local copy time,
missed scheduler intervals, and the number of published frames awaiting an
acknowledgement from QuickPlayer.

The defaults are a 256 by 128 texture, a 10 ms interval, and full opacity. The
tool reads the normal QuickPlayer API token from Local AppData and sets
`u_QuickPlayerData.x` to the requested `--opacity` before testing.
