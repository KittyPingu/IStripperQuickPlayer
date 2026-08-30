using DesktopWallpaper;
using EnumDescription;
using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using IStripperQuickPlayer.Interop;
using Manina.Windows.Forms;
using Manina.Windows.Forms.ImageListViewRenderers;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Taskbar;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Microsoft.WindowsAPICodePack.Shell.PropertySystem.SystemProperties.System;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using SaveFileDialog = System.Windows.Forms.SaveFileDialog;
using Size = System.Drawing.Size;
using Task = System.Threading.Tasks.Task;
//using WebView2.DevTools.Dom;

namespace IStripperQuickPlayer
{
    public partial class Form1 : Form
    {
        private readonly bool apiOnlyMode;
        private readonly int restApiPort;
        private NotifyIcon? apiOnlyNotifyIcon;
        private ContextMenuStrip? apiOnlyContextMenu;

        internal bool IsApiOnlyMode => apiOnlyMode;

        [DllImport("dwmapi.dll")]
        static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);

        private const int PlaybackBridgeVersion = 98;
        private const int PlaybackTimelineIntervalMilliseconds = 500;
        private const int PlaybackIdleIntervalMilliseconds = 5_000;
        private const int PlaybackTransitionIntervalMilliseconds = 100;
        private const int PlaybackMovieDiscoveryRetryMilliseconds = 100;
        private const int PlaybackForcedReadyMilliseconds = 5_000;
        private const int PlaybackReplacementTimeoutMilliseconds = 15_000;
        private const float DesignDpi = 144f;
        private const int VirtualKeyLeftButton = 0x01;
        private const string LatestReleaseApiUrl =
            "https://api.github.com/repos/KittyPingu/IStripperQuickPlayer/releases/latest";

        private float cardScale = 1.0f;
        private Panel clipListHost = null!;
        private readonly Label lblClipListTitle = new()
        {
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold,
                GraphicsUnit.Point),
            Margin = Padding.Empty,
            Name = "lblClipListTitle",
            Padding = new Padding(0, 5, 0, 5),
            Text = "Model: —   Show: —",
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        private bool cardScaleInitialized;
        private readonly Controls.PlaybackSeekBar cardScaleSeekBar = new()
        {
            AccessibleName = "Card size",
            AccessibleDescription =
                "Drag to change the size of cards in the library.",
            Minimum = 10,
            Maximum = 40,
            SmallChange = 1,
            LargeChange = 5,
            Visible = false
        };
        private readonly Label cardScaleLabel = new()
        {
            AutoSize = true,
            Text = "Card size: 100%",
            Visible = false
        };
        private bool isAutoSelecting = false;
        private string nowPlayingPath = "";
        private string nowPlayingTag = "";
        private string nowPlayingFilterMatch = "";
        private string nowPlayingTagShort = "";
        private string nowPlaying = "";
        private string wallpaperTag = "";
        private int nowPlayingClipNumber;
        private string clipListTag = "";
        private MyData? myData = null;
        public CardRenderer cardRenderer = null!;
        internal FilterSettings filterSettings = new FilterSettings();
        static readonly HttpClient client = new HttpClient();
        private NumberStyles style = NumberStyles.AllowDecimalPoint;
        private CultureInfo culture = CultureInfo.CreateSpecificCulture(CultureInfo.CurrentCulture.Name);
        //global hotkeys
        private const int WmShowWindow = 0x0018;
        private const int WmHotkey = 0x0312;
        private static readonly int WheelResizeMessage = (int)
            RegisterWindowMessage("IStripperQuickPlayer.WheelResize.v1");
        private static readonly int VolumeWheelMessage = (int)
            RegisterWindowMessage("IStripperQuickPlayer.VolumeWheel.v1");
        private const int NextClipHotkeyId = 1;
        private const int NextCardHotkeyId = 2;
        private const int ToggleLockHotkeyId = 3;
        private const int PauseHotkeyId = 4;
        private const int RewindHotkeyId = 5;
        private const int FastForwardHotkeyId = 6;
        private const int RestartClipHotkeyId = 7;
        private const int LargePlayerHotkeyId = 8;
        private const int SmallPlayerHotkeyId = 9;
        private const int NowPlayingInfoHotkeyId = 10;
        private const int PanicHotkeyId = 11;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;
        private const uint ModNoRepeat = 0x4000;
        private bool hotkeysInitialized;
        private Int32 vghd_procID = 0;
        private readonly SemaphoreSlim playbackOperationLock = new(1, 1);
        private readonly object playbackApiLock = new();
        private readonly CancellationTokenSource playbackLifetime = new();
        private PlaybackBridgeClient? playbackBridgeClient;
        private bool movieCaptureHookInstalled;
        private int vghdInjectionInProgress;
        private int playerMode = 2;
        private volatile bool playerLockBridgeLoaded;
        private volatile bool playbackBridgeLoaded;
        private volatile bool playbackMovieRegistered;
        private volatile bool playbackFastDecodeEnabled;
        private int playbackFastDecodeRetryPending;
        private volatile bool playbackSeekingSupported = true;
        private volatile bool playbackSeekReady;
        private volatile int playbackDecoderKind;
        private volatile int playbackSeekReadinessMask;
        private long playbackReadinessStartedTicks;
        private int playbackMovieRegistrationMilliseconds = -1;
        private int playbackSeekReadinessMilliseconds = -1;
        private volatile bool playbackBusy;
        private volatile bool playbackControlsAvailableForAccount;
        private bool PlaybackControlEnabled => apiOnlyMode ||
            Properties.Settings.Default.EnablePlaybackControl;
        private bool suppressPlaybackSpeedSelection;
        private double requestedPlaybackSpeed = 1.0;
        private (bool Rewind, bool PlayPause, bool FastForward,
            bool Speed, bool Position)? playbackControlEnabledState;
        private readonly System.Windows.Forms.Timer playbackTimelineTimer =
            new() { Interval = PlaybackTimelineIntervalMilliseconds };
        private readonly System.Windows.Forms.Timer cardOverlayTimer =
            new() { Interval = 75 };
        private readonly record struct PlaybackPollSnapshot(
            int Elapsed, int Total, int DecoderKind, int State,
            int SeekReady, int SeekReadinessMask);
        private readonly ToolStripMenuItem drawCardOverlaysToolStripMenuItem =
            new("Draw Card Overlays") { CheckOnClick = true };
        private readonly ToolStripMenuItem roundCardCornersToolStripMenuItem =
            new("Round Card Corners") { CheckOnClick = true };
        private readonly ToolStripMenuItem overlayDefaultsToolStripMenuItem =
            new("Overlay defaults...");
        private readonly ToolStripMenuItem
            directCompositionOverlaysToolStripMenuItem =
            new("DirectComposition overlays (GPU)")
            { CheckOnClick = true };
        private DirectCompositionCardOverlayControl?
            directCompositionCardOverlays;
        internal event EventHandler? InitialCardListReady;
        private bool initialCardListReady;
        private bool startupInitializationStarted;
        private bool deferCardCompositionUntilShown;
        private readonly ToolStripMenuItem updateToolStripMenuItem = new();
        private readonly ToolStripMenuItem alphaCheckpointCacheToolStripMenuItem =
            new();
        private readonly ToolStripMenuItem alphaCheckpointCacheSizeToolStripMenuItem =
            new("Alpha checkpoint cache size");
        private readonly ToolStripMenuItem avoidRecentRepeatsToolStripMenuItem =
            new("Avoid recently played clips");
        private readonly ToolStripMenuItem clickThroughLockedPlayerToolStripMenuItem =
            new("Click through locked player") { CheckOnClick = true };
        private readonly ToolStripMenuItem wheelResizePlayerToolStripMenuItem =
            new("Resize player with mouse wheel") { CheckOnClick = true };
        private readonly ToolStripMenuItem iStripperRtxHdrToolStripMenuItem =
            new("NVIDIA RTX HDR for iStripper (experimental)")
            { CheckOnClick = true };
        private readonly ToolStripMenuItem allowWheelWhileLockedToolStripMenuItem =
            new("Allow Resize while Locked") { CheckOnClick = true };
        private readonly ToolStripMenuItem playbackHistoryToolStripMenuItem =
            new("Playback History...");
        private readonly ToolStripMenuItem libraryHealthCheckToolStripMenuItem =
            new("Library Health Check...");
        private readonly ToolStripMenuItem refreshCardMetadataToolStripMenuItem =
            new("Refresh Card Metadata...");
        private readonly ToolStripMenuItem addModelToFilterToolStripMenuItem =
            new();
        private readonly Button panicResumeButton = new()
        {
            AccessibleDescription =
                "Resume animation playback, queues and QuickPlayer desktop changes.",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Name = "panicResumeButton",
            Padding = new Padding(8, 2, 8, 3),
            Text = "Resume",
            BackColor = Color.FromArgb(22, 145, 70),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Visible = false,
            UseVisualStyleBackColor = false
        };
        private readonly Button dressingRoomsTestButton = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Name = "dressingRoomsTestButton",
            Padding = new Padding(6, 2, 6, 3),
            Text = "Dressing Rooms (test)"
        };
        private readonly ToolStripMenuItem backupToolStripMenuItem =
            new("Backup QuickPlayer Data...");
        private readonly ToolStripMenuItem restoreToolStripMenuItem =
            new("Restore QuickPlayer Data...");
        private readonly object playbackHistoryLock = new();
        private static readonly object playbackRegistryLock = new();
        private static RegistryKey? playbackParametersKey;
        private bool playbackTimelinePolling;
        private int nowPlayingUiUpdatePending;
        private string displayedNowPlayingCardTag = "";
        private bool playbackTimelineDragging;
        private int playbackTimelineDurationMilliseconds;
        private int playbackLastKnownElapsedMilliseconds;
        private int playbackAlphaCheckpointBucket = -1;
        private string playbackTimelineAnimationPath = "";
        private string playbackCompletedAnimationPath = "";
        private string playbackRequestedAnimationPath = "";
        private DateTime playbackRequestedAnimationAt = DateTime.MinValue;
        private DateTime playbackNextMovieDiscoveryAt = DateTime.MinValue;
        private DateTime playbackMovieCaptureFallbackAt = DateTime.MinValue;
        private DateTime playbackNextClipRetryAt = DateTime.MinValue;
        private DateTime playbackReplacementStableAt = DateTime.MinValue;
        private DateTime playbackSpeedReapplyUntil = DateTime.MinValue;
        private DateTime playbackLastProgressAt = DateTime.UtcNow;
        private long playbackInputQuietUntilTicks;
        private bool formIsClosing;
        private bool restoringBackup;
        private volatile bool panicActive;
        private IntPtr panicMovieWindow;
        private CustomPlayerForm? panicCustomPlayer;
        private bool panicCustomPlayerWasPlaying;
        private int spaceRightOfListModel = 0;
        private int spaceBelowClipList = 0;
        private Size lastAdjustedClientSize;
        private bool layoutSuspendedForMinimize;
        private int dpiBeforeMinimize;
        private string screenBeforeMinimize = "";
        volatile bool playerlocked;
        //private WebView2DevToolsContext devtoolsContext = null;

        private void actNextClip()
        {
            if (Properties.Settings.Default.NextClipEnabled)
                GetNextClip(useQueue: false);
        }

        private void actNextCard()
        {
            if (Properties.Settings.Default.NextCardEnabled) this.BeginInvoke((Action)(() => GetNextCard()));
        }

        private void actToggleLock()
        {
            if (Properties.Settings.Default.ToggleLockEnabled) this.BeginInvoke((Action)(() => { lockPlayerToolStripMenuItem.Checked = !lockPlayerToolStripMenuItem.Checked; setPlayerLocked(); }));
        }

        private async void actPause()
        {
            if (Properties.Settings.Default.PauseHotkeyEnabled)
                await TogglePlaybackPauseAsync();
        }

        private async void actRewind()
        {
            if (Properties.Settings.Default.RewindHotkeyEnabled)
                await SeekPlaybackRelativeAsync(-0.1);
        }

        private async void actFastForward()
        {
            if (Properties.Settings.Default.FastForwardHotkeyEnabled)
                await SeekPlaybackRelativeAsync(0.1);
        }

        private async void actRestartClip()
        {
            if (Properties.Settings.Default.RestartClipHotkeyEnabled)
            {
                if (customPlayer != null) customPlayer.SeekTo(0);
                else await RunPlaybackOperationAsync(token => SeekAbsoluteAsync(0, token));
            }
        }

        private void actSetPlayerLarge(bool large)
        {
            if (customPlayer != null)
            {
                int mode = large ? 1 : 2;
                playerMode = mode;
                customPlayer.SetSizePercent(PlayerSizeForAnimation(
                    customPlayerAnimationPath, mode, large ? 80 : 40),
                    large ? 60 : 10, notifyChange: false);
                customPlayer.SetVolumePercent(CustomPlayerVolume(large));
                return;
            }
            if (!playerLockBridgeLoaded)
                return;

            int result = SetVghdPlayerLarge(large);
            if (result < 0)
            {
                SetPlaybackStatus(
                    $"Player size update failed (0x{result:X8}).");
            }
            else
            {
                int mode = large ? 1 : 2;
                Volatile.Write(ref playerMode, mode);
                ApplyIStripperPlayerVolume(mode);
            }
        }

        private void actNowPlayingInfo()
        {
            string cardTag = GetCardTagFromAnimationPath(nowPlayingPath);
            ModelCard? card = Datastore.findCardByTag(cardTag);
            if (card == null)
                return;
            card.image ??= ModelsLstLoader.LoadCardImage(card);
            if (card.image != null)
            {
                if (customPlayer != null)
                    LockStateOverlay.ShowImageForWindow(customPlayer, card.image);
                else
                    LockStateOverlay.ShowImageForProcess(vghd_procID, card.image);
            }
        }

        private async void actPanic()
        {
            if (panicActive)
            {
                panicResumeButton_Click(null, EventArgs.Empty);
                return;
            }

            panicActive = true;
            panicCustomPlayer = customPlayer;
            panicCustomPlayerWasPlaying = panicCustomPlayer is { Paused: false };
            panicCustomPlayer?.SetPaused(true);
            panicCustomPlayer?.HidePlayer();
            playbackCompletedAnimationPath = "";
            playbackRequestedAnimationPath = "";
            playbackRequestedAnimationAt = DateTime.MinValue;
            playbackNextClipRetryAt = DateTime.MinValue;
            queuedAnimationPendingPath = "";
            queuedAnimationPendingConfirmed = false;
            queuedAnimationProtectedUntil = DateTime.MinValue;
            if (!apiOnlyMode)
                panicResumeButton.Visible = true;
            if (Properties.Settings.Default.EnableIStripperRtxHdr &&
                playbackBridgeClient != null)
                CallPlaybackBridgeApi("IStripperSuspendOpenGlHdrSurface");
            panicMovieWindow =
                LockStateOverlay.HideMovieWindowForProcess(vghd_procID);
            LockStateOverlay.HideRtxHdrWindowForProcess(vghd_procID);
            if (!apiOnlyMode)
            {
                Wallpaper.SuspendAndRestoreOriginalDesktop();
                WindowState = FormWindowState.Minimized;
            }

            if (playbackBridgeLoaded && playbackMovieRegistered)
            {
                await RunPlaybackOperationAsync(_ =>
                {
                    if (RequirePlaybackResult("IStripperGetState") == 3)
                        RequirePlaybackResult("IStripperPause");
                    return Task.CompletedTask;
                }, prepareFastDecode: false);
            }
            // The pause request can overlap an already-rendering HDR frame.
            // Hide once more after it completes so panic always ends blank.
            LockStateOverlay.HideRtxHdrWindowForProcess(vghd_procID);
        }

        private async void panicResumeButton_Click(object? sender, EventArgs e)
        {
            if (!panicActive)
                return;

            if (playbackBridgeLoaded && playbackMovieRegistered)
            {
                await RunPlaybackOperationAsync(_ =>
                {
                    if (RequirePlaybackResult("IStripperGetState") == 4)
                        RequirePlaybackResult("IStripperResume");
                    return Task.CompletedTask;
                }, prepareFastDecode: false);
            }
            LockStateOverlay.ShowMovieWindow(panicMovieWindow);
            if (Properties.Settings.Default.EnableIStripperRtxHdr &&
                playbackBridgeClient != null)
                CallPlaybackBridgeApi("IStripperSetOpenGlPresentProbe", 1UL);
            if (!apiOnlyMode)
                Wallpaper.ResumeQuickPlayerDesktop();
            panicMovieWindow = IntPtr.Zero;
            CustomPlayerForm? player = panicCustomPlayer;
            bool resumeCustomPlayer = panicCustomPlayerWasPlaying;
            panicCustomPlayer = null;
            panicCustomPlayerWasPlaying = false;
            panicActive = false;
            if (customPlayer == player)
            {
                player?.ShowPlayer();
                if (resumeCustomPlayer)
                    player?.SetPaused(false);
            }
            if (!apiOnlyMode)
                panicResumeButton.Visible = false;
        }

        public Form1() : this(AppOptions.Default)
        {
        }

        protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg,
            Keys keyData)
        {
            if (keyData == Keys.Escape && customPlayer != null &&
                listClips.ContainsFocus)
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        internal Form1(AppOptions options)
        {
            apiOnlyMode = options.ApiOnly;
            restApiPort = options.ApiPort;
#if DEBUG
            if (!apiOnlyMode)
            {
            System.Diagnostics.Debug.Assert(
                PlaybackSourceAllowed(fullscreen: true, custom: false));
            System.Diagnostics.Debug.Assert(
                !PlaybackSourceAllowed(fullscreen: true, custom: true));
            System.Diagnostics.Debug.Assert(
                PlaybackSourceAllowed(fullscreen: false, custom: true));
            System.Diagnostics.Debug.Assert(!PlaybackReachedEnd(10_000, 135_000));
            System.Diagnostics.Debug.Assert(!PlaybackReachedEnd(132_999, 135_000));
            System.Diagnostics.Debug.Assert(PlaybackReachedEnd(133_000, 135_000));
            System.Diagnostics.Debug.Assert(PlaybackReachedEnd(134_000, 135_000));
            DateTime replacementCheck = DateTime.UtcNow;
            System.Diagnostics.Debug.Assert(!PlaybackReplacementExpired("clip",
                replacementCheck, replacementCheck.AddMilliseconds(14_999)));
            System.Diagnostics.Debug.Assert(PlaybackReplacementExpired("clip",
                replacementCheck, replacementCheck.AddMilliseconds(15_000)));
            DateTime queueCheck = DateTime.UtcNow;
            System.Diagnostics.Debug.Assert(ShouldKeepQueuedAnimationPending(
                false, DateTime.MinValue, queueCheck, true));
            System.Diagnostics.Debug.Assert(ShouldKeepQueuedAnimationPending(
                true, queueCheck.AddSeconds(1), queueCheck, true));
            System.Diagnostics.Debug.Assert(ShouldKeepQueuedAnimationPending(
                true, DateTime.MinValue, queueCheck, false));
            System.Diagnostics.Debug.Assert(!ShouldKeepQueuedAnimationPending(
                true, DateTime.MinValue, queueCheck, true));
            System.Diagnostics.Debug.Assert(ShouldContinueQueuedCard(
                1_000, 61_000, 1));
            System.Diagnostics.Debug.Assert(!ShouldContinueQueuedCard(
                1_000, 61_001, 1));
            List<PlayQueueEntry> requeueCheck = [new("second")];
            PlayQueueEntry? completedQueueCheck = new("first", "clip.vghd");
            FinishManualQueueEntry(requeueCheck,
                ref completedQueueCheck, true);
            System.Diagnostics.Debug.Assert(completedQueueCheck == null &&
                requeueCheck.Select(entry => entry.CardTag)
                    .SequenceEqual(["second", "first"]));
            System.Diagnostics.Debug.Assert(
                ManualQueueSelectableCount(1, true) == 0 &&
                ManualQueueSelectableCount(2, true) == 1 &&
                ManualQueueSelectableCount(1, false) == 1);
            System.Diagnostics.Debug.Assert(QueueForPersistence(
                [new("second")], new("first"), true)
                .Select(entry => entry.CardTag)
                .SequenceEqual(["second", "first"]));
            List<PlayQueueEntry> clickedQueueCheck =
                [new("first"), new("second")];
            System.Diagnostics.Debug.Assert(TryRemoveQueueEntry(
                clickedQueueCheck,
                new PlayQueueDrag("manual", 1, new("second"))) &&
                clickedQueueCheck.Select(entry => entry.CardTag)
                    .SequenceEqual(["first"]));
            System.Diagnostics.Debug.Assert(
                RatingFavouritePreference(false, 0) == 0 &&
                RatingFavouritePreference(true, 10) == 1);
            Dictionary<string, double> rankCheck = new()
            {
                ["first"] = 0,
                ["last"] = 0
            };
            AddRankScores(rankCheck, [new("first"), new("last")]);
            System.Diagnostics.Debug.Assert(
                rankCheck["first"] == 1 && rankCheck["last"] == 0);
            System.Diagnostics.Debug.Assert(
                HighestScoreIndex([new("low"), new("high")],
                    new Dictionary<string, double>
                    {
                        ["low"] = 0,
                        ["high"] = 1
                    }) == 1);
            System.Diagnostics.Debug.Assert(OrderBySmartQueueScores(
                [new("first"), new("second")],
                new Dictionary<string, double>
                {
                    ["first"] = 1,
                    ["second"] = 1
                }, 0).Select(entry => entry.CardTag)
                .SequenceEqual(["first", "second"]));
            System.Diagnostics.Debug.Assert(RotateQueueModels(
                [new("a1"), new("a2"), new("b1")],
                new Dictionary<string, string>
                {
                    ["a1"] = "A",
                    ["a2"] = "A",
                    ["b1"] = "B"
                }, "").Select(entry => entry.CardTag)
                .SequenceEqual(["a1", "b1", "a2"]));
            DateTime cooldownCheck = DateTime.UtcNow;
            System.Diagnostics.Debug.Assert(CooldownCardTags(
                [
                    new PlaybackHistoryEntry
                    {
                        AnimationPath = @"recent\recent_1.vghd",
                        PlayedUtc = cooldownCheck
                    },
                    new PlaybackHistoryEntry
                    {
                        AnimationPath = @"old\old_1.vghd",
                        PlayedUtc = cooldownCheck.AddHours(-2)
                    }
                ], cooldownCheck.AddHours(-1))
                .SetEquals(["recent"]));
            System.Diagnostics.Debug.Assert(
                SmartQueueRelaxation(false, false, true, true) == 0 &&
                SmartQueueRelaxation(false, true, true, true) == 1 &&
                SmartQueueRelaxation(false, false, false, true) == 2 &&
                SmartQueueRelaxation(true, false, true, true) == 3);
            DateTime stallCheck = DateTime.UtcNow;
            System.Diagnostics.Debug.Assert(LegacyPlaybackStalledNearEnd(
                96_000, 100_000, stallCheck.AddSeconds(-3), stallCheck, 3));
            System.Diagnostics.Debug.Assert(!LegacyPlaybackStalledNearEnd(
                96_000, 100_000, stallCheck.AddSeconds(-3), stallCheck, 4));
            System.Diagnostics.Debug.Assert(!SeekTargetReachesEnd(98_000, 100_000));
            System.Diagnostics.Debug.Assert(SeekTargetReachesEnd(99_000, 100_000));
            System.Diagnostics.Debug.Assert(HasPlatinumPlaybackEntitlement("platinum"));
            System.Diagnostics.Debug.Assert(HasPlatinumPlaybackEntitlement("doubleDiamond"));
            System.Diagnostics.Debug.Assert(HasPlatinumPlaybackEntitlement("qa"));
            System.Diagnostics.Debug.Assert(!HasPlatinumPlaybackEntitlement("gold"));
            System.Diagnostics.Debug.Assert(IsPlayingClip(
                "f0636_6144401.vghd", @"f0636\f0636_6144401.vghd"));
            System.Diagnostics.Debug.Assert(ParseUpdateVersion("v0.62.0") ==
                new Version(0, 62, 0));
            System.Diagnostics.Debug.Assert(IsSetupAssetName(
                "IStripperQuickPlayer-0.63.0-Setup.exe"));
            System.Diagnostics.Debug.Assert(!IsSetupAssetName(
                "IStripperQuickPlayer-0.63.0.zip"));
            System.Diagnostics.Debug.Assert(
                AlphaCheckpointClipKey(@"A/B.VGHD") ==
                AlphaCheckpointClipKey(@"a\b.vghd"));
            System.Diagnostics.Debug.Assert(
                AlphaCheckpointCacheBytes(2_048) == 2_147_483_648UL &&
                AlphaCheckpointCacheBytes(4_096) == 4_294_967_296UL);
            System.Diagnostics.Debug.Assert(TryParseHotKey(
                "Control+Alt+Left", out _, out _));
            System.Diagnostics.Debug.Assert(TryParseHotKey(
                "Control+Alt+Home", out _, out _));
            System.Diagnostics.Debug.Assert(TryParseHotKey(
                "Control+Alt+I", out uint infoModifiers, out uint infoKey) &&
                infoModifiers == (ModNoRepeat | ModControl | ModAlt) &&
                infoKey == (uint)Keys.I);
            System.Diagnostics.Debug.Assert(TryParseHotKey(
                "Control+Alt+X", out _, out _));
            MyData historyCheck = new();
            int metadataChangeCount = 0;
            historyCheck.Changed += () => metadataChangeCount++;
            historyCheck.AddPlayback(@"c0001\c0001_1.vghd",
                DateTime.UtcNow);
            System.Diagnostics.Debug.Assert(metadataChangeCount == 1);
            System.Diagnostics.Debug.Assert(historyCheck
                .RecentPlaybackPaths(100).Contains(
                    @"C0001\C0001_1.VGHD"));
            System.Diagnostics.Debug.Assert(ExcludeRecentClips(
                new[] { new ModelClip { clipName = "c0001_1.vghd" } },
                historyCheck.RecentPlaybackPaths(100)).Count == 0);
            string backupCheckPath = Path.Combine(Path.GetTempPath(),
                $"quickplayer-backup-check-{Guid.NewGuid():N}.iqpb");
            try
            {
                Persistence.Save(backupCheckPath, new QuickPlayerBackup
                {
                    UserData = historyCheck,
                    Settings = new() { ["Randomize"] = "True" }
                });
                QuickPlayerBackup backupCheck =
                    Persistence.Load<QuickPlayerBackup>(backupCheckPath);
                System.Diagnostics.Debug.Assert(
                    backupCheck.UserData.PlaybackHistory.Count == 1 &&
                    backupCheck.Settings["Randomize"] == "True");
            }
            finally
            {
                File.Delete(backupCheckPath);
            }
            System.Diagnostics.Debug.Assert(
                MatchesMeasurement(32, 30, 35, 30) &&
                !MatchesMeasurement(40, 30, 35, 30) &&
                MatchesMeasurement(null, 30, 35, 30) &&
                !MatchesMeasurement(null, 31, 35, 30));
            System.Diagnostics.Debug.Assert(
                MeasurementBounds([null, 0, 24.2M, 39.7M, 105]) ==
                (24M, 40M));
            TextSearchDocument searchCheck = new(
                "Anna Delos c1001 Pool Side duo red table",
                "Anna Delos", "c1001", "Pool Side", "", "duo red table",
                "Latina", "Brunette");
            System.Diagnostics.Debug.Assert(
                TextQuery.Parse("anna AND tag:duo AND !blue")
                    .Matches(searchCheck));
            System.Diagnostics.Debug.Assert(
                TextQuery.Parse("(beth OR model:anna) AND \"pool side\"")
                    .Matches(searchCheck));
            System.Diagnostics.Debug.Assert(
                TooltipManager.NormalizeDelay(-1) == -1 &&
                TooltipManager.NormalizeDelay(200) == 200 &&
                TooltipManager.NormalizeDelay(6_000) == 5_000);
            System.Diagnostics.Debug.Assert(
                !TextQuery.Parse("anna AND tag:blue").Matches(searchCheck));
            System.Diagnostics.Debug.Assert(
                TextQuery.Parse("ethnicity:latin AND hair:brun").Matches(searchCheck) &&
                TextQuery.Parse("hair-color:brunette").Matches(searchCheck) &&
                TextQuery.Parse("hair-colour:brunette").Matches(searchCheck) &&
                !TextQuery.Parse("ethnicity:asian").Matches(searchCheck));
            System.Diagnostics.Debug.Assert(SnapHalfStar(3.24M) == 3M &&
                SnapHalfStar(3.25M) == 3.5M);
            System.Diagnostics.Debug.Assert(AddModelFilter(
                "tag:duo OR tag:pole", "Georgia") ==
                "(tag:duo OR tag:pole) AND model:Georgia");
            System.Diagnostics.Debug.Assert(
                PlayerSizeClipType("Standing, Solo") == "standing" &&
                PlayerSizeClipType("Standing, Table") == "table" &&
                PlayerSizeClipType("Standing, Pole") == "pole" &&
                PlayerSizeClipType("Swing") == "swing" &&
                PlayerSizeClipType("Cage") == "cage" &&
                PlayerSizeClipType("InOut") == null);
            Dictionary<string, int> playerSizeCheck =
                ParseClipTypePlayerSizes(
                    """{"tableSmall":200,"poleLarge":0}""");
            System.Diagnostics.Debug.Assert(
                PlayerSizePercent(playerSizeCheck, "tableSmall") == 200 &&
                PlayerSizePercent(playerSizeCheck, "poleLarge") == 0 &&
                PlayerSizePercent(playerSizeCheck, "standingSmall") == 0);
            TextSearchDocument raeSearchCheck = new(
                "Asia Rae pole", "Asia Rae", "", "", "", "pole", "", "");
            TextSearchDocument kittySearchCheck = new(
                "Ashby Kitty", "Ashby Kitty", "", "", "", "", "", "");
            TextQuery unionSearchCheck =
                TextQuery.Parse("(rae AND pole) OR kitty");
            System.Diagnostics.Debug.Assert(unionSearchCheck.Matches(
                raeSearchCheck));
            System.Diagnostics.Debug.Assert(unionSearchCheck.Matches(
                kittySearchCheck));
            System.Diagnostics.Debug.Assert(CustomShowStore.VerifyCoreLogic());
            }
#endif
            if (apiOnlyMode)
            {
                InitializeApiOnlyShell();
                return;
            }

            InitializeComponent();
            DoubleBuffered = true;
            listModelsNew.RefreshOnItemHoverChanged = false;
            cardOverlayTimer.Tick += (_, _) =>
            {
                if (!listModelsNew.Visible ||
                    WindowState == FormWindowState.Minimized)
                {
                    SetTimerInterval(cardOverlayTimer, 250);
                    return;
                }
                bool animationChanged =
                    Properties.Settings.Default.DrawCardOverlays &&
                    CardOverlayLoader.HasAnimatedOverlays &&
                    (directCompositionCardOverlays?
                        .AnimationFrameChanged() ??
                     CardOverlayLoader.AnimationFrameChanged());
                if (directCompositionCardOverlays != null)
                {
                    bool staticChanged =
                        directCompositionCardOverlays.HasPendingStaticChanges;
                    if (animationChanged || staticChanged)
                    {
                        if (!directCompositionCardOverlays.Render(
                                true,
                                animationChanged && !staticChanged))
                            DisableDirectCompositionCardOverlays();
                    }
                }
                else if (animationChanged)
                {
                    listModelsNew.RefreshItems(
                        listModelsNew.GetCurrentlyVisibleItems()
                        .Where(item => CardOverlayLoader.HasAnimatedOverlay(
                            item.Tag?.ToString() ?? "")));
                    RefreshPlayQueueCardOverlays();
                }
                SetTimerInterval(cardOverlayTimer,
                    directCompositionCardOverlays?
                        .NextAnimationFrameDelay() ??
                    CardOverlayLoader.NextAnimationFrameDelay());
            };
            cardOverlayTimer.Start();
            trkPlaybackPosition.AccessibleDescription =
                "Click or drag to seek within the current clip.";
            trkPlaybackPosition.ShowTimeToolTip = true;
            listClips.SetDoubleBuffered();
            listModelsNew.RefreshOnFocusChanged = false;
            panicResumeButton.Click += panicResumeButton_Click;
            dressingRoomsTestButton.Click += dressingRoomsTestButton_Click;
            panelClip.Controls.Add(panicResumeButton);
            cardScaleSeekBar.Scroll += cardScaleSeekBar_Scroll;
            splitContainer1.Panel1.Controls.Add(cardScaleSeekBar);
            splitContainer1.Panel1.Controls.Add(cardScaleLabel);
            ConfigureResponsiveLayouts();
            nameToolStripMenuItem.Click += nameToolStripMenuItem_Click;
            nameToolStripMenuItem.Font = new Font(nameToolStripMenuItem.Font,
                nameToolStripMenuItem.Font.Style | FontStyle.Underline);
            outfitToolStripMenuItem.Click += outfitToolStripMenuItem_Click;
            outfitToolStripMenuItem.Font = new Font(outfitToolStripMenuItem.Font,
                outfitToolStripMenuItem.Font.Style | FontStyle.Underline);
            addModelToFilterToolStripMenuItem.Click +=
                addModelToFilterToolStripMenuItem_Click;
            menuCardList.Items.Insert(
                menuCardList.Items.IndexOf(ratingSlider) + 1,
                addModelToFilterToolStripMenuItem);
            alphaCheckpointCacheToolStripMenuItem.Text =
                "Enable alpha checkpoint cache";
            alphaCheckpointCacheToolStripMenuItem.CheckOnClick = true;
            alphaCheckpointCacheToolStripMenuItem.Checked =
                Properties.Settings.Default.EnableAlphaCheckpointCache;
            alphaCheckpointCacheToolStripMenuItem.CheckedChanged +=
                alphaCheckpointCacheToolStripMenuItem_CheckedChanged;
            settingsToolStripMenuItem.DropDownItems.Insert(
                settingsToolStripMenuItem.DropDownItems.IndexOf(
                    enablePlaybackControlToolStripMenuItem) + 1,
                alphaCheckpointCacheToolStripMenuItem);
            foreach (int size in new[] { 64, 128, 256, 512, 1024, 2048, 4096 })
            {
                ToolStripMenuItem item = new(
                    size < 1024 ? $"{size} MB" : $"{size / 1024} GB")
                {
                    Tag = size,
                    ToolTipText =
                        $"Limit the alpha checkpoint cache to " +
                        $"{(size < 1024 ? $"{size} MB" : $"{size / 1024} GB")}.",
                    Checked = size ==
                        Properties.Settings.Default.AlphaCheckpointCacheSizeMB
                };
                item.Click += alphaCheckpointCacheSizeToolStripMenuItem_Click;
                alphaCheckpointCacheSizeToolStripMenuItem.DropDownItems.Add(item);
            }
            alphaCheckpointCacheSizeToolStripMenuItem.Enabled =
                alphaCheckpointCacheToolStripMenuItem.Checked;
            settingsToolStripMenuItem.DropDownItems.Insert(
                settingsToolStripMenuItem.DropDownItems.IndexOf(
                    alphaCheckpointCacheToolStripMenuItem) + 1,
                alphaCheckpointCacheSizeToolStripMenuItem);
            avoidRecentRepeatsToolStripMenuItem.CheckOnClick = true;
            avoidRecentRepeatsToolStripMenuItem.Checked =
                Properties.Settings.Default.AvoidRecentRepeats;
            avoidRecentRepeatsToolStripMenuItem.CheckedChanged += (_, _) =>
                Properties.Settings.Default.AvoidRecentRepeats =
                    avoidRecentRepeatsToolStripMenuItem.Checked;
            clickThroughLockedPlayerToolStripMenuItem.Checked =
                Properties.Settings.Default.ClickThroughLockedPlayer;
            clickThroughLockedPlayerToolStripMenuItem.CheckedChanged += (_, _) =>
            {
                Properties.Settings.Default.ClickThroughLockedPlayer =
                    clickThroughLockedPlayerToolStripMenuItem.Checked;
                ChangePlayerClickThrough();
            };
            wheelResizePlayerToolStripMenuItem.Checked =
                Properties.Settings.Default.EnablePlayerWheelResize;
            wheelResizePlayerToolStripMenuItem.CheckedChanged += (_, _) =>
            {
                Properties.Settings.Default.EnablePlayerWheelResize =
                    wheelResizePlayerToolStripMenuItem.Checked;
                ChangePlayerWheelResize();
            };
            iStripperRtxHdrToolStripMenuItem.Checked =
                Properties.Settings.Default.EnableIStripperRtxHdr;
            iStripperRtxHdrToolStripMenuItem.CheckedChanged += (_, _) =>
            {
                Properties.Settings.Default.EnableIStripperRtxHdr =
                    iStripperRtxHdrToolStripMenuItem.Checked;
                if (playbackBridgeClient != null)
                    CallPlaybackBridgeApi("IStripperSetOpenGlPresentProbe",
                        iStripperRtxHdrToolStripMenuItem.Checked ? 1UL : 0UL);
            };
            settingsToolStripMenuItem.DropDownOpening += (_, _) =>
            {
                const string label =
                    "NVIDIA RTX HDR for iStripper (experimental)";
                if (!iStripperRtxHdrToolStripMenuItem.Checked ||
                    playbackBridgeClient == null)
                {
                    iStripperRtxHdrToolStripMenuItem.Text = label;
                    return;
                }
                try
                {
                    int status = playbackBridgeClient.Call(
                        "IStripperGetOpenGlHdrStatus");
                    int frames = playbackBridgeClient.Call(
                        "IStripperGetOpenGlHdrFrameCount");
                    iStripperRtxHdrToolStripMenuItem.Text = status > 0
                        ? $"{label} — Active ({frames:N0} frames)"
                        : status < 0 ? $"{label} — Unavailable"
                        : $"{label} — Waiting for OpenGL video";
                }
                catch
                {
                    iStripperRtxHdrToolStripMenuItem.Text =
                        $"{label} — Bridge unavailable";
                }
            };
            allowWheelWhileLockedToolStripMenuItem.Checked =
                Properties.Settings.Default.AllowWheelWhileLocked;
            allowWheelWhileLockedToolStripMenuItem.CheckedChanged += (_, _) =>
            {
                Properties.Settings.Default.AllowWheelWhileLocked =
                    allowWheelWhileLockedToolStripMenuItem.Checked;
                ChangePlayerWheelWhileLocked();
            };
            settingsToolStripMenuItem.DropDownItems.Insert(
                settingsToolStripMenuItem.DropDownItems.IndexOf(
                    randomPlayOrderToolStripMenuItem) + 1,
                avoidRecentRepeatsToolStripMenuItem);
            drawCardOverlaysToolStripMenuItem.Checked =
                Properties.Settings.Default.DrawCardOverlays;
            drawCardOverlaysToolStripMenuItem.CheckedChanged += (_, _) =>
            {
                Properties.Settings.Default.DrawCardOverlays =
                    drawCardOverlaysToolStripMenuItem.Checked;
                UpdateCardOverlayRenderingPath();
                listModelsNew.Refresh();
            };
            roundCardCornersToolStripMenuItem.Checked =
                Properties.Settings.Default.RoundCardCorners;
            roundCardCornersToolStripMenuItem.CheckedChanged += (_, _) =>
            {
                Properties.Settings.Default.RoundCardCorners =
                    roundCardCornersToolStripMenuItem.Checked;
                listModelsNew.Refresh();
            };
            directCompositionOverlaysToolStripMenuItem.Checked =
                Properties.Settings.Default
                    .DirectCompositionOverlays;
            directCompositionOverlaysToolStripMenuItem
                .CheckedChanged += (_, _) =>
            {
                Properties.Settings.Default
                    .DirectCompositionOverlays =
                    directCompositionOverlaysToolStripMenuItem
                        .Checked;
                UpdateCardOverlayRenderingPath();
                listModelsNew.Refresh();
            };
            overlayDefaultsToolStripMenuItem.Click += (_, _) =>
                EditOverlayDefaults();
            playbackHistoryToolStripMenuItem.Click += (_, _) =>
                ShowPlaybackHistory();
            libraryHealthCheckToolStripMenuItem.Click += async (_, _) =>
                await RunLibraryHealthCheckAsync();
            refreshCardMetadataToolStripMenuItem.Click += async (_, _) =>
                await RefreshCardMetadataAsync();
            fileToolStripMenuItem.DropDownItems.Insert(
                fileToolStripMenuItem.DropDownItems.IndexOf(
                    reloadModelslstToolStripMenuItem) + 1,
                refreshCardMetadataToolStripMenuItem);
            fileToolStripMenuItem.DropDownItems.Insert(
                fileToolStripMenuItem.DropDownItems.IndexOf(
                    refreshCardMetadataToolStripMenuItem) + 1,
                libraryHealthCheckToolStripMenuItem);
            SetupLibraryCleaner();
            SetupCustomShows();
            backupToolStripMenuItem.Click += (_, _) => BackupQuickPlayerData();
            restoreToolStripMenuItem.Click += (_, _) => RestoreQuickPlayerData();
            fileToolStripMenuItem.DropDownItems.Insert(
                fileToolStripMenuItem.DropDownItems.Count - 1,
                playbackHistoryToolStripMenuItem);
            fileToolStripMenuItem.DropDownItems.Insert(
                fileToolStripMenuItem.DropDownItems.Count - 1,
                backupToolStripMenuItem);
            fileToolStripMenuItem.DropDownItems.Insert(
                fileToolStripMenuItem.DropDownItems.Count - 1,
                restoreToolStripMenuItem);
            updateToolStripMenuItem.Text = "Check for Updates...";
            updateToolStripMenuItem.Click +=
                async (_, _) => await CheckForUpdatesAsync(true);
            fileToolStripMenuItem.DropDownItems.Insert(
                fileToolStripMenuItem.DropDownItems.Count - 1,
                updateToolStripMenuItem);
            SetupPlayQueue();
            applicationToolTip = TooltipManager.Attach(this, components,
                Properties.Settings.Default.TooltipInitialDelay);
            RefreshPlaybackControlVisibility();
            playbackTimelineTimer.Tick += playbackTimelineTimer_Tick;
            if (Properties.Settings.Default.EnablePlaybackControl)
                playbackTimelineTimer.Start();
            SetSkin();
        }

        private void InitializeApiOnlyShell()
        {
            Text = "iStripper QuickPlayer API";
            Icon = Properties.Resources.df2284943cc77e7e1a5fa6a0da8ca265;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32_000, -32_000);
            Size = new Size(1, 1);
            Opacity = 0;

            ToolStripMenuItem quit = new("Quit");
            quit.Click += (_, _) => Close();
            apiOnlyContextMenu = new ContextMenuStrip();
            apiOnlyContextMenu.Items.Add(quit);
            apiOnlyNotifyIcon = new NotifyIcon
            {
                ContextMenuStrip = apiOnlyContextMenu,
                Icon = Properties.Resources.df2284943cc77e7e1a5fa6a0da8ca265,
                Text = $"QuickPlayer API - 127.0.0.1:{restApiPort}"
            };

            Load += Form1_Load;
            FormClosing += Form1_FormClosing;
        }

        private void InitializeApiOnly()
        {
            culture.NumberFormat.NumberDecimalSeparator = ".";
            playerlocked = false;
            // Empty manual queues do not intercept iStripper; keeping the
            // process-local queue enabled makes an explicit API add effective.
            Properties.Settings.Default.EnablePlayQueue = true;
            RefreshPlaybackControlVisibility();
            myData = RetrieveMyData();
            myData.Changed += SaveMyData;

            apiOnlyNotifyIcon!.Visible = true;
            try
            {
                RetrieveModels();
            }
            catch (Exception exception)
            {
                Debug.WriteLine("API-only model loading failed: " + exception);
                apiOnlyNotifyIcon.Text = "QuickPlayer API - model load failed";
            }

            StartRestApi();
            _ = Task.Run(SetupRegHooks);
            BeginInvoke((Action)Hide);
        }

        private void SetSkin()
        {
            AppTheme.Apply(this);
            lblPlaybackTime.BackColor = Color.Transparent;
            menuCardList.Renderer = menuStrip1.Renderer;
            menuCardList.ShowImageMargin = false;
            menuCardList.BackColor = menuStrip1.BackColor;
            menuCardList.ForeColor = fileToolStripMenuItem.ForeColor;
            ApplySmartQueueSliderTheme();
            foreach (ToolStripItem item in menuCardList.Items)
                item.ForeColor = menuCardList.ForeColor;
            UpdateFavouriteMenuItem();
            nameToolStripMenuItem.ForeColor =
                Properties.Settings.Default.DarkMode
                    ? Color.LightSkyBlue : Color.Blue;
            outfitToolStripMenuItem.ForeColor =
                nameToolStripMenuItem.ForeColor;
            cardScaleLabel.ForeColor = label1.ForeColor;
            cardScaleSeekBar.BackColor =
                splitContainer1.Panel1.BackColor;
            listClips.BackColor = splitContainer1.Panel2.BackColor;
            listClips.ForeColor = label1.ForeColor;
            RefreshPlayingClipHighlight();
            if (cardRenderer != null) cardRenderer.SetColours();
            SetPlayQueueColours();
            panicResumeButton.FlatStyle = FlatStyle.Flat;
            panicResumeButton.UseVisualStyleBackColor = false;
            panicResumeButton.BackColor = Color.FromArgb(22, 145, 70);
            panicResumeButton.ForeColor = Color.White;
        }

        private void cmdLoadModels_Click(object sender, EventArgs e)
        {
            this.Cursor = Cursors.WaitCursor;
            ReloadModels();
            this.Cursor = Cursors.Arrow;
        }

        private void ReloadModels(Action? afterPopulation = null)
        {
            List<ModelCard> previousCards =
                (Datastore.modelcards ?? []).ToList();
            int previousDeclaredCards = Datastore.numberOfCards;
            int previousVersion = Datastore.versionnumber;
            void RestorePreviousCatalogue()
            {
                Datastore.modelcards = previousCards;
                Datastore.numberOfCards = previousDeclaredCards;
                Datastore.versionnumber = previousVersion;
                if (!apiOnlyMode)
                {
                    lblModelsLoaded.Text =
                        "Models Loaded: " + previousCards.Count;
                }
            }

            string diagnosticsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IStripperQuickPlayer");
            string diagnosticsPath = Path.Combine(
                diagnosticsFolder, "model-reload-diagnostic.txt");
            StringBuilder diagnostics = new();
            diagnostics.AppendLine($"Reload started: {DateTimeOffset.Now:O}");
            diagnostics.AppendLine($"QuickPlayer version: {Application.ProductVersion}");
            diagnostics.AppendLine($"OS: {RuntimeInformation.OSDescription}");
            diagnostics.AppendLine(
                $"Process: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} | " +
                $".NET {Environment.Version} | culture={CultureInfo.CurrentCulture.Name}");
            try
            {
                using Process? vghd = Process.GetProcessesByName("vghd")
                    .FirstOrDefault();
                diagnostics.AppendLine(vghd == null
                    ? "iStripper: not running"
                    : $"iStripper: {vghd.MainModule?.FileVersionInfo.FileVersion} | " +
                        $"{vghd.MainModule?.FileName}");
            }
            catch (Exception exception)
            {
                diagnostics.AppendLine($"iStripper details error: {exception.Message}");
            }

            using RegistryKey? systemKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Totem\vghd\System", false);
            string dataPath = systemKey?.GetValue("DataPath", "")?.ToString() ?? "";
            string modelsPath = systemKey?.GetValue("ModelsPath", "")?.ToString() ?? "";
            string[] multiPaths = systemKey?.GetValue(
                "ModelsMultiPath", Array.Empty<string>()) as string[]
                ?? Array.Empty<string>();
            diagnostics.AppendLine($"DataPath: {dataPath}");
            diagnostics.AppendLine($"ModelsPath: {modelsPath}");
            diagnostics.AppendLine(
                $"ModelsMultiPath: {string.Join(" | ", multiPaths)}");

            string cataloguePath = Path.Combine(dataPath, "models.lst");
            FileInfo catalogue = new(cataloguePath);
            diagnostics.AppendLine($"models.lst: {cataloguePath}");
            diagnostics.AppendLine($"models.lst exists: {catalogue.Exists}");
            if (catalogue.Exists)
            {
                diagnostics.AppendLine($"models.lst size: {catalogue.Length}");
                diagnostics.AppendLine(
                    $"models.lst modified: {catalogue.LastWriteTimeUtc:O}");
            }
            string[] modelRoots = new[] { modelsPath }.Concat(multiPaths)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.TrimEndingDirectorySeparator(path.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Dictionary<string, HashSet<string>> rootFolders =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (string root in modelRoots)
            {
                bool exists = Directory.Exists(root);
                try
                {
                    HashSet<string> folders = exists
                        ? Directory.EnumerateDirectories(root)
                            .Select(Path.GetFileName)
                            .OfType<string>()
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : new(StringComparer.OrdinalIgnoreCase);
                    rootFolders[root] = folders;
                    diagnostics.AppendLine(
                        $"Model root: {root} | exists={exists} | folders={folders.Count}");
                }
                catch (Exception exception)
                {
                    rootFolders[root] = new(StringComparer.OrdinalIgnoreCase);
                    diagnostics.AppendLine(
                        $"Model root: {root} | exists={exists} | error={exception.Message}");
                }
            }

            ModelsLstLoader? lstLoader = null;
            try
            {
                if (!apiOnlyMode)
                    RefreshPlaybackControlVisibility();
                ReloadStaticProperties();
                lstLoader = new ModelsLstLoader(loadCardImages: !apiOnlyMode);
                Datastore.modelcards = [];
                lstLoader.LoadModels();
                int loadedCards = Datastore.modelcards?.Count ?? 0;
                int physicalFolders = rootFolders.Values
                    .SelectMany(folders => folders)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (IsIncompleteCatalogueReload(
                    previousCards.Count, loadedCards, physicalFolders))
                {
                    RestorePreviousCatalogue();
                    if (!apiOnlyMode)
                        QueueModelPopulation(afterPopulation);
                    diagnostics.AppendLine(
                        "Result: incomplete catalogue ignored; " +
                        "previous cards retained");
                    return;
                }
                AppendCustomCards(diagnostics);
                if (!apiOnlyMode)
                    listModelsNew.Items.Clear();
                if (!apiOnlyMode)
                    CardOverlayLoader.Reload(
                        Datastore.modelcards ?? [], myData);
                if (!apiOnlyMode)
                    QueueModelPopulation(afterPopulation);
                PersistModels();
                diagnostics.AppendLine("Result: success");
            }
            catch (Exception exception)
            {
                RestorePreviousCatalogue();
                diagnostics.AppendLine("Result: failed");
                diagnostics.AppendLine(
                    $"Parse offset: {exception.Data["ModelsLstPosition"] ?? "unknown"}");
                diagnostics.AppendLine(
                    $"Catalogue length: {exception.Data["ModelsLstLength"] ?? "unknown"}");
                diagnostics.AppendLine(
                    $"Parse section: {exception.Data["ModelsLstSection"] ?? "unknown"}");
                diagnostics.AppendLine(
                    $"Card index: {exception.Data["ModelsLstCardIndex"] ?? "unknown"}");
                diagnostics.AppendLine(
                    $"Card tag: {exception.Data["ModelsLstCard"] ?? "unknown"}");
                diagnostics.AppendLine(
                    $"Card start offset: {exception.Data["ModelsLstCardOffset"] ?? "unknown"}");
                diagnostics.AppendLine(
                    $"Clip index: {exception.Data["ModelsLstClipIndex"] ?? "unknown"}");
                diagnostics.AppendLine(
                    $"Declared clip count: {exception.Data["ModelsLstClipCount"] ?? "unknown"}");
                diagnostics.AppendLine(
                    $"Raw capture start: {exception.Data["ModelsLstRawStart"] ?? "unavailable"}");
                diagnostics.AppendLine(
                    $"Raw capture length: {exception.Data["ModelsLstRawLength"] ?? "unavailable"}");
                diagnostics.AppendLine(
                    $"Raw includes card start: {exception.Data["ModelsLstRawIncludesCardStart"] ?? "unavailable"}");
                diagnostics.AppendLine(
                    $"Failure hex start: {exception.Data["ModelsLstHexStart"] ?? "unavailable"}");
                diagnostics.AppendLine(
                    $"Failure hex: {exception.Data["ModelsLstFailureHex"] ?? "unavailable"}");
                if (exception.Data["ModelsLstRawCaptureError"] is object rawCaptureError)
                {
                    diagnostics.AppendLine(
                        $"Raw capture error: {rawCaptureError}");
                }
                diagnostics.AppendLine(
                    $"Failing record data (Base64): " +
                    $"{exception.Data["ModelsLstRawBase64"] ?? "unavailable"}");
                if (long.TryParse(
                        exception.Data["ModelsLstPosition"]?.ToString(),
                        out long failureOffset) &&
                    long.TryParse(
                        exception.Data["ModelsLstLength"]?.ToString(),
                        out long failureLength) &&
                    failureLength > 0)
                {
                    diagnostics.AppendLine(
                        $"Parse progress: {100d * failureOffset / failureLength:F2}%");
                }
                diagnostics.AppendLine(exception.ToString());
                throw;
            }
            finally
            {
                List<ModelCard> cards = Datastore.modelcards ?? [];
                int loadedCards = cards.Count;
                diagnostics.AppendLine(
                    $"Declared cards: {Datastore.numberOfCards}");
                diagnostics.AppendLine(
                    $"Loaded cards with clips: {loadedCards}");
                diagnostics.AppendLine(
                    $"Entries without clips/not loaded: {Math.Max(0,
                        Datastore.numberOfCards - loadedCards)}");
                diagnostics.AppendLine($"Parser version: {Datastore.versionnumber}");
                if (lstLoader != null)
                {
                    diagnostics.AppendLine(
                        $"Library: declared={lstLoader.LibraryDeclaredCards} | " +
                        $"parsed={lstLoader.LibraryParsedCards} | " +
                        $"with clips={lstLoader.LibraryCardsWithClips} | " +
                        $"without clips={lstLoader.LibraryParsedCards -
                            lstLoader.LibraryCardsWithClips}");
                    diagnostics.AppendLine(
                        $"Market: declared={lstLoader.MarketDeclaredCards} | " +
                        $"parsed={lstLoader.MarketParsedCards} | " +
                        $"with clips={lstLoader.MarketCardsWithClips} | " +
                        $"without clips={lstLoader.MarketParsedCards -
                            lstLoader.MarketCardsWithClips}");
                    diagnostics.AppendLine(
                        $"Catalogue bytes: read={lstLoader.BytesRead} | " +
                        $"length={lstLoader.CatalogueLength} | " +
                        $"remaining={Math.Max(0,
                            lstLoader.CatalogueLength - lstLoader.BytesRead)}");
                    AppendZeroClipDiagnostics(
                        diagnostics, lstLoader, rootFolders);
                }

                diagnostics.AppendLine(
                    $"Clips: total={cards.Sum(card => card.clips?.Count ?? 0)} | " +
                    $"enabled={cards.Sum(card =>
                        card.clips?.Count(clip => clip.isEnabled == true) ?? 0)}");
                diagnostics.AppendLine(
                    $"Duplicate loaded card tags: {cards
                        .GroupBy(card => card.name, StringComparer.OrdinalIgnoreCase)
                        .Count(group => group.Count() > 1)}");
                foreach (var collection in cards
                    .GroupBy(card => card.collection)
                    .OrderBy(group => group.Key))
                {
                    diagnostics.AppendLine(
                        $"Collection {collection.Key}: {collection.Count()}");
                }

                HashSet<string> uniquePhysicalFolders =
                    rootFolders.Values.SelectMany(folders => folders)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                diagnostics.AppendLine(
                    $"Physical model folders: total across roots=" +
                    $"{rootFolders.Values.Sum(folders => folders.Count)} | " +
                    $"unique names={uniquePhysicalFolders.Count}");
                foreach (var root in rootFolders)
                {
                    diagnostics.AppendLine(
                        $"Loaded cards found in {root.Key}: " +
                        cards.Count(card => root.Value.Contains(card.name)));
                }
                diagnostics.AppendLine(
                    $"Loaded cards missing from all model roots: " +
                    cards.Count(card => !uniquePhysicalFolders.Contains(card.name)));
                diagnostics.AppendLine($"Reload finished: {DateTimeOffset.Now:O}");
                try
                {
                    Directory.CreateDirectory(diagnosticsFolder);
                    File.WriteAllText(diagnosticsPath,
                        RedactUsernamesFromPaths(diagnostics.ToString()));
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(
                        "Could not write model reload diagnostics: " +
                        exception.Message);
                }
            }
        }

        private async Task RunLibraryHealthCheckAsync()
        {
            libraryHealthCheckToolStripMenuItem.Enabled = false;
            Cursor previousCursor = Cursor;
            Cursor = Cursors.WaitCursor;
            List<ModelCard> cards = (Datastore.modelcards ?? []).ToList();
            int totalClips = cards.Sum(card => card.clips?.Count ?? 0);
            using Form progressDialog = new()
            {
                Text = "Library Health Check",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ControlBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(460, 92),
                AutoScaleMode = AutoScaleMode.Dpi
            };
            Label statusLabel = new()
            {
                AutoSize = false,
                Left = 16,
                Top = 14,
                Width = 428,
                Height = 24,
                Text = "Starting library health check..."
            };
            ProgressBar progressBar = new()
            {
                AccessibleDescription =
                    "Shows progress through the installed clip library.",
                Left = 16,
                Top = 48,
                Width = 428,
                Height = 22,
                Minimum = 0,
                Maximum = Math.Max(1, totalClips)
            };
            progressDialog.Controls.Add(statusLabel);
            progressDialog.Controls.Add(progressBar);
            _ = TooltipManager.Attach(progressDialog,
                Properties.Settings.Default.TooltipInitialDelay);
            progressDialog.Show(this);
            IProgress<(int Checked, int Total, string Status)> progress =
                new Progress<(int Checked, int Total, string Status)>(value =>
                {
                    progressBar.Maximum = Math.Max(1, value.Total);
                    progressBar.Value = Math.Clamp(
                        value.Checked, 0, progressBar.Maximum);
                    statusLabel.Text = value.Status;
                });
            try
            {
                (string report, int problems) = await Task.Run(() =>
                    BuildLibraryHealthReport(cards, progress));
                progress.Report((totalClips, totalClips, "Writing report..."));
                string folder = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "IStripperQuickPlayer");
                string path = Path.Combine(folder,
                    "library-health-check.txt");
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, RedactUsernamesFromPaths(report));
                progressDialog.Close();

                DialogResult open = MessageBox.Show(this,
                    problems == 0
                        ? "Library health check passed.\r\n\r\nOpen the report?"
                        : $"Library health check found {problems:N0} " +
                          "problem(s).\r\n\r\nOpen the report?",
                    "Library Health Check", MessageBoxButtons.YesNo,
                    problems == 0 ? MessageBoxIcon.Information :
                        MessageBoxIcon.Warning);
                if (open == DialogResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(path)
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(this,
                    "The library health check could not complete.\r\n" +
                    exception.Message, "Library Health Check",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (!progressDialog.IsDisposed)
                    progressDialog.Close();
                Cursor = previousCursor;
                libraryHealthCheckToolStripMenuItem.Enabled = true;
            }
        }

        private static (string Report, int Problems) BuildLibraryHealthReport(
            IReadOnlyCollection<ModelCard> cards,
            IProgress<(int Checked, int Total, string Status)>? progress = null)
        {
            List<ModelClip> clips = cards.SelectMany(card =>
                card.clips ?? []).ToList();
            progress?.Report((0, clips.Count, "Checking model drives..."));
            using RegistryKey? systemKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Totem\vghd\System", false);
            string primary = systemKey?.GetValue(
                "ModelsPath", "")?.ToString() ?? "";
            string[] additional = systemKey?.GetValue(
                "ModelsMultiPath", Array.Empty<string>()) as string[]
                ?? Array.Empty<string>();
            string[] roots = new[] { primary }.Concat(additional)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.TrimEndingDirectorySeparator(path.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] availableRoots = roots.Where(Directory.Exists).ToArray();
            string[] unavailableRoots = roots.Except(availableRoots,
                StringComparer.OrdinalIgnoreCase).ToArray();

            int indexedClips = 0;
            int disabledClips = 0;
            int missingFiles = 0;
            int emptyFiles = 0;
            int unreadableFiles = 0;
            int duplicateFiles = 0;
            List<string> samples = [];

            for (int index = 0; index < clips.Count; index++)
            {
                ModelClip clip = clips[index];
                int checkedCount = index + 1;
                if (checkedCount == 1 || checkedCount % 25 == 0 ||
                    checkedCount == clips.Count)
                {
                    progress?.Report((checkedCount, clips.Count,
                        $"Checking clips: {checkedCount:N0} / " +
                        $"{clips.Count:N0}"));
                }
                string fileName = Path.GetFileName(clip.clipName ?? "");
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;
                indexedClips++;
                if (clip.isEnabled != true)
                    disabledClips++;

                string cardTag = fileName.Split('_')[0];
                string[] locations = availableRoots.Select(root =>
                        Path.Combine(root, cardTag, fileName))
                    .Where(File.Exists).ToArray();
                if (locations.Length == 0)
                {
                    missingFiles++;
                    if (samples.Count < 200)
                        samples.Add($"Missing: {cardTag}\\{fileName}");
                    continue;
                }
                if (locations.Length > 1)
                {
                    duplicateFiles++;
                    if (samples.Count < 200)
                        samples.Add("Duplicate: " +
                            string.Join(" | ", locations));
                }

                try
                {
                    FileInfo file = new(locations[0]);
                    if (file.Length == 0)
                    {
                        emptyFiles++;
                        if (samples.Count < 200)
                            samples.Add($"Empty: {locations[0]}");
                        continue;
                    }
                    using FileStream stream = new(locations[0],
                        FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    _ = stream.ReadByte();
                }
                catch (Exception exception)
                {
                    unreadableFiles++;
                    if (samples.Count < 200)
                    {
                        samples.Add($"Unreadable: {locations[0]} | " +
                            $"{exception.GetType().Name}: " +
                            exception.Message);
                    }
                }
            }

            int cardsWithoutClips = cards.Count(card =>
                card.clips == null || card.clips.Count == 0);
            progress?.Report((clips.Count, clips.Count,
                "Preparing results..."));
            int problems = unavailableRoots.Length + missingFiles +
                emptyFiles + unreadableFiles + duplicateFiles +
                cardsWithoutClips;
            StringBuilder report = new();
            report.AppendLine(
                $"Library health check: {DateTimeOffset.Now:O}");
            report.AppendLine(
                $"QuickPlayer version: {Application.ProductVersion}");
            report.AppendLine($"Configured roots: {roots.Length}");
            foreach (string root in roots)
            {
                report.AppendLine($"  {root} | available=" +
                    Directory.Exists(root));
            }
            report.AppendLine($"Indexed cards: {cards.Count}");
            report.AppendLine($"Indexed clips: {indexedClips}");
            report.AppendLine($"Disabled clips: {disabledClips}");
            report.AppendLine($"Cards without clips: {cardsWithoutClips}");
            report.AppendLine($"Unavailable roots: {unavailableRoots.Length}");
            report.AppendLine($"Missing clip files: {missingFiles}");
            report.AppendLine($"Zero-byte clip files: {emptyFiles}");
            report.AppendLine($"Unreadable clip files: {unreadableFiles}");
            report.AppendLine(
                $"Clips duplicated across roots: {duplicateFiles}");
            report.AppendLine($"Problems: {problems}");
            report.AppendLine(
                $"Problem samples (first {samples.Count}, maximum 200):");
            foreach (string sample in samples)
                report.AppendLine("  " + sample);
            return (report.ToString(), problems);
        }

        private static string RedactUsernamesFromPaths(string text)
        {
            string profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                text = text.Replace(
                    profile, "%USERPROFILE%",
                    StringComparison.OrdinalIgnoreCase);
            }
            return System.Text.RegularExpressions.Regex.Replace(
                text, @"(?i)\b[A-Z]:\\Users\\[^\\\r\n]+",
                "%USERPROFILE%");
        }

        private static void AppendZeroClipDiagnostics(
            StringBuilder diagnostics, ModelsLstLoader loader,
            IReadOnlyDictionary<string, HashSet<string>> rootFolders)
        {
            var cards = loader.ZeroClipCards;
            diagnostics.AppendLine(
                $"Zero-clip catalogue records: {cards.Count}");
            if (cards.Count == 0)
                return;

            diagnostics.AppendLine(
                $"Zero-clip flags: inCollection={cards.Count(card =>
                    card.InCollection)} | downloaded={cards.Count(card =>
                    card.Downloaded)} | enabled={cards.Count(card =>
                    card.Enabled)} | hidden={cards.Count(card =>
                    card.Hidden)}");

            int foldersFound = 0;
            int cardsWithFiles = 0;
            int foldersWithoutFiles = 0;
            int cardsWithoutFolders = 0;
            int fileReadErrors = 0;
            int totalFiles = 0;
            int matchingFiles = 0;
            int markerFiles = 0;
            Dictionary<string, int> cardsByRoot = rootFolders.Keys
                .ToDictionary(root => root, _ => 0,
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> filesByRoot = rootFolders.Keys
                .ToDictionary(root => root, _ => 0,
                    StringComparer.OrdinalIgnoreCase);
            List<string> fileSamples = [];
            List<string> missingSamples = [];

            foreach (var card in cards)
            {
                string physicalTag = card.Section == "market"
                    ? card.Tag.Split('-')[0]
                    : card.Tag;
                bool folderFound = false;
                bool markerFound = false;
                int cardFiles = 0;
                int cardMatchingFiles = 0;
                List<string> locations = [];
                List<string> examples = [];

                foreach (var root in rootFolders)
                {
                    if (!root.Value.Contains(physicalTag))
                        continue;

                    folderFound = true;
                    string folder = Path.Combine(root.Key, physicalTag);
                    try
                    {
                        string[] files = Directory.GetFiles(
                            folder, "*.vghd", SearchOption.TopDirectoryOnly);
                        int matching = files.Count(file =>
                            Path.GetFileName(file).StartsWith(
                                physicalTag + "_",
                                StringComparison.OrdinalIgnoreCase));
                        bool marker = File.Exists(Path.Combine(
                            folder, physicalTag + ".vhdshows"));
                        cardFiles += files.Length;
                        cardMatchingFiles += matching;
                        markerFound |= marker;
                        if (files.Length > 0)
                        {
                            cardsByRoot[root.Key]++;
                            filesByRoot[root.Key] += files.Length;
                            examples.AddRange(files.Take(2)
                                .Select(Path.GetFileName)
                                .OfType<string>());
                        }
                        locations.Add(
                            $"{root.Key} files={files.Length} " +
                            $"matching={matching} marker={marker}");
                    }
                    catch (Exception exception)
                    {
                        fileReadErrors++;
                        locations.Add(
                            $"{root.Key} error={exception.GetType().Name}: " +
                            exception.Message);
                    }
                }

                if (folderFound)
                    foldersFound++;
                else
                    cardsWithoutFolders++;
                if (cardFiles > 0)
                    cardsWithFiles++;
                else if (folderFound)
                    foldersWithoutFiles++;
                totalFiles += cardFiles;
                matchingFiles += cardMatchingFiles;
                if (markerFound)
                    markerFiles++;

                string sample =
                    $"{card.Section}[{card.Index}] tag={card.Tag} " +
                    $"cardOffset={card.CardOffset} " +
                    $"clipCountOffset={card.ClipCountOffset} " +
                    $"flags=collection:{card.InCollection}," +
                    $"downloaded:{card.Downloaded},enabled:{card.Enabled}," +
                    $"hidden:{card.Hidden} | " +
                    (locations.Count == 0
                        ? "no physical folder"
                        : string.Join(" | ", locations)) +
                    (examples.Count == 0
                        ? ""
                        : $" | examples={string.Join(",", examples)}");
                if (cardFiles > 0 && fileSamples.Count < 25)
                    fileSamples.Add(sample);
                else if (cardFiles == 0 && missingSamples.Count < 10)
                    missingSamples.Add(sample);
            }

            diagnostics.AppendLine(
                $"Zero-clip physical check: folders found={foldersFound} | " +
                $"cards with .vghd={cardsWithFiles} | " +
                $"folders without .vghd={foldersWithoutFiles} | " +
                $"no folder={cardsWithoutFolders} | read errors={fileReadErrors}");
            diagnostics.AppendLine(
                $"Zero-clip files: total .vghd={totalFiles} | " +
                $"matching card tag={matchingFiles} | " +
                $"cards with .vhdshows marker={markerFiles}");
            foreach (string root in rootFolders.Keys)
            {
                diagnostics.AppendLine(
                    $"Zero-clip files in {root}: cards={cardsByRoot[root]} | " +
                    $"files={filesByRoot[root]}");
            }
            diagnostics.AppendLine(
                "Zero-clip samples with physical files " +
                $"(first {fileSamples.Count}):");
            foreach (string sample in fileSamples)
                diagnostics.AppendLine("  " + sample);
            diagnostics.AppendLine(
                "Zero-clip samples without physical files " +
                $"(first {missingSamples.Count}):");
            foreach (string sample in missingSamples)
                diagnostics.AppendLine("  " + sample);
        }

        private void PersistModels()
        {
            string modelfilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "models.bin");
            string modelfolder = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer");
            if (!Directory.Exists(modelfolder))
                Directory.CreateDirectory(modelfolder);
            Serialize(Datastore.modelcards, modelfilepath);
        }

        private void RetrieveModels(Action? afterPopulation = null)
        {
            string modelfilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "models.bin");
            if (System.IO.File.Exists(modelfilepath))
            {
                List<ModelCard>? models = Deserialize(modelfilepath);
                if (models is null || models.Count == 0)
                {
                    if (apiOnlyMode)
                        ReloadModels();
                    else
                        BeginInvoke((Action)(() =>
                            ReloadModels(afterPopulation)));
                }
                else
                {
                    Datastore.modelcards = models
                        .Where(card => !card.IsCustom).ToList();
                    AppendCustomCards();
                    if (!apiOnlyMode)
                    {
                        CardOverlayLoader.Reload(Datastore.modelcards, myData);
                        QueueModelPopulation(afterPopulation);
                    }
                }
            }
            else
            {
                if (apiOnlyMode)
                    ReloadModels();
                else
                    BeginInvoke((Action)(() =>
                        ReloadModels(afterPopulation)));
            }
        }

        private void QueueModelPopulation(Action? afterPopulation)
        {
            BeginInvoke((Action)(() =>
            {
                PopulateModelListview();
                afterPopulation?.Invoke();
            }));
        }

        ListViewItem[]? items; //stores the list of virtualized cards for modelList operations
        internal void PopulateModelListview()
        {
            if (cardRenderer == null)
                return;
            //save the selected card, we can reselect it at the end if it's still valid
            string currentText = "";
            if (listModelsNew.SelectedItems.Count > 0)
            {
                currentText = listModelsNew.SelectedItems[0].Text;
            }
            if (Datastore.modelcards == null)
            {
                listModelsNew.Items.Clear();
                return;
            }

            List<ModelCard> currentCards = Datastore.modelcards;
            if (!string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                TextQuery query = TextQuery.Parse(txtSearch.Text);
                currentCards = currentCards.Where(card =>
                    query.Matches(CreateSearchDocument(card))).ToList();
            }

            currentCards = (Filter(currentCards) ?? [])
                .Where(card => card.clips?.Count > 0)
                .ToList();

            string sortText = cmbSortBy.Text;
            if (cmbSortDirection.Text.StartsWith("Desc"))
                sortText += " (Descending)";

            switch (sortText)
            {
                case "My Rating":
                    if (myData != null) currentCards = currentCards.OrderBy(i => myData.GetCardRating(i.name)).ToList();
                    break;
                case "My Rating (Descending)":
                    if (myData != null) currentCards = currentCards.OrderByDescending(i => myData.GetCardRating(i.name)).ToList();
                    break;
                case "":
                case "Model Name":
                    currentCards = currentCards.OrderBy(i => i.modelName).ToList();
                    break;
                case " (Descending)":
                case "Model Name (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.modelName).ToList();
                    break;
                case "Rating":
                    currentCards = currentCards.OrderBy(i => i.rating).ToList();
                    break;
                case "Rating (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.rating).ToList();
                    break;
                case "Age":
                    currentCards = currentCards.OrderBy(i => i.modelAge).ToList();
                    break;
                case "Age (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.modelAge).ToList();
                    break;
                case "Breast Size":
                    currentCards = currentCards.OrderBy(i => i.bust).ToList();
                    break;
                case "Breast Size (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.bust).ToList();
                    break;
                case "Waist":
                    currentCards = currentCards.OrderBy(i => i.waist).ToList();
                    break;
                case "Waist (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.waist).ToList();
                    break;
                case "Hips":
                    currentCards = currentCards.OrderBy(i => i.hips).ToList();
                    break;
                case "Hips (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.hips).ToList();
                    break;
                case "Ethnicity":
                    currentCards = currentCards.OrderBy(i => i.ethnicity).ToList();
                    break;
                case "Ethnicity (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.ethnicity).ToList();
                    break;
                case "Height":
                    currentCards = currentCards.OrderBy(i => i.height).ToList();
                    break;
                case "Height (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.height).ToList();
                    break;
                case "Date Purchased (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.datePurchased).ToList();
                    break;
                case "Date Purchased":
                    currentCards = currentCards.OrderBy(i => i.datePurchased).ToList();
                    break;
                case "Release Date (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.dateReleased).ToList();
                    break;
                case "Release Date":
                    currentCards = currentCards.OrderBy(i => i.dateReleased).ToList();
                    break;
                default:
                    break;
            }


            items = new ListViewItem[currentCards.Count];
            int idx = 0;
            foreach (var card in currentCards)
            {
                items[idx] = new ListViewItem(
                    card.modelName + Environment.NewLine + card.outfit, 0);
                items[idx].Tag = card.name;
                items[idx].ImageIndex = idx;
                idx++;
            }
            SetModelNewImageList();
            RebuildAutomaticQueue();

            lblModelsLoaded.Text = "Cards Shown: " + listModelsNew.Items.Count + "/" + Datastore.modelcards.Where(c => c.clips != null && c.clips.Count > 0).Count();

            //set the selected card back to what we had selected at start of the function
            if (currentText != "")
            {
                listModelsNew.ClearSelection();
                int index = Array.FindIndex(
                    items, item => item.Text == currentText);
                if (index >= 0)
                {
                    listModelsNew.SelectWhere(x => x.Text == currentText);
                    listModelsNew.EnsureVisible(index);
                }
            }
            this.BeginInvoke((Action)(() => TaskbarThumbnail()));
        }

        private void SignalInitialCardListReady()
        {
            if (initialCardListReady)
                return;
            initialCardListReady = true;
            InitialCardListReady?.Invoke(this, EventArgs.Empty);
        }

        internal void RefreshInitialCardDisplay()
        {
            deferCardCompositionUntilShown = false;
            // The DirectComposition renderer consumes the GPU card scene that
            // CardRenderer builds during a normal list paint.  Prime that
            // scene now that the HWND is visible, while the splash still
            // covers the window, before switching the list to DComp output.
            listModelsNew.Invalidate();
            listModelsNew.Update();
            UpdateCardOverlayRenderingPath();
            listModelsNew.Invalidate();
            listModelsNew.Update();
            directCompositionCardOverlays?.Render();
        }

        private TextSearchDocument CreateSearchDocument(ModelCard card)
        {
            string tags = string.Join(' ', card.tags);
            string userTags = myData == null
                ? string.Empty
                : string.Join(' ', myData.GetCardTags(card.name));
            string all = string.Join('\n',
                card.modelName,
                card.name,
                Properties.Settings.Default.ShowOutfitInSearch
                    ? card.outfit : string.Empty,
                Properties.Settings.Default.ShowDescInSearch
                    ? card.description : string.Empty,
                tags,
                userTags);
            return new TextSearchDocument(all, card.modelName ?? string.Empty,
                card.name, card.outfit, card.description,
                string.Join(' ', tags, userTags), card.ethnicity ?? string.Empty,
                card.hair ?? string.Empty);
        }

        private void SetModelNewImageList()
        {
            if (items == null)
                return;
            var itemsNew = new ImageListViewItem[items.Length];
            int idx = 0;

            cardRenderer.updating = true;
            directCompositionCardOverlays?.ResetCardImages();
            cardRenderer.ResetCardScene();
            listModelsNew.SuspendLayout();
            listModelsNew.Items.Clear();
            listModelsNew.ThumbnailSize = CardThumbnailSize(
                listModelsNew.DeviceDpi, cardScale);
            foreach (var i in items)
            {
                var im = new ImageListViewItem();
                im.FileName = ".";
                im.Text = i.Text;
                im.Tag = i.Tag;
                itemsNew[idx] = im;
                idx++;
            }

            listModelsNew.Items.AddRange(itemsNew);
            listModelsNew.ResumeLayout(false);
            cardRenderer.updating = false;
            listModelsNew.Refresh();
        }

        private List<ModelCard>? Filter(List<ModelCard>? currentCards)
        {
            if (chkFavourite.Checked && myData != null)
                currentCards = currentCards.Where(c => myData.GetCardFavourite(c.name)).ToList();
            currentCards = currentCards.Where(c => (c.dateReleased >= filterSettings.minDate && c.dateReleased <= filterSettings.maxDate) || c.dateReleased == new DateTime(1,1,1)).ToList();
            if ((filterSettings.minMyRating > 0 || filterSettings.maxMyRating < 10) && myData != null)
                currentCards = currentCards.Where(c => myData.GetCardRating(c.name) >= filterSettings.minMyRating
                && myData.GetCardRating(c.name) <= filterSettings.maxMyRating).ToList();
            decimal bustMinimum = MeasurementBounds(
                Datastore.modelcards.Select(card => card.bust)).Minimum;
            decimal waistMinimum = MeasurementBounds(
                Datastore.modelcards.Select(card => card.waist)).Minimum;
            decimal hipsMinimum = MeasurementBounds(
                Datastore.modelcards.Select(card => card.hips)).Minimum;
            currentCards = currentCards.Where(c => ((c.modelAge >= filterSettings.minAge && c.modelAge <= filterSettings.maxAge) || c.modelAge == 0 || c.modelAge > 99)
                && MatchesMeasurement(c.bust, filterSettings.minBust, filterSettings.maxBust, bustMinimum)
                && MatchesMeasurement(c.waist, filterSettings.minWaist, filterSettings.maxWaist, waistMinimum)
                && MatchesMeasurement(c.hips, filterSettings.minHips, filterSettings.maxHips, hipsMinimum)
                && ((c.rating - 5M >= filterSettings.minRating && c.rating - 5M <= filterSettings.maxRating) || c.rating == 0)
                ).ToList();

            if (!String.IsNullOrEmpty(filterSettings.tags))
            {
                string[] parts = filterSettings.tags.ToLower().Split(" and ").Select(p => p.Trim()).ToArray();


                foreach (string p in parts)
                {

                    List<string> taglist = p.Split(" or ").Select(p => p.Trim()).ToList();
                    if (p.Contains("!"))
                    {

                        List<ModelCard>? poslist = null;
                        List<ModelCard>? neglist = currentCards;
                        foreach (string tag in taglist.Where(x => !x.Contains("!")))
                        {
                            //do all the positives first
                            poslist = currentCards.Where(c => (myData != null && string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(tag)) || string.Join(",", c.tags).ContainsWithNot(tag) || c.name.ContainsWithNot(tag)).ToList();
                        }
                        if (poslist == null) poslist = new List<ModelCard> { };
                        foreach (string tag in taglist.Where(x => x.Contains("!")))
                        {
                            neglist = neglist.Where(c => (myData != null && string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(tag)) && string.Join(",", c.tags).ContainsWithNot(tag) && c.name.ContainsWithNot(tag)).ToList();
                        }
                        if (poslist == null) currentCards = new List<ModelCard> { };
                        else
                            if (neglist == null) neglist = new List<ModelCard> { };
                        currentCards = poslist.Union(neglist).ToList();
                    }
                    else
                    {
                        currentCards = currentCards.Where(c => (c.modelName != null && taglist.Any(y => c.modelName.ContainsWithNot(y)))
                            || taglist.Any(d => c.name.ContainsWithNot(d))
                            || myData != null && taglist.Any(x => string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(x.Trim())) || taglist.Any(y => string.Join(",", c.tags).ContainsWithNot(y))).ToList();
                    }


                }

            }
            List<Enum> enabledcollections = new List<Enum> { };
            if (filterSettings.IStripperXXX) enabledcollections.Add(Enums.CollectionType.IStripperXXX);
            if (filterSettings.DeskBabes) enabledcollections.Add(Enums.CollectionType.DeskBabes);
            if (filterSettings.IStripperClassic) enabledcollections.Add(Enums.CollectionType.IStripperClassic);
            if (filterSettings.VGClassic) enabledcollections.Add(Enums.CollectionType.VGClassic);
            if (filterSettings.IStripper) enabledcollections.Add(Enums.CollectionType.IStripper);
            if (filterSettings.VirtuaGuy) enabledcollections.Add(Enums.CollectionType.VirtuaGuy);
            if (filterSettings.TradingCard) enabledcollections.Add(Enums.CollectionType.TradingCard);
            if (filterSettings.Custom) enabledcollections.Add(Enums.CollectionType.Custom);

            HashSet<string> selectedGenders = filterSettings.genders.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool allGendersSelected = CustomShowStore.GenderValues.All(
                selectedGenders.Contains);
            if (!allGendersSelected)
                currentCards = currentCards.Where(card => selectedGenders.Contains(
                    CardGender(card))).ToList();

            if (filterSettings.Normal && !filterSettings.Special)
                currentCards = currentCards.Where(c => c.exclusive != null && !(bool)c.exclusive).ToList();
            else if (filterSettings.Special && !filterSettings.Normal)
                currentCards = currentCards.Where(c => c.exclusive != null && (bool)c.exclusive).ToList();

            currentCards = currentCards.Where(c => enabledcollections.Contains(c.collection)||c.collection == Enums.CollectionType.Undefined).ToList();
            if (!filterSettings.RealtimeCustom)
                currentCards = currentCards.Where(card =>
                    !IsRealtimeCustomShow(card)).ToList();

            try
            {
                if (Properties.Settings.Default.ShowKitty && (txtSearch.Text == "" || txtSearch.Text.ToLower().Contains("kitty")))
                    currentCards.Add(Datastore.modelcards.Where(c => c.name == "f9998").First());
            }
            catch (Exception) { }

            return currentCards;
        }

        internal static string CardGender(ModelCard card) => card.IsCustom
            ? card.gender ?? "Female"
            : card.collection is Enums.CollectionType.VirtuaGuy or
                Enums.CollectionType.VGClassic ? "Male" : "Female";

        internal static bool IsRealtimeCustomShow(ModelCard card) =>
            card.IsCustom && card.clips?.Any(clip =>
                CustomClipMedia.IsRealtimeMode(clip.customMediaMode)) == true;

        internal static bool VerifyRealtimeCardFiltering()
        {
            ModelCard paired = new()
            {
                collection = Enums.CollectionType.Custom,
                clips = [new ModelClip
                {
                    customMediaMode = CustomClipMedia.PairedAlphaMode
                }]
            };
            ModelCard realtime = new()
            {
                collection = Enums.CollectionType.Custom,
                clips = [new ModelClip
                {
                    customMediaMode = CustomClipMedia.RvmOnnxMode
                }]
            };
            ModelCard mixed = new()
            {
                collection = Enums.CollectionType.Custom,
                clips = [new ModelClip
                {
                    customMediaMode = CustomClipMedia.PairedAlphaMode
                }, new ModelClip
                {
                    customMediaMode = CustomClipMedia.RvmOnnxMode
                }]
            };
            return !IsRealtimeCustomShow(paired) &&
                IsRealtimeCustomShow(realtime) && IsRealtimeCustomShow(mixed);
        }

        internal static bool MatchesMeasurement(
            decimal? value, decimal minimum, decimal maximum,
            decimal availableMinimum) =>
            value is null or 0 || value > 99
                ? minimum <= availableMinimum
                : value >= minimum && value <= maximum;

        internal static (decimal Minimum, decimal Maximum)
            MeasurementBounds(IEnumerable<decimal?> values)
        {
            decimal[] known = values
                .Where(value => value is > 0 and <= 99)
                .Select(value => value!.Value)
                .ToArray();
            return known.Length == 0
                ? (0, 99)
                : (Math.Floor(known.Min()), Math.Ceiling(known.Max()));
        }

        internal void setFilter(string v)
        {
            this.BeginInvoke((Action)(() => { PopulateFilterList(); cmbFilter.SelectedItem = v; }));
        }

        internal List<ModelCard>? Deserialize(String filename)
        {
            try
            {
                if (Persistence.IsLegacy(filename))
                {
                    Persistence.MoveLegacyAside(filename);
                    return null;
                }

                List<ModelCard> models = Persistence.Load<List<ModelCard>>(filename);
                if (!apiOnlyMode)
                    foreach (ModelCard card in models)
                        card.image = ModelsLstLoader.LoadCardImage(card);
                return models;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal void Serialize(List<ModelCard>? emps, String filename)
        {
            Persistence.Save(filename, emps ?? new List<ModelCard>());
        }

        internal void SerializeFilter(FilterSettings filter, String filename)
        {
            Persistence.Save(filename, filter);
        }

        internal FilterSettings? DeserializeFilter(string filename)
        {
            try
            {
                return Persistence.Load<FilterSettings>(filename);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal void SaveMyData()
        {
            string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "mydata.bin");
            lock (playbackHistoryLock)
                Persistence.Save(mdatafilepath, myData ?? new MyData());
        }

        internal MyData RetrieveMyData()
        {

            try
            {
                string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "mydata.bin");
                if (!System.IO.File.Exists(mdatafilepath)) return new MyData();
                MyData data = Persistence.Load<MyData>(mdatafilepath);
                data.Normalize();
                return data;
            }
            catch (Exception ex)
            {
                if (apiOnlyMode)
                    Debug.WriteLine("Error reading MyData file: " + ex);
                else
                    MessageBox.Show("Error reading MyData file\r\n" + ex.Message);
                return new MyData();
            }
        }

        private void BackupQuickPlayerData()
        {
            using SaveFileDialog dialog = new()
            {
                Filter = "QuickPlayer backup (*.iqpb)|*.iqpb",
                FileName = $"IStripperQuickPlayer-{DateTime.Now:yyyy-MM-dd}.iqpb",
                AddExtension = true,
                DefaultExt = "iqpb"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                SaveMyData();
                FilterSettingsList.Persist();
                Properties.Settings.Default.Save();
                QuickPlayerBackup backup = new()
                {
                    UserData = myData ?? new MyData(),
                    Filters = FilterSettingsList.filters,
                    Settings = Properties.Settings.Default.Properties
                        .Cast<System.Configuration.SettingsProperty>()
                        .ToDictionary(property => property.Name, property =>
                            Properties.Settings.Default.PropertyValues[
                                property.Name]?.SerializedValue?.ToString())
                };
                lock (playbackHistoryLock)
                    Persistence.Save(dialog.FileName, backup);
                MessageBox.Show(this, "QuickPlayer data was backed up.",
                    "Backup Complete", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The backup could not be created.\r\n" +
                    ex.Message, "Backup Failed", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void RestoreQuickPlayerData()
        {
            using OpenFileDialog dialog = new()
            {
                Filter = "QuickPlayer backup (*.iqpb)|*.iqpb",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                QuickPlayerBackup backup =
                    Persistence.Load<QuickPlayerBackup>(dialog.FileName);
                if (backup.FormatVersion != 1 || backup.UserData == null ||
                    backup.Filters == null || backup.Settings == null)
                {
                    throw new InvalidDataException(
                        "This is not a supported QuickPlayer backup.");
                }
                if (MessageBox.Show(this,
                        "Restore all QuickPlayer settings, filters, ratings, " +
                        "tags, favourites and playback history?\r\n\r\n" +
                        "QuickPlayer will restart afterwards.",
                        "Restore Backup", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }

                backup.UserData.Normalize();
                myData = backup.UserData;
                myData.Changed += SaveMyData;
                FilterSettingsList.filters = backup.Filters;
                foreach (KeyValuePair<string, string?> setting in backup.Settings)
                {
                    if (Properties.Settings.Default.Properties[setting.Key] ==
                        null)
                    {
                        continue;
                    }

                    var value = Properties.Settings.Default.PropertyValues[
                        setting.Key];
                    value.SerializedValue = setting.Value;
                    value.IsDirty = true;
                }
                SaveMyData();
                FilterSettingsList.Persist();
                Properties.Settings.Default.Save();
                restoringBackup = true;
                Application.Restart();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The backup could not be restored.\r\n" +
                    ex.Message, "Restore Failed", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ShowPlaybackHistory()
        {
            using Form dialog = new()
            {
                Text = "Playback History",
                StartPosition = FormStartPosition.CenterParent,
                Width = 850,
                Height = 520,
                MinimizeBox = false,
                MaximizeBox = false
            };
            DataGridView grid = new()
            {
                AccessibleDescription =
                    "View the most recently played cards and clips.",
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            FlowLayoutPanel buttons = new()
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(6)
            };
            Button close = new() { Text = "Close", AutoSize = true };
            Button clear = new() { Text = "Clear History", AutoSize = true };

            void RefreshRows()
            {
                lock (playbackHistoryLock)
                {
                    grid.DataSource = myData?.PlaybackHistory
                        .AsEnumerable()
                        .Reverse()
                        .Select(entry =>
                        {
                            string[] parts = entry.AnimationPath.Split('\\');
                            string tag = parts.FirstOrDefault() ?? "";
                            string clipName = parts.LastOrDefault() ?? "";
                            ModelCard? card = Datastore.findCardByTag(tag);
                            ModelClip? clip = card?.clips?.FirstOrDefault(
                                item => string.Equals(item.clipName, clipName,
                                    StringComparison.OrdinalIgnoreCase));
                            return new
                            {
                                Played = entry.PlayedUtc.ToLocalTime(),
                                Card = card == null ? tag :
                                    $"{card.modelName}, {card.outfit}",
                                Clip = clip?.clipNumber is int number
                                    ? $"Clip {number}" : clipName
                            };
                        }).ToList();
                }
            }

            close.Click += (_, _) => dialog.Close();
            clear.Click += (_, _) =>
            {
                if (MessageBox.Show(dialog, "Clear all playback history?",
                        "Clear History", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }

                lock (playbackHistoryLock)
                    myData?.PlaybackHistory.Clear();
                SaveMyData();
                RefreshRows();
            };
            buttons.Controls.Add(close);
            buttons.Controls.Add(clear);
            dialog.Controls.Add(grid);
            dialog.Controls.Add(buttons);
            _ = TooltipManager.Attach(dialog,
                Properties.Settings.Default.TooltipInitialDelay);
            RefreshRows();
            dialog.ShowDialog(this);
        }

        private void lblModelsLoaded_Click(object sender, EventArgs e)
        {

        }

        private void listModelsNew_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listModelsNew.SelectedItems.Count > 0) listModelsNew.SelectedItems[0].Update();
            FilterClips();
        }

        private void FilterClips()
        {
            var col = listModelsNew.SelectedItems;
            if (col.Count > 0)
                loadListClips(listModelsNew.SelectedItems[0].Tag);
            else
                loadListClips(clipListTag);
        }

        private void loadListClips(object tag)
        {
            string cardTag = tag.ToString() ?? "";
            ModelCard? card = Datastore.findCardByTag(cardTag);
            if (card == null) return;
            // Items are about to be replaced with new ModelClip instances. A
            // remembered selection from the old list must not suppress the first
            // click on a newly published or reprocessed one-clip show.
            clipSelectionPlaybackTimer.Stop();
            clipSelectionPlaybackPending = false;
            lastchosen = "";
            clipListTag = cardTag;
            lblClipListTitle.Text =
                $"Model: {card.modelName}   Show: {card.outfit}";
            if (myData != null)
                txtUserTags.Text = string.Join(",", myData.GetCardTags(cardTag));
            listClips.BeginUpdate();
            listClips.Items.Clear();
            if (card.clips == null) return;

            var currentClips = card.clips;

            string[] parts = txtClipType.Text.ToLower().Split(" and ").Select(p => p.Trim()).ToArray();
            foreach (string p in parts)
            {

                List<string> taglist = p.Split(" or ").Select(p => p.Trim()).ToList();
                if (p.Contains("!"))
                {

                    List<ModelClip>? poslist = null;
                    List<ModelClip>? neglist = currentClips;
                    foreach (string t in taglist.Where(x => !x.Contains("!")))
                    {
                        //do all the positives first
                        poslist = currentClips.Where(c => (c.clipType != null && c.clipType.ContainsWithNot(t))).ToList();
                    }
                    if (poslist == null) poslist = new List<ModelClip> { };
                    foreach (string t in taglist.Where(x => x.Contains("!")))
                    {
                        neglist = neglist.Where(c => (c.clipType != null && c.clipType.ContainsWithNot(t))).ToList();
                    }
                    if (poslist == null) currentClips = new List<ModelClip> { };
                    else
                        if (neglist == null) neglist = new List<ModelClip> { };
                    currentClips = poslist.Union(neglist).ToList();
                }
                else
                {
                    currentClips = currentClips.Where(c => (c.clipType != null && taglist.Any(y => c.clipType.ContainsWithNot(y)))).ToList();
                }


            }


            foreach (ModelClip clip in currentClips)
            {
                bool addThis = false;
                switch (clip.hotnessCode)
                {
                    case Enums.HotnessCode.publ:
                        if (chkPublic.Checked)
                            addThis = true;
                        break;
                    case Enums.HotnessCode.nonudity:
                        if (chkNoNudity.Checked)
                            addThis = true;
                        break;
                    case Enums.HotnessCode.topless:
                        if (chkTopless.Checked)
                            addThis = true;
                        break;
                    case Enums.HotnessCode.nudity:
                        if (chkNudity.Checked)
                            addThis = true;
                        break;
                    case Enums.HotnessCode.fullnudity:
                        if (chkFullNudity.Checked)
                            addThis = true;
                        break;
                    case Enums.HotnessCode.xxx:
                        if (chkXXX.Checked)
                            addThis = true;
                        break;
                    default:
                        break;
                }
                if (Properties.Settings.Default.MinSizeMB > 0 && Properties.Settings.Default.MinSizeMB > clip.size / 1024 / 1024) addThis = false;
                if (clip.clipName != null && clip.clipName.Contains("demo") && !chkDemo.Checked) addThis = false;
                //if (!string.IsNullOrEmpty(txtClipType.Text) && !clip.clipType.ToLower().Contains(txtClipType.Text.ToLower())) addThis = false;
                if (addThis)
                {
                    ListViewItem item = new ListViewItem(new string[] {
                        clip.clipNumber?.ToString() ?? "",
                        clip.clipName ?? "",
                        clip.hotnessCode?.ToString() ?? "",
                        clip.clipType ?? "",
                        (clip.size / 1024 / 1024)?.ToString() + "MB" });
                    listClips.Items.Add(item);
                }
            }
            RefreshPlayingClipHighlight();
            listClips.EndUpdate();
            PositionPhotosButton();
            txtDescription.Text = card.description;
            lblAge.Text = "Age: " + card.modelAge;
            lblStats.Text = "Stats: " + card.bust + "/" + card.waist + "/" + card.hips;
            lblRatingScore.Text = card.rating is > 0
                ? "Rating: " + (card.rating - 5m)
                : "Rating: NA";
            lblCollection.Text = "CardType: " + card.collection.GetDescription();
            lblResolution.Text = "Res: " + card.resolution.GetDescription();
            lblTags.Text = "Tags: " + String.Join(",", card.tags);

        }

        private string lastchosen = "";
        private void listClips_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (selectingClipForContextMenu) return;
            if (listClips.SelectedItems.Count == 0) return;
            if (lastchosen == listClips.SelectedItems[0].SubItems[1].Text ||
                clickingNowPlaying)
            {
                clickingNowPlaying = false;
                return;
            }
            if (DeferClipSelectionPlaybackWhileMouseIsDown())
                return;
            PlaySelectedClip();
        }

        private void PlaySelectedClip()
        {
            if (panicActive || listClips.SelectedItems.Count == 0)
                return;
            ClearQueuedCardSession();
            string r = listClips.SelectedItems[0].SubItems[1].Text;
            ModelCard? card = Datastore.findCardByTag(clipListTag);
            ModelClip? clip = card?.clips?.FirstOrDefault(item =>
                string.Equals(item.clipName, r, StringComparison.OrdinalIgnoreCase));
            string full = clip == null ? "" : GetAnimationPath(clip);
            bool alreadyPlaying = !string.IsNullOrEmpty(full) && string.Equals(
                GetCurrentAnimationPath(), full, StringComparison.OrdinalIgnoreCase);
            bool started = false;
            if (!string.IsNullOrEmpty(full) && !alreadyPlaying)
            {
                StartUnqueuedCardSession(full, sequential: true);
                started = RequestAnimationPlayback(full);
                if (!started) ClearUnqueuedCardSession();
            }
            // A rejected request (for example during the final publication-state
            // transition) must remain immediately retryable.
            lastchosen = ClipSelectionWasHandled(alreadyPlaying, started) ? r : "";
        }

        private static bool ClipSelectionWasHandled(bool alreadyPlaying,
            bool requestSucceeded) => alreadyPlaying || requestSucceeded;


        private async void Form1_Load(object? sender, EventArgs e) =>
            await InitializeStartupAsync();

        internal async void BeginHiddenStartup()
        {
            deferCardCompositionUntilShown = true;
            await InitializeStartupAsync();
        }

        private async Task InitializeStartupAsync()
        {
            if (startupInitializationStarted)
                return;
            startupInitializationStarted = true;
            if (Properties.Settings.Default.UpdateSettings)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpdateSettings = false;
                Properties.Settings.Default.Save();
            }
            if (apiOnlyMode)
            {
                InitializeApiOnly();
                return;
            }
            RefreshTooltipDelay();
            Properties.Settings.Default.PropertyChanged += (_, _) =>
                Properties.Settings.Default.Save();
            spaceBelowClipList = this.Height - listClips.Bottom;
            spaceRightOfListModel = this.Width - listModelsNew.Right;
            lastAdjustedClientSize = ClientSize;
            if (Properties.Settings.Default.Maximised)
            {
                Location = Properties.Settings.Default.Location;
                WindowState = FormWindowState.Maximized;
                Size = Properties.Settings.Default.Size;
            }
            else if (Properties.Settings.Default.Minimised)
            {
                Location = Properties.Settings.Default.Location;
                WindowState = FormWindowState.Minimized;
                Size = Properties.Settings.Default.Size;
            }
            else
            {
                Location = Properties.Settings.Default.Location;
                if (Properties.Settings.Default.Size.Width > 0 && Properties.Settings.Default.Size.Height > 0)
                {
                    Size = Properties.Settings.Default.Size;
                }
            }
            //AdjustControls();
            //DPI_Per_Monitor.TryEnableDPIAware(this, SetUserFonts);
            this.Icon = Properties.Resources.df2284943cc77e7e1a5fa6a0da8ca265;
            culture.NumberFormat.NumberDecimalSeparator = ".";
            //await webModels.EnsureCoreWebView2Async();
            //devtoolsContext = await webModels.CoreWebView2.CreateDevToolsContextAsync();
            Wallpaper.CaptureOriginalDesktopState();
            lockPlayerToolStripMenuItem.Checked = Properties.Settings.Default.LockPlayer;
            playerlocked = lockPlayerToolStripMenuItem.Checked;
            enablePlaybackControlToolStripMenuItem.Checked =
                Properties.Settings.Default.EnablePlaybackControl;
            if (Properties.Settings.Default.EnablePlaybackControl)
                playbackTimelineTimer.Start();
            else
                playbackTimelineTimer.Stop();
            RefreshPlaybackControlVisibility();
            cmbSortBy.Text = Properties.Settings.Default.SortBy;
            cmbSortDirection.Text = Properties.Settings.Default.SortDirection;
            chkFavourite.Checked = Properties.Settings.Default.FavouritesFilter;
            menuShowRatingsStars.Checked = Properties.Settings.Default.ShowRatingStars;
            menuShowCardSortLabels.Checked =
                Properties.Settings.Default.ShowCardSortLabels;
            includeDescriptionInSearchToolStripMenuItem.Checked = Properties.Settings.Default.ShowDescInSearch;
            includeShowTitleInSearchToolStripMenuItem.Checked = Properties.Settings.Default.ShowOutfitInSearch;
            cardScaleSeekBar.Value = Math.Clamp(
                (int)Math.Round(Properties.Settings.Default.CardScale * 20),
                cardScaleSeekBar.Minimum, cardScaleSeekBar.Maximum);
            ApplyCardScale();
            cardScaleInitialized = true;
            trackBarZoomOnHover.Value = (decimal)(Properties.Settings.Default.ZoomOnHover);
            randomPlayOrderToolStripMenuItem.Checked = Properties.Settings.Default.Randomize;
            enforceCardFilterToolStripMenuItem.Checked =
                Properties.Settings.Default.EnforceCardFilter;
            enablePlayQueueToolStripMenuItem.Checked =
                Properties.Settings.Default.EnablePlayQueue;
            RefreshPlayQueueVisibility();
            _ = Task.Run(SetupRegHooks);

            //get number of monitors for wallpaper
            try
            {
                var wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());
                string[] monitorsChecked = Properties.Settings.Default.WallpaperMonitors.Split(",", StringSplitOptions.TrimEntries);
                for (uint i = 0; i < wallpaper.GetMonitorDevicePathCount(); i++)
                {
                    ToolStripMenuItem newitem = new ToolStripMenuItem("Monitor " + (i + 1).ToString());
                    newitem.CheckOnClick = true;
                    newitem.Tag = i;
                    newitem.ToolTipText =
                        $"Generate QuickPlayer wallpaper on monitor {i + 1}.";
                    if (monitorsChecked.Contains((i + 1).ToString())) newitem.Checked = true;
                    this.wallpaperToolStripMenuItem.DropDownItems.Add(newitem);
                    newitem.CheckedChanged += WallpaperMonitor_CheckedChanged;
                }
            }
            catch { }
            trackbarWallpaperBrightness.Value = Properties.Settings.Default.WallpaperBrightness;
            trackBarWallpaperTextSize.Value =
                Properties.Settings.Default.WallpaperTextSize;
            trackBarWallpaperLabelOpacity.Value =
                Properties.Settings.Default.WallpaperLabelOpacity;
            if (!Properties.Settings.Default.BlurWallpaper)
                Properties.Settings.Default.BlurRadius = 0;
            trackBarBlur.Value = Properties.Settings.Default.BlurRadius;
            automaticWallpaperToolStripMenuItem.Checked = Properties.Settings.Default.AutoWallpaper;
            showTextToolStripMenuItem.Checked = Properties.Settings.Default.WallpaperDetails;
            showKittyToolStripMenuItem.Checked = Properties.Settings.Default.ShowKitty;
            minimizeToTrayToolStripMenuItem.Checked = Properties.Settings.Default.MinimizeToTray;
            hideDesktopIconsToolStripMenuItem.Checked = Properties.Settings.Default.HideDesktopIcons;
            darkModeToolStripMenuItem.Checked = Properties.Settings.Default.DarkMode;
            SetSkin();
            myData = RetrieveMyData();
            myData.Changed += SaveMyData;
            FilterSettingsList.Load();
            PopulateFilterList();
            if (cardRenderer == null)
            {
                cardRenderer = new CardRenderer(
                    myData, cmbSortBy.Text, cardScale, culture, style);
                cardRenderer.mZoomRatio = (float)Properties.Settings.Default.ZoomOnHover;
                listModelsNew.SetRenderer(cardRenderer);
                cardRenderer.SetColours();
            }
            if (!deferCardCompositionUntilShown)
                UpdateCardOverlayRenderingPath();

            if (FilterSettingsList.filters.ContainsKey("Default"))
                cmbFilter.SelectedItem = "Default";
            //string REG_KEY = @"HKEY_CURRENT_USER\Software\Totem\vghd\parameters";
            //watcher = new RegistryWatcher(new Tuple<string, string>(REG_KEY, "CurrentAnim"));
            //watcher.RegistryChange += RegistryChanged;
            clickingNowPlaying = true;

            RetrieveModels(SignalInitialCardListReady);
            RestorePreviousQueue();
            GetNowPlaying();
            StartRestApi();
            clickingNowPlaying = false;
            SetupKeyHooks();
            await Task.Delay(1000);
            if (!formIsClosing && string.IsNullOrEmpty(
                    GetCurrentAnimationPath()))
                TryPlayNextQueuedAnimation();
            if (!Application.ProductVersion.Contains(
                    "-dev", StringComparison.OrdinalIgnoreCase))
                _ = CheckForUpdatesAsync(false);
        }

        private async Task CheckForUpdatesAsync(bool showUpToDateMessage)
        {
            try
            {
                using HttpRequestMessage request =
                    new(HttpMethod.Get, LatestReleaseApiUrl);
                request.Headers.UserAgent.ParseAdd(
                    $"IStripperQuickPlayer/{Application.ProductVersion}");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                using CancellationTokenSource timeout =
                    new(TimeSpan.FromSeconds(10));
                using HttpResponseMessage response =
                    await client.SendAsync(request, timeout.Token);
                response.EnsureSuccessStatusCode();
                using JsonDocument release = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(timeout.Token));

                string tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
                Version? latest = ParseUpdateVersion(tag);
                Version? current = ParseUpdateVersion(Application.ProductVersion);
                if (latest == null || current == null)
                    throw new InvalidDataException("GitHub returned invalid release information.");

                string currentDisplay = Application.ProductVersion.Split('+')[0];
                if (currentDisplay.Contains("-dev", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this,
                        $"Development build {currentDisplay}.\r\n" +
                        $"Latest GitHub release: {tag}.",
                        "Check for Updates", MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                if (latest <= current)
                {
                    if (showUpToDateMessage)
                    {
                        MessageBox.Show(this,
                            $"QuickPlayer is up to date (version {currentDisplay}).",
                            "Check for Updates", MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    return;
                }

                JsonElement setupAsset = release.RootElement
                    .GetProperty("assets")
                    .EnumerateArray()
                    .FirstOrDefault(asset =>
                        IsSetupAssetName(asset.GetProperty("name").GetString()));
                if (setupAsset.ValueKind == JsonValueKind.Undefined)
                    throw new InvalidDataException(
                        "The release does not contain a QuickPlayer Setup executable.");

                string installerName =
                    setupAsset.GetProperty("name").GetString() ?? "";
                string installerUrl =
                    setupAsset.GetProperty("browser_download_url").GetString() ?? "";
                string digest = setupAsset.GetProperty("digest").GetString() ?? "";
                if (!Uri.TryCreate(installerUrl, UriKind.Absolute,
                        out Uri? installerUri) ||
                    installerUri.Scheme != Uri.UriSchemeHttps ||
                    !installerUri.Host.Equals("github.com",
                        StringComparison.OrdinalIgnoreCase) ||
                    !digest.StartsWith("sha256:",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "GitHub returned invalid installer information.");
                }

                if (ShowUpdatePrompt(tag))
                    await DownloadAndLaunchUpdateAsync(
                        installerName, installerUri, digest[7..]);
            }
            catch (Exception exception) when (!showUpToDateMessage)
            {
                Debug.WriteLine("Update check failed: " + exception.Message);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this,
                    "Could not check for updates: " + exception.Message,
                    "Check for Updates", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private bool ShowUpdatePrompt(string tag)
        {
            using Form prompt = new()
            {
                Text = "Update Available",
                ClientSize = new Size(430, 145),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent,
                Font = Font
            };
            Label message = new()
            {
                AutoSize = false,
                Location = new Point(20, 18),
                Size = new Size(390, 62),
                Text = $"QuickPlayer {tag} is available.\r\n" +
                    "Download and install it now?"
            };
            Button update = new()
            {
                Text = "Update",
                DialogResult = DialogResult.OK,
                Location = new Point(224, 96),
                Size = new Size(90, 32)
            };
            Button later = new()
            {
                Text = "Later",
                DialogResult = DialogResult.Cancel,
                Location = new Point(320, 96),
                Size = new Size(90, 32)
            };
            prompt.Controls.AddRange(new Control[] { message, update, later });
            prompt.AcceptButton = update;
            prompt.CancelButton = later;
            _ = TooltipManager.Attach(prompt,
                Properties.Settings.Default.TooltipInitialDelay);
            return prompt.ShowDialog(this) == DialogResult.OK;
        }

        private async Task DownloadAndLaunchUpdateAsync(
            string installerName, Uri installerUri, string expectedSha256)
        {
            string previousMenuText =
                updateToolStripMenuItem.Text ?? "Check for Updates...";
            updateToolStripMenuItem.Text = "Downloading Update...";
            updateToolStripMenuItem.Enabled = false;
            UseWaitCursor = true;
            string partialPath = "";
            try
            {
                byte[] expectedDigest = Convert.FromHexString(expectedSha256);
                if (expectedDigest.Length != 32)
                    throw new InvalidDataException("The installer digest is invalid.");

                string updateDirectory = Path.Combine(Path.GetTempPath(),
                    "IStripperQuickPlayer", "updates");
                Directory.CreateDirectory(updateDirectory);
                string installerPath = Path.Combine(updateDirectory,
                    Path.GetFileName(installerName));
                partialPath = installerPath + ".download";

                using HttpRequestMessage request =
                    new(HttpMethod.Get, installerUri);
                request.Headers.UserAgent.ParseAdd(
                    $"IStripperQuickPlayer/{Application.ProductVersion}");
                using CancellationTokenSource timeout =
                    new(TimeSpan.FromMinutes(10));
                using HttpResponseMessage response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                response.EnsureSuccessStatusCode();
                await using (Stream source =
                    await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (FileStream destination = new(partialPath,
                    FileMode.Create, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous))
                {
                    await source.CopyToAsync(destination, timeout.Token);
                }

                byte[] actualDigest;
                await using (FileStream downloaded = File.OpenRead(partialPath))
                {
                    actualDigest =
                        await SHA256.HashDataAsync(downloaded, timeout.Token);
                }
                if (!CryptographicOperations.FixedTimeEquals(
                        actualDigest, expectedDigest))
                {
                    throw new InvalidDataException(
                        "The downloaded installer failed its SHA-256 check.");
                }

                File.Move(partialPath, installerPath, true);
                Process.Start(new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true
                });
                Application.Exit();
            }
            catch (Exception exception)
            {
                if (!string.IsNullOrEmpty(partialPath))
                {
                    try { File.Delete(partialPath); }
                    catch { }
                }
                MessageBox.Show(this,
                    "Could not install the update: " + exception.Message,
                    "QuickPlayer Update", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                if (!formIsClosing && !IsDisposed)
                {
                    UseWaitCursor = false;
                    updateToolStripMenuItem.Text = previousMenuText;
                    updateToolStripMenuItem.Enabled = true;
                }
            }
        }

        private static bool IsSetupAssetName(string? name) =>
            name?.StartsWith("IStripperQuickPlayer-",
                StringComparison.OrdinalIgnoreCase) == true &&
            name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase);

        private static Version? ParseUpdateVersion(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            string version = value.Trim().TrimStart('v', 'V');
            int suffix = version.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0)
                version = version[..suffix];
            return Version.TryParse(version, out Version? parsed) ? parsed : null;
        }

        private void PopulateFilterList()
        {
            cmbFilter.Items.Clear();
            foreach (var f in FilterSettingsList.filters)
                cmbFilter.Items.Add(f.Key);
        }

        private void AdjustWidthComboBox_DropDown(object sender, System.EventArgs e)
        {
            ComboBox senderComboBox = (ComboBox)sender;
            int width = senderComboBox.DropDownWidth;
            Graphics g = senderComboBox.CreateGraphics();
            Font font = senderComboBox.Font;
            int vertScrollBarWidth =
                (senderComboBox.Items.Count > senderComboBox.MaxDropDownItems)
                ? SystemInformation.VerticalScrollBarWidth : 0;

            int newWidth;
            foreach (string s in senderComboBox.Items)
            {
                newWidth = (int)g.MeasureString(s, font).Width
                    + vertScrollBarWidth;
                if (width < newWidth)
                {
                    width = newWidth;
                }
            }
            senderComboBox.DropDownWidth = width;
        }

        private void SetupKeyHooks()
        {
            UnregisterHotKeys();
            if (Properties.Settings.Default.NextClipEnabled)
                RegisterConfiguredHotKey(NextClipHotkeyId, Properties.Settings.Default.NextClipString);
            if (Properties.Settings.Default.NextCardEnabled)
                RegisterConfiguredHotKey(NextCardHotkeyId, Properties.Settings.Default.NextCardString);
            if (Properties.Settings.Default.ToggleLockEnabled)
                RegisterConfiguredHotKey(ToggleLockHotkeyId, Properties.Settings.Default.ToggleLockString);
            if (Properties.Settings.Default.PauseHotkeyEnabled)
                RegisterConfiguredHotKey(PauseHotkeyId, Properties.Settings.Default.PauseHotkeyString);
            if (Properties.Settings.Default.RewindHotkeyEnabled)
                RegisterConfiguredHotKey(RewindHotkeyId, Properties.Settings.Default.RewindHotkeyString);
            if (Properties.Settings.Default.FastForwardHotkeyEnabled)
                RegisterConfiguredHotKey(FastForwardHotkeyId, Properties.Settings.Default.FastForwardHotkeyString);
            if (Properties.Settings.Default.RestartClipHotkeyEnabled)
                RegisterConfiguredHotKey(RestartClipHotkeyId, Properties.Settings.Default.RestartClipHotkeyString);
            if (Properties.Settings.Default.LargePlayerHotkeyEnabled)
                RegisterConfiguredHotKey(LargePlayerHotkeyId, Properties.Settings.Default.LargePlayerHotkeyString);
            if (Properties.Settings.Default.SmallPlayerHotkeyEnabled)
                RegisterConfiguredHotKey(SmallPlayerHotkeyId, Properties.Settings.Default.SmallPlayerHotkeyString);
            if (Properties.Settings.Default.NowPlayingInfoHotkeyEnabled)
                RegisterConfiguredHotKey(NowPlayingInfoHotkeyId, Properties.Settings.Default.NowPlayingInfoHotkeyString);
            if (Properties.Settings.Default.PanicHotkeyEnabled)
                RegisterConfiguredHotKey(PanicHotkeyId, Properties.Settings.Default.PanicHotkeyString);
            hotkeysInitialized = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (hotkeysInitialized && !apiOnlyMode && !formIsClosing)
                SetupKeyHooks();
        }

        private void RegisterConfiguredHotKey(int id, string shortcut)
        {
            if (TryParseHotKey(shortcut, out uint modifiers, out uint key))
                RegisterHotKey(Handle, id, modifiers, key);
        }

        private void UnregisterHotKeys()
        {
            UnregisterHotKey(Handle, NextClipHotkeyId);
            UnregisterHotKey(Handle, NextCardHotkeyId);
            UnregisterHotKey(Handle, ToggleLockHotkeyId);
            UnregisterHotKey(Handle, PauseHotkeyId);
            UnregisterHotKey(Handle, RewindHotkeyId);
            UnregisterHotKey(Handle, FastForwardHotkeyId);
            UnregisterHotKey(Handle, RestartClipHotkeyId);
            UnregisterHotKey(Handle, LargePlayerHotkeyId);
            UnregisterHotKey(Handle, SmallPlayerHotkeyId);
            UnregisterHotKey(Handle, NowPlayingInfoHotkeyId);
            UnregisterHotKey(Handle, PanicHotkeyId);
        }

        internal static bool TryParseHotKey(string shortcut, out uint modifiers, out uint key)
        {
            modifiers = ModNoRepeat;
            key = 0;
            try
            {
                static bool IsWindowsModifier(string part) =>
                    part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("Windows", StringComparison.OrdinalIgnoreCase);

                string[] parts = shortcut.Split('+',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);
                bool hasWindowsModifier = parts.Any(IsWindowsModifier);
                string keysText = string.Join('+',
                    parts.Where(part => !IsWindowsModifier(part)));
                if (new KeysConverter().ConvertFromInvariantString(keysText) is not Keys keys)
                    return false;

                if (keys.HasFlag(Keys.Alt)) modifiers |= ModAlt;
                if (keys.HasFlag(Keys.Control)) modifiers |= ModControl;
                if (keys.HasFlag(Keys.Shift)) modifiers |= ModShift;
                if (hasWindowsModifier) modifiers |= ModWin;
                key = (uint)(keys & Keys.KeyCode);
                return key != 0;
            }
            catch (Exception exception) when (
                exception is NotSupportedException or FormatException)
            {
                return false;
            }
        }

        protected override void WndProc(ref System.Windows.Forms.Message message)
        {
            if (message.Msg == Program.RestoreInstanceMessage)
            {
                if (!apiOnlyMode)
                {
                    WindowState = FormWindowState.Normal;
                    ShowInTaskbar = true;
                    Show();
                    notifyIcon1.Visible = false;
                    ShowWindowAsync(Handle, 9);
                    Activate();
                    BringToFront();
                    SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0,
                        0x0001 | 0x0002 | 0x0010);
                    SetWindowPos(Handle, new IntPtr(-2), 0, 0, 0, 0,
                        0x0001 | 0x0002 | 0x0010);
                    SetForegroundWindow(Handle);
                }
                message.Result = new IntPtr(1);
                return;
            }
            if (message.Msg == WheelResizeMessage)
            {
                int steps = unchecked((int)message.WParam.ToInt64());
                int mode = message.LParam.ToInt32();
                if (steps != 0 && mode is 1 or 2 &&
                    Properties.Settings.Default.EnablePlayerWheelResize)
                {
                    string animationPath = GetCurrentAnimationPath();
                    int current = PlayerSizeForAnimation(
                        animationPath, mode, mode == 1 ? 100 : 30);
                    int percent = Math.Clamp(
                        current + steps * 2, mode == 1 ? 60 : 10, 200);
                    int result = SetVghdPlayerSizePercent(mode, percent);
                    if (result >= 0)
                    {
                        RememberManualPlayerSize(
                            animationPath, mode, percent);
                        LockStateOverlay.ShowTextForProcess(
                            vghd_procID, $"{percent}%");
                    }
                    else
                    {
                        SetPlaybackStatus(
                            $"Player size update failed (0x{result:X8}).");
                    }
                }
                return;
            }
            if (message.Msg == VolumeWheelMessage)
            {
                int steps = unchecked((int)message.WParam.ToInt64());
                int mode = message.LParam.ToInt32();
                if (steps != 0 && mode is 1 or 2)
                    ChangeIStripperPlayerVolume(mode, steps * 5);
                return;
            }
            if (message.Msg == WmHotkey)
            {
                switch (message.WParam.ToInt32())
                {
                    case NextClipHotkeyId:
                        actNextClip();
                        return;
                    case NextCardHotkeyId:
                        actNextCard();
                        return;
                    case ToggleLockHotkeyId:
                        actToggleLock();
                        return;
                    case PauseHotkeyId:
                        PostPlayerHotkey(actPause);
                        return;
                    case RewindHotkeyId:
                        PostPlayerHotkey(actRewind);
                        return;
                    case FastForwardHotkeyId:
                        PostPlayerHotkey(actFastForward);
                        return;
                    case RestartClipHotkeyId:
                        PostPlayerHotkey(actRestartClip);
                        return;
                    case LargePlayerHotkeyId:
                        PostPlayerHotkey(() => actSetPlayerLarge(true));
                        return;
                    case SmallPlayerHotkeyId:
                        PostPlayerHotkey(() => actSetPlayerLarge(false));
                        return;
                    case NowPlayingInfoHotkeyId:
                        actNowPlayingInfo();
                        return;
                    case PanicHotkeyId:
                        actPanic();
                        return;
                }
            }
            try
            {
                base.WndProc(ref message);
            }
            catch (ExternalException exception) when (
                message.Msg == WmShowWindow &&
                exception.ErrorCode == unchecked((int)0x80004005))
            {
                Invalidate(true);
            }
        }

        private void PostPlayerHotkey(Action action)
        {
            if (!formIsClosing && IsHandleCreated && !IsDisposed)
                BeginInvoke(action);
        }

        System.Threading.Timer? timerhook;
        private void SetupRegHooks()
        {
            timerhook = new System.Threading.Timer(
                waitForIStripper, null, 100, Timeout.Infinite);
        }

        private bool InjectVGHDProcess()
        {
            foreach (Process process in Process.GetProcessesByName("vghd"))
            {
                using (process)
                {
                    vghd_procID = process.Id;
                    ConfigurePlaybackHooks(process);
                    if (!playerLockBridgeLoaded)
                        continue;
                    //check that we havent played a new clip while we weren't hooked
                    if (!apiOnlyMode)
                    {
                        clickingNowPlaying = true;
                        GetNowPlaying();
                        clickingNowPlaying = false;
                    }
                    return true;
                }
            }
            return false;
        }

        private void waitForIStripper(object? state)
        {
            int nextCheckMilliseconds = 250;
            if (Interlocked.Exchange(ref vghdInjectionInProgress, 1) != 0)
            {
                return;
            }

            try
            {
                if (AttachedVghdIsRunning())
                {
                    nextCheckMilliseconds = 1_000;
                    return;
                }
                if (Volatile.Read(ref vghd_procID) != 0)
                    ResetVghdAttachment();
                if (InjectVGHDProcess())
                    nextCheckMilliseconds = 1_000;
            }
            catch (Exception exception)
            {
                ResetVghdAttachment();
                Debug.WriteLine(
                    "iStripper process attachment failed: " +
                    exception.Message);
            }
            finally
            {
                Interlocked.Exchange(ref vghdInjectionInProgress, 0);
                try
                {
                    timerhook?.Change(
                        nextCheckMilliseconds, Timeout.Infinite);
                }
                catch (ObjectDisposedException) { }
            }
        }

        private bool AttachedVghdIsRunning()
        {
            int processId = Volatile.Read(ref vghd_procID);
            if (processId == 0)
                return false;

            try
            {
                using Process process = Process.GetProcessById(processId);
                return !process.HasExited &&
                    process.ProcessName.Equals("vghd",
                        StringComparison.OrdinalIgnoreCase) &&
                    playerLockBridgeLoaded &&
                    playbackBridgeClient?.IsConnected == true;
            }
            catch
            {
                return false;
            }
        }

        private void ResetVghdAttachment()
        {
            lock (playbackApiLock)
            {
                playbackBridgeClient?.Dispose();
                playbackBridgeClient = null;
                vghd_procID = 0;
                playerLockBridgeLoaded = false;
                playbackBridgeLoaded = false;
                movieCaptureHookInstalled = false;
                playbackMovieRegistered = false;
                playbackFastDecodeEnabled = false;
                playbackSeekingSupported = true;
                playbackSeekReady = false;
                playbackDecoderKind = 0;
                ResetPlaybackReadinessDiagnostics("");
                playbackTimelineAnimationPath = "";
                playbackTimelineDurationMilliseconds = 0;
                playbackLastKnownElapsedMilliseconds = 0;
                playbackAlphaCheckpointBucket = -1;
                playbackNextMovieDiscoveryAt = DateTime.MinValue;
                playbackMovieCaptureFallbackAt = DateTime.MinValue;
                playbackCompletedAnimationPath = "";
                playbackRequestedAnimationPath = "";
                playbackRequestedAnimationAt = DateTime.MinValue;
                queuedAnimationPendingPath = "";
                queuedAnimationPendingConfirmed = false;
                queuedAnimationProtectedUntil = DateTime.MinValue;
                ClearQueuedCardSession();
                playbackNextClipRetryAt = DateTime.MinValue;
                playbackReplacementStableAt = DateTime.MinValue;
                Volatile.Write(ref playerMode, 2);
                Volatile.Write(ref playbackInputQuietUntilTicks, 0);
            }
            ResetPlayerSizeState();
            SetPlaybackStatus(string.Empty);
        }

        private void ConfigurePlaybackHooks(Process process)
        {
            playerLockBridgeLoaded = false;
            playbackBridgeLoaded = false;
            movieCaptureHookInstalled = false;
            playbackMovieRegistered = false;
            playbackSeekingSupported = true;
            playbackSeekReady = false;
            playbackDecoderKind = 0;
            ResetPlaybackReadinessDiagnostics("");
            playbackNextMovieDiscoveryAt = DateTime.MinValue;
            playbackFastDecodeEnabled = false;

            try
            {
                string localBridgePath = Path.Combine(AppContext.BaseDirectory,
                    "IStripperPlaybackBridge64.dll");
                if (!File.Exists(localBridgePath))
                {
                    SetPlaybackStatus("IStripperPlaybackBridge64.dll was not built or copied to the application folder.");
                    return;
                }

                playbackBridgeClient = PlaybackBridgeClient.Attach(
                    process.Id, localBridgePath, OnBridgeEvent);
                int bridgeVersion = playbackBridgeClient.Call(
                    "IStripperPlaybackBridgeVersion");
                if (bridgeVersion != PlaybackBridgeVersion)
                {
                    SetPlaybackStatus($"Unsupported playback bridge version {bridgeVersion}.");
                    return;
                }

                int hookResult = playbackBridgeClient.StartRegistryHook();
                if (hookResult < 0)
                    throw new COMException(
                        $"Registry hook setup failed (0x{hookResult:X8}).",
                        hookResult);

                int fullscreenHookResult = playbackBridgeClient.Call(
                    "IStripperStartFullscreenHook");
                if (fullscreenHookResult < 0)
                    throw new COMException(
                        $"Fullscreen hook setup failed (0x{fullscreenHookResult:X8}).",
                        fullscreenHookResult);

                int hdrProbeResult = playbackBridgeClient.Call(
                    "IStripperSetOpenGlPresentProbe",
                    Properties.Settings.Default.EnableIStripperRtxHdr ? 1UL : 0UL);
                if (hdrProbeResult < 0)
                    throw new COMException(
                        $"OpenGL HDR probe setup failed (0x{hdrProbeResult:X8}).",
                        hdrProbeResult);

                int resetResult = playbackBridgeClient.Call(
                    "IStripperResetPlaybackSession");
                if (resetResult < 0)
                {
                    throw new COMException(
                        $"Playback session reset failed (0x{resetResult:X8}).",
                        resetResult);
                }

                playerLockBridgeLoaded = true;
                if (!apiOnlyMode)
                {
                    int lockResult = SetVghdPlayerLocked();
                    if (lockResult < 0)
                    {
                        throw new COMException(
                            $"Player lock setup failed (0x{lockResult:X8}).",
                            lockResult);
                    }
                    int clickThroughResult = SetVghdPlayerClickThrough(
                        Properties.Settings.Default.ClickThroughLockedPlayer);
                    if (clickThroughResult < 0)
                    {
                        throw new COMException(
                            $"Player click-through setup failed (0x{clickThroughResult:X8}).",
                            clickThroughResult);
                    }
                    int playerModeResult = SetVghdPlayerMode(
                        Volatile.Read(ref playerMode));
                    if (playerModeResult < 0)
                    {
                        throw new COMException(
                            $"Player mode setup failed (0x{playerModeResult:X8}).",
                            playerModeResult);
                    }
                    int wheelResizeResult = SetVghdPlayerWheelResize(
                        Properties.Settings.Default.EnablePlayerWheelResize);
                    if (wheelResizeResult < 0)
                    {
                        throw new COMException(
                            $"Player wheel resize setup failed (0x{wheelResizeResult:X8}).",
                            wheelResizeResult);
                    }
                    CaptureNormalPlayerSizes();
                    QueueConfiguredPlayerSize();
                }
                if (!PlaybackControlEnabled)
                {
                    WarmDressingRoomCache();
                    return;
                }

                ConfigureMovieCaptureHook();
                ConfigurePlaybackFunctions();
                WarmDressingRoomCache();
            }
            catch (Exception exception)
            {
                playbackBridgeLoaded = false;
                SetPlaybackStatus("Playback controls could not attach: " + exception.Message);
            }
        }

        private void WarmDressingRoomCache()
        {
            PlaybackBridgeClient? bridge = playbackBridgeClient;
            if (bridge == null)
                return;
            _ = Task.Run(() =>
            {
                try
                {
                    int result = bridge.Call("IStripperWarmDressingRoomCache");
                    Debug.WriteLine($"Dressing Room cache warm-up result: 0x{result:X8}");
                }
                catch (Exception exception)
                {
                    Debug.WriteLine("Dressing Room cache warm-up failed: " + exception.Message);
                }
            });
        }

        private void ConfigureMovieCaptureHook()
        {
            try
            {
                movieCaptureHookInstalled = playbackBridgeClient!.Call(
                    "IStripperInstallMovieCaptureHook") >= 0;
            }
            catch (Exception exception)
            {
                movieCaptureHookInstalled = false;
                Debug.WriteLine(
                    "Movie capture hook was unavailable: " +
                    exception.Message);
            }
        }

        private void ConfigurePlaybackFunctions()
        {
            try
            {
                int compatibilityMask = playbackBridgeClient!.Call(
                    "IStripperGetCompatibilityMask");
                if (compatibilityMask != 0x3F)
                {
                    SetPlaybackStatus($"vghd.exe did not match the supported playback engine (mask 0x{compatibilityMask:X}).");
                    return;
                }

                int decoderThreads = Math.Clamp((Environment.ProcessorCount + 1) / 2, 1, 8);
                int fastDecodeResult = playbackBridgeClient.Call(
                    "IStripperEnableFastDecode",
                    unchecked((ulong)decoderThreads));
                playbackFastDecodeEnabled = fastDecodeResult >= 0;

                ulong cacheLimitParameter = AlphaCheckpointCacheBytes(
                    Properties.Settings.Default.AlphaCheckpointCacheSizeMB);
                int cacheLimitResult = playbackBridgeClient.Call(
                    "IStripperSetAlphaCheckpointCacheLimitBytes",
                    cacheLimitParameter);
                if (cacheLimitResult < 0)
                {
                    throw new COMException(
                        $"Alpha cache limit setup failed (0x{cacheLimitResult:X8}).",
                        cacheLimitResult);
                }

                playbackBridgeLoaded = true;
#if DEBUG
                int decoderOpenCount = 0;
                int decoderOptionResult = 0;
                if (playbackFastDecodeEnabled)
                {
                    decoderOpenCount = playbackBridgeClient.Call(
                        "IStripperGetFastDecodeOpenCount");
                    decoderOptionResult = playbackBridgeClient.Call(
                        "IStripperGetFastDecodeOptionResult");
                }

                string decoderStatus = !playbackFastDecodeEnabled
                    ? $"FFmpeg/queue acceleration was unavailable (0x{fastDecodeResult:X8}; " +
                      $"resolver 0x{CallPlaybackApi("IStripperGetFastDecodeResolverMask"):X}, " +
                      $"compatibility 0x{CallPlaybackApi("IStripperGetFastDecodeCompatibilityMask"):X}, " +
                      $"install stage 0x{CallPlaybackApi("IStripperGetFastDecodeInstallStage"):X}); " +
                      "fast-frame scan remains enabled."
                    : decoderOpenCount > 0 && decoderOptionResult < 0
                        ? $"FFmpeg rejected the {decoderThreads}-thread option on the last codec open (0x{decoderOptionResult:X8})."
                        : decoderOpenCount > 0
                            ? $"FFmpeg accepted the {decoderThreads}-thread option ({decoderOpenCount} codec opens); queue-aware catch-up is armed."
                            : $"FFmpeg {decoderThreads}-thread decode and queue-aware catch-up are armed for newly opened clips.";
                SetPlaybackStatus($"Playback controls ready. {decoderStatus}");
#else
                SetPlaybackStatus(string.Empty);
#endif
            }
            catch (Exception exception)
            {
                playbackBridgeLoaded = false;
                SetPlaybackStatus("Playback controls could not attach: " + exception.Message);
            }
        }

        private void SetPlaybackStatus(string status)
        {
            System.Diagnostics.Debug.WriteLine(status);
            if (apiOnlyMode)
                return;
            if (formIsClosing || IsDisposed)
            {
                return;
            }

            void UpdateStatus()
            {
                if (formIsClosing || IsDisposed)
                {
                    return;
                }

                UpdatePlaybackControlsEnabled();
            }

            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    BeginInvoke((Action)UpdateStatus);
                }
            }
            else
            {
                UpdateStatus();
            }
        }

        private void SetPlaybackBusy(bool busy)
        {
            playbackBusy = busy;
            if (apiOnlyMode || formIsClosing || IsDisposed)
            {
                return;
            }
            UpdatePlaybackControlsEnabled();
        }

        private void UpdatePlaybackControlsEnabled()
        {
            if (apiOnlyMode)
                return;

            bool custom = customPlayer != null;
            bool enabled = custom || PlaybackControlEnabled &&
                playbackControlsAvailableForAccount &&
                playbackBridgeLoaded && !playbackBusy && !formIsClosing;
            bool seekEnabled = custom || enabled && playbackMovieRegistered &&
                playbackSeekingSupported && playbackSeekReady;
            var state = (
                Rewind: seekEnabled,
                PlayPause: enabled,
                FastForward: seekEnabled,
                Speed: custom || enabled && playbackSeekingSupported &&
                    (playbackDecoderKind != 2 || playbackSeekReady),
                Position: seekEnabled);
            if (playbackControlEnabledState == state)
                return;
            playbackControlEnabledState = state;
            cmdRewind.Enabled = state.Rewind;
            cmdPlayPause.Enabled = state.PlayPause;
            cmdFastForward.Enabled = state.FastForward;
            cmbPlaybackSpeed.Enabled = state.Speed;
            trkPlaybackPosition.Enabled = state.Position;
        }

        private static bool HasPlatinumPlaybackEntitlement(string? userLevel)
        {
            return userLevel?.Trim().ToLowerInvariant() is
                "platinum" or "diamond" or "doublediamond" or
                "triplediamond" or "elite" or "master" or "qa";
        }

        private void RefreshPlaybackControlVisibility(string? userLevel = null)
        {
            if (userLevel == null)
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Totem\vghd", false);
                userLevel = key?.GetValue("PreviousUserLevel")?.ToString();
            }

            bool entitled = PlaybackControlEnabled &&
                HasPlatinumPlaybackEntitlement(userLevel);
            playbackControlsAvailableForAccount = entitled;
            bool visible = customPlayer != null || entitled;
            if (apiOnlyMode)
                return;
            cmdRewind.Visible = visible;
            cmdPlayPause.Visible = visible;
            cmdFastForward.Visible = visible;
            lblPlaybackSpeed.Visible = visible;
            cmbPlaybackSpeed.Visible = visible;
            lblPlaybackTime.Visible = visible;
            trkPlaybackPosition.Visible = visible;
            customAlphaThresholdLabel.Visible = customPlayer != null;
            customAlphaThresholdInput.Visible = customPlayer != null;
            customAlphaThresholdInput.Enabled = customPlayer != null;
            customEdgeChokeLabel.Visible = customPlayer != null;
            customEdgeChokeInput.Visible = customPlayer != null;
            customEdgeChokeInput.Enabled = customPlayer != null;
            UpdatePlaybackControlsEnabled();
        }

        private int SetVghdPlayerLocked(bool? locked = null)
        {
            if (!playerLockBridgeLoaded || vghd_procID == 0)
            {
                return unchecked((int)0x80070015);
            }

            return CallPlaybackBridgeApi("IStripperSetPlayerLocked",
                (locked ?? playerlocked) ? 1UL : 0UL);
        }

        private int CallPlaybackBridgeApi(string apiName,
            ulong? parameter = null)
        {
            lock (playbackApiLock)
            {
                return playbackBridgeClient?.Call(apiName, parameter) ??
                    unchecked((int)0x80070015);
            }
        }

        private int CallPlaybackApi(string apiName, ulong? parameter = null)
        {
            if (!playbackBridgeLoaded || vghd_procID == 0)
            {
                throw new InvalidOperationException("The iStripper playback bridge is not attached.");
            }

            return CallPlaybackBridgeApi(apiName, parameter);
        }

        private int RequirePlaybackResult(string apiName, ulong? parameter = null)
        {
            int result = CallPlaybackApi(apiName, parameter);
            if (result < 0)
            {
                throw new COMException($"{apiName} failed (0x{result:X8}).", result);
            }
            return result;
        }

        private Task<bool> EnsurePlaybackReadyAsync(
            CancellationToken cancellationToken, bool prepareFastDecode)
        {
            if (!playbackBridgeLoaded)
            {
                SetPlaybackStatus("Playback controls are not available for this iStripper process.");
                return Task.FromResult(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // vghd loads its FFmpeg DLLs lazily with the first animation. If
            // attachment happened earlier, retry now that a clip is available.
            if (prepareFastDecode && !playbackFastDecodeEnabled)
            {
                int decoderThreads = Math.Clamp((Environment.ProcessorCount + 1) / 2, 1, 8);
                int fastDecodeResult = CallPlaybackApi("IStripperEnableFastDecode",
                    unchecked((ulong)decoderThreads));
                playbackFastDecodeEnabled = fastDecodeResult >= 0;
            }

            if (playbackMovieRegistered)
            {
                return Task.FromResult(true);
            }

            playbackMovieRegistered =
                CallPlaybackApi("IStripperDiscoverMovie") >= 0;
            if (playbackMovieRegistered)
            {
                playbackDecoderKind =
                    CallPlaybackApi("IStripperGetDecoderKind");
                playbackSeekingSupported =
                    playbackDecoderKind is 1 or 2;
            }
            if (!playbackMovieRegistered)
            {
                SetPlaybackStatus("No active desktop video was found. Start a clip in iStripper and try again.");
            }
            return Task.FromResult(playbackMovieRegistered);
        }

        private async Task<bool> RunPlaybackOperationAsync(
            Func<CancellationToken, Task> operation,
            bool prepareFastDecode = true)
        {
            if (customPlayer != null)
            {
                await operation(playbackLifetime.Token);
                return true;
            }
            if (!PlaybackControlEnabled ||
                !playbackControlsAvailableForAccount)
            {
                return false;
            }
            if (!await playbackOperationLock.WaitAsync(0))
            {
                SetPlaybackStatus("Another playback operation is already running.");
                return false;
            }

            SetPlaybackBusy(true);
            bool completed = false;
            try
            {
                CancellationToken cancellationToken = playbackLifetime.Token;
                if (await Task.Run(() => EnsurePlaybackReadyAsync(
                        cancellationToken, prepareFastDecode)))
                {
                    await Task.Run(() => operation(cancellationToken),
                        cancellationToken);
                    completed = true;
                }
            }
            catch (OperationCanceledException) when (formIsClosing)
            {
            }
            catch (Exception exception)
            {
                SetPlaybackStatus("Playback control failed: " + exception.Message);
            }
            finally
            {
                SetPlaybackBusy(false);
                playbackOperationLock.Release();
            }
            return completed;
        }

        private void SetPlaybackRate(double playbackSpeed)
        {
            ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(playbackSpeed));
            RequirePlaybackResult("IStripperSetPlayRate", bits);
        }

        private async Task TogglePlaybackPauseAsync()
        {
            if (customPlayer != null)
            {
                customPlayer.TogglePause();
                SetPlaybackStatus(customPlayer.Paused ? "Paused." : "Playing.");
                return;
            }
            await RunPlaybackOperationAsync(_ =>
            {
                int state = RequirePlaybackResult("IStripperGetState");
                if (state == 3)
                {
                    RequirePlaybackResult("IStripperPause");
                    int elapsed = RequirePlaybackResult("IStripperGetElapsedMilliseconds");
                    SetPlaybackStatus($"Paused at {FormatPlaybackTime(elapsed)} ({requestedPlaybackSpeed:0.##}x).");
                }
                else if (state == 4)
                {
                    RequirePlaybackResult("IStripperResume");
                    int elapsed = RequirePlaybackResult("IStripperGetElapsedMilliseconds");
                    SetPlaybackStatus($"Playing from {FormatPlaybackTime(elapsed)} ({requestedPlaybackSpeed:0.##}x).");
                }
                else
                {
                    throw new InvalidOperationException("There is no video in a controllable play/pause state.");
                }
                return Task.CompletedTask;
            }, prepareFastDecode: false);
        }

        private async void cmdPlayPause_Click(object sender, EventArgs e) =>
            await TogglePlaybackPauseAsync();

        private async Task SeekPlaybackRelativeAsync(double fraction)
        {
            if (customPlayer != null)
            {
                customPlayer.SeekBy(customPlayer.DurationSeconds * fraction);
                return;
            }
            await RunPlaybackOperationAsync(token =>
                SeekRelativeAsync(fraction, token));
        }

        private async void cmdRewind_Click(object sender, EventArgs e) =>
            await SeekPlaybackRelativeAsync(-0.1);

        private async void cmdFastForward_Click(object sender, EventArgs e)
            => await SeekPlaybackRelativeAsync(0.1);

        private async void cmbPlaybackSpeed_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (suppressPlaybackSpeedSelection)
            {
                return;
            }
            if (cmbPlaybackSpeed.SelectedItem is not string selected ||
                !double.TryParse(selected.TrimEnd('x'), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out double speed))
            {
                return;
            }

            if (playbackDecoderKind == 2 && speed > 3.0)
            {
                suppressPlaybackSpeedSelection = true;
                cmbPlaybackSpeed.SelectedItem =
                    $"{requestedPlaybackSpeed:0.##}x";
                suppressPlaybackSpeedSelection = false;
                return;
            }

            requestedPlaybackSpeed = speed;
            if (customPlayer != null)
            {
                customPlayer.SetRate(speed);
                SetPlaybackStatus($"Playback speed set to {speed:0.##}x.");
                return;
            }
            if (!playbackBridgeLoaded)
            {
                return;
            }

            await RunPlaybackOperationAsync(_ =>
            {
                SetPlaybackRate(speed);
                SetPlaybackStatus($"Playback speed set to {speed:0.##}x.");
                return Task.CompletedTask;
            });
        }

        private async void playbackTimelineTimer_Tick(object? sender, EventArgs e)
        {
            if (customPlayer != null)
            {
                SuspendIStripperForCustomPlayback();
                int duration = (int)Math.Min(int.MaxValue,
                    customPlayer.DurationSeconds * 1000);
                int elapsed = Math.Clamp((int)(customPlayer.CurrentSeconds * 1000), 0, Math.Max(0, duration));
                playbackTimelineDurationMilliseconds = duration;
                playbackLastKnownElapsedMilliseconds = elapsed;
                int maximum = Math.Max(1, duration);
                if (trkPlaybackPosition.Maximum != maximum)
                    trkPlaybackPosition.Maximum = maximum;
                int value = Math.Min(maximum, elapsed);
                if (!playbackTimelineDragging &&
                    trkPlaybackPosition.Value != value)
                    trkPlaybackPosition.Value = value;
                UpdatePlaybackTime(elapsed, duration);
                trkPlaybackPosition.Enabled = true;
                return;
            }
            if (formIsClosing || panicActive || playbackTimelinePolling ||
                playbackBusy ||
                !Properties.Settings.Default.EnablePlaybackControl ||
                !playbackControlsAvailableForAccount || !playbackBridgeLoaded)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if ((GetAsyncKeyState(VirtualKeyLeftButton) & 0x8000) != 0)
            {
                Volatile.Write(ref playbackInputQuietUntilTicks,
                    now.AddSeconds(1).Ticks);
                return;
            }
            if (now.Ticks < Volatile.Read(ref playbackInputQuietUntilTicks))
                return;

            playbackTimelinePolling = true;
            try
            {
                string animationPath = GetCurrentAnimationPath();
                if (string.IsNullOrEmpty(animationPath) &&
                    PlaybackReplacementExpired(playbackRequestedAnimationPath,
                        playbackRequestedAnimationAt, now))
                {
                    playbackRequestedAnimationPath = "";
                    playbackRequestedAnimationAt = DateTime.MinValue;
                    queuedAnimationPendingPath = "";
                    queuedAnimationPendingConfirmed = false;
                    queuedAnimationProtectedUntil = DateTime.MinValue;
                    if (activeQueuedCard == null &&
                        activeManualQueueEntry != null)
                    {
                        manualPlayQueue.Add(activeManualQueueEntry);
                        activeManualQueueEntry = null;
                        SavePreviousQueue();
                    }
                    GetNextClip();
                    return;
                }
                if (!string.Equals(animationPath, playbackTimelineAnimationPath,
                        StringComparison.Ordinal))
                {
                    ShowNowPlaying(animationPath, doWallpaper: true);
                    ArmMovieCapture();
                    string previousAnimationPath = playbackTimelineAnimationPath;
                    bool previousAnimationReachedEnd = PlaybackReachedEnd(
                        playbackLastKnownElapsedMilliseconds,
                        playbackTimelineDurationMilliseconds);
                    playbackTimelineAnimationPath = animationPath;
                    playbackLastProgressAt = DateTime.UtcNow;
                    playbackAlphaCheckpointBucket = -1;
                    try
                    {
                        CallPlaybackApi("IStripperClearAlphaCheckpoints");
                        CallPlaybackApi("IStripperSetAlphaCheckpointCacheKey",
                            Properties.Settings.Default.EnableAlphaCheckpointCache
                                ? AlphaCheckpointClipKey(animationPath)
                                : 0);
                    }
                    catch { }
                    playbackMovieRegistered = false;
                    playbackSeekingSupported = true;
                    playbackSeekReady = false;
                    playbackDecoderKind = 0;
                    ResetPlaybackReadinessDiagnostics(animationPath);
                    UpdatePlaybackControlsEnabled();
                    playbackNextMovieDiscoveryAt = DateTime.MinValue;
                    playbackSpeedReapplyUntil = DateTime.UtcNow.AddSeconds(30);
                    if (!string.IsNullOrEmpty(animationPath) &&
                        !playbackTimelineDragging)
                    {
                        playbackLastKnownElapsedMilliseconds = 0;
                        trkPlaybackPosition.Maximum = 1;
                        trkPlaybackPosition.Value = 0;
                        playbackTimelineDurationMilliseconds = 0;
                        UpdatePlaybackTime(0, 0);
                    }
                    if (string.IsNullOrEmpty(animationPath) &&
                        !string.IsNullOrEmpty(previousAnimationPath))
                    {
                        if (string.IsNullOrEmpty(playbackRequestedAnimationPath) &&
                            string.IsNullOrEmpty(playbackCompletedAnimationPath) &&
                            previousAnimationReachedEnd)
                        {
                            playbackCompletedAnimationPath = previousAnimationPath;
                            playbackNextClipRetryAt =
                                DateTime.UtcNow.AddSeconds(1);
                        }
                        playbackReplacementStableAt = DateTime.MinValue;
                    }
                    else if (!string.IsNullOrEmpty(animationPath) &&
                        !string.IsNullOrEmpty(playbackCompletedAnimationPath))
                    {
                        playbackReplacementStableAt =
                            DateTime.UtcNow.AddSeconds(2);
                    }
                    if (!string.IsNullOrEmpty(animationPath) &&
                        string.Equals(animationPath,
                            playbackRequestedAnimationPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        playbackRequestedAnimationPath = "";
                        playbackRequestedAnimationAt = DateTime.MinValue;
                        playbackCompletedAnimationPath = "";
                        playbackReplacementStableAt = DateTime.MinValue;
                    }
                }

                if (!string.IsNullOrEmpty(animationPath) &&
                    !string.IsNullOrEmpty(playbackCompletedAnimationPath) &&
                    playbackReplacementStableAt != DateTime.MinValue &&
                    DateTime.UtcNow >= playbackReplacementStableAt)
                {
                    playbackCompletedAnimationPath = "";
                    playbackReplacementStableAt = DateTime.MinValue;
                }

                if (string.IsNullOrEmpty(animationPath) &&
                    !string.IsNullOrEmpty(playbackCompletedAnimationPath) &&
                    DateTime.UtcNow >= playbackNextClipRetryAt)
                {
                    playbackNextClipRetryAt = DateTime.UtcNow.AddSeconds(1);
                    GetNextClip(null, playbackCompletedAnimationPath);
                    return;
                }

                if (string.IsNullOrEmpty(animationPath))
                {
                    SetTimerInterval(playbackTimelineTimer,
                        PlaybackIdleIntervalMilliseconds);
                    trkPlaybackPosition.Enabled = false;
                    return;
                }

                SetTimerInterval(playbackTimelineTimer,
                    playbackSeekReady
                        ? PlaybackTimelineIntervalMilliseconds
                        : PlaybackTransitionIntervalMilliseconds);

                if (!playbackMovieRegistered &&
                    DateTime.UtcNow >= playbackNextMovieDiscoveryAt)
                {
                    int discoveredDecoderKind = 0;
                    bool allowFallbackDiscovery =
                        !movieCaptureHookInstalled ||
                        playbackMovieCaptureFallbackAt ==
                            DateTime.MinValue ||
                        DateTime.UtcNow >=
                            playbackMovieCaptureFallbackAt;
                    if (allowFallbackDiscovery)
                        DisableMovieCapture();
                    playbackMovieRegistered = await Task.Run(() =>
                    {
                        bool registered = movieCaptureHookInstalled &&
                            CallPlaybackApi(
                                "IStripperConsumeCapturedMovie") >= 0;
                        if (!registered && allowFallbackDiscovery)
                        {
                            registered =
                                CallPlaybackApi(
                                    "IStripperDiscoverMovie") >= 0;
                        }
                        if (registered)
                        {
                            discoveredDecoderKind =
                                CallPlaybackApi("IStripperGetDecoderKind");
                        }
                        return registered;
                    });
                    if (playbackMovieRegistered)
                    {
                        DisableMovieCapture();
                        SetTimerInterval(playbackTimelineTimer,
                            PlaybackTransitionIntervalMilliseconds);
                        playbackDecoderKind = discoveredDecoderKind;
                        playbackSeekingSupported =
                            playbackDecoderKind is 1 or 2;
                        RecordPlaybackMovieRegistration();
                        ApplyIStripperPlayerVolume(
                            Volatile.Read(ref playerMode));
                        SetPlaybackBusy(playbackBusy);
                    }
                    playbackNextMovieDiscoveryAt = playbackMovieRegistered
                        ? DateTime.MinValue
                        : DateTime.UtcNow.AddMilliseconds(
                            PlaybackMovieDiscoveryRetryMilliseconds);
                }
                if (!playbackMovieRegistered)
                {
                    trkPlaybackPosition.Enabled = false;
                    return;
                }

                now = DateTime.UtcNow;
                int cachedTotal = playbackTimelineDurationMilliseconds;
                int cachedDecoderKind = playbackDecoderKind;
                bool cachedSeekReady = playbackSeekReady;
                int previousElapsed = playbackLastKnownElapsedMilliseconds;
                DateTime previousProgressAt = playbackLastProgressAt;
                PlaybackPollSnapshot snapshot = await Task.Run(() =>
                {
                    int elapsed = RequirePlaybackResult(
                        "IStripperGetElapsedMilliseconds");
                    int total = cachedTotal > 0
                        ? cachedTotal
                        : RequirePlaybackResult(
                            "IStripperGetTotalMilliseconds");
                    int decoderKind = cachedDecoderKind != 0
                        ? cachedDecoderKind
                        : CallPlaybackApi("IStripperGetDecoderKind");
                    int state = decoderKind == 2 &&
                        elapsed <= previousElapsed &&
                        PlaybackReachedEnd(elapsed, total) &&
                        now - previousProgressAt >= TimeSpan.FromSeconds(2)
                            ? CallPlaybackApi("IStripperGetState")
                            : 0;
                    int seekReady = !cachedSeekReady &&
                        decoderKind is 1 or 2
                            ? CallPlaybackApi("IStripperIsSeekReady")
                            : cachedSeekReady ? 1 : 0;
                    int readinessMask = !cachedSeekReady &&
                        decoderKind == 1 &&
                        elapsed < PlaybackForcedReadyMilliseconds
                            ? CallPlaybackApi(
                                "IStripperGetSeekReadinessMask")
                            : -1;
                    return new PlaybackPollSnapshot(elapsed, total,
                        decoderKind, state, seekReady, readinessMask);
                });
                int elapsed = snapshot.Elapsed;
                int total = snapshot.Total;
                if (formIsClosing || IsDisposed ||
                    !string.Equals(animationPath, GetCurrentAnimationPath(),
                        StringComparison.Ordinal) || playbackBusy)
                {
                    return;
                }

                if (playbackDecoderKind == 0 &&
                    snapshot.DecoderKind is 1 or 2)
                {
                    playbackDecoderKind = snapshot.DecoderKind;
                    playbackSeekingSupported = true;
                }

                if (elapsed > playbackLastKnownElapsedMilliseconds)
                {
                    playbackLastProgressAt = now;
                }
                if (Properties.Settings.Default.EnablePlayQueue &&
                    string.IsNullOrEmpty(playbackCompletedAnimationPath) &&
                    PlaybackReachedEnd(elapsed, total))
                {
                    playbackCompletedAnimationPath = animationPath;
                    playbackNextClipRetryAt = now.AddSeconds(1);
                    GetNextClip(null, animationPath);
                    return;
                }
                else if (playbackDecoderKind == 2 &&
                    LegacyPlaybackStalledNearEnd(elapsed, total,
                        playbackLastProgressAt, now,
                        snapshot.State))
                {
                    playbackCompletedAnimationPath = animationPath;
                    playbackNextClipRetryAt = now.AddSeconds(1);
                    GetNextClip(null, animationPath);
                    return;
                }

                if (snapshot.SeekReadinessMask >= 0 &&
                    snapshot.SeekReadinessMask != playbackSeekReadinessMask)
                {
                    playbackSeekReadinessMask = snapshot.SeekReadinessMask;
                    Debug.WriteLine(
                        $"Playback seek readiness mask 0x{snapshot.SeekReadinessMask:X} " +
                        $"after {PlaybackReadinessElapsedMilliseconds()} ms.");
                }

                int seekReadyResult = snapshot.SeekReady;
                bool wasSeekReady = playbackSeekReady;
                if (!playbackSeekReady && playbackDecoderKind is 1 or 2 &&
                    seekReadyResult == 1 &&
                    (playbackDecoderKind != 2 || elapsed >= 3_500))
                {
                    if (playbackDecoderKind == 1)
                    {
                        int checkpointResult = await Task.Run(() =>
                            CallPlaybackApi(
                                "IStripperCaptureAlphaCheckpoint"));
                        if (checkpointResult >= 0)
                        {
                            playbackAlphaCheckpointBucket = elapsed / 5_000;
                            playbackSeekReady = true;
                        }
                    }
                    else
                    {
                        playbackSeekReady = true;
                    }
                }
                if (!playbackSeekReady && playbackDecoderKind is 1 or 2 &&
                    elapsed >= PlaybackForcedReadyMilliseconds)
                {
                    playbackSeekReady = true;
                }
                else if (!playbackSeekReady && playbackDecoderKind == 1)
                {
                    trkPlaybackPosition.AccessibleDescription =
                        $"Seek readiness 0x{snapshot.SeekReadinessMask:X}";
                }
                if (!wasSeekReady && playbackSeekReady)
                    RecordPlaybackSeekReady();

                int reapplyAfter = playbackDecoderKind == 2 ? 3_500 : 500;
                if (Math.Abs(requestedPlaybackSpeed - 1.0) > 0.001 &&
                    now < playbackSpeedReapplyUntil &&
                    elapsed >= reapplyAfter &&
                    (playbackDecoderKind != 2 || playbackSeekReady))
                {
                    await Task.Run(() => SetPlaybackRate(requestedPlaybackSpeed));
                    playbackSpeedReapplyUntil = DateTime.MinValue;
                }
                else if (Math.Abs(requestedPlaybackSpeed - 1.0) <= 0.001)
                {
                    playbackSpeedReapplyUntil = DateTime.MinValue;
                }

                playbackLastKnownElapsedMilliseconds = elapsed;
                int checkpointBucket = elapsed / 5_000;
                if (playbackDecoderKind == 1 &&
                    playbackSeekReady &&
                    checkpointBucket != playbackAlphaCheckpointBucket)
                {
                    int checkpointResult = await Task.Run(() =>
                        CallPlaybackApi("IStripperCaptureAlphaCheckpoint"));
                    if (checkpointResult >= 0)
                        playbackAlphaCheckpointBucket = checkpointBucket;
                }
                playbackTimelineDurationMilliseconds = Math.Max(0, total);
                if (!playbackTimelineDragging)
                {
                    int maximum = Math.Max(1, playbackTimelineDurationMilliseconds);
                    if (trkPlaybackPosition.Maximum != maximum)
                    {
                        trkPlaybackPosition.Maximum = maximum;
                        trkPlaybackPosition.SmallChange = Math.Min(1_000, maximum);
                        trkPlaybackPosition.LargeChange = Math.Min(10_000, maximum);
                    }
                    int value = Math.Clamp(elapsed, 0, maximum);
                    if (trkPlaybackPosition.Value != value)
                        trkPlaybackPosition.Value = value;
                    UpdatePlaybackTime(elapsed, playbackTimelineDurationMilliseconds);
                }
                UpdatePlaybackControlsEnabled();
                SetTimerInterval(playbackTimelineTimer,
                    playbackSeekReady
                        ? PlaybackTimelineIntervalMilliseconds
                        : PlaybackTransitionIntervalMilliseconds);
            }
            catch
            {
                // Clip transitions briefly invalidate the movie pointer. The next
                // timer tick retries without replacing the useful operation status.
            }
            finally
            {
                playbackTimelinePolling = false;
            }
        }

        private void UpdatePlaybackTime(int elapsedMilliseconds, int totalMilliseconds)
        {
            string text =
                $"{FormatPlaybackTime(elapsedMilliseconds)} / {FormatPlaybackTime(totalMilliseconds)}";
            lblPlaybackTime.SetDisplayText(text);
        }

        private void ResetPlaybackReadinessDiagnostics(string animationPath)
        {
            Volatile.Write(ref playbackReadinessStartedTicks,
                string.IsNullOrEmpty(animationPath)
                    ? 0 : DateTime.UtcNow.Ticks);
            Volatile.Write(ref playbackMovieRegistrationMilliseconds, -1);
            Volatile.Write(ref playbackSeekReadinessMilliseconds, -1);
            playbackSeekReadinessMask = 0;
        }

        private int PlaybackReadinessElapsedMilliseconds()
        {
            long startedTicks = Volatile.Read(
                ref playbackReadinessStartedTicks);
            if (startedTicks <= 0)
                return 0;
            long elapsedTicks = Math.Max(0,
                DateTime.UtcNow.Ticks - startedTicks);
            return (int)Math.Min(int.MaxValue,
                elapsedTicks / TimeSpan.TicksPerMillisecond);
        }

        private void RecordPlaybackMovieRegistration()
        {
            if (Volatile.Read(ref playbackReadinessStartedTicks) <= 0)
            {
                Interlocked.CompareExchange(
                    ref playbackReadinessStartedTicks,
                    DateTime.UtcNow.Ticks, 0);
            }
            int elapsed = PlaybackReadinessElapsedMilliseconds();
            if (Interlocked.CompareExchange(
                    ref playbackMovieRegistrationMilliseconds,
                    elapsed, -1) == -1)
            {
                Debug.WriteLine(
                    $"Playback movie registered after {elapsed} ms " +
                    $"(decoder {playbackDecoderKind}).");
            }
        }

        private void RecordPlaybackSeekReady()
        {
            int elapsed = PlaybackReadinessElapsedMilliseconds();
            if (Interlocked.CompareExchange(
                    ref playbackSeekReadinessMilliseconds,
                    elapsed, -1) == -1)
            {
                Debug.WriteLine(
                    $"Playback seeking ready after {elapsed} ms " +
                    $"(decoder {playbackDecoderKind}, " +
                    $"mask 0x{playbackSeekReadinessMask:X}).");
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        private void QueueFastDecodeRetry()
        {
            if (!playbackBridgeLoaded || playbackFastDecodeEnabled ||
                Interlocked.Exchange(ref playbackFastDecodeRetryPending, 1) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    int decoderThreads = Math.Clamp(
                        (Environment.ProcessorCount + 1) / 2, 1, 8);
                    for (int attempt = 0; attempt < 10 &&
                        !playbackFastDecodeEnabled; attempt++)
                    {
                        await Task.Delay(250).ConfigureAwait(false);
                        if (formIsClosing || playbackBridgeClient == null)
                            return;
                        playbackFastDecodeEnabled = playbackBridgeClient.Call(
                            "IStripperEnableFastDecode",
                            unchecked((ulong)decoderThreads)) >= 0;
                    }
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(
                        "FFmpeg/queue acceleration retry failed: " +
                        exception.Message);
                }
                finally
                {
                    Interlocked.Exchange(
                        ref playbackFastDecodeRetryPending, 0);
                }
            });
        }

        private static void SetTimerInterval(System.Windows.Forms.Timer timer,
            int interval)
        {
            if (timer.Interval != interval)
                timer.Interval = interval;
        }

        private void trkPlaybackPosition_MouseDown(object sender, MouseEventArgs e)
        {
            playbackTimelineDragging = true;
            if (e.Button == MouseButtons.Left)
            {
                UpdatePlaybackTime(trkPlaybackPosition.Value,
                    playbackTimelineDurationMilliseconds);
            }
        }

        private void trkPlaybackPosition_Scroll(object sender, EventArgs e)
        {
            UpdatePlaybackTime(trkPlaybackPosition.Value,
                playbackTimelineDurationMilliseconds);
        }

        private async void trkPlaybackPosition_MouseUp(object sender, MouseEventArgs e)
        {
            playbackTimelineDragging = false;
            int target = trkPlaybackPosition.Value;
            await RunPlaybackOperationAsync(token => SeekAbsoluteAsync(target, token));
        }

        private void trkPlaybackPosition_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right ||
                e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown ||
                e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
            {
                playbackTimelineDragging = true;
            }
        }

        private async void trkPlaybackPosition_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Left && e.KeyCode != Keys.Right &&
                e.KeyCode != Keys.PageUp && e.KeyCode != Keys.PageDown &&
                e.KeyCode != Keys.Home && e.KeyCode != Keys.End)
            {
                return;
            }

            playbackTimelineDragging = false;
            int target = trkPlaybackPosition.Value;
            await RunPlaybackOperationAsync(token => SeekAbsoluteAsync(target, token));
        }

        private async Task SeekRelativeAsync(double clipFraction,
            CancellationToken cancellationToken)
        {
            if (customPlayer != null)
            {
                customPlayer.SeekBy(customPlayer.DurationSeconds * clipFraction);
                await Task.CompletedTask;
                return;
            }
            int current = RequirePlaybackResult("IStripperGetElapsedMilliseconds");
            int total = RequirePlaybackResult("IStripperGetTotalMilliseconds");
            await SeekAbsoluteAsync(current +
                (int)Math.Round(total * clipFraction), cancellationToken);
        }

        private async Task SeekAbsoluteAsync(int requestedTarget,
            CancellationToken cancellationToken)
        {
            if (customPlayer != null)
            {
                customPlayer.SeekTo(requestedTarget / 1000d);
                await Task.CompletedTask;
                return;
            }
            if (!playbackSeekingSupported)
            {
                throw new NotSupportedException(
                    "This clip's decoder does not support seeking or speed changes.");
            }

            int state = RequirePlaybackResult("IStripperGetState");
            if (state != 3 && state != 4)
            {
                throw new InvalidOperationException("There is no active desktop video to scan.");
            }

            bool wasPlaying = state == 3;
            double speedToRestore = requestedPlaybackSpeed;
            int current = RequirePlaybackResult("IStripperGetElapsedMilliseconds");
            if (!playbackSeekReady)
            {
                throw new InvalidOperationException(
                    "The clip's decoders are not ready to seek yet.");
            }
            int total = RequirePlaybackResult("IStripperGetTotalMilliseconds");
            if (SeekTargetReachesEnd(requestedTarget, total))
            {
                if (apiOnlyMode)
                    PlayNextApiOnlyClip();
                else
                    GetNextClip();
                return;
            }
            int target = Math.Max(0, requestedTarget);
            if (total > 0)
            {
                target = Math.Min(target, Math.Max(0, total - 1_000));
            }

            string animationAtStart = GetCurrentAnimationPath();
#if DEBUG
            int skippedScaleCountBefore = playbackFastDecodeEnabled
                ? Math.Max(0, CallPlaybackApi("IStripperGetFastDecodeSkippedScaleCount"))
                : 0;
            int alphaResetCountBefore = playbackFastDecodeEnabled
                ? Math.Max(0, CallPlaybackApi("IStripperGetAlphaResetCount"))
                : 0;
            int codecFlushCountBefore = playbackFastDecodeEnabled
                ? Math.Max(0, CallPlaybackApi("IStripperGetCodecFlushCount"))
                : 0;
            int keyframeSeekCountBefore = playbackFastDecodeEnabled
                ? Math.Max(0, CallPlaybackApi("IStripperGetKeyframeSeekCount"))
                : 0;
            Stopwatch seekStopwatch = Stopwatch.StartNew();
#endif
            int finalPosition = current;
            try
            {
                if (wasPlaying)
                {
                    RequirePlaybackResult("IStripperPause");
                }

                if (target < current)
                {
                    int? decodedPosition = await TryDecodeTargetFrameAsync(current, target,
                        animationAtStart, cancellationToken);
                    if (decodedPosition.HasValue)
                    {
                        finalPosition = decodedPosition.Value;
                    }
                    else if (apiOnlyMode)
                    {
                        finalPosition = await ReloadApiOnlyAnimationAsync(
                            animationAtStart, current, target,
                            cancellationToken);
                    }
                    else
                    {
                        throw new NotSupportedException(
                            "This clip's decoder cannot rewind without changing the active animation.");
                    }
                }
                else if (target > current + 75)
                {
                    finalPosition = await ScanForwardToAsync(current, target,
                        animationAtStart, cancellationToken);
                }
                else
                {
                    finalPosition = current;
                }
            }
            finally
            {
                // Do not pause or resume a replacement clip if the old one ended
                // while its seek was still completing.
                if (string.Equals(animationAtStart, GetCurrentAnimationPath(),
                        StringComparison.Ordinal))
                {
                    try { RequirePlaybackResult("IStripperPause"); } catch { }
                    try { SetPlaybackRate(speedToRestore); } catch { }
                    if (wasPlaying)
                    {
                        try { RequirePlaybackResult("IStripperResume"); } catch { }
                    }
                }
            }

            playbackLastKnownElapsedMilliseconds = finalPosition;
#if DEBUG
            string action = target < current ? "Rewound" :
                target > current ? "Fast-forwarded" : "Stayed";
            string stateText = wasPlaying ? "playing" : "paused";
            int skippedScaleCount = playbackFastDecodeEnabled
                ? Math.Max(0, CallPlaybackApi("IStripperGetFastDecodeSkippedScaleCount"))
                : skippedScaleCountBefore;
            int skippedDuringSeek = Math.Max(0, skippedScaleCount - skippedScaleCountBefore);
            int decoderCatchupDistance = playbackFastDecodeEnabled
                ? Math.Max(0, CallPlaybackApi("IStripperGetFastDecodeCatchupDistance"))
                : 0;
            int droppedVideoFrames = playbackFastDecodeEnabled
                ? Math.Max(0, CallPlaybackApi("IStripperGetFastDecodeDroppedFrameCount"))
                : 0;
            int alphaResetCount = playbackFastDecodeEnabled
                ? Math.Max(0, CallPlaybackApi("IStripperGetAlphaResetCount"))
                : alphaResetCountBefore;
            int alphaFrameBeforeReset = alphaResetCount > alphaResetCountBefore
                ? Math.Max(0, CallPlaybackApi("IStripperGetLastAlphaFrameBeforeReset"))
                : 0;
            int codecFlushCount = playbackFastDecodeEnabled
                ? Math.Max(0, CallPlaybackApi("IStripperGetCodecFlushCount"))
                : codecFlushCountBefore;
            int keyframeSeekCount = playbackFastDecodeEnabled
                ? Math.Max(0, CallPlaybackApi("IStripperGetKeyframeSeekCount"))
                : keyframeSeekCountBefore;
            int keyframeFrame = keyframeSeekCount > keyframeSeekCountBefore
                ? Math.Max(0, CallPlaybackApi("IStripperGetLastKeyframeSeekFrame"))
                : 0;
            string acceleration = decoderCatchupDistance > 0
                ? $" Decoder catch-up: {decoderCatchupDistance} frames in {seekStopwatch.Elapsed.TotalSeconds:0.00}s; " +
                  $"{skippedDuringSeek} colour conversions skipped and {droppedVideoFrames} stale queued frames dropped."
                : "";
            string alphaRebuild = alphaResetCount > alphaResetCountBefore
                ? $" Alpha state reset from frame {alphaFrameBeforeReset} and replayed from frame 0."
                : "";
            string codecFlush = codecFlushCount > codecFlushCountBefore
                ? " VP9 reference and delayed-frame state flushed after demux rewind."
                : "";
            string keyframeSeek = keyframeSeekCount > keyframeSeekCountBefore
                ? $" Colour decode started at indexed VP9 keyframe {keyframeFrame}."
                : "";
            SetPlaybackStatus(
                $"{action} to about {FormatPlaybackTime(finalPosition)}; {stateText} at {speedToRestore:0.##}x.{acceleration}{alphaRebuild}{codecFlush}{keyframeSeek}");
#endif
        }

        private async Task<int> ReloadApiOnlyAnimationAsync(
            string animationPath, int previousElapsed, int target,
            CancellationToken cancellationToken)
        {
            ApiResult reload = ForceApiOnlyAnimation(animationPath);
            if (reload.StatusCode >= 400)
                throw new InvalidOperationException(
                    "iStripper could not reload the active clip.");

            bool cleared = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string current = GetCurrentAnimationPath();
                cleared |= string.IsNullOrEmpty(current);
                if (cleared && string.Equals(current, animationPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    RefreshApiOnlyPlaybackState();
                    if (playbackMovieRegistered &&
                        CallPlaybackApi("IStripperGetState") is 3 or 4)
                    {
                        int elapsed = RequirePlaybackResult(
                            "IStripperGetElapsedMilliseconds");
                        if (previousElapsed >= 1_000 &&
                            elapsed + 250 >= previousElapsed)
                        {
                            await Task.Delay(100, cancellationToken);
                            continue;
                        }
                        return target > elapsed + 75
                            ? await ScanForwardToAsync(elapsed, target,
                                animationPath, cancellationToken)
                            : elapsed;
                    }
                }
                await Task.Delay(100, cancellationToken);
            }
            throw new TimeoutException(
                "iStripper did not finish reloading the active clip.");
        }

        private async Task<int?> TryDecodeTargetFrameAsync(int current, int target,
            string animationAtStart, CancellationToken cancellationToken)
        {
            int prepared = CallPlaybackApi("IStripperPrepareFastForwardMilliseconds",
                unchecked((ulong)target));
            if (prepared < 0)
            {
                if (playbackDecoderKind == 2)
                {
                    throw new COMException(
                        $"The WMV reader could not seek (0x{prepared:X8}).",
                        prepared);
                }
                return null;
            }

            int distance = Math.Abs(target - current);
            double timeoutSeconds = playbackDecoderKind == 2
                ? Math.Clamp(target / 10_000.0 + 10.0, 10.0, 60.0)
                : Math.Clamp(distance / 650.0 + 15.0, 20.0, 1200.0);
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            // One normal-rate advance invokes CAnim for the primed target. Keeping
            // the rate low gives us time to pause before Movie advances past it.
            SetPlaybackRate(1.0);
            RequirePlaybackResult("IStripperResume");

#if DEBUG
            int pollCount = 0;
#endif
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("iStripper could not decode the requested frame in time.");
                }

                string currentAnimation = GetCurrentAnimationPath();
                if (!string.IsNullOrEmpty(animationAtStart) &&
                    !string.Equals(animationAtStart, currentAnimation,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The clip ended while seeking; the next clip was left untouched.");
                }

#if DEBUG
                if (pollCount % 8 == 0)
                {
                    SetPlaybackStatus($"Decoding target frame {FormatPlaybackTime(current)} → {FormatPlaybackTime(target)}...");
                }

#endif
                int status = CallPlaybackApi("IStripperGetFastForwardStatus");
                if (status < 0)
                {
                    throw new COMException(
                        $"The accelerated frame request failed (0x{status:X8}).", status);
                }
                if (status == 1)
                {
                    return RequirePlaybackResult("IStripperGetElapsedMilliseconds");
                }

                await Task.Delay(35, cancellationToken);
#if DEBUG
                pollCount++;
#endif
            }
        }

        private async Task<int> ScanForwardToAsync(int current, int target,
            string animationAtStart,
            CancellationToken cancellationToken)
        {
            int initialDistance = target - current;
            double timeoutSeconds = Math.Clamp(initialDistance / 650.0 + 15.0, 20.0, 1200.0);
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            int? decodedPosition = await TryDecodeTargetFrameAsync(current, target,
                animationAtStart, cancellationToken);
            if (decodedPosition.HasValue)
            {
                return decodedPosition.Value;
            }

            // ponytail: keep the old rate scan as the compatibility fallback.
            double activeScanSpeed = 0;
            int pollCount = 0;
            bool resumed = false;

            while (current < target - 75)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("iStripper could not scan to the requested position in time.");
                }

                int remaining = target - current;
                double scanSpeed = remaining > 12_000 ? 50.0 : remaining > 2_500 ? 10.0 : 2.0;
                if (scanSpeed != activeScanSpeed)
                {
                    SetPlaybackRate(scanSpeed);
                    activeScanSpeed = scanSpeed;
                }

                if (!resumed)
                {
                    RequirePlaybackResult("IStripperResume");
                    resumed = true;
                }

                if (pollCount % 8 == 0)
                {
#if DEBUG
                    SetPlaybackStatus($"Scanning {FormatPlaybackTime(current)} → {FormatPlaybackTime(target)}...");
#endif
                    string currentAnimation = GetCurrentAnimationPath();
                    if (!string.IsNullOrEmpty(animationAtStart) &&
                        !string.IsNullOrEmpty(currentAnimation) &&
                        !string.Equals(animationAtStart, currentAnimation, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The active clip changed while scanning.");
                    }
                }

                await Task.Delay(35, cancellationToken);
                current = RequirePlaybackResult("IStripperGetElapsedMilliseconds");
                pollCount++;
            }

            return current;
        }

        private string GetCurrentAnimationPath()
            => customPlayer != null ? customPlayerAnimationPath :
                GetPlaybackRegistryValue("CurrentAnim");

        private static string GetPlaybackRegistryValue(string valueName)
        {
            lock (playbackRegistryLock)
            {
                try
                {
                    playbackParametersKey ??= Registry.CurrentUser.OpenSubKey(
                        @"Software\Totem\vghd\parameters", false);
                    return playbackParametersKey?.GetValue(
                        valueName, "")?.ToString() ?? "";
                }
                catch
                {
                    playbackParametersKey?.Dispose();
                    playbackParametersKey = null;
                    return "";
                }
            }
        }

        private static void DisposePlaybackRegistryCache()
        {
            lock (playbackRegistryLock)
            {
                playbackParametersKey?.Dispose();
                playbackParametersKey = null;
            }
        }

        private static ulong AlphaCheckpointClipKey(string animationPath)
        {
            if (string.IsNullOrEmpty(animationPath))
                return 0;

            const ulong offset = 14695981039346656037;
            const ulong prime = 1099511628211;
            ulong hash = offset;
            foreach (char rawCharacter in animationPath)
            {
                char character = char.ToUpperInvariant(
                    rawCharacter == '/' ? '\\' : rawCharacter);
                hash = (hash ^ (byte)character) * prime;
                hash = (hash ^ (byte)(character >> 8)) * prime;
            }
            return hash == 0 ? 1 : hash;
        }

        private static ulong AlphaCheckpointCacheBytes(int megabytes) =>
            checked((ulong)megabytes * 1024UL * 1024UL);

        private bool PlaybackReachedEnd(int elapsed, int total)
        {
            int transitionAllowance = (int)Math.Ceiling(
                requestedPlaybackSpeed * 2_000);
            return total > 0 &&
                elapsed >= Math.Max(0, total -
                    Math.Max(1_000, transitionAllowance));
        }

        private void BeginAnimationReplacement(string animationPath)
        {
            playbackRequestedAnimationPath = animationPath;
            playbackRequestedAnimationAt = DateTime.UtcNow;
            playbackCompletedAnimationPath = "";
            playbackNextClipRetryAt = DateTime.MinValue;
            playbackReplacementStableAt = DateTime.MinValue;
        }

        private static bool PlaybackReplacementExpired(string requestedPath,
            DateTime requestedAt, DateTime now) =>
            !string.IsNullOrEmpty(requestedPath) &&
            requestedAt != DateTime.MinValue &&
            now - requestedAt >= TimeSpan.FromMilliseconds(
                PlaybackReplacementTimeoutMilliseconds);

        private int SetVghdPlayerClickThrough(bool enabled)
        {
            if (!playerLockBridgeLoaded || vghd_procID == 0)
            {
                return unchecked((int)0x80070015);
            }

            return CallPlaybackBridgeApi("IStripperSetPlayerClickThrough",
                enabled ? 1UL : 0UL);
        }

        private int SetVghdPlayerWheelResize(bool enabled)
        {
            if (!playerLockBridgeLoaded || vghd_procID == 0)
            {
                return unchecked((int)0x80070015);
            }

            ulong options = enabled ? 1UL : 0UL;
            if (Properties.Settings.Default.AllowWheelWhileLocked)
                options |= 2UL;
            return CallPlaybackBridgeApi(
                "IStripperSetPlayerWheelResize", options);
        }

        private int SetVghdPlayerMode(int mode)
        {
            if (!playerLockBridgeLoaded || vghd_procID == 0)
            {
                return unchecked((int)0x80070015);
            }

            return CallPlaybackBridgeApi(
                "IStripperSetPlayerMode", (ulong)mode);
        }

        private int SetVghdPlayerLarge(bool large)
        {
            if (!playerLockBridgeLoaded || vghd_procID == 0)
            {
                return unchecked((int)0x80070015);
            }

            return CallPlaybackBridgeApi("IStripperSetPlayerLarge",
                large ? 1UL : 0UL);
        }

        private HashSet<string> GetRecentPlaybackPaths()
        {
            if (!Properties.Settings.Default.AvoidRecentRepeats ||
                myData == null)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            lock (playbackHistoryLock)
                return myData.RecentPlaybackPaths(100);
        }

        private static string GetAnimationPath(ModelClip clip)
        {
            string clipName = clip.clipName ?? "";
            if (clipName.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
                return clipName;
            return string.IsNullOrEmpty(clipName)
                ? ""
                : clipName.Split('_')[0] + "\\" + clipName;
        }

        private static string GetCardTagFromAnimationPath(string path)
        {
            if (!path.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
                return path.Split('\\')[0].Split('-')[0];
            int clipSeparator = path.IndexOf(':', "custom:".Length);
            return clipSeparator < 0 ? path : path[..clipSeparator];
        }

        private static List<ModelClip> ExcludeRecentClips(
            IEnumerable<ModelClip> clips, ISet<string> recentPaths)
        {
            return clips.Where(clip =>
                !recentPaths.Contains(GetAnimationPath(clip))).ToList();
        }

        private bool TryChooseRandomAnimation(out string animationPath,
            out string cardTag)
        {
            animationPath = "";
            cardTag = "";
            if (items == null || items.Length == 0)
                return false;

            HashSet<string> recentPaths = GetRecentPlaybackPaths();
            var candidates = new List<(ListViewItem Item,
                List<ModelClip> AllClips, List<ModelClip> FreshClips)>();
            foreach (ListViewItem item in items)
            {
                ModelCard? card = Datastore.findCardByTag(
                    item.Tag?.ToString() ?? "");
                if (!PlaybackCardAllowed(card) || card!.clips == null)
                    continue;

                List<ModelClip> clips = FilterClipList(card.clips);
                if (clips.Count == 0)
                    continue;
                candidates.Add((item, clips,
                    ExcludeRecentClips(clips, recentPaths)));
            }
            if (candidates.Count == 0)
                return false;

            var alternatives = candidates.Where(candidate =>
                !string.Equals(candidate.Item.Tag?.ToString(),
                    nowPlayingTagShort, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (alternatives.Count > 0)
                candidates = alternatives;

            var freshCandidates = candidates.Where(candidate =>
                candidate.FreshClips.Count > 0).ToList();
            if (freshCandidates.Count > 0)
                candidates = freshCandidates;

            var selected = candidates[Random.Shared.Next(candidates.Count)];
            List<ModelClip> selectableClips = selected.FreshClips.Count > 0
                ? selected.FreshClips : selected.AllClips;
            animationPath = GetAnimationPath(
                selectableClips[Random.Shared.Next(selectableClips.Count)]);
            cardTag = selected.Item.Tag?.ToString() ?? "";
            return !string.IsNullOrEmpty(animationPath);
        }

        private static string FormatPlaybackTime(int milliseconds)
        {
            TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }

        private bool OnRegistryValueWrite(string keyname, byte[] data)
        {
            if (formIsClosing || IsDisposed || !IsHandleCreated)
                return false;
            if (keyname == "playingMode" && data.Length >= sizeof(uint))
            {
                try
                {
                    uint mode = BitConverter.ToUInt32(data);
                    if (mode is >= 1 and <= 3)
                    {
                        Volatile.Write(ref playerMode, (int)mode);
                        if (!apiOnlyMode)
                        {
                            if (IsHandleCreated)
                                BeginInvoke((Action)RebuildAutomaticQueue);
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    SetVghdPlayerMode((int)mode);
                                    if (mode != 3)
                                    {
                                        QueueConfiguredPlayerSize(
                                            mode: (int)mode);
                                        ApplyIStripperPlayerVolume((int)mode);
                                    }
                                }
                                catch { }
                            });
                        }
                    }
                }
                catch { }
                return false;
            }
            string str = data.Length < 1 ? "" :
                Encoding.Unicode.GetString(data)
                    .Replace("\0", string.Empty);
            if (keyname == "CurrentAnim" && !apiOnlyMode)
                QueueConfiguredPlayerSize(str);
            if (keyname == "CurrentAnim" && !string.IsNullOrEmpty(str))
                QueueFastDecodeRetry();
            if (data.Length < 1)
            {
                if (apiOnlyMode && keyname == "CurrentAnim")
                {
                    nowPlayingPath = "";
                    playbackTimelineAnimationPath = "";
                    playbackMovieRegistered = false;
                    playbackSeekReady = false;
                    ResetPlaybackReadinessDiagnostics("");
                    playbackLastKnownElapsedMilliseconds = 0;
                    playbackTimelineDurationMilliseconds = 0;
                }
                else if (keyname == "CurrentAnim")
                {
                    BeginInvoke((Action)(() =>
                        ShowNowPlaying("", doWallpaper: false)));
                }
                return false;
            }
            if (keyname == "PreviousUserLevel")
            {
                if (apiOnlyMode)
                {
                    RefreshPlaybackControlVisibility(str);
                }
                else if (!formIsClosing && IsHandleCreated)
                {
                    BeginInvoke((Action)(() => RefreshPlaybackControlVisibility(str)));
                }
                return false;
            }
            if (keyname == "CurrentAnim" && customIstripperSuspended)
            {
                if (!formIsClosing && IsHandleCreated)
                    BeginInvoke(string.IsNullOrEmpty(
                        customPendingIstripperAnimation)
                            ? SuspendIStripperForCustomPlayback
                            : RefreshHiddenIStripperWindow);
                return false;
            }
            if (keyname != "CurrentAnim" || panicActive)
                return false;
            System.Diagnostics.Debug.WriteLine("vghd.exe setting " + keyname + " to " + str);
                bool skipOriginal = false;

                //check if this propsed card is in the filterd list
                string newcardstring = str ?? "";
                bool found = true;
                bool queuedSelection = false;
                bool forceQueuedAnimation = false;
                if (!string.IsNullOrEmpty(newcardstring))
                {
                    this.Invoke((Action)(() =>
                        queuedSelection = TryApplyQueueToAnimationProposal(
                            newcardstring, out newcardstring,
                            out forceQueuedAnimation)));
                }
                if (!apiOnlyMode && !queuedSelection &&
                    !string.Equals(newcardstring,
                        playbackRequestedAnimationPath,
                        StringComparison.OrdinalIgnoreCase) &&
                    (Properties.Settings.Default.EnforceCardFilter ||
                    Properties.Settings.Default.AvoidRecentRepeats)
                    )
                {
                    if (string.IsNullOrEmpty(newcardstring) && !isAutoSelecting)
                    {
                        if (lblNowPlaying != null) this.Invoke((Action)(() => lblNowPlaying.Text = ""));
                        return false;
                    }
                    else if (string.IsNullOrEmpty(newcardstring) && isAutoSelecting)
                    {
                        isAutoSelecting = false;
                    }
                    else
                    {
                        isAutoSelecting = true;
                        ModelCard? model = Datastore.findCardByTag(newcardstring.Split("\\")[0]);
                        ListViewItem? res = null;
                        if (model == null) return false;
                        this.Invoke((Action)(() => res = items.Where(x => x.Text == model.modelName + "\r\n" + model.outfit).FirstOrDefault()));

                        //does the new clip match the clip filter?
                        ModelClip? res2 = null;
                        if (res != null)
                        {
                            var clipstest = FilterClipList(model.clips);
                            string clipstring = newcardstring.Split("\\")[1];
                            res2 = clipstest.Where(c => c.clipName == clipstring).FirstOrDefault();
                        }
                        bool rejectedByFilter =
                            Properties.Settings.Default.EnforceCardFilter &&
                            (res == null || res2 == null);
                        bool rejectedAsRecent =
                            Properties.Settings.Default.AvoidRecentRepeats &&
                            !string.Equals(newcardstring, nowPlayingPath,
                                StringComparison.OrdinalIgnoreCase) &&
                            GetRecentPlaybackPaths().Contains(newcardstring);
                        found = !rejectedByFilter && !rejectedAsRecent;
                        if (!found)
                        {
                            string selectedCardTag = "";
                            bool selected = false;
                            this.Invoke((Action)(() =>
                            {
                                selected = TryChooseRandomAnimation(
                                    out newcardstring, out selectedCardTag);
                                if (selected)
                                {
                                    listModelsNew.ClearSelection();
                                    listModelsNew.SelectWhere(item =>
                                        string.Equals(item.Tag?.ToString(),
                                            selectedCardTag,
                                            StringComparison.OrdinalIgnoreCase));
                                }
                            }));
                            found = selected;
                        }

                    }
                }

                isAutoSelecting = false;
                if (!string.Equals(str, newcardstring,
                        StringComparison.OrdinalIgnoreCase) &&
                    (forceQueuedAnimation || newcardstring != wallpaperTag))
                {
                    if (RequestAnimationPlayback(newcardstring))
                    {
                        wallpaperTag = newcardstring;
                        skipOriginal = true;
                    }
                }
                //if (found) this.BeginInvoke((Action)(() => TaskbarThumbnail()));
                isAutoSelecting = true;
                if (apiOnlyMode && !string.Equals(nowPlayingPath,
                        newcardstring, StringComparison.OrdinalIgnoreCase))
                {
                    nowPlayingPath = newcardstring;
                    playbackTimelineAnimationPath = newcardstring;
                    playbackAlphaCheckpointBucket = -1;
                    try
                    {
                        CallPlaybackApi("IStripperClearAlphaCheckpoints");
                        CallPlaybackApi("IStripperSetAlphaCheckpointCacheKey",
                            Properties.Settings.Default.EnableAlphaCheckpointCache
                                ? AlphaCheckpointClipKey(newcardstring)
                                : 0);
                    }
                    catch { }
                    playbackMovieRegistered = false;
                    playbackSeekingSupported = true;
                    playbackSeekReady = false;
                    playbackDecoderKind = 0;
                    ResetPlaybackReadinessDiagnostics(newcardstring);
                    playbackLastKnownElapsedMilliseconds = 0;
                    playbackTimelineDurationMilliseconds = 0;
                    ArmMovieCapture();
                    if (!string.IsNullOrWhiteSpace(newcardstring))
                    {
                        lock (playbackHistoryLock)
                            myData?.AddPlayback(newcardstring,
                                DateTime.UtcNow);
                    }
                }
                if (!apiOnlyMode)
                {
                    QueueConfiguredPlayerSize(newcardstring);
                    string acceptedAnimationPath = newcardstring;
                    BeginInvoke((Action)(() => ShowNowPlaying(
                        acceptedAnimationPath,
                        doWallpaper: !string.IsNullOrEmpty(
                            acceptedAnimationPath))));
                }
                return skipOriginal;
        }

        private List<ModelClip> FilterClipList(List<ModelClip> clips)
        {
            var currentClips = clips.Where(
                CustomClipAllowedDuringProcessing).ToList();

            string[] parts = txtClipType.Text.ToLower().Split(" and ").Select(p => p.Trim()).ToArray();
            foreach (string p in parts)
            {

                List<string> taglist = p.Split(" or ").Select(p => p.Trim()).ToList();
                if (p.Contains("!"))
                {

                    List<ModelClip>? poslist = null;
                    List<ModelClip>? neglist = currentClips;
                    foreach (string t in taglist.Where(x => !x.Contains("!")))
                    {
                        //do all the positives first
                        poslist = currentClips.Where(c => (c.clipType != null && c.clipType.ContainsWithNot(t))).ToList();
                    }
                    if (poslist == null) poslist = new List<ModelClip> { };
                    foreach (string t in taglist.Where(x => x.Contains("!")))
                    {
                        neglist = neglist.Where(c => (c.clipType != null && c.clipType.ContainsWithNot(t))).ToList();
                    }
                    if (poslist == null) currentClips = new List<ModelClip> { };
                    else
                        if (neglist == null) neglist = new List<ModelClip> { };
                    currentClips = poslist.Union(neglist).ToList();
                }
                else
                {
                    currentClips = currentClips.Where(c => (c.clipType != null && taglist.Any(y => c.clipType.ContainsWithNot(y)))).ToList();
                }


            }

            List<ModelClip> clipsnew = new List<ModelClip>();
            foreach (ModelClip clip in currentClips)
            {
                bool addThis = false;
                switch (clip.hotnessCode)
                {
                    case Enums.HotnessCode.publ:
                        if (chkPublic.Checked)
                            addThis = true;
                        break;
                    case Enums.HotnessCode.nonudity:
                        if (chkNoNudity.Checked)
                            addThis = true;
                        break;
                    case Enums.HotnessCode.topless:
                        if (chkTopless.Checked)
                            addThis = true;
                        break;
                    case Enums.HotnessCode.nudity:
                        if (chkNudity.Checked)
                            addThis = true;
                        break;
                    case Enums.HotnessCode.fullnudity:
                        if (chkFullNudity.Checked)
                            addThis = true;
                        break;
                    case Enums.HotnessCode.xxx:
                        if (chkXXX.Checked)
                            addThis = true;
                        break;
                    default:
                        break;
                }
                if (Properties.Settings.Default.MinSizeMB > 0 && Properties.Settings.Default.MinSizeMB > clip.size / 1024 / 1024) addThis = false;
                if (clip.clipName != null && clip.clipName.Contains("demo") && !chkDemo.Checked) addThis = false;
                if (addThis)
                {
                    clipsnew.Add(clip);
                }
            }
            return clipsnew;
        }

        public static string readItemText(ListView varControl, int itemnum)
        {
            if (varControl.InvokeRequired)
            {
                return (string)varControl.Invoke(
                    new Func<String>(() => readItemText(varControl, itemnum))
                );
            }
            else
            {
                string varText = varControl.Items[itemnum].SubItems[1].Text;
                return varText.Split("_")[0] + "\\" + varText;
            }
        }

        private void GetNowPlaying()
        {
            string nowp = GetCurrentAnimationPath();
            ShowNowPlaying(nowp, !string.IsNullOrEmpty(nowp));
        }

        private bool EnforceNowPlaying(string nowplaying)
        {
            ModelCard? model = Datastore.findCardByTag(
                GetCardTagFromAnimationPath(nowplaying));
            ListViewItem? res = null;
            if (model == null) return false;
            this.Invoke((Action)(() => res = items.Where(x => x.Text == model.modelName + "\r\n" + model.outfit).FirstOrDefault()));
            if (res == null)
            {
                //play a clip from a filtered card instead
                this.BeginInvoke((Action)(() => GetNextCard()));
                return true;
            }
            return false;
        }

        private void ShowNowPlaying(string path, bool doWallpaper = false)
        {
            try
            {
                if (path == nowPlayingPath &&
                    (string.IsNullOrEmpty(path) ||
                     !string.IsNullOrEmpty(nowPlaying)))
                {
                    QueueNowPlayingUiUpdate();
                    return;
                }
                bool pathChanged = path != nowPlayingPath;
                nowPlayingPath = path;
                if (pathChanged) ObserveUnqueuedCardPlayback(path);
                WakePlaybackTimeline();
                ArmMovieCapture();
                nowPlaying = "";
                if (path == "")
                {
                    QueueNowPlayingUiUpdate();
                    return;
                }
                if (pathChanged)
                {
                    lock (playbackHistoryLock)
                        myData?.AddPlayback(path, DateTime.UtcNow);
                }
                if (Datastore.modelcards == null) return;
                if (Datastore.modelcards.Count > 0)
                {
                    bool custom = path.StartsWith("custom:",
                        StringComparison.OrdinalIgnoreCase);
                    string cardTag = GetCardTagFromAnimationPath(path);
                    ModelCard? model = Datastore.findCardByTag(cardTag);
                    if (model == null) return;
                    string clipName = custom ? path : path.Split("\\")[1];
                    ModelClip? modelClip = model.clips?.FirstOrDefault(x =>
                        string.Equals(x.clipName, clipName,
                            StringComparison.OrdinalIgnoreCase));
                    if (modelClip == null) return;
                    nowPlaying = model.modelName + ", " + model.outfit + " (Clip " + modelClip.clipNumber + ")";
                    nowPlayingTagShort = cardTag;
                    nowPlayingTag = model.modelName + "\r\n" + model.outfit;
                    if (listModelsNew.Items.Where(x => x.Text == nowPlayingTag).Any())
                    {
                        nowPlayingFilterMatch = nowPlayingTag;
                    }
                    nowPlayingClipNumber = Convert.ToInt32(modelClip.clipNumber);
                }
                cardRenderer.nowPlayingTag = nowPlayingTag;
            }
            catch { }
            QueueNowPlayingUiUpdate();
            if (Properties.Settings.Default.AutoWallpaper && doWallpaper &&
                nowPlaying != "")
                BeginInvoke((Action)(() => _ = ChangeWallpaper(
                    NotFromCheck: false, onlyWhenCardChanges: true)));
        }

        private bool LegacyPlaybackStalledNearEnd(int elapsed, int total,
            DateTime lastProgress, DateTime now, int state) =>
            state == 3 && PlaybackReachedEnd(elapsed, total) &&
            now - lastProgress >= TimeSpan.FromSeconds(2);

        private static bool SeekTargetReachesEnd(int target, int total) =>
            total > 0 && target >= Math.Max(0, total - 1_000);

        private void WakePlaybackTimeline()
        {
            if (!Properties.Settings.Default.EnablePlaybackControl ||
                !playbackControlsAvailableForAccount || !playbackBridgeLoaded ||
                !IsHandleCreated || IsDisposed)
            {
                return;
            }

            void Wake()
            {
                playbackNextMovieDiscoveryAt = DateTime.MinValue;
                SetTimerInterval(playbackTimelineTimer,
                    PlaybackTransitionIntervalMilliseconds);
                if (!playbackTimelineTimer.Enabled)
                    playbackTimelineTimer.Start();
            }

            if (InvokeRequired)
                BeginInvoke((Action)Wake);
            else
                Wake();
        }

        private void ArmMovieCapture()
        {
            if (!movieCaptureHookInstalled || !playbackBridgeLoaded ||
                !IsHandleCreated || IsDisposed)
            {
                playbackMovieCaptureFallbackAt = DateTime.MinValue;
                return;
            }

            void Arm()
            {
                try
                {
                    CallPlaybackApi("IStripperArmMovieCapture");
                    playbackMovieCaptureFallbackAt =
                        DateTime.UtcNow.AddMilliseconds(750);
                }
                catch
                {
                    playbackMovieCaptureFallbackAt = DateTime.MinValue;
                }
            }

            if (InvokeRequired)
                BeginInvoke((Action)Arm);
            else
                Arm();
        }

        private void DisableMovieCapture()
        {
            if (!movieCaptureHookInstalled || !playbackBridgeLoaded)
                return;
            try { CallPlaybackApi("IStripperCancelMovieCapture"); }
            catch { }
        }

        private void RefreshPlayingClipHighlight()
        {
            Color playingBackColor = Properties.Settings.Default.DarkMode
                ? Color.FromArgb(38, 90, 58)
                : Color.FromArgb(198, 239, 206);
            Color playingForeColor = Properties.Settings.Default.DarkMode
                ? Color.White
                : Color.FromArgb(0, 80, 24);
            foreach (ListViewItem item in listClips.Items)
            {
                bool isPlaying = item.SubItems.Count > 1 &&
                    IsPlayingClip(item.SubItems[1].Text, nowPlayingPath);
                if (isPlaying)
                    item.Selected = false;
                item.BackColor = isPlaying ? playingBackColor : listClips.BackColor;
                item.ForeColor = isPlaying ? playingForeColor : listClips.ForeColor;
            }
        }

        private static bool IsPlayingClip(string clipName, string animationPath) =>
            string.Equals(clipName, animationPath.Split('\\').LastOrDefault(),
                StringComparison.OrdinalIgnoreCase);

        private void chk_CheckedChanged(object sender, EventArgs e)
        {
            FilterClips();
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SortBy = cmbSortBy.Text;
            if (cardRenderer != null)
            {
                cardRenderer.sortBy = cmbSortBy.Text;
                PopulateModelListview();
            }
        }

        private bool clickingNowPlaying = false;
        private void lblNowPlaying_Click(object sender, EventArgs e)
        {
            NowPlayingClick(true);
        }

        private void NowPlayingClick(bool ignoreReg = false)
        {
            if (ignoreReg) clickingNowPlaying = true;
            if (nowPlayingTag != null && nowPlayingTag.Length > 0)
            {
                string[] p = nowPlayingTag.Split("\r\n");
                ModelCard c = Datastore.modelcards.Where(t => t.modelName == p[0] && t.outfit == p[1]).First();
                listModelsNew.ClearSelection();
                var i = items.Where(x => x.Text == nowPlayingTag).FirstOrDefault();
                int? index = items.ToList().FindIndex(x => x.Text == nowPlayingTag);
                if (i != null)
                {
                    listModelsNew.SelectWhere(x => x.Text == nowPlayingTag);
                    cardRenderer.nowPlayingTag = nowPlayingTag;
                    listModelsNew.EnsureVisible((int)index);
                }
                else
                {
                    loadListClips(c.name);
                }

                //select the playing clip in list
                listClips.SelectedItems.Clear();
                if (nowPlayingClipNumber != 0)
                {
                    var k = listClips.FindItemWithText(nowPlayingClipNumber.ToString());
                    if (k != null)
                    {
                        listClips.SelectedIndices.Add(k.Index);
                        k.Selected = true;
                        listClips.EnsureVisible(k.Index);
                    }
                }
            }
        }

        private void txtSearch_TextChanged(object sender, EventArgs e)
        {

        }

        private void txtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                cmdClearSearch.Visible = (txtSearch.Text.Length > 0);
                PopulateModelListview();
            }
            else if (txtSearch.Text.Length > 0)
                cmdClearSearch.Visible = false;
        }

        private void cmdShowModel_click(object sender, EventArgs e)
        {
            txtSearch.Text = "model:" + nowPlayingTag.Split("\r\n")[0];
            PopulateModelListview();
            string[] p = nowPlayingTag.Split("\r\n");
            if (p.Length < 2) return;
            ModelCard c = Datastore.modelcards.Where(t => t.modelName == p[0] && t.outfit == p[1]).First();
            listModelsNew.ClearSelection();
            var i = items.Where(x => x.Text == nowPlayingTag).FirstOrDefault();
            int? index = items.ToList().FindIndex(x => x.Text == nowPlayingTag);
            if (i != null)
            {
                listModelsNew.SelectWhere(x => x.Text == nowPlayingTag);
                cardRenderer.nowPlayingTag = (nowPlayingTag);
                listModelsNew.EnsureVisible((int)index);
                cmdClearSearch.Visible = true;
            }
        }

        private void cmdClearSearch_Click(object sender, EventArgs e)
        {
            txtSearch.Text = "";
            PopulateModelListview();
            cmdClearSearch.Visible = false;
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (apiOnlyMode)
            {
                CloseApiOnly();
                return;
            }
            if (customShowQueueManager?.HasActiveJob == true)
            {
                DialogResult closeQueue = MessageBox.Show(this,
                    "An automatic custom-show job is running. Closing QuickPlayer will " +
                    "cancel it and return it to Pending so it restarts from the beginning " +
                    "next time.\n\nClose QuickPlayer?", "Custom-Show Queue",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (closeQueue != DialogResult.Yes) { e.Cancel = true; return; }
                customShowQueueManager.CancelForExit();
            }

            formIsClosing = true;
            DisposeDressingRoomStreams();
            StopCustomPlayback(restoreIstripper: true);
            StopRestApi();
            cardOverlayTimer.Stop();
            directCompositionCardOverlays?.Dispose();
            directCompositionCardOverlays = null;
            playbackTimelineTimer.Stop();
            DisposePlaybackRegistryCache();
            if (panicActive)
            {
                try
                {
                    if (playbackBridgeLoaded && playbackMovieRegistered &&
                        RequirePlaybackResult("IStripperGetState") == 4)
                        RequirePlaybackResult("IStripperResume");
                }
                catch { }
                LockStateOverlay.ShowMovieWindow(panicMovieWindow);
                panicActive = false;
            }
            playbackTimelineTimer.Dispose();
            cardOverlayTimer.Dispose();
            playbackLifetime.Cancel();
            UnregisterHotKeys();
            timerhook?.Dispose();
            DisableMovieCapture();
            if (playerLockBridgeLoaded)
            {
                try { RestoreNormalPlayerSizes(); } catch { }
                try { SetVghdPlayerWheelResize(false); } catch { }
                try { SetVghdPlayerLocked(false); } catch { }
                playerLockBridgeLoaded = false;
            }
            if (playbackBridgeLoaded && playbackMovieRegistered)
            {
                // If the form closes during an accelerated scan, restore the user's
                // selected rate before the bridge is released.
                try { SetPlaybackRate(requestedPlaybackSpeed); } catch { }
            }
            playbackBridgeClient?.Dispose();
            playbackBridgeClient = null;
            SavePreviousQueue();
            SaveMyData();
            Wallpaper.SuspendAndRestoreOriginalDesktop();
            if (Utils.DefaultIconsVisible != Utils.DesktopIconsVisible())
            {
                Utils.ToggleDesktopIcons();
            }
            if (restoringBackup)
                return;
            if (WindowState == FormWindowState.Maximized)
            {
                Properties.Settings.Default.Location = RestoreBounds.Location;
                Properties.Settings.Default.Size = RestoreBounds.Size;
                Properties.Settings.Default.Maximised = true;
                Properties.Settings.Default.Minimised = false;
            }
            else if (WindowState == FormWindowState.Normal)
            {
                Properties.Settings.Default.Location = Location;
                Properties.Settings.Default.Size = Size;
                Properties.Settings.Default.Maximised = false;
                Properties.Settings.Default.Minimised = false;
            }
            else
            {
                Properties.Settings.Default.Location = RestoreBounds.Location;
                Properties.Settings.Default.Size = RestoreBounds.Size;
                Properties.Settings.Default.Maximised = false;
                Properties.Settings.Default.Minimised = true;
            }
            Properties.Settings.Default.Save();
            customShowQueueForm?.Dispose();
            customShowQueueManager?.Dispose();
        }

        private void QueueNowPlayingUiUpdate()
        {
            if (apiOnlyMode || IsDisposed || formIsClosing)
                return;

            if (!InvokeRequired)
            {
                UpdateNowPlayingUi();
                return;
            }

            if (Interlocked.Exchange(ref nowPlayingUiUpdatePending, 1) != 0)
                return;
            BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref nowPlayingUiUpdatePending, 0);
                UpdateNowPlayingUi();
            }));
        }

        private void UpdateNowPlayingUi()
        {
            if (IsDisposed || formIsClosing) return;
            string previousTag = displayedNowPlayingCardTag;
            string currentTag = nowPlayingTagShort;
            displayedNowPlayingCardTag = currentTag;
            if (lblNowPlaying != null)
                lblNowPlaying.Text = "Now Playing: " + nowPlaying;
            if (!string.Equals(previousTag, currentTag,
                    StringComparison.OrdinalIgnoreCase))
            {
                ImageListViewItem[] changed = listModelsNew.Items
                    .Where(item => string.Equals(item.Tag?.ToString(),
                            previousTag,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(item.Tag?.ToString(), currentTag,
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (changed.Length > 0)
                    listModelsNew.RefreshItems(changed);
            }
            RefreshPlayingClipHighlight();
            if (listClips.Items.Count == 0 &&
                !string.IsNullOrEmpty(nowPlayingPath))
                NowPlayingClick(true);
            TaskbarThumbnail();
        }

        private int IStripperPlayerVolume(int mode)
        {
            EnsureIStripperPlayerVolumes();
            return Math.Clamp(mode == 1
                ? Properties.Settings.Default.IStripperLargePlayerVolume
                : Properties.Settings.Default.IStripperSmallPlayerVolume,
                0, 100);
        }

        private void EnsureIStripperPlayerVolumes()
        {
            if (Properties.Settings.Default.IStripperSmallPlayerVolume >= 0 &&
                Properties.Settings.Default.IStripperLargePlayerVolume >= 0)
                return;
            int current = 100;
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Totem\vghd\parameters", false);
                if (key?.GetValue("SoundVolume") is int volume)
                    current = Math.Clamp(volume, 0, 100);
            }
            catch { }
            if (Properties.Settings.Default.IStripperSmallPlayerVolume < 0)
                Properties.Settings.Default.IStripperSmallPlayerVolume = current;
            if (Properties.Settings.Default.IStripperLargePlayerVolume < 0)
                Properties.Settings.Default.IStripperLargePlayerVolume = current;
        }

        private void ChangeIStripperPlayerVolume(int mode, int delta)
        {
            int percent = Math.Clamp(IStripperPlayerVolume(mode) + delta, 0, 100);
            if (mode == 1)
                Properties.Settings.Default.IStripperLargePlayerVolume = percent;
            else
                Properties.Settings.Default.IStripperSmallPlayerVolume = percent;
            Properties.Settings.Default.Save();
            RefreshIStripperPlayerVolumeMenu();
            ApplyIStripperPlayerVolume(mode, percent, showOverlay: true);
        }

        private void ApplyIStripperPlayerVolume(int mode, int? percent = null,
            bool showOverlay = false)
        {
            if (mode is not (1 or 2))
                return;
            int value = percent ?? IStripperPlayerVolume(mode);
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Totem\vghd\parameters", true);
                key?.SetValue("SoundVolume", value, RegistryValueKind.DWord);
            }
            catch { }
            int result = CallPlaybackBridgeApi(
                "IStripperSetPlayerVolume", (ulong)value);
            if (result < 0)
            {
                SetPlaybackStatus(
                    $"Player volume update failed (0x{result:X8}).");
            }
            if (showOverlay)
                LockStateOverlay.ShowTextForProcess(
                    vghd_procID, $"Volume {value}%");
        }

        private void CloseApiOnly()
        {
            formIsClosing = true;
            StopRestApi();
            DisposePlaybackRegistryCache();
            playbackLifetime.Cancel();
            timerhook?.Dispose();
            DisableMovieCapture();
            if (panicActive)
            {
                try
                {
                    if (playbackBridgeLoaded && playbackMovieRegistered &&
                        RequirePlaybackResult("IStripperGetState") == 4)
                        RequirePlaybackResult("IStripperResume");
                }
                catch { }
                LockStateOverlay.ShowMovieWindow(panicMovieWindow);
                panicActive = false;
            }
            if (playbackBridgeLoaded && playbackMovieRegistered)
            {
                try { SetPlaybackRate(requestedPlaybackSpeed); } catch { }
            }
            playbackBridgeClient?.Dispose();
            playbackBridgeClient = null;
            SaveMyData();
            apiOnlyNotifyIcon?.Dispose();
            apiOnlyNotifyIcon = null;
            apiOnlyContextMenu?.Dispose();
            apiOnlyContextMenu = null;
            playbackTimelineTimer.Dispose();
            cardOverlayTimer.Dispose();
            clipSelectionPlaybackTimer.Dispose();
            playQueueResizeTimer.Dispose();
        }

        private void cmdNextClip_Click(object sender, EventArgs e)
        {
            this.BeginInvoke((Action)(() =>
                GetNextClip(useQueue: false)));
        }

        private void GetNextClip(ModelCard? model = null,
            string? completedAnimation = null, bool useQueue = true)
        {
            if (panicActive)
                return;

            if (!useQueue || model != null)
                ClearQueuedCardSession();

            bool continueUnqueuedCard = useQueue && model == null &&
                activeQueuedCard == null &&
                !string.IsNullOrEmpty(completedAnimation) &&
                IsUnqueuedCardSession(completedAnimation);
            if (continueUnqueuedCard &&
                !CanContinueUnqueuedCard(completedAnimation!))
            {
                ClearUnqueuedCardSession();
                GetNextCard();
                return;
            }

            // A manually selected card owns playback until its show-duration
            // session expires. The automatic queue must not take over between
            // clips in that session.
            if (!continueUnqueuedCard && useQueue && model == null &&
                TryPlayNextQueuedAnimation())
                return;
            if (continueUnqueuedCard &&
                TryPlayPreloadedCustomClip(completedAnimation!))
                return;

            string path = completedAnimation ?? "";
            bool chooseRandom = false;
            if (model != null)
            {
                chooseRandom = true;
            }
            else if (string.IsNullOrEmpty(path))
            {
                path = GetCurrentAnimationPath();
                if (path == "") return;
            }


            if (Datastore.modelcards == null) return;
            if (Datastore.modelcards.Count > 0)
            {
                if (model == null) model = Datastore.findCardByTag(
                    GetCardTagFromAnimationPath(path));
                if (model == null) return;
                List<ModelClip> clips = new List<ModelClip>();
                if (model.clips == null) return;
                clips = FilterClipList(model.clips)
                    .OrderBy(clip => clip.clipNumber).ToList();
                bool sequentialSession = !string.IsNullOrEmpty(path) &&
                    UnqueuedCardContinuesSequentially(path);
                List<ModelClip> selectableClips = clips;
                if (chooseRandom || Properties.Settings.Default.Randomize &&
                    !sequentialSession)
                {
                    List<ModelClip> freshClips = ExcludeRecentClips(clips,
                        GetRecentPlaybackPaths());
                    if (freshClips.Count > 0) selectableClips = freshClips;
                }

                ModelClip? mnew = null;
                if (chooseRandom)
                {
                    if (selectableClips.Count == 0) return;
                    mnew = selectableClips[
                        Random.Shared.Next(selectableClips.Count)];
                }
                else
                {
                    string currentClipName = model.IsCustom
                        ? path : path.Split("\\")[1];
                    var cliplst = clips.Where(x => x.clipName == currentClipName);
                    if (cliplst.Count() > 0)
                    {
                        ModelClip modelClip = cliplst.First();
                        mnew = selectableClips.FirstOrDefault(x =>
                            x.clipNumber > modelClip.clipNumber);
                        if (mnew == null && model.IsCustom)
                        {
                            GetNextCard();
                            return;
                        }
                        mnew ??= selectableClips.FirstOrDefault();
                    }
                    else
                    {
                        mnew = selectableClips.FirstOrDefault();
                    }
                }

                if (mnew != null && mnew.clipName != null)
                {
                    RequestAnimationPlayback(GetAnimationPath(mnew));
                }
            }
        }

        private void GetNextCard()
        {
            if (panicActive)
                return;

            ClearUnqueuedCardSession();

            ClearQueuedCardSession(clearManualQueueEntry: false);
            if (TryPlayNextQueuedAnimation())
                return;

            //find a new model from the filtered cards
            if (items == null || items.Length < 1) return;
            Random r = Random.Shared;

            string newtag = nowPlayingFilterMatch;
            if (Properties.Settings.Default.Randomize)
            {
                if (!TryChooseRandomAnimation(out string animationPath,
                        out string cardTag))
                {
                    return;
                }
                listModelsNew.ClearSelection();
                listModelsNew.SelectWhere(item => string.Equals(
                    item.Tag?.ToString(), cardTag,
                    StringComparison.OrdinalIgnoreCase));
                string clipName = animationPath.Split('\\').Last();
                ListViewItem? clipItem = listClips.Items
                    .Cast<ListViewItem>()
                    .FirstOrDefault(item => string.Equals(
                        item.SubItems[1].Text, clipName,
                        StringComparison.OrdinalIgnoreCase));
                if (clipItem != null)
                {
                    listClips.SelectedItems.Clear();
                    clipItem.Selected = true;
                    listClips.EnsureVisible(clipItem.Index);
                }
                RequestAnimationPlayback(animationPath);
                BeginInvoke((Action)TaskbarThumbnail);
                return;
            }
            else
            {
                ListViewItem[] playableItems = items.Where(item =>
                    PlaybackCardAllowed(Datastore.findCardByTag(
                        item.Tag?.ToString() ?? ""))).ToArray();
                if (playableItems.Length == 0)
                    return;
                //find the current card
                int i = 0;
                for (i = 0; i < playableItems.Length; i++)
                {
                    if (playableItems[i].Text.ToString() == newtag)
                        break;
                }
                i++;
                if (i > playableItems.Length - 1) i = 0;
                newtag = playableItems[i].Text;
            }
            listModelsNew.ClearSelection();
            int? index = items.ToList().FindIndex(x => x.Text == newtag);
            if (index != null)
            {
                listModelsNew.SelectWhere(x => x.Text == newtag);
                listModelsNew.EnsureVisible((int)index);
            }
            //choose a random clip from those shown
            if (listClips.Items.Count == 0) return;
            List<ListViewItem> clipItems = listClips.Items
                .Cast<ListViewItem>().ToList();
            HashSet<string> recentPaths = GetRecentPlaybackPaths();
            List<ListViewItem> freshItems = clipItems.Where(item =>
                !recentPaths.Contains(item.SubItems[1].Text.Split('_')[0] +
                    "\\" + item.SubItems[1].Text)).ToList();
            if (freshItems.Count > 0)
                clipItems = freshItems;
            var itemnum = r.Next(clipItems.Count);
            listClips.SelectedItems.Clear();
            var j = clipItems[itemnum];
            if (j != null)
            {
                j.Selected = true;
                listClips.Select();
                listClips.EnsureVisible(j.Index);
            }
            this.BeginInvoke((Action)(() => TaskbarThumbnail()));
        }

        private void ReloadStaticProperties()
        {
            StaticPropertiesLoader.loadXML();
            PropertiesLoader.loadXML();
        }

        private async Task RefreshCardMetadataAsync()
        {
            refreshCardMetadataToolStripMenuItem.Enabled = false;
            Cursor previousCursor = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                await MetadataRefresh.RefreshAsync();
                ReloadModels();
                MessageBox.Show(this,
                    "Card metadata was refreshed and the catalogue was reloaded.",
                    "Refresh Card Metadata", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                MetadataDiagnostics.RecordRefreshFailure(exception);
                MessageBox.Show(this,
                    "Card metadata could not be refreshed.\r\n" +
                    exception.Message + "\r\n\r\nA privacy-safe diagnostic was " +
                    "written to:\r\n" + MetadataDiagnostics.FilePath,
                    "Refresh Card Metadata",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previousCursor;
                refreshCardMetadataToolStripMenuItem.Enabled = true;
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void hotkeysToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Hotkeys hkeys = new Hotkeys();
            hkeys.ShowDialog();
            SetupKeyHooks();
        }

        private void cmdFilter_Click(object sender, EventArgs e)
        {
            //var frm = Application.OpenForms.Cast<Form>().Where(x => x.Name == "Filter").FirstOrDefault();
            //if (frm == null)
            //{
            string f = "Default";
            if (cmbFilter.SelectedItem != null)
                f = cmbFilter.SelectedItem.ToString() ?? "Default";
            var frm = new Filter(filterSettings, f);
            frm.StartPosition = FormStartPosition.CenterParent;
            frm.ShowDialog(this);
            //string currentFilter = "Default";
            //if (cmbFilter.Items != null && cmbFilter.Items.Count > 0 && cmbFilter.SelectedItem != null)   
            //    currentFilter = cmbFilter.SelectedItem.ToString();
            //PopulateFilterList();
            //if (cmbFilter.Items.Contains(currentFilter)) cmbFilter.SelectedValue = currentFilter;
            //frm.TopMost = true;
            //}
            //frm.BringToFront();

        }

        private void txtSearch_Enter(object sender, EventArgs e)
        {
        }

        private void enforceCardFilterToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.EnforceCardFilter = enforceCardFilterToolStripMenuItem.Checked;
            RefreshPlayQueueVisibility();
            RebuildAutomaticQueue();
        }


        private void menuCardList_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {

            if (mousedownCard == null)
            {
                e.Cancel = true;
                return;
            }
            currentMenuCard = mousedownCard;
            ModelCard? c = Datastore.findCardByTag(mousedownCard.Tag.ToString());
            cardRenderer.CardMenuText = mousedownCard.Tag.ToString();
            if (c == null) return;
            if (myData != null && myData.GetCardRating(c.name.ToString()) > 0)
                ratingSlider.Value =
                    myData.GetCardRating(c.name.ToString()) / 2;
            else
                ratingSlider.Value = 0;
            if (myData != null) menuCardFavourite.Checked = myData.GetCardFavourite(c.name);
            if (c.rating > 0) ratingToolStripMenuItem.Text = "Rating: " + (c.rating - 5M).ToString();
            else ratingToolStripMenuItem.Text = "Rating: NA";
            statsToolStripMenuItem.Text = "Stats: " + c.bust + "/" + c.waist + "/" + c.hips;
            nameToolStripMenuItem.Text = c.modelName;
            outfitToolStripMenuItem.Text = c.outfit;
            outfitToolStripMenuItem.Enabled = !c.IsCustom;
            nameToolStripMenuItem.ToolTipText = c.IsCustom
                ? "Open this model on IAFD."
                : "Open this model on the iStripper website.";
            outfitToolStripMenuItem.ToolTipText = c.IsCustom
                ? "Custom show title."
                : "Open this card on the iStripper website.";
            addModelToFilterToolStripMenuItem.Text = "Filter to Model";
            CultureInfo cultureInfo = Thread.CurrentThread.CurrentCulture;
            TextInfo textInfo = cultureInfo.TextInfo;
            if (c.hair != null) hairToolStripMenuItem.Text = "Hair: " + textInfo.ToTitleCase(c.hair.ToLower());
            if (c.datePurchased != null) purchasedToolStripMenuItem.Text = "Purchased: " + ((DateTime)c.datePurchased).ToShortDateString();
            if (c.IsCustom) hotnessToolStripMenuItem.Text = "Hotness: " + c.hotnessLevel;
            else if (c.hotnessLevel != "") hotnessToolStripMenuItem.Text = "Hotness: " + ((Enums.HotnessCode)Convert.ToInt32(c.hotnessLevel)).GetDescription();
            else hotnessToolStripMenuItem.Text = "Hotness: NA";
            ageToolStripMenuItem.Text = "Age: " + c.modelAge;
        }

        private ImageListViewItem? mousedownCard = null;
        private ImageListViewItem? currentMenuCard = null;
        private void menuCardFavourite_CheckedChanged(object sender, EventArgs e)
        {
            UpdateFavouriteMenuItem();
            if (myData == null || currentMenuCard == null) return;
            if (myData.GetCardFavourite(currentMenuCard.Tag.ToString()) != menuCardFavourite.Checked)
            {
                myData.AddCardFavourite(currentMenuCard.Tag.ToString(), menuCardFavourite.Checked);
                RecalculateCardOverlay(currentMenuCard, true);
            }
        }

        private void chkFavourite_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.FavouritesFilter = chkFavourite.Checked;
            PopulateModelListview();
        }

        private void cmbMenuCardRating_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (myData == null || currentMenuCard == null) return;
            myData.AddCardRating(currentMenuCard.Tag.ToString(), cmbMenuCardRating.SelectedIndex + 1);
            RecalculateCardOverlay(
                currentMenuCard, menuShowRatingsStars.Checked);
        }


        private void RatingSlider_ValueChanged(object sender, EventArgs e)
        {
            if (myData == null || currentMenuCard == null) return;
            decimal rating = SnapHalfStar(ratingSlider.Value);
            if (ratingSlider.Value != rating)
            {
                ratingSlider.Value = rating;
                return;
            }
            if (myData.GetCardRating(currentMenuCard.Tag.ToString()) ==
                rating * 2) return;
            myData.AddCardRating(currentMenuCard.Tag.ToString(), rating * 2);
            RecalculateCardOverlay(
                currentMenuCard, menuShowRatingsStars.Checked);
        }

        private void RecalculateCardOverlay(
            ImageListViewItem changedCard, bool baseCardChanged)
        {
            ModelCard? card = Datastore.findCardByTag(
                changedCard.Tag?.ToString() ?? "");
            bool overlayChanged = card != null &&
                CardOverlayLoader.RecalculateCard(card, myData);
            if (overlayChanged || baseCardChanged)
                listModelsNew.RefreshItems([changedCard]);
            if (overlayChanged)
                RefreshPlayQueueCardOverlays();
        }

        private void UpdateFavouriteMenuItem()
        {
            menuCardFavourite.Text =
                menuCardFavourite.Checked ? "♥ Favourite" : "♡ Favourite";
            menuCardFavourite.ForeColor = menuCardFavourite.Checked
                ? Color.LightGreen : menuCardList.ForeColor;
        }

        private void addModelToFilterToolStripMenuItem_Click(
            object? sender, EventArgs e)
        {
            if (currentMenuCard == null)
                return;
            ModelCard? card = Datastore.findCardByTag(
                currentMenuCard.Tag.ToString());
            if (string.IsNullOrEmpty(card?.modelName))
                return;
            txtSearch.Text = AddModelFilter(txtSearch.Text, card.modelName);
            cmdClearSearch.Visible = true;
            PopulateModelListview();
        }

        private static string AddModelFilter(string filter, string model) =>
            string.IsNullOrWhiteSpace(filter)
                ? $"model:{model}"
                : $"({filter}) AND model:{model}";

        private static decimal SnapHalfStar(decimal rating) =>
            Math.Round(rating * 2, MidpointRounding.AwayFromZero) / 2;

        private void chkShowRatingStars_CheckedChanged(object sender, EventArgs e)
        {
            bool r = Properties.Settings.Default.ShowRatingStars;
            if (r != menuShowRatingsStars.Checked)
            {
                Properties.Settings.Default.ShowRatingStars = menuShowRatingsStars.Checked;
                listModelsNew.Refresh();
            }
        }

        private void menuShowCardSortLabels_CheckedChanged(
            object sender, EventArgs e)
        {
            if (Properties.Settings.Default.ShowCardSortLabels ==
                menuShowCardSortLabels.Checked)
                return;
            Properties.Settings.Default.ShowCardSortLabels =
                menuShowCardSortLabels.Checked;
            listModelsNew.Refresh();
        }

        private void EditOverlayDefaults()
        {
            using OverlayDefaultsForm dialog = new(
                CardOverlayLoader.GetChoices(),
                CardOverlayLoader.GetRules(),
                directCompositionCardOverlays);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            CardOverlayLoader.SaveRules(dialog.Rules);
            drawCardOverlaysToolStripMenuItem.Checked =
                Properties.Settings.Default.DrawCardOverlays;
            CardOverlayLoader.Reload(
                Datastore.modelcards ?? [], myData);
            listModelsNew.Refresh();
        }

        private void UpdateCardOverlayRenderingPath()
        {
            DisableDirectCompositionCardOverlays();
            if (deferCardCompositionUntilShown)
                return;
            if (!Properties.Settings.Default
                    .DirectCompositionOverlays ||
                cardRenderer == null)
                return;

            DirectCompositionCardOverlayControl overlays =
                new(listModelsNew, cardRenderer);
            if (!overlays.Initialize())
                return;
            directCompositionCardOverlays = overlays;
            cardRenderer.DrawWithDirectComposition = true;
            CardOverlayLoader.DrawWithDirectComposition = true;
            UpdatePlayQueueCardOverlays();
            listModelsNew.Refresh();
        }

        private void DisableDirectCompositionCardOverlays()
        {
            if (cardRenderer != null)
                cardRenderer.DrawWithDirectComposition = false;
            directCompositionCardOverlays?.Dispose();
            directCompositionCardOverlays = null;
            CardOverlayLoader.DrawWithDirectComposition = false;
            listModelsNew.Refresh();
        }

        private void menuCardList_Closing(object sender, ToolStripDropDownClosingEventArgs e)
        {
            //currentMenuCard = null;
            cardRenderer.CardMenuText = "";
            if (!listModelsNew.ClientRectangle.Contains(PointToClient(Control.MousePosition)))
            {
                cardRenderer.MouseIsOnList = false;
                listModelsNew.Refresh();
            }
        }

        private void txtUserTags_TextChanged(object sender, EventArgs e)
        {
            if (myData == null) return;
            if (items != null && items.Length > 0)
            {
                List<string> tags = txtUserTags.Text.Split(',').ToList();
                if (clipListTag != "") myData.AddCardTags(clipListTag, tags);
            }
        }

        private async void cmdPhotos_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(clipListTag)) return;
            cmdPhotos.Enabled = false;
            Cursor = Cursors.WaitCursor;
            PhotoViewer? viewer = null;
            try
            {
                CardPhotos? photos = await LoadPhotosForCard(clipListTag);
                if (photos == null || photos.getNumberOfPhotos() == 0)
                {
                    MessageBox.Show(this,
                        "No photos were found for this card. For a custom show, attach a folder in Edit Custom Show Metadata.",
                        "Photos", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                viewer = new PhotoViewer(photos);
                viewer.Show(this);
                Cursor = Cursors.Arrow;
                await viewer.PopulateAsync();
            }
            catch (Exception error)
            {
                if (viewer is { IsDisposed: false }) viewer.Close();
                MessageBox.Show(this, $"The photos could not be loaded.\r\n\r\n{error.Message}",
                    "Photos", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Arrow;
                cmdPhotos.Enabled = true;
            }
        }

        private async Task<CardPhotos?> LoadPhotosForCard(string tag)
        {
            string cardTag = tag.Split('\\')[0];
            ModelCard? card = Datastore.findCardByTag(cardTag);
            CardPhotos photos = new();
            if (card?.customShowId != null)
            {
                CustomShowStore store = new(customShowConfiguration.LibraryRoot);
                CustomShowManifest show = store.LoadManifest(card.customShowId);
                bool loaded = await Task.Run(() =>
                    photos.LoadLocalPhotos(show.Media.PhotosFolder));
                return loaded ? photos : null;
            }
            await photos.LoadCardPhotos(client, tag);
            return photos;
        }

        private void includeDescriptionInSearchToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.ShowDescInSearch = includeDescriptionInSearchToolStripMenuItem.Checked;
        }

        private void includeShowTitleInSearchToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.ShowOutfitInSearch = includeShowTitleInSearchToolStripMenuItem.Checked;
        }

        private void cmbFilter_SelectedIndexChanged(object sender, EventArgs e)
        {
            string? selectedFilter = cmbFilter.SelectedItem?.ToString();
            if (selectedFilter == null) return;
            filterSettings = FilterSettingsList.GetFilter(selectedFilter);
            PopulateModelListview();
        }

        private void txtClipType_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                FilterClips();
            }
        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            AdjustControls();
            try
            {
                ChangePlayerLocked();
                TaskbarThumbnail();

                ThumbnailToolBarManager tb = TaskbarManager.Instance.ThumbnailToolBars;

                ThumbnailToolBarButton nextclipbtn = new ThumbnailToolBarButton(Properties.Resources.next_clip, "Next Clip");
                nextclipbtn.Click += new EventHandler<ThumbnailButtonClickedEventArgs>(nextclipButton_click);
                ThumbnailToolBarButton nextmodelbtn = new ThumbnailToolBarButton(Properties.Resources.next_model, "Next Model");
                nextmodelbtn.Click += new EventHandler<ThumbnailButtonClickedEventArgs>(nextclipModel_click);
                ThumbnailToolBarButton[] buttons = new ThumbnailToolBarButton[2]{
                        nextclipbtn,
                        nextmodelbtn
                    };

                tb.AddButtons(this.Handle, buttons);


            }
            catch (Exception)
            { }
        }

        private void TaskbarThumbnail()
        {
            return;
            //TabbedThumbnailManager th = TaskbarManager.Instance.TabbedThumbnail;
            //var r = items.Where(c => c.Text == nowPlayingTag).FirstOrDefault();
            //if (r != null && r.Index >= 0)
            //{
            //    var res = DwmInvalidateIconicBitmaps(this.Handle);
            //    thumbnailclip = new Rectangle(listModelsNew.Location.X + listModelsNew.Items[r.Index].Bounds.X,
            //        listModelsNew.Location.Y + listModelsNew.Items[r.Index].Bounds.Y,
            //        listModelsNew.Items[r.Index].Bounds.Width,
            //        listModelsNew.Items[r.Index].Bounds.Height);
            //}
            //if (thumbnailclip != null)
            //    th.SetThumbnailClip(this.Handle, thumbnailclip);

        }

        private void nextclipButton_click(object? sender, ThumbnailButtonClickedEventArgs e)
        {
            this.BeginInvoke((Action)(() =>
                GetNextClip(useQueue: false)));
        }

        private void nextclipModel_click(object? sender, ThumbnailButtonClickedEventArgs e)
        {
            GetNextCard();
        }

        private void lblNowPlaying_TextChanged(object sender, EventArgs e)
        {
            UpdateWindowTitle();
        }

        private void UpdateWindowTitle()
        {
            string title = lblNowPlaying.Text.Length > 14
                ? lblNowPlaying.Text.Substring(13) : "iStripper QuickPlayer";
            Text = playerlocked ? "🔒 " + title : title;
        }

        private void lblNowPlaying_MouseEnter(object sender, EventArgs e)
        {
            this.Cursor = Cursors.Hand;
        }

        private void lblNowPlaying_MouseLeave(object sender, EventArgs e)
        {
            this.Cursor = Cursors.Arrow;
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                if (Properties.Settings.Default.MinimizeToTray)
                {
                    Hide();
                    notifyIcon1.Visible = true;
                }
            }
            else if (!layoutSuspendedForMinimize &&
                spaceRightOfListModel != 0 &&
                ClientSize != lastAdjustedClientSize)
            {
                AdjustControls();
                lastAdjustedClientSize = ClientSize;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            if (Visible && WindowState == FormWindowState.Minimized &&
                !layoutSuspendedForMinimize)
            {
                layoutSuspendedForMinimize = true;
                dpiBeforeMinimize = DeviceDpi;
                screenBeforeMinimize = Screen.FromHandle(Handle).DeviceName;
                SuspendLayout();
            }

            base.OnResize(e);

            if (!layoutSuspendedForMinimize ||
                WindowState == FormWindowState.Minimized)
                return;

            bool layoutRequired = RestoreRequiresLayout(
                lastAdjustedClientSize, ClientSize,
                dpiBeforeMinimize, DeviceDpi,
                screenBeforeMinimize,
                Screen.FromHandle(Handle).DeviceName);
            layoutSuspendedForMinimize = false;
            ResumeLayout(layoutRequired);
            if (layoutRequired)
            {
                AdjustControls();
                lastAdjustedClientSize = ClientSize;
            }
        }

        internal static bool RestoreRequiresLayout(
            Size previousSize, Size currentSize,
            int previousDpi, int currentDpi,
            string previousScreen, string currentScreen) =>
            previousSize != currentSize ||
            previousDpi != currentDpi ||
            !string.Equals(previousScreen, currentScreen,
                StringComparison.Ordinal);

        private async void cmdWallpaper_click(object sender, EventArgs e)
        {
            lastWallpaperShortTag = "";
            await ChangeWallpaper();
        }

        private string lastWallpaperShortTag = "";
        private async System.Threading.Tasks.Task ChangeWallpaper(
            bool NotFromCheck = true, bool onlyWhenCardChanges = false)
        {
            System.Diagnostics.Debug.WriteLine("ChangeWallpaper called with nowPlayingTagShort=" + nowPlayingTagShort + ", lbl=" + lblNowPlaying.Text);
            if (string.IsNullOrEmpty(nowPlayingTagShort) ||
                string.IsNullOrEmpty(lblNowPlaying.Text))
                return;
            if (onlyWhenCardChanges && string.Equals(lastWallpaperShortTag,
                    nowPlayingTagShort, StringComparison.OrdinalIgnoreCase))
                return;
            lastWallpaperShortTag = nowPlayingTagShort;
            //check that this wallpaper really matches filters
            ModelCard? model = Datastore.findCardByTag(nowPlayingTagShort.Split("\\")[0]);
            if (model == null) return;
            ListViewItem? res = items.FirstOrDefault(x =>
                x.Text == model.modelName + "\r\n" + model.outfit);

            //does the new clip match the clip filter?
            ModelClip? res2 = null;
            if (res != null)
            {
                var clipstest = FilterClipList(model.clips);
                res2 = clipstest.Where(c => c.clipNumber == nowPlayingClipNumber).FirstOrDefault();
            }

            string modelname = GetModelsString(model);

            ToolStripMenuItem[] monitorItems = wallpaperToolStripMenuItem
                .DropDownItems.OfType<ToolStripMenuItem>()
                .Where(item => item.Tag is uint).ToArray();
            bool canChange = res2 != null &&
                (NotFromCheck || Properties.Settings.Default.AutoWallpaper);
            CardPhotos? photos = null;
            if (canChange && monitorItems.Any(item => item.Checked))
                photos = await LoadPhotosForCard(nowPlayingTagShort);
            string? wallpaperSource = photos == null
                ? null
                : await Task.Run(photos.getRandomWidescreenURL);

            foreach (ToolStripMenuItem item in monitorItems)
            {
                uint monitorNumber = (uint)item.Tag!;
                if (item.Checked)
                {
                    if (canChange)
                        await Wallpaper.ChangeWallpaper(monitorNumber,
                            wallpaperSource, modelname,
                            model.outfit);
                }
                else
                {
                    await Task.Run(() =>
                        Wallpaper.RestoreWallpaperByID(monitorNumber));
                }
            }
        }

        private string GetModelsString(ModelCard card)
        {
            if (card.modelName == null) return "";
            return card.modelName;
        }
        public static string PascalCase(string word)
        {
            return string.Join(" ", word.Split('_')
                         .Select(w => w.Trim())
                         .Where(w => w.Length > 0)
                         .Select(w => w.Substring(0, 1).ToUpper() + w.Substring(1).ToLower()));
        }

        private async void WallpaperMonitor_CheckedChanged(object? sender, EventArgs e)
        {
            string m = "";
            foreach (var item in wallpaperToolStripMenuItem.DropDownItems)
            {
                if (item is ToolStripMenuItem menuItem &&
                    menuItem.Checked && menuItem.Tag is uint monitorNumber)
                {
                    if (m == "")
                        m += (monitorNumber + 1).ToString();
                    else
                        m += "," + (monitorNumber + 1).ToString();
                }
            }
            Properties.Settings.Default.WallpaperMonitors = m;
            if (nowPlayingTag != "") await ChangeWallpaper(false);
        }

        private void automaticWallpaperToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.AutoWallpaper = automaticWallpaperToolStripMenuItem.Checked;
            if (!automaticWallpaperToolStripMenuItem.Checked)
                Wallpaper.ReleaseBlurCache();
        }

        private void trackbarWallpaperBrightness_ValueChanged(object? sender, EventArgs e)
        {
            trackbarWallpaperBrightness.MouseUp += trackbarWallpaperBrightness_MouseUp;
            trackbarWallpaperBrightness.ValueChanged -= trackbarWallpaperBrightness_ValueChanged;
        }

        private void trackbarWallpaperBrightness_MouseUp(object? sender, EventArgs e)
        {
            trackbarWallpaperBrightness.MouseUp -= trackbarWallpaperBrightness_MouseUp;
            trackbarWallpaperBrightness.ValueChanged += trackbarWallpaperBrightness_ValueChanged;

            Properties.Settings.Default.WallpaperBrightness = trackbarWallpaperBrightness.Value;
            this.BeginInvoke((Action)(() => Wallpaper.RedrawImage()));
        }

        private async void showTextToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.WallpaperDetails = showTextToolStripMenuItem.Checked;
            lastWallpaperShortTag = "";
            await ChangeWallpaper();
        }

        private void trackBarWallpaperTextSize_ValueChanged(
            object? sender, EventArgs e)
        {
            Properties.Settings.Default.WallpaperTextSize =
                trackBarWallpaperTextSize.Value;
            if (Control.MouseButtons != MouseButtons.Left)
            {
                if (Properties.Settings.Default.WallpaperDetails)
                    BeginInvoke((Action)Wallpaper.RedrawImage);
                return;
            }
            trackBarWallpaperTextSize.MouseUp +=
                trackBarWallpaperTextSize_MouseUp;
            trackBarWallpaperTextSize.ValueChanged -=
                trackBarWallpaperTextSize_ValueChanged;
        }

        private void trackBarWallpaperTextSize_MouseUp(
            object? sender, EventArgs e)
        {
            trackBarWallpaperTextSize.MouseUp -=
                trackBarWallpaperTextSize_MouseUp;
            trackBarWallpaperTextSize.ValueChanged +=
                trackBarWallpaperTextSize_ValueChanged;
            if (Properties.Settings.Default.WallpaperDetails)
                BeginInvoke((Action)Wallpaper.RedrawImage);
        }

        private void trackBarWallpaperLabelOpacity_ValueChanged(
            object? sender, EventArgs e)
        {
            Properties.Settings.Default.WallpaperLabelOpacity =
                trackBarWallpaperLabelOpacity.Value;
            if (Control.MouseButtons != MouseButtons.Left)
            {
                if (Properties.Settings.Default.WallpaperDetails)
                    BeginInvoke((Action)Wallpaper.RedrawImage);
                return;
            }
            trackBarWallpaperLabelOpacity.MouseUp +=
                trackBarWallpaperLabelOpacity_MouseUp;
            trackBarWallpaperLabelOpacity.ValueChanged -=
                trackBarWallpaperLabelOpacity_ValueChanged;
        }

        private void trackBarWallpaperLabelOpacity_MouseUp(
            object? sender, EventArgs e)
        {
            trackBarWallpaperLabelOpacity.MouseUp -=
                trackBarWallpaperLabelOpacity_MouseUp;
            trackBarWallpaperLabelOpacity.ValueChanged +=
                trackBarWallpaperLabelOpacity_ValueChanged;
            if (Properties.Settings.Default.WallpaperDetails)
                BeginInvoke((Action)Wallpaper.RedrawImage);
        }

        private void showKittyToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ShowKitty = showKittyToolStripMenuItem.Checked;
            this.BeginInvoke((Action)(() => { PopulateModelListview(); }));
        }

        private void cardScaleSeekBar_Scroll(object? sender, EventArgs e)
        {
            ApplyCardScale();
        }

        private void ApplyCardScale(int? dpi = null)
        {
            cardScale = cardScaleSeekBar.Value / 20f;
            cardScaleLabel.Text = $"Card size: {cardScale:P0}";
            if (listModelsNew.Items.Count > 0)
            {
                if (cardRenderer != null)
                    cardRenderer.SetCardScale(cardScale);
                listModelsNew.ThumbnailSize = CardThumbnailSize(
                    dpi ?? listModelsNew.DeviceDpi, cardScale);
                listModelsNew.Invalidate();
            }
            Properties.Settings.Default.CardScale = cardScale;
            PositionCardScaleControls();
        }

        private void listModelsNew_ItemDoubleClick(object sender, ItemClickEventArgs e)
        {
            FilterClips();
            GetNextClip(Datastore.findCardByTag(e.Item.Tag?.ToString() ?? ""));
        }

        private void listModelsNew_ItemClick(object sender, ItemClickEventArgs e)
        {
            ImageListViewItem clickCard = e.Item;
            int idx = clickCard.Index;

            if (!cardRenderer.TryGetItemBounds(idx, out var cardRect))
                return;

            if (!cardRenderer.TryGetStarItemBounds(idx, out var starRect))
                return;

            // Only handle clicks actually on the stars area
            if (!starRect.Contains(e.Location))
                return;

            // X position inside the stars rect (0..Width)
            double x = e.X - starRect.Left;

            // Convert to half-star steps: 5 stars => 10 half-stars
            double halfStars = Math.Round((x / starRect.Width) * 10.0, MidpointRounding.AwayFromZero);

            // Clamp to [0..10]
            halfStars = Math.Max(0, Math.Min(10, halfStars));

            decimal rating = (decimal)(halfStars);   // 0.0, 0.5, 1.0, ... 5.0

            // rating is the nearest 0.5-star value
            Debug.WriteLine("rating: " + rating.ToString());
            if (myData == null || clickCard == null) return;
            if (myData.GetCardRating(clickCard.Tag.ToString()) == rating) return;
            myData.AddCardRating(clickCard.Tag.ToString(), rating);
            RecalculateCardOverlay(
                clickCard, menuShowRatingsStars.Checked);
        }

        private void listModelsNew_MouseDown(object sender, MouseEventArgs e)
        {
            RememberPlayQueueCardDrag(e);
            if (e.Button == MouseButtons.Right)
            {
                ImageListView.HitInfo hit;
                listModelsNew.HitTest(e.Location, out hit);
                if (hit.ItemHit) mousedownCard = listModelsNew.Items[hit.ItemIndex];
                else mousedownCard = null;
                if (mousedownCard != null)
                {
                    menuCardList.Show();
                }
            }
        }

        private void trackBarZoomOnHover_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ZoomOnHover = trackBarZoomOnHover.Value;
            if (cardRenderer != null)
                cardRenderer.mZoomRatio = (float)trackBarZoomOnHover.Value;
        }

        private void listModelsNew_ItemHover(object sender, ItemHoverEventArgs e)
        {
            if (directCompositionCardOverlays != null)
            {
                directCompositionCardOverlays.SetHoveredItem(e.Item?.Index);
                return;
            }
            if (cardRenderer?.mZoomRatio > 0.0f)
                listModelsNew.Refresh();
        }

        private void splitContainer1_SplitterMoved(object sender, SplitterEventArgs e)
        {
            AdjustControls();
        }

        private void ConfigureResponsiveLayouts()
        {
            FlowLayoutPanel searchRow = CreateFlowRow("librarySearchRow");
            AddFlowControls(searchRow,
                lblModelsLoaded, label2, txtSearch, cmdClearSearch);

            FlowLayoutPanel sortRow = CreateFlowRow("librarySortRow");
            cmbSortBy.Width = cmbSortBy.Items.Cast<string>()
                .Max(item => TextRenderer.MeasureText(
                    item, cmbSortBy.Font).Width) +
                SystemInformation.VerticalScrollBarWidth + 8;
            cardScaleSeekBar.Size = new Size(240, 40);
            cmdFilter.AutoSize = true;
            cmdFilter.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            cmdFilter.Padding = new Padding(6, 2, 6, 3);
            AddFlowControls(sortRow,
                label1, cmbSortBy, cmbSortDirection, chkFavourite,
                cmdFilter, cmbFilter, cardScaleLabel, cardScaleSeekBar);

            TableLayoutPanel libraryLayout = new()
            {
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                Name = "libraryLayout",
                RowCount = 3
            };
            libraryLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            libraryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            libraryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            libraryLayout.RowStyles.Add(
                new RowStyle(SizeType.Percent, 100));
            listModelsNew.Dock = DockStyle.Fill;
            libraryLayout.Controls.Add(searchRow, 0, 0);
            libraryLayout.Controls.Add(sortRow, 0, 1);
            libraryLayout.Controls.Add(listModelsNew, 0, 2);
            splitContainer1.Panel1.Controls.Clear();
            splitContainer1.Panel1.Controls.Add(libraryLayout);

            TableLayoutPanel nowPlayingRow = new()
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Name = "nowPlayingRow",
                Padding = new Padding(4),
                RowCount = 1
            };
            nowPlayingRow.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            nowPlayingRow.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            FlowLayoutPanel nowPlayingActions =
                CreateFlowRow("nowPlayingActions");
            nowPlayingActions.Padding = Padding.Empty;
            nowPlayingActions.WrapContents = false;
            AddFlowControls(nowPlayingActions,
                cmdWallpaper, cmdNextClip, cmdShowModel,
                dressingRoomsTestButton, panicResumeButton);
            lblNowPlaying.AutoSize = false;
            lblNowPlaying.AutoEllipsis = true;
            lblNowPlaying.Dock = DockStyle.Fill;
            lblNowPlaying.TextAlign = ContentAlignment.MiddleLeft;
            lblNowPlaying.Margin = new Padding(4);
            nowPlayingActions.Margin = Padding.Empty;
            nowPlayingRow.Controls.Add(lblNowPlaying, 0, 0);
            nowPlayingRow.Controls.Add(nowPlayingActions, 1, 0);

            FlowLayoutPanel playbackRow = CreateFlowRow("playbackRow");
            cmdPlayPause.AutoSize = true;
            cmdPlayPause.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            cmdPlayPause.Padding = new Padding(8, 2, 8, 3);
            AddFlowControls(playbackRow,
                cmdRewind, cmdPlayPause, cmdFastForward,
                lblPlaybackSpeed, cmbPlaybackSpeed,
                customAlphaThresholdLabel, customAlphaThresholdInput,
                customEdgeChokeLabel, customEdgeChokeInput);

            TableLayoutPanel timelineRow = new()
            {
                AutoSize = true,
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Name = "timelineRow",
                RowCount = 1
            };
            timelineRow.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            timelineRow.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            lblPlaybackTime.AutoSize = false;
            lblPlaybackTime.SetDisplayText(lblPlaybackTime.Text);
            lblPlaybackTime.Text = "";
            lblPlaybackTime.TextAlign = ContentAlignment.MiddleLeft;
            lblPlaybackTime.Dock = DockStyle.Fill;
            trkPlaybackPosition.Dock = DockStyle.Fill;
            timelineRow.Controls.Add(lblPlaybackTime, 0, 0);
            timelineRow.Controls.Add(trkPlaybackPosition, 1, 0);

            FlowLayoutPanel clipFilterRow =
                CreateFlowRow("clipFilterRow");
            AddFlowControls(clipFilterRow,
                chkPublic, chkNoNudity, chkTopless, chkNudity,
                chkFullNudity, chkXXX, lblFilterClip, txtClipType,
                chkDemo);

            TableLayoutPanel clipHeader = new()
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Dock = DockStyle.Top,
                Name = "clipHeaderLayout",
                RowCount = 5
            };
            clipHeader.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 5; row++)
                clipHeader.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            clipHeader.Controls.Add(nowPlayingRow, 0, 0);
            clipHeader.Controls.Add(playbackRow, 0, 1);
            clipHeader.Controls.Add(timelineRow, 0, 2);
            clipHeader.Controls.Add(clipFilterRow, 0, 3);
            lblClipListTitle.Height = lblClipListTitle.Font.Height + 12;
            clipHeader.Controls.Add(lblClipListTitle, 0, 4);
            panelClip.Controls.Clear();
            panelClip.AutoSize = true;
            panelClip.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panelClip.Dock = DockStyle.Fill;
            panelClip.Controls.Add(clipHeader);

            int detailsHeight = panelModelDetails.Height +
                txtDescription.Font.Height * 2;
            TableLayoutPanel detailsLayout = new()
            {
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                Name = "detailsLayout",
                RowCount = 3
            };
            detailsLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            detailsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            detailsLayout.RowStyles.Add(
                new RowStyle(SizeType.Percent, 100));
            detailsLayout.RowStyles.Add(
                new RowStyle(SizeType.Absolute, detailsHeight));

            clipListHost = new Panel
            {
                Dock = DockStyle.Fill,
                Name = "clipListHost"
            };
            listClips.Dock = DockStyle.Fill;
            System.Drawing.Size photosSize =
                cmdPhotos.GetPreferredSize(System.Drawing.Size.Empty);
            string photosText = cmdPhotos.Text;
            cmdPhotos.AutoSize = false;
            cmdPhotos.Size = new System.Drawing.Size(
                photosSize.Width, listClips.Font.Height + 5);
            cmdPhotos.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cmdPhotos.AccessibleName = photosText;
            cmdPhotos.Text = "";
            cmdPhotos.Paint += (_, e) => TextRenderer.DrawText(
                e.Graphics, photosText, cmdPhotos.Font,
                cmdPhotos.ClientRectangle, cmdPhotos.ForeColor,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix);
            clipListHost.Controls.Add(listClips);
            clipListHost.Controls.Add(cmdPhotos);
            clipListHost.Layout += (_, _) => PositionPhotosButton();
            listClips.FontChanged += (_, _) => PositionPhotosButton();
            listClips.HandleCreated += (_, _) =>
                listClips.BeginInvoke((Action)PositionPhotosButton);
            PositionPhotosButton();

            FlowLayoutPanel modelMetricsRow =
                CreateFlowRow("modelMetricsRow");
            modelMetricsRow.Padding = Padding.Empty;
            AddFlowControls(modelMetricsRow,
                lblAge, lblStats, lblCollection,
                lblRatingScore, lblResolution);

            TableLayoutPanel userTagsRow = new()
            {
                AutoSize = true,
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Name = "userTagsRow",
                RowCount = 1
            };
            userTagsRow.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            userTagsRow.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            lblUserTags.Anchor = AnchorStyles.Left;
            txtUserTags.Dock = DockStyle.Fill;
            userTagsRow.Controls.Add(lblUserTags, 0, 0);
            userTagsRow.Controls.Add(txtUserTags, 1, 0);

            TableLayoutPanel modelDetailsLayout = new()
            {
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                Name = "modelDetailsLayout",
                RowCount = 4
            };
            modelDetailsLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100));
            modelDetailsLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));
            modelDetailsLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));
            modelDetailsLayout.RowStyles.Add(
                new RowStyle(SizeType.AutoSize));
            modelDetailsLayout.RowStyles.Add(
                new RowStyle(SizeType.Percent, 100));
            lblTags.AutoSize = true;
            lblTags.Dock = DockStyle.Fill;
            lblTags.MaximumSize = System.Drawing.Size.Empty;
            txtDescription.Dock = DockStyle.Fill;
            modelDetailsLayout.Controls.Add(modelMetricsRow, 0, 0);
            modelDetailsLayout.Controls.Add(lblTags, 0, 1);
            modelDetailsLayout.Controls.Add(userTagsRow, 0, 2);
            modelDetailsLayout.Controls.Add(txtDescription, 0, 3);
            panelModelDetails.Controls.Clear();
            panelModelDetails.Dock = DockStyle.Fill;
            panelModelDetails.Controls.Add(modelDetailsLayout);
            detailsLayout.Controls.Add(panelClip, 0, 0);
            detailsLayout.Controls.Add(clipListHost, 0, 1);
            detailsLayout.Controls.Add(panelModelDetails, 0, 2);
            splitContainer1.Panel2.Controls.Clear();
            splitContainer1.Panel2.Controls.Add(detailsLayout);
        }

        private void PositionPhotosButton()
        {
            if (clipListHost == null || cmdPhotos == null)
                return;
            int headerHeight = listClips.Font.Height + 7;
            if (listClips.IsHandleCreated && listClips.Items.Count > 0)
            {
                try
                {
                    headerHeight = listClips.GetItemRect(0).Top;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // HandleCreated can run before the native ListView has
                    // materialized its managed items. The deferred callback
                    // will measure the real header once creation completes.
                }
            }
            cmdPhotos.Height = Math.Max(1, headerHeight);
            cmdPhotos.Location = new Point(
                Math.Max(0, clipListHost.ClientSize.Width -
                    cmdPhotos.Width), 0);
            cmdPhotos.BringToFront();
        }

        private static FlowLayoutPanel CreateFlowRow(string name) => new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Name = name,
            Padding = new Padding(4),
            WrapContents = true
        };

        private static void AddFlowControls(
            FlowLayoutPanel row, params Control[] controls)
        {
            foreach (Control control in controls)
            {
                control.Margin = control is Label or CheckBox
                    ? new Padding(4, 8, 4, 4)
                    : new Padding(4);
                row.Controls.Add(control);
            }
        }

        internal static bool IsIncompleteCatalogueReload(
            int previousCards, int loadedCards, int physicalFolders) =>
            previousCards > 0 &&
            (loadedCards == 0 ||
                loadedCards * 2 < previousCards &&
                loadedCards * 2 < physicalFolders);

        private void AdjustControls()
        {
            if (spaceRightOfListModel == 0) return;
            splitContainer1.Panel1.PerformLayout();
            splitContainer1.Panel2.PerformLayout();
        }

        private void PositionCardScaleControls()
        {
            cardScaleSeekBar.Visible = true;
            cardScaleLabel.Visible = true;
            splitContainer1.Panel1.PerformLayout();
        }

        internal static Size CardThumbnailSize(int dpi, float scale) =>
            new(
                Math.Max(1, (int)Math.Round(
                    162 * scale * dpi / DesignDpi)),
                Math.Max(1, (int)Math.Round(
                    242 * scale * dpi / DesignDpi)));

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            if (!cardScaleInitialized)
                return;
            ApplyCardScale(e.DeviceDpiNew);
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)(() =>
                {
                    SetSkin();
                    ApplyCardScale();
                    UpdatePlayQueueHeader();
                    PositionPhotosButton();
                    AdjustControls();
                }));
        }

        private void listModelsNew_MouseLeave(object sender, EventArgs e)
        {
            if (menuCardList.Visible) return;
            if (cardRenderer == null) return;
            cardRenderer.MouseIsOnList = false;
            if (directCompositionCardOverlays != null)
                directCompositionCardOverlays.SetHoveredItem(null);
            else
                listModelsNew.Refresh();
        }

        private void listModelsNew_MouseEnter(object sender, EventArgs e)
        {
            if (cardRenderer != null)
                cardRenderer.MouseIsOnList = true;
        }

        private async void nameToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (currentMenuCard == null)
                return;
            ModelCard? card = Datastore.findCardByTag(
                currentMenuCard.Tag.ToString());
            if (string.IsNullOrEmpty(card?.modelName)) return;
            if (card.IsCustom)
                OpenBrowser(await ResolveIafdPerformerUrl(card.modelName));
            else
                OpenBrowser(@"https://www.istripper.com/models/" +
                    card.modelName.Replace(" ", "-"));
        }

        internal static string IafdSearchUrl(string modelName) =>
            "https://www.iafd.com/results.asp?searchtype=comprehensive&searchstring=" +
            WebUtility.UrlEncode(modelName);

        private static async Task<string> ResolveIafdPerformerUrl(
            string modelName)
        {
            string searchUrl = IafdSearchUrl(modelName);
            try
            {
                using CancellationTokenSource timeout = new(
                    TimeSpan.FromSeconds(8));
                using HttpRequestMessage request = new(
                    HttpMethod.Get, searchUrl);
                request.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 IStripperQuickPlayer/1.0");
                using HttpResponseMessage response = await client.SendAsync(
                    request, timeout.Token);
                response.EnsureSuccessStatusCode();
                string html = await response.Content.ReadAsStringAsync(
                    timeout.Token);
                return FindIafdPerformerUrl(html, modelName) ?? searchUrl;
            }
            catch { return searchUrl; }
        }

        internal static string? FindIafdPerformerUrl(
            string html, string modelName)
        {
            foreach (Match match in Regex.Matches(html,
                @"<a\b[^>]*\bhref\s*=\s*(?:""(?<url>[^""]*person\.rme[^""]*)""|'(?<url>[^']*person\.rme[^']*)')[^>]*>(?<text>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string text = WebUtility.HtmlDecode(Regex.Replace(
                    match.Groups["text"].Value, "<[^>]+>", " "));
                text = Regex.Replace(text, @"\s+", " ").Trim();
                if (!string.Equals(text, modelName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                string href = WebUtility.HtmlDecode(
                    match.Groups["url"].Value);
                if (Uri.TryCreate(new Uri("https://www.iafd.com/"), href,
                        out Uri? url) && url.Scheme == Uri.UriSchemeHttps &&
                    url.Host.Equals("www.iafd.com",
                        StringComparison.OrdinalIgnoreCase) &&
                    url.AbsolutePath.StartsWith("/person.rme/",
                        StringComparison.OrdinalIgnoreCase))
                    return url.AbsoluteUri;
            }
            return null;
        }

        internal static bool VerifyIafdLinkParsing() =>
            IafdSearchUrl("Natalie Mars").EndsWith("natalie+mars",
                StringComparison.OrdinalIgnoreCase) &&
            FindIafdPerformerUrl(
                "<a href='/person.rme/id=7e3ad313-5f7c-49ab-bfb7-b8ceca902abf'><b>Natalie Mars</b></a>",
                "Natalie Mars") ==
            "https://www.iafd.com/person.rme/id=7e3ad313-5f7c-49ab-bfb7-b8ceca902abf" &&
            FindIafdPerformerUrl(
                "<a href='https://evil.example/person.rme/id=x'>Natalie Mars</a>",
                "Natalie Mars") == null;

        private void outfitToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (currentMenuCard == null)
                return;
            ModelCard? card = Datastore.findCardByTag(
                currentMenuCard.Tag.ToString());
            if (!string.IsNullOrEmpty(card?.modelName))
                OpenBrowser(@"https://www.istripper.com/models/" +
                    (card.modelName + "/" + card.outfit + " " + card.name)
                        .Replace(" ", "-"));
        }

        private static void OpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url)
                    { UseShellExecute = true });
            }
            catch { }
        }

        private void lockPlayerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            setPlayerLocked();
        }

        private void enablePlaybackControlToolStripMenuItem_Click(
            object sender, EventArgs e)
        {
            bool enabled = enablePlaybackControlToolStripMenuItem.Checked;
            Properties.Settings.Default.EnablePlaybackControl = enabled;

            if (enabled)
            {
                playbackTimelineTimer.Start();
                if (playerLockBridgeLoaded && !playbackBridgeLoaded)
                    _ = Task.Run(ConfigurePlaybackFunctions);
            }
            else
            {
                playbackTimelineTimer.Stop();
                playbackBridgeLoaded = false;
                playbackMovieRegistered = false;
            }

            RefreshPlaybackControlVisibility();
        }

        private void alphaCheckpointCacheToolStripMenuItem_CheckedChanged(
            object? sender, EventArgs e)
        {
            bool enabled = alphaCheckpointCacheToolStripMenuItem.Checked;
            Properties.Settings.Default.EnableAlphaCheckpointCache = enabled;
            alphaCheckpointCacheSizeToolStripMenuItem.Enabled = enabled;
            if (!playbackBridgeLoaded)
                return;

            string animationPath = GetCurrentAnimationPath();
            _ = Task.Run(() =>
            {
                try
                {
                    CallPlaybackApi("IStripperSetAlphaCheckpointCacheKey",
                        enabled ? AlphaCheckpointClipKey(animationPath) : 0);
                }
                catch { }
            });
        }

        private void alphaCheckpointCacheSizeToolStripMenuItem_Click(
            object? sender, EventArgs e)
        {
            if (sender is not ToolStripMenuItem { Tag: int size })
                return;

            Properties.Settings.Default.AlphaCheckpointCacheSizeMB = size;
            foreach (ToolStripMenuItem item in
                alphaCheckpointCacheSizeToolStripMenuItem.DropDownItems)
            {
                item.Checked = item.Tag is int itemSize && itemSize == size;
            }
            if (!playbackBridgeLoaded)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    CallPlaybackApi(
                        "IStripperSetAlphaCheckpointCacheLimitBytes",
                        AlphaCheckpointCacheBytes(size));
                }
                catch { }
            });
        }

        private void setPlayerLocked()
        {
            Properties.Settings.Default.LockPlayer = lockPlayerToolStripMenuItem.Checked;
            playerlocked = lockPlayerToolStripMenuItem.Checked;
            ChangePlayerLocked();
            if (customPlayer != null)
                LockStateOverlay.ShowForWindow(customPlayer, playerlocked);
            else
                LockStateOverlay.ShowForProcess(vghd_procID, playerlocked);
        }

        private void doTaskbarPadlock()
        {
            if (!TaskbarManager.IsPlatformSupported)
            {
                uint WM_SETICON = 0x80u;
                IntPtr ICON_SMALL = new IntPtr(0);
                IntPtr ICON_BIG = new IntPtr(1);
                if (playerlocked)
                {
                    Utils.SendMessage(this.Handle, WM_SETICON, ICON_SMALL, Properties.Resources.locked.Handle);
                    Utils.SendMessage(this.Handle, WM_SETICON, ICON_BIG, Properties.Resources.locked.Handle);
                }
                else
                {
                    Utils.SendMessage(this.Handle, WM_SETICON, ICON_SMALL, Properties.Resources.df2284943cc77e7e1a5fa6a0da8ca265.Handle);
                    Utils.SendMessage(this.Handle, WM_SETICON, ICON_BIG, Properties.Resources.df2284943cc77e7e1a5fa6a0da8ca265.Handle);
                }
                return;
            }

            this.Icon =
                Properties.Resources.df2284943cc77e7e1a5fa6a0da8ca265;
            TaskbarManager.Instance.SetOverlayIcon(
                Handle,
                playerlocked ? Properties.Resources.padlock : null,
                playerlocked ? "iStripper is locked" : "");
        }

        private void ChangePlayerLocked()
        {
            customPlayer?.SetLocked(playerlocked);
            if (playerLockBridgeLoaded)
            {
                int result = SetVghdPlayerLocked();
                if (result < 0)
                {
                    SetPlaybackStatus(
                        $"Player lock update failed (0x{result:X8}).");
                }
            }

            if (!playerlocked)
            {
                notifyIcon1.Icon = Properties.Resources.df2284943cc77e7e1a5fa6a0da8ca265;
            }
            else
            {
                notifyIcon1.Icon = Properties.Resources.locked;
            }
            doTaskbarPadlock();
            UpdateWindowTitle();
        }

        private void ChangePlayerClickThrough()
        {
            customPlayer?.SetClickThroughLocked(
                Properties.Settings.Default.ClickThroughLockedPlayer);
            if (!playerLockBridgeLoaded)
                return;

            int result = SetVghdPlayerClickThrough(
                Properties.Settings.Default.ClickThroughLockedPlayer);
            if (result < 0)
            {
                SetPlaybackStatus(
                    $"Player click-through update failed (0x{result:X8}).");
            }
        }

        private void ChangePlayerWheelResize()
        {
            customPlayer?.SetWheelResize(
                Properties.Settings.Default.EnablePlayerWheelResize);
            if (!playerLockBridgeLoaded)
                return;

            int result = SetVghdPlayerWheelResize(
                Properties.Settings.Default.EnablePlayerWheelResize);
            if (result < 0)
            {
                SetPlaybackStatus(
                    $"Player wheel resize update failed (0x{result:X8}).");
            }
        }

        private void ChangePlayerWheelWhileLocked()
        {
            bool enabled =
                Properties.Settings.Default.AllowWheelWhileLocked;
            customPlayer?.SetAllowWheelWhileLocked(enabled);
            if (!playerLockBridgeLoaded)
                return;

            int result = SetVghdPlayerWheelResize(
                Properties.Settings.Default.EnablePlayerWheelResize);
            if (result < 0)
            {
                SetPlaybackStatus(
                    $"Locked player wheel update failed (0x{result:X8}).");
            }
        }

        private void notifyIcon1_DoubleClick(object sender, EventArgs e)
        {
            Show();
            this.WindowState = FormWindowState.Normal;
            notifyIcon1.Visible = false;
            doTaskbarPadlock();
        }

        private void minimizeToTrayToolStripMenuItem_CheckStateChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.MinimizeToTray = minimizeToTrayToolStripMenuItem.Checked;
        }

        private void trackBarBlur_ValueChanged(object? sender, EventArgs e)
        {
            trackBarBlur.MouseUp += trackBarBlur_MouseUp;
            trackBarBlur.KeyUp += trackBarBlur_MouseUp;
            trackBarBlur.ValueChanged -= trackBarBlur_ValueChanged;
        }

        private void trackBarBlur_MouseUp(object? sender, EventArgs e)
        {
            trackBarBlur.MouseUp -= trackBarBlur_MouseUp;
            trackBarBlur.KeyUp -= trackBarBlur_MouseUp;
            trackBarBlur.ValueChanged += trackBarBlur_ValueChanged;

            Properties.Settings.Default.BlurRadius = trackBarBlur.Value;
            Properties.Settings.Default.BlurWallpaper =
                trackBarBlur.Value > 0;
            if (trackBarBlur.Value == 0)
                Wallpaper.ReleaseBlurCache();
            this.BeginInvoke((Action)(() => Wallpaper.RedrawImage()));
        }

        private void hideDesktopIconsToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.HideDesktopIcons = hideDesktopIconsToolStripMenuItem.Checked;
            if (Utils.DesktopIconsVisible() == Properties.Settings.Default.HideDesktopIcons)
                Utils.ToggleDesktopIcons();
        }

        private void cmbSortDirection_SelectedIndexChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SortDirection = cmbSortDirection.Text;
            if (cardRenderer != null)
            {
                cardRenderer.sortBy = cmbSortBy.Text;
                PopulateModelListview();
            }
        }

        private void exportFiltersToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            var r = dlg.ShowDialog();
            if (r == DialogResult.OK)
            {
                foreach (var f in FilterSettingsList.filters)
                {
                    SerializeFilter(f.Value, Path.Join(dlg.SelectedPath, f.Key + ".flt"));
                }
            }
        }

        private void importFiltersToolStripMenuItem_Click(object sender, EventArgs e)
        {
            System.Windows.Forms.OpenFileDialog dlg = new System.Windows.Forms.OpenFileDialog();
            dlg.Filter = "*.flt|*.flt";
            dlg.Title = "Select filter file";
            var r = dlg.ShowDialog();
            if (r == DialogResult.OK)
            {
                var f = DeserializeFilter(dlg.FileName);
                if (f is null)
                {
                    MessageBox.Show("The selected filter file could not be read.");
                    return;
                }
                var fname = Path.GetFileNameWithoutExtension(dlg.FileName);
                if (!FilterSettingsList.filters.ContainsKey(fname))
                {
                    FilterSettingsList.filters.Add(fname, f);
                    FilterSettingsList.Persist();
                    this.BeginInvoke((Action)(() => { PopulateFilterList(); }));
                }
                else
                {
                    var d = MessageBox.Show("A filter with this name already exists - do you want to overwrite it?", "Overwrite Filter?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (d == DialogResult.Yes)
                    {
                        FilterSettingsList.Delete(fname);
                        FilterSettingsList.filters.Add(fname, f);
                        FilterSettingsList.Persist();
                        this.BeginInvoke((Action)(() => { PopulateFilterList(); }));
                        if (cmbFilter.Text == fname)
                        {
                            string? selectedFilter = cmbFilter.SelectedItem?.ToString();
                            if (selectedFilter != null)
                                filterSettings = FilterSettingsList.GetFilter(selectedFilter);
                            this.BeginInvoke((Action)(() => { PopulateModelListview(); }));
                        }
                    }
                }
            }
        }

        private void deleteFromDiskToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var r = MessageBox.Show("Really delete this card from local disk?\r\nIt is best if you Exit iStripper before deleting cards here\r\nUser rating/tags will be retained", "Delete card?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.No) return;
            string? t = mousedownCard?.Tag?.ToString();
            if (t == null) return;
            string cardfolder = CardFolders.findCardFolder(t);
            if (!string.IsNullOrEmpty(cardfolder)) Directory.Delete(cardfolder, true);
            var c = Datastore.modelcards.Where(x => x.name == t).FirstOrDefault();
            if (c != null)
            {
                Datastore.modelcards.Remove(c);
            }
            c = null;

            var cardMetaFolder = CardFolders.findCardMetaFolder(t);
            if (!string.IsNullOrEmpty(cardMetaFolder)) Directory.Delete(cardMetaFolder, true);
            ReloadModels();
            MessageBox.Show("You may need to restart iStripper and/or use the Synchronize With Server function in the app\r\nIf you want to download it again, you may need to delete it in iStripper too first.", "Local folders have been deleted", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void loadPlaylistToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "*.vpl|*.vpl";
            var r = openFileDialog.ShowDialog();
            if (r.Equals(DialogResult.OK))
            {
                if (!string.IsNullOrEmpty(openFileDialog.FileName))
                {
                    var cards = PlaylistLoader.LoadPlaylist(openFileDialog.FileName);
                    txtSearch.Text = String.Join(" or ", cards);
                    PopulateModelListview();
                }
            }
        }

        private void randomPlayOrderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.Randomize = randomPlayOrderToolStripMenuItem.Checked;
            RebuildAutomaticQueue();
        }

        private void darkModeToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.DarkMode = darkModeToolStripMenuItem.Checked;
            this.BeginInvoke((Action)(() => { SetSkin(); }));
        }
    }
}
