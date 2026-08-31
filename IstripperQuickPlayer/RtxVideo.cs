using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace IStripperQuickPlayer;

internal sealed class RtxVideoSession : IDisposable
{
    const string Bridge = "IStripperRtxVideoBridge64.dll";
    IntPtr handle;
    readonly long diagnosticId = CustomRtxDiagnostics.NewId();
    internal bool HasVsr { get; }
    internal bool HasTrueHdr { get; }

    RtxVideoSession(IntPtr handle, int features)
    {
        this.handle = handle;
        HasVsr = (features & 1) != 0;
        HasTrueHdr = (features & 2) != 0;
        CustomRtxDiagnostics.Write("rtx-session", diagnosticId, "created",
            $"handle=0x{handle.ToInt64():X} features={features} " +
            $"vsr={HasVsr} hdr={HasTrueHdr}");
    }

    internal static RtxVideoSession? TryCreate(ID3D11Device device,
        bool enableVsr, bool enableTrueHdr, out string? unavailableReason)
    {
        long attempt = CustomRtxDiagnostics.NewId();
        CustomRtxDiagnostics.Write("rtx-create", attempt, "start",
            $"device=0x{device.NativePointer.ToInt64():X} " +
            $"vsr={enableVsr} hdr={enableTrueHdr}");
        unavailableReason = null;
        try
        {
            StringBuilder error = new(512);
            IntPtr handle = RtxVideo_CreateD3D11(device.NativePointer,
                enableVsr ? 1 : 0, enableTrueHdr ? 1 : 0, out int features,
                error, error.Capacity);
            if (handle != IntPtr.Zero)
            {
                CustomRtxDiagnostics.Write("rtx-create", attempt, "ok",
                    $"handle=0x{handle.ToInt64():X} features={features}");
                return new(handle, features);
            }
            unavailableReason = error.Length == 0
                ? "NVIDIA RTX Video Super Resolution is unavailable."
                : error.ToString();
        }
        catch (Exception error) when (error is DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException)
        {
            unavailableReason = error.Message;
            CustomRtxDiagnostics.Write("rtx-create", attempt, "exception",
                error: error);
        }
        CustomRtxDiagnostics.Write("rtx-create", attempt, "failed",
            $"reason=\"{unavailableReason}\"");
        return null;
    }

    internal bool EvaluateTrueHdr(ID3D11Texture2D input,
        ID3D11Texture2D output, int width, int height, out string? failure)
    {
        StringBuilder error = new(512);
        bool succeeded = RtxVideo_EvaluateTrueHdrD3D11(handle,
            input.NativePointer, output.NativePointer, width, height,
            contrast: 100, saturation: 100, middleGray: 50,
            maxLuminance: 1000, error, error.Capacity) != 0;
        failure = succeeded ? null : error.ToString();
        if (!succeeded)
            CustomRtxDiagnostics.Write("rtx-session", diagnosticId,
                "hdr-evaluate-failed", $"handle=0x{handle.ToInt64():X} " +
                $"size={width}x{height} failure=\"{failure}\"");
        return succeeded;
    }

    internal bool Evaluate(ID3D11Texture2D input, ID3D11Texture2D output,
        int inputWidth, int inputHeight, int outputWidth, int outputHeight,
        int quality, out string? failure)
    {
        StringBuilder error = new(512);
        bool succeeded = RtxVideo_EvaluateVsrD3D11(handle,
            input.NativePointer, output.NativePointer, inputWidth, inputHeight,
            outputWidth, outputHeight, Math.Clamp(quality, 1, 4), error,
            error.Capacity) != 0;
        failure = succeeded ? null : error.ToString();
        if (!succeeded)
            CustomRtxDiagnostics.Write("rtx-session", diagnosticId,
                "vsr-evaluate-failed", $"handle=0x{handle.ToInt64():X} " +
                $"input={inputWidth}x{inputHeight} " +
                $"output={outputWidth}x{outputHeight} quality={quality} " +
                $"failure=\"{failure}\"");
        return succeeded;
    }

    public void Dispose()
    {
        IntPtr current = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (current != IntPtr.Zero)
        {
            CustomRtxDiagnostics.Write("rtx-session", diagnosticId,
                "destroy-start", $"handle=0x{current.ToInt64():X}");
            RtxVideo_Destroy(current);
            CustomRtxDiagnostics.Write("rtx-session", diagnosticId,
                "destroy-ok");
        }
    }

    internal static void Verify()
    {
        using ID3D11Device device = D3D11CreateDevice(
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            Vortice.Direct3D.FeatureLevel.Level_11_1,
            Vortice.Direct3D.FeatureLevel.Level_11_0);
        using RtxVideoSession session = TryCreate(device, enableVsr: true,
            enableTrueHdr: true, out string? unavailable)
            ?? throw new NotSupportedException(unavailable);
        if (!session.HasVsr || !session.HasTrueHdr)
            throw new NotSupportedException(
                "Both NVIDIA RTX VSR and RTX HDR must be available.");
        using ID3D11Texture2D input = VideoTexture(device, 640, 360);
        using ID3D11Texture2D output = VideoTexture(device, 1280, 720);
        if (!session.Evaluate(input, output, 640, 360, 1280, 720, 2,
                out string? failure))
            throw new InvalidOperationException(failure);
        using ID3D11Texture2D hdrOutput = VideoTexture(device, 1280, 720,
            Format.R16G16B16A16_Float);
        if (!session.EvaluateTrueHdr(output, hdrOutput, 1280, 720,
                out failure))
            throw new InvalidOperationException(failure);
    }

    static ID3D11Texture2D VideoTexture(ID3D11Device device, int width,
        int height, Format format = Format.R8G8B8A8_UNorm) =>
        device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget |
                BindFlags.UnorderedAccess
        });

    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    static extern IntPtr RtxVideo_CreateD3D11(IntPtr device, int enableVsr,
        int enableTrueHdr, out int availableFeatures, StringBuilder error,
        int errorCapacity);

    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    static extern int RtxVideo_EvaluateVsrD3D11(IntPtr handle, IntPtr input,
        IntPtr output, int inputWidth, int inputHeight, int outputWidth,
        int outputHeight, int quality, StringBuilder error, int errorCapacity);

    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    static extern int RtxVideo_EvaluateTrueHdrD3D11(IntPtr handle,
        IntPtr input, IntPtr output, int width, int height, int contrast,
        int saturation, int middleGray, int maxLuminance, StringBuilder error,
        int errorCapacity);

    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl)]
    static extern void RtxVideo_Destroy(IntPtr handle);
}
