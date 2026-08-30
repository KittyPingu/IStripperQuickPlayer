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

    RtxVideoSession(IntPtr handle) => this.handle = handle;

    internal static RtxVideoSession? TryCreate(ID3D11Device device,
        out string? unavailableReason)
    {
        unavailableReason = null;
        try
        {
            StringBuilder error = new(512);
            IntPtr handle = RtxVideo_CreateD3D11(device.NativePointer,
                error, error.Capacity);
            if (handle != IntPtr.Zero) return new(handle);
            unavailableReason = error.Length == 0
                ? "NVIDIA RTX Video Super Resolution is unavailable."
                : error.ToString();
        }
        catch (Exception error) when (error is DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException)
        {
            unavailableReason = error.Message;
        }
        return null;
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
        return succeeded;
    }

    public void Dispose()
    {
        IntPtr current = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (current != IntPtr.Zero) RtxVideo_Destroy(current);
    }

    internal static void Verify()
    {
        using ID3D11Device device = D3D11CreateDevice(
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            Vortice.Direct3D.FeatureLevel.Level_11_1,
            Vortice.Direct3D.FeatureLevel.Level_11_0);
        using RtxVideoSession session = TryCreate(device, out string? unavailable)
            ?? throw new NotSupportedException(unavailable);
        using ID3D11Texture2D input = VideoTexture(device, 640, 360);
        using ID3D11Texture2D output = VideoTexture(device, 1280, 720);
        if (!session.Evaluate(input, output, 640, 360, 1280, 720, 2,
                out string? failure))
            throw new InvalidOperationException(failure);
    }

    static ID3D11Texture2D VideoTexture(ID3D11Device device, int width,
        int height) => device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget |
                BindFlags.UnorderedAccess
        });

    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    static extern IntPtr RtxVideo_CreateD3D11(IntPtr device,
        StringBuilder error, int errorCapacity);

    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    static extern int RtxVideo_EvaluateVsrD3D11(IntPtr handle, IntPtr input,
        IntPtr output, int inputWidth, int inputHeight, int outputWidth,
        int outputHeight, int quality, StringBuilder error, int errorCapacity);

    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl)]
    static extern void RtxVideo_Destroy(IntPtr handle);
}
