# RTX Video native bridge

This bridge integrates the Direct3D 11 API from NVIDIA RTX Video SDK 1.1.0.
It is structured as a per-player session so VSR can later be followed by
TrueHDR in NVIDIA's required VSR-to-HDR order.

To rebuild it, download `RTX_Video_SDK_v1.1.0.zip` from NVIDIA Developer and
extract it to `third_party/nvidia/RTX_Video_SDK_v1.1.0`. The SDK is not kept in
the repository because its license does not permit stand-alone redistribution.

The bridge source is derived from NVIDIA's DX11 sample API and therefore keeps
the notice required for derivative sample source.
