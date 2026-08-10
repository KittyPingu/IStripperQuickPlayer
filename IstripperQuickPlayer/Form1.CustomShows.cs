using System.Diagnostics;
using System.Text;
using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using Microsoft.Win32;

namespace IStripperQuickPlayer;

public partial class Form1
{
    readonly ToolStripMenuItem customShowsMenu = new("Custom Shows");
    readonly ToolStripMenuItem customPlayerVolumeMenu =
        new("Custom player volume");
    readonly ToolStripMenuItem customPlayerSmallVolumeMenu = new();
    readonly ToolStripMenuItem customPlayerLargeVolumeMenu = new();
    readonly ToolStripMenuItem customPlayerFullOpacityMenu = new();
    readonly TrackBar customPlayerFullOpacitySlider = new();
    readonly Label customAlphaThresholdLabel = new()
        { Text = "Alpha threshold", AutoSize = true };
    readonly NumericUpDown customAlphaThresholdInput = new()
        { Minimum = 0, Maximum = 255, Width = 70 };
    readonly ToolStripMenuItem editCustomShowMenu = new("Edit Custom Show Metadata...");
    readonly ToolStripMenuItem deleteCustomShowMenu = new("Delete Custom Show");
    CustomShowConfiguration customShowConfiguration = CustomShowConfiguration.Load();
    CustomPlayerForm? customPlayer;
    ModelCard? customPlayerCard;
    ModelClip? customPlayerClip;
    bool loadingCustomAlphaThreshold;
    string customPlayerAnimationPath = "";
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
        customAlphaThresholdLabel.Location = new Point(500, 62);
        customAlphaThresholdInput.Location = new Point(650, 58);
        customAlphaThresholdLabel.Visible = customAlphaThresholdInput.Visible = false;
        customAlphaThresholdInput.ValueChanged += (_, _) =>
            SetCurrentCustomAlphaThreshold((int)customAlphaThresholdInput.Value);
        panelClip.Controls.Add(customAlphaThresholdLabel);
        panelClip.Controls.Add(customAlphaThresholdInput);
        SetupCustomPlayerVolumeMenu();
        SetupCustomPlayerFullOpacityMenu();
        ToolStripMenuItem create = new("Create Show...");
        ToolStripMenuItem removeWatermark = new("Remove Video Object / Watermark...");
        ToolStripMenuItem models = new("Manage Models...");
        ToolStripMenuItem setup = new("Install / Update Processing Tools...");
        ToolStripMenuItem settings = new("Settings...");
        ToolStripMenuItem open = new("Open Folder");
        create.Click += (_, _) => EditCustomShow(null);
        removeWatermark.Click += (_, _) => RemoveVideoWatermark();
        models.Click += (_, _) => ManageCustomModels();
        setup.Click += (_, _) => InstallCustomShowTools();
        settings.Click += (_, _) => ConfigureCustomShows();
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(customShowConfiguration.LibraryRoot);
            Process.Start(new ProcessStartInfo(customShowConfiguration.LibraryRoot)
            { UseShellExecute = true });
        };
        customShowsMenu.DropDownItems.AddRange(
            [create, removeWatermark, models, setup, settings, open]);
        fileToolStripMenuItem.DropDownItems.Insert(0, customShowsMenu);

        editCustomShowMenu.Click += (_, _) =>
            EditCustomShow(CurrentContextCustomShowId());
        deleteCustomShowMenu.Click += (_, _) => DeleteCurrentCustomShow();
        menuCardList.Items.Add(new ToolStripSeparator());
        menuCardList.Items.Add(editCustomShowMenu);
        menuCardList.Items.Add(deleteCustomShowMenu);
        menuCardList.Opening += (_, _) =>
        {
            bool custom = CurrentContextCustomShowId() != null;
            editCustomShowMenu.Visible = custom;
            deleteCustomShowMenu.Visible = custom;
            deleteFromDiskToolStripMenuItem.Visible = !custom;
        };
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

    void SelectCustomAlphaThreshold(ModelCard card, ModelClip clip)
    {
        customPlayerCard = card;
        customPlayerClip = clip;
        loadingCustomAlphaThreshold = true;
        customAlphaThresholdInput.Value = clip.customAlphaThreshold;
        loadingCustomAlphaThreshold = false;
    }

    void AppendCustomCards(StringBuilder? diagnostics = null)
    {
        try
        {
            CustomShowStore store = new(customShowConfiguration.LibraryRoot);
            IReadOnlyList<ModelCard> cards = store.LoadCards(!apiOnlyMode);
            Datastore.modelcards ??= [];
            Datastore.modelcards.AddRange(cards);
            diagnostics?.AppendLine($"Custom shows: loaded={cards.Count} skipped={store.LoadWarnings.Count}");
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

    void EditCustomShow(string? showId)
    {
        using CustomShowEditorForm form = new(
            new CustomShowStore(customShowConfiguration.LibraryRoot),
            customShowConfiguration, showId);
        if (form.ShowDialog(this) == DialogResult.OK)
            ReloadCustomCards();
    }

    void ManageCustomModels()
    {
        using CustomModelManagerForm form = new(
            new CustomShowStore(customShowConfiguration.LibraryRoot));
        if (form.ShowDialog(this) == DialogResult.OK)
            ReloadCustomCards();
    }

    void ConfigureCustomShows()
    {
        using CustomShowSettingsForm form = new(customShowConfiguration);
        if (form.ShowDialog(this) != DialogResult.OK)
            return;
        customShowConfiguration = form.Configuration;
        customShowConfiguration.Save();
        ReloadCustomCards();
    }

    void InstallCustomShowTools()
    {
        using CustomShowSetupOptionsForm options = new();
        if (options.ShowDialog(this) != DialogResult.OK) return;

        string script = Path.Combine(AppContext.BaseDirectory,
            "custom-shows", "setup.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show(this, "The setup script is missing:\n" + script,
                "Custom Shows", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            using CustomShowSetupForm form = new(script,
                options.InstallTransNetV2, options.InstallOmniShotCut,
                options.InstallMatAnyone2,
                options.InstallVideoMaMa, options.InstallViTMatte,
                options.InstallProPainter);
            if (form.ShowDialog(this) != DialogResult.OK) return;
            string python = CustomShowConfiguration.FindPythonExecutable();
            if (!File.Exists(python) ||
                !python.Contains("rvm-runtime", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Setup completed but its Python environment was not found.");
            customShowConfiguration.PythonExecutable = python;
            customShowConfiguration.Save();
            MessageBox.Show(this,
                "Processing tools are installed and Python is now set to:\n" + python,
                "Custom Shows", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Custom Show Setup",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void RemoveVideoWatermark()
    {
        using CustomWatermarkRemovalForm form = new(customShowConfiguration);
        form.ShowDialog(this);
    }

    void DeleteCurrentCustomShow()
    {
        string? showId = CurrentContextCustomShowId();
        ModelCard? card = showId == null ? null :
            Datastore.findCardByTag("custom:" + showId);
        if (showId == null || card == null || MessageBox.Show(this,
                $"Move '{card.outfit}' to the Recycle Bin?",
                "Delete Custom Show", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        new CustomShowStore(customShowConfiguration.LibraryRoot).DeleteShow(showId);
        ReloadCustomCards();
    }

    bool RequestAnimationPlayback(string animationPath)
    {
        if (animationPath.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            return StartCustomPlayback(animationPath);
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
        int size = previous?.SizePercent ?? (playerMode == 1 ? 80 : 40);
        CustomPlayerForm player = new(clip.customForegroundPath,
            clip.customAlphaPath, size,
            CustomPlayerVolume(playerMode == 1), clip.customAlphaThreshold,
            customShowConfiguration.FullOpacityThreshold,
            startMs: clip.customStartMs,
            endMs: clip.customEndMs, initialBounds: initialBounds);
        player.SetLocked(playerlocked);
        player.SetClickThroughLocked(Properties.Settings.Default.ClickThroughLockedPlayer);
        player.SetWheelResize(Properties.Settings.Default.EnablePlayerWheelResize);
        player.VolumeChanged += percent => BeginInvoke(() =>
        {
            if (customPlayer == player)
                SetCustomPlayerVolume(playerMode == 1, percent, false);
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
        previous?.ClosePlayer();
        player.PlaybackCompleted += (_, _) =>
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
                GetNextClip(null, animationPath);
                if (customPlayer == null &&
                    string.IsNullOrEmpty(customPendingIstripperAnimation))
                    RestoreIStripperAfterCustomPlayback();
            });
        };
        player.PlaybackFailed += (_, _) => BeginInvoke(() =>
        {
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
