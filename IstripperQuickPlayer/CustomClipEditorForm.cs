using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed record SceneDetectionRange(long StartMs, long EndMs,
    string IntraLabel, string InterLabel);

internal sealed record SceneDetectionResult(string Method, long[] BoundariesMs,
    SceneDetectionRange[] Ranges, string? ToolRevision = null, int? OverlapFrames = null,
    string? SensitivityDataFormat = null, string? SensitivityData = null,
    double? DetectionFrameRate = null);

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
    CustomShowClipDetection? clipDetection;
    readonly DxgiMaskPreviewControl preview = new() { Dock = DockStyle.Fill };
    readonly ClipTimelineControl timeline = new() { Dock = DockStyle.Fill, Height = 60 };
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    readonly ComboBox hotness = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckedListBox clipTypes = new() { CheckOnClick = true, Height = 120 };
    readonly Controls.PlaybackSeekBar alphaThreshold = new()
    {
        Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 10,
        Dock = DockStyle.Fill, Height = 32,
        AccessibleName = "Selected clip minimum alpha"
    };
    readonly TextBox alphaThresholdText = new()
    {
        Width = 48, MaxLength = 3,
        TextAlign = HorizontalAlignment.Right
    };
    readonly Controls.PlaybackSeekBar edgeChoke = new()
    {
        Minimum = 0, Maximum = 16, SmallChange = 1, LargeChange = 4,
        Value = 4, Dock = DockStyle.Fill, Height = 32,
        AccessibleName = "Selected clip edge cleanup"
    };
    readonly TextBox edgeChokeText = new()
    {
        Width = 48, MaxLength = 4,
        TextAlign = HorizontalAlignment.Right
    };
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
    readonly Button importSetup = new() { Text = "Import clip setup...", AutoSize = true };
    readonly Button autoDetect = new() { Text = "Auto-detect clips", AutoSize = true };
    readonly Button reapplyDetection = new()
    {
        Text = "Reapply detected clips", AutoSize = true, Enabled = false
    };
    readonly ComboBox detector = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 190 };
    readonly Label sensitivityLabel = new() { Text = "Sensitivity: 50%",
        AutoSize = true, Margin = new Padding(8, 7, 3, 3) };
    readonly Controls.PlaybackSeekBar sensitivity = new() { Minimum = 1,
        Maximum = 100, Value = 50, Width = 220, Height = 32,
        AccessibleName = "Clip detection sensitivity" };
    readonly CheckBox skipTransitions = new() { Text = "Skip transition ±",
        AutoSize = true };
    readonly NumericUpDown transitionSeconds = new() { DecimalPlaces = 2,
        Minimum = .05m, Maximum = 30, Increment = .1m, Value = .5m, Width = 62,
        Enabled = false };
    readonly Label transitionSecondsLabel = new() { Text = "seconds", AutoSize = true,
        Margin = new Padding(0, 7, 3, 3) };
    readonly CheckBox showSkipped = new() { Text = "Show skipped clips in grid",
        AutoSize = true, Checked = true };
    readonly NumericUpDown shortClipSeconds = new() { DecimalPlaces = 1,
        Minimum = 0, Maximum = 3600, Increment = .5m, Value = 20, Width = 72 };
    readonly Label shortClipLabel = new() { Text = "Auto-skip shorter than (seconds)",
        AutoSize = true, Margin = new Padding(8, 7, 3, 3) };
    readonly System.Windows.Forms.Timer playback = new() { Interval = 100 };
    readonly System.Windows.Forms.Timer previewDelay = new() { Interval = 140 };
    readonly System.Windows.Forms.Timer sensitivityDelay = new() { Interval = 180 };
    CancellationTokenSource? previewCancellation;
    CancellationTokenSource? streamCancellation;
    CancellationTokenSource? detectionCancellation;
    Stopwatch playClock = new();
    long playStartMs;
    long playEndMs;
    bool loadingMetadata;
    bool resumeAfterScrub;
    int sensitivityBeforeChange;
    bool applyingSensitivity;

    internal CustomShowClip[] Clips => clips.Select(Clone).ToArray();
    internal CustomShowClipDetection? Detection => CloneDetection(clipDetection);

    internal CustomClipEditorForm(string videoPath,
        IReadOnlyList<CustomShowClip> existing, string defaultHotness,
        string[] defaultTypes, CustomShowConfiguration? configuration = null,
        bool allowBoundaryEditing = true,
        CustomShowClipDetection? existingDetection = null)
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
        clipDetection = CloneDetection(existingDetection);
        clips[^1].EndMs = durationMs;
        CustomShowStore.ValidateClips([.. clips], durationMs);

        Text = allowBoundaryEditing ? "Split Custom Show into Clips" :
            "Edit Custom Show Clip Metadata";
        // Wide enough at normal Windows scaling to keep the complete clip-action
        // row on one line (matching the preferred editor layout).
        ClientSize = new Size(1500, 1000);
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
        TableLayoutPanel gridPanel = new() { Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 2 };
        gridPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        gridPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        TableLayoutPanel gridHeader = new() { Dock = DockStyle.Fill, AutoSize = true,
            ColumnCount = 3 };
        gridHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gridHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        gridHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        gridHeader.Controls.Add(showSkipped, 0, 0);
        gridHeader.Controls.Add(sensitivityLabel, 1, 0);
        gridHeader.Controls.Add(sensitivity, 2, 0);
        gridPanel.Controls.Add(gridHeader, 0, 0);
        gridPanel.Controls.Add(grid, 0, 1);
        details.Controls.Add(gridPanel, 0, 0);
        TableLayoutPanel metadata = new() { Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 9, Padding = new Padding(8) };
        metadata.Controls.Add(include);
        metadata.Controls.Add(new Label { Text = "Selected clip hotness", AutoSize = true });
        metadata.Controls.Add(hotness);
        metadata.Controls.Add(new Label { Text = "Selected clip types", AutoSize = true });
        metadata.Controls.Add(clipTypes);
        Label alphaThresholdLabel = new()
        {
            Text = "Selected clip minimum alpha (0–255)", AutoSize = true
        };
        TableLayoutPanel alphaThresholdRow = new()
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2
        };
        alphaThresholdRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        alphaThresholdRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        alphaThresholdRow.Controls.Add(alphaThreshold, 0, 0);
        alphaThresholdRow.Controls.Add(alphaThresholdText, 1, 0);
        alphaThresholdLabel.Visible = alphaThresholdRow.Visible = usesPerClipMedia;
        metadata.Controls.Add(alphaThresholdLabel);
        metadata.Controls.Add(alphaThresholdRow);
        Label edgeChokeLabel = new()
        {
            Text = "Selected clip edge cleanup (0–4 px)", AutoSize = true
        };
        TableLayoutPanel edgeChokeRow = new()
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2
        };
        edgeChokeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        edgeChokeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        edgeChokeRow.Controls.Add(edgeChoke, 0, 0);
        edgeChokeRow.Controls.Add(edgeChokeText, 1, 0);
        edgeChokeLabel.Visible = edgeChokeRow.Visible = usesPerClipMedia;
        metadata.Controls.Add(edgeChokeLabel);
        metadata.Controls.Add(edgeChokeRow);
        details.Controls.Add(metadata, 1, 0);
        root.Controls.Add(details, 0, 3);

        FlowLayoutPanel actions = new() { Dock = DockStyle.Fill, AutoSize = true,
            WrapContents = true };
        Button ok = new() { Text = "OK", AutoSize = true };
        Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        detector.Items.Add("Fast (FFmpeg)");
        if (configuration != null &&
            CustomShowProcessor.IsTransNetV2Installed(configuration))
            detector.Items.Add("Accurate (TransNetV2)");
        if (configuration != null &&
            CustomShowProcessor.IsOmniShotCutInstalled(configuration))
            detector.Items.Add("Modern/Quality (OmniShotCut)");
        string preferred = configuration?.LastClipDetector ?? "transnetv2";
        int preferredIndex = detector.Items.Cast<string>().ToList().FindIndex(item =>
            DetectorId(item) == preferred);
        if (preferredIndex < 0)
            preferredIndex = detector.Items.Cast<string>().ToList().FindIndex(item =>
                DetectorId(item) == "transnetv2");
        if (preferredIndex < 0)
            preferredIndex = detector.Items.Cast<string>().ToList().FindIndex(item =>
                DetectorId(item) == "omnishotcut");
        detector.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
        LoadDetectorSensitivity(existingDetection);
        long retainedBufferMs = existingDetection?.TransitionBufferMs ?? 0;
        skipTransitions.Checked = retainedBufferMs > 0;
        transitionSeconds.Value = Math.Clamp(
            (retainedBufferMs > 0 ? retainedBufferMs : 500) / 1000m,
            transitionSeconds.Minimum, transitionSeconds.Maximum);
        transitionSeconds.Enabled = skipTransitions.Checked;
        shortClipSeconds.Value = Math.Clamp(
            (existingDetection?.MinimumClipMs ?? 20_000) / 1000m,
            shortClipSeconds.Minimum, shortClipSeconds.Maximum);
        actions.Controls.AddRange([addDivider, removeDivider, importSetup,
            detector, skipTransitions, transitionSeconds,
            transitionSecondsLabel, shortClipLabel, shortClipSeconds,
            reapplyDetection, autoDetect, ok, cancel]);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        grid.Columns.Add("number", "Segment");
        grid.Columns.Add("included", "Use");
        grid.Columns.Add("detected", "Detected as");
        grid.Columns.Add("start", "Start");
        grid.Columns.Add("end", "End");
        grid.Columns.Add("length", "Length");
        grid.Columns.Add("heat", "Hotness");
        grid.Columns.Add("types", "Clip types");
        grid.Columns.Add("alpha", "Min alpha");
        grid.Columns.Add("choke", "Edge cleanup");
        grid.Columns["alpha"]!.Visible = usesPerClipMedia;
        grid.Columns["choke"]!.Visible = usesPerClipMedia;
        hotness.Items.AddRange(HotnessValues);
        clipTypes.Items.AddRange(ClipTypeValues);
        timeline.DurationMs = durationMs;
        timeline.Clips = clips;
        timeline.AllowDividerDragging = allowBoundaryEditing;
        addDivider.Visible = removeDivider.Visible = importSetup.Visible = autoDetect.Visible =
            detector.Visible = allowBoundaryEditing;
        reapplyDetection.Visible = allowBoundaryEditing && HasReusableDetectionData();
        sensitivityLabel.Visible = sensitivity.Visible =
            allowBoundaryEditing && DetectorSupportsSensitivity();
        skipTransitions.Visible = transitionSeconds.Visible =
            transitionSecondsLabel.Visible = allowBoundaryEditing;
        shortClipLabel.Visible = shortClipSeconds.Visible = allowBoundaryEditing;
        timeline.PositionChanged += (_, _) =>
        {
            UpdatePosition();
            SelectClipAtTimelinePosition();
            RequestPreview();
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
        timeline.DividerMoved += _ => { MarkDetectionManuallyEdited(); UpdateGridTimes(); };
        grid.SelectionChanged += (_, _) => LoadSelectedMetadata();
        showSkipped.CheckedChanged += (_, _) => RefreshGrid(
            ClipIndexAt(clips, timeline.PositionMs));
        grid.CellDoubleClick += (_, e) => SeekToSegment(
            e.RowIndex, e.ColumnIndex == 4);
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
        alphaThreshold.Scroll += (_, _) =>
            ApplyAlphaThreshold(alphaThreshold.Value);
        alphaThresholdText.TextChanged += (_, _) =>
        {
            if (int.TryParse(alphaThresholdText.Text, out int value))
                ApplyAlphaThreshold(value);
        };
        alphaThresholdText.Leave += (_, _) =>
            LoadSelectedAlphaThreshold();
        edgeChoke.Scroll += (_, _) =>
            ApplyEdgeChoke(EdgeCleanupPixels(edgeChoke.Value));
        edgeChokeText.TextChanged += (_, _) =>
        {
            if (float.TryParse(edgeChokeText.Text, out float pixels))
                ApplyEdgeChoke(pixels);
        };
        edgeChokeText.Leave += (_, _) => LoadSelectedEdgeChoke();
        addDivider.Click += (_, _) => AddDivider();
        removeDivider.Click += (_, _) => RemoveDivider();
        importSetup.Click += (_, _) => ImportClipSetup();
        reapplyDetection.Click += (_, _) => ReapplyDetectionSettings();
        autoDetect.Click += async (_, _) => await AutoDetectAsync();
        detector.SelectedIndexChanged += (_, _) =>
        {
            SaveDetectorPreference();
            LoadDetectorSensitivity(null);
        };
        sensitivity.Scroll += SensitivityPreview;
        sensitivity.MouseUp += SensitivityCommit;
        sensitivity.KeyUp += SensitivityCommit;
        sensitivityDelay.Tick += (_, _) =>
        {
            sensitivityDelay.Stop();
            ApplySensitivityChange(allowConfirmation: false);
        };
        skipTransitions.CheckedChanged += (_, _) =>
        {
            transitionSeconds.Enabled = skipTransitions.Checked;
            UpdateReapplyDetectionButton();
        };
        transitionSeconds.ValueChanged += (_, _) => UpdateReapplyDetectionButton();
        shortClipSeconds.ValueChanged += (_, _) => UpdateReapplyDetectionButton();
        ok.Click += (_, _) => AcceptClips();
        _ = new HoldRepeatButton(previousFrame, () => StepFrame(-1));
        play.Click += (_, _) => TogglePlayback();
        _ = new HoldRepeatButton(nextFrame, () => StepFrame(1));
        slowMotion.CheckedChanged += (_, _) => RestartPlayback();
        playback.Tick += (_, _) => PlaybackTick();
        previewDelay.Tick += (_, _) => { previewDelay.Stop(); _ = LoadPreviewAsync(timeline.PositionMs); };
        FormClosed += (_, _) => { playback.Stop(); previewDelay.Stop(); sensitivityDelay.Stop(); previewCancellation?.Cancel(); streamCancellation?.Cancel(); detectionCancellation?.Cancel(); };
        RefreshGrid(0);
        UpdateReapplyDetectionButton();
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
        MarkDetectionManuallyEdited();
        timeline.Invalidate();
        RefreshGrid(index + 1);
    }

    void RemoveDivider()
    {
        int index = SelectedIndex;
        if (index <= 0 || index >= clips.Count) return;
        clips[index - 1].EndMs = clips[index].EndMs;
        clips[index - 1].DetectionLabels = clips[index - 1].DetectionLabels
            .Concat(clips[index].DetectionLabels).Distinct(StringComparer.Ordinal).ToArray();
        clips[index - 1].Media = null;
        clips.RemoveAt(index);
        MarkDetectionManuallyEdited();
        timeline.Invalidate();
        RefreshGrid(index - 1);
    }

    void ImportClipSetup()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Import clip setup from a custom show",
            Filter = "Custom show manifest (show.json)|show.json|JSON files (*.json)|*.json",
            FileName = "show.json",
            RestoreDirectory = true
        };
        string? showsFolder = configuration == null ? null :
            Path.Combine(configuration.LibraryRoot, "shows");
        if (showsFolder != null && Directory.Exists(showsFolder))
            dialog.InitialDirectory = showsFolder;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(dialog.FileName));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("clips", out JsonElement clipElement))
                throw new InvalidDataException("The selected JSON file has no clips array.");
            CustomShowClip[] source = clipElement.Deserialize<CustomShowClip[]>(
                CustomShowStore.JsonOptions) ??
                throw new InvalidDataException("The selected clips array is empty.");
            CustomShowClip[] imported = PrepareImportedClips(
                source, durationMs, frameDurationMs);

            clips.Clear();
            clips.AddRange(imported);
            clipDetection = null;
            reapplyDetection.Visible = false;
            timeline.PositionMs = 0;
            timeline.Invalidate();
            RefreshGrid(0);
            UpdateReapplyDetectionButton();
            UpdatePosition();
            RequestPreview();
            MessageBox.Show(this,
                $"Imported {clips.Count} clip segments. Generated media references " +
                "from the original show were not copied.",
                "Clip Setup Imported", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, "The clip setup could not be imported.\n\n" + error.Message,
                "Import Clip Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void RefreshGrid(int selected)
    {
        loadingMetadata = true;
        grid.Rows.Clear();
        for (int i = 0; i < clips.Count; i++)
        {
            CustomShowClip clip = clips[i];
            if (!showSkipped.Checked && !clip.Included) continue;
            int rowIndex = grid.Rows.Add(i + 1, clip.Included ? "Clip" : "Skip",
                string.Join(", ", clip.DetectionLabels),
                Format(clip.StartMs), Format(clip.EndMs),
                Format(clip.EndMs - clip.StartMs), clip.Hotness,
                string.Join(", ", clip.ClipTypes),
                clip.Included && clip.Media != null
                    ? clip.AlphaThreshold.ToString(CultureInfo.InvariantCulture)
                    : "",
                clip.Included && clip.Media != null
                    ? clip.EdgeChokePixels.ToString("0.##",
                        CultureInfo.InvariantCulture)
                    : "");
            grid.Rows[rowIndex].Tag = i;
        }
        DataGridViewRow? selectedRow = RowForClip(selected) ??
            grid.Rows.Cast<DataGridViewRow>().FirstOrDefault();
        grid.ClearSelection();
        if (selectedRow != null) selectedRow.Selected = true;
        loadingMetadata = false;
        LoadSelectedMetadata();
    }

    int SelectedIndex => grid.SelectedRows.Count == 0 ? -1 :
        (int?)grid.SelectedRows[0].Tag ?? -1;
    int[] SelectedIndexes => grid.SelectedRows.Cast<DataGridViewRow>()
        .Select(row => (int?)row.Tag ?? -1)
        .Where(index => index >= 0 && index < clips.Count)
        .Order().ToArray();

    DataGridViewRow? RowForClip(int clipIndex) => grid.Rows
        .Cast<DataGridViewRow>().FirstOrDefault(row =>
            (int?)row.Tag == clipIndex);

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
        LoadSelectedAlphaThreshold(selected);
        LoadSelectedEdgeChoke(selected);
        loadingMetadata = false;
        hotness.Enabled = clipTypes.Enabled = true;
    }

    void LoadSelectedAlphaThreshold(CustomShowClip[]? selected = null)
    {
        selected ??= SelectedIndexes.Select(index => clips[index]).ToArray();
        bool enabled = usesPerClipMedia && selected.Length > 0 &&
            selected.All(clip => clip.Included && clip.Media != null);
        alphaThreshold.Enabled = alphaThresholdText.Enabled = enabled;
        if (!enabled)
        {
            alphaThresholdText.Text = "";
            return;
        }
        int[] values = selected.Select(clip => clip.AlphaThreshold)
            .Distinct().ToArray();
        alphaThreshold.Value = values[0];
        alphaThresholdText.Text = values.Length == 1
            ? values[0].ToString(CultureInfo.InvariantCulture) : "";
    }

    void ApplyAlphaThreshold(int value)
    {
        if (loadingMetadata || !usesPerClipMedia) return;
        int[] indexes = SelectedIndexes;
        if (indexes.Length == 0 || indexes.Any(index =>
                !clips[index].Included || clips[index].Media == null))
            return;
        value = Math.Clamp(value, alphaThreshold.Minimum,
            alphaThreshold.Maximum);
        loadingMetadata = true;
        alphaThreshold.Value = value;
        alphaThresholdText.Text = value.ToString(CultureInfo.InvariantCulture);
        foreach (int index in indexes)
        {
            clips[index].AlphaThreshold = value;
            UpdateGridMetadata(index);
        }
        loadingMetadata = false;
    }

    void LoadSelectedEdgeChoke(CustomShowClip[]? selected = null)
    {
        selected ??= SelectedIndexes.Select(index => clips[index]).ToArray();
        bool enabled = usesPerClipMedia && selected.Length > 0 &&
            selected.All(clip => clip.Included && clip.Media != null);
        edgeChoke.Enabled = edgeChokeText.Enabled = enabled;
        if (!enabled)
        {
            edgeChokeText.Text = "";
            return;
        }
        float[] values = selected.Select(clip => clip.EdgeChokePixels)
            .Distinct().ToArray();
        edgeChoke.Value = EdgeCleanupStep(values[0]);
        edgeChokeText.Text = values.Length == 1
            ? EdgeCleanupText(values[0]) : "";
    }

    void ApplyEdgeChoke(float pixels)
    {
        if (loadingMetadata || !usesPerClipMedia) return;
        int[] indexes = SelectedIndexes;
        if (indexes.Length == 0 || indexes.Any(index =>
                !clips[index].Included || clips[index].Media == null))
            return;
        pixels = Math.Clamp(MathF.Round(pixels * 4) / 4, 0, 4);
        loadingMetadata = true;
        edgeChoke.Value = EdgeCleanupStep(pixels);
        edgeChokeText.Text = EdgeCleanupText(pixels);
        foreach (int index in indexes)
        {
            clips[index].EdgeChokePixels = pixels;
            UpdateGridMetadata(index);
        }
        loadingMetadata = false;
    }

    static int EdgeCleanupStep(float pixels) =>
        Math.Clamp((int)MathF.Round(pixels * 4), 0, 16);

    static float EdgeCleanupPixels(int step) =>
        Math.Clamp(step, 0, 16) / 4f;

    static string EdgeCleanupText(float pixels) =>
        Math.Clamp(pixels, 0, 4).ToString("0.##",
            CultureInfo.CurrentCulture);

    void SaveSelectedMetadata()
    {
        if (loadingMetadata) return;
        int[] indexes = SelectedIndexes;
        if (indexes.Length == 0) return;
        foreach (int index in indexes)
        {
            if (include.CheckState != CheckState.Indeterminate)
            {
                bool included = include.CheckState == CheckState.Checked;
                if (clips[index].Included != included) MarkDetectionManuallyEdited();
                clips[index].Included = included;
            }
            if (hotness.SelectedItem is string selectedHotness)
                clips[index].Hotness = selectedHotness;
            UpdateGridMetadata(index);
        }
        if (!showSkipped.Checked && indexes.Any(index => !clips[index].Included))
            RefreshGrid(ClipIndexAt(clips, timeline.PositionMs));
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
        DataGridViewRow? row = RowForClip(index);
        if (row == null)
            return;
        row.Cells[1].Value = clips[index].Included ? "Clip" : "Skip";
        row.Cells[2].Value = string.Join(", ", clips[index].DetectionLabels);
        row.Cells[6].Value = clips[index].Hotness;
        row.Cells[7].Value = string.Join(", ", clips[index].ClipTypes);
        row.Cells[8].Value = clips[index].Included &&
            clips[index].Media != null
                ? clips[index].AlphaThreshold.ToString(CultureInfo.InvariantCulture)
                : "";
        row.Cells[9].Value = clips[index].Included &&
            clips[index].Media != null
                ? clips[index].EdgeChokePixels.ToString("0.##",
                    CultureInfo.InvariantCulture)
                : "";
    }

    void UpdateGridTimes()
    {
        for (int i = 0; i < clips.Count; i++)
        {
            DataGridViewRow? row = RowForClip(i);
            if (row == null) continue;
            row.Cells[3].Value = Format(clips[i].StartMs);
            row.Cells[4].Value = Format(clips[i].EndMs);
            row.Cells[5].Value = Format(clips[i].EndMs - clips[i].StartMs);
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

    static string DetectorId(string? label) => label switch
    {
        string value when value.StartsWith("Accurate", StringComparison.Ordinal) => "transnetv2",
        string value when value.StartsWith("Modern/Quality", StringComparison.Ordinal) => "omnishotcut",
        _ => "ffmpeg"
    };

    internal static double DetectionThreshold(int sensitivityPercent) =>
        1 - Math.Clamp(sensitivityPercent, 1, 99) / 100d;

    internal static bool DetectionSettingsChanged(CustomShowClipDetection detection,
        long transitionBufferMs, long minimumClipMs) =>
        detection.TransitionBufferMs != transitionBufferMs ||
        detection.MinimumClipMs != minimumClipMs;

    void SaveDetectorPreference()
    {
        if (configuration == null || detector.SelectedItem == null) return;
        string selected = DetectorId(detector.SelectedItem.ToString());
        if (configuration.LastClipDetector == selected) return;
        configuration.LastClipDetector = selected;
        configuration.Save();
    }

    bool DetectorSupportsSensitivity() => detector.SelectedItem != null;

    void LoadDetectorSensitivity(CustomShowClipDetection? existing)
    {
        string method = DetectorId(detector.SelectedItem?.ToString());
        sensitivity.Maximum = method == "omnishotcut" ? 100 : 99;
        int value = existing?.Method == method && existing.SensitivityPercent is int saved
            ? saved : method == "ffmpeg"
                ? configuration?.FastClipDetectionSensitivity ?? 65
                : method == "transnetv2"
                    ? configuration?.TransNetClipDetectionSensitivity ?? 50
                    : configuration?.OmniShotCutClipDetectionSensitivity ?? 100;
        applyingSensitivity = true;
        sensitivity.Value = Math.Clamp(value, sensitivity.Minimum,
            sensitivity.Maximum);
        sensitivityBeforeChange = sensitivity.Value;
        sensitivityLabel.Text = $"Sensitivity: {sensitivity.Value}%";
        applyingSensitivity = false;
        bool visible = detector.Visible && DetectorSupportsSensitivity();
        sensitivityLabel.Visible = sensitivity.Visible = visible;
    }

    void SaveDetectorSensitivity()
    {
        if (configuration == null || !DetectorSupportsSensitivity()) return;
        if (DetectorId(detector.SelectedItem?.ToString()) == "ffmpeg")
            configuration.FastClipDetectionSensitivity = (int)sensitivity.Value;
        else if (DetectorId(detector.SelectedItem?.ToString()) == "transnetv2")
            configuration.TransNetClipDetectionSensitivity = (int)sensitivity.Value;
        else configuration.OmniShotCutClipDetectionSensitivity =
            (int)sensitivity.Value;
        configuration.Save();
    }

    void SensitivityPreview(object? sender, EventArgs e)
    {
        if (applyingSensitivity) return;
        sensitivityLabel.Text = $"Sensitivity: {sensitivity.Value}%";
        sensitivityDelay.Stop();
        if (HasReusableSensitivityData() && clipDetection?.ManuallyEdited != true)
            sensitivityDelay.Start();
    }

    void SensitivityCommit(object? sender, EventArgs e)
    {
        sensitivityDelay.Stop();
        ApplySensitivityChange(allowConfirmation: true);
    }

    void ApplySensitivityChange(bool allowConfirmation)
    {
        if (applyingSensitivity) return;
        int requested = sensitivity.Value;
        if (requested == sensitivityBeforeChange) return;
        sensitivityLabel.Text = $"Sensitivity: {requested}%";
        if (clipDetection?.ManuallyEdited == true && HasReusableSensitivityData())
        {
            if (!allowConfirmation) return;
            DialogResult answer = MessageBox.Show(this,
                "Changing sensitivity will recalculate the detected clips and lose " +
                "divider and included/skipped changes made since the last detection.\n\n" +
                "Continue?", "Change Detection Sensitivity", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                applyingSensitivity = true;
                sensitivity.Value = sensitivityBeforeChange;
                sensitivityLabel.Text = $"Sensitivity: {sensitivity.Value}%";
                applyingSensitivity = false;
                return;
            }
        }
        SaveDetectorSensitivity();
        if (HasReusableSensitivityData()) ApplyRetainedSensitivity(requested);
        else if (clipDetection != null && string.Equals(clipDetection.Method,
                     DetectorId(detector.SelectedItem?.ToString()),
                     StringComparison.Ordinal))
            MessageBox.Show(this,
                "This detection was created before reusable detector scores were " +
                "saved. Run Auto-detect clips once to apply the new sensitivity. " +
                "Later changes will apply without rerunning detection.",
                "Detection Sensitivity", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        sensitivityBeforeChange = requested;
    }

    bool HasReusableSensitivityData() => clipDetection != null &&
        string.Equals(clipDetection.Method,
            DetectorId(detector.SelectedItem?.ToString()), StringComparison.Ordinal) &&
        HasReusableDetectionData();

    bool HasReusableDetectionData() => clipDetection != null &&
        !string.IsNullOrWhiteSpace(clipDetection.SensitivityDataFormat) &&
        !string.IsNullOrWhiteSpace(clipDetection.SensitivityData);

    long RequestedTransitionBufferMs() => skipTransitions.Checked
        ? checked((long)Math.Round(transitionSeconds.Value * 1000)) : 0;

    long RequestedMinimumClipMs() => checked((long)Math.Round(
        shortClipSeconds.Value * 1000));

    void UpdateReapplyDetectionButton()
    {
        reapplyDetection.Enabled = reapplyDetection.Visible && clipDetection != null &&
            DetectionSettingsChanged(clipDetection, RequestedTransitionBufferMs(),
                RequestedMinimumClipMs());
    }

    void ReapplyDetectionSettings()
    {
        if (clipDetection == null || !HasReusableDetectionData()) return;
        SaveSelectedMetadata();
        if (clipDetection.ManuallyEdited && MessageBox.Show(this,
                "Reapplying the retained detection will replace divider positions, " +
                "added or removed segments, and included/skipped changes made since " +
                "the last detection. The video will not be analysed again.\n\nContinue?",
                "Reapply Detected Clips", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        int retainedSensitivity = clipDetection.SensitivityPercent ??
            (clipDetection.Method == "ffmpeg" ? 65 :
             clipDetection.Method == "transnetv2" ? 50 : 100);
        RebuildRetainedClips(retainedSensitivity, RequestedTransitionBufferMs(),
            RequestedMinimumClipMs());
    }

    void ApplyRetainedSensitivity(int sensitivityPercent)
    {
        if (clipDetection == null) return;
        RebuildRetainedClips(sensitivityPercent, clipDetection.TransitionBufferMs,
            clipDetection.MinimumClipMs);
    }

    void RebuildRetainedClips(int sensitivityPercent, long bufferMs,
        long minimumClipMs)
    {
        if (clipDetection == null) return;
        SceneDetectionResult result;
        try { result = RebuildDetection(clipDetection, sensitivityPercent); }
        catch (Exception error)
        {
            MessageBox.Show(this, "The retained detector data could not be reapplied. " +
                "Run Auto-detect clips again.\n\n" + error.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        StopPlayback(requestPreview: false);
        CustomShowClip[] previous = clips.Select(Clone).ToArray();
        CustomShowClip seed = Clone(clips.FirstOrDefault(clip => clip.Included) ?? clips[0]);
        int selected = ClipIndexAt(clips, timeline.PositionMs);
        CustomShowClip[] rebuilt = BuildDetectedClips(seed, result, durationMs,
            bufferMs, minimumClipMs);
        foreach (CustomShowClip clip in rebuilt)
        {
            long midpoint = clip.StartMs + (clip.EndMs - clip.StartMs) / 2;
            CustomShowClip? metadata = previous.FirstOrDefault(value =>
                midpoint >= value.StartMs && midpoint < value.EndMs);
            if (metadata == null) continue;
            clip.Hotness = metadata.Hotness;
            clip.ClipTypes = [.. metadata.ClipTypes];
            clip.AlphaThreshold = metadata.AlphaThreshold;
            clip.EdgeChokePixels = metadata.EdgeChokePixels;
        }
        clips.Clear();
        clips.AddRange(rebuilt);
        clipDetection.SensitivityPercent = sensitivityPercent;
        clipDetection.TransitionBufferMs = bufferMs;
        clipDetection.MinimumClipMs = minimumClipMs;
        clipDetection.ManuallyEdited = false;
        timeline.Invalidate();
        RefreshGrid(Math.Clamp(selected, 0, clips.Count - 1));
        UpdateReapplyDetectionButton();
        RequestPreview();
    }

    void MarkDetectionManuallyEdited()
    {
        if (clipDetection != null) clipDetection.ManuallyEdited = true;
    }

    async Task AutoDetectAsync()
    {
        string detectorId = DetectorId(detector.SelectedItem?.ToString());
        if (detectorId != "ffmpeg" && configuration == null) return;
        if (clips.Count > 1 && MessageBox.Show(this,
                "Replace the current dividers with automatically detected scene changes?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        StopPlayback(requestPreview: false);
        SaveSelectedMetadata();
        autoDetect.Enabled = reapplyDetection.Enabled = detector.Enabled = skipTransitions.Enabled =
            transitionSeconds.Enabled = shortClipSeconds.Enabled = importSetup.Enabled = addDivider.Enabled =
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
            SceneDetectionResult result = detectorId switch
            {
                "transnetv2" => await CustomSceneDetector.DetectTransNetAsync(configuration!,
                    videoPath, (int)sensitivity.Value, progress, detectionCancellation.Token),
                "omnishotcut" => await CustomSceneDetector.DetectOmniShotCutAsync(configuration!,
                    videoPath, (int)sensitivity.Value, progress,
                    detectionCancellation.Token),
                _ => await CustomSceneDetector.DetectFastAsync(videoPath, durationMs,
                    (int)sensitivity.Value, progress, detectionCancellation.Token)
            };
            long[] validDividers = result.BoundariesMs
                .Where(value => value > 250 && value < durationMs - 250)
                .Distinct().Order().ToArray();
            SceneDetectionRange[] validRanges = result.Ranges.Where(range =>
                range.EndMs > range.StartMs && range.EndMs > 0 && range.StartMs < durationMs)
                .Select(range => range with { StartMs = Math.Clamp(range.StartMs, 0, durationMs),
                    EndMs = Math.Clamp(range.EndMs, 0, durationMs) }).ToArray();
            result = result with { BoundariesMs = validDividers, Ranges = validRanges };
            if (validDividers.Length == 0 && !validRanges.Any(range =>
                    range.IntraLabel != "General"))
            {
                MessageBox.Show(this, "No scene changes were found.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            CustomShowClip seed = Clone(clips.FirstOrDefault(clip => clip.Included) ?? clips[0]);
            long bufferMs = RequestedTransitionBufferMs();
            long minimumClipMs = RequestedMinimumClipMs();
            CustomShowClip[] detected = BuildDetectedClips(
                seed, result, durationMs, bufferMs, minimumClipMs);
            clips.Clear();
            clips.AddRange(detected);
            clipDetection = new()
            {
                Method = result.Method,
                ToolRevision = result.ToolRevision,
                OverlapFrames = result.OverlapFrames,
                SensitivityPercent = DetectorSupportsSensitivity()
                    ? (int)sensitivity.Value : null,
                SensitivityDataFormat = result.SensitivityDataFormat,
                SensitivityData = result.SensitivityData,
                DetectionFrameRate = result.DetectionFrameRate,
                TransitionBufferMs = bufferMs,
                MinimumClipMs = minimumClipMs,
                ManuallyEdited = false
            };
            timeline.PositionMs = validDividers.FirstOrDefault(
                validRanges.FirstOrDefault()?.StartMs ?? 0);
            timeline.Invalidate();
            RefreshGrid(0);
            reapplyDetection.Visible = HasReusableDetectionData();
            UpdateReapplyDetectionButton();
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
                    shortClipSeconds.Enabled = importSetup.Enabled = addDivider.Enabled =
                    timeline.Enabled = true;
                transitionSeconds.Enabled = skipTransitions.Checked;
                removeDivider.Enabled = SelectedIndex > 0;
                UpdateReapplyDetectionButton();
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
        SelectClipAtTimelinePosition();
    }

    void SelectClipAtTimelinePosition()
    {
        int index = ClipIndexAt(clips, timeline.PositionMs);
        if (index < 0 || index == SelectedIndex) return;
        DataGridViewRow? row = RowForClip(index);
        if (row == null) return;
        grid.ClearSelection();
        row.Selected = true;
        if (!row.Displayed)
            grid.FirstDisplayedScrollingRowIndex = row.Index;
    }

    void SeekToSegment(int index, bool seekToEnd)
    {
        if (index < 0 || index >= grid.Rows.Count) return;
        int clipIndex = (int?)grid.Rows[index].Tag ?? -1;
        if (clipIndex < 0 || clipIndex >= clips.Count) return;
        StopPlayback(requestPreview: false);
        grid.ClearSelection();
        grid.Rows[index].Selected = true;
        timeline.PositionMs = SegmentSeekPosition(clips[clipIndex], seekToEnd,
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
        HoldRepeatButton.SchedulePreview(previewDelay);
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
            await using ProcessCancellationScope cancellationScope = new(process, token);
            _ = process.StandardError.ReadToEndAsync(token);
            byte[] frame = new byte[640 * 360 * 4];
            while (!token.IsCancellationRequested)
            {
                await process.StandardOutput.BaseStream.ReadExactlyAsync(frame, token);
                if (!IsHandleCreated || IsDisposed) break;
                preview.SetSourceBgraOwned(frame, 640, 360);
                frame = new byte[640 * 360 * 4];
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

    async Task LoadPreviewAsync(long atMs)
    {
        previewCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        previewCancellation = cancellation;
        try
        {
            ProcessStartInfo start = PreviewProcessStart(
                PreviewFramePosition(atMs, frameDurationMs, durationMs));
            foreach (string argument in new[] { "-i", videoPath,
                "-frames:v", "1", "-vf",
                "scale=960:540:force_original_aspect_ratio=decrease,pad=960:540:(ow-iw)/2:(oh-ih)/2",
                "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1" })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ??
                throw new InvalidOperationException("FFmpeg did not start.");
            await using ProcessCancellationScope cancellationScope =
                new(process, cancellation.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellation.Token);
            using MemoryStream bytes = new();
            await process.StandardOutput.BaseStream.CopyToAsync(bytes, cancellation.Token);
            await process.WaitForExitAsync(cancellation.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
            bytes.Position = 0;
            using Image image = Image.FromStream(bytes);
            Bitmap bitmap = new(image);
            if (cancellation.IsCancellationRequested) { bitmap.Dispose(); return; }
            preview.SetSourceOwned(bitmap);
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
        EdgeChokePixels = clip.EdgeChokePixels,
        ClipTypes = [.. clip.ClipTypes],
        DetectionLabels = [.. clip.DetectionLabels],
        Source = clip.Source == null ? null : new()
        {
            Mode = clip.Source.Mode,
            Path = clip.Source.Path
        },
        SourceStartMs = clip.SourceStartMs,
        SourceEndMs = clip.SourceEndMs,
        Media = clip.Media == null ? null : new()
        {
            Foreground = clip.Media.Foreground, Alpha = clip.Media.Alpha,
            Width = clip.Media.Width, Height = clip.Media.Height,
            FrameRate = clip.Media.FrameRate, DurationMs = clip.Media.DurationMs
        }
    };

    internal static CustomShowClip[] PrepareImportedClips(
        IReadOnlyList<CustomShowClip> source, long durationMs, double frameDurationMs)
    {
        if (source.Count == 0)
            throw new InvalidDataException("The selected show has no clip setup.");
        if (source.Any(clip => clip.ClipTypes == null || clip.DetectionLabels == null))
            throw new InvalidDataException("The selected show has invalid clip metadata.");
        long toleranceMs = Math.Max(1, checked((long)Math.Ceiling(frameDurationMs)));
        if (Math.Abs((double)source[^1].EndMs - durationMs) > toleranceMs)
            throw new InvalidDataException(
                $"The saved setup is {Format(source[^1].EndMs)} long, but the current " +
                $"video is {Format(durationMs)} long. Choose a show created from the same video.");

        CustomShowClip[] imported = source.Select(Clone).ToArray();
        foreach (CustomShowClip clip in imported)
        {
            clip.Id = Guid.NewGuid().ToString("N");
            clip.Source = null;
            clip.SourceStartMs = null;
            clip.SourceEndMs = null;
            clip.Media = null;
        }
        imported[^1].EndMs = durationMs;
        CustomShowStore.ValidateClips(imported, durationMs);
        return imported;
    }

    static CustomShowClipDetection? CloneDetection(CustomShowClipDetection? value) =>
        value == null ? null : new()
        {
            Method = value.Method,
            ToolRevision = value.ToolRevision,
            OverlapFrames = value.OverlapFrames,
            SensitivityPercent = value.SensitivityPercent,
            SensitivityDataFormat = value.SensitivityDataFormat,
            SensitivityData = value.SensitivityData,
            DetectionFrameRate = value.DetectionFrameRate,
            TransitionBufferMs = value.TransitionBufferMs,
            MinimumClipMs = value.MinimumClipMs,
            ManuallyEdited = value.ManuallyEdited
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
        IEnumerable<long> dividers, long durationMs, long bufferMs,
        long minimumClipMs = 10_000)
        => BuildDetectedClips(seed, new SceneDetectionResult("ffmpeg",
            dividers.ToArray(), []), durationMs, bufferMs, minimumClipMs);

    internal static CustomShowClip[] BuildDetectedClips(CustomShowClip seed,
        SceneDetectionResult detection, long durationMs, long bufferMs,
        long minimumClipMs = 10_000)
    {
        if (minimumClipMs is < 0 or > 3_600_000)
            throw new ArgumentOutOfRangeException(nameof(minimumClipMs));
        string[] gradual = ["Dissolve", "Wipes", "Push", "Slide", "Zoom", "Fade", "Doorway"];
        string[] intra = ["General", .. gradual, "Padding"];
        string[] inter = ["New_Start", "Hard_Cut", "Transition_Source", "Transition",
            "Sudden_Jump", "Padding"];
        if (detection.Ranges.Any(range => !intra.Contains(range.IntraLabel) ||
                !inter.Contains(range.InterLabel)))
            throw new InvalidDataException("OmniShotCut returned an unknown classification label.");

        List<long> cuts = [0, durationMs];
        List<(long Start, long End, string Label, bool Exact)> skipped = [];
        foreach (SceneDetectionRange range in detection.Ranges)
        {
            long start = Math.Clamp(range.StartMs, 0, durationMs);
            long end = Math.Clamp(range.EndMs, 0, durationMs);
            if (end <= start) continue;
            cuts.Add(start);
            cuts.Add(end);
            if (range.IntraLabel == "Padding" || range.InterLabel == "Padding")
            {
                skipped.Add((start, end, "Padding", true));
                continue;
            }
            if (gradual.Contains(range.IntraLabel))
            {
                skipped.Add((start, end, range.IntraLabel, true));
                if (bufferMs > 0)
                    skipped.Add((Math.Max(0, start - bufferMs),
                        Math.Min(durationMs, end + bufferMs), range.IntraLabel + " buffer", false));
            }
            if (range.InterLabel is "Hard_Cut" or "Sudden_Jump" && start > 0)
            {
                cuts.Add(start);
                if (bufferMs > 0)
                    skipped.Add((Math.Max(0, start - bufferMs),
                        Math.Min(durationMs, start + bufferMs),
                        range.InterLabel == "Hard_Cut" ? "Hard Cut buffer" :
                            "Sudden Jump buffer", false));
            }
        }
        foreach (long divider in detection.BoundariesMs.Where(value => value > 0 && value < durationMs))
        {
            if (bufferMs == 0) cuts.Add(divider);
            else
                skipped.Add((Math.Max(0, divider - bufferMs),
                    Math.Min(durationMs, divider + bufferMs), "Scene change buffer", false));
        }
        foreach ((long start, long end, _, _) in skipped)
        {
            cuts.Add(start);
            cuts.Add(end);
        }
        long[] points = cuts.Distinct().Order().ToArray();
        List<CustomShowClip> result = [];
        for (int index = 0; index + 1 < points.Length; index++)
        {
            long start = points[index], end = points[index + 1];
            if (end <= start) continue;
            long midpoint = start + (end - start) / 2;
            SceneDetectionRange? sourceRange = detection.Ranges.FirstOrDefault(range =>
                midpoint >= range.StartMs && midpoint < range.EndMs &&
                range.IntraLabel != "Padding" && range.InterLabel != "Padding");
            string[] exactLabels = skipped.Where(value => value.Exact &&
                    start >= value.Start && end <= value.End)
                .Select(value => value.Label).Distinct(StringComparer.Ordinal).ToArray();
            string[] bufferLabels = skipped.Where(value => !value.Exact &&
                    start >= value.Start && end <= value.End)
                .Select(value => value.Label).Distinct(StringComparer.Ordinal).ToArray();
            bool isSkipped = exactLabels.Length > 0 || bufferLabels.Length > 0;
            List<string> labels = [];
            if (exactLabels.Length > 0) labels.AddRange(exactLabels);
            else if (bufferLabels.Length > 0) labels.AddRange(bufferLabels);
            else if (sourceRange != null)
            {
                labels.Add(sourceRange.IntraLabel);
                if (sourceRange.InterLabel == "Hard_Cut") labels.Add("Hard Cut");
                if (sourceRange.InterLabel == "Sudden_Jump") labels.Add("Sudden Jump");
            }
            CustomShowClip clip = Clone(seed);
            clip.Id = Guid.NewGuid().ToString("N");
            clip.StartMs = start;
            clip.EndMs = end;
            clip.Included = !isSkipped && end - start >= minimumClipMs;
            string shortLabel = ShortClipLabel(minimumClipMs);
            if (!isSkipped && end - start < minimumClipMs) labels.Add(shortLabel);
            clip.DetectionLabels = labels.Distinct(StringComparer.Ordinal).ToArray();
            clip.Media = null;
            if (result.Count > 0 && !clip.Included && !result[^1].Included &&
                !clip.DetectionLabels.Contains(shortLabel) &&
                !result[^1].DetectionLabels.Contains(shortLabel))
            {
                result[^1].EndMs = clip.EndMs;
                result[^1].DetectionLabels = result[^1].DetectionLabels
                    .Concat(clip.DetectionLabels).Distinct(StringComparer.Ordinal).ToArray();
            }
            else result.Add(clip);
        }
        return [.. result];
    }

    static string ShortClipLabel(long minimumClipMs) =>
        $"Short (<{minimumClipMs / 1000d:0.###}s)";

    internal static string CompressSensitivityData<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value,
            CustomShowStore.JsonOptions);
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.SmallestSize,
            leaveOpen: true)) gzip.Write(json);
        return Convert.ToBase64String(output.ToArray());
    }

    internal static T DecompressSensitivityData<T>(string value)
    {
        using MemoryStream input = new(Convert.FromBase64String(value));
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(gzip, CustomShowStore.JsonOptions) ??
            throw new InvalidDataException("Retained detector data is empty.");
    }

    internal sealed record FastCandidate(long TimeMs, float Score);
    internal sealed record OmniCandidate(int EndFrame, string Intra, string Inter,
        float Confidence);
    internal sealed record OmniSensitivityData(int TotalFrames, OmniCandidate[] Boundaries);

    static IEnumerable<OmniCandidate> SelectOmniCandidates(
        IReadOnlyList<OmniCandidate> candidates, int sensitivityPercent)
    {
        if (candidates.Count == 0) return [];
        int count = Math.Clamp((int)Math.Ceiling(candidates.Count *
            Math.Clamp(sensitivityPercent, 1, 100) / 100d), 1, candidates.Count);
        HashSet<int> selectedFrames = candidates
            .OrderByDescending(value => value.Confidence)
            .ThenBy(value => value.EndFrame)
            .Take(count)
            .Select(value => value.EndFrame)
            .ToHashSet();
        return candidates.Where(value => selectedFrames.Contains(value.EndFrame));
    }

    internal static SceneDetectionResult RebuildDetection(CustomShowClipDetection detection,
        int sensitivityPercent)
    {
        double threshold = DetectionThreshold(sensitivityPercent);
        if (detection.SensitivityDataFormat == "ffmpeg-scene-gzip-json-v1")
        {
            FastCandidate[] candidates = DecompressSensitivityData<FastCandidate[]>(
                detection.SensitivityData!);
            return new("ffmpeg", candidates.Where(value => value.Score >= threshold)
                .Select(value => value.TimeMs).ToArray(), [],
                SensitivityDataFormat: detection.SensitivityDataFormat,
                SensitivityData: detection.SensitivityData);
        }
        if (detection.SensitivityDataFormat == "transnet-logits-gzip-f32-v1")
        {
            byte[] bytes = DecompressRawSensitivityData(detection.SensitivityData!);
            float[] logits = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, logits, 0, bytes.Length);
            double fps = detection.DetectionFrameRate ??
                throw new InvalidDataException("TransNetV2 frame rate is missing.");
            long[] dividers = CustomSceneDetector.TransNetDividers(logits, fps, threshold);
            return new("transnetv2", dividers, [], detection.ToolRevision,
                SensitivityDataFormat: detection.SensitivityDataFormat,
                SensitivityData: detection.SensitivityData, DetectionFrameRate: fps);
        }
        if (detection.SensitivityDataFormat == "omnishotcut-gzip-json-v1")
        {
            OmniSensitivityData data = DecompressSensitivityData<OmniSensitivityData>(
                detection.SensitivityData!);
            double fps = detection.DetectionFrameRate ??
                throw new InvalidDataException("OmniShotCut frame rate is missing.");
            long start = 0;
            List<SceneDetectionRange> ranges = [];
            foreach (OmniCandidate candidate in SelectOmniCandidates(
                         data.Boundaries, sensitivityPercent))
            {
                long end = checked((long)Math.Round(candidate.EndFrame * 1000 / fps));
                if (end <= start) continue;
                ranges.Add(new(start, end, candidate.Intra, candidate.Inter));
                start = end;
            }
            long duration = checked((long)Math.Round(data.TotalFrames * 1000 / fps));
            if (start < duration) ranges.Add(new(start, duration, "General",
                ranges.Count == 0 ? "New_Start" : "Transition_Source"));
            long[] boundaries = ranges.SelectMany(value => new[]
                { value.StartMs, value.EndMs }).Where(value => value > 0)
                .Distinct().Order().ToArray();
            return new("omnishotcut", boundaries, [.. ranges], detection.ToolRevision,
                detection.OverlapFrames, detection.SensitivityDataFormat,
                detection.SensitivityData, fps);
        }
        throw new InvalidDataException("No reusable detector scores were saved.");
    }

    internal static byte[] DecompressRawSensitivityData(string value)
    {
        using MemoryStream input = new(Convert.FromBase64String(value));
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    internal static long FrameStep(long position, int direction,
        double frameDurationMs, long durationMs) => Math.Clamp(
            (long)Math.Round((Math.Round(position / frameDurationMs) +
                Math.Sign(direction)) * frameDurationMs), 0, durationMs);

    internal static long PreviewFramePosition(long position,
        double frameDurationMs, long durationMs)
    {
        long lastFrame = Math.Max(0, durationMs - Math.Max(1,
            (long)Math.Ceiling(frameDurationMs)));
        return Math.Clamp(position, 0, lastFrame);
    }

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
        CustomShowClip[] sixSecondMinimum = BuildDetectedClips(
            seed, [7_000], 15_000, 0, 6_000);
        CustomShowClip[] eightSecondMinimum = BuildDetectedClips(
            seed, [7_000], 15_000, 0, 8_000);
        SceneDetectionResult omni = new("omnishotcut", [12_000, 14_000],
            [new(0, 12_000, "General", "New_Start"),
             new(12_000, 14_000, "Fade", "Transition"),
             new(14_000, 40_000, "General", "Hard_Cut")], "revision", 20);
        CustomShowClip[] transitionAware = BuildDetectedClips(seed, omni, 40_000, 0);
        CustomShowClip[] transitionBuffered = BuildDetectedClips(seed, omni, 40_000, 1_000);
        SceneDetectionResult generalBoundary = new("omnishotcut", [10_000],
            [new(0, 10_000, "General", "New_Start"),
             new(10_000, 20_000, "General", "Transition_Source")]);
        CustomShowClip[] hardCutBuffered = BuildDetectedClips(seed,
            generalBoundary, 20_000, 1_500, 0);
        FastCandidate[] fastCandidates =
            [new(1_000, .2f), new(2_000, .6f), new(3_000, .9f)];
        CustomShowClipDetection retainedFast = new()
        {
            Method = "ffmpeg", SensitivityDataFormat = "ffmpeg-scene-gzip-json-v1",
            SensitivityData = CompressSensitivityData(fastCandidates),
            TransitionBufferMs = 500, MinimumClipMs = 20_000
        };
        SceneDetectionResult rebuiltFast = RebuildDetection(retainedFast, 65);
        float[] transNetScores = [-1f, -1f, -1f, 1f, -1f];
        byte[] transNetBytes = new byte[transNetScores.Length * sizeof(float)];
        Buffer.BlockCopy(transNetScores, 0, transNetBytes, 0, transNetBytes.Length);
        using MemoryStream transNetCompressed = new();
        using (GZipStream gzip = new(transNetCompressed,
            CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(transNetBytes);
        CustomShowClipDetection retainedTransNet = new()
        {
            Method = "transnetv2", DetectionFrameRate = 10,
            SensitivityDataFormat = "transnet-logits-gzip-f32-v1",
            SensitivityData = Convert.ToBase64String(transNetCompressed.ToArray())
        };
        SceneDetectionResult rebuiltTransNet = RebuildDetection(retainedTransNet, 50);
        OmniSensitivityData omniData = new(300,
            [new(100, "General", "Hard_Cut", .2f),
             new(200, "General", "Hard_Cut", .8f)]);
        CustomShowClipDetection retainedOmni = new()
        {
            Method = "omnishotcut", DetectionFrameRate = 10,
            SensitivityDataFormat = "omnishotcut-gzip-json-v1",
            SensitivityData = CompressSensitivityData(omniData)
        };
        SceneDetectionResult rebuiltOmni = RebuildDetection(retainedOmni, 50);
        CustomShowClip importFirst = new()
        {
            StartMs = 0, EndMs = 400, Included = false,
            Source = new() { Mode = "video", Path = "old.mp4" },
            SourceStartMs = 10, SourceEndMs = 410,
            Media = new() { Foreground = "old-foreground.mp4", Alpha = "old-alpha.mp4" }
        };
        CustomShowClip importSecond = new()
        {
            StartMs = 400, EndMs = 1_000, Hotness = "Topless",
            ClipTypes = ["Standing", "Prop"]
        };
        CustomShowClip[] imported = PrepareImportedClips(
            [importFirst, importSecond], 1_005, 10);
        return first.EndMs == second.StartMs && second.EndMs == 1_000 &&
            first.Id != second.Id && !second.Included &&
            ClipIndexAt([first, second], 399) == 0 &&
            ClipIndexAt([first, second], 400) == 1 &&
            ClipIndexAt([first, second], 1_000) == 1 &&
            FrameStep(1_000, 1, 40, 2_000) == 1_040 &&
            FrameStep(1_000, -1, 40, 2_000) == 960 &&
            PreviewFramePosition(2_000, 40, 2_000) == 1_960 &&
            PreviewFramePosition(1_500, 40, 2_000) == 1_500 &&
            SegmentSeekPosition(first, false, 40, 1_000) == 0 &&
            SegmentSeekPosition(first, true, 40, 1_000) == 360 &&
            SegmentSeekPosition(second, true, 40, 1_000) == 960 &&
            NextIncludeCheckState(CheckState.Unchecked) == CheckState.Checked &&
            NextIncludeCheckState(CheckState.Checked) == CheckState.Unchecked &&
            NextIncludeCheckState(CheckState.Indeterminate) == CheckState.Checked &&
            Math.Abs(DetectionThreshold(65) - .35) < .0001 &&
            Math.Abs(DetectionThreshold(50) - .5) < .0001 &&
            !DetectionSettingsChanged(retainedFast, 500, 20_000) &&
            DetectionSettingsChanged(retainedFast, 1_000, 20_000) &&
            DetectionSettingsChanged(retainedFast, 500, 10_000) &&
            detected.Length == 5 && detected[0].Included &&
            detected[0].StartMs == 0 && detected[0].EndMs == 14_000 &&
            !detected[1].Included && detected[1].EndMs == 16_000 &&
            detected[2].Included && detected[2].EndMs == 29_000 &&
            !detected[3].Included && !detected[4].Included &&
            detected[4].StartMs == 31_000 && detected[4].EndMs == 40_000 &&
            unbuffered.Length == 2 && unbuffered.All(clip => clip.Included) &&
            unbuffered[0].EndMs == 12_000 && unbuffered[1].StartMs == 12_000 &&
            sixSecondMinimum.All(clip => clip.Included) &&
            !eightSecondMinimum[0].Included &&
            eightSecondMinimum[0].DetectionLabels.Contains("Short (<8s)") &&
            eightSecondMinimum[1].Included &&
            transitionAware.Length == 3 && transitionAware[0].Included &&
            !transitionAware[1].Included && transitionAware[1].StartMs == 12_000 &&
            transitionAware[1].EndMs == 14_000 &&
            transitionAware[1].DetectionLabels.SequenceEqual(["Fade"]) &&
            transitionAware[2].Included &&
            transitionAware[2].DetectionLabels.Contains("Hard Cut") &&
            transitionBuffered.First().EndMs == 11_000 &&
            transitionBuffered.Last().StartMs == 15_000 &&
            transitionBuffered.Where(clip => !clip.Included).Any(clip =>
                clip.DetectionLabels.Contains("Fade buffer")) &&
            hardCutBuffered.Length == 3 &&
            hardCutBuffered[0].Included && hardCutBuffered[0].EndMs == 8_500 &&
            !hardCutBuffered[1].Included && hardCutBuffered[1].StartMs == 8_500 &&
            hardCutBuffered[1].EndMs == 11_500 &&
            hardCutBuffered[1].DetectionLabels.Contains("Scene change buffer") &&
            hardCutBuffered[2].Included && hardCutBuffered[2].StartMs == 11_500 &&
            rebuiltFast.BoundariesMs.SequenceEqual([2_000, 3_000]) &&
            rebuiltTransNet.BoundariesMs.SequenceEqual([300]) &&
            rebuiltOmni.Ranges.Length == 2 &&
            rebuiltOmni.Ranges[0].EndMs == 20_000 &&
            rebuiltOmni.Ranges[1].EndMs == 30_000 &&
            imported.Length == 2 && imported[0].EndMs == imported[1].StartMs &&
            imported[1].EndMs == 1_005 && !imported[0].Included &&
            imported[1].Hotness == "Topless" &&
            imported[1].ClipTypes.SequenceEqual(["Standing", "Prop"]) &&
            imported.All(clip => clip.Source == null && clip.SourceStartMs == null &&
                clip.SourceEndMs == null && clip.Media == null) &&
            imported[0].Id != importFirst.Id && imported[1].Id != importSecond.Id &&
            VerifyOmniShotCutPolicies() &&
            CustomSceneDetector.MonotonicProgress(18, 5) == 18 &&
            CustomSceneDetector.MonotonicProgress(18, 20) == 20;
    }

    static bool VerifyOmniShotCutPolicies()
    {
        CustomShowClip seed = new();
        foreach (string label in new[]
            { "Dissolve", "Wipes", "Push", "Slide", "Zoom", "Fade", "Doorway" })
        {
            SceneDetectionResult detection = new("omnishotcut", [12_000, 14_000],
                [new(0, 12_000, "General", "New_Start"),
                 new(12_000, 14_000, label, "Transition"),
                 new(14_000, 30_000, "General", "Transition_Source")]);
            CustomShowClip[] values = BuildDetectedClips(seed, detection, 30_000, 0);
            if (!values.Any(clip => !clip.Included &&
                    clip.DetectionLabels.Contains(label))) return false;
        }
        SceneDetectionResult special = new("omnishotcut", [1_000, 12_000],
            [new(0, 1_000, "Padding", "Padding"),
             new(1_000, 12_000, "General", "New_Start"),
             new(12_000, 30_000, "General", "Sudden_Jump")]);
        CustomShowClip[] specialValues = BuildDetectedClips(seed, special, 30_000, 500);
        return specialValues.Any(clip => !clip.Included &&
                clip.DetectionLabels.Contains("Padding")) &&
            specialValues.Any(clip => !clip.Included &&
                clip.DetectionLabels.Contains("Sudden Jump buffer")) &&
            specialValues.All(clip => clip.EndMs > clip.StartMs);
    }
}

internal static class CustomSceneDetector
{
    static string TransNetWorkerPath => Path.Combine(AppContext.BaseDirectory,
        "custom-shows", "transnetv2_worker.py");
    static string OmniShotCutWorkerPath => Path.Combine(AppContext.BaseDirectory,
        "custom-shows", "omnishotcut_worker.py");

    internal static async Task<SceneDetectionResult> DetectFastAsync(string source, long durationMs,
        int sensitivityPercent, IProgress<int>? progress, CancellationToken token,
        long startMs = 0, long? endMs = null)
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        ProcessStartInfo start = new(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        double threshold = CustomClipEditorForm.DetectionThreshold(sensitivityPercent);
        long boundedEndMs = Math.Clamp(endMs ?? durationMs, startMs + 1, durationMs);
        long boundedDurationMs = boundedEndMs - startMs;
        string sceneFilter = "scale=320:-2:flags=fast_bilinear," +
            "select='gte(scene,0)',metadata=print:key=lavfi.scene_score";
        foreach (string argument in new[] { "-v", "info", "-nostats",
            "-ss", (startMs / 1000d).ToString("0.######", CultureInfo.InvariantCulture),
            "-i", source,
            "-t", (boundedDurationMs / 1000d).ToString("0.######", CultureInfo.InvariantCulture),
            "-an", "-vf", sceneFilter,
            "-fps_mode", "vfr", "-f", "null", "-", "-progress", "pipe:1" })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("FFmpeg scene detection could not start.");
        await using ProcessCancellationScope cancellationScope = new(process, token);
        List<CustomClipEditorForm.FastCandidate> candidates = [];
        long? pendingTimeMs = null;
        Task readProgress = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(token) is string line)
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line[12..], out long microseconds) && boundedDurationMs > 0)
                    progress?.Report((int)Math.Clamp(
                        microseconds / 1000d / boundedDurationMs * 100, 0, 99));
        }, token);
        StringBuilder errors = new();
        Task readScenes = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(token) is string line)
            {
                int timeMarker = line.IndexOf("pts_time:", StringComparison.Ordinal);
                if (timeMarker >= 0)
                {
                    string value = line[(timeMarker + 9)..].Split(' ',
                        StringSplitOptions.RemoveEmptyEntries)[0];
                    if (double.TryParse(value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double seconds))
                        pendingTimeMs = checked((long)Math.Round(seconds * 1000));
                    continue;
                }
                const string scoreMarker = "lavfi.scene_score=";
                int scoreAt = line.IndexOf(scoreMarker, StringComparison.Ordinal);
                if (scoreAt >= 0 && pendingTimeMs is long at &&
                    float.TryParse(line[(scoreAt + scoreMarker.Length)..].Trim(),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out float score))
                {
                    candidates.Add(new(at, score));
                    pendingTimeMs = null;
                    continue;
                }
                if (errors.Length < 16_384) errors.AppendLine(line);
            }
        }, token);
        await process.WaitForExitAsync(token);
        await Task.WhenAll(readProgress, readScenes);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(errors.ToString().Trim());
        progress?.Report(100);
        List<long> result = [];
        foreach (long cut in candidates.Where(value => value.Score >= threshold)
                     .Select(value => value.TimeMs)
                     .Where(value => value >= 250 && value <= boundedDurationMs - 250)
                     .Distinct().Order())
            if (result.Count == 0 || cut - result[^1] >= 500) result.Add(cut);
        return new SceneDetectionResult("ffmpeg", [.. result], [],
            SensitivityDataFormat: "ffmpeg-scene-gzip-json-v1",
            SensitivityData: CustomClipEditorForm.CompressSensitivityData(
                candidates.Where(value => value.TimeMs >= 250 &&
                    value.TimeMs <= boundedDurationMs - 250).ToArray()));
    }

    internal static async Task<SceneDetectionResult> DetectTransNetAsync(CustomShowConfiguration configuration,
        string source, int sensitivityPercent, IProgress<int>? progress,
        CancellationToken token, long startMs = 0, long? endMs = null)
    {
        if (!File.Exists(configuration.PythonExecutable))
            throw new FileNotFoundException("Configure the custom-show Python environment first.",
                configuration.PythonExecutable);
        if (!File.Exists(TransNetWorkerPath))
            throw new FileNotFoundException("The TransNetV2 worker is missing.", TransNetWorkerPath);
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
        List<string> arguments =
        [
            TransNetWorkerPath, "--source", source, "--runtime", runtime,
            "--batch-size", Math.Clamp(configuration.TransNetPreferredBatchSize, 1, 64)
                .ToString(CultureInfo.InvariantCulture),
            "--compile-cutoff-frames", Math.Max(1, configuration.TransNetCompileCutoffFrames)
                .ToString(CultureInfo.InvariantCulture),
            "--execution-mode", "auto",
            "--threshold-probability", CustomClipEditorForm.DetectionThreshold(sensitivityPercent)
                .ToString("0.00", CultureInfo.InvariantCulture),
            "--decode-mode", configuration.TransNetDecodeMode?.ToLowerInvariant() switch
            {
                "legacy" => "legacy",
                "cpu" => "cpu",
                _ => "auto"
            },
            "--profile-log", Path.Combine(configuration.LibraryRoot, ".logs",
                "transnetv2.ndjson"),
            "--start-ms", Math.Max(0, startMs).ToString(CultureInfo.InvariantCulture)
        ];
        if (endMs is long boundedEndMs)
            arguments.AddRange(["--end-ms", Math.Max(startMs + 1, boundedEndMs)
                .ToString(CultureInfo.InvariantCulture)]);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["IQP_FFMPEG"] = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        start.Environment["IQP_FFPROBE"] = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("Python could not be started.");
        await using ProcessCancellationScope cancellationScope = new(process, token);
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        long[]? dividers = null;
        string? sensitivityDataFormat = null, sensitivityData = null;
        double? detectionFrameRate = null;
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
                if (root.TryGetProperty("sensitivityDataFormat", out JsonElement format))
                    sensitivityDataFormat = format.GetString();
                if (root.TryGetProperty("sensitivityData", out JsonElement data))
                    sensitivityData = data.GetString();
                if (root.TryGetProperty("detectionFrameRate", out JsonElement rate))
                    detectionFrameRate = rate.GetDouble();
            }
            catch (JsonException) { }
        }
        await process.WaitForExitAsync(token);
        string errorText = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText)
                ? $"TransNetV2 failed with exit code {process.ExitCode}." : errorText.Trim());
        return new SceneDetectionResult("transnetv2", dividers ??
            throw new InvalidDataException("TransNetV2 did not return scene boundaries."), [],
            ReadRevision(runtime, "TRANSNETV2_COMMIT"),
            SensitivityDataFormat: sensitivityDataFormat,
            SensitivityData: sensitivityData, DetectionFrameRate: detectionFrameRate);
    }

    internal static async Task<SceneDetectionResult> DetectOmniShotCutAsync(
        CustomShowConfiguration configuration, string source, int sensitivityPercent,
        IProgress<int>? progress, CancellationToken token,
        long startMs = 0, long? endMs = null)
    {
        if (!File.Exists(configuration.PythonExecutable))
            throw new FileNotFoundException("Configure the custom-show Python environment first.",
                configuration.PythonExecutable);
        if (!File.Exists(OmniShotCutWorkerPath))
            throw new FileNotFoundException("The OmniShotCut worker is missing.",
                OmniShotCutWorkerPath);
        string runtime = CustomShowProcessor.RuntimeRoot(configuration);
        if (!CustomShowProcessor.IsOmniShotCutInstalled(configuration))
            throw new InvalidOperationException(
                "OmniShotCut is not installed. Run Install / Update Processing Tools and select OmniShotCut.");
        ProcessStartInfo start = new(configuration.PythonExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        List<string> arguments =
        [
            OmniShotCutWorkerPath, "--source", source, "--runtime", runtime,
            "--sensitivity-percent", Math.Clamp(sensitivityPercent, 1, 100)
                .ToString(CultureInfo.InvariantCulture),
            "--profile-log", Path.Combine(configuration.LibraryRoot, ".logs",
                "omnishotcut.ndjson"),
            "--start-ms", Math.Max(0, startMs).ToString(CultureInfo.InvariantCulture)
        ];
        if (endMs is long boundedEndMs)
            arguments.AddRange(["--end-ms", Math.Max(startMs + 1, boundedEndMs)
                .ToString(CultureInfo.InvariantCulture)]);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["IQP_FFMPEG"] = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        start.Environment["IQP_FFPROBE"] = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("Python could not be started.");
        await using ProcessCancellationScope cancellationScope = new(process, token);
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        SceneDetectionRange[]? ranges = null;
        string? revision = null;
        int? overlap = null;
        string? sensitivityDataFormat = null, sensitivityData = null;
        double? detectionFrameRate = null;
        int displayedProgress = 0;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token) is string line)
            {
                try
                {
                    using JsonDocument json = JsonDocument.Parse(line);
                    JsonElement root = json.RootElement;
                    if (root.TryGetProperty("percent", out JsonElement percent))
                    {
                        int next = MonotonicProgress(displayedProgress, percent.GetDouble());
                        if (next > displayedProgress)
                        {
                            displayedProgress = next;
                            progress?.Report(next);
                        }
                    }
                    if (root.TryGetProperty("revision", out JsonElement revisionValue))
                        revision = revisionValue.GetString();
                    if (root.TryGetProperty("overlapFrames", out JsonElement overlapValue))
                        overlap = overlapValue.GetInt32();
                    if (root.TryGetProperty("sensitivityDataFormat", out JsonElement format))
                        sensitivityDataFormat = format.GetString();
                    if (root.TryGetProperty("sensitivityData", out JsonElement data))
                        sensitivityData = data.GetString();
                    if (root.TryGetProperty("detectionFrameRate", out JsonElement rate))
                        detectionFrameRate = rate.GetDouble();
                    if (!root.TryGetProperty("ranges", out JsonElement rangeValues)) continue;
                    List<SceneDetectionRange> parsed = [];
                    foreach (JsonElement value in rangeValues.EnumerateArray())
                    {
                        string intra = value.GetProperty("intraLabel").GetString() ?? "";
                        string inter = value.GetProperty("interLabel").GetString() ?? "";
                        if (!KnownIntraLabels.Contains(intra) || !KnownInterLabels.Contains(inter))
                            throw new InvalidDataException(
                                "OmniShotCut returned an unknown classification label.");
                        parsed.Add(new(value.GetProperty("startMs").GetInt64(),
                            value.GetProperty("endMs").GetInt64(), intra, inter));
                    }
                    ranges = [.. parsed];
                }
                catch (JsonException errorJson)
                {
                    throw new InvalidDataException("OmniShotCut returned malformed JSON.", errorJson);
                }
            }
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            try { await error; } catch { }
            throw;
        }
        await process.WaitForExitAsync(token);
        string errorText = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText)
                ? $"OmniShotCut failed with exit code {process.ExitCode}." : errorText.Trim());
        if (ranges == null)
            throw new InvalidDataException("OmniShotCut did not return shot ranges.");
        long[] dividers = ranges.SelectMany(value => new[] { value.StartMs, value.EndMs })
            .Where(value => value > 0).Distinct().Order().ToArray();
        progress?.Report(100);
        return new SceneDetectionResult("omnishotcut", dividers, ranges,
            revision ?? ReadRevision(runtime, "OMNISHOTCUT_COMMIT"), overlap ?? 20,
            sensitivityDataFormat, sensitivityData, detectionFrameRate);
    }

    internal static long[] TransNetDividers(float[] predictions, double fps,
        double thresholdProbability)
    {
        double thresholdLogit = Math.Log(thresholdProbability /
            (1 - thresholdProbability));
        List<long> found = [];
        int start = -1;
        for (int index = 0; index < predictions.Length; index++)
        {
            bool transition = predictions[index] > thresholdLogit;
            if (transition && start < 0) start = index;
            if (start >= 0 && (!transition || index == predictions.Length - 1))
            {
                int end = transition ? index + 1 : index;
                int peak = start;
                for (int candidate = start + 1; candidate < end; candidate++)
                    if (predictions[candidate] > predictions[peak]) peak = candidate;
                long at = checked((long)Math.Round(peak * 1000 / fps));
                if (at >= 250 && (found.Count == 0 || at - found[^1] >= 500))
                    found.Add(at);
                start = -1;
            }
        }
        return [.. found];
    }

    static readonly HashSet<string> KnownIntraLabels =
        ["General", "Dissolve", "Wipes", "Push", "Slide", "Zoom", "Fade", "Doorway", "Padding"];
    static readonly HashSet<string> KnownInterLabels =
        ["New_Start", "Hard_Cut", "Transition_Source", "Transition", "Sudden_Jump", "Padding"];

    static string? ReadRevision(string runtime, string marker)
    {
        string path = Path.Combine(runtime, marker);
        if (!File.Exists(path)) return null;
        string value = File.ReadAllText(path).Trim();
        return value.Length == 0 ? null : value;
    }

    internal static int MonotonicProgress(int current, double reported) =>
        Math.Max(Math.Clamp(current, 0, 100),
            (int)Math.Round(Math.Clamp(reported, 0, 100)));
}

internal sealed class ClipTimelineControl : Control
{
    long durationMs, positionMs;
    bool scrubbing;
    readonly ToolTip hoverTip = new() { InitialDelay = 120, ReshowDelay = 0,
        AutoPopDelay = 30_000, ShowAlways = true };
    string? hoverText;
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
        using Pen outline = new(Color.FromArgb(24, 24, 24), 5);
        using Pen pen = new(Color.FromArgb(255, 48, 48), 3);
        e.Graphics.DrawLine(outline, playhead, 1, playhead, bar.Bottom + 3);
        e.Graphics.DrawLine(pen, playhead, 1, playhead, bar.Bottom + 3);
        Point[] head = [new(playhead - 6, 1), new(playhead + 6, 1),
            new(playhead, 9)];
        e.Graphics.FillPolygon(Brushes.Red, head);
        e.Graphics.DrawPolygon(Pens.Black, head);
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
        string text = FormatHoverTime(TimeAt(e.X));
        if (!string.Equals(text, hoverText, StringComparison.Ordinal))
        {
            hoverText = text;
            hoverTip.SetToolTip(this, text);
        }
        if (e.Button == MouseButtons.Left)
        {
            if (draggingDivider > 0) MoveDivider(e.X); else if (scrubbing) SetPosition(e.X);
        }
        else Cursor = AllowDividerDragging && MarkerAt(e.Location) > 0
            ? Cursors.SizeWE : Cursors.Hand;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hoverText = null;
        hoverTip.Hide(this);
        base.OnMouseLeave(e);
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
        PositionMs = TimeAt(x);
    }

    long TimeAt(int x)
    {
        Rectangle bar = BarRectangle;
        return (long)Math.Round(durationMs *
            Math.Clamp((x - bar.Left) / (double)bar.Width, 0, 1));
    }

    static string FormatHoverTime(long milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"
            : $"{time.Minutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) hoverTip.Dispose();
        base.Dispose(disposing);
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
