using IStripperQuickPlayer.BLL;
using Manina.Windows.Forms;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DColor = Vortice.Mathematics.Color4;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingRectangle = System.Drawing.Rectangle;
using Int2 = Vortice.Mathematics.Int2;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;
using DxgiFormat = Vortice.DXGI.Format;

namespace IStripperQuickPlayer;

internal sealed class DirectCompositionCardOverlayControl : IDisposable
{
    private sealed class SharedOverlaySurface(
        DrawingBitmap image,
        Size size,
        IDCompositionSurface surface) : IDisposable
    {
        internal DrawingBitmap Image { get; } = image;
        internal Size Size { get; } = size;
        internal IDCompositionSurface Surface { get; } = surface;
        internal DrawingRectangle Source { get; set; }
        internal bool Rendered { get; set; }

        public void Dispose() => Surface.Dispose();
    }

    private sealed class CardOverlayVisual(
        IDCompositionVisual visual,
        IDCompositionRectangleClip clip) : IDisposable
    {
        internal IDCompositionVisual Visual { get; } = visual;
        internal IDCompositionRectangleClip Clip { get; } = clip;
        internal SharedOverlaySurface? Surface { get; set; }
        internal DrawingRectangle Bounds { get; set; }
        internal bool Rounded { get; set; }

        public void Dispose()
        {
            Clip.Dispose();
            Visual.Dispose();
        }
    }

    private void UpdateCardOverlayVisuals()
    {
        DrawingRectangle viewport = new(Point.Empty, list.ClientSize);
        List<(int Index, CardRenderer.GpuCardVisual Visual,
            CardOverlayFrame Frame)> cards = [];
        if (Properties.Settings.Default.DrawCardOverlays)
        {
            foreach (ImageListViewItem item in list.Items)
            {
                if (list.IsItemVisible(item) ==
                        ItemVisibility.NotVisible ||
                    !renderer.TryGetGpuCard(
                        item.Index, out CardRenderer.GpuCardVisual visual) ||
                    visual.Card.image == null ||
                    !visual.Bounds.IntersectsWith(viewport) ||
                    !FitsHorizontally(visual.Bounds, viewport) ||
                    !CardOverlayLoader.TryGetFrame(
                        visual.Card.name, out CardOverlayFrame frame) ||
                    frame.Source.Width <= 0 || frame.Source.Height <= 0)
                    continue;
                cards.Add((item.Index, visual, frame));
            }
        }

        DrawingRectangle[] zoomed = cards
            .Where(card => !card.Visual.DrawText)
            .Select(card => card.Visual.ImageBounds)
            .ToArray();
        if (zoomed.Length != 0)
            cards.RemoveAll(card => card.Visual.DrawText &&
                zoomed.Any(bounds =>
                    bounds.IntersectsWith(card.Visual.ImageBounds)));

        HashSet<int> active = cards.Select(card => card.Index).ToHashSet();
        foreach (int removed in cardOverlayVisuals.Keys
            .Where(index => !active.Contains(index)).ToArray())
        {
            CardOverlayVisual visual = cardOverlayVisuals[removed];
            compositionVisual!.RemoveVisual(visual.Visual);
            visual.Dispose();
            cardOverlayVisuals.Remove(removed);
        }

        HashSet<SharedOverlaySurface> updated = [];
        foreach (var card in cards.OrderBy(card => card.Visual.DrawText))
        {
            SharedOverlaySurface surface =
                GetSharedOverlaySurface(card.Frame);
            if (updated.Add(surface))
                UpdateSharedOverlaySurface(surface, card.Frame);

            if (!cardOverlayVisuals.TryGetValue(
                    card.Index, out CardOverlayVisual? overlayVisual))
            {
                IDCompositionVisual visual =
                    compositionDevice!.CreateVisual();
                IDCompositionRectangleClip clip =
                    compositionDevice.CreateRectangleClip();
                overlayVisual = new CardOverlayVisual(visual, clip);
                visual.SetClip(clip);
                compositionVisual!.AddVisual(visual, true, null);
                cardOverlayVisuals.Add(card.Index, overlayVisual);
            }
            if (!ReferenceEquals(overlayVisual.Surface, surface))
            {
                overlayVisual.Visual.SetContent(surface.Surface);
                overlayVisual.Surface = surface;
            }

            DrawingRectangle bounds = card.Visual.ImageBounds;
            bool rounded = Properties.Settings.Default.RoundCardCorners;
            if (overlayVisual.Bounds != bounds ||
                overlayVisual.Rounded != rounded)
            {
                float scaleX = bounds.Width / (float)surface.Size.Width;
                float scaleY = bounds.Height / (float)surface.Size.Height;
                Matrix3x2 scale = Matrix3x2.CreateScale(scaleX, scaleY);
                overlayVisual.Visual.SetTransform(scale);
                overlayVisual.Visual.SetOffsetX(bounds.Left);
                overlayVisual.Visual.SetOffsetY(bounds.Top);
                SetOverlayClip(
                    overlayVisual.Clip, surface.Size, rounded);
                overlayVisual.Bounds = bounds;
                overlayVisual.Rounded = rounded;
            }
        }
    }

    private void UpdateCardOverlayFrames()
    {
        HashSet<SharedOverlaySurface> updated = [];
        foreach ((int index, CardOverlayVisual visual) in cardOverlayVisuals)
        {
            if (visual.Surface == null ||
                !renderer.TryGetGpuCard(
                    index, out CardRenderer.GpuCardVisual card) ||
                !CardOverlayLoader.TryGetFrame(
                    card.Card.name, out CardOverlayFrame frame) ||
                !updated.Add(visual.Surface))
                continue;
            UpdateSharedOverlaySurface(visual.Surface, frame);
        }
    }

    private SharedOverlaySurface GetSharedOverlaySurface(
        CardOverlayFrame frame)
    {
        if (sharedOverlaySurfaces.TryGetValue(
                frame.Image, out SharedOverlaySurface? surface))
            return surface;
        Size size = frame.Source.Size;
        compositionDevice!.CreateSurface(
            (uint)size.Width, (uint)size.Height,
            DxgiFormat.B8G8R8A8_UNorm,
            DxgiAlphaMode.Premultiplied,
            out IDCompositionSurface compositionSurface);
        surface = new SharedOverlaySurface(
            frame.Image, size, compositionSurface);
        sharedOverlaySurfaces.Add(frame.Image, surface);
        return surface;
    }

    private void UpdateSharedOverlaySurface(
        SharedOverlaySurface surface, CardOverlayFrame frame)
    {
        if (surface.Rendered && surface.Source == frame.Source)
            return;

        IDXGISurface updateSurface =
            surface.Surface.BeginDraw<IDXGISurface>(
                null, out Int2 offset);
        try
        {
            using (updateSurface)
            using (ID2D1Bitmap1 target =
                d2dContext!.CreateBitmapFromDxgiSurface(
                    updateSurface,
                    new BitmapProperties1(
                        new D2DPixelFormat(
                            DxgiFormat.B8G8R8A8_UNorm,
                            D2DAlphaMode.Premultiplied),
                        96, 96,
                        BitmapOptions.Target |
                        BitmapOptions.CannotDraw)))
            {
                d2dContext.Target = target;
                d2dContext.Transform =
                    Matrix3x2.CreateTranslation(offset.X, offset.Y);
                d2dContext.BeginDraw();
                d2dContext.Clear(new D2DColor(0, 0, 0, 0));
                d2dContext.DrawBitmap(
                    GetBitmap(frame.Image),
                    ToRaw(new DrawingRectangle(
                        Point.Empty, surface.Size)),
                    1, Vortice.Direct2D1.InterpolationMode.Linear,
                    frame.Source, Matrix4x4.Identity);
                d2dContext.EndDraw();
                d2dContext.Target = null;
                d2dContext.Transform = Matrix3x2.Identity;
            }
        }
        finally
        {
            surface.Surface.EndDraw();
        }
        surface.Source = frame.Source;
        surface.Rendered = true;
        sharedOverlayDrawCount++;
    }

    private static void SetOverlayClip(
        IDCompositionRectangleClip clip, Size size, bool rounded)
    {
        clip.SetLeft(0);
        clip.SetTop(0);
        clip.SetRight(size.Width);
        clip.SetBottom(size.Height);
        float radius = rounded ? size.Width * 9f / 162f : 0;
        clip.SetTopLeftRadiusX(radius);
        clip.SetTopLeftRadiusY(radius);
        clip.SetTopRightRadiusX(radius);
        clip.SetTopRightRadiusY(radius);
        clip.SetBottomLeftRadiusX(radius);
        clip.SetBottomLeftRadiusY(radius);
        clip.SetBottomRightRadiusX(radius);
        clip.SetBottomRightRadiusY(radius);
    }

    private sealed class QueueSurface(
        Control host,
        IDXGISwapChain1 swapChain,
        ID2D1Bitmap1 targetBitmap,
        IDCompositionTarget compositionTarget,
        IDCompositionVisual compositionVisual) : IDisposable
    {
        internal Control Host { get; } = host;
        internal IDXGISwapChain1 SwapChain { get; } = swapChain;
        internal ID2D1Bitmap1 TargetBitmap { get; set; } = targetBitmap;
        internal IDCompositionTarget CompositionTarget { get; } =
            compositionTarget;
        internal IDCompositionVisual CompositionVisual { get; } =
            compositionVisual;
        internal Size Size { get; set; } = ValidSize(host.ClientSize);
        internal List<(
            PictureBox Picture, string CardTag, bool Playing)> Cards { get; }
            = [];

        public void Dispose()
        {
            TargetBitmap.Dispose();
            CompositionVisual.Dispose();
            CompositionTarget.Dispose();
            SwapChain.Dispose();
        }
    }

    private readonly ImageListView list;
    private readonly CardRenderer renderer;
    private readonly Dictionary<DrawingBitmap, ID2D1Bitmap1> bitmaps =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, ID2D1SolidColorBrush> brushes = [];
    private readonly Dictionary<(
        string Family, float Size, FontWeight Weight),
        IDWriteTextFormat> textFormats = [];
    private readonly Dictionary<DrawingBitmap, SharedOverlaySurface>
        sharedOverlaySurfaces =
            new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, CardOverlayVisual> cardOverlayVisuals = [];
    private readonly Dictionary<Control, QueueSurface> queueSurfaces = [];
    private QueueSurface? previewSurface;
    private Func<CardOverlayFrame?>? previewFrame;
    private DrawingBitmap? renderedPreviewImage;
    private DrawingRectangle renderedPreviewSource;
    private bool renderedPreview;
    private ID3D11Device? d3dDevice;
    private IDXGIDevice? dxgiDevice;
    private IDXGIFactory2? dxgiFactory;
    private ID2D1Factory1? d2dFactory;
    private ID2D1Device? d2dDevice;
    private ID2D1DeviceContext? d2dContext;
    private IDWriteFactory? dwriteFactory;
    private IDXGISwapChain1? swapChain;
    private ID2D1Bitmap1? targetBitmap;
    private IDCompositionDevice? compositionDevice;
    private IDCompositionTarget? compositionTarget;
    private IDCompositionVisual? compositionVisual;
    private Size surfaceSize;
    private int overlayGeneration = -1;
    private int renderedGpuSceneVersion = -1;
    private int renderedCardCount;
    private int staticSurfaceDrawCount;
    private int sharedOverlayDrawCount;
    private bool disposed;

    internal DirectCompositionCardOverlayControl(
        ImageListView list, CardRenderer renderer)
    {
        this.list = list;
        this.renderer = renderer;
    }

    internal bool Initialize()
    {
        try
        {
            _ = list.Handle;
            surfaceSize = ValidSize(list.ClientSize);
            d3dDevice = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                Vortice.Direct3D.FeatureLevel.Level_11_1,
                Vortice.Direct3D.FeatureLevel.Level_11_0);
            dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
            dxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
            d2dDevice = d2dFactory.CreateDevice(dxgiDevice);
            d2dContext = d2dDevice.CreateDeviceContext();
            dwriteFactory =
                DWrite.DWriteCreateFactory<IDWriteFactory>();
            swapChain = dxgiFactory.CreateSwapChainForComposition(
                d3dDevice,
                new SwapChainDescription1(
                    (uint)surfaceSize.Width,
                    (uint)surfaceSize.Height,
                    DxgiFormat.B8G8R8A8_UNorm,
                    bufferUsage: Usage.RenderTargetOutput,
                    bufferCount: 2,
                    scaling: Scaling.Stretch,
                    swapEffect: SwapEffect.FlipSequential,
                    alphaMode: DxgiAlphaMode.Premultiplied));
            compositionDevice =
                DComp.DCompositionCreateDevice<IDCompositionDevice>(
                    dxgiDevice);
            compositionTarget =
                compositionDevice.CreateSurfaceFromHwnd(list.Handle, true);
            compositionVisual = compositionDevice.CreateVisual();
            compositionVisual.SetContent(swapChain);
            compositionTarget.SetRoot(compositionVisual);
            compositionDevice.Commit();
            CreateTargetBitmap();
            if (!Render())
            {
                Dispose();
                return false;
            }
            list.Paint += list_Paint;
            list.SizeChanged += list_SizeChanged;
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"DirectComposition card overlays unavailable: {exception}");
            Dispose();
            return false;
        }
    }

    private void list_Paint(object? sender, PaintEventArgs e) => Render();

    private void list_SizeChanged(object? sender, EventArgs e)
    {
        if (disposed || !list.IsHandleCreated)
            return;
        list.BeginInvoke((Action)(() =>
        {
            if (!disposed && !list.IsDisposed)
                list.Refresh();
        }));
    }

    internal void SetQueueCards(IEnumerable<(
        Control Host, PictureBox Picture, string CardTag,
        bool Playing)> cards)
    {
        if (disposed)
            return;
        try
        {
            var groups = cards
                .Where(card => !card.Host.IsDisposed &&
                    !card.Picture.IsDisposed)
                .GroupBy(card => card.Host)
                .ToDictionary(group => group.Key, group => group.ToArray());
            foreach (Control removed in queueSurfaces.Keys
                .Where(host => !groups.ContainsKey(host)).ToArray())
            {
                queueSurfaces[removed].Dispose();
                queueSurfaces.Remove(removed);
            }
            foreach ((Control host, var entries) in groups)
            {
                if (!queueSurfaces.TryGetValue(
                        host, out QueueSurface? surface))
                {
                    surface = CreateQueueSurface(host);
                    queueSurfaces.Add(host, surface);
                }
                surface.Cards.Clear();
                surface.Cards.AddRange(entries.Select(entry =>
                    (entry.Picture, entry.CardTag, entry.Playing)));
            }
            compositionDevice!.Commit();
            Render(true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"DirectComposition queue overlays unavailable: {exception}");
            Dispose();
        }
    }

    internal bool SetPreview(
        Control host, Func<CardOverlayFrame?> frame)
    {
        ClearPreview();
        if (disposed)
            return false;
        try
        {
            previewSurface = CreateQueueSurface(host);
            previewFrame = frame;
            compositionDevice!.Commit();
            return Render(true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"DirectComposition overlay preview unavailable: {exception}");
            ClearPreview();
            return false;
        }
    }

    internal void ClearPreview()
    {
        previewSurface?.Dispose();
        previewSurface = null;
        previewFrame = null;
        renderedPreviewImage = null;
        renderedPreview = false;
        if (!disposed)
            compositionDevice?.Commit();
    }

    internal bool Render(
        bool overlaysOnly = false, bool animationOnly = false)
    {
        if (disposed || d2dContext == null || swapChain == null)
            return false;
        try
        {
            bool resized = ResizeIfNeeded();
            bool overlayReloaded =
                overlayGeneration != CardOverlayLoader.Generation;
            if (overlayReloaded)
            {
                ClearSharedOverlays();
                ClearBitmaps();
                overlayGeneration = CardOverlayLoader.Generation;
            }

            bool staticChanged =
                !overlaysOnly || resized || HasPendingStaticChanges;
            if (staticChanged)
            {
                d2dContext.Target = targetBitmap;
                d2dContext.BeginDraw();
                d2dContext.Clear(new D2DColor(0, 0, 0, 0));
                DrawingRectangle viewport =
                    new(Point.Empty, list.ClientSize);
                renderedCardCount = 0;
                for (int layer = 0; layer < 2; layer++)
                {
                    foreach (ImageListViewItem item in list.Items)
                    {
                        if (list.IsItemVisible(item) ==
                                ItemVisibility.NotVisible ||
                            !renderer.TryGetGpuCard(
                                item.Index,
                                out CardRenderer.GpuCardVisual card) ||
                            !card.Bounds.IntersectsWith(viewport) ||
                            !FitsHorizontally(card.Bounds, viewport) ||
                            card.DrawText != (layer == 0))
                            continue;
                        DrawCard(item.Index, card);
                    }
                }
                d2dContext.EndDraw();
                swapChain.Present(0, PresentFlags.None);
                renderedGpuSceneVersion = renderer.GpuSceneVersion;
                staticSurfaceDrawCount++;
            }
            if (animationOnly && !staticChanged && !overlayReloaded)
                UpdateCardOverlayFrames();
            else
                UpdateCardOverlayVisuals();
            foreach (QueueSurface surface in queueSurfaces.Values)
                RenderQueueSurface(surface);
            if (previewSurface != null)
                RenderPreviewSurface(previewSurface);
            compositionDevice!.Commit();
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"DirectComposition card overlay render failed: {exception}");
            Dispose();
            return false;
        }
    }

    private void DrawCard(
        int itemIndex, CardRenderer.GpuCardVisual visual)
    {
        renderedCardCount++;
        ModelCard card = visual.Card;
        DrawingRectangle bounds = visual.Bounds;
        DrawingRectangle imageBounds = visual.ImageBounds;
        if (visual.Selected)
        {
            DrawingRectangle selected = visual.DrawText
                ? new(bounds.Left - 3, bounds.Top - 3,
                    bounds.Width + 6, bounds.Height + 6)
                : new(bounds.Left - 3, bounds.Top - 3,
                    bounds.Width + 6, bounds.Height - 28);
            d2dContext!.FillRectangle(
                ToRect(selected),
                GetBrush(renderer.highlightBrush.Color));
        }

        if (card.image != null)
        {
            float radius = Math.Max(
                2, CardRenderer.CardPixels(imageBounds, 9));
            RoundedRectangle rounded = new(
                imageBounds, radius, radius);
            using ID2D1RoundedRectangleGeometry? mask =
                Properties.Settings.Default.RoundCardCorners
                    ? d2dFactory!.CreateRoundedRectangleGeometry(rounded)
                    : null;
            if (mask != null)
            {
                LayerParameters1 layer = new()
                {
                    ContentBounds = ToRaw(imageBounds),
                    GeometricMask = mask,
                    MaskAntialiasMode = AntialiasMode.PerPrimitive,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1,
                    LayerOptions = LayerOptions1.None
                };
                d2dContext!.PushLayer(ref layer, null);
            }
            ID2D1Bitmap1 cardBitmap =
                GetBitmap((DrawingBitmap)card.image);
            d2dContext!.DrawBitmap(
                cardBitmap, ToRaw(imageBounds), 1,
                Vortice.Direct2D1.InterpolationMode.Linear,
                null, null);
            if (mask != null)
                d2dContext.PopLayer();
        }

        if (visual.DrawText)
        {
            DrawText(
                card.modelName ?? "",
                visual.NameBounds,
                "Segoe UI", visual.NameFontSize,
                renderer.labelColor);
            DrawText(
                card.outfit ?? "",
                visual.OutfitBounds,
                "Segoe UI", visual.OutfitFontSize,
                renderer.labelColor);
        }

        float statusSize = CardRenderer.CardPixels(imageBounds, 20);
        float statusTop =
            bounds.Top + CardRenderer.CardPixels(imageBounds, 3);
        int statusPixels = Math.Max(1, (int)Math.Round(statusSize));
        if (card.exclusive == true)
        {
            DrawBitmap(
                renderer.GetIconBitmap("special", statusPixels),
                imageBounds.Left, statusTop);
            if (card.hotnessLevel == "5")
                DrawBitmap(
                    renderer.GetIconBitmap("hotness", statusPixels),
                    imageBounds.Left, statusTop + statusSize * 1.12f);
        }
        else if (card.hotnessLevel == "5")
        {
            DrawBitmap(
                renderer.GetIconBitmap("hotness", statusPixels),
                imageBounds.Left, statusTop);
        }

        if (renderer.myData?.GetCardFavourite(card.name) == true)
        {
            float heartSize = CardRenderer.CardPixels(imageBounds, 28);
            float heartMargin = CardRenderer.CardPixels(
                imageBounds, 10.5f);
            int heartPixels = Math.Max(1, (int)Math.Round(heartSize));
            DrawBitmap(
                renderer.GetIconBitmap("heart", heartPixels),
                imageBounds.Right - heartSize - heartMargin,
                bounds.Top + CardRenderer.CardPixels(imageBounds, 3));
        }

        if (Properties.Settings.Default.ShowRatingStars)
        {
            int size = Math.Max(1, (int)Math.Round(Math.Min(
                CardRenderer.CardPixels(imageBounds, 28),
                imageBounds.Width / 5f)));
            DrawingRectangle stars = new(
                imageBounds.Left + (imageBounds.Width - size * 5) / 2,
                (int)Math.Round(
                    imageBounds.Top + imageBounds.Height / 2f + size / 2f),
                size * 5, size);
            renderer.SetGpuStarBounds(itemIndex, stars);
            DrawBitmap(
                renderer.GetIconBitmap(
                    "rating", size,
                    Math.Clamp((int)visual.MyRating, 0, 10)),
                stars.Left, stars.Top);
        }

        if (visual.SortText.Length != 0)
        {
            DrawingRectangle panel = visual.SortBounds;
            float radius = Math.Max(2, panel.Height / 4f);
            d2dContext!.FillRoundedRectangle(
                new RoundedRectangle(panel, radius, radius),
                GetBrush(Color.FromArgb(175, 18, 18, 18)));
            d2dContext.DrawRoundedRectangle(
                new RoundedRectangle(panel, radius, radius),
                GetBrush(Color.FromArgb(70, Color.White)));
            DrawText(
                visual.SortText, panel, "Verdana",
                visual.SortFontSize, Color.White);
        }

        if (renderer.nowPlayingTag ==
            card.modelName + "\r\n" + card.outfit)
        {
            DrawingRectangle playing = visual.PlayingBounds;
            d2dContext!.FillRectangle(
                ToRect(playing), GetBrush(Color.Green));
            d2dContext.DrawRectangle(
                ToRect(playing), GetBrush(Color.Black), 2);
            DrawText(
                "Playing", playing, "Verdana",
                visual.PlayingFontSize, Color.White,
                FontWeight.Bold);
        }
    }

    private bool VerifyCard(CardRenderer.GpuCardVisual visual)
    {
        d2dContext!.Target = targetBitmap;
        d2dContext.BeginDraw();
        d2dContext.Clear(new D2DColor(0, 0, 0, 0));
        renderedCardCount = 0;
        DrawCard(0, visual);
        d2dContext.EndDraw();
        swapChain!.Present(0, PresentFlags.None);
        return renderedCardCount == 1;
    }

    internal bool HasPendingStaticChanges =>
        renderer.GpuSceneVersion != renderedGpuSceneVersion;

    private bool VerifySharedOverlaySurface(DrawingBitmap sheet)
    {
        CardOverlayFrame first = new(
            sheet, new DrawingRectangle(0, 0, 162, 242));
        CardOverlayFrame second = new(
            sheet, new DrawingRectangle(162, 0, 162, 242));
        int before = sharedOverlayDrawCount;
        SharedOverlaySurface shared = GetSharedOverlaySurface(first);
        UpdateSharedOverlaySurface(shared, first);
        UpdateSharedOverlaySurface(shared, first);
        if (!ReferenceEquals(
                shared, GetSharedOverlaySurface(first)) ||
            sharedOverlayDrawCount != before + 1)
            return false;
        UpdateSharedOverlaySurface(shared, second);
        if (sharedOverlayDrawCount != before + 2)
            return false;

        using IDCompositionVisual firstVisual =
            compositionDevice!.CreateVisual();
        using IDCompositionVisual secondVisual =
            compositionDevice.CreateVisual();
        firstVisual.SetContent(shared.Surface);
        secondVisual.SetContent(shared.Surface);
        compositionVisual!.AddVisual(firstVisual, true, null);
        compositionVisual.AddVisual(secondVisual, true, null);
        compositionDevice.Commit();
        compositionVisual.RemoveVisual(firstVisual);
        compositionVisual.RemoveVisual(secondVisual);
        compositionDevice.Commit();
        return true;
    }

    private Color ReadBitmapPixel(ID2D1Bitmap1 source, int x, int y)
    {
        using ID2D1Bitmap1 readable = d2dContext!.CreateBitmap(
            source.PixelSize,
            IntPtr.Zero, 0,
            new BitmapProperties1(
                new D2DPixelFormat(
                    DxgiFormat.B8G8R8A8_UNorm,
                    D2DAlphaMode.Premultiplied),
                96, 96,
                BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        readable.CopyFromBitmap(source);
        MappedRectangle mapped = readable.Map(MapOptions.Read);
        try
        {
            IntPtr pixel = IntPtr.Add(
                mapped.Bits, (int)mapped.Pitch * y + x * 4);
            return Color.FromArgb(
                System.Runtime.InteropServices.Marshal.ReadByte(pixel, 3),
                System.Runtime.InteropServices.Marshal.ReadByte(pixel, 2),
                System.Runtime.InteropServices.Marshal.ReadByte(pixel, 1),
                System.Runtime.InteropServices.Marshal.ReadByte(pixel));
        }
        finally
        {
            readable.Unmap();
        }
    }

    private void DrawBitmap(
        DrawingBitmap bitmap, float left, float top)
    {
        d2dContext!.DrawBitmap(
            GetBitmap(bitmap),
            ToRaw(new DrawingRectangle(
                (int)Math.Round(left), (int)Math.Round(top),
                bitmap.Width, bitmap.Height)),
            1, Vortice.Direct2D1.InterpolationMode.Linear,
            null, null);
    }

    private void DrawText(
        string text, DrawingRectangle bounds, string family,
        float pixels, Color color,
        FontWeight weight = FontWeight.Normal)
    {
        if (text.Length == 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;
        d2dContext!.DrawText(
            text, GetTextFormat(family, pixels, weight),
            ToRect(bounds),
            GetBrush(color), DrawTextOptions.Clip);
    }

    private IDWriteTextFormat GetTextFormat(
        string family, float pixels, FontWeight weight)
    {
        var key = (family, pixels, weight);
        if (textFormats.TryGetValue(
                key, out IDWriteTextFormat? format))
            return format;
        format = dwriteFactory!.CreateTextFormat(
            family, null, weight, Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal, pixels,
            CultureInfo.CurrentUICulture.Name);
        format.TextAlignment = TextAlignment.Center;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        format.WordWrapping = WordWrapping.NoWrap;
        textFormats.Add(key, format);
        return format;
    }

    private ID2D1SolidColorBrush GetBrush(Color color)
    {
        if (brushes.TryGetValue(
                color.ToArgb(), out ID2D1SolidColorBrush? brush))
            return brush;
        brush = d2dContext!.CreateSolidColorBrush(new D2DColor(
            color.R / 255f, color.G / 255f,
            color.B / 255f, color.A / 255f));
        brushes.Add(color.ToArgb(), brush);
        return brush;
    }

    private static Vortice.Mathematics.Rect ToRect(
        DrawingRectangle bounds) => new(
            bounds.Left, bounds.Top, bounds.Width, bounds.Height);

    private static bool FitsHorizontally(
        DrawingRectangle bounds, DrawingRectangle viewport) =>
        bounds.Left >= viewport.Left && bounds.Right <= viewport.Right;

    private static Vortice.RawRectF ToRaw(
        DrawingRectangle bounds) => new(
            bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);

    private QueueSurface CreateQueueSurface(Control host)
    {
        _ = host.Handle;
        Size size = ValidSize(host.ClientSize);
        IDXGISwapChain1 queueSwapChain =
            dxgiFactory!.CreateSwapChainForComposition(
                d3dDevice!,
                new SwapChainDescription1(
                    (uint)size.Width,
                    (uint)size.Height,
                    DxgiFormat.B8G8R8A8_UNorm,
                    bufferUsage: Usage.RenderTargetOutput,
                    bufferCount: 2,
                    scaling: Scaling.Stretch,
                    swapEffect: SwapEffect.FlipSequential,
                    alphaMode: DxgiAlphaMode.Premultiplied));
        IDCompositionTarget queueTarget =
            compositionDevice!.CreateSurfaceFromHwnd(host.Handle, true);
        IDCompositionVisual queueVisual = compositionDevice.CreateVisual();
        queueVisual.SetContent(queueSwapChain);
        queueTarget.SetRoot(queueVisual);
        return new QueueSurface(
            host,
            queueSwapChain,
            CreateTargetBitmap(queueSwapChain),
            queueTarget,
            queueVisual);
    }

    private void RenderQueueSurface(QueueSurface surface)
    {
        ResizeSurface(surface);

        d2dContext!.Target = surface.TargetBitmap;
        d2dContext.BeginDraw();
        d2dContext.Clear(new D2DColor(0, 0, 0, 0));
        if (surface.Host.Visible &&
            Properties.Settings.Default.DrawCardOverlays)
        {
            foreach ((PictureBox picture, string cardTag, bool playing)
                     in surface.Cards)
            {
                if (!picture.Visible)
                    continue;
                DrawingRectangle destination = GetZoomBounds(picture);
                destination.Offset(surface.Host.PointToClient(
                    picture.PointToScreen(Point.Empty)));
                if (CardOverlayLoader.TryGetFrame(
                        cardTag, out CardOverlayFrame frame))
                {
                    d2dContext.DrawBitmap(
                        GetBitmap(frame.Image),
                        destination,
                        1,
                        Vortice.Direct2D1.InterpolationMode.Linear,
                        frame.Source,
                        Matrix4x4.Identity);
                }
                if (playing)
                {
                    int height = Math.Min(
                        24, Math.Max(18, destination.Height / 5));
                    DrawingRectangle badge = new(
                        destination.Left, destination.Bottom - height,
                        destination.Width, height);
                    d2dContext.FillRectangle(
                        ToRect(badge),
                        GetBrush(Color.FromArgb(220, 22, 145, 70)));
                    DrawText("Playing", badge, "Segoe UI", 12, Color.White);
                }
            }
        }
        d2dContext.EndDraw();
        surface.SwapChain.Present(0, PresentFlags.None);
    }

    private void RenderPreviewSurface(QueueSurface surface)
    {
        bool resized = ResizeSurface(surface);
        CardOverlayFrame? frame = previewFrame?.Invoke();
        if (!resized &&
            (frame == null && !renderedPreview ||
             frame != null && renderedPreview &&
             ReferenceEquals(frame.Value.Image, renderedPreviewImage) &&
             frame.Value.Source == renderedPreviewSource))
            return;

        d2dContext!.Target = surface.TargetBitmap;
        d2dContext.BeginDraw();
        d2dContext.Clear(new D2DColor(0, 0, 0, 0));
        if (frame != null && surface.Host.Visible)
        {
            DrawingRectangle destination = Fit(
                surface.Host.ClientRectangle,
                frame.Value.Source.Width /
                    (float)frame.Value.Source.Height, 14);
            d2dContext.DrawBitmap(
                GetBitmap(frame.Value.Image),
                destination, 1,
                Vortice.Direct2D1.InterpolationMode.Linear,
                frame.Value.Source, Matrix4x4.Identity);
        }
        d2dContext.EndDraw();
        surface.SwapChain.Present(0, PresentFlags.None);
        renderedPreview = frame != null;
        renderedPreviewImage = frame?.Image;
        renderedPreviewSource = frame?.Source ?? default;
    }

    private bool ResizeSurface(QueueSurface surface)
    {
        Size size = ValidSize(surface.Host.ClientSize);
        if (size == surface.Size)
            return false;
        d2dContext!.Target = null;
        surface.TargetBitmap.Dispose();
        surface.SwapChain.ResizeBuffers(
            2, (uint)size.Width, (uint)size.Height,
            DxgiFormat.B8G8R8A8_UNorm);
        surface.TargetBitmap =
            CreateTargetBitmap(surface.SwapChain);
        surface.Size = size;
        return true;
    }

    private static DrawingRectangle Fit(
        DrawingRectangle bounds, float ratio, int margin)
    {
        bounds.Inflate(-margin, -margin);
        int width = Math.Min(
            bounds.Width,
            (int)Math.Round(bounds.Height * ratio));
        int height = Math.Min(
            bounds.Height,
            (int)Math.Round(width / ratio));
        return new DrawingRectangle(
            bounds.Left + (bounds.Width - width) / 2,
            bounds.Top + (bounds.Height - height) / 2,
            Math.Max(1, width), Math.Max(1, height));
    }

    internal static DrawingRectangle GetZoomBounds(PictureBox picture)
    {
        if (picture.Image == null)
            return picture.ClientRectangle;
        double scale = Math.Min(
            picture.ClientSize.Width / (double)picture.Image.Width,
            picture.ClientSize.Height / (double)picture.Image.Height);
        Size size = new(
            (int)Math.Round(picture.Image.Width * scale),
            (int)Math.Round(picture.Image.Height * scale));
        return new DrawingRectangle(
            (picture.ClientSize.Width - size.Width) / 2,
            (picture.ClientSize.Height - size.Height) / 2,
            size.Width, size.Height);
    }

    private ID2D1Bitmap1 GetBitmap(DrawingBitmap source)
    {
        if (bitmaps.TryGetValue(source, out ID2D1Bitmap1? bitmap))
            return bitmap;

        DrawingBitmap? converted = null;
        DrawingBitmap upload = source;
        if (source.PixelFormat != PixelFormat.Format32bppPArgb)
        {
            converted = new DrawingBitmap(
                source.Width, source.Height, PixelFormat.Format32bppPArgb);
            using Graphics graphics = Graphics.FromImage(converted);
            graphics.DrawImage(
                source,
                new DrawingRectangle(Point.Empty, source.Size),
                0, 0, source.Width, source.Height,
                GraphicsUnit.Pixel);
            upload = converted;
        }

        BitmapData data = upload.LockBits(
            new DrawingRectangle(Point.Empty, upload.Size),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            bitmap = d2dContext!.CreateBitmap(
                new Vortice.Mathematics.SizeI(
                    upload.Width, upload.Height),
                data.Scan0,
                (uint)data.Stride,
                new BitmapProperties1(new D2DPixelFormat(
                    DxgiFormat.B8G8R8A8_UNorm,
                    D2DAlphaMode.Premultiplied), 96, 96));
            bitmaps.Add(source, bitmap);
            return bitmap;
        }
        finally
        {
            upload.UnlockBits(data);
            converted?.Dispose();
        }
    }

    private bool ResizeIfNeeded()
    {
        Size size = ValidSize(list.ClientSize);
        if (size == surfaceSize)
            return false;
        d2dContext!.Target = null;
        targetBitmap?.Dispose();
        targetBitmap = null;
        swapChain!.ResizeBuffers(
            2, (uint)size.Width, (uint)size.Height,
            DxgiFormat.B8G8R8A8_UNorm);
        surfaceSize = size;
        CreateTargetBitmap();
        return true;
    }

    private void CreateTargetBitmap()
    {
        targetBitmap = CreateTargetBitmap(swapChain!);
        d2dContext!.Target = targetBitmap;
    }

    private ID2D1Bitmap1 CreateTargetBitmap(IDXGISwapChain1 chain)
    {
        using IDXGISurface surface = chain.GetBuffer<IDXGISurface>(0);
        return d2dContext!.CreateBitmapFromDxgiSurface(
            surface,
            new BitmapProperties1(
                new D2DPixelFormat(
                    DxgiFormat.B8G8R8A8_UNorm,
                    D2DAlphaMode.Premultiplied),
                96, 96,
                BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    private static Size ValidSize(Size size) =>
        new(Math.Max(1, size.Width), Math.Max(1, size.Height));

    private void ClearBitmaps()
    {
        foreach (ID2D1Bitmap1 bitmap in bitmaps.Values)
            bitmap.Dispose();
        bitmaps.Clear();
    }

    private void ClearSharedOverlays()
    {
        foreach (CardOverlayVisual visual in cardOverlayVisuals.Values)
        {
            compositionVisual?.RemoveVisual(visual.Visual);
            visual.Dispose();
        }
        cardOverlayVisuals.Clear();
        foreach (SharedOverlaySurface surface in
            sharedOverlaySurfaces.Values)
            surface.Dispose();
        sharedOverlaySurfaces.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        list.Paint -= list_Paint;
        list.SizeChanged -= list_SizeChanged;
        renderer.DrawWithDirectComposition = false;
        CardOverlayLoader.DrawWithDirectComposition = false;
        ClearSharedOverlays();
        ClearBitmaps();
        foreach (ID2D1SolidColorBrush brush in brushes.Values)
            brush.Dispose();
        brushes.Clear();
        foreach (IDWriteTextFormat format in textFormats.Values)
            format.Dispose();
        textFormats.Clear();
        ClearPreview();
        foreach (QueueSurface surface in queueSurfaces.Values)
            surface.Dispose();
        queueSurfaces.Clear();
        if (d2dContext != null)
            d2dContext.Target = null;
        targetBitmap?.Dispose();
        compositionVisual?.Dispose();
        compositionTarget?.Dispose();
        compositionDevice?.Dispose();
        swapChain?.Dispose();
        d2dContext?.Dispose();
        dwriteFactory?.Dispose();
        d2dDevice?.Dispose();
        d2dFactory?.Dispose();
        dxgiFactory?.Dispose();
        dxgiDevice?.Dispose();
        d3dDevice?.Dispose();
        if (!list.IsDisposed)
            list.Invalidate();
    }

    internal static bool Verify()
    {
        Vortice.Mathematics.Rect rectangle =
            ToRect(new DrawingRectangle(10, 20, 30, 40));
        if (rectangle.X != 10 || rectangle.Y != 20 ||
            rectangle.Width != 30 || rectangle.Height != 40)
            return false;
        DrawingRectangle viewport = new(0, 0, 100, 100);
        if (!FitsHorizontally(new DrawingRectangle(0, 80, 100, 40),
                viewport) ||
            FitsHorizontally(new DrawingRectangle(90, 0, 20, 40),
                viewport))
            return false;

        using DrawingBitmap image = new(
            162, 242, PixelFormat.Format24bppRgb);
        using DrawingBitmap overlaySheet = new(
            324, 242, PixelFormat.Format32bppPArgb);
        image.SetResolution(72, 72);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.CornflowerBlue);
            graphics.FillRectangle(
                Brushes.Red, 0, 0, 24, image.Height);
            graphics.FillRectangle(
                Brushes.Lime, image.Width - 24, 0, 24, image.Height);
        }
        ModelCard card = new()
        {
            name = "gpu-card-test",
            modelName = "GPU card",
            outfit = "Direct2D",
            image = image
        };
        CardRenderer.GpuCardVisual visual = new(
            card,
            new DrawingRectangle(0, 0, 140, 210),
            new DrawingRectangle(10, 0, 120, 180),
            true, true, "Newest", 8, 10, 9, 13, 14,
            new DrawingRectangle(0, 180, 140, 15),
            new DrawingRectangle(0, 195, 140, 15),
            new DrawingRectangle(30, 5, 100, 20),
            new DrawingRectangle(0, 60, 98, 22));
        using Form form = new() { ShowInTaskbar = false };
        using ImageListView list = new() {
            Dock = DockStyle.Fill, Size = new Size(320, 240) };
        using FlowLayoutPanel queue = new() {
            Dock = DockStyle.Bottom, Height = 100 };
        using PictureBox queueCard = new() {
            Size = new Size(60, 90) };
        using Panel preview = new() {
            Dock = DockStyle.Right, Width = 100 };
        queue.Controls.Add(queueCard);
        form.Controls.Add(list);
        form.Controls.Add(queue);
        form.Controls.Add(preview);
        _ = form.Handle;
        _ = list.Handle;
        using CardRenderer cardRenderer = new(
            null, "", 1, CultureInfo.InvariantCulture,
            NumberStyles.AllowDecimalPoint);
        list.SetRenderer(cardRenderer);
        using DirectCompositionCardOverlayControl control =
            new(list, cardRenderer);
        if (!control.Initialize())
        {
            Console.Error.WriteLine("GPU verify: initialization failed");
            return false;
        }
        cardRenderer.DrawWithDirectComposition = true;
        ID2D1Bitmap1 uploaded = control.GetBitmap(image);
        uploaded.GetDpi(out float bitmapDpiX, out float bitmapDpiY);
        if (uploaded.PixelSize.Width != image.Width ||
            uploaded.PixelSize.Height != image.Height ||
            bitmapDpiX != 96 || bitmapDpiY != 96)
            return false;
        Color uploadedLeft = control.ReadBitmapPixel(uploaded, 12, 100);
        Color uploadedRight = control.ReadBitmapPixel(uploaded, 150, 100);
        if (uploadedLeft.R < 200 ||
            uploadedRight.G < 200 || uploadedRight.R > 40)
        {
            Console.Error.WriteLine(
                $"Uploaded pixels: left={uploadedLeft}; " +
                $"right={uploadedRight}");
            return false;
        }
        if (!control.VerifyCard(visual))
        {
            Console.Error.WriteLine("GPU verify: card draw failed");
            return false;
        }
        if (!control.VerifySharedOverlaySurface(overlaySheet))
        {
            Console.Error.WriteLine(
                "GPU verify: shared overlay surface failed");
            return false;
        }
        int staticDraws = control.staticSurfaceDrawCount;
        if (!control.Render(true) ||
            control.staticSurfaceDrawCount != staticDraws)
            return false;
        cardRenderer.SetCardScale(1.1f);
        if (!control.HasPendingStaticChanges ||
            !control.Render(true) ||
            control.staticSurfaceDrawCount != staticDraws + 1 ||
            control.HasPendingStaticChanges)
        {
            Console.Error.WriteLine(
                "GPU verify: static invalidation failed");
            return false;
        }
        control.SetQueueCards([(queue, queueCard, "missing", false)]);
        bool rendered = control.SetPreview(preview, () => null) &&
            control.Render(true);
        control.ClearPreview();
        return rendered;
    }
}
