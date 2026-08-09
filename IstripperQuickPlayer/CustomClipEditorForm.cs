using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class CustomClipEditorForm : Form
{
    static readonly string[] HotnessValues =
        ["Public", "NoNudity", "Topless", "Nudity", "FullNudity", "XXX"];
    static readonly string[] ClipTypeValues =
        ["Standing", "Table", "Behind Table", "Swing", "Cage", "Pole",
         "Glass", "Sign", "Prop", "Full Legs", "Side"];
    readonly string videoPath;
    readonly CustomShowConfiguration? configuration;
    readonly long durationMs;
    readonly double frameDurationMs;
    readonly List<CustomShowClip> clips;
    readonly bool usesPerClipMedia;
    readonly PictureBox preview = new() { Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    readonly ClipTimelineControl timeline = new() { Dock = DockStyle.Fill, Height = 60 };
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    readonly ComboBox hotness = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckedListBox clipTypes = new() { CheckOnClick = true, Height = 120 };
    readonly CheckBox include = new() { Text = "Include this segment as a playable clip",
        AutoSize = true, Checked = true, ThreeState = true, AutoCheck = false };
    readonly Label position = new() { AutoSize = true, Padding = new Padding(6, 8, 6, 0) };
    readonly Button previousFrame = new() { Text = "◀ 1 frame", AutoSize = true };
    readonly Button play = new() { Text = "Play", AutoSize = true };
    readonly Button nextFrame = new() { Text = "1 frame ▶", AutoSize = true };
    readonly CheckBox slowMotion = new() { Text = "Slow motion (0.25×)", AutoSize = true,
        Padding = new Padding(8, 6, 0, 0) };
    readonly Button addDivider = new() { Text = "Add divider here", AutoSize = true };
    readonly Button removeDivider = new() { Text = "Remove divider before clip", AutoSize = true };
    readonly Button autoDetect = new() { Text = "Auto-detect clips", AutoSize = true };
    readonly ComboBox detector = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 190 };
    readonly CheckBox skipTransitions = new() { Text = "Skip transition ±",
        AutoSize = true };
    readonly NumericUpDown transitionSeconds = new() { DecimalPlaces = 2,
        Minimum = .05m, Maximum = 30, Increment = .1m, Value = .5m, Width = 62,
        Enabled = false };
    readonly Label transitionSecondsLabel = new() { Text = "seconds", AutoSize = true,
        Margin = new Padding(0, 7, 3, 3) };
    readonly System.Windows.Forms.Timer playback = new() { Interval = 100 };
    readonly System.Windows.Forms.Timer previewDelay = new() { Interval = 140 };
    CancellationTokenSource? previewCancellation;
    CancellationTokenSource? streamCancellation;
    CancellationTokenSource? detectionCancellation;
    Stopwatch playClock = new();
    long playStartMs;
    long playEndMs;
    bool loadingMetadata;
    bool resumeAfterScrub;

    internal CustomShowClip[] Clips => clips.Select(Clone).ToArray();

    internal CustomClipEditorForm(string videoPath,
        IReadOnlyList<CustomShowClip> existing, string defaultHotness,
        string[] defaultTypes, CustomShowConfiguration? configuration = null,
        bool allowBoundaryEditing = true)
    {
        this.videoPath = videoPath;
        this.configuration = configuration;
        using (FfmpegCpuDecoder decoder = new(videoPath, fastDecode: true))
        {
            durationMs = !allowBoundaryEditing && existing.Count > 0
                ? existing[^1].EndMs
                : checked((long)Math.Round(decoder.Duration * 1000));
            frameDurationMs = decoder.FrameDuration * 1000;
        }
        playEndMs = durationMs;
        clips = existing.Count == 0
            ? [new CustomShowClip { StartMs = 0, EndMs = durationMs,
                Hotness = defaultHotness, ClipTypes = [.. defaultTypes] }]
            : existing.Select(Clone).ToList();
        usesPerClipMedia = !allowBoundaryEditing && existing.Any(clip => clip.Media != null);
        clips[^1].EndMs = durationMs;
        CustomShowStore.ValidateClips([.. clips], durationMs);

        Text = allowBoundaryEditing ? "Split Custom Show into Clips" :
            "Edit Custom Show Clip Metadata";
        ClientSize = new Size(1220, 1000);
        MinimumSize = new Size(760, 620);
        StartPosition = FormStartPosition.CenterParent;
        preview.AccessibleName = "Video preview";
        timeline.AccessibleName = "Clip timeline and dividers";

        TableLayoutPanel root = new() { Dock = DockStyle.Fill, RowCount = 5,
            ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(preview, 0, 0);
        root.Controls.Add(timeline, 0, 1);
        FlowLayoutPanel precision = new() { Dock = DockStyle.Fill, AutoSize = true,
            WrapContents = false };
        precision.Controls.AddRange([previousFrame, play, position, nextFrame, slowMotion]);
        root.Controls.Add(precision, 0, 2);

        TableLayoutPanel details = new() { Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1 };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        details.Controls.Add(grid, 0, 0);
        TableLayoutPanel metadata = new() { Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
        metadata.Controls.Add(include);
        metadata.Controls.Add(new Label { Text = "Selected clip hotness", AutoSize = true });
        metadata.Controls.Add(hotness);
        metadata.Controls.Add(new Label { Text = "Selected clip types", AutoSize = true });
        metadata.Controls.Add(clipTypes);
        details.Controls.Add(metadata, 1, 0);
        root.Controls.Add(details, 0, 3);

        FlowLayoutPanel actions = new() { Dock = DockStyle.Fill, AutoSize = true,
            WrapContents = false };
        Button ok = new() { Text = "OK", AutoSize = true };
        Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        detector.Items.Add("Fast (FFmpeg)");
        if (configuration != null &&
            CustomShowProcessor.IsTransNetV2Installed(configuration))
            detector.Items.Add("Accurate (TransNetV2)");
        detector.SelectedIndex = detector.Items.Count - 1;
        actions.Controls.AddRange([addDivider, removeDivider,
            detector, skipTransitions, transitionSeconds,
            transitionSecondsLabel, autoDetect, ok, cancel]);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        grid.Columns.Add("number", "Segment");
        grid.Columns.Add("included", "Use");
        grid.Columns.Add("start", "Start");
        grid.Columns.Add("end", "End");
        grid.Columns.Add("length", "Length");
        grid.Columns.Add("heat", "Hotness");
        grid.Columns.Add("types", "Clip types");
        hotness.Items.AddRange(HotnessValues);
        clipTypes.Items.AddRange(ClipTypeValues);
        timeline.DurationMs = durationMs;
        timeline.Clips = clips;
        timeline.AllowDividerDragging = allowBoundaryEditing;
        addDivider.Visible = removeDivider.Visible = autoDetect.Visible =
            detector.Visible = allowBoundaryEditing;
        skipTransitions.Visible = transitionSeconds.Visible =
            transitionSecondsLabel.Visible = allowBoundaryEditing;
        timeline.PositionChanged += (_, _) => { UpdatePosition(); RequestPreview(); };
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
        timeline.DividerMoved += _ => UpdateGridTimes();
        grid.SelectionChanged += (_, _) => LoadSelectedMetadata();
        grid.CellDoubleClick += (_, e) => SeekToSegment(
            e.RowIndex, e.ColumnIndex == 3);
        include.Click += (_, _) =>
        {
            if (loadingMetadata || !include.Enabled) return;
            include.CheckState = NextIncludeCheckState(include.CheckState);
            SaveSelectedMetadata();
        };
        hotness.SelectedIndexChanged += (_, _) => SaveSelectedMetadata();
        clipTypes.ItemCheck += (_, e) =>
        {
            if (!loadingMetadata) BeginInvoke(() => ApplyClipType(
                e.Index, e.NewValue == CheckState.Checked));
        };
        addDivider.Click += (_, _) => AddDivider();
        removeDivider.Click += (_, _) => RemoveDivider();
        autoDetect.Click += async (_, _) => await AutoDetectAsync();
        skipTransitions.CheckedChanged += (_, _) =>
            transitionSeconds.Enabled = skipTransitions.Checked;
        ok.Click += (_, _) => AcceptClips();
        previousFrame.Click += (_, _) => StepFrame(-1);
        play.Click += (_, _) => TogglePlayback();
        nextFrame.Click += (_, _) => StepFrame(1);
        slowMotion.CheckedChanged += (_, _) => RestartPlayback();
        playback.Tick += (_, _) => PlaybackTick();
        previewDelay.Tick += (_, _) => { previewDelay.Stop(); _ = LoadPreviewAsync(timeline.PositionMs); };
        FormClosed += (_, _) => { playback.Stop(); previewDelay.Stop(); previewCancellation?.Cancel(); streamCancellation?.Cancel(); detectionCancellation?.Cancel(); preview.Image?.Dispose(); };
        RefreshGrid(0);
        UpdatePosition();
        RequestPreview();
        AppTheme.Apply(this);
    }

    void AddDivider()
    {
        long at = timeline.PositionMs;
        int index = clips.FindIndex(clip => at > clip.StartMs + 250 && at < clip.EndMs - 250);
        if (index < 0)
        {
            MessageBox.Show(this, "Place the playhead at least 0.25 seconds from an existing divider.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        CustomShowClip first = clips[index];
        CustomShowClip second = Clone(first);
        first.Media = second.Media = null;
        second.Id = Guid.NewGuid().ToString("N");
        second.StartMs = at;
        first.EndMs = at;
        clips.Insert(index + 1, second);
        timeline.Invalidate();
        RefreshGrid(index + 1);
    }

    void RemoveDivider()
    {
        int index = SelectedIndex;
        if (index <= 0 || index >= clips.Count) return;
        clips[index - 1].EndMs = clips[index].EndMs;
        clips[index - 1].Media = null;
        clips.RemoveAt(index);
        timeline.Invalidate();
        RefreshGrid(index - 1);
    }

    void RefreshGrid(int selected)
    {
        loadingMetadata = true;
        grid.Rows.Clear();
        for (int i = 0; i < clips.Count; i++)
        {
            CustomShowClip clip = clips[i];
            grid.Rows.Add(i + 1, clip.Included ? "Clip" : "Skip",
                Format(clip.StartMs), Format(clip.EndMs),
                Format(clip.EndMs - clip.StartMs), clip.Hotness,
                string.Join(", ", clip.ClipTypes));
        }
        if (grid.Rows.Count > 0)
            grid.Rows[Math.Clamp(selected, 0, grid.Rows.Count - 1)].Selected = true;
        loadingMetadata = false;
        LoadSelectedMetadata();
    }

    int SelectedIndex => grid.SelectedRows.Count == 0 ? -1 : grid.SelectedRows[0].Index;
    int[] SelectedIndexes => grid.SelectedRows.Cast<DataGridViewRow>()
        .Select(row => row.Index).Where(index => index >= 0 && index < clips.Count)
        .Order().ToArray();

    void LoadSelectedMetadata()
    {
        if (loadingMetadata) return;
        int[] indexes = SelectedIndexes;
        removeDivider.Enabled = indexes.Length == 1 && indexes[0] > 0;
        if (indexes.Length == 0) return;
        loadingMetadata = true;
        CustomShowClip[] selected = indexes.Select(index => clips[index]).ToArray();
        include.Enabled = !usesPerClipMedia || selected.All(clip => clip.Media != null);
        include.CheckState = selected.All(clip => clip.Included) ? CheckState.Checked :
            selected.All(clip => !clip.Included) ? CheckState.Unchecked : CheckState.Indeterminate;
        hotness.SelectedItem = selected.Select(clip => clip.Hotness).Distinct().Count() == 1
            ? selected[0].Hotness : null;
        for (int i = 0; i < clipTypes.Items.Count; i++)
            clipTypes.SetItemChecked(i, selected.All(clip =>
                clip.ClipTypes.Contains(clipTypes.Items[i]!.ToString())));
        loadingMetadata = false;
        hotness.Enabled = clipTypes.Enabled = true;
    }

    void SaveSelectedMetadata()
    {
        if (loadingMetadata) return;
        int[] indexes = SelectedIndexes;
        if (indexes.Length == 0) return;
        foreach (int index in indexes)
        {
            if (include.CheckState != CheckState.Indeterminate)
                clips[index].Included = include.CheckState == CheckState.Checked;
            if (hotness.SelectedItem is string selectedHotness)
                clips[index].Hotness = selectedHotness;
            UpdateGridMetadata(index);
        }
        timeline.Invalidate();
    }

    void ApplyClipType(int itemIndex, bool add)
    {
        if (loadingMetadata || itemIndex < 0 || itemIndex >= clipTypes.Items.Count) return;
        int[] indexes = SelectedIndexes;
        string value = clipTypes.Items[itemIndex]!.ToString()!;
        if (!add && indexes.Any(index => clips[index].ClipTypes.Length == 1 &&
                clips[index].ClipTypes.Contains(value)))
        {
            LoadSelectedMetadata();
            return;
        }
        foreach (int index in indexes)
        {
            HashSet<string> values = [.. clips[index].ClipTypes];
            if (add) values.Add(value); else values.Remove(value);
            clips[index].ClipTypes = ClipTypeValues.Where(values.Contains).ToArray();
            UpdateGridMetadata(index);
        }
    }

    void UpdateGridMetadata(int index)
    {
        grid.Rows[index].Cells[1].Value = clips[index].Included ? "Clip" : "Skip";
        grid.Rows[index].Cells[5].Value = clips[index].Hotness;
        grid.Rows[index].Cells[6].Value = string.Join(", ", clips[index].ClipTypes);
    }

    void UpdateGridTimes()
    {
        for (int i = 0; i < clips.Count && i < grid.Rows.Count; i++)
        {
            grid.Rows[i].Cells[2].Value = Format(clips[i].StartMs);
            grid.Rows[i].Cells[3].Value = Format(clips[i].EndMs);
            grid.Rows[i].Cells[4].Value = Format(clips[i].EndMs - clips[i].StartMs);
        }
    }

    void AcceptClips()
    {
        SaveSelectedMetadata();
        if (!clips.Any(clip => clip.Included))
        {
            MessageBox.Show(this, "Include at least one segment as a playable clip.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    async Task AutoDetectAsync()
    {
        bool transNet = detector.SelectedItem?.ToString()?.StartsWith(
            "Accurate", StringComparison.Ordinal) == true;
        if (transNet && configuration == null) return;
        if (clips.Count > 1 && MessageBox.Show(this,
                "Replace the current dividers with automatically detected scene changes?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        StopPlayback(requestPreview: false);
        SaveSelectedMetadata();
        autoDetect.Enabled = detector.Enabled = skipTransitions.Enabled =
            transitionSeconds.Enabled = addDivider.Enabled =
            removeDivider.Enabled = timeline.Enabled = false;
        Cursor = Cursors.WaitCursor;
        detectionCancellation?.Cancel();
        detectionCancellation = new CancellationTokenSource();
        bool acceptingProgress = true;
        try
        {
            Progress<int> progress = new(value =>
            {
                if (acceptingProgress)
                    autoDetect.Text = $"Detecting... {Math.Clamp(value, 0, 100)}%";
            });
            long[] dividers = transNet
                ? await CustomSceneDetector.DetectTransNetAsync(configuration!,
                    videoPath, progress, detectionCancellation.Token)
                : await CustomSceneDetector.DetectFastAsync(videoPath, durationMs,
                    progress, detectionCancellation.Token);
            long[] validDividers = dividers.Where(value => value > 250 && value < durationMs - 250)
                .Distinct().Order().ToArray();
            if (validDividers.Length == 0)
            {
                MessageBox.Show(this, "No scene changes were found.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            CustomShowClip seed = Clone(clips.FirstOrDefault(clip => clip.Included) ?? clips[0]);
            long bufferMs = skipTransitions.Checked
                ? checked((long)Math.Round(transitionSeconds.Value * 1000)) : 0;
            CustomShowClip[] detected = BuildDetectedClips(
                seed, validDividers, durationMs, bufferMs);
            clips.Clear();
            clips.AddRange(detected);
            timeline.PositionMs = validDividers[0];
            timeline.Invalidate();
            RefreshGrid(0);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Auto-detect Clips",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            acceptingProgress = false;
            detectionCancellation?.Dispose();
            detectionCancellation = null;
            if (!IsDisposed)
            {
                autoDetect.Text = "Auto-detect clips";
                autoDetect.Enabled = detector.Enabled = skipTransitions.Enabled =
                    addDivider.Enabled = timeline.Enabled = true;
                transitionSeconds.Enabled = skipTransitions.Checked;
                removeDivider.Enabled = SelectedIndex > 0;
                Cursor = Cursors.Default;
                grid.Cursor = Cursors.Default;
                RequestPreview();
            }
        }
    }

    void TogglePlayback()
    {
        if (playback.Enabled)
        {
            StopPlayback();
            return;
        }
        StartPlayback();
    }

    void StartPlayback(long? endMs = null)
    {
        if (timeline.PositionMs >= durationMs) timeline.PositionMs = 0;
        playEndMs = Math.Clamp(endMs ?? durationMs, timeline.PositionMs, durationMs);
        previewDelay.Stop();
        previewCancellation?.Cancel();
        playStartMs = timeline.PositionMs;
        playClock.Restart();
        playback.Start();
        play.Text = "Pause";
        streamCancellation?.Cancel();
        streamCancellation = new CancellationTokenSource();
        double speed = slowMotion.Checked ? .25 : 1;
        _ = Task.Run(() => StreamPreviewAsync(
            timeline.PositionMs, speed, streamCancellation.Token));
    }

    void StopPlayback(bool requestPreview = true)
    {
        playback.Stop();
        playClock.Stop();
        streamCancellation?.Cancel();
        streamCancellation = null;
        play.Text = "Play";
        if (requestPreview) RequestPreview();
    }

    void PlaybackTick()
    {
        timeline.PositionMs = Math.Min(playEndMs,
            playStartMs + (long)Math.Round(playClock.ElapsedMilliseconds *
                (slowMotion.Checked ? .25 : 1)));
        HighlightPlayingClip();
        if (timeline.PositionMs >= playEndMs) TogglePlayback();
    }

    void RestartPlayback()
    {
        if (!playback.Enabled) return;
        long end = playEndMs;
        StopPlayback(requestPreview: false);
        StartPlayback(end);
    }

    void StepFrame(int direction)
    {
        StopPlayback(requestPreview: false);
        timeline.PositionMs = FrameStep(timeline.PositionMs, direction,
            frameDurationMs, durationMs);
        HighlightPlayingClip();
    }

    void HighlightPlayingClip()
    {
        int index = ClipIndexAt(clips, timeline.PositionMs);
        if (index < 0 || index == SelectedIndex) return;
        grid.ClearSelection();
        grid.Rows[index].Selected = true;
        if (!grid.Rows[index].Displayed)
            grid.FirstDisplayedScrollingRowIndex = index;
    }

    void SeekToSegment(int index, bool seekToEnd)
    {
        if (index < 0 || index >= clips.Count) return;
        StopPlayback(requestPreview: false);
        grid.ClearSelection();
        grid.Rows[index].Selected = true;
        timeline.PositionMs = SegmentSeekPosition(clips[index], seekToEnd,
            frameDurationMs, durationMs);
    }

    internal static long SegmentSeekPosition(CustomShowClip clip, bool seekToEnd,
        double frameDurationMs, long durationMs) => seekToEnd
        ? Math.Clamp((long)Math.Round(clip.EndMs - frameDurationMs),
            clip.StartMs, durationMs)
        : clip.StartMs;

    void UpdatePosition() => position.Text = $"{Format(timeline.PositionMs)} / {Format(durationMs)}";

    void RequestPreview()
    {
        if (playback.Enabled) return;
        previewDelay.Stop();
        previewDelay.Start();
    }

    async Task StreamPreviewAsync(long atMs, double speed, CancellationToken token)
    {
        try
        {
            ProcessStartInfo start = PreviewProcessStart(atMs);
            start.ArgumentList.Add(speed == 1 ? "-re" : "-readrate");
            if (speed != 1) start.ArgumentList.Add(speed.ToString(
                CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-i");
            start.ArgumentList.Add(videoPath);
            foreach (string argument in new[] { "-an", "-vf",
                "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2",
                "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ??
                throw new InvalidOperationException("FFmpeg did not start.");
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });
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
                        Image? old = preview.Image;
                        preview.Image = bitmap;
                        old?.Dispose();
                    });
                }
                catch { bitmap.Dispose(); throw; }
            }
        }
        catch (EndOfStreamException) { }
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
            (atMs / 1000d).ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture) })
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

    async Task LoadPreviewAsync(long atMs)
    {
        previewCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        previewCancellation = cancellation;
        try
        {
            ProcessStartInfo start = PreviewProcessStart(atMs);
            foreach (string argument in new[] { "-i", videoPath,
                "-frames:v", "1", "-vf",
                "scale=960:540:force_original_aspect_ratio=decrease,pad=960:540:(ow-iw)/2:(oh-ih)/2",
                "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1" })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ??
                throw new InvalidOperationException("FFmpeg did not start.");
            using CancellationTokenRegistration registration = cancellation.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });
            Task<string> error = process.StandardError.ReadToEndAsync(cancellation.Token);
            using MemoryStream bytes = new();
            await process.StandardOutput.BaseStream.CopyToAsync(bytes, cancellation.Token);
            await process.WaitForExitAsync(cancellation.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
            bytes.Position = 0;
            using Image image = Image.FromStream(bytes);
            Bitmap bitmap = new(image);
            if (cancellation.IsCancellationRequested) { bitmap.Dispose(); return; }
            Image? old = preview.Image;
            preview.Image = bitmap;
            old?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsDisposed) position.Text = "Preview unavailable: " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(previewCancellation, cancellation))
                previewCancellation = null;
        }
    }

    static CustomShowClip Clone(CustomShowClip clip) => new()
    {
        Id = clip.Id, StartMs = clip.StartMs, EndMs = clip.EndMs,
        Included = clip.Included, Hotness = clip.Hotness,
        AlphaThreshold = clip.AlphaThreshold,
        ClipTypes = [.. clip.ClipTypes],
        Media = clip.Media == null ? null : new()
        {
            Foreground = clip.Media.Foreground, Alpha = clip.Media.Alpha,
            Width = clip.Media.Width, Height = clip.Media.Height,
            FrameRate = clip.Media.FrameRate, DurationMs = clip.Media.DurationMs
        }
    };

    static string Format(long milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(
            milliseconds >= 3_600_000 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");

    static int ClipIndexAt(IReadOnlyList<CustomShowClip> values, long position)
    {
        for (int i = 0; i < values.Count; i++)
            if (position >= values[i].StartMs &&
                (position < values[i].EndMs || i == values.Count - 1 && position == values[i].EndMs))
                return i;
        return -1;
    }

    internal static CustomShowClip[] BuildDetectedClips(CustomShowClip seed,
        IEnumerable<long> dividers, long durationMs, long bufferMs)
    {
        const long minimumClipMs = 10_000;
        List<(long Start, long End)> skipped = [];
        foreach (long divider in dividers.Where(value => value > 0 && value < durationMs)
                     .Distinct().Order())
        {
            long start = Math.Max(0, divider - Math.Max(0, bufferMs));
            long end = Math.Min(durationMs, divider + Math.Max(0, bufferMs));
            if (skipped.Count > 0 && start <= skipped[^1].End)
                skipped[^1] = (skipped[^1].Start, Math.Max(skipped[^1].End, end));
            else skipped.Add((start, end));
        }
        List<(long Start, long End, bool Included)> ranges = [];
        long cursor = 0;
        foreach ((long start, long end) in skipped)
        {
            if (cursor < start) ranges.Add((cursor, start, true));
            if (start < end) ranges.Add((start, end, false));
            cursor = end;
        }
        if (cursor < durationMs) ranges.Add((cursor, durationMs, true));
        return ranges.Select(range =>
        {
            CustomShowClip clip = Clone(seed);
            clip.Id = Guid.NewGuid().ToString("N");
            clip.StartMs = range.Start;
            clip.EndMs = range.End;
            clip.Included = range.Included && range.End - range.Start >= minimumClipMs;
            clip.Media = null;
            return clip;
        }).ToArray();
    }

    internal static long FrameStep(long position, int direction,
        double frameDurationMs, long durationMs) => Math.Clamp(
            (long)Math.Round((Math.Round(position / frameDurationMs) +
                Math.Sign(direction)) * frameDurationMs), 0, durationMs);

    internal static CheckState NextIncludeCheckState(CheckState current) =>
        current == CheckState.Checked
            ? CheckState.Unchecked : CheckState.Checked;

    internal static bool VerifyClipSplitting()
    {
        CustomShowClip first = new() { StartMs = 0, EndMs = 1_000 };
        CustomShowClip second = Clone(first);
        second.Id = Guid.NewGuid().ToString("N");
        first.EndMs = 400;
        second.StartMs = 400;
        second.Included = false;
        CustomShowClip seed = new();
        CustomShowClip[] detected = BuildDetectedClips(
            seed, [15_000, 30_000], 40_000, 1_000);
        CustomShowClip[] unbuffered = BuildDetectedClips(
            seed, [12_000], 30_000, 0);
        return first.EndMs == second.StartMs && second.EndMs == 1_000 &&
            first.Id != second.Id && !second.Included &&
            ClipIndexAt([first, second], 399) == 0 &&
            ClipIndexAt([first, second], 400) == 1 &&
            ClipIndexAt([first, second], 1_000) == 1 &&
            FrameStep(1_000, 1, 40, 2_000) == 1_040 &&
            FrameStep(1_000, -1, 40, 2_000) == 960 &&
            SegmentSeekPosition(first, false, 40, 1_000) == 0 &&
            SegmentSeekPosition(first, true, 40, 1_000) == 360 &&
            SegmentSeekPosition(second, true, 40, 1_000) == 960 &&
            NextIncludeCheckState(CheckState.Unchecked) == CheckState.Checked &&
            NextIncludeCheckState(CheckState.Checked) == CheckState.Unchecked &&
            NextIncludeCheckState(CheckState.Indeterminate) == CheckState.Checked &&
            detected.Length == 5 && detected[0].Included &&
            detected[0].StartMs == 0 && detected[0].EndMs == 14_000 &&
            !detected[1].Included && detected[1].EndMs == 16_000 &&
            detected[2].Included && detected[2].EndMs == 29_000 &&
            !detected[3].Included && !detected[4].Included &&
            detected[4].StartMs == 31_000 && detected[4].EndMs == 40_000 &&
            unbuffered.Length == 2 && unbuffered.All(clip => clip.Included) &&
            unbuffered[0].EndMs == 12_000 && unbuffered[1].StartMs == 12_000 &&
            CustomSceneDetector.MonotonicProgress(18, 5) == 18 &&
            CustomSceneDetector.MonotonicProgress(18, 20) == 20;
    }
}

internal static class CustomSceneDetector
{
    static string WorkerPath => Path.Combine(AppContext.BaseDirectory,
        "custom-shows", "transnetv2_worker.py");

    internal static async Task<long[]> DetectFastAsync(string source, long durationMs,
        IProgress<int>? progress, CancellationToken token)
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        ProcessStartInfo start = new(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-v", "info", "-nostats", "-i", source,
            "-an", "-vf", "scale=320:-2,select=gt(scene\\,0.35),showinfo",
            "-fps_mode", "vfr", "-f", "null", "-", "-progress", "pipe:1" })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("FFmpeg scene detection could not start.");
        using CancellationTokenRegistration registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        List<long> cuts = [];
        Task readProgress = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(token) is string line)
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line[12..], out long microseconds) && durationMs > 0)
                    progress?.Report((int)Math.Clamp(microseconds / 1000d / durationMs * 100, 0, 99));
        }, token);
        StringBuilder errors = new();
        Task readScenes = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(token) is string line)
            {
                errors.AppendLine(line);
                int marker = line.IndexOf("pts_time:", StringComparison.Ordinal);
                if (marker < 0) continue;
                string value = line[(marker + 9)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double seconds))
                    cuts.Add(checked((long)Math.Round(seconds * 1000)));
            }
        }, token);
        await process.WaitForExitAsync(token);
        await Task.WhenAll(readProgress, readScenes);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(errors.ToString().Trim());
        progress?.Report(100);
        List<long> result = [];
        foreach (long cut in cuts.Where(value => value >= 250 && value <= durationMs - 250)
                     .Distinct().Order())
            if (result.Count == 0 || cut - result[^1] >= 500) result.Add(cut);
        return [.. result];
    }

    internal static async Task<long[]> DetectTransNetAsync(CustomShowConfiguration configuration,
        string source, IProgress<int>? progress, CancellationToken token)
    {
        if (!File.Exists(configuration.PythonExecutable))
            throw new FileNotFoundException("Configure the custom-show Python environment first.",
                configuration.PythonExecutable);
        if (!File.Exists(WorkerPath))
            throw new FileNotFoundException("The TransNetV2 worker is missing.", WorkerPath);
        string runtime = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(configuration.PythonExecutable)!, "..", ".."));
        if (!File.Exists(Path.Combine(runtime, "TRANSNETV2_COMMIT")))
            throw new InvalidOperationException(
                "TransNetV2 is not installed. Run Install / Update Processing Tools and choose Yes.");
        ProcessStartInfo start = new(configuration.PythonExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            WorkerPath, "--source", source, "--runtime", runtime
        }) start.ArgumentList.Add(argument);
        start.Environment["IQP_FFMPEG"] = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        start.Environment["IQP_FFPROBE"] = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("Python could not be started.");
        using CancellationTokenRegistration registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        long[]? dividers = null;
        int displayedProgress = 0;
        while (await process.StandardOutput.ReadLineAsync(token) is string line)
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(line);
                JsonElement root = json.RootElement;
                if (root.TryGetProperty("percent", out JsonElement percent))
                {
                    int next = MonotonicProgress(
                        displayedProgress, percent.GetDouble());
                    if (next > displayedProgress)
                    {
                        displayedProgress = next;
                        progress?.Report(next);
                    }
                }
                if (root.TryGetProperty("dividersMs", out JsonElement values))
                    dividers = values.EnumerateArray().Select(value => value.GetInt64()).ToArray();
            }
            catch (JsonException) { }
        }
        await process.WaitForExitAsync(token);
        string errorText = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText)
                ? $"TransNetV2 failed with exit code {process.ExitCode}." : errorText.Trim());
        return dividers ?? throw new InvalidDataException(
            "TransNetV2 did not return scene boundaries.");
    }

    internal static int MonotonicProgress(int current, double reported) =>
        Math.Max(Math.Clamp(current, 0, 100),
            (int)Math.Round(Math.Clamp(reported, 0, 100)));
}

internal sealed class ClipTimelineControl : Control
{
    long durationMs, positionMs;
    bool scrubbing;
    int draggingDivider = -1;
    IList<long> fixedMarkers = [];
    IList<(long StartMs, long EndMs, Color Color)> highlightedRanges = [];
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal long DurationMs { get => durationMs; set { durationMs = Math.Max(1, value); Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal long PositionMs
    {
        get => positionMs;
        set
        {
            long next = Math.Clamp(value, 0, durationMs);
            if (next == positionMs) return;
            positionMs = next;
            Invalidate();
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal IList<CustomShowClip> Clips { get; set; } = [];
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool AllowDividerDragging { get; set; } = true;
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal IList<long> FixedMarkers
    {
        get => fixedMarkers;
        set { fixedMarkers = value; Invalidate(); }
    }
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal IList<(long StartMs, long EndMs, Color Color)> HighlightedRanges
    {
        get => highlightedRanges;
        set { highlightedRanges = value; Invalidate(); }
    }
    internal event EventHandler? PositionChanged;
    internal event EventHandler? ScrubStarted;
    internal event EventHandler? ScrubEnded;
    internal event Action<int>? DividerMoved;
    internal event Action<long>? FixedMarkerClicked;

    internal ClipTimelineControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle bar = BarRectangle;
        e.Graphics.FillRectangle(SystemBrushes.ControlDark, bar);
        for (int i = 0; i < Clips.Count; i++)
        {
            CustomShowClip clip = Clips[i];
            int left = bar.Left + (int)Math.Round(bar.Width * clip.StartMs / (double)durationMs);
            int right = bar.Left + (int)Math.Round(bar.Width * clip.EndMs / (double)durationMs);
            using Brush brush = new SolidBrush(!clip.Included
                ? Color.FromArgb(90, 90, 90)
                : i % 2 == 0 ? Color.FromArgb(70, 145, 210) : Color.FromArgb(72, 180, 125));
            e.Graphics.FillRectangle(brush, left, bar.Top, Math.Max(1, right - left), bar.Height);
            if (!clip.Included && right - left > 34)
                TextRenderer.DrawText(e.Graphics, "SKIP", Font,
                    new Rectangle(left, bar.Top, right - left, bar.Height), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (i > 0)
            {
                e.Graphics.DrawLine(Pens.White, left, bar.Top, left, bar.Bottom);
                Point[] marker = [new(left - 8, bar.Bottom + 4),
                    new(left + 8, bar.Bottom + 4), new(left, Height - 3)];
                e.Graphics.FillPolygon(Brushes.Gold, marker);
                e.Graphics.DrawPolygon(Pens.Black, marker);
            }
        }
        foreach ((long startMs, long endMs, Color color) in highlightedRanges)
        {
            int left = bar.Left + (int)Math.Round(
                bar.Width * startMs / (double)durationMs);
            int right = bar.Left + (int)Math.Round(
                bar.Width * endMs / (double)durationMs);
            using Brush brush = new SolidBrush(color);
            e.Graphics.FillRectangle(brush, left, bar.Top,
                Math.Max(1, right - left), bar.Height);
        }
        foreach (long markerMs in fixedMarkers)
        {
            int markerX = bar.Left + (int)Math.Round(
                bar.Width * markerMs / (double)durationMs);
            e.Graphics.DrawLine(Pens.White, markerX, bar.Top, markerX, bar.Bottom);
            Point[] marker = [new(markerX - 8, bar.Bottom + 4),
                new(markerX + 8, bar.Bottom + 4), new(markerX, Height - 3)];
            e.Graphics.FillPolygon(Brushes.Gold, marker);
            e.Graphics.DrawPolygon(Pens.Black, marker);
        }
        int playhead = bar.Left + (int)Math.Round(bar.Width * positionMs / (double)durationMs);
        using Pen pen = new(Color.Red, 2);
        e.Graphics.DrawLine(pen, playhead, 3, playhead, bar.Bottom + 2);
        e.Graphics.DrawRectangle(Pens.DimGray, bar);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        int fixedMarker = FixedMarkerAt(e.Location);
        if (fixedMarker >= 0)
        {
            PositionMs = fixedMarkers[fixedMarker];
            FixedMarkerClicked?.Invoke(positionMs);
            return;
        }
        draggingDivider = AllowDividerDragging ? MarkerAt(e.Location) : -1;
        scrubbing = true;
        Capture = true;
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        if (draggingDivider > 0) MoveDivider(e.X); else SetPosition(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.Left)
        {
            if (draggingDivider > 0) MoveDivider(e.X); else if (scrubbing) SetPosition(e.X);
        }
        else Cursor = AllowDividerDragging && MarkerAt(e.Location) > 0
            ? Cursors.SizeWE : Cursors.Hand;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!scrubbing || e.Button != MouseButtons.Left) return;
        if (draggingDivider > 0) MoveDivider(e.X); else SetPosition(e.X);
        draggingDivider = -1;
        scrubbing = false;
        Capture = false;
        Cursor = Cursors.Hand;
        ScrubEnded?.Invoke(this, EventArgs.Empty);
    }

    Rectangle BarRectangle => new(8, 7, Math.Max(1, Width - 16),
        Math.Max(12, Height - 28));

    int MarkerAt(Point point)
    {
        Rectangle bar = BarRectangle;
        if (point.Y < bar.Bottom + 1) return -1;
        for (int i = 1; i < Clips.Count; i++)
        {
            int divider = bar.Left + (int)Math.Round(
                bar.Width * Clips[i].StartMs / (double)durationMs);
            if (Math.Abs(point.X - divider) <= 9) return i;
        }
        return -1;
    }

    int FixedMarkerAt(Point point)
    {
        Rectangle bar = BarRectangle;
        if (point.Y < bar.Bottom + 1) return -1;
        for (int i = 0; i < fixedMarkers.Count; i++)
        {
            int marker = bar.Left + (int)Math.Round(
                bar.Width * fixedMarkers[i] / (double)durationMs);
            if (Math.Abs(point.X - marker) <= 9) return i;
        }
        return -1;
    }

    void MoveDivider(int x)
    {
        int index = draggingDivider;
        if (index <= 0 || index >= Clips.Count) return;
        Rectangle bar = BarRectangle;
        long at = (long)Math.Round(durationMs *
            Math.Clamp((x - bar.Left) / (double)bar.Width, 0, 1));
        at = Math.Clamp(at, Clips[index - 1].StartMs + 250,
            Clips[index].EndMs - 250);
        Clips[index - 1].EndMs = Clips[index].StartMs = at;
        PositionMs = at;
        Invalidate();
        DividerMoved?.Invoke(index);
    }

    void SetPosition(int x)
    {
        Rectangle bar = BarRectangle;
        PositionMs = (long)Math.Round(durationMs *
            Math.Clamp((x - bar.Left) / (double)bar.Width, 0, 1));
    }

    internal static bool VerifyMarkerHitTesting()
    {
        ClipTimelineControl control = new()
        {
            Size = new Size(400, 60), DurationMs = 1000,
            Clips = [new() { StartMs = 0, EndMs = 500 },
                new() { StartMs = 500, EndMs = 1000 }],
            FixedMarkers = [250]
        };
        int x = control.BarRectangle.Left + control.BarRectangle.Width / 2;
        int fixedX = control.BarRectangle.Left + control.BarRectangle.Width / 4;
        return control.MarkerAt(new Point(x, control.Height - 5)) == 1 &&
            control.MarkerAt(new Point(x, control.BarRectangle.Top + 5)) == -1 &&
            control.FixedMarkerAt(new Point(fixedX, control.Height - 5)) == 0;
    }
}
