using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using D3D11 = Vortice.Direct3D11.D3D11;
using DComp = Vortice.DirectComposition.DComp;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;
using DxgiFormat = Vortice.DXGI.Format;
using GpuColor = Vortice.Mathematics.Color4;
using GpuViewport = Vortice.Mathematics.Viewport;

namespace IStripperQuickPlayer;

internal sealed class CustomPlayerForm : Form
{
    const int WsExNoRedirectionBitmap = 0x00200000, WsExTransparent = 0x20,
        WsExLayered = 0x80000, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    const int GwlExStyle = -20, WmNcHitTest = 0x84,
        WmMouseMove = 0x200, WmLeftButtonDown = 0x201, WmLeftButtonUp = 0x202,
        WmRightButtonDown = 0x204, WmRightButtonUp = 0x205,
        WmMiddleButtonDown = 0x207, WmMiddleButtonUp = 0x208,
        WmMouseWheel = 0x20A, WmXButtonDown = 0x20B, WmXButtonUp = 0x20C,
        WmDisplayChange = 0x7E, WmPowerBroadcast = 0x218,
        WmDpiChanged = 0x2E0, PbtApmResumeCritical = 0x6,
        PbtApmResumeSuspend = 0x7, PbtApmResumeAutomatic = 0x12,
        WmEnterSizeMove = 0x231, WmExitSizeMove = 0x232,
        HtTransparent = -1, HtClient = 1, HtCaption = 2,
        WhMouseLl = 14, HcAction = 0, VkControl = 0x11;
    delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    struct LowLevelMouseData
    {
        internal Point Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
    }
    internal sealed record AlphaHitMap(byte[] Pixels, byte[] Y, byte[] Cb, byte[] Cr,
        int Width, int Height);
    static readonly LowLevelMouseProc globalWheelProc = GlobalWheelCallback;
    static readonly object globalMouseSync = new();
    static readonly List<CustomPlayerForm> globalMousePlayers = [];
    static IntPtr globalWheelHook;
    readonly string foregroundPath;
    readonly long diagnosticId = CustomRtxDiagnostics.NewId();
    readonly string? alphaPath;
    readonly string? rvmOnnxModelPath;
    CustomRvmOnnxSettings? rvmOnnxSettings;
    readonly bool suppressErrorDialog;
    readonly double rangeStartSeconds, requestedRangeEndSeconds;
    readonly Rectangle? initialBounds;
    readonly Task<PreparedPlayback?>? preparedPlayback;
    ID3D11Device? sharedGraphicsDevice;
    readonly CancellationTokenSource cancellation = new();
    readonly System.Windows.Forms.Timer settleTimer = new() { Interval = 15 };
    readonly Stopwatch settleClock = new();
    PairedRenderer? renderer;
    AlphaHitMap? hitTestAlpha;
    volatile bool paused, locked, clickThroughLocked, pointerClickThrough, wheelResize,
        allowWheelWhileLocked;
    bool? mouseTransparent;
    bool movingWindow;
    bool systemEventsSubscribed;
    int pointerRefreshPending, displayRecoveryPending;
    int visualRefreshPending;
    bool windowConfigured;
    bool preloadRequested;
    int sizePercent, volumePercent, wheelDelta, volumeWheelDelta,
        settleStart, settleTarget;
    volatile int alphaThreshold, fullOpacityThreshold;
    volatile bool alphaAntialiasing;
    int smallPatchSize, minimumTransparentAreaRadius;
    volatile float edgeChokePixels;
    CustomVirtualGreenScreen virtualGreenScreen;
    volatile int rtxVideoSuperResolutionQuality;
    volatile bool rtxVideoHdr;
    readonly bool showRtxVideoStatus;
    VirtualGreenScreenSample[] hitTestGreenSamples;
    Task playbackTask = Task.CompletedTask;
    Action? beforeRendererAttach;
    long requestedSeekBits = BitConverter.DoubleToInt64Bits(double.NaN);
    long requestedRateBits = BitConverter.DoubleToInt64Bits(1d);
    double RangeEndSeconds => renderer == null || requestedRangeEndSeconds <= 0
        ? renderer?.Duration ?? 0
        : Math.Min(requestedRangeEndSeconds, renderer.Duration);
    internal bool HasEstablishedBounds =>
        IsEstablishedWindow(windowConfigured, Width, Height);
    internal long DiagnosticId => diagnosticId;
    internal double CurrentSeconds => Math.Max(0,
        (renderer?.CurrentTime ?? rangeStartSeconds) - rangeStartSeconds);
    internal double DurationSeconds => Math.Max(0,
        RangeEndSeconds - rangeStartSeconds);
    internal int SizePercent => sizePercent;
    internal int AlphaThreshold => alphaThreshold;
    internal bool Paused => paused;
    internal event EventHandler? PlaybackCompleted;
    internal event EventHandler<Exception>? PlaybackFailed;
    internal event EventHandler? FirstFramePresented;
    internal event EventHandler? PreloadRequested;
    internal event Action<int>? VolumeChanged;
    internal event Action<int>? SizePercentChanged;
    internal event Action<string>? RvmOnnxFallback;
    internal void SetBeforeRendererAttach(Action callback) =>
        beforeRendererAttach = callback;
    internal bool HoldFinalFrameOnCompletion;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int width, int height, int flags);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int hook, LowLevelMouseProc callback,
        IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code,
        IntPtr message, IntPtr data);
    [DllImport("user32.dll")]
    static extern short GetKeyState(int key);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    internal CustomPlayerForm(string foregroundPath, string? alphaPath,
        int playerSizePercent = 40, int volumePercent = 100,
        int alphaThreshold = CustomShowClip.DefaultAlphaThreshold,
        int fullOpacityThreshold = 200,
        bool alphaAntialiasing = true,
        bool suppressErrorDialog = false, long startMs = 0, long endMs = 0,
        Rectangle? initialBounds = null,
        Task<PreparedPlayback?>? preparedPlayback = null,
        float edgeChokePixels = 1,
        CustomVirtualGreenScreen? virtualGreenScreen = null,
        int rtxVideoSuperResolutionQuality = 0,
        bool rtxVideoHdr = false,
        bool showRtxVideoStatus = false,
        string? rvmOnnxModelPath = null,
        CustomRvmOnnxSettings? rvmOnnxSettings = null,
        CustomPlayerForm? graphicsSource = null)
    {
        this.foregroundPath = foregroundPath;
        this.alphaPath = alphaPath;
        this.rvmOnnxModelPath = rvmOnnxModelPath;
        this.rvmOnnxSettings = rvmOnnxSettings?.Clone();
        this.rtxVideoSuperResolutionQuality = Math.Clamp(
            rtxVideoSuperResolutionQuality, 0, 4);
        this.rtxVideoHdr = rtxVideoHdr;
        this.showRtxVideoStatus = showRtxVideoStatus;
        this.suppressErrorDialog = suppressErrorDialog;
        rangeStartSeconds = Math.Max(0, startMs / 1000d);
        requestedRangeEndSeconds = endMs / 1000d;
        this.initialBounds = initialBounds;
        this.preparedPlayback = preparedPlayback;
        sharedGraphicsDevice = graphicsSource?.renderer?.ShareDevice();
        sizePercent = Math.Clamp(playerSizePercent, 10, 200);
        this.volumePercent = Math.Clamp(volumePercent, 0, 100);
        this.alphaThreshold = Math.Clamp(alphaThreshold, 0, 255);
        this.fullOpacityThreshold = Math.Clamp(fullOpacityThreshold, 1, 255);
        this.alphaAntialiasing = alphaAntialiasing;
        this.edgeChokePixels = Math.Clamp(edgeChokePixels, 0, 4);
        this.virtualGreenScreen = virtualGreenScreen?.Clone() ??
            new CustomVirtualGreenScreen { Enabled = false };
        CustomRtxDiagnostics.Write("player", diagnosticId, "created",
            $"foreground=\"{foregroundPath}\" alpha=\"{alphaPath}\" " +
            $"vsr={this.rtxVideoSuperResolutionQuality} hdr={rtxVideoHdr} " +
            $"sizePercent={sizePercent} prepared={preparedPlayback != null}");
        smallPatchSize = VirtualGreenScreenMath.SmallPatchSize(
            this.virtualGreenScreen);
        minimumTransparentAreaRadius =
            VirtualGreenScreenMath.MinimumTransparentAreaRadius(
                this.virtualGreenScreen);
        hitTestGreenSamples = VirtualGreenScreenMath.Samples(
            this.virtualGreenScreen);
        Text = "QuickPlayer Custom Show";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ClientSize = new Size(2, 2);
        SetStyle(ControlStyles.Opaque, true);
        Shown += (_, _) =>
        {
            SubscribeSystemEvents();
            UpdateGlobalWheelHook();
            playbackTask = Task.Run(PlayAsync);
        };
        FormClosed += (_, _) =>
        {
            UnsubscribeSystemEvents();
            ReleaseGlobalWheelHook();
            cancellation.Cancel();
            settleTimer.Dispose();
        };
        settleTimer.Tick += (_, _) => SettleTick();
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space) TogglePause();
            else if (e.KeyCode == Keys.Escape) Close();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams p = base.CreateParams;
            p.ExStyle |= WsExNoRedirectionBitmap | WsExToolWindow |
                WsExNoActivate;
            if (UseTransparentWindowStyle(locked, clickThroughLocked))
                p.ExStyle |= WsExTransparent | WsExLayered;
            return p;
        }
    }
    static bool UseTransparentWindowStyle(bool locked, bool clickThrough) =>
        locked && clickThrough;
    protected override bool ShowWithoutActivation => true;
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        mouseTransparent = null;
        UpdateMouseTransparency();
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    async Task PlayAsync()
    {
        CustomRtxDiagnostics.Write("player", diagnosticId, "play-start");
        try
        {
            PreparedPlayback? prepared = null;
            if (preparedPlayback != null)
            {
                try
                {
                    prepared = await preparedPlayback.WaitAsync(
                        cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    _ = preparedPlayback.ContinueWith(task =>
                    {
                        if (task.Status == TaskStatus.RanToCompletion)
                            task.Result?.Dispose();
                        else
                            _ = task.Exception;
                    }, TaskScheduler.Default);
                    throw;
                }
                catch
                {
                    // A speculative preload must never prevent normal playback.
                }
            }
            renderer = prepared?.TakeRenderer();
            bool rendererWasPrepared = renderer != null;
            CustomRtxDiagnostics.Write("player", diagnosticId,
                "renderer-selected", $"prepared={rendererWasPrepared}");
            ID3D11Device? sharedDevice = Interlocked.Exchange(
                ref sharedGraphicsDevice, null);
            if (renderer == null)
            {
                try
                {
                    renderer = await Task.Run(() => new PairedRenderer(
                        foregroundPath, alphaPath, alphaThreshold,
                        fullOpacityThreshold, alphaAntialiasing, edgeChokePixels,
                        virtualGreenScreen, rvmOnnxModelPath,
                        Volatile.Read(ref rvmOnnxSettings),
                        rtxVideoSuperResolutionQuality, rtxVideoHdr,
                        sharedDevice), cancellation.Token);
                    sharedDevice = null;
                }
                finally { sharedDevice?.Dispose(); }
            }
            else sharedDevice?.Dispose();
            renderer.SetRtxVideoHdr(rtxVideoHdr, out _);
            prepared?.Dispose();
            renderer.SetVolume(volumePercent);
            renderer.SetAlphaThreshold(alphaThreshold);
            renderer.SetFullOpacityThreshold(fullOpacityThreshold);
            renderer.SetAlphaAntialiasing(alphaAntialiasing);
            renderer.SetEdgeChoke(edgeChokePixels);
            renderer.SetVirtualGreenScreen(virtualGreenScreen);
            if (showRtxVideoStatus)
                renderer.RtxVideoStatusChanged += status => BeginInvoke(() =>
                {
                    if (!IsDisposed)
                        LockStateOverlay.ShowStatusForWindow(this, status);
                });
            if (rangeStartSeconds >= RangeEndSeconds)
                throw new InvalidDataException("The custom clip time range is invalid.");
            if (!rendererWasPrepared)
            {
                renderer.Seek(rangeStartSeconds);
                // The first DirectML runs initialize the native GPU pipeline
                // and its recurrent output buffers.  Do that before starting
                // the media clock so playback never has to catch up after the
                // expensive first frames.
                await Task.Run(renderer.Prime, cancellation.Token);
            }
            Invoke(() => ConfigureWindow(renderer.Width, renderer.Height));
            (IntPtr handle, int width, int height) window =
                ((IntPtr, int, int))Invoke(() => (Handle, ClientSize.Width, ClientSize.Height));
            renderer.PrepareForAttach();
            Action? beforeAttach = Interlocked.Exchange(
                ref beforeRendererAttach, null);
            if (beforeAttach != null)
            {
                long releaseStarted = Stopwatch.GetTimestamp();
                CustomRtxDiagnostics.Write("player", diagnosticId,
                    "before-attach-start", $"renderer={renderer.DiagnosticId}");
                Invoke(beforeAttach);
                CustomRtxDiagnostics.Write("player", diagnosticId,
                    "before-attach-ok", $"renderer={renderer.DiagnosticId} " +
                    $"elapsedMs={CustomRtxDiagnostics.ElapsedMilliseconds(releaseStarted):F3}");
            }
            renderer.AttachWindow(window.handle, window.width, window.height);
            CustomRtxDiagnostics.Write("player", diagnosticId,
                "renderer-attached", $"renderer={renderer.DiagnosticId} " +
                $"window=0x{window.handle.ToInt64():X} " +
                $"size={window.width}x{window.height}");
            renderer.Play();
            bool firstFramePresented = false;
            bool rvmFallbackReported = false;
            bool refreshPausedFrame = true;
            while (!renderer.Ended && renderer.CurrentTime < RangeEndSeconds)
            {
                if (!rvmFallbackReported &&
                    renderer.RvmOnnxFallbackReason is string rvmFallback)
                {
                    rvmFallbackReported = true;
                    BeginInvoke(() => RvmOnnxFallback?.Invoke(rvmFallback));
                }
                cancellation.Token.ThrowIfCancellationRequested();
                double seek = BitConverter.Int64BitsToDouble(Interlocked.Exchange(
                    ref requestedSeekBits, BitConverter.DoubleToInt64Bits(double.NaN)));
                if (!double.IsNaN(seek))
                {
                    renderer.Seek(Math.Clamp(rangeStartSeconds + seek,
                        rangeStartSeconds, RangeEndSeconds));
                    refreshPausedFrame = true;
                }
                double rate = BitConverter.Int64BitsToDouble(Volatile.Read(ref requestedRateBits));
                if (Math.Abs(renderer.PlaybackRate - rate) > .0001) renderer.PlaybackRate = rate;
                if (paused)
                {
                    renderer.Pause();
                    if (refreshPausedFrame && renderer.TryRenderDue(
                            captureHitMap: true, out AlphaHitMap? pausedAlpha,
                            allowFutureFrame: true))
                    {
                        refreshPausedFrame = false;
                        if (pausedAlpha != null)
                        {
                            Volatile.Write(ref hitTestAlpha, pausedAlpha);
                            QueuePointerTransparencyRefresh();
                        }
                        if (!firstFramePresented)
                        {
                            firstFramePresented = true;
                            BeginInvoke(() => FirstFramePresented?.Invoke(
                                this, EventArgs.Empty));
                        }
                    }
                    else if (Interlocked.Exchange(
                            ref visualRefreshPending, 0) != 0)
                    {
                        renderer.RefreshCurrentFrame();
                    }
                    else await Task.Delay(15, cancellation.Token);
                    continue;
                }
                Interlocked.Exchange(ref visualRefreshPending, 0);
                renderer.Play();
                if (!preloadRequested &&
                    RangeEndSeconds - renderer.CurrentTime <= 5)
                {
                    preloadRequested = true;
                    BeginInvoke(() => PreloadRequested?.Invoke(
                        this, EventArgs.Empty));
                }
                bool captureHitMap = !(locked && clickThroughLocked) ||
                    allowWheelWhileLocked;
                if (renderer.TryRenderDue(captureHitMap, out AlphaHitMap? alpha))
                {
                    if (alpha != null)
                    {
                        Volatile.Write(ref hitTestAlpha, alpha);
                        QueuePointerTransparencyRefresh();
                    }
                    if (!firstFramePresented)
                    {
                        firstFramePresented = true;
                        CustomRtxDiagnostics.Write("player", diagnosticId,
                            "first-frame", $"renderer={renderer.DiagnosticId}");
                        BeginInvoke(() => FirstFramePresented?.Invoke(
                            this, EventArgs.Empty));
                    }
                }
                else await Task.Delay(1, cancellation.Token);
            }
            renderer.Pause();
            CustomRtxDiagnostics.Write("player", diagnosticId,
                "play-completed", $"renderer={renderer.DiagnosticId} " +
                $"time={renderer.CurrentTime:F3}");
            if (!IsDisposed) BeginInvoke(() =>
            {
                PlaybackCompleted?.Invoke(this, EventArgs.Empty);
                if (!HoldFinalFrameOnCompletion) Close();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            CustomRtxDiagnostics.Write("player", diagnosticId,
                "play-failed", error: error);
            if (!IsDisposed) BeginInvoke(() =>
            {
                PlaybackFailed?.Invoke(this, error);
                if (!suppressErrorDialog)
                    MessageBox.Show(error.Message, "Custom Show Player",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            });
        }
        finally
        {
            CustomRtxDiagnostics.Write("player", diagnosticId,
                "play-finally", $"renderer={renderer?.DiagnosticId}");
            renderer?.Dispose();
            renderer = null;
            Interlocked.Exchange(ref sharedGraphicsDevice, null)?.Dispose();
        }
    }

    void ConfigureWindow(int width, int height)
    {
        Rectangle work = initialBounds is Rectangle previousBounds
            ? Screen.FromRectangle(previousBounds).WorkingArea
            : Screen.FromPoint(Cursor.Position).WorkingArea;
        double scale = work.Height * sizePercent / 100d / height;
        int w = Math.Max(2, (int)Math.Round(width * scale));
        int h = Math.Max(2, (int)Math.Round(height * scale));
        Point location = initialBounds is Rectangle previous
            ? AnchoredLocation(new Size(w, h), previous, work)
            : new Point(work.Left + (work.Width - w) / 2, work.Bottom - h);
        Bounds = new Rectangle(location, new Size(w, h));
        windowConfigured = true;
    }

    static Point AnchoredLocation(Size size, Rectangle previous,
        Rectangle workArea) => new(
        previous.Left + (previous.Width - size.Width) / 2,
        workArea.Bottom - size.Height);

    internal void TogglePause() => paused = !paused;
    internal void SetPaused(bool value) => paused = value;
    internal void SeekTo(double seconds)
    {
        double duration = renderer == null && requestedRangeEndSeconds > 0
            ? Math.Max(0, requestedRangeEndSeconds - rangeStartSeconds)
            : DurationSeconds;
        Interlocked.Exchange(ref requestedSeekBits,
            BitConverter.DoubleToInt64Bits(Math.Clamp(seconds, 0, duration)));
    }
    internal void SeekBy(double seconds) => SeekTo(CurrentSeconds + seconds);
    internal void SetRate(double rate) => Interlocked.Exchange(ref requestedRateBits,
        BitConverter.DoubleToInt64Bits(Math.Clamp(rate, .25, 4)));
    internal void SetLocked(bool value)
    {
        locked = value;
        UpdateMouseTransparency();
        QueuePointerTransparencyRefresh();
        UpdateGlobalWheelHook();
    }
    internal void SetClickThroughLocked(bool value)
    {
        clickThroughLocked = value;
        UpdateMouseTransparency();
        QueuePointerTransparencyRefresh();
        UpdateGlobalWheelHook();
    }
    internal void SetWheelResize(bool value)
    {
        wheelResize = value;
        wheelDelta = 0;
        UpdateGlobalWheelHook();
    }
    internal void SetAllowWheelWhileLocked(bool value)
    {
        allowWheelWhileLocked = value;
        wheelDelta = 0;
        volumeWheelDelta = 0;
    }
    internal void SetVolumePercent(int percent)
    {
        volumePercent = Math.Clamp(percent, 0, 100);
        renderer?.SetVolume(volumePercent);
    }
    internal void SetAlphaThreshold(int value)
    {
        alphaThreshold = Math.Clamp(value, 0, 255);
        renderer?.SetAlphaThreshold(alphaThreshold);
        RequestVisualRefresh();
        UpdateMouseTransparency();
        QueuePointerTransparencyRefresh();
    }
    internal void SetFullOpacityThreshold(int value)
    {
        fullOpacityThreshold = Math.Clamp(value, 1, 255);
        renderer?.SetFullOpacityThreshold(fullOpacityThreshold);
        RequestVisualRefresh();
    }
    internal void SetAlphaAntialiasing(bool enabled)
    {
        alphaAntialiasing = enabled;
        renderer?.SetAlphaAntialiasing(enabled);
        RequestVisualRefresh();
    }
    internal void SetEdgeChoke(float pixels)
    {
        edgeChokePixels = Math.Clamp(pixels, 0, 4);
        renderer?.SetEdgeChoke(edgeChokePixels);
        RequestVisualRefresh();
        UpdateMouseTransparency();
        QueuePointerTransparencyRefresh();
    }

    internal void SetVirtualGreenScreen(CustomVirtualGreenScreen? settings)
    {
        CustomVirtualGreenScreen next = settings?.Clone() ??
            new CustomVirtualGreenScreen { Enabled = false };
        Volatile.Write(ref virtualGreenScreen, next);
        Volatile.Write(ref hitTestGreenSamples,
            VirtualGreenScreenMath.Samples(next));
        Volatile.Write(ref smallPatchSize,
            VirtualGreenScreenMath.SmallPatchSize(next));
        Volatile.Write(ref minimumTransparentAreaRadius,
            VirtualGreenScreenMath.MinimumTransparentAreaRadius(next));
        renderer?.SetVirtualGreenScreen(next);
        RequestVisualRefresh();
        UpdateMouseTransparency();
        QueuePointerTransparencyRefresh();
    }

    internal Task SetRvmOnnxSettingsAsync(CustomRvmOnnxSettings settings)
    {
        CustomShowStore.ValidateRvmOnnxSettings(settings);
        CustomRvmOnnxSettings next = settings.Clone();
        Volatile.Write(ref rvmOnnxSettings, next);
        PairedRenderer? current = renderer;
        return current == null ? Task.CompletedTask :
            Task.Run(() => current.SetRvmOnnxSettings(next));
    }

    internal bool SetRtxVideoHdr(bool enabled, out string? failure)
    {
        CustomRtxDiagnostics.Write("player", diagnosticId, "hdr-request",
            $"enabled={enabled} renderer={renderer?.DiagnosticId}");
        rtxVideoHdr = enabled;
        PairedRenderer? current = renderer;
        if (current == null)
        {
            failure = null;
            return true;
        }
        bool applied = current.SetRtxVideoHdr(enabled, out failure);
        CustomRtxDiagnostics.Write("player", diagnosticId, "hdr-result",
            $"enabled={enabled} applied={applied} failure=\"{failure}\"");
        return applied;
    }
    internal bool SetRtxVideoSuperResolutionQuality(int quality,
        out string? failure)
    {
        int requested = Math.Clamp(quality, 0, 4);
        rtxVideoSuperResolutionQuality = requested;
        PairedRenderer? current = renderer;
        if (current == null)
        {
            failure = null;
            return true;
        }
        return current.SetRtxVideoSuperResolutionQuality(requested,
            out failure);
    }

    void RequestVisualRefresh() =>
        Interlocked.Exchange(ref visualRefreshPending, 1);
    internal void SetSizePercent(int percent, int minimumPercent = 10,
        bool notifyChange = true)
    {
        settleTimer.Stop();
        int next = Math.Clamp(percent, minimumPercent, 200);
        if (next == sizePercent) return;
        sizePercent = next;
        if (renderer != null)
        {
            Rectangle work = Screen.FromHandle(Handle).WorkingArea;
            double scale = work.Height * sizePercent / 100d / renderer.Height;
            int width = Math.Max(2, (int)Math.Round(renderer.Width * scale));
            int height = Math.Max(2, (int)Math.Round(renderer.Height * scale));
            CustomRtxDiagnostics.Write("player", diagnosticId,
                "size-request", $"percent={next} output={width}x{height} " +
                $"renderer={renderer.DiagnosticId}");
            SetWindowPos(Handle, new IntPtr(-1), Left + (Width - width) / 2,
                work.Bottom - height, width, height, 0x10 | 0x40);
            renderer.ResizeOutput(width, height);
            LockStateOverlay.ShowTextForWindow(this, $"{sizePercent}%");
        }
        if (notifyChange)
            SizePercentChanged?.Invoke(sizePercent);
    }
    internal void ClosePlayer() { if (!IsDisposed) BeginInvoke(Close); }
    internal void PrepareRtxHandoff()
    {
        long started = Stopwatch.GetTimestamp();
        CustomRtxDiagnostics.Write("player", diagnosticId, "handoff-request",
            $"renderer={renderer?.DiagnosticId}");
        renderer?.PrepareRtxHandoff();
        CustomRtxDiagnostics.Write("player", diagnosticId, "handoff-returned",
            $"renderer={renderer?.DiagnosticId} " +
            $"elapsedMs={CustomRtxDiagnostics.ElapsedMilliseconds(started):F3}");
    }
    internal bool PrepareRtxHandoffTo(CustomPlayerForm target)
    {
        PairedRenderer? sourceRenderer = renderer;
        PairedRenderer? targetRenderer = target.renderer;
        if (sourceRenderer == null || targetRenderer == null)
            return false;
        bool transferred = sourceRenderer.TransferRtxSessionTo(targetRenderer);
        CustomRtxDiagnostics.Write("player", diagnosticId,
            "handoff-transfer", $"target={target.diagnosticId} " +
            $"transferred={transferred}");
        return transferred;
    }
    internal async Task ClosePlayerAsync()
    {
        cancellation.Cancel();
        if (!IsDisposed)
        {
            if (InvokeRequired) BeginInvoke(Close); else Close();
        }
        await playbackTask;
    }
    internal void HidePlayer() { if (!IsDisposed) BeginInvoke(Hide); }
    internal void ShowPlayer() { if (!IsDisposed) BeginInvoke(Show); }

    internal static Task<PreparedPlayback?> PrepareAsync(string foregroundPath,
        string? alphaPath, int alphaThreshold, int fullOpacityThreshold,
        bool alphaAntialiasing, float edgeChokePixels,
        CustomVirtualGreenScreen? virtualGreenScreen,
        long startMs,
        CancellationToken cancellationToken,
        int rtxVideoSuperResolutionQuality = 0,
        bool rtxVideoHdr = false,
        string? rvmOnnxModelPath = null,
        CustomRvmOnnxSettings? rvmOnnxSettings = null,
        CustomPlayerForm? graphicsSource = null)
    {
        ID3D11Device? sharedDevice =
            graphicsSource?.renderer?.ShareDevice();
        return Task.Run(() =>
        {
            PairedRenderer? renderer = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                renderer = new PairedRenderer(foregroundPath, alphaPath,
                    alphaThreshold, fullOpacityThreshold, alphaAntialiasing,
                    edgeChokePixels,
                    virtualGreenScreen, rvmOnnxModelPath, rvmOnnxSettings,
                    rtxVideoSuperResolutionQuality, rtxVideoHdr,
                    sharedDevice);
                sharedDevice = null;
                renderer.Seek(Math.Max(0, startMs / 1000d));
                renderer.Prime();
                return (PreparedPlayback?)new PreparedPlayback(renderer);
            }
            catch
            {
                renderer?.Dispose();
                throw;
            }
            finally { sharedDevice?.Dispose(); }
        }, CancellationToken.None);
    }

    bool IsAlphaVisible(Point point)
    {
        AlphaHitMap? alpha = Volatile.Read(ref hitTestAlpha);
        if (alpha == null || renderer == null || ClientSize.Width <= 0 ||
            ClientSize.Height <= 0 || point.X < 0 || point.Y < 0 ||
            point.X >= ClientSize.Width || point.Y >= ClientSize.Height) return false;
        int x = Math.Min(alpha.Width - 1, point.X * alpha.Width / ClientSize.Width);
        int y = Math.Min(alpha.Height - 1, point.Y * alpha.Height / ClientSize.Height);
        int offset = y * alpha.Width + x;
        byte sourceAlpha = ChokedHitTestAlpha(alpha, x, y);
        VirtualGreenScreenSample[] samples =
            Volatile.Read(ref hitTestGreenSamples);
        byte keyed = VirtualGreenScreenMath.Apply(sourceAlpha, alpha.Y[offset],
            alpha.Cb[offset], alpha.Cr[offset], alphaThreshold,
            fullOpacityThreshold, samples);
        bool visible = keyed > 0;
        int patch = Volatile.Read(ref smallPatchSize);
        if (visible && patch > 0 && renderer != null)
        {
            int radius = Math.Max(1, (int)Math.Round(
                patch * alpha.Width / (double)renderer.Width));
            visible = OpaqueDiskSupport(alpha, x, y, radius, samples) >=
                    VirtualGreenScreenMath.SmallPatchMinimumDiskSupport &&
                ConnectedOpaqueRays(alpha, x, y, radius, samples) >=
                    VirtualGreenScreenMath.SmallPatchMinimumConnectedRays;
        }
        int transparentRadius = Volatile.Read(ref minimumTransparentAreaRadius);
        byte source = ApplyOpacityThresholds(sourceAlpha, alphaThreshold,
            fullOpacityThreshold);
        if (VirtualGreenScreenMath.ReducesAlpha(source, keyed) &&
            transparentRadius > 0 && renderer != null)
        {
            int radius = Math.Max(1, (int)Math.Round(
                transparentRadius * alpha.Width / (double)renderer.Width));
            if (ConnectedRemovedRays(alpha, x, y, radius, samples) <
                    VirtualGreenScreenMath.MinimumTransparentConnectedRays ||
                RemovedDiskSupport(alpha, x, y, radius, samples) <
                    VirtualGreenScreenMath.MinimumTransparentDiskSupport)
                visible = true;
        }
        return visible;
    }

    int OpaqueDiskSupport(AlphaHitMap alpha, int x, int y, int radius,
        ReadOnlySpan<VirtualGreenScreenSample> samples)
    {
        int support = 0;
        foreach (PointF direction in VirtualGreenScreenMath.SmallPatchDiskOffsets)
        {
            int sx = Math.Clamp(x + (int)Math.Round(direction.X * radius),
                0, alpha.Width - 1);
            int sy = Math.Clamp(y + (int)Math.Round(direction.Y * radius),
                0, alpha.Height - 1);
            int offset = sy * alpha.Width + sx;
            if (VirtualGreenScreenMath.Apply(alpha.Pixels[offset], alpha.Y[offset],
                alpha.Cb[offset], alpha.Cr[offset], alphaThreshold,
                fullOpacityThreshold, samples) > 0) support++;
        }
        return support;
    }

    int ConnectedOpaqueRays(AlphaHitMap alpha, int x, int y, int radius,
        ReadOnlySpan<VirtualGreenScreenSample> samples)
    {
        int connectedRays = 0;
        foreach (PointF direction in
            VirtualGreenScreenMath.TransparentAreaDirections)
        {
            bool connected = true;
            for (int step = 1;
                step <= VirtualGreenScreenMath.TransparentAreaSamplesPerRay;
                step++)
            {
                float distance = radius * step /
                    (float)VirtualGreenScreenMath.TransparentAreaSamplesPerRay;
                int sx = Math.Clamp(x +
                    (int)Math.Round(direction.X * distance), 0, alpha.Width - 1);
                int sy = Math.Clamp(y +
                    (int)Math.Round(direction.Y * distance), 0, alpha.Height - 1);
                int offset = sy * alpha.Width + sx;
                if (VirtualGreenScreenMath.Apply(alpha.Pixels[offset],
                    alpha.Y[offset], alpha.Cb[offset], alpha.Cr[offset], alphaThreshold,
                    fullOpacityThreshold, samples) > 0) continue;
                connected = false;
                break;
            }
            if (connected) connectedRays++;
        }
        return connectedRays;
    }

    int ConnectedRemovedRays(AlphaHitMap alpha, int x, int y, int radius,
        ReadOnlySpan<VirtualGreenScreenSample> samples)
    {
        int connectedRays = 0;
        foreach (PointF direction in
            VirtualGreenScreenMath.TransparentAreaDirections)
        {
            bool connected = true;
            for (int step = 1;
                step <= VirtualGreenScreenMath.TransparentAreaSamplesPerRay;
                step++)
            {
                float distance = radius * step /
                    (float)VirtualGreenScreenMath.TransparentAreaSamplesPerRay;
                int sx = Math.Clamp(x +
                    (int)Math.Round(direction.X * distance), 0, alpha.Width - 1);
                int sy = Math.Clamp(y +
                    (int)Math.Round(direction.Y * distance), 0, alpha.Height - 1);
                int offset = sy * alpha.Width + sx;
                byte source = ApplyOpacityThresholds(alpha.Pixels[offset],
                    alphaThreshold, fullOpacityThreshold);
                byte keyed = VirtualGreenScreenMath.Apply(alpha.Pixels[offset],
                    alpha.Y[offset], alpha.Cb[offset], alpha.Cr[offset], alphaThreshold,
                    fullOpacityThreshold, samples);
                if (VirtualGreenScreenMath.ReducesAlpha(source, keyed)) continue;
                connected = false;
                break;
            }
            if (connected) connectedRays++;
        }
        return connectedRays;
    }

    int RemovedDiskSupport(AlphaHitMap alpha, int x, int y, int radius,
        ReadOnlySpan<VirtualGreenScreenSample> samples)
    {
        int support = 0;
        foreach (PointF direction in VirtualGreenScreenMath.SmallPatchDiskOffsets)
        {
            int sx = Math.Clamp(x + (int)Math.Round(direction.X * radius),
                0, alpha.Width - 1);
            int sy = Math.Clamp(y + (int)Math.Round(direction.Y * radius),
                0, alpha.Height - 1);
            int offset = sy * alpha.Width + sx;
            byte source = ApplyOpacityThresholds(alpha.Pixels[offset],
                alphaThreshold, fullOpacityThreshold);
            byte keyed = VirtualGreenScreenMath.Apply(alpha.Pixels[offset],
                alpha.Y[offset], alpha.Cb[offset], alpha.Cr[offset], alphaThreshold,
                fullOpacityThreshold, samples);
            if (VirtualGreenScreenMath.ReducesAlpha(source, keyed)) support++;
        }
        return support;
    }

    byte ChokedHitTestAlpha(AlphaHitMap alpha, int x, int y)
    {
        if (renderer == null || edgeChokePixels <= 0)
            return alpha.Pixels[y * alpha.Width + x];
        int radius = Math.Max(1, (int)Math.Ceiling(
            edgeChokePixels * alpha.Width / renderer.Width));
        byte value = 255;
        for (int offsetY = -radius; offsetY <= radius; offsetY += radius)
            for (int offsetX = -radius; offsetX <= radius; offsetX += radius)
            {
                int sampleX = Math.Clamp(x + offsetX, 0, alpha.Width - 1);
                int sampleY = Math.Clamp(y + offsetY, 0, alpha.Height - 1);
                value = Math.Min(value,
                    alpha.Pixels[sampleY * alpha.Width + sampleX]);
            }
        return value;
    }

    void UpdateMouseTransparency()
    {
        if (!IsHandleCreated) return;
        bool transparent = locked && clickThroughLocked || pointerClickThrough;
        long style = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        const long transparentStyles = WsExTransparent | WsExLayered;
        bool styleMatches = transparent
            ? (style & transparentStyles) == transparentStyles
            : (style & transparentStyles) == 0;
        if (mouseTransparent == transparent && styleMatches) return;
        mouseTransparent = transparent;
        long updated = transparent ? style | transparentStyles :
            style & ~transparentStyles;
        if (updated != style)
        {
            SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(updated));
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                0x1 | 0x2 | 0x4 | 0x10 | 0x20);
        }
    }
    void SetPointerClickThrough(bool value)
    {
        pointerClickThrough = value;
        // Revalidate the actual HWND style even when the sampled alpha state is
        // unchanged. Windows can recreate or alter styles independently of the
        // last state cached here.
        UpdateMouseTransparency();
    }

    void QueuePointerTransparencyRefresh()
    {
        if (!IsHandleCreated || IsDisposed ||
            Interlocked.Exchange(ref pointerRefreshPending, 1) != 0) return;
        try
        {
            BeginInvoke(() =>
            {
                Interlocked.Exchange(ref pointerRefreshPending, 0);
                if (!GetCursorPos(out Point screen)) return;
                bool inside = TryClientPoint(screen, out Point client);
                bool transparent = inside && !movingWindow &&
                    !IsAlphaVisible(client);
                SetPointerClickThrough(transparent);
            });
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref pointerRefreshPending, 0);
        }
    }

    void UpdateGlobalWheelHook()
    {
        bool needed = IsHandleCreated && Visible && !IsDisposed;
        if (!needed)
        {
            ReleaseGlobalWheelHook();
            return;
        }
        lock (globalMouseSync)
        {
            if (!globalMousePlayers.Contains(this))
                globalMousePlayers.Add(this);
            if (globalWheelHook == IntPtr.Zero)
                globalWheelHook = SetWindowsHookEx(
                    WhMouseLl, globalWheelProc, GetModuleHandle(null), 0);
        }
    }

    static void ReinstallGlobalMouseHook()
    {
        lock (globalMouseSync)
        {
            if (globalWheelHook != IntPtr.Zero)
                UnhookWindowsHookEx(globalWheelHook);
            globalWheelHook = globalMousePlayers.Any(player =>
                    player.IsHandleCreated && player.Visible &&
                    !player.IsDisposed)
                ? SetWindowsHookEx(WhMouseLl, globalWheelProc,
                    GetModuleHandle(null), 0)
                : IntPtr.Zero;
        }
    }

    void SubscribeSystemEvents()
    {
        if (systemEventsSubscribed) return;
        try
        {
            SystemEvents.DisplaySettingsChanged += SystemDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += SystemPowerModeChanged;
            SystemEvents.SessionSwitch += SystemSessionSwitch;
            systemEventsSubscribed = true;
        }
        catch
        {
            SystemEvents.DisplaySettingsChanged -= SystemDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= SystemPowerModeChanged;
            SystemEvents.SessionSwitch -= SystemSessionSwitch;
        }
    }

    void UnsubscribeSystemEvents()
    {
        if (!systemEventsSubscribed) return;
        SystemEvents.DisplaySettingsChanged -= SystemDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= SystemPowerModeChanged;
        SystemEvents.SessionSwitch -= SystemSessionSwitch;
        systemEventsSubscribed = false;
    }

    void SystemDisplaySettingsChanged(object? sender, EventArgs e) =>
        QueueDisplayRecovery();

    void SystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) QueueDisplayRecovery();
    }

    void SystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
            QueueDisplayRecovery();
    }

    void QueueDisplayRecovery()
    {
        if (!IsHandleCreated || IsDisposed ||
            Interlocked.Exchange(ref displayRecoveryPending, 1) != 0) return;
        try
        {
            BeginInvoke(() =>
            {
                Interlocked.Exchange(ref displayRecoveryPending, 0);
                RecoverAfterDisplayChange();
            });
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref displayRecoveryPending, 0);
        }
    }

    void RecoverAfterDisplayChange()
    {
        if (!IsHandleCreated || IsDisposed) return;
        ReinstallGlobalMouseHook();
        mouseTransparent = null;
        UpdateMouseTransparency();
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            0x1 | 0x2 | 0x4 | 0x10 | 0x20);
        if (renderer != null && ClientSize.Width > 0 && ClientSize.Height > 0)
            try { renderer.ResizeOutput(ClientSize.Width, ClientSize.Height); }
            catch (Exception error)
            {
                Debug.WriteLine("Could not resize the custom player after a " +
                    "display change: " + error.Message);
            }
        RequestVisualRefresh();
        QueuePointerTransparencyRefresh();
    }

    void ReleaseGlobalWheelHook()
    {
        lock (globalMouseSync)
        {
            globalMousePlayers.Remove(this);
            if (globalMousePlayers.Count > 0 || globalWheelHook == IntPtr.Zero)
                return;
            UnhookWindowsHookEx(globalWheelHook);
            globalWheelHook = IntPtr.Zero;
        }
    }

    static IntPtr GlobalWheelCallback(int code, IntPtr message, IntPtr data)
    {
        uint mouseMessage = unchecked((uint)message.ToInt64());
        if (code == HcAction && IsForwardableMouseMessage(mouseMessage))
        {
            LowLevelMouseData mouse = Marshal.PtrToStructure<LowLevelMouseData>(data);
            CustomPlayerForm? player = PlayerAt(mouse.Point);
            if (player == null)
                return CallNextHookEx(globalWheelHook, code, message, data);
            bool inside = player.TryClientPoint(mouse.Point, out Point client);
            bool alphaVisible = inside && player.IsAlphaVisible(client);
            bool passThrough = inside &&
                (player.locked && player.clickThroughLocked || !alphaVisible);
            if (mouseMessage != WmMouseWheel)
                player.SetPointerClickThrough(passThrough);
            if (mouseMessage == WmMouseMove)
                return CallNextHookEx(globalWheelHook, code, message, data);
            if (mouseMessage == WmMouseWheel &&
                (!player.locked || player.allowWheelWhileLocked) &&
                ShouldCaptureWheel(inside, alphaVisible))
            {
                int delta = (short)((mouse.MouseData >> 16) & 0xffff);
                bool control = (GetKeyState(VkControl) & 0x8000) != 0;
                if (ShouldHandleHookWheel(player.locked,
                    player.allowWheelWhileLocked, player.wheelResize, control))
                {
                    try { player.BeginInvoke(() => player.HandleWheel(delta, control)); }
                    catch (InvalidOperationException) { }
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(globalWheelHook, code, message, data);
    }

    static CustomPlayerForm? PlayerAt(Point screenPoint)
    {
        lock (globalMouseSync)
        {
            for (int index = globalMousePlayers.Count - 1; index >= 0; index--)
            {
                CustomPlayerForm player = globalMousePlayers[index];
                if (player.IsDisposed || !player.Visible)
                    continue;
                if (player.TryClientPoint(screenPoint, out _))
                    return player;
            }
        }
        return null;
    }

    static bool ShouldCaptureWheel(bool insidePlayer, bool alphaVisible) =>
        insidePlayer && alphaVisible;

    bool TryClientPoint(Point screenPoint, out Point clientPoint)
    {
        clientPoint = screenPoint;
        if (!IsHandleCreated || !GetClientRect(Handle, out NativeRect rect) ||
            !ScreenToClient(Handle, ref clientPoint)) return false;
        return clientPoint.X >= rect.Left && clientPoint.Y >= rect.Top &&
            clientPoint.X < rect.Right && clientPoint.Y < rect.Bottom;
    }

    static bool ShouldHandleHookWheel(bool locked, bool allowWhileLocked,
        bool resizeEnabled, bool control) =>
        (!locked || allowWhileLocked) && (resizeEnabled || control);

    static bool IsForwardableMouseMessage(uint message) => message is
        WmMouseMove or WmLeftButtonDown or WmLeftButtonUp or WmRightButtonDown or
        WmRightButtonUp or
        WmMiddleButtonDown or WmMiddleButtonUp or WmMouseWheel or
        WmXButtonDown or WmXButtonUp;

    bool HandleWheel(int delta, bool control)
    {
        if (locked && !allowWheelWhileLocked) return false;
        if (control)
        {
            volumeWheelDelta += delta;
            int steps = volumeWheelDelta / 120;
            volumeWheelDelta -= steps * 120;
            if (steps != 0)
            {
                int next = Math.Clamp(volumePercent + steps * 5, 0, 100);
                SetVolumePercent(next);
                LockStateOverlay.ShowTextForWindow(this, $"Volume {next}%");
                VolumeChanged?.Invoke(next);
            }
            return true;
        }
        if (!wheelResize) return false;
        wheelDelta += delta;
        int resizeSteps = wheelDelta / 120;
        wheelDelta -= resizeSteps * 120;
        if (resizeSteps != 0)
            SetSizePercent(sizePercent + resizeSteps * 2);
        return true;
    }

    void StartSettle()
    {
        Rectangle work = Screen.FromHandle(Handle).WorkingArea;
        settleTimer.Stop();
        settleStart = Top;
        settleTarget = work.Bottom - Height;
        if (settleStart >= settleTarget)
        {
            Top = settleTarget;
            return;
        }
        settleClock.Restart();
        settleTimer.Start();
    }

    void SettleTick()
    {
        (int top, bool complete) = SettlePosition(
            settleStart, settleTarget, settleClock.Elapsed.TotalSeconds);
        Top = top;
        if (complete) settleTimer.Stop();
    }

    static (int Top, bool Complete) SettlePosition(
        int start, int target, double elapsed)
    {
        const double gravity = 4_000;
        if (start >= target) return (target, true);
        double distance = target - start;
        double impactTime = Math.Sqrt(2 * distance / gravity);
        if (elapsed < impactTime)
            return (start + (int)Math.Round(
                .5 * gravity * elapsed * elapsed), false);
        double bounceHeight = Math.Clamp(distance / 16, 4, 16);
        double bounceVelocity = Math.Sqrt(2 * gravity * bounceHeight);
        double bounceElapsed = elapsed - impactTime;
        if (bounceElapsed < 2 * bounceVelocity / gravity)
            return (target - (int)Math.Round(
                bounceVelocity * bounceElapsed -
                .5 * gravity * bounceElapsed * bounceElapsed), false);
        return (target, true);
    }

    protected override void WndProc(ref Message message)
    {
        bool recoverDisplay = message.Msg is WmDisplayChange or WmDpiChanged ||
            message.Msg == WmPowerBroadcast && message.WParam.ToInt32() is
                PbtApmResumeCritical or PbtApmResumeSuspend or
                PbtApmResumeAutomatic;
        if (message.Msg == WmNcHitTest)
        {
            long coordinates = message.LParam.ToInt64();
            Point point = new((short)(coordinates & 0xffff),
                (short)((coordinates >> 16) & 0xffff));
            ScreenToClient(Handle, ref point);
            message.Result = new IntPtr(HitTest(
                locked, clickThroughLocked, movingWindow,
                IsAlphaVisible(point)));
            return;
        }
        if (message.Msg == WmMouseWheel)
        {
            long value = message.WParam.ToInt64();
            int delta = (short)((value >> 16) & 0xffff);
            long coordinates = message.LParam.ToInt64();
            Point screenPoint = new((short)(coordinates & 0xffff),
                (short)((coordinates >> 16) & 0xffff));
            if (HandleWheel(delta, (value & 0xffff & 0x0008) != 0))
                return;
        }
        if (message.Msg == WmEnterSizeMove)
        {
            movingWindow = true;
            UpdateMouseTransparency();
            settleTimer.Stop();
        }
        else if (message.Msg == WmExitSizeMove)
        {
            movingWindow = false;
            UpdateMouseTransparency();
            StartSettle();
        }
        base.WndProc(ref message);
        if (recoverDisplay) QueueDisplayRecovery();
    }

    internal static bool VerifyHitTesting() =>
        HitTest(false, false, false, false) == HtTransparent &&
        HitTest(false, false, false, true) == HtCaption &&
        HitTest(true, false, false, true) == HtClient &&
        HitTest(true, true, false, true) == HtTransparent &&
        HitTest(false, false, true, false) == HtCaption &&
        !UseTransparentWindowStyle(false, false) &&
        UseTransparentWindowStyle(true, true) &&
        !IsVisibleAlpha(0, 0) && !IsVisibleAlpha(15, 16) &&
        IsVisibleAlpha(16, 16) && IsVisibleAlpha(255, 255) &&
        !ShouldCaptureWheel(true, false) &&
        ShouldCaptureWheel(true, true) &&
        !ShouldCaptureWheel(false, true) &&
        ShouldHandleHookWheel(true, true, true, false) &&
        ShouldHandleHookWheel(true, true, false, true) &&
        !ShouldHandleHookWheel(true, false, true, false) &&
        !ShouldHandleHookWheel(false, true, false, false) &&
        ApplyOpacityThresholds(10, 20, 200) == 0 &&
        ApplyOpacityThresholds(100, 20, 200) == 100 &&
        ApplyOpacityThresholds(200, 20, 200) == 255 &&
        ApplyOpacityThresholds(99, 100, 50) == 0 &&
        ApplyOpacityThresholds(100, 100, 50) == 255 &&
        PairedRenderer.VerifyShader() &&
        PairedRenderer.Alpha16ToByte(0) == 0 &&
        PairedRenderer.Alpha16ToByte(32768) == 128 &&
        PairedRenderer.Alpha16ToByte(65535) == 255 &&
        TimestampStreamToAdvance(100, 110, 20) == 0 &&
        TimestampStreamToAdvance(100, 142, 20) == -1 &&
        TimestampStreamToAdvance(142, 100, 20) == 1 &&
        PairDurationMatches(145.733333, 145.8, 1d / 30) &&
        !PairDurationMatches(145.733333, 145.85, 1d / 30) &&
        !ShouldSeekForRealtimeCatchUp(0, TimeSpan.TicksPerSecond / 50,
            TimeSpan.TicksPerMillisecond * 200) &&
        ShouldSeekForRealtimeCatchUp(0, TimeSpan.TicksPerSecond / 50,
            TimeSpan.TicksPerMillisecond * 300) &&
        Math.Clamp(205, 10, 200) == 200 &&
        SettlePosition(100, 500, 0) == (100, false) &&
        SettlePosition(100, 500, 10) == (500, true) &&
        !IsEstablishedWindow(false, 1024, 576) &&
            IsEstablishedWindow(true, 1024, 576) &&
            HitMapSize(3840, 2160, 512) == new Size(512, 288) &&
            HitMapSize(320, 240, 512) == new Size(320, 240) &&
            AnchoredLocation(new Size(100, 200),
            new Rectangle(300, 400, 200, 300),
            new Rectangle(0, 0, 1920, 1080)) == new Point(350, 880);

    static bool IsEstablishedWindow(bool configured, int width, int height) =>
        configured && width > 15 && height > 15;

    static bool PairDurationMatches(double foreground, double alpha,
        double frameDuration) => Math.Abs(foreground - alpha) <=
            Math.Max(.05, frameDuration * 2 + .002);

    static bool ShouldSeekForRealtimeCatchUp(long frameTimestamp,
        long frameDuration, long currentTimestamp) =>
        currentTimestamp - (frameTimestamp + frameDuration) >
            Math.Max(frameDuration * 4, TimeSpan.TicksPerMillisecond * 250);

    static Size HitMapSize(int width, int height, int maximumDimension)
    {
        double scale = Math.Min(1, maximumDimension /
            (double)Math.Max(1, Math.Max(width, height)));
        return new Size(Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    static int TimestampStreamToAdvance(long foreground, long alpha,
        long tolerance) => Math.Abs(foreground - alpha) <= tolerance
            ? 0 : foreground < alpha ? -1 : 1;

    static bool IsVisibleAlpha(byte alpha, int threshold) =>
        alpha > 0 && alpha >= Math.Clamp(threshold, 0, 255);

    static byte ApplyOpacityThresholds(byte alpha, int lower, int upper) =>
        alpha < Math.Clamp(lower, 0, 255) ? (byte)0 :
        alpha >= Math.Clamp(upper, 1, 255) ? (byte)255 : alpha;

    static int HitTest(bool locked, bool clickThrough,
        bool moving, bool alphaVisible) =>
        locked && clickThrough || !moving && !alphaVisible
            ? HtTransparent : locked ? HtClient : HtCaption;

    internal sealed class PreparedPlayback : IDisposable
    {
        PairedRenderer? renderer;

        internal PreparedPlayback(PairedRenderer renderer) =>
            this.renderer = renderer;

        internal PairedRenderer? TakeRenderer() =>
            Interlocked.Exchange(ref renderer, null);

        public void Dispose() =>
            Interlocked.Exchange(ref renderer, null)?.Dispose();
    }

    internal sealed class PairedRenderer : IDisposable
    {
        readonly long diagnosticId = CustomRtxDiagnostics.NewId();
        const string Shader = """
            struct O { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };
            O VSMain(uint id:SV_VertexID) { O o; float2 uv=float2((id<<1)&2,id&2); o.uv=uv; o.p=float4(uv.x*2-1,1-uv.y*2,0,1); return o; }
            Texture2D<float4> Y:register(t0); Texture2D<float2> U:register(t1); Texture2D<float2> V:register(t2); Texture2D<float> A:register(t3); Texture2D<float4> T:register(t4); Texture2D<float4> K:register(t5); Texture2D<float> M:register(t6); Texture2D<float4> E:register(t7);
            SamplerState S:register(s0);
            static const float2 Disk[16]={float2(.1767767,0),float2(-.2257722,.2068258),float2(.0345581,-.3937712),float2(.2845712,.3711728),float2(-.5222232,-.0923739),float2(.4946954,-.3146847),float2(-.1654659,.6155250),float2(-.3155615,-.6075944),float2(.6846422,.2500302),float2(-.7122561,.2940090),float2(.3433545,-.7337286),float2(.2537302,.8089320),float2(-.7647459,-.4431859),float2(.8971340,-.1972324),float2(-.5475069,.7787722),float2(-.1264868,-.9760897)};
            static const float2 Rays[8]={float2(1,0),float2(.7071068,.7071068),float2(0,1),float2(-.7071068,.7071068),float2(-1,0),float2(-.7071068,-.7071068),float2(0,-1),float2(.7071068,-.7071068)};
            float LinearChannel(float c){return c<=.04045?c/12.92:pow((c+.055)/1.055,2.4);}
            int InputFlags(){return (int)round(T.Load(int3(0,0,0)).a*255.0);}
            int InputMode(){return InputFlags()&3;}
            bool AlphaAntialiasingEnabled(){return (InputFlags()&4)!=0;}
            float2 ChromaAt(float2 uv){if(InputMode()==2){float3 c=Y.Sample(S,uv).rgb;float l=.2126*c.r+.7152*c.g+.0722*c.b;return float2(.5+(c.b-l)*.5389,.5+(c.r-l)*.6350);}float2 first=U.Sample(S,uv);return float2(first.r,InputMode()==1?first.g:V.Sample(S,uv).r);}
            float3 RgbAt(float2 uv){if(InputMode()==2)return Y.Sample(S,uv).rgb;float y=1.16438356*(Y.Sample(S,uv).r-16.0/255.0);float2 chroma=ChromaAt(uv)-.5;return saturate(float3(y+1.79274107*chroma.y,y-.21324861*chroma.x-.53290933*chroma.y,y+2.11240179*chroma.x));}
            float3 DomainAt(float2 uv,int domain){float2 chroma=ChromaAt(uv);if(domain==0)return float3(chroma,0);float3 c=RgbAt(uv);if(domain==1)return c;if(domain==2){float maximum=max(c.r,max(c.g,c.b));float minimum=min(c.r,min(c.g,c.b));float delta=maximum-minimum;float hue=0;if(delta>.000001){hue=maximum==c.r?(c.g-c.b)/delta:maximum==c.g?2+(c.b-c.r)/delta:4+(c.r-c.g)/delta;hue=frac(hue/6+1);}float saturation=maximum<=0?0:delta/maximum;float angle=hue*6.283185307;return float3(.5+cos(angle)*saturation*.5,.5+sin(angle)*saturation*.5,maximum);}c=float3(LinearChannel(c.r),LinearChannel(c.g),LinearChannel(c.b));float l=pow(max(0,.4122214708*c.r+.5363325363*c.g+.0514459929*c.b),1.0/3.0);float m=pow(max(0,.2119034982*c.r+.6806995451*c.g+.1073969566*c.b),1.0/3.0);float s=pow(max(0,.0883024619*c.r+.2817188376*c.g+.6299787005*c.b),1.0/3.0);return float3(.2104542553*l+.793617785*m-.0040720468*s,1.9779984951*l-2.428592205*m+.4505937099*s+.5,.0259040371*l+.7827717662*m-.808675766*s+.5);}
            float PaletteKeep(float2 uv,int domain,float threshold){float3 source=DomainAt(uv,domain);float keyStrength=0;float lockedStrength=0;uint width,height;K.GetDimensions(width,height);int capacity=(int)width/2;[loop]for(int n=0;n<capacity;n++){float4 meta=K.Load(int3(n+capacity,0,0));if(meta.y<.5)continue;float4 k=K.Load(int3(n,0,0));float d=distance(source,k.xyz);float keep=meta.x<=0?(d<=k.w?0:1):smoothstep(k.w,k.w+meta.x,d);float strength=1-keep;if(meta.z>.5)lockedStrength=max(lockedStrength,strength);else keyStrength=max(keyStrength,strength);}return keyStrength-lockedStrength>threshold?0:1;}
            float MatteMain(O i):SV_TARGET{float4 spatial=T.Load(int3(1,0,0));int domain=(int)round(spatial.a*255.0);return PaletteKeep(i.uv,domain,spatial.b);}
            float Supported(float2 uv,float lower){float a=A.Sample(S,uv);if(a<=0||a<lower)return 0;a*=M.Sample(S,uv);return a>0&&a>=lower?1:0;}
            float DiskSupport(float2 uv,float2 radius,float lower){float support=0;[unroll]for(int n=0;n<16;n++)support+=Supported(uv+Disk[n]*radius,lower);return support;}
            float ConnectedSupportedRays(float2 uv,float2 radius,float lower){float rays=0;[unroll]for(int n=0;n<8;n++){float connected=1;[unroll]for(int step=1;step<=4;step++)connected*=Supported(uv+Rays[n]*radius*(step*.25),lower);rays+=connected;}return rays;}
            float OutputAlpha(float a,float lower,float upper){return a<lower?0:a>=upper?1:a;}
            float AntialiasedOutputAlpha(float a,float lower,float upper){float w=max(fwidth(a),1.0/255.0);float visible=smoothstep(lower-w,lower+w,a);float opaque=smoothstep(upper-w,upper+w,a);return lerp(a*visible,1,opaque);}
            float Removed(float2 uv,float lower,float upper){float a=A.Sample(S,uv);float before=OutputAlpha(a,lower,upper);float after=OutputAlpha(a*M.Sample(S,uv),lower,upper);return after<before?1:0;}
            float RemovedDiskSupport(float2 uv,float2 radius,float lower,float upper){float support=0;[unroll]for(int n=0;n<16;n++)support+=Removed(uv+Disk[n]*radius,lower,upper);return support;}
            float ConnectedRemovedRays(float2 uv,float2 radius,float lower,float upper){float rays=0;[unroll]for(int n=0;n<8;n++){float connected=1;[unroll]for(int step=1;step<=4;step++)connected*=Removed(uv+Rays[n]*radius*(step*.25),lower,upper);rays+=connected;}return rays;}
            float4 RgbMain(O i):SV_TARGET{return float4(RgbAt(i.uv),1);}
            float4 PixelMain(O i,bool enhanced,bool hdr) { float3 c=enhanced?E.Sample(S,i.uv).rgb:RgbAt(i.uv); float a=A.Sample(S,i.uv); float4 t=T.Load(int3(0,0,0)); float r=round(t.b*255.0)*.25;uint aw,ah;A.GetDimensions(aw,ah);
              if(r>0){float2 d=r/float2(aw,ah);a=min(a,A.Sample(S,i.uv+float2(d.x,0)));a=min(a,A.Sample(S,i.uv-float2(d.x,0)));a=min(a,A.Sample(S,i.uv+float2(0,d.y)));a=min(a,A.Sample(S,i.uv-float2(0,d.y)));a=min(a,A.Sample(S,i.uv+d));a=min(a,A.Sample(S,i.uv-d));a=min(a,A.Sample(S,i.uv+float2(d.x,-d.y)));a=min(a,A.Sample(S,i.uv+float2(-d.x,d.y)));}
              float4 spatial=T.Load(int3(1,0,0));float baseAlpha=a;a*=M.Sample(S,i.uv);float keyedAlpha=a;float patch=round(spatial.r*255.0);if(patch>0&&a>0&&a>=t.r){float2 d=patch/float2(aw,ah);if(DiskSupport(i.uv,d,t.r)<4||ConnectedSupportedRays(i.uv,d,t.r)<2)a=0;}float area=round(spatial.g*255.0);if(area>0&&OutputAlpha(keyedAlpha,t.r,t.g)<OutputAlpha(baseAlpha,t.r,t.g)){float2 d=area/float2(aw,ah);if(ConnectedRemovedRays(i.uv,d,t.r,t.g)<3||RemovedDiskSupport(i.uv,d,t.r,t.g)<8)a=baseAlpha;}
              a=AlphaAntialiasingEnabled()?AntialiasedOutputAlpha(a,t.r,t.g):OutputAlpha(a,t.r,t.g); return float4((hdr?max(c,0):saturate(c))*a,a); }
            float4 PSMain(O i):SV_TARGET{return PixelMain(i,false,false);}
            float4 PSMainEnhanced(O i):SV_TARGET{return PixelMain(i,true,false);}
            float4 PSMainHdr(O i):SV_TARGET{return PixelMain(i,true,true);}
            """;
        sealed record CompiledShaders(byte[] Vertex, byte[] Matte,
            byte[] Rgb, byte[] Standard, byte[] Enhanced, byte[] Hdr);
        static readonly Lazy<CompiledShaders> compiledShaders = new(() =>
            new CompiledShaders(
                CompileShader("VSMain", "vs_5_0"),
                CompileShader("MatteMain", "ps_5_0"),
                CompileShader("RgbMain", "ps_5_0"),
                CompileShader("PSMain", "ps_5_0"),
                CompileShader("PSMainEnhanced", "ps_5_0"),
                CompileShader("PSMainHdr", "ps_5_0")),
            LazyThreadSafetyMode.ExecutionAndPublication);
        static byte[] CompileShader(string entryPoint, string profile) =>
            Compiler.Compile(Shader, entryPoint, "CustomPlayer.hlsl", profile,
                ShaderFlags.OptimizationLevel3).Span.ToArray();
        readonly string foregroundPath;
        FfmpegCpuDecoder rgb;
        readonly FfmpegCpuDecoder? alpha;
        RvmOnnxSession? rvmOnnx;
        RvmGpuSession? rvmGpu;
        CustomRvmOnnxSettings? activeRvmSettings;
        byte[]? realtimeAlpha;
        int alphaWidth, alphaHeight;
        readonly string? rvmOnnxModelPath;
        internal string? RvmOnnxFallbackReason { get; private set; }
        readonly InternalAudioPlayer? audio;
        readonly Stopwatch clock = new();
        readonly object sync = new();
        ID3D11Device? device; ID3D11DeviceContext? context;
        IDXGIDevice? dxgiDevice; IDXGIFactory2? factory; IDXGISwapChain1? swap;
        ID3D11Texture2D? back, yTex, uTex, vTex, aTex, thresholdTex, keyTex,
            matteTex, rtxInputTex, rtxOutputTex, hdrOutputTex;
        ID3D11RenderTargetView? target, matteTarget, rtxInputTarget,
            rtxOutputTarget;
        ID3D11ShaderResourceView? yView, uView, vView, aView, thresholdView,
            keyView, matteView, rtxOutputView, hdrOutputView;
        ID3D11SamplerState? sampler; ID3D11VertexShader? vs;
        ID3D11PixelShader? ps, mattePs, rgbPs, enhancedPs, hdrPs;
        RtxVideoSession? rtxVideo;
        int rtxVideoSuperResolutionQuality;
        bool rtxVideoHdr;
        string? rtxVideoStatus;
        internal event Action<string>? RtxVideoStatusChanged;
        ID3D11Device1? device1;
        ID3D11Device3? device3;
        HardwareViews? activeHardwareViews;
        HardwareViews? gpuDisplayViews;
        ID3D11Texture2D? gpuAlphaTexture;
        ID3D11ShaderResourceView? gpuAlphaView;
        IDXGIKeyedMutex? gpuAlphaMutex;
        bool hardwareVideoFrame;
        IDCompositionDevice? composition; IDCompositionTarget? compositionTarget;
        IDCompositionVisual? visual;
        bool pending, playing, disposed, frameUploaded, handoffSuspended;
        long rgbTick, alphaTick;
        int alphaThreshold, fullOpacityThreshold, smallPatchSize,
            minimumTransparentAreaRadius, greenDomain;
        bool alphaAntialiasing;
        float edgeChokePixels;
        VirtualGreenScreenSample[] greenSamples;
        VirtualGreenScreenSample[]? uploadedGreenSamples;
        int keyTextureCapacity = 1;
        int uploadedAlphaThreshold = -1, uploadedFullOpacityThreshold = -1,
            uploadedEdgeChokeQuarters = -1, uploadedSmallPatchSize = -1,
            uploadedMinimumTransparentAreaRadius = -1, uploadedGreenDomain = -1,
            uploadedInputFlags = -1;
        double clockSeconds, rate = 1;
        long publishedClockSecondsBits = BitConverter.DoubleToInt64Bits(0);
        long publishedRateBits = BitConverter.DoubleToInt64Bits(1);
        long publishedClockTimestamp;
        int publishedClockPlaying, publishedClockVersion;
        byte[] alphaRow;
        readonly byte[] yRow, cbRow, crRow;
        readonly bool alpha16;

        sealed class HardwareViews : IDisposable
        {
            internal readonly ID3D11Texture2D Texture;
            internal readonly ID3D11ShaderResourceView Y;
            internal readonly ID3D11ShaderResourceView UV;
            internal readonly IDXGIKeyedMutex KeyedMutex;
            internal HardwareViews(ID3D11Texture2D texture,
                ID3D11ShaderResourceView y, ID3D11ShaderResourceView uv)
            {
                Texture = texture;
                Y = y;
                UV = uv;
                KeyedMutex = texture.QueryInterface<IDXGIKeyedMutex>();
            }
            public void Dispose() { KeyedMutex.Dispose(); UV.Dispose(); Y.Dispose(); Texture.Dispose(); }
        }
        internal int Width => rgb.Width; internal int Height => rgb.Height;
        internal long DiagnosticId => diagnosticId;
        internal double Duration { get; }
        internal bool Ended { get; private set; }
        internal double CurrentTime => PublishedCurrentTime();
        internal double PlaybackRate
        {
            get => BitConverter.Int64BitsToDouble(
                Volatile.Read(ref publishedRateBits));
            set
            {
                lock (sync)
                {
                    UpdateClock();
                    rate = value;
                    PublishClockLocked();
                    audio?.SetRate(value, clockSeconds, playing);
                }
            }
        }

        internal PairedRenderer(string foreground, string? alphaPath,
            int alphaThreshold, int fullOpacityThreshold,
            bool alphaAntialiasing, float edgeChokePixels,
            CustomVirtualGreenScreen? virtualGreenScreen,
            string? rvmOnnxModelPath = null,
            CustomRvmOnnxSettings? rvmOnnxSettings = null,
            int rtxVideoSuperResolutionQuality = 0, bool rtxVideoHdr = false,
            ID3D11Device? sharedDevice = null)
        {
            long constructStarted = Stopwatch.GetTimestamp();
            foregroundPath = foreground;
            CustomRtxDiagnostics.Write("renderer", diagnosticId,
                "construct-start", $"foreground=\"{foreground}\" " +
                $"alpha=\"{alphaPath}\" vsr={rtxVideoSuperResolutionQuality} " +
                $"hdr={rtxVideoHdr} rvm={rvmOnnxSettings != null}");
            this.rvmOnnxModelPath = rvmOnnxModelPath;
            this.alphaThreshold = Math.Clamp(alphaThreshold, 0, 255);
            this.fullOpacityThreshold = Math.Clamp(fullOpacityThreshold, 1, 255);
            this.alphaAntialiasing = alphaAntialiasing;
            this.edgeChokePixels = Math.Clamp(edgeChokePixels, 0, 4);
            this.rtxVideoSuperResolutionQuality = Math.Clamp(
                rtxVideoSuperResolutionQuality, 0, 4);
            this.rtxVideoHdr = rtxVideoHdr;
            greenSamples = VirtualGreenScreenMath.Samples(virtualGreenScreen);
            greenDomain = (int)VirtualGreenScreenMath.Domain(virtualGreenScreen);
            smallPatchSize = VirtualGreenScreenMath.SmallPatchSize(
                virtualGreenScreen);
            minimumTransparentAreaRadius =
                VirtualGreenScreenMath.MinimumTransparentAreaRadius(
                    virtualGreenScreen);
            FfmpegCpuDecoder? preparedRgb = null;
            if (rvmOnnxSettings != null)
            {
                try
                {
                    preparedRgb = new FfmpegCpuDecoder(foreground,
                        d3d12Hardware: true);
                    (int gpuWidth, int gpuHeight) = RvmOnnxSupport.InferenceSize(
                        preparedRgb.Width, preparedRgb.Height,
                        rvmOnnxSettings.Quality);
                    rvmGpu = new RvmGpuSession(rvmOnnxModelPath ?? "",
                        gpuWidth, gpuHeight, rvmOnnxSettings);
                }
                catch
                {
                    preparedRgb?.Dispose();
                    preparedRgb = null;
                    rvmGpu?.Dispose();
                    rvmGpu = null;
                }
                activeRvmSettings = rvmOnnxSettings.Clone();
            }
            rgb = preparedRgb ?? new FfmpegCpuDecoder(foreground);
            if (rvmOnnxSettings != null)
            {
                (alphaWidth, alphaHeight) = RvmOnnxSupport.InferenceSize(
                    rgb.Width, rgb.Height, rvmOnnxSettings.Quality);
                realtimeAlpha = new byte[checked(alphaWidth * alphaHeight)];
                Array.Fill(realtimeAlpha, (byte)255);
                try
                {
                    if (rvmGpu == null)
                        rvmOnnx = new RvmOnnxSession(rvmOnnxModelPath ?? "",
                            alphaWidth, alphaHeight, rvmOnnxSettings);
                }
                catch (Exception error)
                {
                    RvmOnnxFallbackReason = error.Message;
                }
                Duration = rgb.Duration;
                alpha16 = false;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(alphaPath))
                    throw new InvalidDataException(
                        "Paired-alpha playback requires an alpha video.");
                try { alpha = new FfmpegCpuDecoder(alphaPath); }
                catch { rgb.Dispose(); throw; }
                if (rgb.Width != alpha.Width || rgb.Height != alpha.Height ||
                    Math.Abs(rgb.FrameDuration - alpha.FrameDuration) > .0005 ||
                    !PairDurationMatches(rgb.Duration, alpha.Duration,
                        rgb.FrameDuration))
                    throw new InvalidDataException("Foreground and alpha dimensions, frame rate, or duration do not match.");
                Duration = Math.Min(rgb.Duration, alpha.Duration);
                alpha16 = alpha.IsGray16;
                alphaWidth = Width;
                alphaHeight = Height;
            }
            alphaRow = new byte[checked(alphaWidth * (alpha16 ? 2 : 1))];
            yRow = new byte[Width];
            cbRow = new byte[(Width + 1) / 2];
            crRow = new byte[(Width + 1) / 2];
            audio = InternalAudioPlayer.TryOpen(foreground);
            device = sharedDevice ?? D3D11.D3D11CreateDevice(
                DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                Vortice.Direct3D.FeatureLevel.Level_11_1,
                Vortice.Direct3D.FeatureLevel.Level_11_0);
            context = device.ImmediateContext; dxgiDevice = device.QueryInterface<IDXGIDevice>();
            device1 = device.QueryInterface<ID3D11Device1>();
            device3 = device.QueryInterface<ID3D11Device3>();
            factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            PublishClockLocked();
            CustomRtxDiagnostics.Write("renderer", diagnosticId,
                "construct-ok", $"size={Width}x{Height} duration={Duration:F3} " +
                $"alphaSize={alphaWidth}x{alphaHeight} " +
                $"device=0x{device.NativePointer.ToInt64():X} " +
                $"sharedDevice={sharedDevice != null} " +
                $"elapsedMs={CustomRtxDiagnostics.ElapsedMilliseconds(constructStarted):F3}");
        }
        internal ID3D11Device ShareDevice()
        {
            lock (sync)
            {
                if (disposed || device == null)
                    throw new ObjectDisposedException(nameof(PairedRenderer));
                return device.QueryInterface<ID3D11Device>();
            }
        }

        internal void AttachWindow(IntPtr handle, int width, int height)
        {
            long attachStarted = Stopwatch.GetTimestamp();
            CustomRtxDiagnostics.Write("renderer", diagnosticId,
                "attach-start", $"window=0x{handle.ToInt64():X} " +
                $"output={width}x{height} vsr={rtxVideoSuperResolutionQuality} " +
                $"hdrRequested={rtxVideoHdr}");
            string? unavailable = null;
            long sessionStarted = Stopwatch.GetTimestamp();
            bool sessionReused = rtxVideo != null &&
                (rtxVideoSuperResolutionQuality == 0 || rtxVideo.HasVsr) &&
                (!rtxVideoHdr || rtxVideo.HasTrueHdr);
            if (!sessionReused &&
                (rtxVideoSuperResolutionQuality > 0 || rtxVideoHdr))
                rtxVideo = RtxVideoSession.TryCreate(device!,
                    enableVsr: true,
                    // Keep TrueHDR warm whenever VSR already requires an NGX
                    // session.  The render path still bypasses it while the
                    // user setting is off.
                    enableTrueHdr: true, out unavailable);
            bool hdrActive = rtxVideoHdr &&
                rtxVideo?.HasTrueHdr == true;
            CustomRtxDiagnostics.Write("renderer", diagnosticId,
                "session-created", $"session={rtxVideo != null} " +
                $"vsrActive={rtxVideo?.HasVsr == true} hdrActive={hdrActive} " +
                $"reused={sessionReused} " +
                $"unavailable=\"{unavailable}\" " +
                $"elapsedMs={CustomRtxDiagnostics.ElapsedMilliseconds(sessionStarted):F3}");
            DxgiFormat swapFormat = hdrActive
                ? DxgiFormat.R16G16B16A16_Float : DxgiFormat.B8G8R8A8_UNorm;
            swap = factory!.CreateSwapChainForComposition(device!, new SwapChainDescription1(
                (uint)width, (uint)height, swapFormat,
                bufferUsage: Usage.RenderTargetOutput, bufferCount: 2,
                scaling: Scaling.Stretch, swapEffect: SwapEffect.FlipSequential,
                alphaMode: DxgiAlphaMode.Premultiplied));
            if (hdrActive)
            {
                using IDXGISwapChain3 swap3 = swap.QueryInterface<IDXGISwapChain3>();
                swap3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
            }
            back = swap.GetBuffer<ID3D11Texture2D>(0); target = device!.CreateRenderTargetView(back);
            PresentTransparentLocked();
            yTex = Texture(Width, Height); uTex = Texture((Width+1)/2, (Height+1)/2);
            vTex = Texture((Width+1)/2, (Height+1)/2); aTex = Texture(alphaWidth, alphaHeight,
                alpha16 ? DxgiFormat.R16_UNorm : DxgiFormat.R8_UNorm);
            thresholdTex = Texture(2, 1, DxgiFormat.R8G8B8A8_UNorm);
            keyTex = Texture(2, 1,
                DxgiFormat.R32G32B32A32_Float);
            matteTex = RenderTexture((Width + 1) / 2, (Height + 1) / 2);
            if (rtxVideo?.HasVsr == true)
            {
                rtxInputTex = RenderTexture(Width, Height,
                    DxgiFormat.R8G8B8A8_UNorm);
                rtxInputTarget = device.CreateRenderTargetView(rtxInputTex);
            }
            if (rtxVideo == null &&
                (rtxVideoSuperResolutionQuality > 0 || rtxVideoHdr))
                SetRtxVideoStatus("RTX Video: Unavailable\n" + unavailable);
            else if (rtxVideoSuperResolutionQuality == 0 && !rtxVideoHdr)
                SetRtxVideoStatus("RTX Video: Off");
            yView=device.CreateShaderResourceView(yTex); uView=device.CreateShaderResourceView(uTex);
            vView=device.CreateShaderResourceView(vTex); aView=device.CreateShaderResourceView(aTex);
            thresholdView=device.CreateShaderResourceView(thresholdTex);
            keyView=device.CreateShaderResourceView(keyTex);
            matteView=device.CreateShaderResourceView(matteTex);
            matteTarget=device.CreateRenderTargetView(matteTex);
            sampler=device.CreateSamplerState(SamplerDescription.LinearClamp);
            long shaderStarted = Stopwatch.GetTimestamp();
            bool shaderCacheHit = compiledShaders.IsValueCreated;
            CompiledShaders shaders = compiledShaders.Value;
            vs=device.CreateVertexShader(shaders.Vertex);
            mattePs=device.CreatePixelShader(shaders.Matte);
            rgbPs=device.CreatePixelShader(shaders.Rgb);
            ps=device.CreatePixelShader(shaders.Standard);
            enhancedPs=device.CreatePixelShader(shaders.Enhanced);
            hdrPs=device.CreatePixelShader(shaders.Hdr);
            CustomRtxDiagnostics.Write("renderer", diagnosticId,
                "shaders-created", $"elapsedMs=" +
                $"{CustomRtxDiagnostics.ElapsedMilliseconds(shaderStarted):F3} " +
                $"cacheHit={shaderCacheHit}");
            composition=DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice!);
            composition.CreateTargetForHwnd(handle,true,out compositionTarget).CheckError();
            visual=composition.CreateVisual(); visual.SetContent(swap); compositionTarget.SetRoot(visual); composition.Commit();
            CustomRtxDiagnostics.Write("renderer", diagnosticId,
                "attach-ok", $"swap=0x{swap.NativePointer.ToInt64():X} " +
                $"format={back.Description.Format} output=" +
                $"{back.Description.Width}x{back.Description.Height} " +
                $"elapsedMs={CustomRtxDiagnostics.ElapsedMilliseconds(attachStarted):F3}");
        }
        internal void PrepareForAttach()
        {
            long started = Stopwatch.GetTimestamp();
            bool cacheHit = compiledShaders.IsValueCreated;
            _ = compiledShaders.Value;
            CustomRtxDiagnostics.Write("renderer", diagnosticId,
                "attach-prepared", $"shaderCacheHit={cacheHit} elapsedMs=" +
                $"{CustomRtxDiagnostics.ElapsedMilliseconds(started):F3}");
        }
        (IDXGISwapChain1 Swap, ID3D11Texture2D Back,
            ID3D11RenderTargetView Target) CreateSwapChain(
            int width, int height, bool hdr)
        {
            IDXGISwapChain1 nextSwap = factory!.CreateSwapChainForComposition(
                device!, new SwapChainDescription1(
                    (uint)Math.Max(2, width), (uint)Math.Max(2, height),
                    hdr ? DxgiFormat.R16G16B16A16_Float :
                        DxgiFormat.B8G8R8A8_UNorm,
                    bufferUsage: Usage.RenderTargetOutput, bufferCount: 2,
                    scaling: Scaling.Stretch,
                    swapEffect: SwapEffect.FlipSequential,
                    alphaMode: DxgiAlphaMode.Premultiplied));
            try
            {
                if (hdr)
                {
                    using IDXGISwapChain3 swap3 =
                        nextSwap.QueryInterface<IDXGISwapChain3>();
                    swap3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
                }
                ID3D11Texture2D nextBack =
                    nextSwap.GetBuffer<ID3D11Texture2D>(0);
                try
                {
                    ID3D11RenderTargetView nextTarget =
                        device!.CreateRenderTargetView(nextBack);
                    return (nextSwap, nextBack, nextTarget);
                }
                catch
                {
                    nextBack.Dispose();
                    throw;
                }
            }
            catch
            {
                nextSwap.Dispose();
                throw;
            }
        }
        void ReleaseOutputTexturesLocked()
        {
            context!.PSUnsetShaderResource(7);
            rtxOutputTarget?.Dispose();
            rtxOutputTarget = null;
            rtxOutputView?.Dispose();
            rtxOutputView = null;
            rtxOutputTex?.Dispose();
            rtxOutputTex = null;
            hdrOutputView?.Dispose();
            hdrOutputView = null;
            hdrOutputTex?.Dispose();
            hdrOutputTex = null;
        }
        void ReplaceSwapChainLocked(int width, int height, bool hdr)
        {
            long replaceStarted = Stopwatch.GetTimestamp();
            CustomRtxDiagnostics.Write("renderer", diagnosticId,
                "swap-replace-start", $"oldSwap=0x{swap?.NativePointer.ToInt64():X} " +
                $"oldFormat={back?.Description.Format} " +
                $"oldSize={back?.Description.Width}x{back?.Description.Height} " +
                $"newSize={width}x{height} hdr={hdr}");
            var next = CreateSwapChain(width, height, hdr);
            long created = Stopwatch.GetTimestamp();
            try
            {
                visual!.SetContent(next.Swap);
                composition!.Commit();
            }
            catch
            {
                next.Target.Dispose();
                next.Back.Dispose();
                next.Swap.Dispose();
                throw;
            }
            long committed = Stopwatch.GetTimestamp();

            context!.OMSetRenderTargets(
                Array.Empty<ID3D11RenderTargetView>());
            ID3D11RenderTargetView? oldTarget = target;
            ID3D11Texture2D? oldBack = back;
            IDXGISwapChain1? oldSwap = swap;
            target = next.Target;
            back = next.Back;
            swap = next.Swap;
            ReleaseOutputTexturesLocked();
            oldTarget?.Dispose();
            oldBack?.Dispose();
            oldSwap?.Dispose();
            long released = Stopwatch.GetTimestamp();
            CustomRtxDiagnostics.Write("renderer", diagnosticId,
                "swap-replace-ok", $"swap=0x{swap.NativePointer.ToInt64():X} " +
                $"format={back.Description.Format} " +
                $"size={back.Description.Width}x{back.Description.Height} " +
                $"createMs={Stopwatch.GetElapsedTime(replaceStarted, created).TotalMilliseconds:F3} " +
                $"commitMs={Stopwatch.GetElapsedTime(created, committed).TotalMilliseconds:F3} " +
                $"releaseMs={Stopwatch.GetElapsedTime(committed, released).TotalMilliseconds:F3} " +
                $"totalMs={Stopwatch.GetElapsedTime(replaceStarted, released).TotalMilliseconds:F3}");
        }
        internal bool SetRtxVideoHdr(bool enabled, out string? failure)
        {
            lock (sync)
            {
                long switchStarted = Stopwatch.GetTimestamp();
                CustomRtxDiagnostics.Write("renderer", diagnosticId,
                    "hdr-switch-start", $"requested={enabled} " +
                    $"configured={rtxVideoHdr} session={rtxVideo != null} " +
                    $"vsrActive={rtxVideo?.HasVsr == true} " +
                    $"hdrActive={rtxVideo?.HasTrueHdr == true} " +
                    $"swapFormat={back?.Description.Format}");
                failure = null;
                if (swap == null)
                {
                    rtxVideoHdr = enabled;
                    return true;
                }
                if (rtxVideoHdr == enabled)
                    return true;

                bool warmSession = rtxVideo != null &&
                    (!enabled || rtxVideo.HasTrueHdr) &&
                    (rtxVideoSuperResolutionQuality == 0 || rtxVideo.HasVsr);
                if (warmSession)
                {
                    try
                    {
                        ReplaceSwapChainLocked((int)back!.Description.Width,
                            (int)back.Description.Height, enabled);
                        rtxVideoHdr = enabled;
                        if (frameUploaded) DrawCurrentFrameLocked();
                        else PresentTransparentLocked();
                        if (!enabled && rtxVideoSuperResolutionQuality == 0)
                            SetRtxVideoStatus("RTX Video: Off");
                        CustomRtxDiagnostics.Write("renderer", diagnosticId,
                            "hdr-switch-warm-ok", $"enabled={enabled} " +
                            $"totalMs={CustomRtxDiagnostics.ElapsedMilliseconds(switchStarted):F3}");
                        return true;
                    }
                    catch (Exception error)
                    {
                        failure = error.Message;
                        CustomRtxDiagnostics.Write("renderer", diagnosticId,
                            "hdr-switch-warm-failed", error: error);
                        return false;
                    }
                }

                RtxVideoSession? previous = rtxVideo;
                rtxVideo = null;
                previous?.Dispose();
                long oldDisposed = Stopwatch.GetTimestamp();
                CustomRtxDiagnostics.Write("renderer", diagnosticId,
                    "hdr-old-session-disposed", $"hadSession={previous != null} " +
                    $"elapsedMs={Stopwatch.GetElapsedTime(switchStarted, oldDisposed).TotalMilliseconds:F3}");

                RtxVideoSession? replacement = null;
                if (rtxVideoSuperResolutionQuality > 0 || enabled)
                    replacement = RtxVideoSession.TryCreate(device!,
                        enableVsr: true,
                        enableTrueHdr: true, out failure);
                CustomRtxDiagnostics.Write("renderer", diagnosticId,
                    "hdr-new-session", $"session={replacement != null} " +
                    $"vsrActive={replacement?.HasVsr == true} " +
                    $"hdrActive={replacement?.HasTrueHdr == true} " +
                    $"failure=\"{failure}\" " +
                    $"elapsedMs={Stopwatch.GetElapsedTime(oldDisposed).TotalMilliseconds:F3}");
                if (enabled && replacement?.HasTrueHdr != true)
                {
                    rtxVideo = replacement;
                    rtxVideoHdr = false;
                    failure ??= "NVIDIA RTX Video HDR is unavailable.";
                    SetRtxVideoStatus("RTX HDR: Unavailable\n" + failure);
                    try
                    {
                        if (back!.Description.Format ==
                            DxgiFormat.R16G16B16A16_Float)
                            ReplaceSwapChainLocked(
                                (int)back.Description.Width,
                                (int)back.Description.Height, hdr: false);
                    }
                    catch (Exception error)
                    {
                        Debug.WriteLine("Could not restore the SDR custom " +
                            "player surface: " + error.Message);
                    }
                    return false;
                }

                try
                {
                    ReplaceSwapChainLocked((int)back!.Description.Width,
                        (int)back.Description.Height, enabled);
                }
                catch (Exception error)
                {
                    replacement?.Dispose();
                    rtxVideoHdr = false;
                    failure = error.Message;
                    SetRtxVideoStatus("RTX HDR: Could not switch\n" +
                        error.Message);
                    return false;
                }

                rtxVideo = replacement;
                rtxVideoHdr = enabled;
                if (rtxVideo?.HasVsr == true && rtxInputTex == null)
                {
                    rtxInputTex = RenderTexture(Width, Height,
                        DxgiFormat.R8G8B8A8_UNorm);
                    rtxInputTarget =
                        device!.CreateRenderTargetView(rtxInputTex);
                }
                try
                {
                    if (frameUploaded) DrawCurrentFrameLocked();
                    else PresentTransparentLocked();
                    if (!enabled && rtxVideoSuperResolutionQuality == 0)
                        SetRtxVideoStatus("RTX Video: Off");
                    CustomRtxDiagnostics.Write("renderer", diagnosticId,
                        "hdr-switch-ok", $"enabled={enabled} " +
                        $"vsrActive={rtxVideo?.HasVsr == true} " +
                        $"hdrActive={rtxVideo?.HasTrueHdr == true} " +
                        $"totalMs={CustomRtxDiagnostics.ElapsedMilliseconds(switchStarted):F3}");
                    return true;
                }
                catch (Exception error)
                {
                    CustomRtxDiagnostics.Write("renderer", diagnosticId,
                        "hdr-switch-draw-failed", error: error);
                    failure = error.Message;
                    SetRtxVideoStatus("RTX HDR: Switch failed\n" +
                        error.Message);
                    return false;
                }
            }
        }
        internal bool SetRtxVideoSuperResolutionQuality(int quality,
            out string? failure)
        {
            lock (sync)
            {
                int requested = Math.Clamp(quality, 0, 4);
                failure = null;
                if (rtxVideoSuperResolutionQuality == requested)
                    return true;
                if (swap == null)
                {
                    rtxVideoSuperResolutionQuality = requested;
                    return true;
                }

                if (requested == 0 || rtxVideo?.HasVsr == true)
                {
                    rtxVideoSuperResolutionQuality = requested;
                    try
                    {
                        if (frameUploaded) DrawCurrentFrameLocked();
                        else PresentTransparentLocked();
                        if (requested == 0 && !rtxVideoHdr)
                            SetRtxVideoStatus("RTX Video: Off");
                        return true;
                    }
                    catch (Exception error)
                    {
                        failure = error.Message;
                        return false;
                    }
                }

                RtxVideoSession? replacement = RtxVideoSession.TryCreate(
                    device!, enableVsr: true, enableTrueHdr: true,
                    out failure);
                if (replacement?.HasVsr != true)
                {
                    replacement?.Dispose();
                    failure ??= "NVIDIA RTX Video Super Resolution is unavailable.";
                    return false;
                }
                RtxVideoSession? previous = rtxVideo;
                rtxVideo = replacement;
                rtxVideoSuperResolutionQuality = requested;
                previous?.Dispose();
                try
                {
                    if (frameUploaded) DrawCurrentFrameLocked();
                    else PresentTransparentLocked();
                    return true;
                }
                catch (Exception error)
                {
                    failure = error.Message;
                    return false;
                }
            }
        }
        internal void PrepareRtxHandoff()
        {
            lock (sync)
            {
                long handoffStarted = Stopwatch.GetTimestamp();
                CustomRtxDiagnostics.Write("renderer", diagnosticId,
                    "handoff-start", $"session={rtxVideo != null} " +
                    $"vsrActive={rtxVideo?.HasVsr == true} " +
                    $"hdrActive={rtxVideo?.HasTrueHdr == true}");
                if (rtxVideo == null) return;
                rtxVideo.Dispose();
                rtxVideo = null;
                long destroyed = Stopwatch.GetTimestamp();
                try
                {
                    if (back?.Description.Format ==
                        DxgiFormat.R16G16B16A16_Float)
                        ReplaceSwapChainLocked((int)back.Description.Width,
                            (int)back.Description.Height, hdr: false);
                    if (frameUploaded) DrawCurrentFrameLocked();
                    else if (swap != null) PresentTransparentLocked();
                    SetRtxVideoStatus("RTX Video: Handing off to next clip");
                    long finished = Stopwatch.GetTimestamp();
                    CustomRtxDiagnostics.Write("renderer", diagnosticId,
                        "handoff-ok", $"swapFormat={back?.Description.Format} " +
                        $"destroyMs={Stopwatch.GetElapsedTime(handoffStarted, destroyed).TotalMilliseconds:F3} " +
                        $"afterDestroyMs={Stopwatch.GetElapsedTime(destroyed, finished).TotalMilliseconds:F3} " +
                        $"totalMs={Stopwatch.GetElapsedTime(handoffStarted, finished).TotalMilliseconds:F3}");
                }
                catch (Exception error)
                {
                    CustomRtxDiagnostics.Write("renderer", diagnosticId,
                        "handoff-failed", error: error);
                    Debug.WriteLine("Could not release RTX Video for clip " +
                        "handoff: " + error.Message);
                }
            }
        }
        internal bool TransferRtxSessionTo(PairedRenderer targetRenderer)
        {
            lock (sync)
            lock (targetRenderer.sync)
            {
                if (disposed || targetRenderer.disposed || rtxVideo == null ||
                    device == null || targetRenderer.device == null ||
                    device.NativePointer != targetRenderer.device.NativePointer ||
                    targetRenderer.rtxVideo != null ||
                    targetRenderer.rtxVideoSuperResolutionQuality > 0 &&
                        !rtxVideo.HasVsr ||
                    targetRenderer.rtxVideoHdr && !rtxVideo.HasTrueHdr)
                {
                    CustomRtxDiagnostics.Write("renderer", diagnosticId,
                        "session-transfer-rejected", $"target=" +
                        $"{targetRenderer.diagnosticId} session={rtxVideo != null} " +
                        $"sameDevice={device?.NativePointer == targetRenderer.device?.NativePointer} " +
                        $"targetSession={targetRenderer.rtxVideo != null}");
                    return false;
                }
                handoffSuspended = true;
                targetRenderer.rtxVideo = rtxVideo;
                rtxVideo = null;
                CustomRtxDiagnostics.Write("renderer", diagnosticId,
                    "session-transfer-ok", $"target={targetRenderer.diagnosticId} " +
                    $"device=0x{device.NativePointer.ToInt64():X} " +
                    $"vsr={targetRenderer.rtxVideo.HasVsr} " +
                    $"hdr={targetRenderer.rtxVideo.HasTrueHdr}");
                return true;
            }
        }
        ID3D11Texture2D Texture(int width,int height,DxgiFormat format=DxgiFormat.R8_UNorm) => device!.CreateTexture2D(new Texture2DDescription
            { Width=(uint)width, Height=(uint)height, MipLevels=1, ArraySize=1, Format=format,
              SampleDescription=new SampleDescription(1,0), Usage=ResourceUsage.Default, BindFlags=BindFlags.ShaderResource });
        ID3D11Texture2D RenderTexture(int width, int height,
            DxgiFormat format = DxgiFormat.R8_UNorm) =>
            device!.CreateTexture2D(new Texture2DDescription
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
        internal void Play() { lock(sync) { if(playing)return; clock.Restart(); playing=true; PublishClockLocked(); audio?.Play(clockSeconds); } }
        internal void Pause() { lock(sync) { if(!playing)return; UpdateClock(); playing=false; clock.Stop(); PublishClockLocked(); audio?.Pause(); } }
        internal void Seek(double seconds) { lock(sync) { seconds=Math.Clamp(seconds,0,Duration); rgb.Seek(seconds); alpha?.Seek(seconds); rvmOnnx?.Reset(); rvmGpu?.Reset(); clockSeconds=seconds; clock.Restart(); if(!playing)clock.Stop(); PublishClockLocked(); pending=false; Ended=false; audio?.Seek(seconds,playing); } }
        internal void Prime()
        {
            lock (sync)
            {
                if (!pending && !DecodePair()) return;
                if (alpha != null || rvmOnnx == null && rvmGpu == null) return;

                GenerateRealtimeAlphaLocked();
                // Temporal playback alternates between two recurrent output
                // sets.  Warm both so allocating the second set cannot stall
                // the first visible seconds of a clip.
                if (activeRvmSettings?.Temporal == true &&
                    (rvmOnnx != null || rvmGpu != null))
                    GenerateRealtimeAlphaLocked();

                // Warming must not leak the repeated first frame into the
                // clip's temporal history.
                rvmOnnx?.Reset();
                rvmGpu?.Reset();
            }
        }
        internal void SetVolume(int percent)=>audio?.SetVolume(percent);
        internal void SetAlphaThreshold(int value) =>
            Volatile.Write(ref alphaThreshold, Math.Clamp(value, 0, 255));
        internal void SetFullOpacityThreshold(int value) =>
            Volatile.Write(ref fullOpacityThreshold, Math.Clamp(value, 1, 255));
        internal void SetAlphaAntialiasing(bool enabled) =>
            Volatile.Write(ref alphaAntialiasing, enabled);
        internal void SetEdgeChoke(float pixels) =>
            Volatile.Write(ref edgeChokePixels, Math.Clamp(pixels, 0, 4));
        internal void SetVirtualGreenScreen(CustomVirtualGreenScreen? settings)
        {
            Volatile.Write(ref greenSamples,
                VirtualGreenScreenMath.Samples(settings));
            Volatile.Write(ref greenDomain,
                (int)VirtualGreenScreenMath.Domain(settings));
            Volatile.Write(ref smallPatchSize,
                VirtualGreenScreenMath.SmallPatchSize(settings));
            Volatile.Write(ref minimumTransparentAreaRadius,
                VirtualGreenScreenMath.MinimumTransparentAreaRadius(settings));
        }
        internal void SetRvmOnnxSettings(CustomRvmOnnxSettings settings)
        {
            CustomShowStore.ValidateRvmOnnxSettings(settings);
            if (alpha != null)
                throw new InvalidOperationException(
                    "RVM ONNX settings cannot be applied to paired-alpha playback.");
            (int width, int height) = RvmOnnxSupport.InferenceSize(
                Width, Height, settings.Quality);
            byte[] mask = new byte[checked(width * height)];
            Array.Fill(mask, (byte)255);
            RvmOnnxSession? replacement = null;
            RvmGpuSession? replacementGpu = null;
            FfmpegCpuDecoder? replacementDecoder = null;
            Exception? failure = null;
            try
            {
                replacementDecoder = new FfmpegCpuDecoder(foregroundPath,
                    d3d12Hardware: true);
                replacementDecoder.Seek(CurrentTime);
                if (!replacementDecoder.DecodeNext(out _))
                    throw new InvalidDataException(
                        "Could not restore the current hardware-decoded frame.");
                replacementGpu = new RvmGpuSession(
                    RvmOnnxSupport.ModelPathFor(settings.Model), width, height,
                    settings);
            }
            catch (Exception gpuError)
            {
                replacementGpu?.Dispose();
                replacementGpu = null;
                replacementDecoder?.Dispose();
                replacementDecoder = null;
                try
                {
                    replacement = new RvmOnnxSession(
                        RvmOnnxSupport.ModelPathFor(settings.Model),
                        width, height, settings);
                }
                catch (Exception error) { failure = new AggregateException(
                    gpuError.Message, error); }
            }
            lock (sync)
            {
                if (disposed)
                {
                    replacement?.Dispose();
                    replacementGpu?.Dispose();
                    replacementDecoder?.Dispose();
                    return;
                }
                RvmOnnxSession? previous = rvmOnnx;
                RvmGpuSession? previousGpu = rvmGpu;
                FfmpegCpuDecoder? previousDecoder = null;
                if (replacementDecoder != null)
                {
                    previousDecoder = rgb;
                    rgb = replacementDecoder;
                    replacementDecoder = null;
                    hardwareVideoFrame = true;
                }
                DisposeGpuSharedViews();
                rvmOnnx = replacement;
                rvmGpu = replacementGpu;
                activeRvmSettings = settings.Clone();
                realtimeAlpha = mask;
                alphaWidth = width;
                alphaHeight = height;
                alphaRow = new byte[checked(width * (alpha16 ? 2 : 1))];
                RvmOnnxFallbackReason = failure?.Message;
                if (device != null)
                {
                    context?.PSUnsetShaderResource(3);
                    aView?.Dispose();
                    aTex?.Dispose();
                    aTex = Texture(alphaWidth, alphaHeight,
                        alpha16 ? DxgiFormat.R16_UNorm : DxgiFormat.R8_UNorm);
                    aView = device.CreateShaderResourceView(aTex);
                }
                if (frameUploaded && aTex != null)
                {
                    GenerateRealtimeAlphaLocked();
                    if (rgb.IsHardwareDecoded) PrepareGpuDisplayFrame();
                    unsafe
                    {
                        fixed (byte* alphaPixels = realtimeAlpha!)
                            context!.UpdateSubresource(aTex, 0, null,
                                new IntPtr(alphaPixels), (uint)alphaWidth, 0);
                    }
                    DrawCurrentFrameLocked();
                }
                previous?.Dispose();
                previousGpu?.Dispose();
                previousDecoder?.Dispose();
            }
            replacementDecoder?.Dispose();
        }
        void EnsureKeyTextureCapacity(int count)
        {
            int capacity = Math.Max(1, count);
            if (capacity == keyTextureCapacity) return;
            keyView?.Dispose();
            keyTex?.Dispose();
            keyTex = Texture(checked(capacity * 2), 1,
                DxgiFormat.R32G32B32A32_Float);
            keyView = device!.CreateShaderResourceView(keyTex);
            keyTextureCapacity = capacity;
            uploadedGreenSamples = null;
        }
        internal bool RefreshCurrentFrame()
        {
            lock (sync)
            {
                if (handoffSuspended || !frameUploaded) return false;
                DrawCurrentFrameLocked();
                return true;
            }
        }
        internal unsafe bool TryRenderDue(bool captureHitMap,
            out AlphaHitMap? alphaBytes, bool allowFutureFrame = false)
        {
            alphaBytes=null; lock(sync)
            {
                if (handoffSuspended) return false;
                if(!pending && !DecodePair()) return false;
                long now=(long)Math.Round(TimeLocked()*10_000_000);
                long frame=(long)Math.Round(rgb.FrameDuration*10_000_000);
                if (alpha == null && rgbTick + frame < now)
                {
                    bool skipped = false;
                    // FFmpeg's D3D12 decoder can reject the first packet after
                    // avcodec_flush_buffers with EINVAL.  Decode/discard to
                    // catch up on hardware instead of flush-seeking it.
                    if (!rgb.IsHardwareDecoded &&
                        ShouldSeekForRealtimeCatchUp(rgbTick, frame, now))
                    {
                        rgb.Seek(now / (double)TimeSpan.TicksPerSecond);
                        pending = false;
                        if (!DecodePair()) return false;
                        skipped = true;
                    }
                    else
                    {
                        while (rgbTick + frame < now)
                        {
                            pending = false;
                            if (!DecodePair()) return false;
                            skipped = true;
                        }
                    }
                    if (skipped)
                    {
                        rvmOnnx?.Reset();
                        rvmGpu?.Reset();
                    }
                }
                else
                    while(rgbTick+frame<now) { pending=false; if(!DecodePair())return false; }
                if(!allowFutureFrame&&rgbTick>now+10_000)return false;
                if(alpha != null && Math.Abs(rgbTick-alphaTick)>Math.Max(10_000,frame/2)) throw new InvalidDataException("Foreground and alpha timestamps do not match.");
                IntPtr yd=IntPtr.Zero,ud=IntPtr.Zero,vd=IntPtr.Zero;
                uint yp=0,up=0,vp=0;
                if (rgb.IsHardwareDecoded)
                {
                    hardwareVideoFrame = true;
                }
                else
                {
                    rgb.ValidateGpuFrame(); (yd,yp)=rgb.Plane(0); (ud,up)=rgb.Plane(1); (vd,vp)=rgb.Plane(2);
                    context!.UpdateSubresource(yTex!,0,null,yd,yp,0); context.UpdateSubresource(uTex!,0,null,ud,up,0); context.UpdateSubresource(vTex!,0,null,vd,vp,0);
                    hardwareVideoFrame=false; activeHardwareViews=null;
                }
                if (alpha != null)
                {
                    (IntPtr ad,uint ap)=alpha.GrayPlane();
                    context!.UpdateSubresource(aTex!,0,null,ad,ap,0);
                    frameUploaded=true; DrawCurrentFrameLocked();
                    if(captureHitMap) alphaBytes=rgb.IsHardwareDecoded
                        ? CreateHitMap(ad,ap,alphaWidth,alphaHeight)
                        : CreateHitMap(ad,ap,alphaWidth,
                            alphaHeight,yd,yp,ud,up,vd,vp);
                }
                else
                {
                    GenerateRealtimeAlphaLocked(captureHitMap);
                    if (rgb.IsHardwareDecoded)
                    {
                        try { PrepareGpuDisplayFrame(); }
                        catch (Exception error)
                        {
                            RvmOnnxFallbackReason ??= error.Message;
                            if (!TryFallbackRvmToCpuLocked()) throw;
                            rvmOnnx!.Run(rgb, realtimeAlpha!);
                        }
                    }
                    if (!rgb.IsHardwareDecoded && yd == IntPtr.Zero)
                    {
                        rgb.ValidateGpuFrame(); (yd,yp)=rgb.Plane(0);
                        (ud,up)=rgb.Plane(1); (vd,vp)=rgb.Plane(2);
                        context!.UpdateSubresource(yTex!,0,null,yd,yp,0);
                        context.UpdateSubresource(uTex!,0,null,ud,up,0);
                        context.UpdateSubresource(vTex!,0,null,vd,vp,0);
                    }
                    fixed(byte* ad=realtimeAlpha!)
                    {
                        IntPtr alphaPointer=new(ad);
                        if (!rgb.IsHardwareDecoded)
                            context!.UpdateSubresource(aTex!,0,null,alphaPointer,
                                (uint)alphaWidth,0);
                        frameUploaded=true; DrawCurrentFrameLocked();
                        if(captureHitMap) alphaBytes=rgb.IsHardwareDecoded
                            ? CreateHitMap(alphaPointer,(uint)alphaWidth,
                                alphaWidth,alphaHeight)
                            : CreateHitMap(alphaPointer,
                                (uint)alphaWidth,alphaWidth,alphaHeight,yd,yp,ud,up,vd,vp);
                    }
                }
                pending=false; return true;
            }
        }
        void GenerateRealtimeAlphaLocked(bool readback = false)
        {
            try
            {
                if (rvmOnnx == null && rvmGpu == null) return;
                if (rvmGpu != null) rvmGpu.Run(rgb, realtimeAlpha!, readback);
                else rvmOnnx!.Run(rgb, realtimeAlpha!);
            }
            catch (Exception error)
            {
                CustomRtxDiagnostics.Write("renderer", diagnosticId,
                    "rvm-frame-failed", $"gpu={rvmGpu != null} " +
                    $"cpu={rvmOnnx != null} tick={rgbTick}", error);
                RvmOnnxFallbackReason ??= error.Message;
                rvmOnnx?.Dispose(); rvmOnnx = null;
                if (rvmGpu != null && TryFallbackRvmToCpuLocked())
                {
                    try { rvmOnnx!.Run(rgb, realtimeAlpha!); return; }
                    catch { rvmOnnx?.Dispose(); rvmOnnx = null; }
                }
                rvmGpu?.Dispose(); rvmGpu = null;
                Array.Fill(realtimeAlpha!, (byte)255);
            }
        }

        bool TryFallbackRvmToCpuLocked()
        {
            if (activeRvmSettings == null) return false;
            FfmpegCpuDecoder? software = null;
            try
            {
                long started = Stopwatch.GetTimestamp();
                software = new FfmpegCpuDecoder(foregroundPath);
                software.Seek(Math.Max(0, rgbTick / 10_000_000d));
                if (!software.DecodeNext(out rgbTick))
                {
                    software.Dispose();
                    return false;
                }
                RvmOnnxSession replacement = new(
                    RvmOnnxSupport.ModelPathFor(activeRvmSettings.Model),
                    alphaWidth, alphaHeight, activeRvmSettings);
                FfmpegCpuDecoder old = rgb;
                rgb = software;
                DisposeGpuSharedViews();
                rvmGpu?.Dispose();
                rvmGpu = null;
                rvmOnnx = replacement;
                hardwareVideoFrame = false;
                activeHardwareViews = null;
                old.Dispose();
                CustomRtxDiagnostics.Write("renderer", diagnosticId,
                    "rvm-cpu-fallback-ok", $"tick={rgbTick} elapsedMs=" +
                    $"{CustomRtxDiagnostics.ElapsedMilliseconds(started):F3}");
                return true;
            }
            catch (Exception error)
            {
                software?.Dispose();
                CustomRtxDiagnostics.Write("renderer", diagnosticId,
                    "rvm-cpu-fallback-failed", $"tick={rgbTick}", error);
                return false;
            }
        }

        bool TryFallbackDecoderToSoftwareLocked()
        {
            FfmpegCpuDecoder? software = null;
            try
            {
                long started = Stopwatch.GetTimestamp();
                software = new FfmpegCpuDecoder(foregroundPath);
                software.Seek(Math.Max(0, rgbTick / 10_000_000d));
                if (!software.DecodeNext(out rgbTick))
                    throw new InvalidDataException(
                        "Could not restore the current software-decoded frame.");
                FfmpegCpuDecoder old = rgb;
                rgb = software;
                software = null;
                DisposeGpuSharedViews();
                rvmGpu?.Dispose();
                rvmGpu = null;
                hardwareVideoFrame = false;
                activeHardwareViews = null;
                old.Dispose();
                CustomRtxDiagnostics.Write("renderer", diagnosticId,
                    "decoder-software-fallback-ok", $"tick={rgbTick} " +
                    $"elapsedMs={CustomRtxDiagnostics.ElapsedMilliseconds(started):F3}");
                return true;
            }
            catch (Exception error)
            {
                software?.Dispose();
                CustomRtxDiagnostics.Write("renderer", diagnosticId,
                    "decoder-software-fallback-failed", $"tick={rgbTick}",
                    error);
                return false;
            }
        }

        void PrepareGpuDisplayFrame()
        {
            if (gpuDisplayViews == null)
            {
                IntPtr shared = rvmGpu!.CreateDisplaySharedHandle();
                try
                {
                    ID3D11Texture2D texture =
                        device1!.OpenSharedResource1<ID3D11Texture2D>(shared);
                    ID3D11ShaderResourceView y = device3!.CreateShaderResourceView1(
                        texture, new ShaderResourceViewDescription1(texture,
                            ShaderResourceViewDimension.Texture2D,
                            DxgiFormat.R8G8B8A8_UNorm, 0, 1, 0, 1, 0));
                    ID3D11ShaderResourceView uv = device3.CreateShaderResourceView1(
                        texture, new ShaderResourceViewDescription1(texture,
                            ShaderResourceViewDimension.Texture2D,
                            DxgiFormat.R8G8B8A8_UNorm, 0, 1, 0, 1, 0));
                    gpuDisplayViews = new HardwareViews(texture, y, uv);
                }
                finally { CloseHandle(shared); }
                IntPtr alphaShared = rvmGpu.CreateAlphaSharedHandle();
                try
                {
                    gpuAlphaTexture =
                        device1!.OpenSharedResource1<ID3D11Texture2D>(alphaShared);
                    gpuAlphaView = device3!.CreateShaderResourceView1(
                        gpuAlphaTexture, new ShaderResourceViewDescription1(
                            gpuAlphaTexture, ShaderResourceViewDimension.Texture2D,
                            DxgiFormat.R8_UNorm, 0, 1, 0, 1, 0));
                    gpuAlphaMutex =
                        gpuAlphaTexture.QueryInterface<IDXGIKeyedMutex>();
                }
                catch
                {
                    DisposeGpuSharedViews();
                    throw;
                }
                finally { CloseHandle(alphaShared); }
            }
            activeHardwareViews = gpuDisplayViews;
            hardwareVideoFrame = true;
        }

        void DisposeGpuSharedViews()
        {
            activeHardwareViews = null;
            gpuAlphaMutex?.Dispose();
            gpuAlphaMutex = null;
            gpuAlphaView?.Dispose();
            gpuAlphaView = null;
            gpuAlphaTexture?.Dispose();
            gpuAlphaTexture = null;
            gpuDisplayViews?.Dispose();
            gpuDisplayViews = null;
        }
        unsafe void DrawCurrentFrameLocked()
        {
            float maskScale=alpha==null?alphaWidth/(float)Width:1;int lower=Volatile.Read(ref alphaThreshold), upper=Volatile.Read(ref fullOpacityThreshold), chokeQuarters=(int)Math.Round(Volatile.Read(ref edgeChokePixels)*4*maskScale), patch=(int)Math.Round(Volatile.Read(ref smallPatchSize)*maskScale), area=(int)Math.Round(Volatile.Read(ref minimumTransparentAreaRadius)*maskScale), domain=Volatile.Read(ref greenDomain), inputFlags=(hardwareVideoFrame?2:0)|(Volatile.Read(ref alphaAntialiasing)?4:0); VirtualGreenScreenSample[] samples=Volatile.Read(ref greenSamples); int competition=samples.Length==0?0:(int)Math.Round(samples[0].KeyOverLockedThreshold*255); EnsureKeyTextureCapacity(samples.Length); bool greenChanged=!ReferenceEquals(samples,uploadedGreenSamples); if(greenChanged){float[] values=new float[keyTextureCapacity*8];for(int index=0;index<samples.Length;index++){int offset=index*4;values[offset]=samples[index].X;values[offset+1]=samples[index].Y;values[offset+2]=samples[index].Z;values[offset+3]=samples[index].Tolerance;int metadata=(index+keyTextureCapacity)*4;values[metadata]=samples[index].Feather;values[metadata+1]=1;values[metadata+2]=samples[index].Locked?1:0;}fixed(float* data=values)context!.UpdateSubresource(keyTex!,0,null,new IntPtr(data),(uint)(keyTextureCapacity*32),0);uploadedGreenSamples=samples;} if(lower!=uploadedAlphaThreshold||upper!=uploadedFullOpacityThreshold||chokeQuarters!=uploadedEdgeChokeQuarters||patch!=uploadedSmallPatchSize||area!=uploadedMinimumTransparentAreaRadius||domain!=uploadedGreenDomain||inputFlags!=uploadedInputFlags||greenChanged){uint[] values=[(uint)(lower|(upper<<8)|(chokeQuarters<<16)|(inputFlags<<24)),(uint)(patch|(area<<8)|(competition<<16)|(domain<<24))];fixed(uint* data=values)context!.UpdateSubresource(thresholdTex!,0,null,new IntPtr(data),8,0);uploadedAlphaThreshold=lower;uploadedFullOpacityThreshold=upper;uploadedEdgeChokeQuarters=chokeQuarters;uploadedSmallPatchSize=patch;uploadedMinimumTransparentAreaRadius=area;uploadedGreenDomain=domain;uploadedInputFlags=inputFlags;}
            ID3D11ShaderResourceView drawY=activeHardwareViews?.Y??yView!;
            ID3D11ShaderResourceView drawU=activeHardwareViews?.UV??uView!;
            ID3D11ShaderResourceView drawV=activeHardwareViews?.UV??vView!;
            ID3D11ShaderResourceView drawA=gpuAlphaView??aView!;
            IDXGIKeyedMutex? keyedMutex=activeHardwareViews?.KeyedMutex;
            bool displayLocked=false, alphaLocked=false;
            try
            {
            if(keyedMutex!=null){keyedMutex.AcquireSync(0,-1);displayLocked=true;}
            if(gpuAlphaMutex!=null){gpuAlphaMutex.AcquireSync(0,-1);alphaLocked=true;}
            ID3D11ShaderResourceView? enhancedView = null;
            bool hdrEnhanced = false;
            bool upscaling = back!.Description.Width > Width ||
                back.Description.Height > Height;
            bool useVsr = rtxVideoSuperResolutionQuality > 0 &&
                rtxVideo?.HasVsr == true && upscaling;
            bool useHdr = rtxVideoHdr && rtxVideo?.HasTrueHdr == true;
            if (useVsr || useHdr)
            {
                EnsureRtxOutput();
                ID3D11RenderTargetView rgbTarget = useVsr
                    ? rtxInputTarget! : rtxOutputTarget!;
                int rgbWidth = useVsr ? Width : (int)back.Description.Width;
                int rgbHeight = useVsr ? Height : (int)back.Description.Height;
                context!.OMSetRenderTargets(rgbTarget);
                context.RSSetViewport(new GpuViewport(0, 0, rgbWidth, rgbHeight));
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.VSSetShader(vs); context.PSSetShader(rgbPs);
                context.PSSetShaderResource(0, drawY);
                context.PSSetShaderResource(1, drawU);
                context.PSSetShaderResource(2, drawV);
                context.PSSetShaderResource(4, thresholdView);
                context.PSSetSampler(0, sampler); context.Draw(3, 0);
                context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                context.PSUnsetShaderResource(0); context.PSUnsetShaderResource(1);
                context.PSUnsetShaderResource(2); context.PSUnsetShaderResource(4);
                if (useVsr && !rtxVideo!.Evaluate(rtxInputTex!, rtxOutputTex!,
                    Width, Height, (int)back.Description.Width,
                    (int)back.Description.Height, rtxVideoSuperResolutionQuality,
                    out string? vsrFailure))
                {
                    SetRtxVideoStatus($"RTX VSR {RtxQualityName()}: Failed\n" +
                        (vsrFailure ?? "Falling back to standard scaling"));
                    throw new InvalidOperationException(vsrFailure ??
                        "RTX Video Super Resolution evaluation failed.");
                }
                enhancedView = rtxOutputView;
                if (useHdr)
                {
                    EnsureHdrOutput();
                    if (!rtxVideo!.EvaluateTrueHdr(rtxOutputTex!, hdrOutputTex!,
                        (int)back.Description.Width, (int)back.Description.Height,
                        out string? hdrFailure))
                    {
                        SetRtxVideoStatus("RTX HDR: Failed\n" +
                            (hdrFailure ?? "HDR conversion could not be applied"));
                        throw new InvalidOperationException(hdrFailure ??
                            "RTX Video HDR evaluation failed.");
                    }
                    enhancedView = hdrOutputView;
                    hdrEnhanced = true;
                }
                string operation = useHdr
                    ? useVsr ? $"RTX VSR {RtxQualityName()} + HDR" : "RTX HDR"
                    : $"RTX VSR {RtxQualityName()}";
                string bypass = useHdr && rtxVideo!.HasVsr && !upscaling
                    ? " · VSR bypassed" : "";
                SetRtxVideoStatus($"{operation}: Active{bypass}\n" +
                    $"{Width}×{Height} → {back.Description.Width}×" +
                    back.Description.Height);
            }
            else if (rtxVideoSuperResolutionQuality > 0 &&
                rtxVideo?.HasVsr == true && !upscaling)
                SetRtxVideoStatus($"RTX VSR {RtxQualityName()}: Bypassed\n" +
                    $"Output {back.Description.Width}×{back.Description.Height} " +
                    $"is not larger than source {Width}×{Height}");
            context!.PSUnsetShaderResource(6);
            context.OMSetRenderTargets(matteTarget!);
            context.RSSetViewport(new GpuViewport(0, 0,
                matteTex!.Description.Width, matteTex.Description.Height));
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(vs); context.PSSetShader(mattePs);
            context.PSSetShaderResource(0,drawY); context.PSSetShaderResource(1,drawU); context.PSSetShaderResource(2,drawV);
            context.PSSetShaderResource(4,thresholdView); context.PSSetShaderResource(5,keyView);
            context.PSSetSampler(0,sampler); context.Draw(3,0);
            context.PSUnsetShaderResource(0); context.PSUnsetShaderResource(1); context.PSUnsetShaderResource(2);
            context.PSUnsetShaderResource(4); context.PSUnsetShaderResource(5);

            context.OMSetRenderTargets(target!);
            context.RSSetViewport(new GpuViewport(0,0,back!.Description.Width,back.Description.Height));
            context.PSSetShader(hdrEnhanced ? hdrPs :
                enhancedView != null ? enhancedPs : ps);
            context.PSSetShaderResource(0,drawY);
            context.PSSetShaderResource(1,drawU); context.PSSetShaderResource(2,drawV);
            context.PSSetShaderResource(3,drawA); context.PSSetShaderResource(4,thresholdView);
            context.PSSetShaderResource(5,keyView); context.PSSetShaderResource(6,matteView);
            if(enhancedView != null)context.PSSetShaderResource(7,enhancedView);
            context.Draw(3,0);
            context.PSUnsetShaderResource(0); context.PSUnsetShaderResource(1);
            context.PSUnsetShaderResource(2); context.PSUnsetShaderResource(3);
            context.PSUnsetShaderResource(4); context.PSUnsetShaderResource(5);
            context.PSUnsetShaderResource(6); swap!.Present(1,PresentFlags.None);
            context.PSUnsetShaderResource(7);
            }
            finally
            {
                if(alphaLocked)gpuAlphaMutex!.ReleaseSync(0);
                if(displayLocked)keyedMutex!.ReleaseSync(0);
            }
        }
        void EnsureRtxOutput()
        {
            if (rtxOutputTex != null &&
                rtxOutputTex.Description.Width == back!.Description.Width &&
                rtxOutputTex.Description.Height == back.Description.Height) return;
            rtxOutputTarget?.Dispose();
            rtxOutputView?.Dispose();
            rtxOutputTex?.Dispose();
            rtxOutputTex = RenderTexture((int)back!.Description.Width,
                (int)back.Description.Height, DxgiFormat.R8G8B8A8_UNorm);
            rtxOutputView = device!.CreateShaderResourceView(rtxOutputTex);
            rtxOutputTarget = device.CreateRenderTargetView(rtxOutputTex);
        }
        void EnsureHdrOutput()
        {
            if (hdrOutputTex != null &&
                hdrOutputTex.Description.Width == back!.Description.Width &&
                hdrOutputTex.Description.Height == back.Description.Height) return;
            hdrOutputView?.Dispose();
            hdrOutputTex?.Dispose();
            hdrOutputTex = RenderTexture((int)back!.Description.Width,
                (int)back.Description.Height, DxgiFormat.R16G16B16A16_Float);
            hdrOutputView = device!.CreateShaderResourceView(hdrOutputTex);
        }
        string RtxQualityName() => rtxVideoSuperResolutionQuality switch
        {
            1 => "Low", 2 => "Medium", 3 => "High", 4 => "Ultra", _ => "Off"
        };
        void SetRtxVideoStatus(string status)
        {
            if (string.Equals(rtxVideoStatus, status,
                    StringComparison.Ordinal)) return;
            rtxVideoStatus = status;
            CustomRtxDiagnostics.Write("renderer", diagnosticId,
                "rtx-status", $"status=\"{status}\"");
            RtxVideoStatusChanged?.Invoke(status);
        }
        void PresentTransparentLocked()
        {
            context!.OMSetRenderTargets(target!);
            context.ClearRenderTargetView(target!, new GpuColor(0, 0, 0, 0));
            context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            swap!.Present(0, PresentFlags.None);
        }
        AlphaHitMap CreateHitMap(IntPtr alphaPlane, uint pitch,
            int maskWidth, int maskHeight,
            IntPtr yPlane, uint yPitch, IntPtr cbPlane, uint cbPitch,
            IntPtr crPlane, uint crPitch)
        {
            Size size = HitMapSize(Width, Height, 512);
            byte[] pixels = GC.AllocateUninitializedArray<byte>(
                checked(size.Width * size.Height));
            byte[] yValues = GC.AllocateUninitializedArray<byte>(pixels.Length);
            byte[] cb = GC.AllocateUninitializedArray<byte>(pixels.Length);
            byte[] cr = GC.AllocateUninitializedArray<byte>(pixels.Length);
            for (int y = 0; y < size.Height; y++)
            {
                int sourceY = Math.Min(Height - 1, y * Height / size.Height);
                int maskY = Math.Min(maskHeight - 1, y * maskHeight / size.Height);
                Marshal.Copy(alphaPlane + maskY * (int)pitch,
                    alphaRow, 0, alphaRow.Length);
                Marshal.Copy(yPlane + sourceY * (int)yPitch,
                    yRow, 0, yRow.Length);
                int chromaY = sourceY / 2;
                Marshal.Copy(cbPlane + chromaY * (int)cbPitch,
                    cbRow, 0, cbRow.Length);
                Marshal.Copy(crPlane + chromaY * (int)crPitch,
                    crRow, 0, crRow.Length);
                int destination = y * size.Width;
                for (int x = 0; x < size.Width; x++)
                {
                    int sourceX = Math.Min(Width - 1, x * Width / size.Width);
                    int maskX = Math.Min(maskWidth - 1,
                        x * maskWidth / size.Width);
                    pixels[destination + x] = alpha16
                        ? Alpha16ToByte((ushort)(alphaRow[maskX * 2] |
                            alphaRow[maskX * 2 + 1] << 8))
                        : alphaRow[maskX];
                    yValues[destination + x] = yRow[sourceX];
                    cb[destination + x] = cbRow[sourceX / 2];
                    cr[destination + x] = crRow[sourceX / 2];
                }
            }
            return new AlphaHitMap(pixels, yValues, cb, cr,
                size.Width, size.Height);
        }
        AlphaHitMap CreateHitMap(IntPtr alphaPlane, uint pitch,
            int maskWidth, int maskHeight)
        {
            Size size = HitMapSize(Width, Height, 512);
            byte[] pixels = GC.AllocateUninitializedArray<byte>(
                checked(size.Width * size.Height));
            byte[] yValues = new byte[pixels.Length];
            byte[] cb = new byte[pixels.Length];
            byte[] cr = new byte[pixels.Length];
            Array.Fill(yValues, (byte)128);
            Array.Fill(cb, (byte)128);
            Array.Fill(cr, (byte)128);
            for (int y = 0; y < size.Height; y++)
            {
                int maskY = Math.Min(maskHeight - 1,
                    y * maskHeight / size.Height);
                Marshal.Copy(alphaPlane + maskY * (int)pitch,
                    alphaRow, 0, alphaRow.Length);
                int destination = y * size.Width;
                for (int x = 0; x < size.Width; x++)
                {
                    int maskX = Math.Min(maskWidth - 1,
                        x * maskWidth / size.Width);
                    pixels[destination + x] = alphaRow[maskX];
                }
            }
            return new AlphaHitMap(pixels, yValues, cb, cr,
                size.Width, size.Height);
        }
        internal static byte Alpha16ToByte(ushort value) =>
            (byte)((value + 128u) / 257u);
        internal static bool VerifyShader()
        {
            try
            {
                return !Compiler.Compile(Shader, "VSMain", "CustomPlayer.hlsl",
                    "vs_5_0", ShaderFlags.OptimizationLevel3).IsEmpty &&
                    !Compiler.Compile(Shader, "MatteMain", "CustomPlayer.hlsl",
                        "ps_5_0", ShaderFlags.OptimizationLevel3).IsEmpty &&
                    !Compiler.Compile(Shader, "RgbMain", "CustomPlayer.hlsl",
                        "ps_5_0", ShaderFlags.OptimizationLevel3).IsEmpty &&
                    !Compiler.Compile(Shader, "PSMain", "CustomPlayer.hlsl",
                        "ps_5_0", ShaderFlags.OptimizationLevel3).IsEmpty &&
                    !Compiler.Compile(Shader, "PSMainEnhanced", "CustomPlayer.hlsl",
                        "ps_5_0", ShaderFlags.OptimizationLevel3).IsEmpty &&
                    !Compiler.Compile(Shader, "PSMainHdr", "CustomPlayer.hlsl",
                        "ps_5_0", ShaderFlags.OptimizationLevel3).IsEmpty;
            }
            catch
            {
                return false;
            }
        }
        bool DecodePair()
        {
            if (alpha == null)
            {
                bool decoded;
                try { decoded = rgb.DecodeNext(out rgbTick); }
                catch (Exception error) when (rgb.IsHardwareDecoded)
                {
                    CustomRtxDiagnostics.Write("renderer", diagnosticId,
                        "hardware-decode-failed", $"tick={rgbTick} " +
                        $"rvmGpu={rvmGpu != null}", error);
                    RvmOnnxFallbackReason ??= error.Message;
                    bool recovered = rvmGpu != null &&
                        TryFallbackRvmToCpuLocked();
                    if (!recovered)
                        recovered = TryFallbackDecoderToSoftwareLocked();
                    if (!recovered) throw;
                    decoded = true;
                }
                alphaTick = rgbTick;
                pending = decoded;
                Ended = !decoded;
                return decoded;
            }
            bool rgbDecoded = false, alphaDecoded = false;
            long nextRgbTick = 0, nextAlphaTick = 0;
            // The streams have independent decoder state. Decode them in
            // parallel so alpha does not serialize behind the foreground.
            Parallel.Invoke(
                () => rgbDecoded = rgb.DecodeNext(out nextRgbTick),
                () => alphaDecoded = alpha.DecodeNext(out nextAlphaTick));
            rgbTick = nextRgbTick;
            alphaTick = nextAlphaTick;
            if (!rgbDecoded || !alphaDecoded)
            {
                Ended = true;
                return false;
            }
            long frame = (long)Math.Round(rgb.FrameDuration * 10_000_000);
            long tolerance = Math.Max(10_000, frame / 2);
            for (int discarded = 0; ; discarded++)
            {
                int advance = TimestampStreamToAdvance(
                    rgbTick, alphaTick, tolerance);
                if (advance == 0)
                {
                    pending = true;
                    return true;
                }
                if (discarded >= 2)
                    throw new InvalidDataException(
                        "Foreground and alpha timestamps do not match.");
                bool decoded = advance < 0
                    ? rgb.DecodeNext(out rgbTick)
                    : alpha.DecodeNext(out alphaTick);
                if (!decoded)
                {
                    Ended = true;
                    return false;
                }
            }
        }
        double TimeLocked()=>Math.Clamp(clockSeconds+(playing?clock.Elapsed.TotalSeconds*rate:0),0,Duration);
        void UpdateClock(){clockSeconds=TimeLocked();clock.Restart();if(!playing)clock.Stop();}
        void PublishClockLocked()
        {
            Interlocked.Increment(ref publishedClockVersion);
            Volatile.Write(ref publishedClockSecondsBits,
                BitConverter.DoubleToInt64Bits(clockSeconds));
            Volatile.Write(ref publishedRateBits,
                BitConverter.DoubleToInt64Bits(rate));
            Volatile.Write(ref publishedClockTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref publishedClockPlaying, playing ? 1 : 0);
            Interlocked.Increment(ref publishedClockVersion);
        }
        double PublishedCurrentTime()
        {
            while (true)
            {
                int before = Volatile.Read(ref publishedClockVersion);
                if ((before & 1) != 0)
                {
                    Thread.Yield();
                    continue;
                }
                double seconds = BitConverter.Int64BitsToDouble(
                    Volatile.Read(ref publishedClockSecondsBits));
                double publishedRate = BitConverter.Int64BitsToDouble(
                    Volatile.Read(ref publishedRateBits));
                long timestamp = Volatile.Read(ref publishedClockTimestamp);
                bool publishedPlaying =
                    Volatile.Read(ref publishedClockPlaying) != 0;
                if (before != Volatile.Read(ref publishedClockVersion))
                    continue;
                if (publishedPlaying)
                    seconds += Stopwatch.GetElapsedTime(timestamp).TotalSeconds *
                        publishedRate;
                return Math.Clamp(seconds, 0, Duration);
            }
        }
        internal void ResizeOutput(int width, int height)
        {
            lock (sync)
            {
                long resizeStarted = Stopwatch.GetTimestamp();
                CustomRtxDiagnostics.Write("renderer", diagnosticId,
                    "resize-start", $"requested={width}x{height} " +
                    $"current={back?.Description.Width}x" +
                    $"{back?.Description.Height} format={back?.Description.Format} " +
                    $"vsrActive={rtxVideo?.HasVsr == true} " +
                    $"hdrActive={rtxVideo?.HasTrueHdr == true}");
                if (swap == null || back == null) return;
                if (handoffSuspended) return;
                int nextWidth = Math.Max(2, width);
                int nextHeight = Math.Max(2, height);
                if (back.Description.Width == nextWidth &&
                    back.Description.Height == nextHeight) return;
                try
                {
                    ReplaceSwapChainLocked(nextWidth, nextHeight,
                        rtxVideoHdr && rtxVideo?.HasTrueHdr == true);
                    if (frameUploaded) DrawCurrentFrameLocked();
                    else PresentTransparentLocked();
                    CustomRtxDiagnostics.Write("renderer", diagnosticId,
                        "resize-ok", $"output={back.Description.Width}x" +
                        $"{back.Description.Height} format={back.Description.Format} " +
                        $"elapsedMs={CustomRtxDiagnostics.ElapsedMilliseconds(resizeStarted):F3}");
                }
                catch (Exception error)
                {
                    CustomRtxDiagnostics.Write("renderer", diagnosticId,
                        "resize-failed", error: error);
                    SetRtxVideoStatus("Video resize failed\n" + error.Message);
                    Debug.WriteLine("Could not resize the custom player: " +
                        error.Message);
                }
            }
        }
        public void Dispose(){lock(sync){if(disposed)return;CustomRtxDiagnostics.Write("renderer",diagnosticId,"dispose-start",$"playing={playing} time={TimeLocked():F3} session={rtxVideo!=null} swap=0x{swap?.NativePointer.ToInt64():X}");disposed=true;audio?.Dispose();rvmOnnx?.Dispose();DisposeGpuSharedViews();rvmGpu?.Dispose();rtxVideo?.Dispose();rtxVideo=null;rgb.Dispose();alpha?.Dispose();visual?.Dispose();compositionTarget?.Dispose();composition?.Dispose();hdrPs?.Dispose();enhancedPs?.Dispose();rgbPs?.Dispose();mattePs?.Dispose();ps?.Dispose();vs?.Dispose();sampler?.Dispose();hdrOutputView?.Dispose();hdrOutputTex?.Dispose();rtxOutputTarget?.Dispose();rtxOutputView?.Dispose();rtxOutputTex?.Dispose();rtxInputTarget?.Dispose();rtxInputTex?.Dispose();matteView?.Dispose();matteTarget?.Dispose();matteTex?.Dispose();keyView?.Dispose();keyTex?.Dispose();thresholdView?.Dispose();thresholdTex?.Dispose();aView?.Dispose();aTex?.Dispose();vView?.Dispose();vTex?.Dispose();uView?.Dispose();uTex?.Dispose();yView?.Dispose();yTex?.Dispose();target?.Dispose();back?.Dispose();swap?.Dispose();factory?.Dispose();dxgiDevice?.Dispose();device3?.Dispose();device1?.Dispose();context?.Dispose();device?.Dispose();CustomRtxDiagnostics.Write("renderer",diagnosticId,"dispose-ok");}}
    }
}
