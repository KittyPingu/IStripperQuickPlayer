namespace IStripperQuickPlayer;

internal enum CustomShowWizardArea
{
    Source,
    Title,
    Profile,
    Metadata,
    Clips,
    Processing,
    FinalAction
}

internal enum CustomShowWizardPlayback
{
    Preprocessed,
    Realtime
}

internal enum CustomShowWizardInteraction
{
    Automatic,
    HandsOn
}

internal enum CustomShowWizardPriority
{
    Fast,
    Balanced,
    ExactSelection,
    SoftEdges,
    SceneChanges
}

internal enum CustomShowWizardHardware
{
    UnknownOrCpu,
    ModestGpu,
    StrongGpu
}

internal enum CustomShowWizardRunMode
{
    Interactive,
    Queue
}

internal sealed record CustomShowWizardGoals(
    CustomShowWizardPlayback Playback,
    CustomShowWizardInteraction Interaction,
    CustomShowWizardPriority Priority,
    CustomShowWizardHardware Hardware,
    CustomShowWizardRunMode RunMode);

internal sealed record CustomShowWizardState(
    bool SourceValid,
    string SourceName,
    bool TitleValid,
    string Title,
    bool ProfileValid,
    string ProfileName,
    bool ClipsConfirmed,
    int IncludedClips,
    int SkippedClips,
    string SelectedAlgorithm,
    string SelectedSettings,
    string FinalAction,
    IReadOnlySet<string> AvailableAlgorithms);

internal sealed record CustomShowWizardRecommendation(
    string IdealAlgorithm,
    string AppliedAlgorithm,
    bool IdealAvailable,
    string Reason,
    string Limitation,
    int Sam2TrackerIndex,
    int MattingDetailIndex,
    int VitMatteDetailIndex,
    bool AutoAccept,
    bool UseAutoBatch);

internal interface ICustomShowWizardHost
{
    event EventHandler? WizardStateChanged;
    CustomShowWizardState GetWizardState();
    void NavigateWizardTo(CustomShowWizardArea area);
    void OpenWizardSourcePicker();
    void OpenWizardClipEditor();
    void ApplyWizardRecommendation(CustomShowWizardRecommendation recommendation,
        bool useFallback);
    void InstallWizardTools(string algorithm);
    void FocusWizardFinalAction();
}

internal static class CustomShowWizardRecommendations
{
    internal const string Quality = "quality";
    internal const string Fast = "fast";
    internal const string RvmMatAnyone = "rvm-matanyone2";
    internal const string RvmVitMatteSmall = "rvm-vitmatte-s";
    internal const string MatAnyone = "matanyone2";
    internal const string VitMatteSmall = "vitmatte-s";

    internal static CustomShowWizardRecommendation Recommend(
        CustomShowWizardGoals goals, IReadOnlySet<string> available)
    {
        string ideal = IdealAlgorithm(goals);
        string applied = available.Contains(ideal) ? ideal :
            Fallbacks(ideal).FirstOrDefault(available.Contains) ?? Fast;
        bool modest = goals.Hardware != CustomShowWizardHardware.StrongGpu;
        int tracker = goals.Interaction == CustomShowWizardInteraction.Automatic
            ? modest ? 2 : 3
            : modest ? 0 : 1;
        bool autoAccept = goals.RunMode == CustomShowWizardRunMode.Queue &&
            applied is Quality or Fast or RvmMatAnyone or RvmVitMatteSmall or
                MatAnyone;
        return new CustomShowWizardRecommendation(ideal, applied,
            ideal == applied, Reason(ideal), Limitation(ideal, applied), tracker,
            2, 2, autoAccept, true);
    }

    static string IdealAlgorithm(CustomShowWizardGoals goals)
    {
        if (goals.Playback == CustomShowWizardPlayback.Realtime)
            return RvmOnnxSupport.Algorithm;
        if (goals.Priority == CustomShowWizardPriority.SceneChanges)
            return Sam2MattingSupport.Algorithm;
        if (goals.Priority == CustomShowWizardPriority.ExactSelection)
            return MatAnyone;
        if (goals.Priority == CustomShowWizardPriority.SoftEdges)
            return goals.Interaction == CustomShowWizardInteraction.Automatic
                ? RvmVitMatteSmall : VitMatteSmall;
        if (goals.Interaction == CustomShowWizardInteraction.HandsOn)
            return MatAnyone;
        return goals.Priority == CustomShowWizardPriority.Fast ||
            goals.Hardware == CustomShowWizardHardware.UnknownOrCpu
                ? Fast : goals.Hardware == CustomShowWizardHardware.StrongGpu &&
                    goals.Priority != CustomShowWizardPriority.Balanced
                ? RvmVitMatteSmall : RvmMatAnyone;
    }

    static IEnumerable<string> Fallbacks(string algorithm) => algorithm switch
    {
        RvmVitMatteSmall or VitMatteSmall =>
            [RvmMatAnyone, Quality, Fast],
        MatAnyone => [RvmMatAnyone, Quality, Fast],
        Sam2MattingSupport.Algorithm => [RvmMatAnyone, Quality, Fast],
        RvmOnnxSupport.Algorithm => [Fast, Quality],
        RvmMatAnyone => [Quality, Fast],
        _ => [Fast, Quality]
    };

    internal static string DisplayName(string algorithm) => algorithm switch
    {
        Quality => "RVM Quality",
        Fast => "RVM Fast",
        RvmMatAnyone => "RVM-MatAnyone",
        RvmVitMatteSmall => "RVM-ViTMatte S",
        MatAnyone => "MatAnyone 2",
        VitMatteSmall => "ViTMatte S",
        Sam2MattingSupport.Algorithm => "SAM2Matting",
        RvmOnnxSupport.Algorithm => "RVM ONNX",
        _ => algorithm
    };

    static string Reason(string algorithm) => algorithm switch
    {
        RvmOnnxSupport.Algorithm =>
            "Creates compact RGB clips and generates transparency during playback. " +
            "Balanced analysis with temporal memory is the recommended starting point.",
        Sam2MattingSupport.Algorithm =>
            "Treats scene changes independently, so each scene can receive a fresh " +
            "automatic or editable subject mask.",
        MatAnyone =>
            "Lets you choose the exact person or people before high-quality temporal " +
            "matting begins.",
        VitMatteSmall =>
            "Lets you correct tracked masks before ViTMatte refines hair and other " +
            "soft edges.",
        RvmVitMatteSmall =>
            "Automatically creates a mask for every frame, then spends more time " +
            "refining fine and soft edges.",
        RvmMatAnyone =>
            "A strong general-purpose automatic workflow: RVM finds the person and " +
            "MatAnyone produces the final matte.",
        Fast =>
            "Uses the lightest built-in model for drafts, slower hardware, and the " +
            "shortest processing time.",
        _ =>
            "Uses the dependable built-in ResNet50 model for a quick automatic result."
    };

    static string Limitation(string ideal, string applied)
    {
        if (ideal == applied) return ideal switch
        {
            RvmOnnxSupport.Algorithm =>
                "Real-time quality depends on playback hardware and is person-focused.",
            RvmVitMatteSmall or VitMatteSmall =>
                "This is substantially slower and uses more GPU memory than RVM.",
            MatAnyone => "You will create or correct an initial mask for each clip.",
            Sam2MattingSupport.Algorithm =>
                "Scene-aware processing has its own setup and may request several masks.",
            _ => "Review the preview before accepting the finished clips."
        };
        string loss = ideal switch
        {
            RvmOnnxSupport.Algorithm =>
                "The fallback is preprocessed; it cannot provide real-time matting.",
            Sam2MattingSupport.Algorithm =>
                "The fallback does not provide independent per-scene prompts.",
            MatAnyone =>
                "The fallback cannot guarantee the same exact manual subject selection.",
            RvmVitMatteSmall or VitMatteSmall =>
                "The fallback does not provide the same ViTMatte soft-edge refinement.",
            _ => "The fallback uses a simpler matte."
        };
        return $"{DisplayName(ideal)} is not installed. {loss}";
    }

    internal static bool VerifyContracts()
    {
        HashSet<string> all = [Quality, Fast, RvmMatAnyone,
            RvmVitMatteSmall, MatAnyone, VitMatteSmall,
            Sam2MattingSupport.Algorithm, RvmOnnxSupport.Algorithm];
        CustomShowWizardRecommendation realtime = Recommend(new(
            CustomShowWizardPlayback.Realtime,
            CustomShowWizardInteraction.Automatic,
            CustomShowWizardPriority.Balanced,
            CustomShowWizardHardware.ModestGpu,
            CustomShowWizardRunMode.Interactive), all);
        CustomShowWizardRecommendation scenesAutomatic = Recommend(new(
            CustomShowWizardPlayback.Preprocessed,
            CustomShowWizardInteraction.Automatic,
            CustomShowWizardPriority.SceneChanges,
            CustomShowWizardHardware.ModestGpu,
            CustomShowWizardRunMode.Queue), all);
        CustomShowWizardRecommendation scenesManual = Recommend(new(
            CustomShowWizardPlayback.Preprocessed,
            CustomShowWizardInteraction.HandsOn,
            CustomShowWizardPriority.SceneChanges,
            CustomShowWizardHardware.StrongGpu,
            CustomShowWizardRunMode.Interactive), all);
        CustomShowWizardRecommendation exact = Recommend(new(
            CustomShowWizardPlayback.Preprocessed,
            CustomShowWizardInteraction.Automatic,
            CustomShowWizardPriority.ExactSelection,
            CustomShowWizardHardware.StrongGpu,
            CustomShowWizardRunMode.Queue), all);
        CustomShowWizardRecommendation softAutomatic = Recommend(new(
            CustomShowWizardPlayback.Preprocessed,
            CustomShowWizardInteraction.Automatic,
            CustomShowWizardPriority.SoftEdges,
            CustomShowWizardHardware.StrongGpu,
            CustomShowWizardRunMode.Queue), all);
        CustomShowWizardRecommendation softManual = Recommend(new(
            CustomShowWizardPlayback.Preprocessed,
            CustomShowWizardInteraction.HandsOn,
            CustomShowWizardPriority.SoftEdges,
            CustomShowWizardHardware.StrongGpu,
            CustomShowWizardRunMode.Interactive), all);
        CustomShowWizardRecommendation fast = Recommend(new(
            CustomShowWizardPlayback.Preprocessed,
            CustomShowWizardInteraction.Automatic,
            CustomShowWizardPriority.Fast,
            CustomShowWizardHardware.UnknownOrCpu,
            CustomShowWizardRunMode.Interactive), all);
        CustomShowWizardRecommendation balanced = Recommend(new(
            CustomShowWizardPlayback.Preprocessed,
            CustomShowWizardInteraction.Automatic,
            CustomShowWizardPriority.Balanced,
            CustomShowWizardHardware.ModestGpu,
            CustomShowWizardRunMode.Queue), all);
        HashSet<string> builtInOnly = [Quality, Fast];
        CustomShowWizardRecommendation fallback = Recommend(new(
            CustomShowWizardPlayback.Realtime,
            CustomShowWizardInteraction.Automatic,
            CustomShowWizardPriority.Balanced,
            CustomShowWizardHardware.ModestGpu,
            CustomShowWizardRunMode.Queue), builtInOnly);
        CustomShowWizardRecommendation exactFallback = Recommend(new(
            CustomShowWizardPlayback.Preprocessed,
            CustomShowWizardInteraction.HandsOn,
            CustomShowWizardPriority.ExactSelection,
            CustomShowWizardHardware.StrongGpu,
            CustomShowWizardRunMode.Interactive),
            new HashSet<string> { Quality, Fast, RvmMatAnyone });
        CustomShowWizardRecommendation edgeFallback = Recommend(new(
            CustomShowWizardPlayback.Preprocessed,
            CustomShowWizardInteraction.Automatic,
            CustomShowWizardPriority.SoftEdges,
            CustomShowWizardHardware.StrongGpu,
            CustomShowWizardRunMode.Interactive), builtInOnly);
        CustomShowWizardRecommendation sceneFallback = Recommend(new(
            CustomShowWizardPlayback.Preprocessed,
            CustomShowWizardInteraction.Automatic,
            CustomShowWizardPriority.SceneChanges,
            CustomShowWizardHardware.ModestGpu,
            CustomShowWizardRunMode.Interactive), builtInOnly);
        return realtime.IdealAlgorithm == RvmOnnxSupport.Algorithm &&
            scenesAutomatic.IdealAlgorithm == Sam2MattingSupport.Algorithm &&
            scenesAutomatic.Sam2TrackerIndex == 2 &&
            scenesManual.Sam2TrackerIndex == 1 &&
            exact.IdealAlgorithm == MatAnyone && exact.AutoAccept &&
            softAutomatic.IdealAlgorithm == RvmVitMatteSmall &&
            softManual.IdealAlgorithm == VitMatteSmall &&
            fast.IdealAlgorithm == Fast &&
            balanced.IdealAlgorithm == RvmMatAnyone && balanced.AutoAccept &&
            fallback.AppliedAlgorithm == Fast && !fallback.IdealAvailable &&
            fallback.AutoAccept &&
            exactFallback.AppliedAlgorithm == RvmMatAnyone &&
            !exactFallback.IdealAvailable &&
            edgeFallback.AppliedAlgorithm == Quality &&
            !edgeFallback.IdealAvailable &&
            sceneFallback.AppliedAlgorithm == Quality &&
            !sceneFallback.IdealAvailable;
    }
}

internal sealed class CustomShowWizardForm : Form
{
    const int PageCount = 6;
    readonly ICustomShowWizardHost host;
    readonly Form editor;
    readonly Label progress = new() { AutoSize = true };
    readonly Label heading = new()
    {
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold)
    };
    readonly FlowLayoutPanel page = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(14)
    };
    readonly Button back = new() { Text = "Back", AutoSize = true };
    readonly Button next = new() { Text = "Next", AutoSize = true };
    readonly Button close = new() { Text = "Close Wizard", AutoSize = true };
    readonly ComboBox playback = Choice(
        "Preprocess transparency for the best playback compatibility",
        "Generate transparency in real time during playback");
    readonly ComboBox interaction = Choice(
        "Fully automatic",
        "I am willing to choose or correct the subject mask");
    readonly ComboBox priority = Choice(
        "Fast draft / limited hardware",
        "Balanced general-purpose result",
        "Exact person or multiple-person selection",
        "Best hair and soft-edge quality",
        "Frequent scene changes need separate masks");
    readonly ComboBox hardware = Choice(
        "No CUDA GPU or I am not sure",
        "Modest CUDA GPU",
        "Strong CUDA GPU with generous VRAM");
    readonly ComboBox runMode = Choice(
        "Process interactively and review the preview",
        "Queue unattended work for later");
    readonly Dictionary<string, Label> liveStatuses =
        new(StringComparer.Ordinal);
    Button? liveClipButton;
    int step;

    internal CustomShowWizardForm(ICustomShowWizardHost host, Form editor)
    {
        this.host = host;
        this.editor = editor;
        Text = "Custom Show Wizard";
        ClientSize = new Size(440, 690);
        MinimumSize = new Size(390, 560);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        AccessibleName = "Custom Show creation wizard";
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        FlowLayoutPanel title = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(14, 12, 14, 4)
        };
        title.Controls.Add(progress);
        title.Controls.Add(heading);
        root.Controls.Add(title, 0, 0);
        root.Controls.Add(page, 0, 1);
        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10)
        };
        buttons.Controls.AddRange([close, next, back]);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        back.Click += (_, _) => SetStep(step - 1);
        next.Click += (_, _) => SetStep(step + 1);
        close.Click += (_, _) => Close();
        foreach (ComboBox choice in new[]
            { playback, interaction, priority, hardware, runMode })
            choice.SelectedIndexChanged += (_, _) => RefreshPage();
        host.WizardStateChanged += HostStateChanged;
        editor.LocationChanged += EditorBoundsChanged;
        editor.SizeChanged += EditorBoundsChanged;
        FormClosed += (_, _) =>
        {
            host.WizardStateChanged -= HostStateChanged;
            editor.LocationChanged -= EditorBoundsChanged;
            editor.SizeChanged -= EditorBoundsChanged;
        };
        Shown += (_, _) => PositionBesideEditor();
        SetStep(0);
        AppTheme.Apply(this);
    }

    static ComboBox Choice(params string[] values)
    {
        ComboBox result = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 385,
            AccessibleRole = AccessibleRole.ComboBox
        };
        result.Items.AddRange(values);
        result.SelectedIndex = 0;
        return result;
    }

    void HostStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (editor.ContainsFocus)
            UpdateLiveState(host.GetWizardState());
        else
            RefreshPage();
    }

    void UpdateLiveState(CustomShowWizardState state)
    {
        UpdateStatus("Source video", state.SourceValid,
            state.SourceValid ? state.SourceName : step == 0
                ? "Choose an existing video file." : "Missing");
        UpdateStatus("Show title", state.TitleValid,
            state.TitleValid ? state.Title : step == 0
                ? "Enter a title for the custom card." : "Missing");
        UpdateStatus("Model profile", state.ProfileValid,
            state.ProfileValid ? state.ProfileName : step == 0
                ? "Select an existing model or create a reusable profile." : "Missing");
        string clips = state.ClipsConfirmed
            ? step == 2
                ? $"Reviewed: {state.IncludedClips} included, " +
                    $"{state.SkippedClips} skipped."
                : $"{state.IncludedClips} included, {state.SkippedClips} skipped"
            : step == 2
                ? "Not reviewed yet. QuickPlayer can still offer automatic " +
                    "scene detection later."
                : "Not reviewed (recommended, but not required)";
        UpdateStatus(step == 2 ? "Video sections" : "Clip divisions",
            state.ClipsConfirmed, clips, incompleteIsWarning: true);
        if (liveClipButton != null)
            liveClipButton.Text = state.ClipsConfirmed
                ? "Review clip divisions…" : "Split into clips…";
    }

    void EditorBoundsChanged(object? sender, EventArgs e) => PositionBesideEditor();

    void PositionBesideEditor()
    {
        if (!editor.Visible || editor.WindowState == FormWindowState.Minimized)
            return;
        Rectangle work = Screen.FromControl(editor).WorkingArea;
        int x = editor.Right + 8;
        if (x + Width > work.Right) x = editor.Left - Width - 8;
        if (x < work.Left)
            x = Math.Clamp(editor.Right - Width, work.Left,
                Math.Max(work.Left, work.Right - Width));
        int y = Math.Clamp(editor.Top, work.Top,
            Math.Max(work.Top, work.Bottom - Height));
        Location = new Point(x, y);
    }

    void SetStep(int value)
    {
        step = Math.Clamp(value, 0, PageCount - 1);
        back.Enabled = step > 0;
        next.Enabled = step < PageCount - 1;
        next.Text = step == PageCount - 2 ? "Review" : "Next";
        NavigateEditor();
        RefreshPage();
    }

    void NavigateEditor()
    {
        CustomShowWizardArea? area = step switch
        {
            0 => CustomShowWizardArea.Source,
            1 => CustomShowWizardArea.Metadata,
            2 => CustomShowWizardArea.Clips,
            3 or 4 => CustomShowWizardArea.Processing,
            5 => null,
            _ => null
        };
        if (area != null) host.NavigateWizardTo(area.Value);
    }

    void RefreshPage()
    {
        if (IsDisposed) return;
        CustomShowWizardState state = host.GetWizardState();
        progress.Text = $"Step {step + 1} of {PageCount}";
        page.SuspendLayout();
        page.Controls.Clear();
        liveStatuses.Clear();
        liveClipButton = null;
        switch (step)
        {
            case 0: BuildSourcePage(state); break;
            case 1: BuildDetailsPage(); break;
            case 2: BuildClipsPage(state); break;
            case 3: BuildGoalsPage(); break;
            case 4: BuildRecommendationPage(state); break;
            default: BuildReviewPage(state); break;
        }
        page.ResumeLayout(true);
        AppTheme.Apply(this);
    }

    void BuildSourcePage(CustomShowWizardState state)
    {
        heading.Text = "Choose the source and identify the show";
        AddParagraph("QuickPlayer supports MP4, MOV, MKV, AVI, and WebM files. " +
            "The source remains where it is; QuickPlayer does not copy or delete it.");
        AddStatus(state.SourceValid, "Source video",
            state.SourceValid ? state.SourceName : "Choose an existing video file.");
        AddAction("Choose source video…", CustomShowWizardArea.Source);
        AddStatus(state.TitleValid, "Show title",
            state.TitleValid ? state.Title : "Enter a title for the custom card.");
        AddAction("Enter show title", CustomShowWizardArea.Title);
        AddStatus(state.ProfileValid, "Model profile",
            state.ProfileValid ? state.ProfileName :
                "Select an existing model or create a reusable profile.");
        AddAction("Choose or create model", CustomShowWizardArea.Profile);
        AddParagraph("You can continue at any time. The editor will still check " +
            "required information before processing starts.");
    }

    void BuildDetailsPage()
    {
        heading.Text = "Add optional show details";
        AddParagraph("Metadata improves searching and helps QuickPlayer classify " +
            "clips. Hotness and clip types become defaults that can be changed for " +
            "individual clips later.");
        AddParagraph("Model profiles are reusable. Editing a custom profile updates " +
            "every custom show linked to that model.");
        AddParagraph("You can also add dates, tags, a cover image, a photo folder, " +
            "and cover fonts. None of these are required to process the show.");
        AddAction("Open optional metadata", CustomShowWizardArea.Metadata);
    }

    void BuildClipsPage(CustomShowWizardState state)
    {
        heading.Text = "Split and classify the video";
        AddParagraph("Each included clip restarts the matting model. Put boundaries " +
            "at scene changes, skip titles or unwanted sections, and classify clips " +
            "so player filters work correctly.");
        string summary = state.ClipsConfirmed
            ? $"Reviewed: {state.IncludedClips} included, {state.SkippedClips} skipped."
            : "Not reviewed yet. QuickPlayer can still offer automatic scene detection later.";
        AddStatus(state.ClipsConfirmed, "Video sections", summary,
            incompleteIsWarning: true);
        liveClipButton = PageButton(state.ClipsConfirmed
            ? "Review clip divisions…" : "Split into clips…");
        liveClipButton.Click += (_, _) => host.OpenWizardClipEditor();
        page.Controls.Add(liveClipButton);
    }

    void BuildGoalsPage()
    {
        heading.Text = "Tell QuickPlayer what matters most";
        AddParagraph("These plain-language choices produce a starting recommendation. " +
            "You can still change every processing option in the editor.");
        AddQuestion("Playback", playback);
        AddQuestion("How hands-on?", interaction);
        AddQuestion("Main priority", priority);
        AddQuestion("Hardware", hardware);
        AddQuestion("When to run", runMode);
        if (HardwareGoal == CustomShowWizardHardware.UnknownOrCpu &&
            PlaybackGoal == CustomShowWizardPlayback.Preprocessed)
            AddParagraph("MatAnyone, ViTMatte, and scene tracking are normally too " +
                "slow without an NVIDIA CUDA GPU. The recommendation will favour RVM Fast.");
    }

    void BuildRecommendationPage(CustomShowWizardState state)
    {
        heading.Text = "Recommended processing method";
        CustomShowWizardRecommendation recommendation = Recommendation(state);
        AddHeading(CustomShowWizardRecommendations.DisplayName(
            recommendation.IdealAlgorithm));
        AddParagraph(recommendation.Reason);
        AddParagraph(recommendation.Limitation);
        if (recommendation.IdealAvailable)
        {
            Button apply = PageButton("Apply recommendation");
            apply.Click += (_, _) => host.ApplyWizardRecommendation(
                recommendation, useFallback: false);
            page.Controls.Add(apply);
        }
        else
        {
            Button install = PageButton("Install required tools…");
            install.Click += (_, _) => host.InstallWizardTools(
                recommendation.IdealAlgorithm);
            page.Controls.Add(install);
            Button fallback = PageButton("Use available fallback: " +
                CustomShowWizardRecommendations.DisplayName(
                    recommendation.AppliedAlgorithm));
            fallback.Click += (_, _) => host.ApplyWizardRecommendation(
                recommendation, useFallback: true);
            page.Controls.Add(fallback);
        }
        AddParagraph("Current editor choice: " +
            CustomShowWizardRecommendations.DisplayName(state.SelectedAlgorithm) +
            ". Manual changes are respected; use the button above only when you want " +
            "to reapply the wizard's settings.");
    }

    void BuildReviewPage(CustomShowWizardState state)
    {
        heading.Text = "Review and continue in the editor";
        AddStatus(state.SourceValid, "Source video",
            state.SourceValid ? state.SourceName : "Missing");
        AddStatus(state.TitleValid, "Show title",
            state.TitleValid ? state.Title : "Missing");
        AddStatus(state.ProfileValid, "Model profile",
            state.ProfileValid ? state.ProfileName : "Missing");
        AddStatus(state.ClipsConfirmed, "Clip divisions",
            state.ClipsConfirmed
                ? $"{state.IncludedClips} included, {state.SkippedClips} skipped"
                : "Not reviewed (recommended, but not required)",
            incompleteIsWarning: true);
        AddHeading("Processing");
        AddParagraph(CustomShowWizardRecommendations.DisplayName(
            state.SelectedAlgorithm) + " — " + state.SelectedSettings);
        AddParagraph("The editor's final action is “" + state.FinalAction +
            "”. Its existing validation and preview/review flow remain authoritative.");
        Button finish = PageButton("Go to “" + state.FinalAction + "”");
        finish.Click += (_, _) => host.FocusWizardFinalAction();
        page.Controls.Add(finish);
    }

    CustomShowWizardRecommendation Recommendation(CustomShowWizardState state) =>
        CustomShowWizardRecommendations.Recommend(new CustomShowWizardGoals(
            PlaybackGoal, InteractionGoal, PriorityGoal, HardwareGoal, RunModeGoal),
            state.AvailableAlgorithms);

    CustomShowWizardPlayback PlaybackGoal => playback.SelectedIndex == 1
        ? CustomShowWizardPlayback.Realtime : CustomShowWizardPlayback.Preprocessed;
    CustomShowWizardInteraction InteractionGoal => interaction.SelectedIndex == 1
        ? CustomShowWizardInteraction.HandsOn : CustomShowWizardInteraction.Automatic;
    CustomShowWizardPriority PriorityGoal => priority.SelectedIndex switch
    {
        0 => CustomShowWizardPriority.Fast,
        2 => CustomShowWizardPriority.ExactSelection,
        3 => CustomShowWizardPriority.SoftEdges,
        4 => CustomShowWizardPriority.SceneChanges,
        _ => CustomShowWizardPriority.Balanced
    };
    CustomShowWizardHardware HardwareGoal => hardware.SelectedIndex switch
    {
        1 => CustomShowWizardHardware.ModestGpu,
        2 => CustomShowWizardHardware.StrongGpu,
        _ => CustomShowWizardHardware.UnknownOrCpu
    };
    CustomShowWizardRunMode RunModeGoal => runMode.SelectedIndex == 1
        ? CustomShowWizardRunMode.Queue : CustomShowWizardRunMode.Interactive;

    void AddQuestion(string label, Control control)
    {
        AddHeading(label);
        page.Controls.Add(control);
    }

    void AddAction(string text, CustomShowWizardArea area)
    {
        Button button = PageButton(text);
        button.Click += (_, _) => BeginInvoke(() =>
        {
            if (IsDisposed) return;
            if (area == CustomShowWizardArea.Source)
                host.OpenWizardSourcePicker();
            else
                host.NavigateWizardTo(area);
        });
        page.Controls.Add(button);
    }

    Label AddStatus(bool complete, string label, string detail,
        bool incompleteIsWarning = false)
    {
        Label status = new()
        {
            AutoSize = true,
            MaximumSize = new Size(390, 0),
            Text = $"{(complete ? "✓" : incompleteIsWarning ? "!" : "○")} " +
                $"{label}: {detail}",
            ForeColor = complete ? Color.ForestGreen : incompleteIsWarning
                ? Color.DarkOrange : SystemColors.ControlText,
            AccessibleName = label,
            AccessibleDescription = detail,
            Margin = new Padding(3, 7, 3, 5)
        };
        liveStatuses[label] = status;
        page.Controls.Add(status);
        return status;
    }

    void UpdateStatus(string label, bool complete, string detail,
        bool incompleteIsWarning = false)
    {
        if (!liveStatuses.TryGetValue(label, out Label? status)) return;
        status.Text = $"{(complete ? "✓" : incompleteIsWarning ? "!" : "○")} " +
            $"{label}: {detail}";
        status.ForeColor = complete ? Color.ForestGreen : incompleteIsWarning
            ? Color.DarkOrange : Properties.Settings.Default.DarkMode
                ? Color.AntiqueWhite : SystemColors.ControlText;
        status.AccessibleDescription = detail;
    }

    void AddHeading(string text) => page.Controls.Add(new Label
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(390, 0),
        Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
        Margin = new Padding(3, 10, 3, 3)
    });

    void AddParagraph(string text) => page.Controls.Add(new Label
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(390, 0),
        Margin = new Padding(3, 5, 3, 8)
    });

    static Button PageButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(0, 34),
        AccessibleName = text,
        Margin = new Padding(3, 5, 3, 8)
    };
}
