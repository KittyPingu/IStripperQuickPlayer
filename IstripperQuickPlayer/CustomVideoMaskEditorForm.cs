using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class CustomVideoMaskEditorForm : Form
{
    readonly string sourcePath, initialMask, maskFolder, sam2Model, maskEngine,
        profileLog, statePath;
    readonly CustomShowConfiguration configuration;
    readonly long startMs, endMs, initialFrameMs;
    readonly string temporary = Path.Combine(Path.GetTempPath(),
        "iqp-sam2-" + Guid.NewGuid().ToString("N"));
    readonly DxgiMaskPreviewControl image = new() { Dock = DockStyle.Fill,
        BackColor = Color.Black,
        Enabled = false, TabStop = true, Cursor = Cursors.Cross,
        ViewNavigationEnabled = true,
        AccessibleName = "SAM2 tracked foreground mask" };
    readonly ClipTimelineControl timeline = new() { Dock = DockStyle.Fill,
        Height = 60, AllowDividerDragging = false };
    readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 20 };
    // Fixed bounds prevent every frame/status change from re-laying out all of
    // the controls below the GPU preview.
    readonly Label position = new() { AutoSize = false, Size = new Size(300, 34),
        Padding = new Padding(8, 8, 8, 0), AutoEllipsis = true };
    readonly Label status = new() { AutoSize = false, Size = new Size(760, 36),
        Padding = new Padding(8), AutoEllipsis = true };
    readonly Button previous = new() { Text = "◀ 1 frame", AutoSize = true, Enabled = false };
    readonly Button play = new() { Text = "Play", AutoSize = true, Enabled = false };
    readonly Button next = new() { Text = "1 frame ▶", AutoSize = true, Enabled = false };
    readonly CheckBox slowMotion = new() { Text = "Slow motion (0.25×)", AutoSize = true,
        Padding = new Padding(8, 6, 0, 0), Enabled = false };
    readonly Button generation = new() { Text = "Pause and Correct", AutoSize = true,
        Enabled = false };
    readonly Button automatic = new() { Text = "Auto mask frame", AutoSize = true,
        Enabled = false };
    readonly CheckBox paintMode = new() { Text = "Paint mask", AutoSize = true,
        Enabled = false, Padding = new Padding(8, 6, 0, 0) };
    readonly TrackBar brushSize = new() { Minimum = 2, Maximum = 200, Value = 40,
        TickStyle = TickStyle.None, Width = 150, Enabled = false,
        AccessibleName = "Mask paint brush size" };
    readonly Label brushSizeLabel = new() { Text = "40 px", AutoSize = true,
        Padding = new Padding(0, 7, 8, 0) };
    readonly Label previewRateTitle = new() { Text = "Preview interval", AutoSize = true,
        Padding = new Padding(8, 7, 0, 0) };
    readonly Controls.PlaybackSeekBar previewRate = new()
    {
        Minimum = 0, Maximum = 1000, Value = 200,
        SmallChange = 10, LargeChange = 100, Size = new Size(160, 32),
        AccessibleName = "Mask preview update interval in milliseconds" };
    readonly Label previewRateLabel = new() { Text = "200 ms", AutoSize = true,
        Padding = new Padding(0, 7, 8, 0) };
    readonly Button undo = new() { Text = "Undo", AutoSize = true, Enabled = false };
    readonly Button updateMasks = new() { Text = "Update masks", AutoSize = true,
        Enabled = false };
    // TEMPORARY DIAGNOSTIC: remove after validating in-process SAM2 state reset.
    readonly Button flushSam2Vram = new() { Text = "Flush SAM2 VRAM (test)",
        AutoSize = true, Enabled = false };
    readonly Button accept = new() { Text = "Use corrected masks", AutoSize = true, Enabled = false };
    readonly System.Windows.Forms.Timer playback = new();
    // Coalesce rapid scrub positions before doing a full JPEG + mask composite.
    readonly System.Windows.Forms.Timer previewDelay = new() { Interval = 60 };
    readonly System.Windows.Forms.Timer workerClock = new() { Interval = 1000 };
    readonly SemaphoreSlim previewLoadLock = new(1, 1);
    readonly Stopwatch workerElapsed = new();
    readonly Dictionary<int, List<PointF>> points = [];
    readonly Dictionary<int, List<int>> labels = [];
    readonly Dictionary<int, string> anchorModes = [];
    readonly SortedSet<int> correctedFrames = [];
    readonly Dictionary<int, (int Start, int End)> correctedRanges = [];
    readonly Dictionary<int, List<string>> paintUndo = [];
    (int Start, int End)? activeRange;
    (int Start, int End)? activeBackward, activeForward;
    int activeAnchor, lastTimelineProgress = -1;
    Process? worker;
    Task<string>? workerError;
    CancellationTokenSource? previewLoadCancellation;
    bool previewReloadPending;
    int previewRevision;
    int frameCount;
    int displayedFrame = -1;
    int reviewWidth, reviewHeight;
    double fps = 25;
    bool updating;
    bool closing;
    int pendingFrame = -1;
    bool pendingRemoval;
    bool loadedDraftState;
    bool supportsCorrections;
    bool generationActive;
    bool pauseRequested;
    bool generationPaused;
    bool generationComplete;
    bool canStopBackward;
    int incompleteRecoveryAttempts;
    int availableEnd = -1;
    int initialFrame = -1;
    long lastGenerationPreview;
    long lastProgressUiTick;
    string pendingMode = "prompt";
    string workerStatus = "";
    string framesFolder;
    Bitmap? paintingMask;
    PointF lastPaintPoint;
    MouseButtons paintButton;
    bool painting;
    int paintingFrame = -1;
    int paintRevision;

    string FramesFolder => framesFolder;

    internal CustomVideoMaskEditorForm(string sourcePath,
        CustomShowConfiguration configuration, long startMs, long endMs,
        string initialMask, string maskFolder,
        string? clipLabel = null, string sam2Model = "base-plus",
        string? profileLog = null, string? statePath = null,
        string maskEngine = "sam2", long? initialFrameMs = null)
    {
        this.sourcePath = sourcePath;
        this.configuration = configuration;
        this.startMs = startMs;
        this.endMs = endMs;
        this.initialFrameMs = Math.Clamp(initialFrameMs ?? startMs, startMs, endMs);
        this.initialMask = initialMask;
        this.maskFolder = maskFolder;
        this.sam2Model = sam2Model;
        this.maskEngine = maskEngine;
        supportsCorrections = maskEngine != "rvm";
        this.profileLog = profileLog ?? Path.Combine(
            Path.GetDirectoryName(maskFolder)!, "processing.log");
        this.statePath = statePath ?? Path.Combine(
            Path.GetDirectoryName(maskFolder)!, "sam2-corrections.json");
        framesFolder = Path.Combine(temporary, "frames");
        LoadDraftState();
        string engineName = maskEngine switch { "rvm" => "RVM", "edgetam" => "EdgeTAM", _ => "SAM2" };
        image.AccessibleName = $"{engineName} tracked foreground mask";
        if (maskEngine == "rvm") accept.Text = "Use RVM masks";
        Text = clipLabel == null ? $"Review {engineName} Masks" :
            $"Review {engineName} Masks - {clipLabel}";
        ClientSize = new Size(1320, 980);
        MinimumSize = new Size(760, 620);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        TableLayoutPanel root = new() { Dock = DockStyle.Fill, RowCount = 5,
            ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(progress, 0, 0);
        root.Controls.Add(image, 0, 1);
        root.Controls.Add(timeline, 0, 2);
        FlowLayoutPanel precision = new() { AutoSize = true, Dock = DockStyle.Fill,
            WrapContents = false };
        precision.Controls.AddRange([previous, play, position, next, slowMotion, generation]);
        root.Controls.Add(precision, 0, 3);
        FlowLayoutPanel actions = new() { AutoSize = true, Dock = DockStyle.Fill,
            WrapContents = true };
        Button cancel = new() { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([paintMode, brushSize, brushSizeLabel, automatic,
            undo, updateMasks, previewRateTitle, previewRate, previewRateLabel,
            flushSam2Vram, accept, cancel, status]);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);
        CancelButton = cancel;

        if (!supportsCorrections)
        {
            generation.Visible = false;
            paintMode.Visible = false;
            brushSize.Visible = false;
            brushSizeLabel.Visible = false;
            automatic.Visible = false;
            undo.Visible = false;
            updateMasks.Visible = false;
        }
        flushSam2Vram.Visible = maskEngine == "sam2";

        timeline.PositionChanged += (_, _) =>
        {
            if (!generationComplete && (pauseRequested || generationPaused) &&
                timeline.PositionMs > FrameTime(AvailableLastFrame))
            {
                timeline.PositionMs = FrameTime(AvailableLastFrame);
                return;
            }
            displayedFrame = -1;
            if (!generationActive && !updating) image.Enabled = false;
            UpdatePosition();
            if (playback.Enabled) LoadPreview();
            else HoldRepeatButton.SchedulePreview(previewDelay);
        };
        timeline.FixedMarkerClicked += _ =>
        {
            playback.Stop(); play.Text = "Play";
            previewDelay.Stop(); LoadPreview(); UpdatePosition();
        };
        previewDelay.Tick += (_, _) => { previewDelay.Stop(); LoadPreview(); };
        workerClock.Tick += (_, _) => UpdateWorkerStatus();
        _ = new HoldRepeatButton(previous, () => SetFrame(CurrentFrame - 1));
        _ = new HoldRepeatButton(next, () => SetFrame(CurrentFrame + 1));
        play.Click += (_, _) => TogglePlayback();
        slowMotion.CheckedChanged += (_, _) => ConfigurePlaybackInterval();
        generation.Click += async (_, _) => await ToggleGenerationAsync();
        playback.Tick += (_, _) =>
        {
            if (CurrentFrame >= AvailableLastFrame) TogglePlayback();
            else SetFrame(CurrentFrame + 1);
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
            brushSize.Enabled = supportsCorrections && paintMode.Checked && !updating;
            image.Cursor = paintMode.Checked ? Cursors.Default : Cursors.Cross;
            image.SetBrush(null, brushSize.Value);
        };
        brushSize.ValueChanged += (_, _) =>
        {
            brushSizeLabel.Text = $"{brushSize.Value} px";
            image.SetBrush(null, brushSize.Value);
        };
        previewRate.Scroll += (_, _) => previewRateLabel.Text =
            previewRate.Value == 0 ? "Fastest (~33 ms)" : $"{previewRate.Value} ms";
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
        automatic.Click += async (_, _) => await PreviewCorrectionAsync(CurrentFrame, true);
        undo.Click += async (_, _) => await UndoClickAsync();
        updateMasks.Click += async (_, _) => await ApplyCorrectionAsync();
        flushSam2Vram.Click += async (_, _) => await FlushSam2VramAsync();
        accept.Click += async (_, _) =>
        {
            if (updating || !generationComplete) return;
            accept.Enabled = false;
            status.Text = "Finishing mask writes and saving profiling data...";
            await StopWorkerAsync(graceful: true);
            SaveDraftState();
            DialogResult = DialogResult.OK;
            closing = true;
            Close();
        };
        Shown += async (_, _) => await StartWorkerAsync();
        FormClosing += (_, _) =>
        {
            if (closing) return;
            closing = true; workerClock.Stop(); StopWorker();
        };
        AppTheme.Apply(this);
    }

    void LoadDraftState()
    {
        CustomVideoMaskDraft? saved = CustomShowMaskDraft.Load<CustomVideoMaskDraft>(statePath);
        if (saved == null || saved.SchemaVersion != CustomShowMaskDraft.SchemaVersion) return;
        loadedDraftState = true;
        frameCount = Math.Max(0, saved.FrameCount);
        if (saved.Fps > 0 && double.IsFinite(saved.Fps)) fps = saved.Fps;
        reviewWidth = Math.Max(0, saved.ReviewWidth);
        reviewHeight = Math.Max(0, saved.ReviewHeight);
        IEnumerable<CustomVideoMaskAnchor> savedAnchors = saved.Anchors;
        if (saved.PendingAnchor != null)
            savedAnchors = savedAnchors
                .Where(value => value.Frame != saved.PendingAnchor.Frame)
                .Append(saved.PendingAnchor);
        foreach (CustomVideoMaskAnchor anchor in savedAnchors)
        {
            if (anchor.Frame < 0 || anchor.Frame >= frameCount ||
                anchor.Points.Length != anchor.Labels.Length ||
                anchor.Labels.Any(value => value is not (0 or 1))) continue;
            List<PointF> framePoints = [];
            foreach (float[] value in anchor.Points)
                if (value.Length == 2 && float.IsFinite(value[0]) && float.IsFinite(value[1]))
                    framePoints.Add(new PointF(value[0], value[1]));
            if (framePoints.Count != anchor.Labels.Length) continue;
            points[anchor.Frame] = framePoints;
            labels[anchor.Frame] = [.. anchor.Labels];
            anchorModes[anchor.Frame] = anchor.Mode is "auto" or "paint"
                ? anchor.Mode : "prompt";
            correctedFrames.Add(anchor.Frame);
        }
        foreach (CustomVideoMaskRange range in saved.CorrectedRanges)
            if (range.AnchorFrame >= 0 && range.AnchorFrame < frameCount &&
                range.Start >= 0 && range.End >= range.Start && range.End < frameCount)
                correctedRanges[range.AnchorFrame] = (range.Start, range.End);
    }

    void SaveDraftState()
    {
        CustomShowStore.WriteJsonAtomic(statePath, new CustomVideoMaskDraft
        {
            FrameCount = frameCount,
            Fps = fps,
            ReviewWidth = reviewWidth,
            ReviewHeight = reviewHeight,
            Anchors = anchorModes.Keys.Order().Select(frame =>
                new CustomVideoMaskAnchor
                {
                    Frame = frame,
                    Mode = anchorModes[frame],
                    Points = points.GetValueOrDefault(frame, []).Select(
                        value => new[] { value.X, value.Y }).ToArray(),
                    Labels = [.. labels.GetValueOrDefault(frame, [])]
                }).ToArray(),
            PendingAnchor = pendingFrame < 0 || pendingRemoval ? null : new()
            {
                Frame = pendingFrame,
                Mode = pendingMode,
                Points = points.GetValueOrDefault(pendingFrame, []).Select(
                    value => new[] { value.X, value.Y }).ToArray(),
                Labels = [.. labels.GetValueOrDefault(pendingFrame, [])]
            },
            CorrectedRanges = correctedRanges.OrderBy(pair => pair.Key).Select(pair =>
                new CustomVideoMaskRange
                {
                    AnchorFrame = pair.Key,
                    Start = pair.Value.Start,
                    End = pair.Value.End
                }).ToArray()
        });
    }

    int AvailableLastFrame => Math.Clamp(availableEnd, 0, Math.Max(0, frameCount - 1));

    int CurrentFrame => frameCount == 0 ? 0 : Math.Clamp(
        (int)Math.Round(timeline.PositionMs * fps / 1000), 0, AvailableLastFrame);

    async Task StartWorkerAsync()
    {
        try
        {
            Directory.CreateDirectory(temporary);
            workerStatus = maskEngine == "rvm" ? "Generating RVM person masks..." :
                File.Exists(statePath) ? $"Loading saved {maskEngine.ToUpperInvariant()} masks and correction history..."
                : $"Extracting review frames and running the initial {maskEngine.ToUpperInvariant()} pass...";
            workerElapsed.Restart(); workerClock.Start(); UpdateWorkerStatus();
            ProcessStartInfo start = new(configuration.PythonExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            string workerScript = maskEngine == "rvm" ? "rvm_mask_worker.py" :
                "sam2_refine_worker.py";
            foreach (string argument in new[] {
                Path.Combine(AppContext.BaseDirectory, "custom-shows", workerScript),
                "--source", sourcePath, "--runtime", CustomShowProcessor.RuntimeRoot(configuration),
                "--frames", FramesFolder, "--masks", maskFolder,
                "--start-ms", startMs.ToString(CultureInfo.InvariantCulture),
                "--end-ms", endMs.ToString(CultureInfo.InvariantCulture),
                "--profile-log", this.profileLog })
                start.ArgumentList.Add(argument);
            if (maskEngine != "rvm")
                foreach (string argument in new[] { "--mask", initialMask,
                "--model", sam2Model, "--engine", maskEngine,
                "--initial-frame-ms", initialFrameMs.ToString(CultureInfo.InvariantCulture),
                "--compile-cutoff-frames", Math.Max(1,
                    configuration.Sam2CompileCutoffFrames(sam2Model))
                    .ToString(CultureInfo.InvariantCulture),
                "--cache-root", CustomShowProcessor.Sam2FrameCacheRoot,
                "--cache-limit-bytes", checked((long)Math.Clamp(
                    configuration.Sam2FrameCacheSizeGb, 0, 100) * 1024 * 1024 * 1024)
                    .ToString(CultureInfo.InvariantCulture) })
                    start.ArgumentList.Add(argument);
            if (maskEngine != "rvm" && File.Exists(statePath))
            {
                start.ArgumentList.Add("--resume-state");
                start.ArgumentList.Add(statePath);
            }
            start.Environment["IQP_FFMPEG"] = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            start.Environment["IQP_FFPROBE"] = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
            CustomShowProcessor.ConfigureProcessingPriorities(start, configuration);
            // Interactive SAM2 otherwise keeps the CUDA queue saturated enough
            // to make the Windows compositor and pointer visibly stutter.
            start.Environment["IQP_INTERACTIVE_CUDA_GAP_MS"] = "4";
            worker = Process.Start(start) ?? throw new InvalidOperationException("Mask worker did not start.");
            workerError = worker.StandardError.ReadToEndAsync();
            JsonElement terminal = await ReadUntilAsync("ready", "paused");
            workerClock.Stop(); workerElapsed.Stop(); progress.Style = ProgressBarStyle.Blocks;
            await ApplyVerifiedTerminalStateAsync(terminal);
        }
        catch (Exception error)
        {
            workerClock.Stop(); workerElapsed.Stop();
            if (!closing && !IsDisposed && !Disposing)
            {
                status.Text = "Mask review failed.";
                MessageBox.Show(this, error.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    async Task<JsonElement> ReadUntilAsync(params string[] expected)
    {
        double displayed = -1;
        while (await worker!.StandardOutput.ReadLineAsync() is string line)
        {
            JsonElement response;
            try { using JsonDocument document = JsonDocument.Parse(line);
                response = document.RootElement.Clone(); }
            catch (JsonException) { continue; }
            if (closing) throw new OperationCanceledException();
            string kind = response.GetProperty("status").GetString() ?? "";
            if (kind == "generation")
            {
                ApplyWorkerMetadata(response, freshGeneration: true);
                BeginGenerationPhase(response);
                continue;
            }
            if (expected.Contains(kind))
            {
                if (kind is "ready" or "paused")
                    ApplyWorkerMetadata(response, freshGeneration: false);
                return response;
            }
            if (kind == "error")
                throw new InvalidOperationException(response.GetProperty("message").GetString());
            if (kind == "maintenance")
            {
                workerStatus = response.TryGetProperty("message", out JsonElement message)
                    ? message.GetString() ?? "Maintaining GPU memory..."
                    : "Maintaining GPU memory...";
                UpdateWorkerStatus();
                continue;
            }
            if (kind == "progress")
            {
                double value = response.GetProperty("percent").GetDouble();
                workerStatus = response.GetProperty("message").GetString() ?? "Updating masks...";
                bool compiling = workerStatus.StartsWith("Compiling ",
                    StringComparison.Ordinal) || workerStatus.StartsWith(
                    "Loading cached ", StringComparison.Ordinal);
                long now = Environment.TickCount64;
                bool refreshUi = value >= 100 || now - lastProgressUiTick >= 250;
                if (refreshUi)
                {
                    lastProgressUiTick = now;
                    progress.Style = compiling ? ProgressBarStyle.Marquee :
                        ProgressBarStyle.Blocks;
                    if (!compiling && (value >= displayed + .5 || value >= 100))
                    {
                        displayed = value;
                        progress.Value = Math.Clamp((int)Math.Round(value), 0, 100);
                    }
                    UpdateWorkerStatus();
                    UpdateTimelineProgress(response);
                }
                UpdateGenerationPreview(response);
            }
        }
        string detail = workerError == null ? "" : (await workerError).Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? "The SAM2 mask worker stopped unexpectedly." : detail);
    }

    void ApplyWorkerMetadata(JsonElement response, bool freshGeneration)
    {
        if (response.TryGetProperty("supportsCorrections", out JsonElement correctionsValue))
            supportsCorrections = correctionsValue.GetBoolean();
        if (response.TryGetProperty("frameCount", out JsonElement frameCountValue))
            frameCount = frameCountValue.GetInt32();
        if (response.TryGetProperty("fps", out JsonElement fpsValue))
            fps = fpsValue.GetDouble();
        if (response.TryGetProperty("width", out JsonElement widthValue))
            reviewWidth = widthValue.GetInt32();
        if (response.TryGetProperty("height", out JsonElement heightValue))
            reviewHeight = heightValue.GetInt32();
        if (response.TryGetProperty("framesFolder", out JsonElement frameFolder))
            framesFolder = frameFolder.GetString() ?? framesFolder;
        if (freshGeneration && loadedDraftState && !generationActive &&
            !generationPaused && !generationComplete)
        {
            points.Clear(); labels.Clear(); anchorModes.Clear();
            correctedFrames.Clear(); correctedRanges.Clear();
            loadedDraftState = false;
        }
        if (response.TryGetProperty("initialFrame", out JsonElement initialFrameValue))
        {
            initialFrame = Math.Clamp(initialFrameValue.GetInt32(), 0,
                Math.Max(0, frameCount - 1));
            correctedFrames.Add(initialFrame);
        }
        if (frameCount <= 0) return;
        long duration = Math.Max(1, checked((long)Math.Round(frameCount * 1000 / fps)));
        timeline.Clips = [new CustomShowClip { StartMs = 0, EndMs = duration }];
        timeline.DurationMs = duration;
        timeline.FixedMarkers = TimelineMarkers();
        ConfigurePlaybackInterval();
    }

    void BeginGenerationPhase(JsonElement response)
    {
        ClearPreviewCache();
        string phase = response.TryGetProperty("phase", out JsonElement phaseValue)
            ? phaseValue.GetString() ?? "forward" : "forward";
        canStopBackward = phase == "backward" &&
            response.TryGetProperty("canStopBackward", out JsonElement stopValue) &&
            stopValue.GetBoolean();
        generationActive = true;
        pauseRequested = false;
        generationPaused = false;
        generation.Text = canStopBackward ? "Stop backward propagation" :
            phase == "backward" ? "Updating backward…" : "Pause and Correct";
        generation.Enabled = supportsCorrections && (phase == "forward" || canStopBackward);
        playback.Stop(); play.Text = "Play";
        timeline.Enabled = image.Enabled = previous.Enabled = play.Enabled = next.Enabled =
            slowMotion.Enabled = automatic.Enabled = paintMode.Enabled = brushSize.Enabled =
            undo.Enabled = updateMasks.Enabled =
            accept.Enabled = false;
        flushSam2Vram.Enabled = false;
        workerStatus = phase == "backward"
            ? "Regenerating masks backward from the correction…"
            : "Generating masks forward…";
        UpdateWorkerStatus();
    }

    void UpdateGenerationPreview(JsonElement response)
    {
        if (!generationActive || !response.TryGetProperty("previewFrameIndex",
            out JsonElement previewValue)) return;
        int frame = previewValue.GetInt32();
        if (frame < 0 || frame >= frameCount) return;
        long now = Environment.TickCount64;
        bool final = response.TryGetProperty("percent", out JsonElement percentValue) &&
            percentValue.GetDouble() >= 100;
        if (!final && now - lastGenerationPreview < PreviewIntervalMs) return;
        lastGenerationPreview = now;
        availableEnd = Math.Max(availableEnd, frame);
        if (pauseRequested)
        {
            SetGeneratedReviewRange();
            timeline.Enabled = previous.Enabled = play.Enabled = next.Enabled =
                slowMotion.Enabled = true;
            return;
        }
        timeline.PositionMs = FrameTime(frame);
        previewDelay.Stop();
        LoadPreview(); UpdatePosition();
    }

    void ApplyTerminalState(JsonElement response)
    {
        string kind = response.GetProperty("status").GetString() ?? "";
        bool userRequestedPause = pauseRequested;
        generationActive = false;
        pauseRequested = false;
        canStopBackward = false;
        generationPaused = kind == "paused";
        generationComplete = kind == "ready";
        availableEnd = generationComplete ? Math.Max(0, frameCount - 1) :
            response.TryGetProperty("availableEnd", out JsonElement availableValue)
                ? availableValue.GetInt32() : availableEnd;
        if (response.TryGetProperty("anchorFrame", out JsonElement anchorValue) &&
            response.TryGetProperty("completedStart", out JsonElement startValue) &&
            response.TryGetProperty("completedEnd", out JsonElement endValue))
        {
            int anchor = anchorValue.GetInt32();
            bool removing = response.TryGetProperty("removing", out JsonElement removingValue) &&
                removingValue.GetBoolean();
            if (!removing && anchorModes.ContainsKey(anchor))
                correctedRanges[anchor] = (startValue.GetInt32(), endValue.GetInt32());
        }
        long duration = Math.Max(1, checked((long)Math.Round(frameCount * 1000 / fps)));
        timeline.Clips = [new CustomShowClip { StartMs = 0, EndMs = duration }];
        timeline.DurationMs = duration;
        timeline.FixedMarkers = TimelineMarkers();
        RefreshTimelineRanges();
        ConfigurePlaybackInterval();
        timeline.Enabled = image.Enabled = previous.Enabled = play.Enabled = next.Enabled =
            slowMotion.Enabled = true;
        automatic.Enabled = supportsCorrections;
        paintMode.Enabled = supportsCorrections;
        brushSize.Enabled = supportsCorrections && paintMode.Checked;
        accept.Enabled = generationComplete;
        generation.Text = generationPaused ? "Continue generation" : "Generation complete";
        generation.Enabled = supportsCorrections && generationPaused;
        flushSam2Vram.Enabled = maskEngine == "sam2";
        progress.Value = generationComplete ? 100 : progress.Value;
        if (!userRequestedPause &&
            response.TryGetProperty("pauseFrame", out JsonElement pauseValue))
            timeline.PositionMs = FrameTime(Math.Min(pauseValue.GetInt32(), AvailableLastFrame));
        previewDelay.Stop();
        LoadPreview(); UpdatePosition();
        if (generationComplete)
        {
            SaveDraftState();
            string execution = response.TryGetProperty("execution", out JsonElement executionValue)
                ? executionValue.GetString() ?? "eager" : "eager";
            bool resumed = response.TryGetProperty("resumed", out JsonElement resumedValue) &&
                resumedValue.GetBoolean();
            status.Text = (resumed ? "Recovered saved masks and correction history · " : "") +
                $"{response.GetProperty("checkpoint").GetString()} · " +
                $"{response.GetProperty("device").GetString()?.ToUpperInvariant()}/" +
                $"{response.GetProperty("precision").GetString()} · {execution}" +
                (response.TryGetProperty("frameCacheHit", out JsonElement cacheHit) &&
                    cacheHit.GetBoolean() ? " · cached frames" : "");
        }
        else status.Text = "Generation paused. Correct this mask, then update masks, or continue.";
    }

    async Task ApplyVerifiedTerminalStateAsync(JsonElement response)
    {
        string kind = response.GetProperty("status").GetString() ?? "";
        if (kind == "ready")
        {
            int contiguousEnd = await Task.Run(ContiguousMaskEnd);
            if (contiguousEnd < frameCount - 1)
            {
                if (++incompleteRecoveryAttempts > 2)
                    throw new InvalidDataException(
                        $"SAM2 reported completion, but only {contiguousEnd + 1:N0} " +
                        $"of {frameCount:N0} masks were saved. The completed prefix " +
                        "and all corrections remain preserved in the draft.");
                availableEnd = contiguousEnd;
                generationComplete = false;
                generationPaused = true;
                status.Text = $"Recovering saved masks from frame " +
                    $"{contiguousEnd + 2:N0} of {frameCount:N0}…";
                await StopWorkerAsync(graceful: false);
                await StartWorkerAsync();
                return;
            }
        }
        incompleteRecoveryAttempts = 0;
        ApplyTerminalState(response);
    }

    int ContiguousMaskEnd()
    {
        for (int frame = 0; frame < frameCount; frame++)
            if (!File.Exists(Path.Combine(maskFolder, $"{frame + 1:00000000}.png")))
                return frame - 1;
        return frameCount - 1;
    }

    async Task ToggleGenerationAsync()
    {
        if (!supportsCorrections || worker == null || worker.HasExited) return;
        if (generationActive)
        {
            generation.Enabled = false;
            generation.Text = canStopBackward ? "Stopping backward…" : "Pausing…";
            status.Text = canStopBackward
                ? "Finishing the current backward mask, then updating forward…"
                : "Pausing after the current mask finishes…";
            string command = canStopBackward ? "stop-backward" : "pause";
            if (!canStopBackward)
            {
                pauseRequested = true;
                workerStatus = "Pausing SAM2; completed masks are available for review...";
                playback.Stop();
                play.Text = "Play";
                SetGeneratedReviewRange();
                timeline.Enabled = previous.Enabled = play.Enabled = next.Enabled =
                    slowMotion.Enabled = availableEnd >= 0;
            }
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { command }));
            await worker.StandardInput.FlushAsync();
            return;
        }
        if (!generationPaused || updating) return;
        updating = true;
        workerElapsed.Restart(); workerClock.Start();
        try
        {
            await worker.StandardInput.WriteLineAsync("{\"command\":\"continue\"}");
            await worker.StandardInput.FlushAsync();
            JsonElement terminal = await ReadUntilAsync("ready", "paused");
            workerClock.Stop(); workerElapsed.Stop();
            await ApplyVerifiedTerminalStateAsync(terminal);
        }
        catch (Exception error)
        {
            workerClock.Stop(); workerElapsed.Stop();
            if (!closing && !IsDisposed && !Disposing)
                MessageBox.Show(this, error.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { updating = false; }
    }

    // TEMPORARY DIAGNOSTIC: exercises an in-process inference-state reset while
    // keeping the Python process and loaded SAM2 model alive.
    async Task FlushSam2VramAsync()
    {
        if (maskEngine != "sam2" || updating || generationActive || worker == null ||
            worker.HasExited || updateMasks.Enabled) return;
        updating = true;
        flushSam2Vram.Enabled = false;
        status.Text = "Resetting SAM2 tracking state and measuring VRAM...";
        try
        {
            SaveDraftState();
            await worker.StandardInput.WriteLineAsync("{\"command\":\"flush-vram\"}");
            await worker.StandardInput.FlushAsync();
            JsonElement result = await ReadUntilAsync("flushed");
            static string Vram(JsonElement value, string name) =>
                value.TryGetProperty(name, out JsonElement bytes)
                    ? $"{bytes.GetInt64() / 1024d / 1024d:0} MB" : "unknown";
            status.Text = "SAM2 VRAM allocated: " +
                $"{Vram(result, "allocatedBefore")} before, " +
                $"{Vram(result, "allocatedReleased")} after release, " +
                $"{Vram(result, "allocatedAfter")} after state rebuild; reserved " +
                $"{Vram(result, "reservedBefore")} -> {Vram(result, "reservedAfter")}.";
        }
        catch (Exception error)
        {
            if (!closing && !IsDisposed && !Disposing)
            {
                // The latest successful preview and its mask are durable before
                // any recovery attempt. Restarting with --resume-state rebuilds
                // SAM2 around that anchor instead of leaving the editor disabled.
                SaveDraftState();
                string detail = error.Message;
                await StopWorkerAsync(graceful: false);
                status.Text = "SAM2 stopped; recovering the saved correction...";
                await StartWorkerAsync();
                if (worker is { HasExited: false })
                    status.Text = "SAM2 recovered after an error. Your latest " +
                        "successful correction was retained. (" + detail + ")";
            }
        }
        finally
        {
            updating = false;
            flushSam2Vram.Enabled = maskEngine == "sam2" &&
                !generationActive && !updateMasks.Enabled;
        }
    }

    void SetGeneratedReviewRange()
    {
        if (frameCount <= 0 || availableEnd < 0) return;
        long duration = Math.Max(1, checked((long)Math.Round(frameCount * 1000 / fps)));
        long current = Math.Min(timeline.PositionMs, FrameTime(AvailableLastFrame));
        timeline.Clips = [new CustomShowClip { StartMs = 0, EndMs = duration }];
        timeline.DurationMs = duration;
        timeline.PositionMs = current;
        timeline.FixedMarkers = TimelineMarkers();
    }

    void UpdateWorkerStatus()
    {
        if (string.IsNullOrEmpty(workerStatus)) return;
        TimeSpan value = workerElapsed.Elapsed;
        status.Text = $"{workerStatus}  ·  Elapsed {(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    async void Image_MouseClick(object? sender, MouseEventArgs e)
    {
        if (paintMode.Checked) return;
        if (!supportsCorrections || updating || !image.HasImage || e.Button is not
            (MouseButtons.Left or MouseButtons.Right)) return;
        if (displayedFrame != CurrentFrame)
        {
            status.Text = "Wait for this frame's preview to finish loading before editing.";
            return;
        }
        PointF? point = image.ImagePoint(e.Location);
        if (point == null) return;
        int frame = CurrentFrame;
        if (!points.TryGetValue(frame, out List<PointF>? framePoints))
            points[frame] = framePoints = [];
        if (!labels.TryGetValue(frame, out List<int>? frameLabels))
            labels[frame] = frameLabels = [];
        framePoints.Add(point.Value);
        frameLabels.Add(e.Button == MouseButtons.Right ? 0 : 1);
        image.SetMarkers([.. framePoints], [.. frameLabels]);
        await PreviewCorrectionAsync(frame, useAutomatic:
            anchorModes.GetValueOrDefault(frame) == "auto" ||
            pendingFrame == frame && pendingMode == "auto");
    }

    void Image_MouseDown(object? sender, MouseEventArgs e)
    {
        if (!paintMode.Checked || !supportsCorrections || updating || !image.HasImage ||
            e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
        if (displayedFrame != CurrentFrame)
        {
            status.Text = "Wait for this frame's preview to finish loading before painting.";
            return;
        }
        PointF? point = image.ImagePoint(e.Location);
        if (point == null) return;
        int frame = CurrentFrame;
        string maskPath = MaskPath(frame);
        if (!File.Exists(maskPath)) return;
        try
        {
            paintingMask?.Dispose();
            paintingMask = LoadBitmap(maskPath);
            InvalidatePreview(frame);
            string undoPath = Path.Combine(temporary,
                $"paint-undo-{frame}-{++paintRevision}.png");
            paintingMask.Save(undoPath, System.Drawing.Imaging.ImageFormat.Png);
            if (!paintUndo.TryGetValue(frame, out List<string>? undoPaths))
                paintUndo[frame] = undoPaths = [];
            undoPaths.Add(undoPath);
            painting = true;
            paintingFrame = frame;
            paintButton = e.Button;
            lastPaintPoint = point.Value;
            image.Capture = true;
            Rectangle dirty = ApplyBrush(point.Value, point.Value);
            RenderPreview(frame, paintingMask, dirty);
            UpdateUndoState(frame);
        }
        catch (Exception error)
        {
            paintingMask?.Dispose(); paintingMask = null;
            painting = false; image.Capture = false;
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void Image_MouseMove(object? sender, MouseEventArgs e)
    {
        if (paintMode.Checked)
            image.SetBrush(image.ImagePoint(e.Location), brushSize.Value);
        if (!painting || paintingMask == null || paintingFrame != CurrentFrame) return;
        PointF? point = image.ImagePoint(e.Location);
        if (point == null) return;
        Rectangle dirty = ApplyBrush(lastPaintPoint, point.Value);
        lastPaintPoint = point.Value;
        RenderPreview(paintingFrame, paintingMask, dirty);
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
        if ((ModifierKeys & Keys.Control) != 0 && paintMode.Checked)
        {
            ResizeBrush(e.Delta);
            return;
        }
        image.ZoomAt(e.Location, e.Delta);
    }

    async void Image_MouseUp(object? sender, MouseEventArgs e)
    {
        if (!painting || paintingMask == null) return;
        painting = false; image.Capture = false;
        int frame = paintingFrame;
        string paintedPath = Path.Combine(temporary,
            $"paint-preview-{frame}-{++paintRevision}.png");
        try
        {
            paintingMask.Save(paintedPath, System.Drawing.Imaging.ImageFormat.Png);
            paintingMask.Dispose(); paintingMask = null;
            await PreviewCorrectionAsync(frame, paintedMask: paintedPath);
        }
        finally
        {
            paintingMask?.Dispose(); paintingMask = null;
            try { if (File.Exists(paintedPath)) File.Delete(paintedPath); } catch { }
        }
    }

    Rectangle ApplyBrush(PointF from, PointF to)
    {
        using Graphics graphics = Graphics.FromImage(paintingMask!);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        using Pen pen = new(paintButton == MouseButtons.Right ? Color.Black : Color.White,
            brushSize.Value)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };
        graphics.DrawLine(pen, from, to);
        float radius = brushSize.Value / 2f;
        using Brush endpoint = new SolidBrush(
            paintButton == MouseButtons.Right ? Color.Black : Color.White);
        graphics.FillEllipse(endpoint, from.X - radius, from.Y - radius,
            brushSize.Value, brushSize.Value);
        graphics.FillEllipse(endpoint, to.X - radius, to.Y - radius,
            brushSize.Value, brushSize.Value);
        Rectangle dirty = Rectangle.Ceiling(RectangleF.FromLTRB(
            Math.Min(from.X, to.X) - radius - 2,
            Math.Min(from.Y, to.Y) - radius - 2,
            Math.Max(from.X, to.X) + radius + 2,
            Math.Max(from.Y, to.Y) + radius + 2));
        dirty.Intersect(new Rectangle(Point.Empty, paintingMask!.Size));
        return dirty;
    }

    async Task UndoClickAsync()
    {
        if (!supportsCorrections) return;
        int frame = CurrentFrame;
        if (!updating && paintUndo.TryGetValue(frame, out List<string>? strokes) &&
            strokes.Count > 0)
        {
            string previousMask = strokes[^1];
            strokes.RemoveAt(strokes.Count - 1);
            try
            {
                if (strokes.Count == 0)
                {
                    paintUndo.Remove(frame);
                    await PreviewCorrectionAsync(frame, reset: true);
                    status.Text = "Paint stroke undone; the previous mask was restored.";
                }
                else
                {
                    await PreviewCorrectionAsync(frame, paintedMask: previousMask);
                    status.Text = "Paint stroke undone.";
                }
            }
            finally
            {
                try { if (File.Exists(previousMask)) File.Delete(previousMask); } catch { }
            }
            UpdateUndoState(frame);
            return;
        }
        if (updating || !points.TryGetValue(frame, out List<PointF>? framePoints) ||
            framePoints.Count == 0) return;
        framePoints.RemoveAt(framePoints.Count - 1);
        labels[frame].RemoveAt(labels[frame].Count - 1);
        image.SetMarkers([.. framePoints], [.. labels[frame]]);
        if (framePoints.Count == 0)
        {
            if (anchorModes.GetValueOrDefault(frame) == "auto")
            {
                await PreviewCorrectionAsync(frame, useAutomatic: true);
                return;
            }
            bool removeAnchor = anchorModes.GetValueOrDefault(frame) == "prompt";
            if (!removeAnchor && !anchorModes.ContainsKey(frame))
            {
                points.Remove(frame);
                labels.Remove(frame);
            }
            await PreviewCorrectionAsync(frame, reset: !removeAnchor,
                removeAnchor: removeAnchor);
        }
        else await PreviewCorrectionAsync(frame, useAutomatic:
            anchorModes.GetValueOrDefault(frame) == "auto" ||
            pendingFrame == frame && pendingMode == "auto");
    }

    async Task PreviewCorrectionAsync(int frame, bool useAutomatic = false,
        bool reset = false, bool removeAnchor = false, string? paintedMask = null)
    {
        if (!supportsCorrections || worker == null || worker.HasExited || updating) return;
        points.TryGetValue(frame, out List<PointF>? framePoints);
        labels.TryGetValue(frame, out List<int>? frameLabels);
        updating = true;
        playback.Stop(); play.Text = "Play";
        timeline.Enabled = false;
        image.Enabled = automatic.Enabled = paintMode.Enabled = brushSize.Enabled =
            undo.Enabled = generation.Enabled = flushSam2Vram.Enabled = false;
        previous.Enabled = play.Enabled = next.Enabled = slowMotion.Enabled = false;
        status.Text = removeAnchor ? "Previewing this frame without its correction..." :
            reset ? "Restoring this frame's tracked mask..." : useAutomatic ?
            "Generating automatic mask for this frame..." : paintedMask != null ?
            "Applying the painted mask..." :
            "Updating this frame preview...";
        try
        {
            await worker!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                command = removeAnchor ? "remove-preview" : reset ? "reset" :
                    paintedMask != null ? "mask" : useAutomatic ? "auto" : "prompt", frame,
                mask = paintedMask,
                points = (framePoints ?? []).Select(value => new[] { value.X, value.Y }),
                labels = frameLabels ?? []
            }));
            await worker.StandardInput.FlushAsync();
            JsonElement response = await ReadUntilAsync("preview");
            InvalidatePreview(frame);
            if (paintedMask != null)
            {
                // The painted bitmap was based on the currently displayed mask,
                // so any earlier click result is already baked into it. Do not
                // resend those old clicks if the user switches back to click
                // mode; that would resurrect superseded prompt state.
                points.Remove(frame);
                labels.Remove(frame);
            }
            pendingFrame = reset ? -1 : frame;
            pendingRemoval = removeAnchor;
            if (!reset && !removeAnchor)
                pendingMode = paintedMask != null ? "paint" :
                    useAutomatic ? "auto" : "prompt";
            SaveDraftState();
            updateMasks.Enabled = !reset;
            LoadPreview();
            status.Text = removeAnchor
                ? "Correction removed in this preview. Choose Update masks to regenerate its range."
                : reset ? "Last click undone; the tracked mask was restored." :
                useAutomatic && response.TryGetProperty("candidates", out JsonElement count)
                ? $"Automatic frame mask selected from {count.GetInt32()} candidates. Add clicks or update masks."
                : paintedMask != null
                ? "Painted mask ready. Add another stroke or update masks from this anchor."
                : "Frame preview updated. Add more clicks, then update masks from this anchor.";
        }
        catch (Exception error)
        {
            if (!closing && !IsDisposed && !Disposing)
            {
                // Preserve the latest successful pending preview, then rebuild
                // the worker from that atomic draft instead of leaving the
                // editor disabled after an inference-state failure.
                SaveDraftState();
                string detail = error.Message;
                await StopWorkerAsync(graceful: false);
                status.Text = "SAM2 stopped; recovering the saved correction...";
                await StartWorkerAsync();
                if (worker is { HasExited: false })
                    status.Text = "SAM2 recovered after an error. Your latest " +
                        "successful correction was retained. (" + detail + ")";
            }
        }
        finally
        {
            updating = false;
            if (!closing && !IsDisposed)
            {
                image.Enabled = true;
                automatic.Enabled = supportsCorrections;
                paintMode.Enabled = supportsCorrections;
                brushSize.Enabled = supportsCorrections && paintMode.Checked;
                bool canMove = pendingFrame < 0;
                timeline.Enabled = canMove;
                previous.Enabled = play.Enabled = next.Enabled = slowMotion.Enabled = canMove;
                accept.Enabled = canMove && generationComplete;
                generation.Enabled = canMove && generationPaused;
                flushSam2Vram.Enabled = maskEngine == "sam2" && canMove;
                UpdateUndoState(CurrentFrame);
            }
        }
    }

    async Task ApplyCorrectionAsync()
    {
        if (!supportsCorrections || pendingFrame < 0 || worker == null ||
            worker.HasExited || updating) return;
        int frame = pendingFrame;
        activeRange = CorrectionRange(frame);
        activeAnchor = frame;
        activeBackward = activeForward = null;
        lastTimelineProgress = -1;
        RefreshTimelineRanges();
        updating = true;
        updateMasks.Enabled = image.Enabled = automatic.Enabled = paintMode.Enabled =
            brushSize.Enabled = accept.Enabled =
            generation.Enabled = flushSam2Vram.Enabled = false;
        progress.Value = 0;
        workerElapsed.Restart(); workerClock.Start();
        try
        {
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                command = pendingRemoval ? "remove" : "update", frame
            }));
            await worker.StandardInput.FlushAsync();
            JsonElement terminal = await ReadUntilAsync("ready", "paused");
            workerClock.Stop(); workerElapsed.Stop();
            int completedStart = terminal.TryGetProperty("completedStart",
                out JsonElement startValue) ? startValue.GetInt32() : activeRange!.Value.Start;
            int completedEnd = terminal.TryGetProperty("completedEnd",
                out JsonElement endValue) ? endValue.GetInt32() : activeRange!.Value.End;
            if (pendingRemoval)
            {
                points.Remove(frame);
                labels.Remove(frame);
                anchorModes.Remove(frame);
                correctedFrames.Remove(frame);
                if (frame == initialFrame) correctedFrames.Add(frame);
                correctedRanges.Remove(frame);
            }
            else
            {
                anchorModes[frame] = pendingMode;
                if (pendingMode == "paint")
                {
                    points.Remove(frame);
                    labels.Remove(frame);
                }
                correctedFrames.Add(frame);
                correctedRanges[frame] = (completedStart, completedEnd);
            }
            ClearPaintUndo(frame);
            activeRange = null;
            activeBackward = activeForward = null;
            timeline.FixedMarkers = TimelineMarkers();
            RefreshTimelineRanges();
            pendingFrame = -1;
            pendingRemoval = false;
            await ApplyVerifiedTerminalStateAsync(terminal);
            SaveDraftState();
            status.Text = generationPaused
                ? "Forward generation paused. Correct another generated mask, or continue."
                : anchorModes.ContainsKey(frame)
                    ? "Correction saved as an anchor. Scrub to another frame that needs correction."
                    : "Correction removed and its surrounding masks regenerated.";
        }
        catch (Exception error)
        {
            workerClock.Stop(); workerElapsed.Stop();
            activeRange = activeBackward = activeForward = null;
            RefreshTimelineRanges();
            if (!closing && !IsDisposed && !Disposing)
                MessageBox.Show(this, error.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            updating = false;
            if (!closing && !IsDisposed)
            {
                bool canReview = generationPaused || generationComplete;
                timeline.Enabled = image.Enabled = previous.Enabled = play.Enabled = next.Enabled =
                    slowMotion.Enabled = canReview;
                accept.Enabled = generationComplete;
                automatic.Enabled = supportsCorrections && canReview;
                paintMode.Enabled = supportsCorrections && canReview;
                brushSize.Enabled = supportsCorrections && canReview && paintMode.Checked;
                generation.Enabled = generationPaused;
            }
        }
    }

    (int Start, int End) CorrectionRange(int frame) => (
        correctedFrames.Where(value => value < frame).DefaultIfEmpty(0).Max(),
        correctedFrames.Where(value => value > frame).DefaultIfEmpty(frameCount - 1).Min());

    long FrameTime(int frame) => checked((long)Math.Round(frame * 1000 / fps));

    long[] TimelineMarkers() => anchorModes.Keys
        .Concat(initialFrame >= 0 ? [initialFrame] : [])
        .Distinct().Order().Select(FrameTime).ToArray();

    void UpdateTimelineProgress(JsonElement response)
    {
        if (activeRange == null ||
            !response.TryGetProperty("frameIndex", out JsonElement frameValue) ||
            !response.TryGetProperty("reverse", out JsonElement reverseValue)) return;
        int frame = frameValue.GetInt32();
        int completedStart = response.TryGetProperty("completedStart",
            out JsonElement completedStartValue) ? completedStartValue.GetInt32() :
            Math.Min(frame, activeAnchor);
        int completedEnd = response.TryGetProperty("completedEnd",
            out JsonElement completedEndValue) ? completedEndValue.GetInt32() :
            Math.Max(frame, activeAnchor);
        int minimumStep = Math.Max(1, frameCount / Math.Max(1, timeline.ClientSize.Width));
        if (lastTimelineProgress >= 0 && Math.Abs(frame - lastTimelineProgress) < minimumStep)
            return;
        lastTimelineProgress = frame;
        if (reverseValue.GetBoolean()) activeBackward = (completedStart, completedEnd);
        else activeForward = (completedStart, completedEnd);
        RefreshTimelineRanges();
    }

    void RefreshTimelineRanges()
    {
        List<(long, long, Color)> ranges = correctedRanges.Values.Select(value =>
            (FrameTime(value.Start), FrameTime(value.End), Color.FromArgb(72, 180, 125))).ToList();
        if (activeRange is { } active)
            ranges.Add((FrameTime(active.Start), FrameTime(active.End), Color.FromArgb(90, 90, 90)));
        foreach ((int start, int end) in new[] { activeBackward, activeForward }
            .Where(value => value.HasValue).Select(value => value!.Value))
            ranges.Add((FrameTime(start), FrameTime(end), Color.FromArgb(72, 180, 125)));
        timeline.HighlightedRanges = ranges;
    }

    void ConfigurePlaybackInterval() => playback.Interval = Math.Clamp(
        (int)Math.Round(1000 / fps * (slowMotion.Checked ? 4 : 1)), 15, 1000);

    void TogglePlayback()
    {
        if (playback.Enabled) { playback.Stop(); play.Text = "Play"; }
        else
        {
            if (CurrentFrame >= AvailableLastFrame) SetFrame(0);
            playback.Start(); play.Text = "Pause";
        }
    }

    void SetFrame(int frame)
    {
        if (frameCount == 0) return;
        timeline.PositionMs = checked((long)Math.Round(
            Math.Clamp(frame, 0, AvailableLastFrame) * 1000 / fps));
    }

    void UpdatePosition() => position.Text = frameCount == 0 ? "Preparing masks..." :
        $"Frame {CurrentFrame + 1:N0}/{frameCount:N0}  ·  " +
        TimeSpan.FromSeconds(CurrentFrame / fps).ToString(@"m\:ss\.fff");

    void LoadPreview()
    {
        if (frameCount == 0) return;
        int frame = CurrentFrame;
        string framePath = Path.Combine(FramesFolder, $"{frame + 1:00000000}.jpg"),
            maskPath = MaskPath(frame);
        if (!File.Exists(framePath) || !File.Exists(maskPath)) return;
        // Never build a queue of obsolete JPEG/mask decodes. Generation can
        // report frames faster than decoding (especially at a 0 ms setting),
        // so retain only the newest requested frame while one decode is active.
        if (previewLoadLock.CurrentCount == 0)
        {
            previewReloadPending = true;
            previewLoadCancellation?.Cancel();
            return;
        }
        previewReloadPending = false;
        previewLoadCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        previewLoadCancellation = cancellation;
        int revision = ++previewRevision;
        PointF[] framePoints = points.GetValueOrDefault(frame)?.ToArray() ?? [];
        int[] frameLabels = labels.GetValueOrDefault(frame)?.ToArray() ?? [];
        _ = LoadPreviewAsync(frame, framePath, maskPath, framePoints, frameLabels,
            revision, cancellation);
    }

    async Task LoadPreviewAsync(int frame, string framePath, string maskPath,
        PointF[] framePoints, int[] frameLabels,
        int revision, CancellationTokenSource cancellation)
    {
        try
        {
            await previewLoadLock.WaitAsync(cancellation.Token);
            Bitmap? source = null, mask = null;
            try
            {
                bool retainSource = displayedFrame == frame && image.HasImage;
                if (retainSource)
                    mask = await Task.Run(() => LoadBitmap(maskPath), cancellation.Token);
                else
                    (source, mask) = await Task.Run(() =>
                        (LoadBitmap(framePath), LoadBitmap(maskPath)), cancellation.Token);
                if (cancellation.IsCancellationRequested || revision != previewRevision ||
                    frame != CurrentFrame || closing || IsDisposed) return;
                if (source == null)
                    image.SetMaskOwned(mask, framePoints, frameLabels);
                else
                {
                    image.SetPreviewOwned(source, mask, framePoints, frameLabels);
                    source = null;
                }
                mask = null;
                displayedFrame = frame;
                if (!generationActive && !updating) image.Enabled = true;
                UpdateUndoState(frame);
                if (image.RendererFailure is Exception error)
                    status.Text = "DXGI mask preview unavailable: " + error.Message;
            }
            finally
            {
                source?.Dispose(); mask?.Dispose(); previewLoadLock.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!closing && !IsDisposed) status.Text =
                "Mask preview unavailable: " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(previewLoadCancellation, cancellation))
                previewLoadCancellation = null;
            cancellation.Dispose();
            if (previewReloadPending && !closing && !IsDisposed && !Disposing)
            {
                previewReloadPending = false;
                BeginInvoke(LoadPreview);
            }
        }
    }

    void InvalidatePreview(int frame)
    {
        previewLoadCancellation?.Cancel();
        previewRevision++;
    }

    void ClearPreviewCache()
    {
        previewLoadCancellation?.Cancel();
        previewReloadPending = false;
        previewRevision++;
    }

    void RenderPreview(int frame, Bitmap mask, Rectangle? dirty = null)
    {
        PointF[] framePoints = points.GetValueOrDefault(frame)?.ToArray() ?? [];
        int[] frameLabels = labels.GetValueOrDefault(frame)?.ToArray() ?? [];
        if (dirty is Rectangle region)
            image.SetMaskRegion(mask, region, framePoints, frameLabels);
        else image.SetMask(mask, framePoints, frameLabels);
        displayedFrame = frame;
        UpdateUndoState(frame);
    }

    string MaskPath(int frame) =>
        Path.Combine(maskFolder, $"{frame + 1:00000000}.png");

    // Zero means fastest useful display rate, not an unbounded decode/present
    // loop. 33 ms aligns the preview with a typical 30 Hz UI cadence.
    int PreviewIntervalMs => Math.Max(33, previewRate.Value);

    void UpdateUndoState(int frame)
    {
        bool hasStroke = paintUndo.TryGetValue(frame, out List<string>? strokes) &&
            strokes.Count > 0;
        bool hasClick = points.TryGetValue(frame, out List<PointF>? framePoints) &&
            framePoints.Count > 0;
        undo.Enabled = !updating && supportsCorrections && (hasStroke || hasClick);
        undo.Text = hasStroke ? "Undo stroke" : "Undo click";
    }

    void ClearPaintUndo(int frame)
    {
        if (!paintUndo.Remove(frame, out List<string>? paths)) return;
        foreach (string path in paths)
            try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static Bitmap LoadBitmap(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using Image source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    void StopWorker()
    {
        playback.Stop(); previewDelay.Stop();
        try { if (worker is { HasExited: false }) worker.Kill(true); } catch { }
        worker?.Dispose(); worker = null;
    }

    async Task StopWorkerAsync(bool graceful)
    {
        playback.Stop(); previewDelay.Stop(); workerClock.Stop();
        Process? process = worker;
        if (process == null) return;
        try
        {
            if (!process.HasExited && graceful)
            {
                await process.StandardInput.WriteLineAsync("{\"command\":\"quit\"}");
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        catch { }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(); } catch { }
            process.Dispose();
            if (ReferenceEquals(worker, process)) worker = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearPreviewCache();
            StopWorker(); paintingMask?.Dispose();
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
