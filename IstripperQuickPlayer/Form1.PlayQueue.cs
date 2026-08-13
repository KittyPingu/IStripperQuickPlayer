using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using Manina.Windows.Forms;
using Microsoft.Win32;
using System.Text.Json;

namespace IStripperQuickPlayer
{
    public partial class Form1
    {
        private const string PlayQueueDragFormat =
            "IStripperQuickPlayer.PlayQueueItem";
        private const int DefaultPlayQueueExpandedHeight = 330;
        private const int PlayQueueCollapsedHeight = 34;
        private const int QueueStartProtectionMilliseconds = 3_000;

        private sealed record PlayQueueEntry(string CardTag,
            string? ClipName = null);

        private sealed record SmartQueueCandidate(PlayQueueEntry Entry,
            int Relaxation);

        private sealed record PlayQueueDrag(string Source, int Index,
            PlayQueueEntry Entry, bool Playing = false);

        private readonly List<PlayQueueEntry> manualPlayQueue = [];
        private readonly List<PlayQueueEntry> automaticPlayQueue = [];
        private static string PreviousQueuePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IStripperQuickPlayer", "previous-queue.iqpq");
        private readonly ToolStripMenuItem enablePlayQueueToolStripMenuItem =
            new("Enable play next queue") { CheckOnClick = true };
        private readonly ToolStripMenuItem autoQueueLengthToolStripMenuItem =
            new("Automatic queue length");
        private readonly ToolStripMenuItem minimumClipSizeToolStripMenuItem =
            new("Minimum clip size");
        private readonly ToolStripMenuItem smartQueueToolStripMenuItem =
            new("Smart automatic queue");
        private readonly ToolStripMenuItem smartQueueFavouritesToolStripMenuItem =
            new("Favourites only") { CheckOnClick = true };
        private readonly ToolStripMenuItem smartQueueLeastRecentToolStripMenuItem =
            new("Favour cards played least recently")
            {
                CheckOnClick = true
            };
        private readonly ToolStripMenuItem smartQueueRotateModelsToolStripMenuItem =
            new("Avoid adjacent cards from same model")
            {
                CheckOnClick = true
            };
        private readonly ToolStripMenuItem smartQueueUnplayedClipsToolStripMenuItem =
            new("Prefer never-played clips") { CheckOnClick = true };
        private readonly ToolStripMenuItem smartQueueWeightedToolStripMenuItem =
            new("Favour favourites and higher ratings")
            {
                CheckOnClick = true
            };
        private readonly ToolStripMenuItem smartQueueNewestToolStripMenuItem =
            new("Favour newer purchases") { CheckOnClick = true };
        private readonly ToolStripMenuItem smartQueueCooldownToolStripMenuItem =
            new("Cooldown");
        private readonly ToolStripMenuItem smartQueueCooldownByModelToolStripMenuItem =
            new("Apply cooldown across whole model")
            {
                CheckOnClick = true
            };
        private readonly ToolStripLabel smartQueueIgnoreScoresLabel = new();
        private readonly Controls.PlaybackSeekBar
            smartQueueIgnoreScoresTrackBar = new()
        {
            AccessibleName = "Chance of random queue choice",
            AccessibleDescription =
                "Set the chance of ignoring favour scores and choosing randomly.",
            Minimum = 0,
            Maximum = 100,
            SmallChange = 1,
            LargeChange = 10,
            Size = new System.Drawing.Size(230, 42)
        };
        private ToolStripControlHost? smartQueueIgnoreScoresHost;
        private readonly ToolStripMenuItem requeueCompletedManualItemsToolStripMenuItem =
            new("Return completed manual items to end")
            {
                CheckOnClick = true
            };
        private readonly ToolStripMenuItem randomManualQueueSelectionToolStripMenuItem =
            new("Choose manual items randomly") { CheckOnClick = true };
        private readonly ToolStripMenuItem playbackSettingsToolStripMenuItem =
            new("Playback & queue");
        private readonly ToolStripMenuItem librarySettingsToolStripMenuItem =
            new("Library & search");
        private readonly ToolStripMenuItem appearanceSettingsToolStripMenuItem =
            new("Appearance");
        private readonly ToolStripMenuItem
            playerSizeByClipTypeToolStripMenuItem =
            new("Player size by clip type...");
        private readonly object playerSizeLock = new();
        private int? normalSmallPlayerSizePercent;
        private int? normalLargePlayerSizePercent;
        private bool smallPlayerSizeOverrideActive;
        private bool largePlayerSizeOverrideActive;
        private readonly Dictionary<string, int> manualPlayerSizes =
            new(StringComparer.OrdinalIgnoreCase);
        private long playerSizeRequestVersion;
        private readonly ToolStripMenuItem tooltipDelayToolStripMenuItem =
            new("Tooltip delay");
        private ToolTip? applicationToolTip;
        private readonly ToolStripMenuItem queueFileToolStripMenuItem =
            new("Queue");
        private readonly ToolStripMenuItem saveQueueToolStripMenuItem =
            new("Save Queue...");
        private readonly ToolStripMenuItem loadQueueToolStripMenuItem =
            new("Load Queue...");
        private readonly ToolStripMenuItem clearQueueToolStripMenuItem =
            new("Clear Queue");

        private Panel playQueuePanel = null!;
        private TableLayoutPanel playQueueLayout = null!;
        private Panel playQueueResizeGrip = null!;
        private Panel playQueueBody = null!;
        private Button playQueueHeader = null!;
        private Label manualQueueLabel = null!;
        private Label automaticQueueLabel = null!;
        private Button automaticQueueRefreshButton = null!;
        private FlowLayoutPanel manualQueueFlow = null!;
        private FlowLayoutPanel automaticQueueFlow = null!;
        private SplitContainer playQueueSections = null!;
        private bool playQueueExpanded = true;
        private bool playQueueDividerMoving;
        private bool resizingPlayQueueFlows;
        private int playQueueExpandedHeight = DefaultPlayQueueExpandedHeight;
        private int playQueueResizeStartY;
        private int playQueueResizeStartHeight;
        private Panel? highlightedPlayQueueCard;
        private Point libraryQueueDragStart;
        private PlayQueueEntry? libraryQueueDragEntry;
        private string queuedAnimationPendingPath = "";
        private bool queuedAnimationPendingConfirmed;
        private DateTime queuedAnimationProtectedUntil = DateTime.MinValue;
        private PlayQueueEntry? activeQueuedCard;
        private PlayQueueEntry? activeManualQueueEntry;
        private PlayQueueEntry? activeAutomaticQueueEntry;
        private long activeQueuedCardStartedAt = -1;
        private string activeQueuedCardLastAnimationPath = "";
        private bool activeQueuedCardUsesSmartRules;
        private long activeUnqueuedCardStartedAt = -1;
        private string activeUnqueuedCardTag = "";
        private bool activeUnqueuedCardSequential;
        private readonly System.Windows.Forms.Timer clipSelectionPlaybackTimer =
            new() { Interval = 15 };
        private readonly System.Windows.Forms.Timer playQueueResizeTimer =
            new() { Interval = 75 };
        private bool clipSelectionPlaybackPending;
        private bool clipDragInProgress;
        private void SetupPlayQueue()
        {
            components.Add(playQueueResizeTimer);
            playQueueResizeTimer.Tick += (_, _) =>
            {
                playQueueResizeTimer.Stop();
                if (!playQueueResizeGrip.Capture &&
                    !playQueueDividerMoving)
                    ResizeBothPlayQueueFlows();
            };
            playQueueExpandedHeight = Math.Max(110,
                Properties.Settings.Default.PlayQueueHeight);
            enablePlayQueueToolStripMenuItem.Checked =
                Properties.Settings.Default.EnablePlayQueue;
            enablePlayQueueToolStripMenuItem.CheckedChanged += (_, _) =>
            {
                Properties.Settings.Default.EnablePlayQueue =
                    enablePlayQueueToolStripMenuItem.Checked;
                if (!enablePlayQueueToolStripMenuItem.Checked)
                {
                    queuedAnimationPendingPath = "";
                    queuedAnimationPendingConfirmed = false;
                    queuedAnimationProtectedUntil = DateTime.MinValue;
                    ClearQueuedCardSession();
                }
                RefreshPlayQueueVisibility();
                if (enablePlayQueueToolStripMenuItem.Checked)
                    RebuildAutomaticQueue();
            };
            requeueCompletedManualItemsToolStripMenuItem.Checked =
                Properties.Settings.Default.RequeueCompletedManualItems;
            requeueCompletedManualItemsToolStripMenuItem.CheckedChanged +=
                (_, _) =>
                {
                    Properties.Settings.Default.RequeueCompletedManualItems =
                        requeueCompletedManualItemsToolStripMenuItem.Checked;
                    SavePreviousQueue();
                };
            randomManualQueueSelectionToolStripMenuItem.Checked =
                Properties.Settings.Default.RandomManualQueueSelection;
            randomManualQueueSelectionToolStripMenuItem.CheckedChanged +=
                (_, _) => Properties.Settings.Default
                    .RandomManualQueueSelection =
                        randomManualQueueSelectionToolStripMenuItem.Checked;
            smartQueueFavouritesToolStripMenuItem.Checked =
                Properties.Settings.Default.SmartQueueFavouritesOnly;
            smartQueueLeastRecentToolStripMenuItem.Checked =
                Properties.Settings.Default.SmartQueueLeastRecentlyPlayed;
            smartQueueRotateModelsToolStripMenuItem.Checked =
                Properties.Settings.Default.SmartQueueRotateModels;
            smartQueueUnplayedClipsToolStripMenuItem.Checked =
                Properties.Settings.Default.SmartQueueUnplayedClipsFirst;
            smartQueueWeightedToolStripMenuItem.Checked =
                Properties.Settings.Default.SmartQueueWeightedPreferences;
            smartQueueNewestToolStripMenuItem.Checked =
                Properties.Settings.Default.SmartQueueNewestPurchasesFirst;
            smartQueueCooldownByModelToolStripMenuItem.Checked =
                Properties.Settings.Default.SmartQueueCooldownByModel;
            smartQueueCooldownByModelToolStripMenuItem.Enabled =
                Properties.Settings.Default.SmartQueueCooldownHours > 0;
            smartQueueIgnoreScoresTrackBar.Value = Math.Clamp(
                Properties.Settings.Default.SmartQueueIgnoreScoresPercent,
                smartQueueIgnoreScoresTrackBar.Minimum,
                smartQueueIgnoreScoresTrackBar.Maximum);
            UpdateSmartQueueIgnoreScoresLabel();
            smartQueueIgnoreScoresTrackBar.Scroll += (_, _) =>
            {
                Properties.Settings.Default.SmartQueueIgnoreScoresPercent =
                    smartQueueIgnoreScoresTrackBar.Value;
                UpdateSmartQueueIgnoreScoresLabel();
            };
            smartQueueIgnoreScoresTrackBar.MouseUp += (_, _) =>
                CommitSmartQueueSettings();
            smartQueueIgnoreScoresTrackBar.KeyUp += (_, _) =>
                CommitSmartQueueSettings();
            smartQueueFavouritesToolStripMenuItem.CheckedChanged +=
                SmartQueueRuleChanged;
            smartQueueLeastRecentToolStripMenuItem.CheckedChanged +=
                SmartQueueRuleChanged;
            smartQueueRotateModelsToolStripMenuItem.CheckedChanged +=
                SmartQueueRuleChanged;
            smartQueueUnplayedClipsToolStripMenuItem.CheckedChanged +=
                SmartQueueRuleChanged;
            smartQueueWeightedToolStripMenuItem.CheckedChanged +=
                SmartQueueRuleChanged;
            smartQueueNewestToolStripMenuItem.CheckedChanged +=
                SmartQueueRuleChanged;
            smartQueueCooldownByModelToolStripMenuItem.CheckedChanged +=
                SmartQueueRuleChanged;
            foreach ((int hours, string label) in new[]
            {
                (0, "Off"),
                (1, "1 hour"),
                (6, "6 hours"),
                (12, "12 hours"),
                (24, "1 day"),
                (72, "3 days"),
                (168, "7 days")
            })
            {
                ToolStripMenuItem item = new(label)
                {
                    Tag = hours,
                    ToolTipText = hours == 0
                        ? "Do not exclude recently played cards."
                        : $"Exclude cards played within the last {label}.",
                    Checked = hours == Properties.Settings.Default
                        .SmartQueueCooldownHours
                };
                item.Click += (_, _) =>
                {
                    Properties.Settings.Default.SmartQueueCooldownHours =
                        hours;
                    foreach (ToolStripMenuItem cooldownItem in
                        smartQueueCooldownToolStripMenuItem.DropDownItems)
                    {
                        cooldownItem.Checked =
                            cooldownItem.Tag is int value && value == hours;
                    }
                    smartQueueCooldownByModelToolStripMenuItem.Enabled =
                        hours > 0;
                    Properties.Settings.Default.Save();
                    RebuildAutomaticQueue();
                };
                smartQueueCooldownToolStripMenuItem.DropDownItems.Add(item);
            }
            smartQueueIgnoreScoresHost =
                new ToolStripControlHost(smartQueueIgnoreScoresTrackBar)
                {
                    AutoSize = false,
                    Size = new System.Drawing.Size(240, 46)
                };
            smartQueueToolStripMenuItem.DropDownOpening += (_, _) =>
                ApplySmartQueueSliderTheme();
            smartQueueToolStripMenuItem.DropDownOpened += (_, _) =>
                ApplySmartQueueSliderTheme();
            smartQueueToolStripMenuItem.DropDownItems.AddRange(
            [
                smartQueueFavouritesToolStripMenuItem,
                smartQueueCooldownToolStripMenuItem,
                smartQueueCooldownByModelToolStripMenuItem,
                new ToolStripSeparator(),
                smartQueueLeastRecentToolStripMenuItem,
                smartQueueWeightedToolStripMenuItem,
                smartQueueNewestToolStripMenuItem,
                new ToolStripMenuItem(
                    "Favour rules normally pick the highest score")
                {
                    Enabled = false,
                    ToolTipText =
                        "Enabled favour rules combine into a score; the highest score normally wins."
                },
                smartQueueIgnoreScoresLabel,
                smartQueueIgnoreScoresHost,
                new ToolStripSeparator(),
                smartQueueUnplayedClipsToolStripMenuItem,
                smartQueueRotateModelsToolStripMenuItem
            ]);

            foreach (int length in new[] { 5, 10, 15, 20, 30, 50 })
            {
                ToolStripMenuItem item = new(length.ToString())
                {
                    Tag = length,
                    ToolTipText =
                        $"Keep {length} filtered cards ready in the automatic queue.",
                    Checked = length ==
                        Properties.Settings.Default.AutoQueueLength
                };
                item.Click += (_, _) =>
                {
                    Properties.Settings.Default.AutoQueueLength = length;
                    foreach (ToolStripMenuItem lengthItem in
                        autoQueueLengthToolStripMenuItem.DropDownItems)
                    {
                        lengthItem.Checked =
                            lengthItem.Tag is int value && value == length;
                    }
                    RebuildAutomaticQueue();
                };
                autoQueueLengthToolStripMenuItem.DropDownItems.Add(item);
            }

            saveQueueToolStripMenuItem.Click += (_, _) => SaveManualQueue();
            loadQueueToolStripMenuItem.Click += (_, _) => LoadManualQueue();
            clearQueueToolStripMenuItem.Click += (_, _) => ClearManualQueue();
            queueFileToolStripMenuItem.DropDownItems.AddRange(
            [
                saveQueueToolStripMenuItem,
                loadQueueToolStripMenuItem,
                new ToolStripSeparator(),
                clearQueueToolStripMenuItem
            ]);
            fileToolStripMenuItem.DropDownItems.Insert(
                fileToolStripMenuItem.DropDownItems.IndexOf(
                    playbackHistoryToolStripMenuItem),
                queueFileToolStripMenuItem);

            playQueuePanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = playQueueExpandedHeight
            };
            playQueueHeader = new Button
            {
                AccessibleDescription =
                    "Expand or collapse the manual and automatic play-next queues.",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Height = PlayQueueCollapsedHeight,
                FlatStyle = FlatStyle.Flat,
                Name = "playQueueHeader",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 1, 0, 3)
            };
            playQueueHeader.FlatAppearance.BorderSize = 0;
            playQueueHeader.Click += (_, _) =>
            {
                if (playQueueExpanded)
                    playQueueExpandedHeight = playQueuePanel.Height;
                playQueueExpanded = !playQueueExpanded;
                playQueueBody.Visible = playQueueExpanded;
                playQueuePanel.Height = playQueueExpanded
                    ? playQueueExpandedHeight : playQueueHeader.Height;
                playQueueResizeGrip.Visible = playQueueExpanded &&
                    Properties.Settings.Default.EnablePlayQueue;
                UpdatePlayQueueHeader();
                AdjustControls();
            };

            manualQueueFlow = CreateQueueFlowPanel();
            automaticQueueFlow = CreateQueueFlowPanel();
            manualQueueFlow.AccessibleDescription =
                "Drop cards or clips here and drag entries to reorder them.";
            automaticQueueFlow.AccessibleDescription =
                "Automatic queue generated from the current filters and smart rules.";
            manualQueueLabel = CreateQueueLabel("manualQueueLabel");
            automaticQueueLabel = CreateQueueLabel("automaticQueueLabel");
            automaticQueueRefreshButton = new Button
            {
                AccessibleName = "Generate a new automatic queue",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe MDL2 Assets", 9, FontStyle.Regular),
                Name = "automaticQueueRefreshButton",
                Padding = new Padding(3, 0, 3, 0),
                TabStop = true,
                Text = "\uE72C",
                TextAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = false
            };
            automaticQueueRefreshButton.FlatAppearance.BorderSize = 0;
            automaticQueueRefreshButton.Click += (_, _) =>
                RebuildAutomaticQueue();
            Panel manualSection = CreateQueueSection(
                manualQueueLabel, manualQueueFlow);
            Panel automaticSection = CreateQueueSection(
                automaticQueueLabel, automaticQueueFlow,
                automaticQueueRefreshButton);
            playQueueSections = new SplitContainer
            {
                AccessibleDescription =
                    "Drag the divider to resize the manual and automatic queues.",
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                Panel1MinSize = 100,
                Panel2MinSize = 100,
                Padding = new Padding(6, 0, 6, 5)
            };
            playQueueSections.Panel1.Controls.Add(manualSection);
            playQueueSections.Panel2.Controls.Add(automaticSection);
            bool initialSplitSet = false;
            playQueueSections.Layout += (_, _) =>
            {
                if (initialSplitSet || playQueueSections.ClientSize.Width < 240)
                    return;
                initialSplitSet = true;
                playQueueSections.SplitterDistance =
                    (playQueueSections.ClientSize.Width -
                     playQueueSections.SplitterWidth) * 45 / 100;
            };
            playQueueSections.SplitterMoving += (_, _) =>
            {
                playQueueDividerMoving = true;
                ClearPlayQueueCardHighlight();
            };
            playQueueSections.SplitterMoved += (_, _) =>
            {
                playQueueDividerMoving = false;
                ResizeBothPlayQueueFlows();
            };

            playQueueBody = new Panel
            {
                Dock = DockStyle.Fill,
                Name = "playQueueBody"
            };
            playQueueBody.Controls.Add(playQueueSections);
            playQueueResizeGrip = new Panel
            {
                AccessibleDescription =
                    "Drag to change the height of the play-next queue.",
                Dock = DockStyle.Top,
                Height = 7,
                Name = "playQueueResizeGrip",
                Cursor = Cursors.SizeNS
            };
            playQueueResizeGrip.MouseDown += playQueueResizeGrip_MouseDown;
            playQueueResizeGrip.MouseMove += playQueueResizeGrip_MouseMove;
            playQueueResizeGrip.MouseUp += playQueueResizeGrip_MouseUp;
            playQueueResizeGrip.Paint += playQueueResizeGrip_Paint;
            playQueueResizeGrip.SizeChanged += (_, _) =>
                playQueueResizeGrip.Invalidate();
            playQueueLayout = new TableLayoutPanel
            {
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Name = "playQueueLayout",
                Padding = Padding.Empty,
                RowCount = 3
            };
            playQueueLayout.RowStyles.Add(
                new RowStyle(SizeType.Absolute, playQueueResizeGrip.Height));
            playQueueLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));
            playQueueLayout.RowStyles.Add(
                new RowStyle(SizeType.Percent, 100));
            playQueueBody.Margin = Padding.Empty;
            playQueueHeader.Margin = Padding.Empty;
            playQueueResizeGrip.Margin = Padding.Empty;
            playQueueLayout.Controls.Add(playQueueResizeGrip, 0, 0);
            playQueueLayout.Controls.Add(playQueueHeader, 0, 1);
            playQueueLayout.Controls.Add(playQueueBody, 0, 2);
            playQueuePanel.Controls.Add(playQueueLayout);
            Controls.Add(playQueuePanel);
            Controls.SetChildIndex(playQueuePanel, Controls.Count - 1);

            manualQueueFlow.DragEnter += QueueFlow_DragEnter;
            manualQueueFlow.DragOver += QueueFlow_DragOver;
            manualQueueFlow.DragDrop += ManualQueueFlow_DragDrop;
            automaticQueueFlow.DragEnter += AutomaticQueueFlow_DragEnter;
            automaticQueueFlow.DragOver += AutomaticQueueFlow_DragEnter;
            automaticQueueFlow.DragDrop += AutomaticQueueFlow_DragDrop;
            manualQueueFlow.Scroll += QueueFlow_Scroll;
            automaticQueueFlow.Scroll += QueueFlow_Scroll;
            manualQueueFlow.SizeChanged += (_, _) =>
            {
                ClearPlayQueueCardHighlight();
                if (!playQueueResizeGrip.Capture &&
                    !playQueueDividerMoving &&
                    !resizingPlayQueueFlows)
                    SchedulePlayQueueResize();
            };
            automaticQueueFlow.SizeChanged += (_, _) =>
            {
                ClearPlayQueueCardHighlight();
                if (!playQueueResizeGrip.Capture &&
                    !playQueueDividerMoving &&
                    !resizingPlayQueueFlows)
                    SchedulePlayQueueResize();
            };
            listModelsNew.MouseMove += listModelsNew_MouseMoveForQueue;
            listClips.ItemDrag += listClips_ItemDragForQueue;
            listClips.MouseUp += listClips_MouseUpForQueue;
            clipSelectionPlaybackTimer.Tick += (_, _) =>
            {
                if (!clipSelectionPlaybackPending ||
                    clipDragInProgress)
                {
                    clipSelectionPlaybackTimer.Stop();
                    clipSelectionPlaybackPending = false;
                    return;
                }
                if ((Control.MouseButtons & MouseButtons.Left) != 0)
                    return;
                clipSelectionPlaybackTimer.Stop();
                clipSelectionPlaybackPending = false;
                PlaySelectedClip();
            };
            SetupTooltipDelayMenu();
            SetupPlaybackQueueTooltips();
            SetupMinimumClipSizeMenu();
            playerSizeByClipTypeToolStripMenuItem.Click += (_, _) =>
                ShowPlayerSizeByClipTypeSettings();
            UpdateMinimumClipSizeMenuText();
            GroupSettingsMenu();
            SetPlayQueueColours();
            RefreshPlayQueueVisibility();
        }

        private void QueueFlow_Scroll(object? sender, ScrollEventArgs e)
        {
            if (directCompositionCardOverlays != null &&
                !directCompositionCardOverlays.Render(true))
                DisableDirectCompositionCardOverlays();
        }

        private void SaveManualQueue()
        {
            if (manualPlayQueue.Count == 0)
                return;

            using System.Windows.Forms.SaveFileDialog dialog = new()
            {
                Filter = "QuickPlayer queue (*.iqpq)|*.iqpq",
                FileName = $"QuickPlayer-Queue-{DateTime.Now:yyyy-MM-dd}.iqpq",
                AddExtension = true,
                DefaultExt = "iqpq"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                Persistence.Save(dialog.FileName, manualPlayQueue);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The queue could not be saved.\r\n" +
                    ex.Message, "Save Queue Failed", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ClearManualQueue()
        {
            manualPlayQueue.Clear();
            queuedAnimationPendingPath = "";
            queuedAnimationPendingConfirmed = false;
            queuedAnimationProtectedUntil = DateTime.MinValue;
            ClearQueuedCardSession(discardManualQueueEntry: true);
            RebuildAutomaticQueue();
            SavePreviousQueue();
        }

        private void LoadManualQueue()
        {
            using System.Windows.Forms.OpenFileDialog dialog = new()
            {
                Filter = "QuickPlayer queue (*.iqpq)|*.iqpq",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                LoadManualQueue(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The queue could not be loaded.\r\n" +
                    ex.Message, "Load Queue Failed", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void LoadManualQueue(string path)
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("The queue file is too large.");

            List<PlayQueueEntry?> loaded =
                Persistence.Load<List<PlayQueueEntry?>>(path);
            List<PlayQueueEntry> valid = loaded.Take(1_000)
                .Where(entry => entry != null && IsAvailableQueueEntry(entry))
                .Select(entry => entry!).ToList();
            manualPlayQueue.Clear();
            manualPlayQueue.AddRange(valid);
            SavePreviousQueue();
            RebuildAutomaticQueue();
        }

        private void RestorePreviousQueue()
        {
            if (!File.Exists(PreviousQueuePath))
                return;

            try { LoadManualQueue(PreviousQueuePath); }
            catch { }
        }

        private void SavePreviousQueue()
        {
            if (apiOnlyMode)
                return;
            try
            {
                Persistence.Save(PreviousQueuePath, QueueForPersistence(
                    manualPlayQueue, activeManualQueueEntry,
                    Properties.Settings.Default.RequeueCompletedManualItems));
            }
            catch { }
        }

        private static List<PlayQueueEntry> QueueForPersistence(
            IEnumerable<PlayQueueEntry> queue, PlayQueueEntry? active,
            bool requeueActive)
        {
            List<PlayQueueEntry> saved = queue.ToList();
            if (requeueActive && active != null)
                saved.Add(active);
            return saved;
        }

        private static bool IsAvailableQueueEntry(PlayQueueEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.CardTag))
                return false;
            ModelCard? card = Datastore.findCardByTag(entry.CardTag);
            return card?.clips != null &&
                (string.IsNullOrEmpty(entry.ClipName) ||
                    card.clips.Any(clip => string.Equals(
                        clip.clipName, entry.ClipName,
                        StringComparison.OrdinalIgnoreCase)));
        }

        private static FlowLayoutPanel CreateQueueFlowPanel() => new()
        {
            AllowDrop = true,
            AutoScroll = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(2),
            WrapContents = false
        };

        private static Label CreateQueueLabel(string name)
        {
            Label label = new()
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Name = name,
                Padding = new Padding(4, 3, 0, 4)
            };
            label.MinimumSize = new System.Drawing.Size(
                0, label.Font.Height + label.Padding.Vertical + 2);
            return label;
        }

        private static Panel CreateQueueSection(Label label,
            FlowLayoutPanel flow, Button? action = null)
        {
            FlowLayoutPanel header = new()
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                Name = label.Name.Replace("Label", "Header"),
                WrapContents = false
            };
            label.Margin = Padding.Empty;
            header.Controls.Add(label);
            if (action != null)
            {
                action.Margin = new Padding(4, 1, 0, 1);
                header.Controls.Add(action);
            }
            Panel panel = new() { Dock = DockStyle.Fill };
            panel.Controls.Add(flow);
            panel.Controls.Add(header);
            return panel;
        }

        private void SetupTooltipDelayMenu()
        {
            foreach (int delay in new[] { -1, 0, 100, 200, 500, 1_000, 2_000 })
            {
                ToolStripMenuItem item = new(
                    delay < 0 ? "Disabled" :
                    delay == 0 ? "Immediate" : $"{delay:N0} ms")
                {
                    Tag = delay,
                    ToolTipText = delay < 0
                        ? "Do not show tooltips."
                        : $"Wait {delay:N0} milliseconds before showing a tooltip."
                };
                item.Click += (_, _) =>
                {
                    Properties.Settings.Default.TooltipInitialDelay = delay;
                    Properties.Settings.Default.Save();
                    RefreshTooltipDelay();
                };
                tooltipDelayToolStripMenuItem.DropDownItems.Add(item);
            }
            tooltipDelayToolStripMenuItem.ToolTipText =
                "Choose how long the pointer waits before showing help.";
            RefreshTooltipDelay();
        }

        private void SetupPlaybackQueueTooltips()
        {
            playerSizeByClipTypeToolStripMenuItem.ToolTipText =
                "Set separate small and large player sizes for standing, table, pole, swing and cage clips.";
            playbackSettingsToolStripMenuItem.ToolTipText =
                "Configure what plays next and QuickPlayer's playback controls.";
            enablePlayQueueToolStripMenuItem.ToolTipText =
                "Show and use the manual and automatic play-next queues.";
            autoQueueLengthToolStripMenuItem.ToolTipText =
                "Choose how many filtered cards are prepared automatically.";
            smartQueueToolStripMenuItem.ToolTipText =
                "Configure filtering, scoring and ordering for the automatic queue.";
            requeueCompletedManualItemsToolStripMenuItem.ToolTipText =
                "Move a finished manual item to the end instead of removing it.";
            randomManualQueueSelectionToolStripMenuItem.ToolTipText =
                "Choose the next manual item randomly instead of from the front.";
            enforceCardFilterToolStripMenuItem.ToolTipText =
                "Limit automatic playback to cards allowed by the current search and filter.";
            randomPlayOrderToolStripMenuItem.ToolTipText =
                "Randomize choices when smart-queue scoring does not determine the order.";
            avoidRecentRepeatsToolStripMenuItem.ToolTipText =
                "Skip recently played clips when another eligible clip is available.";
            enablePlaybackControlToolStripMenuItem.ToolTipText =
                "Attach pause, seek, speed and timeline controls to the desktop animation.";
            alphaCheckpointCacheToolStripMenuItem.ToolTipText =
                "Cache modern-clip transparency checkpoints to speed up repeated seeks.";
            alphaCheckpointCacheSizeToolStripMenuItem.ToolTipText =
                "Set the maximum space used by the alpha checkpoint cache.";
            playbackHistoryToolStripMenuItem.ToolTipText =
                "View or clear the local history used by repeat avoidance and cooldowns.";

            smartQueueFavouritesToolStripMenuItem.ToolTipText =
                "Allow only favourite cards in the automatic queue.";
            smartQueueCooldownToolStripMenuItem.ToolTipText =
                "Temporarily exclude cards that played within the selected period.";
            smartQueueCooldownByModelToolStripMenuItem.ToolTipText =
                "Apply the cooldown to every card featuring the same model.";
            smartQueueLeastRecentToolStripMenuItem.ToolTipText =
                "Give higher scores to cards that have gone longest without playing.";
            smartQueueWeightedToolStripMenuItem.ToolTipText =
                "Give higher scores to favourites and cards with higher personal ratings.";
            smartQueueNewestToolStripMenuItem.ToolTipText =
                "Give higher scores to the most recently purchased cards.";
            smartQueueUnplayedClipsToolStripMenuItem.ToolTipText =
                "When a card is chosen, prefer one of its clips that has never played.";
            smartQueueRotateModelsToolStripMenuItem.ToolTipText =
                "Avoid placing cards from the same model next to each other.";
        }

        private void RefreshTooltipDelay()
        {
            int delay = TooltipManager.NormalizeDelay(
                Properties.Settings.Default.TooltipInitialDelay);
            foreach (ToolStripMenuItem item in
                     tooltipDelayToolStripMenuItem.DropDownItems)
                item.Checked = item.Tag is int value && value == delay;
            if (applicationToolTip != null)
                TooltipManager.SetDelay(applicationToolTip, delay);
            TooltipManager.SetToolStripTips(this, delay >= 0);
        }

        private void GroupSettingsMenu()
        {
            settingsToolStripMenuItem.DropDownItems.Clear();
            playbackSettingsToolStripMenuItem.DropDownItems.AddRange(
            [
                enablePlayQueueToolStripMenuItem,
                autoQueueLengthToolStripMenuItem,
                minimumClipSizeToolStripMenuItem,
                smartQueueToolStripMenuItem,
                requeueCompletedManualItemsToolStripMenuItem,
                randomManualQueueSelectionToolStripMenuItem,
                new ToolStripSeparator(),
                enforceCardFilterToolStripMenuItem,
                randomPlayOrderToolStripMenuItem,
                avoidRecentRepeatsToolStripMenuItem,
                new ToolStripSeparator(),
                iStripperPlayerVolumeMenu,
                customPlayerVolumeMenu,
                customPlayerFullOpacityMenu,
                enablePlaybackControlToolStripMenuItem,
                alphaCheckpointCacheToolStripMenuItem,
                alphaCheckpointCacheSizeToolStripMenuItem,
                new ToolStripSeparator(),
                playbackHistoryToolStripMenuItem
            ]);
            librarySettingsToolStripMenuItem.DropDownItems.AddRange(
            [
                zoomOnHoverToolStripMenuItem,
                menuShowRatingsStars,
                menuShowCardSortLabels,
                roundCardCornersToolStripMenuItem,
                drawCardOverlaysToolStripMenuItem,
                directCompositionOverlaysToolStripMenuItem,
                overlayDefaultsToolStripMenuItem,
                new ToolStripSeparator(),
                includeDescriptionInSearchToolStripMenuItem,
                includeShowTitleInSearchToolStripMenuItem
            ]);
            appearanceSettingsToolStripMenuItem.DropDownItems.AddRange(
            [
                playerSizeByClipTypeToolStripMenuItem,
                new ToolStripSeparator(),
                wallpaperToolStripMenuItem,
                showKittyToolStripMenuItem,
                minimizeToTrayToolStripMenuItem,
                tooltipDelayToolStripMenuItem,
                darkModeToolStripMenuItem
            ]);
            settingsToolStripMenuItem.DropDownItems.AddRange(
            [
                hotkeysToolStripMenuItem,
                lockPlayerToolStripMenuItem,
                clickThroughLockedPlayerToolStripMenuItem,
                wheelResizePlayerToolStripMenuItem,
                new ToolStripSeparator(),
                playbackSettingsToolStripMenuItem,
                librarySettingsToolStripMenuItem,
                appearanceSettingsToolStripMenuItem
            ]);
        }

        private static Dictionary<string, int> ParseClipTypePlayerSizes(
            string? json)
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, int>>(
                    json ?? "") is { } values
                    ? new Dictionary<string, int>(values,
                        StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string? PlayerSizeClipType(string? clipTypes)
        {
            string[] types = (clipTypes ?? "").Split(',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
            if (types.Contains("Pole", StringComparer.OrdinalIgnoreCase))
                return "pole";
            if (types.Contains("Cage", StringComparer.OrdinalIgnoreCase))
                return "cage";
            if (types.Contains("Swing", StringComparer.OrdinalIgnoreCase))
                return "swing";
            if (types.Contains("Table", StringComparer.OrdinalIgnoreCase) ||
                types.Contains("BehindTable",
                    StringComparer.OrdinalIgnoreCase))
                return "table";
            return types.Contains("Standing",
                StringComparer.OrdinalIgnoreCase) ? "standing" : null;
        }

        private static int PlayerSizePercent(
            IReadOnlyDictionary<string, int> sizes, string key) =>
            sizes.TryGetValue(key, out int percent) &&
            percent is >= 1 and <= 200 ? percent : 0;

        private string? PlayerSizeKey(string animationPath, int mode)
        {
            string[] parts = animationPath.Replace('/', '\\').Split('\\', 2);
            if (parts.Length != 2)
                return null;
            ModelCard? card = Datastore.findCardByTag(
                parts[0].Split('-')[0]);
            ModelClip? clip = card?.clips?.FirstOrDefault(item =>
                string.Equals(item.clipName, parts[1],
                    StringComparison.OrdinalIgnoreCase));
            string? type = PlayerSizeClipType(clip?.clipType);
            return type == null ? null :
                type + (mode == 1 ? "Large" : "Small");
        }

        internal static int? PlayerSizeTarget(int configured,
            int? normal, int? manuallySelected) => manuallySelected ??
            (configured > 0 ? configured : normal);

        private static int? ReadNormalPlayerSizePercent(bool large)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Totem\vghd\player", false);
                object? value = key?.GetValue(large
                    ? "expectedLargeHeightPercent"
                    : "expectedHeightPercent");
                int percent = Convert.ToInt32(value);
                return percent is >= 1 and <= 200 ? percent : null;
            }
            catch
            {
                return null;
            }
        }

        private void CaptureNormalPlayerSizes()
        {
            int? small = ReadNormalPlayerSizePercent(false);
            int? large = ReadNormalPlayerSizePercent(true);
            lock (playerSizeLock)
            {
                normalSmallPlayerSizePercent ??= small;
                normalLargePlayerSizePercent ??= large;
            }
        }

        private int PlayerSizeForAnimation(
            string animationPath, int mode, int fallback)
        {
            CaptureNormalPlayerSizes();
            string? sizeKey = PlayerSizeKey(animationPath, mode);
            Dictionary<string, int> sizes = ParseClipTypePlayerSizes(
                Properties.Settings.Default.ClipTypePlayerSizes);
            int configured = sizeKey == null
                ? 0 : PlayerSizePercent(sizes, sizeKey);
            lock (playerSizeLock)
            {
                int? normal = mode == 1
                    ? normalLargePlayerSizePercent
                    : normalSmallPlayerSizePercent;
                int? manual = sizeKey != null &&
                    manualPlayerSizes.TryGetValue(sizeKey, out int percent)
                        ? percent : null;
                return PlayerSizeTarget(configured, normal, manual) ?? fallback;
            }
        }

        private int SetVghdPlayerSizePercent(int mode, int percent)
        {
            if (!playerLockBridgeLoaded || vghd_procID == 0 ||
                mode is not (1 or 2) || percent is < 1 or > 200)
            {
                return unchecked((int)0x80070057);
            }
            ulong packed = ((ulong)(uint)mode << 32) | (uint)percent;
            return CallPlaybackBridgeApi(
                "IStripperSetPlayerSizePercent", packed);
        }

        private void QueueConfiguredPlayerSize(string? animationPath = null,
            int? mode = null)
        {
            string path = animationPath ?? GetCurrentAnimationPath();
            int requestedMode = mode ?? Volatile.Read(ref playerMode);
            long request = Interlocked.Increment(
                ref playerSizeRequestVersion);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                CaptureNormalPlayerSizes();
                Dictionary<string, int> sizes = ParseClipTypePlayerSizes(
                    Properties.Settings.Default.ClipTypePlayerSizes);
                string? sizeKey = PlayerSizeKey(path, requestedMode);
                int configured = sizeKey == null
                    ? 0 : PlayerSizePercent(sizes, sizeKey);
                lock (playerSizeLock)
                {
                    if (request != Volatile.Read(
                            ref playerSizeRequestVersion) ||
                        !playerLockBridgeLoaded)
                        return;
                    int? normal = requestedMode == 1
                        ? normalLargePlayerSizePercent
                        : normalSmallPlayerSizePercent;
                    int? manuallySelected = sizeKey != null &&
                        manualPlayerSizes.TryGetValue(
                            sizeKey, out int manualPercent)
                                ? manualPercent : null;
                    int? target = PlayerSizeTarget(
                        configured, normal, manuallySelected);
                    if (target == null)
                        return;
                    int result = SetVghdPlayerSizePercent(
                        requestedMode, target.Value);
                    if (result < 0)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Player size update failed (0x{result:X8}).");
                        return;
                    }
                    if (requestedMode == 1)
                        largePlayerSizeOverrideActive =
                            sizeKey != null &&
                            (manuallySelected != null || configured > 0);
                    else
                        smallPlayerSizeOverrideActive =
                            sizeKey != null &&
                            (manuallySelected != null || configured > 0);
                }
            });
        }

        private void RememberManualPlayerSize(
            string animationPath, int mode, int percent)
        {
            if (percent is < 1 or > 200)
                return;
            string? sizeKey = PlayerSizeKey(animationPath, mode);
            lock (playerSizeLock)
            {
                if (sizeKey != null)
                {
                    manualPlayerSizes[sizeKey] = percent;
                    if (mode == 1)
                        largePlayerSizeOverrideActive = true;
                    else if (mode == 2)
                        smallPlayerSizeOverrideActive = true;
                    return;
                }
                if (mode == 1)
                {
                    normalLargePlayerSizePercent = percent;
                    largePlayerSizeOverrideActive = false;
                }
                else if (mode == 2)
                {
                    normalSmallPlayerSizePercent = percent;
                    smallPlayerSizeOverrideActive = false;
                }
            }
        }

        private void RememberCustomPlayerSize(
            string animationPath, int mode, int percent) =>
            RememberManualPlayerSize(animationPath, mode, percent);

        private void RestoreNormalPlayerSizes()
        {
            Interlocked.Increment(ref playerSizeRequestVersion);
            lock (playerSizeLock)
            {
                if (smallPlayerSizeOverrideActive &&
                    normalSmallPlayerSizePercent is int small)
                    SetVghdPlayerSizePercent(2, small);
                if (largePlayerSizeOverrideActive &&
                    normalLargePlayerSizePercent is int large)
                    SetVghdPlayerSizePercent(1, large);
                smallPlayerSizeOverrideActive = false;
                largePlayerSizeOverrideActive = false;
            }
        }

        private void ResetPlayerSizeState()
        {
            Interlocked.Increment(ref playerSizeRequestVersion);
            lock (playerSizeLock)
            {
                normalSmallPlayerSizePercent = null;
                normalLargePlayerSizePercent = null;
                smallPlayerSizeOverrideActive = false;
                largePlayerSizeOverrideActive = false;
            }
        }

        private void ShowPlayerSizeByClipTypeSettings()
        {
            Dictionary<string, int> sizes = ParseClipTypePlayerSizes(
                Properties.Settings.Default.ClipTypePlayerSizes);
            string[] types =
                ["Standing", "Table", "Pole", "Swing", "Cage"];
            using Form dialog = new()
            {
                Text = "Player size by clip type",
                AutoScaleMode = AutoScaleMode.Font,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent,
                Padding = new Padding(12)
            };
            TableLayoutPanel grid = new()
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                RowCount = types.Length + 3,
                Dock = DockStyle.Fill
            };
            grid.ColumnStyles.Add(new(SizeType.AutoSize));
            grid.ColumnStyles.Add(new(SizeType.Absolute, 100));
            grid.ColumnStyles.Add(new(SizeType.Absolute, 100));
            grid.Controls.Add(new Label { Text = "Clip type", AutoSize = true },
                0, 0);
            grid.Controls.Add(new Label { Text = "Small (%)", AutoSize = true },
                1, 0);
            grid.Controls.Add(new Label { Text = "Large (%)", AutoSize = true },
                2, 0);

            Dictionary<string, NumericUpDown> inputs =
                new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < types.Length; index++)
            {
                string type = types[index];
                grid.Controls.Add(new Label
                {
                    Text = type,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(3, 7, 12, 3)
                }, 0, index + 1);
                foreach ((string suffix, int column) in
                    new[] { ("Small", 1), ("Large", 2) })
                {
                    string key = type.ToLowerInvariant() + suffix;
                    NumericUpDown input = new()
                    {
                        Minimum = 0,
                        Maximum = 200,
                        Increment = 5,
                        Value = sizes.GetValueOrDefault(key),
                        Dock = DockStyle.Fill,
                        AccessibleName = $"{type} {suffix} player size"
                    };
                    inputs[key] = input;
                    grid.Controls.Add(input, column, index + 1);
                }
            }
            int noteRow = types.Length + 1;
            Label note = new()
            {
                Text = "0 uses iStripper's normal size.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 10, 3, 8)
            };
            grid.Controls.Add(note, 0, noteRow);
            grid.SetColumnSpan(note, 3);

            FlowLayoutPanel buttons = new()
            {
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill
            };
            Button ok = new()
            {
                Text = "OK",
                AutoSize = true,
                DialogResult = DialogResult.OK
            };
            Button cancel = new()
            {
                Text = "Cancel",
                AutoSize = true,
                DialogResult = DialogResult.Cancel
            };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            grid.Controls.Add(buttons, 0, noteRow + 1);
            grid.SetColumnSpan(buttons, 3);
            dialog.Controls.Add(grid);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            AppTheme.Apply(dialog);

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            Dictionary<string, int> saved = inputs
                .Where(pair => pair.Value.Value > 0)
                .ToDictionary(pair => pair.Key,
                    pair => Decimal.ToInt32(pair.Value.Value),
                    StringComparer.OrdinalIgnoreCase);
            Properties.Settings.Default.ClipTypePlayerSizes =
                JsonSerializer.Serialize(saved);
            Properties.Settings.Default.Save();
            lock (playerSizeLock)
            {
                manualPlayerSizes.Clear();
            }
            QueueConfiguredPlayerSize();
        }

        private void RefreshPlayQueueVisibility()
        {
            if (playQueuePanel == null)
                return;

            bool enabled = Properties.Settings.Default.EnablePlayQueue;
            playQueuePanel.Visible = enabled;
            playQueueResizeGrip.Visible = enabled && playQueueExpanded;
            autoQueueLengthToolStripMenuItem.Enabled =
                enabled && Properties.Settings.Default.EnforceCardFilter;
            automaticQueueFlow.Enabled =
                enabled && Properties.Settings.Default.EnforceCardFilter;
            automaticQueueRefreshButton.Enabled =
                automaticQueueFlow.Enabled;
            if (!Properties.Settings.Default.EnforceCardFilter)
                automaticPlayQueue.Clear();
            RenderPlayQueues();
            if (spaceRightOfListModel != 0)
                AdjustControls();
        }

        private void SetPlayQueueColours()
        {
            if (playQueuePanel == null)
                return;

            bool dark = Properties.Settings.Default.DarkMode;
            Color background = dark
                ? Color.FromArgb(40, 40, 40) : Color.WhiteSmoke;
            Color secondary = dark
                ? Color.FromArgb(48, 48, 48) : Color.FromArgb(238, 238, 238);
            Color foreground = dark ? Color.AntiqueWhite : Color.Black;
            playQueuePanel.BackColor = background;
            playQueueBody.BackColor = background;
            playQueueHeader.BackColor = secondary;
            playQueueHeader.ForeColor = foreground;
            playQueueSections.BackColor = dark
                ? Color.FromArgb(84, 100, 108)
                : Color.FromArgb(130, 145, 152);
            playQueueResizeGrip.BackColor = dark
                ? Color.FromArgb(84, 100, 108) : Color.FromArgb(130, 145, 152);
            playQueueResizeGrip.Invalidate();
            manualQueueFlow.BackColor = background;
            automaticQueueFlow.BackColor = background;
            manualQueueLabel.BackColor = background;
            automaticQueueLabel.BackColor = background;
            manualQueueLabel.ForeColor = foreground;
            automaticQueueLabel.ForeColor = foreground;
            automaticQueueRefreshButton.BackColor = background;
            automaticQueueRefreshButton.ForeColor = foreground;
            automaticQueueRefreshButton.FlatAppearance.MouseOverBackColor =
                secondary;
            automaticQueueRefreshButton.FlatAppearance.MouseDownBackColor =
                secondary;
            automaticQueueRefreshButton.Invalidate();
            RenderPlayQueues();
        }

        private void UpdatePlayQueueHeader()
        {
            playQueueHeader.Text =
                $"{(playQueueExpanded ? "▼" : "▶")}  Play next queue" +
                $"  —  {manualPlayQueue.Count +
                    (activeManualQueueEntry == null ? 0 : 1)} manual · " +
                $"{automaticPlayQueue.Count +
                    (activeAutomaticQueueEntry == null ? 0 : 1)} automatic";
            int headerHeight = playQueueHeader.Font.Height +
                playQueueHeader.Padding.Vertical + 6;
            int gripHeight = Math.Max(5, 7 * DeviceDpi / 96);
            playQueueResizeGrip.Height = gripHeight;
            bool showGrip = playQueueExpanded &&
                Properties.Settings.Default.EnablePlayQueue;
            playQueueResizeGrip.Visible = showGrip;
            playQueueHeader.MinimumSize =
                new System.Drawing.Size(0, headerHeight);
            if (playQueueLayout != null)
                playQueueLayout.RowStyles[0].Height =
                    showGrip ? gripHeight : 0;
            if (!playQueueExpanded)
                playQueuePanel.Height = headerHeight;
        }

        private void UpdateMinimumClipSizeMenuText()
        {
            minimumClipSizeToolStripMenuItem.Text =
                Properties.Settings.Default.MinSizeMB == 0
                    ? "Minimum clip size: none"
                    : $"Minimum clip size: " +
                      $"{Properties.Settings.Default.MinSizeMB:N0} MB";
        }

        private void SetupMinimumClipSizeMenu()
        {
            long current = Math.Clamp(
                (long)Math.Round(
                    Properties.Settings.Default.MinSizeMB / 5d,
                    MidpointRounding.AwayFromZero) * 5,
                0, 40);
            if (current != Properties.Settings.Default.MinSizeMB)
            {
                Properties.Settings.Default.MinSizeMB = current;
                Properties.Settings.Default.Save();
            }
            foreach (long size in Enumerable.Range(0, 9)
                         .Select(value => (long)value * 5))
            {
                ToolStripMenuItem item = new(
                    size == 0 ? "No minimum" : $"{size:N0} MB")
                {
                    Checked = size == current,
                    Tag = size
                };
                item.Click += (_, _) =>
                {
                    Properties.Settings.Default.MinSizeMB = size;
                    foreach (ToolStripMenuItem choice in
                        minimumClipSizeToolStripMenuItem.DropDownItems)
                        choice.Checked = (long)choice.Tag! == size;
                    UpdateMinimumClipSizeMenuText();
                    if (items is { Length: > 0 } &&
                        listModelsNew.SelectedItems.Count > 0)
                        loadListClips(
                            listModelsNew.SelectedItems[0].Tag);
                };
                minimumClipSizeToolStripMenuItem.DropDownItems.Add(item);
            }
        }

        private void RenderPlayQueues()
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)RenderPlayQueues);
                return;
            }

            if (manualQueueFlow == null)
                return;

            RenderPlayQueue(manualQueueFlow, manualPlayQueue, "manual",
                activeManualQueueEntry);
            RenderPlayQueue(automaticQueueFlow, automaticPlayQueue,
                "automatic", activeAutomaticQueueEntry);
            manualQueueLabel.Text =
                $"Manual ({manualPlayQueue.Count +
                    (activeManualQueueEntry == null ? 0 : 1)}) — " +
                "drop cards or clips here";
            automaticQueueLabel.Text =
                Properties.Settings.Default.EnforceCardFilter
                    ? $"Automatic ({automaticPlayQueue.Count +
                        (activeAutomaticQueueEntry == null ? 0 : 1)}) — " +
                      "filtered cards"
                    : "Automatic — disabled while card filter enforcement is off";
            UpdatePlayQueueHeader();
            UpdatePlayQueueCardOverlays();
        }

        private void RenderPlayQueue(FlowLayoutPanel flow,
            List<PlayQueueEntry> entries, string source,
            PlayQueueEntry? active)
        {
            bool dark = Properties.Settings.Default.DarkMode;
            List<PlayQueueDrag> cards = [];
            if (active != null)
                cards.Add(new(source, -1, active, true));
            cards.AddRange(entries.Select((entry, index) =>
                new PlayQueueDrag(source, index, entry)));
            if (QueueRenderIsCurrent(flow, cards, dark))
                return;

            ClearPlayQueueCardHighlight();
            flow.SuspendLayout();
            try
            {
                while (flow.Controls.Count > 0)
                    flow.Controls[0].Dispose();
                System.Drawing.Size cardSize =
                    PlayQueueCardSize(flow, cards.Count);
                foreach (PlayQueueDrag card in cards)
                    flow.Controls.Add(CreatePlayQueueCard(card, cardSize));
                flow.Tag = dark;
            }
            finally
            {
                flow.ResumeLayout(true);
            }
            if (cards.Count == 0)
                flow.AutoScrollMinSize = new System.Drawing.Size(1, 1);
        }

        private static bool QueueRenderIsCurrent(FlowLayoutPanel flow,
            IReadOnlyList<PlayQueueDrag> cards, bool dark)
        {
            if (flow.Controls.Count != cards.Count ||
                flow.Tag is not bool renderedDark || renderedDark != dark)
                return false;

            for (int index = 0; index < cards.Count; index++)
            {
                if (flow.Controls[index] is not Panel panel ||
                    panel.Tag is not PlayQueueDrag drag ||
                    drag != cards[index])
                    return false;

                PictureBox? picture = panel.Controls.OfType<PictureBox>()
                    .FirstOrDefault();
                Image? expected = Datastore.findCardByTag(
                    cards[index].Entry.CardTag)?.image;
                if (picture == null ||
                    !ReferenceEquals(picture.Image, expected))
                    return false;
            }
            return true;
        }

        private static System.Drawing.Size PlayQueueCardSize(
            FlowLayoutPanel flow, int cardCount)
        {
            int scrollBarHeight =
                SystemInformation.HorizontalScrollBarHeight;
            int fullClientHeight = flow.ClientSize.Height +
                (flow.HorizontalScroll.Visible ? scrollBarHeight : 0);
            int availableHeight =
                fullClientHeight - flow.Padding.Vertical - 8;
            int height = Math.Max(52, availableHeight);
            int imageHeight = Math.Max(25, height - 27);
            int width = Math.Max(46,
                (int)Math.Round(imageHeight * 162.0 / 242.0) + 8);
            int contentWidth =
                cardCount * (width + 6) + flow.Padding.Horizontal;
            bool needsScrollBar = contentWidth > flow.ClientSize.Width;
            System.Drawing.Size scrollSize = needsScrollBar
                ? new System.Drawing.Size(contentWidth, 0)
                : System.Drawing.Size.Empty;
            if (flow.AutoScrollMinSize != scrollSize)
                flow.AutoScrollMinSize = scrollSize;
            if (needsScrollBar)
                height = Math.Max(52, availableHeight - scrollBarHeight);
            return new System.Drawing.Size(width, height);
        }

        private static void ResizePlayQueueCards(FlowLayoutPanel flow)
        {
            flow.SuspendLayout();
            try
            {
                System.Drawing.Size size =
                    PlayQueueCardSize(flow, flow.Controls.Count);
                int imageHeight = Math.Max(25, size.Height - 27);
                foreach (Panel panel in flow.Controls.OfType<Panel>())
                {
                    if (panel.Size != size)
                        panel.Size = size;
                    PictureBox? image = panel.Controls.OfType<PictureBox>()
                        .FirstOrDefault();
                    if (image != null && image.Height != imageHeight)
                        image.Height = imageHeight;
                }
            }
            finally
            {
                flow.ResumeLayout(true);
            }
        }

        private void SchedulePlayQueueResize()
        {
            playQueueResizeTimer.Stop();
            playQueueResizeTimer.Start();
        }

        private void ResizeBothPlayQueueFlows()
        {
            playQueueResizeTimer.Stop();
            if (resizingPlayQueueFlows)
                return;

            resizingPlayQueueFlows = true;
            try
            {
                ResizePlayQueueCards(manualQueueFlow);
                ResizePlayQueueCards(automaticQueueFlow);
            }
            finally
            {
                resizingPlayQueueFlows = false;
            }
            UpdatePlayQueueCardOverlays();
        }

        private void playQueueResizeGrip_MouseDown(object? sender,
            MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;
            playQueueResizeStartY = Control.MousePosition.Y;
            playQueueResizeStartHeight = playQueuePanel.Height;
            playQueueResizeGrip.Capture = true;
        }

        private void playQueueResizeGrip_MouseMove(object? sender,
            MouseEventArgs e)
        {
            if (!playQueueResizeGrip.Capture ||
                (Control.MouseButtons & MouseButtons.Left) == 0)
                return;
            int maximum = Math.Max(110,
                ClientSize.Height - menuStrip1.Height - 240);
            playQueueExpandedHeight = Math.Clamp(
                playQueueResizeStartHeight +
                playQueueResizeStartY - Control.MousePosition.Y,
                110, maximum);
            playQueuePanel.Height = playQueueExpandedHeight;
        }

        private void playQueueResizeGrip_MouseUp(object? sender,
            MouseEventArgs e)
        {
            playQueueResizeGrip.Capture = false;
            Properties.Settings.Default.PlayQueueHeight =
                playQueueExpandedHeight;
            playQueueResizeTimer.Stop();
            ResizeBothPlayQueueFlows();
            AdjustControls();
        }

        private void playQueueResizeGrip_Paint(object? sender, PaintEventArgs e)
        {
            e.Graphics.Clear(Properties.Settings.Default.DarkMode
                ? Color.FromArgb(65, 82, 88)
                : Color.FromArgb(190, 200, 204));
            int left = Math.Max(0, (playQueueResizeGrip.Width - 80) / 2);
            using Pen pen = new(Properties.Settings.Default.DarkMode
                ? Color.WhiteSmoke : Color.FromArgb(55, 65, 70), 1.5f);
            e.Graphics.DrawLine(pen, left, 2, left + 80, 2);
            e.Graphics.DrawLine(pen, left, 4, left + 80, 4);
        }

        private Control CreatePlayQueueCard(PlayQueueDrag drag,
            System.Drawing.Size cardSize)
        {
            bool dark = Properties.Settings.Default.DarkMode;
            ModelCard? card = Datastore.findCardByTag(drag.Entry.CardTag);
            string title = card == null
                ? drag.Entry.CardTag
                : $"{card.modelName}\r\n{card.outfit}";
            if (drag.Entry.ClipName != null)
            {
                ModelClip? clip = card?.clips?.FirstOrDefault(item =>
                    string.Equals(item.clipName, drag.Entry.ClipName,
                        StringComparison.OrdinalIgnoreCase));
                title += clip?.clipNumber is int number
                    ? $"\r\nClip {number}" : "\r\nClip";
            }

            Panel panel = new()
            {
                AccessibleDescription =
                    drag.Playing
                        ? "Currently playing. Drag out of the queue to stop it."
                        : "Click to select, double-click to play, or drag to move this queue item.",
                Size = cardSize,
                Margin = new Padding(3),
                Padding = new Padding(2),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = dark
                    ? Color.FromArgb(52, 52, 52) : Color.White,
                Cursor = Cursors.SizeAll,
                Tag = drag
            };
            PictureBox image = new()
            {
                AccessibleDescription = panel.AccessibleDescription,
                Dock = DockStyle.Top,
                Height = Math.Max(25, cardSize.Height - 27),
                BackColor = dark ? Color.FromArgb(32, 32, 32) : Color.Gainsboro,
                Image = card?.image,
                SizeMode = PictureBoxSizeMode.Zoom,
                Cursor = Cursors.SizeAll
            };
            image.Paint += (_, e) =>
            {
                DrawPlayQueueCardOverlay(
                    image, drag.Entry.CardTag, e.Graphics);
                if (drag.Playing)
                    DrawPlayingQueueOverlay(image, e.Graphics);
            };
            Label label = new()
            {
                AccessibleDescription = panel.AccessibleDescription,
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                Text = title.Replace("\r\n", " · "),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 7.5F),
                ForeColor = dark ? Color.AntiqueWhite : Color.Black,
                Cursor = Cursors.SizeAll
            };
            MouseEventHandler down = (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    libraryQueueDragStart = Control.MousePosition;
            };
            MouseEventHandler move = (_, e) =>
            {
                if (e.Button != MouseButtons.Left ||
                    !HasMovedPastDragThreshold(libraryQueueDragStart))
                    return;
                StartPlayQueueDrag(panel, drag);
            };
            MouseEventHandler click = (_, e) =>
            {
                if (e.Button == MouseButtons.Left && e.Clicks == 1)
                    SelectCardInModelList(drag.Entry.CardTag);
            };
            MouseEventHandler doubleClick = (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    PlayQueueEntryImmediately(drag);
            };
            panel.MouseDown += down;
            panel.MouseMove += move;
            panel.MouseClick += click;
            panel.MouseDoubleClick += doubleClick;
            image.MouseDown += down;
            image.MouseMove += move;
            image.MouseClick += click;
            image.MouseDoubleClick += doubleClick;
            label.MouseDown += down;
            label.MouseMove += move;
            label.MouseClick += click;
            label.MouseDoubleClick += doubleClick;
            EventHandler enter = (_, _) => HighlightPlayQueueCard(panel);
            EventHandler leave = (_, _) =>
                QueuePlayQueueCardHighlightClear(panel);
            panel.MouseEnter += enter;
            panel.MouseLeave += leave;
            image.MouseEnter += enter;
            image.MouseLeave += leave;
            label.MouseEnter += enter;
            label.MouseLeave += leave;
            panel.Controls.Add(label);
            panel.Controls.Add(image);
            image.Tag = drag;
            label.Tag = drag;
            return panel;
        }

        private static void DrawPlayingQueueOverlay(
            PictureBox picture, Graphics graphics)
        {
            int height = Math.Min(24, Math.Max(18, picture.Height / 5));
            Rectangle badge = new(0, Math.Max(0, picture.Height - height),
                picture.Width, height);
            using Brush background =
                new SolidBrush(Color.FromArgb(220, 22, 145, 70));
            graphics.FillRectangle(background, badge);
            TextRenderer.DrawText(graphics, "Playing",
                SystemFonts.MessageBoxFont, badge, Color.White,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine);
        }

        private static void DrawPlayQueueCardOverlay(
            PictureBox picture, string cardTag, Graphics graphics)
        {
            if (!Properties.Settings.Default.DrawCardOverlays ||
                CardOverlayLoader.DrawWithDirectComposition ||
                !CardOverlayLoader.TryGetFrame(
                    cardTag, out CardOverlayFrame frame))
                return;
            graphics.DrawImage(
                frame.Image,
                DirectCompositionCardOverlayControl.GetZoomBounds(picture),
                frame.Source,
                GraphicsUnit.Pixel);
        }

        private void UpdatePlayQueueCardOverlays()
        {
            if (directCompositionCardOverlays == null ||
                manualQueueFlow == null)
                return;
            directCompositionCardOverlays.SetQueueCards(
                QueueOverlayCards(manualQueueFlow)
                    .Concat(QueueOverlayCards(automaticQueueFlow)));
        }

        private static IEnumerable<(
            Control Host, PictureBox Picture, string CardTag,
            bool Playing)>
            QueueOverlayCards(FlowLayoutPanel flow)
        {
            foreach (Panel panel in flow.Controls.OfType<Panel>())
            {
                if (panel.Tag is not PlayQueueDrag drag)
                    continue;
                PictureBox? picture =
                    panel.Controls.OfType<PictureBox>().FirstOrDefault();
                if (picture != null)
                    yield return (
                        flow, picture, drag.Entry.CardTag, drag.Playing);
            }
        }

        private void RefreshPlayQueueCardOverlays()
        {
            if (manualQueueFlow == null)
                return;
            foreach (PictureBox picture in
                QueueOverlayCards(manualQueueFlow)
                    .Concat(QueueOverlayCards(automaticQueueFlow))
                    .Where(card => CardOverlayLoader.HasAnimatedOverlay(
                        card.CardTag))
                    .Select(card => card.Picture))
                picture.Invalidate();
        }

        private void PlayQueueEntryImmediately(PlayQueueDrag drag)
        {
            if (panicActive)
                return;

            PlayQueueEntry entry = drag.Entry;
            SelectCardInModelList(entry.CardTag);
            if (drag.Playing)
                return;
            bool manual = drag.Source == "manual";
            List<PlayQueueEntry> queue = manual
                ? manualPlayQueue : automaticPlayQueue;
            if (!TryResolveQueueEntry(entry, out string animationPath,
                    useSmartRules: manual) ||
                !TryRemoveQueueEntry(queue, drag))
                return;

            ClearQueuedCardSession();
            StartQueuedCardSession(entry, animationPath,
                useSmartRules: manual);
            if (manual)
            {
                activeManualQueueEntry = entry;
                SavePreviousQueue();
            }
            else
            {
                activeAutomaticQueueEntry = entry;
                FillAutomaticQueue(entry.CardTag);
            }
            RenderPlayQueues();
            queuedAnimationPendingPath = animationPath;
            queuedAnimationPendingConfirmed = false;
            queuedAnimationProtectedUntil = DateTime.MinValue;
            if (!RequestAnimationPlayback(animationPath))
            {
                queue.Insert(Math.Min(drag.Index, queue.Count), entry);
                return;
            }
            SelectQueuedCard(entry.CardTag, animationPath);
            BeginInvoke((Action)TaskbarThumbnail);
        }

        private static bool TryRemoveQueueEntry(List<PlayQueueEntry> queue,
            PlayQueueDrag drag)
        {
            int index = drag.Index >= 0 && drag.Index < queue.Count &&
                queue[drag.Index] == drag.Entry
                    ? drag.Index : queue.IndexOf(drag.Entry);
            if (index < 0)
                return false;
            queue.RemoveAt(index);
            return true;
        }

        internal static bool VerifyQueueEntryRemoval()
        {
            PlayQueueEntry first = new("first");
            PlayQueueEntry second = new("second");
            List<PlayQueueEntry> queue = [first, second];
            PlayQueueDrag active = new(
                "automatic", -1, new PlayQueueEntry("active"), true);
            if (TryRemoveQueueEntry(queue, active) || queue.Count != 2)
                return false;

            PlayQueueDrag staleIndex = new("automatic", 99, second);
            return TryRemoveQueueEntry(queue, staleIndex) &&
                queue.SequenceEqual([first]);
        }

        private void HighlightPlayQueueCard(Panel panel)
        {
            if (highlightedPlayQueueCard == panel)
                return;

            ClearPlayQueueCardHighlight();
            highlightedPlayQueueCard = panel;
            panel.BackColor = Color.FromArgb(35, 185, 95);
        }

        private void QueuePlayQueueCardHighlightClear(Panel panel)
        {
            if (highlightedPlayQueueCard != panel || IsDisposed)
                return;

            BeginInvoke(() =>
            {
                if (highlightedPlayQueueCard != panel ||
                    panel.RectangleToScreen(panel.ClientRectangle)
                        .Contains(Control.MousePosition))
                    return;
                ClearPlayQueueCardHighlight();
            });
        }

        private void ClearPlayQueueCardHighlight()
        {
            Panel? panel = highlightedPlayQueueCard;
            if (panel == null)
                return;

            highlightedPlayQueueCard = null;
            if (!panel.IsDisposed)
                panel.BackColor = Properties.Settings.Default.DarkMode
                    ? Color.FromArgb(52, 52, 52) : Color.White;
        }

        private void StartPlayQueueDrag(Control sourceControl,
            PlayQueueDrag drag)
        {
            ClearPlayQueueCardHighlight();
            DataObject data = new();
            data.SetData(PlayQueueDragFormat, drag);
            DragDropEffects result = sourceControl.DoDragDrop(data,
                DragDropEffects.Move | DragDropEffects.Copy);
            if (result != DragDropEffects.None ||
                manualQueueFlow.RectangleToScreen(
                    manualQueueFlow.ClientRectangle).Contains(
                    Control.MousePosition) ||
                automaticQueueFlow.RectangleToScreen(
                    automaticQueueFlow.ClientRectangle).Contains(
                    Control.MousePosition))
            {
                return;
            }

            if (drag.Playing)
            {
                RemovePlayingQueueEntry(drag);
                return;
            }

            List<PlayQueueEntry> queue = drag.Source == "manual"
                ? manualPlayQueue : automaticPlayQueue;
            if (drag.Index >= 0 && drag.Index < queue.Count &&
                queue[drag.Index] == drag.Entry)
            {
                queue.RemoveAt(drag.Index);
            }
            else
            {
                queue.Remove(drag.Entry);
            }
            if (drag.Source == "manual")
                SavePreviousQueue();
            if (drag.Source == "automatic")
                FillAutomaticQueue(drag.Entry.CardTag);
            RenderPlayQueues();
        }

        private void RemovePlayingQueueEntry(PlayQueueDrag drag)
        {
            PlayQueueEntry? active = drag.Source == "manual"
                ? activeManualQueueEntry : activeAutomaticQueueEntry;
            if (active != drag.Entry)
                return;

            queuedAnimationPendingPath = "";
            queuedAnimationPendingConfirmed = false;
            queuedAnimationProtectedUntil = DateTime.MinValue;
            ClearQueuedCardSession(discardManualQueueEntry: true);
            SavePreviousQueue();
            if (!TryPlayNextQueuedAnimation())
                StopQueuedPlayback();
            RenderPlayQueues();
        }

        private static void StopQueuedPlayback()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Totem\vghd\parameters", true);
            key?.SetValue("ForceAnim", "");
            key?.SetValue("CurrentAnim", "");
        }

        private static bool HasMovedPastDragThreshold(Point start)
        {
            System.Drawing.Size drag = SystemInformation.DragSize;
            return Math.Abs(Control.MousePosition.X - start.X) >
                drag.Width / 2 ||
                Math.Abs(Control.MousePosition.Y - start.Y) >
                drag.Height / 2;
        }

        private void RememberPlayQueueCardDrag(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left ||
                !Properties.Settings.Default.EnablePlayQueue)
                return;

            listModelsNew.HitTest(e.Location, out ImageListView.HitInfo hit);
            libraryQueueDragEntry = hit.ItemHit
                ? new PlayQueueEntry(
                    listModelsNew.Items[hit.ItemIndex].Tag?.ToString() ?? "")
                : null;
            libraryQueueDragStart = Control.MousePosition;
        }

        private void listModelsNew_MouseMoveForQueue(object? sender,
            MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left ||
                libraryQueueDragEntry == null ||
                !HasMovedPastDragThreshold(libraryQueueDragStart))
                return;

            PlayQueueDrag drag = new("library", -1, libraryQueueDragEntry);
            libraryQueueDragEntry = null;
            DataObject data = new();
            data.SetData(PlayQueueDragFormat, drag);
            listModelsNew.DoDragDrop(data, DragDropEffects.Copy);
            Utils.SendMessage(listModelsNew.Handle, 0x0202,
                IntPtr.Zero, new IntPtr(-1));
            listModelsNew.Focus();
            cardRenderer.MouseIsOnList = false;
            listModelsNew.Refresh();
        }

        private void listClips_ItemDragForQueue(object? sender,
            ItemDragEventArgs e)
        {
            if (!Properties.Settings.Default.EnablePlayQueue ||
                e.Item is not ListViewItem item || item.SubItems.Count < 2 ||
                string.IsNullOrEmpty(clipListTag))
                return;

            clipDragInProgress = true;
            clipSelectionPlaybackPending = false;
            clipSelectionPlaybackTimer.Stop();
            try
            {
                DataObject data = new();
                data.SetData(PlayQueueDragFormat, new PlayQueueDrag("clip", -1,
                    new PlayQueueEntry(clipListTag, item.SubItems[1].Text)));
                listClips.DoDragDrop(data, DragDropEffects.Copy);
            }
            finally
            {
                clipDragInProgress = false;
            }
        }

        private bool DeferClipSelectionPlaybackWhileMouseIsDown()
        {
            if ((Control.MouseButtons & MouseButtons.Left) == 0)
                return false;
            clipSelectionPlaybackPending = true;
            clipSelectionPlaybackTimer.Start();
            return true;
        }

        private void listClips_MouseUpForQueue(object? sender,
            MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || clipDragInProgress ||
                listClips.HitTest(e.Location).Item == null)
                return;
            clipSelectionPlaybackTimer.Stop();
            clipSelectionPlaybackPending = false;
            if (listClips.SelectedItems.Count > 0 &&
                lastchosen != listClips.SelectedItems[0].SubItems[1].Text)
                PlaySelectedClip();
        }

        private static PlayQueueDrag? GetPlayQueueDrag(DragEventArgs e) =>
            e.Data?.GetDataPresent(PlayQueueDragFormat) == true
                ? e.Data.GetData(PlayQueueDragFormat) as PlayQueueDrag
                : null;

        private void QueueFlow_DragEnter(object? sender, DragEventArgs e)
        {
            PlayQueueDrag? drag = GetPlayQueueDrag(e);
            e.Effect = drag == null || drag.Playing
                ? DragDropEffects.None :
                drag.Source is "library" or "clip"
                    ? DragDropEffects.Copy : DragDropEffects.Move;
        }

        private void QueueFlow_DragOver(object? sender, DragEventArgs e) =>
            QueueFlow_DragEnter(sender, e);

        private void AutomaticQueueFlow_DragEnter(object? sender,
            DragEventArgs e)
        {
            PlayQueueDrag? drag = GetPlayQueueDrag(e);
            e.Effect = drag is { Source: "automatic", Playing: false }
                ? DragDropEffects.Move : DragDropEffects.None;
        }

        private void ManualQueueFlow_DragDrop(object? sender, DragEventArgs e)
        {
            PlayQueueDrag? drag = GetPlayQueueDrag(e);
            if (drag == null || drag.Playing)
                return;

            int insertAt = QueueDropIndex(manualQueueFlow, e);
            if (drag.Source == "manual")
            {
                RemoveMovedQueueEntry(manualPlayQueue, drag, ref insertAt);
            }
            else if (drag.Source == "automatic")
            {
                int ignored = int.MaxValue;
                RemoveMovedQueueEntry(automaticPlayQueue, drag, ref ignored);
            }
            manualPlayQueue.Insert(
                Math.Clamp(insertAt, 0, manualPlayQueue.Count), drag.Entry);
            automaticPlayQueue.RemoveAll(entry => string.Equals(
                entry.CardTag, drag.Entry.CardTag,
                StringComparison.OrdinalIgnoreCase));
            SavePreviousQueue();
            FillAutomaticQueue();
            BeginInvoke((Action)RenderPlayQueues);
            e.Effect = drag.Source is "library" or "clip"
                ? DragDropEffects.Copy : DragDropEffects.Move;
        }

        private void AutomaticQueueFlow_DragDrop(object? sender,
            DragEventArgs e)
        {
            PlayQueueDrag? drag = GetPlayQueueDrag(e);
            if (drag is not { Source: "automatic", Playing: false })
                return;

            int insertAt = QueueDropIndex(automaticQueueFlow, e);
            RemoveMovedQueueEntry(automaticPlayQueue, drag, ref insertAt);
            automaticPlayQueue.Insert(
                Math.Clamp(insertAt, 0, automaticPlayQueue.Count), drag.Entry);
            BeginInvoke((Action)RenderPlayQueues);
            e.Effect = DragDropEffects.Move;
        }

        private static void RemoveMovedQueueEntry(List<PlayQueueEntry> queue,
            PlayQueueDrag drag, ref int insertAt)
        {
            int oldIndex = drag.Index >= 0 && drag.Index < queue.Count &&
                queue[drag.Index] == drag.Entry
                    ? drag.Index : queue.IndexOf(drag.Entry);
            if (oldIndex < 0)
                return;
            queue.RemoveAt(oldIndex);
            if (oldIndex < insertAt)
                insertAt--;
        }

        private static int QueueDropIndex(FlowLayoutPanel flow,
            DragEventArgs e)
        {
            Point point = flow.PointToClient(new Point(e.X, e.Y));
            for (int index = 0; index < flow.Controls.Count; index++)
            {
                Control control = flow.Controls[index];
                if (point.X < control.Left + control.Width / 2)
                    return index;
            }
            return flow.Controls.Count;
        }

        private void RebuildAutomaticQueue()
        {
            if (apiOnlyMode)
            {
                automaticPlayQueue.Clear();
                activeAutomaticQueueEntry = null;
                return;
            }
            if (automaticQueueFlow == null)
                return;

            automaticPlayQueue.Clear();
            if (!Properties.Settings.Default.EnablePlayQueue ||
                !Properties.Settings.Default.EnforceCardFilter ||
                items == null)
            {
                RenderPlayQueues();
                return;
            }

            int target = Math.Max(0,
                Properties.Settings.Default.AutoQueueLength);
            automaticPlayQueue.AddRange(SelectAutomaticQueueEntries(
                target, [], nowPlayingTagShort));
            RenderPlayQueues();
        }

        private List<SmartQueueCandidate> EligibleAutomaticQueueEntries()
        {
            if (items == null)
                return [];

            List<SmartQueueCandidate> candidates = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> manualCards = manualPlayQueue
                .Select(entry => entry.CardTag)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            (HashSet<string> cooldownCards,
                HashSet<string> cooldownModels) = SmartQueueCooldown();
            foreach (ListViewItem item in items)
            {
                string tag = item.Tag?.ToString() ?? "";
                ModelCard? card = Datastore.findCardByTag(tag);
                if (string.IsNullOrEmpty(tag) || manualCards.Contains(tag) ||
                    !seen.Add(tag) ||
                    !PlaybackCardAllowed(card) || card!.clips == null ||
                    FilterClipList(card.clips).Count == 0)
                    continue;
                int relaxation = SmartQueueRelaxation(
                    cooldownCards.Contains(tag),
                    Properties.Settings.Default.SmartQueueCooldownByModel &&
                    !string.IsNullOrEmpty(card.modelName) &&
                    cooldownModels.Contains(card.modelName),
                    myData?.GetCardFavourite(tag) == true,
                    Properties.Settings.Default.SmartQueueFavouritesOnly);
                candidates.Add(new(new(tag), relaxation));
            }

            if (candidates.Count > 1)
                candidates.RemoveAll(candidate => string.Equals(
                    candidate.Entry.CardTag, nowPlayingTagShort,
                    StringComparison.OrdinalIgnoreCase));
            return candidates;
        }

        private void FillAutomaticQueue(string? excludedCardTag = null)
        {
            if (apiOnlyMode)
                return;
            if (!Properties.Settings.Default.EnablePlayQueue ||
                !Properties.Settings.Default.EnforceCardFilter)
                return;

            int target = Math.Max(0,
                Properties.Settings.Default.AutoQueueLength);
            HashSet<string> excluded = automaticPlayQueue
                .Select(entry => entry.CardTag)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(excludedCardTag))
                excluded.Add(excludedCardTag);
            string previousCardTag =
                automaticPlayQueue.LastOrDefault()?.CardTag ??
                excludedCardTag ?? nowPlayingTagShort;
            automaticPlayQueue.AddRange(SelectAutomaticQueueEntries(
                Math.Max(0, target - automaticPlayQueue.Count),
                excluded, previousCardTag));
        }

        private List<PlayQueueEntry> SelectAutomaticQueueEntries(
            int count, IEnumerable<string> excludedCardTags,
            string previousCardTag)
        {
            HashSet<string> excluded = excludedCardTags.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            List<PlayQueueEntry> selected = [];
            foreach (IGrouping<int, SmartQueueCandidate> tier in
                EligibleAutomaticQueueEntries()
                    .Where(candidate =>
                        !excluded.Contains(candidate.Entry.CardTag))
                    .GroupBy(candidate => candidate.Relaxation)
                    .OrderBy(group => group.Key))
            {
                List<PlayQueueEntry> ordered = ApplySmartQueueRules(
                    tier.Select(candidate => candidate.Entry).ToList(),
                    selected.LastOrDefault()?.CardTag ?? previousCardTag);
                selected.AddRange(ordered.Take(count - selected.Count));
                if (selected.Count >= count)
                    break;
            }
            return selected;
        }

        private static int SmartQueueRelaxation(bool cardOnCooldown,
            bool modelOnCooldown, bool favourite, bool favouritesOnly)
        {
            if (cardOnCooldown)
                return 3;
            if (favouritesOnly && !favourite)
                return 2;
            return modelOnCooldown ? 1 : 0;
        }

        private void SmartQueueRuleChanged(object? sender, EventArgs e)
        {
            Properties.Settings.Default.SmartQueueFavouritesOnly =
                smartQueueFavouritesToolStripMenuItem.Checked;
            Properties.Settings.Default.SmartQueueLeastRecentlyPlayed =
                smartQueueLeastRecentToolStripMenuItem.Checked;
            Properties.Settings.Default.SmartQueueRotateModels =
                smartQueueRotateModelsToolStripMenuItem.Checked;
            Properties.Settings.Default.SmartQueueUnplayedClipsFirst =
                smartQueueUnplayedClipsToolStripMenuItem.Checked;
            Properties.Settings.Default.SmartQueueWeightedPreferences =
                smartQueueWeightedToolStripMenuItem.Checked;
            Properties.Settings.Default.SmartQueueNewestPurchasesFirst =
                smartQueueNewestToolStripMenuItem.Checked;
            Properties.Settings.Default.SmartQueueCooldownByModel =
                smartQueueCooldownByModelToolStripMenuItem.Checked;
            Properties.Settings.Default.Save();
            RebuildAutomaticQueue();
        }

        private void UpdateSmartQueueIgnoreScoresLabel()
        {
            smartQueueIgnoreScoresLabel.Text =
                "Chance of random choice: " +
                $"{smartQueueIgnoreScoresTrackBar.Value}%";
            smartQueueIgnoreScoresLabel.ToolTipText =
                "Chance each queued card is picked randomly instead of " +
                "using the favour scores.";
        }

        private void ApplySmartQueueSliderTheme()
        {
            Color background =
                smartQueueToolStripMenuItem.DropDown.BackColor;
            if (smartQueueIgnoreScoresHost != null)
                smartQueueIgnoreScoresHost.BackColor = background;
            smartQueueIgnoreScoresTrackBar.BackColor = background;
            smartQueueIgnoreScoresTrackBar.Invalidate();
        }

        private void CommitSmartQueueSettings()
        {
            Properties.Settings.Default.Save();
            RebuildAutomaticQueue();
        }

        private (HashSet<string> Cards, HashSet<string> Models)
            SmartQueueCooldown()
        {
            int hours = Properties.Settings.Default.SmartQueueCooldownHours;
            if (hours <= 0 || myData == null)
            {
                return (new(StringComparer.OrdinalIgnoreCase),
                    new(StringComparer.OrdinalIgnoreCase));
            }

            HashSet<string> cards;
            lock (playbackHistoryLock)
            {
                cards = CooldownCardTags(myData.PlaybackHistory,
                    DateTime.UtcNow.AddHours(-hours));
            }
            HashSet<string> models = cards.Select(tag =>
                    Datastore.findCardByTag(tag)?.modelName)
                .Where(model => !string.IsNullOrEmpty(model))
                .Select(model => model!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (cards, models);
        }

        private static HashSet<string> CooldownCardTags(
            IEnumerable<PlaybackHistoryEntry> history, DateTime cutoffUtc) =>
            history.Where(entry => entry.PlayedUtc >= cutoffUtc &&
                    !string.IsNullOrWhiteSpace(entry.AnimationPath))
                .Select(entry => entry.AnimationPath
                    .Replace('/', '\\').Split('\\')[0])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private List<PlayQueueEntry> ApplySmartQueueRules(
            List<PlayQueueEntry> candidates, string previousCardTag)
        {
            Dictionary<string, double> scores = candidates.ToDictionary(
                entry => entry.CardTag, _ => 0d,
                StringComparer.OrdinalIgnoreCase);
            bool hasCardPreference = false;

            if (Properties.Settings.Default.SmartQueueWeightedPreferences &&
                myData != null)
            {
                hasCardPreference = true;
                foreach (PlayQueueEntry entry in candidates)
                {
                    scores[entry.CardTag] += RatingFavouritePreference(
                        myData.GetCardFavourite(entry.CardTag),
                        myData.GetCardRating(entry.CardTag));
                }
            }

            if (Properties.Settings.Default.SmartQueueNewestPurchasesFirst)
            {
                hasCardPreference = true;
                AddRankScores(scores,
                    candidates.OrderByDescending(entry =>
                        Datastore.findCardByTag(entry.CardTag)?.datePurchased ??
                        DateTime.MinValue));
            }

            if (Properties.Settings.Default.SmartQueueLeastRecentlyPlayed &&
                myData != null)
            {
                hasCardPreference = true;
                Dictionary<string, DateTime> lastPlayed;
                lock (playbackHistoryLock)
                {
                    lastPlayed = myData.PlaybackHistory
                        .Where(entry => !string.IsNullOrWhiteSpace(
                            entry.AnimationPath))
                        .GroupBy(entry => entry.AnimationPath.Split('\\')[0],
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key,
                            group => group.Max(entry => entry.PlayedUtc),
                            StringComparer.OrdinalIgnoreCase);
                }
                AddRankScores(scores,
                    candidates.OrderBy(entry =>
                        lastPlayed.TryGetValue(entry.CardTag,
                            out DateTime played)
                                ? played : DateTime.MinValue),
                    1);
            }

            if (hasCardPreference)
            {
                candidates = OrderBySmartQueueScores(candidates, scores,
                    Properties.Settings.Default
                        .SmartQueueIgnoreScoresPercent);
            }
            else if (Properties.Settings.Default.Randomize)
            {
                Shuffle(candidates);
            }

            if (Properties.Settings.Default.SmartQueueRotateModels)
            {
                Dictionary<string, string> models = candidates.ToDictionary(
                    entry => entry.CardTag,
                    entry => Datastore.findCardByTag(entry.CardTag)
                        ?.modelName ?? "",
                    StringComparer.OrdinalIgnoreCase);
                string previousModel = Datastore.findCardByTag(
                    previousCardTag)?.modelName ?? "";
                candidates = RotateQueueModels(
                    candidates, models, previousModel);
            }
            return candidates;
        }

        private static double RatingFavouritePreference(bool favourite,
            decimal rating) => (favourite ? 0.5 : 0) +
                (double)Math.Clamp(rating, 0, 10) / 20;

        private static List<PlayQueueEntry> OrderBySmartQueueScores(
            IEnumerable<PlayQueueEntry> candidates,
            IReadOnlyDictionary<string, double> scores,
            int ignoreScoresPercent)
        {
            List<PlayQueueEntry> remaining = candidates.ToList();
            List<PlayQueueEntry> ordered = new(remaining.Count);
            int randomPercent = Math.Clamp(ignoreScoresPercent, 0, 100);
            while (remaining.Count > 0)
            {
                int index = Random.Shared.Next(100) < randomPercent
                    ? Random.Shared.Next(remaining.Count)
                    : HighestScoreIndex(remaining, scores);
                ordered.Add(remaining[index]);
                remaining.RemoveAt(index);
            }
            return ordered;
        }

        private static int HighestScoreIndex(
            IReadOnlyList<PlayQueueEntry> candidates,
            IReadOnlyDictionary<string, double> scores)
        {
            int best = 0;
            for (int index = 1; index < candidates.Count; index++)
            {
                if (scores[candidates[index].CardTag] >
                    scores[candidates[best].CardTag])
                    best = index;
            }
            return best;
        }

        private static void AddRankScores(
            IDictionary<string, double> scores,
            IEnumerable<PlayQueueEntry> orderedCandidates,
            double maximumScore = 1)
        {
            List<PlayQueueEntry> ordered = orderedCandidates.ToList();
            for (int index = 0; index < ordered.Count; index++)
                scores[ordered[index].CardTag] += maximumScore *
                    (ordered.Count <= 1
                        ? 1 : 1d - (double)index / (ordered.Count - 1));
        }

        private static List<PlayQueueEntry> RotateQueueModels(
            IEnumerable<PlayQueueEntry> candidates,
            IReadOnlyDictionary<string, string> models,
            string previousModel)
        {
            List<PlayQueueEntry> remaining = candidates.ToList();
            List<PlayQueueEntry> ordered = [];
            // ponytail: O(n²) is simpler and bounded by the library size;
            // bucket models only if this becomes measurable.
            while (remaining.Count > 0)
            {
                int index = remaining.FindIndex(entry =>
                    !string.Equals(models[entry.CardTag], previousModel,
                        StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    index = 0;
                PlayQueueEntry selected = remaining[index];
                remaining.RemoveAt(index);
                ordered.Add(selected);
                previousModel = models[selected.CardTag];
            }
            return ordered;
        }

        private static void Shuffle<T>(IList<T> values)
        {
            for (int index = values.Count - 1; index > 0; index--)
            {
                int other = Random.Shared.Next(index + 1);
                (values[index], values[other]) =
                    (values[other], values[index]);
            }
        }

        private bool TryTakeQueuedAnimation(out string animationPath,
            out string cardTag)
        {
            animationPath = "";
            cardTag = "";
            if (!Properties.Settings.Default.EnablePlayQueue)
            {
                ClearQueuedCardSession();
                return false;
            }

            bool completedManualItemRequeued = false;
            if (activeManualQueueEntry != null && activeQueuedCard == null)
                completedManualItemRequeued =
                    CompleteActiveManualQueueEntry();
            if (activeAutomaticQueueEntry != null &&
                activeQueuedCard == null)
                activeAutomaticQueueEntry = null;
            if (TryContinueQueuedCard(out animationPath, out cardTag))
                return true;
            completedManualItemRequeued |= CompleteActiveManualQueueEntry();

            while (manualPlayQueue.Count > 0)
            {
                int selectableCount = ManualQueueSelectableCount(
                    manualPlayQueue.Count, completedManualItemRequeued);
                if (selectableCount == 0)
                    break;
                List<int> selectableIndexes = Enumerable.Range(0,
                        selectableCount)
                    .Where(index => QueueEntryAllowedForPlayback(
                        manualPlayQueue[index])).ToList();
                if (selectableIndexes.Count == 0)
                    break;
                int index = Properties.Settings.Default
                    .RandomManualQueueSelection
                        ? selectableIndexes[Random.Shared.Next(
                            selectableIndexes.Count)]
                        : selectableIndexes[0];
                PlayQueueEntry entry = manualPlayQueue[index];
                manualPlayQueue.RemoveAt(index);
                if (TryResolveQueueEntry(entry, out animationPath,
                        useSmartRules: true))
                {
                    cardTag = entry.CardTag;
                    StartQueuedCardSession(
                        entry, animationPath, useSmartRules: true);
                    activeManualQueueEntry = entry;
                    SavePreviousQueue();
                    RenderPlayQueues();
                    return true;
                }
            }

            while (!apiOnlyMode &&
                Properties.Settings.Default.EnforceCardFilter &&
                automaticPlayQueue.Count > 0)
            {
                int index = automaticPlayQueue.FindIndex(
                    QueueEntryAllowedForPlayback);
                if (index < 0)
                    break;
                PlayQueueEntry entry = automaticPlayQueue[index];
                automaticPlayQueue.RemoveAt(index);
                FillAutomaticQueue(entry.CardTag);
                if (TryResolveQueueEntry(entry, out animationPath))
                {
                    cardTag = entry.CardTag;
                    StartQueuedCardSession(entry, animationPath);
                    activeAutomaticQueueEntry = entry;
                    RenderPlayQueues();
                    return true;
                }
            }

            RenderPlayQueues();
            return false;
        }

        private bool TryResolveQueueEntry(PlayQueueEntry entry,
            out string animationPath, string previousAnimationPath = "",
            bool useSmartRules = false)
        {
            animationPath = "";
            ModelCard? card = Datastore.findCardByTag(entry.CardTag);
            if (!PlaybackCardAllowed(card) || card!.clips == null ||
                card.clips.Count == 0)
                return false;

            if (!string.IsNullOrEmpty(entry.ClipName))
            {
                ModelClip? exact = card.clips.FirstOrDefault(clip =>
                    string.Equals(clip.clipName, entry.ClipName,
                        StringComparison.OrdinalIgnoreCase));
                if (exact == null)
                    return false;
                animationPath = GetAnimationPath(exact);
                return !string.IsNullOrEmpty(animationPath);
            }

            List<ModelClip> clips = apiOnlyMode
                ? card.clips.Where(clip =>
                    !string.IsNullOrWhiteSpace(clip.clipName)).ToList()
                : FilterClipList(card.clips);
            if (clips.Count == 0)
                return false;
            bool sequentialContinuation =
                !Properties.Settings.Default.Randomize &&
                !string.IsNullOrEmpty(previousAnimationPath);
            if (!sequentialContinuation && useSmartRules &&
                Properties.Settings.Default.SmartQueueUnplayedClipsFirst &&
                myData != null)
            {
                HashSet<string> played;
                lock (playbackHistoryLock)
                {
                    played = myData.PlaybackHistory.Select(history =>
                            history.AnimationPath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                List<ModelClip> unplayed = ExcludeRecentClips(clips, played);
                if (unplayed.Count > 0)
                    clips = unplayed;
            }
            bool progressive = IsProgressiveHotnessEnabled();
            ModelClip? previous = clips.FirstOrDefault(clip =>
                string.Equals(GetAnimationPath(clip), previousAnimationPath,
                    StringComparison.OrdinalIgnoreCase));
            ModelClip selected;
            if (sequentialContinuation || progressive)
            {
                List<ModelClip> ordered = clips.OrderBy(clip =>
                    clip.clipNumber).ToList();
                selected = previous == null
                    ? ordered[Random.Shared.Next(ordered.Count)]
                    : ordered.FirstOrDefault(clip =>
                        clip.clipNumber > previous.clipNumber) ?? ordered[0];
            }
            else
            {
                List<ModelClip> alternatives = clips.Where(clip =>
                    !string.Equals(GetAnimationPath(clip),
                        previousAnimationPath,
                        StringComparison.OrdinalIgnoreCase)).ToList();
                if (alternatives.Count > 0)
                    clips = alternatives;
                List<ModelClip> fresh = ExcludeRecentClips(clips,
                    GetRecentPlaybackPaths());
                if (fresh.Count > 0)
                    clips = fresh;
                selected = clips[Random.Shared.Next(clips.Count)];
            }
            animationPath = GetAnimationPath(selected);
            return !string.IsNullOrEmpty(animationPath);
        }

        private bool QueueEntryAllowedForPlayback(PlayQueueEntry entry) =>
            PlaybackCardAllowed(Datastore.findCardByTag(entry.CardTag));

        private bool TryContinueQueuedCard(out string animationPath,
            out string cardTag)
        {
            animationPath = "";
            cardTag = "";
            if (activeQueuedCard == null)
                return false;
            if (!QueueEntryAllowedForPlayback(activeQueuedCard) ||
                !ShouldContinueQueuedCard(activeQueuedCardStartedAt,
                    Environment.TickCount64, ReadShowDurationMinutes()) ||
                !TryResolveQueueEntry(activeQueuedCard, out animationPath,
                    activeQueuedCardLastAnimationPath,
                    activeQueuedCardUsesSmartRules))
            {
                ClearQueuedCardSession(clearManualQueueEntry: false);
                return false;
            }

            cardTag = activeQueuedCard.CardTag;
            activeQueuedCardLastAnimationPath = animationPath;
            return true;
        }

        private void StartQueuedCardSession(PlayQueueEntry entry,
            string animationPath, bool useSmartRules = false)
        {
            if (!string.IsNullOrEmpty(entry.ClipName))
            {
                ClearQueuedCardSession();
                return;
            }

            activeQueuedCard = entry;
            activeQueuedCardStartedAt = Environment.TickCount64;
            activeQueuedCardLastAnimationPath = animationPath;
            activeQueuedCardUsesSmartRules = useSmartRules;
        }

        private bool CompleteActiveManualQueueEntry()
        {
            bool requeue = Properties.Settings.Default
                .RequeueCompletedManualItems &&
                activeManualQueueEntry != null &&
                IsAvailableQueueEntry(activeManualQueueEntry);
            FinishManualQueueEntry(manualPlayQueue,
                ref activeManualQueueEntry, requeue);
            SavePreviousQueue();
            return requeue;
        }

        private static void FinishManualQueueEntry(
            List<PlayQueueEntry> queue, ref PlayQueueEntry? active,
            bool requeue)
        {
            if (active == null)
                return;
            if (requeue)
                queue.Add(active);
            active = null;
        }

        private static int ManualQueueSelectableCount(
            int queueCount, bool completedItemRequeued) =>
            Math.Max(0, queueCount - (completedItemRequeued ? 1 : 0));

        private void ClearQueuedCardSession(
            bool clearManualQueueEntry = true,
            bool discardManualQueueEntry = false)
        {
            activeQueuedCard = null;
            activeAutomaticQueueEntry = null;
            activeQueuedCardStartedAt = -1;
            activeQueuedCardLastAnimationPath = "";
            activeQueuedCardUsesSmartRules = false;
            if (clearManualQueueEntry)
            {
                bool requeue = !discardManualQueueEntry &&
                    Properties.Settings.Default.RequeueCompletedManualItems &&
                    activeManualQueueEntry != null &&
                    IsAvailableQueueEntry(activeManualQueueEntry);
                FinishManualQueueEntry(manualPlayQueue,
                    ref activeManualQueueEntry, requeue);
                SavePreviousQueue();
            }
            RenderPlayQueues();
        }

        private static bool ShouldContinueQueuedCard(long startedAt,
            long now, int durationMinutes) =>
            startedAt >= 0 && durationMinutes > 0 &&
            now - startedAt <= durationMinutes * 60_000L;

        private static int ReadShowDurationMinutes() =>
            ReadRegistryInteger(@"Software\Totem\vghd\player", "duration");

        private void ObserveUnqueuedCardPlayback(string animationPath)
        {
            if (activeQueuedCard != null ||
                string.IsNullOrEmpty(animationPath))
                return;
            string cardTag = GetCardTagFromAnimationPath(animationPath);
            if (string.Equals(activeUnqueuedCardTag, cardTag,
                    StringComparison.OrdinalIgnoreCase))
                return;
            activeUnqueuedCardTag = cardTag;
            activeUnqueuedCardStartedAt = Environment.TickCount64;
            activeUnqueuedCardSequential = false;
        }

        private void StartUnqueuedCardSession(string animationPath,
            bool sequential)
        {
            activeUnqueuedCardTag = GetCardTagFromAnimationPath(animationPath);
            activeUnqueuedCardStartedAt = Environment.TickCount64;
            activeUnqueuedCardSequential = sequential;
        }

        private bool CanContinueUnqueuedCard(string animationPath)
        {
            return IsUnqueuedCardSession(animationPath) &&
                ShouldContinueQueuedCard(activeUnqueuedCardStartedAt,
                    Environment.TickCount64, ReadShowDurationMinutes());
        }

        private bool IsUnqueuedCardSession(string animationPath) =>
            activeUnqueuedCardStartedAt >= 0 && string.Equals(
                activeUnqueuedCardTag,
                GetCardTagFromAnimationPath(animationPath),
                StringComparison.OrdinalIgnoreCase);

        private bool CanPreloadCurrentCard(string animationPath) =>
            activeQueuedCard != null
                ? ShouldContinueQueuedCard(activeQueuedCardStartedAt,
                    Environment.TickCount64, ReadShowDurationMinutes())
                : CanContinueUnqueuedCard(animationPath);

        private bool UnqueuedCardContinuesSequentially(string animationPath) =>
            activeUnqueuedCardSequential && string.Equals(
                activeUnqueuedCardTag,
                GetCardTagFromAnimationPath(animationPath),
                StringComparison.OrdinalIgnoreCase);

        private void ClearUnqueuedCardSession()
        {
            activeUnqueuedCardStartedAt = -1;
            activeUnqueuedCardTag = "";
            activeUnqueuedCardSequential = false;
        }

        private static bool IsProgressiveHotnessEnabled() =>
            ReadRegistryInteger(@"Software\Totem\vghd\parameters",
                "EroRandom") == 1;

        private static int ReadRegistryInteger(string keyPath,
            string valueName)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                keyPath, false);
            return int.TryParse(key?.GetValue(valueName)?.ToString(),
                out int value) ? value : 0;
        }

        private bool TryPlayNextQueuedAnimation()
        {
            if (panicActive)
                return false;

            if (!TryTakeQueuedAnimation(
                    out string animationPath, out string cardTag))
                return false;

            queuedAnimationPendingPath = animationPath;
            queuedAnimationPendingConfirmed = false;
            queuedAnimationProtectedUntil = DateTime.MinValue;
            if (!RequestAnimationPlayback(animationPath))
                return false;
            SelectQueuedCard(cardTag, animationPath);
            return true;
        }

        private bool TryApplyQueueToAnimationProposal(string proposed,
            out string selected, out bool forceAnimation)
        {
            selected = proposed;
            forceAnimation = false;
            if (panicActive ||
                !Properties.Settings.Default.EnablePlayQueue)
                return false;

            if (!string.IsNullOrEmpty(playbackRequestedAnimationPath) &&
                string.Equals(proposed, playbackRequestedAnimationPath,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(proposed, queuedAnimationPendingPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                queuedAnimationPendingPath = "";
                queuedAnimationPendingConfirmed = false;
                queuedAnimationProtectedUntil = DateTime.MinValue;
                ClearQueuedCardSession();
                return false;
            }

            if (!string.IsNullOrEmpty(queuedAnimationPendingPath))
            {
                selected = queuedAnimationPendingPath;
                if (string.Equals(proposed, queuedAnimationPendingPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!queuedAnimationPendingConfirmed)
                    {
                        queuedAnimationPendingConfirmed = true;
                        queuedAnimationProtectedUntil =
                            DateTime.UtcNow.AddMilliseconds(
                                QueueStartProtectionMilliseconds);
                    }
                }
                else if (IsForcedAnimationProposal(proposed))
                {
                    queuedAnimationPendingPath = "";
                    queuedAnimationPendingConfirmed = false;
                    queuedAnimationProtectedUntil = DateTime.MinValue;
                    ClearQueuedCardSession();
                    return false;
                }
                else if (ShouldKeepQueuedAnimationPending(
                    queuedAnimationPendingConfirmed,
                    queuedAnimationProtectedUntil, DateTime.UtcNow,
                    CurrentAnimationReachedQueueAdvancePoint()))
                {
                    forceAnimation = true;
                }
                else
                {
                    queuedAnimationPendingPath = "";
                    queuedAnimationPendingConfirmed = false;
                    queuedAnimationProtectedUntil = DateTime.MinValue;
                }
                if (!string.IsNullOrEmpty(queuedAnimationPendingPath) ||
                    string.Equals(proposed, selected,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (string.Equals(proposed, playbackRequestedAnimationPath,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            if (IsForcedAnimationProposal(proposed))
                return false;

            if (string.Equals(proposed, nowPlayingPath,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            if (!apiOnlyMode && !CurrentAnimationReachedQueueAdvancePoint())
                return false;

            if (!TryTakeQueuedAnimation(out selected, out string cardTag))
                return false;
            BeginAnimationReplacement(selected);
            forceAnimation = !string.Equals(proposed, selected,
                StringComparison.OrdinalIgnoreCase);
            queuedAnimationPendingPath = selected;
            queuedAnimationPendingConfirmed = false;
            queuedAnimationProtectedUntil = DateTime.MinValue;
            SelectQueuedCard(cardTag, selected);
            return true;
        }

        private bool CurrentAnimationReachedQueueAdvancePoint()
        {
            if (!string.IsNullOrEmpty(playbackCompletedAnimationPath))
                return true;

            return !string.IsNullOrEmpty(playbackTimelineAnimationPath) &&
                string.Equals(playbackTimelineAnimationPath, nowPlayingPath,
                    StringComparison.OrdinalIgnoreCase) &&
                PlaybackReachedEnd(playbackLastKnownElapsedMilliseconds,
                    playbackTimelineDurationMilliseconds);
        }

        private static bool ShouldKeepQueuedAnimationPending(bool confirmed,
            DateTime protectedUntil, DateTime now, bool currentReachedEnd) =>
            !confirmed || now < protectedUntil || !currentReachedEnd;

        private static bool IsForcedAnimationProposal(string proposed)
        {
            string forced = GetPlaybackRegistryValue("ForceAnim");
            return string.Equals(proposed, forced,
                StringComparison.OrdinalIgnoreCase);
        }

        private void SelectQueuedCard(string cardTag, string animationPath)
        {
            if (apiOnlyMode)
                return;
            SelectCardInModelList(cardTag);
            string clipName = animationPath.Split('\\').LastOrDefault() ?? "";
            ListViewItem? clipItem = listClips.Items.Cast<ListViewItem>()
                .FirstOrDefault(item => item.SubItems.Count > 1 &&
                    string.Equals(item.SubItems[1].Text, clipName,
                        StringComparison.OrdinalIgnoreCase));
            if (clipItem != null)
            {
                clickingNowPlaying = true;
                try
                {
                    listClips.SelectedItems.Clear();
                    clipItem.Selected = true;
                    listClips.EnsureVisible(clipItem.Index);
                }
                finally
                {
                    clickingNowPlaying = false;
                }
            }
        }

        private void SelectCardInModelList(string cardTag)
        {
            int index = -1;
            for (int current = 0;
                 current < listModelsNew.Items.Count; current++)
            {
                if (string.Equals(
                        listModelsNew.Items[current].Tag?.ToString(), cardTag,
                        StringComparison.OrdinalIgnoreCase))
                {
                    index = current;
                    break;
                }
            }
            listModelsNew.ClearSelection();
            listModelsNew.SelectWhere(item => string.Equals(
                item.Tag?.ToString(), cardTag,
                StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                listModelsNew.EnsureVisible(index);
            else
                loadListClips(cardTag);
        }
    }
}
