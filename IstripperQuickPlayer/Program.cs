using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
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
        {      // ***this line is added***
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
                args[0] == "--verify-overlay-defaults-layout")
            {
                ApplicationConfiguration.Initialize();
                Environment.ExitCode =
                    OverlayDefaultsForm.VerifyLayout() ? 0 : 1;
                return;
            }
            if (args.Length == 1 &&
                args[0] == "--verify-partial-card-refresh")
            {
                ApplicationConfiguration.Initialize();
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
                ApplicationConfiguration.Initialize();
                Environment.ExitCode =
                    CardRenderer.VerifyRoundedCorners() ? 0 : 1;
                return;
            }
            if (args.Length == 1 &&
                args[0] == "--verify-direct-composition-overlays")
            {
                ApplicationConfiguration.Initialize();
                Environment.ExitCode =
                    DirectCompositionCardOverlayControl.Verify() ? 0 : 1;
                return;
            }
            if (args.Length == 2 && args[0] == "--verify-persistence")
            {
                Environment.ExitCode = Persistence.VerifyMigration(args[1]) ? 0 : 1;
                return;
            }
            if (args.Length == 1 && args[0] == "--verify-controls")
            {
                if (!Form1.TryParseHotKey("Control+Alt+N", out uint modifiers, out uint key) ||
                    modifiers != 0x4003 || key != (uint)Keys.N ||
                    !Form1.TryParseHotKey("Ctrl+Alt+Shift+N",
                        out modifiers, out key) ||
                    modifiers != 0x4007 || key != (uint)Keys.N ||
                    !Form1.TryParseHotKey("Win+N", out modifiers, out key) ||
                    modifiers != 0x4008 || key != (uint)Keys.N ||
                    !Form1.TryParseHotKey("Windows+Control+N",
                        out modifiers, out key) ||
                    modifiers != 0x400A || key != (uint)Keys.N)
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
                ApplicationConfiguration.Initialize();
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
            if (Environment.OSVersion.Version.Major >= 6)
                SetProcessDPIAware();

            Application.EnableVisualStyles();
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            //CultureInfo.CurrentCulture = new CultureInfo("en-GB", false);
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }

        // ***also dllimport of that function***
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

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
                    "Blur generated wallpaper backgrounds.",
                ["trackBarBlur"] =
                    "Adjust the amount of wallpaper background blur.",
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
                ["Appearance & desktop"] =
                    "Configure themes, wallpaper and minimize behaviour.",
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
}
