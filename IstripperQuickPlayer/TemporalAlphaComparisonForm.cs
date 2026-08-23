using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class TemporalAlphaComparisonForm : Form
{
    sealed record ClipChoice(CustomShowClip Clip, int Number)
    {
        public override string ToString() => $"Clip {Number}: " +
            $"{FormatTime(Clip.StartMs / 1000d)}–{FormatTime(Clip.EndMs / 1000d)}";
    }

    readonly CustomShowConfiguration configuration;
    readonly CustomShowStore store;
    readonly string temporary = Path.Combine(Path.GetTempPath(),
        "iqp-temporal-alpha-" + Guid.NewGuid().ToString("N"));
    readonly TextBox foregroundPath = new() { Dock = DockStyle.Fill };
    readonly TextBox alphaPath = new() { Dock = DockStyle.Fill };
    readonly TextBox outputPath = new() { Dock = DockStyle.Fill, ReadOnly = true };
    readonly TextBox selectedShow = new() { Dock = DockStyle.Fill, ReadOnly = true };
    readonly ComboBox selectedClip = new()
        { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly NumericUpDown windowFrames = new()
        { Minimum = 1, Maximum = 12, Value = 3, Width = 58 };
    readonly TrackBar strength = new()
        { Minimum = 25, Maximum = 100, Value = 100, TickFrequency = 5,
          SmallChange = 1, LargeChange = 5, Width = 240, Height = 32 };
    readonly Label strengthValue = new()
        { Text = "100%", AutoSize = true, Padding = new Padding(0, 7, 0, 0) };
    readonly NumericUpDown alphaThreshold = new()
        { Minimum = 0, Maximum = 255, Value = 120, Width = 62 };
    readonly Button run = new() { Text = "Run stabilization", AutoSize = true };
    readonly Button stop = new() { Text = "Stop", AutoSize = true, Enabled = false };
    readonly ProgressBar progress = new() { Width = 220, Height = 24 };
    readonly Label status = new() { Text = "Choose an existing foreground and alpha pair.",
        AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    readonly PictureBox inputPreview = PreviewBox();
    readonly PictureBox outputPreview = PreviewBox();
    readonly Controls.PlaybackSeekBar timeline = new()
    {
        Dock = DockStyle.Fill, Minimum = 0, Maximum = 1, Enabled = false,
        ShowTimeToolTip = true, AccessibleName = "Alpha comparison timeline"
    };
    readonly Label position = new()
        { Text = "0:00 / 0:00", AutoSize = true, Padding = new Padding(8, 7, 0, 0) };
    readonly Label framePosition = new()
        { Text = "Frame 0 / 0", AutoSize = true, Padding = new Padding(12, 7, 0, 0) };
    readonly Button previous = new() { Text = "◀ 1 frame", AutoSize = true,
        Enabled = false };
    readonly Button play = new() { Text = "Play", AutoSize = true, Enabled = false };
    readonly Button next = new() { Text = "1 frame ▶", AutoSize = true,
        Enabled = false };
    readonly ComboBox speed = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 78, Enabled = false };
    readonly CheckBox decisionOverlay = new()
        { Text = "Show red/green overlay", AutoSize = true, Checked = true,
          Enabled = false };
    readonly System.Windows.Forms.Timer cachePoll = new() { Interval = 150 };
    readonly System.Windows.Forms.Timer settingsDelay = new() { Interval = 700 };
    CancellationTokenSource? processingCancellation, previewCancellation;
    Task? processingTask, previewTask;
    volatile bool processingActive;
    bool processingComplete, closing, previewPlaying, loadingClip;
    int previewWidth, previewHeight, totalFrames, availableFrames, currentFrame = -1,
        previewRevision;
    string frameRate = "25/1";
    string activeForeground = "", activeAlpha = "";
    CustomShowManifest? customShow;
    double fps = 25, playbackSpeed = 1;

    string PreviewFolder => Path.Combine(temporary, "preview");
    string PreviewAlpha => Path.Combine(PreviewFolder, "output-alpha.gray8");
    string PreviewDecisions => Path.Combine(PreviewFolder,
        "decision-flags.bitplanes");
    string PreviewMetadata => Path.Combine(PreviewFolder, "preview.json");

    internal TemporalAlphaComparisonForm(CustomShowConfiguration configuration,
        string? showId = null, string? clipId = null)
    {
        this.configuration = configuration;
        store = new(configuration.LibraryRoot);
        windowFrames.Value = Math.Clamp(
            configuration.LastTemporalAlphaCleanupWindowFrames, 1, 12);
        strength.Value = Math.Clamp(
            configuration.LastTemporalAlphaCleanupStrengthPercent, 25, 100);
        alphaThreshold.Value = Math.Clamp(
            configuration.LastTemporalAlphaCleanupAlphaThreshold, 0, 255);
        strengthValue.Text = $"{strength.Value}%";
        speed.Items.AddRange(["0.25×", "0.5×", "1×", "2×"]);
        speed.SelectedIndex = 2;
        speed.SelectedIndexChanged += (_, _) => Volatile.Write(ref playbackSpeed,
            speed.SelectedIndex switch { 0 => .25, 1 => .5, 3 => 2, _ => 1 });
        timeline.ToolTipFormatter = value => FormatTime(value / fps);
        Text = "Compare Automatic Alpha Stabilization";
        ClientSize = new Size(1320, 900);
        MinimumSize = new Size(900, 650);
        StartPosition = FormStartPosition.CenterParent;

        TableLayoutPanel root = new() { Dock = DockStyle.Fill, Padding = new Padding(10),
            ColumnCount = 1, RowCount = 6 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        TableLayoutPanel paths = new() { Dock = DockStyle.Fill, AutoSize = true,
            ColumnCount = 3, RowCount = 5 };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        AddFieldLabel(paths, 0, "Custom show");
        paths.Controls.Add(selectedShow, 1, 0);
        Button selectShow = new() { Text = "Select...", Dock = DockStyle.Fill };
        selectShow.Click += SelectCustomShow;
        paths.Controls.Add(selectShow, 2, 0);
        AddFieldLabel(paths, 1, "Clip");
        paths.Controls.Add(selectedClip, 1, 1);
        paths.SetColumnSpan(selectedClip, 2);
        AddPath(paths, 2, "Foreground video", foregroundPath, BrowseForeground);
        AddPath(paths, 3, "Existing alpha", alphaPath, BrowseAlpha);
        AddFieldLabel(paths, 4, "Stabilized alpha copy");
        paths.Controls.Add(outputPath, 1, 4);
        paths.Controls.Add(new Label { Text = "Automatic", AutoSize = true,
            Padding = new Padding(4, 6, 0, 0) }, 2, 4);
        root.Controls.Add(paths, 0, 0);

        FlowLayoutPanel options = new() { Dock = DockStyle.Fill, AutoSize = true,
            WrapContents = true, Padding = new Padding(0, 5, 0, 5) };
        options.Controls.AddRange([new Label { Text = "Maximum flicker", AutoSize = true,
                Padding = new Padding(0, 7, 0, 0) }, windowFrames,
            new Label { Text = "frames", AutoSize = true,
                Padding = new Padding(0, 7, 12, 0) },
            new Label { Text = "Strength", AutoSize = true,
                Padding = new Padding(0, 7, 0, 0) }, strength, strengthValue,
            new Label { Text = "Alpha threshold", AutoSize = true,
                Padding = new Padding(12, 7, 0, 0) }, alphaThreshold,
            run, stop, progress, status]);
        root.Controls.Add(options, 0, 1);

        TableLayoutPanel previews = new() { Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 2 };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previews.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previews.Controls.Add(PreviewLabel("Input alpha"), 0, 0);
        previews.Controls.Add(PreviewLabel("Stabilized output"), 1, 0);
        previews.Controls.Add(inputPreview, 0, 1);
        previews.Controls.Add(outputPreview, 1, 1);
        root.Controls.Add(previews, 0, 2);

        TableLayoutPanel timelineRow = new() { Dock = DockStyle.Fill,
            AutoSize = true, ColumnCount = 3 };
        timelineRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        timelineRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timelineRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timelineRow.Controls.Add(timeline, 0, 0);
        timelineRow.Controls.Add(position, 1, 0);
        timelineRow.Controls.Add(framePosition, 2, 0);
        root.Controls.Add(timelineRow, 0, 3);
        FlowLayoutPanel transport = new() { Dock = DockStyle.Fill, AutoSize = true,
            WrapContents = true };
        transport.Controls.AddRange([previous, play, next,
            new Label { Text = "Speed", AutoSize = true,
                Padding = new Padding(12, 7, 0, 0) }, speed,
            decisionOverlay,
            new Label { Text = "Red: removed   Green: restored", AutoSize = true,
                Padding = new Padding(4, 7, 0, 0) }]);
        root.Controls.Add(transport, 0, 4);
        Button close = new() { Text = "Close", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        root.Controls.Add(new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            Controls = { close } }, 0, 5);
        Controls.Add(root);
        CancelButton = close;

        run.Click += async (_, _) => await StartComparisonAsync();
        stop.Click += async (_, _) => await StopProcessingAsync();
        windowFrames.ValueChanged += SettingsChanged;
        strength.ValueChanged += SettingsChanged;
        alphaThreshold.ValueChanged += ThresholdChanged;
        selectedClip.SelectedIndexChanged += (_, _) => ApplySelectedClip();
        timeline.Scroll += (_, _) => StartPreview(timeline.Value, false);
        previous.Click += (_, _) => StartPreview(Math.Max(0, currentFrame - 1), false);
        next.Click += (_, _) => StartPreview(Math.Min(availableFrames - 1,
            currentFrame + 1), false);
        play.Click += (_, _) =>
        {
            if (previewPlaying) StopPreview();
            else StartPreview(currentFrame < 0 ? 0 : currentFrame, true);
        };
        decisionOverlay.CheckedChanged += (_, _) =>
        {
            if (currentFrame >= 0 && availableFrames > 0)
                StartPreview(currentFrame, previewPlaying);
        };
        cachePoll.Tick += (_, _) => PollPreviewCache();
        settingsDelay.Tick += async (_, _) =>
        {
            settingsDelay.Stop();
            if (processingActive || processingComplete)
                await StartComparisonAsync();
        };
        FormClosing += (_, _) =>
        {
            closing = true;
            settingsDelay.Stop(); cachePoll.Stop();
            processingCancellation?.Cancel(); previewCancellation?.Cancel();
        };
        AppTheme.Apply(this);
        if (showId != null) LoadCustomShow(showId, clipId);
    }

    static PictureBox PreviewBox() => new()
    {
        Dock = DockStyle.Fill, BackColor = Color.Black,
        SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle
    };

    static Label PreviewLabel(string text) => new()
        { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont,
            FontStyle.Bold), Padding = new Padding(4) };

    static void AddPath(TableLayoutPanel table, int row, string label, TextBox box,
        EventHandler browse)
    {
        AddFieldLabel(table, row, label);
        table.Controls.Add(box, 1, row);
        Button button = new() { Text = "Browse...", Dock = DockStyle.Fill };
        button.Click += browse;
        table.Controls.Add(button, 2, row);
    }

    static void AddFieldLabel(TableLayoutPanel table, int row, string text) =>
        table.Controls.Add(new Label { Text = text, AutoSize = true,
            Padding = new Padding(0, 6, 8, 0) }, 0, row);

    void SelectCustomShow(object? sender, EventArgs e)
    {
        using CustomShowPickerForm picker = new(store,
            "Select Custom Show", "Select show");
        if (picker.ShowDialog(this) != DialogResult.OK || picker.ShowId == null)
            return;
        try { LoadCustomShow(picker.ShowId, null); }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    void LoadCustomShow(string showId, string? clipId)
    {
        CustomShowManifest show = store.LoadManifest(showId);
        ClipChoice[] clips = show.Clips.Where(clip => clip.Included &&
                clip.Media != null).Select((clip, index) =>
                    new ClipChoice(clip, index + 1)).ToArray();
        if (clips.Length == 0)
            throw new InvalidDataException("The selected custom show has no playable clips.");
        customShow = show;
        selectedShow.Text = show.Title;
        selectedClip.BeginUpdate();
        selectedClip.Items.Clear();
        selectedClip.Items.AddRange(clips);
        selectedClip.EndUpdate();
        int index = clipId == null ? 0 : Array.FindIndex(clips, choice =>
            string.Equals(choice.Clip.Id, clipId, StringComparison.OrdinalIgnoreCase));
        selectedClip.SelectedIndex = index >= 0 ? index : 0;
    }

    void ApplySelectedClip()
    {
        if (customShow == null || selectedClip.SelectedItem is not ClipChoice choice ||
            choice.Clip.Media is not CustomClipMedia media) return;
        loadingClip = true;
        try
        {
            string folder = Path.Combine(store.ShowsFolder, customShow.Id);
            foregroundPath.Text = CustomShowStore.ResolveRelative(
                folder, media.Foreground);
            alphaPath.Text = CustomShowStore.ResolveRelative(folder, media.Alpha);
            outputPath.Text = CustomShowStore.CleanedAlphaPath(alphaPath.Text);
            alphaThreshold.Value = Math.Clamp(choice.Clip.AlphaThreshold, 0, 255);
        }
        finally { loadingClip = false; }
        SettingsChanged(this, EventArgs.Empty);
    }

    void BrowseForeground(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new() { Filter =
            "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm|All files|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            foregroundPath.Text = dialog.FileName;
    }

    void BrowseAlpha(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new() { Filter =
            "Alpha video|*.mkv;*.mp4;*.mov;*.avi;*.webm|All files|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        alphaPath.Text = dialog.FileName;
        string folder = Path.GetDirectoryName(dialog.FileName)!;
        string sibling = Path.Combine(folder, "foreground.mp4");
        if (File.Exists(sibling)) foregroundPath.Text = sibling;
        outputPath.Text = CustomShowStore.CleanedAlphaPath(dialog.FileName);
    }

    void SettingsChanged(object? sender, EventArgs e)
    {
        strengthValue.Text = $"{strength.Value}%";
        if (!processingActive && !processingComplete) return;
        status.Text = "Settings changed; restarting stabilization shortly...";
        settingsDelay.Stop(); settingsDelay.Start();
    }

    void ThresholdChanged(object? sender, EventArgs e)
    {
        if (loadingClip) return;
        SettingsChanged(sender, e);
        if (currentFrame >= 0 && availableFrames > 0)
            StartPreview(currentFrame, previewPlaying);
    }

    async Task StartComparisonAsync()
    {
        settingsDelay.Stop();
        try
        {
            string foreground = Path.GetFullPath(foregroundPath.Text);
            string alpha = Path.GetFullPath(alphaPath.Text);
            string output = Path.GetFullPath(outputPath.Text);
            if (!File.Exists(foreground))
                throw new FileNotFoundException("Choose the matching foreground video.", foreground);
            if (!File.Exists(alpha))
                throw new FileNotFoundException("Choose an existing alpha video.", alpha);
            if (string.Equals(alpha, output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The stabilized output must not overwrite the input alpha.");
            await StopProcessingAsync();
            StopPreview();
            try { if (Directory.Exists(PreviewFolder)) Directory.Delete(PreviewFolder, true); }
            catch { }
            Directory.CreateDirectory(PreviewFolder);
            configuration.LastTemporalAlphaCleanupWindowFrames = (int)windowFrames.Value;
            configuration.LastTemporalAlphaCleanupStrengthPercent = strength.Value;
            configuration.LastTemporalAlphaCleanupAlphaThreshold =
                (int)alphaThreshold.Value;
            configuration.Save();
            processingCancellation = new CancellationTokenSource();
            processingActive = true; processingComplete = false;
            activeForeground = foreground; activeAlpha = alpha;
            currentFrame = -1; availableFrames = totalFrames = 0;
            position.Text = "0:00 / 0:00"; framePosition.Text = "Frame 0 / 0";
            previewWidth = previewHeight = 0; frameRate = "25/1"; fps = 25;
            progress.Value = 0; run.Text = "Restart stabilization"; stop.Enabled = true;
            SetTransportEnabled(false);
            status.Text = "Starting automatic alpha stabilization...";
            cachePoll.Start();
            Progress<CustomShowProgress> updates = new(value =>
            {
                progress.Value = Math.Clamp((int)Math.Round(value.Percent), 0, 100);
                status.Text = value.Message;
            });
            CancellationTokenSource cancellation = processingCancellation;
            Task task = CustomShowProcessor.RunTemporalAlphaCleanupAsync(
                configuration, temporary, (int)windowFrames.Value, strength.Value,
                (int)alphaThreshold.Value,
                Path.Combine(temporary, "processing.log"), updates, cancellation.Token,
                foreground, alpha, output, PreviewFolder);
            processingTask = task;
            _ = ObserveProcessingAsync(task, cancellation, output);
        }
        catch (Exception error)
        {
            processingActive = false; stop.Enabled = false;
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    async Task ObserveProcessingAsync(Task task, CancellationTokenSource cancellation,
        string output)
    {
        try
        {
            await task;
            if (!ReferenceEquals(processingTask, task)) return;
            processingComplete = true;
            status.Text = "Stabilization complete. Alpha saved to " + output;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(processingTask, task)) status.Text = "Stabilization stopped.";
        }
        catch (Exception error)
        {
            if (!ReferenceEquals(processingTask, task) || closing) return;
            status.Text = "Stabilization failed.";
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            if (ReferenceEquals(processingTask, task))
            {
                processingTask = null; processingActive = false; stop.Enabled = false;
                if (ReferenceEquals(processingCancellation, cancellation))
                    processingCancellation = null;
                cancellation.Dispose();
                PollPreviewCache();
            }
        }
    }

    async Task StopProcessingAsync()
    {
        Task? task = processingTask;
        CancellationTokenSource? cancellation = processingCancellation;
        cancellation?.Cancel();
        if (task != null)
            try { await task; } catch { }
        if (ReferenceEquals(processingTask, task))
        {
            processingTask = null; processingActive = false; stop.Enabled = false;
        }
        if (ReferenceEquals(processingCancellation, cancellation))
            processingCancellation = null;
        cancellation?.Dispose();
    }

    void PollPreviewCache()
    {
        try
        {
            if (previewWidth == 0 && File.Exists(PreviewMetadata))
            {
                using JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(PreviewMetadata));
                JsonElement root = document.RootElement;
                previewWidth = root.GetProperty("width").GetInt32();
                previewHeight = root.GetProperty("height").GetInt32();
                frameRate = root.GetProperty("frameRate").GetString() ?? "25/1";
                string[] parts = frameRate.Split('/');
                fps = double.Parse(parts[0], CultureInfo.InvariantCulture) /
                    double.Parse(parts[1], CultureInfo.InvariantCulture);
                totalFrames = root.GetProperty("totalFrames").GetInt32();
                timeline.Maximum = Math.Max(1, totalFrames - 1);
                timeline.ToolTipFormatter = value => FormatTime(value / fps);
            }
            if (previewWidth == 0 || !File.Exists(PreviewAlpha) ||
                !File.Exists(PreviewDecisions)) return;
            int pixels = previewWidth * previewHeight;
            int decisionFrameBytes = 2 * ((pixels + 7) / 8);
            long alphaFrames = new FileInfo(PreviewAlpha).Length / pixels;
            long decisionFrames = new FileInfo(PreviewDecisions).Length /
                decisionFrameBytes;
            availableFrames = (int)Math.Min(totalFrames,
                Math.Min(alphaFrames, decisionFrames));
            SetTransportEnabled(availableFrames > 0);
            if (currentFrame < 0 && availableFrames > 0 && previewTask == null)
                StartPreview(0, true);
        }
        catch (IOException) { }
        catch (JsonException) { }
    }

    void SetTransportEnabled(bool enabled)
    {
        timeline.Enabled = previous.Enabled = play.Enabled = next.Enabled =
            speed.Enabled = decisionOverlay.Enabled = enabled;
    }

    void StartPreview(int frame, bool playing)
    {
        if (availableFrames <= 0 || previewWidth <= 0) return;
        StopPreview();
        frame = Math.Clamp(frame, 0, availableFrames - 1);
        previewCancellation = new CancellationTokenSource();
        previewPlaying = playing; play.Text = playing ? "Pause" : "Play";
        int revision = ++previewRevision;
        string foreground = activeForeground, alpha = activeAlpha;
        int width = previewWidth, height = previewHeight, frames = totalFrames;
        int lowerAlphaThreshold = (int)alphaThreshold.Value;
        int fullOpacityThreshold = Math.Clamp(
            configuration.FullOpacityThreshold, 1, 255);
        bool showDecisions = decisionOverlay.Checked;
        double previewFps = fps;
        string previewFrameRate = frameRate;
        CancellationToken token = previewCancellation.Token;
        previewTask = Task.Run(() => PreviewLoopAsync(frame, playing, foreground,
            alpha, width, height, frames, previewFps, previewFrameRate,
            lowerAlphaThreshold, fullOpacityThreshold, showDecisions,
            revision, token));
    }

    void StopPreview()
    {
        previewRevision++;
        previewCancellation?.Cancel(); previewCancellation?.Dispose();
        previewCancellation = null; previewPlaying = false; play.Text = "Play";
        previewTask = null;
    }

    async Task PreviewLoopAsync(int startFrame, bool playing, string foreground,
        string alpha, int width, int height, int frames, double previewFps,
        string previewFrameRate, int lowerAlphaThreshold,
        int fullOpacityThreshold, bool showDecisions, int revision,
        CancellationToken token)
    {
        Process? source = null, input = null;
        try
        {
            double seconds = startFrame / previewFps;
            source = PreviewDecoder(foreground, seconds, "bgra", width, height,
                previewFrameRate);
            input = PreviewDecoder(alpha, seconds, "gray", width, height,
                previewFrameRate);
            int pixels = width * height;
            int decisionPlaneBytes = (pixels + 7) / 8;
            int decisionFrameBytes = decisionPlaneBytes * 2;
            await using FileStream output = new(PreviewAlpha, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            await using FileStream decisions = new(PreviewDecisions, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            output.Seek((long)startFrame * pixels, SeekOrigin.Begin);
            decisions.Seek((long)startFrame * decisionFrameBytes, SeekOrigin.Begin);
            int frame = startFrame;
            Stopwatch clock = Stopwatch.StartNew();
            while (!token.IsCancellationRequested && frame < frames)
            {
                while (new FileInfo(PreviewAlpha).Length < (long)(frame + 1) * pixels ||
                    new FileInfo(PreviewDecisions).Length <
                        (long)(frame + 1) * decisionFrameBytes)
                {
                    if (!processingActive) return;
                    await Task.Delay(40, token);
                }
                byte[] outputAlpha = new byte[pixels];
                byte[] sourceBgra = new byte[pixels * 4];
                byte[] inputAlpha = new byte[pixels];
                byte[] decisionFlags = new byte[decisionFrameBytes];
                if (!await ReadExactAsync(output, outputAlpha, token) ||
                    !await ReadExactAsync(decisions, decisionFlags, token) ||
                    !await ReadExactAsync(source.StandardOutput.BaseStream, sourceBgra, token) ||
                    !await ReadExactAsync(input.StandardOutput.BaseStream, inputAlpha, token))
                    return;
                byte[] inputComposite = Composite(sourceBgra, inputAlpha,
                    width, height, lowerAlphaThreshold, fullOpacityThreshold);
                if (showDecisions)
                    ApplyDecisionOverlay(inputComposite, decisionFlags, pixels,
                        decisionPlaneBytes);
                byte[] outputComposite = Composite(sourceBgra, outputAlpha,
                    width, height, lowerAlphaThreshold, fullOpacityThreshold);
                int shown = frame;
                await ShowFrameAsync(inputComposite, outputComposite, shown, width,
                    height, frames, previewFps, revision, token);
                if (!playing) return;
                frame++;
                double speedMultiplier = Volatile.Read(ref playbackSpeed);
                int delay = (int)Math.Round(1000 / previewFps / speedMultiplier -
                    clock.Elapsed.TotalMilliseconds);
                clock.Restart();
                if (delay > 0) await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (closing || IsDisposed) { }
        catch (Exception error)
        {
            if (!closing && revision == previewRevision && IsHandleCreated)
                BeginInvoke(() => status.Text = "Preview stopped: " + error.Message);
        }
        finally
        {
            foreach (Process? process in new[] { source, input })
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    process.Dispose();
                }
            if (!closing && revision == previewRevision && IsHandleCreated)
                BeginInvoke(() =>
                {
                    if (revision != previewRevision) return;
                    previewPlaying = false; play.Text = "Play"; previewTask = null;
                });
        }
    }

    Process PreviewDecoder(string path, double seconds, string pixelFormat,
        int width, int height, string previewFrameRate)
    {
        ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = false
        };
        foreach (string argument in new[] { "-v", "error", "-ss",
            seconds.ToString("0.######", CultureInfo.InvariantCulture), "-i", path,
            "-vf", $"fps={previewFrameRate},scale={width}:{height}:flags=area," +
                $"format={pixelFormat}", "-f", "rawvideo", "-pix_fmt", pixelFormat,
            "pipe:1" }) start.ArgumentList.Add(argument);
        return Process.Start(start) ??
            throw new InvalidOperationException("FFmpeg preview decoding could not start.");
    }

    async Task ShowFrameAsync(byte[] input, byte[] output, int frame, int width,
        int height, int frames, double previewFps, int revision,
        CancellationToken token)
    {
        TaskCompletionSource<bool> shown = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!IsHandleCreated || closing) return;
        BeginInvoke(() =>
        {
            if (token.IsCancellationRequested || closing || revision != previewRevision)
            { shown.TrySetResult(true); return; }
            Bitmap inputBitmap = BitmapFromBgra(input, width, height);
            Bitmap outputBitmap = BitmapFromBgra(output, width, height);
            Image? oldInput = inputPreview.Image, oldOutput = outputPreview.Image;
            inputPreview.Image = inputBitmap; outputPreview.Image = outputBitmap;
            oldInput?.Dispose(); oldOutput?.Dispose();
            currentFrame = frame; timeline.Value = frame;
            position.Text = $"{FormatTime(frame / previewFps)} / " +
                FormatTime(frames / previewFps);
            framePosition.Text = $"Frame {frame + 1:N0} / {frames:N0}";
            shown.TrySetResult(true);
        });
        await shown.Task.WaitAsync(token);
    }

    static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer,
        CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), token);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    static byte[] Composite(byte[] source, byte[] alpha, int width, int height,
        int lowerAlphaThreshold, int fullOpacityThreshold)
    {
        byte[] result = new byte[source.Length];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            int offset = pixel * 4, opacity = alpha[pixel];
            opacity = PreviewOpacity((byte)opacity, lowerAlphaThreshold,
                fullOpacityThreshold);
            int checker = (((pixel % width) / 24 + (pixel / width) / 24) & 1) == 0
                ? 125 : 190;
            result[offset] = (byte)((source[offset] * opacity + checker * (255 - opacity)) / 255);
            result[offset + 1] = (byte)((source[offset + 1] * opacity + checker * (255 - opacity)) / 255);
            result[offset + 2] = (byte)((source[offset + 2] * opacity + checker * (255 - opacity)) / 255);
            result[offset + 3] = 255;
        }
        return result;
    }

    internal static int PreviewOpacity(byte alpha, int lowerThreshold,
        int fullOpacityThreshold) => alpha < Math.Clamp(lowerThreshold, 0, 255)
            ? 0 : alpha >= Math.Clamp(fullOpacityThreshold, 1, 255) ? 255 : alpha;

    internal static bool VerifyThresholdPreview()
    {
        byte[] removed = [100, 100, 100, 255];
        byte[] restored = [100, 100, 100, 255];
        ApplyDecisionOverlay(removed, [1, 0], 1, 1);
        ApplyDecisionOverlay(restored, [0, 1], 1, 1);
        return PreviewOpacity(0, 0, 200) == 0 &&
            PreviewOpacity(119, 120, 200) == 0 &&
            PreviewOpacity(120, 120, 200) == 120 &&
            PreviewOpacity(199, 120, 200) == 199 &&
            PreviewOpacity(200, 120, 200) == 255 &&
            PreviewOpacity(210, 220, 200) == 0 &&
            removed[2] > removed[1] && removed[2] > removed[0] &&
            restored[1] > restored[0] && restored[1] > restored[2];
    }

    static void ApplyDecisionOverlay(byte[] composite, byte[] flags, int pixels,
        int planeBytes)
    {
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            int mask = 1 << (pixel & 7), index = pixel >> 3;
            bool removed = (flags[index] & mask) != 0;
            bool restored = (flags[planeBytes + index] & mask) != 0;
            if (!removed && !restored) continue;
            int offset = pixel * 4;
            if (removed)
            {
                composite[offset] = (byte)(composite[offset] * 35 / 100);
                composite[offset + 1] = (byte)(composite[offset + 1] * 35 / 100);
                composite[offset + 2] = (byte)((composite[offset + 2] * 35 +
                    255 * 65) / 100);
            }
            else
            {
                composite[offset] = (byte)(composite[offset] * 35 / 100);
                composite[offset + 1] = (byte)((composite[offset + 1] * 35 +
                    255 * 65) / 100);
                composite[offset + 2] = (byte)(composite[offset + 2] * 35 / 100);
            }
        }
    }

    static Bitmap BitmapFromBgra(byte[] bytes, int width, int height)
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

    static string FormatTime(double seconds) => TimeSpan.FromSeconds(
        Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss",
            CultureInfo.InvariantCulture);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true; cachePoll.Dispose(); settingsDelay.Dispose();
            processingCancellation?.Cancel(); previewCancellation?.Cancel();
            inputPreview.Image?.Dispose(); outputPreview.Image?.Dispose();
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
            catch { }
        }
        base.Dispose(disposing);
    }
}
