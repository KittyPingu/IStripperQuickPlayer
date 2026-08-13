using System.Drawing.Imaging;
using Vortice.D3DCompiler;
using Vortice.DirectComposition;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3D11 = Vortice.Direct3D11.D3D11;
using DComp = Vortice.DirectComposition.DComp;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;
using DxgiFormat = Vortice.DXGI.Format;
using GpuViewport = Vortice.Mathematics.Viewport;

namespace IStripperQuickPlayer;

/// <summary>
/// HWND-bound DXGI preview. The render thread owns every D3D object and is the
/// only thread that waits in Present, keeping CUDA contention off the UI pump.
/// Source and mask are committed together in a single swap-chain frame.
/// </summary>
internal sealed class DxgiMaskPreviewControl : Control
{
    const string Shader = """
        Texture2D Source : register(t0); Texture2D Mask : register(t1);
        Texture2D Data : register(t2); Texture2D Points : register(t3);
        SamplerState Sampler : register(s0);
        struct Out { float4 position:SV_POSITION; float2 uv:TEXCOORD0; };
        Out VSMain(uint id:SV_VertexID) { Out o; float2 p=float2((id<<1)&2,id&2);
          o.uv=p; o.position=float4(p*float2(2,-2)+float2(-1,1),0,1); return o; }
        float4 PSMain(Out i):SV_TARGET {
          float4 sizes=Data.Load(int3(0,0,0)), edit=Data.Load(int3(1,0,0));
          float scale=min(sizes.z/sizes.x,sizes.w/sizes.y);
          float2 offset=(sizes.zw-sizes.xy*scale)*.5;
          float2 pixel=(i.uv*sizes.zw-offset)/scale;
          if(any(pixel<0)||any(pixel>=sizes.xy)) return float4(0,0,0,1);
          float2 uv=pixel/sizes.xy; float4 color=Source.Sample(Sampler,uv);
          color.rgb=lerp(color.rgb,float3(0,1,0),Mask.Sample(Sampler,uv).r*.412);
          [loop] for(int n=0;n<(int)edit.x && n<64;n++) {
            float4 p=Points.Load(int3(n,0,0)); float d=distance(pixel,p.xy);
            if(d>=5&&d<=9) color=float4(p.z>.5?float3(0,1,0):float3(1,0,0),1);
          }
          if(edit.y>=0&&abs(distance(pixel,edit.yz)-edit.w)<=1.5)
            color=float4(1,1,1,1);
          return float4(color.rgb,1);
        }
        """;

    sealed class FrameUpdate : IDisposable
    {
        internal Bitmap? Source;
        internal required Bitmap Mask;
        internal required PointF[] Points;
        internal required int[] Labels;
        public void Dispose() { Source?.Dispose(); Mask.Dispose(); }
    }

    readonly object sync = new();
    readonly AutoResetEvent changed = new(false);
    Thread? renderThread;
    FrameUpdate? pending;
    Size pendingSize;
    Size imageSize;
    PointF? brush;
    int brushDiameter;
    bool overlayDirty;
    volatile bool stopping;
    Exception? failure;

    internal bool HasImage { get { lock (sync) return !imageSize.IsEmpty || pending != null; } }
    internal Exception? RendererFailure => failure;

    internal DxgiMaskPreviewControl()
    {
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        lock (sync) pendingSize = ClientSize;
        stopping = false;
        renderThread = new Thread(RenderLoop) { IsBackground = true,
            Name = "mask-preview-dxgi" };
        renderThread.Start(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        StopRenderer();
        base.OnHandleDestroyed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        lock (sync) pendingSize = ClientSize;
        changed.Set();
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void WndProc(ref Message m)
    {
        const int WmEraseBackground = 0x14;
        if (m.Msg == WmEraseBackground) { m.Result = (IntPtr)1; return; }
        base.WndProc(ref m);
    }

    internal void SetPreviewOwned(Bitmap source, Bitmap mask, PointF[] points, int[] labels)
    {
        Queue(new FrameUpdate { Source = source, Mask = mask,
            Points = [.. points.Take(64)], Labels = [.. labels.Take(64)] });
    }

    internal void SetPreview(Bitmap source, Bitmap mask, PointF[] points, int[] labels) =>
        Queue(new FrameUpdate { Source = new Bitmap(source), Mask = new Bitmap(mask),
            Points = [.. points.Take(64)], Labels = [.. labels.Take(64)] });

    internal void SetMask(Bitmap mask, PointF[] points, int[] labels) =>
        Queue(new FrameUpdate { Mask = new Bitmap(mask),
            Points = [.. points.Take(64)], Labels = [.. labels.Take(64)] });

    void Queue(FrameUpdate update)
    {
        lock (sync)
        {
            // A paint update may overtake the initial source+mask upload. Carry
            // the source forward so coalescing can never produce a mask-only
            // first frame.
            if (update.Source == null && pending?.Source != null)
            {
                update.Source = pending.Source;
                pending.Source = null;
            }
            pending?.Dispose();
            pending = update;
        }
        changed.Set();
    }

    internal void SetBrush(PointF? value, int diameter)
    {
        lock (sync) { brush = value; brushDiameter = diameter; overlayDirty = true; }
        changed.Set();
    }

    internal PointF? ImagePoint(Point location)
    {
        Size size; lock (sync) size = imageSize;
        if (size.IsEmpty || ClientSize.Width <= 0 || ClientSize.Height <= 0) return null;
        float scale = Math.Min(ClientSize.Width / (float)size.Width,
            ClientSize.Height / (float)size.Height);
        float left = (ClientSize.Width - size.Width * scale) / 2,
            top = (ClientSize.Height - size.Height * scale) / 2;
        float x = (location.X - left) / scale, y = (location.Y - top) / scale;
        return x < 0 || y < 0 || x >= size.Width || y >= size.Height
            ? null : new PointF(x, y);
    }

    unsafe void RenderLoop(object? value)
    {
        try
        {
            IntPtr handle = (IntPtr)value!;
            using ID3D11Device device = D3D11.D3D11CreateDevice(DriverType.Hardware,
                DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_1, FeatureLevel.Level_11_0);
            using ID3D11DeviceContext context = device.ImmediateContext;
            using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using IDXGIFactory2 factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            Size size; lock (sync) size = pendingSize;
            size = ValidSize(size);
            using IDXGISwapChain1 swap = factory.CreateSwapChainForComposition(device,
                Description(size));
            using IDCompositionDevice composition =
                DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            composition.CreateTargetForHwnd(handle, true,
                out IDCompositionTarget compositionTarget).CheckError();
            using (compositionTarget)
            using (IDCompositionVisual visual = composition.CreateVisual())
            {
                visual.SetContent(swap);
                compositionTarget.SetRoot(visual);
                composition.Commit();
                RenderFrames(device, context, swap, composition, size);
            }
        }
        catch (Exception error) { failure = error; }
    }

    void RenderFrames(ID3D11Device device, ID3D11DeviceContext context,
        IDXGISwapChain1 swap, IDCompositionDevice composition, Size size)
    {
        try
        {
            PresentBlack(context, swap);
            using ID3D11SamplerState sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
            using ID3D11VertexShader vs = device.CreateVertexShader(Compiler.Compile(
                Shader, "VSMain", "MaskPreview.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3).Span);
            using ID3D11PixelShader ps = device.CreatePixelShader(Compiler.Compile(
                Shader, "PSMain", "MaskPreview.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3).Span);
            using ID3D11Texture2D data = FloatTexture(device, 2);
            using ID3D11ShaderResourceView dataView = device.CreateShaderResourceView(data);
            using ID3D11Texture2D pointData = FloatTexture(device, 64);
            using ID3D11ShaderResourceView pointView = device.CreateShaderResourceView(pointData);
            ID3D11Texture2D? sourceTexture = null, maskTexture = null;
            ID3D11ShaderResourceView? sourceView = null, maskView = null;
            PointF[] currentPoints = [];
            int[] currentLabels = [];
            Size currentImageSize = Size.Empty, swapSize = size;
            try
            {
                while (!stopping)
                {
                    changed.WaitOne();
                    if (stopping) break;
                    FrameUpdate? update; Size requested; PointF? currentBrush;
                    int currentBrushDiameter; bool redrawOverlay;
                    lock (sync)
                    {
                        update = pending; pending = null; requested = pendingSize;
                        currentBrush = brush; currentBrushDiameter = brushDiameter;
                        redrawOverlay = overlayDirty; overlayDirty = false;
                    }
                    requested = ValidSize(requested);
                    bool resized = requested != swapSize;
                    if (resized)
                    {
                        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                        swap.ResizeBuffers(2, (uint)requested.Width, (uint)requested.Height,
                            DxgiFormat.B8G8R8A8_UNorm, SwapChainFlags.None);
                        swapSize = requested;
                        if (sourceView == null || maskView == null)
                            PresentBlack(context, swap);
                    }
                    if (update != null)
                    {
                        try
                        {
                            if (update.Source != null)
                            {
                                ID3D11Texture2D next = Upload(device, context, update.Source);
                                ID3D11ShaderResourceView nextView = device.CreateShaderResourceView(next);
                                sourceView?.Dispose(); sourceTexture?.Dispose();
                                sourceTexture = next; sourceView = nextView;
                                currentImageSize = update.Source.Size;
                                lock (sync) imageSize = currentImageSize;
                            }
                            ID3D11Texture2D nextMask = Upload(device, context, update.Mask);
                            ID3D11ShaderResourceView nextMaskView = device.CreateShaderResourceView(nextMask);
                            maskView?.Dispose(); maskTexture?.Dispose();
                            maskTexture = nextMask; maskView = nextMaskView;
                            currentPoints = update.Points; currentLabels = update.Labels;
                        }
                        finally { update.Dispose(); }
                    }
                    if (sourceView == null || maskView == null || currentImageSize.IsEmpty ||
                        (update == null && !resized && !redrawOverlay)) continue;
                    Draw(context, swap, swapSize, currentImageSize, sourceView, maskView,
                        data, dataView, pointData, pointView, sampler, vs, ps,
                        currentPoints, currentLabels, currentBrush, currentBrushDiameter);
                }
            }
            finally
            {
                sourceView?.Dispose(); maskView?.Dispose();
                sourceTexture?.Dispose(); maskTexture?.Dispose();
            }
        }
        catch (Exception error) { failure = error; }
    }

    static unsafe void Draw(ID3D11DeviceContext context, IDXGISwapChain1 swap,
        Size swapSize, Size sourceSize, ID3D11ShaderResourceView sourceView,
        ID3D11ShaderResourceView maskView, ID3D11Texture2D data,
        ID3D11ShaderResourceView dataView, ID3D11Texture2D pointData,
        ID3D11ShaderResourceView pointView, ID3D11SamplerState sampler,
        ID3D11VertexShader vs, ID3D11PixelShader ps, PointF[] points, int[] labels,
        PointF? brush, int brushDiameter)
    {
        using ID3D11Texture2D back = swap.GetBuffer<ID3D11Texture2D>(0);
        using ID3D11RenderTargetView target = context.Device.CreateRenderTargetView(back);
        float[] values = [sourceSize.Width, sourceSize.Height, swapSize.Width,
            swapSize.Height, points.Length, brush?.X ?? -1, brush?.Y ?? -1,
            brushDiameter / 2f];
        fixed (float* p = values) context.UpdateSubresource(data, 0, null, (IntPtr)p, 32, 0);
        float[] pv = new float[256];
        for (int i = 0; i < points.Length; i++)
        {
            pv[i * 4] = points[i].X; pv[i * 4 + 1] = points[i].Y;
            pv[i * 4 + 2] = i < labels.Length ? labels[i] : 1;
        }
        fixed (float* p = pv) context.UpdateSubresource(pointData, 0, null, (IntPtr)p, 1024, 0);
        context.OMSetRenderTargets(target);
        context.RSSetViewport(new GpuViewport(0, 0, swapSize.Width, swapSize.Height));
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vs); context.PSSetShader(ps);
        context.PSSetShaderResource(0, sourceView); context.PSSetShaderResource(1, maskView);
        context.PSSetShaderResource(2, dataView); context.PSSetShaderResource(3, pointView);
        context.PSSetSampler(0, sampler); context.Draw(3, 0);
        context.PSUnsetShaderResource(0); context.PSUnsetShaderResource(1);
        context.PSUnsetShaderResource(2); context.PSUnsetShaderResource(3);
        // The preview is disposable visual feedback. If DWM has not consumed
        // the previous frame, drop this one instead of ever stalling behind a
        // saturated CUDA queue.
        swap.Present(0, PresentFlags.DoNotWait);
    }

    static void PresentBlack(ID3D11DeviceContext context, IDXGISwapChain1 swap)
    {
        using ID3D11Texture2D back = swap.GetBuffer<ID3D11Texture2D>(0);
        using ID3D11RenderTargetView target = context.Device.CreateRenderTargetView(back);
        context.ClearRenderTargetView(target, new Vortice.Mathematics.Color4(0, 0, 0, 1));
        swap.Present(0, PresentFlags.DoNotWait);
    }

    static Size ValidSize(Size value) => new(Math.Max(1, value.Width), Math.Max(1, value.Height));

    static SwapChainDescription1 Description(Size size) => new((uint)size.Width,
        (uint)size.Height, DxgiFormat.B8G8R8A8_UNorm,
        bufferUsage: Usage.RenderTargetOutput, bufferCount: 2, scaling: Scaling.Stretch,
        swapEffect: SwapEffect.FlipSequential, alphaMode: DxgiAlphaMode.Premultiplied);

    static ID3D11Texture2D FloatTexture(ID3D11Device device, int width) => device.CreateTexture2D(
        new Texture2DDescription { Width = (uint)width, Height = 1, MipLevels = 1,
            ArraySize = 1, Format = DxgiFormat.R32G32B32A32_Float,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource });

    static ID3D11Texture2D Upload(ID3D11Device device, ID3D11DeviceContext context,
        Bitmap bitmap)
    {
        ID3D11Texture2D texture = device.CreateTexture2D(new Texture2DDescription {
            Width = (uint)bitmap.Width, Height = (uint)bitmap.Height, MipLevels = 1,
            ArraySize = 1, Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource });
        BitmapData bits = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { context.UpdateSubresource(texture, 0, null, bits.Scan0, (uint)bits.Stride, 0); }
        finally { bitmap.UnlockBits(bits); }
        return texture;
    }

    void StopRenderer()
    {
        stopping = true; changed.Set();
        renderThread?.Join(2000); renderThread = null;
        lock (sync) { pending?.Dispose(); pending = null; imageSize = Size.Empty; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { StopRenderer(); changed.Dispose(); }
        base.Dispose(disposing);
    }
}
