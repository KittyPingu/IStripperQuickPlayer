using DesktopWallpaper;
using EnumDescription;
using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using Manina.Windows.Forms;
using Manina.Windows.Forms.ImageListViewRenderers;
using MaterialSkin;
using MaterialSkin.Controls;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Taskbar;
using Nektra.Deviare2;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Microsoft.WindowsAPICodePack.Shell.PropertySystem.SystemProperties.System;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using Size = System.Drawing.Size;
using Task = System.Threading.Tasks.Task;
//using WebView2.DevTools.Dom;

namespace IStripperQuickPlayer
{
    public partial class Form1 : MaterialForm
    {

        [DllImport("dwmapi.dll")]
        static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private const int PlaybackBridgeVersion = 58;
        private const int PlaybackTimelineIntervalMilliseconds = 500;
        private const int PlaybackTransitionIntervalMilliseconds = 100;
        private const int PlaybackMovieDiscoveryRetryMilliseconds = 100;
        private const int PlaybackForcedReadyMilliseconds = 5_000;
        private const string LatestReleaseApiUrl =
            "https://api.github.com/repos/KittyPingu/IStripperQuickPlayer/releases/latest";

        private float cardScale = 1.0f;
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
        private bool fontInstalled = false;
        public CardRenderer cardRenderer = null!;
        internal FilterSettings filterSettings = new FilterSettings();
        static readonly HttpClient client = new HttpClient();
        private NumberStyles style = NumberStyles.AllowDecimalPoint;
        private CultureInfo culture = CultureInfo.CreateSpecificCulture(CultureInfo.CurrentCulture.Name);
        //global hotkeys
        private const int WmHotkey = 0x0312;
        private const int NextClipHotkeyId = 1;
        private const int NextCardHotkeyId = 2;
        private const int ToggleLockHotkeyId = 3;
        private const int PauseHotkeyId = 4;
        private const int RewindHotkeyId = 5;
        private const int FastForwardHotkeyId = 6;
        private const int RestartClipHotkeyId = 7;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModNoRepeat = 0x4000;
        //deviare2 hooking
        private NktSpyMgr _spyMgr = null!;
        private Int32 vghd_procID = 0;
        private readonly SemaphoreSlim playbackOperationLock = new(1, 1);
        private readonly object playbackApiLock = new();
        private readonly CancellationTokenSource playbackLifetime = new();
        private bool movieCaptureHookInstalled;
        private string playbackBridgePath = "";
        private int vghdInjectionInProgress;
        private volatile bool playerLockBridgeLoaded;
        private volatile bool playbackBridgeLoaded;
        private volatile bool playbackMovieRegistered;
        private volatile bool playbackFastDecodeEnabled;
        private volatile bool playbackSeekingSupported = true;
        private volatile bool playbackSeekReady;
        private volatile int playbackDecoderKind;
        private volatile bool playbackBusy;
        private volatile bool playbackControlsAvailableForAccount;
        private bool suppressPlaybackSpeedSelection;
        private double requestedPlaybackSpeed = 1.0;
        private readonly System.Windows.Forms.Timer playbackTimelineTimer =
            new() { Interval = PlaybackTimelineIntervalMilliseconds };
        private readonly ToolStripMenuItem updateToolStripMenuItem = new();
        private readonly ToolStripMenuItem alphaCheckpointCacheToolStripMenuItem =
            new();
        private readonly ToolStripMenuItem alphaCheckpointCacheSizeToolStripMenuItem =
            new("Alpha checkpoint cache size");
        private bool playbackTimelinePolling;
        private bool playbackTimelineDragging;
        private int playbackTimelineDurationMilliseconds;
        private int playbackLastKnownElapsedMilliseconds;
        private int playbackAlphaCheckpointBucket = -1;
        private string playbackTimelineAnimationPath = "";
        private string playbackCompletedAnimationPath = "";
        private string playbackRequestedAnimationPath = "";
        private DateTime playbackNextMovieDiscoveryAt = DateTime.MinValue;
        private DateTime playbackMovieCaptureFallbackAt = DateTime.MinValue;
        private DateTime playbackNextClipRetryAt = DateTime.MinValue;
        private DateTime playbackReplacementStableAt = DateTime.MinValue;
        private DateTime playbackSpeedReapplyUntil = DateTime.MinValue;
        private DateTime playbackLastProgressAt = DateTime.UtcNow;
        private bool formIsClosing;
        private ControlScrollListener? _processListViewScrollListener;
        private int spaceRightOfListModel = 0;
        private int spaceBelowClipList = 0;
        bool playerlocked;
        //private WebView2DevToolsContext devtoolsContext = null;

        private void actNextClip()
        {
            if (Properties.Settings.Default.NextClipEnabled) GetNextClip();
        }

        private void actNextCard()
        {
            if (Properties.Settings.Default.NextCardEnabled) this.BeginInvoke((Action)(() => GetNextCard()));
        }

        private void actToggleLock()
        {
            if (Properties.Settings.Default.ToggleLockEnabled) this.BeginInvoke((Action)(() => { lockPlayerToolStripMenuItem.Checked = !lockPlayerToolStripMenuItem.Checked; setPlayerLocked(); }));
        }

        private void actPause()
        {
            if (Properties.Settings.Default.PauseHotkeyEnabled)
                cmdPlayPause.PerformClick();
        }

        private void actRewind()
        {
            if (Properties.Settings.Default.RewindHotkeyEnabled)
                cmdRewind.PerformClick();
        }

        private void actFastForward()
        {
            if (Properties.Settings.Default.FastForwardHotkeyEnabled)
                cmdFastForward.PerformClick();
        }

        private async void actRestartClip()
        {
            if (Properties.Settings.Default.RestartClipHotkeyEnabled)
                await RunPlaybackOperationAsync(token => SeekAbsoluteAsync(0, token));
        }

        public Form1()
        {
#if DEBUG
            System.Diagnostics.Debug.Assert(!PlaybackReachedEnd(10_000, 135_000));
            System.Diagnostics.Debug.Assert(PlaybackReachedEnd(134_000, 135_000));
            DateTime stallCheck = DateTime.UtcNow;
            System.Diagnostics.Debug.Assert(LegacyPlaybackStalledNearEnd(
                96_000, 100_000, stallCheck.AddSeconds(-3), stallCheck, 3));
            System.Diagnostics.Debug.Assert(!LegacyPlaybackStalledNearEnd(
                96_000, 100_000, stallCheck.AddSeconds(-3), stallCheck, 4));
            System.Diagnostics.Debug.Assert(!SeekTargetReachesEnd(98_000, 100_000));
            System.Diagnostics.Debug.Assert(SeekTargetReachesEnd(99_000, 100_000));
            System.Diagnostics.Debug.Assert(HasPlatinumPlaybackEntitlement("platinum"));
            System.Diagnostics.Debug.Assert(HasPlatinumPlaybackEntitlement("doubleDiamond"));
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
            System.Diagnostics.Debug.Assert(TryParseHotKey(
                "Control+Alt+Left", out _, out _));
            System.Diagnostics.Debug.Assert(TryParseHotKey(
                "Control+Alt+Home", out _, out _));
            TextSearchDocument searchCheck = new(
                "Anna Delos c1001 Pool Side duo red table",
                "Anna Delos", "c1001", "Pool Side", "", "duo red table");
            System.Diagnostics.Debug.Assert(
                TextQuery.Parse("anna tag:duo !blue").Matches(searchCheck));
            System.Diagnostics.Debug.Assert(
                TextQuery.Parse("(beth OR model:anna) AND \"pool side\"")
                    .Matches(searchCheck));
            System.Diagnostics.Debug.Assert(
                !TextQuery.Parse("anna AND tag:blue").Matches(searchCheck));
            TextSearchDocument raeSearchCheck = new(
                "Asia Rae pole", "Asia Rae", "", "", "", "pole");
            TextSearchDocument kittySearchCheck = new(
                "Ashby Kitty", "Ashby Kitty", "", "", "", "");
            TextQuery unionSearchCheck =
                TextQuery.Parse("(rae AND pole) OR kitty");
            System.Diagnostics.Debug.Assert(unionSearchCheck.Matches(
                raeSearchCheck));
            System.Diagnostics.Debug.Assert(unionSearchCheck.Matches(
                kittySearchCheck));
#endif
            InitializeComponent();
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
            updateToolStripMenuItem.Text = "Check for Updates...";
            updateToolStripMenuItem.Click +=
                async (_, _) => await CheckForUpdatesAsync(true);
            fileToolStripMenuItem.DropDownItems.Insert(
                fileToolStripMenuItem.DropDownItems.Count - 1,
                updateToolStripMenuItem);
            RefreshPlaybackControlVisibility();
            playbackTimelineTimer.Tick += playbackTimelineTimer_Tick;
            if (Properties.Settings.Default.EnablePlaybackControl)
                playbackTimelineTimer.Start();
            SetSkin();
        }

        private void SetSkin()
        {
            var materialSkinManager = MaterialSkinManager.Instance;
            materialSkinManager.RemoveFormToManage(this);
            materialSkinManager.AddFormToManage(this);
            materialSkinManager.Theme = MaterialSkinManager.Themes.LIGHT;
            if (Properties.Settings.Default.DarkMode) materialSkinManager.Theme = MaterialSkinManager.Themes.DARK;
            materialSkinManager.ColorScheme = new ColorScheme(Primary.BlueGrey800, Primary.BlueGrey900, Primary.BlueGrey500, Accent.LightBlue200, TextShade.WHITE);
            if (cardRenderer != null) cardRenderer.SetColours();
        }

        private void cmdLoadModels_Click(object sender, EventArgs e)
        {
            this.Cursor = Cursors.WaitCursor;
            ReloadModels();
            this.Cursor = Cursors.Arrow;
        }

        private void ReloadModels()
        {
            RefreshPlaybackControlVisibility();
            ReloadStaticProperties();
            ModelsLstLoader lstLoader = new ModelsLstLoader();
            listModelsNew.Items.Clear();
            if (Datastore.modelcards != null)
                Datastore.modelcards.Clear();
            lstLoader.LoadModels();
            this.BeginInvoke((Action)(() => { PopulateModelListview(); }));
            PersistModels();
        }

        private void PersistModels()
        {
            string modelfilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "models.bin");
            string modelfolder = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer");
            if (!Directory.Exists(modelfolder))
                Directory.CreateDirectory(modelfolder);
            Serialize(Datastore.modelcards, modelfilepath);
        }

        private void RetrieveModels()
        {
            string modelfilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "models.bin");
            if (System.IO.File.Exists(modelfilepath))
            {
                List<ModelCard>? models = Deserialize(modelfilepath);
                if (models is null)
                    this.BeginInvoke((Action)(() => { ReloadModels(); }));
                else
                {
                    Datastore.modelcards = models;
                    this.BeginInvoke((Action)(() => { PopulateModelListview(); }));
                }
            }
            else
            {
                this.BeginInvoke((Action)(() => { ReloadModels(); }));
            }
        }

        ListViewItem[]? items; //stores the list of virtualized cards for modelList operations
        internal void PopulateModelListview()
        {
            //save the selected card, we can reselect it at the end if it's still valid
            string currentText = "";
            if (listModelsNew.SelectedItems.Count > 0)
            {
                currentText = listModelsNew.SelectedItems[0].Text;
            }
            listModelsNew.Items.Clear();
            if (Datastore.modelcards == null)
            {
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

            lblModelsLoaded.Text = "Cards Shown: " + listModelsNew.Items.Count + "/" + Datastore.modelcards.Where(c => c.clips != null && c.clips.Count > 0).Count();

            //set the selected card back to what we had selected at start of the function
            if (currentText != "")
            {
                listModelsNew.ClearSelection();
                int? index = items.ToList().FindIndex(x => x.Text == currentText);
                if (index != null && index > 0)
                {
                    listModelsNew.SelectWhere(x => x.Text == currentText);
                    listModelsNew.EnsureVisible((int)index);
                }
                listModelsNew.Refresh();
            }
            this.BeginInvoke((Action)(() => TaskbarThumbnail()));
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
                string.Join(' ', tags, userTags));
        }

        private void SetModelNewImageList()
        {
            var itemsNew = new ImageListViewItem[items.Count()];
            int idx = 0;

            cardRenderer.updating = true;
            listModelsNew.SuspendLayout();
            listModelsNew.Items.Clear();
            listModelsNew.ThumbnailSize = new Size((int)(cardScale * 162), (int)(242 * cardScale));
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
            listModelsNew.ResumeLayout();
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
            currentCards = currentCards.Where(c => ((c.modelAge >= filterSettings.minAge && c.modelAge <= filterSettings.maxAge) || c.modelAge == 0 || c.modelAge > 99)
                && (c.bust == null || (c.bust >= filterSettings.minBust && c.bust <= filterSettings.maxBust) || c.bust == 0 || c.bust > 99)
                && (c.rating - 5M >= filterSettings.minRating && c.rating - 5M <= filterSettings.maxRating) || c.rating == 0
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

            if (filterSettings.Normal && !filterSettings.Special)
                currentCards = currentCards.Where(c => c.exclusive != null && !(bool)c.exclusive).ToList();
            else if (filterSettings.Special && !filterSettings.Normal)
                currentCards = currentCards.Where(c => c.exclusive != null && (bool)c.exclusive).ToList();

            currentCards = currentCards.Where(c => enabledcollections.Contains(c.collection)||c.collection == Enums.CollectionType.Undefined).ToList();

            try
            {
                if (Properties.Settings.Default.ShowKitty && (txtSearch.Text == "" || txtSearch.Text.ToLower().Contains("kitty")))
                    currentCards.Add(Datastore.modelcards.Where(c => c.name == "f9998").First());
            }
            catch (Exception) { }

            return currentCards;
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
            Persistence.Save(mdatafilepath, myData ?? new MyData());
        }

        internal MyData RetrieveMyData()
        {

            try
            {
                string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "mydata.bin");
                if (!System.IO.File.Exists(mdatafilepath)) return new MyData();
                return Persistence.Load<MyData>(mdatafilepath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error reading MyData file\r\n" + ex.Message);
                return new MyData();
            }
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
            clipListTag = cardTag;
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
            txtDescription.Text = card.description;
            lblAge.Text = "Age: " + card.modelAge;
            lblStats.Text = "Stats: " + card.bust + "/" + card.waist + "/" + card.hips;
            lblRatingScore.Text = "Rating: " + (Convert.ToDecimal(card.rating) - 5m).ToString();
            lblCollection.Text = "CardType: " + card.collection.GetDescription();
            lblResolution.Text = "Res: " + card.resolution.GetDescription();
            lblTags.Text = "Tags: " + String.Join(",", card.tags);

        }

        private string lastchosen = "";
        private void listClips_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listClips.SelectedItems.Count == 0) return;
            if (lastchosen == listClips.SelectedItems[0].SubItems[1].Text || clickingNowPlaying)
            {
                clickingNowPlaying = false;
                return;
            }
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", true);

            string r = listClips.SelectedItems[0].SubItems[1].Text;
            string p = r.Split("_")[0];
            string full = p + "\\" + r;
            if (key != null)
            {
                var currentkey = key.GetValue("CurrentAnim");
                string? currentkeystring = "";
                if (currentkey != null) currentkeystring = currentkey.ToString();
                if (currentkey != null && currentkeystring != full)
                {
                    BeginAnimationReplacement(full);
                    key.SetValue("ForceAnim", full);
                    key.Close();
                }
            }
            lastchosen = listClips.SelectedItems[0].SubItems[1].Text;
        }


        private async void Form1_Load(object sender, EventArgs e)
        {
            if (Properties.Settings.Default.UpdateSettings)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpdateSettings = false;
                Properties.Settings.Default.Save();
            }
            spaceBelowClipList = this.Height - listClips.Bottom;
            spaceRightOfListModel = this.Width - listModelsNew.Right;
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
            //check if we Segoe Fluent Icons font - this comes with windows 11
            var fontsCollection = new InstalledFontCollection();
            foreach (var fontFamily in fontsCollection.Families)
            {
                if (fontFamily.Name == "Segoe Fluent Icons")
                    fontInstalled = true;
            }
            Utils.DefaultIconsVisible = Utils.DesktopIconsVisible();
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
            includeDescriptionInSearchToolStripMenuItem.Checked = Properties.Settings.Default.ShowDescInSearch;
            includeShowTitleInSearchToolStripMenuItem.Checked = Properties.Settings.Default.ShowOutfitInSearch;
            trackBarCardScale.Value = (decimal)(Properties.Settings.Default.CardScale);
            trackBarZoomOnHover.Value = (decimal)(Properties.Settings.Default.ZoomOnHover);
            blurImageToolStripMenuItem.Checked = Properties.Settings.Default.BlurWallpaper;
            randomPlayOrderToolStripMenuItem.Checked = Properties.Settings.Default.Randomize;
            if (Properties.Settings.Default.MinSizeMB != 0)
            {
                numMinSizeMB.Value = Properties.Settings.Default.MinSizeMB;
            }
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
                    if (monitorsChecked.Contains((i + 1).ToString())) newitem.Checked = true;
                    this.wallpaperToolStripMenuItem.DropDownItems.Add(newitem);
                    newitem.CheckedChanged += WallpaperMonitor_CheckedChanged;
                }
            }
            catch { }
            trackbarWallpaperBrightness.Value = Properties.Settings.Default.WallpaperBrightness;
            trackBarBlur.Value = Properties.Settings.Default.BlurRadius;
            automaticWallpaperToolStripMenuItem.Checked = Properties.Settings.Default.AutoWallpaper;
            showTextToolStripMenuItem.Checked = Properties.Settings.Default.WallpaperDetails;
            showKittyToolStripMenuItem.Checked = Properties.Settings.Default.ShowKitty;
            minimizeToTrayToolStripMenuItem.Checked = Properties.Settings.Default.MinimizeToTray;
            hideDesktopIconsToolStripMenuItem.Checked = Properties.Settings.Default.HideDesktopIcons;
            darkModeToolStripMenuItem.Checked = Properties.Settings.Default.DarkMode;
            SetSkin();
            myData = RetrieveMyData();
            FilterSettingsList.Load();
            PopulateFilterList();
            if (cardRenderer == null)
            {
                cardRenderer = new CardRenderer(myData, cmbSortBy.Text, cardScale, culture, fontInstalled, style);
                cardRenderer.mZoomRatio = (float)Properties.Settings.Default.ZoomOnHover;
                listModelsNew.SetRenderer(cardRenderer);
                cardRenderer.SetColours();
            }

            if (FilterSettingsList.filters.ContainsKey("Default"))
                cmbFilter.SelectedItem = "Default";
            //string REG_KEY = @"HKEY_CURRENT_USER\Software\Totem\vghd\parameters";
            //watcher = new RegistryWatcher(new Tuple<string, string>(REG_KEY, "CurrentAnim"));
            //watcher.RegistryChange += RegistryChanged;
            _processListViewScrollListener = new ControlScrollListener(listModelsNew);
            _processListViewScrollListener.ControlScrolled += ProcessListViewScrollListener_ControlScrolled;
            clickingNowPlaying = true;

            RetrieveModels();
            GetNowPlaying();
            clickingNowPlaying = false;
            SetupKeyHooks();
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

        private void ProcessListViewScrollListener_ControlScrolled(object sender, EventArgs e)
        {
            listModelsNew.Refresh();
            //this.BeginInvoke((Action)(() => TaskbarThumbnail()));
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
        }

        internal static bool TryParseHotKey(string shortcut, out uint modifiers, out uint key)
        {
            modifiers = ModNoRepeat;
            key = 0;
            try
            {
                if (new KeysConverter().ConvertFromInvariantString(shortcut) is not Keys keys)
                    return false;

                if (keys.HasFlag(Keys.Alt)) modifiers |= ModAlt;
                if (keys.HasFlag(Keys.Control)) modifiers |= ModControl;
                if (keys.HasFlag(Keys.Shift)) modifiers |= ModShift;
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
                        actPause();
                        return;
                    case RewindHotkeyId:
                        actRewind();
                        return;
                    case FastForwardHotkeyId:
                        actFastForward();
                        return;
                    case RestartClipHotkeyId:
                        actRestartClip();
                        return;
                }
            }
            base.WndProc(ref message);
        }

        System.Threading.Timer? timerhook;
        NktProcess? tempProcess;
        private void SetupRegHooks()
        {
            try
            {
                _spyMgr = new NktSpyMgr();
                int result = _spyMgr.Initialize();
                if (result < 0)
                {
                    SetPlaybackStatus("Deviare could not initialise; playback controls are unavailable.");
                    return;
                }
                _spyMgr.OnFunctionCalled += new DNktSpyMgrEvents_OnFunctionCalledEventHandler(OnFunctionCalled);
                timerhook = new System.Threading.Timer(waitForIStripper, null, 100, 250);
            }
            catch (Exception exception)
            {
                SetPlaybackStatus("Playback hook setup failed: " + exception.Message);
            }
        }

        private bool InjectVGHDProcess()
        {

            NktProcessesEnum enumProcess = _spyMgr.Processes();
            tempProcess = enumProcess.First();
            while (tempProcess != null)
            {
                if (tempProcess.Name.Equals("vghd.exe", StringComparison.InvariantCultureIgnoreCase) && tempProcess.PlatformBits == 64)
                {
                    timerhook?.Dispose();
                    NktHook hook = _spyMgr.CreateHook("KernelBase.dll!RegSetValueExW", (int)(eNktHookFlags.flgAutoHookChildProcess | eNktHookFlags.flgOnlyPreCall));
                    hook.Hook(true);
                    hook.Attach(tempProcess, true);
                    vghd_procID = tempProcess.Id;
                    ConfigurePlaybackHooks(tempProcess);
                    //check that we havent played a new clip while we weren't hooked
                    clickingNowPlaying = true;
                    GetNowPlaying();
                    clickingNowPlaying = false;

                    //set up a watch for this process finishing so we can start looking for a new one again
                    return true;
                }
                tempProcess = enumProcess.Next();
            }
            return false;
        }

        private void waitForIStripper(object? state)
        {
            if (Interlocked.Exchange(ref vghdInjectionInProgress, 1) != 0)
            {
                return;
            }

            try
            {
                if (InjectVGHDProcess()) timerhook?.Dispose();
            }
            finally
            {
                Interlocked.Exchange(ref vghdInjectionInProgress, 0);
            }
        }

        private void ConfigurePlaybackHooks(NktProcess process)
        {
            playerLockBridgeLoaded = false;
            playbackBridgeLoaded = false;
            movieCaptureHookInstalled = false;
            playbackMovieRegistered = false;
            playbackSeekingSupported = true;
            playbackSeekReady = false;
            playbackDecoderKind = 0;
            playbackNextMovieDiscoveryAt = DateTime.MinValue;
            playbackFastDecodeEnabled = false;

            try
            {
                string localBridgePath = Path.Combine(AppContext.BaseDirectory,
                    "IStripperPlaybackBridge64.dll");
                playbackBridgePath = localBridgePath;
                if (!File.Exists(playbackBridgePath))
                {
                    SetPlaybackStatus("IStripperPlaybackBridge64.dll was not built or copied to the application folder.");
                    return;
                }

                // Function-pointer hooks keep the first bridge pinned in vghd.
                // Reuse it when QuickPlayer is restarted from another folder.
                try
                {
                    using Process target = Process.GetProcessById(process.Id);
                    playbackBridgePath = target.Modules
                            .Cast<ProcessModule>()
                            .FirstOrDefault(module => string.Equals(
                                module.ModuleName,
                                Path.GetFileName(localBridgePath),
                                StringComparison.OrdinalIgnoreCase))
                            ?.FileName ?? localBridgePath;
                }
                catch { }

                _spyMgr.LoadCustomDll(process, playbackBridgePath, true, true);
                object noParameters = null!;
                int bridgeVersion = _spyMgr.CallCustomApi(process, playbackBridgePath,
                    "IStripperPlaybackBridgeVersion", ref noParameters, true);
                if (bridgeVersion != PlaybackBridgeVersion)
                {
                    SetPlaybackStatus($"Unsupported playback bridge version {bridgeVersion}.");
                    return;
                }

                int resetResult = _spyMgr.CallCustomApi(process,
                    playbackBridgePath, "IStripperResetPlaybackSession",
                    ref noParameters, true);
                if (resetResult < 0)
                {
                    throw new COMException(
                        $"Playback session reset failed (0x{resetResult:X8}).",
                        resetResult);
                }

                playerLockBridgeLoaded = true;
                int lockResult = SetVghdPlayerLocked(playerlocked);
                if (lockResult < 0)
                {
                    throw new COMException(
                        $"Player lock setup failed (0x{lockResult:X8}).",
                        lockResult);
                }

                if (!Properties.Settings.Default.EnablePlaybackControl)
                {
                    return;
                }

                ConfigureMovieCaptureHook(process);
                ConfigurePlaybackFunctions(process);
            }
            catch (Exception exception)
            {
                playbackBridgeLoaded = false;
                SetPlaybackStatus("Playback controls could not attach: " + exception.Message);
            }
        }

        private void ConfigureMovieCaptureHook(NktProcess process)
        {
            try
            {
                object noParameters = null!;
                movieCaptureHookInstalled = _spyMgr.CallCustomApi(
                    process, playbackBridgePath,
                    "IStripperInstallMovieCaptureHook",
                    ref noParameters, true) >= 0;
            }
            catch (Exception exception)
            {
                movieCaptureHookInstalled = false;
                Debug.WriteLine(
                    "Movie capture hook was unavailable: " +
                    exception.Message);
            }
        }

        private void ConfigurePlaybackFunctions(NktProcess process)
        {
            try
            {
                object noParameters = null!;
                int compatibilityMask = _spyMgr.CallCustomApi(process, playbackBridgePath,
                    "IStripperGetCompatibilityMask", ref noParameters, true);
                if (compatibilityMask != 0x3F)
                {
                    SetPlaybackStatus($"vghd.exe did not match the supported playback engine (mask 0x{compatibilityMask:X}).");
                    return;
                }

                int decoderThreads = Math.Clamp((Environment.ProcessorCount + 1) / 2, 1, 8);
                object decoderThreadParameter = unchecked((ulong)decoderThreads);
                int fastDecodeResult = _spyMgr.CallCustomApi(process, playbackBridgePath,
                    "IStripperEnableFastDecode", ref decoderThreadParameter, true);
                playbackFastDecodeEnabled = fastDecodeResult >= 0;

                object cacheLimitParameter = unchecked((ulong)
                    Properties.Settings.Default.AlphaCheckpointCacheSizeMB *
                    1024 * 1024);
                int cacheLimitResult = _spyMgr.CallCustomApi(process,
                    playbackBridgePath,
                    "IStripperSetAlphaCheckpointCacheLimitBytes",
                    ref cacheLimitParameter, true);
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
                    noParameters = null!;
                    decoderOpenCount = _spyMgr.CallCustomApi(process, playbackBridgePath,
                        "IStripperGetFastDecodeOpenCount", ref noParameters, true);
                    noParameters = null!;
                    decoderOptionResult = _spyMgr.CallCustomApi(process, playbackBridgePath,
                        "IStripperGetFastDecodeOptionResult", ref noParameters, true);
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
            if (formIsClosing || IsDisposed)
            {
                return;
            }
            UpdatePlaybackControlsEnabled();
        }

        private void UpdatePlaybackControlsEnabled()
        {
            bool enabled = Properties.Settings.Default.EnablePlaybackControl &&
                playbackControlsAvailableForAccount &&
                playbackBridgeLoaded && !playbackBusy && !formIsClosing;
            bool seekEnabled = enabled && playbackMovieRegistered &&
                playbackSeekingSupported && playbackSeekReady;
            cmdRewind.Enabled = seekEnabled;
            cmdPlayPause.Enabled = enabled;
            cmdFastForward.Enabled = seekEnabled;
            cmbPlaybackSpeed.Enabled = enabled && playbackSeekingSupported &&
                (playbackDecoderKind != 2 || playbackSeekReady);
            trkPlaybackPosition.Enabled = seekEnabled;
        }

        private static bool HasPlatinumPlaybackEntitlement(string? userLevel)
        {
            return userLevel?.Trim().ToLowerInvariant() is
                "platinum" or "diamond" or "doublediamond" or
                "triplediamond" or "elite" or "master";
        }

        private void RefreshPlaybackControlVisibility(string? userLevel = null)
        {
            if (userLevel == null)
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Totem\vghd", false);
                userLevel = key?.GetValue("PreviousUserLevel")?.ToString();
            }

            bool visible = Properties.Settings.Default.EnablePlaybackControl &&
                HasPlatinumPlaybackEntitlement(userLevel);
            playbackControlsAvailableForAccount = visible;
            cmdRewind.Visible = visible;
            cmdPlayPause.Visible = visible;
            cmdFastForward.Visible = visible;
            lblPlaybackSpeed.Visible = visible;
            cmbPlaybackSpeed.Visible = visible;
            lblPlaybackTime.Visible = visible;
            trkPlaybackPosition.Visible = visible;
            UpdatePlaybackControlsEnabled();
        }

        private int SetVghdPlayerLocked(bool locked)
        {
            if (!playerLockBridgeLoaded || vghd_procID == 0)
            {
                return unchecked((int)0x80070015);
            }

            object parameter = locked ? 1UL : 0UL;
            lock (playbackApiLock)
            {
                return _spyMgr.CallCustomApi(vghd_procID, playbackBridgePath,
                    "IStripperSetPlayerLocked", ref parameter, true);
            }
        }

        private int CallPlaybackApi(string apiName, ulong? parameter = null)
        {
            if (!playbackBridgeLoaded || vghd_procID == 0)
            {
                throw new InvalidOperationException("The iStripper playback bridge is not attached.");
            }

            object parameters = parameter.HasValue ? parameter.Value : null!;
            lock (playbackApiLock)
            {
                return _spyMgr.CallCustomApi(vghd_procID, playbackBridgePath, apiName,
                    ref parameters, true);
            }
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

        private async Task RunPlaybackOperationAsync(
            Func<CancellationToken, Task> operation,
            bool prepareFastDecode = true)
        {
            if (!Properties.Settings.Default.EnablePlaybackControl ||
                !playbackControlsAvailableForAccount)
            {
                return;
            }
            if (!await playbackOperationLock.WaitAsync(0))
            {
                SetPlaybackStatus("Another playback operation is already running.");
                return;
            }

            SetPlaybackBusy(true);
            try
            {
                CancellationToken cancellationToken = playbackLifetime.Token;
                if (await Task.Run(() => EnsurePlaybackReadyAsync(
                        cancellationToken, prepareFastDecode)))
                {
                    await Task.Run(() => operation(cancellationToken),
                        cancellationToken);
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
        }

        private void SetPlaybackRate(double playbackSpeed)
        {
            ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(playbackSpeed));
            RequirePlaybackResult("IStripperSetPlayRate", bits);
        }

        private async void cmdPlayPause_Click(object sender, EventArgs e)
        {
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

        private async void cmdRewind_Click(object sender, EventArgs e)
        {
            await RunPlaybackOperationAsync(token => SeekRelativeAsync(-0.1, token));
        }

        private async void cmdFastForward_Click(object sender, EventArgs e)
        {
            await RunPlaybackOperationAsync(token => SeekRelativeAsync(0.1, token));
        }

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
            if (formIsClosing || playbackTimelinePolling || playbackBusy ||
                !Properties.Settings.Default.EnablePlaybackControl ||
                !playbackControlsAvailableForAccount || !playbackBridgeLoaded)
            {
                return;
            }

            playbackTimelinePolling = true;
            try
            {
                string animationPath = GetCurrentAnimationPath();
                if (!string.Equals(animationPath, playbackTimelineAnimationPath,
                        StringComparison.Ordinal))
                {
                    ShowNowPlaying(animationPath);
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
                    playbackTimelineTimer.Interval =
                        PlaybackTimelineIntervalMilliseconds;
                    trkPlaybackPosition.Enabled = false;
                    return;
                }

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
                        playbackTimelineTimer.Interval =
                            PlaybackTimelineIntervalMilliseconds;
                        playbackDecoderKind = discoveredDecoderKind;
                        playbackSeekingSupported =
                            playbackDecoderKind is 1 or 2;
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

                DateTime now = DateTime.UtcNow;
                int elapsed = await Task.Run(() =>
                    RequirePlaybackResult("IStripperGetElapsedMilliseconds"));
                int total = playbackTimelineDurationMilliseconds;
                if (total <= 0)
                {
                    total = await Task.Run(() =>
                        RequirePlaybackResult("IStripperGetTotalMilliseconds"));
                }
                if (formIsClosing || IsDisposed ||
                    !string.Equals(animationPath, GetCurrentAnimationPath(),
                        StringComparison.Ordinal) || playbackBusy)
                {
                    return;
                }

                if (playbackDecoderKind == 0)
                {
                    int decoderKind = await Task.Run(() =>
                        CallPlaybackApi("IStripperGetDecoderKind"));
                    if (decoderKind is 1 or 2)
                    {
                        playbackDecoderKind = decoderKind;
                        playbackSeekingSupported = true;
                    }
                }

                if (elapsed > playbackLastKnownElapsedMilliseconds)
                {
                    playbackLastProgressAt = now;
                }
                else if (playbackDecoderKind == 2 &&
                    LegacyPlaybackStalledNearEnd(elapsed, total,
                        playbackLastProgressAt, now,
                        await Task.Run(() =>
                            CallPlaybackApi("IStripperGetState"))))
                {
                    playbackCompletedAnimationPath = animationPath;
                    playbackNextClipRetryAt = now.AddSeconds(1);
                    GetNextClip(null, animationPath);
                    return;
                }

                int seekReadyResult = playbackSeekReady ? 1 :
                    playbackDecoderKind is 1 or 2
                        ? await Task.Run(() =>
                            CallPlaybackApi("IStripperIsSeekReady"))
                        : 0;
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
                    int readinessMask = await Task.Run(() =>
                        CallPlaybackApi("IStripperGetSeekReadinessMask"));
                    trkPlaybackPosition.AccessibleDescription =
                        $"Seek readiness 0x{readinessMask:X}";
                }

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
                    trkPlaybackPosition.Value = Math.Clamp(elapsed, 0, maximum);
                    UpdatePlaybackTime(elapsed, playbackTimelineDurationMilliseconds);
                }
                UpdatePlaybackControlsEnabled();
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
            lblPlaybackTime.Text =
                $"{FormatPlaybackTime(elapsedMilliseconds)} / {FormatPlaybackTime(totalMilliseconds)}";
        }

        private void trkPlaybackPosition_MouseDown(object sender, MouseEventArgs e)
        {
            playbackTimelineDragging = true;
            if (e.Button == MouseButtons.Left)
            {
                double position = (double)e.X /
                    Math.Max(1, trkPlaybackPosition.ClientSize.Width - 1);
                trkPlaybackPosition.Value = Math.Clamp(
                    trkPlaybackPosition.Minimum +
                        (int)Math.Round(position *
                            (trkPlaybackPosition.Maximum -
                                trkPlaybackPosition.Minimum)),
                    trkPlaybackPosition.Minimum,
                    trkPlaybackPosition.Maximum);
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
            int current = RequirePlaybackResult("IStripperGetElapsedMilliseconds");
            int total = RequirePlaybackResult("IStripperGetTotalMilliseconds");
            await SeekAbsoluteAsync(current +
                (int)Math.Round(total * clipFraction), cancellationToken);
        }

        private async Task SeekAbsoluteAsync(int requestedTarget,
            CancellationToken cancellationToken)
        {
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

        private static string GetCurrentAnimationPath()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", false);
            return key?.GetValue("CurrentAnim", "")?.ToString() ?? "";
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

        private bool PlaybackReachedEnd(int elapsed, int total)
        {
            int transitionAllowance = (int)Math.Ceiling(
                Math.Max(1.0, requestedPlaybackSpeed) * 5_000);
            return total > 0 &&
                elapsed >= Math.Max(0, total -
                    Math.Max(2_000, transitionAllowance));
        }

        private void BeginAnimationReplacement(string animationPath)
        {
            playbackRequestedAnimationPath = animationPath;
            playbackCompletedAnimationPath = "";
            playbackNextClipRetryAt = DateTime.MinValue;
            playbackReplacementStableAt = DateTime.MinValue;
        }

        private static string FormatPlaybackTime(int milliseconds)
        {
            TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }

        private void OnFunctionCalled(NktHook hook, NktProcess process, NktHookCallInfo hookCallInfo)
        {
            var p = hookCallInfo.Params();
            IntPtr pointer = IntPtr.Zero;
            string keyname = "";
            int length = 0;
            foreach (INktParam param in p)
            {
                if (param.Name == "lpData") pointer = param.PointerVal;
                if (param.Name == "cbData")
                {
                    length = Convert.ToInt16(param.Value);
                }
                if (param.Name == "lpValueName")
                    keyname = param.Value?.ToString() ?? "";
            }
            if (length < 1) return;
            string str = GetStringFromPointer(pointer, length);
            if (keyname == "PreviousUserLevel")
            {
                if (!formIsClosing && IsHandleCreated)
                {
                    BeginInvoke((Action)(() => RefreshPlaybackControlVisibility(str)));
                }
                return;
            }
            if (keyname != "CurrentAnim") return;
            System.Diagnostics.Debug.WriteLine("vghd.exe setting " + keyname + " to " + str);

                //check if this propsed card is in the filterd list
                string newcardstring = str ?? "";
                bool found = true;
                if (Properties.Settings.Default.EnforceCardFilter)
                {
                    if (string.IsNullOrEmpty(newcardstring) && !isAutoSelecting)
                    {
                        if (lblNowPlaying != null) this.Invoke((Action)(() => lblNowPlaying.Text = ""));
                        return;
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
                        if (model == null) return;
                        this.Invoke((Action)(() => res = items.Where(x => x.Text == model.modelName + "\r\n" + model.outfit).FirstOrDefault()));

                        //does the new clip match the clip filter?
                        ModelClip? res2 = null;
                        if (res != null)
                        {
                            var clipstest = FilterClipList(model.clips);
                            string clipstring = newcardstring.Split("\\")[1];
                            res2 = clipstest.Where(c => c.clipName == clipstring).FirstOrDefault();
                        }
                        if (res == null || res2 == null) found = false;
                        while (!found)
                        {
                            //play a clip from a filtered card instead
                            //find a new model from the filtered cards
                            if (items == null || items.Length < 1) return;
                            string newtag = nowPlayingFilterMatch;
                            if (Properties.Settings.Default.Randomize)
                            {
                                Random r = new Random();
                                if (res == null || res2 == null) //choose a different card
                                {
                                    while (newtag == nowPlayingFilterMatch)
                                    {
                                        Int64 newr = r.Next(items.Length);
                                        newtag = items[(int)newr].Text;
                                        if (items.Length == 1) break;
                                    }

                                }
                            }
                            else
                            {
                                //find the current card
                                int i = 0;
                                for (i = 0; i < items.Length; i++)
                                {
                                    if (items[i].Text.ToString() == newtag)
                                        break;
                                }
                                i++;
                                if (i > items.Length - 1) i = 0;
                                newtag = items[i].Text;
                            }
                            //choose a random clip from those shown
                            var mod = Datastore.findCardByText(newtag);
                            if (mod == null) continue;
                            List<ModelClip> clips = FilterClipList(mod.clips);
                            if (clips.Count > 0)
                            {
                                Random r = new Random();
                                var itemnum = r.Next(clips.Count);
                                ModelClip selectedClip = clips[itemnum];
                                if (selectedClip.clipName == null) continue;
                                res2 = selectedClip;
                                newcardstring =
                                    selectedClip.clipName.Split("_")[0] + "\\" +
                                    selectedClip.clipName;
                                found = true;

                                listModelsNew.Invoke((Action)(() => listModelsNew.ClearSelection()));
                                int? index = items.ToList().FindIndex(x => x.Text == newtag);
                                if (index != null)
                                {
                                    listModelsNew.Invoke((Action)(() =>
                                        listModelsNew.SelectWhere(x =>
                                            string.Equals(x.Tag?.ToString(), newtag,
                                                StringComparison.Ordinal))));
                                }
                            }

                        }

                    }
                }

                isAutoSelecting = false;
                ShowNowPlaying(newcardstring, found);
                if (str != newcardstring && newcardstring != wallpaperTag)
                {
                    RegistryKey? keynew = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", true);

                    string r = nowPlayingTag;
                    string pp = r.Split("_")[0];
                    string full = pp + "\\" + r;
                    if (keynew != null)
                    {
                        keynew.SetValue("ForceAnim", newcardstring);
                        keynew.Close();
                        wallpaperTag = newcardstring;
                    }

                    hookCallInfo.Result().LongLongVal = -1;
                    hookCallInfo.Result().LongVal = -1;
                    hookCallInfo.Result().Value = -1;
                    hookCallInfo.LastError = 5;
                }
                //if (found) this.BeginInvoke((Action)(() => TaskbarThumbnail()));
                isAutoSelecting = true;
                return;
        }

        private List<ModelClip> FilterClipList(List<ModelClip> clips)
        {
            var currentClips = clips;

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

        private string GetStringFromPointer(IntPtr address, int length)
        {
            INktProcessMemory procMem = _spyMgr.ProcessMemoryFromPID((int)vghd_procID);
            var buffer = new byte[length];
            var lenptr = new IntPtr(length);
            GCHandle pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            IntPtr pDest = pinnedBuffer.AddrOfPinnedObject();
            Int64 bytesReaded = procMem.ReadMem(pDest, address, lenptr).ToInt64();
            procMem.WriteMem(pDest, address, new IntPtr(0));
            pinnedBuffer.Free();

            var res = System.Text.Encoding.Unicode.GetString(buffer);
            return res.Replace("\0", string.Empty);
        }


        private void GetNowPlaying()
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", false);
            if (key != null)
            {
                var a = key.GetValue("CurrentAnim", "");
                if (a != null)
                {
                    string nowp = a.ToString() ?? "";
                    ShowNowPlaying(nowp, !string.IsNullOrEmpty(nowp));
                    key.Close();
                }
            }
        }

        private bool EnforceNowPlaying(string nowplaying)
        {
            ModelCard? model = Datastore.findCardByTag(nowplaying.Split("\\")[0]);
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
                if (path == nowPlayingPath)
                {
                    listClips.BeginInvoke(RefreshPlayingClipHighlight);
                    return;
                }
                nowPlayingPath = path;
                WakePlaybackTimeline();
                ArmMovieCapture();
                nowPlaying = "";
                if (path == "")
                {
                    listClips.BeginInvoke(RefreshPlayingClipHighlight);
                    return;
                }
                if (Datastore.modelcards == null) return;
                if (Datastore.modelcards.Count > 0)
                {
                    ModelCard? model = Datastore.findCardByTag(path.Split("\\")[0].Split("-")[0]);
                    if (model == null) return;
                    ModelClip? modelClip = model.clips.Where(x => x.clipName == path.Split("\\")[1]).FirstOrDefault();
                    if (modelClip == null) return;
                    nowPlaying = model.modelName + ", " + model.outfit + " (Clip " + modelClip.clipNumber + ")";
                    nowPlayingTagShort = path.Split("\\")[0];
                    nowPlayingTag = model.modelName + "\r\n" + model.outfit;
                    if (listModelsNew.Items.Where(x => x.Text == nowPlayingTag).Any())
                    {
                        nowPlayingFilterMatch = nowPlayingTag;
                    }
                    nowPlayingClipNumber = Convert.ToInt32(modelClip.clipNumber);
                }
                listClips.BeginInvoke(RefreshPlayingClipHighlight);
                if (lblNowPlaying != null) lblNowPlaying.BeginInvoke((Action)(() => { lblNowPlaying.Text = "Now Playing: " + nowPlaying; }));
                if (listClips.Items.Count == 0)
                    this.BeginInvoke((Action)(() => NowPlayingClick(true)));
                cardRenderer.nowPlayingTag = nowPlayingTag;
                listModelsNew.BeginInvoke((Action)(() => listModelsNew.Refresh()));
            }
            catch { }
            this.BeginInvoke((Action)(() => TaskbarThumbnail()));
            if (doWallpaper && lblNowPlaying != null)
                lblNowPlaying.BeginInvoke((Action)(() =>
                    lblNowPlaying.Text = "Now Playing: " + nowPlaying));
            if (Properties.Settings.Default.AutoWallpaper && doWallpaper &&
                nowPlaying != "")
                BeginInvoke((Action)(() => _ = ChangeWallpaper()));
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
                playbackTimelineTimer.Interval =
                    PlaybackTransitionIntervalMilliseconds;
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
                    listModelsNew.Refresh();
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
            txtSearch.Text = nowPlayingTag.Split("\r\n")[0];
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

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            formIsClosing = true;
            playbackTimelineTimer.Stop();
            playbackTimelineTimer.Dispose();
            playbackLifetime.Cancel();
            UnregisterHotKeys();
            timerhook?.Dispose();
            DisableMovieCapture();
            if (playerLockBridgeLoaded)
            {
                try { SetVghdPlayerLocked(false); } catch { }
                playerLockBridgeLoaded = false;
            }
            if (playbackBridgeLoaded && playbackMovieRegistered)
            {
                // If the form closes during an accelerated scan, restore the user's
                // selected rate before the Deviare agent and bridge are released.
                try { SetPlaybackRate(requestedPlaybackSpeed); } catch { }
            }
            SaveMyData();
            Wallpaper.RestoreWallpaper();
            if (Utils.DefaultIconsVisible != Utils.DesktopIconsVisible())
            {
                Utils.ToggleDesktopIcons();
            }
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
        }

        private void cmdNextClip_Click(object sender, EventArgs e)
        {
            this.BeginInvoke((Action)(() => GetNextClip()));
        }

        private void GetNextClip(ModelCard? model = null,
            string? completedAnimation = null)
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", false);
            string path = completedAnimation ?? "";
            bool chooseRandom = false;
            if (model != null)
            {
                chooseRandom = true;
            }
            else if (string.IsNullOrEmpty(path))
            {
                if (key == null) return;
                var a = key.GetValue("CurrentAnim", "");
                if (a == null) return;
                path = a.ToString() ?? "";
                key.Close();
                if (path == "") return;
            }


            if (Datastore.modelcards == null) return;
            if (Datastore.modelcards.Count > 0)
            {
                if (model == null) model = Datastore.findCardByTag(path.Split("\\")[0]);
                if (model == null) return;
                List<ModelClip> clips = new List<ModelClip>();
                if (model.clips == null) return;
                clips = FilterClipList(model.clips);

                ModelClip? mnew = null;
                if (chooseRandom)
                {
                    if (clips.Count == 0) return;
                    Random rand = new Random();
                    mnew = clips[rand.Next(clips.Count)];
                }
                else
                {
                    var cliplst = clips.Where(x => x.clipName == path.Split("\\")[1]);
                    if (cliplst.Count() > 0)
                    {
                        ModelClip modelClip = cliplst.First();
                        if (modelClip.clipNumber < clips.Last().clipNumber)
                        {
                            //play next
                            mnew = clips.Where((x) => x.clipNumber > modelClip.clipNumber).FirstOrDefault();
                        }
                        else
                        {
                            //play first
                            mnew = clips.FirstOrDefault();
                        }
                    }
                    else
                    {
                        mnew = clips.FirstOrDefault();
                    }
                }

                if (mnew != null && mnew.clipName != null)
                {
                    RegistryKey? keynew = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", true);

                    string r = mnew.clipName;
                    string p = r.Split("_")[0];
                    string full = p + "\\" + r;
                    if (keynew != null)
                    {
                        if (completedAnimation == null)
                        {
                            BeginAnimationReplacement(full);
                        }
                        keynew.SetValue("ForceAnim", full);
                        keynew.Close();
                    }
                }
            }
            this.BeginInvoke((Action)(() => TaskbarThumbnail()));
        }

        private void GetNextCard()
        {
            //find a new model from the filtered cards
            if (items == null || items.Length < 1) return;
            Random r = new Random();

            string newtag = nowPlayingFilterMatch;
            if (Properties.Settings.Default.Randomize)
            {
                while (newtag == nowPlayingFilterMatch)
                {
                    Int64 newr = r.Next(items.Length);
                    newtag = items[(int)newr].Text;
                    ModelCard? candidate = Datastore.findCardByTag(
                        items[(int)newr].Tag?.ToString() ?? "");
                    if (candidate == null ||
                        FilterClipList(candidate.clips).Count < 1)
                        newtag = nowPlayingTag;
                    if (items.Length == 1) break;
                }
            }
            else
            {
                //find the current card
                int i = 0;
                for (i = 0; i < items.Length; i++)
                {
                    if (items[i].Text.ToString() == newtag)
                        break;
                }
                i++;
                if (i > items.Length - 1) i = 0;
                newtag = items[i].Text;
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
            var itemnum = r.Next(listClips.Items.Count - 1);
            listClips.SelectedItems.Clear();
            var j = listClips.Items[(int)itemnum];
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

        private void ValidateMinSizeMB()
        {
            Properties.Settings.Default.MinSizeMB = (long)numMinSizeMB.Value;
            if (items != null && items.Length > 0 && listModelsNew.SelectedItems.Count > 0) loadListClips(listModelsNew.SelectedItems[0].Tag);

        }

        private void txtSearch_Enter(object sender, EventArgs e)
        {
        }

        private void numMinSizeMB_ValueChanged(object sender, EventArgs e)
        {
            ValidateMinSizeMB();
        }

        private void enforceCardFilterToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.EnforceCardFilter = enforceCardFilterToolStripMenuItem.Checked;
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
                ratingSlider.Value = myData.GetCardRating(c.name.ToString());
            else
                ratingSlider.Value = 0;
            if (myData != null) menuCardFavourite.Checked = myData.GetCardFavourite(c.name);
            if (c.rating > 0) ratingToolStripMenuItem.Text = "Rating: " + (c.rating - 5M).ToString();
            else ratingToolStripMenuItem.Text = "Rating: NA";
            statsToolStripMenuItem.Text = "Stats: " + c.bust + "/" + c.waist + "/" + c.hips;
            nameToolStripMenuItem.Text = c.modelName;
            outfitToolStripMenuItem.Text = c.outfit;
            CultureInfo cultureInfo = Thread.CurrentThread.CurrentCulture;
            TextInfo textInfo = cultureInfo.TextInfo;
            if (c.hair != null) hairToolStripMenuItem.Text = "Hair: " + textInfo.ToTitleCase(c.hair.ToLower());
            if (c.datePurchased != null) purchasedToolStripMenuItem.Text = "Purchased: " + ((DateTime)c.datePurchased).ToShortDateString();
            if (c.hotnessLevel != "") hotnessToolStripMenuItem.Text = "Hotness: " + ((Enums.HotnessCode)Convert.ToInt32(c.hotnessLevel)).GetDescription();
            else hotnessToolStripMenuItem.Text = "Hotness: NA";
            ageToolStripMenuItem.Text = "Age: " + c.modelAge;
        }

        private ImageListViewItem? mousedownCard = null;
        private ImageListViewItem? currentMenuCard = null;
        private void menuCardFavourite_CheckedChanged(object sender, EventArgs e)
        {
            if (myData == null || currentMenuCard == null) return;
            if (myData.GetCardFavourite(currentMenuCard.Tag.ToString()) != menuCardFavourite.Checked)
            {
                myData.AddCardFavourite(currentMenuCard.Tag.ToString(), menuCardFavourite.Checked);
                currentMenuCard.Update();
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
        }


        private void RatingSlider_ValueChanged(object sender, EventArgs e)
        {
            if (myData == null || currentMenuCard == null) return;
            if (myData.GetCardRating(currentMenuCard.Tag.ToString()) == ratingSlider.Value) return;
            myData.AddCardRating(currentMenuCard.Tag.ToString(), ratingSlider.Value);
            if (menuShowRatingsStars.Checked)
            {
                currentMenuCard.Update();
            }
        }

        private void chkShowRatingStars_CheckedChanged(object sender, EventArgs e)
        {
            bool r = Properties.Settings.Default.ShowRatingStars;
            if (r != menuShowRatingsStars.Checked)
            {
                Properties.Settings.Default.ShowRatingStars = menuShowRatingsStars.Checked;
                listModelsNew.Refresh();
            }
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
            if (!string.IsNullOrEmpty(clipListTag))
            {
                this.Cursor = Cursors.WaitCursor;
                CardPhotos photos = new CardPhotos();
                await photos.LoadCardPhotos(client, clipListTag);
                PhotoViewer p = new PhotoViewer(photos);
                await p.PopulateAsync();
                p.Show();
                this.Cursor = Cursors.Arrow;
            }
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
            this.BeginInvoke((Action)(() => GetNextClip()));
        }

        private void nextclipModel_click(object? sender, ThumbnailButtonClickedEventArgs e)
        {
            GetNextCard();
        }

        private void lblNowPlaying_TextChanged(object sender, EventArgs e)
        {
            string t = "iStripper QuickPlayer";
            if (lblNowPlaying.Text.Length > 14) t = lblNowPlaying.Text.Substring(13);
            this.Text = t;
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
            else
                AdjustControls();
        }

        private async void cmdWallpaper_click(object sender, EventArgs e)
        {
            lastWallpaperClipNumber = 0;
            lastWallpaperShortTag = "";
            await ChangeWallpaper();
        }

        private string lastWallpaperShortTag = "";
        private int lastWallpaperClipNumber = 0;
        private async System.Threading.Tasks.Task ChangeWallpaper(bool NotFromCheck = true)
        {
            System.Diagnostics.Debug.WriteLine("ChangeWallpaper called with nowPlayingTagShort=" + nowPlayingTagShort + ", lbl=" + lblNowPlaying.Text);
            //check that this wallpaper really matches filters
            ModelCard? model = Datastore.findCardByTag(nowPlayingTagShort.Split("\\")[0]);
            ListViewItem? res = null;
            if (model == null) return;
            this.Invoke((Action)(() => res = items.Where(x => x.Text == model.modelName + "\r\n" + model.outfit).FirstOrDefault()));

            //does the new clip match the clip filter?
            ModelClip? res2 = null;
            if (res != null)
            {
                var clipstest = FilterClipList(model.clips);
                res2 = clipstest.Where(c => c.clipNumber == nowPlayingClipNumber).FirstOrDefault();
            }

            if (nowPlayingTagShort == null || nowPlayingTagShort.Length == 0) return;
            if (string.IsNullOrEmpty(lblNowPlaying.Text)) return;

            string modelname = GetModelsString(model);

            foreach (ToolStripMenuItem item in
                wallpaperToolStripMenuItem.DropDownItems.OfType<ToolStripMenuItem>())
            {
                if (item.Tag is not uint monitorNumber) continue;
                if (item.Checked)
                {
                    if (NotFromCheck || Properties.Settings.Default.AutoWallpaper)
                    {
                        CardPhotos photos = new CardPhotos();
                        await photos.LoadCardPhotos(client, nowPlayingTagShort);
                        if (res2 != null &&
                            ((lastWallpaperClipNumber != nowPlayingClipNumber ||
                              lastWallpaperClipNumber == 0) ||
                             (lastWallpaperShortTag == "" ||
                              lastWallpaperShortTag != nowPlayingTagShort)))
                        {
                            await Wallpaper.ChangeWallpaper(monitorNumber,
                                photos.getRandomWidescreenURL(), modelname,
                                model.outfit);
                        }
                    }
                }
                else
                {
                    Wallpaper.RestoreWallpaperByID(monitorNumber);
                }
            }
            lastWallpaperShortTag = nowPlayingTagShort;
            lastWallpaperClipNumber = nowPlayingClipNumber;
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
            lastWallpaperClipNumber = 0;
            lastWallpaperShortTag = "";
            await ChangeWallpaper();
        }

        private void showKittyToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ShowKitty = showKittyToolStripMenuItem.Checked;
            this.BeginInvoke((Action)(() => { PopulateModelListview(); }));
        }

        private void trackBarCardScale_ValueChanged(object sender, EventArgs e)
        {
            cardScale = (float)(trackBarCardScale.Value);
            if (listModelsNew.Items.Count > 0)
            {
                if (cardRenderer != null)
                    cardRenderer.cardScale = cardScale;
                listModelsNew.ThumbnailSize = new Size((int)(cardScale * 162), (int)(242 * cardScale));
                listModelsNew.Invalidate();
            }
            Properties.Settings.Default.CardScale = (float)(trackBarCardScale.Value);
        }

        private void listModelsNew_ItemDoubleClick(object sender, ItemClickEventArgs e)
        {
            FilterClips();
            if (listModelsNew.SelectedItems.Count > 0)
                GetNextClip(Datastore.findCardByText(listModelsNew.SelectedItems[0].Text));
        }

        private void listModelsNew_ItemClick(object sender, ItemClickEventArgs e)
        {
            ImageListView.HitInfo hit;
            listModelsNew.HitTest(e.Location, out hit);

            if (!hit.ItemHit)
                return;          
            ImageListViewItem clickCard = listModelsNew.Items[hit.ItemIndex];
            int idx = hit.ItemIndex;

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
            if (menuShowRatingsStars.Checked)
            {
                clickCard.Update();
            }
        }

        private void listModelsNew_MouseDown(object sender, MouseEventArgs e)
        {
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
            if (e.Item != null && cardRenderer.mZoomRatio > 0.0f) e.Item.Update();
        }

        private void splitContainer1_SplitterMoved(object sender, SplitterEventArgs e)
        {
            AdjustControls();
        }

        private void AdjustControls()
        {
            if (spaceRightOfListModel == 0) return;
            Graphics g = this.CreateGraphics();
            float dy, dx = 120f;
            try
            {
                dx = g.DpiX;
                dy = g.DpiY;
            }
            finally
            {
                g.Dispose();
            }

            listModelsNew.Height = this.Height - listModelsNew.Top - (int)(92 * dx / 120);
            listClips.Top = txtClipType.Bottom + 10;
            listClips.Height = this.Height - panelModelDetails.Height - (int)(72.0 * dx / 96.0) - listClips.Top;
            panelModelDetails.Top = listClips.Bottom + 8;
            listModelsNew.Width = splitContainer1.Panel1.Width - 24;
            panelClip.Width = splitContainer1.Panel2.Width;
            //cmdWallpaper.Left = panelClip.Width -  370; //(int)(370*dx/120);
            //cmdNextClip.Left = cmdWallpaper.Right + 5;
            //cmdShowModel.Left = cmdNextClip.Right + 5;            
            listClips.Width = panelClip.Width - 28;
            cmdShowModel.Left = listClips.Right - cmdShowModel.Width;
            cmdNextClip.Left = cmdShowModel.Left - cmdNextClip.Width - 5;
            cmdWallpaper.Left = cmdNextClip.Left - cmdWallpaper.Width - 5;
            cmdPhotos.Top = listClips.Top;
            cmdPhotos.Left = listClips.Right - cmdPhotos.Width;// - System.Windows.Forms.SystemInformation.VerticalScrollBarWidth;
            panelModelDetails.Width = listClips.Width;
            txtDescription.Width = listClips.Right - txtDescription.Left - 11;
            txtUserTags.Width = listClips.Right - txtUserTags.Left;
            this.BeginInvoke(new Action(() => listModelsNew.Refresh()));
        }

        private void listModelsNew_MouseLeave(object sender, EventArgs e)
        {
            if (menuCardList.Visible) return;
            cardRenderer.MouseIsOnList = false;
            listModelsNew.Refresh();
        }

        private void listModelsNew_MouseEnter(object sender, EventArgs e)
        {
            cardRenderer.MouseIsOnList = true;
        }

        private void showInBrowserToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (mousedownCard == null)
            {
                return;
            }
            ModelCard? c = Datastore.findCardByTag(mousedownCard.Tag.ToString());
            if (c != null)
            {
                try
                {
                    string url = @"https://www.istripper.com/models/" + (c.modelName + "/" + c.outfit + " " + c.name).Replace(" ", "-");

                    var psi = new System.Diagnostics.ProcessStartInfo();
                    psi.UseShellExecute = true;
                    psi.FileName = url;
                    System.Diagnostics.Process.Start(psi);
                }
                catch (Exception) { }
            }
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
                if (playerLockBridgeLoaded && !playbackBridgeLoaded &&
                    tempProcess != null)
                {
                    NktProcess process = tempProcess;
                    _ = Task.Run(() => ConfigurePlaybackFunctions(process));
                }
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
                        unchecked((ulong)size * 1024 * 1024));
                }
                catch { }
            });
        }

        private void setPlayerLocked()
        {
            Properties.Settings.Default.LockPlayer = lockPlayerToolStripMenuItem.Checked;
            playerlocked = lockPlayerToolStripMenuItem.Checked;
            ChangePlayerLocked();
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
                playerlocked ? Properties.Resources.padlock : null,
                playerlocked ? "iStripper is locked" : "");
        }

        private void ChangePlayerLocked()
        {
            if (playerLockBridgeLoaded)
            {
                int result = SetVghdPlayerLocked(playerlocked);
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

        private void blurImageToolStripMenuItem_CheckStateChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.BlurWallpaper = blurImageToolStripMenuItem.Checked;
            this.BeginInvoke((Action)(() => Wallpaper.RedrawImage()));
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
        }

        private void darkModeToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.DarkMode = darkModeToolStripMenuItem.Checked;
            this.BeginInvoke((Action)(() => { SetSkin(); }));
        }
    }
}
