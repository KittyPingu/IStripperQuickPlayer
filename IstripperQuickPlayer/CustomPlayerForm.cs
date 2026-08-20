using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using D3D11 = Vortice.Direct3D11.D3D11;
using DComp = Vortice.DirectComposition.DComp;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;
using DxgiFormat = Vortice.DXGI.Format;
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
    internal sealed record AlphaHitMap(byte[] Pixels, int Width, int Height);
    static readonly LowLevelMouseProc globalWheelProc = GlobalWheelCallback;
    static readonly object globalMouseSync = new();
    static readonly List<CustomPlayerForm> globalMousePlayers = [];
    static IntPtr globalWheelHook;
    readonly string foregroundPath, alphaPath;
    readonly bool suppressErrorDialog;
    readonly double rangeStartSeconds, requestedRangeEndSeconds;
    readonly Rectangle? initialBounds;
    readonly Task<PreparedPlayback?>? preparedPlayback;
    readonly CancellationTokenSource cancellation = new();
    readonly System.Windows.Forms.Timer settleTimer = new() { Interval = 15 };
    readonly Stopwatch settleClock = new();
    PairedRenderer? renderer;
    AlphaHitMap? hitTestAlpha;
    volatile bool paused, locked, clickThroughLocked, pointerClickThrough, wheelResize,
        allowWheelWhileLocked;
    bool? mouseTransparent;
    bool movingWindow;
    int pointerRefreshPending;
    bool windowConfigured;
    bool preloadRequested;
    int sizePercent, volumePercent, wheelDelta, volumeWheelDelta,
        settleStart, settleTarget;
    volatile int alphaThreshold, fullOpacityThreshold;
    Task playbackTask = Task.CompletedTask;
    long requestedSeekBits = BitConverter.DoubleToInt64Bits(double.NaN);
    long requestedRateBits = BitConverter.DoubleToInt64Bits(1d);
    double RangeEndSeconds => renderer == null || requestedRangeEndSeconds <= 0
        ? renderer?.Duration ?? 0
        : Math.Min(requestedRangeEndSeconds, renderer.Duration);
    internal bool HasEstablishedBounds =>
        IsEstablishedWindow(windowConfigured, Width, Height);
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

    internal CustomPlayerForm(string foregroundPath, string alphaPath,
        int playerSizePercent = 40, int volumePercent = 100,
        int alphaThreshold = 0, int fullOpacityThreshold = 200,
        bool suppressErrorDialog = false, long startMs = 0, long endMs = 0,
        Rectangle? initialBounds = null,
        Task<PreparedPlayback?>? preparedPlayback = null)
    {
        this.foregroundPath = foregroundPath;
        this.alphaPath = alphaPath;
        this.suppressErrorDialog = suppressErrorDialog;
        rangeStartSeconds = Math.Max(0, startMs / 1000d);
        requestedRangeEndSeconds = endMs / 1000d;
        this.initialBounds = initialBounds;
        this.preparedPlayback = preparedPlayback;
        sizePercent = Math.Clamp(playerSizePercent, 10, 200);
        this.volumePercent = Math.Clamp(volumePercent, 0, 100);
        this.alphaThreshold = Math.Clamp(alphaThreshold, 0, 255);
        this.fullOpacityThreshold = Math.Clamp(fullOpacityThreshold, 1, 255);
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
            UpdateGlobalWheelHook();
            playbackTask = Task.Run(PlayAsync);
        };
        FormClosed += (_, _) =>
        {
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
            renderer ??= await Task.Run(() => new PairedRenderer(
                foregroundPath, alphaPath, alphaThreshold,
                fullOpacityThreshold), cancellation.Token);
            prepared?.Dispose();
            renderer.SetVolume(volumePercent);
            if (rangeStartSeconds >= RangeEndSeconds)
                throw new InvalidDataException("The custom clip time range is invalid.");
            if (!rendererWasPrepared)
                renderer.Seek(rangeStartSeconds);
            Invoke(() => ConfigureWindow(renderer.Width, renderer.Height));
            (IntPtr handle, int width, int height) window =
                ((IntPtr, int, int))Invoke(() => (Handle, ClientSize.Width, ClientSize.Height));
            renderer.AttachWindow(window.handle, window.width, window.height);
            renderer.Play();
            bool firstFramePresented = false;
            bool refreshPausedFrame = true;
            while (!renderer.Ended && renderer.CurrentTime < RangeEndSeconds)
            {
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
                            captureHitMap: true, out AlphaHitMap? pausedAlpha))
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
                    else await Task.Delay(15, cancellation.Token);
                    continue;
                }
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
                        BeginInvoke(() => FirstFramePresented?.Invoke(
                            this, EventArgs.Empty));
                    }
                }
                else await Task.Delay(1, cancellation.Token);
            }
            renderer.Pause();
            if (!IsDisposed) BeginInvoke(() =>
            {
                PlaybackCompleted?.Invoke(this, EventArgs.Empty);
                if (!HoldFinalFrameOnCompletion) Close();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsDisposed) BeginInvoke(() =>
            {
                PlaybackFailed?.Invoke(this, error);
                if (!suppressErrorDialog)
                    MessageBox.Show(error.Message, "Custom Show Player",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            });
        }
        finally { renderer?.Dispose(); renderer = null; }
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
        UpdateMouseTransparency();
        QueuePointerTransparencyRefresh();
    }
    internal void SetFullOpacityThreshold(int value)
    {
        fullOpacityThreshold = Math.Clamp(value, 1, 255);
        renderer?.SetFullOpacityThreshold(fullOpacityThreshold);
    }
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
            SetWindowPos(Handle, new IntPtr(-1), Left + (Width - width) / 2,
                work.Bottom - height, width, height, 0x10 | 0x40);
            renderer.ResizeOutput(width, height);
            LockStateOverlay.ShowTextForWindow(this, $"{sizePercent}%");
        }
        if (notifyChange)
            SizePercentChanged?.Invoke(sizePercent);
    }
    internal void ClosePlayer() { if (!IsDisposed) BeginInvoke(Close); }
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
        string alphaPath, int alphaThreshold, int fullOpacityThreshold,
        long startMs, CancellationToken cancellationToken) => Task.Run(() =>
        {
            PairedRenderer renderer = new(foregroundPath, alphaPath,
                alphaThreshold, fullOpacityThreshold);
            try
            {
                renderer.Seek(Math.Max(0, startMs / 1000d));
                renderer.Prime();
                return (PreparedPlayback?)new PreparedPlayback(renderer);
            }
            catch
            {
                renderer.Dispose();
                throw;
            }
        }, cancellationToken);

    bool IsAlphaVisible(Point point)
    {
        AlphaHitMap? alpha = Volatile.Read(ref hitTestAlpha);
        if (alpha == null || renderer == null || ClientSize.Width <= 0 ||
            ClientSize.Height <= 0 || point.X < 0 || point.Y < 0 ||
            point.X >= ClientSize.Width || point.Y >= ClientSize.Height) return false;
        int x = Math.Min(alpha.Width - 1, point.X * alpha.Width / ClientSize.Width);
        int y = Math.Min(alpha.Height - 1, point.Y * alpha.Height / ClientSize.Height);
        return IsVisibleAlpha(alpha.Pixels[y * alpha.Width + x], alphaThreshold);
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
        PairedRenderer.Alpha16ToByte(0) == 0 &&
        PairedRenderer.Alpha16ToByte(32768) == 128 &&
        PairedRenderer.Alpha16ToByte(65535) == 255 &&
        TimestampStreamToAdvance(100, 110, 20) == 0 &&
        TimestampStreamToAdvance(100, 142, 20) == -1 &&
        TimestampStreamToAdvance(142, 100, 20) == 1 &&
        PairDurationMatches(145.733333, 145.8, 1d / 30) &&
        !PairDurationMatches(145.733333, 145.85, 1d / 30) &&
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
        const string Shader = """
            struct O { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };
            O VSMain(uint id:SV_VertexID) { O o; float2 uv=float2((id<<1)&2,id&2); o.uv=uv; o.p=float4(uv.x*2-1,1-uv.y*2,0,1); return o; }
            Texture2D<float> Y:register(t0); Texture2D<float> U:register(t1); Texture2D<float> V:register(t2); Texture2D<float> A:register(t3); Texture2D<float2> T:register(t4);
            SamplerState S:register(s0);
            float4 PSMain(O i):SV_TARGET { float y=1.16438356*(Y.Sample(S,i.uv)-16.0/255.0); float u=U.Sample(S,i.uv)-.5; float v=V.Sample(S,i.uv)-.5;
              float3 c=float3(y+1.79274107*v,y-.21324861*u-.53290933*v,y+2.11240179*u); float a=A.Sample(S,i.uv); float2 t=T.Load(int3(0,0,0)); a=a<t.r?0:a>=t.g?1:a; return float4(saturate(c)*a,a); }
            """;
        readonly FfmpegCpuDecoder rgb, alpha;
        readonly InternalAudioPlayer? audio;
        readonly Stopwatch clock = new();
        readonly object sync = new();
        ID3D11Device? device; ID3D11DeviceContext? context;
        IDXGIDevice? dxgiDevice; IDXGIFactory2? factory; IDXGISwapChain1? swap;
        ID3D11Texture2D? back, yTex, uTex, vTex, aTex, thresholdTex;
        ID3D11RenderTargetView? target;
        ID3D11ShaderResourceView? yView, uView, vView, aView, thresholdView;
        ID3D11SamplerState? sampler; ID3D11VertexShader? vs; ID3D11PixelShader? ps;
        IDCompositionDevice? composition; IDCompositionTarget? compositionTarget;
        IDCompositionVisual? visual;
        bool pending, playing, disposed; long rgbTick, alphaTick;
        int alphaThreshold, fullOpacityThreshold;
        int uploadedAlphaThreshold = -1, uploadedFullOpacityThreshold = -1;
        double clockSeconds, rate = 1;
        readonly byte[] alphaRow;
        readonly bool alpha16;
        internal int Width => rgb.Width; internal int Height => rgb.Height;
        internal double Duration { get; }
        internal bool Ended { get; private set; }
        internal double CurrentTime { get { lock (sync) return TimeLocked(); } }
        internal double PlaybackRate { get { lock (sync) return rate; } set { lock (sync) { UpdateClock(); rate = value; audio?.SetRate(value, clockSeconds, playing); } } }

        internal PairedRenderer(string foreground, string alphaPath,
            int alphaThreshold, int fullOpacityThreshold)
        {
            this.alphaThreshold = Math.Clamp(alphaThreshold, 0, 255);
            this.fullOpacityThreshold = Math.Clamp(fullOpacityThreshold, 1, 255);
            rgb = new FfmpegCpuDecoder(foreground);
            try { alpha = new FfmpegCpuDecoder(alphaPath); }
            catch { rgb.Dispose(); throw; }
            if (rgb.Width != alpha.Width || rgb.Height != alpha.Height ||
                Math.Abs(rgb.FrameDuration - alpha.FrameDuration) > .0005 ||
                !PairDurationMatches(rgb.Duration, alpha.Duration, rgb.FrameDuration))
                throw new InvalidDataException("Foreground and alpha dimensions, frame rate, or duration do not match.");
            Duration = Math.Min(rgb.Duration, alpha.Duration);
            alpha16 = alpha.IsGray16;
            alphaRow = new byte[checked(Width * (alpha16 ? 2 : 1))];
            audio = InternalAudioPlayer.TryOpen(foreground);
            device = D3D11.D3D11CreateDevice(DriverType.Hardware,
                DeviceCreationFlags.BgraSupport, Vortice.Direct3D.FeatureLevel.Level_11_1,
                Vortice.Direct3D.FeatureLevel.Level_11_0);
            context = device.ImmediateContext; dxgiDevice = device.QueryInterface<IDXGIDevice>();
            factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        }

        internal void AttachWindow(IntPtr handle, int width, int height)
        {
            swap = factory!.CreateSwapChainForComposition(device!, new SwapChainDescription1(
                (uint)width, (uint)height, DxgiFormat.B8G8R8A8_UNorm,
                bufferUsage: Usage.RenderTargetOutput, bufferCount: 2,
                scaling: Scaling.Stretch, swapEffect: SwapEffect.FlipSequential,
                alphaMode: DxgiAlphaMode.Premultiplied));
            back = swap.GetBuffer<ID3D11Texture2D>(0); target = device!.CreateRenderTargetView(back);
            yTex = Texture(Width, Height); uTex = Texture((Width+1)/2, (Height+1)/2);
            vTex = Texture((Width+1)/2, (Height+1)/2); aTex = Texture(Width, Height,
                alpha16 ? DxgiFormat.R16_UNorm : DxgiFormat.R8_UNorm);
            thresholdTex = Texture(1, 1, DxgiFormat.R8G8_UNorm);
            yView=device.CreateShaderResourceView(yTex); uView=device.CreateShaderResourceView(uTex);
            vView=device.CreateShaderResourceView(vTex); aView=device.CreateShaderResourceView(aTex);
            thresholdView=device.CreateShaderResourceView(thresholdTex);
            sampler=device.CreateSamplerState(SamplerDescription.LinearClamp);
            vs=device.CreateVertexShader(Compiler.Compile(Shader,"VSMain","CustomPlayer.hlsl","vs_5_0",ShaderFlags.OptimizationLevel3).Span);
            ps=device.CreatePixelShader(Compiler.Compile(Shader,"PSMain","CustomPlayer.hlsl","ps_5_0",ShaderFlags.OptimizationLevel3).Span);
            composition=DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice!);
            composition.CreateTargetForHwnd(handle,true,out compositionTarget).CheckError();
            visual=composition.CreateVisual(); visual.SetContent(swap); compositionTarget.SetRoot(visual); composition.Commit();
        }
        ID3D11Texture2D Texture(int width,int height,DxgiFormat format=DxgiFormat.R8_UNorm) => device!.CreateTexture2D(new Texture2DDescription
            { Width=(uint)width, Height=(uint)height, MipLevels=1, ArraySize=1, Format=format,
              SampleDescription=new SampleDescription(1,0), Usage=ResourceUsage.Default, BindFlags=BindFlags.ShaderResource });
        internal void Play() { lock(sync) { if(playing)return; clock.Restart(); playing=true; audio?.Play(clockSeconds); } }
        internal void Pause() { lock(sync) { if(!playing)return; UpdateClock(); playing=false; audio?.Pause(); } }
        internal void Seek(double seconds) { lock(sync) { seconds=Math.Clamp(seconds,0,Duration); rgb.Seek(seconds); alpha.Seek(seconds); clockSeconds=seconds; clock.Restart(); if(!playing)clock.Stop(); pending=false; Ended=false; audio?.Seek(seconds,playing); } }
        internal void Prime() { lock(sync) { if(!pending) DecodePair(); } }
        internal void SetVolume(int percent)=>audio?.SetVolume(percent);
        internal void SetAlphaThreshold(int value) =>
            Volatile.Write(ref alphaThreshold, Math.Clamp(value, 0, 255));
        internal void SetFullOpacityThreshold(int value) =>
            Volatile.Write(ref fullOpacityThreshold, Math.Clamp(value, 1, 255));
        internal unsafe bool TryRenderDue(bool captureHitMap,
            out AlphaHitMap? alphaBytes)
        {
            alphaBytes=null; lock(sync)
            {
                if(!pending && !DecodePair()) return false;
                long now=(long)Math.Round(TimeLocked()*10_000_000);
                long frame=(long)Math.Round(rgb.FrameDuration*10_000_000);
                while(rgbTick+frame<now) { pending=false; if(!DecodePair())return false; }
                if(rgbTick>now+10_000)return false;
                if(Math.Abs(rgbTick-alphaTick)>Math.Max(10_000,frame/2)) throw new InvalidDataException("Foreground and alpha timestamps do not match.");
                rgb.ValidateGpuFrame(); (IntPtr yd,uint yp)=rgb.Plane(0); (IntPtr ud,uint up)=rgb.Plane(1); (IntPtr vd,uint vp)=rgb.Plane(2); (IntPtr ad,uint ap)=alpha.GrayPlane();
                context!.UpdateSubresource(yTex!,0,null,yd,yp,0); context.UpdateSubresource(uTex!,0,null,ud,up,0); context.UpdateSubresource(vTex!,0,null,vd,vp,0); context.UpdateSubresource(aTex!,0,null,ad,ap,0);
                int lower=Volatile.Read(ref alphaThreshold), upper=Volatile.Read(ref fullOpacityThreshold); if(lower!=uploadedAlphaThreshold||upper!=uploadedFullOpacityThreshold){ushort value=(ushort)(lower|(upper<<8));context.UpdateSubresource(thresholdTex!,0,null,new IntPtr(&value),2,0);uploadedAlphaThreshold=lower;uploadedFullOpacityThreshold=upper;}
                context.OMSetRenderTargets(target!); context.RSSetViewport(new GpuViewport(0,0,back!.Description.Width,back.Description.Height)); context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.VSSetShader(vs); context.PSSetShader(ps); context.PSSetShaderResource(0,yView); context.PSSetShaderResource(1,uView); context.PSSetShaderResource(2,vView); context.PSSetShaderResource(3,aView); context.PSSetShaderResource(4,thresholdView); context.PSSetSampler(0,sampler); context.Draw(3,0);
                context.PSUnsetShaderResource(0); context.PSUnsetShaderResource(1); context.PSUnsetShaderResource(2); context.PSUnsetShaderResource(3); context.PSUnsetShaderResource(4); swap!.Present(1,PresentFlags.None);
                if(captureHitMap) alphaBytes=CreateHitMap(ad,ap);
                pending=false; return true;
            }
        }
        AlphaHitMap CreateHitMap(IntPtr alphaPlane, uint pitch)
        {
            Size size = HitMapSize(Width, Height, 512);
            byte[] pixels = GC.AllocateUninitializedArray<byte>(
                checked(size.Width * size.Height));
            for (int y = 0; y < size.Height; y++)
            {
                int sourceY = Math.Min(Height - 1, y * Height / size.Height);
                Marshal.Copy(alphaPlane + sourceY * (int)pitch,
                    alphaRow, 0, alphaRow.Length);
                int destination = y * size.Width;
                for (int x = 0; x < size.Width; x++)
                {
                    int sourceX = Math.Min(Width - 1, x * Width / size.Width);
                    pixels[destination + x] = alpha16
                        ? Alpha16ToByte((ushort)(alphaRow[sourceX * 2] |
                            alphaRow[sourceX * 2 + 1] << 8))
                        : alphaRow[sourceX];
                }
            }
            return new AlphaHitMap(pixels, size.Width, size.Height);
        }
        internal static byte Alpha16ToByte(ushort value) =>
            (byte)((value + 128u) / 257u);
        bool DecodePair()
        {
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
        internal void ResizeOutput(int width,int height){lock(sync){if(swap==null)return;context!.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());target?.Dispose();back?.Dispose();swap.ResizeBuffers(2,(uint)Math.Max(2,width),(uint)Math.Max(2,height),DxgiFormat.B8G8R8A8_UNorm);back=swap.GetBuffer<ID3D11Texture2D>(0);target=device!.CreateRenderTargetView(back);}}
        public void Dispose(){if(disposed)return;disposed=true;audio?.Dispose();rgb.Dispose();alpha.Dispose();visual?.Dispose();compositionTarget?.Dispose();composition?.Dispose();ps?.Dispose();vs?.Dispose();sampler?.Dispose();thresholdView?.Dispose();thresholdTex?.Dispose();aView?.Dispose();aTex?.Dispose();vView?.Dispose();vTex?.Dispose();uView?.Dispose();uTex?.Dispose();yView?.Dispose();yTex?.Dispose();target?.Dispose();back?.Dispose();swap?.Dispose();factory?.Dispose();dxgiDevice?.Dispose();context?.Dispose();device?.Dispose();}
    }
}
