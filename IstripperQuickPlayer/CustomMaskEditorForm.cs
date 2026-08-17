using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed record CustomMaskFrameSeed(string FramePath, string MaskPath);

internal sealed class HoldRepeatButton : IDisposable
{
    const int InitialDelayMs = 400;
    const int RepeatIntervalMs = 75;

    static int heldButtonCount;

    readonly Button button;
    readonly Action action;
    readonly System.Windows.Forms.Timer timer = new() { Interval = InitialDelayMs };
    bool held;

    internal HoldRepeatButton(Button button, Action action)
    {
        this.button = button;
        this.action = action;
        button.MouseDown += Button_MouseDown;
        button.MouseUp += Button_MouseUp;
        button.KeyDown += Button_KeyDown;
        timer.Tick += Timer_Tick;
        button.Disposed += Button_Disposed;
    }

    void Button_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !button.Enabled) return;
        if (!held) heldButtonCount++;
        held = true;
        action();
        timer.Interval = InitialDelayMs;
        timer.Start();
    }

    void Button_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Stop();
    }

    void Button_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is not (Keys.Space or Keys.Enter) || !button.Enabled) return;
        e.Handled = e.SuppressKeyPress = true;
        action();
    }

    void Timer_Tick(object? sender, EventArgs e)
    {
        if (!held || (Control.MouseButtons & MouseButtons.Left) == 0)
        {
            Stop();
            return;
        }
        timer.Interval = RepeatIntervalMs;
        action();
    }

    void Stop()
    {
        if (held) heldButtonCount--;
        held = false;
        timer.Stop();
    }

    internal static void SchedulePreview(System.Windows.Forms.Timer previewTimer)
    {
        if (heldButtonCount == 0) previewTimer.Stop();
        if (!previewTimer.Enabled) previewTimer.Start();
    }

    void Button_Disposed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        Stop();
        timer.Dispose();
        button.MouseDown -= Button_MouseDown;
        button.MouseUp -= Button_MouseUp;
        button.KeyDown -= Button_KeyDown;
        button.Disposed -= Button_Disposed;
    }
}

internal sealed class CustomMaskEditorForm : Form
{
    static readonly SemaphoreSlim Sam2MattingWorkerGate = new(1, 1);
    static readonly object Sam2MattingWorkerSync = new();
    static Process? sharedSam2MattingWorker;
    static Task<string>? sharedSam2MattingError;
    static string? sharedSam2MattingTracker;
    static IDisposable? sharedSam2MattingGpuLease;
    sealed record PaintUndoState(string MaskPath, bool HadMask,
        PointF[] Points, int[] Labels, bool Automatic, bool HadPaintSeed);

    readonly string videoPath;
    readonly CustomShowConfiguration configuration;
    readonly long startMs;
    readonly long endMs;
    readonly long durationMs;
    readonly double frameDurationMs;
    readonly bool allowFrameSelection;
    readonly string sam2Model;
    readonly bool sam2Matting;
    readonly bool paintOnly;
    readonly Func<long, CancellationToken, Task<CustomMaskFrameSeed?>>?
        frameSeedProvider;
    readonly string? draftFolder;
    readonly string temporary = Path.Combine(Path.GetTempPath(),
        "iqp-mask-" + Guid.NewGuid().ToString("N"));
    readonly DxgiMaskPreviewControl image = new() { Dock = DockStyle.Fill,
        BackColor = Color.Black,
        Enabled = false, TabStop = true, Cursor = Cursors.Cross,
        AccessibleName = "Initial foreground mask image" };
    readonly ClipTimelineControl timeline = new() { Dock = DockStyle.Fill,
        Height = 60, AllowDividerDragging = false,
        AccessibleName = "Initial mask frame timeline" };
    readonly Label position = new() { AutoSize = true, Padding = new Padding(6, 8, 6, 0) };
    readonly Button previousFrame = new() { Text = "◀ 1 frame", AutoSize = true };
    readonly Button play = new() { Text = "Play", AutoSize = true };
    readonly Button nextFrame = new() { Text = "1 frame ▶", AutoSize = true };
    readonly CheckBox slowMotion = new() { Text = "Slow motion (0.25×)", AutoSize = true,
        Padding = new Padding(8, 6, 0, 0) };
    readonly Label status = new() { AutoSize = true, Padding = new Padding(8) };
    readonly Button clear = new() { Text = "Clear mask", AutoSize = true, Enabled = false };
    readonly CheckBox paintMode = new() { Text = "Paint mask", AutoSize = true,
        Enabled = false, Padding = new Padding(8, 6, 0, 0) };
    readonly TrackBar brushSize = new() { Minimum = 2, Maximum = 200, Value = 40,
        TickStyle = TickStyle.None, Width = 150, Enabled = false,
        AccessibleName = "Initial mask paint brush size" };
    readonly Label brushSizeLabel = new() { Text = "40 px", AutoSize = true,
        Padding = new Padding(0, 7, 8, 0) };
    readonly Button undo = new() { Text = "Undo", AutoSize = true, Enabled = false };
    readonly Button automatic = new() { Text = "Auto mask frame", AutoSize = true,
        Enabled = false };
    readonly Button accept = new() { Text = "Use this mask", AutoSize = true, Enabled = false };
    readonly List<PointF> points = [];
    readonly List<int> labels = [];
    readonly Stack<PaintUndoState> paintUndo = [];
    readonly SemaphoreSlim responseLock = new(1, 1);
    readonly System.Windows.Forms.Timer playback = new() { Interval = 100 };
    readonly System.Windows.Forms.Timer previewDelay = new() { Interval = 140 };
    readonly Stopwatch playClock = new();
    CancellationTokenSource? previewCancellation;
    CancellationTokenSource? streamCancellation;
    Process? worker;
    IDisposable? gpuLease;
    Task<string>? workerError;
    long playStartMs;
    long displayedFrameMs = -1;
    long workerFrameMs = -1;
    bool updatingMask;
    bool workerReady;
    bool frameReady;
    bool resumeAfterScrub;
    bool closing;
    bool sharedWorkerLease;
    bool currentAutomatic;
    bool hasPaintSeed;
    Bitmap? paintingMask;
    PointF lastPaintPoint;
    MouseButtons paintButton;
    bool painting;
    int paintRevision;
    bool previewLoading;
    long queuedPreviewMs = -1;

    internal long FrameMs => Math.Min(Math.Max(startMs, endMs - 1),
        startMs + timeline.PositionMs);
    string FramePath => Path.Combine(temporary, "selected-frame.png");
    string MaskPath => Path.Combine(temporary, "initial-mask.png");
    string PaintSeedPath => Path.Combine(temporary, "paint-seed.png");
    string PreviewPath => Path.Combine(temporary, "mask-preview.jpg");

    internal CustomMaskEditorForm(string videoPath,
        CustomShowConfiguration configuration, long startMs, long endMs,
        string? clipLabel = null, string algorithm = "MatAnyone 2",
        bool allowFrameSelection = false, string sam2Model = "base-plus",
        string? draftFolder = null, bool sam2Matting = false,
        bool showAutomaticMask = true, bool paintOnly = false,
        Func<long, CancellationToken, Task<CustomMaskFrameSeed?>>?
            frameSeedProvider = null)
    {
        this.videoPath = videoPath;
        this.configuration = configuration;
        this.startMs = startMs;
        this.endMs = endMs;
        this.allowFrameSelection = allowFrameSelection;
        this.sam2Model = sam2Model;
        this.sam2Matting = sam2Matting;
        this.paintOnly = paintOnly;
        this.frameSeedProvider = frameSeedProvider;
        this.draftFolder = draftFolder;
        durationMs = Math.Max(1, endMs - startMs);
        try
        {
            using FfmpegCpuDecoder decoder = new(videoPath, fastDecode: true);
            frameDurationMs = Math.Max(.1, decoder.FrameDuration * 1000);
        }
        catch { frameDurationMs = 1000d / 30; }

        Text = clipLabel == null ? $"Create {algorithm} Initial Mask" :
            $"Create {algorithm} Initial Mask - {clipLabel}";
        ClientSize = new Size(1050, allowFrameSelection ? 880 : 780);
        MinimumSize = new Size(720, allowFrameSelection ? 650 : 560);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        Label instructions = new() { Dock = DockStyle.Fill, Height = 44,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0),
            Text = paintOnly && allowFrameSelection
                ? "Scrub backward to the frame to correct, paint foreground with the left button and background with the right button, then use this mask."
                : paintOnly
                ? "Paint foreground with the left button and background with the right button, then use this mask."
                : allowFrameSelection
                ? "Choose any frame, then click to prompt SAM2 or enable Paint mask for exact brush edits."
                : "Click to prompt SAM2, or enable Paint mask and drag to edit the foreground directly." };
        TableLayoutPanel root = new() { Dock = DockStyle.Fill, RowCount = 5,
            ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, allowFrameSelection ? 64 : 0));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(instructions, 0, 0);
        root.Controls.Add(image, 0, 1);
        root.Controls.Add(timeline, 0, 2);

        FlowLayoutPanel precision = new() { Dock = DockStyle.Fill, AutoSize = true,
            WrapContents = false, Padding = new Padding(6, 0, 0, 0) };
        precision.Controls.AddRange([previousFrame, play, position, nextFrame, slowMotion]);
        root.Controls.Add(precision, 0, 3);
        FlowLayoutPanel actions = new() { Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6),
            WrapContents = true };
        Button cancel = new() { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([paintMode, brushSize, brushSizeLabel, clear,
            undo, automatic, accept, cancel, status]);
        automatic.Visible = showAutomaticMask && !paintOnly;
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);
        CancelButton = cancel;

        timeline.Visible = precision.Visible = allowFrameSelection;
        timeline.DurationMs = durationMs;
        timeline.Clips = [new CustomShowClip
        {
            StartMs = 0, EndMs = durationMs, Included = true
        }];
        LoadDraftState();
        UpdatePosition();
        timeline.PositionChanged += (_, _) =>
        {
            UpdatePosition();
            if (!playback.Enabled) RequestPreview();
        };
        timeline.ScrubStarted += (_, _) =>
        {
            resumeAfterScrub = playback.Enabled;
            if (resumeAfterScrub) StopPlayback(requestPreview: false);
        };
        timeline.ScrubEnded += (_, _) =>
        {
            if (resumeAfterScrub) StartPlayback(); else RequestPreview();
            resumeAfterScrub = false;
        };
        _ = new HoldRepeatButton(previousFrame, () => StepFrame(-1));
        play.Click += (_, _) => TogglePlayback();
        _ = new HoldRepeatButton(nextFrame, () => StepFrame(1));
        slowMotion.CheckedChanged += (_, _) => RestartPlayback();
        playback.Tick += (_, _) => PlaybackTick();
        previewDelay.Tick += async (_, _) =>
        {
            previewDelay.Stop();
            queuedPreviewMs = FrameMs;
            if (previewLoading) return;
            previewLoading = true;
            try
            {
                while (queuedPreviewMs >= 0 && !playback.Enabled && !closing)
                {
                    long atMs = queuedPreviewMs;
                    queuedPreviewMs = -1;
                    await LoadSelectedFrameAsync(atMs);
                }
            }
            finally
            {
                previewLoading = false;
            }
        };
        image.MouseClick += Image_MouseClick;
        image.MouseDown += Image_MouseDown;
        image.MouseMove += Image_MouseMove;
        image.MouseUp += Image_MouseUp;
        image.MouseEnter += (_, _) => image.Focus();
        image.MouseWheel += Image_MouseWheel;
        image.MouseLeave += (_, _) =>
        {
            image.SetBrush(null, brushSize.Value);
        };
        paintMode.CheckedChanged += (_, _) =>
        {
            brushSize.Enabled = paintMode.Checked && image.Enabled;
            image.Cursor = paintMode.Checked ? Cursors.Default : Cursors.Cross;
            image.SetBrush(null, brushSize.Value);
        };
        if (paintOnly) paintMode.Checked = true;
        brushSize.ValueChanged += (_, _) =>
        {
            brushSizeLabel.Text = $"{brushSize.Value} px";
            image.SetBrush(null, brushSize.Value);
        };
        KeyDown += async (_, e) =>
        {
            if (!e.Control || e.Alt || e.Shift) return;
            if (e.KeyCode == Keys.Z)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (undo.Enabled) await UndoClickAsync();
            }
            else if (e.KeyCode == Keys.P)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (paintMode.Enabled) paintMode.Checked = !paintMode.Checked;
            }
        };
        clear.Click += (_, _) => ClearClicks();
        undo.Click += async (_, _) => await UndoClickAsync();
        automatic.Click += async (_, _) =>
        {
            ClearPaintUndo();
            ClearPaintSeed();
            await UpdateMaskAsync(true);
        };
        accept.Click += (_, _) =>
        {
            SaveDraftState();
            DialogResult = DialogResult.OK;
            Close();
        };
        Shown += async (_, _) => await StartWorkerAsync();
        FormClosing += (_, _) =>
        {
            closing = true;
            previewDelay.Stop();
            playback.Stop();
            previewCancellation?.Cancel();
            streamCancellation?.Cancel();
            StopWorker();
            gpuLease?.Dispose();
            gpuLease = null;
        };
        AppTheme.Apply(this);
        UpdateEnabledState();
    }

    internal void SaveMask(string destination)
    {
        if (!File.Exists(MaskPath))
            throw new InvalidOperationException("Create an initial mask first.");
        File.Copy(MaskPath, destination, true);
    }

    void LoadDraftState()
    {
        if (draftFolder == null) return;
        CustomInitialMaskDraft? saved = CustomShowMaskDraft.Load<CustomInitialMaskDraft>(
            Path.Combine(draftFolder, "initial-mask.json"));
        if (saved == null || saved.SchemaVersion != CustomShowMaskDraft.SchemaVersion ||
            saved.FrameMs < startMs || saved.FrameMs >= endMs ||
            saved.Points.Length != saved.Labels.Length) return;
        timeline.PositionMs = Math.Clamp(saved.FrameMs - startMs, 0, durationMs);
        foreach (float[] value in saved.Points)
            if (value.Length == 2 && float.IsFinite(value[0]) && float.IsFinite(value[1]))
                points.Add(new PointF(value[0], value[1]));
        if (points.Count != saved.Labels.Length ||
            saved.Labels.Any(value => value is not (0 or 1)))
        {
            points.Clear();
            return;
        }
        labels.AddRange(saved.Labels);
        currentAutomatic = saved.Automatic;
    }

    void SaveDraftState()
    {
        if (draftFolder == null) return;
        Directory.CreateDirectory(draftFolder);
        string savedMask = Path.Combine(draftFolder, "initial-mask.png");
        string savedPreview = Path.Combine(draftFolder, "initial-mask-preview.jpg");
        if (File.Exists(MaskPath)) CustomShowMaskDraft.CopyAtomic(MaskPath, savedMask);
        else try { File.Delete(savedMask); } catch { }
        if (File.Exists(PreviewPath)) CustomShowMaskDraft.CopyAtomic(PreviewPath, savedPreview);
        else try { File.Delete(savedPreview); } catch { }
        CustomShowStore.WriteJsonAtomic(Path.Combine(draftFolder, "initial-mask.json"),
            new CustomInitialMaskDraft
            {
                FrameMs = FrameMs,
                Automatic = currentAutomatic,
                Points = points.Select(value => new[] { value.X, value.Y }).ToArray(),
                Labels = [.. labels]
            });
    }

    async Task StartWorkerAsync()
    {
        try
        {
            status.Text = "Extracting the selected frame...";
            if (!sam2Matting && !paintOnly)
                gpuLease = await CustomShowGpuScheduler.AcquireAsync(
                    CancellationToken.None);
            Directory.CreateDirectory(temporary);
            string? savedFrame = draftFolder == null ? null : Path.Combine(
                draftFolder, "initial-frame.png");
            if (savedFrame != null && File.Exists(savedFrame))
                CustomShowMaskDraft.CopyAtomic(savedFrame, FramePath);
            else await ExtractFrameAsync(FrameMs, CancellationToken.None);
            SetSource(LoadBitmap(FramePath));
            displayedFrameMs = FrameMs;
            frameReady = true;
            if (draftFolder != null &&
                File.Exists(Path.Combine(draftFolder, "initial-mask.png")))
            {
                CustomShowMaskDraft.CopyAtomic(Path.Combine(draftFolder,
                    "initial-mask.png"), MaskPath);
                if (points.Count == 0 && !currentAutomatic)
                {
                    File.Copy(MaskPath, PaintSeedPath, true);
                    hasPaintSeed = true;
                }
                string savedPreview = Path.Combine(draftFolder,
                    "initial-mask-preview.jpg");
                if (File.Exists(savedPreview))
                {
                    CustomShowMaskDraft.CopyAtomic(savedPreview, PreviewPath);
                }
                SetPreviewFromFiles();
            }
            using (Bitmap check = LoadBitmap(FramePath))
            if (!paintOnly && IsMostlyBlack(check))
            {
                timeline.PositionMs = Math.Min(durationMs,
                    Math.Min(3_000, durationMs / 2));
                await ExtractFrameAsync(FrameMs, CancellationToken.None);
                SetSource(LoadBitmap(FramePath));
                displayedFrameMs = FrameMs;
            }
            if (paintOnly)
            {
                workerReady = true;
                status.Text = allowFrameSelection
                    ? "Scrub backward if needed, paint the MatAnyone mask, then use this mask."
                    : "Paint the paused MatAnyone mask, then use this mask.";
                UpdateEnabledState();
                return;
            }
            string python = sam2Matting ? configuration.Sam2MattingPythonExecutable :
                configuration.PythonExecutable;
            string runtime = sam2Matting ? Sam2MattingSupport.RuntimeRoot :
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(python)!, "..", ".."));
            string workerPath = Path.Combine(AppContext.BaseDirectory,
                "custom-shows", sam2Matting ? "sam2matting_mask_worker.py" :
                    "sam_mask_worker.py");
            if (sam2Matting ? !Sam2MattingSupport.IsInstalled(configuration,
                    sam2Model) : !File.Exists(Path.Combine(runtime, "SAM2_COMMIT")))
                throw new InvalidOperationException(
                    "Interactive masking is not installed. Run Install / Update Processing Tools and select this algorithm.");
            if (sam2Matting)
            {
                await Sam2MattingWorkerGate.WaitAsync();
                sharedWorkerLease = true;
                if (sharedSam2MattingGpuLease == null)
                    sharedSam2MattingGpuLease = await
                        CustomShowGpuScheduler.AcquireAsync(CancellationToken.None);
                lock (Sam2MattingWorkerSync)
                    if (sharedSam2MattingWorker is { HasExited: false } &&
                        sharedSam2MattingTracker != sam2Model)
                    {
                        try { sharedSam2MattingWorker.Kill(true); } catch { }
                        sharedSam2MattingWorker.Dispose();
                        sharedSam2MattingWorker = null;
                        sharedSam2MattingError = null;
                        sharedSam2MattingTracker = null;
                    }
            }
            ProcessStartInfo start = new(python)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { workerPath, "--runtime", runtime,
                "--image", FramePath, "--mask", MaskPath, "--preview", PreviewPath,
                sam2Matting ? "--tracker" : "--model", sam2Model })
                start.ArgumentList.Add(argument);
            bool reused = false;
            string readyDescription = "";
            if (sam2Matting)
                lock (Sam2MattingWorkerSync)
                    if (sharedSam2MattingWorker is { HasExited: false })
                    {
                        worker = sharedSam2MattingWorker;
                        workerError = sharedSam2MattingError;
                        reused = true;
                    }
            if (!reused)
            {
                worker = Process.Start(start) ??
                    throw new InvalidOperationException("Python could not be started.");
                workerError = worker.StandardError.ReadToEndAsync();
                if (sam2Matting)
                    lock (Sam2MattingWorkerSync)
                    {
                        sharedSam2MattingWorker = worker;
                        sharedSam2MattingError = workerError;
                        sharedSam2MattingTracker = sam2Model;
                    }
                JsonElement ready = await ReadResponseAsync();
                if (ready.GetProperty("status").GetString() != "ready")
                    throw new InvalidOperationException(ResponseError(ready));
                readyDescription = $"{ready.GetProperty("checkpoint").GetString()} ready on " +
                    $"{ready.GetProperty("device").GetString()?.ToUpperInvariant()}/" +
                    ready.GetProperty("precision").GetString();
            }
            else
            {
                await worker!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    command = "load", image = FramePath,
                    mask = MaskPath, preview = PreviewPath
                }));
                await worker.StandardInput.FlushAsync();
                JsonElement loaded = await ReadResponseAsync();
                if (loaded.GetProperty("status").GetString() != "loaded")
                    throw new InvalidOperationException(ResponseError(loaded));
                readyDescription = $"{sam2Model} reused on CUDA/BF16";
            }
            workerFrameMs = displayedFrameMs;
            workerReady = true;
            status.Text = File.Exists(MaskPath)
                ? $"Recovered saved mask at {Format(FrameMs - startMs)} with " +
                    $"{points.Count} undoable click{(points.Count == 1 ? "" : "s")}."
                : readyDescription +
                    (allowFrameSelection
                        ? "; choose the best frame, then create its mask." : "");
            UpdateEnabledState();
        }
        catch (Exception error)
        {
            if (!closing && !IsDisposed && !Disposing)
            {
                status.Text = "Mask setup failed";
                MessageBox.Show(this, error.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    async Task ExtractFrameAsync(long atMs, CancellationToken token)
    {
        string extracted = Path.Combine(temporary,
            "frame-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            ProcessStartInfo extract = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "-y", "-v", "error", "-ss",
                (atMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture), "-i", videoPath,
                "-frames:v", "1", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,setsar=1",
                extracted }) extract.ArgumentList.Add(argument);
            using Process process = Process.Start(extract) ??
                throw new InvalidOperationException("FFmpeg could not be started.");
            await using ProcessCancellationScope cancellationScope = new(process, token);
            Task<string> error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException((await error).Trim());
            File.Move(extracted, FramePath, true);
        }
        finally { try { if (File.Exists(extracted)) File.Delete(extracted); } catch { } }
    }

    void TogglePlayback()
    {
        if (playback.Enabled) StopPlayback(); else StartPlayback();
    }

    void StartPlayback()
    {
        if (!allowFrameSelection || updatingMask) return;
        if (timeline.PositionMs >= durationMs) timeline.PositionMs = 0;
        previewDelay.Stop();
        queuedPreviewMs = -1;
        previewCancellation?.Cancel();
        ResetMaskSelection();
        frameReady = false;
        playStartMs = timeline.PositionMs;
        playClock.Restart();
        playback.Start();
        play.Text = "Pause";
        streamCancellation?.Cancel();
        streamCancellation = new CancellationTokenSource();
        double speed = slowMotion.Checked ? .25 : 1;
        _ = Task.Run(() => frameSeedProvider == null
            ? StreamPreviewAsync(FrameMs, speed, streamCancellation.Token)
            : StreamMaskedPreviewAsync(FrameMs, speed, streamCancellation.Token));
        UpdateEnabledState();
    }

    void StopPlayback(bool requestPreview = true)
    {
        playback.Stop();
        playClock.Stop();
        streamCancellation?.Cancel();
        streamCancellation = null;
        play.Text = "Play";
        if (requestPreview) RequestPreview();
        UpdateEnabledState();
    }

    void PlaybackTick()
    {
        if (frameSeedProvider != null) return;
        timeline.PositionMs = Math.Min(durationMs,
            playStartMs + (long)Math.Round(playClock.ElapsedMilliseconds *
                (slowMotion.Checked ? .25 : 1)));
        if (timeline.PositionMs >= durationMs) StopPlayback();
    }

    void RestartPlayback()
    {
        if (!playback.Enabled) return;
        StopPlayback(requestPreview: false);
        StartPlayback();
    }

    void StepFrame(int direction)
    {
        if (!allowFrameSelection || updatingMask) return;
        StopPlayback(requestPreview: false);
        timeline.PositionMs = CustomClipEditorForm.FrameStep(timeline.PositionMs,
            direction, frameDurationMs, durationMs);
    }

    void UpdatePosition() => position.Text =
        $"{Format(timeline.PositionMs)} / {Format(durationMs)}";

    void RequestPreview()
    {
        if (!allowFrameSelection || !workerReady || playback.Enabled || closing) return;
        frameReady = false;
        ResetMaskSelection();
        UpdateEnabledState();
        status.Text = "Seeking selected frame...";
        HoldRepeatButton.SchedulePreview(previewDelay);
    }

    async Task LoadSelectedFrameAsync(long atMs)
    {
        CancellationTokenSource cancellation = new();
        previewCancellation = cancellation;
        try
        {
            CustomMaskFrameSeed? seed = frameSeedProvider == null ? null :
                await frameSeedProvider(atMs, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                DeleteFrameSeed(seed);
                return;
            }
            if (seed != null)
            {
                CustomShowMaskDraft.CopyAtomic(seed.FramePath, FramePath);
                if (File.Exists(seed.MaskPath))
                    CustomShowMaskDraft.CopyAtomic(seed.MaskPath, MaskPath);
            }
            else await ExtractFrameAsync(atMs, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            displayedFrameMs = atMs;
            frameReady = FrameMs == atMs;
            if (seed != null && File.Exists(seed.MaskPath))
            {
                File.Copy(MaskPath, PaintSeedPath, true);
                hasPaintSeed = true;
                SetPreviewFromFiles();
                DeleteFrameSeed(seed);
            }
            else SetSource(LoadBitmap(FramePath));
            status.Text = frameReady
                ? paintOnly
                    ? $"Frame {Format(atMs - startMs)} ready with its MatAnyone mask; paint corrections."
                    : $"Frame {Format(atMs - startMs)} ready; click the foreground to create its mask."
                : "Seeking selected frame...";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!closing && !IsDisposed)
                status.Text = "Frame preview unavailable: " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(previewCancellation, cancellation))
                previewCancellation = null;
            UpdateEnabledState();
        }
    }

    async Task StreamPreviewAsync(long atMs, double speed, CancellationToken token)
    {
        try
        {
            ProcessStartInfo start = PreviewProcessStart(atMs);
            start.ArgumentList.Add(speed == 1 ? "-re" : "-readrate");
            if (speed != 1)
                start.ArgumentList.Add(speed.ToString(CultureInfo.InvariantCulture));
            foreach (string argument in new[] { "-i", videoPath, "-t",
                Math.Max(.001, (endMs - atMs) / 1000d).ToString("0.###",
                    CultureInfo.InvariantCulture), "-an", "-vf",
                "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2",
                "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ??
                throw new InvalidOperationException("FFmpeg did not start.");
            await using ProcessCancellationScope cancellationScope = new(process, token);
            _ = process.StandardError.ReadToEndAsync(token);
            byte[] frame = new byte[640 * 360 * 4];
            while (!token.IsCancellationRequested)
            {
                await process.StandardOutput.BaseStream.ReadExactlyAsync(frame, token);
                Bitmap bitmap = FrameBitmap(frame, 640, 360);
                try
                {
                    if (!IsHandleCreated || IsDisposed) { bitmap.Dispose(); break; }
                    Invoke(() =>
                    {
                        if (token.IsCancellationRequested || !playback.Enabled)
                            bitmap.Dispose();
                        else SetSource(bitmap);
                    });
                }
                catch { bitmap.Dispose(); throw; }
            }
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated) { }
    }

    async Task StreamMaskedPreviewAsync(long atMs, double speed,
        CancellationToken token)
    {
        long stepMs = Math.Max(1, (long)Math.Round(frameDurationMs));
        try
        {
            for (long currentMs = atMs; currentMs < endMs; currentMs += stepMs)
            {
                Stopwatch frameClock = Stopwatch.StartNew();
                CustomMaskFrameSeed? seed = await frameSeedProvider!(currentMs, token);
                if (seed == null) throw new InvalidDataException(
                    "MatAnyone did not return the playback frame and mask.");
                Bitmap? source = null;
                Bitmap? mask = null;
                try
                {
                    source = LoadBitmap(seed.FramePath);
                    mask = LoadBitmap(seed.MaskPath);
                }
                catch
                {
                    source?.Dispose();
                    mask?.Dispose();
                    throw;
                }
                finally { DeleteFrameSeed(seed); }
                try
                {
                    if (!IsHandleCreated || IsDisposed)
                    {
                        source.Dispose();
                        mask.Dispose();
                        break;
                    }
                    Invoke(() =>
                    {
                        if (token.IsCancellationRequested || !playback.Enabled)
                        {
                            source.Dispose();
                            mask.Dispose();
                            return;
                        }
                        timeline.PositionMs = Math.Clamp(currentMs - startMs,
                            0, durationMs);
                        displayedFrameMs = currentMs;
                        image.SetPreviewOwned(source, mask, [], []);
                        status.Text = $"Playing MatAnyone mask · {Format(currentMs - startMs)}";
                    });
                }
                catch
                {
                    source.Dispose();
                    mask.Dispose();
                    throw;
                }
                int delay = (int)Math.Round(stepMs / speed -
                    frameClock.Elapsed.TotalMilliseconds);
                if (delay > 0) await Task.Delay(delay, token);
            }
            if (!token.IsCancellationRequested && IsHandleCreated && !IsDisposed)
                Invoke(() => StopPlayback());
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated) { }
    }

    ProcessStartInfo PreviewProcessStart(long atMs)
    {
        ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-v", "error", "-ss",
            (atMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        return start;
    }

    static Bitmap FrameBitmap(byte[] bytes, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int row = 0; row < height; row++)
                Marshal.Copy(bytes, row * width * 4,
                    data.Scan0 + row * data.Stride, width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    async void Image_MouseClick(object? sender, MouseEventArgs e)
    {
        if (paintMode.Checked) return;
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right) ||
            worker == null || worker.HasExited || !image.HasImage ||
            !image.Enabled || updatingMask || displayedFrameMs != FrameMs) return;
        PointF? point = image.ImagePoint(e.Location);
        if (point == null) return;
        ClearPaintUndo();
        points.Add(point.Value);
        labels.Add(e.Button == MouseButtons.Right ? 0 : 1);
        image.SetMarkers([.. points], [.. labels]);
        await UpdateMaskAsync(currentAutomatic);
    }

    void Image_MouseDown(object? sender, MouseEventArgs e)
    {
        if (!paintMode.Checked || updatingMask || !frameReady || !image.HasImage ||
            e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
        PointF? point = image.ImagePoint(e.Location);
        if (point == null || !File.Exists(FramePath)) return;
        try
        {
            bool hadMask = File.Exists(MaskPath);
            string undoPath = Path.Combine(temporary,
                $"initial-paint-undo-{++paintRevision}.png");
            if (hadMask) File.Copy(MaskPath, undoPath, true);
            paintUndo.Push(new PaintUndoState(undoPath, hadMask,
                [.. points], [.. labels], currentAutomatic, hasPaintSeed));
            paintingMask?.Dispose();
            if (hadMask)
            {
                using Image loadedMask = LoadImage(MaskPath);
                paintingMask = new Bitmap(loadedMask);
            }
            else
            {
                using Image source = LoadImage(FramePath);
                paintingMask = new Bitmap(source.Width, source.Height,
                    PixelFormat.Format24bppRgb);
                using Graphics blank = Graphics.FromImage(paintingMask);
                blank.Clear(Color.Black);
            }
            // The painted bitmap now contains the visible result of the existing
            // prompts. It becomes the base for subsequent SAM2 clicks.
            points.Clear();
            labels.Clear();
            currentAutomatic = false;
            painting = true;
            paintButton = e.Button;
            lastPaintPoint = point.Value;
            image.Capture = true;
            Rectangle dirty = ApplyBrush(point.Value, point.Value);
            RenderPaintPreview(paintingMask, dirty, save: false);
            UpdateEnabledState();
        }
        catch (Exception error)
        {
            gpuLease?.Dispose();
            gpuLease = null;
            ReleaseSharedWorkerLease();
            paintingMask?.Dispose(); paintingMask = null;
            painting = false; image.Capture = false;
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static void DeleteFrameSeed(CustomMaskFrameSeed? seed)
    {
        if (seed == null) return;
        try { File.Delete(seed.FramePath); } catch { }
        try { File.Delete(seed.MaskPath); } catch { }
    }

    void Image_MouseMove(object? sender, MouseEventArgs e)
    {
        if (paintMode.Checked)
            image.SetBrush(image.ImagePoint(e.Location), brushSize.Value);
        if (!painting || paintingMask == null) return;
        PointF? point = image.ImagePoint(e.Location);
        if (point == null) return;
        Rectangle dirty = ApplyBrush(lastPaintPoint, point.Value);
        lastPaintPoint = point.Value;
        RenderPaintPreview(paintingMask, dirty, save: false);
    }

    void ResizeBrush(int wheelDelta)
    {
        if (!paintMode.Checked || !paintMode.Enabled || wheelDelta == 0) return;
        int notches = Math.Max(1, Math.Abs(wheelDelta) / 120);
        brushSize.Value = Math.Clamp(brushSize.Value +
            Math.Sign(wheelDelta) * notches * 2, brushSize.Minimum, brushSize.Maximum);
    }

    void Image_MouseWheel(object? sender, MouseEventArgs e)
    {
        if (e.Delta == 0) return;
        if (paintMode.Checked)
        {
            ResizeBrush(e.Delta);
            return;
        }
        if (playback.Enabled || !allowFrameSelection || updatingMask) return;
        int frames = Math.Max(1, Math.Abs(e.Delta) / 120);
        StepFrame(Math.Sign(e.Delta) * frames);
    }

    void Image_MouseUp(object? sender, MouseEventArgs e)
    {
        if (!painting || paintingMask == null) return;
        painting = false; image.Capture = false;
        try
        {
            paintingMask.Save(MaskPath, ImageFormat.Png);
            File.Copy(MaskPath, PaintSeedPath, true);
            hasPaintSeed = true;
            RenderPaintPreview(paintingMask, null, save: true);
            status.Text = "Painted initial mask ready. Add another stroke or use this mask.";
            SaveDraftState();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            paintingMask.Dispose(); paintingMask = null;
            UpdateEnabledState();
        }
    }

    Rectangle ApplyBrush(PointF from, PointF to)
    {
        DrawBrush(paintingMask!, from, to, brushSize.Value,
            paintButton != MouseButtons.Right);
        float radius = brushSize.Value / 2f;
        Rectangle dirty = Rectangle.Ceiling(RectangleF.FromLTRB(
            Math.Min(from.X, to.X) - radius - 2,
            Math.Min(from.Y, to.Y) - radius - 2,
            Math.Max(from.X, to.X) + radius + 2,
            Math.Max(from.Y, to.Y) + radius + 2));
        dirty.Intersect(new Rectangle(Point.Empty, paintingMask!.Size));
        return dirty;
    }

    static void DrawBrush(Bitmap mask, PointF from, PointF to, int size, bool add)
    {
        using Graphics graphics = Graphics.FromImage(mask);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        using Pen pen = new(add ? Color.White : Color.Black, size)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };
        graphics.DrawLine(pen, from, to);
        float radius = size / 2f;
        using Brush endpoint = new SolidBrush(add ? Color.White : Color.Black);
        graphics.FillEllipse(endpoint, from.X - radius, from.Y - radius, size, size);
        graphics.FillEllipse(endpoint, to.X - radius, to.Y - radius, size, size);
    }

    void RenderPaintPreview(Bitmap mask, Rectangle? dirty, bool save)
    {
        if (dirty is Rectangle region)
            image.SetMaskRegion(mask, region, [.. points], [.. labels]);
        else image.SetMask(mask, [.. points], [.. labels]);
        if (save) SavePreviewFile(mask);
    }

    async Task EnsureWorkerFrameAsync()
    {
        if (displayedFrameMs != FrameMs || !frameReady)
            throw new InvalidOperationException("Wait for the selected frame to finish loading.");
        if (workerFrameMs == displayedFrameMs) return;
        status.Text = "Preparing the selected frame for SAM2...";
        await worker!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            command = "load", image = FramePath,
            mask = MaskPath, preview = PreviewPath
        }));
        await worker.StandardInput.FlushAsync();
        JsonElement response = await ReadResponseAsync();
        if (response.GetProperty("status").GetString() != "loaded")
            throw new InvalidOperationException(ResponseError(response));
        workerFrameMs = displayedFrameMs;
    }

    async Task UpdateMaskAsync(bool useAutomatic = false)
    {
        if (worker == null || worker.HasExited || updatingMask || !frameReady) return;
        updatingMask = true;
        UpdateEnabledState();
        status.Text = useAutomatic ? "Generating automatic SAM2 masks..." : "Updating mask...";
        try
        {
            await EnsureWorkerFrameAsync();
            string request = JsonSerializer.Serialize(new
            {
                command = useAutomatic ? "auto" : "prompt",
                seedMask = !useAutomatic && hasPaintSeed ? PaintSeedPath : null,
                mask = MaskPath, preview = PreviewPath,
                points = points.Select(point => new[] { point.X, point.Y }),
                labels
            });
            await worker.StandardInput.WriteLineAsync(request);
            await worker.StandardInput.FlushAsync();
            JsonElement response = await ReadResponseAsync();
            if (response.GetProperty("status").GetString() != "mask")
                throw new InvalidOperationException(ResponseError(response));
            currentAutomatic = useAutomatic;
            SetPreviewFromFiles();
            status.Text = useAutomatic && response.TryGetProperty("candidates", out JsonElement count)
                ? $"Automatic mask ready from {count.GetInt32()} candidates; refine with clicks if needed."
                : $"Mask ready · {points.Count} click{(points.Count == 1 ? "" : "s")}";
            SaveDraftState();
        }
        catch (Exception error)
        {
            if (!closing && !IsDisposed && !Disposing)
                MessageBox.Show(this, error.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            updatingMask = false;
            UpdateEnabledState();
        }
    }

    async Task UndoClickAsync()
    {
        if (!updatingMask && paintUndo.Count > 0)
        {
            PaintUndoState previous = paintUndo.Pop();
            points.Clear(); points.AddRange(previous.Points);
            labels.Clear(); labels.AddRange(previous.Labels);
            currentAutomatic = previous.Automatic;
            try
            {
                if (previous.HadMask)
                {
                    File.Copy(previous.MaskPath, MaskPath, true);
                    if (previous.HadPaintSeed)
                    {
                        File.Copy(MaskPath, PaintSeedPath, true);
                        hasPaintSeed = true;
                    }
                    else ClearPaintSeed();
                    using Image loadedMask = LoadImage(MaskPath);
                    using Bitmap restored = new(loadedMask);
                    RenderPaintPreview(restored, null, save: true);
                }
                else
                {
                    hasPaintSeed = false;
                    try { if (File.Exists(PaintSeedPath)) File.Delete(PaintSeedPath); } catch { }
                    try { if (File.Exists(MaskPath)) File.Delete(MaskPath); } catch { }
                    try { if (File.Exists(PreviewPath)) File.Delete(PreviewPath); } catch { }
                    SetSource(LoadBitmap(FramePath));
                }
                status.Text = "Paint stroke undone.";
                SaveDraftState();
            }
            finally
            {
                try { if (File.Exists(previous.MaskPath)) File.Delete(previous.MaskPath); } catch { }
            }
            UpdateEnabledState();
            return;
        }
        if (updatingMask || points.Count == 0) return;
        points.RemoveAt(points.Count - 1);
        labels.RemoveAt(labels.Count - 1);
        image.SetMarkers([.. points], [.. labels]);
        if (points.Count == 0)
        {
            if (hasPaintSeed && File.Exists(PaintSeedPath))
            {
                File.Copy(PaintSeedPath, MaskPath, true);
                using Image loadedMask = LoadImage(MaskPath);
                using Bitmap restored = new(loadedMask);
                RenderPaintPreview(restored, null, save: true);
                status.Text = "Last click undone; painted mask restored.";
                currentAutomatic = false;
                SaveDraftState();
                UpdateEnabledState();
                return;
            }
            try { if (File.Exists(MaskPath)) File.Delete(MaskPath); } catch { }
            try { if (File.Exists(PreviewPath)) File.Delete(PreviewPath); } catch { }
            if (frameReady && File.Exists(FramePath)) SetSource(LoadBitmap(FramePath));
            status.Text = "Last click undone; click the foreground to create a mask.";
            currentAutomatic = false;
            SaveDraftState();
            UpdateEnabledState();
            return;
        }
        await UpdateMaskAsync(currentAutomatic);
    }

    async Task<JsonElement> ReadResponseAsync()
    {
        await responseLock.WaitAsync();
        try
        {
            while (await worker!.StandardOutput.ReadLineAsync() is string line)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                }
                catch (JsonException) { }
            }
            string detail = workerError == null ? "" : (await workerError).Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "The mask worker stopped unexpectedly." : detail);
        }
        finally { responseLock.Release(); }
    }

    void ClearClicks()
    {
        ClearPaintUndo();
        points.Clear();
        labels.Clear();
        currentAutomatic = false;
        ClearPaintSeed();
        try { if (File.Exists(MaskPath)) File.Delete(MaskPath); } catch { }
        try { if (File.Exists(PreviewPath)) File.Delete(PreviewPath); } catch { }
        if (frameReady && File.Exists(FramePath)) SetSource(LoadBitmap(FramePath));
        status.Text = "Click one or more people";
        SaveDraftState();
        UpdateEnabledState();
    }

    void ResetMaskSelection()
    {
        ClearPaintUndo();
        points.Clear();
        labels.Clear();
        currentAutomatic = false;
        ClearPaintSeed();
        try { if (File.Exists(MaskPath)) File.Delete(MaskPath); } catch { }
        try { if (File.Exists(PreviewPath)) File.Delete(PreviewPath); } catch { }
    }

    void UpdateEnabledState()
    {
        if (closing || IsDisposed) return;
        bool editable = workerReady && frameReady && !updatingMask && !playback.Enabled;
        image.Enabled = automatic.Enabled = paintMode.Enabled = editable;
        brushSize.Enabled = editable && paintMode.Checked;
        clear.Enabled = editable && (File.Exists(MaskPath) || points.Count > 0);
        undo.Enabled = editable && (paintUndo.Count > 0 || points.Count > 0);
        undo.Text = paintUndo.Count > 0 ? "Undo stroke" : "Undo click";
        accept.Enabled = editable && File.Exists(MaskPath);
        timeline.Enabled = previousFrame.Enabled = nextFrame.Enabled =
            slowMotion.Enabled = allowFrameSelection && workerReady && !updatingMask;
        play.Enabled = allowFrameSelection && workerReady && !updatingMask;
    }

    void ClearPaintUndo()
    {
        while (paintUndo.Count > 0)
        {
            PaintUndoState state = paintUndo.Pop();
            try { if (File.Exists(state.MaskPath)) File.Delete(state.MaskPath); } catch { }
        }
    }

    void ClearPaintSeed()
    {
        hasPaintSeed = false;
        try { if (File.Exists(PaintSeedPath)) File.Delete(PaintSeedPath); } catch { }
    }

    void SetSource(Bitmap source) => image.SetSourceOwned(source);

    void SetPreviewFromFiles()
    {
        Bitmap source = LoadBitmap(FramePath);
        if (!File.Exists(MaskPath)) { SetSource(source); return; }
        Bitmap mask = LoadBitmap(MaskPath);
        image.SetPreviewOwned(source, mask, [.. points], [.. labels]);
    }

    void SavePreviewFile(Bitmap mask)
    {
        using Bitmap source = LoadBitmap(FramePath);
        using Bitmap result = new(source.Width, source.Height);
        using Graphics graphics = Graphics.FromImage(result);
        graphics.DrawImageUnscaled(source, 0, 0);
        using ImageAttributes attributes = new();
        attributes.SetRemapTable([new ColorMap { OldColor = Color.Black,
            NewColor = Color.Transparent }, new ColorMap { OldColor = Color.White,
            NewColor = Color.FromArgb(105, Color.Lime) }]);
        graphics.DrawImage(mask, new Rectangle(0, 0, result.Width, result.Height),
            0, 0, mask.Width, mask.Height, GraphicsUnit.Pixel, attributes);
        result.Save(PreviewPath, ImageFormat.Jpeg);
    }

    static Bitmap LoadBitmap(string path)
    {
        using Image source = LoadImage(path);
        return new Bitmap(source);
    }

    static Image LoadImage(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using Image source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    static bool IsMostlyBlack(Image source)
    {
        using Bitmap bitmap = new(source);
        long brightness = 0;
        int samples = 0;
        int stepX = Math.Max(1, bitmap.Width / 40);
        int stepY = Math.Max(1, bitmap.Height / 40);
        for (int y = 0; y < bitmap.Height; y += stepY)
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                Color pixel = bitmap.GetPixel(x, y);
                brightness += pixel.R + pixel.G + pixel.B;
                samples++;
            }
        return brightness / Math.Max(1, samples * 3) < 12;
    }

    static string Format(long milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(
            milliseconds >= 3_600_000 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");

    internal static bool VerifyFrameSelection()
    {
        using Bitmap black = new(4, 4);
        using Bitmap white = new(4, 4);
        using Graphics graphics = Graphics.FromImage(white);
        graphics.Clear(Color.White);
        using Bitmap painted = new(20, 20, PixelFormat.Format24bppRgb);
        DrawBrush(painted, new PointF(10, 10), new PointF(10, 10), 6, true);
        bool added = painted.GetPixel(10, 10).R == 255;
        DrawBrush(painted, new PointF(10, 10), new PointF(10, 10), 6, false);
        return IsMostlyBlack(black) && !IsMostlyBlack(white) && added &&
            painted.GetPixel(10, 10).R == 0;
    }

    static string ResponseError(JsonElement response) =>
        response.TryGetProperty("message", out JsonElement message)
            ? message.GetString() ?? "Mask generation failed." : "Mask generation failed.";

    void StopWorker()
    {
        if (sam2Matting)
        {
            worker = null;
            workerError = null;
            ReleaseSharedWorkerLease();
            return;
        }
        try { if (worker is { HasExited: false }) worker.Kill(true); } catch { }
        worker?.Dispose();
        worker = null;
    }

    void ReleaseSharedWorkerLease()
    {
        if (!sharedWorkerLease) return;
        sharedWorkerLease = false;
        Sam2MattingWorkerGate.Release();
    }

    internal static void CloseSam2MattingWorker()
    {
        lock (Sam2MattingWorkerSync)
        {
            try
            {
                if (sharedSam2MattingWorker is { HasExited: false })
                    sharedSam2MattingWorker.Kill(true);
            }
            catch { }
            sharedSam2MattingWorker?.Dispose();
            sharedSam2MattingWorker = null;
            sharedSam2MattingError = null;
            sharedSam2MattingTracker = null;
            sharedSam2MattingGpuLease?.Dispose();
            sharedSam2MattingGpuLease = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true;
            previewDelay.Dispose();
            playback.Dispose();
            previewCancellation?.Cancel();
            previewCancellation?.Dispose();
            streamCancellation?.Cancel();
            streamCancellation?.Dispose();
            StopWorker();
            responseLock.Dispose();
            paintingMask?.Dispose();
            ClearPaintUndo();
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
