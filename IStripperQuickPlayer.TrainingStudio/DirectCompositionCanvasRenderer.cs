using System.Drawing.Imaging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using D3D11 = Vortice.Direct3D11.D3D11;
using DComp = Vortice.DirectComposition.DComp;
using DxgiFormat = Vortice.DXGI.Format;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;
using GpuViewport = Vortice.Mathematics.Viewport;

namespace IStripperQuickPlayer.TrainingStudio;

/// <summary>Owns the annotation canvas DirectComposition swap chain and render thread.</summary>
internal sealed class DirectCompositionCanvasRenderer : IDisposable
{
    const string Shader = """
        Texture2D Scene : register(t0); SamplerState Sampler : register(s0);
        struct Out { float4 position:SV_POSITION; float2 uv:TEXCOORD0; };
        Out VSMain(uint id:SV_VertexID) { Out o; float2 p=float2((id<<1)&2,id&2);
          o.uv=p; o.position=float4(p*float2(2,-2)+float2(-1,1),0,1); return o; }
        float4 PSMain(Out i):SV_TARGET { return Scene.Sample(Sampler,i.uv); }
        """;

    readonly object sync = new();
    readonly AutoResetEvent changed = new(false);
    readonly Thread thread;
    Bitmap? pending;
    Size requestedSize;
    volatile bool stopping;
    internal Exception? Failure { get; private set; }

    internal DirectCompositionCanvasRenderer(IntPtr handle, Size size)
    {
        requestedSize = size;
        thread = new Thread(() => RenderLoop(handle)) { IsBackground = true,
            Name = "annotation-direct-composition" };
        thread.Start();
    }

    internal void Resize(Size size)
    {
        lock (sync) requestedSize = size;
        changed.Set();
    }

    internal void Present(Bitmap ownedFrame)
    {
        lock (sync) { pending?.Dispose(); pending = ownedFrame; }
        changed.Set();
    }

    void RenderLoop(IntPtr handle)
    {
        try
        {
            using ID3D11Device device = D3D11.D3D11CreateDevice(DriverType.Hardware,
                DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_1, FeatureLevel.Level_11_0);
            using ID3D11DeviceContext context = device.ImmediateContext;
            using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using IDXGIFactory2 factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            Size size; lock (sync) size = ValidSize(requestedSize);
            using IDXGISwapChain1 swap = factory.CreateSwapChainForComposition(device, Description(size));
            using IDCompositionDevice composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            composition.CreateTargetForHwnd(handle, true, out IDCompositionTarget target).CheckError();
            using (target)
            using (IDCompositionVisual visual = composition.CreateVisual())
            using (IDCompositionRectangleClip clip = composition.CreateRectangleClip())
            using (ID3D11SamplerState sampler = device.CreateSamplerState(SamplerDescription.LinearClamp))
            using (ID3D11VertexShader vs = device.CreateVertexShader(Compiler.Compile(
                Shader, "VSMain", "AnnotationCanvas.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3).Span))
            using (ID3D11PixelShader ps = device.CreatePixelShader(Compiler.Compile(
                Shader, "PSMain", "AnnotationCanvas.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3).Span))
            {
                SetClip(clip, size); visual.SetClip(clip);
                visual.SetContent(swap); target.SetRoot(visual); composition.Commit();
                while (!stopping)
                {
                    changed.WaitOne(); if (stopping) break;
                    Bitmap? frame; Size next;
                    lock (sync) { frame = pending; pending = null; next = ValidSize(requestedSize); }
                    if (next != size)
                    {
                        // DirectComposition visuals are not implicitly clipped to a child
                        // HWND while its swap chain is being resized. Update the explicit
                        // client clip first so the old surface cannot cover sibling controls.
                        SetClip(clip, next); composition.Commit();
                        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                        swap.ResizeBuffers(2, (uint)next.Width, (uint)next.Height,
                            DxgiFormat.B8G8R8A8_UNorm, SwapChainFlags.None); size = next;
                    }
                    if (frame == null) continue;
                    try { Draw(device, context, swap, frame, size, sampler, vs, ps); }
                    finally { frame.Dispose(); }
                }
            }
        }
        catch (Exception error) { Failure = error; }
    }

    static void Draw(ID3D11Device device, ID3D11DeviceContext context,
        IDXGISwapChain1 swap, Bitmap frame, Size size, ID3D11SamplerState sampler,
        ID3D11VertexShader vs, ID3D11PixelShader ps)
    {
        using ID3D11Texture2D texture = Upload(device, context, frame);
        using ID3D11ShaderResourceView view = device.CreateShaderResourceView(texture);
        using ID3D11Texture2D back = swap.GetBuffer<ID3D11Texture2D>(0);
        using ID3D11RenderTargetView target = device.CreateRenderTargetView(back);
        context.OMSetRenderTargets(target);
        context.RSSetViewport(new GpuViewport(0, 0, size.Width, size.Height));
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vs); context.PSSetShader(ps);
        context.PSSetShaderResource(0, view); context.PSSetSampler(0, sampler);
        context.Draw(3, 0); context.PSUnsetShaderResource(0);
        swap.Present(1, PresentFlags.None).CheckError();
    }

    static ID3D11Texture2D Upload(ID3D11Device device, ID3D11DeviceContext context, Bitmap bitmap)
    {
        ID3D11Texture2D texture = device.CreateTexture2D(new Texture2DDescription {
            Width = (uint)bitmap.Width, Height = (uint)bitmap.Height, MipLevels = 1, ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource });
        BitmapData bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { context.UpdateSubresource(texture, 0, null, bits.Scan0, (uint)bits.Stride, 0); }
        finally { bitmap.UnlockBits(bits); }
        return texture;
    }

    static Size ValidSize(Size value) => new(Math.Max(1, value.Width), Math.Max(1, value.Height));
    static void SetClip(IDCompositionRectangleClip clip, Size size)
    {
        clip.SetLeft(0); clip.SetTop(0);
        clip.SetRight(size.Width); clip.SetBottom(size.Height);
    }
    static SwapChainDescription1 Description(Size size) => new((uint)size.Width, (uint)size.Height,
        DxgiFormat.B8G8R8A8_UNorm, bufferUsage: Usage.RenderTargetOutput, bufferCount: 2,
        scaling: Scaling.Stretch, swapEffect: SwapEffect.FlipSequential,
        alphaMode: DxgiAlphaMode.Premultiplied);

    public void Dispose()
    {
        stopping = true; changed.Set();
        if (thread.IsAlive) thread.Join(2000);
        lock (sync) { pending?.Dispose(); pending = null; }
        changed.Dispose();
    }
}
