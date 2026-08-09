using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class CustomVideoMaskEditorForm : Form
{
    readonly string sourcePath, initialMask, maskFolder, sam2Model, profileLog,
        statePath;
    readonly CustomShowConfiguration configuration;
    readonly long startMs, endMs;
    readonly string temporary = Path.Combine(Path.GetTempPath(),
        "iqp-sam2-" + Guid.NewGuid().ToString("N"));
    readonly PictureBox image = new() { Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black,
        Enabled = false, AccessibleName = "SAM2 tracked foreground mask" };
    readonly ClipTimelineControl timeline = new() { Dock = DockStyle.Fill,
        Height = 60, AllowDividerDragging = false };
    readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 20 };
    readonly Label position = new() { AutoSize = true, Padding = new Padding(8, 8, 8, 0) };
    readonly Label status = new() { AutoSize = true, Padding = new Padding(8) };
    readonly Button previous = new() { Text = "◀ 1 frame", AutoSize = true, Enabled = false };
    readonly Button play = new() { Text = "Play", AutoSize = true, Enabled = false };
    readonly Button next = new() { Text = "1 frame ▶", AutoSize = true, Enabled = false };
    readonly CheckBox slowMotion = new() { Text = "Slow motion (0.25×)", AutoSize = true,
        Padding = new Padding(8, 6, 0, 0), Enabled = false };
    readonly Button automatic = new() { Text = "Auto mask frame", AutoSize = true,
        Enabled = false };
    readonly Button undo = new() { Text = "Undo click", AutoSize = true, Enabled = false };
    readonly Button updateMasks = new() { Text = "Update masks", AutoSize = true,
        Enabled = false };
    readonly Button accept = new() { Text = "Use corrected masks", AutoSize = true, Enabled = false };
    readonly System.Windows.Forms.Timer playback = new();
    readonly System.Windows.Forms.Timer previewDelay = new() { Interval = 50 };
    readonly System.Windows.Forms.Timer workerClock = new() { Interval = 1000 };
    readonly Stopwatch workerElapsed = new();
    readonly Dictionary<int, List<PointF>> points = [];
    readonly Dictionary<int, List<int>> labels = [];
    readonly Dictionary<int, string> anchorModes = [];
    readonly SortedSet<int> correctedFrames = [];
    readonly Dictionary<int, (int Start, int End)> correctedRanges = [];
    (int Start, int End)? activeRange;
    (int Start, int End)? activeBackward, activeForward;
    int activeAnchor, lastTimelineProgress = -1;
    Process? worker;
    Task<string>? workerError;
    int frameCount;
    int reviewWidth, reviewHeight;
    double fps = 25;
    bool updating;
    bool closing;
    int pendingFrame = -1;
    bool pendingRemoval;
    bool loadedDraftState;
    string pendingMode = "prompt";
    string workerStatus = "";
    string framesFolder;

    string FramesFolder => framesFolder;

    internal CustomVideoMaskEditorForm(string sourcePath,
        CustomShowConfiguration configuration, long startMs, long endMs,
        string initialMask, string maskFolder,
        string? clipLabel = null, string sam2Model = "base-plus",
        string? profileLog = null, string? statePath = null)
    {
        this.sourcePath = sourcePath;
        this.configuration = configuration;
        this.startMs = startMs;
        this.endMs = endMs;
        this.initialMask = initialMask;
        this.maskFolder = maskFolder;
        this.sam2Model = sam2Model;
        this.profileLog = profileLog ?? Path.Combine(
            Path.GetDirectoryName(maskFolder)!, "processing.log");
        this.statePath = statePath ?? Path.Combine(
            Path.GetDirectoryName(maskFolder)!, "sam2-corrections.json");
        framesFolder = Path.Combine(temporary, "frames");
        LoadDraftState();
        Text = clipLabel == null ? "Review and Correct SAM2 Masks" :
            $"Review and Correct SAM2 Masks - {clipLabel}";
        ClientSize = new Size(1200, 900);
        MinimumSize = new Size(760, 620);
        StartPosition = FormStartPosition.CenterParent;

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
        precision.Controls.AddRange([previous, play, position, next, slowMotion]);
        root.Controls.Add(precision, 0, 3);
        FlowLayoutPanel actions = new() { AutoSize = true, Dock = DockStyle.Fill,
            WrapContents = false };
        Button cancel = new() { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([automatic, undo, updateMasks, accept, cancel, status]);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);
        CancelButton = cancel;

        timeline.PositionChanged += (_, _) =>
        {
            UpdatePosition();
            if (playback.Enabled) LoadPreview();
            else { previewDelay.Stop(); previewDelay.Start(); }
        };
        timeline.FixedMarkerClicked += _ =>
        {
            playback.Stop(); play.Text = "Play";
            previewDelay.Stop(); LoadPreview(); UpdatePosition();
        };
        previewDelay.Tick += (_, _) => { previewDelay.Stop(); LoadPreview(); };
        workerClock.Tick += (_, _) => UpdateWorkerStatus();
        previous.Click += (_, _) => SetFrame(CurrentFrame - 1);
        next.Click += (_, _) => SetFrame(CurrentFrame + 1);
        play.Click += (_, _) => TogglePlayback();
        slowMotion.CheckedChanged += (_, _) => ConfigurePlaybackInterval();
        playback.Tick += (_, _) =>
        {
            if (CurrentFrame >= frameCount - 1) TogglePlayback();
            else SetFrame(CurrentFrame + 1);
        };
        image.MouseClick += Image_MouseClick;
        automatic.Click += async (_, _) => await PreviewCorrectionAsync(CurrentFrame, true);
        undo.Click += async (_, _) => await UndoClickAsync();
        updateMasks.Click += async (_, _) => await ApplyCorrectionAsync();
        accept.Click += async (_, _) =>
        {
            if (updating) return;
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
        foreach (CustomVideoMaskAnchor anchor in saved.Anchors)
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
            anchorModes[anchor.Frame] = anchor.Mode == "auto" ? "auto" : "prompt";
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
            CorrectedRanges = correctedRanges.OrderBy(pair => pair.Key).Select(pair =>
                new CustomVideoMaskRange
                {
                    AnchorFrame = pair.Key,
                    Start = pair.Value.Start,
                    End = pair.Value.End
                }).ToArray()
        });
    }

    int CurrentFrame => frameCount == 0 ? 0 : Math.Clamp(
        (int)Math.Round(timeline.PositionMs * fps / 1000), 0, frameCount - 1);

    async Task StartWorkerAsync()
    {
        try
        {
            Directory.CreateDirectory(temporary);
            workerStatus = File.Exists(statePath)
                ? "Loading saved SAM2 masks and correction history..."
                : "Extracting review frames and running the initial SAM2 pass...";
            workerElapsed.Restart(); workerClock.Start(); UpdateWorkerStatus();
            ProcessStartInfo start = new(configuration.PythonExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] {
                Path.Combine(AppContext.BaseDirectory, "custom-shows", "sam2_refine_worker.py"),
                "--source", sourcePath, "--runtime", CustomShowProcessor.RuntimeRoot(configuration),
                "--mask", initialMask, "--frames", FramesFolder, "--masks", maskFolder,
                "--start-ms", startMs.ToString(CultureInfo.InvariantCulture),
                "--end-ms", endMs.ToString(CultureInfo.InvariantCulture),
                "--model", sam2Model,
                "--compile-cutoff-frames", Math.Max(1,
                    configuration.Sam2CompileCutoffFrames(sam2Model))
                    .ToString(CultureInfo.InvariantCulture),
                "--cache-root", CustomShowProcessor.Sam2FrameCacheRoot,
                "--cache-limit-bytes", checked((long)Math.Clamp(
                    configuration.Sam2FrameCacheSizeGb, 0, 100) * 1024 * 1024 * 1024)
                    .ToString(CultureInfo.InvariantCulture),
                "--profile-log", this.profileLog })
                start.ArgumentList.Add(argument);
            if (File.Exists(statePath))
            {
                start.ArgumentList.Add("--resume-state");
                start.ArgumentList.Add(statePath);
            }
            start.Environment["IQP_FFMPEG"] = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            start.Environment["IQP_FFPROBE"] = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
            worker = Process.Start(start) ?? throw new InvalidOperationException("SAM2 did not start.");
            workerError = worker.StandardError.ReadToEndAsync();
            await ReadUntilAsync("ready");
            workerClock.Stop(); workerElapsed.Stop(); progress.Style = ProgressBarStyle.Blocks;
            SaveDraftState();
            timeline.Clips = [new CustomShowClip { StartMs = 0,
                EndMs = Math.Max(1, checked((long)Math.Round(frameCount * 1000 / fps))) }];
            timeline.DurationMs = timeline.Clips[0].EndMs;
            timeline.FixedMarkers = anchorModes.Keys.Select(FrameTime).ToArray();
            RefreshTimelineRanges();
            ConfigurePlaybackInterval();
            image.Enabled = previous.Enabled = play.Enabled = next.Enabled =
                slowMotion.Enabled = automatic.Enabled = accept.Enabled = true;
            progress.Value = 100;
            LoadPreview(); UpdatePosition();
        }
        catch (Exception error)
        {
            workerClock.Stop(); workerElapsed.Stop();
            if (!closing && !IsDisposed && !Disposing)
            {
                status.Text = "SAM2 mask review failed.";
                MessageBox.Show(this, error.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    async Task<JsonElement> ReadUntilAsync(string expected)
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
            if (kind == expected)
            {
                if (kind == "ready")
                {
                    frameCount = response.GetProperty("frameCount").GetInt32();
                    fps = response.GetProperty("fps").GetDouble();
                    reviewWidth = response.TryGetProperty("width", out JsonElement widthValue)
                        ? widthValue.GetInt32() : reviewWidth;
                    reviewHeight = response.TryGetProperty("height", out JsonElement heightValue)
                        ? heightValue.GetInt32() : reviewHeight;
                    if (response.TryGetProperty("framesFolder", out JsonElement frameFolder))
                        framesFolder = frameFolder.GetString() ?? framesFolder;
                    string execution = response.TryGetProperty("execution",
                        out JsonElement executionValue)
                        ? executionValue.GetString() ?? "eager"
                        : response.GetProperty("optimized").GetBoolean()
                            ? "compiled VOS" : "eager";
                    bool resumed = response.TryGetProperty("resumed",
                        out JsonElement resumedValue) && resumedValue.GetBoolean();
                    if (loadedDraftState && !resumed)
                    {
                        points.Clear(); labels.Clear(); anchorModes.Clear();
                        correctedFrames.Clear(); correctedRanges.Clear();
                        loadedDraftState = false;
                    }
                    status.Text = (resumed
                        ? "Recovered saved masks and correction history · " : "") +
                        $"{response.GetProperty("checkpoint").GetString()} · " +
                        $"{response.GetProperty("device").GetString()?.ToUpperInvariant()}/" +
                        $"{response.GetProperty("precision").GetString()} · {execution}" +
                        (response.TryGetProperty("frameCacheHit", out JsonElement cacheHit) &&
                            cacheHit.GetBoolean() ? " · cached frames" : "");
                }
                return response;
            }
            if (kind == "error")
                throw new InvalidOperationException(response.GetProperty("message").GetString());
            if (kind == "progress")
            {
                double value = response.GetProperty("percent").GetDouble();
                workerStatus = response.GetProperty("message").GetString() ?? "Updating masks...";
                bool compiling = workerStatus.StartsWith("Compiling SAM2",
                    StringComparison.Ordinal) || workerStatus.StartsWith(
                    "Loading cached SAM2", StringComparison.Ordinal);
                progress.Style = compiling ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
                if (!compiling && (value >= displayed + .5 || value >= 100))
                {
                    displayed = value;
                    progress.Value = Math.Clamp((int)Math.Round(value), 0, 100);
                }
                UpdateWorkerStatus();
                UpdateTimelineProgress(response);
            }
        }
        string detail = workerError == null ? "" : (await workerError).Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? "The SAM2 mask worker stopped unexpectedly." : detail);
    }

    void UpdateWorkerStatus()
    {
        if (string.IsNullOrEmpty(workerStatus)) return;
        TimeSpan value = workerElapsed.Elapsed;
        status.Text = $"{workerStatus}  ·  Elapsed {(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    async void Image_MouseClick(object? sender, MouseEventArgs e)
    {
        if (updating || image.Image == null || e.Button is not
            (MouseButtons.Left or MouseButtons.Right)) return;
        PointF? point = ImagePoint(e.Location);
        if (point == null) return;
        int frame = CurrentFrame;
        if (!points.TryGetValue(frame, out List<PointF>? framePoints))
            points[frame] = framePoints = [];
        if (!labels.TryGetValue(frame, out List<int>? frameLabels))
            labels[frame] = frameLabels = [];
        framePoints.Add(point.Value);
        frameLabels.Add(e.Button == MouseButtons.Right ? 0 : 1);
        await PreviewCorrectionAsync(frame, useAutomatic:
            anchorModes.GetValueOrDefault(frame) == "auto" ||
            pendingFrame == frame && pendingMode == "auto");
    }

    async Task UndoClickAsync()
    {
        int frame = CurrentFrame;
        if (updating || !points.TryGetValue(frame, out List<PointF>? framePoints) ||
            framePoints.Count == 0) return;
        framePoints.RemoveAt(framePoints.Count - 1);
        labels[frame].RemoveAt(labels[frame].Count - 1);
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
        bool reset = false, bool removeAnchor = false)
    {
        if (worker == null || worker.HasExited || updating) return;
        points.TryGetValue(frame, out List<PointF>? framePoints);
        labels.TryGetValue(frame, out List<int>? frameLabels);
        updating = true;
        playback.Stop(); play.Text = "Play";
        timeline.Enabled = false;
        image.Enabled = automatic.Enabled = undo.Enabled = false;
        previous.Enabled = play.Enabled = next.Enabled = slowMotion.Enabled = false;
        status.Text = removeAnchor ? "Previewing this frame without its correction..." :
            reset ? "Restoring this frame's tracked mask..." : useAutomatic ?
            "Generating automatic mask for this frame..." :
            "Updating this frame preview...";
        try
        {
            await worker!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                command = removeAnchor ? "remove-preview" : reset ? "reset" :
                    useAutomatic ? "auto" : "prompt", frame,
                points = (framePoints ?? []).Select(value => new[] { value.X, value.Y }),
                labels = frameLabels ?? []
            }));
            await worker.StandardInput.FlushAsync();
            JsonElement response = await ReadUntilAsync("preview");
            pendingFrame = reset ? -1 : frame;
            pendingRemoval = removeAnchor;
            if (!reset && !removeAnchor)
                pendingMode = useAutomatic ? "auto" : "prompt";
            updateMasks.Enabled = !reset;
            LoadPreview();
            status.Text = removeAnchor
                ? "Correction removed in this preview. Choose Update masks to regenerate its range."
                : reset ? "Last click undone; the tracked mask was restored." :
                useAutomatic && response.TryGetProperty("candidates", out JsonElement count)
                ? $"Automatic frame mask selected from {count.GetInt32()} candidates. Add clicks or update masks."
                : "Frame preview updated. Add more clicks, then update masks from this anchor.";
        }
        catch (Exception error)
        {
            if (!closing && !IsDisposed && !Disposing)
                MessageBox.Show(this, error.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            updating = false;
            if (!closing && !IsDisposed)
            {
                image.Enabled = automatic.Enabled = true;
                bool canMove = pendingFrame < 0;
                timeline.Enabled = canMove;
                previous.Enabled = play.Enabled = next.Enabled = slowMotion.Enabled = canMove;
                accept.Enabled = canMove;
                undo.Enabled = points.TryGetValue(CurrentFrame,
                    out List<PointF>? current) && current.Count > 0;
            }
        }
    }

    async Task ApplyCorrectionAsync()
    {
        if (pendingFrame < 0 || worker == null || worker.HasExited || updating) return;
        int frame = pendingFrame;
        activeRange = CorrectionRange(frame);
        activeAnchor = frame;
        activeBackward = activeForward = null;
        lastTimelineProgress = -1;
        RefreshTimelineRanges();
        updating = true;
        updateMasks.Enabled = image.Enabled = automatic.Enabled = accept.Enabled = false;
        progress.Value = 0;
        try
        {
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                command = pendingRemoval ? "remove" : "update", frame
            }));
            await worker.StandardInput.FlushAsync();
            await ReadUntilAsync("ready");
            if (pendingRemoval)
            {
                points.Remove(frame);
                labels.Remove(frame);
                anchorModes.Remove(frame);
                correctedFrames.Remove(frame);
                correctedRanges.Remove(frame);
            }
            else
            {
                anchorModes[frame] = pendingMode;
                correctedFrames.Add(frame);
                correctedRanges[frame] = activeRange!.Value;
            }
            activeRange = null;
            activeBackward = activeForward = null;
            timeline.FixedMarkers = anchorModes.Keys.Select(FrameTime).ToArray();
            RefreshTimelineRanges();
            pendingFrame = -1;
            pendingRemoval = false;
            progress.Value = 100;
            LoadPreview();
            SaveDraftState();
            status.Text = anchorModes.ContainsKey(frame)
                ? "Correction saved as an anchor. Scrub to another frame that needs correction."
                : "Correction removed and its surrounding masks regenerated.";
        }
        catch (Exception error)
        {
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
                timeline.Enabled = image.Enabled = previous.Enabled = play.Enabled = next.Enabled =
                    slowMotion.Enabled = automatic.Enabled = accept.Enabled = true;
        }
    }

    (int Start, int End) CorrectionRange(int frame) => (
        correctedFrames.Where(value => value < frame).DefaultIfEmpty(0).Max(),
        correctedFrames.Where(value => value > frame).DefaultIfEmpty(frameCount - 1).Min());

    long FrameTime(int frame) => checked((long)Math.Round(frame * 1000 / fps));

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
            if (CurrentFrame >= frameCount - 1) SetFrame(0);
            playback.Start(); play.Text = "Pause";
        }
    }

    void SetFrame(int frame)
    {
        if (frameCount == 0) return;
        timeline.PositionMs = checked((long)Math.Round(
            Math.Clamp(frame, 0, frameCount - 1) * 1000 / fps));
    }

    void UpdatePosition() => position.Text = frameCount == 0 ? "Preparing masks..." :
        $"Frame {CurrentFrame + 1:N0}/{frameCount:N0}  ·  " +
        TimeSpan.FromSeconds(CurrentFrame / fps).ToString(@"m\:ss\.fff");

    void LoadPreview()
    {
        if (frameCount == 0) return;
        int frame = CurrentFrame;
        string framePath = Path.Combine(FramesFolder, $"{frame + 1:00000000}.jpg"),
            maskPath = Path.Combine(maskFolder, $"{frame + 1:00000000}.png");
        if (!File.Exists(framePath) || !File.Exists(maskPath)) return;
        using Bitmap source = LoadBitmap(framePath);
        using Bitmap mask = LoadBitmap(maskPath);
        Bitmap result = new(source.Width, source.Height);
        using (Graphics graphics = Graphics.FromImage(result))
        {
            graphics.DrawImageUnscaled(source, 0, 0);
            using Bitmap overlay = new(mask);
            overlay.MakeTransparent(Color.Black);
            using System.Drawing.Imaging.ImageAttributes attributes = new();
            attributes.SetRemapTable([new System.Drawing.Imaging.ColorMap
            {
                OldColor = Color.White, NewColor = Color.FromArgb(105, Color.Lime)
            }]);
            graphics.DrawImage(overlay, new Rectangle(0, 0, result.Width, result.Height),
                0, 0, overlay.Width, overlay.Height, GraphicsUnit.Pixel, attributes);
            if (points.TryGetValue(frame, out List<PointF>? framePoints))
                for (int index = 0; index < framePoints.Count; index++)
                {
                    PointF point = framePoints[index];
                    using Pen pen = new(labels[frame][index] == 1 ? Color.Lime : Color.Red, 4);
                    graphics.DrawEllipse(pen, point.X - 7, point.Y - 7, 14, 14);
                }
        }
        Image? old = image.Image; image.Image = result; old?.Dispose();
        undo.Enabled = !updating && points.TryGetValue(frame,
            out List<PointF>? currentPoints) && currentPoints.Count > 0;
    }

    PointF? ImagePoint(Point location)
    {
        Image source = image.Image!;
        float scale = Math.Min(image.ClientSize.Width / (float)source.Width,
            image.ClientSize.Height / (float)source.Height);
        float left = (image.ClientSize.Width - source.Width * scale) / 2,
            top = (image.ClientSize.Height - source.Height * scale) / 2;
        float x = (location.X - left) / scale, y = (location.Y - top) / scale;
        return x < 0 || y < 0 || x >= source.Width || y >= source.Height
            ? null : new PointF(x, y);
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
            StopWorker(); image.Image?.Dispose();
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
