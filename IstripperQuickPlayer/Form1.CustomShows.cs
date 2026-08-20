using System.Diagnostics;
using System.Text;
using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using Manina.Windows.Forms;
using Microsoft.Win32;

namespace IStripperQuickPlayer;

public partial class Form1
{
    readonly ToolStripMenuItem customShowsMenu = new("Custom Shows");
    readonly ToolStripMenuItem customPlayerVolumeMenu =
        new("Custom player volume");
    readonly ToolStripMenuItem customPlayerSmallVolumeMenu = new();
    readonly ToolStripMenuItem customPlayerLargeVolumeMenu = new();
    readonly ToolStripMenuItem iStripperPlayerVolumeMenu =
        new("iStripper player volume");
    readonly ToolStripMenuItem iStripperPlayerSmallVolumeMenu = new();
    readonly ToolStripMenuItem iStripperPlayerLargeVolumeMenu = new();
    readonly ToolStripMenuItem customPlayerFullOpacityMenu = new();
    readonly TrackBar customPlayerFullOpacitySlider = new();
    readonly Label customAlphaThresholdLabel = new()
        { Text = "Alpha threshold", AutoSize = true };
    readonly Controls.PlaybackSeekBar customAlphaThresholdInput = new()
    {
        Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 10,
        Width = 150, Height = 32, ShowTimeToolTip = true,
        AccessibleName = "Custom player alpha threshold"
    };
    readonly Label customEdgeChokeLabel = new()
        { Text = "Edge cleanup", AutoSize = true };
    readonly Controls.PlaybackSeekBar customEdgeChokeInput = new()
    {
        Minimum = 0, Maximum = 16, SmallChange = 1, LargeChange = 4,
        Value = 4, Width = 150, Height = 32, ShowTimeToolTip = true,
        AccessibleName = "Custom player edge cleanup"
    };
    readonly ToolStripMenuItem editCustomShowMenu = new("Edit Custom Show Metadata...");
    readonly ToolStripMenuItem reprocessCustomShowMenu = new("Reprocess Custom Show...");
    readonly ToolStripMenuItem deleteCustomShowMenu = new("Delete Custom Show");
    readonly ContextMenuStrip customClipContextMenu = new();
    readonly ToolStripMenuItem trimCustomClipMenu =
        new("Trim Custom Clip...");
    readonly ToolStripMenuItem deleteCustomClipMenu =
        new("Delete Custom Clip...");
    readonly ToolStripMenuItem customClipHotnessMenu = new("Hotness");
    readonly ToolStripMenuItem customClipTypesMenu = new("Clip types");
    readonly Controls.PlaybackSeekBar customClipAlphaSlider = new()
    {
        Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 10,
        Dock = DockStyle.Fill, Height = 32,
        AccessibleName = "Custom clip minimum alpha"
    };
    readonly TextBox customClipAlphaText = new()
    {
        Width = 46, MaxLength = 3,
        TextAlign = HorizontalAlignment.Right
    };
    readonly Controls.PlaybackSeekBar customClipEdgeChokeInput = new()
    {
        Minimum = 0, Maximum = 16, SmallChange = 1, LargeChange = 4,
        Value = 4, Dock = DockStyle.Fill, Height = 32,
        AccessibleName = "Custom clip edge cleanup"
    };
    readonly TextBox customClipEdgeChokeText = new()
    {
        Width = 46, MaxLength = 4,
        TextAlign = HorizontalAlignment.Right
    };
    readonly System.Windows.Forms.Timer customClipAlphaSaveTimer = new()
        { Interval = 250 };
    ToolStripControlHost? customClipAlphaHost;
    ModelCard? customClipAlphaCard;
    ModelClip? customClipAlphaClip;
    CustomShowConfiguration customShowConfiguration = CustomShowConfiguration.Load();
    CustomShowQueueManager? customShowQueueManager;
    CustomShowQueueForm? customShowQueueForm;
    CustomPlayerForm? customPlayer;
    ModelCard? customPlayerCard;
    ModelClip? customPlayerClip;
    bool loadingCustomAlphaThreshold;
    bool loadingCustomEdgeChoke;
    bool loadingCustomClipAlphaThreshold;
    bool loadingCustomClipEdgeChoke;
    bool customClipAlphaDirty;
    bool selectingClipForContextMenu;
    string customPlayerAnimationPath = "";
    string customPreloadedAnimationPath = "";
    Task<CustomPlayerForm.PreparedPlayback?>? customPreloadedPlayback;
    CancellationTokenSource? customPreloadCancellation;
    IntPtr customHiddenMovieWindow;
    Rectangle? customPlayerBounds;
    bool customResumeIstripper;
    bool customIstripperSuspended;
    string customPendingIstripperAnimation = "";
    static readonly string customPlayerPositionLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IStripperQuickPlayer", "custom-player-position.log");

    void SetupCustomShows()
    {
        customAlphaThresholdLabel.Visible = customAlphaThresholdInput.Visible = false;
        customAlphaThresholdInput.ToolTipFormatter = value => value.ToString();
        customAlphaThresholdInput.Scroll += (_, _) =>
            SetCurrentCustomAlphaThreshold(customAlphaThresholdInput.Value);
        customEdgeChokeLabel.Visible = customEdgeChokeInput.Visible = false;
        customEdgeChokeInput.ToolTipFormatter = value =>
            $"{EdgeCleanupText(EdgeCleanupPixels(value))} px";
        customEdgeChokeInput.Scroll += (_, _) =>
            SetCurrentCustomEdgeChoke(
                EdgeCleanupPixels(customEdgeChokeInput.Value));
        SetupCustomPlayerVolumeMenu();
        SetupIStripperPlayerVolumeMenu();
        SetupCustomPlayerFullOpacityMenu();
        ToolStripMenuItem create = new("Create Show...");
        ToolStripMenuItem queues = new("Queues...");
        ToolStripMenuItem check = new("Check Custom Shows...");
        ToolStripMenuItem stabilize = new("Stabilize Video (FFmpeg)...");
        ToolStripMenuItem backgroundLock = new(
            "Camera / Background Lock (Stabilo + SAM2)...");
        ToolStripMenuItem removeWatermark = new("Remove Video Object / Watermark...");
        ToolStripMenuItem settings = new("Settings...");
        customShowQueueManager = new CustomShowQueueManager(
            new CustomShowStore(customShowConfiguration.LibraryRoot),
            customShowConfiguration, QueueShowPublished,
            PrepareCustomShowPublicationAsync);
        create.Click += (_, _) => EditCustomShow(null);
        queues.Click += (_, _) => ShowCustomShowQueues();
        check.Click += async (_, _) => await CheckCustomShowsAsync(check);
        stabilize.Click += (_, _) => StabilizeVideo();
        backgroundLock.Click += (_, _) => StabilizeBackgroundVideo();
        removeWatermark.Click += (_, _) => RemoveVideoWatermark();
        settings.Click += (_, _) => ConfigureCustomShows();
        customShowsMenu.DropDownItems.AddRange(
            [create, queues, check, new ToolStripSeparator(), stabilize,
                backgroundLock, removeWatermark, settings]);
        fileToolStripMenuItem.DropDownItems.Insert(0, customShowsMenu);

        editCustomShowMenu.Click += (_, _) =>
            EditCustomShow(CurrentContextCustomShowId());
        reprocessCustomShowMenu.Click += (_, _) =>
            EditCustomShow(CurrentContextCustomShowId(), reprocess: true);
        deleteCustomShowMenu.Click += (_, _) => DeleteCurrentCustomShow();
        menuCardList.Items.Add(new ToolStripSeparator());
        menuCardList.Items.Add(editCustomShowMenu);
        menuCardList.Items.Add(reprocessCustomShowMenu);
        menuCardList.Items.Add(deleteCustomShowMenu);
        menuCardList.Opening += (_, _) =>
        {
            bool custom = CurrentContextCustomShowId() != null;
            editCustomShowMenu.Visible = custom;
            reprocessCustomShowMenu.Visible = custom;
            deleteCustomShowMenu.Visible = custom;
            deleteFromDiskToolStripMenuItem.Visible = !custom;
        };

        TableLayoutPanel alphaPanel = new()
        {
            AutoSize = false, Size = new System.Drawing.Size(310, 102),
            ColumnCount = 2, RowCount = 4, Padding = new Padding(6, 3, 6, 3)
        };
        alphaPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        alphaPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        Label alphaLabel = new()
        {
            Text = "Minimum alpha (0–255)", AutoSize = true,
            Margin = new Padding(3, 2, 3, 0)
        };
        alphaPanel.Controls.Add(alphaLabel, 0, 0);
        alphaPanel.SetColumnSpan(alphaLabel, 2);
        alphaPanel.Controls.Add(customClipAlphaSlider, 0, 1);
        alphaPanel.Controls.Add(customClipAlphaText, 1, 1);
        Label edgeChokeLabel = new()
        {
            Text = "Edge cleanup (0–4 px)", AutoSize = true,
            Margin = new Padding(3, 5, 3, 0)
        };
        alphaPanel.Controls.Add(edgeChokeLabel, 0, 2);
        alphaPanel.SetColumnSpan(edgeChokeLabel, 2);
        alphaPanel.Controls.Add(customClipEdgeChokeInput, 0, 3);
        alphaPanel.Controls.Add(customClipEdgeChokeText, 1, 3);
        customClipAlphaHost = new ToolStripControlHost(alphaPanel)
        {
            AutoSize = false, Size = alphaPanel.Size, Margin = Padding.Empty
        };
        customClipAlphaSlider.Scroll += (_, _) =>
            ChangeContextClipAlphaThreshold(customClipAlphaSlider.Value);
        customClipAlphaText.TextChanged += (_, _) =>
        {
            if (int.TryParse(customClipAlphaText.Text, out int value))
                ChangeContextClipAlphaThreshold(value);
        };
        customClipAlphaText.Leave += (_, _) =>
            customClipAlphaText.Text = customClipAlphaSlider.Value.ToString();
        customClipEdgeChokeInput.Scroll += (_, _) =>
            ChangeContextClipEdgeChoke(
                EdgeCleanupPixels(customClipEdgeChokeInput.Value));
        customClipEdgeChokeText.TextChanged += (_, _) =>
        {
            if (float.TryParse(customClipEdgeChokeText.Text, out float pixels))
                ChangeContextClipEdgeChoke(pixels);
        };
        customClipEdgeChokeText.Leave += (_, _) =>
            customClipEdgeChokeText.Text = EdgeCleanupText(
                EdgeCleanupPixels(customClipEdgeChokeInput.Value));
        customClipAlphaSaveTimer.Tick += (_, _) =>
        {
            customClipAlphaSaveTimer.Stop();
            PersistContextClipAlphaThreshold();
        };
        deleteCustomClipMenu.Click += async (_, _) =>
        {
            PersistContextClipAlphaThreshold();
            await DeleteSelectedCustomClipAsync();
        };
        trimCustomClipMenu.Click += async (_, _) =>
        {
            PersistContextClipAlphaThreshold();
            await TrimSelectedCustomClipAsync();
        };
        foreach (string hotness in CustomShowStore.HotnessOptions)
        {
            string label = hotness switch
            {
                "NoNudity" => "No Nudity",
                "FullNudity" => "Full Nudity",
                _ => hotness
            };
            ToolStripMenuItem item = new(label) { Tag = hotness };
            item.Click += (_, _) => ChangeContextClipHotness(hotness);
            customClipHotnessMenu.DropDownItems.Add(item);
        }
        foreach (string clipType in CustomShowStore.ClipTypeOptions)
        {
            ToolStripMenuItem item = new(clipType)
            {
                Tag = clipType,
                CheckOnClick = true
            };
            item.Click += (_, _) => ChangeContextClipType(item);
            customClipTypesMenu.DropDownItems.Add(item);
        }
        customClipContextMenu.Items.AddRange([
            customClipAlphaHost, new ToolStripSeparator(),
            customClipHotnessMenu, customClipTypesMenu,
            new ToolStripSeparator(),
            trimCustomClipMenu, deleteCustomClipMenu]);
        customClipContextMenu.Opening += (sender, eventArgs) =>
        {
            bool custom = TryGetSelectedCustomClip(
                out ModelCard card, out ModelClip clip);
            customClipAlphaCard = custom ? card : null;
            customClipAlphaClip = custom ? clip : null;
            loadingCustomClipAlphaThreshold = true;
            customClipAlphaSlider.Value = custom
                ? Math.Clamp(clip.customAlphaThreshold, 0, 255) : 0;
            customClipAlphaText.Text = custom
                ? customClipAlphaSlider.Value.ToString() : "";
            loadingCustomClipAlphaThreshold = false;
            loadingCustomClipEdgeChoke = true;
            customClipEdgeChokeInput.Value = custom
                ? EdgeCleanupStep(clip.customEdgeChokePixels) : 4;
            customClipEdgeChokeText.Text = custom
                ? EdgeCleanupText(clip.customEdgeChokePixels) : "";
            loadingCustomClipEdgeChoke = false;
            customClipAlphaDirty = false;
            RefreshContextClipClassificationChecks(custom ? clip : null);
            customClipAlphaHost.Visible = custom;
            customClipHotnessMenu.Visible = custom;
            customClipTypesMenu.Visible = custom;
            trimCustomClipMenu.Visible = custom;
            deleteCustomClipMenu.Visible = custom;
            eventArgs.Cancel = !custom;
        };
        customClipContextMenu.Closed += (_, _) =>
            PersistContextClipAlphaThreshold();
        listClips.ContextMenuStrip = customClipContextMenu;
        listClips.MouseDown += listClips_MouseDownForCustomContext;
        AppTheme.Apply(alphaPanel);
    }

    void SetupCustomPlayerVolumeMenu()
    {
        customPlayerVolumeMenu.ToolTipText =
            "Set separate audio volume for small and large custom shows. " +
            "Ctrl+mouse wheel over a show also changes it.";
        customPlayerVolumeMenu.DropDownItems.AddRange(
            [customPlayerSmallVolumeMenu, customPlayerLargeVolumeMenu]);
        foreach ((ToolStripMenuItem menu, bool large) in new[]
        {
            (customPlayerSmallVolumeMenu, false),
            (customPlayerLargeVolumeMenu, true)
        })
            foreach (int percent in Enumerable.Range(0, 11)
                         .Select(value => value * 10))
            {
                ToolStripMenuItem item = new($"{percent}%") { Tag = percent };
                item.Click += (_, _) => SetCustomPlayerVolume(large, percent);
                menu.DropDownItems.Add(item);
            }
        RefreshCustomPlayerVolumeMenu();
    }

    void SetupIStripperPlayerVolumeMenu()
    {
        iStripperPlayerVolumeMenu.ToolTipText =
            "Set separate audio volume for small and large iStripper shows. " +
            "Ctrl+mouse wheel over a show also changes it.";
        iStripperPlayerVolumeMenu.DropDownItems.AddRange(
            [iStripperPlayerSmallVolumeMenu, iStripperPlayerLargeVolumeMenu]);
        foreach ((ToolStripMenuItem menu, int mode) in new[]
        {
            (iStripperPlayerSmallVolumeMenu, 2),
            (iStripperPlayerLargeVolumeMenu, 1)
        })
            foreach (int percent in Enumerable.Range(0, 11)
                         .Select(value => value * 10))
            {
                ToolStripMenuItem item = new($"{percent}%") { Tag = percent };
                item.Click += (_, _) => SetIStripperPlayerVolume(mode, percent);
                menu.DropDownItems.Add(item);
            }
        RefreshIStripperPlayerVolumeMenu();
    }

    void SetIStripperPlayerVolume(int mode, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (mode == 1)
            Properties.Settings.Default.IStripperLargePlayerVolume = percent;
        else
            Properties.Settings.Default.IStripperSmallPlayerVolume = percent;
        Properties.Settings.Default.Save();
        RefreshIStripperPlayerVolumeMenu();
        if (customPlayer == null && Volatile.Read(ref playerMode) == mode)
            ApplyIStripperPlayerVolume(mode, percent, showOverlay: true);
    }

    void RefreshIStripperPlayerVolumeMenu()
    {
        int small = IStripperPlayerVolume(2), large = IStripperPlayerVolume(1);
        iStripperPlayerSmallVolumeMenu.Text = $"Small: {small}%";
        iStripperPlayerLargeVolumeMenu.Text = $"Large: {large}%";
        foreach ((ToolStripMenuItem menu, int current) in new[]
        {
            (iStripperPlayerSmallVolumeMenu, small),
            (iStripperPlayerLargeVolumeMenu, large)
        })
            foreach (ToolStripMenuItem item in menu.DropDownItems)
                item.Checked = item.Tag is int value && value == current;
    }

    void SetupCustomPlayerFullOpacityMenu()
    {
        int value = Math.Clamp(customShowConfiguration.FullOpacityThreshold, 1, 255);
        customShowConfiguration.FullOpacityThreshold = value;
        customPlayerFullOpacityMenu.Text = $"Custom player full opacity: {value}";
        customPlayerFullOpacityMenu.ToolTipText =
            "Custom-show alpha values at or above this level are rendered fully opaque.";
        customPlayerFullOpacitySlider.Minimum = 1;
        customPlayerFullOpacitySlider.Maximum = 255;
        customPlayerFullOpacitySlider.SmallChange = 1;
        customPlayerFullOpacitySlider.LargeChange = 16;
        customPlayerFullOpacitySlider.TickFrequency = 32;
        customPlayerFullOpacitySlider.TickStyle = TickStyle.BottomRight;
        customPlayerFullOpacitySlider.AutoSize = false;
        customPlayerFullOpacitySlider.Size = new System.Drawing.Size(260, 34);
        customPlayerFullOpacitySlider.AccessibleName =
            "Custom player full opacity threshold";
        customPlayerFullOpacitySlider.Value = value;
        customPlayerFullOpacitySlider.ValueChanged += (_, _) =>
        {
            int threshold = Math.Clamp((int)customPlayerFullOpacitySlider.Value, 1, 255);
            customShowConfiguration.FullOpacityThreshold = threshold;
            customPlayerFullOpacityMenu.Text = $"Custom player full opacity: {threshold}";
            customPlayer?.SetFullOpacityThreshold(threshold);
        };
        customPlayerFullOpacityMenu.DropDownClosed += (_, _) =>
            customShowConfiguration.Save();
        customPlayerFullOpacityMenu.DropDownItems.Add(new ToolStripControlHost(
            customPlayerFullOpacitySlider) { AutoSize = false, Size = new(260, 34) });
    }

    int CustomPlayerVolume(bool large) => Math.Clamp(large
        ? customShowConfiguration.LargePlayerVolume
        : customShowConfiguration.SmallPlayerVolume, 0, 100);

    void SetCustomPlayerVolume(bool large, int percent, bool apply = true)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (large) customShowConfiguration.LargePlayerVolume = percent;
        else customShowConfiguration.SmallPlayerVolume = percent;
        customShowConfiguration.Save();
        RefreshCustomPlayerVolumeMenu();
        if (apply && customPlayer != null && (playerMode == 1) == large)
            customPlayer.SetVolumePercent(percent);
    }

    void RefreshCustomPlayerVolumeMenu()
    {
        int small = CustomPlayerVolume(false), large = CustomPlayerVolume(true);
        customPlayerSmallVolumeMenu.Text = $"Small: {small}%";
        customPlayerLargeVolumeMenu.Text = $"Large: {large}%";
        foreach ((ToolStripMenuItem menu, int current) in new[]
        {
            (customPlayerSmallVolumeMenu, small),
            (customPlayerLargeVolumeMenu, large)
        })
            foreach (ToolStripMenuItem item in menu.DropDownItems)
                item.Checked = item.Tag is int value && value == current;
    }

    void SetCurrentCustomAlphaThreshold(int value)
    {
        if (loadingCustomAlphaThreshold || customPlayer == null ||
            customPlayerCard == null || customPlayerClip == null) return;
        value = Math.Clamp(value, 0, 255);
        customPlayer.SetAlphaThreshold(value);
        customPlayerClip.customAlphaThreshold = value;
        try
        {
            CustomShowStore store = new(customShowConfiguration.LibraryRoot);
            CustomShowManifest show = store.LoadManifest(customPlayerCard.customShowId!);
            int index = customPlayerCard.clips!.IndexOf(customPlayerClip);
            CustomShowClip[] included = show.Clips.Where(item => item.Included).ToArray();
            if (index < 0 || index >= included.Length) return;
            included[index].AlphaThreshold = value;
            store.SaveManifest(show);
        }
        catch (Exception error)
        {
            SetPlaybackStatus("Could not save alpha threshold: " + error.Message);
        }
    }

    void SetCurrentCustomEdgeChoke(float pixels)
    {
        if (loadingCustomEdgeChoke || customPlayer == null ||
            customPlayerCard == null || customPlayerClip == null) return;
        pixels = Math.Clamp(MathF.Round(pixels * 4) / 4, 0, 4);
        loadingCustomEdgeChoke = true;
        customEdgeChokeInput.Value = EdgeCleanupStep(pixels);
        loadingCustomEdgeChoke = false;
        customPlayer.SetEdgeChoke(pixels);
        customPlayerClip.customEdgeChokePixels = pixels;
        try
        {
            CustomShowStore store = new(customShowConfiguration.LibraryRoot);
            CustomShowManifest show = store.LoadManifest(
                customPlayerCard.customShowId!);
            int index = customPlayerCard.clips!.IndexOf(customPlayerClip);
            CustomShowClip[] included = show.Clips.Where(item => item.Included)
                .ToArray();
            if (index < 0 || index >= included.Length) return;
            included[index].EdgeChokePixels = pixels;
            store.SaveManifest(show);
        }
        catch (Exception error)
        {
            SetPlaybackStatus("Could not save edge cleanup: " + error.Message);
        }
    }

    static int EdgeCleanupStep(float pixels) =>
        Math.Clamp((int)MathF.Round(pixels * 4), 0, 16);

    static float EdgeCleanupPixels(int step) =>
        Math.Clamp(step, 0, 16) / 4f;

    static string EdgeCleanupText(float pixels) =>
        Math.Clamp(pixels, 0, 4).ToString("0.##");

    void ChangeContextClipAlphaThreshold(int value)
    {
        if (loadingCustomClipAlphaThreshold || customClipAlphaCard == null ||
            customClipAlphaClip == null) return;
        value = Math.Clamp(value, 0, 255);
        loadingCustomClipAlphaThreshold = true;
        customClipAlphaSlider.Value = value;
        customClipAlphaText.Text = value.ToString();
        loadingCustomClipAlphaThreshold = false;
        customClipAlphaClip.customAlphaThreshold = value;
        customClipAlphaDirty = true;
        customClipAlphaSaveTimer.Stop();
        customClipAlphaSaveTimer.Start();

        if (!string.Equals(customPlayerAnimationPath,
                GetAnimationPath(customClipAlphaClip),
                StringComparison.OrdinalIgnoreCase)) return;
        customPlayer?.SetAlphaThreshold(value);
        if (customPlayerClip != null)
            customPlayerClip.customAlphaThreshold = value;
        loadingCustomAlphaThreshold = true;
        customAlphaThresholdInput.Value = value;
        loadingCustomAlphaThreshold = false;
    }

    void PersistContextClipAlphaThreshold()
    {
        customClipAlphaSaveTimer.Stop();
        if (!customClipAlphaDirty || customClipAlphaCard == null ||
            customClipAlphaClip == null ||
            customClipAlphaCard.customShowId == null) return;
        customClipAlphaDirty = false;
        try
        {
            CustomShowStore store = new(customShowConfiguration.LibraryRoot);
            CustomShowManifest show = store.LoadManifest(
                customClipAlphaCard.customShowId);
            int index = customClipAlphaCard.clips!.IndexOf(customClipAlphaClip);
            CustomShowClip[] included = show.Clips.Where(value =>
                value.Included).ToArray();
            if (index < 0 || index >= included.Length)
                throw new InvalidDataException(
                    "The selected clip no longer matches the saved show.");
            included[index].AlphaThreshold =
                customClipAlphaClip.customAlphaThreshold;
            included[index].EdgeChokePixels =
                customClipAlphaClip.customEdgeChokePixels;
            store.SaveManifest(show);
        }
        catch (Exception error)
        {
            SetPlaybackStatus("Could not save alpha threshold: " +
                error.Message);
        }
    }

    void ChangeContextClipEdgeChoke(float pixels)
    {
        if (loadingCustomClipEdgeChoke || customClipAlphaCard == null ||
            customClipAlphaClip == null) return;
        pixels = Math.Clamp(MathF.Round(pixels * 4) / 4, 0, 4);
        loadingCustomClipEdgeChoke = true;
        customClipEdgeChokeInput.Value = EdgeCleanupStep(pixels);
        customClipEdgeChokeText.Text = EdgeCleanupText(pixels);
        loadingCustomClipEdgeChoke = false;
        customClipAlphaClip.customEdgeChokePixels = pixels;
        customClipAlphaDirty = true;
        customClipAlphaSaveTimer.Stop();
        customClipAlphaSaveTimer.Start();

        if (!string.Equals(customPlayerAnimationPath,
                GetAnimationPath(customClipAlphaClip),
                StringComparison.OrdinalIgnoreCase)) return;
        customPlayer?.SetEdgeChoke(pixels);
        if (customPlayerClip != null)
            customPlayerClip.customEdgeChokePixels = pixels;
        loadingCustomEdgeChoke = true;
        customEdgeChokeInput.Value = EdgeCleanupStep(pixels);
        loadingCustomEdgeChoke = false;
    }

    void RefreshContextClipClassificationChecks(ModelClip? clip)
    {
        string hotness = clip?.hotnessCode switch
        {
            Enums.HotnessCode.publ => "Public",
            Enums.HotnessCode.topless => "Topless",
            Enums.HotnessCode.nudity => "Nudity",
            Enums.HotnessCode.fullnudity => "FullNudity",
            Enums.HotnessCode.xxx => "XXX",
            _ => "NoNudity"
        };
        foreach (ToolStripMenuItem item in
                 customClipHotnessMenu.DropDownItems.OfType<ToolStripMenuItem>())
            item.Checked = clip != null && string.Equals(item.Tag as string,
                hotness, StringComparison.OrdinalIgnoreCase);

        HashSet<string> types = (clip?.clipType ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ToolStripMenuItem item in
                 customClipTypesMenu.DropDownItems.OfType<ToolStripMenuItem>())
            item.Checked = types.Contains(item.Tag as string ?? "");
    }

    void ChangeContextClipHotness(string hotness)
    {
        if (customClipAlphaCard == null || customClipAlphaClip == null) return;
        PersistContextClipClassification(customClipAlphaCard,
            customClipAlphaClip, hotness, null);
    }

    void ChangeContextClipType(ToolStripMenuItem changedItem)
    {
        if (customClipAlphaCard == null || customClipAlphaClip == null) return;
        string[] types = customClipTypesMenu.DropDownItems
            .OfType<ToolStripMenuItem>()
            .Where(item => item.Checked)
            .Select(item => item.Tag as string ?? "")
            .Where(value => value.Length > 0)
            .ToArray();
        if (types.Length == 0)
        {
            changedItem.Checked = true;
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        PersistContextClipClassification(customClipAlphaCard,
            customClipAlphaClip, null, types);
    }

    void PersistContextClipClassification(ModelCard card, ModelClip clip,
        string? hotness, string[]? clipTypes)
    {
        if (card.customShowId == null) return;
        PersistContextClipAlphaThreshold();
        try
        {
            CustomShowStore store = new(customShowConfiguration.LibraryRoot);
            CustomShowManifest show = store.LoadManifest(card.customShowId);
            int index = card.clips!.IndexOf(clip);
            CustomShowClip[] included = show.Clips.Where(value =>
                value.Included).ToArray();
            if (index < 0 || index >= included.Length)
                throw new InvalidDataException(
                    "The selected clip no longer matches the saved show.");
            if (hotness != null)
            {
                included[index].Hotness = hotness;
            }
            if (clipTypes != null)
            {
                included[index].ClipTypes = clipTypes;
            }
            store.SaveManifest(show);
            if (hotness != null)
                clip.hotnessCode = CustomShowStore.ParseHotness(hotness);
            if (clipTypes != null)
                clip.clipType = string.Join(", ", clipTypes);
            RefreshContextClipClassificationChecks(clip);
            selectingClipForContextMenu = true;
            try { loadListClips(card.name!); }
            finally { selectingClipForContextMenu = false; }
            RebuildAutomaticQueue();
        }
        catch (Exception error)
        {
            SetPlaybackStatus("Could not save clip classification: " +
                error.Message);
            RefreshContextClipClassificationChecks(clip);
        }
    }

    void SelectCustomAlphaThreshold(ModelCard card, ModelClip clip)
    {
        customPlayerCard = card;
        customPlayerClip = clip;
        loadingCustomAlphaThreshold = true;
        customAlphaThresholdInput.Value = clip.customAlphaThreshold;
        loadingCustomAlphaThreshold = false;
        loadingCustomEdgeChoke = true;
        customEdgeChokeInput.Value = EdgeCleanupStep(
            clip.customEdgeChokePixels);
        loadingCustomEdgeChoke = false;
    }

    void AppendCustomCards(StringBuilder? diagnostics = null)
    {
        try
        {
            CustomShowStore store = new(customShowConfiguration.LibraryRoot);
            IReadOnlyList<ModelCard> cards = store.LoadCards(!apiOnlyMode);
            Datastore.modelcards ??= [];
            Datastore.modelcards.AddRange(cards);
            diagnostics?.AppendLine($"Custom shows: loaded={cards.Count} " +
                $"skipped={store.LoadWarnings.Count} mediaValidation=manifest-only");
            foreach (string warning in store.LoadWarnings)
            {
                diagnostics?.AppendLine("Custom show skipped: " + warning);
                Debug.WriteLine("Custom show skipped: " + warning);
            }
        }
        catch (Exception error)
        {
            diagnostics?.AppendLine("Custom show library error: " + error.Message);
            Debug.WriteLine("Custom show library error: " + error);
        }
    }

    async Task CheckCustomShowsAsync(ToolStripMenuItem menu)
    {
        menu.Enabled = false;
        try
        {
            CustomShowStore store = new(customShowConfiguration.LibraryRoot);
            CustomShowCheckReport? report = null;
            using CustomShowProcessingForm checking = new(
                async (progress, token) =>
                {
                    report = await Task.Run(() => store.CheckShows(progress, token),
                        token);
                    return new CustomShowProcessResult();
                }, "Checking Custom Shows",
                processDescription:
                    "Opening each foreground and alpha video to verify the custom-show library",
                showPreviews: false);
            if (checking.ShowDialog(this) != DialogResult.OK || report == null)
                return;
            if (report.Issues.Count == 0)
            {
                MessageBox.Show(this,
                    report.TotalShows == 0
                        ? "No custom shows were found."
                        : $"All {report.ValidShows:N0} custom shows passed the " +
                            "manifest and foreground/alpha media checks.",
                    "Check Custom Shows", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            using CustomShowCheckForm results = new(store, report);
            DialogResult result = results.ShowDialog(this);
            if (results.DeletedAny) ReloadCustomCards();
            if (result == DialogResult.Retry && results.RepairShowId != null)
                EditCustomShow(results.RepairShowId, reprocess: true);
        }
        finally { menu.Enabled = true; }
    }

    void ReloadCustomCards()
    {
        Datastore.modelcards = (Datastore.modelcards ?? [])
            .Where(card => !card.IsCustom).ToList();
        AppendCustomCards();
        if (!apiOnlyMode)
        {
            CardOverlayLoader.Reload(Datastore.modelcards, myData);
            PopulateModelListview();
        }
        PersistModels();
    }

    string? CurrentContextCustomShowId()
    {
        string tag = currentMenuCard?.Tag?.ToString() ??
            mousedownCard?.Tag?.ToString() ?? "";
        return Datastore.findCardByTag(tag)?.customShowId;
    }

    void EditCustomShow(string? showId, bool reprocess = false)
    {
        using CustomShowEditorForm form = new(
            new CustomShowStore(customShowConfiguration.LibraryRoot),
            customShowConfiguration, showId, reprocess, customShowQueueManager);
        DialogResult result = form.ShowDialog(this);
        if (result == DialogResult.OK || form.SavedShowId != null)
        {
            if (form.SavedShowId != null)
            {
                // Refresh on the next UI turn, after the using scope has disposed
                // the editor and released its last preview/image handles.
                string savedShowId = form.SavedShowId;
                bool performerChanged = form.SavedPerformerChanged;
                BeginInvoke((Action)(() =>
                {
                    if (performerChanged)
                    {
                        ReloadCustomCards();
                        RebindCurrentCustomPlayback();
                    }
                    else
                        RefreshSavedCustomShow(savedShowId);
                }));
            }
            else
            {
                ReloadCustomCards();
                RebindCurrentCustomPlayback();
            }
        }
    }

    void RestoreIncompleteCustomShow(IWin32Window? owner = null)
    {
        owner ??= this;
        CustomShowStore store = new(customShowConfiguration.LibraryRoot);
        using CustomShowIncompletePickerForm picker = new(store);
        if (picker.ShowDialog(owner) != DialogResult.OK || picker.Selected == null)
            return;
        using CustomShowEditorForm form = new(store, customShowConfiguration,
            null, false, customShowQueueManager, restoredSetup: picker.Selected);
        DialogResult result = form.ShowDialog(owner);
        if (result == DialogResult.OK && form.SavedShowId != null)
            RefreshSavedCustomShow(form.SavedShowId);
    }

    async void RefreshSavedCustomShow(string showId)
    {
        const int attempts = 3;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                CustomShowStore store = new(
                    customShowConfiguration.LibraryRoot);
                ModelCard refreshed = await Task.Run(() =>
                    store.LoadCard(showId));
                ReplaceCustomCard(refreshed);
                RebindCurrentCustomPlayback();
                return;
            }
            catch (IOException) when (attempt < attempts) { }
            if (attempt < attempts)
                await Task.Delay(200);
        }
        Debug.WriteLine($"Published custom show {showId} was not available after refresh.");
    }

    void ReplaceCustomCard(ModelCard refreshed)
    {
        List<ModelCard> cards = Datastore.modelcards ?? [];
        int index = cards.FindIndex(card => string.Equals(card.name,
            refreshed.name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            ReloadCustomCards();
            return;
        }

        ModelCard previous = cards[index];
        List<ModelCard> replacement = [.. cards];
        replacement[index] = refreshed;
        Datastore.modelcards = replacement;
        CardOverlayLoader.RecalculateCard(refreshed, myData);

        bool rebuild = RequiresCardListRebuild(previous, refreshed);
        ImageListViewItem? visible = listModelsNew.Items.FirstOrDefault(item =>
            string.Equals(item.Tag?.ToString(), refreshed.name,
                StringComparison.OrdinalIgnoreCase));
        ListViewItem? virtualItem = items?.FirstOrDefault(item =>
            string.Equals(item.Tag?.ToString(), refreshed.name,
                StringComparison.OrdinalIgnoreCase));
        if (rebuild || visible == null || virtualItem == null)
        {
            directCompositionCardOverlays?.InvalidateCardImage(previous.image);
            previous.image?.Dispose();
            PopulateModelListview();
            return;
        }

        string text = refreshed.modelName + Environment.NewLine +
            refreshed.outfit;
        virtualItem.Text = text;
        visible.Text = text;
        directCompositionCardOverlays?.InvalidateCardImage(previous.image);
        listModelsNew.RefreshItems([visible]);
        RenderPlayQueues();
        if (visible.Selected)
            loadListClips(refreshed.name);
        previous.image?.Dispose();
    }

    bool RequiresCardListRebuild(ModelCard previous, ModelCard current)
    {
        if (!string.IsNullOrWhiteSpace(txtSearch.Text) ||
            chkFavourite.Checked ||
            !string.Equals(cmbFilter.Text, "Default",
                StringComparison.OrdinalIgnoreCase))
            return true;
        string sort = cmbSortBy.Text;
        return sort switch
        {
            "Model Name" => previous.modelName != current.modelName,
            "Rating" => previous.rating != current.rating,
            "Age" => previous.modelAge != current.modelAge,
            "Breast Size" => previous.bust != current.bust,
            "Waist" => previous.waist != current.waist,
            "Hips" => previous.hips != current.hips,
            "Ethnicity" => previous.ethnicity != current.ethnicity,
            "Height" => previous.height != current.height,
            "Date Purchased" => previous.datePurchased != current.datePurchased,
            "Release Date" => previous.dateReleased != current.dateReleased,
            _ => false
        };
    }

    void ShowCustomShowQueues()
    {
        if (customShowQueueManager == null) return;
        if (customShowQueueForm is null || customShowQueueForm.IsDisposed)
        {
            customShowQueueForm = new(customShowQueueManager, EditCustomShowQueueJob,
                DuplicateCustomShowQueueJob);
            customShowQueueForm.Show(this);
            return;
        }
        if (!customShowQueueForm.Visible)
            customShowQueueForm.Show(this);
        if (customShowQueueForm.WindowState == FormWindowState.Minimized)
            customShowQueueForm.WindowState = FormWindowState.Normal;
        customShowQueueForm.BringToFront();
        customShowQueueForm.Activate();
    }

    void QueueShowPublished()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke((Action)(() =>
        {
            ReloadCustomCards();
            RebindCurrentCustomPlayback();
        }));
    }

    async Task PrepareCustomShowPublicationAsync(string? targetShowId)
    {
        if (string.IsNullOrWhiteSpace(targetShowId)) return;
        if (InvokeRequired)
        {
            TaskCompletionSource completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(async () =>
            {
                try
                {
                    await PrepareCustomShowPublicationAsync(targetShowId);
                    completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
            });
            await completion.Task;
            return;
        }
        if (!string.Equals(customPlayerCard?.customShowId, targetShowId,
                StringComparison.OrdinalIgnoreCase))
            return;

        CustomPlayerForm? previous = customPlayer;
        string? replacement = FindPublicationReplacement(targetShowId);
        bool started = replacement != null && RequestAnimationPlayback(replacement);
        if (!started) StopCustomPlayback(restoreIstripper: true);
        if (previous != null) await previous.ClosePlayerAsync();
    }

    string? FindPublicationReplacement(string targetShowId)
    {
        if (items == null) return null;
        HashSet<string> recent = GetRecentPlaybackPaths();
        List<ModelClip> candidates = [];
        foreach (ListViewItem item in items)
        {
            ModelCard? card = Datastore.findCardByTag(item.Tag?.ToString() ?? "");
            if (!PlaybackCardAllowed(card) || card?.clips == null ||
                string.Equals(card.customShowId, targetShowId,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            candidates.AddRange(FilterClipList(card.clips));
        }
        if (candidates.Count == 0) return null;
        List<ModelClip> fresh = ExcludeRecentClips(candidates, recent);
        List<ModelClip> selectable = fresh.Count > 0 ? fresh : candidates;
        return GetAnimationPath(selectable[Random.Shared.Next(selectable.Count)]);
    }

    void EditCustomShowQueueJob(CustomShowQueueJob job)
    {
        if (job.Status == CustomShowQueueStatus.Completed &&
            !string.IsNullOrWhiteSpace(job.PublishedShowId))
        {
            EditCustomShow(job.PublishedShowId);
            return;
        }
        using CustomShowEditorForm form = new(
            new CustomShowStore(customShowConfiguration.LibraryRoot),
            customShowConfiguration,
            job.Operation == CustomShowQueueOperation.Reprocess ? job.TargetShowId : null,
            job.Operation == CustomShowQueueOperation.Reprocess,
            customShowQueueManager, job.Id);
        form.ShowDialog(this);
    }

    void DuplicateCustomShowQueueJob(CustomShowQueueJob draft, string assetOwnerId)
    {
        CustomShowStore store = new(customShowConfiguration.LibraryRoot);
        using CustomShowEditorForm form = new(store, customShowConfiguration,
            null, queueManager: customShowQueueManager,
            queueDraft: draft, queueDraftAssetOwnerId: assetOwnerId);
        if (form.ShowDialog(this) == DialogResult.OK)
            customShowQueueManager?.Run();
    }

    void ManageCustomModels(IWin32Window? owner = null)
    {
        owner ??= this;
        using CustomModelManagerForm form = new(
            new CustomShowStore(customShowConfiguration.LibraryRoot));
        if (form.ShowDialog(owner) == DialogResult.OK)
            ReloadCustomCards();
    }

    void CleanupFailedCustomShows(IWin32Window? owner = null)
    {
        owner ??= this;
        if (customShowQueueManager == null) return;
        using CustomShowFailedCleanupForm form = new(
            new CustomShowStore(customShowConfiguration.LibraryRoot),
            customShowQueueManager);
        form.ShowDialog(owner);
    }

    void ConfigureCustomShows()
    {
        if (customShowQueueManager?.IsRunning == true)
        {
            MessageBox.Show(this, "Stop the custom-show queue before changing its settings.",
                "Custom Shows", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using CustomShowSettingsForm form = new(customShowConfiguration,
            RestoreIncompleteCustomShow, ManageCustomModels,
            InstallCustomShowTools, CleanupFailedCustomShows);
        if (form.ShowDialog(this) != DialogResult.OK)
            return;
        customShowConfiguration = form.Configuration;
        customShowConfiguration.Save();
        customShowQueueForm?.Dispose();
        customShowQueueForm = null;
        customShowQueueManager?.Dispose();
        customShowQueueManager = new CustomShowQueueManager(
            new CustomShowStore(customShowConfiguration.LibraryRoot),
            customShowConfiguration, QueueShowPublished,
            PrepareCustomShowPublicationAsync);
        ReloadCustomCards();
    }

    string? InstallCustomShowTools(IWin32Window? owner = null)
    {
        owner ??= this;
        using CustomShowSetupOptionsForm options = new();
        if (options.ShowDialog(owner) != DialogResult.OK) return null;

        string script = Path.Combine(AppContext.BaseDirectory,
            "custom-shows", "setup.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show(owner, "The setup script is missing:\n" + script,
                "Custom Shows", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }

        try
        {
            using CustomShowSetupForm form = new(script,
                options.InstallTransNetV2, options.InstallOmniShotCut,
                options.InstallMatAnyone2, options.InstallViTMatte,
                options.InstallProPainter, options.InstallEdgeTam,
                options.InstallStabilo);
            if (form.ShowDialog(owner) != DialogResult.OK) return null;
            if (options.InstallSam2Matting)
            {
                string sam2MattingScript = Path.Combine(AppContext.BaseDirectory,
                    "custom-shows", "setup-sam2matting.ps1");
                if (!File.Exists(sam2MattingScript))
                    throw new FileNotFoundException(
                        "The SAM2Matting setup script is missing.", sam2MattingScript);
                using CustomShowSam2MattingSetupForm sam2MattingSetup =
                    new(sam2MattingScript);
                if (sam2MattingSetup.ShowDialog(owner) != DialogResult.OK)
                    return null;
                customShowConfiguration.Sam2MattingPythonExecutable =
                    CustomShowConfiguration.FindSam2MattingPythonExecutable();
            }
            string python = CustomShowConfiguration.FindPythonExecutable();
            if (!File.Exists(python) ||
                !python.Contains("rvm-runtime", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Setup completed but its Python environment was not found.");
            customShowConfiguration.PythonExecutable = python;
            customShowConfiguration.Save();
            MessageBox.Show(owner,
                "Processing tools are installed and Python is now set to:\n" + python,
                "Custom Shows", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return python;
        }
        catch (Exception error)
        {
            MessageBox.Show(owner, error.Message, "Custom Show Setup",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    void RemoveVideoWatermark()
    {
        using CustomWatermarkRemovalForm form = new(customShowConfiguration);
        form.ShowDialog(this);
    }

    void StabilizeVideo()
    {
        using CustomVideoStabilizationForm form = new(customShowConfiguration);
        form.ShowDialog(this);
    }

    void StabilizeBackgroundVideo()
    {
        using CustomStabiloStabilizationForm form = new(customShowConfiguration);
        form.ShowDialog(this);
    }

    async void DeleteCurrentCustomShow()
    {
        string? showId = CurrentContextCustomShowId();
        ModelCard? card = showId == null ? null :
            Datastore.findCardByTag("custom:" + showId);
        if (showId == null || card == null)
            return;
        string playing = !string.IsNullOrEmpty(customPlayerAnimationPath)
            ? customPlayerAnimationPath : GetCurrentAnimationPath();
        if (string.Equals(GetCardTagFromAnimationPath(playing), card.name,
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "This custom show is currently playing. Play another show " +
                "or stop playback before deleting it.",
                "Delete Custom Show", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Move '{card.outfit}' to the Recycle Bin?",
                "Delete Custom Show", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        try
        {
            UseWaitCursor = true;
            CustomShowStore store = new(customShowConfiguration.LibraryRoot);
            await Task.Run(() => store.DeleteShow(showId));
            RemoveCustomClipQueueEntries(card.name, null);
            Datastore.modelcards = (Datastore.modelcards ?? [])
                .Where(value => !string.Equals(value.name, card.name,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            CardOverlayLoader.ForgetCard(card.name);
            ImageListViewItem? visible = listModelsNew.Items.FirstOrDefault(
                item => string.Equals(item.Tag?.ToString(), card.name,
                    StringComparison.OrdinalIgnoreCase));
            items = items?.Where(item => !string.Equals(
                    item.Tag?.ToString(), card.name,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            directCompositionCardOverlays?.ResetCardImages();
            cardRenderer?.ResetCardScene();
            if (visible != null)
                listModelsNew.Items.Remove(visible);
            if (string.Equals(clipListTag, card.name,
                    StringComparison.OrdinalIgnoreCase))
            {
                clipListTag = "";
                listClips.Items.Clear();
            }
            lblModelsLoaded.Text = "Cards Shown: " +
                listModelsNew.Items.Count + "/" +
                (Datastore.modelcards?.Count(value =>
                    value.clips?.Count > 0) ?? 0);
            RebuildAutomaticQueue();
            card.image?.Dispose();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Delete Custom Show",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; }
    }

    void listClips_MouseDownForCustomContext(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        ListViewItem? item = listClips.HitTest(e.Location).Item;
        if (item == null) return;
        selectingClipForContextMenu = true;
        try
        {
            listClips.SelectedItems.Clear();
            item.Selected = true;
            item.Focused = true;
        }
        finally
        {
            selectingClipForContextMenu = false;
        }
    }

    bool TryGetSelectedCustomClip(out ModelCard card, out ModelClip clip)
    {
        card = null!;
        clip = null!;
        if (listClips.SelectedItems.Count != 1) return false;
        card = Datastore.findCardByTag(clipListTag)!;
        if (card?.IsCustom != true || card.clips == null) return false;
        string clipName = listClips.SelectedItems[0].SubItems[1].Text;
        clip = card.clips.FirstOrDefault(value => string.Equals(
            value.clipName, clipName, StringComparison.OrdinalIgnoreCase))!;
        return clip != null;
    }

    async Task TrimSelectedCustomClipAsync()
    {
        if (!TryGetSelectedCustomClip(out ModelCard card,
                out ModelClip modelClip) || card.customShowId == null ||
            card.clips == null || string.IsNullOrWhiteSpace(modelClip.clipName))
            return;

        string animationPath = GetAnimationPath(modelClip);
        bool selectedIsPlaying = string.Equals(customPlayerAnimationPath,
            animationPath, StringComparison.OrdinalIgnoreCase);
        bool wasPaused = customPlayer?.Paused == true;
        if (selectedIsPlaying)
        {
            customPlayer?.SetPaused(true);
            customPlayer?.HidePlayer();
        }

        try
        {
            CustomShowStore store = new(customShowConfiguration.LibraryRoot);
            CustomShowManifest show = store.LoadManifest(card.customShowId);
            CustomShowClip[] included = show.Clips.Where(value =>
                value.Included).ToArray();
            int index = card.clips.IndexOf(modelClip);
            if (index < 0 || index >= included.Length ||
                included[index].Media is not CustomClipMedia media)
                throw new InvalidDataException(
                    "The selected clip no longer matches the saved show.");

            string folder = Path.Combine(store.ShowsFolder, show.Id);
            using CustomClipTrimForm trim = new(
                $"{card.outfit} (Clip {modelClip.clipNumber})",
                CustomShowStore.ResolveRelative(folder, media.Foreground),
                CustomShowStore.ResolveRelative(folder, media.Alpha), media,
                included[index].AlphaThreshold,
                customShowConfiguration.FullOpacityThreshold,
                included[index].EdgeChokePixels);
            DialogResult result;
            try { result = trim.ShowDialog(this); }
            finally { await trim.ClosePreviewAsync(); }

            if (result != DialogResult.OK)
            {
                if (selectedIsPlaying && customPlayer != null)
                {
                    customPlayer.ShowPlayer();
                    customPlayer.SetPaused(wasPaused);
                }
                return;
            }

            media.PlaybackStartMs = trim.StartMs;
            media.PlaybackEndMs = trim.EndMs == media.DurationMs
                ? null : trim.EndMs;
            store.SaveManifest(show);

            if (selectedIsPlaying)
                StopCustomPlayback(restoreIstripper: false);
            selectingClipForContextMenu = true;
            try
            {
                ReloadCustomCards();
                RebindCurrentCustomPlayback();
                if (Datastore.findCardByTag(card.name!) != null)
                    loadListClips(card.name!);
            }
            finally
            {
                selectingClipForContextMenu = false;
            }
            RebuildAutomaticQueue();

            if (selectedIsPlaying && RequestAnimationPlayback(animationPath) &&
                wasPaused)
                customPlayer?.SetPaused(true);
            SetPlaybackStatus(trim.StartMs == 0 && trim.EndMs == media.DurationMs
                ? "Custom clip trim reset to the full processed clip."
                : $"Custom clip trimmed to {TimeSpan.FromMilliseconds(
                    trim.EndMs - trim.StartMs):g}.");
        }
        catch (Exception error)
        {
            if (selectedIsPlaying && customPlayer != null)
            {
                customPlayer.ShowPlayer();
                customPlayer.SetPaused(wasPaused);
            }
            MessageBox.Show(this, error.Message, "Trim Custom Clip",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    async Task DeleteSelectedCustomClipAsync()
    {
        if (!TryGetSelectedCustomClip(out ModelCard card,
                out ModelClip modelClip) || card.customShowId == null ||
            string.IsNullOrWhiteSpace(modelClip.clipName))
            return;

        string animationPath = GetAnimationPath(modelClip);
        bool finalClip = card.clips?.Count == 1;
        string prompt = finalClip
            ? $"'{card.outfit}' has only one clip. Deleting it will move the " +
              "entire custom show to the Recycle Bin. Continue?"
            : $"Delete clip {modelClip.clipNumber} from '{card.outfit}' and " +
              "move its processed foreground and alpha files to the Recycle Bin?";
        if (MessageBox.Show(this, prompt, "Delete Custom Clip",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) !=
            DialogResult.Yes)
            return;

        try
        {
            if (string.Equals(customPlayerAnimationPath, animationPath,
                    StringComparison.OrdinalIgnoreCase))
                await AdvanceBeforeDeletingCustomClipAsync(animationPath);

            CustomShowStore store = new(customShowConfiguration.LibraryRoot);
            string? cleanupWarning;
            if (finalClip)
            {
                store.DeleteShow(card.customShowId);
                cleanupWarning = null;
                RemoveCustomClipQueueEntries(card.name!, null);
            }
            else
            {
                CustomShowManifest show = store.LoadManifest(card.customShowId);
                CustomShowClip[] included = show.Clips.Where(value =>
                    value.Included).ToArray();
                int index = card.clips!.IndexOf(modelClip);
                if (index < 0 || index >= included.Length)
                    throw new InvalidDataException(
                        "The selected clip no longer matches the saved show.");
                cleanupWarning = store.DeleteClip(card.customShowId,
                    included[index].Id);
                RemoveCustomClipQueueEntries(card.name!, animationPath);
            }

            selectingClipForContextMenu = true;
            try
            {
                ReloadCustomCards();
                RebindCurrentCustomPlayback();
                if (!finalClip && Datastore.findCardByTag(card.name!) != null)
                    loadListClips(card.name!);
            }
            finally
            {
                selectingClipForContextMenu = false;
            }

            if (!string.IsNullOrWhiteSpace(cleanupWarning))
                MessageBox.Show(this,
                    "The clip was removed from the library, but its processed " +
                    "files could not be moved to the Recycle Bin:\n" +
                    cleanupWarning,
                    "Delete Custom Clip", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Delete Custom Clip",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    async Task AdvanceBeforeDeletingCustomClipAsync(string animationPath)
    {
        CustomPlayerForm? previous = customPlayer;
        GetNextClip(null, animationPath, useQueue: false);
        if (previous != null)
            await previous.ClosePlayerAsync();

        if (!string.Equals(customPlayerAnimationPath, animationPath,
                StringComparison.OrdinalIgnoreCase))
            return;
        CustomPlayerForm? repeated = customPlayer;
        StopCustomPlayback(restoreIstripper: true);
        if (repeated != null && repeated != previous)
            await repeated.ClosePlayerAsync();
    }

    void RemoveCustomClipQueueEntries(string cardTag, string? clipName)
    {
        bool Matches(PlayQueueEntry entry) => string.Equals(entry.CardTag,
                cardTag, StringComparison.OrdinalIgnoreCase) &&
            (clipName == null || string.Equals(entry.ClipName, clipName,
                StringComparison.OrdinalIgnoreCase));
        manualPlayQueue.RemoveAll(entry => Matches(entry));
        automaticPlayQueue.RemoveAll(entry => Matches(entry));
        if (activeManualQueueEntry != null && Matches(activeManualQueueEntry))
            activeManualQueueEntry = null;
        if (activeAutomaticQueueEntry != null &&
            Matches(activeAutomaticQueueEntry))
            activeAutomaticQueueEntry = null;
        if (activeQueuedCard != null && Matches(activeQueuedCard))
            activeQueuedCard = null;
        SavePreviousQueue();
        RenderPlayQueues();
    }

    void RebindCurrentCustomPlayback()
    {
        if (string.IsNullOrEmpty(customPlayerAnimationPath)) return;
        ModelCard? card = Datastore.findCardByTag(
            GetCardTagFromAnimationPath(customPlayerAnimationPath));
        ModelClip? clip = card?.clips?.FirstOrDefault(value => string.Equals(
            value.clipName, customPlayerAnimationPath,
            StringComparison.OrdinalIgnoreCase));
        if (card == null || clip == null) return;
        customPlayerCard = card;
        customPlayerClip = clip;
        customPlayer?.SetAlphaThreshold(clip.customAlphaThreshold);
        customPlayer?.SetEdgeChoke(clip.customEdgeChokePixels);
        loadingCustomAlphaThreshold = true;
        customAlphaThresholdInput.Value = clip.customAlphaThreshold;
        loadingCustomAlphaThreshold = false;
        loadingCustomEdgeChoke = true;
        customEdgeChokeInput.Value = EdgeCleanupStep(
            clip.customEdgeChokePixels);
        loadingCustomEdgeChoke = false;
    }

    bool RequestAnimationPlayback(string animationPath)
    {
        if (animationPath.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            return PlaybackSourceAllowed(IsFullscreenModeActive(), custom: true) &&
                StartCustomPlayback(animationPath);
        bool customTransition = customIstripperSuspended;
        StopCustomPlayback(restoreIstripper: !customTransition);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Totem\vghd\parameters", true);
        if (key == null)
        {
            if (customTransition) RestoreIStripperAfterCustomPlayback();
            return false;
        }
        customPendingIstripperAnimation = customTransition ? animationPath : "";
        BeginAnimationReplacement(animationPath);
        key.SetValue("ForceAnim", animationPath);
        if (customTransition)
        {
            ResumeHiddenIStripperForTransition();
            _ = CompleteIStripperTransitionAsync(animationPath);
        }
        return true;
    }

    private bool IsFullscreenModeActive() =>
        Volatile.Read(ref playerMode) == 3 ||
        ReadRegistryInteger(@"Software\Totem\vghd\player", "playingMode") == 3;

    internal static bool PlaybackSourceAllowed(bool fullscreen, bool custom) =>
        !fullscreen || !custom;

    private bool PlaybackCardAllowed(ModelCard? card) => card != null &&
        PlaybackSourceAllowed(IsFullscreenModeActive(), card.IsCustom);

    bool StartCustomPlayback(string animationPath)
    {
        if (InvokeRequired)
            return (bool)Invoke(() => StartCustomPlayback(animationPath));
        ModelCard? card = Datastore.findCardByTag(
            GetCardTagFromAnimationPath(animationPath));
        ModelClip? clip = card?.clips?.FirstOrDefault(item => string.Equals(
            item.clipName, animationPath, StringComparison.OrdinalIgnoreCase));
        if (clip?.customForegroundPath == null || clip.customAlphaPath == null ||
            !File.Exists(clip.customForegroundPath) || !File.Exists(clip.customAlphaPath))
            return false;
        CustomPlayerForm? previous = customPlayer;
        LogCustomPlayerPosition($"start {animationPath}; previous=" +
            FormatCustomPlayerBounds(previous?.Bounds) + "; custom=" +
            FormatCustomPlayerBounds(customPlayerBounds));
        if (previous == null && !customIstripperSuspended)
            SuspendIStripperForCustomPlayback();
        if (previous != null) RememberCustomPlayerBounds(previous);
        Rectangle? initialBounds = customPlayerBounds;
        LogCustomPlayerPosition("handoff initial=" +
            FormatCustomPlayerBounds(initialBounds));
        int mode = Volatile.Read(ref playerMode);
        int size = PlayerSizeForAnimation(
            animationPath, mode, mode == 1 ? 80 : 40);
        Task<CustomPlayerForm.PreparedPlayback?>? preparedPlayback =
            TakeCustomPreload(animationPath);
        CustomPlayerForm player = new(clip.customForegroundPath,
            clip.customAlphaPath, size,
            CustomPlayerVolume(playerMode == 1), clip.customAlphaThreshold,
            customShowConfiguration.FullOpacityThreshold,
            startMs: clip.customStartMs,
            endMs: clip.customEndMs, initialBounds: initialBounds,
            preparedPlayback: preparedPlayback,
            edgeChokePixels: clip.customEdgeChokePixels);
        player.HoldFinalFrameOnCompletion = true;
        player.SetLocked(playerlocked);
        player.SetClickThroughLocked(Properties.Settings.Default.ClickThroughLockedPlayer);
        player.SetWheelResize(Properties.Settings.Default.EnablePlayerWheelResize);
        player.SetAllowWheelWhileLocked(
            Properties.Settings.Default.AllowWheelWhileLocked);
        player.VolumeChanged += percent => BeginInvoke(() =>
        {
            if (customPlayer == player)
                SetCustomPlayerVolume(playerMode == 1, percent, false);
        });
        player.SizePercentChanged += percent => BeginInvoke(() =>
        {
            if (customPlayer == player)
                RememberCustomPlayerSize(
                    animationPath, Volatile.Read(ref playerMode), percent);
        });
        player.LocationChanged += (_, _) =>
        {
            if (!player.HasEstablishedBounds || player.Left == 0)
                LogCustomPlayerPosition("location " +
                    FormatCustomPlayerBounds(player.Bounds) + "; established=" +
                    player.HasEstablishedBounds);
            if (customPlayer == player) RememberCustomPlayerBounds(player);
        };
        player.SizeChanged += (_, _) =>
        {
            if (!player.HasEstablishedBounds || player.Left == 0)
                LogCustomPlayerPosition("size " +
                    FormatCustomPlayerBounds(player.Bounds) + "; established=" +
                    player.HasEstablishedBounds);
            if (customPlayer == player) RememberCustomPlayerBounds(player);
        };
        customPlayer = player;
        SelectCustomAlphaThreshold(card!, clip);
        customPlayerAnimationPath = animationPath;
        RefreshPlaybackControlVisibility();
        if (previous != null)
        {
            player.FirstFramePresented += (_, _) => previous.ClosePlayer();
        }
        player.PreloadRequested += (_, _) => BeginInvoke(() =>
            PreloadNextCustomClip(card!, clip));
        player.PlaybackCompleted += (_, _) =>
        {
            Rectangle playerBounds = player.Bounds;
            BeginInvoke(() =>
            {
                if (customPlayer != player) return;
                customPlayerBounds = playerBounds;
                GetNextClip(null, animationPath);
                if (customPlayer == player)
                {
                    customPlayer = null;
                    customPlayerCard = null;
                    customPlayerClip = null;
                    customPlayerAnimationPath = "";
                    player.ClosePlayer();
                }
                if (customPlayer == null && string.IsNullOrEmpty(
                        customPendingIstripperAnimation))
                    RestoreIStripperAfterCustomPlayback();
            });
        };
        player.PlaybackFailed += (_, _) => BeginInvoke(() =>
        {
            previous?.ClosePlayer();
            if (customPlayer == player) StopCustomPlayback(true);
        });
        player.FormClosed += (_, _) =>
        {
            Rectangle playerBounds = player.Bounds;
            BeginInvoke(() =>
            {
                if (customPlayer != player) return;
                customPlayerBounds = playerBounds;
                customPlayer = null;
                customPlayerCard = null;
                customPlayerClip = null;
                customPlayerAnimationPath = "";
                RestoreIStripperAfterCustomPlayback();
            });
        };
        BeginAnimationReplacement(animationPath);
        ShowNowPlaying(animationPath, doWallpaper: true);
        player.Show(this);
        return true;
    }

    void PreloadNextCustomClip(ModelCard card, ModelClip current)
    {
        if (customPlayerCard != card || customPlayerClip != current ||
            card.clips == null || !CanPreloadCurrentCard(
                GetAnimationPath(current)))
            return;
        List<ModelClip> playable = FilterClipList(card.clips)
            .OrderBy(value => value.clipNumber).ToList();
        int index = playable.IndexOf(current);
        if (index < 0 || playable.Count < 2) return;
        ModelClip? next;
        if (Properties.Settings.Default.Randomize &&
            !UnqueuedCardContinuesSequentially(GetAnimationPath(current)))
        {
            List<ModelClip> candidates = playable.Where(value =>
                value != current).ToList();
            List<ModelClip> fresh = ExcludeRecentClips(candidates,
                GetRecentPlaybackPaths());
            if (fresh.Count > 0) candidates = fresh;
            next = candidates.Count == 0 ? null : candidates[
                Random.Shared.Next(candidates.Count)];
        }
        else
        {
            next = index + 1 < playable.Count ? playable[index + 1] : null;
        }
        if (next == null) return;
        if (next.customForegroundPath == null || next.customAlphaPath == null ||
            !File.Exists(next.customForegroundPath) ||
            !File.Exists(next.customAlphaPath)) return;
        string path = GetAnimationPath(next);
        if (string.Equals(customPreloadedAnimationPath, path,
                StringComparison.OrdinalIgnoreCase)) return;

        CancelCustomPreload();
        customPreloadCancellation = new CancellationTokenSource();
        customPreloadedAnimationPath = path;
        customPreloadedPlayback = CustomPlayerForm.PrepareAsync(
            next.customForegroundPath, next.customAlphaPath,
            next.customAlphaThreshold,
            customShowConfiguration.FullOpacityThreshold,
            next.customEdgeChokePixels,
            next.customStartMs, customPreloadCancellation.Token);
    }

    Task<CustomPlayerForm.PreparedPlayback?>? TakeCustomPreload(
        string animationPath)
    {
        if (!string.Equals(customPreloadedAnimationPath, animationPath,
                StringComparison.OrdinalIgnoreCase))
        {
            CancelCustomPreload();
            return null;
        }
        Task<CustomPlayerForm.PreparedPlayback?>? prepared =
            customPreloadedPlayback;
        customPreloadedPlayback = null;
        customPreloadedAnimationPath = "";
        customPreloadCancellation?.Dispose();
        customPreloadCancellation = null;
        return prepared;
    }

    bool TryPlayPreloadedCustomClip(string completedAnimation)
    {
        if (string.IsNullOrEmpty(customPreloadedAnimationPath) ||
            !completedAnimation.StartsWith("custom:",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetCardTagFromAnimationPath(completedAnimation),
                GetCardTagFromAnimationPath(customPreloadedAnimationPath),
                StringComparison.OrdinalIgnoreCase))
            return false;
        return RequestAnimationPlayback(customPreloadedAnimationPath);
    }

    void CancelCustomPreload()
    {
        CancellationTokenSource? cancellation = customPreloadCancellation;
        Task<CustomPlayerForm.PreparedPlayback?>? prepared =
            customPreloadedPlayback;
        customPreloadCancellation = null;
        customPreloadedPlayback = null;
        customPreloadedAnimationPath = "";
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (prepared != null)
            _ = prepared.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    task.Result?.Dispose();
            }, TaskScheduler.Default);
    }

    void SuspendIStripperForCustomPlayback()
    {
        bool firstAttempt = !customIstripperSuspended;
        customIstripperSuspended = true;
        if (firstAttempt) customResumeIstripper = false;
        IntPtr visibleMovieWindow =
            LockStateOverlay.HideMovieWindowForProcess(vghd_procID);
        if (visibleMovieWindow != IntPtr.Zero)
        {
            customHiddenMovieWindow = visibleMovieWindow;
        }
        if (!playbackBridgeLoaded) return;
        try
        {
            if (!playbackMovieRegistered)
                playbackMovieRegistered = CallPlaybackBridgeApi(
                    "IStripperDiscoverMovie") >= 0;
            if (!playbackMovieRegistered ||
                RequirePlaybackResult("IStripperGetState") != 3) return;
            customResumeIstripper = true;
            RequirePlaybackResult("IStripperPause");
        }
        catch { }
    }

    void StopCustomPlayback(bool restoreIstripper)
    {
        CancelCustomPreload();
        CustomPlayerForm? player = customPlayer;
        if (player != null) RememberCustomPlayerBounds(player);
        customPlayer = null;
        customPlayerCard = null;
        customPlayerClip = null;
        customPlayerAnimationPath = "";
        if (!apiOnlyMode) RefreshPlaybackControlVisibility();
        player?.ClosePlayer();
        if (restoreIstripper) RestoreIStripperAfterCustomPlayback();
    }

    void RestoreIStripperAfterCustomPlayback()
    {
        if (!customIstripperSuspended) return;
        customIstripperSuspended = false;
        customPendingIstripperAnimation = "";
        if (!apiOnlyMode) RefreshPlaybackControlVisibility();
        customHiddenMovieWindow = LockStateOverlay.ResolveMovieWindowForProcess(
            vghd_procID, customHiddenMovieWindow);
        IntPtr movieWindow = customHiddenMovieWindow;
        LockStateOverlay.ShowMovieWindow(movieWindow);
        customHiddenMovieWindow = IntPtr.Zero;
        if (customResumeIstripper && playbackBridgeLoaded && playbackMovieRegistered)
        {
            try
            {
                if (RequirePlaybackResult("IStripperGetState") == 4)
                    RequirePlaybackResult("IStripperResume");
            }
            catch { }
        }
        customResumeIstripper = false;
        QueueConfiguredPlayerSize(mode: Volatile.Read(ref playerMode));
    }

    void ResumeHiddenIStripperForTransition()
    {
        if (!customResumeIstripper || !playbackBridgeLoaded ||
            !playbackMovieRegistered) return;
        try
        {
            if (RequirePlaybackResult("IStripperGetState") == 4)
                RequirePlaybackResult("IStripperResume");
        }
        catch { }
    }

    void RefreshHiddenIStripperWindow()
    {
        IntPtr visible = LockStateOverlay.HideMovieWindowForProcess(vghd_procID);
        if (visible != IntPtr.Zero)
            customHiddenMovieWindow = visible;
    }

    async Task CompleteIStripperTransitionAsync(string animationPath)
    {
        DateTime matchedAt = DateTime.MinValue;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (!customIstripperSuspended || !string.Equals(
                    customPendingIstripperAnimation, animationPath,
                    StringComparison.OrdinalIgnoreCase))
                return;
            RefreshHiddenIStripperWindow();
            if (string.Equals(GetCurrentAnimationPath(), animationPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                matchedAt = matchedAt == DateTime.MinValue
                    ? DateTime.UtcNow : matchedAt;
                if (IStripperTransitionSettled(animationPath,
                        GetCurrentAnimationPath(), matchedAt, DateTime.UtcNow))
                    break;
            }
            else
            {
                matchedAt = DateTime.MinValue;
            }
            await Task.Delay(50);
        }
        if (customIstripperSuspended && string.Equals(
                customPendingIstripperAnimation, animationPath,
                StringComparison.OrdinalIgnoreCase))
            RestoreIStripperAfterCustomPlayback();
    }

    static bool IStripperTransitionSettled(string expected, string current,
        DateTime matchedAt, DateTime now) =>
        matchedAt != DateTime.MinValue &&
        string.Equals(expected, current, StringComparison.OrdinalIgnoreCase) &&
        now - matchedAt >= TimeSpan.FromMilliseconds(300);

    internal static bool VerifyCustomPlaybackHandoff()
    {
        DateTime now = DateTime.UtcNow;
        return IStripperTransitionSettled("card\\clip", "CARD\\CLIP",
                   now.AddMilliseconds(-301), now) &&
            !IStripperTransitionSettled("card\\clip", "other\\clip",
                now.AddSeconds(-1), now) &&
            !IStripperTransitionSettled("card\\clip", "card\\clip",
                now.AddMilliseconds(-299), now);
    }

    void RememberCustomPlayerBounds(CustomPlayerForm player)
    {
        if (!player.IsDisposed && player.HasEstablishedBounds)
            customPlayerBounds = player.Bounds;
    }

    static string FormatCustomPlayerBounds(Rectangle? bounds) =>
        bounds is Rectangle value
            ? $"{value.Left},{value.Top},{value.Width},{value.Height}"
            : "null";

    static void LogCustomPlayerPosition(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(
                customPlayerPositionLog)!);
            File.AppendAllText(customPlayerPositionLog,
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
