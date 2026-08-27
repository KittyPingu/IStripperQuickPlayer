# RVM GPU bridge

`IStripperRvmGpuBridge64` is QuickPlayer's native zero-copy playback path for
RVM ONNX custom shows. It has no proprietary segmentation-SDK dependency.

For each frame, FFmpeg supplies a hardware-decoded D3D12 YUV texture and its
decode fence. The bridge waits on that fence, converts and resizes YUV to the
planar RGB tensor with a compute shader, runs ONNX Runtime DirectML with GPU-
resident recurrent state, and publishes full-resolution RGB plus alpha as
shared textures for QuickPlayer's D3D11 compositor. Alpha is copied to CPU only
when the player requests an alpha-aware hit map.

The managed player treats this bridge as an optimization. Unsupported hardware
decoding, mismatched adapters, initialization errors, or sharing failures fall
back to the managed software-decoded DirectML path. If inference itself remains
unavailable, playback continues with opaque alpha.

The project is built as part of the x64 Release solution and the application
project copies `IStripperRvmGpuBridge64.dll` into build and publish outputs. Use
the following optional diagnostic with installed RVM ONNX models and a playable
video:

```powershell
.\IStripperQuickPlayer.exe --verify-rvm-gpu 'C:\path\to\video.mp4'
```
