using System.Drawing.Imaging;
using System.Diagnostics;
using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed class CustomShowEditorForm : Form
{
    static readonly string[] HotnessValues =
        ["Public", "NoNudity", "Topless", "Nudity", "FullNudity", "XXX"];
    static readonly string[] ClipTypeValues =
        ["Standing", "Table", "Behind Table", "Swing", "Cage", "Pole",
         "Glass", "Sign", "Prop", "Full Legs", "Side"];

    readonly CustomShowStore store;
    readonly CustomShowConfiguration configuration;
    readonly string? showId;
    readonly TextBox source = new();
    readonly CheckBox copySource = new() { Text = "Copy original into the show folder" };
    readonly TextBox title = new();
    readonly ComboBox performer = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Button newPerformer = new() { Text = "New model...", AutoSize = true };
    readonly Button editPerformer = new() { Text = "Edit model...", AutoSize = true,
        Enabled = false };
    readonly TextBox description = new() { Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical };
    readonly TextBox tags = new();
    readonly DateTimePicker releaseDate = new() { Format = DateTimePickerFormat.Short };
    readonly DateTimePicker showDate = new() { Format = DateTimePickerFormat.Short, ShowCheckBox = true };
    readonly NumericUpDown ageOverride = new() { Minimum = 18, Maximum = 120, Value = 18 };
    readonly CheckBox useAgeOverride = new() { Text = "Use" };
    readonly NumericUpDown rating = new() { Minimum = 0, Maximum = 5, DecimalPlaces = 1, Increment = .5m };
    readonly CheckBox useRating = new() { Text = "Set" };
    readonly ComboBox hotness = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox exclusive = new() { Text = "Exclusive" };
    readonly NumericUpDown performerCount = new() { Minimum = 1, Maximum = 20, Value = 1 };
    readonly CheckedListBox clipTypes = new() { Height = 90, CheckOnClick = true };
    readonly Button editClips = new() { Text = "Split into clips...", AutoSize = true };
    readonly TextBox cover = new();
    readonly Button coverTitleColor = new() { AutoSize = true,
        BackColor = Color.DeepPink, UseVisualStyleBackColor = false };
    readonly ComboBox preset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox sam2Model = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox mattingDetail = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox sequenceChunk = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox processingDetails = new() { ReadOnly = true, Multiline = true,
        Height = 88, ScrollBars = ScrollBars.Vertical, TabStop = false,
        Text = "Recorded in show.json after processing completes." };
    readonly Button save = new() { Text = "Process and Preview", AutoSize = true };
    readonly Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    List<CustomPerformerProfile> profiles = [];
    CustomPerformerProfile? selectedProfile;
    CustomShowClip[] showClips = [];

    internal CustomShowEditorForm(CustomShowStore store,
        CustomShowConfiguration configuration, string? showId)
    {
        this.store = store;
        this.configuration = configuration;
        this.showId = showId;
        Text = showId == null ? "Create Custom Show" : "Edit Custom Show Metadata";
        ClientSize = new Size(760, 780);
        MinimumSize = new Size(650, 650);
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = true;
        TableLayoutPanel table = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(12)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(table);
        AddFileRow(table, "Source video", source, "Video files|*.mp4;*.mov;*.mkv;*.avi;*.webm|All files|*.*");
        AddRow(table, "", copySource);
        AddRow(table, "Show title", title);
        AddRow(table, "Model profile", performer, Flow(newPerformer, editPerformer));
        AddRow(table, "Description", description);
        AddRow(table, "Tags (comma separated)", tags);
        AddRow(table, "Release date", releaseDate);
        AddRow(table, "Show / recording date", showDate);
        FlowLayoutPanel agePanel = Flow(ageOverride, useAgeOverride);
        AddRow(table, "Age at release override", agePanel);
        FlowLayoutPanel ratingPanel = Flow(rating, useRating);
        AddRow(table, "Official rating (0–5)", ratingPanel);
        hotness.Items.AddRange(HotnessValues);
        hotness.SelectedItem = "NoNudity";
        AddRow(table, "Hotness", hotness);
        AddRow(table, "", exclusive);
        AddRow(table, "Number of performers", performerCount);
        clipTypes.Items.AddRange(ClipTypeValues);
        clipTypes.SetItemChecked(0, true);
        AddRow(table, "Clip types", clipTypes);
        AddRow(table, "Video sections", editClips);
        AddFileRow(table, "Custom cover (optional)", cover,
            "Images|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*");
        AddRow(table, "Auto-cover title colour", coverTitleColor);
        preset.Items.AddRange(["RVM Quality (ResNet50)", "RVM Fast (MobileNetV3)"]);
        if (CustomShowProcessor.IsMatAnyone2Installed(configuration))
            preset.Items.Add("MatAnyone 2 (interactive initial mask)");
        if (CustomShowProcessor.IsVideoMaMaInstalled(configuration))
            preset.Items.Add("VideoMaMa High Quality (SAM2 + diffusion)");
        if (CustomShowProcessor.IsViTMatteSmallInstalled(configuration))
            preset.Items.Add("ViTMatte S (editable SAM2 masks)");
        if (CustomShowProcessor.IsViTMatteBaseInstalled(configuration))
            preset.Items.Add("ViTMatte B (editable SAM2 masks, higher quality)");
        preset.SelectedIndex = 0;
        AddRow(table, "Processing algorithm", preset);
        foreach (string model in CustomShowProcessor.InstalledSam2Models(configuration))
            sam2Model.Items.Add(model switch
            {
                "small" => "Small (balanced)",
                "tiny" => "Tiny (fastest)",
                _ => "Base+ (best robustness)"
            });
        if (sam2Model.Items.Count > 0)
            sam2Model.SelectedIndex = sam2Model.Items.Cast<string>().ToList().FindIndex(
                value => value.StartsWith("Base+", StringComparison.Ordinal)) is int index &&
                index >= 0 ? index : 0;
        AddRow(table, "SAM2 mask model", sam2Model);
        mattingDetail.Items.AddRange([
            "Very Low (256 px)", "Low (384 px)",
            "Standard (512 px - recommended)", "High (768 px)",
            "Very High (1024 px)", "Full resolution (slowest)"]);
        mattingDetail.SelectedIndex = 2;
        AddRow(table, "Matting detail", mattingDetail);
        sequenceChunk.Items.AddRange([1, 2, 3, 4, 6, 8, 12, 16, 24]);
        sequenceChunk.SelectedItem = 12;
        AddRow(table, "Processing batch size", sequenceChunk);
        AddRow(table, "Created using", processingDetails);
        FlowLayoutPanel buttons = Flow(save, cancel);
        AddRow(table, "", buttons);
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += Save;
        preset.SelectedIndexChanged += (_, _) => UpdateProcessingOptions();
        newPerformer.Click += (_, _) => OpenPerformer(null);
        editPerformer.Click += (_, _) => OpenPerformer(selectedProfile);
        editClips.Click += EditClips;
        coverTitleColor.Click += ChooseCoverTitleColor;
        cover.TextChanged += (_, _) => coverTitleColor.Enabled =
            string.IsNullOrWhiteSpace(cover.Text);
        performer.SelectedIndexChanged += (_, _) =>
        {
            selectedProfile = performer.SelectedItem as CustomPerformerProfile;
            editPerformer.Enabled = selectedProfile != null;
        };
        LoadData();
        UpdateProcessingOptions();
        Color selectedCoverColor = coverTitleColor.BackColor;
        AppTheme.Apply(this);
        coverTitleColor.BackColor = selectedCoverColor;
        UpdateColorButton();
    }

    void ChooseCoverTitleColor(object? sender, EventArgs e)
    {
        using ColorDialog dialog = new() { Color = coverTitleColor.BackColor,
            FullOpen = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        coverTitleColor.BackColor = dialog.Color;
        UpdateColorButton();
    }

    void UpdateColorButton()
    {
        Color color = coverTitleColor.BackColor;
        coverTitleColor.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        coverTitleColor.ForeColor = color.GetBrightness() > .55f ? Color.Black : Color.White;
    }

    void UpdateProcessingOptions()
    {
        string selected = SelectedPreset();
        if (showId == null && selected is "quality" or "fast")
        {
            int preferred = selected == "fast" ? configuration.RvmFastPreferredChunk :
                configuration.RvmQualityPreferredChunk;
            sequenceChunk.SelectedItem = sequenceChunk.Items.Cast<int>()
                .OrderBy(value => Math.Abs(value - preferred)).First();
        }
        else if (showId == null && selected is "vitmatte-s" or "vitmatte-b")
        {
            int recommended = selected == "vitmatte-b" ?
                configuration.VitMatteBasePreferredBatchSize :
                configuration.VitMatteSmallPreferredBatchSize;
            int closest = sequenceChunk.Items.Cast<int>()
                .OrderBy(value => Math.Abs(value - recommended)).First();
            sequenceChunk.SelectedItem = closest;
        }
        bool rvm = selected is "quality" or "fast";
        mattingDetail.Enabled = (rvm || selected == "matanyone2") && showId == null;
        sequenceChunk.Enabled = (rvm || UsesSam2(selected)) && showId == null;
        sam2Model.Enabled = (selected == "matanyone2" || UsesSam2(selected)) &&
            showId == null && sam2Model.Items.Count > 0;
    }

    int SelectedMattingResolution() => mattingDetail.SelectedIndex switch
    {
        0 => 256, 1 => 384, 3 => 768, 4 => 1024, 5 => 0, _ => 512
    };

    string SelectedSam2Model()
    {
        string selected = sam2Model.SelectedItem?.ToString() ?? "Base+";
        if (selected.StartsWith("Small", StringComparison.Ordinal)) return "small";
        if (selected.StartsWith("Tiny", StringComparison.Ordinal)) return "tiny";
        return "base-plus";
    }

    string SelectedPreset()
    {
        string selected = preset.SelectedItem?.ToString() ?? "";
        if (selected.StartsWith("RVM Fast", StringComparison.Ordinal)) return "fast";
        if (selected.StartsWith("MatAnyone", StringComparison.Ordinal)) return "matanyone2";
        if (selected.StartsWith("VideoMaMa", StringComparison.Ordinal)) return "videomama";
        if (selected.StartsWith("ViTMatte S", StringComparison.Ordinal)) return "vitmatte-s";
        if (selected.StartsWith("ViTMatte B", StringComparison.Ordinal)) return "vitmatte-b";
        return "quality";
    }

    static bool UsesSam2(string selectedPreset) => selectedPreset is
        "videomama" or "vitmatte-s" or "vitmatte-b";

    void LoadData()
    {
        profiles = store.LoadPerformers().ToList();
        performer.DataSource = null;
        performer.DisplayMember = nameof(CustomPerformerProfile.ModelName);
        performer.DataSource = profiles;
        if (showId == null) return;
        CustomShowManifest show = store.LoadManifest(showId);
        copySource.Enabled = preset.Enabled =
            mattingDetail.Enabled = sequenceChunk.Enabled = false;
        source.Text = SourceVideo(show);
        save.Text = "Save Metadata";
        title.Text = show.Title;
        description.Text = show.Description;
        tags.Text = string.Join(", ", show.Tags);
        releaseDate.Value = show.ReleaseDate.ToDateTime(TimeOnly.MinValue);
        showDate.Checked = show.ShowDate != null;
        if (show.ShowDate is DateOnly date) showDate.Value = date.ToDateTime(TimeOnly.MinValue);
        useAgeOverride.Checked = show.AgeAtReleaseOverride != null;
        if (show.AgeAtReleaseOverride is int age) ageOverride.Value = age;
        useRating.Checked = show.OfficialRating != null;
        if (show.OfficialRating is decimal value) rating.Value = value;
        hotness.SelectedItem = show.Hotness;
        exclusive.Checked = show.Exclusive;
        performerCount.Value = show.PerformerCount;
        coverTitleColor.BackColor = ColorTranslator.FromHtml(
            show.Media.CoverTitleColor);
        coverTitleColor.Enabled = show.Media.AutoGeneratedCover;
        for (int i = 0; i < clipTypes.Items.Count; i++)
            clipTypes.SetItemChecked(i, show.ClipTypes.Contains(clipTypes.Items[i]!.ToString()));
        performer.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == show.PerformerId);
        showClips = show.Clips;
        if (show.Processing is CustomShowProcessing processing)
        {
            processingDetails.Text = DescribeProcessing(processing);
            processingDetails.Visible = true;
        }
        UpdateClipButton();
    }

    static string DescribeProcessing(CustomShowProcessing value)
    {
        string algorithm = value.Algorithm switch
        {
            "quality" => "RVM Quality (ResNet50)",
            "fast" => "RVM Fast (MobileNetV3)",
            "matanyone2" => "MatAnyone 2",
            "videomama" => "VideoMaMa",
            "vitmatte-s" => "ViTMatte S",
            "vitmatte-b" => "ViTMatte B",
            _ => value.Algorithm
        };
        string detail = value.MattingDetailPx == 0 ? "full resolution" :
            $"{value.MattingDetailPx} px";
        string sam2 = value.Sam2Model == null ? "" :
            $"; SAM2 {value.Sam2Model}";
        string effective = value.EffectiveBatchSize is int batch && batch != value.BatchSize ?
            $" (effective {batch})" : "";
        string execution = value.ResolvedExecutionMode == null ? "" :
            $"; {value.ResolvedExecutionMode}";
        string encoder = value.Encoder == null ? "" :
            $"; {value.Encoder} {value.EncoderPreset}";
        return $"{algorithm}; detail {detail}; batch {value.BatchSize}{effective}{sam2}{execution}{encoder}\r\n" +
            $"Processed {value.ProcessedUtc.ToLocalTime():g}; QuickPlayer {value.QuickPlayerVersion}";
    }

    void EditClips(object? sender, EventArgs e)
    {
        string video = source.Text;
        if (showId != null && string.IsNullOrWhiteSpace(video))
            video = SourceVideo(store.LoadManifest(showId));
        if (!File.Exists(video))
        {
            MessageBox.Show(this, showId == null
                    ? "Select a source video first."
                    : "The original source video is no longer available. Clip dividers can only be edited against the original source.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string[] overallTypes = clipTypes.CheckedItems.Cast<object>()
            .Select(item => item.ToString()!).ToArray();
        using CustomClipEditorForm form = new(video, showClips,
            hotness.SelectedItem?.ToString() ?? "NoNudity",
            overallTypes.Length == 0 ? ["Standing"] : overallTypes,
            configuration, allowBoundaryEditing: showId == null);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        showClips = form.Clips;
        UpdateClipButton();
    }

    void UpdateClipButton()
    {
        if (showClips.Length < 2) { editClips.Text = "Split into clips..."; return; }
        int included = showClips.Count(clip => clip.Included);
        int skipped = showClips.Length - included;
        editClips.Text = skipped == 0 ? $"Edit {included} clips..." :
            $"Edit {included} clips ({skipped} skipped)...";
    }

    void OpenPerformer(CustomPerformerProfile? profile)
    {
        using CustomPerformerForm form = new(profile);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        selectedProfile = form.Profile;
        int index = profiles.FindIndex(profile => profile.Id == selectedProfile.Id);
        if (index >= 0) profiles[index] = selectedProfile; else profiles.Add(selectedProfile);
        performer.DataSource = null;
        performer.DisplayMember = nameof(CustomPerformerProfile.ModelName);
        performer.DataSource = profiles.OrderBy(profile => profile.ModelName).ToList();
        performer.SelectedItem = performer.Items.Cast<CustomPerformerProfile>()
            .First(profile => profile.Id == selectedProfile.Id);
    }

    async void Save(object? sender, EventArgs e)
    {
        save.Enabled = false;
        try
        {
            if (selectedProfile == null) throw new InvalidDataException("Select or create a model profile.");
            CustomShowStore.ValidateProfile(selectedProfile);
            CustomShowManifest show = showId == null ? new() : store.LoadManifest(showId);
            ApplyFields(show);
            CustomShowStore.ValidateLink(show, selectedProfile);
            if (showId != null)
            {
                RelinkSource(show);
                store.SavePerformer(selectedProfile);
                string destination = CustomShowStore.ResolveRelative(
                    Path.Combine(store.ShowsFolder, show.Id), show.Media.Cover);
                if (!string.IsNullOrWhiteSpace(cover.Text))
                {
                    SaveCover(cover.Text, destination);
                    show.Media.AutoGeneratedCover = false;
                }
                else if (show.Media.AutoGeneratedCover)
                    await SaveAutoCover(show, destination);
                store.SaveManifest(show);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            if (!File.Exists(source.Text)) throw new FileNotFoundException("Select a source video.", source.Text);
            if (showClips.Length == 0)
            {
                using FfmpegCpuDecoder decoder = new(source.Text, fastDecode: true);
                showClips = [new()
                {
                    StartMs = 0,
                    EndMs = checked((long)Math.Round(decoder.Duration * 1000)),
                    Hotness = show.Hotness,
                    ClipTypes = [.. show.ClipTypes]
                }];
            }
            string staging = Path.Combine(store.Root, ".staging", show.Id);
            await DeleteDirectoryWhenReleasedAsync(staging);
            Directory.CreateDirectory(staging);
            bool discardStaging = false;
            bool discardMaskDraft = false;
            CustomShowMaskDraft? maskDraft = null;
            try
            {
                string input = source.Text;
                if (copySource.Checked)
                {
                    string relative = Path.Combine("source", "original" + Path.GetExtension(source.Text));
                    input = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(input)!);
                    File.Copy(source.Text, input, true);
                    show.Source = new() { Mode = "copy", Path = relative.Replace('\\', '/') };
                }
                else show.Source = new() { Mode = "reference", Path = Path.GetFullPath(source.Text) };
                Dictionary<string, string> initialMasks = [];
                Dictionary<string, long> initialMaskFrames = [];
                Dictionary<string, string> sam2Masks = [];
                string selectedPreset = SelectedPreset();
                string selectedSam2Model = SelectedSam2Model();
                int detail = SelectedMattingResolution();
                string log = Path.Combine(staging, "processing.log");
                if (File.Exists(log)) File.Delete(log);
                if (selectedPreset == "matanyone2" || UsesSam2(selectedPreset))
                {
                    CustomShowClip[] clipsToMask = showClips.Where(clip => clip.Included).ToArray();
                    maskDraft = CustomShowMaskDraft.Open(store, source.Text,
                        selectedPreset, selectedSam2Model, clipsToMask);
                    for (int index = 0; index < clipsToMask.Length; index++)
                    {
                        CustomShowClip clip = clipsToMask[index];
                        string draftClip = maskDraft.ClipFolder(index);
                        using CustomMaskEditorForm maskEditor = new(input, configuration,
                            clip.StartMs, clip.EndMs, clipsToMask.Length == 1 ? null :
                            $"Clip {index + 1} of {clipsToMask.Length}",
                            selectedPreset switch
                            {
                                "videomama" => "VideoMaMa",
                                "vitmatte-s" => "ViTMatte S",
                                "vitmatte-b" => "ViTMatte B",
                                _ => "MatAnyone 2"
                            }, allowFrameSelection: selectedPreset == "matanyone2",
                            sam2Model: selectedSam2Model, draftFolder: draftClip);
                        if (maskEditor.ShowDialog(this) != DialogResult.OK)
                        {
                            discardStaging = true;
                            return;
                        }
                        if (selectedPreset != "matanyone2" &&
                            maskEditor.FrameMs > clip.StartMs)
                        {
                            int clipIndex = Array.FindIndex(showClips,
                                value => value.Id == clip.Id);
                            CustomShowClip skipped = new()
                            {
                                StartMs = clip.StartMs, EndMs = maskEditor.FrameMs,
                                Included = false, Hotness = clip.Hotness,
                                ClipTypes = [.. clip.ClipTypes]
                            };
                            clip.StartMs = maskEditor.FrameMs;
                            showClips = [.. showClips[..clipIndex], skipped, clip,
                                .. showClips[(clipIndex + 1)..]];
                            UpdateClipButton();
                        }
                        string mask = Path.Combine(draftClip, "initial-mask.png");
                        maskEditor.SaveMask(mask);
                        initialMasks[clip.Id] = mask;
                        initialMaskFrames[clip.Id] = maskEditor.FrameMs;
                        if (UsesSam2(selectedPreset))
                        {
                            string maskSequence = Path.Combine(draftClip, "sam2-masks");
                            string correctionState = Path.Combine(draftClip,
                                "sam2-corrections.json");
                            using CustomVideoMaskEditorForm refinement = new(input,
                                configuration, clip.StartMs, clip.EndMs, mask, maskSequence,
                                clipsToMask.Length == 1 ? null :
                                $"Clip {index + 1} of {clipsToMask.Length}",
                                selectedSam2Model, log, correctionState);
                            if (refinement.ShowDialog(this) != DialogResult.OK)
                            {
                                discardStaging = true;
                                return;
                            }
                            sam2Masks[clip.Id] = maskSequence;
                        }
                    }
                }
                bool retry;
                do
                {
                    using CustomShowProcessingForm processing = new(async (progress, token) =>
                    {
                        int chunk = (int)(sequenceChunk.SelectedItem ?? 3);
                        CustomShowClip[] included = showClips.Where(clip => clip.Included).ToArray();
                        long totalDuration = included.Sum(clip =>
                            Math.Max(1, clip.EndMs - clip.StartMs));
                        long completedDuration = 0;
                        CustomShowProcessResult? first = null;
                        if (selectedPreset is "quality" or "fast")
                        {
                            CustomShowProcessJob[] jobs = included.Select(clip =>
                                new CustomShowProcessJob(
                                    Path.Combine(staging, "clips", clip.Id),
                                    clip.StartMs, clip.EndMs)).ToArray();
                            first = await CustomShowProcessor.RunAsync(
                                configuration, input, staging, selectedPreset,
                                null, null, detail, chunk, 0, null, log, File.Exists(log),
                                progress, token, jobs);
                            for (int index = 0; index < included.Length; index++)
                            {
                                CustomShowClip clip = included[index];
                                string relativeFolder = Path.Combine("clips", clip.Id);
                                CustomShowProcessResult result = index == 0 ? first :
                                    System.Text.Json.JsonSerializer.Deserialize<CustomShowProcessResult>(
                                        await File.ReadAllTextAsync(Path.Combine(
                                            jobs[index].Output, "result.json"), token),
                                        CustomShowStore.JsonOptions) ?? throw new InvalidDataException(
                                            "The RVM worker did not return clip media information.");
                                clip.Media = new()
                                {
                                    Foreground = Path.Combine(relativeFolder,
                                        "foreground.mp4").Replace('\\', '/'),
                                    Alpha = Path.Combine(relativeFolder,
                                        "alpha.mkv").Replace('\\', '/'),
                                    Width = result.Width, Height = result.Height,
                                    FrameRate = result.FrameRate,
                                    DurationMs = result.DurationMs
                                };
                            }
                            return new CustomShowProcessResult
                            {
                                Width = first.Width, Height = first.Height,
                                FrameRate = first.FrameRate,
                                DurationMs = showClips[^1].EndMs,
                                RequestedSequenceChunk = first.RequestedSequenceChunk,
                                EffectiveSequenceChunk = first.EffectiveSequenceChunk,
                                ExecutionMode = first.ExecutionMode,
                                PipelineDepth = first.PipelineDepth,
                                Encoder = first.Encoder,
                                EncoderPreset = first.EncoderPreset
                            };
                        }
                        for (int index = 0; index < included.Length; index++)
                        {
                            CustomShowClip clip = included[index];
                            int clipIndex = index;
                            long clipDuration = Math.Max(1, clip.EndMs - clip.StartMs);
                            long completedBefore = completedDuration;
                            Progress<CustomShowProgress> aggregate = new(value =>
                                progress.Report(value with
                                {
                                    Percent = CustomShowProcessingForm.AggregateClipPercent(
                                        completedBefore, clipDuration, totalDuration,
                                        value.Percent),
                                    Message = $"Clip {clipIndex + 1}/{included.Length}: {value.Message}"
                                }));
                            string relativeFolder = Path.Combine("clips", clip.Id);
                            string output = Path.Combine(staging, relativeFolder);
                            CustomShowProcessResult result = await CustomShowProcessor.RunAsync(
                                configuration, input, output, selectedPreset,
                                initialMasks.GetValueOrDefault(clip.Id),
                                sam2Masks.GetValueOrDefault(clip.Id), detail, chunk,
                                clip.StartMs, clip.EndMs, log,
                                index > 0 || File.Exists(log), aggregate, token,
                                maskFrameMs: initialMaskFrames.GetValueOrDefault(
                                    clip.Id, clip.StartMs));
                            clip.Media = new()
                            {
                                Foreground = Path.Combine(relativeFolder, "foreground.mp4").Replace('\\', '/'),
                                Alpha = Path.Combine(relativeFolder, "alpha.mkv").Replace('\\', '/'),
                                Width = result.Width, Height = result.Height,
                                FrameRate = result.FrameRate, DurationMs = result.DurationMs
                            };
                            completedDuration += clipDuration;
                            first ??= result;
                        }
                        return new CustomShowProcessResult
                        {
                            Width = first!.Width, Height = first.Height,
                            FrameRate = first.FrameRate, DurationMs = showClips[^1].EndMs
                        };
                    });
                    DialogResult processingResult = processing.ShowDialog(this);
                    if (processingResult != DialogResult.OK || processing.Result == null)
                    {
                        if (processingResult == DialogResult.Cancel)
                        {
                            discardStaging = true;
                            return;
                        }
                        using CustomShowDecisionForm failure = new(
                            Path.Combine(staging, "processing.log"), allowAccept: false);
                        DialogResult failureDecision = failure.ShowDialog(this);
                        if (failureDecision == DialogResult.Retry)
                        {
                            retry = true;
                            continue;
                        }
                        discardStaging = failureDecision == DialogResult.Abort;
                        discardMaskDraft = discardStaging;
                        if (!discardStaging)
                            MessageBox.Show(this,
                                $"Staged files were preserved at:\n{staging}", Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    CustomShowProcessResult media = processing.Result;
                    show.Media = new()
                    {
                        Width = media.Width, Height = media.Height,
                        FrameRate = media.FrameRate, DurationMs = media.DurationMs,
                        AutoGeneratedCover = string.IsNullOrWhiteSpace(cover.Text),
                        CoverTitleColor = ColorHex(coverTitleColor.BackColor)
                    };
                    if (showClips.Length > 0)
                    {
                        showClips[^1].EndMs = media.DurationMs;
                        CustomShowStore.ValidateClips(showClips, media.DurationMs);
                    }
                    show.Clips = showClips;
                    int processingBatchSize = (int)(sequenceChunk.SelectedItem ?? 3);
                    bool usesSam2 = selectedPreset == "matanyone2" || UsesSam2(selectedPreset);
                    show.Processing = new()
                    {
                        Algorithm = selectedPreset,
                        MattingDetailPx = detail,
                        BatchSize = processingBatchSize,
                        Sam2Model = usesSam2 ? selectedSam2Model : null,
                        ExecutionPolicy = "auto",
                        ResolvedExecutionMode = media.ExecutionMode,
                        EffectiveBatchSize = media.EffectiveSequenceChunk,
                        PipelineDepth = media.PipelineDepth,
                        Encoder = media.Encoder,
                        EncoderPreset = media.EncoderPreset,
                        PrecisionPolicy = "fp16-autocast-fp32-cpu-fallback",
                        RecurrentRefinementSteps = selectedPreset == "matanyone2" ? 11 : 0,
                        ProcessedUtc = DateTime.UtcNow,
                        QuickPlayerVersion = Application.ProductVersion,
                        ToolRevisions = ReadProcessingRevisions(selectedPreset),
                        Clips = showClips.Where(clip => clip.Included).Select(clip =>
                            new CustomClipProcessing
                            {
                                ClipId = clip.Id,
                                InitialMaskFrameMs = usesSam2
                                    ? initialMaskFrames.GetValueOrDefault(clip.Id, clip.StartMs)
                                    : null,
                                Sam2MaskTracking = UsesSam2(selectedPreset)
                            }).ToArray()
                    };
                    if (!string.IsNullOrWhiteSpace(cover.Text))
                        SaveCover(cover.Text, Path.Combine(staging, show.Media.Cover));
                    else await CustomShowProcessor.GenerateCoverAsync(input,
                        Path.Combine(staging, show.Media.Cover), show.Media.DurationMs,
                        selectedProfile.ModelName, show.Title, coverTitleColor.BackColor,
                        CancellationToken.None);
                    DialogResult decision = await ReviewProcessedShow(staging,
                        showClips.Where(clip => clip.Included).ToArray());
                    retry = decision == DialogResult.Retry;
                    if (decision == DialogResult.Cancel)
                    {
                        discardStaging = true;
                        discardMaskDraft = true;
                        return;
                    }
                } while (retry);
                store.SavePerformer(selectedProfile);
                store.Publish(staging, show);
                if (maskDraft != null && Directory.Exists(maskDraft.Root))
                    try { await DeleteDirectoryWhenReleasedAsync(maskDraft.Root); }
                    catch (Exception cleanupError)
                    {
                        Debug.WriteLine("Could not remove published custom-show mask draft: " +
                            cleanupError);
                    }
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"{error.Message}\n\nStaged files were preserved at:\n{staging}", error);
            }
            finally
            {
                if (discardStaging && Directory.Exists(staging))
                    try { await DeleteDirectoryWhenReleasedAsync(staging); }
                    catch (Exception cleanupError)
                    {
                        Debug.WriteLine("Could not remove cancelled custom-show staging: " +
                            cleanupError);
                    }
                if (discardMaskDraft && maskDraft != null &&
                    Directory.Exists(maskDraft.Root))
                    try { await DeleteDirectoryWhenReleasedAsync(maskDraft.Root); }
                    catch (Exception cleanupError)
                    {
                        Debug.WriteLine("Could not remove discarded custom-show mask draft: " +
                            cleanupError);
                    }
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { save.Enabled = true; }
        await Task.CompletedTask;
    }

    Dictionary<string, string> ReadProcessingRevisions(string selectedPreset)
    {
        string[] markers = selectedPreset switch
        {
            "quality" or "fast" => ["RVM_COMMIT"],
            "matanyone2" => ["MATANYONE2_COMMIT", "SAM2_COMMIT"],
            "videomama" => ["VIDEOMAMA_COMMIT", "VIDEOMAMA_SVD_REVISION",
                "VIDEOMAMA_MODEL_REVISION", "SAM2_COMMIT"],
            "vitmatte-s" => ["VITMATTE_S_REVISION", "SAM2_COMMIT"],
            "vitmatte-b" => ["VITMATTE_B_REVISION", "SAM2_COMMIT"],
            _ => []
        };
        Dictionary<string, string> revisions = [];
        string runtime;
        try { runtime = CustomShowProcessor.RuntimeRoot(configuration); }
        catch { return revisions; }
        foreach (string marker in markers)
        {
            string path = Path.Combine(runtime, marker);
            if (!File.Exists(path)) continue;
            string value = File.ReadAllText(path).Trim();
            if (value.Length > 0) revisions[marker] = value;
        }
        return revisions;
    }

    internal static async Task DeleteDirectoryWhenReleasedAsync(string path)
    {
        if (!Directory.Exists(path)) return;
        for (int attempt = 0; ; attempt++)
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException &&
                attempt < 100)
            {
                await Task.Delay(100);
            }
    }

    internal static bool VerifyStagingCleanup()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "iqp-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FileStream held = new(Path.Combine(root, "alpha.mkv"), FileMode.Create,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            _ = Task.Run(async () => { await Task.Delay(150); held.Dispose(); });
            DeleteDirectoryWhenReleasedAsync(root).GetAwaiter().GetResult();
            return !Directory.Exists(root);
        }
        finally
        {
            held.Dispose();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    async Task<DialogResult> ReviewProcessedShow(
        string staging, CustomShowClip[] clips)
    {
        List<CustomPlayerForm> previews = [];
        CustomPlayerForm? preview = null;
        string? previewClipId = null;
        try
        {
            using CustomShowDecisionForm review = new(
                Path.Combine(staging, "processing.log"), true, clips);
            review.PreviewChanged += (clip, threshold) =>
            {
                if (previewClipId == clip.Id &&
                    preview is { IsDisposed: false })
                {
                    preview.SetAlphaThreshold(threshold);
                    return;
                }
                preview?.Close();
                preview = new CustomPlayerForm(
                    CustomShowStore.ResolveRelative(
                        staging, clip.Media!.Foreground),
                    CustomShowStore.ResolveRelative(
                        staging, clip.Media.Alpha),
                    alphaThreshold: threshold,
                    fullOpacityThreshold:
                        configuration.FullOpacityThreshold);
                previews.Add(preview);
                previewClipId = clip.Id;
                preview.Show(this);
            };
            return review.ShowDialog(this);
        }
        finally
        {
            await Task.WhenAll(previews.Select(player =>
                player.ClosePlayerAsync()));
            foreach (CustomPlayerForm player in previews) player.Dispose();
        }
    }

    void ApplyFields(CustomShowManifest show)
    {
        show.PerformerId = selectedProfile!.Id;
        show.Title = title.Text.Trim();
        show.Description = description.Text.Trim();
        show.Tags = tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        show.ReleaseDate = DateOnly.FromDateTime(releaseDate.Value);
        show.ShowDate = showDate.Checked ? DateOnly.FromDateTime(showDate.Value) : null;
        show.AgeAtReleaseOverride = selectedProfile.BirthDate == null && useAgeOverride.Checked
            ? (int)ageOverride.Value : null;
        show.OfficialRating = useRating.Checked ? rating.Value : null;
        show.Hotness = hotness.SelectedItem?.ToString() ?? "NoNudity";
        show.Exclusive = exclusive.Checked;
        show.PerformerCount = (int)performerCount.Value;
        show.ClipTypes = clipTypes.CheckedItems.Cast<object>()
            .Select(item => item.ToString()!).ToArray();
        show.Clips = showClips;
        show.Media.CoverTitleColor = ColorHex(coverTitleColor.BackColor);
        if (string.IsNullOrWhiteSpace(show.Title)) throw new InvalidDataException("Show title is required.");
    }

    string SourceVideo(CustomShowManifest show)
    {
        string folder = Path.Combine(store.ShowsFolder, show.Id);
        return show.Source.Mode == "copy"
            ? CustomShowStore.ResolveRelative(folder, show.Source.Path)
            : show.Source.Path;
    }

    void RelinkSource(CustomShowManifest show)
    {
        string selected = source.Text.Trim();
        if (string.IsNullOrWhiteSpace(selected) ||
            SamePath(selected, SourceVideo(show))) return;
        if (!File.Exists(selected))
            throw new FileNotFoundException(
                "Select the renamed or moved original source video.", selected);
        show.Source = new()
        {
            Mode = "reference",
            Path = Path.GetFullPath(selected)
        };
    }

    static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);

    async Task SaveAutoCover(CustomShowManifest show, string destination)
    {
        string video = SourceVideo(show);
        if (!File.Exists(video))
            throw new FileNotFoundException(
                "The original source video is required to regenerate the automatic cover.", video);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp.jpg";
        try
        {
            await CustomShowProcessor.GenerateCoverAsync(video, temporary,
                show.Media.DurationMs, selectedProfile!.ModelName, show.Title,
                coverTitleColor.BackColor, CancellationToken.None);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static string ColorHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    static void SaveCover(string source, string destination)
    {
        using Image image = Image.FromFile(source);
        using Bitmap copy = new(600, 900);
        using (Graphics graphics = Graphics.FromImage(copy))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, CoverDestination(image.Size));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            copy.Save(temporary, ImageFormat.Jpeg);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static RectangleF CoverDestination(Size source)
    {
        float scale = Math.Max(600f / source.Width, 900f / source.Height);
        SizeF size = new(source.Width * scale, source.Height * scale);
        return new RectangleF((600 - size.Width) / 2, (900 - size.Height) / 2,
            size.Width, size.Height);
    }

    internal static bool VerifyCoverCrop() =>
        CoverDestination(new Size(1920, 1080)) ==
            new RectangleF(-500, 0, 1600, 900) &&
        CoverDestination(new Size(600, 900)) ==
            new RectangleF(0, 0, 600, 900) &&
        ColorHex(Color.DeepPink) == "#FF1493" &&
        SamePath(@"C:\video\..\source.mp4", @"c:\source.mp4");

    static void AddRow(TableLayoutPanel table, string label, Control control,
        Control? extra = null)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true,
            Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, row);
        control.Dock = DockStyle.Fill;
        table.Controls.Add(control, 1, row);
        if (extra != null) table.Controls.Add(extra, 2, row);
        else table.SetColumnSpan(control, 2);
    }

    static void AddFileRow(TableLayoutPanel table, string label, TextBox box, string filter)
    {
        Button browse = new() { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using OpenFileDialog dialog = new() { Filter = filter };
            if (dialog.ShowDialog(table.FindForm()) == DialogResult.OK) box.Text = dialog.FileName;
        };
        AddRow(table, label, box, browse);
    }

    static FlowLayoutPanel Flow(params Control[] controls)
    {
        FlowLayoutPanel panel = new() { AutoSize = true, Dock = DockStyle.Fill };
        panel.Controls.AddRange(controls);
        return panel;
    }
}

internal sealed class CustomShowDecisionForm : Form
{
    readonly CustomShowClip[] clips;
    readonly ComboBox clip = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Controls.PlaybackSeekBar threshold = new()
        { Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 10,
          Width = 260, Height = 32, AccessibleName = "Alpha threshold" };
    readonly TextBox thresholdText = new()
        { Width = 48, MaxLength = 3, TextAlign = HorizontalAlignment.Right };
    bool loading;
    internal event Action<CustomShowClip, int>? PreviewChanged;

    internal CustomShowDecisionForm(string logPath, bool allowAccept,
        IReadOnlyList<CustomShowClip>? clips = null)
    {
        this.clips = clips?.ToArray() ?? [];
        Text = allowAccept ? "Review Custom Show" : "Processing Failed";
        ClientSize = new Size(860, allowAccept ? 180 : 130);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        Label message = new()
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = allowAccept
                ? "Tune pixels below the alpha threshold to transparent, then accept, retry, inspect the log, or discard."
                : "Processing failed. Retry, inspect processing.log, or discard the staged show."
        };
        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Bottom, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft
        };
        Button discard = new() { Text = "Discard", AutoSize = true,
            DialogResult = allowAccept ? DialogResult.Cancel : DialogResult.Abort };
        Button openLog = new() { Text = "Open Log", AutoSize = true };
        Button retry = new() { Text = "Retry", AutoSize = true, DialogResult = DialogResult.Retry };
        openLog.Click += (_, _) =>
        {
            if (File.Exists(logPath)) Process.Start(new ProcessStartInfo(logPath)
            { UseShellExecute = true });
        };
        buttons.Controls.AddRange([discard, openLog, retry]);
        if (allowAccept)
        {
            Button accept = new() { Text = "Accept", AutoSize = true, DialogResult = DialogResult.OK };
            buttons.Controls.Add(accept);
            AcceptButton = accept;
        }
        CancelButton = discard;
        Controls.Add(message);
        Controls.Add(buttons);
        if (allowAccept && this.clips.Length > 0)
        {
            FlowLayoutPanel tuning = new()
            {
                Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8),
                FlowDirection = FlowDirection.LeftToRight
            };
            for (int index = 0; index < this.clips.Length; index++)
                clip.Items.Add($"Clip {index + 1}");
            tuning.Controls.AddRange([
                new Label { Text = "Preview", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
                clip,
                new Label { Text = "Alpha threshold (0–255)", AutoSize = true, Margin = new Padding(14, 7, 3, 3) },
                threshold,
                thresholdText
            ]);
            Controls.Add(tuning);
            clip.SelectedIndexChanged += (_, _) => SelectClip();
            threshold.Scroll += (_, _) => ChangeThreshold(threshold.Value);
            thresholdText.TextChanged += (_, _) =>
            {
                if (int.TryParse(thresholdText.Text, out int value))
                    ChangeThreshold(value);
            };
            thresholdText.Leave += (_, _) =>
                thresholdText.Text = threshold.Value.ToString();
            Shown += (_, _) => { if (clip.SelectedIndex < 0) clip.SelectedIndex = 0; };
        }
        AppTheme.Apply(this);
    }

    void SelectClip()
    {
        if (clip.SelectedIndex < 0) return;
        loading = true;
        threshold.Value = clips[clip.SelectedIndex].AlphaThreshold;
        thresholdText.Text = threshold.Value.ToString();
        loading = false;
        PreviewChanged?.Invoke(clips[clip.SelectedIndex], threshold.Value);
    }

    void ChangeThreshold(int value)
    {
        if (loading || clip.SelectedIndex < 0) return;
        value = Math.Clamp(value, threshold.Minimum, threshold.Maximum);
        loading = true;
        threshold.Value = value;
        thresholdText.Text = value.ToString();
        loading = false;
        CustomShowClip selected = clips[clip.SelectedIndex];
        selected.AlphaThreshold = value;
        PreviewChanged?.Invoke(selected, selected.AlphaThreshold);
    }

    internal static bool VerifyAlphaReview()
    {
        CustomShowClip first = new() { AlphaThreshold = 12 };
        CustomShowClip second = new() { AlphaThreshold = 34 };
        using CustomShowDecisionForm form = new("", true, [first, second]);
        CustomShowClip? selected = null;
        form.PreviewChanged += (value, _) => selected = value;
        form.clip.SelectedIndex = 1;
        form.thresholdText.Text = "40";
        return ReferenceEquals(selected, second) && second.AlphaThreshold == 40 &&
            new CustomShowClip().AlphaThreshold == 25;
    }
}

internal sealed class CustomPerformerForm : Form
{
    readonly TextBox name = new();
    readonly DateTimePicker birth = new() { Format = DateTimePickerFormat.Short, ShowCheckBox = true };
    readonly NumericUpDown height = Measure(80, 250);
    readonly NumericUpDown bust = Measure(30, 250);
    readonly NumericUpDown waist = Measure(30, 250);
    readonly NumericUpDown hips = Measure(30, 250);
    readonly CheckBox useHeight = new() { Text = "Set" };
    readonly CheckBox useBust = new() { Text = "Set" };
    readonly CheckBox useWaist = new() { Text = "Set" };
    readonly CheckBox useHips = new() { Text = "Set" };
    readonly TextBox hair = new(), ethnicity = new(), city = new(), country = new();
    internal CustomPerformerProfile Profile { get; private set; }

    internal CustomPerformerForm(CustomPerformerProfile? profile)
    {
        Profile = profile == null ? new() : Clone(profile);
        Text = profile == null ? "New Model Profile" : "Edit Model Profile";
        ClientSize = new Size(500, 470);
        TableLayoutPanel table = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(table);
        Add(table, "Model name", name);
        Add(table, "Birth date", birth);
        Add(table, "Height (cm)", Pair(height, useHeight));
        Add(table, "Bust (cm)", Pair(bust, useBust));
        Add(table, "Waist (cm)", Pair(waist, useWaist));
        Add(table, "Hips (cm)", Pair(hips, useHips));
        Add(table, "Hair", hair); Add(table, "Ethnicity", ethnicity);
        Add(table, "City", city); Add(table, "Country", country);
        Button ok = new() { Text = "OK", AutoSize = true };
        Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        Add(table, "", Pair(ok, cancel));
        ok.Click += Save;
        AcceptButton = ok; CancelButton = cancel;
        Read();
        AppTheme.Apply(this);
    }

    void Read()
    {
        name.Text = Profile.ModelName; birth.Checked = Profile.BirthDate != null;
        if (Profile.BirthDate is DateOnly date) birth.Value = date.ToDateTime(TimeOnly.MinValue);
        Set(height, useHeight, Profile.HeightCm); Set(bust, useBust, Profile.BustCm);
        Set(waist, useWaist, Profile.WaistCm); Set(hips, useHips, Profile.HipsCm);
        hair.Text = Profile.Hair; ethnicity.Text = Profile.Ethnicity;
        city.Text = Profile.City; country.Text = Profile.Country;
    }

    void Save(object? sender, EventArgs e)
    {
        try
        {
            Profile.ModelName = name.Text.Trim();
            Profile.BirthDate = birth.Checked ? DateOnly.FromDateTime(birth.Value) : null;
            Profile.HeightCm = useHeight.Checked ? height.Value : null;
            Profile.BustCm = useBust.Checked ? bust.Value : null;
            Profile.WaistCm = useWaist.Checked ? waist.Value : null;
            Profile.HipsCm = useHips.Checked ? hips.Value : null;
            Profile.Hair = hair.Text.Trim(); Profile.Ethnicity = ethnicity.Text.Trim();
            Profile.City = city.Text.Trim(); Profile.Country = country.Text.Trim();
            CustomShowStore.ValidateProfile(Profile);
            DialogResult = DialogResult.OK; Close();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text); }
    }

    static NumericUpDown Measure(int min, int max) => new()
        { Minimum = min, Maximum = max, DecimalPlaces = 1, Increment = .5m };
    static void Set(NumericUpDown input, CheckBox enabled, decimal? value)
        { enabled.Checked = value != null; if (value != null) input.Value = value.Value; }
    static FlowLayoutPanel Pair(params Control[] controls)
        { FlowLayoutPanel p = new() { AutoSize = true, Dock = DockStyle.Fill }; p.Controls.AddRange(controls); return p; }
    static void Add(TableLayoutPanel table, string label, Control control)
        { int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); control.Dock = DockStyle.Fill; table.Controls.Add(control, 1, row); }
    static CustomPerformerProfile Clone(CustomPerformerProfile p) => new()
        { SchemaVersion=p.SchemaVersion, Id=p.Id, ModelName=p.ModelName, BirthDate=p.BirthDate,
          HeightCm=p.HeightCm, BustCm=p.BustCm, WaistCm=p.WaistCm, HipsCm=p.HipsCm,
          Hair=p.Hair, Ethnicity=p.Ethnicity, City=p.City, Country=p.Country };
}

internal sealed class CustomModelManagerForm : Form
{
    readonly CustomShowStore store;
    readonly ListBox list = new() { Dock = DockStyle.Fill, DisplayMember = nameof(CustomPerformerProfile.ModelName) };
    internal CustomModelManagerForm(CustomShowStore store)
    {
        this.store = store; Text = "Custom Show Models"; ClientSize = new Size(440, 420);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, AutoSize = true };
        Button add = new() { Text = "Add...", AutoSize = true };
        Button edit = new() { Text = "Edit...", AutoSize = true };
        Button close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([add, edit, close]); Controls.Add(list); Controls.Add(buttons);
        add.Click += (_, _) => Edit(null);
        edit.Click += (_, _) => Edit(list.SelectedItem as CustomPerformerProfile);
        list.SelectedIndexChanged += (_, _) => edit.Enabled = list.SelectedItem != null;
        edit.Enabled = false;
        Reload(); AppTheme.Apply(this);
    }
    void Reload() { list.DataSource = null; list.DataSource = store.LoadPerformers(); }
    void Edit(CustomPerformerProfile? profile)
    {
        using CustomPerformerForm form = new(profile);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        store.SavePerformer(form.Profile); Reload(); DialogResult = DialogResult.OK;
    }
}

internal sealed class CustomShowSettingsForm : Form
{
    readonly TextBox root = new(), python = new();
    readonly NumericUpDown sam2CacheSize = new()
    {
        Minimum = 0, Maximum = 100, DecimalPlaces = 0, Width = 90
    };
    readonly Label sam2CacheUsage = new() { AutoSize = true };
    readonly NumericUpDown transNetBatch = new()
    {
        Minimum = 1, Maximum = 64, DecimalPlaces = 0, Width = 90
    };
    readonly NumericUpDown transNetCutoff = CutoffInput();
    readonly ComboBox transNetDecode = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240 };
    readonly ComboBox rvmQualityChunk = RvmChunkInput();
    readonly ComboBox rvmFastChunk = RvmChunkInput();
    readonly NumericUpDown rvmQualityCutoff = CompileCutoffInput();
    readonly NumericUpDown rvmFastCutoff = CompileCutoffInput();
    readonly ComboBox rvmNvencPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 90 };
    readonly NumericUpDown matAnyoneCutoff = CutoffInput();
    readonly NumericUpDown sam2BaseCutoff = CutoffInput();
    readonly NumericUpDown sam2SmallCutoff = CutoffInput();
    readonly NumericUpDown sam2TinyCutoff = CutoffInput();
    readonly NumericUpDown vitMatteSmallCutoff = CutoffInput();
    readonly NumericUpDown vitMatteBaseCutoff = CutoffInput();
    readonly NumericUpDown vitMatteSmallBatch = BatchInput();
    readonly NumericUpDown vitMatteBaseBatch = BatchInput();
    readonly Label validation = new() { AutoSize = true };
    CancellationTokenSource? validationCancellation;
    internal CustomShowConfiguration Configuration { get; }
    internal CustomShowSettingsForm(CustomShowConfiguration current)
    {
        Configuration = new()
        {
            LibraryRoot = current.LibraryRoot,
            PythonExecutable = current.PythonExecutable,
            SmallPlayerVolume = current.SmallPlayerVolume,
            LargePlayerVolume = current.LargePlayerVolume,
            FullOpacityThreshold = current.FullOpacityThreshold,
            Sam2FrameCacheSizeGb = current.Sam2FrameCacheSizeGb,
            TransNetPreferredBatchSize = current.TransNetPreferredBatchSize,
            TransNetCompileCutoffFrames = current.TransNetCompileCutoffFrames,
            TransNetDecodeMode = current.TransNetDecodeMode,
            RvmQualityPreferredChunk = current.RvmQualityPreferredChunk,
            RvmFastPreferredChunk = current.RvmFastPreferredChunk,
            RvmQualityCompileCutoffFrames = current.RvmQualityCompileCutoffFrames,
            RvmFastCompileCutoffFrames = current.RvmFastCompileCutoffFrames,
            RvmNvencPreset = current.RvmNvencPreset,
            MatAnyone2CompileCutoffFrames = current.MatAnyone2CompileCutoffFrames,
            Sam2BasePlusCompileCutoffFrames = current.Sam2BasePlusCompileCutoffFrames,
            Sam2SmallCompileCutoffFrames = current.Sam2SmallCompileCutoffFrames,
            Sam2TinyCompileCutoffFrames = current.Sam2TinyCompileCutoffFrames,
            VitMatteSmallCompileCutoffFrames = current.VitMatteSmallCompileCutoffFrames,
            VitMatteBaseCompileCutoffFrames = current.VitMatteBaseCompileCutoffFrames,
            VitMatteSmallPreferredBatchSize = current.VitMatteSmallPreferredBatchSize,
            VitMatteBasePreferredBatchSize = current.VitMatteBasePreferredBatchSize
        };
        Text = "Custom Show Settings"; ClientSize = new Size(920, 940);
        MinimumSize = new Size(820, 820);
        TableLayoutPanel table = new() { Dock = DockStyle.Fill, ColumnCount = 3,
            Padding = new Padding(12), AutoScroll = true };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); Controls.Add(table);
        AddPath(table, "Library folder", root, true); AddPath(table, "Python executable", python, false);
        int cacheRow = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = "SAM2 frame cache (GB)", AutoSize = true,
            Anchor = AnchorStyles.Left }, 0, cacheRow);
        FlowLayoutPanel cacheControls = new() { AutoSize = true, WrapContents = false };
        Button clearCache = new() { Text = "Clear SAM2 frame cache", AutoSize = true };
        cacheControls.Controls.AddRange([sam2CacheSize, clearCache, sam2CacheUsage]);
        table.Controls.Add(cacheControls, 1, cacheRow); table.SetColumnSpan(cacheControls, 2);
        AddChoice(table, "TransNetV2 preferred window batch", transNetBatch);
        AddCutoff(table, "TransNetV2 compile cutoff", transNetCutoff);
        transNetDecode.Items.AddRange(["Auto (codec-aware, exact-compatible)",
            "Legacy CUDA-first", "CPU fallback (manual)"]);
        AddChoice(table, "TransNetV2 decode mode", transNetDecode);
        Button benchmarkTransNet = new() { Text = "Benchmark TransNetV2...", AutoSize = true };
        Label transNetHelp = new() { AutoSize = true,
            Text = "Manual; tests batch, compilation, CUDA graphs, decode speed, and exact divider compatibility." };
        FlowLayoutPanel transNetActions = new() { AutoSize = true, WrapContents = true };
        transNetActions.Controls.AddRange([benchmarkTransNet, transNetHelp]);
        int transNetActionRow = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(transNetActions, 1, transNetActionRow);
        table.SetColumnSpan(transNetActions, 2);
        AddChoice(table, "RVM Quality preferred chunk", rvmQualityChunk);
        AddChoice(table, "RVM Fast preferred chunk", rvmFastChunk);
        AddChoice(table, "RVM Quality compile cutoff", rvmQualityCutoff);
        AddChoice(table, "RVM Fast compile cutoff", rvmFastCutoff);
        rvmNvencPreset.Items.AddRange(["p1", "p2", "p3", "p4", "p5", "p6", "p7"]);
        AddChoice(table, "Custom show NVENC preset", rvmNvencPreset);
        Button benchmarkRvm = new() { Text = "Benchmark RVM...", AutoSize = true };
        Label rvmHelp = new() { AutoSize = true,
            Text = "Manual; tests Quality/Fast chunks, bounded pipeline, previews, and optional compilation." };
        FlowLayoutPanel rvmActions = new() { AutoSize = true, WrapContents = true };
        rvmActions.Controls.AddRange([benchmarkRvm, rvmHelp]);
        int rvmActionRow = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(rvmActions, 1, rvmActionRow);
        table.SetColumnSpan(rvmActions, 2);
        AddCutoff(table, "MatAnyone2 compile cutoff", matAnyoneCutoff);
        AddCutoff(table, "SAM2 Base+ compile cutoff", sam2BaseCutoff);
        AddCutoff(table, "SAM2 Small compile cutoff", sam2SmallCutoff);
        AddCutoff(table, "SAM2 Tiny compile cutoff", sam2TinyCutoff);
        AddCutoff(table, "ViTMatte S compile cutoff", vitMatteSmallCutoff);
        AddCutoff(table, "ViTMatte B compile cutoff", vitMatteBaseCutoff);
        AddBatch(table, "ViTMatte S preferred batch", vitMatteSmallBatch);
        AddBatch(table, "ViTMatte B preferred batch", vitMatteBaseBatch);
        Button benchmark = new() { Text = "Benchmark and recalculate cutoffs...",
            AutoSize = true };
        Label cutoffHelp = new() { AutoSize = true,
            Text = "Frames below these cutoffs use eager execution. Benchmarking is manual and may take a long time." };
        FlowLayoutPanel cutoffActions = new() { AutoSize = true, WrapContents = true };
        cutoffActions.Controls.AddRange([benchmark, cutoffHelp]);
        int cutoffActionRow = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(cutoffActions, 1, cutoffActionRow);
        table.SetColumnSpan(cutoffActions, 2);
        Button validate = new() { Text = "Validate setup", AutoSize = true };
        Button ok = new() { Text = "OK", AutoSize = true };
        Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        FlowLayoutPanel buttons = new() { AutoSize = true }; buttons.Controls.AddRange([validate, ok, cancel]);
        int validationRow = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(validation, 1, validationRow); table.SetColumnSpan(validation, 2);
        int buttonRow = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(buttons, 1, buttonRow); table.SetColumnSpan(buttons, 2);
        root.Text = Configuration.LibraryRoot; python.Text = Configuration.PythonExecutable;
        sam2CacheSize.Value = Math.Clamp(Configuration.Sam2FrameCacheSizeGb, 0, 100);
        transNetBatch.Value = Math.Clamp(Configuration.TransNetPreferredBatchSize, 1, 64);
        transNetCutoff.Value = ClampCutoff(Configuration.TransNetCompileCutoffFrames);
        transNetDecode.SelectedIndex = DecodeModeIndex(Configuration.TransNetDecodeMode);
        rvmQualityChunk.SelectedItem = ClosestRvmChunk(Configuration.RvmQualityPreferredChunk);
        rvmFastChunk.SelectedItem = ClosestRvmChunk(Configuration.RvmFastPreferredChunk);
        rvmQualityCutoff.Value = ClampCompileCutoff(Configuration.RvmQualityCompileCutoffFrames);
        rvmFastCutoff.Value = ClampCompileCutoff(Configuration.RvmFastCompileCutoffFrames);
        rvmNvencPreset.SelectedItem = CustomShowProcessor.ValidNvencPreset(
            Configuration.RvmNvencPreset);
        matAnyoneCutoff.Value = ClampCutoff(Configuration.MatAnyone2CompileCutoffFrames);
        sam2BaseCutoff.Value = ClampCutoff(Configuration.Sam2BasePlusCompileCutoffFrames);
        sam2SmallCutoff.Value = ClampCutoff(Configuration.Sam2SmallCompileCutoffFrames);
        sam2TinyCutoff.Value = ClampCutoff(Configuration.Sam2TinyCompileCutoffFrames);
        vitMatteSmallCutoff.Value = ClampCutoff(Configuration.VitMatteSmallCompileCutoffFrames);
        vitMatteBaseCutoff.Value = ClampCutoff(Configuration.VitMatteBaseCompileCutoffFrames);
        vitMatteSmallBatch.Value = ClampBatch(Configuration.VitMatteSmallPreferredBatchSize);
        vitMatteBaseBatch.Value = ClampBatch(Configuration.VitMatteBasePreferredBatchSize);
        sam2CacheUsage.Text = "Calculating usage...";
        Shown += async (_, _) => await UpdateCacheUsageAsync();
        clearCache.Click += async (_, _) => await ClearSam2CacheAsync(clearCache);
        benchmarkTransNet.Click += (_, _) => BenchmarkTransNet();
        benchmarkRvm.Click += (_, _) => BenchmarkRvm();
        benchmark.Click += (_, _) => BenchmarkCutoffs();
        validate.Click += async (_, _) => await ValidateSetup(validate);
        ok.Click += SaveSettings;
        FormClosed += (_, _) => validationCancellation?.Cancel();
        AcceptButton = ok; CancelButton = cancel; AppTheme.Apply(this);
    }
    async Task ValidateSetup(Button button)
    {
        if (validationCancellation != null)
        {
            validation.Text = "Cancelling validation...";
            validationCancellation.Cancel();
            return;
        }
        validationCancellation = new();
        button.Text = "Cancel validation";
        validation.Text = "Starting validation...";
        string result;
        try { Configuration.LibraryRoot = Path.GetFullPath(root.Text); Configuration.PythonExecutable = Path.GetFullPath(python.Text); Directory.CreateDirectory(Configuration.LibraryRoot); result = await CustomShowProcessor.ValidateAsync(Configuration, validationCancellation.Token, new Progress<string>(message => validation.Text = message)); }
        catch (OperationCanceledException) { result = "Validation cancelled."; }
        catch (Exception error) { result = "Failed: " + error.Message; }
        finally { validationCancellation.Dispose(); validationCancellation = null; button.Text = "Validate setup"; }
        if (result.StartsWith("OK:", StringComparison.Ordinal))
            result = "Setup validated successfully.";
        BeginInvoke(() => validation.Text = result);
    }
    void SaveSettings(object? sender, EventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root.Text))
                throw new InvalidDataException("The custom library folder is required.");
            if (!File.Exists(python.Text))
                throw new FileNotFoundException("Select the Python executable created by setup.", python.Text);
            Configuration.LibraryRoot = Path.GetFullPath(root.Text);
            Configuration.PythonExecutable = Path.GetFullPath(python.Text);
            Configuration.Sam2FrameCacheSizeGb = (int)sam2CacheSize.Value;
            Configuration.TransNetPreferredBatchSize = (int)transNetBatch.Value;
            Configuration.TransNetCompileCutoffFrames = (int)transNetCutoff.Value;
            Configuration.TransNetDecodeMode = DecodeModeValue(transNetDecode.SelectedIndex);
            Configuration.RvmQualityPreferredChunk = RvmChunkValue(rvmQualityChunk);
            Configuration.RvmFastPreferredChunk = RvmChunkValue(rvmFastChunk);
            Configuration.RvmQualityCompileCutoffFrames = (int)rvmQualityCutoff.Value;
            Configuration.RvmFastCompileCutoffFrames = (int)rvmFastCutoff.Value;
            Configuration.RvmNvencPreset = rvmNvencPreset.SelectedItem?.ToString() ?? "p5";
            Configuration.MatAnyone2CompileCutoffFrames = (int)matAnyoneCutoff.Value;
            Configuration.Sam2BasePlusCompileCutoffFrames = (int)sam2BaseCutoff.Value;
            Configuration.Sam2SmallCompileCutoffFrames = (int)sam2SmallCutoff.Value;
            Configuration.Sam2TinyCompileCutoffFrames = (int)sam2TinyCutoff.Value;
            Configuration.VitMatteSmallCompileCutoffFrames = (int)vitMatteSmallCutoff.Value;
            Configuration.VitMatteBaseCompileCutoffFrames = (int)vitMatteBaseCutoff.Value;
            Configuration.VitMatteSmallPreferredBatchSize = (int)vitMatteSmallBatch.Value;
            Configuration.VitMatteBasePreferredBatchSize = (int)vitMatteBaseBatch.Value;
            Directory.CreateDirectory(Configuration.LibraryRoot);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void BenchmarkCutoffs()
    {
        if (!File.Exists(python.Text))
        {
            MessageBox.Show(this, "Select the processing-environment Python executable first.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this,
            "This benchmark loads and compiles the installed MatAnyone2, SAM2, and ViTMatte models. " +
            "It can take many minutes, uses the GPU heavily, and runs only when you continue.\n\nRun it now?",
            "Benchmark model cutoffs", MessageBoxButtons.YesNo,
            MessageBoxIcon.Information) != DialogResult.Yes) return;
        CustomShowConfiguration benchmarkConfiguration = new()
        {
            LibraryRoot = root.Text,
            PythonExecutable = python.Text,
            MatAnyone2CompileCutoffFrames = (int)matAnyoneCutoff.Value,
            Sam2BasePlusCompileCutoffFrames = (int)sam2BaseCutoff.Value,
            Sam2SmallCompileCutoffFrames = (int)sam2SmallCutoff.Value,
            Sam2TinyCompileCutoffFrames = (int)sam2TinyCutoff.Value,
            VitMatteSmallCompileCutoffFrames = (int)vitMatteSmallCutoff.Value,
            VitMatteBaseCompileCutoffFrames = (int)vitMatteBaseCutoff.Value,
            VitMatteSmallPreferredBatchSize = (int)vitMatteSmallBatch.Value,
            VitMatteBasePreferredBatchSize = (int)vitMatteBaseBatch.Value
        };
        using CustomShowCutoffBenchmarkForm form = new(benchmarkConfiguration);
        if (form.ShowDialog(this) != DialogResult.OK || form.Result == null) return;
        matAnyoneCutoff.Value = ClampCutoff(form.Result.MatAnyone2CompileCutoffFrames);
        sam2BaseCutoff.Value = ClampCutoff(form.Result.Sam2BasePlusCompileCutoffFrames);
        sam2SmallCutoff.Value = ClampCutoff(form.Result.Sam2SmallCompileCutoffFrames);
        sam2TinyCutoff.Value = ClampCutoff(form.Result.Sam2TinyCompileCutoffFrames);
        vitMatteSmallCutoff.Value = ClampCutoff(form.Result.VitMatteSmallCompileCutoffFrames);
        vitMatteBaseCutoff.Value = ClampCutoff(form.Result.VitMatteBaseCompileCutoffFrames);
        vitMatteSmallBatch.Value = ClampBatch(form.Result.VitMatteSmallPreferredBatchSize);
        vitMatteBaseBatch.Value = ClampBatch(form.Result.VitMatteBasePreferredBatchSize);
        validation.Text = "Benchmark completed; review the recalculated cutoffs and click OK to save them.";
    }

    void BenchmarkTransNet()
    {
        if (!File.Exists(python.Text))
        {
            MessageBox.Show(this, "Select the processing-environment Python executable first.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this,
            "This benchmark runs TransNetV2 batch, compilation, CUDA-graph, and decoder tests. " +
            "It uses the GPU heavily, may perform one-time compilation, and changes no settings " +
            "until you accept its results.\n\nRun it now?",
            "Benchmark TransNetV2", MessageBoxButtons.YesNo,
            MessageBoxIcon.Information) != DialogResult.Yes) return;
        CustomShowConfiguration benchmarkConfiguration = new()
        {
            LibraryRoot = root.Text,
            PythonExecutable = python.Text,
            TransNetPreferredBatchSize = (int)transNetBatch.Value,
            TransNetCompileCutoffFrames = (int)transNetCutoff.Value,
            TransNetDecodeMode = DecodeModeValue(transNetDecode.SelectedIndex)
        };
        using CustomShowTransNetBenchmarkForm form = new(benchmarkConfiguration);
        if (form.ShowDialog(this) != DialogResult.OK || form.Result == null) return;
        transNetBatch.Value = Math.Clamp(form.Result.TransNetPreferredBatchSize, 1, 64);
        transNetCutoff.Value = ClampCutoff(form.Result.TransNetCompileCutoffFrames);
        transNetDecode.SelectedIndex = DecodeModeIndex(form.Result.TransNetDecodeMode);
        validation.Text = "TransNetV2 benchmark completed; review the values and click OK to save them.";
    }

    void BenchmarkRvm()
    {
        if (!File.Exists(python.Text))
        {
            MessageBox.Show(this, "Select the processing-environment Python executable first.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this,
            "This benchmark generates 1080p and 4K fixtures and tests both RVM models, " +
            "chunk sizes, the bounded pipeline, previews, and compilation. It uses the GPU " +
            "heavily and can take a long time. Settings are unchanged until you accept the results.\n\nRun it now?",
            "Benchmark RVM", MessageBoxButtons.YesNo,
            MessageBoxIcon.Information) != DialogResult.Yes) return;
        CustomShowConfiguration benchmarkConfiguration = new()
        {
            LibraryRoot = root.Text,
            PythonExecutable = python.Text,
            RvmQualityPreferredChunk = RvmChunkValue(rvmQualityChunk),
            RvmFastPreferredChunk = RvmChunkValue(rvmFastChunk),
            RvmQualityCompileCutoffFrames = (int)rvmQualityCutoff.Value,
            RvmFastCompileCutoffFrames = (int)rvmFastCutoff.Value,
            RvmNvencPreset = rvmNvencPreset.SelectedItem?.ToString() ?? "p5"
        };
        using CustomShowRvmBenchmarkForm form = new(benchmarkConfiguration);
        if (form.ShowDialog(this) != DialogResult.OK || form.Result == null) return;
        rvmQualityChunk.SelectedItem = ClosestRvmChunk(form.Result.RvmQualityPreferredChunk);
        rvmFastChunk.SelectedItem = ClosestRvmChunk(form.Result.RvmFastPreferredChunk);
        rvmQualityCutoff.Value = ClampCompileCutoff(
            form.Result.RvmQualityCompileCutoffFrames);
        rvmFastCutoff.Value = ClampCompileCutoff(form.Result.RvmFastCompileCutoffFrames);
        validation.Text = "RVM benchmark completed; review the values and click OK to save them.";
    }

    static int DecodeModeIndex(string? value) => value?.ToLowerInvariant() switch
    {
        "legacy" => 1,
        "cpu" => 2,
        _ => 0
    };

    static string DecodeModeValue(int index) => index switch
    {
        1 => "legacy",
        2 => "cpu",
        _ => "auto"
    };

    static NumericUpDown CutoffInput() => new()
    {
        Minimum = 1, Maximum = 10_000_000, Increment = 1000,
        ThousandsSeparator = true, Width = 130
    };

    static decimal ClampCutoff(int value) => Math.Clamp(value, 1, 10_000_000);

    static NumericUpDown CompileCutoffInput() => new()
    {
        Minimum = 0, Maximum = 10_000_000, Increment = 1000,
        ThousandsSeparator = true, Width = 130
    };

    static decimal ClampCompileCutoff(int value) => Math.Clamp(value, 0, 10_000_000);

    static readonly int[] RvmChunks = [1, 2, 3, 4, 6, 8, 12, 16, 24];

    static ComboBox RvmChunkInput()
    {
        ComboBox input = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        input.Items.AddRange(RvmChunks.Cast<object>().ToArray());
        return input;
    }

    static int ClosestRvmChunk(int value) => RvmChunks.MinBy(chunk => Math.Abs(chunk - value));

    static int RvmChunkValue(ComboBox input) => input.SelectedItem is int value ? value : 12;

    static NumericUpDown BatchInput() => new()
    {
        Minimum = 1, Maximum = 12, Increment = 1, Width = 90
    };

    static decimal ClampBatch(int value) => Math.Clamp(value, 1, 12);

    static void AddCutoff(TableLayoutPanel table, string label, NumericUpDown input)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label + " (frames)", AutoSize = true,
            Anchor = AnchorStyles.Left }, 0, row);
        table.Controls.Add(input, 1, row);
    }

    static void AddBatch(TableLayoutPanel table, string label, NumericUpDown input)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label + " (frames)", AutoSize = true,
            Anchor = AnchorStyles.Left }, 0, row);
        table.Controls.Add(input, 1, row);
    }

    static void AddChoice(TableLayoutPanel table, string label, Control input)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true,
            Anchor = AnchorStyles.Left }, 0, row);
        table.Controls.Add(input, 1, row);
    }

    async Task UpdateCacheUsageAsync()
    {
        try
        {
            long bytes = await Task.Run(() =>
                Directory.Exists(CustomShowProcessor.Sam2FrameCacheRoot)
                    ? Directory.EnumerateFiles(CustomShowProcessor.Sam2FrameCacheRoot, "*",
                        SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0);
            if (IsDisposed) return;
            sam2CacheUsage.Text = $"Used: {bytes / (1024d * 1024 * 1024):0.00} GB";
        }
        catch { sam2CacheUsage.Text = "Usage unavailable"; }
    }

    async Task ClearSam2CacheAsync(Button button)
    {
        button.Enabled = false;
        sam2CacheUsage.Text = "Clearing inactive entries...";
        await Task.Run(() =>
        {
            string root = CustomShowProcessor.Sam2FrameCacheRoot;
            if (!Directory.Exists(root)) return;
            foreach (string entry in Directory.EnumerateDirectories(root))
            {
                bool inUse = false;
                try
                {
                    foreach (string active in Directory.EnumerateFiles(entry, ".active*"))
                    {
                        if (!int.TryParse(File.ReadAllText(active), out int pid)) continue;
                        using Process process = Process.GetProcessById(pid);
                        if (!process.HasExited) { inUse = true; break; }
                    }
                }
                catch { }
                if (!inUse) try { Directory.Delete(entry, true); } catch { }
            }
        });
        button.Enabled = true;
        await UpdateCacheUsageAsync();
    }
    static void AddPath(TableLayoutPanel table, string label, TextBox box, bool folder)
    {
        int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); box.Dock = DockStyle.Fill; table.Controls.Add(box, 1, row);
        Button browse = new() { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) => { if (folder) { using FolderBrowserDialog d = new() { SelectedPath = box.Text }; if (d.ShowDialog(table.FindForm()) == DialogResult.OK) box.Text = d.SelectedPath; } else { using OpenFileDialog d = new() { Filter = "Python|python.exe" }; if (d.ShowDialog(table.FindForm()) == DialogResult.OK) box.Text = d.FileName; } };
        table.Controls.Add(browse, 2, row);
    }
}

internal sealed class CustomShowCutoffBenchmarkForm : Form
{
    readonly CustomShowConfiguration configuration;
    readonly TextBox output = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false
    };
    readonly Button close = new() { Dock = DockStyle.Bottom, Height = 36, Text = "Cancel" };
    Process? process;
    bool complete;
    internal CustomShowConfiguration? Result { get; private set; }

    internal CustomShowCutoffBenchmarkForm(CustomShowConfiguration configuration)
    {
        this.configuration = configuration;
        Text = "Benchmark Custom Show Model Cutoffs";
        ClientSize = new Size(920, 560);
        Controls.Add(output); Controls.Add(close);
        close.Click += (_, _) => CloseBenchmark();
        FormClosing += (_, _) => { if (!complete) try { process?.Kill(true); } catch { } };
        Shown += async (_, _) => await RunAsync();
        AppTheme.Apply(this);
    }

    async Task RunAsync()
    {
        string resultPath = Path.Combine(Path.GetTempPath(),
            "iqp-model-cutoffs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Append("Preparing model cutoff benchmarks. This runs only from this dialog...");
            ProcessStartInfo start = new(configuration.PythonExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "custom-shows", "benchmark_model_cutoffs.py"),
                "--runtime", CustomShowProcessor.RuntimeRoot(configuration),
                "--ffmpeg", Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                "--ffprobe", Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"),
                "--sam-worker", Path.Combine(AppContext.BaseDirectory, "custom-shows", "sam2_refine_worker.py"),
                "--vitmatte-worker", Path.Combine(AppContext.BaseDirectory, "custom-shows", "vitmatte_worker.py"),
                "--output", resultPath,
                "--matanyone-default", configuration.MatAnyone2CompileCutoffFrames.ToString(),
                "--sam-base-default", configuration.Sam2BasePlusCompileCutoffFrames.ToString(),
                "--sam-small-default", configuration.Sam2SmallCompileCutoffFrames.ToString(),
                "--sam-tiny-default", configuration.Sam2TinyCompileCutoffFrames.ToString(),
                "--vitmatte-small-default", configuration.VitMatteSmallCompileCutoffFrames.ToString(),
                "--vitmatte-base-default", configuration.VitMatteBaseCompileCutoffFrames.ToString(),
                "--vitmatte-small-batch-default", configuration.VitMatteSmallPreferredBatchSize.ToString(),
                "--vitmatte-base-batch-default", configuration.VitMatteBasePreferredBatchSize.ToString()
            }) start.ArgumentList.Add(argument);
            process = Process.Start(start) ??
                throw new InvalidOperationException("Python could not start the benchmark.");
            Task standardOutput = PumpAsync(process.StandardOutput);
            Task standardError = PumpAsync(process.StandardError);
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Benchmark failed with exit code {process.ExitCode}.");
            Result = JsonSerializer.Deserialize<CustomShowConfiguration>(
                await File.ReadAllTextAsync(resultPath), CustomShowStore.JsonOptions) ??
                throw new InvalidDataException("The benchmark did not return cutoff values.");
            Append("Benchmark completed. The measured values will be copied into Settings.");
        }
        catch (Exception error)
        {
            if (!IsDisposed) Append("Benchmark failed: " + error.Message);
        }
        finally
        {
            complete = true;
            if (!IsDisposed) close.Text = Result == null ? "Close" : "Use results";
            try { File.Delete(resultPath); } catch { }
        }
    }

    async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is string line) Append(line);
    }

    void Append(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Append(text)); return; }
        output.AppendText(text + Environment.NewLine);
    }

    void CloseBenchmark()
    {
        if (!complete)
        {
            try { process?.Kill(true); } catch { }
            DialogResult = DialogResult.Cancel;
        }
        else if (Result != null) DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class CustomShowRvmBenchmarkForm : Form
{
    readonly CustomShowConfiguration configuration;
    readonly TextBox output = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false
    };
    readonly Button close = new() { Dock = DockStyle.Bottom, Height = 36, Text = "Cancel" };
    Process? process;
    bool complete;
    internal CustomShowConfiguration? Result { get; private set; }

    internal CustomShowRvmBenchmarkForm(CustomShowConfiguration configuration)
    {
        this.configuration = configuration;
        Text = "Benchmark RVM";
        ClientSize = new Size(920, 560);
        Controls.Add(output); Controls.Add(close);
        close.Click += (_, _) => CloseBenchmark();
        FormClosing += (_, _) => { if (!complete) try { process?.Kill(true); } catch { } };
        Shown += async (_, _) => await RunAsync();
        AppTheme.Apply(this);
    }

    async Task RunAsync()
    {
        string resultPath = Path.Combine(Path.GetTempPath(),
            "iqp-rvm-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Append("Preparing full-pipeline RVM benchmarks. Settings are not changed until you use the results.");
            Append("Compilation can incur a one-time PyTorch compiler cost; later jobs reuse its cache.");
            ProcessStartInfo start = new(configuration.PythonExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "custom-shows", "benchmark_rvm.py"),
                "--runtime", CustomShowProcessor.RuntimeRoot(configuration),
                "--worker", Path.Combine(AppContext.BaseDirectory, "custom-shows", "rvm_worker.py"),
                "--ffmpeg", Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                "--ffprobe", Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"),
                "--output", resultPath,
                "--quality-chunk-default", configuration.RvmQualityPreferredChunk.ToString(),
                "--fast-chunk-default", configuration.RvmFastPreferredChunk.ToString(),
                "--quality-cutoff-default", configuration.RvmQualityCompileCutoffFrames.ToString(),
                "--fast-cutoff-default", configuration.RvmFastCompileCutoffFrames.ToString(),
                "--encoder-preset", CustomShowProcessor.ValidNvencPreset(
                    configuration.RvmNvencPreset)
            }) start.ArgumentList.Add(argument);
            process = Process.Start(start) ??
                throw new InvalidOperationException("Python could not start the RVM benchmark.");
            Task standardOutput = PumpAsync(process.StandardOutput);
            Task standardError = PumpAsync(process.StandardError);
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"RVM benchmark failed with exit code {process.ExitCode}.");
            Result = JsonSerializer.Deserialize<CustomShowConfiguration>(
                await File.ReadAllTextAsync(resultPath), CustomShowStore.JsonOptions) ??
                throw new InvalidDataException("The benchmark did not return RVM settings.");
            Append("Benchmark completed. Review the measured settings before saving them.");
        }
        catch (Exception error)
        {
            if (!IsDisposed) Append("Benchmark failed: " + error.Message);
        }
        finally
        {
            complete = true;
            if (!IsDisposed) close.Text = Result == null ? "Close" : "Use results";
            try { File.Delete(resultPath); } catch { }
        }
    }

    async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is string line) Append(line);
    }

    void Append(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Append(text)); return; }
        output.AppendText(text + Environment.NewLine);
    }

    void CloseBenchmark()
    {
        if (!complete)
        {
            try { process?.Kill(true); } catch { }
            DialogResult = DialogResult.Cancel;
        }
        else if (Result != null) DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class CustomShowTransNetBenchmarkForm : Form
{
    readonly CustomShowConfiguration configuration;
    readonly TextBox output = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false
    };
    readonly Button close = new() { Dock = DockStyle.Bottom, Height = 36, Text = "Cancel" };
    Process? process;
    bool complete;
    internal CustomShowConfiguration? Result { get; private set; }

    internal CustomShowTransNetBenchmarkForm(CustomShowConfiguration configuration)
    {
        this.configuration = configuration;
        Text = "Benchmark TransNetV2";
        ClientSize = new Size(920, 560);
        Controls.Add(output); Controls.Add(close);
        close.Click += (_, _) => CloseBenchmark();
        FormClosing += (_, _) => { if (!complete) try { process?.Kill(true); } catch { } };
        Shown += async (_, _) => await RunAsync();
        AppTheme.Apply(this);
    }

    async Task RunAsync()
    {
        string resultPath = Path.Combine(Path.GetTempPath(),
            "iqp-transnetv2-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Append("Preparing TransNetV2 benchmarks. QuickPlayer settings are not changed until you use the results.");
            Append("Compilation tests can incur a one-time PyTorch compiler cost; cached later runs do not repeat it.");
            ProcessStartInfo start = new(configuration.PythonExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "custom-shows", "benchmark_transnetv2.py"),
                "--runtime", CustomShowProcessor.RuntimeRoot(configuration),
                "--worker", Path.Combine(AppContext.BaseDirectory, "custom-shows", "transnetv2_worker.py"),
                "--ffmpeg", Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                "--ffprobe", Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"),
                "--output", resultPath,
                "--batch-default", configuration.TransNetPreferredBatchSize.ToString(),
                "--cutoff-default", configuration.TransNetCompileCutoffFrames.ToString()
            }) start.ArgumentList.Add(argument);
            process = Process.Start(start) ??
                throw new InvalidOperationException("Python could not start the TransNetV2 benchmark.");
            Task standardOutput = PumpAsync(process.StandardOutput);
            Task standardError = PumpAsync(process.StandardError);
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"TransNetV2 benchmark failed with exit code {process.ExitCode}.");
            Result = JsonSerializer.Deserialize<CustomShowConfiguration>(
                await File.ReadAllTextAsync(resultPath), CustomShowStore.JsonOptions) ??
                throw new InvalidDataException("The benchmark did not return settings.");
            Append("Benchmark completed. Review the measured settings before saving them.");
        }
        catch (Exception error)
        {
            if (!IsDisposed) Append("Benchmark failed: " + error.Message);
        }
        finally
        {
            complete = true;
            if (!IsDisposed) close.Text = Result == null ? "Close" : "Use results";
            try { File.Delete(resultPath); } catch { }
        }
    }

    async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is string line) Append(line);
    }

    void Append(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Append(text)); return; }
        output.AppendText(text + Environment.NewLine);
    }

    void CloseBenchmark()
    {
        if (!complete)
        {
            try { process?.Kill(true); } catch { }
            DialogResult = DialogResult.Cancel;
        }
        else if (Result != null) DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class CustomShowSetupOptionsForm : Form
{
    readonly CheckBox transNet = new() { Text = "TransNetV2 automatic clip detection",
        AutoSize = true, Checked = true };
    readonly CheckBox matAnyone = new() { Text =
        "MatAnyone 2 + SAM2 Base+/Small/Tiny interactive masking (~900 MB)", AutoSize = true,
        Checked = true };
    readonly CheckBox videoMaMa = new() { Text =
        "VideoMaMa high-quality matting + SAM2 (~8 GB, NVIDIA CUDA required)",
        AutoSize = true };
    readonly CheckBox vitMatte = new() { Text =
        "ViTMatte S + B with editable SAM2 video masks (~900 MB)", AutoSize = true };
    readonly CheckBox proPainter = new() { Text =
        "ProPainter video object removal (~200 MB, NVIDIA CUDA recommended)",
        AutoSize = true };
    internal bool InstallTransNetV2 => transNet.Checked;
    internal bool InstallMatAnyone2 => matAnyone.Checked;
    internal bool InstallVideoMaMa => videoMaMa.Checked;
    internal bool InstallViTMatte => vitMatte.Checked;
    internal bool InstallProPainter => proPainter.Checked;

    internal CustomShowSetupOptionsForm()
    {
        Text = "Choose Custom Show Processing Tools";
        ClientSize = new Size(680, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(16),
            ColumnCount = 1, RowCount = 13 };
        layout.Controls.Add(new Label { Text =
            "Robust Video Matting is always installed. Select optional tools:", AutoSize = true });
        layout.Controls.Add(transNet);
        layout.Controls.Add(matAnyone);
        layout.Controls.Add(videoMaMa);
        layout.Controls.Add(vitMatte);
        layout.Controls.Add(proPainter);
        layout.Controls.Add(new Label { Text =
            "MatAnyone 2, VideoMaMa, and ProPainter are non-commercial; ViTMatte and SAM2 permit commercial use.", AutoSize = true });
        LinkLabel matAnyoneLicence = new() { Text = "Read the MatAnyone 2 licence", AutoSize = true };
        matAnyoneLicence.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/pq-yang/MatAnyone2/blob/main/LICENSE.txt") { UseShellExecute = true });
        LinkLabel videoMaMaLicence = new() { Text = "Read the VideoMaMa licence", AutoSize = true };
        videoMaMaLicence.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/cvlab-kaist/VideoMaMa/blob/main/License.md") { UseShellExecute = true });
        layout.Controls.Add(matAnyoneLicence);
        layout.Controls.Add(videoMaMaLicence);
        LinkLabel vitMatteLicence = new() { Text = "Read the ViTMatte licence", AutoSize = true };
        vitMatteLicence.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/hustvl/ViTMatte/blob/main/LICENSE") { UseShellExecute = true });
        layout.Controls.Add(vitMatteLicence);
        LinkLabel proPainterLicence = new() { Text = "Read the ProPainter licence", AutoSize = true };
        proPainterLicence.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/sczhou/ProPainter/blob/main/LICENSE") { UseShellExecute = true });
        layout.Controls.Add(proPainterLicence);
        FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(new Button { Text = "Install / Update", AutoSize = true,
            DialogResult = DialogResult.OK });
        buttons.Controls.Add(new Button { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = (Button)buttons.Controls[0];
        CancelButton = (Button)buttons.Controls[1];
        AppTheme.Apply(this);
        matAnyoneLicence.LinkColor = videoMaMaLicence.LinkColor = vitMatteLicence.LinkColor =
            proPainterLicence.LinkColor =
            Properties.Settings.Default.DarkMode ? Color.LightSkyBlue : Color.Blue;
    }

    internal static bool VerifyDefaults()
    {
        using CustomShowSetupOptionsForm form = new();
        return form.InstallTransNetV2 && form.InstallMatAnyone2 &&
            !form.InstallVideoMaMa && !form.InstallViTMatte &&
            !form.InstallProPainter;
    }
}

internal sealed class CustomShowSetupForm : Form
{
    readonly string script;
    readonly bool installTransNetV2;
    readonly bool installMatAnyone2;
    readonly bool installVideoMaMa;
    readonly bool installViTMatte;
    readonly bool installProPainter;
    readonly TextBox output = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false
    };
    readonly Button close = new() { Dock = DockStyle.Bottom, Height = 36, Text = "Cancel" };
    Process? process;
    bool complete;
    internal bool Succeeded { get; private set; }

    internal CustomShowSetupForm(string script, bool installTransNetV2,
        bool installMatAnyone2, bool installVideoMaMa, bool installViTMatte,
        bool installProPainter)
    {
        this.script = script;
        this.installTransNetV2 = installTransNetV2;
        this.installMatAnyone2 = installMatAnyone2;
        this.installVideoMaMa = installVideoMaMa;
        this.installViTMatte = installViTMatte;
        this.installProPainter = installProPainter;
        Text = "Install Custom Show Processing Tools";
        ClientSize = new Size(900, 520);
        Controls.Add(output);
        Controls.Add(close);
        close.Click += (_, _) => CloseSetup();
        FormClosing += (_, _) => { if (!complete) process?.Kill(true); };
        Shown += async (_, _) => await Run();
        AppTheme.Apply(this);
    }

    async Task Run()
    {
        Append("Starting custom-show processing setup...");
        try
        {
            ProcessStartInfo start = new("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            if (installTransNetV2)
                start.ArgumentList.Add("-InstallTransNetV2");
            if (installMatAnyone2)
                start.ArgumentList.Add("-InstallMatAnyone2");
            if (installVideoMaMa)
                start.ArgumentList.Add("-InstallVideoMaMa");
            if (installViTMatte)
                start.ArgumentList.Add("-InstallViTMatte");
            if (installProPainter)
                start.ArgumentList.Add("-InstallProPainter");
            process = Process.Start(start) ??
                throw new InvalidOperationException("PowerShell could not be started.");
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            Succeeded = process.ExitCode == 0;
            Append(Succeeded ? "Setup completed successfully." :
                $"Setup failed with exit code {process.ExitCode}.");
        }
        catch (Exception error) { Append("Setup failed: " + error.Message); }
        finally
        {
            complete = true;
            close.Text = "Close";
        }
    }

    void Append(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Append(text)); return; }
        output.AppendText(text + Environment.NewLine);
    }

    void CloseSetup()
    {
        if (!complete)
        {
            try { process?.Kill(true); } catch { }
            DialogResult = DialogResult.Cancel;
        }
        else DialogResult = Succeeded ? DialogResult.OK : DialogResult.Abort;
        Close();
    }
}
