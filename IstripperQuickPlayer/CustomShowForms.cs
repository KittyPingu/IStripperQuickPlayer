using System.Drawing.Imaging;
using System.Diagnostics;
using System.Text;
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
    readonly bool reprocess, metadataOnly;
    readonly CustomShowQueueManager? queueManager;
    readonly string? queueJobId;
    readonly CustomShowQueueJob? queueDraft;
    readonly string? queueDraftAssetOwnerId;
    readonly CustomShowIncompleteSetupEntry? restoredSetup;
    string? appendShowId;
    readonly TextBox source = new();
    readonly Button addToExisting = new() { Text = "Add To Existing Show", AutoSize = true };
    readonly TextBox title = new();
    readonly ComboBox performer = new() { DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems };
    readonly Button newPerformer = new() { Text = "New model...", AutoSize = true };
    readonly Button editPerformer = new() { Text = "Edit model...", AutoSize = true,
        Enabled = false };
    readonly TextBox description = new() { Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical };
    readonly TextBox tags = new();
    readonly DateTimePicker releaseDate = new() { Format = DateTimePickerFormat.Short };
    readonly DateTimePicker showDate = new() { Format = DateTimePickerFormat.Short, ShowCheckBox = true };
    readonly NumericUpDown ageOverride = new() { Minimum = 18, Maximum = 120, Value = 18 };
    readonly CheckBox useAgeOverride = new() { Text = "Use" };
    readonly ComboBox gender = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox hotness = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly NumericUpDown performerCount = new() { Minimum = 1, Maximum = 20, Value = 1 };
    readonly CheckedListBox clipTypes = new() { Height = 90, CheckOnClick = true };
    readonly Button editClips = new() { Text = "Split into clips...", AutoSize = true };
    readonly TextBox cover = new();
    readonly TextBox photosFolder = new();
    readonly Button coverTitleColor = new() { AutoSize = true,
        BackColor = Color.DeepPink, UseVisualStyleBackColor = false };
    readonly ComboBox coverModelFont = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox coverTitleFont = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox preset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox nvidiaMode = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 340, DropDownWidth = 340 };
    readonly Button openNvidiaSetup = new() { Text = "Open Setup", AutoSize = true };
    readonly CheckBox nvidiaTemporal = new()
        { Text = "Temporal filtering", AutoSize = true, Checked = true };
    readonly ComboBox nvidiaInferenceResolution = new()
        { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox rvmOnnxQuality = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330,
          DropDownWidth = 465 };
    readonly ComboBox rvmOnnxModel = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    readonly CheckBox rvmOnnxTemporal = new()
        { Text = "Temporal memory", AutoSize = true, Checked = true };
    readonly Button openRvmOnnxSetup = new()
        { Text = "Open Setup", AutoSize = true };
    readonly ComboBox sam2MattingTracker = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 620,
          DropDownWidth = 620 };
    readonly TextBox foregroundConcepts = new()
    {
        Multiline = true, AcceptsReturn = true, Height = 96, Width = 620,
        ScrollBars = ScrollBars.Vertical, Text = "person"
    };
    readonly Label conceptHelp = new()
    {
        AutoSize = true,
        Text = "One independent foreground concept per line. Multi-word phrases are supported."
    };
    readonly Label sam2MattingStatus = new() { AutoSize = true, Padding = new Padding(6, 7, 0, 0) };
    readonly Label sceneMaskSummary = new()
    {
        AutoSize = true, Text = "Initial masks: not created",
        Padding = new Padding(0, 7, 0, 0)
    };
    readonly Button openSam2MattingSetup = new() { Text = "Open Setup", AutoSize = true };
    readonly ComboBox maskEngine = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox sam2Model = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox mattingDetail = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox vitMatteInferenceDetail = new()
        { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox sequenceChunk = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TrackBar rvmInitializerThreshold = new()
        { Minimum = 10, Maximum = 90, Value = 40, TickFrequency = 5,
          SmallChange = 1, LargeChange = 5, Width = 260, Height = 32 };
    readonly Label rvmInitializerThresholdValue = new() { Text = "40%", AutoSize = true,
        Margin = new Padding(3, 7, 3, 3) };
    readonly CheckBox rvmMatAnyoneMaskRefresh = new()
    {
        Text = "Inject persistent new RVM foreground into MatAnyone memory",
        AutoSize = true
    };
    readonly CheckBox propSegmenterEveryFrame = new()
    {
        Text = "Run the trained prop model on every frame",
        AutoSize = true
    };
    readonly ComboBox propSegmenterModel = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    readonly CheckBox debugPropContribution = new()
    {
        Text = "Generate prop-contribution.mp4 for each processed clip",
        AutoSize = true
    };
    readonly TrackBar rvmMatAnyoneRefreshStrength = new()
        { Minimum = 25, Maximum = 100, Value = 100, TickFrequency = 5,
          SmallChange = 1, LargeChange = 5, Width = 260, Height = 32 };
    readonly Label rvmMatAnyoneRefreshStrengthValue = new()
        { Text = "100%", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
    readonly NumericUpDown matAnyoneMaxMemoryFrames = new()
        { Minimum = 6, Maximum = 14, Value = 14, Width = 70 };
    readonly CheckBox matAnyoneUseLongTermMemory = new()
        { Text = "Use compressed long-term memory", AutoSize = true, Checked = true };
    readonly CheckBox temporalAlphaCleanup = new()
        { Text = "Repair short alpha flicker after matting", AutoSize = true };
    readonly NumericUpDown temporalAlphaCleanupWindow = new()
        { Minimum = 1, Maximum = 30, Value = 3, Width = 58 };
    readonly TrackBar temporalAlphaCleanupStrength = new()
        { Minimum = 25, Maximum = 100, Value = 100, TickFrequency = 5,
          SmallChange = 1, LargeChange = 5, Width = 220, Height = 32 };
    readonly Label temporalAlphaCleanupStrengthValue = new()
        { Text = "100%", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
    readonly TrackBar temporalAlphaTrackingStrength = new()
        { Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 5,
          SmallChange = 1, LargeChange = 5, Width = 180, Height = 32 };
    readonly Label temporalAlphaTrackingStrengthValue = new()
        { Text = "100%", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
    readonly NumericUpDown temporalAlphaCleanupAlphaThreshold = new()
        { Minimum = 0, Maximum = 255, Value = 120, Width = 62 };
    readonly Label matAnyoneMemoryWarning = new()
    {
        Text = "Above Standard matting detail, lower max frames to avoid exhausting VRAM.",
        AutoSize = true
    };
    readonly CheckBox autoAccept = new()
    {
        Text = "Automatically accept result with alpha threshold " +
            CustomShowClip.DefaultAlphaThreshold,
        AutoSize = true
    };
    readonly TextBox processingDetails = new() { ReadOnly = true, Multiline = true,
        Height = 88, ScrollBars = ScrollBars.Vertical, TabStop = false,
        Text = "Recorded in show.json after processing completes." };
    readonly CheckBox keepClips = new() { Text = "Keep existing clip divisions",
        AutoSize = true, Checked = true, Visible = false };
    readonly CheckBox keepMasks = new() { Text = "Keep existing masks",
        AutoSize = true, Checked = true, Visible = false };
    readonly CheckedListBox reprocessClips = new() { Height = 126,
        CheckOnClick = true, IntegralHeight = false, Visible = false };
    readonly Button save = new() { Text = "Process and Preview", AutoSize = true };
    readonly Button queue = new() { Text = "Queue", AutoSize = true, Visible = false };
    readonly Button queueBatch = new()
        { Text = "Queue Batch...", AutoSize = true, Visible = false };
    readonly Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    TableLayoutPanel? processingTable;
    int sam2MattingTrackerRow, foregroundConceptsRow, sceneMaskSummaryRow,
        maskEngineRow, sam2ModelRow, mattingDetailRow, vitMatteInferenceDetailRow,
        rvmThresholdRow, rvmMaskRefreshRow, rvmMaskRefreshStrengthRow,
        propSegmenterModelRow, rvmPropEveryFrameRow, debugPropContributionRow,
        matAnyoneMemoryRow, temporalAlphaCleanupRow, temporalAlphaCleanupControlsRow,
        batchSizeRow, autoAcceptRow, processingDetailsRow;
    int nvidiaModeRow, nvidiaTemporalRow, nvidiaInferenceResolutionRow,
        rvmOnnxModelRow, rvmOnnxQualityRow, rvmOnnxTemporalRow;
    int reprocessingRow = -1;
    int reprocessClipsRow = -1;
    List<CustomPerformerProfile> profiles = [];
    CustomPerformerProfile? selectedProfile;
    CustomShowClip[] showClips = [];
    CustomShowClipDetection? clipDetection;
    bool clipLayoutConfirmed;
    bool clipEditorConfirmedThisSession;
    string? originalCoverTitle;
    string? originalCoverPerformerId;
    string? originalCoverModelName;
    string? originalCoverTitleColor;
    string? originalCoverModelFont;
    string? originalCoverTitleFont;
    bool loadingPerformerSelection;
    bool reprocessLayoutChanged;

    internal string? SavedShowId { get; private set; }
    internal bool SavedPerformerChanged { get; private set; }

    internal CustomShowEditorForm(CustomShowStore store,
        CustomShowConfiguration configuration, string? showId, bool reprocess = false,
        CustomShowQueueManager? queueManager = null, string? queueJobId = null,
        CustomShowIncompleteSetupEntry? restoredSetup = null,
        CustomShowQueueJob? queueDraft = null,
        string? queueDraftAssetOwnerId = null)
    {
        this.store = store;
        this.configuration = configuration;
        this.showId = showId;
        this.reprocess = reprocess && showId != null;
        this.queueManager = queueManager;
        this.queueJobId = queueJobId;
        this.queueDraft = queueDraft;
        this.queueDraftAssetOwnerId = queueDraftAssetOwnerId;
        this.restoredSetup = restoredSetup;
        autoAccept.Text =
            "Automatically accept result with alpha threshold " +
            configuration.DefaultAlphaThreshold;
        metadataOnly = showId != null && !this.reprocess &&
            queueJobId == null;
        Text = showId == null ? "Create Custom Show" : this.reprocess
            ? "Reprocess Custom Show" : "Edit Custom Show Metadata";
        ClientSize = new Size(900, 760);
        MinimumSize = new Size(760, 650);
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = false;
        TableLayoutPanel shell = new()
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Panel scrollHost = new()
        {
            Dock = DockStyle.Fill, AutoScroll = true,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        shell.Controls.Add(scrollHost, 0, 0);
        Controls.Add(shell);
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            Padding = new Padding(12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scrollHost.Controls.Add(root);
        TableLayoutPanel basic = SectionTable();
        AddFileRow(basic, "Source video", source,
            "Video files|*.mp4;*.mov;*.mkv;*.avi;*.webm|All files|*.*");
        source.TextChanged += (_, _) =>
        {
            showClips = [];
            clipDetection = null;
            clipLayoutConfirmed = false;
            clipEditorConfirmedThisSession = false;
            UpdateClipButton();
        };
        if (showId == null) AddRow(basic, "", addToExisting);
        AddRow(basic, "Show title", title);
        AddRow(basic, "Model profile", performer,
            FlowVertical(newPerformer, editPerformer));
        AddSection(root, "Show", basic);

        TableLayoutPanel metadata = SectionTable();
        AddRow(metadata, "Description", description);
        AddRow(metadata, "Tags (comma separated)", tags);
        AddRow(metadata, "Release date", releaseDate);
        AddRow(metadata, "Show / recording date", showDate);
        FlowLayoutPanel agePanel = Flow(ageOverride, useAgeOverride);
        AddRow(metadata, "Age at release override", agePanel);
        gender.Items.AddRange(CustomShowStore.GenderValues);
        gender.SelectedItem = "Female";
        AddRow(metadata, "Gender", gender);
        hotness.Items.AddRange(HotnessValues);
        hotness.SelectedItem = "NoNudity";
        AddRow(metadata, "Hotness", hotness);
        AddRow(metadata, "Number of performers", performerCount);
        clipTypes.Items.AddRange(ClipTypeValues);
        clipTypes.SetItemChecked(0, true);
        AddRow(metadata, "Clip types", clipTypes);
        AddFileRow(metadata, "Custom cover (optional)", cover,
            "Images|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*");
        AddFolderRow(metadata, "Photo folder (optional)", photosFolder);
        PopulateCoverFonts();
        AddRow(metadata, "Cover model font", coverModelFont);
        AddRow(metadata, "Cover title font", coverTitleFont);
        AddRow(metadata, "Cover title colour", coverTitleColor);
        AddCollapsibleSection(root, "Metadata (optional)", metadata);

        TableLayoutPanel sections = SectionTable();
        editClips.MinimumSize = new Size(0, 38);
        AddRow(sections, "Split and classify", editClips);
        AddSection(root, "Video sections", sections);

        processingTable = SectionTable();
        preset.Items.AddRange(["RVM Quality (ResNet50)", "RVM Fast (MobileNetV3)"]);
        if (metadataOnly || CustomShowProcessor.IsRvmMatAnyone2Installed(configuration))
            preset.Items.Add("RVM-MatAnyone (automatic RVM initialization)");
        if (metadataOnly || CustomShowProcessor.IsRvmViTMatteSmallInstalled(configuration))
            preset.Items.Add("RVM-ViTMatte S (automatic full RVM masks)");
        if (metadataOnly || CustomShowProcessor.IsRvmViTMatteBaseInstalled(configuration))
            preset.Items.Add("RVM-ViTMatte B (automatic full RVM masks, higher quality)");
        if (metadataOnly || CustomShowProcessor.IsMatAnyone2Installed(configuration))
            preset.Items.Add("MatAnyone 2 (interactive initial mask)");
        if (metadataOnly || CustomShowProcessor.IsViTMatteSmallInstalled(configuration))
            preset.Items.Add("ViTMatte S (editable SAM2 masks)");
        if (metadataOnly || CustomShowProcessor.IsViTMatteBaseInstalled(configuration))
            preset.Items.Add("ViTMatte B (editable SAM2 masks, higher quality)");
        preset.Items.Add("SAM2Matting");
        preset.Items.Add("NVIDIA AI Green Screen (real-time playback)");
        preset.Items.Add("RVM ONNX (real-time playback)");
        preset.SelectedIndex = 0;
        AddRow(processingTable, "Processing algorithm", preset);
        nvidiaMode.Items.AddRange([
            "Quality — chairs are foreground",
            "Performance — chairs are foreground",
            "Quality — chairs are background",
            "Performance — chairs are background"]);
        nvidiaMode.SelectedIndex = 0;
        nvidiaModeRow = AddRow(processingTable, "NVIDIA mode",
            Flow(nvidiaMode, openNvidiaSetup));
        nvidiaTemporalRow = AddRow(processingTable, "NVIDIA filtering",
            nvidiaTemporal);
        nvidiaInferenceResolution.Items.AddRange([
            "540p", "720p (recommended)", "1080p", "Source resolution"]);
        nvidiaInferenceResolution.SelectedIndex = 1;
        nvidiaInferenceResolutionRow = AddRow(processingTable,
            "NVIDIA inference resolution", nvidiaInferenceResolution);
        rvmOnnxModel.Items.AddRange([
            "MobileNetV3 — faster (recommended)",
            "ResNet50 — larger, slightly higher quality"]);
        rvmOnnxModel.SelectedIndex = 0;
        rvmOnnxModelRow = AddRow(processingTable, "RVM ONNX model",
            Flow(rvmOnnxModel, openRvmOnnxSetup));
        rvmOnnxQuality.Items.AddRange([
            "Fast — 256px analysis", "Balanced — 384px analysis (recommended)",
            "Quality — 512px analysis",
            "Full-res refine — source mask, 512px analysis"]);
        rvmOnnxQuality.SelectedIndex = 1;
        rvmOnnxQualityRow = AddRow(processingTable, "RVM ONNX quality",
            rvmOnnxQuality);
        rvmOnnxTemporalRow = AddRow(processingTable, "RVM ONNX filtering",
            rvmOnnxTemporal);
        sam2MattingTracker.Items.AddRange([
            "SAM2.1-T — fastest, interactive initial mask",
            "SAM2.1-B+ — higher-capacity, interactive initial mask",
            "RVM → SAM2.1-T — automatic person mask",
            "RVM → SAM2.1-B+ — automatic person mask"]);
        sam2MattingTracker.SelectedIndex = 3;
        sam2MattingTrackerRow = AddRow(processingTable, "Backbone tracker",
            Flow(sam2MattingTracker, sam2MattingStatus, openSam2MattingSetup));
        foregroundConceptsRow = AddRow(processingTable, "Foreground concepts",
            FlowVertical(foregroundConcepts, conceptHelp));
        sceneMaskSummaryRow = AddRow(processingTable, "Scene prompts", sceneMaskSummary);
        maskEngine.Items.Add("RVM ResNet50 (fast, person-only)");
        IReadOnlyList<string> installedSam2Models = metadataOnly
            ? ["base-plus", "small", "tiny"]
            : CustomShowProcessor.InstalledSam2Models(configuration);
        if (installedSam2Models.Count > 0)
            maskEngine.Items.Insert(0,
                "RVM → SAM2 (automatic person mask, editable)");
        if (installedSam2Models.Count > 0)
            maskEngine.Items.Insert(0, "SAM2 (editable, best robustness)");
        if (metadataOnly || CustomShowProcessor.IsEdgeTamInstalled(configuration))
            maskEngine.Items.Add("EdgeTAM (editable, faster)");
        maskEngine.SelectedIndex = 0;
        maskEngineRow = AddRow(processingTable, "Mask generation", maskEngine);
        foreach (string model in installedSam2Models)
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
        sam2ModelRow = AddRow(processingTable, "SAM2 mask model", sam2Model);
        mattingDetail.Items.AddRange([
            "Very Low (256 px)", "Low (384 px)",
            "Standard (512 px - recommended)", "High (768 px)",
            "Very High (1024 px)", "Full resolution (slowest)"]);
        mattingDetail.SelectedIndex = 2;
        mattingDetailRow = AddRow(processingTable, "Matting detail", mattingDetail);
        vitMatteInferenceDetail.Items.AddRange([
            "Standard (512 px)", "High (768 px)",
            "Very High (1024 px - recommended)", "Full resolution (slowest)"]);
        vitMatteInferenceDetail.SelectedIndex = 2;
        vitMatteInferenceDetailRow = AddRow(processingTable,
            "ViTMatte inference detail", vitMatteInferenceDetail);
        rvmThresholdRow = AddRow(processingTable, "RVM initializer alpha", Flow(rvmInitializerThreshold,
            rvmInitializerThresholdValue));
        rvmMaskRefreshRow = AddRow(processingTable, "RVM mask refresh",
            rvmMatAnyoneMaskRefresh);
        propSegmenterModel.Items.Add(new PropSegmenterChoice(null,
            "No prop model"));
        foreach (PropSegmenterPackage package in PropSegmenterPackage.Installed())
            propSegmenterModel.Items.Add(new PropSegmenterChoice(package,
                package.DisplayName));
        propSegmenterModel.SelectedIndex = 0;
        propSegmenterModelRow = AddRow(processingTable, "Prop model",
            propSegmenterModel);
        rvmPropEveryFrameRow = AddRow(processingTable,
            "Prop model injection", propSegmenterEveryFrame);
        debugPropContributionRow = AddRow(processingTable,
            "Debug contribution", debugPropContribution);
        rvmMaskRefreshStrengthRow = AddRow(processingTable,
            "RVM refresh strength", Flow(rvmMatAnyoneRefreshStrength,
                rvmMatAnyoneRefreshStrengthValue));
        matAnyoneMemoryRow = AddRow(processingTable, "MatAnyone memory",
            FlowVertical(Flow(matAnyoneMaxMemoryFrames, new Label
                {
                    Text = "max frames", AutoSize = true,
                    Margin = new Padding(3, 7, 12, 3)
                }, matAnyoneUseLongTermMemory), matAnyoneMemoryWarning));
        temporalAlphaCleanupRow = AddRow(processingTable,
            "Automatic alpha stabilization", temporalAlphaCleanup);
        temporalAlphaCleanupControlsRow = AddRow(processingTable,
            "Stabilization controls", Flow(new Label
            {
                Text = "Maximum flicker", AutoSize = true,
                Margin = new Padding(0, 7, 3, 3)
            }, temporalAlphaCleanupWindow, new Label
            {
                Text = "frames   Strength", AutoSize = true,
                Margin = new Padding(3, 7, 3, 3)
            }, temporalAlphaCleanupStrength,
                temporalAlphaCleanupStrengthValue, new Label
            {
                Text = "Tracking strength", AutoSize = true,
                Margin = new Padding(12, 7, 3, 3)
            }, temporalAlphaTrackingStrength,
                temporalAlphaTrackingStrengthValue, new Label
            {
                Text = "Alpha threshold", AutoSize = true,
                Margin = new Padding(12, 7, 3, 3)
            }, temporalAlphaCleanupAlphaThreshold));
        sequenceChunk.Items.Add("Auto");
        sequenceChunk.Items.AddRange([1, 2, 3, 4, 6, 8, 12, 16, 24]);
        sequenceChunk.SelectedIndex = 0;
        batchSizeRow = AddRow(processingTable, "Processing batch size", sequenceChunk);
        autoAcceptRow = AddRow(processingTable, "After processing", autoAccept);
        processingDetailsRow = AddRow(processingTable, "Created using", processingDetails);
        if (this.reprocess)
        {
            reprocessingRow = AddRow(processingTable, "Reprocessing", Flow(keepClips, keepMasks));
            reprocessClipsRow = AddRow(processingTable, "Clips to reprocess", reprocessClips);
        }
        AddSection(root, "Processing", processingTable);
        FlowLayoutPanel buttons = Flow(save, queue, cancel);
        buttons.Dock = DockStyle.Fill;
        buttons.Margin = Padding.Empty;
        buttons.Padding = new Padding(12, 8, 12, 10);
        shell.Controls.Add(buttons, 0, 1);
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += (sender, e) =>
        {
            if (RoutesSaveToQueue(metadataOnly, SelectedPreset()))
                QueueJob(sender, e);
            else
                Save(sender, e);
        };
        queue.Click += QueueJob;
        queueBatch.Click += QueueBatch;
        addToExisting.Click += SelectExistingShow;
        preset.SelectedIndexChanged += (_, _) => UpdateProcessingOptions(
            applyRecommendedBatch: true);
        rvmInitializerThreshold.ValueChanged += (_, _) =>
            rvmInitializerThresholdValue.Text = $"{rvmInitializerThreshold.Value}%";
        rvmMatAnyoneRefreshStrength.ValueChanged += (_, _) =>
            rvmMatAnyoneRefreshStrengthValue.Text =
                $"{rvmMatAnyoneRefreshStrength.Value}%";
        rvmMatAnyoneMaskRefresh.CheckedChanged += (_, _) =>
            UpdateProcessingOptions();
        propSegmenterModel.SelectedIndexChanged += (_, _) =>
            UpdateProcessingOptions();
        temporalAlphaCleanup.CheckedChanged += (_, _) =>
            UpdateProcessingOptions();
        temporalAlphaCleanupStrength.ValueChanged += (_, _) =>
            temporalAlphaCleanupStrengthValue.Text =
                $"{temporalAlphaCleanupStrength.Value}%";
        temporalAlphaTrackingStrength.ValueChanged += (_, _) =>
            temporalAlphaTrackingStrengthValue.Text =
                $"{temporalAlphaTrackingStrength.Value}%";
        propSegmenterEveryFrame.CheckedChanged += (_, _) =>
            UpdateProcessingOptions();
        matAnyoneUseLongTermMemory.CheckedChanged += (_, _) =>
        {
            bool useLongTermDefault = matAnyoneUseLongTermMemory.Checked &&
                matAnyoneMaxMemoryFrames.Value <
                    CustomShowConfiguration.MatAnyoneLongTermMinimumMemoryFrames;
            matAnyoneMaxMemoryFrames.Minimum =
                matAnyoneUseLongTermMemory.Checked
                    ? CustomShowConfiguration.MatAnyoneLongTermMinimumMemoryFrames : 2;
            matAnyoneMaxMemoryFrames.Maximum =
                matAnyoneUseLongTermMemory.Checked
                    ? CustomShowConfiguration.MatAnyoneLongTermMaximumMemoryFrames : 30;
            if (useLongTermDefault)
                matAnyoneMaxMemoryFrames.Value =
                    CustomShowConfiguration.MatAnyoneLongTermMaximumMemoryFrames;
        };
        maskEngine.SelectedIndexChanged += (_, _) => UpdateProcessingOptions();
        sam2MattingTracker.SelectedIndexChanged += (_, _) =>
        {
            sceneMaskSummary.Text = SelectedSam2MattingPromptMode() switch
            {
                "rvm-initial-mask" =>
                    "RVM will create one automatic person mask per scene when the job runs",
                _ => "Initial masks: not created"
            };
            UpdateProcessingOptions();
        };
        foregroundConcepts.TextChanged += (_, _) => UpdateProcessingOptions();
        openSam2MattingSetup.Click += (_, _) => OpenSam2MattingSetup();
        openNvidiaSetup.Click += (_, _) =>
        {
            using NvidiaVfxSetupForm setup = new(configuration);
            setup.ShowDialog(this);
        };
        openRvmOnnxSetup.Click += (_, _) =>
        {
            using RvmOnnxSetupForm setup = new();
            setup.ShowDialog(this);
        };
        autoAccept.CheckedChanged += (_, _) => UpdateProcessingOptions();
        newPerformer.Click += (_, _) => OpenPerformer(null);
        editPerformer.Click += (_, _) => OpenPerformer(selectedProfile);
        editClips.Click += EditClips;
        coverTitleColor.Click += ChooseCoverTitleColor;
        keepClips.CheckedChanged += (_, _) =>
        {
            keepMasks.Enabled = keepClips.Checked && RetainedMasksAvailable();
            if (!keepMasks.Enabled) keepMasks.Checked = false;
            UpdateReprocessClipSelectionAvailability();
        };
        performer.SelectedIndexChanged += (_, _) =>
        {
            selectedProfile = performer.SelectedItem as CustomPerformerProfile;
            bool editable = selectedProfile != null &&
                string.IsNullOrWhiteSpace(selectedProfile.IstripperModelId);
            editPerformer.Enabled = editPerformer.Visible = editable;
            if (!loadingPerformerSelection && showId == null &&
                queueJobId == null && appendShowId == null &&
                !string.IsNullOrWhiteSpace(selectedProfile?.Gender))
                gender.SelectedItem = selectedProfile.Gender;
        };
        LoadData();
        if (restoredSetup != null) RestoreIncompleteSetup(restoredSetup);
        if (queueJobId != null) LoadQueueJob(queueJobId);
        else if (queueDraft != null) LoadQueueDraft(queueDraft);
        UpdateProcessingOptions();
        if (showId == null && queueJobId == null && queueDraft == null &&
            restoredSetup == null)
            RestoreProcessingOptions();
        FormClosing += (_, _) =>
        {
            if (showId == null) RememberProcessingOptions();
        };
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

    void PopulateCoverFonts()
    {
        ConfigureCoverFontPreview(coverModelFont);
        ConfigureCoverFontPreview(coverTitleFont);
        string[] families = FontFamily.Families.Select(value => value.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        coverModelFont.Items.AddRange(families);
        coverTitleFont.Items.AddRange(families);
        SelectCoverFont(coverModelFont, "Segoe Script", FontFamily.GenericSansSerif.Name);
        SelectCoverFont(coverTitleFont, "Segoe UI", FontFamily.GenericSansSerif.Name);
    }

    static void ConfigureCoverFontPreview(ComboBox selector)
    {
        selector.DrawMode = DrawMode.OwnerDrawFixed;
        selector.ItemHeight = 30;
        selector.DropDownWidth = Math.Max(selector.DropDownWidth, 320);
        selector.DrawItem += DrawCoverFontItem;
    }

    static void DrawCoverFontItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox selector || e.Index < 0 ||
            e.Index >= selector.Items.Count) return;
        e.DrawBackground();
        string name = selector.Items[e.Index]?.ToString() ?? "";
        Font? previewFont = null;
        try
        {
            using FontFamily family = new(name);
            FontStyle style = family.IsStyleAvailable(FontStyle.Regular)
                ? FontStyle.Regular : family.IsStyleAvailable(FontStyle.Bold)
                ? FontStyle.Bold : family.IsStyleAvailable(FontStyle.Italic)
                ? FontStyle.Italic : FontStyle.Bold | FontStyle.Italic;
            previewFont = new Font(family, 12, style, GraphicsUnit.Point);
        }
        catch (ArgumentException) { }
        Color color = (e.State & DrawItemState.Selected) != 0
            ? SystemColors.HighlightText : selector.ForeColor;
        TextRenderer.DrawText(e.Graphics, name, previewFont ?? selector.Font,
            e.Bounds, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        previewFont?.Dispose();
        e.DrawFocusRectangle();
    }

    static void SelectCoverFont(ComboBox selector, string? requested, string fallback)
    {
        string selected = selector.Items.Cast<string>().FirstOrDefault(value =>
            string.Equals(value, requested, StringComparison.CurrentCultureIgnoreCase)) ??
            selector.Items.Cast<string>().FirstOrDefault(value =>
                string.Equals(value, fallback, StringComparison.CurrentCultureIgnoreCase)) ??
            selector.Items.Cast<string>().First();
        selector.SelectedItem = selected;
    }

    string SelectedCoverModelFont() => coverModelFont.SelectedItem?.ToString() ??
        "Segoe Script";

    string SelectedCoverTitleFont() => coverTitleFont.SelectedItem?.ToString() ??
        "Segoe UI";

    void UpdateColorButton()
    {
        Color color = coverTitleColor.BackColor;
        coverTitleColor.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        coverTitleColor.ForeColor = color.GetBrightness() > .55f ? Color.Black : Color.White;
    }

    void UpdateProcessingOptions(bool applyRecommendedBatch = false)
    {
        string selected = SelectedPreset();
        if (CanProcess && selected is ("quality" or "fast") &&
            (!reprocess || applyRecommendedBatch) && SelectedBatchSize() != 0)
        {
            int preferred = selected == "fast" ? configuration.RvmFastPreferredChunk :
                configuration.RvmQualityPreferredChunk;
            sequenceChunk.SelectedItem = sequenceChunk.Items.OfType<int>()
                .OrderBy(value => Math.Abs(value - preferred)).First();
        }
        else if (CanProcess &&
            selected is ("vitmatte-s" or "vitmatte-b" or "rvm-vitmatte-s" or "rvm-vitmatte-b") &&
            (!reprocess || applyRecommendedBatch) && SelectedBatchSize() != 0)
        {
            int recommended = selected.EndsWith("-b", StringComparison.Ordinal) ?
                configuration.VitMatteBasePreferredBatchSize :
                configuration.VitMatteSmallPreferredBatchSize;
            int closest = sequenceChunk.Items.OfType<int>()
                .OrderBy(value => Math.Abs(value - recommended)).First();
            sequenceChunk.SelectedItem = closest;
        }
        bool rvm = selected is "quality" or "fast";
        bool sam2Matting = selected == Sam2MattingSupport.Algorithm;
        bool nvidia = selected == CustomClipMedia.NvidiaAigsMode;
        bool rvmOnnx = selected == CustomClipMedia.RvmOnnxMode;
        bool realtime = nvidia || rvmOnnx;
        string sam2MattingPromptMode = SelectedSam2MattingPromptMode();
        bool rvmSam2Matting = sam2Matting &&
            sam2MattingPromptMode == "rvm-initial-mask";
        mattingDetail.Enabled = (rvm || selected is "matanyone2" or
            "rvm-matanyone2") && CanProcess;
        vitMatteInferenceDetail.Enabled = selected is
            ("vitmatte-s" or "vitmatte-b" or "rvm-vitmatte-s" or "rvm-vitmatte-b") && CanProcess;
        sequenceChunk.Enabled = (rvm || UsesSam2(selected) ||
            selected is "rvm-vitmatte-s" or "rvm-vitmatte-b") && CanProcess;
        maskEngine.Enabled = UsesSam2(selected) && CanProcess;
        sam2Model.Enabled = (selected == "matanyone2" ||
            (UsesSam2(selected) && SelectedMaskEngine() != "rvm")) &&
            CanProcess && sam2Model.Items.Count > 0;
        bool rvmSam2 = UsesSam2(selected) &&
            SelectedMaskEngine() == "rvm-sam2";
        bool propCompatible = selected is
            "matanyone2" or "rvm-matanyone2" or
            "rvm-vitmatte-s" or "rvm-vitmatte-b" ||
            rvmSam2 || rvmSam2Matting;
        PropSegmenterPackage? selectedPropModel =
            SelectedPropSegmenterPackage();
        propSegmenterModel.Enabled = propCompatible && CanProcess &&
            propSegmenterModel.Items.Count > 1;
        rvmInitializerThreshold.Enabled =
            (selected is ("rvm-matanyone2" or "rvm-vitmatte-s" or "rvm-vitmatte-b") || rvmSam2 ||
                rvmSam2Matting) &&
            CanProcess;
        rvmInitializerThresholdValue.Enabled = rvmInitializerThreshold.Enabled;
        rvmMatAnyoneMaskRefresh.Enabled = selected == "rvm-matanyone2" && CanProcess;
        bool rvmVitMatte = selected is "rvm-vitmatte-s" or "rvm-vitmatte-b";
        propSegmenterEveryFrame.Enabled =
            (selected == "matanyone2" || rvmVitMatte ||
                rvmMatAnyoneMaskRefresh.Enabled &&
                rvmMatAnyoneMaskRefresh.Checked) &&
            selectedPropModel != null &&
            selectedPropModel.ManifestSchemaVersion != 2;
        if (selectedPropModel == null ||
            selectedPropModel.ManifestSchemaVersion == 2)
            propSegmenterEveryFrame.Checked = false;
        debugPropContribution.Enabled = propSegmenterEveryFrame.Enabled &&
            propSegmenterEveryFrame.Checked;
        if (!debugPropContribution.Enabled)
            debugPropContribution.Checked = false;
        rvmMatAnyoneRefreshStrength.Enabled =
            rvmMatAnyoneRefreshStrengthValue.Enabled =
                rvmMatAnyoneMaskRefresh.Enabled && rvmMatAnyoneMaskRefresh.Checked;
        bool matAnyone = selected is "matanyone2" or "rvm-matanyone2";
        matAnyoneMaxMemoryFrames.Enabled = matAnyoneUseLongTermMemory.Enabled =
            matAnyone && CanProcess;
        temporalAlphaCleanup.Enabled = CanProcess && !realtime;
        temporalAlphaCleanupWindow.Enabled =
            temporalAlphaCleanupStrength.Enabled =
            temporalAlphaCleanupStrengthValue.Enabled =
            temporalAlphaTrackingStrength.Enabled =
            temporalAlphaTrackingStrengthValue.Enabled =
            temporalAlphaCleanupAlphaThreshold.Enabled =
                CanProcess && temporalAlphaCleanup.Checked;
        autoAccept.Enabled = CanProcess && !realtime;
        if (processingTable != null)
        {
            bool usesMasks = UsesSam2(selected);
            bool usesSamModel = selected == "matanyone2" ||
                usesMasks && SelectedMaskEngine() != "rvm";
            SetRowVisible(processingTable, maskEngineRow, usesMasks);
            SetRowVisible(processingTable, sam2ModelRow, usesSamModel);
            SetRowVisible(processingTable, mattingDetailRow,
                rvm || selected is "matanyone2" or "rvm-matanyone2");
            SetRowVisible(processingTable, vitMatteInferenceDetailRow,
                selected is "vitmatte-s" or "vitmatte-b" or "rvm-vitmatte-s" or "rvm-vitmatte-b");
            SetRowVisible(processingTable, rvmThresholdRow,
                selected is "rvm-matanyone2" or "rvm-vitmatte-s" or "rvm-vitmatte-b" || rvmSam2 ||
                rvmSam2Matting);
            SetRowVisible(processingTable, rvmMaskRefreshRow,
                selected == "rvm-matanyone2");
            SetRowVisible(processingTable, propSegmenterModelRow,
                propCompatible);
            SetRowVisible(processingTable, rvmPropEveryFrameRow,
                selected is "matanyone2" or "rvm-matanyone2" || rvmVitMatte);
            SetRowVisible(processingTable, debugPropContributionRow,
                selected is "matanyone2" or "rvm-matanyone2" || rvmVitMatte);
            SetRowVisible(processingTable, rvmMaskRefreshStrengthRow,
                selected == "rvm-matanyone2");
            SetRowVisible(processingTable, matAnyoneMemoryRow, matAnyone);
            SetRowVisible(processingTable, temporalAlphaCleanupRow, !realtime);
            SetRowVisible(processingTable, temporalAlphaCleanupControlsRow,
                temporalAlphaCleanup.Checked && !realtime);
            SetRowVisible(processingTable, batchSizeRow, rvm || usesMasks ||
                selected is "rvm-vitmatte-s" or "rvm-vitmatte-b");
            SetRowVisible(processingTable, processingDetailsRow,
                processingDetails.Text !=
                    "Recorded in show.json after processing completes.");
            SetRowVisible(processingTable, sam2MattingTrackerRow, sam2Matting);
            SetRowVisible(processingTable, foregroundConceptsRow, false);
            SetRowVisible(processingTable, sceneMaskSummaryRow,
                sam2Matting);
            SetRowVisible(processingTable, autoAcceptRow,
                !sam2Matting && !realtime);
            SetRowVisible(processingTable, nvidiaModeRow, nvidia);
            SetRowVisible(processingTable, nvidiaTemporalRow, nvidia);
            SetRowVisible(processingTable, nvidiaInferenceResolutionRow, nvidia);
            SetRowVisible(processingTable, rvmOnnxModelRow, rvmOnnx);
            SetRowVisible(processingTable, rvmOnnxQualityRow, rvmOnnx);
            SetRowVisible(processingTable, rvmOnnxTemporalRow, rvmOnnx);
            if (reprocessingRow >= 0)
                SetRowVisible(processingTable, reprocessingRow, reprocess);
        }
        keepMasks.Visible = !realtime;
        bool sam2Installed = !sam2Matting || Sam2MattingSupport.IsInstalled(
            configuration, SelectedSam2MattingTracker());
        bool rvmInstalled = !rvmSam2Matting ||
            CustomShowProcessor.IsRvmInitialMaskInstalled(configuration);
        bool installed = sam2Installed && rvmInstalled;
        sam2MattingStatus.Text = installed ? "Installed" : !sam2Installed
            ? "Setup required" : "RVM setup required";
        sam2MattingStatus.ForeColor = installed ? Color.ForestGreen : Color.DarkOrange;
        openSam2MattingSetup.Visible = sam2Matting && !sam2Installed;
        string? queueAction = sam2Matting
            ? rvmSam2Matting ? "Queue" : "Mask Scenes and Queue"
            : QueueAction(selected, autoAccept.Checked);
        queue.Visible = queueManager != null && CanProcess && queueAction != null;
        queueBatch.Visible = false;
        if (queueAction != null) queue.Text = queueAction;
        if (CanProcess)
        {
            save.Visible = sam2Matting ||
                queueJobId == null && queueDraft == null;
            if (sam2Matting)
                save.Text = rvmSam2Matting
                    ? (reprocess ? "Reprocess and Preview" :
                    Appending ? "Process and Add Clips" : "Process and Preview") :
                    "Mask Scenes and Process";
            else if (realtime)
                save.Text = "Encode and Preview";
        }
    }

    void OpenSam2MattingSetup()
    {
        string script = Path.Combine(AppContext.BaseDirectory, "custom-shows",
            "setup-sam2matting.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show(this, "The SAM2Matting setup script is missing:\n" + script,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using CustomShowSam2MattingSetupForm setup = new(script);
        if (setup.ShowDialog(this) != DialogResult.OK) return;
        configuration.Sam2MattingPythonExecutable =
            CustomShowConfiguration.FindSam2MattingPythonExecutable();
        configuration.Save();
        UpdateProcessingOptions();
    }

    bool CanProcess => showId == null || reprocess;
    bool Appending => showId == null && appendShowId != null;

    int SelectedMattingResolution() => mattingDetail.SelectedIndex switch
    {
        0 => 256, 1 => 384, 3 => 768, 4 => 1024, 5 => 0, _ => 512
    };

    int SelectedVitMatteInferenceResolution() => vitMatteInferenceDetail.SelectedIndex switch
    {
        0 => 512, 1 => 768, 3 => 0, _ => 1024
    };

    int SelectedBatchSize() => sequenceChunk.SelectedItem is int value ? value : 0;

    void SelectBatchSize(int value)
    {
        if (value == 0) sequenceChunk.SelectedIndex = 0;
        else if (sequenceChunk.Items.Contains(value)) sequenceChunk.SelectedItem = value;
    }

    string SelectedSam2Model()
    {
        string selected = sam2Model.SelectedItem?.ToString() ?? "Base+";
        if (selected.StartsWith("Small", StringComparison.Ordinal)) return "small";
        if (selected.StartsWith("Tiny", StringComparison.Ordinal)) return "tiny";
        return "base-plus";
    }

    string SelectedSam2MattingTracker() => sam2MattingTracker.SelectedIndex switch
    {
        0 or 2 => "sam2.1-tiny",
        1 or 3 => "sam2.1-base-plus",
        _ => "sam2.1-base-plus"
    };

    string SelectedSam2MattingPromptMode() => sam2MattingTracker.SelectedIndex switch
    {
        2 or 3 => "rvm-initial-mask",
        _ => "initial-mask"
    };

    string SelectedMaskEngine()
    {
        string selected = maskEngine.SelectedItem?.ToString() ?? "RVM";
        if (selected.StartsWith("RVM → SAM2", StringComparison.Ordinal))
            return "rvm-sam2";
        if (selected.StartsWith("EdgeTAM", StringComparison.Ordinal)) return "edgetam";
        if (selected.StartsWith("SAM2", StringComparison.Ordinal)) return "sam2";
        return "rvm";
    }

    string SelectedPreset()
    {
        string selected = preset.SelectedItem?.ToString() ?? "";
        if (selected.StartsWith("RVM Fast", StringComparison.Ordinal)) return "fast";
        if (selected.StartsWith("RVM-MatAnyone", StringComparison.Ordinal))
            return "rvm-matanyone2";
        if (selected.StartsWith("RVM-ViTMatte S", StringComparison.Ordinal))
            return "rvm-vitmatte-s";
        if (selected.StartsWith("RVM-ViTMatte B", StringComparison.Ordinal))
            return "rvm-vitmatte-b";
        if (selected.StartsWith("MatAnyone", StringComparison.Ordinal)) return "matanyone2";
        if (selected.StartsWith("ViTMatte S", StringComparison.Ordinal)) return "vitmatte-s";
        if (selected.StartsWith("ViTMatte B", StringComparison.Ordinal)) return "vitmatte-b";
        if (selected.StartsWith("SAM2Matting", StringComparison.Ordinal))
            return Sam2MattingSupport.Algorithm;
        if (selected.StartsWith("NVIDIA AI Green Screen", StringComparison.Ordinal))
            return CustomClipMedia.NvidiaAigsMode;
        if (selected.StartsWith("RVM ONNX", StringComparison.Ordinal))
            return CustomClipMedia.RvmOnnxMode;
        return "quality";
    }

    static bool UsesSam2(string selectedPreset) => selectedPreset is
        "vitmatte-s" or "vitmatte-b";

    internal static string? QueueAction(string algorithm, bool autoAccept) =>
        algorithm is "sam2matting" or "nvidia-aigs" or "rvm-onnx"
            ? "Queue" : !autoAccept ? null : algorithm switch
        {
            "quality" or "fast" or "rvm-matanyone2" or "rvm-vitmatte-s" or "rvm-vitmatte-b" => "Queue",
            "matanyone2" => "Mask and Queue",
            _ => null
        };

    void LoadQueueJob(string id)
    {
        CustomShowQueueJob job = queueManager?.Find(id) ??
            throw new InvalidOperationException("The queue job no longer exists.");
        LoadQueueDraft(job);
    }

    void LoadQueueDraft(CustomShowQueueJob job)
    {
        source.Text = job.SourcePath;
        if (!profiles.Any(profile => profile.Id == job.Performer.Id))
        {
            profiles.Add(CloneQueueValue(job.Performer));
            performer.DataSource = null;
            performer.DisplayMember = nameof(CustomPerformerProfile.DisplayName);
            performer.DataSource = profiles.OrderBy(profile => profile.ModelName).ToList();
        }
        PopulateFromShow(job.Manifest, editingExisting: false);
        showClips = JsonSerializer.Deserialize<CustomShowClip[]>(JsonSerializer.Serialize(
            job.Clips, CustomShowStore.JsonOptions), CustomShowStore.JsonOptions) ?? [];
        clipLayoutConfirmed = showClips.Length > 0;
        if (job.Operation == CustomShowQueueOperation.Reprocess)
            PopulateReprocessClips(job.ReprocessClipIds.Length == 0 ? null :
                job.ReprocessClipIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
        clipDetection = job.Manifest.ClipDetection;
        appendShowId = job.Operation == CustomShowQueueOperation.Append
            ? job.TargetShowId : null;
        Text = "Edit Queued Custom Show";
        queue.Text = job.Manifest.Processing?.Algorithm == "matanyone2"
            ? "Mask and Queue" : job.Manifest.Processing?.Algorithm == "sam2matting" &&
                job.Manifest.Processing.PromptMode == "initial-mask"
                ? "Mask Scenes and Update Queue Job" : "Update Queue Job";
        if (job.Manifest.Processing?.Algorithm == "sam2matting")
            sceneMaskSummary.Text = job.Manifest.Processing.PromptMode == "text-concepts"
                ? "Automatic text prompts"
                : $"{job.ScenePrompts.Length} of {job.Manifest.Processing.Scenes.Length} " +
                    (job.Manifest.Processing.PromptMode == "rvm-initial-mask"
                        ? job.ScenePrompts.Length == 0
                            ? "automatic RVM scene masks will be created when the job runs"
                            : "automatic RVM scene masks ready"
                        : "scenes ready");
        AcceptButton = queue;
        autoAccept.Checked = true;
        string assetOwner = queueDraftAssetOwnerId ?? job.Id;
        if (job.CoverAsset != null)
            cover.Text = Path.Combine(queueManager!.AssetFolder(assetOwner),
                job.CoverAsset.Replace('/', Path.DirectorySeparatorChar));
        UpdateClipButton();
    }

    void LoadData()
    {
        loadingPerformerSelection = true;
        profiles = store.LoadPerformers().ToList();
        AddIstripperModels(profiles);
        performer.DataSource = null;
        performer.DisplayMember = nameof(CustomPerformerProfile.DisplayName);
        performer.DataSource = profiles.OrderBy(profile => profile.ModelName).ToList();
        if (showId != null)
            PopulateFromShow(store.LoadManifest(showId), editingExisting: true);
        loadingPerformerSelection = false;
        selectedProfile = performer.SelectedItem as CustomPerformerProfile;
        if (showId == null && queueJobId == null &&
            !string.IsNullOrWhiteSpace(selectedProfile?.Gender))
            gender.SelectedItem = selectedProfile.Gender;
    }

    void RestoreIncompleteSetup(CustomShowIncompleteSetupEntry entry)
    {
        CustomShowManifest restored = entry.Setup.Show;
        source.Text = restored.Source.Path;
        PopulateFromShow(restored, editingExisting: false);
        showClips = JsonSerializer.Deserialize<CustomShowClip[]>(
            JsonSerializer.Serialize(restored.Clips, CustomShowStore.JsonOptions),
            CustomShowStore.JsonOptions) ?? [];
        clipLayoutConfirmed = showClips.Length > 0;
        clipDetection = restored.ClipDetection;
        Text = "Restore Incomplete Custom Show";
        save.Text = "Continue Processing";
        UpdateClipButton();
        UpdateProcessingOptions();
    }

    void SelectExistingShow(object? sender, EventArgs e)
    {
        using CustomShowPickerForm picker = new(store);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.ShowId == null) return;
        appendShowId = picker.ShowId;
        CustomShowManifest show = store.LoadManifest(appendShowId);
        PopulateFromShow(show, editingExisting: false);
        showClips = [];
        clipDetection = null;
        clipLayoutConfirmed = false;
        clipEditorConfirmedThisSession = false;
        addToExisting.Text = $"Adding to: {show.Title}";
        save.Text = "Process and Add Clips";
        Text = "Add Video to Existing Custom Show";
        UpdateClipButton();
        UpdateProcessingOptions();
    }

    void PopulateFromShow(CustomShowManifest show, bool editingExisting)
    {
        if (editingExisting)
        {
            source.Text = SourceVideo(show);
            save.Text = reprocess ? "Reprocess and Preview" : "Save Metadata";
            originalCoverTitle = show.Title;
            originalCoverPerformerId = show.PerformerId;
            originalCoverModelName = profiles.FirstOrDefault(profile =>
                profile.Id == show.PerformerId)?.ModelName;
            originalCoverTitleColor = show.Media.CoverTitleColor;
            originalCoverModelFont = show.Media.CoverModelFont;
            originalCoverTitleFont = show.Media.CoverTitleFont;
        }
        title.Text = show.Title;
        description.Text = show.Description;
        tags.Text = string.Join(", ", show.Tags);
        releaseDate.Value = show.ReleaseDate.ToDateTime(TimeOnly.MinValue);
        showDate.Checked = show.ShowDate != null;
        if (show.ShowDate is DateOnly date) showDate.Value = date.ToDateTime(TimeOnly.MinValue);
        useAgeOverride.Checked = show.AgeAtReleaseOverride != null;
        if (show.AgeAtReleaseOverride is int age) ageOverride.Value = age;
        gender.SelectedItem = show.Gender;
        hotness.SelectedItem = show.Hotness;
        performerCount.Value = show.PerformerCount;
        coverTitleColor.BackColor = ColorTranslator.FromHtml(
            show.Media.CoverTitleColor);
        SelectCoverFont(coverModelFont, show.Media.CoverModelFont, "Segoe Script");
        SelectCoverFont(coverTitleFont, show.Media.CoverTitleFont, "Segoe UI");
        photosFolder.Text = show.Media.PhotosFolder;
        if (editingExisting && !show.Media.AutoGeneratedCover &&
            !string.IsNullOrWhiteSpace(show.Media.CoverSource))
            cover.Text = CustomShowStore.ResolveRelative(
                Path.Combine(store.ShowsFolder, show.Id), show.Media.CoverSource);
        coverTitleColor.Enabled = true;
        for (int i = 0; i < clipTypes.Items.Count; i++)
            clipTypes.SetItemChecked(i, show.ClipTypes.Contains(clipTypes.Items[i]!.ToString()));
        performer.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == show.PerformerId);
        if (editingExisting)
        {
            showClips = show.Clips;
            clipDetection = show.ClipDetection;
            clipLayoutConfirmed = showClips.Length > 0;
            PopulateReprocessClips();
        }
        if (show.Processing is CustomShowProcessing processing)
        {
            processingDetails.Text = DescribeProcessing(processing);
            processingDetails.Visible = true;
            SelectProcessingOptions(processing, show.Clips);
        }
        keepClips.Visible = keepMasks.Visible = editingExisting && reprocess;
        keepMasks.Enabled = reprocess && RetainedMasksAvailable();
        keepMasks.Checked = keepMasks.Enabled;
        if (reprocess && !keepMasks.Enabled)
            keepMasks.Text = "Keep existing masks (not retained for this show)";
        if (editingExisting && !reprocess)
            preset.Enabled = maskEngine.Enabled = propSegmenterModel.Enabled =
                mattingDetail.Enabled =
                vitMatteInferenceDetail.Enabled =
                sequenceChunk.Enabled = sam2Model.Enabled =
                rvmInitializerThreshold.Enabled = rvmInitializerThresholdValue.Enabled =
                rvmMatAnyoneRefreshStrength.Enabled =
                rvmMatAnyoneRefreshStrengthValue.Enabled =
                temporalAlphaCleanup.Enabled =
                temporalAlphaCleanupWindow.Enabled =
                temporalAlphaCleanupStrength.Enabled =
                temporalAlphaCleanupStrengthValue.Enabled =
                temporalAlphaTrackingStrength.Enabled =
                temporalAlphaTrackingStrengthValue.Enabled =
                temporalAlphaCleanupAlphaThreshold.Enabled =
                autoAccept.Enabled = nvidiaMode.Enabled = nvidiaTemporal.Enabled =
                nvidiaInferenceResolution.Enabled = openNvidiaSetup.Enabled = false;
        UpdateClipButton();
        UpdateReprocessClipSelectionAvailability();
    }

    void PopulateReprocessClips(IReadOnlyCollection<string>? selectedIds = null)
    {
        if (!reprocess) return;
        selectedIds ??= showClips.Where(clip => clip.Included)
            .Select(clip => clip.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        reprocessClips.Items.Clear();
        int playable = 0;
        for (int index = 0; index < showClips.Length; index++)
        {
            CustomShowClip clip = showClips[index];
            if (!clip.Included) continue;
            playable++;
            reprocessClips.Items.Add(new ReprocessClipChoice(clip.Id,
                $"Clip {playable}: {FormatTime(clip.StartMs)}–{FormatTime(clip.EndMs)}  " +
                $"{clip.Hotness}; {string.Join(", ", clip.ClipTypes)}"),
                selectedIds.Contains(clip.Id));
        }
    }

    void UpdateReprocessClipSelectionAvailability()
    {
        if (!reprocess || processingTable == null || reprocessClipsRow < 0) return;
        bool available = keepClips.Checked && !reprocessLayoutChanged;
        SetRowVisible(processingTable, reprocessClipsRow, available);
        reprocessClips.Visible = available;
    }

    string[] SelectedReprocessClipIds() => !reprocess ||
        !keepClips.Checked || reprocessLayoutChanged
        ? showClips.Where(clip => clip.Included).Select(clip => clip.Id).ToArray()
        : reprocessClips.CheckedItems.Cast<ReprocessClipChoice>()
            .Select(choice => choice.Id).ToArray();

    static string FormatTime(long milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(@"h\:mm\:ss\.fff");

    sealed record ReprocessClipChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    sealed record PropSegmenterChoice(PropSegmenterPackage? Package, string Label)
    {
        public override string ToString() => Label;
    }

    PropSegmenterPackage? SelectedPropSegmenterPackage() =>
        (propSegmenterModel.SelectedItem as PropSegmenterChoice)?.Package;

    void SelectPropSegmenter(string? modelId)
    {
        PropSegmenterChoice? choice = propSegmenterModel.Items
            .OfType<PropSegmenterChoice>().FirstOrDefault(value =>
                modelId != null && value.Package?.ModelId == modelId) ??
            propSegmenterModel.Items.OfType<PropSegmenterChoice>()
                .First(value => value.Package == null);
        propSegmenterModel.SelectedItem = choice;
    }

    void SelectProcessingOptions(CustomShowProcessing processing,
        IReadOnlyList<CustomShowClip> processingClips)
    {
        string prefix = processing.Algorithm switch
        {
            "fast" => "RVM Fast", "matanyone2" => "MatAnyone",
            "rvm-matanyone2" => "RVM-MatAnyone",
            "rvm-vitmatte-s" => "RVM-ViTMatte S",
            "rvm-vitmatte-b" => "RVM-ViTMatte B",
            "vitmatte-s" => "ViTMatte S",
            "vitmatte-b" => "ViTMatte B", "sam2matting" => "SAM2Matting",
            "nvidia-aigs" => "NVIDIA AI Green Screen",
            "rvm-onnx" => "RVM ONNX",
            _ => "RVM Quality"
        };
        SelectStartingWith(preset, prefix);
        rvmOnnxQuality.SelectedIndex = RvmOnnxSupport.QualityIndex(
            configuration.LastRvmOnnxQuality);
        rvmOnnxModel.SelectedIndex = configuration.LastRvmOnnxModel ==
            RvmOnnxSupport.ResNet50 ? 1 : 0;
        rvmOnnxTemporal.Checked = configuration.LastRvmOnnxTemporal;
        sam2MattingTracker.SelectedIndex = processing.Tracker switch
        {
            "sam2.1-tiny" when processing.PromptMode == "rvm-initial-mask" => 2,
            "sam2.1-base-plus" when processing.PromptMode == "rvm-initial-mask" => 3,
            "sam2.1-tiny" => 0, "sam2.1-base-plus" => 1,
            // Retired/unknown trackers reopen on the automatic Base+ path.
            _ => 3
        };
        if (processing.Algorithm == "sam2matting")
            foregroundConcepts.Text = string.Join(Environment.NewLine,
                processing.ForegroundConcepts);
        SelectStartingWith(maskEngine, processing.MaskEngine switch
        {
            "sam2" => "SAM2", "rvm-sam2" => "RVM → SAM2",
            "edgetam" => "EdgeTAM", _ => "RVM"
        });
        SelectStartingWith(sam2Model, processing.Sam2Model switch
        {
            "small" => "Small", "tiny" => "Tiny", _ => "Base+"
        });
        mattingDetail.SelectedIndex = processing.MattingDetailPx switch
        {
            256 => 0, 384 => 1, 768 => 3, 1024 => 4, 0 => 5, _ => 2
        };
        vitMatteInferenceDetail.SelectedIndex =
            (processing.VitMatteInferenceDetailPx ?? 1024) switch
            {
                512 => 0, 768 => 1, 0 => 3, _ => 2
            };
        SelectBatchSize(processing.BatchSize);
        rvmInitializerThreshold.Value = Math.Clamp(
            processing.RvmInitializerAlphaThresholdPercent ?? 40,
            rvmInitializerThreshold.Minimum, rvmInitializerThreshold.Maximum);
        rvmMatAnyoneMaskRefresh.Checked = processing.RvmMatAnyoneMaskRefresh;
        propSegmenterEveryFrame.Checked =
            processing.PropSegmenterEveryFrame;
        debugPropContribution.Checked = processing.DebugPropContribution;
        rvmMatAnyoneRefreshStrength.Value = Math.Clamp(
            processing.RvmMatAnyoneRefreshStrengthPercent,
            rvmMatAnyoneRefreshStrength.Minimum,
            rvmMatAnyoneRefreshStrength.Maximum);
        matAnyoneUseLongTermMemory.Checked =
            processing.MatAnyoneUseLongTermMemory;
        matAnyoneMaxMemoryFrames.Value = Math.Clamp(
            processing.MatAnyoneMaxMemoryFrames,
            (int)matAnyoneMaxMemoryFrames.Minimum,
            (int)matAnyoneMaxMemoryFrames.Maximum);
        temporalAlphaCleanup.Checked = processing.TemporalAlphaCleanup;
        temporalAlphaCleanupWindow.Value = Math.Clamp(
            processing.TemporalAlphaCleanupWindowFrames,
            (int)temporalAlphaCleanupWindow.Minimum,
            (int)temporalAlphaCleanupWindow.Maximum);
        temporalAlphaCleanupStrength.Value = Math.Clamp(
            processing.TemporalAlphaCleanupStrengthPercent,
            temporalAlphaCleanupStrength.Minimum,
            temporalAlphaCleanupStrength.Maximum);
        temporalAlphaTrackingStrength.Value = Math.Clamp(
            processing.TemporalAlphaTrackingStrengthPercent,
            temporalAlphaTrackingStrength.Minimum,
            temporalAlphaTrackingStrength.Maximum);
        temporalAlphaCleanupAlphaThreshold.Value = Math.Clamp(
            processing.TemporalAlphaCleanupAlphaThreshold,
            (int)temporalAlphaCleanupAlphaThreshold.Minimum,
            (int)temporalAlphaCleanupAlphaThreshold.Maximum);
        autoAccept.Checked = processing.AutoAcceptedAlphaThreshold.HasValue;
        SelectPropSegmenter(processing.PropSegmenterModelId);
        CustomNvidiaSettings? nvidia = processingClips.FirstOrDefault(value =>
            value.Media?.Mode == CustomClipMedia.NvidiaAigsMode)?.Nvidia;
        if (nvidia != null)
        {
            nvidiaMode.SelectedIndex = Math.Clamp(nvidia.Mode, 0, 3);
            nvidiaTemporal.Checked = nvidia.Temporal;
            nvidiaInferenceResolution.SelectedIndex =
                nvidia.InferenceResolution switch
                { "540p" => 0, "1080p" => 2, "source" => 3, _ => 1 };
        }
        CustomRvmOnnxSettings? rvmOnnx = processingClips.FirstOrDefault(value =>
            value.Media?.Mode == CustomClipMedia.RvmOnnxMode)?.RvmOnnx;
        if (rvmOnnx != null)
        {
            rvmOnnxModel.SelectedIndex = rvmOnnx.Model ==
                RvmOnnxSupport.ResNet50 ? 1 : 0;
            rvmOnnxQuality.SelectedIndex = RvmOnnxSupport.QualityIndex(
                rvmOnnx.Quality);
            rvmOnnxTemporal.Checked = rvmOnnx.Temporal;
        }
    }

    static void SelectStartingWith(ComboBox combo, string prefix)
    {
        int index = combo.Items.Cast<object>().Select((value, index) => (value, index))
            .FirstOrDefault(item => item.value.ToString()!.StartsWith(
                prefix, StringComparison.Ordinal)).index;
        if (index >= 0 && index < combo.Items.Count && combo.Items[index]!.ToString()!
            .StartsWith(prefix, StringComparison.Ordinal)) combo.SelectedIndex = index;
    }

    void RestoreProcessingOptions()
    {
        string prefix = configuration.LastProcessingAlgorithm switch
        {
            "fast" => "RVM Fast",
            "rvm-matanyone2" => "RVM-MatAnyone",
            "rvm-vitmatte-s" => "RVM-ViTMatte S",
            "rvm-vitmatte-b" => "RVM-ViTMatte B",
            "matanyone2" => "MatAnyone",
            "vitmatte-s" => "ViTMatte S",
            "vitmatte-b" => "ViTMatte B",
            "sam2matting" => "SAM2Matting",
            "nvidia-aigs" => "NVIDIA AI Green Screen",
            "rvm-onnx" => "RVM ONNX",
            _ => "RVM Quality"
        };
        SelectStartingWith(preset, prefix);
        rvmOnnxModel.SelectedIndex = configuration.LastRvmOnnxModel ==
            RvmOnnxSupport.ResNet50 ? 1 : 0;
        // New jobs and retired tracker preferences start on automatic Base+.
        sam2MattingTracker.SelectedIndex =
            configuration.LastSam2MattingTracker == "sam2.1-tiny" ? 2 : 3;
        SelectStartingWith(maskEngine, configuration.LastMaskEngine switch
        {
            "edgetam" => "EdgeTAM", "rvm" => "RVM",
            "rvm-sam2" => "RVM → SAM2", _ => "SAM2"
        });
        SelectStartingWith(sam2Model, configuration.LastSam2Model switch
        {
            "small" => "Small", "tiny" => "Tiny", _ => "Base+"
        });
        mattingDetail.SelectedIndex = configuration.LastMattingDetailPx switch
        {
            256 => 0, 384 => 1, 768 => 3, 1024 => 4, 0 => 5, _ => 2
        };
        vitMatteInferenceDetail.SelectedIndex =
            configuration.LastVitMatteInferenceDetailPx switch
            {
                512 => 0, 768 => 1, 0 => 3, _ => 2
            };
        SelectBatchSize(configuration.LastProcessingBatchSize);
        rvmInitializerThreshold.Value = Math.Clamp(
            configuration.LastRvmInitializerAlphaThresholdPercent,
            rvmInitializerThreshold.Minimum, rvmInitializerThreshold.Maximum);
        rvmMatAnyoneMaskRefresh.Checked = configuration.LastRvmMatAnyoneMaskRefresh;
        propSegmenterEveryFrame.Checked =
            configuration.LastPropSegmenterEveryFrame;
        debugPropContribution.Checked = configuration.LastDebugPropContribution;
        rvmMatAnyoneRefreshStrength.Value = Math.Clamp(
            configuration.LastRvmMatAnyoneRefreshStrengthPercent,
            rvmMatAnyoneRefreshStrength.Minimum,
            rvmMatAnyoneRefreshStrength.Maximum);
        matAnyoneUseLongTermMemory.Checked =
            configuration.LastMatAnyoneUseLongTermMemory;
        matAnyoneMaxMemoryFrames.Value = Math.Clamp(
            configuration.LastMatAnyoneMaxMemoryFrames,
            (int)matAnyoneMaxMemoryFrames.Minimum,
            (int)matAnyoneMaxMemoryFrames.Maximum);
        temporalAlphaCleanup.Checked = configuration.LastTemporalAlphaCleanup;
        temporalAlphaCleanupWindow.Value = Math.Clamp(
            configuration.LastTemporalAlphaCleanupWindowFrames,
            (int)temporalAlphaCleanupWindow.Minimum,
            (int)temporalAlphaCleanupWindow.Maximum);
        temporalAlphaCleanupStrength.Value = Math.Clamp(
            configuration.LastTemporalAlphaCleanupStrengthPercent,
            temporalAlphaCleanupStrength.Minimum,
            temporalAlphaCleanupStrength.Maximum);
        temporalAlphaTrackingStrength.Value = Math.Clamp(
            configuration.LastTemporalAlphaTrackingStrengthPercent,
            temporalAlphaTrackingStrength.Minimum,
            temporalAlphaTrackingStrength.Maximum);
        temporalAlphaCleanupAlphaThreshold.Value = Math.Clamp(
            configuration.LastTemporalAlphaCleanupAlphaThreshold,
            (int)temporalAlphaCleanupAlphaThreshold.Minimum,
            (int)temporalAlphaCleanupAlphaThreshold.Maximum);
        autoAccept.Checked = configuration.LastAutoAcceptAlphaThreshold;
        string? rememberedPropModel = configuration.LastPropSegmenterModelId;
        if (rememberedPropModel == null && configuration.PropSegmenterEnabled)
            rememberedPropModel = configuration.ActivePropSegmenterModelId;
        SelectPropSegmenter(rememberedPropModel);
        UpdateProcessingOptions();
        SelectBatchSize(configuration.LastProcessingBatchSize);
    }

    void RememberProcessingOptions()
    {
        configuration.LastProcessingAlgorithm = SelectedPreset();
        configuration.LastRvmOnnxModel = rvmOnnxModel.SelectedIndex == 1
            ? RvmOnnxSupport.ResNet50 : RvmOnnxSupport.MobileNetV3;
        configuration.LastRvmOnnxQuality = RvmOnnxSupport.QualityFromIndex(
            rvmOnnxQuality.SelectedIndex);
        configuration.LastRvmOnnxTemporal = rvmOnnxTemporal.Checked;
        configuration.LastSam2MattingTracker = SelectedSam2MattingTracker();
        configuration.LastMaskEngine = SelectedMaskEngine();
        configuration.LastSam2Model = SelectedSam2Model();
        configuration.LastMattingDetailPx = SelectedMattingResolution();
        configuration.LastVitMatteInferenceDetailPx =
            SelectedVitMatteInferenceResolution();
        configuration.LastProcessingBatchSize = SelectedBatchSize();
        configuration.LastRvmInitializerAlphaThresholdPercent =
            rvmInitializerThreshold.Value;
        configuration.LastRvmMatAnyoneMaskRefresh = rvmMatAnyoneMaskRefresh.Checked;
        configuration.LastPropSegmenterEveryFrame =
            propSegmenterEveryFrame.Checked;
        configuration.LastPropSegmenterModelId =
            SelectedPropSegmenterPackage()?.ModelId ?? "";
        configuration.LastDebugPropContribution = debugPropContribution.Checked;
        configuration.LastRvmMatAnyoneRefreshStrengthPercent =
            rvmMatAnyoneRefreshStrength.Value;
        configuration.LastMatAnyoneMaxMemoryFrames =
            (int)matAnyoneMaxMemoryFrames.Value;
        configuration.LastMatAnyoneUseLongTermMemory =
            matAnyoneUseLongTermMemory.Checked;
        configuration.LastTemporalAlphaCleanup = temporalAlphaCleanup.Checked;
        configuration.LastTemporalAlphaCleanupWindowFrames =
            (int)temporalAlphaCleanupWindow.Value;
        configuration.LastTemporalAlphaCleanupStrengthPercent =
            temporalAlphaCleanupStrength.Value;
        configuration.LastTemporalAlphaTrackingStrengthPercent =
            temporalAlphaTrackingStrength.Value;
        configuration.LastTemporalAlphaCleanupAlphaThreshold =
            (int)temporalAlphaCleanupAlphaThreshold.Value;
        configuration.LastAutoAcceptAlphaThreshold = autoAccept.Checked;
        configuration.Save();
    }

    internal static void AddIstripperModels(List<CustomPerformerProfile> target)
    {
        HashSet<string> existingIds = target
            .Where(profile => !string.IsNullOrWhiteSpace(profile.IstripperModelId))
            .Select(profile => profile.IstripperModelId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<ModelCard> nativeModels = (DataModel.Datastore.modelcards ?? [])
            .Where(card => !card.IsCustom &&
                !string.IsNullOrWhiteSpace(card.modelId) &&
                !card.modelId.Contains(',') &&
                !string.IsNullOrWhiteSpace(card.modelName))
            .GroupBy(card => card.modelId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(card => card.modelName, StringComparer.CurrentCultureIgnoreCase);
        foreach (ModelCard card in nativeModels)
        {
            string modelId = card.modelId!.Trim();
            if (!existingIds.Add(modelId)) continue;
            target.Add(new CustomPerformerProfile
            {
                IstripperModelId = modelId,
                ModelName = card.modelName!.Trim(),
                Gender = Form1.CardGender(card),
                BirthDate = card.birthdate is DateTime birth && birth != default
                    ? DateOnly.FromDateTime(birth) : null,
                Hair = card.hair ?? "",
                Ethnicity = card.ethnicity ?? "",
                City = card.city ?? "",
                Country = card.country ?? ""
            });
        }
    }

    static string DescribeProcessing(CustomShowProcessing value)
    {
        string algorithm = value.Algorithm switch
        {
            "quality" => "RVM Quality (ResNet50)",
            "fast" => "RVM Fast (MobileNetV3)",
            "matanyone2" => "MatAnyone 2",
            "rvm-matanyone2" => "RVM-MatAnyone (automatic)",
            "rvm-vitmatte-s" => "RVM-ViTMatte S (automatic)",
            "rvm-vitmatte-b" => "RVM-ViTMatte B (automatic)",
            "vitmatte-s" => "ViTMatte S",
            "vitmatte-b" => "ViTMatte B",
            "sam2matting" => $"SAM2Matting ({Sam2MattingSupport.DisplayName(value.Tracker)})",
            "nvidia-aigs" => "NVIDIA AI Green Screen (real-time playback)",
            "rvm-onnx" => "RVM ONNX (real-time playback)",
            _ => value.Algorithm
        };
        string detail = value.MattingDetailPx == 0 ? "full resolution" :
            $"{value.MattingDetailPx} px";
        string detailText = value.Algorithm is
            "vitmatte-s" or "vitmatte-b" or "rvm-vitmatte-s" or "rvm-vitmatte-b"
            ? $"; ViTMatte inference detail " +
                ((value.VitMatteInferenceDetailPx ?? 0) == 0 ? "full resolution" :
                    $"{value.VitMatteInferenceDetailPx ?? 1024} px")
            : $"; detail {detail}";
        string sam2 = value.Sam2Model == null ? "" :
            $"; SAM2 {value.Sam2Model}";
        string mask = value.MaskEngine == null ? "" :
            $"; masks {value.MaskEngine}";
        string requestedBatch = value.BatchSize == 0 ? "Auto" : value.BatchSize.ToString();
        string effective = value.EffectiveBatchSize is int batch &&
            (value.BatchSize == 0 || batch != value.BatchSize) ?
            $" (effective {batch})" : "";
        string execution = value.ResolvedExecutionMode == null ? "" :
            $"; {value.ResolvedExecutionMode}";
        string encoder = value.Encoder == null ? "" :
            $"; {value.Encoder} {value.EncoderPreset}";
        string initializer = value.RvmInitializerAlphaThresholdPercent is int threshold ?
            $"; RVM initializer alpha {threshold}%" : "";
        string refresh = value.RvmMatAnyoneMaskRefresh
            ? $"; RVM mask refresh {value.RvmMatAnyoneRefreshStrengthPercent}%"
            : "";
        string prop = value.PropSegmenterModelId is string propModelId
            ? "; prop model " + (PropSegmenterPackage.TryLoad(propModelId, false,
                out PropSegmenterPackage? propPackage)
                    ? propPackage!.DisplayName : propModelId) +
                (value.PropSegmenterEveryFrame ? " (every frame)" : "")
            : "";
        string propDebug = value.DebugPropContribution
            ? "; prop contribution video" : "";
        string memory = value.Algorithm is "matanyone2" or "rvm-matanyone2"
            ? $"; memory {value.MatAnyoneMaxMemoryFrames} frames" +
                (value.MatAnyoneUseLongTermMemory ? " + long-term" : "")
            : "";
        string cleanup = value.TemporalAlphaCleanup
            ? $"; temporal cleanup {value.TemporalAlphaCleanupWindowFrames} frames " +
                $"at {value.TemporalAlphaCleanupStrengthPercent}% " +
                $"(tracking {value.TemporalAlphaTrackingStrengthPercent}%, " +
                $"alpha {value.TemporalAlphaCleanupAlphaThreshold})"
            : "";
        string accepted = value.AutoAcceptedAlphaThreshold is int alpha ?
            $"; automatically accepted at alpha {alpha}" : "";
        return $"{algorithm}{detailText}; batch {requestedBatch}{effective}{sam2}{mask}{initializer}{refresh}{prop}{propDebug}{memory}{cleanup}{accepted}{execution}{encoder}\r\n" +
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
        CustomShowClip[] priorLayout = CloneQueueValue(showClips);
        using CustomClipEditorForm form = new(video, showClips,
            hotness.SelectedItem?.ToString() ?? "NoNudity",
            overallTypes.Length == 0 ? ["Standing"] : overallTypes,
            configuration, allowBoundaryEditing: showId == null ||
                reprocess && !keepClips.Checked,
            existingDetection: clipDetection);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        showClips = form.Clips;
        clipDetection = form.Detection;
        clipLayoutConfirmed = true;
        clipEditorConfirmedThisSession = true;
        if (reprocess && !SameQueueLayout(priorLayout, showClips))
            reprocessLayoutChanged = true;
        if (reprocess && !RetainedMasksMatch(showClips))
        {
            keepMasks.Checked = false;
            keepMasks.Enabled = false;
            keepMasks.Text = "Keep existing masks (clip divisions changed)";
        }
        UpdateClipButton();
        UpdateReprocessClipSelectionAvailability();
    }

    void UpdateClipButton()
    {
        if (showClips.Length < 2) { editClips.Text = "Split into clips..."; return; }
        int included = showClips.Count(clip => clip.Included);
        int skipped = showClips.Length - included;
        editClips.Text = skipped == 0 ? $"Edit {included} clips..." :
            $"Edit {included} clips ({skipped} skipped)...";
    }

    bool ConfirmAutomaticRvmSceneCreation()
    {
        if (clipLayoutConfirmed) return true;
        DialogResult choice = MessageBox.Show(this,
            "No clip layout has been confirmed yet. RVM initialization should " +
            "normally use clips you have reviewed in the clip editor.\r\n\r\n" +
            "Choose Yes to open the clip editor now.\r\n" +
            "Choose No to let QuickPlayer try to detect processing scenes " +
            "automatically.\r\n" +
            "Choose Cancel to stop.",
            "Create clips before RVM initialization?",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);
        if (choice == DialogResult.Cancel) return false;
        if (choice == DialogResult.No) return true;
        EditClips(null, EventArgs.Empty);
        return clipLayoutConfirmed;
    }

    void OpenPerformer(CustomPerformerProfile? profile)
    {
        using CustomPerformerForm form = new(profile, profiles);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        if (profile != null)
            SavedPerformerChanged = true;
        selectedProfile = form.Profile;
        string selectedProfileId = selectedProfile.Id;
        int index = profiles.FindIndex(profile => profile.Id == selectedProfile.Id);
        if (index >= 0) profiles[index] = selectedProfile; else profiles.Add(selectedProfile);
        performer.DataSource = null;
        performer.DisplayMember = nameof(CustomPerformerProfile.DisplayName);
        performer.DataSource = profiles.OrderBy(profile => profile.ModelName).ToList();
        performer.SelectedItem = performer.Items.Cast<CustomPerformerProfile>()
            .First(profile => profile.Id == selectedProfileId);
    }

    async void Save(object? sender, EventArgs e)
    {
        save.Enabled = false;
        try
        {
            if (selectedProfile == null) throw new InvalidDataException("Select or create a model profile.");
            CustomShowStore.ValidateProfile(selectedProfile);
            CustomShowManifest show = Appending
                ? store.LoadManifest(appendShowId!)
                : showId == null ? new() : store.LoadManifest(showId);
            CustomShowProcessing? previousProcessing = reprocess && show.Processing != null
                ? CloneQueueValue(show.Processing) : null;
            CustomShowClip[] existingClips = Appending ? show.Clips : [];
            long existingDuration = Appending ? show.Media.DurationMs : 0;
            ApplyFields(show, includeClipLayout: !Appending);
            AdoptSelectedProfileGender(show.Gender);
            CustomShowStore.ValidateLink(show, selectedProfile);
            if (showId != null) RelinkSource(show);
            if (showId != null && !reprocess)
            {
                store.SavePerformer(selectedProfile);
                string destination = CustomShowStore.ResolveRelative(
                    Path.Combine(store.ShowsFolder, show.Id), show.Media.Cover);
                if (!string.IsNullOrWhiteSpace(cover.Text))
                {
                    show.Media.CoverSource = "cover-source.jpg";
                    SaveCover(cover.Text, destination, selectedProfile.ModelName,
                        show.Title, coverTitleColor.BackColor,
                        SelectedCoverModelFont(), SelectedCoverTitleFont(),
                        Path.Combine(store.ShowsFolder, show.Id,
                            show.Media.CoverSource));
                    show.Media.AutoGeneratedCover = false;
                }
                else if (show.Media.AutoGeneratedCover && AutoCoverSettingsChanged(show))
                    await SaveAutoCover(show, destination);
                else if (!show.Media.AutoGeneratedCover &&
                    AutoCoverSettingsChanged(show))
                {
                    if (string.IsNullOrWhiteSpace(show.Media.CoverSource))
                        throw new InvalidDataException(
                            "This custom cover predates editable cover captions. " +
                            "Use Browse to select its original image once, then save again.");
                    string cleanCover = CustomShowStore.ResolveRelative(
                        Path.Combine(store.ShowsFolder, show.Id),
                        show.Media.CoverSource);
                    SaveCover(cleanCover, destination, selectedProfile.ModelName,
                        show.Title, coverTitleColor.BackColor,
                        SelectedCoverModelFont(), SelectedCoverTitleFont());
                }
                store.SaveManifest(show);
                SavedShowId = show.Id;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            if (!File.Exists(source.Text)) throw new FileNotFoundException("Select a source video.", source.Text);
            if (!ConfirmGpuBatch()) return;
            if (showClips.Length == 0)
            {
                using FfmpegCpuDecoder decoder = new(source.Text, fastDecode: true);
                showClips = [new()
                {
                    StartMs = 0,
                    EndMs = checked((long)Math.Round(decoder.Duration * 1000)),
                    Hotness = show.Hotness,
                    ClipTypes = [.. show.ClipTypes],
                    AlphaThreshold = configuration.DefaultAlphaThreshold
                }];
            }
            string[] reprocessClipIds = SelectedReprocessClipIds();
            if (reprocess && reprocessClipIds.Length == 0)
                throw new InvalidDataException("Select at least one clip to reprocess.");
            HashSet<string> reprocessClipSet = reprocessClipIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            string staging = Path.Combine(store.Root, ".staging", show.Id);
            if (!await PrepareStaging(show, staging, Appending || reprocess)) return;
            bool discardStaging = false;
            bool discardMaskDraft = false;
            CustomShowMaskDraft? maskDraft = null;
            CustomShowSource? appendedSource = null;
            try
            {
                string input = source.Text;
                if (Appending)
                {
                    appendedSource = new CustomShowSource
                    {
                        Mode = "reference", Path = Path.GetFullPath(source.Text)
                    };
                }
                else if (reprocess && show.Source.Mode == "copy")
                {
                    string original = SourceVideo(show);
                    input = Path.Combine(staging, show.Source.Path.Replace('/',
                        Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(input)!);
                    File.Copy(original, input, true);
                }
                else show.Source = new() { Mode = "reference", Path = Path.GetFullPath(source.Text) };
                Dictionary<string, string> initialMasks = [];
                Dictionary<string, long> initialMaskFrames = [];
                Dictionary<string, string> sam2Masks = [];
                Dictionary<string, string> maskDraftFolders = [];
                string selectedPreset = SelectedPreset();
                string selectedSam2Model = SelectedSam2Model();
                string selectedMaskEngine = UsesSam2(selectedPreset) ?
                    SelectedMaskEngine() : "sam2";
                int detail = SelectedMattingResolution();
                int vitMatteDetail = SelectedVitMatteInferenceResolution();
                string log = Path.Combine(staging, "processing.log");
                if (File.Exists(log)) File.Delete(log);
                if (reprocess)
                {
                    if (keepMasks.Checked)
                    {
                        using CustomShowProcessingForm retainedPreparation = new(
                            async (progress, token) =>
                            {
                                progress.Report(new CustomShowProgress("preparing", 0,
                                    "Opening retained masks for the selected clips…"));
                                await Task.Run(() => LoadRetainedMasks(
                                    Path.Combine(staging, "masks"), show,
                                    Path.Combine(staging, ".mask-work"), initialMasks,
                                    initialMaskFrames, sam2Masks, reprocessClipSet,
                                    message => progress.Report(new CustomShowProgress(
                                        "preparing", 0, message))), token);
                                progress.Report(new CustomShowProgress("preparing", 100,
                                    "Retained masks ready"));
                                return new CustomShowProcessResult();
                            }, "Preparing Retained Masks",
                            processDescription:
                                "Loading masks only for the clips selected for reprocessing",
                            showPreviews: false);
                        if (retainedPreparation.ShowDialog(this) != DialogResult.OK)
                        {
                            discardStaging = true;
                            return;
                        }
                    }
                    if (!keepMasks.Checked)
                        foreach (string clipId in reprocessClipIds)
                        {
                            initialMasks.Remove(clipId);
                            initialMaskFrames.Remove(clipId);
                            sam2Masks.Remove(clipId);
                            string folder = Path.Combine(staging, "masks", clipId);
                            if (Directory.Exists(folder)) Directory.Delete(folder, true);
                        }
                    RemoveRetainedMaskEntries(Path.Combine(staging, "masks"),
                        reprocessClipIds);
                }
                if (selectedPreset == "matanyone2" || UsesSam2(selectedPreset))
                {
                    CustomShowClip[] clipsToMask = showClips.Where(clip => clip.Included &&
                        (!reprocess || reprocessClipSet.Contains(clip.Id))).ToArray();
                    maskDraft = CustomShowMaskDraft.Open(store, source.Text,
                        selectedPreset, selectedSam2Model, selectedMaskEngine,
                        clipsToMask);
                    CustomShowManifest draftSetup = CloneQueueValue(show);
                    draftSetup.Source = new() { Mode = "reference",
                        Path = Path.GetFullPath(source.Text) };
                    draftSetup.Clips = CloneQueueValue(showClips);
                    draftSetup.ClipDetection = clipDetection;
                    draftSetup.Processing = new()
                    {
                        Algorithm = selectedPreset,
                        MattingDetailPx = detail,
                        VitMatteInferenceDetailPx = selectedPreset is
                            "vitmatte-s" or "vitmatte-b" or "rvm-vitmatte-s" or "rvm-vitmatte-b"
                            ? vitMatteDetail : null,
                        BatchSize = SelectedBatchSize(),
                        RvmInitializerAlphaThresholdPercent = selectedPreset is
                            "rvm-matanyone2" or "rvm-vitmatte-s" or "rvm-vitmatte-b" ||
                            selectedMaskEngine == "rvm-sam2"
                            ? rvmInitializerThreshold.Value : null,
                        RvmMatAnyoneMaskRefresh = selectedPreset == "rvm-matanyone2" &&
                            rvmMatAnyoneMaskRefresh.Checked,
                        PropSegmenterEveryFrame = selectedPreset == "matanyone2" &&
                                propSegmenterEveryFrame.Checked ||
                            selectedPreset is
                            "rvm-vitmatte-s" or "rvm-vitmatte-b" &&
                                propSegmenterEveryFrame.Checked ||
                            selectedPreset == "rvm-matanyone2" &&
                                rvmMatAnyoneMaskRefresh.Checked &&
                                propSegmenterEveryFrame.Checked,
                        DebugPropContribution = debugPropContribution.Checked,
                        RvmMatAnyoneRefreshStrengthPercent = selectedPreset ==
                            "rvm-matanyone2" ? rvmMatAnyoneRefreshStrength.Value : 100,
                        MatAnyoneMaxMemoryFrames = selectedPreset is
                            "matanyone2" or "rvm-matanyone2"
                            ? (int)matAnyoneMaxMemoryFrames.Value : 5,
                        MatAnyoneUseLongTermMemory = selectedPreset is
                            "matanyone2" or "rvm-matanyone2" &&
                            matAnyoneUseLongTermMemory.Checked,
                        TemporalAlphaCleanup = temporalAlphaCleanup.Checked,
                        TemporalAlphaCleanupWindowFrames =
                            (int)temporalAlphaCleanupWindow.Value,
                        TemporalAlphaCleanupStrengthPercent =
                            temporalAlphaCleanupStrength.Value,
                        TemporalAlphaTrackingStrengthPercent =
                            temporalAlphaTrackingStrength.Value,
                        TemporalAlphaCleanupAlphaThreshold =
                            (int)temporalAlphaCleanupAlphaThreshold.Value,
                        AutoAcceptedAlphaThreshold = autoAccept.Checked
                            ? configuration.DefaultAlphaThreshold : null,
                        Sam2Model = UsesSam2(selectedPreset) ? selectedSam2Model : null,
                        MaskEngine = UsesSam2(selectedPreset) ? selectedMaskEngine : null
                    };
                    ApplyPropSegmenter(draftSetup.Processing,
                        selectedPreset is "matanyone2" or "rvm-matanyone2" or
                            "rvm-vitmatte-s" or "rvm-vitmatte-b" ||
                        UsesSam2(selectedPreset) &&
                            selectedMaskEngine == "rvm-sam2");
                    maskDraft.SaveSetup(draftSetup);
                    for (int index = 0; index < clipsToMask.Length; index++)
                    {
                        CustomShowClip clip = clipsToMask[index];
                        string draftClip = maskDraft.ClipFolder(index);
                        maskDraftFolders[clip.Id] = draftClip;
                        bool hasInitial = initialMasks.ContainsKey(clip.Id);
                        bool hasTracked = sam2Masks.ContainsKey(clip.Id);
                        string draftInitialMask = Path.Combine(draftClip,
                            "initial-mask.png");
                        string draftTrackedMasks = Path.Combine(draftClip,
                            selectedMaskEngine == "rvm" ? "rvm-masks" : "sam2-masks");
                        string draftTrackedState = Path.Combine(draftClip,
                            selectedMaskEngine == "rvm" ? "rvm-review.json" :
                            "sam2-corrections.json");
                        if (UsesSam2(selectedPreset) && !hasTracked &&
                            CompleteTrackedMaskDraft(draftTrackedState,
                                draftTrackedMasks))
                        {
                            sam2Masks[clip.Id] = draftTrackedMasks;
                            hasTracked = true;
                        }
                        if (selectedMaskEngine == "rvm-sam2" && !hasInitial &&
                            File.Exists(draftInitialMask))
                        {
                            initialMasks[clip.Id] = draftInitialMask;
                            initialMaskFrames[clip.Id] = clip.StartMs;
                            hasInitial = true;
                        }
                        if (selectedPreset == "matanyone2" && hasInitial)
                            continue;
                        if (UsesSam2(selectedPreset) && hasTracked)
                            continue;
                        if (UsesSam2(selectedPreset) && selectedMaskEngine == "rvm")
                        {
                            string maskSequence = Path.Combine(draftClip, "rvm-masks");
                            using CustomVideoMaskEditorForm refinement = new(input,
                                configuration, clip.StartMs, clip.EndMs, "", maskSequence,
                                clipsToMask.Length == 1 ? null :
                                $"Clip {index + 1} of {clipsToMask.Length}",
                                selectedSam2Model, log,
                                Path.Combine(draftClip, "rvm-review.json"),
                                selectedMaskEngine);
                            if (refinement.ShowDialog(this) != DialogResult.OK)
                            {
                                discardStaging = true;
                                return;
                            }
                            sam2Masks[clip.Id] = maskSequence;
                            continue;
                        }
                        if (!hasInitial && selectedMaskEngine == "rvm-sam2")
                        {
                            using CustomShowProcessingForm initializer = new(
                                async (progress, token) =>
                                {
                                    await CustomShowProcessor.GenerateRvmInitialMaskAsync(
                                        configuration, input, draftInitialMask,
                                        clip.StartMs, rvmInitializerThreshold.Value,
                                        progress, token,
                                        SelectedPropSegmenterPackage()?.Folder);
                                    return new CustomShowProcessResult();
                                }, "Create RVM Person Mask",
                                processDescription:
                                    "Creating an automatic person mask for SAM2");
                            if (initializer.ShowDialog(this) != DialogResult.OK)
                            {
                                discardStaging = true;
                                return;
                            }
                            initialMasks[clip.Id] = draftInitialMask;
                            initialMaskFrames[clip.Id] = clip.StartMs;
                            hasInitial = true;
                        }
                        if (!hasInitial)
                        {
                            using CustomMaskEditorForm maskEditor = new(input, configuration,
                                clip.StartMs, clip.EndMs, clipsToMask.Length == 1 ? null :
                                $"Clip {index + 1} of {clipsToMask.Length}",
                                selectedPreset switch
                                {
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
                        }
                        if (UsesSam2(selectedPreset))
                        {
                            string mask = initialMasks[clip.Id];
                            string maskSequence = Path.Combine(draftClip, "sam2-masks");
                            string correctionState = Path.Combine(draftClip,
                                "sam2-corrections.json");
                            using CustomVideoMaskEditorForm refinement = new(input,
                                configuration, clip.StartMs, clip.EndMs, mask, maskSequence,
                                clipsToMask.Length == 1 ? null :
                                $"Clip {index + 1} of {clipsToMask.Length}",
                                selectedSam2Model, log, correctionState,
                                selectedMaskEngine == "rvm-sam2"
                                    ? "sam2" : selectedMaskEngine);
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
                        int chunk = SelectedBatchSize();
                        CustomShowClip[] included = showClips.Where(clip => clip.Included &&
                            (!reprocess || reprocessClipSet.Contains(clip.Id))).ToArray();
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
                                    clip.Id, clip.StartMs),
                                 rvmInitializerAlphaThresholdPercent:
                                    rvmInitializerThreshold.Value,
                                 rvmMatAnyoneMaskRefresh:
                                    selectedPreset == "rvm-matanyone2" &&
                                    rvmMatAnyoneMaskRefresh.Checked,
                                 propSegmenterEveryFrame:
                                    selectedPreset == "matanyone2" &&
                                        propSegmenterEveryFrame.Checked ||
                                    selectedPreset is
                                        "rvm-vitmatte-s" or "rvm-vitmatte-b" &&
                                            propSegmenterEveryFrame.Checked ||
                                    selectedPreset == "rvm-matanyone2" &&
                                        rvmMatAnyoneMaskRefresh.Checked &&
                                        propSegmenterEveryFrame.Checked,
                                 debugPropContribution:
                                    debugPropContribution.Checked,
                                 rvmMatAnyoneRefreshStrengthPercent:
                                    rvmMatAnyoneRefreshStrength.Value,
                                 matAnyoneMaxMemoryFrames:
                                    (int)matAnyoneMaxMemoryFrames.Value,
                                 matAnyoneUseLongTermMemory:
                                    matAnyoneUseLongTermMemory.Checked,
                                 vitMatteInferenceDetailPx: vitMatteDetail,
                                 matAnyoneInteractiveCorrections:
                                    selectedPreset == "matanyone2",
                                 propSegmenterModelPath:
                                    selectedPreset is "matanyone2" or
                                        "rvm-matanyone2" or
                                        "rvm-vitmatte-s" or "rvm-vitmatte-b"
                                        ? SelectedPropSegmenterPackage()?.Folder
                                        : null);
                            if (selectedPreset is "rvm-vitmatte-s" or "rvm-vitmatte-b" &&
                                !sam2Masks.ContainsKey(clip.Id))
                            {
                                string generatedMasks = Path.Combine(output,
                                    ".rvm-masks");
                                if (Directory.Exists(generatedMasks) &&
                                    Directory.EnumerateFiles(generatedMasks,
                                        "*.png").Any())
                                    sam2Masks[clip.Id] = generatedMasks;
                            }
                            if (selectedPreset == "rvm-matanyone2" &&
                                !initialMasks.ContainsKey(clip.Id))
                            {
                                string generatedMask = Path.Combine(output,
                                    "initial-mask.png");
                                if (File.Exists(generatedMask))
                                {
                                    initialMasks[clip.Id] = generatedMask;
                                    initialMaskFrames[clip.Id] =
                                        result.InitialMaskFrameMs ?? clip.StartMs;
                                }
                            }
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
                    }, correctionConfiguration: selectedPreset == "matanyone2"
                            ? configuration : null,
                        correctionSource: selectedPreset == "matanyone2" ? input : null,
                        correctionSam2Model: selectedSam2Model);
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
                    if (Appending && selectedPreset is "rvm-vitmatte-s" or "rvm-vitmatte-b")
                    {
                        string saveText = save.Text;
                        save.Text = "Compressing retained masks…";
                        try
                        {
                            await Task.Run(() => CompressGeneratedRvmMasks(
                                staging, sam2Masks));
                        }
                        finally { save.Text = saveText; }
                    }
                    else if (!Appending &&
                        (initialMasks.Count > 0 || sam2Masks.Count > 0))
                    {
                        string saveText = save.Text;
                        save.Text = "Compressing retained masks…";
                        try
                        {
                            await Task.Run(() => SaveRetainedMasks(staging, source.Text,
                                showClips, maskDraftFolders, initialMasks,
                                initialMaskFrames, sam2Masks));
                        }
                        finally { save.Text = saveText; }
                    }
                    if (selectedPreset is "rvm-vitmatte-s" or "rvm-vitmatte-b")
                        foreach (string generated in GeneratedRvmMaskFolders(
                            staging, sam2Masks))
                            try { Directory.Delete(generated, true); } catch { }
                    CustomShowProcessResult media = processing.Result;
                    await DeleteDirectoryWhenReleasedAsync(
                        Path.Combine(staging, ".mask-work"));
                    RemoveExpandedRetainedMasks(Path.Combine(staging, "masks"));
                    bool retainExistingCover = (reprocess || Appending) &&
                        string.IsNullOrWhiteSpace(cover.Text) &&
                        !show.Media.AutoGeneratedCover;
                    string? existingCover = retainExistingCover && !Appending
                        ? CustomShowStore.ResolveRelative(
                            Path.Combine(store.ShowsFolder, show.Id), show.Media.Cover)
                        : null;
                    string? existingCoverSource = retainExistingCover && !Appending &&
                        !string.IsNullOrWhiteSpace(show.Media.CoverSource)
                        ? CustomShowStore.ResolveRelative(
                            Path.Combine(store.ShowsFolder, show.Id),
                            show.Media.CoverSource)
                        : null;
                    if (showClips.Length > 0)
                    {
                        showClips[^1].EndMs = media.DurationMs;
                        CustomShowStore.ValidateClips(showClips, media.DurationMs);
                    }
                    CustomShowClip[] processedClips = showClips.Where(clip => clip.Included &&
                        (!reprocess || reprocessClipSet.Contains(clip.Id))).ToArray();
                    if (autoAccept.Checked)
                        foreach (CustomShowClip clip in processedClips)
                            clip.AlphaThreshold =
                                configuration.DefaultAlphaThreshold;
                    if (Appending)
                    {
                        show.Media.DurationMs = checked(existingDuration + media.DurationMs);
                        show.Media.CoverTitleColor = ColorHex(coverTitleColor.BackColor);
                        show.Media.CoverModelFont = SelectedCoverModelFont();
                        show.Media.CoverTitleFont = SelectedCoverTitleFont();
                        if (!string.IsNullOrWhiteSpace(cover.Text))
                        {
                            show.Media.AutoGeneratedCover = false;
                            show.Media.CoverSource = "cover-source.jpg";
                        }
                        show.Clips = [.. existingClips, .. OffsetClips(showClips,
                            existingDuration, appendedSource!)];
                        show.ClipDetection = null;
                    }
                    else
                    {
                        show.Media = new()
                        {
                            Width = media.Width, Height = media.Height,
                            FrameRate = media.FrameRate, DurationMs = media.DurationMs,
                            PhotosFolder = photosFolder.Text.Trim(),
                            AutoGeneratedCover = !retainExistingCover &&
                                string.IsNullOrWhiteSpace(cover.Text),
                            CoverSource = !string.IsNullOrWhiteSpace(cover.Text)
                                ? "cover-source.jpg"
                                : existingCoverSource != null ? "cover-source.jpg" : null,
                            CoverTitleColor = ColorHex(coverTitleColor.BackColor),
                            CoverModelFont = SelectedCoverModelFont(),
                            CoverTitleFont = SelectedCoverTitleFont()
                        };
                        show.Clips = showClips;
                    }
                    int processingBatchSize = SelectedBatchSize();
                    bool usesInitialMask = selectedPreset is "matanyone2" or
                        "rvm-matanyone2" || selectedMaskEngine == "rvm-sam2";
                    bool usesSam2 = selectedPreset == "matanyone2" ||
                        (UsesSam2(selectedPreset) && selectedMaskEngine != "rvm");
                    show.Processing = new()
                    {
                        Algorithm = selectedPreset,
                        MattingDetailPx = detail,
                        VitMatteInferenceDetailPx = selectedPreset is
                            "vitmatte-s" or "vitmatte-b" or "rvm-vitmatte-s" or "rvm-vitmatte-b"
                            ? vitMatteDetail : null,
                        BatchSize = processingBatchSize,
                        RvmInitializerAlphaThresholdPercent = selectedPreset is
                            "rvm-matanyone2" or "rvm-vitmatte-s" or "rvm-vitmatte-b" ||
                            selectedMaskEngine == "rvm-sam2"
                            ? rvmInitializerThreshold.Value : null,
                        RvmMatAnyoneMaskRefresh = selectedPreset == "rvm-matanyone2" &&
                            rvmMatAnyoneMaskRefresh.Checked,
                        PropSegmenterEveryFrame = selectedPreset == "matanyone2" &&
                                propSegmenterEveryFrame.Checked ||
                            selectedPreset is
                            "rvm-vitmatte-s" or "rvm-vitmatte-b" &&
                                propSegmenterEveryFrame.Checked ||
                            selectedPreset == "rvm-matanyone2" &&
                                rvmMatAnyoneMaskRefresh.Checked &&
                                propSegmenterEveryFrame.Checked,
                        DebugPropContribution = debugPropContribution.Checked,
                        RvmMatAnyoneRefreshStrengthPercent = selectedPreset ==
                            "rvm-matanyone2" ? rvmMatAnyoneRefreshStrength.Value : 100,
                        MatAnyoneMaxMemoryFrames = selectedPreset is
                            "matanyone2" or "rvm-matanyone2"
                            ? (int)matAnyoneMaxMemoryFrames.Value : 5,
                        MatAnyoneUseLongTermMemory = selectedPreset is
                            "matanyone2" or "rvm-matanyone2" &&
                            matAnyoneUseLongTermMemory.Checked,
                        AutoAcceptedAlphaThreshold = autoAccept.Checked
                            ? configuration.DefaultAlphaThreshold : null,
                        Sam2Model = usesSam2 ? selectedSam2Model : null,
                        MaskEngine = UsesSam2(selectedPreset) ? selectedMaskEngine : null,
                        ExecutionPolicy = "auto",
                        ResolvedExecutionMode = media.ExecutionMode,
                        EffectiveBatchSize = media.EffectiveSequenceChunk,
                        PipelineDepth = media.PipelineDepth,
                        Encoder = media.Encoder,
                        EncoderPreset = media.EncoderPreset,
                        PrecisionPolicy = "fp16-autocast-fp32-cpu-fallback",
                        RecurrentRefinementSteps = selectedPreset is "matanyone2" or
                            "rvm-matanyone2" ? 11 : 0,
                        ProcessedUtc = DateTime.UtcNow,
                        QuickPlayerVersion = Application.ProductVersion,
                        ToolRevisions = ReadProcessingRevisions(selectedPreset,
                            selectedMaskEngine),
                        Clips = (Appending ? existingClips.Where(clip => clip.Included)
                            .Select(clip => new CustomClipProcessing { ClipId = clip.Id })
                            : []).Concat(showClips.Where(clip => clip.Included).Select(clip =>
                            new CustomClipProcessing
                            {
                                ClipId = clip.Id,
                                InitialMaskFrameMs = usesInitialMask
                                    ? checked(initialMaskFrames.GetValueOrDefault(
                                        clip.Id, clip.StartMs) + existingDuration)
                                    : null,
                                Sam2MaskTracking = UsesSam2(selectedPreset) &&
                                    selectedMaskEngine is "sam2" or "rvm-sam2"
                            })).ToArray()
                    };
                    ApplyPropSegmenter(show.Processing,
                        selectedPreset is "matanyone2" or "rvm-matanyone2" or
                            "rvm-vitmatte-s" or "rvm-vitmatte-b" ||
                        UsesSam2(selectedPreset) &&
                            selectedMaskEngine == "rvm-sam2");
                    if (reprocess && previousProcessing != null &&
                        processedClips.Length < showClips.Count(clip => clip.Included))
                        show.Processing = previousProcessing;
                    if (!string.IsNullOrWhiteSpace(cover.Text))
                        SaveCover(cover.Text, Path.Combine(staging, show.Media.Cover),
                            selectedProfile.ModelName, show.Title,
                            coverTitleColor.BackColor, SelectedCoverModelFont(),
                            SelectedCoverTitleFont(), Path.Combine(staging,
                                show.Media.CoverSource ?? "cover-source.jpg"));
                    else if (existingCover != null)
                    {
                        File.Copy(existingCover, Path.Combine(staging, show.Media.Cover), true);
                        if (existingCoverSource != null)
                            File.Copy(existingCoverSource, Path.Combine(staging,
                                show.Media.CoverSource!), true);
                    }
                    else if (!Appending) await CustomShowProcessor.GenerateCoverAsync(input,
                        Path.Combine(staging, show.Media.Cover), show.Media.DurationMs / 2,
                        selectedProfile.ModelName, show.Title, coverTitleColor.BackColor,
                        CancellationToken.None, SelectedCoverModelFont(),
                        SelectedCoverTitleFont());
                    DialogResult decision;
                    if (autoAccept.Checked) decision = DialogResult.OK;
                    else decision = await ReviewProcessedShow(staging, processedClips);
                    retry = decision == DialogResult.Retry;
                    if (decision == DialogResult.Cancel)
                    {
                        discardStaging = true;
                        discardMaskDraft = true;
                        return;
                    }
                } while (retry);
                store.SavePerformer(selectedProfile);
                if (reprocess || Appending) store.Replace(staging, show);
                else store.Publish(staging, show);
                // Record the successful library mutation before draft cleanup. The
                // owner must refresh even if cleanup or WinForms dialog state later
                // prevents this form from returning DialogResult.OK.
                SavedShowId = show.Id;
                if (maskDraft != null && Directory.Exists(maskDraft.Root))
                    try
                    {
                        maskDraft.MarkPublished(show);
                        await DeleteDirectoryWhenReleasedAsync(maskDraft.Root);
                    }
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

    async void QueueJob(object? sender, EventArgs e)
    {
        bool processNow = ReferenceEquals(sender, save);
        Button action = processNow ? save : queue;
        action.Enabled = false;
        string? maskWork = null;
        try
        {
            if (queueManager == null) return;
            string algorithm = SelectedPreset();
            string selectedSam2MattingPromptMode =
                SelectedSam2MattingPromptMode();
            if (algorithm is not (Sam2MattingSupport.Algorithm or
                    CustomClipMedia.NvidiaAigsMode or CustomClipMedia.RvmOnnxMode) &&
                !autoAccept.Checked)
                throw new InvalidDataException(
                    "Queued processing requires automatic acceptance at alpha threshold " +
                    configuration.DefaultAlphaThreshold + ".");
            if (selectedProfile == null)
                throw new InvalidDataException("Select or create a model profile.");
            CustomShowStore.ValidateProfile(selectedProfile);
            if (!File.Exists(source.Text))
                throw new FileNotFoundException("Select a source video.", source.Text);
            if (algorithm == Sam2MattingSupport.Algorithm &&
                !Sam2MattingSupport.IsInstalled(configuration,
                    SelectedSam2MattingTracker()))
                throw new InvalidDataException(
                    "Setup required: install the SAM2Matting environment and all checkpoints.");
            if (algorithm == Sam2MattingSupport.Algorithm &&
                selectedSam2MattingPromptMode == "rvm-initial-mask" &&
                !CustomShowProcessor.IsRvmInitialMaskInstalled(configuration))
                throw new InvalidDataException(
                    "Setup required: install or repair the Robust Video Matting processing tools.");
            if (algorithm == Sam2MattingSupport.Algorithm &&
                selectedSam2MattingPromptMode == "rvm-initial-mask" &&
                !ConfirmAutomaticRvmSceneCreation()) return;
            if (!ConfirmGpuBatch()) return;
            if (showClips.Length == 0)
            {
                using FfmpegCpuDecoder decoder = new(source.Text, fastDecode: true);
                showClips = [new()
                {
                    StartMs = 0,
                    EndMs = checked((long)Math.Round(decoder.Duration * 1000)),
                    Hotness = hotness.SelectedItem?.ToString() ?? "NoNudity",
                    ClipTypes = clipTypes.CheckedItems.Cast<object>()
                        .Select(value => value.ToString()!).ToArray(),
                    AlphaThreshold = configuration.DefaultAlphaThreshold
                }];
            }
            CustomShowQueueJob? existing = queueJobId == null
                ? queueDraft : queueManager.Find(queueJobId);
            CustomShowQueueOperation operation = existing?.Operation ??
                (Appending ? CustomShowQueueOperation.Append : reprocess
                    ? CustomShowQueueOperation.Reprocess : CustomShowQueueOperation.New);
            string? targetId = existing?.TargetShowId ??
                (operation == CustomShowQueueOperation.Append ? appendShowId :
                    operation == CustomShowQueueOperation.Reprocess ? showId : null);
            CustomShowManifest manifest = operation == CustomShowQueueOperation.New
                ? existing?.Manifest ?? new() : store.LoadManifest(targetId!);
            ApplyFields(manifest, includeClipLayout: true);
            AdoptSelectedProfileGender(manifest.Gender);
            manifest.Clips = CloneQueueValue(showClips);
            if (algorithm == CustomClipMedia.NvidiaAigsMode)
            {
                HashSet<string> selectedNvidiaClips = reprocess
                    ? SelectedReprocessClipIds().ToHashSet(
                        StringComparer.OrdinalIgnoreCase)
                    : manifest.Clips.Select(value => value.Id).ToHashSet(
                        StringComparer.OrdinalIgnoreCase);
                foreach (CustomShowClip clip in manifest.Clips)
                {
                    if (!selectedNvidiaClips.Contains(clip.Id)) continue;
                    clip.RvmOnnx = null;
                    if (clip.Media != null)
                    {
                        clip.Media.Mode = CustomClipMedia.NvidiaAigsMode;
                        clip.Media.Alpha = null;
                    }
                    clip.Nvidia = new CustomNvidiaSettings
                    {
                        Mode = Math.Max(0, nvidiaMode.SelectedIndex),
                        Temporal = nvidiaTemporal.Checked,
                        InferenceResolution = nvidiaInferenceResolution.SelectedIndex switch
                        {
                            0 => "540p", 2 => "1080p", 3 => "source", _ => "720p"
                        }
                    };
                }
            }
            if (algorithm == CustomClipMedia.RvmOnnxMode)
            {
                HashSet<string> selectedRvmClips = reprocess
                    ? SelectedReprocessClipIds().ToHashSet(
                        StringComparer.OrdinalIgnoreCase)
                    : manifest.Clips.Select(value => value.Id).ToHashSet(
                        StringComparer.OrdinalIgnoreCase);
                foreach (CustomShowClip clip in manifest.Clips)
                {
                    if (!selectedRvmClips.Contains(clip.Id)) continue;
                    clip.Nvidia = null;
                    if (clip.Media != null)
                    {
                        clip.Media.Mode = CustomClipMedia.RvmOnnxMode;
                        clip.Media.Alpha = null;
                    }
                    clip.RvmOnnx = new CustomRvmOnnxSettings
                    {
                        Model = rvmOnnxModel.SelectedIndex == 1
                            ? RvmOnnxSupport.ResNet50 : RvmOnnxSupport.MobileNetV3,
                        Quality = RvmOnnxSupport.QualityFromIndex(
                            rvmOnnxQuality.SelectedIndex),
                        Temporal = rvmOnnxTemporal.Checked
                    };
                }
            }
            if (!CustomClipMedia.IsRealtimeMode(algorithm))
            {
                HashSet<string> selectedOfflineClips = reprocess
                    ? SelectedReprocessClipIds().ToHashSet(
                        StringComparer.OrdinalIgnoreCase)
                    : manifest.Clips.Select(value => value.Id).ToHashSet(
                        StringComparer.OrdinalIgnoreCase);
                foreach (CustomShowClip clip in manifest.Clips)
                    if (selectedOfflineClips.Contains(clip.Id))
                    {
                        clip.Nvidia = null;
                        clip.RvmOnnx = null;
                    }
            }
            manifest.ClipDetection = clipDetection;
            manifest.Source = new() { Mode = "reference", Path = Path.GetFullPath(source.Text) };
            CustomShowProcessingScene[] processingScenes = [];
            string tracker = SelectedSam2MattingTracker();
            string promptMode = selectedSam2MattingPromptMode;
            if (algorithm == Sam2MattingSupport.Algorithm)
            {
                if (clipLayoutConfirmed)
                {
                    CustomShowProcessingScene[] retainedScenes =
                        existing?.Manifest.Processing?.Scenes ?? [];
                    processingScenes = !clipEditorConfirmedThisSession &&
                        retainedScenes.Length > 0
                            ? CloneQueueValue(retainedScenes)
                            : Sam2MattingScenePlanner.FromConfirmedClips(
                                source.Text, showClips);
                    // The confirmed clip layout is authoritative regardless of
                    // which detector originally proposed it. Keep that detector's
                    // provenance, but do not run it again.
                    manifest.ClipDetection = clipDetection;
                }
                else
                {
                    long detectionTotalFrames;
                    using (FfmpegCpuDecoder detectorMedia = new(source.Text,
                        fastDecode: true))
                    {
                        if (!CustomShowStore.TryFrameRate(detectorMedia.FrameRate,
                                out double detectorFps))
                            throw new InvalidDataException(
                                "The source frame rate is invalid.");
                        detectionTotalFrames = Math.Max(1, showClips
                            .Where(value => value.Included).Sum(value => Math.Max(1,
                                Sam2MattingScenePlanner.Frame(value.EndMs, detectorFps) -
                                Sam2MattingScenePlanner.Frame(value.StartMs, detectorFps))));
                    }
                    CustomShowClipDetection? plannedDetection = null;
                    CustomShowProcessingScene[]? plannedScenes = null;
                    CustomShowClipDetection? reusableDetection =
                        Sam2MattingScenePlanner.ReusableDetection(
                            configuration, clipDetection);
                    using CustomShowProcessingForm planning = new(async (progress, token) =>
                    {
                        Progress<int> detectorProgress = new(value =>
                        {
                            long analysed = Math.Clamp(checked((long)Math.Round(
                                detectionTotalFrames * value / 100d)), 0,
                                detectionTotalFrames);
                            progress.Report(new CustomShowProgress("scene-analysis", value,
                                $"Detecting processing scenes… {analysed:N0}/" +
                                $"{detectionTotalFrames:N0} frames ({value}%)"));
                        });
                        var planned = await Sam2MattingScenePlanner.DetectAsync(configuration,
                            source.Text, showClips, detectorProgress, token,
                            reusableDetection);
                        plannedDetection = planned.Detection;
                        plannedScenes = planned.Scenes;
                        progress.Report(new CustomShowProgress("scene-analysis", 100,
                            $"Resolved {planned.Scenes.Length} processing scenes"));
                        return new CustomShowProcessResult();
                    }, "Detecting SAM2Matting Scenes",
                        processDescription:
                            promptMode == "rvm-initial-mask"
                                ? "Resolving scenes for automatic RVM initialization"
                                : "Resolving scenes for the required initial-mask wizard",
                        showPreviews: false);
                    if (planning.ShowDialog(this) != DialogResult.OK) return;
                    clipDetection = plannedDetection ?? throw new InvalidDataException(
                        "Scene detection did not return detector provenance.");
                    manifest.ClipDetection = clipDetection;
                    processingScenes = plannedScenes ?? throw new InvalidDataException(
                        "Scene detection did not return a processing plan.");
                }
            }
            string[] concepts = [];
            CustomShowProcessing processingOptions = new()
            {
                Algorithm = algorithm,
                ProcessingOptionVersion = algorithm == Sam2MattingSupport.Algorithm
                    ? Sam2MattingSupport.OptionVersion : 0,
                Tracker = algorithm == Sam2MattingSupport.Algorithm ? tracker : null,
                PromptMode = algorithm == Sam2MattingSupport.Algorithm
                    ? promptMode : null,
                ForegroundConcepts = concepts,
                EnvironmentSpecVersion = algorithm == Sam2MattingSupport.Algorithm
                    ? Sam2MattingSupport.EnvironmentVersion : null,
                SourceRevision = algorithm == Sam2MattingSupport.Algorithm
                    ? Sam2MattingSupport.SourceRevision : null,
                CheckpointRevision = algorithm == Sam2MattingSupport.Algorithm
                    ? Sam2MattingSupport.CheckpointRevision : null,
                CheckpointSha256 = algorithm == Sam2MattingSupport.Algorithm
                    ? Sam2MattingSupport.CheckpointSha256(tracker) : null,
                AttentionPolicy = algorithm == Sam2MattingSupport.Algorithm
                    ? "pytorch-sdpa" : null,
                EncoderPolicy = algorithm == Sam2MattingSupport.Algorithm
                    ? "quickplayer-h264-aac" : null,
                AlphaEncodingPolicy = algorithm == Sam2MattingSupport.Algorithm
                    ? Sam2MattingSupport.AlphaEncodingPolicy : null,
                ScenePlanVersion = algorithm == Sam2MattingSupport.Algorithm
                    ? Sam2MattingSupport.ScenePlanVersion : null,
                Scenes = processingScenes,
                MattingDetailPx = CustomClipMedia.IsRealtimeMode(algorithm)
                    ? 0 : SelectedMattingResolution(),
                VitMatteInferenceDetailPx = algorithm is
                    "vitmatte-s" or "vitmatte-b" or "rvm-vitmatte-s" or "rvm-vitmatte-b"
                    ? SelectedVitMatteInferenceResolution() : null,
                BatchSize = SelectedBatchSize(),
                RvmInitializerAlphaThresholdPercent = algorithm is
                    "rvm-matanyone2" or "rvm-vitmatte-s" or "rvm-vitmatte-b" ||
                    algorithm == Sam2MattingSupport.Algorithm &&
                    promptMode == "rvm-initial-mask"
                    ? rvmInitializerThreshold.Value : null,
                RvmMatAnyoneMaskRefresh = algorithm == "rvm-matanyone2" &&
                    rvmMatAnyoneMaskRefresh.Checked,
                PropSegmenterEveryFrame = algorithm == "matanyone2" &&
                        propSegmenterEveryFrame.Checked ||
                    algorithm is
                    "rvm-vitmatte-s" or "rvm-vitmatte-b" &&
                        propSegmenterEveryFrame.Checked ||
                    algorithm == "rvm-matanyone2" &&
                        rvmMatAnyoneMaskRefresh.Checked &&
                        propSegmenterEveryFrame.Checked,
                DebugPropContribution = debugPropContribution.Checked,
                RvmMatAnyoneRefreshStrengthPercent = algorithm == "rvm-matanyone2"
                    ? rvmMatAnyoneRefreshStrength.Value : 100,
                MatAnyoneMaxMemoryFrames = algorithm is
                    "matanyone2" or "rvm-matanyone2"
                    ? (int)matAnyoneMaxMemoryFrames.Value : 5,
                MatAnyoneUseLongTermMemory = algorithm is
                    "matanyone2" or "rvm-matanyone2" &&
                    matAnyoneUseLongTermMemory.Checked,
                TemporalAlphaCleanup = !CustomClipMedia.IsRealtimeMode(algorithm) &&
                    temporalAlphaCleanup.Checked,
                TemporalAlphaCleanupWindowFrames =
                    (int)temporalAlphaCleanupWindow.Value,
                TemporalAlphaCleanupStrengthPercent =
                    temporalAlphaCleanupStrength.Value,
                TemporalAlphaTrackingStrengthPercent =
                    temporalAlphaTrackingStrength.Value,
                TemporalAlphaCleanupAlphaThreshold =
                    (int)temporalAlphaCleanupAlphaThreshold.Value,
                AutoAcceptedAlphaThreshold = algorithm is
                    (Sam2MattingSupport.Algorithm or CustomClipMedia.NvidiaAigsMode or
                     CustomClipMedia.RvmOnnxMode) ? null :
                        configuration.DefaultAlphaThreshold,
                Sam2Model = algorithm == "matanyone2" ? SelectedSam2Model() : null,
                ExecutionPolicy = algorithm == Sam2MattingSupport.Algorithm
                    ? "eager" : CustomClipMedia.IsRealtimeMode(algorithm)
                        ? "realtime-playback" : "auto",
                PrecisionPolicy = algorithm == Sam2MattingSupport.Algorithm
                    ? "bf16-autocast" : algorithm == CustomClipMedia.NvidiaAigsMode
                        ? "sdk-managed" : algorithm == CustomClipMedia.RvmOnnxMode
                            ? "onnx-directml-fp32" :
                            "fp16-autocast-fp32-cpu-fallback",
                RecurrentRefinementSteps = algorithm is "matanyone2" or
                    "rvm-matanyone2" ? 11 : 0,
                ToolRevisions = ReadProcessingRevisions(algorithm,
                    promptMode == "rvm-initial-mask"
                        ? "rvm-sam2" : SelectedMaskEngine())
            };
            ApplyPropSegmenter(processingOptions,
                algorithm is "matanyone2" or "rvm-matanyone2" or
                    "rvm-vitmatte-s" or "rvm-vitmatte-b" ||
                algorithm == Sam2MattingSupport.Algorithm &&
                    promptMode == "rvm-initial-mask" ||
                algorithm is ("vitmatte-s" or "vitmatte-b") &&
                    SelectedMaskEngine() == "rvm-sam2");
            manifest.Processing = processingOptions;
            FileInfo sourceInfo = new(source.Text);
            CustomShowQueueJob job = existing ?? new();
            bool masksStillMatch = existing != null &&
                existing.SourceLength == sourceInfo.Length &&
                existing.SourceLastWriteUtcTicks == sourceInfo.LastWriteTimeUtc.Ticks &&
                SameQueueLayout(existing.Clips, showClips);
            job.Operation = operation;
            job.Status = CustomShowQueueStatus.Pending;
            job.Manifest = manifest;
            job.Performer = CloneQueueValue(selectedProfile);
            job.Clips = CloneQueueValue(manifest.Clips);
            job.SourcePath = Path.GetFullPath(source.Text);
            job.RequestedOutputPath = Path.Combine(store.ShowsFolder,
                operation == CustomShowQueueOperation.New ? manifest.Id : targetId!);
            job.SourceLength = sourceInfo.Length;
            job.SourceLastWriteUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks;
            job.TargetShowId = targetId;
            job.TargetManifestSha256 = targetId == null ? null :
                CustomShowQueueStore.HashFile(Path.Combine(store.ShowsFolder,
                    targetId, "show.json"));
            job.KeepExistingMasks = reprocess && keepMasks.Checked;
            job.ReprocessClipIds = operation == CustomShowQueueOperation.Reprocess
                ? SelectedReprocessClipIds() : [];
            job.Percent = 0;
            job.Message = algorithm switch
            {
                CustomClipMedia.NvidiaAigsMode =>
                    operation == CustomShowQueueOperation.Reprocess
                        ? "Pending; existing RGB clips will be reused where possible"
                        : "Pending; RGB clips will be encoded when the job runs",
                CustomClipMedia.RvmOnnxMode =>
                    operation == CustomShowQueueOperation.Reprocess
                        ? "Pending; existing RGB clips will be reused where possible"
                        : "Pending; RGB clips will be encoded when the job runs",
                Sam2MattingSupport.Algorithm when promptMode == "rvm-initial-mask" =>
                    "Pending; RVM masks will be created when the job runs",
                _ => "Pending"
            };
            job.Error = null;
            job.StartedUtc = null; job.CompletedUtc = null;
            job.PublishedShowId = null; job.ReadyToPublish = false;
            Dictionary<string, string> masks = [];
            Dictionary<string, string> sceneMasks = [];
            Dictionary<string, long> maskFrames = [];
            HashSet<string> queuedReprocessClips = job.ReprocessClipIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            CustomShowClip[] clipsNeedingProcessing = showClips.Where(value =>
                value.Included && (operation != CustomShowQueueOperation.Reprocess ||
                    queuedReprocessClips.Contains(value.Id))).ToArray();
            if (operation == CustomShowQueueOperation.Reprocess &&
                clipsNeedingProcessing.Length == 0)
                throw new InvalidDataException("Select at least one clip to reprocess.");
            bool missingMask = clipsNeedingProcessing.Any(clip =>
                !job.InitialMaskAssets.TryGetValue(clip.Id, out string? relative) ||
                !File.Exists(Path.Combine(queueManager.AssetFolder(job.Id),
                    relative.Replace('/', Path.DirectorySeparatorChar))));
            if (algorithm == "matanyone2" && (!masksStillMatch || missingMask))
            {
                maskWork = Path.Combine(store.Root, ".queue-mask-work",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(maskWork);
                foreach (CustomShowClip clip in clipsNeedingProcessing)
                {
                    string clipFolder = Path.Combine(maskWork, clip.Id);
                    using CustomMaskEditorForm editor = new(source.Text, configuration,
                        clip.StartMs, clip.EndMs, showClips.Length == 1 ? null :
                        $"Clip {Array.IndexOf(showClips, clip) + 1} of {showClips.Length}",
                        "MatAnyone 2", allowFrameSelection: true,
                        sam2Model: SelectedSam2Model(), draftFolder: clipFolder);
                    if (editor.ShowDialog(this) != DialogResult.OK) return;
                    string mask = Path.Combine(clipFolder, "initial-mask.png");
                    editor.SaveMask(mask);
                    masks[clip.Id] = mask;
                    maskFrames[clip.Id] = editor.FrameMs;
                }
                job.InitialMaskFrameMs = maskFrames;
            }
            else if (algorithm != "matanyone2")
            {
                job.InitialMaskAssets.Clear();
                job.InitialMaskFrameMs.Clear();
            }
            if (algorithm == Sam2MattingSupport.Algorithm)
            {
                {
                    CustomShowProcessingScene[] scenesNeedingProcessing =
                        processingScenes.Where(scene => clipsNeedingProcessing.Any(clip =>
                            clip.Id.Equals(scene.ClipId,
                                StringComparison.OrdinalIgnoreCase))).ToArray();
                    string promptAssetOwner = queueDraftAssetOwnerId ?? existing?.Id ?? "";
                    bool promptsStillMatch = masksStillMatch && existing?.Manifest.Processing is
                        { Algorithm: "sam2matting" } prior && prior.Tracker == tracker &&
                        prior.PromptMode == promptMode &&
                        (promptMode != "rvm-initial-mask" ||
                            prior.RvmInitializerAlphaThresholdPercent ==
                                rvmInitializerThreshold.Value) &&
                        SameScenePlan(prior.Scenes, processingScenes) &&
                        existing.ScenePrompts.Length == scenesNeedingProcessing.Length &&
                        existing.ScenePrompts.All(prompt =>
                            File.Exists(Path.Combine(queueManager.AssetFolder(promptAssetOwner),
                                prompt.InitialMaskAsset.Replace('/',
                                    Path.DirectorySeparatorChar))));
                    if (!promptsStillMatch)
                    {
                        job.ScenePrompts = [];
                        if (promptMode == "rvm-initial-mask")
                        {
                            sceneMaskSummary.Text =
                                $"{scenesNeedingProcessing.Length} automatic RVM scene masks " +
                                "will be created when the job runs";
                        }
                        else
                        {
                            maskWork ??= Path.Combine(store.Root, ".queue-mask-work",
                                Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(maskWork);
                            using FfmpegCpuDecoder decoder = new(source.Text,
                                fastDecode: true);
                            if (!CustomShowStore.TryFrameRate(decoder.FrameRate,
                                    out double fps))
                                throw new InvalidDataException(
                                    "The source frame rate is invalid.");
                            for (int index = 0;
                                 index < scenesNeedingProcessing.Length; index++)
                            {
                                CustomShowProcessingScene scene =
                                    scenesNeedingProcessing[index];
                                string sceneFolder = Path.Combine(maskWork, scene.Id);
                                using CustomMaskEditorForm editor = new(source.Text,
                                    configuration, scene.StartMs, scene.EndMs,
                                    $"Scene {index + 1} of {scenesNeedingProcessing.Length}",
                                    Sam2MattingSupport.DisplayName(tracker),
                                    allowFrameSelection: true, sam2Model: tracker,
                                    draftFolder: sceneFolder, sam2Matting: true,
                                    showAutomaticMask: false);
                                if (editor.ShowDialog(this) != DialogResult.OK) return;
                                string mask = Path.Combine(sceneFolder,
                                    "initial-mask.png");
                                editor.SaveMask(mask);
                                long promptFrame = Math.Clamp(checked((long)Math.Round(
                                    editor.FrameMs / 1000d * fps,
                                    MidpointRounding.AwayFromZero)), scene.StartFrame,
                                    scene.EndFrameExclusive - 1);
                                job.ScenePrompts = [.. job.ScenePrompts,
                                    new CustomShowScenePrompt
                                    {
                                        SceneId = scene.Id, PromptFrame = promptFrame,
                                        PromptFrameMs = editor.FrameMs
                                    }];
                                sceneMasks[scene.Id] = mask;
                                sceneMaskSummary.Text =
                                    $"{index + 1} of {scenesNeedingProcessing.Length} scenes ready";
                            }
                        }
                    }
                    else if (queueDraftAssetOwnerId != null)
                        foreach (CustomShowScenePrompt prompt in job.ScenePrompts)
                            sceneMasks[prompt.SceneId] = Path.Combine(
                                queueManager.AssetFolder(queueDraftAssetOwnerId),
                                prompt.InitialMaskAsset.Replace('/',
                                    Path.DirectorySeparatorChar));
                }
            }
            else job.ScenePrompts = [];
            if (string.IsNullOrWhiteSpace(cover.Text)) job.CoverAsset = null;
            SaveNewQueuedPerformer(selectedProfile);
            string queuedId = queueManager.AddOrUpdate(job, queueJobId,
                string.IsNullOrWhiteSpace(cover.Text) ? null : cover.Text, masks,
                sceneMasks);
            RememberProcessingOptions();
            if (processNow)
            {
                string? publishedId = null;
                using CustomShowProcessingForm processing = new(
                    async (progress, token) =>
                    {
                        publishedId = await queueManager.RunNowAsync(queuedId,
                            progress, async (staging, processedShow) =>
                                await ReviewProcessedShow(staging,
                                    processedShow.Clips.Where(clip =>
                                        clip.Included).ToArray()) == DialogResult.OK,
                            token);
                        return new CustomShowProcessResult();
                    }, CustomClipMedia.IsRealtimeMode(algorithm)
                        ? algorithm == CustomClipMedia.RvmOnnxMode
                            ? "Preparing RVM ONNX Show"
                            : "Preparing NVIDIA AI Green Screen Show"
                        : "Processing SAM2Matting Show",
                    processDescription:
                        CustomClipMedia.IsRealtimeMode(algorithm)
                            ? operation == CustomShowQueueOperation.Reprocess
                                ? "Reusing existing RGB clips where possible, then opening the live foreground preview"
                                : "Encoding RGB clips, then opening the live foreground preview"
                            : "Processing the selected source now, then opening its alpha preview",
                    showPreviews: true);
                if (processing.ShowDialog(this) != DialogResult.OK ||
                    publishedId == null)
                {
                    queueManager.Delete(queuedId);
                    return;
                }
                SavedShowId = publishedId;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            CustomMaskEditorForm.CloseSam2MattingWorker();
            if (maskWork != null) CustomShowQueueStore.TryDelete(maskWork);
            action.Enabled = true;
        }
        await Task.CompletedTask;
    }

    async void QueueBatch(object? sender, EventArgs e)
    {
        queueBatch.Enabled = false;
        string? draftRoot = null;
        try
        {
            if (queueManager == null || SelectedPreset() != Sam2MattingSupport.Algorithm)
                return;
            if (selectedProfile == null)
                throw new InvalidDataException("Select or create a model profile.");
            CustomShowStore.ValidateProfile(selectedProfile);
            string tracker = SelectedSam2MattingTracker();
            string promptMode = SelectedSam2MattingPromptMode();
            if (!Sam2MattingSupport.IsInstalled(configuration, tracker))
                throw new InvalidDataException(
                    "Setup required: install the SAM2Matting environment and all checkpoints.");
            if (promptMode == "rvm-initial-mask" &&
                !CustomShowProcessor.IsRvmInitialMaskInstalled(configuration))
                throw new InvalidDataException(
                    "Setup required: install or repair the Robust Video Matting processing tools.");
            string[] concepts = [];
            using OpenFileDialog files = new()
            {
                Filter = "Video files|*.mp4;*.mov;*.mkv;*.avi;*.webm|All files|*.*",
                Multiselect = true, CheckFileExists = true,
                Title = "Select videos for the SAM2Matting batch"
            };
            if (files.ShowDialog(this) != DialogResult.OK || files.FileNames.Length == 0)
                return;
            using CustomShowBatchTitlesForm titles = new(files.FileNames,
                title.Text.Trim());
            if (titles.ShowDialog(this) != DialogResult.OK) return;
            (string Video, string Title)[] selected = titles.Items;
            List<(string Video, string Title, CustomShowClip Clip,
                CustomShowClipDetection Detection,
                CustomShowProcessingScene[] Scenes)> plans = [];
            using CustomShowProcessingForm planning = new(async (progress, token) =>
            {
                for (int index = 0; index < selected.Length; index++)
                {
                    (string video, string outputTitle) = selected[index];
                    using FfmpegCpuDecoder decoder = new(video, fastDecode: true);
                    CustomShowClip clip = new()
                    {
                        StartMs = 0,
                        EndMs = checked((long)Math.Round(decoder.Duration * 1000)),
                        Hotness = hotness.SelectedItem?.ToString() ?? "NoNudity",
                        ClipTypes = clipTypes.CheckedItems.Cast<object>()
                            .Select(value => value.ToString()!).ToArray(),
                        AlphaThreshold = configuration.DefaultAlphaThreshold
                    };
                    Progress<int> detectionProgress = new(value => progress.Report(
                        new CustomShowProgress("scene-analysis",
                            100d * (index + value / 100d) / selected.Length,
                            $"Video {index + 1}/{selected.Length}: detecting scenes… {value}%")));
                    var detected = await Sam2MattingScenePlanner.DetectAsync(
                        configuration, video, [clip], detectionProgress, token);
                    plans.Add((video, outputTitle, clip,
                        detected.Detection, detected.Scenes));
                }
                progress.Report(new CustomShowProgress("scene-analysis", 100,
                    $"Resolved scenes for {selected.Length} videos"));
                return new CustomShowProcessResult();
            }, "Preparing SAM2Matting Batch",
                processDescription:
                    "No queue jobs are committed until every video and scene prompt is valid",
                showPreviews: false);
            if (planning.ShowDialog(this) != DialogResult.OK) return;

            List<CustomShowQueueBatchEntry> entries = [];
            int sceneNumber = 0;
            int totalScenes = plans.Sum(plan => plan.Scenes.Length);
            foreach (var plan in plans)
            {
                CustomShowManifest manifest = new();
                ApplyFields(manifest, includeClipLayout: false);
                manifest.Title = plan.Title;
                manifest.Source = new() { Mode = "reference",
                    Path = Path.GetFullPath(plan.Video) };
                manifest.Clips = [plan.Clip];
                manifest.ClipDetection = plan.Detection;
                manifest.Processing = CreateSam2MattingProcessing(
                    tracker, promptMode, concepts, plan.Scenes);
                FileInfo sourceInfo = new(plan.Video);
                CustomShowQueueJob job = new()
                {
                    Operation = CustomShowQueueOperation.New,
                    Manifest = manifest,
                    Performer = CloneQueueValue(selectedProfile),
                    Clips = [CloneQueueValue(plan.Clip)],
                    SourcePath = Path.GetFullPath(plan.Video),
                    SourceLength = sourceInfo.Length,
                    SourceLastWriteUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks,
                    RequestedOutputPath = Path.Combine(store.ShowsFolder, manifest.Id),
                    Message = promptMode == "rvm-initial-mask"
                        ? "Pending; RVM masks will be created when the job runs"
                        : "Pending"
                };
                Dictionary<string, string> sceneMasks = [];
                if (promptMode == "rvm-initial-mask")
                {
                    job.ScenePrompts = [];
                }
                else
                {
                    draftRoot ??= Path.Combine(store.Root, ".queue-mask-work",
                        "batch-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(draftRoot);
                    foreach (CustomShowProcessingScene scene in plan.Scenes)
                    {
                        string sceneFolder = Path.Combine(draftRoot, job.Id, scene.Id);
                        using CustomMaskEditorForm editor = new(plan.Video,
                            configuration, scene.StartMs, scene.EndMs,
                            $"Video {plans.IndexOf(plan) + 1}/{plans.Count}, " +
                            $"scene {++sceneNumber}/{totalScenes}",
                            Sam2MattingSupport.DisplayName(tracker),
                            allowFrameSelection: true, sam2Model: tracker,
                            draftFolder: sceneFolder, sam2Matting: true,
                            showAutomaticMask: false);
                        if (editor.ShowDialog(this) != DialogResult.OK) return;
                        string mask = Path.Combine(sceneFolder, "initial-mask.png");
                        editor.SaveMask(mask);
                        using FfmpegCpuDecoder decoder = new(plan.Video,
                            fastDecode: true);
                        if (!CustomShowStore.TryFrameRate(decoder.FrameRate,
                                out double fps))
                            throw new InvalidDataException(
                                "The source frame rate is invalid.");
                        long promptFrame = Math.Clamp(checked((long)Math.Round(
                            editor.FrameMs / 1000d * fps,
                            MidpointRounding.AwayFromZero)), scene.StartFrame,
                            scene.EndFrameExclusive - 1);
                        job.ScenePrompts = [.. job.ScenePrompts,
                            new CustomShowScenePrompt
                            {
                                SceneId = scene.Id, PromptFrame = promptFrame,
                                PromptFrameMs = editor.FrameMs
                            }];
                        sceneMasks[scene.Id] = mask;
                    }
                }
                entries.Add(new CustomShowQueueBatchEntry(job,
                    string.IsNullOrWhiteSpace(cover.Text) ? null : cover.Text,
                    sceneMasks));
            }
            SaveNewQueuedPerformer(selectedProfile);
            queueManager.AddBatch(entries);
            RememberProcessingOptions();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            CustomMaskEditorForm.CloseSam2MattingWorker();
            if (draftRoot != null) CustomShowQueueStore.TryDelete(draftRoot);
            queueBatch.Enabled = true;
        }
        await Task.CompletedTask;
    }

    CustomShowProcessing CreateSam2MattingProcessing(string tracker,
        string promptMode, string[] concepts,
        CustomShowProcessingScene[] scenes)
    {
        CustomShowProcessing value = new()
        {
            Algorithm = Sam2MattingSupport.Algorithm,
            ProcessingOptionVersion = Sam2MattingSupport.OptionVersion,
            Tracker = tracker,
            PromptMode = promptMode,
            ForegroundConcepts = [.. concepts],
            EnvironmentSpecVersion = Sam2MattingSupport.EnvironmentVersion,
            SourceRevision = Sam2MattingSupport.SourceRevision,
            CheckpointRevision = Sam2MattingSupport.CheckpointRevision,
            CheckpointSha256 = Sam2MattingSupport.CheckpointSha256(tracker),
            AttentionPolicy = "pytorch-sdpa",
            PrecisionPolicy = "bf16-autocast",
            EncoderPolicy = "quickplayer-h264-aac",
            AlphaEncodingPolicy = Sam2MattingSupport.AlphaEncodingPolicy,
            ScenePlanVersion = Sam2MattingSupport.ScenePlanVersion,
            Scenes = CloneQueueValue(scenes),
            MattingDetailPx = 512,
            BatchSize = 0,
            RvmInitializerAlphaThresholdPercent = promptMode == "rvm-initial-mask"
                ? rvmInitializerThreshold.Value : null,
            AutoAcceptedAlphaThreshold = null,
            ExecutionPolicy = "eager",
            ToolRevisions = ReadProcessingRevisions(Sam2MattingSupport.Algorithm,
                promptMode == "rvm-initial-mask" ? "rvm-sam2" : "sam2")
        };
        ApplyPropSegmenter(value, promptMode == "rvm-initial-mask");
        return value;
    }

    void ApplyPropSegmenter(CustomShowProcessing processing, bool compatible)
    {
        PropSegmenterPackage? package = compatible
            ? SelectedPropSegmenterPackage() : null;
        if (package == null) return;
        if (!PropSegmenterPackage.TryLoad(package.ModelId, true,
                out PropSegmenterPackage? verified))
            throw new InvalidOperationException(
                "The selected prop-model package is missing or invalid.");
        package = verified!;
        if (package.ManifestSchemaVersion == 2 && processing.Algorithm is
                "rvm-vitmatte-s" or "rvm-vitmatte-b")
            return;
        if (package.ManifestSchemaVersion == 2)
        {
            processing.PropSegmenterEveryFrame = false;
            processing.DebugPropContribution = false;
        }
        processing.PropSegmenterModelId = package.ModelId;
        processing.PropSegmenterCheckpointSha256 = package.CheckpointSha256;
        processing.PropSegmenterConfidenceThreshold = package.ConfidenceThreshold;
        processing.PropSegmenterProximityRadiusAt512 = package.ProximityRadiusAt512;
        processing.PropSegmenterManifestSchemaVersion = package.ManifestSchemaVersion;
        processing.PropSegmenterArchitecture = package.Architecture;
        processing.PropSegmenterManifestSha256 = package.ManifestSha256;
        processing.PropSegmenterPostprocessingContract = package.PostprocessingContract;
    }

    void SaveNewQueuedPerformer(CustomPerformerProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.IstripperModelId)) return;
        string path = Path.Combine(store.PerformersFolder, profile.Id + ".json");
        if (!File.Exists(path)) store.SavePerformer(profile);
    }

    static bool SameQueueLayout(CustomShowClip[] first, CustomShowClip[] second) =>
        first.Length == second.Length && first.Zip(second).All(pair =>
            pair.First.Id == pair.Second.Id &&
            pair.First.StartMs == pair.Second.StartMs &&
            pair.First.EndMs == pair.Second.EndMs &&
            pair.First.Included == pair.Second.Included);

    static bool SameScenePlan(CustomShowProcessingScene[] first,
        CustomShowProcessingScene[] second) => first.Length == second.Length &&
        first.Zip(second).All(pair => pair.First.Id == pair.Second.Id &&
            pair.First.ClipId == pair.Second.ClipId &&
            pair.First.StartFrame == pair.Second.StartFrame &&
            pair.First.EndFrameExclusive == pair.Second.EndFrameExclusive &&
            pair.First.StartMs == pair.Second.StartMs &&
            pair.First.EndMs == pair.Second.EndMs);

    static T CloneQueueValue<T>(T value) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(value, CustomShowStore.JsonOptions),
        CustomShowStore.JsonOptions)!;

    bool ConfirmGpuBatch()
    {
        string algorithm = SelectedPreset();
        if (algorithm is ("matanyone2" or "rvm-matanyone2") &&
            SelectedMattingResolution() == 0)
        {
            using FfmpegCpuDecoder decoder = new(source.Text, fastDecode: true);
            if (CustomShowProcessor.MatAnyoneFullResolution4kRisk(algorithm, 0,
                    decoder.Width, decoder.Height))
            {
                CustomShowProcessor.NvidiaMemory? matAnyoneMemory =
                    CustomShowProcessor.NvidiaMemoryInfo();
                string memoryText = matAnyoneMemory == null ? "" :
                    $" The detected GPU has {matAnyoneMemory.TotalMiB / 1024d:0.#} GB total " +
                    $"and {matAnyoneMemory.FreeMiB / 1024d:0.#} GB currently free.";
                TaskDialogButton useVeryHigh = new("Use Very High (1024 px)");
                TaskDialogButton continueFull = new("Continue with Full resolution");
                TaskDialogButton cancelFullResolution = new("Cancel processing");
                TaskDialogPage fullResolutionWarning = new()
                {
                    Caption = "MatAnyone GPU Memory",
                    Heading = "Full-resolution 4K MatAnyone is unlikely to fit in GPU memory",
                    Text = $"The source is {decoder.Width}×{decoder.Height}. Full resolution " +
                        $"would run MatAnyone at {decoder.Width}×{decoder.Height} and is " +
                        "expected to exhaust GPU memory." + memoryText +
                        " Very High limits internal inference to 1024 px while preserving " +
                        "the source output resolution.",
                    Icon = TaskDialogIcon.Warning,
                    AllowCancel = true,
                    DefaultButton = useVeryHigh
                };
                fullResolutionWarning.Buttons.Add(useVeryHigh);
                fullResolutionWarning.Buttons.Add(continueFull);
                fullResolutionWarning.Buttons.Add(cancelFullResolution);
                TaskDialogButton fullResolutionChoice =
                    TaskDialog.ShowDialog(this, fullResolutionWarning);
                if (fullResolutionChoice == continueFull) return true;
                if (fullResolutionChoice != useVeryHigh) return false;
                mattingDetail.SelectedIndex = 4;
                return true;
            }
        }
        if (algorithm is not ("vitmatte-s" or "vitmatte-b" or
                "rvm-vitmatte-s" or "rvm-vitmatte-b") ||
            sequenceChunk.SelectedItem is not int requested) return true;
        CustomShowProcessor.NvidiaMemory? memory =
            CustomShowProcessor.NvidiaMemoryInfo();
        if (memory == null) return true;
        using FfmpegCpuDecoder vitMatteDecoder = new(source.Text, fastDecode: true);
        bool baseModel = algorithm is "vitmatte-b" or "rvm-vitmatte-b";
        int detail = SelectedVitMatteInferenceResolution();
        double scale = detail <= 0 ? 1 : Math.Min(1d,
            detail / (double)Math.Max(vitMatteDecoder.Width, vitMatteDecoder.Height));
        int suggested = CustomShowProcessor.ViTMatteSafeBatch(memory, baseModel,
            Math.Max(1, (int)Math.Round(vitMatteDecoder.Width * scale)),
            Math.Max(1, (int)Math.Round(vitMatteDecoder.Height * scale)));
        string name = baseModel ? "ViTMatte-B" : "ViTMatte-S";
        if (requested <= suggested) return true;
        TaskDialogButton useSuggested = new(
            $"Use suggested batch size {suggested}");
        TaskDialogButton continueRequested = new(
            $"Continue with batch size {requested}");
        TaskDialogButton cancelProcessing = new("Cancel processing");
        TaskDialogPage warning = new()
        {
            Caption = $"{name} GPU Memory",
            Heading = $"Batch size {requested} may exceed available GPU memory",
            Text = $"{name} batch size {requested} is unlikely to fit safely in the " +
                $"detected GPU memory ({memory.TotalMiB / 1024d:0.#} GB total, " +
                $"{memory.FreeMiB / 1024d:0.#} GB currently free).",
            Icon = TaskDialogIcon.Warning,
            AllowCancel = true,
            DefaultButton = useSuggested
        };
        warning.Buttons.Add(useSuggested);
        warning.Buttons.Add(continueRequested);
        warning.Buttons.Add(cancelProcessing);
        TaskDialogButton choice = TaskDialog.ShowDialog(this, warning);
        if (choice == continueRequested) return true;
        if (choice != useSuggested) return false;
        sequenceChunk.SelectedItem = suggested;
        return true;
    }

    bool RetainedMasksAvailable() => reprocess && RetainedMasksMatch(showClips);

    bool RetainedMasksMatch(IReadOnlyList<CustomShowClip> clips)
    {
        if (showId == null || string.IsNullOrWhiteSpace(source.Text) ||
            !File.Exists(source.Text)) return false;
        string root = Path.Combine(store.ShowsFolder, showId, "masks");
        CustomRetainedMasksManifest? manifest = CustomShowMaskDraft.Load<
            CustomRetainedMasksManifest>(Path.Combine(root, "retained-masks.json"));
        if (manifest == null) return false;
        FileInfo sourceInfo = new(source.Text);
        if (manifest.SourceLength != sourceInfo.Length ||
            manifest.SourceLastWriteUtcTicks != sourceInfo.LastWriteTimeUtc.Ticks)
            return false;
        return clips.Where(clip => clip.Included).Any(clip =>
            FindRetainedMask(root, manifest, clip) != null);
    }

    static CustomRetainedMaskClip? FindRetainedMask(string root,
        CustomRetainedMasksManifest manifest, CustomShowClip clip)
    {
        CustomRetainedMaskClip? saved = manifest.Clips.FirstOrDefault(value =>
            value.ClipId.Equals(clip.Id, StringComparison.OrdinalIgnoreCase) &&
            value.StartMs == clip.StartMs && value.EndMs == clip.EndMs);
        if (saved != null && RetainedMaskFilesExist(root, saved)) return saved;

        // Older partial reprocessing accidentally dropped untouched entries from the
        // manifest while leaving their SAM2 archives intact. Recover only when the
        // correction state proves that the archive spans this exact clip duration.
        string folder = Path.Combine(root, clip.Id);
        CustomVideoMaskDraft? corrections = CustomShowMaskDraft.Load<
            CustomVideoMaskDraft>(Path.Combine(folder, "corrections.json"));
        if (corrections == null || corrections.FrameCount <= 0 || corrections.Fps <= 0)
            return null;
        long expectedFrames = Math.Max(1, (long)Math.Round(
            (clip.EndMs - clip.StartMs) * corrections.Fps / 1000d));
        long tolerance = Math.Max(2, (long)Math.Ceiling(corrections.Fps / 10d));
        if (Math.Abs(corrections.FrameCount - expectedFrames) > tolerance) return null;
        CustomRetainedMaskClip recovered = new()
        {
            ClipId = clip.Id, StartMs = clip.StartMs, EndMs = clip.EndMs,
            InitialMaskFrameMs = clip.StartMs,
            HasInitialMask = File.Exists(Path.Combine(folder, "initial-mask.png")),
            HasTrackedMasks = File.Exists(Path.Combine(folder,
                "tracked-masks.iqpmask")),
            TrackedMaskArchive = "tracked-masks.iqpmask"
        };
        return RetainedMaskFilesExist(root, recovered) ? recovered : null;
    }

    static bool RetainedMaskFilesExist(string root, CustomRetainedMaskClip saved)
    {
        string folder = Path.Combine(root, saved.ClipId);
        return saved.HasInitialMask && File.Exists(Path.Combine(folder,
                "initial-mask.png")) ||
            saved.HasTrackedMasks && (
                Path.Combine(folder,
                    saved.TrackedMaskArchive ?? "tracked-masks.iqpmask") is string archive &&
                File.Exists(archive) && CustomMaskArchive.IsSupportedArchive(archive) ||
                Directory.Exists(Path.Combine(folder, "tracked-masks")) &&
                Directory.EnumerateFiles(Path.Combine(folder,
                    "tracked-masks"), "*.png").Any());
    }

    static bool CompleteTrackedMaskDraft(string statePath, string masksFolder)
    {
        CustomVideoMaskDraft? state = CustomShowMaskDraft.Load<CustomVideoMaskDraft>(
            statePath);
        if (state == null || state.FrameCount <= 0 || !Directory.Exists(masksFolder))
            return false;
        for (int frame = 1; frame <= state.FrameCount; frame++)
            if (!File.Exists(Path.Combine(masksFolder, $"{frame:00000000}.png")))
                return false;
        return true;
    }

    static void RemoveRetainedMaskEntries(string masksRoot,
        IEnumerable<string> clipIds)
    {
        string path = Path.Combine(masksRoot, "retained-masks.json");
        CustomRetainedMasksManifest? retained = CustomShowMaskDraft.Load<
            CustomRetainedMasksManifest>(path);
        if (retained == null) return;
        HashSet<string> removed = clipIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        retained.Clips = retained.Clips.Where(clip =>
            !removed.Contains(clip.ClipId)).ToArray();
        CustomShowStore.WriteJsonAtomic(path, retained);
    }

    static void LoadRetainedMasks(string root, CustomShowManifest show,
        string workRoot,
        Dictionary<string, string> initialMasks,
        Dictionary<string, long> initialMaskFrames,
        Dictionary<string, string> trackedMasks,
        IReadOnlySet<string>? selectedClipIds = null,
        Action<string>? status = null)
    {
        CustomRetainedMasksManifest? manifest = CustomShowMaskDraft.Load<
            CustomRetainedMasksManifest>(Path.Combine(root, "retained-masks.json"));
        if (manifest == null) return;
        Dictionary<string, CustomShowClip> clips = show.Clips.Where(clip => clip.Included)
            .ToDictionary(clip => clip.Id, StringComparer.OrdinalIgnoreCase);
        CustomRetainedMaskClip[] selected = clips.Values.Where(clip =>
                selectedClipIds == null || selectedClipIds.Contains(clip.Id))
            .Select(clip => FindRetainedMask(root, manifest, clip))
            .Where(saved => saved != null).Cast<CustomRetainedMaskClip>().ToArray();
        for (int savedIndex = 0; savedIndex < selected.Length; savedIndex++)
        {
            CustomRetainedMaskClip saved = selected[savedIndex];
            if (!clips.TryGetValue(saved.ClipId, out CustomShowClip? clip) ||
                clip.StartMs != saved.StartMs || clip.EndMs != saved.EndMs) continue;
            string folder = Path.Combine(root, saved.ClipId);
            string initial = Path.Combine(folder, "initial-mask.png");
            string tracked = Path.Combine(folder, "tracked-masks");
            string archive = Path.Combine(folder,
                saved.TrackedMaskArchive ?? "tracked-masks.iqpmask");
            if (saved.HasInitialMask && File.Exists(initial))
            {
                initialMasks[clip.Id] = initial;
                initialMaskFrames[clip.Id] = saved.InitialMaskFrameMs ?? clip.StartMs;
            }
            if (saved.HasTrackedMasks && File.Exists(archive) &&
                CustomMaskArchive.IsSupportedArchive(archive))
            {
                status?.Invoke($"Expanding retained masks for selected clip " +
                    $"{savedIndex + 1}/{selected.Length}…");
                tracked = Path.Combine(workRoot, saved.ClipId);
                CustomMaskArchive.Extract(archive, tracked);
            }
            if (saved.HasTrackedMasks && Directory.Exists(tracked) &&
                Directory.EnumerateFiles(tracked, "*.png").Any())
            {
                trackedMasks[clip.Id] = tracked;
                if (!initialMasks.ContainsKey(clip.Id))
                {
                    string first = Directory.EnumerateFiles(tracked, "*.png")
                        .OrderBy(path => Path.GetFileName(path),
                            StringComparer.OrdinalIgnoreCase).First();
                    initialMasks[clip.Id] = first;
                    initialMaskFrames[clip.Id] = clip.StartMs;
                }
            }
        }
    }

    static void SaveRetainedMasks(string staging, string sourcePath,
        IReadOnlyList<CustomShowClip> clips,
        IReadOnlyDictionary<string, string> draftFolders,
        IReadOnlyDictionary<string, string> initialMasks,
        IReadOnlyDictionary<string, long> initialMaskFrames,
        IReadOnlyDictionary<string, string> trackedMasks)
    {
        string root = Path.Combine(staging, "masks");
        Directory.CreateDirectory(root);
        CustomRetainedMasksManifest? previous = CustomShowMaskDraft.Load<
            CustomRetainedMasksManifest>(Path.Combine(root, "retained-masks.json"));
        Dictionary<string, CustomRetainedMaskClip> saved = previous == null ? [] :
            clips.Where(clip => clip.Included)
                .Select(clip => FindRetainedMask(root, previous, clip))
                .Where(mask => mask != null).Cast<CustomRetainedMaskClip>()
                .ToDictionary(mask => mask.ClipId, StringComparer.OrdinalIgnoreCase);
        foreach (CustomShowClip clip in clips.Where(clip => clip.Included))
        {
            string folder = Path.Combine(root, clip.Id);
            Directory.CreateDirectory(folder);
            bool hasInitial = initialMasks.TryGetValue(clip.Id, out string? initial) &&
                File.Exists(initial);
            bool hasTracked = trackedMasks.TryGetValue(clip.Id, out string? tracked) &&
                Directory.Exists(tracked) && Directory.EnumerateFiles(tracked, "*.png").Any();
            if (hasInitial)
            {
                string destination = Path.Combine(folder, "initial-mask.png");
                if (!SamePath(initial!, destination)) File.Copy(initial!, destination, true);
            }
            if (hasTracked)
            {
                CustomMaskArchive.Create(tracked!, Path.Combine(folder,
                    "tracked-masks.iqpmask"));
            }
            if (draftFolders.TryGetValue(clip.Id, out string? draft))
                foreach ((string from, string to) in new[]
                {
                    (Path.Combine(draft, "initial-mask.json"),
                        Path.Combine(folder, "initial-mask.json")),
                    (Path.Combine(draft, "sam2-corrections.json"),
                        Path.Combine(folder, "corrections.json")),
                    (Path.Combine(draft, "rvm-review.json"),
                        Path.Combine(folder, "corrections.json"))
                })
                    if (File.Exists(from)) File.Copy(from, to, true);
            if (hasInitial || hasTracked)
                saved[clip.Id] = new CustomRetainedMaskClip
                {
                    ClipId = clip.Id, StartMs = clip.StartMs, EndMs = clip.EndMs,
                    InitialMaskFrameMs = initialMaskFrames.GetValueOrDefault(
                        clip.Id, clip.StartMs),
                    HasInitialMask = hasInitial, HasTrackedMasks = hasTracked,
                    TrackedMaskArchive = hasTracked ? "tracked-masks.iqpmask" : null
                };
        }
        FileInfo source = new(sourcePath);
        CustomShowStore.WriteJsonAtomic(Path.Combine(root, "retained-masks.json"),
            new CustomRetainedMasksManifest
            {
                SourceLength = source.Length,
                SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
                Clips = [.. saved.Values]
            });
    }

    static IEnumerable<string> GeneratedRvmMaskFolders(string staging,
        IReadOnlyDictionary<string, string> trackedMasks) =>
        trackedMasks.Values.Where(path => IsGeneratedRvmMaskFolder(staging, path));

    static bool IsGeneratedRvmMaskFolder(string staging, string path) =>
        path.StartsWith(staging + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(path).Equals(".rvm-masks",
            StringComparison.OrdinalIgnoreCase);

    static void CompressGeneratedRvmMasks(string staging,
        IReadOnlyDictionary<string, string> trackedMasks)
    {
        foreach ((string clipId, string masks) in trackedMasks)
        {
            if (!IsGeneratedRvmMaskFolder(staging, masks) ||
                !Directory.Exists(masks) ||
                !Directory.EnumerateFiles(masks, "*.png").Any())
                continue;
            string folder = Path.Combine(staging, "masks", clipId);
            Directory.CreateDirectory(folder);
            CustomMaskArchive.Create(masks, Path.Combine(folder,
                "tracked-masks.iqpmask"));
        }
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (string folder in Directory.EnumerateDirectories(source))
            CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }

    async Task<bool> PrepareStaging(CustomShowManifest show, string staging,
        bool copyExistingShow)
    {
        using CustomShowProcessingForm preparing = new(async (progress, token) =>
        {
            progress.Report(new CustomShowProgress("preparing", 0,
                "Preparing a safe working copy…"));
            await Task.Run(() => DeleteDirectoryWhenReleasedAsync(staging), token);
            token.ThrowIfCancellationRequested();
            if (copyExistingShow)
            {
                string existing = Path.Combine(store.ShowsFolder, show.Id);
                await Task.Run(() => CopyDirectoryWithProgress(existing, staging,
                    progress, token), token);
            }
            else Directory.CreateDirectory(staging);
            progress.Report(new CustomShowProgress("preparing", 100,
                "Working copy ready"));
            return new CustomShowProcessResult();
        }, reprocess ? "Preparing Reprocessing" : "Preparing Custom Show",
            processDescription: copyExistingShow
                ? "Creating a safe working copy of the existing show"
                : "Preparing the custom show workspace",
            showPreviews: false);
        return preparing.ShowDialog(this) == DialogResult.OK;
    }

    static void CopyDirectoryWithProgress(string source, string destination,
        IProgress<CustomShowProgress> progress, CancellationToken token)
    {
        int copied = 0;
        Copy(source, destination);
        return;

        void Copy(string from, string to)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from))
            {
                token.ThrowIfCancellationRequested();
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
                copied++;
                if (copied == 1 || copied % 100 == 0)
                    progress.Report(new CustomShowProgress("preparing", 0,
                        $"Copied {copied:N0} files into the safe working copy"));
            }
            foreach (string folder in Directory.EnumerateDirectories(from))
                Copy(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
    }

    static void RemoveExpandedRetainedMasks(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            string expanded = Path.Combine(folder, "tracked-masks");
            if (Directory.Exists(expanded)) Directory.Delete(expanded, true);
        }
    }

    Dictionary<string, string> ReadProcessingRevisions(string selectedPreset,
        string maskEngine)
    {
        string[] markers = selectedPreset switch
        {
            "quality" or "fast" => ["RVM_COMMIT"],
            "matanyone2" => ["MATANYONE2_COMMIT", "SAM2_COMMIT"],
            "rvm-matanyone2" => ["MATANYONE2_COMMIT", "RVM_COMMIT"],
            "rvm-vitmatte-s" => ["VITMATTE_S_REVISION", "RVM_COMMIT"],
            "rvm-vitmatte-b" => ["VITMATTE_B_REVISION", "RVM_COMMIT"],
            "sam2matting" when maskEngine == "rvm-sam2" => ["RVM_COMMIT"],
            "vitmatte-s" => ["VITMATTE_S_REVISION", "SAM2_COMMIT"],
            "vitmatte-b" => ["VITMATTE_B_REVISION", "SAM2_COMMIT"],
            _ => []
        };
        if (UsesSam2(selectedPreset))
            markers = maskEngine switch
            {
                "edgetam" => [.. markers, "EDGETAM_COMMIT"],
                "rvm" => [.. markers.Where(value => value != "SAM2_COMMIT"),
                    "RVM_COMMIT"],
                "rvm-sam2" => [.. markers, "RVM_COMMIT"],
                _ => markers
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
            review.NvidiaSetupRequested += () =>
            {
                using NvidiaVfxSetupForm setup = new(configuration);
                if (setup.ShowDialog(review) == DialogResult.OK)
                    review.RefreshPreview();
            };
            review.RvmOnnxSetupRequested += () =>
            {
                using RvmOnnxSetupForm setup = new();
                if (setup.ShowDialog(review) == DialogResult.OK)
                    review.RefreshPreview();
            };
            review.RvmOnnxSettingsChanged += async clip =>
            {
                if (previewClipId == clip.Id && preview is { IsDisposed: false })
                    await preview.SetRvmOnnxSettingsAsync(clip.RvmOnnx ?? new());
            };
            review.PreviewChanged += (clip, threshold) =>
            {
                if (previewClipId == clip.Id &&
                    preview is { IsDisposed: false } &&
                    clip.Media?.Mode != CustomClipMedia.NvidiaAigsMode)
                {
                    preview.SetAlphaThreshold(threshold);
                    return;
                }
                preview?.Close();
                preview = new CustomPlayerForm(
                    CustomShowStore.ResolveRelative(
                        staging, clip.Media!.Foreground),
                    CustomClipMedia.IsRealtimeMode(clip.Media.Mode) ? null :
                        CustomShowStore.ResolveRelative(
                            staging, clip.Media.Alpha!),
                    alphaThreshold: threshold,
                    fullOpacityThreshold:
                        configuration.FullOpacityThreshold,
                    nvidiaSdkRoot: CustomClipMedia.IsRealtimeMode(
                        clip.Media.Mode)
                            ? configuration.NvidiaVfxSdkRoot : null,
                    nvidiaSettings: clip.Nvidia,
                    rvmOnnxModelPath: RvmOnnxSupport.ModelPathFor(
                        clip.RvmOnnx?.Model ?? RvmOnnxSupport.MobileNetV3),
                    rvmOnnxSettings: clip.RvmOnnx);
                previews.Add(preview);
                preview.NvidiaFallback += reason => BeginInvoke(() =>
                    review.ShowNvidiaWarning(reason));
                preview.RvmOnnxFallback += reason => BeginInvoke(() =>
                    review.ShowRvmOnnxWarning(reason));
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

    void ApplyFields(CustomShowManifest show, bool includeClipLayout = true)
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
        show.Gender = gender.SelectedItem?.ToString() ?? "Female";
        show.Hotness = hotness.SelectedItem?.ToString() ?? "NoNudity";
        show.PerformerCount = (int)performerCount.Value;
        show.ClipTypes = clipTypes.CheckedItems.Cast<object>()
            .Select(item => item.ToString()!).ToArray();
        if (includeClipLayout)
        {
            show.Clips = showClips;
            show.ClipDetection = clipDetection;
        }
        show.Media.CoverTitleColor = ColorHex(coverTitleColor.BackColor);
        show.Media.CoverModelFont = SelectedCoverModelFont();
        show.Media.CoverTitleFont = SelectedCoverTitleFont();
        show.Media.PhotosFolder = photosFolder.Text.Trim();
        if (string.IsNullOrWhiteSpace(show.Title)) throw new InvalidDataException("Show title is required.");
    }

    void AdoptSelectedProfileGender(string showGender)
    {
        if (selectedProfile == null ||
            !string.IsNullOrWhiteSpace(selectedProfile.Gender)) return;
        selectedProfile.Gender = showGender;
    }

    static CustomShowClip[] OffsetClips(IEnumerable<CustomShowClip> clips, long offset,
        CustomShowSource source) =>
        clips.Select(clip => new CustomShowClip
        {
            Id = clip.Id,
            StartMs = checked(clip.StartMs + offset),
            EndMs = checked(clip.EndMs + offset),
            Included = clip.Included,
            Hotness = clip.Hotness,
            ClipTypes = [.. clip.ClipTypes],
            AlphaThreshold = clip.AlphaThreshold,
            EdgeChokePixels = clip.EdgeChokePixels,
            DetectionLabels = [.. clip.DetectionLabels],
            Source = new CustomShowSource { Mode = source.Mode, Path = source.Path },
            SourceStartMs = clip.StartMs,
            SourceEndMs = clip.EndMs,
            Media = clip.Media
        }).ToArray();

    string SourceVideo(CustomShowManifest show)
    {
        return SourceVideo(show, show.Source);
    }

    string SourceVideo(CustomShowManifest show, CustomShowSource source)
    {
        string folder = Path.Combine(store.ShowsFolder, show.Id);
        return source.Mode == "copy"
            ? CustomShowStore.ResolveRelative(folder, source.Path)
            : source.Path;
    }

    (string Video, long PositionMs) CoverFrameSource(CustomShowManifest show)
    {
        (CustomShowSource source, long positionMs) = CoverFramePosition(show);
        return (SourceVideo(show, source), positionMs);
    }

    static (CustomShowSource Source, long PositionMs) CoverFramePosition(
        CustomShowManifest show)
    {
        CustomShowClip[] playable = show.Clips.Where(clip => clip.Included &&
            clip.EndMs > clip.StartMs).ToArray();
        long remaining = playable.Sum(clip => clip.EndMs - clip.StartMs) / 2;
        foreach (CustomShowClip clip in playable)
        {
            long duration = clip.EndMs - clip.StartMs;
            if (remaining >= duration)
            {
                remaining -= duration;
                continue;
            }

            CustomShowSource clipSource = clip.Source ?? show.Source;
            long sourceStart = clip.SourceStartMs ?? clip.StartMs;
            return (clipSource, checked(sourceStart + remaining));
        }

        return (show.Source, Math.Max(0, show.Media.DurationMs / 2));
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
        (string video, long positionMs) = CoverFrameSource(show);
        if (!File.Exists(video))
            throw new FileNotFoundException(
                "The original source video is required to regenerate the automatic cover.", video);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp.jpg";
        try
        {
            await CustomShowProcessor.GenerateCoverAsync(video, temporary,
                positionMs, selectedProfile!.ModelName, show.Title,
                coverTitleColor.BackColor, CancellationToken.None,
                SelectedCoverModelFont(), SelectedCoverTitleFont());
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    bool AutoCoverSettingsChanged(CustomShowManifest show) =>
        !string.Equals(originalCoverTitle, show.Title, StringComparison.Ordinal) ||
        !string.Equals(originalCoverPerformerId, show.PerformerId,
            StringComparison.Ordinal) ||
        !string.Equals(originalCoverModelName, selectedProfile?.ModelName,
            StringComparison.Ordinal) ||
        !string.Equals(originalCoverTitleColor, show.Media.CoverTitleColor,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(originalCoverModelFont, show.Media.CoverModelFont,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(originalCoverTitleFont, show.Media.CoverTitleFont,
            StringComparison.OrdinalIgnoreCase);

    static string ColorHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    static void SaveCover(string source, string destination, string modelName,
        string showTitle, Color titleColor, string modelFont, string titleFont,
        string? cleanSourceDestination = null)
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
        if (!string.IsNullOrWhiteSpace(cleanSourceDestination))
            SaveCoverBitmap(copy, cleanSourceDestination);
        CustomShowProcessor.DrawCoverText(copy, modelName, showTitle, titleColor,
            modelFont, titleFont);
        SaveCoverBitmap(copy, destination);
    }

    static void SaveCoverBitmap(Bitmap image, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            image.Save(temporary, ImageFormat.Jpeg);
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

    internal static bool VerifyAppendLayout()
    {
        CustomShowSource source = new() { Mode = "reference", Path = @"C:\second.mp4" };
        CustomShowClip[] appended = OffsetClips([
            new() { StartMs = 0, EndMs = 400 },
            new() { StartMs = 400, EndMs = 1_000, Included = false }
        ], 2_000, source);
        CustomShowManifest combined = new()
        {
            Source = new() { Mode = "reference", Path = @"C:\first.mp4" },
            Media = new() { DurationMs = 3_000 },
            Clips =
            [
                new() { StartMs = 0, EndMs = 1_000 },
                .. appended
            ]
        };
        (CustomShowSource coverSource, long coverPosition) =
            CoverFramePosition(combined);
        return appended[0].StartMs == 2_000 && appended[0].EndMs == 2_400 &&
            appended[1].StartMs == 2_400 && appended[1].EndMs == 3_000 &&
            appended.All(clip => clip.Source?.Path == source.Path) &&
            appended[0].SourceStartMs == 0 && appended[0].SourceEndMs == 400 &&
            appended[1].SourceStartMs == 400 && appended[1].SourceEndMs == 1_000 &&
            coverSource.Path == combined.Source.Path && coverPosition == 700;
    }

    static bool RoutesSaveToQueue(bool metadataOnly, string algorithm) =>
        !metadataOnly && algorithm is
            (Sam2MattingSupport.Algorithm or CustomClipMedia.NvidiaAigsMode or
             CustomClipMedia.RvmOnnxMode);

    internal static bool VerifySaveRouting() =>
        !RoutesSaveToQueue(true, Sam2MattingSupport.Algorithm) &&
        RoutesSaveToQueue(false, Sam2MattingSupport.Algorithm) &&
        RoutesSaveToQueue(false, CustomClipMedia.NvidiaAigsMode) &&
        RoutesSaveToQueue(false, CustomClipMedia.RvmOnnxMode) &&
        !RoutesSaveToQueue(false, "quality");

    static TableLayoutPanel SectionTable()
    {
        TableLayoutPanel table = new()
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3,
            Padding = new Padding(8)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.SuspendLayout();
        return table;
    }

    static void AddRootControl(TableLayoutPanel root, Control control)
    {
        int row = root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top;
        root.Controls.Add(control, 0, row);
    }

    static void AddSection(TableLayoutPanel root, string title,
        TableLayoutPanel contents)
    {
        contents.ResumeLayout(false);
        GroupBox group = new()
        {
            Text = title, AutoSize = true, Dock = DockStyle.Top,
            Padding = new Padding(8), Margin = new Padding(0, 0, 0, 14)
        };
        group.Controls.Add(contents);
        AddRootControl(root, group);
    }

    static void AddCollapsibleSection(TableLayoutPanel root, string title,
        TableLayoutPanel contents)
    {
        contents.ResumeLayout(false);
        Button toggle = new()
        {
            Text = $"▶  {title}", AutoSize = false, Height = 36,
            Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 0, 14)
        };
        GroupBox body = new() { AutoSize = true, Dock = DockStyle.Top,
            Padding = new Padding(8), Visible = false,
            Margin = new Padding(0, 0, 0, 14) };
        body.Controls.Add(contents);
        AddRootControl(root, toggle);
        AddRootControl(root, body);
        toggle.Click += (_, _) =>
        {
            ScrollableControl? scroll = root.Parent as ScrollableControl;
            scroll?.SuspendLayout();
            root.SuspendLayout();
            bool expanding = !body.Visible;
            toggle.Margin = expanding
                ? Padding.Empty : new Padding(0, 0, 0, 14);
            body.Visible = expanding;
            toggle.Text = $"{(expanding ? "▼" : "▶")}  {title}";
            root.ResumeLayout(true);
            scroll?.ResumeLayout(true);
        };
    }

    static int AddRow(TableLayoutPanel table, string label, Control control,
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
        return row;
    }

    static void SetRowVisible(TableLayoutPanel table, int row, bool visible)
    {
        if (row < 0 || row >= table.RowCount) return;
        foreach (Control control in table.Controls.Cast<Control>()
            .Where(control => table.GetRow(control) == row))
            control.Visible = visible;
        table.RowStyles[row].SizeType = visible ? SizeType.AutoSize : SizeType.Absolute;
        table.RowStyles[row].Height = 0;
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

    static void AddFolderRow(TableLayoutPanel table, string label, TextBox box)
    {
        Button browse = new() { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using FolderBrowserDialog dialog = new()
            {
                SelectedPath = Directory.Exists(box.Text) ? box.Text : "",
                Description = "Select the folder containing photos for this custom show",
                UseDescriptionForTitle = true
            };
            if (dialog.ShowDialog(table.FindForm()) == DialogResult.OK)
                box.Text = dialog.SelectedPath;
        };
        AddRow(table, label, box, browse);
    }

    static FlowLayoutPanel Flow(params Control[] controls)
    {
        FlowLayoutPanel panel = new()
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, WrapContents = false
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    static FlowLayoutPanel FlowVertical(params Control[] controls)
    {
        FlowLayoutPanel panel = new()
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        panel.Controls.AddRange(controls);
        return panel;
    }
}

internal sealed class CustomShowBatchTitlesForm : Form
{
    readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AutoGenerateColumns = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect
    };
    readonly string[] videos;
    internal (string Video, string Title)[] Items => videos.Select((video, index) =>
        (video, Convert.ToString(grid.Rows[index].Cells[1].Value)?.Trim() ?? ""))
        .ToArray();

    internal CustomShowBatchTitlesForm(string[] videos, string baseTitle)
    {
        this.videos = [.. videos];
        Text = "Review Batch Output Titles";
        ClientSize = new Size(820, 440);
        StartPosition = FormStartPosition.CenterParent;
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Source video", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 55
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Output title", AutoSizeMode =
                DataGridViewAutoSizeColumnMode.Fill, FillWeight = 45
        });
        foreach (string video in videos)
        {
            string filename = Path.GetFileNameWithoutExtension(video);
            string outputTitle = string.IsNullOrWhiteSpace(baseTitle)
                ? filename : videos.Length == 1 ? baseTitle : $"{baseTitle} - {filename}";
            grid.Rows.Add(Path.GetFileName(video), outputTitle);
        }
        Button accept = new() { Text = "Continue", AutoSize = true };
        Button cancel = new() { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        accept.Click += (_, _) =>
        {
            grid.EndEdit();
            if (Items.Any(item => string.IsNullOrWhiteSpace(item.Title)))
            {
                MessageBox.Show(this, "Every batch item requires an output title.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, AutoSize = true,
            Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.AddRange([accept, cancel]);
        Controls.Add(grid);
        Controls.Add(buttons);
        AcceptButton = accept;
        CancelButton = cancel;
        AppTheme.Apply(this);
    }
}

internal sealed class CustomShowIncompletePickerForm : Form
{
    sealed record Choice(CustomShowIncompleteSetupEntry Entry, string Label)
    {
        public override string ToString() => Label;
    }

    readonly ListBox drafts = new() { Dock = DockStyle.Fill };
    readonly Button restore = new() { Text = "Restore selected setup",
        AutoSize = true, Enabled = false };
    internal CustomShowIncompleteSetupEntry? Selected { get; private set; }

    internal CustomShowIncompletePickerForm(CustomShowStore store)
    {
        Text = "Restore Incomplete Custom Show";
        ClientSize = new Size(650, 390);
        MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.CenterParent;
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill,
            Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "Select saved clips and masks to continue processing:",
            AutoSize = true, Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);
        layout.Controls.Add(drafts, 0, 1);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 10, 0, 0) };
        Button cancel = new() { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([cancel, restore]);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        CancelButton = cancel;

        foreach (CustomShowIncompleteSetupEntry entry in
            CustomShowMaskDraft.FindIncomplete(store))
        {
            CustomShowManifest show = entry.Setup.Show;
            int masks = Directory.EnumerateFiles(entry.Folder, "*.png",
                SearchOption.AllDirectories).Count();
            drafts.Items.Add(new Choice(entry,
                $"{show.Title} — {show.Clips.Count(clip => clip.Included)} clips, " +
                $"{masks:N0} saved masks — {entry.Setup.SavedUtc.ToLocalTime():g}"));
        }
        if (drafts.Items.Count == 0)
            drafts.Items.Add("No recoverable incomplete setups were found.");
        drafts.SelectedIndexChanged += (_, _) => restore.Enabled =
            drafts.SelectedItem is Choice;
        drafts.DoubleClick += (_, _) => AcceptSelection();
        restore.Click += (_, _) => AcceptSelection();
        AppTheme.Apply(this);
    }

    void AcceptSelection()
    {
        if (drafts.SelectedItem is not Choice choice) return;
        Selected = choice.Entry;
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class CustomShowPickerForm : Form
{
    sealed record Choice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    readonly TextBox filter = new() { Dock = DockStyle.Top,
        PlaceholderText = "Filter by show or model name" };
    readonly ListBox shows = new() { Dock = DockStyle.Fill };
    readonly Button select = new() { Text = "Add to selected show", AutoSize = true,
        Enabled = false };
    readonly Choice[] choices;

    internal string? ShowId { get; private set; }

    internal CustomShowPickerForm(CustomShowStore store,
        string title = "Add To Existing Show",
        string selectText = "Add to selected show")
    {
        Text = title;
        select.Text = selectText;
        ClientSize = new Size(560, 520);
        MinimumSize = new Size(420, 340);
        StartPosition = FormStartPosition.CenterParent;
        choices = LoadChoices(store);

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(12),
            ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(filter, 0, 0);
        layout.Controls.Add(shows, 0, 1);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft };
        Button cancel = new() { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([cancel, select]);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        CancelButton = cancel;

        filter.TextChanged += (_, _) => RefreshChoices();
        shows.SelectedIndexChanged += (_, _) => select.Enabled =
            shows.SelectedItem is Choice;
        shows.DoubleClick += (_, _) => AcceptSelection();
        select.Click += (_, _) => AcceptSelection();
        RefreshChoices();
        AppTheme.Apply(this);
    }

    static Choice[] LoadChoices(CustomShowStore store)
    {
        Dictionary<string, string> models = store.LoadPerformers().ToDictionary(
            profile => profile.Id, profile => profile.ModelName,
            StringComparer.OrdinalIgnoreCase);
        List<Choice> result = [];
        foreach (string folder in Directory.EnumerateDirectories(store.ShowsFolder))
            try
            {
                CustomShowManifest show = store.LoadManifest(Path.GetFileName(folder));
                result.Add(new Choice(show.Id,
                    $"{models.GetValueOrDefault(show.PerformerId, "Unknown model")} — {show.Title}"));
            }
            catch (Exception error)
            {
                Debug.WriteLine($"Could not list custom show {folder}: {error.Message}");
            }
        return result.OrderBy(choice => choice.Label,
            StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    void RefreshChoices()
    {
        string query = filter.Text.Trim();
        Choice? selected = shows.SelectedItem as Choice;
        shows.BeginUpdate();
        shows.Items.Clear();
        shows.Items.AddRange(choices.Where(choice => query.Length == 0 ||
            choice.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Cast<object>().ToArray());
        if (selected != null)
            shows.SelectedItem = shows.Items.Cast<Choice>().FirstOrDefault(
                choice => choice.Id == selected.Id);
        if (shows.SelectedIndex < 0 && shows.Items.Count > 0) shows.SelectedIndex = 0;
        shows.EndUpdate();
    }

    void AcceptSelection()
    {
        if (shows.SelectedItem is not Choice choice) return;
        ShowId = choice.Id;
        DialogResult = DialogResult.OK;
        Close();
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
    readonly ComboBox nvidiaMode = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 210 };
    readonly CheckBox nvidiaTemporal = new() { Text = "Temporal", AutoSize = true };
    readonly ComboBox nvidiaResolution = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    readonly ComboBox rvmOnnxQuality = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140,
          DropDownWidth = 180 };
    readonly ComboBox rvmOnnxModel = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    readonly CheckBox rvmOnnxTemporal = new()
        { Text = "Temporal memory", AutoSize = true };
    FlowLayoutPanel? tuning;
    readonly Label message = new() { Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter };
    bool loading;
    internal event Action<CustomShowClip, int>? PreviewChanged;
    internal event Action<CustomShowClip>? RvmOnnxSettingsChanged;
    internal event Action? NvidiaSetupRequested;
    internal event Action? RvmOnnxSetupRequested;

    internal CustomShowDecisionForm(string logPath, bool allowAccept,
        IReadOnlyList<CustomShowClip>? clips = null)
    {
        this.clips = clips?.ToArray() ?? [];
        Text = allowAccept ? "Review Custom Show" : "Processing Failed";
        ClientSize = new Size(960, allowAccept ? 180 : 130);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        message.Text = allowAccept
                ? "Tune pixels below the alpha threshold to transparent, then accept, retry, inspect the log, or discard."
                : "Processing failed. Retry, inspect processing.log, or discard the staged show.";
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
        if (allowAccept && this.clips.Any(value => value.Media?.Mode ==
                CustomClipMedia.NvidiaAigsMode))
        {
            Button setupNvidia = new() { Text = "Setup NVIDIA...", AutoSize = true };
            setupNvidia.Click += (_, _) => NvidiaSetupRequested?.Invoke();
            buttons.Controls.Add(setupNvidia);
        }
        if (allowAccept && this.clips.Any(value => value.Media?.Mode ==
                CustomClipMedia.RvmOnnxMode))
        {
            Button setupRvm = new() { Text = "Setup RVM ONNX...", AutoSize = true };
            setupRvm.Click += (_, _) => RvmOnnxSetupRequested?.Invoke();
            buttons.Controls.Add(setupRvm);
        }
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
            tuning = new FlowLayoutPanel
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
            nvidiaMode.Items.AddRange(["Quality / chairs FG",
                "Performance / chairs FG", "Quality / chairs BG",
                "Performance / chairs BG"]);
            nvidiaResolution.Items.AddRange(["540p", "720p", "1080p", "Source"]);
            rvmOnnxModel.Items.AddRange(["MobileNetV3", "ResNet50"]);
            rvmOnnxQuality.Items.AddRange(["Fast", "Balanced", "Quality",
                "Full-res refine"]);
            tuning.Controls.AddRange([nvidiaMode, nvidiaTemporal,
                nvidiaResolution, rvmOnnxModel, rvmOnnxQuality,
                rvmOnnxTemporal]);
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
            nvidiaMode.SelectedIndexChanged += (_, _) => ChangeNvidia();
            nvidiaTemporal.CheckedChanged += (_, _) => ChangeNvidia();
            nvidiaResolution.SelectedIndexChanged += (_, _) => ChangeNvidia();
            rvmOnnxModel.SelectedIndexChanged += (_, _) => ChangeRvmOnnx();
            rvmOnnxQuality.SelectedIndexChanged += (_, _) => ChangeRvmOnnx();
            rvmOnnxTemporal.CheckedChanged += (_, _) => ChangeRvmOnnx();
            Shown += (_, _) => { if (clip.SelectedIndex < 0) clip.SelectedIndex = 0; };
        }
        AppTheme.Apply(this);
    }

    void SelectClip()
    {
        if (clip.SelectedIndex < 0) return;
        loading = true;
        CustomShowClip selected = clips[clip.SelectedIndex];
        threshold.Value = selected.AlphaThreshold;
        thresholdText.Text = threshold.Value.ToString();
        bool isNvidia = selected.Media?.Mode == CustomClipMedia.NvidiaAigsMode;
        bool isRvmOnnx = selected.Media?.Mode == CustomClipMedia.RvmOnnxMode;
        threshold.Visible = thresholdText.Visible = !isNvidia;
        nvidiaMode.Visible = nvidiaTemporal.Visible =
            nvidiaResolution.Visible = isNvidia;
        rvmOnnxModel.Visible = rvmOnnxQuality.Visible =
            rvmOnnxTemporal.Visible = isRvmOnnx;
        if (isNvidia)
        {
            CustomNvidiaSettings settings = selected.Nvidia ??= new();
            nvidiaMode.SelectedIndex = Math.Clamp(settings.Mode, 0, 3);
            nvidiaTemporal.Checked = settings.Temporal;
            nvidiaResolution.SelectedIndex = settings.InferenceResolution switch
                { "540p" => 0, "1080p" => 2, "source" => 3, _ => 1 };
        }
        if (isRvmOnnx)
        {
            CustomRvmOnnxSettings settings = selected.RvmOnnx ??= new();
            rvmOnnxModel.SelectedIndex = settings.Model ==
                RvmOnnxSupport.ResNet50 ? 1 : 0;
            rvmOnnxQuality.SelectedIndex = RvmOnnxSupport.QualityIndex(
                settings.Quality);
            rvmOnnxTemporal.Checked = settings.Temporal;
        }
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

    internal void ShowNvidiaWarning(string reason)
    {
        if (IsDisposed) return;
        message.Text = "NVIDIA AI Green Screen is unavailable. Preview is opaque RGB; " +
            "you may still accept it or use Open Setup in custom-show creation. " + reason;
    }

    internal void ShowRvmOnnxWarning(string reason)
    {
        if (IsDisposed) return;
        message.Text = "RVM ONNX is unavailable. Preview is opaque RGB; " +
            "you may still accept it or install the model from Setup. " + reason;
    }

    internal void RefreshPreview() => SelectClip();

    void ChangeNvidia()
    {
        if (loading || clip.SelectedIndex < 0 ||
            clips[clip.SelectedIndex].Media?.Mode != CustomClipMedia.NvidiaAigsMode)
            return;
        CustomShowClip selected = clips[clip.SelectedIndex];
        selected.Nvidia = new CustomNvidiaSettings
        {
            Mode = Math.Max(0, nvidiaMode.SelectedIndex),
            Temporal = nvidiaTemporal.Checked,
            InferenceResolution = nvidiaResolution.SelectedIndex switch
                { 0 => "540p", 2 => "1080p", 3 => "source", _ => "720p" }
        };
        PreviewChanged?.Invoke(selected, selected.AlphaThreshold);
    }

    void ChangeRvmOnnx()
    {
        if (loading || clip.SelectedIndex < 0 ||
            clips[clip.SelectedIndex].Media?.Mode != CustomClipMedia.RvmOnnxMode)
            return;
        CustomShowClip selected = clips[clip.SelectedIndex];
        selected.RvmOnnx = new CustomRvmOnnxSettings
        {
            Model = rvmOnnxModel.SelectedIndex == 1
                ? RvmOnnxSupport.ResNet50 : RvmOnnxSupport.MobileNetV3,
            Quality = RvmOnnxSupport.QualityFromIndex(
                rvmOnnxQuality.SelectedIndex),
            Temporal = rvmOnnxTemporal.Checked
        };
        RvmOnnxSettingsChanged?.Invoke(selected);
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
            new CustomShowClip().AlphaThreshold ==
                CustomShowClip.DefaultAlphaThreshold &&
            new CustomShowClip().EdgeChokePixels == 1;
    }
}

internal sealed class CustomPerformerForm : Form
{
    readonly TextBox name = new();
    readonly ComboBox gender = new() { DropDownStyle = ComboBoxStyle.DropDownList };
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
    readonly IReadOnlyList<CustomPerformerProfile> existingProfiles;

    internal CustomPerformerForm(CustomPerformerProfile? profile,
        IEnumerable<CustomPerformerProfile>? existingProfiles = null)
    {
        Profile = profile == null ? new() : Clone(profile);
        this.existingProfiles = existingProfiles?.ToArray() ?? [];
        Text = profile == null ? "New Model Profile" : "Edit Model Profile";
        ClientSize = new Size(500, 510);
        TableLayoutPanel table = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(table);
        Add(table, "Model name", name);
        gender.Items.Add("(Not set)");
        gender.Items.AddRange(CustomShowStore.GenderValues);
        Add(table, "Gender", gender);
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
        name.Text = Profile.ModelName;
        gender.SelectedItem = string.IsNullOrWhiteSpace(Profile.Gender)
            ? "(Not set)" : Profile.Gender;
        birth.Checked = Profile.BirthDate != null;
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
            Profile.Gender = gender.SelectedIndex <= 0
                ? null : gender.SelectedItem?.ToString();
            Profile.BirthDate = birth.Checked ? DateOnly.FromDateTime(birth.Value) : null;
            Profile.HeightCm = useHeight.Checked ? height.Value : null;
            Profile.BustCm = useBust.Checked ? bust.Value : null;
            Profile.WaistCm = useWaist.Checked ? waist.Value : null;
            Profile.HipsCm = useHips.Checked ? hips.Value : null;
            Profile.Hair = hair.Text.Trim(); Profile.Ethnicity = ethnicity.Text.Trim();
            Profile.City = city.Text.Trim(); Profile.Country = country.Text.Trim();
            CustomShowStore.ValidateProfile(Profile);
            if (existingProfiles.Any(existing =>
                    !string.Equals(existing.Id, Profile.Id,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.ModelName.Trim(), Profile.ModelName,
                        StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    $"A custom model named '{Profile.ModelName}' already exists.");
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
        { SchemaVersion=p.SchemaVersion, Id=p.Id, IstripperModelId=p.IstripperModelId,
          ModelName=p.ModelName, Gender=p.Gender, BirthDate=p.BirthDate,
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
        using CustomPerformerForm form = new(profile, store.LoadPerformers());
        if (form.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            store.SavePerformer(form.Profile); Reload(); DialogResult = DialogResult.OK;
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class CustomShowSettingsForm : Form
{
    readonly TextBox root = new(), python = new();
    readonly NumericUpDown sam2CacheSize = new()
    {
        Minimum = 0, Maximum = 100, DecimalPlaces = 0, Width = 90
    };
    readonly NumericUpDown defaultAlphaThreshold = new()
    {
        Minimum = 0, Maximum = 255, DecimalPlaces = 0, Width = 90
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
    readonly ComboBox nvencPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 90 };
    readonly ComboBox cpuPriority = PriorityInput();
    readonly ComboBox gpuPriority = PriorityInput();
    readonly NumericUpDown matAnyoneCutoff = CutoffInput();
    readonly NumericUpDown sam2BaseCutoff = CutoffInput();
    readonly NumericUpDown sam2SmallCutoff = CutoffInput();
    readonly NumericUpDown sam2TinyCutoff = CutoffInput();
    readonly NumericUpDown vitMatteSmallCutoff = CutoffInput();
    readonly NumericUpDown vitMatteBaseCutoff = CutoffInput();
    readonly NumericUpDown vitMatteSmallBatch = BatchInput();
    readonly NumericUpDown vitMatteBaseBatch = BatchInput();
    readonly Label validation = new() { AutoSize = true };
    readonly Label transNetStatus = StatusLabel();
    readonly Label rvmStatus = StatusLabel();
    readonly Label matAnyoneStatus = StatusLabel();
    readonly Label sam2Status = StatusLabel();
    readonly Label vitMatteStatus = StatusLabel();
    CancellationTokenSource? validationCancellation;
    internal CustomShowConfiguration Configuration { get; }
    internal CustomShowSettingsForm(CustomShowConfiguration current,
        Action<IWin32Window?>? restoreIncomplete = null,
        Action<IWin32Window?>? manageModels = null,
        Func<IWin32Window?, string?>? installTools = null,
        Action<IWin32Window?>? cleanupFailedShows = null)
    {
        Configuration = new()
        {
            LibraryRoot = current.LibraryRoot,
            PythonExecutable = current.PythonExecutable,
            Sam2MattingPythonExecutable = current.Sam2MattingPythonExecutable,
            NvidiaVfxSdkRoot = current.NvidiaVfxSdkRoot,
            SmallPlayerVolume = current.SmallPlayerVolume,
            LargePlayerVolume = current.LargePlayerVolume,
            DefaultAlphaThreshold = current.DefaultAlphaThreshold,
            FullOpacityThreshold = current.FullOpacityThreshold,
            Sam2FrameCacheSizeGb = current.Sam2FrameCacheSizeGb,
            TransNetPreferredBatchSize = current.TransNetPreferredBatchSize,
            TransNetCompileCutoffFrames = current.TransNetCompileCutoffFrames,
            TransNetDecodeMode = current.TransNetDecodeMode,
            LastClipDetector = current.LastClipDetector,
            FastClipDetectionSensitivity = current.FastClipDetectionSensitivity,
            TransNetClipDetectionSensitivity = current.TransNetClipDetectionSensitivity,
            OmniShotCutClipDetectionSensitivity =
                current.OmniShotCutClipDetectionSensitivity,
            RvmQualityPreferredChunk = current.RvmQualityPreferredChunk,
            RvmFastPreferredChunk = current.RvmFastPreferredChunk,
            RvmQualityCompileCutoffFrames = current.RvmQualityCompileCutoffFrames,
            RvmFastCompileCutoffFrames = current.RvmFastCompileCutoffFrames,
            RvmNvencPreset = current.RvmNvencPreset,
            ProcessingCpuPriority = current.ProcessingCpuPriority,
            ProcessingGpuPriority = current.ProcessingGpuPriority,
            MatAnyone2CompileCutoffFrames = current.MatAnyone2CompileCutoffFrames,
            Sam2BasePlusCompileCutoffFrames = current.Sam2BasePlusCompileCutoffFrames,
            Sam2SmallCompileCutoffFrames = current.Sam2SmallCompileCutoffFrames,
            Sam2TinyCompileCutoffFrames = current.Sam2TinyCompileCutoffFrames,
            VitMatteSmallCompileCutoffFrames = current.VitMatteSmallCompileCutoffFrames,
            VitMatteBaseCompileCutoffFrames = current.VitMatteBaseCompileCutoffFrames,
            VitMatteSmallPreferredBatchSize = current.VitMatteSmallPreferredBatchSize,
            VitMatteBasePreferredBatchSize = current.VitMatteBasePreferredBatchSize,
            LastProcessingAlgorithm = current.LastProcessingAlgorithm,
            LastRvmOnnxModel = current.LastRvmOnnxModel,
            LastRvmOnnxQuality = current.LastRvmOnnxQuality,
            LastRvmOnnxTemporal = current.LastRvmOnnxTemporal,
            LastSam2MattingTracker = current.LastSam2MattingTracker,
            LastMaskEngine = current.LastMaskEngine,
            LastSam2Model = current.LastSam2Model,
            LastMattingDetailPx = current.LastMattingDetailPx,
            LastVitMatteInferenceDetailPx = current.LastVitMatteInferenceDetailPx,
            LastProcessingBatchSize = current.LastProcessingBatchSize,
            LastRvmInitializerAlphaThresholdPercent =
                current.LastRvmInitializerAlphaThresholdPercent,
            LastRvmMatAnyoneMaskRefresh = current.LastRvmMatAnyoneMaskRefresh,
            LastPropSegmenterEveryFrame =
                current.LastPropSegmenterEveryFrame,
            LastPropSegmenterModelId = current.LastPropSegmenterModelId,
            LastDebugPropContribution = current.LastDebugPropContribution,
            LastRvmMatAnyoneRefreshStrengthPercent =
                current.LastRvmMatAnyoneRefreshStrengthPercent,
            MatAnyoneMemoryDefaultsVersion = current.MatAnyoneMemoryDefaultsVersion,
            LastMatAnyoneMaxMemoryFrames = current.LastMatAnyoneMaxMemoryFrames,
            LastMatAnyoneUseLongTermMemory =
                current.LastMatAnyoneUseLongTermMemory,
            LastTemporalAlphaCleanup = current.LastTemporalAlphaCleanup,
            LastTemporalAlphaCleanupWindowFrames =
                current.LastTemporalAlphaCleanupWindowFrames,
            LastTemporalAlphaCleanupStrengthPercent =
                current.LastTemporalAlphaCleanupStrengthPercent,
            LastTemporalAlphaTrackingStrengthPercent =
                current.LastTemporalAlphaTrackingStrengthPercent,
            LastTemporalAlphaCleanupAlphaThreshold =
                current.LastTemporalAlphaCleanupAlphaThreshold,
            LastAutoAcceptAlphaThreshold = current.LastAutoAcceptAlphaThreshold,
            PropSegmenterEnabled = current.PropSegmenterEnabled,
            ActivePropSegmenterModelId = current.ActivePropSegmenterModelId
        };
        Text = "Custom Show Settings"; ClientSize = new Size(900, 700);
        MinimumSize = new Size(780, 620);
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        layout.Controls.Add(tabs, 0, 0);

        TabPage generalTab = new("General");
        TableLayoutPanel general = SettingsTable();
        generalTab.Controls.Add(general); tabs.TabPages.Add(generalTab);
        AddExplanation(general, "Paths and shared cache",
            "These settings are shared by all custom-show processing tools.");
        AddPath(general, "Library folder", root, true);
        AddPath(general, "Python executable", python, false);
        AddChoice(general, "Default alpha threshold", defaultAlphaThreshold);
        AddExplanation(general, "Default playback transparency",
            "Applied to newly created and automatically processed clips. " +
            "Existing clips keep their saved per-clip threshold.");
        AddExplanation(general, "Processing priority",
            "Controls custom-show processing workers only. Normal preserves current behaviour. " +
            "Lower priorities favour playback and desktop responsiveness when resources are busy.");
        AddChoice(general, "CPU priority", cpuPriority);
        AddChoice(general, "GPU priority", gpuPriority);
        int cacheRow = general.RowCount++;
        general.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        general.Controls.Add(new Label { Text = "SAM2 frame cache (GB)", AutoSize = true,
            Anchor = AnchorStyles.Left }, 0, cacheRow);
        FlowLayoutPanel cacheControls = new() { AutoSize = true, WrapContents = false };
        Button clearCache = new() { Text = "Clear frame cache", AutoSize = true };
        cacheControls.Controls.AddRange([sam2CacheSize, clearCache, sam2CacheUsage]);
        general.Controls.Add(cacheControls, 1, cacheRow); general.SetColumnSpan(cacheControls, 2);
        AddExplanation(general, "How compilation is selected",
            "A cutoff is only a minimum workload. TransNetV2, MatAnyone2, SAM2, and ViTMatte " +
            "also require a successful benchmark policy for the current hardware and model. " +
            "RVM is the exception: a nonzero cutoff enables its lazy compiled path directly.");
        nvencPreset.Items.AddRange(["p1", "p2", "p3", "p4", "p5", "p6", "p7"]);
        AddChoice(general, "Output NVENC preset", nvencPreset);
        AddExplanation(general, "Output encoding",
            "The NVENC preset applies to RVM, MatAnyone2, and ViTMatte output. " +
            "It is not used by scene detection.");

        TabPage transNetTab = new("TransNetV2");
        TableLayoutPanel transNetTable = SettingsTable();
        transNetTab.Controls.Add(transNetTable); tabs.TabPages.Add(transNetTab);
        AddExplanation(transNetTable, "TransNetV2 scene detection",
            "Only “Benchmark TransNetV2” calibrates these settings. Compilation remains eager " +
            "unless its hardware-specific benchmark policy approves the selected batch.");
        AddStatus(transNetTable, transNetStatus);
        AddChoice(transNetTable, "Preferred window batch", transNetBatch);
        AddCutoff(transNetTable, "Compile minimum", transNetCutoff);
        transNetDecode.Items.AddRange(["Auto (codec/resolution-aware, exact-compatible)",
            "Legacy CUDA-first", "CPU fallback (manual)"]);
        AddChoice(transNetTable, "Decode mode", transNetDecode);
        Button benchmarkTransNet = new() { Text = "Benchmark TransNetV2 only...", AutoSize = true };
        Label transNetHelp = new() { AutoSize = true,
            Text = "Tests batches, compilation, CUDA graphs, decoding, and exact cut compatibility." };
        FlowLayoutPanel transNetActions = new() { AutoSize = true, WrapContents = true };
        transNetActions.Controls.AddRange([benchmarkTransNet, transNetHelp]);
        AddWideControl(transNetTable, transNetActions);

        TabPage rvmTab = new("RVM");
        TableLayoutPanel rvmTable = SettingsTable();
        rvmTab.Controls.Add(rvmTable); tabs.TabPages.Add(rvmTab);
        AddExplanation(rvmTable, "Robust Video Matting",
            "Only “Benchmark RVM” calibrates these settings. Unlike the other engines, RVM " +
            "does not require a policy file: zero disables compilation; a nonzero cutoff allows it.");
        AddStatus(rvmTable, rvmStatus);
        AddChoice(rvmTable, "Quality preferred chunk", rvmQualityChunk);
        AddChoice(rvmTable, "Fast preferred chunk", rvmFastChunk);
        AddChoice(rvmTable, "Quality compile minimum", rvmQualityCutoff);
        AddChoice(rvmTable, "Fast compile minimum", rvmFastCutoff);
        Button benchmarkRvm = new() { Text = "Benchmark RVM only...", AutoSize = true };
        Label rvmHelp = new() { AutoSize = true,
            Text = "Manual; tests Quality/Fast chunks, bounded pipeline, previews, and optional compilation." };
        FlowLayoutPanel rvmActions = new() { AutoSize = true, WrapContents = true };
        rvmActions.Controls.AddRange([benchmarkRvm, rvmHelp]);
        AddWideControl(rvmTable, rvmActions);

        TabPage modelsTab = new("Matting && masks");
        TableLayoutPanel models = SettingsTable();
        modelsTab.Controls.Add(models); tabs.TabPages.Add(modelsTab);
        AddExplanation(models, "MatAnyone2, SAM2, and ViTMatte",
            "The model-cutoff benchmark tests MatAnyone2, SAM2, and ViTMatte. At or above a displayed " +
            "minimum, compilation is still used only when the status below says it is available.");
        GroupBox matAnyoneGroup = SettingsGroup("MatAnyone2", out TableLayoutPanel matAnyoneTable);
        AddStatus(matAnyoneTable, matAnyoneStatus);
        AddCutoff(matAnyoneTable, "Compile minimum", matAnyoneCutoff);
        AddWideControl(models, matAnyoneGroup);
        GroupBox sam2Group = SettingsGroup("SAM2 mask tracking", out TableLayoutPanel sam2Table);
        AddStatus(sam2Table, sam2Status);
        AddCutoff(sam2Table, "Base+ compile minimum", sam2BaseCutoff);
        AddCutoff(sam2Table, "Small compile minimum", sam2SmallCutoff);
        AddCutoff(sam2Table, "Tiny compile minimum", sam2TinyCutoff);
        AddWideControl(models, sam2Group);
        GroupBox vitMatteGroup = SettingsGroup("ViTMatte", out TableLayoutPanel vitMatteTable);
        AddStatus(vitMatteTable, vitMatteStatus);
        AddCutoff(vitMatteTable, "S compile minimum", vitMatteSmallCutoff);
        AddCutoff(vitMatteTable, "B compile minimum", vitMatteBaseCutoff);
        AddBatch(vitMatteTable, "S preferred batch", vitMatteSmallBatch);
        AddBatch(vitMatteTable, "B preferred batch", vitMatteBaseBatch);
        AddWideControl(models, vitMatteGroup);
        Button benchmark = new() { Text = "Benchmark MatAnyone2 + SAM2 + ViTMatte...",
            AutoSize = true };
        Label cutoffHelp = new() { AutoSize = true,
            Text = "Creates/updates the policies shown above; this can take a long time." };
        FlowLayoutPanel cutoffActions = new() { AutoSize = true, WrapContents = true };
        cutoffActions.Controls.AddRange([benchmark, cutoffHelp]);
        AddWideControl(models, cutoffActions);
        AddWideControl(models, new Panel { Height = 28, Margin = Padding.Empty });

        TabPage maintenanceTab = new("Maintenance");
        TableLayoutPanel maintenance = SettingsTable();
        maintenanceTab.Controls.Add(maintenance); tabs.TabPages.Add(maintenanceTab);
        AddExplanation(maintenance, "Custom-show maintenance",
            "Restore unfinished work, clean up failed processing files, maintain model " +
            "profiles, install processing tools, or open the custom-show library folder.");
        Button restore = new() { Text = "Restore Incomplete Setup...", AutoSize = true,
            Enabled = restoreIncomplete != null };
        Button cleanupFailed = new() { Text = "Clean Up Failed Shows...", AutoSize = true,
            Enabled = cleanupFailedShows != null };
        Button modelsButton = new() { Text = "Manage Models...", AutoSize = true,
            Enabled = manageModels != null };
        Button setup = new() { Text = "Install / Update Processing Tools...",
            AutoSize = true, Enabled = installTools != null };
        Button openFolder = new() { Text = "Open Folder", AutoSize = true };
        FlowLayoutPanel maintenanceActions = new() { AutoSize = true,
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(4, 8, 4, 8) };
        maintenanceActions.Controls.AddRange(
            [restore, cleanupFailed, modelsButton, setup, openFolder]);
        AddWideControl(maintenance, maintenanceActions);

        Button validate = new() { Text = "Validate setup", AutoSize = true };
        Button refresh = new() { Text = "Refresh compilation status", AutoSize = true };
        Button ok = new() { Text = "OK", AutoSize = true };
        Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        FlowLayoutPanel buttons = new() { AutoSize = true, Dock = DockStyle.Fill,
            Padding = new Padding(12, 6, 12, 10), WrapContents = true };
        buttons.Controls.AddRange([validate, refresh, ok, cancel, validation]);
        layout.Controls.Add(buttons, 0, 1);
        root.Text = Configuration.LibraryRoot; python.Text = Configuration.PythonExecutable;
        defaultAlphaThreshold.Value = Math.Clamp(
            Configuration.DefaultAlphaThreshold, 0, 255);
        sam2CacheSize.Value = Math.Clamp(Configuration.Sam2FrameCacheSizeGb, 0, 100);
        transNetBatch.Value = Math.Clamp(Configuration.TransNetPreferredBatchSize, 1, 64);
        transNetCutoff.Value = ClampCutoff(Configuration.TransNetCompileCutoffFrames);
        transNetDecode.SelectedIndex = DecodeModeIndex(Configuration.TransNetDecodeMode);
        rvmQualityChunk.SelectedItem = ClosestRvmChunk(Configuration.RvmQualityPreferredChunk);
        rvmFastChunk.SelectedItem = ClosestRvmChunk(Configuration.RvmFastPreferredChunk);
        rvmQualityCutoff.Value = ClampCompileCutoff(Configuration.RvmQualityCompileCutoffFrames);
        rvmFastCutoff.Value = ClampCompileCutoff(Configuration.RvmFastCompileCutoffFrames);
        nvencPreset.SelectedItem = CustomShowProcessor.ValidNvencPreset(
            Configuration.RvmNvencPreset);
        cpuPriority.SelectedIndex = PriorityIndex(Configuration.ProcessingCpuPriority);
        gpuPriority.SelectedIndex = PriorityIndex(Configuration.ProcessingGpuPriority);
        matAnyoneCutoff.Value = ClampCutoff(Configuration.MatAnyone2CompileCutoffFrames);
        sam2BaseCutoff.Value = ClampCutoff(Configuration.Sam2BasePlusCompileCutoffFrames);
        sam2SmallCutoff.Value = ClampCutoff(Configuration.Sam2SmallCompileCutoffFrames);
        sam2TinyCutoff.Value = ClampCutoff(Configuration.Sam2TinyCompileCutoffFrames);
        vitMatteSmallCutoff.Value = ClampCutoff(Configuration.VitMatteSmallCompileCutoffFrames);
        vitMatteBaseCutoff.Value = ClampCutoff(Configuration.VitMatteBaseCompileCutoffFrames);
        vitMatteSmallBatch.Value = ClampBatch(Configuration.VitMatteSmallPreferredBatchSize);
        vitMatteBaseBatch.Value = ClampBatch(Configuration.VitMatteBasePreferredBatchSize);
        sam2CacheUsage.Text = "Calculating usage...";
        Shown += async (_, _) => { await UpdateCacheUsageAsync(); RefreshCompilationStatus(); };
        clearCache.Click += async (_, _) => await ClearSam2CacheAsync(clearCache);
        benchmarkTransNet.Click += (_, _) => BenchmarkTransNet();
        benchmarkRvm.Click += (_, _) => BenchmarkRvm();
        benchmark.Click += (_, _) => BenchmarkCutoffs();
        restore.Click += (_, _) => restoreIncomplete?.Invoke(this);
        cleanupFailed.Click += (_, _) => cleanupFailedShows?.Invoke(this);
        modelsButton.Click += (_, _) => manageModels?.Invoke(this);
        setup.Click += (_, _) =>
        {
            string? installedPython = installTools?.Invoke(this);
            CustomShowConfiguration installed = CustomShowConfiguration.Load();
            Configuration.NvidiaVfxSdkRoot = installed.NvidiaVfxSdkRoot;
            Configuration.Sam2MattingPythonExecutable =
                installed.Sam2MattingPythonExecutable;
            if (!string.IsNullOrWhiteSpace(installedPython))
            {
                python.Text = installedPython;
                Configuration.PythonExecutable = installedPython;
                RefreshCompilationStatus();
            }
        };
        openFolder.Click += (_, _) =>
        {
            string folder = string.IsNullOrWhiteSpace(root.Text)
                ? Configuration.LibraryRoot : root.Text;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        };
        validate.Click += async (_, _) => await ValidateSetup(validate);
        refresh.Click += (_, _) => RefreshCompilationStatus();
        tabs.SelectedIndexChanged += (_, _) => RefreshCompilationStatus();
        python.TextChanged += (_, _) => RefreshCompilationStatus();
        transNetBatch.ValueChanged += (_, _) => RefreshCompilationStatus();
        rvmQualityCutoff.ValueChanged += (_, _) => RefreshCompilationStatus();
        rvmFastCutoff.ValueChanged += (_, _) => RefreshCompilationStatus();
        matAnyoneCutoff.ValueChanged += (_, _) => RefreshCompilationStatus();
        sam2BaseCutoff.ValueChanged += (_, _) => RefreshCompilationStatus();
        sam2SmallCutoff.ValueChanged += (_, _) => RefreshCompilationStatus();
        sam2TinyCutoff.ValueChanged += (_, _) => RefreshCompilationStatus();
        vitMatteSmallCutoff.ValueChanged += (_, _) => RefreshCompilationStatus();
        vitMatteBaseCutoff.ValueChanged += (_, _) => RefreshCompilationStatus();
        ok.Click += SaveSettings;
        FormClosed += (_, _) => validationCancellation?.Cancel();
        AcceptButton = ok; CancelButton = cancel; AppTheme.Apply(this);
    }

    static Label StatusLabel() => new()
    {
        AutoSize = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle,
        Padding = new Padding(8), Margin = new Padding(3, 4, 3, 8),
        MaximumSize = new Size(620, 0),
        Text = "Checking compilation status..."
    };

    static TableLayoutPanel SettingsTable() => new()
    {
        Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12),
        AutoScroll = true,
        ColumnStyles =
        {
            new ColumnStyle(SizeType.Absolute, 205),
            new ColumnStyle(SizeType.Percent, 100),
            new ColumnStyle(SizeType.AutoSize)
        }
    };

    static GroupBox SettingsGroup(string text, out TableLayoutPanel table)
    {
        GroupBox group = new() { Text = text, AutoSize = true, Dock = DockStyle.Top,
            Padding = new Padding(8), Margin = new Padding(0, 4, 0, 8) };
        table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 3, Padding = new Padding(4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        group.Controls.Add(table);
        return group;
    }

    static void AddExplanation(TableLayoutPanel table, string heading, string text)
    {
        FlowLayoutPanel panel = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
        panel.Controls.Add(new Label { Text = heading, AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) });
        panel.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(760, 0) });
        AddWideControl(table, panel);
    }

    static void AddStatus(TableLayoutPanel table, Label status)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = "Compilation status", AutoSize = true,
            Anchor = AnchorStyles.Left }, 0, row);
        table.Controls.Add(status, 1, row);
        table.SetColumnSpan(status, 2);
    }

    static void AddWideControl(TableLayoutPanel table, Control control)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top;
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 3);
    }

    void RefreshCompilationStatus()
    {
        string? runtime = RuntimeFromPython();
        if (runtime == null)
        {
            const string unavailable = "Unknown — select a valid processing-environment Python executable.";
            transNetStatus.Text = rvmStatus.Text = matAnyoneStatus.Text =
                sam2Status.Text = vitMatteStatus.Text = unavailable;
            return;
        }

        IReadOnlyList<(string Key, JsonElement Value)> transNet = ReadPolicyEntries(
            Path.Combine(runtime, "transnetv2-performance-policy-v1.json"));
        int selectedBatch = (int)transNetBatch.Value;
        (string Key, JsonElement Value)[] matchingTransNet = transNet.Where(item =>
            item.Key.EndsWith($"|{selectedBatch}|FP16", StringComparison.OrdinalIgnoreCase) ||
            item.Key.EndsWith($"|{selectedBatch}|FP32", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (transNet.Count == 0)
            transNetStatus.Text = "Not benchmarked — automatic execution uses eager mode. Run “Benchmark TransNetV2 only”.";
        else if (matchingTransNet.Any(item => Boolean(item.Value, "compiledEnabled")))
            transNetStatus.Text = $"Available for benchmarked batch {selectedBatch}. Auto may compile at/above " +
                $"{transNetCutoff.Value:N0} frames when the measured break-even is also met.";
        else if (matchingTransNet.Length > 0)
            transNetStatus.Text = $"Benchmarked for batch {selectedBatch}, but compilation was not approved. Auto uses eager mode.";
        else
            transNetStatus.Text = $"No benchmark result for batch {selectedBatch}. Auto uses eager mode for this setting.";

        int rvmMarkers = SafeFiles(Path.Combine(runtime, "torchinductor-rvm"),
            "ready-*.json").Length;
        rvmStatus.Text = $"Quality: {RvmStatus((int)rvmQualityCutoff.Value)}  " +
            $"Fast: {RvmStatus((int)rvmFastCutoff.Value)}  " +
            $"Cached compiled shapes: {rvmMarkers}. RVM does not require a policy file.";

        string matPolicy = Path.Combine(runtime, "matanyone2-compile-policy.json");
        JsonElement? matAnyone = ReadJson(matPolicy);
        if (matAnyone == null)
            matAnyoneStatus.Text = "Not benchmarked — automatic execution uses eager mode.";
        else if (Boolean(matAnyone.Value, "enabled"))
            matAnyoneStatus.Text = $"Available. Auto may compile at/above {matAnyoneCutoff.Value:N0} frames.";
        else
            matAnyoneStatus.Text = "Benchmarked, but partial compilation was not approved. Auto uses eager mode.";

        IReadOnlyList<(string Key, JsonElement Value)> sam2 = ReadPolicyEntries(
            Path.Combine(runtime, "sam2-performance-policy-v1.json"));
        string samCache = Path.Combine(runtime, "torchinductor-cache");
        string[] samMarkers = SafeFiles(samCache, "SAM2_VOS*_READY*")
            .Select(Path.GetFileName).OfType<string>().ToArray();
        sam2Status.Text = sam2.Count == 0
            ? "Not benchmarked — all editable SAM2 models use eager mode. " +
                (samMarkers.Length > 0 ? "Cached kernels exist, but auto will not select them without an approved policy. " : "") +
                "The cutoff values alone do not enable compilation."
            : string.Join("  ", new[] { ("Base+", "base-plus", sam2BaseCutoff.Value),
                ("Small", "small", sam2SmallCutoff.Value), ("Tiny", "tiny", sam2TinyCutoff.Value) }
                .Select(model => Sam2ModelStatus(sam2, model.Item1, model.Item2, model.Item3,
                    samMarkers.Any(marker => marker.Contains(
                        model.Item2.Replace("-", "_"), StringComparison.OrdinalIgnoreCase)))));

        IReadOnlyList<(string Key, JsonElement Value)> vitMatte = ReadPolicyEntries(
            Path.Combine(runtime, "vitmatte-performance-policy-v1.json"));
        vitMatteStatus.Text = vitMatte.Count == 0
            ? "Not benchmarked — ViTMatte uses eager mode."
            : string.Join("  ", new[] { ("S", "|s|", vitMatteSmallCutoff.Value),
                ("B", "|b|", vitMatteBaseCutoff.Value) }.Select(model =>
                    ViTMatteModelStatus(vitMatte, model.Item1, model.Item2, model.Item3)));
    }

    string? RuntimeFromPython()
    {
        try
        {
            if (!File.Exists(python.Text)) return null;
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(python.Text)!, "..", ".."));
        }
        catch { return null; }
    }

    static string RvmStatus(int cutoff) => cutoff <= 0
        ? "compilation disabled (cutoff 0)."
        : $"eligible above approximately {Math.Ceiling(cutoff * 1.2):N0} frames; first use compiles.";

    static string Sam2ModelStatus(IReadOnlyList<(string Key, JsonElement Value)> entries,
        string label, string model, decimal cutoff, bool cached)
    {
        (string Key, JsonElement Value)[] matching = entries.Where(item =>
            item.Key.EndsWith("|" + model, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool enabled = matching.Any(item => NestedBoolean(item.Value, "encoder", "enabled") ||
            NestedBoolean(item.Value, "vos", "enabled"));
        return matching.Length == 0 ? $"{label}: no current policy; eager."
            : enabled ? $"{label}: compiled available from {cutoff:N0} frames when break-even is met; " +
                (cached ? "cache ready." : "first qualifying use will compile.")
            : $"{label}: benchmarked, compiled disabled; eager.";
    }

    static string ViTMatteModelStatus(
        IReadOnlyList<(string Key, JsonElement Value)> entries, string label,
        string keyPart, decimal cutoff)
    {
        (string Key, JsonElement Value)[] matching = entries.Where(item =>
            item.Key.Contains(keyPart, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length == 0) return $"{label}: no policy; eager.";
        return matching.Any(item => Boolean(item.Value, "compiledEnabled"))
            ? $"{label}: compiled available from {cutoff:N0} frames for approved shapes/batches."
            : $"{label}: calibrated, compiled not approved; eager.";
    }

    static JsonElement? ReadJson(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch { return null; }
    }

    static string[] SafeFiles(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern,
                SearchOption.TopDirectoryOnly) : [];
        }
        catch { return []; }
    }

    static IReadOnlyList<(string Key, JsonElement Value)> ReadPolicyEntries(string path)
    {
        JsonElement? root = ReadJson(path);
        if (root == null || !root.Value.TryGetProperty("entries", out JsonElement entries) ||
            entries.ValueKind != JsonValueKind.Object) return [];
        return entries.EnumerateObject().Select(item => (item.Name, item.Value.Clone())).ToArray();
    }

    static bool Boolean(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement item) &&
        item.ValueKind is JsonValueKind.True;

    static bool NestedBoolean(JsonElement value, string group, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(group, out JsonElement nested) &&
        Boolean(nested, property);

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
            Configuration.DefaultAlphaThreshold =
                (int)defaultAlphaThreshold.Value;
            Configuration.Sam2FrameCacheSizeGb = (int)sam2CacheSize.Value;
            Configuration.TransNetPreferredBatchSize = (int)transNetBatch.Value;
            Configuration.TransNetCompileCutoffFrames = (int)transNetCutoff.Value;
            Configuration.TransNetDecodeMode = DecodeModeValue(transNetDecode.SelectedIndex);
            Configuration.RvmQualityPreferredChunk = RvmChunkValue(rvmQualityChunk);
            Configuration.RvmFastPreferredChunk = RvmChunkValue(rvmFastChunk);
            Configuration.RvmQualityCompileCutoffFrames = (int)rvmQualityCutoff.Value;
            Configuration.RvmFastCompileCutoffFrames = (int)rvmFastCutoff.Value;
            Configuration.RvmNvencPreset = nvencPreset.SelectedItem?.ToString() ?? "p5";
            Configuration.ProcessingCpuPriority = PriorityValue(cpuPriority.SelectedIndex);
            Configuration.ProcessingGpuPriority = PriorityValue(gpuPriority.SelectedIndex);
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
        RefreshCompilationStatus();
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
        RefreshCompilationStatus();
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
            RvmNvencPreset = nvencPreset.SelectedItem?.ToString() ?? "p5"
        };
        using CustomShowRvmBenchmarkForm form = new(benchmarkConfiguration);
        if (form.ShowDialog(this) != DialogResult.OK || form.Result == null) return;
        rvmQualityChunk.SelectedItem = ClosestRvmChunk(form.Result.RvmQualityPreferredChunk);
        rvmFastChunk.SelectedItem = ClosestRvmChunk(form.Result.RvmFastPreferredChunk);
        rvmQualityCutoff.Value = ClampCompileCutoff(
            form.Result.RvmQualityCompileCutoffFrames);
        rvmFastCutoff.Value = ClampCompileCutoff(form.Result.RvmFastCompileCutoffFrames);
        RefreshCompilationStatus();
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

    static ComboBox PriorityInput()
    {
        ComboBox input = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        input.Items.AddRange(["Idle", "Below Normal", "Normal", "Above Normal", "High"]);
        return input;
    }

    static int PriorityIndex(string? value) => value switch
    {
        "idle" => 0,
        "below-normal" => 1,
        "above-normal" => 3,
        "high" => 4,
        _ => 2
    };

    static string PriorityValue(int index) => index switch
    {
        0 => "idle",
        1 => "below-normal",
        3 => "above-normal",
        4 => "high",
        _ => "normal"
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
    const string ProgressPrefix = "IQP_BENCHMARK_PROGRESS ";
    readonly CustomShowConfiguration configuration;
    readonly Label currentStep = new() { AutoSize = true, Font = new Font(
        SystemFonts.MessageBoxFont, FontStyle.Bold), Text = "Preparing benchmark..." };
    readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 22,
        Minimum = 0, Maximum = 1000 };
    readonly Label progressDetail = new() { AutoSize = true, Text = "Waiting for progress..." };
    readonly Label timing = new() { AutoSize = true, Text = "Elapsed 00:00" };
    readonly TextBox output = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false
    };

    readonly Button close = new() { Dock = DockStyle.Bottom, Height = 36, Text = "Cancel" };
    readonly Stopwatch elapsed = new();
    readonly System.Windows.Forms.Timer clock = new() { Interval = 1000 };
    Process? process;
    bool complete;
    double completedFraction;
    internal CustomShowConfiguration? Result { get; private set; }

    internal CustomShowCutoffBenchmarkForm(CustomShowConfiguration configuration)
    {
        this.configuration = configuration;
        Text = "Benchmark Custom Show Model Cutoffs";
        ClientSize = new Size(920, 620);
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, RowCount = 4,
            Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        FlowLayoutPanel summary = new() { AutoSize = true, Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown, WrapContents = false };
        summary.Controls.Add(currentStep);
        summary.Controls.Add(progressDetail);
        summary.Controls.Add(timing);
        layout.Controls.Add(summary, 0, 0);
        layout.Controls.Add(progress, 0, 1);
        layout.Controls.Add(new Label { Text = "Detailed output", AutoSize = true,
            Margin = new Padding(0, 8, 0, 2) }, 0, 2);
        layout.Controls.Add(output, 0, 3);
        Controls.Add(layout); Controls.Add(close);
        close.Click += (_, _) => CloseBenchmark();
        FormClosing += (_, _) => { if (!complete) try { process?.Kill(true); } catch { } };
        Shown += async (_, _) => await RunAsync();
        clock.Tick += (_, _) => UpdateTiming();
        AppTheme.Apply(this);
    }

    async Task RunAsync()
    {
        string resultPath = Path.Combine(Path.GetTempPath(),
            "iqp-model-cutoffs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            elapsed.Start(); clock.Start();
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
            completedFraction = 1; progress.Value = progress.Maximum;
            currentStep.Text = "Benchmark complete";
            progressDetail.Text = "All requested stages completed successfully.";
            Append("Benchmark completed. The measured values will be copied into Settings.");
        }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                currentStep.Text = "Benchmark failed";
                progressDetail.Text = error.Message;
                Append("Benchmark failed: " + error.Message);
            }
        }
        finally
        {
            elapsed.Stop(); clock.Stop(); UpdateTiming();
            complete = true;
            if (!IsDisposed) close.Text = Result == null ? "Close" : "Use results";
            try { File.Delete(resultPath); } catch { }
        }
    }

    async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is string line) HandleOutput(line);
    }

    void HandleOutput(string line)
    {
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
        {
            Append(line);
            return;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(line[ProgressPrefix.Length..]);
            JsonElement value = document.RootElement;
            int phase = value.GetProperty("phase").GetInt32();
            int phases = value.GetProperty("phases").GetInt32();
            int completed = value.GetProperty("completed").GetInt32();
            int total = Math.Max(1, value.GetProperty("total").GetInt32());
            string message = value.GetProperty("message").GetString() ?? "Working...";
            double inner = Math.Clamp(completed / (double)total, 0, 1);
            double overall = value.TryGetProperty("overallCompleted", out JsonElement overallDone) &&
                value.TryGetProperty("overallTotal", out JsonElement overallTotal) &&
                overallTotal.GetDouble() > 0
                ? Math.Clamp(overallDone.GetDouble() / overallTotal.GetDouble(), 0, 1)
                : Math.Clamp(((phase - 1) + inner) / Math.Max(1, phases), 0, 1);
            if (InvokeRequired)
            {
                BeginInvoke(() => UpdateProgress(phase, phases, completed, total,
                    overall, message));
                return;
            }
            UpdateProgress(phase, phases, completed, total, overall, message);
        }
        catch
        {
            Append(line);
        }
    }

    void UpdateProgress(int phase, int phases, int completed, int total,
        double overall, string message)
    {
        completedFraction = overall;
        progress.Value = Math.Clamp((int)Math.Round(overall * progress.Maximum),
            progress.Minimum, progress.Maximum);
        currentStep.Text = message;
        progressDetail.Text = $"Stage {phase} of {phases} • step {completed:N0} of {total:N0} " +
            $"• {overall * 100:0}% overall";
        UpdateTiming();
    }

    void UpdateTiming()
    {
        if (IsDisposed) return;
        TimeSpan used = elapsed.Elapsed;
        string estimate = completedFraction >= .02 && completedFraction < 1
            ? $" • estimated remaining {FormatDuration(TimeSpan.FromSeconds(
                used.TotalSeconds * (1 - completedFraction) / completedFraction))} (rough)"
            : completedFraction >= 1 ? "" : " • estimating remaining time...";
        timing.Text = $"Elapsed {FormatDuration(used)}{estimate}";
    }

    static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes:00}:{value.Seconds:00}";

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

internal sealed class SetupProcessOutput
{
    internal const string ProgressPrefix = "##quickplayer-progress##";

    readonly TextBox output;
    int? progressStart;

    internal SetupProcessOutput(TextBox output) => this.output = output;

    internal async Task PumpAsync(StreamReader reader)
    {
        char[] buffer = new char[1024];
        StringBuilder pending = new();
        bool pendingCarriageReturn = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            for (int index = 0; index < read; index++)
            {
                char character = buffer[index];
                if (pendingCarriageReturn)
                {
                    if (character == '\n')
                    {
                        HandleLine(pending.ToString());
                        pending.Clear();
                        pendingCarriageReturn = false;
                        continue;
                    }

                    SetProgress(pending.ToString());
                    pending.Clear();
                    pendingCarriageReturn = false;
                }

                if (character == '\r')
                    pendingCarriageReturn = true;
                else if (character == '\n')
                {
                    HandleLine(pending.ToString());
                    pending.Clear();
                }
                else
                    pending.Append(character);
            }
        }

        if (pendingCarriageReturn)
            SetProgress(pending.ToString());
        else if (pending.Length > 0)
            HandleLine(pending.ToString());
    }

    void HandleLine(string line)
    {
        if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            SetProgress(line[ProgressPrefix.Length..]);
        else
            AppendLine(line);
    }

    internal void AppendLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        OnUi(() =>
        {
            FinishProgressOnUi();
            output.AppendText(text + Environment.NewLine);
            ScrollToEnd();
        });
    }

    void SetProgress(string text) => OnUi(() =>
    {
        progressStart ??= output.TextLength;
        output.Select(progressStart.Value, output.TextLength - progressStart.Value);
        output.SelectedText = text;
        ScrollToEnd();
    });

    void FinishProgress(string? finalText = null) => OnUi(() =>
    {
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            progressStart ??= output.TextLength;
            output.Select(progressStart.Value, output.TextLength - progressStart.Value);
            output.SelectedText = finalText;
        }
        FinishProgressOnUi();
        ScrollToEnd();
    });

    void FinishProgressOnUi()
    {
        if (!progressStart.HasValue) return;
        output.AppendText(Environment.NewLine);
        progressStart = null;
    }

    void ScrollToEnd()
    {
        output.SelectionStart = output.TextLength;
        output.SelectionLength = 0;
        output.ScrollToCaret();
    }

    void OnUi(Action action)
    {
        if (output.IsDisposed) return;
        if (output.InvokeRequired)
        {
            try { output.Invoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            return;
        }
        action();
    }
}

internal sealed class CustomShowSam2MattingSetupForm : Form
{
    readonly string script;
    readonly TextBox output = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false
    };
    readonly Button close = new() { Dock = DockStyle.Bottom, Height = 36,
        Text = "Cancel" };
    readonly SetupProcessOutput outputWriter;
    Process? process;
    bool complete;

    internal CustomShowSam2MattingSetupForm(string script)
    {
        this.script = script;
        outputWriter = new(output);
        Text = "Install SAM2Matting Trackers";
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
        Append("Installing the pinned SAM2Matting environment and all three checkpoints...");
        try
        {
            ProcessStartInfo start = new("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            foreach (string argument in new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script
            }) start.ArgumentList.Add(argument);
            process = Process.Start(start) ??
                throw new InvalidOperationException("PowerShell could not be started.");
            Task standardOutput = outputWriter.PumpAsync(process.StandardOutput);
            Task standardError = outputWriter.PumpAsync(process.StandardError);
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Setup failed with exit code {process.ExitCode}.");
            Append("SAM2Matting setup completed successfully.");
            DialogResult = DialogResult.OK;
        }
        catch (Exception error)
        {
            Append("Setup failed: " + error.Message);
        }
        finally
        {
            complete = true;
            close.Text = "Close";
        }
    }

    void Append(string? text) => outputWriter.AppendLine(text);

    void CloseSetup()
    {
        if (!complete)
        {
            try { process?.Kill(true); } catch { }
            DialogResult = DialogResult.Cancel;
        }
        Close();
    }
}

internal sealed class CustomShowSetupOptionsForm : Form
{
    readonly CheckBox offline = new() { Text =
        "Offline Python/RVM processing environment", AutoSize = true,
        Checked = true };
    readonly CheckBox nvidiaAigs = new() { Text =
        "NVIDIA AI Green Screen SDK (real-time playback, NGC account required)",
        AutoSize = true };
    readonly CheckBox rvmOnnx = new() { Text =
        "RVM ONNX MobileNetV3 + ResNet50 models (~117 MB, DirectML)", AutoSize = true };
    readonly CheckBox sam2Matting = new() { Text =
        "SAM2Matting with SAM2.1-T and SAM2.1-B+ (~600 MB, NVIDIA CUDA required)",
        AutoSize = true, Checked = true };
    readonly CheckBox transNet = new() { Text = "TransNetV2 automatic clip detection",
        AutoSize = true, Checked = true };
    readonly CheckBox omniShotCut = new() { Text =
        "OmniShotCut modern transition detection (~170 MB, NVIDIA CUDA required)",
        AutoSize = true };
    readonly CheckBox matAnyone = new() { Text =
        "MatAnyone 2 + SAM2 Base+/Small/Tiny interactive masking (~900 MB)", AutoSize = true,
        Checked = true };
    readonly CheckBox vitMatte = new() { Text =
        "ViTMatte S + B with editable SAM2 video masks (~900 MB)", AutoSize = true };
    readonly CheckBox edgeTam = new() { Text =
        "EdgeTAM fast editable video masks (~60 MB, NVIDIA CUDA recommended)",
        AutoSize = true };
    readonly CheckBox stabilo = new() { Text =
        "Stabilo + SAM2 camera/background-lock stabilization (~800 MB if SAM2 is not installed)",
        AutoSize = true };
    readonly CheckBox proPainter = new() { Text =
        "ProPainter video object removal (~200 MB, NVIDIA CUDA recommended)",
        AutoSize = true };
    internal bool InstallTransNetV2 => transNet.Checked;
    internal bool InstallOmniShotCut => omniShotCut.Checked;
    internal bool InstallMatAnyone2 => matAnyone.Checked;
    internal bool InstallViTMatte => vitMatte.Checked;
    internal bool InstallEdgeTam => edgeTam.Checked;
    internal bool InstallStabilo => stabilo.Checked;
    internal bool InstallProPainter => proPainter.Checked;
    internal bool InstallSam2Matting => sam2Matting.Checked;
    internal bool InstallOfflineProcessing => offline.Checked;
    internal bool InstallNvidiaAigs => nvidiaAigs.Checked;
    internal bool InstallRvmOnnx => rvmOnnx.Checked;

    internal CustomShowSetupOptionsForm()
    {
        Text = "Choose Custom Show Processing Tools";
        ClientSize = new Size(860, 650);
        MinimumSize = new Size(760, 560);
        AutoScroll = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        StartPosition = FormStartPosition.CenterParent;
        TableLayoutPanel layout = new() { Dock = DockStyle.Top, AutoSize = true,
            Padding = new Padding(16), ColumnCount = 1, RowCount = 16 };
        layout.Controls.Add(new Label { Text =
            "Select the processing environments and optional tools to install:", AutoSize = true });
        layout.Controls.Add(offline);
        layout.Controls.Add(nvidiaAigs);
        layout.Controls.Add(rvmOnnx);
        layout.Controls.Add(transNet);
        layout.Controls.Add(omniShotCut);
        layout.Controls.Add(matAnyone);
        layout.Controls.Add(sam2Matting);
        layout.Controls.Add(vitMatte);
        layout.Controls.Add(edgeTam);
        layout.Controls.Add(stabilo);
        layout.Controls.Add(proPainter);
        layout.Controls.Add(new Label { Text =
            "SAM2Matting, MatAnyone 2, and ProPainter are non-commercial; " +
            "ViTMatte, SAM2, EdgeTAM, and Stabilo permit commercial use.",
            AutoSize = true, MaximumSize = new Size(810, 0) });
        LinkLabel omniLicence = new() { Text = "Read the OmniShotCut MIT licence", AutoSize = true };
        omniLicence.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/UVA-Computer-Vision-Lab/OmniShotCut/blob/23ad6fb41b296fb9258b0e7825125a914573b906/LICENSE") { UseShellExecute = true });
        layout.Controls.Add(omniLicence);
        LinkLabel matAnyoneLicence = new() { Text = "Read the MatAnyone 2 licence", AutoSize = true };
        matAnyoneLicence.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/pq-yang/MatAnyone2/blob/main/LICENSE.txt") { UseShellExecute = true });
        layout.Controls.Add(matAnyoneLicence);
        LinkLabel sam2MattingLicenceLink = new()
        {
            Text = "Read the Fudan SAM2Matting licence and non-commercial notice",
            AutoSize = true
        };
        sam2MattingLicenceLink.LinkClicked += (_, _) => Process.Start(
            new ProcessStartInfo(
                "https://github.com/FudanCVL/SAM2Matting/blob/73dd721d77b56749248aefe5e8824d7f61b9d13c/LICENSE")
            { UseShellExecute = true });
        layout.Controls.Add(sam2MattingLicenceLink);
        LinkLabel vitMatteLicence = new() { Text = "Read the ViTMatte licence", AutoSize = true };
        vitMatteLicence.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/hustvl/ViTMatte/blob/main/LICENSE") { UseShellExecute = true });
        layout.Controls.Add(vitMatteLicence);
        LinkLabel edgeTamLicence = new() { Text = "Read the EdgeTAM Apache 2.0 licence", AutoSize = true };
        edgeTamLicence.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/facebookresearch/EdgeTAM/blob/7711e012a30a2402c4eaab637bdb00a521302c91/LICENSE") { UseShellExecute = true });
        layout.Controls.Add(edgeTamLicence);
        LinkLabel stabiloLicence = new() { Text = "Read the Stabilo MIT licence", AutoSize = true };
        stabiloLicence.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/rfonod/stabilo/blob/52ebd524d26fb940b868dc9d7eeb3e2602f895a3/LICENSE") { UseShellExecute = true });
        layout.Controls.Add(stabiloLicence);
        LinkLabel proPainterLicence = new() { Text = "Read the ProPainter licence", AutoSize = true };
        proPainterLicence.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/sczhou/ProPainter/blob/main/LICENSE") { UseShellExecute = true });
        layout.Controls.Add(proPainterLicence);
        FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        Button install = new() { Text = "Install / Update", AutoSize = true };
        install.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(install);
        buttons.Controls.Add(new Button { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = (Button)buttons.Controls[0];
        CancelButton = (Button)buttons.Controls[1];
        AppTheme.Apply(this);
        omniLicence.LinkColor = matAnyoneLicence.LinkColor =
            sam2MattingLicenceLink.LinkColor = vitMatteLicence.LinkColor = edgeTamLicence.LinkColor = stabiloLicence.LinkColor =
            proPainterLicence.LinkColor =
            Properties.Settings.Default.DarkMode ? Color.LightSkyBlue : Color.Blue;
    }

    internal static bool VerifyDefaults()
    {
        using CustomShowSetupOptionsForm form = new();
        return form.InstallOfflineProcessing && !form.InstallNvidiaAigs &&
            !form.InstallRvmOnnx &&
            form.InstallTransNetV2 && !form.InstallOmniShotCut && form.InstallMatAnyone2 &&
            form.InstallSam2Matting &&
            !form.InstallViTMatte && !form.InstallEdgeTam &&
            !form.InstallStabilo && !form.InstallProPainter;
    }
}

internal sealed class CustomShowSetupForm : Form
{
    readonly string script;
    readonly bool installTransNetV2;
    readonly bool installOmniShotCut;
    readonly bool installMatAnyone2;
    readonly bool installViTMatte;
    readonly bool installEdgeTam;
    readonly bool installStabilo;
    readonly bool installProPainter;
    readonly TextBox output = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false
    };
    readonly Button close = new() { Dock = DockStyle.Bottom, Height = 36, Text = "Cancel" };
    readonly SetupProcessOutput outputWriter;
    Process? process;
    bool complete;
    internal bool Succeeded { get; private set; }

    internal CustomShowSetupForm(string script, bool installTransNetV2,
        bool installOmniShotCut,
        bool installMatAnyone2, bool installViTMatte,
        bool installProPainter, bool installEdgeTam, bool installStabilo)
    {
        this.script = script;
        outputWriter = new(output);
        this.installTransNetV2 = installTransNetV2;
        this.installOmniShotCut = installOmniShotCut;
        this.installMatAnyone2 = installMatAnyone2;
        this.installViTMatte = installViTMatte;
        this.installProPainter = installProPainter;
        this.installEdgeTam = installEdgeTam;
        this.installStabilo = installStabilo;
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
            if (installOmniShotCut)
                start.ArgumentList.Add("-InstallOmniShotCut");
            if (installMatAnyone2)
                start.ArgumentList.Add("-InstallMatAnyone2");
            if (installViTMatte)
                start.ArgumentList.Add("-InstallViTMatte");
            if (installEdgeTam)
                start.ArgumentList.Add("-InstallEdgeTam");
            if (installStabilo)
                start.ArgumentList.Add("-InstallStabilo");
            if (installProPainter)
                start.ArgumentList.Add("-InstallProPainter");
            process = Process.Start(start) ??
                throw new InvalidOperationException("PowerShell could not be started.");
            Task standardOutput = outputWriter.PumpAsync(process.StandardOutput);
            Task standardError = outputWriter.PumpAsync(process.StandardError);
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
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

    void Append(string? text) => outputWriter.AppendLine(text);

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
