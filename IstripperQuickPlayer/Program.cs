using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using IStripperQuickPlayer.Interop;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace IStripperQuickPlayer
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();

            if (args.Length == 1 && args[0] == "--verify-custom-shows")
            {
                (string Name, bool Passed)[] checks =
                [
                    ("core", CustomShowStore.VerifyCoreLogic()),
                    ("integration", CustomShowStore.VerifyIntegration()),
                    ("hit testing", CustomPlayerForm.VerifyHitTesting()),
                    ("custom playback handoff", Form1.VerifyCustomPlaybackHandoff()),
                    ("cover crop", CustomShowEditorForm.VerifyCoverCrop()),
                    ("cover text", CustomShowProcessor.VerifyCoverOverlay()),
                    ("IAFD link parsing", Form1.VerifyIafdLinkParsing()),
                    ("mask frame selection", CustomMaskEditorForm.VerifyFrameSelection()),
                    ("watermark selection", CustomWatermarkRemovalForm.VerifySelection()),
                    ("staging cleanup", CustomShowEditorForm.VerifyStagingCleanup()),
                    ("clip splitting", CustomClipEditorForm.VerifyClipSplitting()),
                    ("divider handles", ClipTimelineControl.VerifyMarkerHitTesting()),
                    ("time estimate", CustomShowProcessingForm.VerifyEstimate()),
                    ("alpha review", CustomShowDecisionForm.VerifyAlphaReview()),
                    ("setup defaults", CustomShowSetupOptionsForm.VerifyDefaults()),
                    ("card index refresh", Datastore.VerifyTagIndexReplacement()),
                    ("optional tool detection", CustomShowProcessor.VerifyOptionalToolDetection()),
                    ("FFmpeg runtime", FfmpegCpuDecoder.VerifyRuntime())
                ];
                foreach ((string name, bool passed) in checks)
                    if (!passed) Console.Error.WriteLine($"Custom-show check failed: {name}");
                Environment.ExitCode = checks.All(check => check.Passed) ? 0 : 1;
                return;
            }

            if (args.Length == 2 && args[0] == "--verify-card-overlay")
            {
                Environment.ExitCode =
                    CardOverlayLoader.Verify(args[1]) ? 0 : 1;
                return;
            }
            if (args.Length == 2 &&
                args[0] == "--verify-card-overlay-id")
            {
                bool valid = CardOverlayLoader.VerifyOverlayId(args[1]);
                if (!valid && GlslOverlayRenderer.LastError != null)
                    Console.Error.WriteLine(GlslOverlayRenderer.LastError);
                Environment.ExitCode = valid ? 0 : 1;
                return;
            }
            if (args.Length == 1 &&
                args[0] == "--verify-all-overlays-machine")
            {
                Environment.ExitCode =
                    CardOverlayLoader.VerifyAllOverlaysMachine() ? 0 : 1;
                return;
            }
            if (args.Length == 1 &&
                args[0] == "--verify-overlay-defaults-layout")
            {
                Environment.ExitCode =
                    OverlayDefaultsForm.VerifyLayout() ? 0 : 1;
                return;
            }
            if (args.Length == 1 &&
                args[0] == "--verify-partial-card-refresh")
            {
                using Manina.Windows.Forms.ImageListView list = new()
                {
                    Size = new System.Drawing.Size(400, 300),
                    ThumbnailSize = new System.Drawing.Size(100, 150)
                };
                PartialRefreshRenderer renderer = new();
                list.SetRenderer(renderer);
                list.Items.Add(
                    new Manina.Windows.Forms.ImageListViewItem(
                        new object(), "first"));
                list.Items.Add(
                    new Manina.Windows.Forms.ImageListViewItem(
                        new object(), "second"));
                using System.Drawing.Bitmap bitmap =
                    new(list.Width, list.Height);
                list.DrawToBitmap(bitmap, list.ClientRectangle);
                renderer.DrawnItems.Clear();
                list.RefreshItems([list.Items[0]]);
                Environment.ExitCode =
                    renderer.DrawnItems.SequenceEqual([0]) ? 0 : 1;
                return;
            }
            if (args.Length == 1 &&
                args[0] == "--verify-rounded-card-corners")
            {
                Environment.ExitCode =
                    CardRenderer.VerifyRoundedCorners() ? 0 : 1;
                return;
            }
            if (args.Length == 1 &&
                args[0] == "--verify-direct-composition-overlays")
            {
                Environment.ExitCode =
                    DirectCompositionCardOverlayControl.Verify() ? 0 : 1;
                return;
            }
            if (args.Length == 1 &&
                args[0] == "--verify-library-cleaner")
            {
                Environment.ExitCode =
                    Form1.VerifyLibraryCleaner() ? 0 : 1;
                return;
            }
            if (args.Length == 2 && args[0] == "--verify-persistence")
            {
                Environment.ExitCode = Persistence.VerifyMigration(args[1]) ? 0 : 1;
                return;
            }
            if (args.Length == 1 &&
                args[0] == "--verify-metadata-refresh")
            {
                Environment.ExitCode =
                    MetadataRefresh.VerifyValidation() &&
                    MetadataDiagnostics.VerifyPrivacy() &&
                    ModelsLstLoader.VerifyMetadataFallbackDiagnostics()
                        ? 0 : 1;
                return;
            }
            if (args.Length == 1 && args[0] == "--verify-controls")
            {
                if (!AreDpiAwarenessContextsEqual(
                        GetThreadDpiAwarenessContext(), new IntPtr(-4)) ||
                    !LockStateOverlay.IsMovieWindowCandidate(
                        "Qt51519QWindowToolSaveBitsOwnDC", 0x80000, false) ||
                    LockStateOverlay.IsMovieWindowCandidate(
                        "Qt51519QWindowToolSaveBitsOwnDC", 0x80, false) ||
                    LockStateOverlay.IsMovieWindowCandidate(
                        "Qt51519QWindowToolSaveBitsOwnDC", 0x80000, true) ||
                    !Form1.IsIncompleteCatalogueReload(299, 1, 314) ||
                    Form1.IsIncompleteCatalogueReload(299, 299, 314) ||
                    Form1.IsIncompleteCatalogueReload(299, 1, 1) ||
                    !Form1.TryParseHotKey("Control+Alt+N", out uint modifiers, out uint key) ||
                    modifiers != 0x4003 || key != (uint)Keys.N ||
                    !Form1.TryParseHotKey("Ctrl+Alt+Shift+N",
                        out modifiers, out key) ||
                    modifiers != 0x4007 || key != (uint)Keys.N ||
                    !Form1.TryParseHotKey("Win+N", out modifiers, out key) ||
                    modifiers != 0x4008 || key != (uint)Keys.N ||
                    !Form1.TryParseHotKey("Windows+Control+N",
                        out modifiers, out key) ||
                    modifiers != 0x400A || key != (uint)Keys.N ||
                    !PlaybackBridgeClient.VerifyProtocol())
                {
                    Environment.ExitCode = 1;
                    return;
                }
                if (Form1.RestoreRequiresLayout(
                        new System.Drawing.Size(100, 100),
                        new System.Drawing.Size(100, 100),
                        96, 96, "DISPLAY1", "DISPLAY1") ||
                    !Form1.RestoreRequiresLayout(
                        new System.Drawing.Size(100, 100),
                        new System.Drawing.Size(101, 100),
                        96, 96, "DISPLAY1", "DISPLAY1"))
                {
                    Environment.ExitCode = 1;
                    return;
                }
                if (Form1.CardThumbnailSize(96, 1) !=
                        new System.Drawing.Size(108, 161) ||
                    Form1.CardThumbnailSize(144, 1) !=
                        new System.Drawing.Size(162, 242) ||
                    Form1.CardThumbnailSize(192, 1) !=
                        new System.Drawing.Size(216, 323) ||
                    !CardRenderer.VerifyRelativeMetrics() ||
                    !Wallpaper.VerifyBlurBufferReuse())
                {
                    Environment.ExitCode = 1;
                    return;
                }
                FilterSettings filter = new();
                FilterSettings sameFilter =
                    (FilterSettings)filter.Clone();
                if (!filter.HasSameValues(sameFilter))
                {
                    Environment.ExitCode = 1;
                    return;
                }
                sameFilter.minWaist++;
                if (filter.HasSameValues(sameFilter))
                {
                    Environment.ExitCode = 1;
                    return;
                }
                using System.ComponentModel.Container tooltipComponents = new();
                using Panel tooltipPanel = new();
                using Button tooltipButton = new() { Text = "Test action" };
                using MenuStrip tooltipMenu = new();
                ToolStripMenuItem tooltipMenuItem = new("Dark Mode")
                {
                    Name = "darkModeToolStripMenuItem"
                };
                tooltipMenu.Items.Add(tooltipMenuItem);
                tooltipPanel.Controls.Add(tooltipButton);
                tooltipPanel.Controls.Add(tooltipMenu);
                using ToolTip tooltip = TooltipManager.Attach(
                    tooltipPanel, tooltipComponents, 200);
                if (tooltip.InitialDelay != 200 ||
                    tooltip.GetToolTip(tooltipButton) != "Test action" ||
                    tooltipMenuItem.ToolTipText?.Contains(
                        "dark colour theme",
                        StringComparison.OrdinalIgnoreCase) != true)
                {
                    Environment.ExitCode = 1;
                    return;
                }
                TooltipManager.SetDelay(tooltip, -1);
                if (tooltip.Active)
                {
                    Environment.ExitCode = 1;
                    return;
                }
                using Hotkeys hotkeys = new();
                using Form1 mainWindow = new();
                mainWindow.CreateControl();
                mainWindow.PerformLayout();
                string[] responsiveRows =
                [
                    "librarySearchRow", "librarySortRow",
                    "nowPlayingActions", "playbackRow", "clipFilterRow"
                ];
                TableLayoutPanel nowPlayingRow =
                    (TableLayoutPanel)mainWindow.Controls.Find(
                        "nowPlayingRow", true).Single();
                FlowLayoutPanel nowPlayingActions =
                    (FlowLayoutPanel)mainWindow.Controls.Find(
                        "nowPlayingActions", true).Single();
                Button panicResume = (Button)mainWindow.Controls.Find(
                    "panicResumeButton", true).Single();
                Button showModel = (Button)mainWindow.Controls.Find(
                    "cmdShowModel", true).Single();
                Label[] queueLabels =
                [
                    (Label)mainWindow.Controls.Find(
                        "manualQueueLabel", true).Single(),
                    (Label)mainWindow.Controls.Find(
                        "automaticQueueLabel", true).Single()
                ];
                Button queueHeader = (Button)mainWindow.Controls.Find(
                    "playQueueHeader", true).Single();
                TableLayoutPanel queueLayout =
                    (TableLayoutPanel)mainWindow.Controls.Find(
                        "playQueueLayout", true).Single();
                Control queueBody = mainWindow.Controls.Find(
                    "playQueueBody", true).Single();
                Button queueReload = (Button)mainWindow.Controls.Find(
                    "automaticQueueRefreshButton", true).Single();
                Control description = mainWindow.Controls.Find(
                    "txtDescription", true).Single();
                Control photos = mainWindow.Controls.Find(
                    "cmdPhotos", true).Single();
                SplitContainer responsiveSplit = (SplitContainer)
                    mainWindow.Controls.Find(
                        "splitContainer1", true).Single();
                Control responsiveCardList = mainWindow.Controls.Find(
                    "listModelsNew", true).Single();
                Control librarySortRow = mainWindow.Controls.Find(
                    "librarySortRow", true).Single();
                Control detailsLayout = mainWindow.Controls.Find(
                    "detailsLayout", true).Single();
                Control playbackTime = mainWindow.Controls.Find(
                    "lblPlaybackTime", true).Single();
                BufferedLabel bufferedPlaybackTime =
                    (BufferedLabel)playbackTime;
                bufferedPlaybackTime.SetDisplayText("1:23 / 4:56");
                Control queueGrip = mainWindow.Controls.Find(
                    "playQueueResizeGrip", true).Single();
                Button filterButton = (Button)mainWindow.Controls.Find(
                    "cmdFilter", true).Single();
                Button playPause = (Button)mainWindow.Controls.Find(
                    "cmdPlayPause", true).Single();
                ComboBox sortBy = (ComboBox)mainWindow.Controls.Find(
                    "cmbSortBy", true).Single();
                MenuStrip mainMenu = mainWindow.MainMenuStrip!;
                ToolStripItem[] menuItems = AllMenuItems(
                    mainMenu.Items).ToArray();
                int menuFontHeight =
                    mainMenu.Font.Height;
                TrackBarMenuItem[] endpointSliders = menuItems
                    .OfType<TrackBarMenuItem>()
                    .Where(item => item.Name is
                        "trackBarZoomOnHover" or
                        "trackbarWallpaperBrightness" or
                        "trackBarBlur")
                    .ToArray();
                TrackBarMenuItem[] menuSliders = menuItems
                    .OfType<TrackBarMenuItem>().ToArray();
                ToolStripMenuItem minimumClipSize = menuItems
                    .OfType<ToolStripMenuItem>()
                    .Single(item => (item.Text ?? "").StartsWith(
                        "Minimum clip size:", StringComparison.Ordinal));
                ToolStripMenuItem blurImage = menuItems
                    .OfType<ToolStripMenuItem>()
                    .Single(item => item.Name ==
                        "blurImageToolStripMenuItem");
                foreach (ToolStripDropDownItem item in
                    menuItems.OfType<ToolStripDropDownItem>())
                    item.DropDown.CreateControl();
                foreach (ToolStripControlHost host in
                    menuItems.OfType<ToolStripControlHost>())
                    host.Control.CreateControl();
                bool monitorMenuFontsValid = true;
                foreach (Screen screen in Screen.AllScreens)
                {
                    using Form1 probe = new()
                    {
                        StartPosition = FormStartPosition.Manual,
                        Location = new System.Drawing.Point(
                            screen.WorkingArea.Left + 20,
                            screen.WorkingArea.Top + 20)
                    };
                    probe.CreateControl();
                    MenuStrip probeMenu = probe.MainMenuStrip!;
                    ToolStripItem[] probeItems = AllMenuItems(
                        probeMenu.Items).ToArray();
                    monitorMenuFontsValid &=
                        probeItems
                            .Where(item =>
                                item is not ToolStripSeparator)
                            .All(item => Math.Abs(
                                item.Font.SizeInPoints -
                        probeMenu.Font.SizeInPoints) < 0.01f);
                }
                responsiveSplit.Dock = DockStyle.None;
                responsiveSplit.Size =
                    new System.Drawing.Size(1800, 800);
                responsiveSplit.SplitterDistance = 900;
                responsiveSplit.PerformLayout();
                if (mainWindow.AutoScaleMode != AutoScaleMode.Dpi ||
                    mainWindow.Padding.Top <= 0 ||
                    queueHeader.Height < queueHeader.Font.Height +
                        queueHeader.Padding.Vertical + 2 ||
                    queueLayout.GetRow(queueHeader) != 1 ||
                    queueLayout.GetRow(queueBody) != 2 ||
                    queueGrip.Height < 5 ||
                    responsiveCardList.Width >
                        responsiveSplit.Panel1.ClientSize.Width ||
                    responsiveCardList.Parent != librarySortRow.Parent ||
                    responsiveCardList.Top - librarySortRow.Bottom > 8 ||
                    detailsLayout.Width >
                        responsiveSplit.Panel2.ClientSize.Width ||
                    playbackTime is not BufferedLabel ||
                    bufferedPlaybackTime.BackColor != Color.Transparent ||
                    bufferedPlaybackTime.TextAlign !=
                        ContentAlignment.MiddleLeft ||
                    bufferedPlaybackTime.Text.Length != 0 ||
                    bufferedPlaybackTime.GetDisplayText() != "1:23 / 4:56" ||
                    bufferedPlaybackTime.MinimumSize.Width !=
                        TextRenderer.MeasureText(
                            "1:23 / 4:56", bufferedPlaybackTime.Font,
                            System.Drawing.Size.Empty,
                            TextFormatFlags.NoPadding).Width + 2 ||
                    queueLabels.Any(label => label.Height <
                        label.Font.Height + label.Padding.Vertical + 2) ||
                    queueReload.Width <= 0 ||
                    queueReload.Text != "\uE72C" ||
                    queueReload.Parent?.Name != "automaticQueueHeader" ||
                    queueReload.Parent.Controls.GetChildIndex(
                        queueReload) <=
                    queueReload.Parent.Controls.GetChildIndex(
                        queueLabels[1]) ||
                    nowPlayingRow.GetColumn(nowPlayingActions) != 1 ||
                    nowPlayingRow.ColumnStyles[0].SizeType !=
                        SizeType.Percent ||
                    new[] { "cmdWallpaper", "cmdNextClip", "cmdShowModel" }
                        .Any(name =>
                            nowPlayingActions.Controls.Find(
                                name, false).Length != 1) ||
                    !panicResume.AutoSize ||
                    panicResume.Width <
                        panicResume.GetPreferredSize(
                            System.Drawing.Size.Empty).Width ||
                    nowPlayingActions.Controls.GetChildIndex(
                        panicResume) <=
                    nowPlayingActions.Controls.GetChildIndex(
                        showModel) ||
                    description.Height <= 0 ||
                    description.Parent?.Name != "modelDetailsLayout" ||
                    photos.Parent?.Name != "clipListHost" ||
                    filterButton.Height < filterButton.Font.Height +
                        filterButton.Padding.Vertical ||
                    playPause.Width <
                        playPause.GetPreferredSize(
                            System.Drawing.Size.Empty).Width ||
                    sortBy.Width < sortBy.Items.Cast<string>()
                        .Max(item => TextRenderer.MeasureText(
                            item, sortBy.Font).Width) +
                        SystemInformation.VerticalScrollBarWidth ||
                    menuItems
                        .Where(item => item is not ToolStripSeparator)
                        .Any(item => item.Font.Height != menuFontHeight) ||
                    menuItems.OfType<ToolStripControlHost>()
                        .Any(host =>
                            host.Control.Font.Height != menuFontHeight) ||
                    endpointSliders.Length != 3 ||
                    endpointSliders.Any(slider =>
                        slider.ScaleDivisions != 1 ||
                        slider.TickColor.A != 0 ||
                        slider.SliderPadding <
                            TextRenderer.MeasureText(
                                slider.Maximum.ToString(
                                    CultureInfo.CurrentCulture),
                                slider.Control.Font).Width / 2) ||
                    blurImage.CheckOnClick ||
                    menuSliders.Any(slider =>
                        slider.Control.Height <
                            slider.Control.Font.Height * 3 + 8) ||
                    menuItems.OfType<ToolStripDropDownItem>()
                        .Select(item => item.DropDown)
                        .OfType<ToolStripDropDownMenu>()
                        .Any(menu => menu.ShowImageMargin ||
                            !menu.ShowCheckMargin) ||
                    menuItems.OfType<ToolStripMenuItem>()
                        .Any(item => item.Owner is ToolStripDropDown &&
                            item.Padding != Padding.Empty) ||
                    minimumClipSize.DropDownItems
                        .OfType<ToolStripControlHost>().Any() ||
                    !minimumClipSize.DropDownItems
                        .Cast<ToolStripMenuItem>()
                        .Select(item => (long)item.Tag!)
                        .SequenceEqual(Enumerable.Range(0, 9)
                            .Select(value => (long)value * 5)) ||
                    !monitorMenuFontsValid ||
                    (mainWindow.listClips.Items.Count > 0 &&
                        photos.Height !=
                        mainWindow.listClips.GetItemRect(0).Top) ||
                    responsiveRows.Select(name =>
                            mainWindow.Controls.Find(name, true)
                                .SingleOrDefault())
                        .Any(row => row is not FlowLayoutPanel flow ||
                            FlowHasOverlaps(flow)))
                {
                    Environment.ExitCode = 1;
                    return;
                }
                using ImageView imageView = new();
                using System.Drawing.Bitmap image = new(1, 1);
                imageView.LoadImage(image);
                using Manina.Windows.Forms.ImageListView cardList = new();
                cardList.Size = new System.Drawing.Size(320, 240);
                using System.Drawing.Bitmap cardListImage =
                    new(cardList.Width, cardList.Height);
                cardList.DrawToBitmap(cardListImage,
                    cardList.ClientRectangle);
                cardList.DrawToBitmap(cardListImage,
                    cardList.ClientRectangle);
                return;
            }
            //CultureInfo.CurrentCulture = new CultureInfo("en-GB", false);
            if (args.Length == 1 && args[0] == "--verify-api-options")
            {
                Environment.ExitCode = AppOptions.VerifyParser() ? 0 : 1;
                return;
            }
            if (!AppOptions.TryParse(args, out AppOptions options,
                    out string optionError))
            {
                Console.Error.WriteLine(optionError);
                Environment.ExitCode = 2;
                return;
            }

            using Mutex instanceMutex = new(true,
                @"Local\IStripperQuickPlayer.SingleInstance.v1",
                out bool firstInstance);
            if (!firstInstance)
            {
                Console.Error.WriteLine(
                    "Another iStripper QuickPlayer instance is already running.");
                Environment.ExitCode = 3;
                return;
            }

            Application.Run(new Form1(options));
        }

        private sealed class PartialRefreshRenderer :
            Manina.Windows.Forms.ImageListView.ImageListViewRenderer
        {
            internal List<int> DrawnItems { get; } = [];

            public override void DrawItem(
                Graphics graphics,
                Manina.Windows.Forms.ImageListViewItem item,
                Manina.Windows.Forms.ItemState state,
                Rectangle bounds)
            {
                DrawnItems.Add(item.Index);
                graphics.FillRectangle(Brushes.Black, bounds);
            }
        }

        private static bool FlowHasOverlaps(FlowLayoutPanel flow)
        {
            Control[] visible = flow.Controls.Cast<Control>()
                .Where(control => control.Visible).ToArray();
            for (int first = 0; first < visible.Length; first++)
            {
                for (int second = first + 1;
                     second < visible.Length; second++)
                {
                    if (visible[first].Bounds.IntersectsWith(
                            visible[second].Bounds))
                        return true;
                }
            }
            return false;
        }

        private static IEnumerable<ToolStripItem> AllMenuItems(
            ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                yield return item;
                if (item is ToolStripDropDownItem dropDown)
                {
                    foreach (ToolStripItem child in
                        AllMenuItems(dropDown.DropDownItems))
                        yield return child;
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetThreadDpiAwarenessContext();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool AreDpiAwarenessContextsEqual(
            IntPtr dpiContextA, IntPtr dpiContextB);
    }

    internal static partial class TooltipManager
    {
        private const string HotkeySyntax =
            "Allowed modifiers: Ctrl (or Control), Alt, Shift, Win (or Windows). Separate keys with +, for example ";

        private static readonly Dictionary<string, string> Descriptions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["listModelsNew"] =
                    "Select a card to view its clips; double-click to play it.",
                ["listClips"] =
                    "Select a clip to play it, or drag it into the manual queue.",
                ["txtSearch"] =
                    "Search cards by model, title, description, tags, or search fields.",
                ["cmdClearSearch"] = "Clear the card search.",
                ["cmbSortBy"] = "Choose how cards are sorted.",
                ["cmbSortDirection"] = "Choose ascending or descending card order.",
                ["chkFavourite"] = "Show only cards marked as favourites.",
                ["cmdFilter"] = "Open the detailed card filter.",
                ["cmbFilter"] = "Select a saved card filter.",
                ["txtUserTags"] = "Add your own searchable tags to this card.",
                ["txtDescription"] = "View the selected card's description.",
                ["cmdPhotos"] = "View photos for the selected card.",
                ["cmdWallpaper"] = "Create desktop wallpaper from the current card.",
                ["cmdNextClip"] = "Play the next clip or queued item.",
                ["cmdShowModel"] = "Filter the card list to the current model.",
                ["cmdRewind"] = "Move playback backward by 10%.",
                ["cmdPlayPause"] = "Pause or resume playback.",
                ["cmdFastForward"] = "Move playback forward by 10%.",
                ["cmbPlaybackSpeed"] = "Choose the playback speed.",
                ["trkPlaybackPosition"] = "View or seek within the current clip.",
                ["numMinSizeMB"] = "Exclude clips smaller than this file size.",
                ["txtClipType"] = "Filter clips by text in their clip type.",
                ["chkPublic"] = "Include clips marked as public.",
                ["chkNoNudity"] = "Include clips with no nudity.",
                ["chkTopless"] = "Include topless clips.",
                ["chkNudity"] = "Include nude clips.",
                ["chkFullNudity"] = "Include full-nudity clips.",
                ["chkXXX"] = "Include explicit XXX clips.",
                ["chkDemo"] = "Include demo clips.",
                ["listView1"] =
                    "Browse the selected card's photos; double-click to open one.",

                ["cmdApply"] = "Apply these filter values without closing.",
                ["cmdOK"] = "Apply the current values and close this window.",
                ["txtTags"] =
                    "Require card tags using OR, AND and ! expressions.",
                ["cmdRevert"] =
                    "Restore this saved filter's values and apply them.",
                ["cmdSaveDefault"] =
                    "Save these values as the default card filter.",
                ["cmdSaveAs"] = "Save these values as a named card filter.",
                ["button1"] = "Delete the selected named filter.",
                ["dateTimePickerMin"] =
                    "Include cards released on or after this date.",
                ["dateTimePickerMax"] =
                    "Include cards released on or before this date.",
                ["chkIStripperClassic"] =
                    "Include iStripper Classic collection cards.",
                ["chkDeskBabes"] = "Include Desk Babes collection cards.",
                ["chkVGClassic"] = "Include VG Classic collection cards.",
                ["chkIStripper"] = "Include iStripper collection cards.",
                ["chkIStripperXXX"] = "Include iStripper XXX collection cards.",
                ["chkNormal"] = "Include normal-edition cards.",
                ["chkSpecial"] = "Include special-edition cards.",
                ["chkVirtuaGuy"] = "Include VirtuaGuy cards.",
                ["chkTradingCard"] = "Include Trading Card collection cards.",

                ["chkNextClip"] = "Enable the global Next clip hotkey.",
                ["chkNextCard"] = "Enable the global Next card hotkey.",
                ["chkToggleLock"] = "Enable the global player-lock hotkey.",
                ["chkPause"] = "Enable the global Pause / Play hotkey.",
                ["chkRewind"] = "Enable the global Back 10% hotkey.",
                ["chkFastForward"] = "Enable the global Forward 10% hotkey.",
                ["chkRestartClip"] = "Enable the global Restart clip hotkey.",
                ["chkLargePlayer"] = "Enable the global Large animation hotkey.",
                ["chkSmallPlayer"] = "Enable the global Small animation hotkey.",
                ["chkNowPlayingInfo"] =
                    "Enable the global Now Playing Info overlay hotkey.",
                ["chkPanic"] = "Enable the global Panic / Resume hotkey.",
                ["txtNextClip"] =
                    HotkeySyntax + "Ctrl+Alt+N.",
                ["txtNextCard"] =
                    HotkeySyntax + "Ctrl+Alt+C.",
                ["txtToggleLock"] =
                    HotkeySyntax + "Ctrl+Alt+L.",
                ["txtPause"] =
                    HotkeySyntax + "Ctrl+Alt+P.",
                ["txtRewind"] =
                    HotkeySyntax + "Ctrl+Alt+Left.",
                ["txtFastForward"] =
                    HotkeySyntax + "Ctrl+Alt+Right.",
                ["txtRestartClip"] =
                    HotkeySyntax + "Ctrl+Alt+Home.",
                ["txtLargePlayer"] =
                    HotkeySyntax + "Ctrl+Alt+Up.",
                ["txtSmallPlayer"] =
                    HotkeySyntax + "Ctrl+Alt+Down.",
                ["txtNowPlayingInfo"] =
                    HotkeySyntax + "Ctrl+Alt+I.",
                ["txtPanic"] =
                    HotkeySyntax + "Ctrl+Alt+X."
            };

        private static readonly Dictionary<string, string> MenuDescriptions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["fileToolStripMenuItem"] =
                    "Open library, queue, backup and update actions.",
                ["reloadModelslstToolStripMenuItem"] =
                    "Reload installed cards and clips from iStripper's catalogue.",
                ["exportFiltersToolStripMenuItem"] =
                    "Save all named card filters to a file.",
                ["loadPlaylistToolStripMenuItem"] =
                    "Load an iStripper playlist into the card search.",
                ["importFiltersToolStripMenuItem"] =
                    "Import named card filters from a previously exported file.",
                ["exitToolStripMenuItem"] =
                    "Close QuickPlayer and restore the original desktop state.",
                ["settingsToolStripMenuItem"] =
                    "Configure hotkeys, playback, library and appearance.",
                ["hotkeysToolStripMenuItem"] =
                    "Configure system-wide QuickPlayer keyboard shortcuts.",
                ["lockPlayerToolStripMenuItem"] =
                    "Prevent animation windows from being moved or interacted with.",
                ["zoomOnHoverToolStripMenuItem"] =
                    "Choose how much a card enlarges when the pointer is over it.",
                ["trackBarZoomOnHover"] =
                    "Adjust card enlargement while hovering.",
                ["menuShowRatingsStars"] =
                    "Draw your personal rating as stars over each card.",
                ["includeDescriptionInSearchToolStripMenuItem"] =
                    "Include card descriptions in unqualified text searches.",
                ["includeShowTitleInSearchToolStripMenuItem"] =
                    "Include show titles in unqualified text searches.",
                ["wallpaperToolStripMenuItem"] =
                    "Configure wallpaper generation from the current card.",
                ["automaticWallpaperToolStripMenuItem"] =
                    "Generate new wallpaper when the playing card changes.",
                ["trackbarWallpaperBrightness"] =
                    "Adjust the brightness of generated wallpaper images.",
                ["showTextToolStripMenuItem"] =
                    "Draw model and show details on generated wallpaper.",
                ["wallpaperTextSizeToolStripMenuItem"] =
                    "Adjust generated wallpaper label text size.",
                ["trackBarWallpaperTextSize"] =
                    "Set wallpaper label text from 10 to 70 pixels.",
                ["wallpaperLabelOpacityToolStripMenuItem"] =
                    "Adjust the transparency of wallpaper label backgrounds.",
                ["trackBarWallpaperLabelOpacity"] =
                    "Set wallpaper label opacity from transparent to opaque.",
                ["blurImageToolStripMenuItem"] =
                    "Set wallpaper blur; zero disables it.",
                ["trackBarBlur"] =
                    "Set wallpaper blur from off to maximum.",
                ["hideDesktopIconsToolStripMenuItem"] =
                    "Hide desktop icons while QuickPlayer wallpaper is active.",
                ["showKittyToolStripMenuItem"] =
                    "Include the built-in Kitty card in the library.",
                ["minimizeToTrayToolStripMenuItem"] =
                    "Hide QuickPlayer in the notification area when minimized.",
                ["darkModeToolStripMenuItem"] =
                    "Use the dark colour theme throughout QuickPlayer.",

                ["menuCardFavourite"] =
                    "Add or remove this card from your favourites.",
                ["nameToolStripMenuItem"] =
                    "Open this model's page on the iStripper website.",
                ["outfitToolStripMenuItem"] =
                    "Open this card's page on the iStripper website.",
                ["ratingSlider"] =
                    "Set your personal rating from 0 to 5 stars.",
                ["addModelToFilterToolStripMenuItem"] =
                    "Add this model to the current card search.",
                ["ratingToolStripMenuItem"] =
                    "The card's official community rating.",
                ["hotnessToolStripMenuItem"] =
                    "The explicitness category assigned to this card.",
                ["statsToolStripMenuItem"] =
                    "The model's bust, waist and hip measurements.",
                ["ageToolStripMenuItem"] =
                    "The model's age recorded for this card.",
                ["hairToolStripMenuItem"] =
                    "The model's recorded hair colour.",
                ["purchasedToolStripMenuItem"] =
                    "The date this card was added to the account.",
                ["deleteFromDiskToolStripMenuItem"] =
                    "Delete this card's downloaded files from local storage.",
                ["copyToolStripMenuItem"] =
                    "Copy the displayed image to the Windows clipboard.",
                ["saveAsToolStripMenuItem"] =
                    "Save the displayed image to a file.",

                ["Library Health Check"] =
                    "Check installed clip files and write a diagnostic report.",
                ["Refresh Card Metadata"] =
                    "Download fresh model and release metadata, then reload the library.",
                ["Backup QuickPlayer Data"] =
                    "Save settings, filters, metadata and playback history to one file.",
                ["Restore QuickPlayer Data"] =
                    "Replace QuickPlayer data from a backup and restart the app.",
                ["Check for Updates"] =
                    "Check GitHub for a newer QuickPlayer installer.",
                ["Click through locked player"] =
                    "Pass mouse input through locked animation windows to apps behind them.",
                ["Resize player with mouse wheel"] =
                    "Resize an animation by scrolling while the pointer is over it.",
                ["Library & search"] =
                    "Configure card display and searchable library fields.",
                ["Appearance"] =
                    "Configure player sizing, themes, wallpaper and minimize behaviour.",
                ["Player size by clip type"] =
                    "Set separate small and large player sizes for standing, table, pole, swing and cage clips.",
                ["Tooltip delay"] =
                    "Choose whether tooltips appear and how quickly they open.",
                ["Queue"] = "Save, load or clear the manual play-next queue.",
                ["Save Queue"] = "Save the manual queue to an .iqpq file.",
                ["Load Queue"] =
                    "Replace the manual queue with entries from an .iqpq file.",
                ["Clear Queue"] =
                    "Remove every entry from the manual play-next queue."
            };

        internal static ToolTip Attach(Form form, int delay)
        {
            Container components = new();
            form.FormClosed += (_, _) => components.Dispose();
            return Attach(form, components, delay);
        }

        internal static ToolTip Attach(Control root, IContainer components,
            int delay)
        {
            ToolTip tooltip = new(components)
            {
                AutoPopDelay = 10_000,
                ShowAlways = true,
                UseAnimation = true,
                UseFading = true
            };
            SetDelay(tooltip, delay);
            HashSet<Control> controls = [];
            HashSet<ToolStrip> strips = [];

            void AddItem(ToolStripItem item)
            {
                if (item is ToolStripSeparator)
                    return;
                string label = CleanText(item.Text);
                if (MenuDescriptions.TryGetValue(item.Name,
                        out string? description) ||
                    MenuDescriptions.TryGetValue(label, out description))
                {
                    item.ToolTipText = description;
                }
                else if (string.IsNullOrWhiteSpace(item.ToolTipText))
                    item.ToolTipText = label;
                item.AutoToolTip = true;
                if (item is ToolStripControlHost host)
                    AddControl(host.Control);
                if (item is ToolStripDropDownItem dropDown &&
                    dropDown.DropDownItems.Count > 0)
                    AddStrip(dropDown.DropDown);
            }

            void AddStrip(ToolStrip strip)
            {
                if (!strips.Add(strip))
                    return;
                strip.ShowItemToolTips = delay >= 0;
                foreach (ToolStripItem item in strip.Items)
                    AddItem(item);
                strip.ItemAdded += (_, e) => AddItem(e.Item);
            }

            void AddControl(Control control)
            {
                if (!controls.Add(control))
                    return;
                string text = TooltipText(control);
                if (!string.IsNullOrWhiteSpace(text))
                    tooltip.SetToolTip(control, text);
                if (control is ToolStrip strip)
                    AddStrip(strip);
                if (control.ContextMenuStrip != null)
                    AddStrip(control.ContextMenuStrip);
                foreach (Control child in control.Controls)
                    AddControl(child);
                control.ControlAdded += (_, e) => AddControl(e.Control);
            }

            AddControl(root);
            return tooltip;
        }

        internal static void SetDelay(ToolTip tooltip, int delay)
        {
            int safeDelay = NormalizeDelay(delay);
            tooltip.Active = safeDelay >= 0;
            if (safeDelay >= 0)
            {
                tooltip.InitialDelay = safeDelay;
                tooltip.ReshowDelay = Math.Min(100, safeDelay);
            }
        }

        internal static int NormalizeDelay(int delay) =>
            delay < 0 ? -1 : Math.Clamp(delay, 0, 5_000);

        internal static void SetToolStripTips(Control root, bool enabled)
        {
            HashSet<ToolStrip> strips = [];

            void ApplyStrip(ToolStrip strip)
            {
                if (!strips.Add(strip))
                    return;
                strip.ShowItemToolTips = enabled;
                foreach (ToolStripDropDownItem item in strip.Items
                             .OfType<ToolStripDropDownItem>())
                {
                    if (item.DropDownItems.Count > 0)
                        ApplyStrip(item.DropDown);
                }
            }

            void ApplyControl(Control control)
            {
                if (control is ToolStrip strip)
                    ApplyStrip(strip);
                if (control.ContextMenuStrip != null)
                    ApplyStrip(control.ContextMenuStrip);
                foreach (Control child in control.Controls)
                    ApplyControl(child);
            }

            ApplyControl(root);
        }

        private static string TooltipText(Control control)
        {
            if (!string.IsNullOrWhiteSpace(control.AccessibleDescription))
                return control.AccessibleDescription;
            if (Descriptions.TryGetValue(control.Name, out string? description))
                return description;
            if (!string.IsNullOrWhiteSpace(control.AccessibleName))
                return control.AccessibleName;

            string name = HumanizeName(control.Name);
            return control switch
            {
                ButtonBase button => CleanText(button.Text),
                TextBoxBase => $"Enter or edit {name.ToLowerInvariant()}.",
                ComboBox => $"Select {name.ToLowerInvariant()}.",
                ListControl or ListView or DataGridView =>
                    $"View and select {name.ToLowerInvariant()}.",
                NumericUpDown or TrackBar or DateTimePicker =>
                    $"Adjust {name.ToLowerInvariant()}.",
                _ => ""
            };
        }

        private static string HumanizeName(string name)
        {
            string value = PrefixRegex().Replace(name ?? "", "");
            value = WordRegex().Replace(value, "$1 $2").Trim();
            return string.IsNullOrWhiteSpace(value) ? "this value" : value;
        }

        private static string CleanText(string? text) =>
            (text ?? "").Replace("&", "").Replace("...", "").Trim();

        [GeneratedRegex("^(cmd|btn|chk|txt|cmb|lbl|trk|num|list|menu|panel)",
            RegexOptions.IgnoreCase)]
        private static partial Regex PrefixRegex();

        [GeneratedRegex("([a-z0-9])([A-Z])")]
        private static partial Regex WordRegex();
    }

    internal static class AppTheme
    {
        private static readonly Color DarkBackground =
            Color.FromArgb(48, 48, 48);
        private static readonly Color DarkSurface =
            Color.FromArgb(58, 58, 58);
        private static readonly Color DarkBorder =
            Color.FromArgb(90, 90, 90);
        private static readonly Color DarkText = Color.AntiqueWhite;
        private static readonly Color DarkDisabledText =
            Color.FromArgb(175, 175, 175);

        internal static void Apply(Form form)
        {
            form.HandleCreated -= Form_HandleCreated;
            form.HandleCreated += Form_HandleCreated;
            Apply((Control)form);
            SetDarkTitleBar(form);
        }

        internal static void Apply(Control root)
        {
            ApplyControl(root, Properties.Settings.Default.DarkMode);
        }

        private static void ApplyControl(Control control, bool dark)
        {
            Color background = dark ? DarkBackground : SystemColors.Control;
            Color surface = dark ? DarkSurface : SystemColors.Window;
            Color foreground = dark ? DarkText : SystemColors.ControlText;

            control.ForeColor = foreground;
            switch (control)
            {
                case TextBoxBase:
                case ComboBox:
                case ListBox:
                case ListView:
                case NumericUpDown:
                    control.BackColor = surface;
                    break;
                case Button button:
                    button.Paint -= Button_Paint;
                    button.FlatStyle =
                        dark ? FlatStyle.Flat : FlatStyle.Standard;
                    button.UseVisualStyleBackColor = !dark;
                    button.BackColor = background;
                    if (dark)
                    {
                        button.FlatAppearance.BorderColor = DarkBorder;
                        button.Paint += Button_Paint;
                    }
                    break;
                case CheckBox checkBox:
                    checkBox.UseVisualStyleBackColor = !dark;
                    checkBox.BackColor = background;
                    break;
                case RadioButton radioButton:
                    radioButton.UseVisualStyleBackColor = !dark;
                    radioButton.BackColor = background;
                    break;
                case DataGridView grid:
                    ApplyGrid(grid, dark);
                    break;
                case ToolStrip strip:
                    ApplyToolStrip(strip, dark);
                    break;
                default:
                    control.BackColor = background;
                    break;
            }

            foreach (Control child in control.Controls)
                ApplyControl(child, dark);
            if (control.ContextMenuStrip != null)
                ApplyToolStrip(control.ContextMenuStrip, dark);
        }

        private static void Button_Paint(object? sender, PaintEventArgs e)
        {
            if (sender is not Button { Enabled: false } button ||
                string.IsNullOrEmpty(button.Text)) return;
            Rectangle interior = Rectangle.Inflate(button.ClientRectangle, -2, -2);
            using Brush background = new SolidBrush(button.BackColor);
            e.Graphics.FillRectangle(background, interior);
            TextRenderer.DrawText(e.Graphics, button.Text, button.Font, interior,
                DarkDisabledText, TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static void ApplyGrid(DataGridView grid, bool dark)
        {
            Color background =
                dark ? DarkBackground : SystemColors.Window;
            Color secondary = dark ? DarkSurface : SystemColors.Control;
            Color foreground = dark ? DarkText : SystemColors.ControlText;
            grid.BackgroundColor = background;
            grid.GridColor = dark ? DarkBorder : SystemColors.ControlDark;
            grid.EnableHeadersVisualStyles = false;
            grid.DefaultCellStyle.BackColor = background;
            grid.DefaultCellStyle.ForeColor = foreground;
            grid.DefaultCellStyle.SelectionBackColor =
                dark ? Color.FromArgb(55, 105, 135)
                    : SystemColors.Highlight;
            grid.DefaultCellStyle.SelectionForeColor =
                dark ? Color.White : SystemColors.HighlightText;
            grid.ColumnHeadersDefaultCellStyle.BackColor = secondary;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
            grid.RowsDefaultCellStyle.BackColor = background;
            grid.RowsDefaultCellStyle.ForeColor = foreground;
        }

        private static void ApplyToolStrip(ToolStrip strip, bool dark)
        {
            Color background =
                dark ? DarkBackground : SystemColors.Menu;
            Color foreground = dark ? DarkText : SystemColors.MenuText;
            strip.BackColor = background;
            strip.ForeColor = foreground;
            strip.Renderer = new ToolStripProfessionalRenderer(
                dark ? new DarkColorTable() : new ProfessionalColorTable())
                { RoundedEdges = false };
            foreach (ToolStripItem item in strip.Items)
                ApplyToolStripItem(
                    item, background, foreground, dark);
        }

        private static void ApplyToolStripItem(
            ToolStripItem item, Color background,
            Color foreground, bool dark)
        {
            item.BackColor = background;
            item.ForeColor = foreground;
            if (item is ToolStripControlHost host)
            {
                host.Control.Font = item.Owner?.Font ?? item.Font;
                ApplyControl(host.Control, dark);
                if (host is TrackBarMenuItem)
                {
                    int height = host.Control.Font.Height * 3 + 8;
                    host.Control.Height = height;
                    host.Height = height;
                    int endpointWidth = Math.Max(
                        TextRenderer.MeasureText(
                            ((TrackBarMenuItem)host).Minimum.ToString(
                                CultureInfo.CurrentCulture),
                            host.Control.Font).Width,
                        TextRenderer.MeasureText(
                            ((TrackBarMenuItem)host).Maximum.ToString(
                                CultureInfo.CurrentCulture),
                            host.Control.Font).Width);
                    ((TrackBarMenuItem)host).SliderPadding =
                        endpointWidth / 2 + 2;
                }
            }
            if (item is not ToolStripDropDownItem dropDown)
                return;
            dropDown.DropDown.BackColor = background;
            dropDown.DropDown.ForeColor = foreground;
            dropDown.DropDown.Renderer = new ToolStripProfessionalRenderer(
                dark ? new DarkColorTable() : new ProfessionalColorTable())
                { RoundedEdges = false };
            if (dropDown.DropDown is ToolStripDropDownMenu menu)
            {
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = true;
            }
            foreach (ToolStripItem child in dropDown.DropDownItems)
            {
                if (child is ToolStripMenuItem menuItem)
                    menuItem.Padding = Padding.Empty;
                ApplyToolStripItem(
                    child, background, foreground, dark);
            }
        }

        private static void Form_HandleCreated(
            object? sender, EventArgs e)
        {
            if (sender is Form form)
                SetDarkTitleBar(form);
        }

        private static void SetDarkTitleBar(Form form)
        {
            if (!OperatingSystem.IsWindows() || !form.IsHandleCreated)
                return;
            int enabled = Properties.Settings.Default.DarkMode ? 1 : 0;
            if (DwmSetWindowAttribute(
                    form.Handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(
                    form.Handle, 19, ref enabled, sizeof(int));
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr window, int attribute, ref int value, int size);

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected => DarkSurface;
            public override Color MenuItemBorder => DarkBorder;
            public override Color MenuItemSelectedGradientBegin =>
                DarkSurface;
            public override Color MenuItemSelectedGradientEnd =>
                DarkSurface;
            public override Color MenuItemPressedGradientBegin =>
                DarkSurface;
            public override Color MenuItemPressedGradientEnd =>
                DarkSurface;
            public override Color ToolStripDropDownBackground =>
                DarkBackground;
            public override Color ImageMarginGradientBegin =>
                DarkBackground;
            public override Color ImageMarginGradientMiddle =>
                DarkBackground;
            public override Color ImageMarginGradientEnd =>
                DarkBackground;
            public override Color SeparatorDark => DarkBorder;
            public override Color SeparatorLight => DarkSurface;
        }

    }

    internal sealed record AppOptions(bool ApiOnly, int ApiPort)
    {
        internal const int DefaultApiPort = 17871;

        internal static AppOptions Default { get; } =
            new(false, DefaultApiPort);

        internal static bool TryParse(string[] args, out AppOptions options,
            out string error)
        {
            bool apiOnly = false;
            bool apiOnlySeen = false;
            int apiPort = DefaultApiPort;
            bool apiPortSeen = false;

            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index].ToLowerInvariant())
                {
                    case "--api-only":
                        if (apiOnlySeen)
                            return Fail(
                                "'--api-only' was specified more than once.",
                                out options, out error);
                        apiOnlySeen = true;
                        apiOnly = true;
                        break;

                    case "--api-port":
                        if (apiPortSeen)
                            return Fail(
                                "'--api-port' was specified more than once.",
                                out options, out error);
                        if (++index >= args.Length ||
                            !int.TryParse(args[index], out apiPort) ||
                            apiPort is < 1 or > 65535)
                        {
                            return Fail(
                                "'--api-port' requires a number from 1 to 65535.",
                                out options, out error);
                        }
                        apiPortSeen = true;
                        break;

                    default:
                        return Fail($"Unknown argument '{args[index]}'.",
                            out options, out error);
                }
            }

            options = new(apiOnly, apiPort);
            error = "";
            return true;
        }

        private static bool Fail(string message, out AppOptions options,
            out string error)
        {
            options = Default;
            error = message;
            return false;
        }

        internal static bool VerifyParser()
        {
            return
                TryParse([], out AppOptions defaults, out _) &&
                defaults == Default &&
                TryParse(["--api-only"],
                    out AppOptions headless, out _) &&
                headless == new AppOptions(true, DefaultApiPort) &&
                TryParse(["--api-port", "18000"],
                    out AppOptions port, out _) &&
                port == new AppOptions(false, 18000) &&
                TryParse(["--api-port", "18000", "--api-only"],
                    out AppOptions combined, out _) &&
                combined == new AppOptions(true, 18000) &&
                !TryParse(["--api-port"], out _, out _) &&
                !TryParse(["--api-port", "0"], out _, out _) &&
                !TryParse(["--api-port", "65536"], out _, out _) &&
                !TryParse(["--api-only", "--api-only"], out _, out _) &&
                !TryParse(["--unknown"], out _, out _);
        }
    }
}
