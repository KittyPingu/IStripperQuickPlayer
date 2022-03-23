using EnumDescription;
using Gma.System.MouseKeyHook;
using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using Microsoft.Win32;
using Nektra.Deviare2;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using Size = System.Drawing.Size;

namespace IStripperQuickPlayer
{
    public partial class Form1 : Form
    {
        private string nowPlayingTag = "";
        private int nowPlayingClipNumber;
        private string clipListTag = "";
        private bool changesort = false;
        private MyData? myData = null;
        private bool fontInstalled = false;
        internal FilterSettings filterSettings = new FilterSettings();
        static readonly HttpClient client = new HttpClient();
        
        //global hotkeys
        Combination? nextClip;// = Combination.FromString("Control+Alt+N");
        Action? actionNextClip = null;
        Combination? nextCard;// = Combination.FromString("Control+Alt+C");
        Action? actionNextCard = null;
        //deviare2 hooking
        private NktSpyMgr _spyMgr;
        private Int32 vghd_procID=0;

        private void actNextClip()
        {
            if (Properties.Settings.Default.NextClipEnabled) GetNextClip();
        }

        private  void actNextCard()
        {
           if (Properties.Settings.Default.NextCardEnabled) this.BeginInvoke((Action)(() => GetNextCard()));
        }

        public Form1()
        {
            InitializeComponent();
        }

        private void cmdLoadModels_Click(object sender, EventArgs e)
        {
            this.Cursor = Cursors.WaitCursor;
            ReloadModels();
            this.Cursor = Cursors.Arrow;
        }

        private void ReloadModels()
        {
            ReloadStaticProperties();
            ModelsLstLoader lstLoader = new ModelsLstLoader();
            listModels.Items.Clear();
            if (Datastore.modelcards != null)
                Datastore.modelcards.Clear();
            lstLoader.LoadModels();
            PopulateModelListview();
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
            if (File.Exists(modelfilepath))
            {
                Datastore.modelcards = Deserialize(modelfilepath);
                PopulateModelListview();
            }
            else
            {
                ReloadModels();
            }
        }

        ListViewItem[]? items; //stores the list of virtualized cards for modelList operations
        internal void PopulateModelListview()
        {
            //save the selected card, we can reselect it at the end if it's still valid
            string currentText = "";
            if (listModels.SelectedIndices.Count > 0)
            {
                currentText = listModels.Items[listModels.SelectedIndices[0]].Text;
            }
            listModels.BeginUpdate();
            listModels.Items.Clear();
            if (Datastore.modelcards == null)
            {
                listModels.EndUpdate();
                return;
            }
            
            List<ModelCard>? currentCards=Datastore.modelcards;
            if (txtSearch.Text != "")
            {
                 
                string[] parts = txtSearch.Text.ToLower().Split("and").Select(p => p.Trim()).ToArray();
                foreach(string p in parts)
                { 
                    
                    List<string> taglist = p.Split("or").Select(p => p.Trim()).ToList();
                    if (p.Contains("!"))
                    {
                        
                        List<ModelCard>? poslist=null;
                        List<ModelCard>? neglist=currentCards;
                        foreach (string tag in taglist.Where(x => !x.Contains("!")))
                        {
                           //do all the positives first
                           poslist = currentCards.Where(c => (c.modelName != null && c.modelName.ContainsWithNot(tag))
                            || (c.description != null && Properties.Settings.Default.ShowDescInSearch && taglist.Any(d => c.description.ContainsWithNot(d)))                            
                            || (c.outfit != null && Properties.Settings.Default.ShowOutfitInSearch && taglist.Any(d => c.outfit.ContainsWithNot(d)))
                            || (myData != null && string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(tag)) || string.Join(",",c.tags).ContainsWithNot(tag)).ToList();                        
                        }
                        if (poslist == null) poslist = new List<ModelCard>{ };
                        foreach (string tag in taglist.Where(x => x.Contains("!")))
                        {
                            neglist = neglist.Where(c => (c.modelName != null && c.modelName.ContainsWithNot(tag))
                            && (c.description != null && Properties.Settings.Default.ShowDescInSearch && taglist.Any(d => c.description.ContainsWithNot(d)))
                            && (c.outfit != null && Properties.Settings.Default.ShowOutfitInSearch && taglist.Any(d => c.outfit.ContainsWithNot(d)))
                            && (myData != null && string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(tag)) && string.Join(",",c.tags).ContainsWithNot(tag)).ToList();         
                        }
                        if (poslist == null) currentCards = new List<ModelCard>{ };
                        else
                            if (neglist == null) neglist = new List<ModelCard>{ };
                            currentCards = poslist.Union(neglist).ToList();
                    }
                    else
                    {
                        currentCards = currentCards.Where(c => (c.modelName != null && taglist.Any(y => c.modelName.ContainsWithNot(y)))
                            || (c.description != null && Properties.Settings.Default.ShowDescInSearch && taglist.Any(d => c.description.ContainsWithNot(d)))
                            || (c.outfit != null && Properties.Settings.Default.ShowOutfitInSearch && taglist.Any(d => c.outfit.ContainsWithNot(d)))
                            || myData != null && taglist.Any(x => string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(x.Trim())) || taglist.Any(y => string.Join(",",c.tags).ContainsWithNot(y))).ToList();
                    }

                    
                }
                
            
            }
            else
                currentCards = Datastore.modelcards;

            if (currentCards == null) return;
            currentCards = Filter(currentCards);

            switch (cmbSortBy.Text)
            {
                case "My Rating":
                    if (myData != null) currentCards = currentCards.OrderByDescending(i => myData.GetCardRating(i.name)).ToList();
                    break;
                case "":
                case "Model Name":
                    currentCards = currentCards.OrderBy(i => i.modelName).ToList();
                    break;
                case "Rating":
                    currentCards = currentCards.OrderByDescending(i => i.rating).ToList();
                    break;
                case "Age":
                    currentCards = currentCards.OrderBy(i => i.modelAge).ToList();
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
                case "Height":
                    currentCards = currentCards.OrderBy(i => i.height).ToList();
                    break;
                case "Date Purchased (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.datePurchased).ToList();
                    break;
                case "Date Purchased":
                    currentCards = currentCards.OrderBy(i => i.datePurchased).ToList();
                    break;
                default:
                    break;
            }


            items = new ListViewItem[currentCards.Count()];
            int idx = 0;
            foreach (var card in currentCards)
            {
                if (card.clips != null && card.clips.Count > 0)
                {   
                    items[idx] = new ListViewItem(card.modelName + Environment.NewLine + card.outfit, 0);
                    items[idx].Tag = card.name;
                    items[idx].ImageIndex = idx;
                    idx++;
                    //listModels.Items.Add(card.name, card.modelName + Environment.NewLine + card.outfit, largeimagelist.Images.Count - 1);
                }
            }

            ImageList blankimagelist = new ImageList();
            blankimagelist.ImageSize = new Size(130, 180);
            blankimagelist.ColorDepth = ColorDepth.Depth32Bit;
            Image newblankimage = new Bitmap(130,180, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            blankimagelist.Images.Add(newblankimage);


            //listModels.Items.AddRange(items);
            listModels.LargeImageList = blankimagelist;
            listModels.EndUpdate();

            listModels.VirtualListSize = items.Length;
            listModels.VirtualMode = true;
            lblModelsLoaded.Text = "Cards Shown: " + listModels.Items.Count + "/" + Datastore.modelcards.Where(c => c.clips != null && c.clips.Count > 0).Count();

            //set the selected card back to what we had selected at start of the function
            if (currentText != "")
            {
                listModels.SelectedIndices.Clear();
                int? index = items.ToList().FindIndex(x => x.Text == currentText);
                if (index != null && index > 0)
                {
                    listModels.SelectedIndices.Add((int)index);
                    //listModels.Items[i.Index].Selected = true;
                    listModels.FindItemWithText(currentText);
                    listModels.EnsureVisible((int)index);     
                }
               
            }
        }

        void listModels_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        { 
            if (items == null)
            {       
                e.Item = new ListViewItem();
                return;
            }
            //A cache miss, so create a new ListViewItem and pass it back.
            e.Item = items[e.ItemIndex];
            e.Item.ImageIndex = 0;
        }


        private List<ModelCard>? Filter(List<ModelCard>? currentCards)
        {   
            if (chkFavourite.Checked && myData != null)
                currentCards = currentCards.Where(c => myData.GetCardFavourite(c.name)).ToList();
            if ((filterSettings.minMyRating > 0 || filterSettings.maxMyRating < 10) && myData != null)
                currentCards = currentCards.Where(c => myData.GetCardRating(c.name) >= filterSettings.minMyRating 
                && myData.GetCardRating(c.name) <= filterSettings.maxMyRating).ToList();  
            currentCards = currentCards.Where(c => Decimal.Parse(c.modelAge) >= filterSettings.minAge && Decimal.Parse(c.modelAge) <= filterSettings.maxAge
                && c.bust >= filterSettings.minBust && c.bust <= filterSettings.maxBust     
                && c.rating-5M >= filterSettings.minRating && c.rating-5M <= filterSettings.maxRating           
                ).ToList();

            if (!String.IsNullOrEmpty(filterSettings.tags))
            {
                string[] parts = filterSettings.tags.ToLower().Split("and").Select(p => p.Trim()).ToArray();


                foreach(string p in parts)
                { 
                    
                    List<string> taglist = p.Split("or").Select(p => p.Trim()).ToList();
                    if (p.Contains("!"))
                    {
                        
                        List<ModelCard>? poslist=null;
                        List<ModelCard>? neglist=currentCards;
                        foreach (string tag in taglist.Where(x => !x.Contains("!")))
                        {
                           //do all the positives first
                           poslist = currentCards.Where(c => (myData != null && string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(tag)) || string.Join(",",c.tags).ContainsWithNot(tag)).ToList();                        
                        }
                        if (poslist == null) poslist = new List<ModelCard>{ };
                        foreach (string tag in taglist.Where(x => x.Contains("!")))
                        {
                            neglist = neglist.Where(c => (myData != null && string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(tag)) && string.Join(",",c.tags).ContainsWithNot(tag)).ToList();         
                        }
                        if (poslist == null) currentCards = new List<ModelCard>{ };
                        else
                            if (neglist == null) neglist = new List<ModelCard>{ };
                            currentCards = poslist.Union(neglist).ToList();
                    }
                    else
                    {
                        currentCards = currentCards.Where(c => (c.modelName != null && taglist.Any(y => c.modelName.ContainsWithNot(y)))
                            || myData != null && taglist.Any(x => string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(x.Trim())) || taglist.Any(y => string.Join(",",c.tags).ContainsWithNot(y))).ToList();
                    }

                    
                }
                
            }
            List<Enum> enabledcollections = new List<Enum>{ };
            if (filterSettings.IStripperXXX) enabledcollections.Add(Enums.CollectionType.IStripperXXX);
            if (filterSettings.DeskBabes) enabledcollections.Add(Enums.CollectionType.DeskBabes);
            if (filterSettings.IStripperClassic) enabledcollections.Add(Enums.CollectionType.IStripperClassic);
            if (filterSettings.VGClassic) enabledcollections.Add(Enums.CollectionType.VGClassic);
            if (filterSettings.IStripper) enabledcollections.Add(Enums.CollectionType.IStripper);

            if (filterSettings.Normal && !filterSettings.Special)
                currentCards = currentCards.Where(c => c.exclusive != null && !(bool)c.exclusive).ToList();
            else if (filterSettings.Special && !filterSettings.Normal)
                currentCards = currentCards.Where(c => c.exclusive != null && (bool)c.exclusive).ToList();

            currentCards = currentCards.Where(c=> enabledcollections.Contains(c.collection)).ToList();

            return currentCards;
        }

        internal List<ModelCard>? Deserialize(String filename)  
        {  
            //Format the object as Binary  
            BinaryFormatter formatter = new BinaryFormatter();  
   
            //Reading the file from the server  
            FileStream fs = File.Open(filename, FileMode.Open);   
            object obj = formatter.Deserialize(fs);  
            List<ModelCard>? emps = (List<ModelCard>?)obj;  
            fs.Flush();  
            fs.Close();  
            fs.Dispose(); 
            return emps;
        }  

        internal void Serialize(List<ModelCard>? emps, String filename)  
        {  
            //Create the stream to add object into it.  
            System.IO.Stream ms = File.OpenWrite(filename);   
            //Format the object as Binary  
  
            BinaryFormatter formatter = new BinaryFormatter();  
            //It serialize the employee object  
            formatter.Serialize(ms, emps);  
            ms.Flush();  
            ms.Close();  
            ms.Dispose();  
        } 

        internal void SaveMyData()
        {
            string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "mydata.bin");
            string mdatafolder = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer");
            if (!Directory.Exists(mdatafolder))
                Directory.CreateDirectory(mdatafolder);
            
            System.IO.Stream ms = File.OpenWrite(mdatafilepath);     
            BinaryFormatter formatter = new BinaryFormatter();              
            formatter.Serialize(ms, myData);  
            ms.Flush();  
            ms.Close();  
            ms.Dispose();  
        }

        internal void LoadDefaultFilters()
        { 
            try
            {
                string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "filters.bin");
                if (!File.Exists(mdatafilepath)) return ;
                BinaryFormatter formatter = new BinaryFormatter();  
                   
                //Reading the file from the server  
                FileStream fs = File.Open(mdatafilepath, FileMode.Open);   
                object obj = formatter.Deserialize(fs);  
                filterSettings = (FilterSettings)obj;  
                fs.Flush();  
                fs.Close();  
                fs.Dispose(); 
            }
            catch (Exception ex){
                MessageBox.Show("Error reading MyData file\r\n" + ex.Message);
                filterSettings = new FilterSettings();
            } 
         }

        internal MyData RetrieveMyData()
        {
            
            try
            {
                string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "mydata.bin");
                if (!File.Exists(mdatafilepath)) return new MyData();
                BinaryFormatter formatter = new BinaryFormatter();  
   
                //Reading the file from the server  
                FileStream fs = File.Open(mdatafilepath, FileMode.Open);   
                object obj = formatter.Deserialize(fs);  
                MyData m = (MyData)obj;  
                fs.Flush();  
                fs.Close();  
                fs.Dispose(); 
                return m;
            }
            catch (Exception ex){
                MessageBox.Show("Error reading MyData file\r\n" + ex.Message);
                return new MyData();
            }            
        }

        private void lblModelsLoaded_Click(object sender, EventArgs e)
        {

        }

        private void listModels_SelectedIndexChanged(object sender, EventArgs e)
        {
            FilterClips();
        }

        private void FilterClips()
        {
             ListView.SelectedIndexCollection col = listModels.SelectedIndices;
             if (col.Count > 0)
                loadListClips(listModels.Items[col[0]].Tag);
        }

        private void loadListClips(object tag)
        {
            ModelCard? card = Datastore.findCardByTag(tag.ToString());           
            if (card == null) return; 
            clipListTag = tag.ToString();
            lblCipListDetails.Text = card.modelName + ": " +card.outfit;
            if (myData != null) 
                txtUserTags.Text = string.Join(",", myData.GetCardTags(tag.ToString()));
            listClips.BeginUpdate();
            listClips.Items.Clear();
            if (card.clips == null) return;
            foreach(ModelClip clip in card.clips)
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
                if (Properties.Settings.Default.MinSizeMB > 0 && Properties.Settings.Default.MinSizeMB >clip.size/1024/1024) addThis = false;
                if (clip.clipName != null && clip.clipName.Contains("demo") && !chkDemo.Checked) addThis = false;
                if (addThis)
                {
                    ListViewItem item = new ListViewItem(new [] {clip.clipNumber.ToString(), clip.clipName, clip.hotnessCode.ToString(), clip.clipType, (clip.size/1024/1024).ToString() +"MB"});
                    listClips.Items.Add(item);
                }
            }
            listClips.EndUpdate();
            txtDescription.Text = card.description;
            lblAge.Text = "Age: "+card.modelAge;
            lblStats.Text = "Stats: "+card.bust+"/"+card.waist+"/"+card.hips;
            lblRatingScore.Text = "Rating: "+(Convert.ToDecimal(card.rating)-5m).ToString();
            lblCollection.Text = "CardType: "+card.collection.GetDescription();
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
                    key.SetValue("ForceAnim", full);  
                    key.Close(); 
                }
            }
            lastchosen = listClips.SelectedItems[0].SubItems[1].Text;
        }

        void SetUserFonts(float scaleFactorX, float scaleFactorY) {
            var OldFont = Font;
            Font = new Font(OldFont.FontFamily, 11f * scaleFactorX, OldFont.Style, GraphicsUnit.Pixel); 
            OldFont.Dispose();
         }
         protected override void DefWndProc(ref Message m) {
             //DPI_Per_Monitor.Check_WM_DPICHANGED(SetUserFonts,m, this.Handle);
             base.DefWndProc(ref m);
         }

        private void Form1_Load(object sender, EventArgs e)
        {
            //DPI_Per_Monitor.TryEnableDPIAware(this, SetUserFonts);
            this.Icon = Properties.Resources.df2284943cc77e7e1a5fa6a0da8ca265;

            //check if we Segoe Fluent Icons font - this comes with windows 11
            var fontsCollection = new InstalledFontCollection();
            foreach (var fontFamily in fontsCollection.Families)
            {
                if (fontFamily.Name == "Segoe Fluent Icons")
                    fontInstalled = true;
            }
            cmbSortBy.Text = Properties.Settings.Default.SortBy;
            chkFavourite.Checked = Properties.Settings.Default.FavouritesFilter;
            menuShowRatingsStars.Checked = Properties.Settings.Default.ShowRatingStars;
            includeDescriptionInSearchToolStripMenuItem.Checked = Properties.Settings.Default.ShowDescInSearch;
            includeShowTitleInSearchToolStripMenuItem.Checked = Properties.Settings.Default.ShowOutfitInSearch;
            if (Properties.Settings.Default.MinSizeMB != 0)
            {
                numMinSizeMB.Value = Properties.Settings.Default.MinSizeMB;
            }
            LoadDefaultFilters();
            myData = RetrieveMyData();
            listModels.SetDoubleBuffered();
            string REG_KEY = @"HKEY_CURRENT_USER\Software\Totem\vghd\parameters";
            //watcher = new RegistryWatcher(new Tuple<string, string>(REG_KEY, "CurrentAnim"));
            //watcher.RegistryChange += RegistryChanged;
            clickingNowPlaying = true;
            RetrieveModels();
            GetNowPlaying();
            clickingNowPlaying = false;
            SetupKeyHooks();
            Task.Run(() => SetupRegHooks());
        }

        private void SetupKeyHooks()
        {
            nextClip = Combination.FromString(Properties.Settings.Default.NextClipString);
            nextCard = Combination.FromString(Properties.Settings.Default.NextCardString);
            actionNextClip = actNextClip;
            actionNextCard = actNextCard;
            var assignment = new Dictionary<Combination, Action>
            {
                {nextClip, actionNextClip},
                {nextCard, actionNextCard}
            };
            Hook.GlobalEvents().OnCombination(assignment);
        }

        System.Threading.Timer timerhook;
        NktHook hook;
        private void SetupRegHooks()
        {
            _spyMgr = new NktSpyMgr();
            _spyMgr.Initialize();
            _spyMgr.OnFunctionCalled += new DNktSpyMgrEvents_OnFunctionCalledEventHandler(OnFunctionCalled);
            timerhook = new System.Threading.Timer(new TimerCallback(waitForIStripper), null, 1000, 1000);
            return ;
        }

        private bool InjectVGHDProcess()
        {
            NktProcessesEnum enumProcess = _spyMgr.Processes();
            NktProcess tempProcess = enumProcess.First();
            while (tempProcess != null)
            {
                hook = _spyMgr.CreateHook("KernelBase.dll!RegSetValueExW", (int)(eNktHookFlags.flgAutoHookChildProcess));
                hook.Hook(true);
                if (tempProcess.Name.Equals("vghd.exe", StringComparison.InvariantCultureIgnoreCase) && tempProcess.PlatformBits == 32)
                {
                    hook.Attach(tempProcess, true);
                    vghd_procID = tempProcess.Id;
                    //check that we havent played a new clip while we weren't hooked
                    clickingNowPlaying = true;
                    GetNowPlaying();
                    clickingNowPlaying = false;
                    return true;
                }
                tempProcess = enumProcess.Next();
            }
                      
            return false;
        }

        private void waitForIStripper(object state)
        {
            if (InjectVGHDProcess()) timerhook.Dispose();
        }

        private void OnFunctionCalled(NktHook hook, NktProcess process, NktHookCallInfo hookCallInfo)
        {
            string newclip = @"f0954\f0954_6176201.vghd";
                     
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
                if (param.Name == "lpValueName") keyname = param.Value.ToString();
            }
            if (keyname != "CurrentAnim" || length < 1) return;
            string str = GetStringFromPointer(pointer, length);
            System.Diagnostics.Debug.WriteLine("vghd.exe setting " + keyname + " to " + str);

            //check if this propsed card is in the filterd list
            string newcardstring = str ?? "";
            if (Properties.Settings.Default.EnforceCardFilter)
            {
                if (string.IsNullOrEmpty(newcardstring))
                {
                    this.BeginInvoke((Action)(() => lblNowPlaying.Text = ""));
                    return;
                }
                else
                {
                    ModelCard? model = Datastore.findCardByTag(newcardstring.Split("\\")[0]);   
                    ListViewItem? res = null;
                    if (model == null) return;
                    this.Invoke((Action)(() => res = items.Where(x => x.Text == model.modelName + "\r\n" + model.outfit).FirstOrDefault()));
                    bool found = false;
                    while (res == null && !found)
                    {
                        //play a clip from a filtered card instead
                        //find a new model from the filtered cards
                        if (items == null || items.Length < 1) return;
                        Random r = new Random();
            
                        string newtag = nowPlayingTag;
                        while (newtag == nowPlayingTag)
                        {
                            Int64 newr = r.Next(items.Length);
                            newtag = items[(int)newr].Text;
                            if (items.Length == 1) break;
                        }
                        listModels.BeginInvoke((Action)(() => listModels.SelectedIndices.Clear()));
                        int? index = items.ToList().FindIndex(x => x.Text == newtag);
                        if (index != null)
                        {
                            listModels.BeginInvoke((Action)(() => listModels.SelectedIndices.Add((int)index)));
                            listModels.BeginInvoke((Action)(() => listModels.FindItemWithText(newtag)));
                            listModels.BeginInvoke((Action)(() => listModels.EnsureVisible((int)index)));     
                        }
            
                        //choose a random clip from those shown
                        var mod = Datastore.findCardByText(newtag);
                        List<ModelClip>? clips = FilterClipList(mod.clips);
                        if (clips.Count > 0)
                        {
                            var itemnum = r.Next(clips.Count-1);
                            newcardstring = clips[itemnum].clipName.Split("_")[0] + "\\" + clips[itemnum].clipName;
                            found = true;
                        }
                    
                    }
                        
                }
            }
            
            
            ShowNowPlaying(newcardstring);
            if (str != newcardstring)
            {
                RegistryKey? keynew = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", true);

                string r = nowPlayingTag;
                string pp = r.Split("_")[0];
                string full = pp + "\\" + r;
                if (keynew != null)
                {
                    keynew.SetValue("ForceAnim", newcardstring);
                    keynew.Close();
                }

                hookCallInfo.Result().LongLongVal = -1;
                hookCallInfo.Result().LongVal = -1;
                hookCallInfo.Result().Value = -1;
                hookCallInfo.LastError = 5;
            }

            return;
        }

        private List<ModelClip>? FilterClipList(List<ModelClip> clips)
        {
            List<ModelClip> clipsnew = new List<ModelClip>();
            foreach (ModelClip clip in clips)
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
                if (Properties.Settings.Default.MinSizeMB > 0 && Properties.Settings.Default.MinSizeMB >clip.size/1024/1024) addThis = false;
                if (clip.clipName != null && clip.clipName.Contains("demo") && !chkDemo.Checked) addThis = false;
                if (addThis)
                {
                    clipsnew.Add(clip);
                }
            }
            return clipsnew;
        }

        public static string readItemText(ListView varControl, int itemnum) {
            if (varControl.InvokeRequired) {
                return (string)varControl.Invoke(
                    new Func<String>(() => readItemText(varControl, itemnum))
                );
            }
            else {
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
                    ShowNowPlaying(nowp);
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

        private void ShowNowPlaying(string path)
        {
            try
            {
                string nowPlaying = path;
                if (path == "") return;
                if (Datastore.modelcards == null) return;
                if (Datastore.modelcards.Count > 0)
                {
                    ModelCard? model = Datastore.findCardByTag(path.Split("\\")[0]);
                    if (model == null) return;
                    ModelClip? modelClip = model.clips.Where(x => x.clipName == path.Split("\\")[1]).FirstOrDefault();
                    if (modelClip == null) return;
                    nowPlaying = model.modelName + ", " + model.outfit + " (Clip " + modelClip.clipNumber + ")";
                    //nowPlayingTag = path.Split("\\")[0]
                    nowPlayingTag = model.modelName + "\r\n" + model.outfit;
                    nowPlayingClipNumber = Convert.ToInt32(modelClip.clipNumber);
                }
                if (lblNowPlaying != null) lblNowPlaying.BeginInvoke((Action)(() => { lblNowPlaying.Text = "Now Playing: " + nowPlaying;}));
                if (listClips.Items.Count == 0)
                    this.BeginInvoke((Action)(() => NowPlayingClick(true)));
                listModels.BeginInvoke((Action)(() => listModels.Refresh()));
            }
            catch { }
        }

        private void chk_CheckedChanged(object sender, EventArgs e)
        {
            changesort = true;
            FilterClips();
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            PopulateModelListview();
            Properties.Settings.Default.SortBy = cmbSortBy.Text;
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
                listModels.SelectedIndices.Clear();
                var i = items.Where(x => x.Text == nowPlayingTag).FirstOrDefault();
                int? index = items.ToList().FindIndex(x => x.Text == nowPlayingTag);            
                if (i != null)
                {
                    listModels.SelectedIndices.Add((int)index);
                    listModels.FindItemWithText(nowPlayingTag);
                    listModels.EnsureVisible((int)index);                    
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
              
                //loadListClips(nowPlayingTag);
            }
        }

        private void listModels_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            if (changesort)
            {
                changesort = false;
                e.Graphics.Clear(Color.White);
            }
            Rectangle imgrect = new Rectangle(e.Bounds.Left + (int)((e.Graphics.DpiY/192)*28), e.Bounds.Top, e.Bounds.Width - (int)((e.Graphics.DpiY/192)*55), e.Bounds.Height - (int)((e.Graphics.DpiY/192)*47));
                
            if (e.Item.Selected)
            {
                   
                //e.DrawFocusRectangle();
                e.Graphics.FillRectangle(Brushes.PaleGreen, e.Bounds);
            }
            
            string text = "";
            ModelCard? card = Datastore.findCardByTag(e.Item.Tag.ToString());
            if (card == null) return;
            if (card.image != null) e.Graphics.DrawImage(card.image, imgrect);
            decimal myrating=0M;
            if (myData != null) myrating = myData.GetCardRating(card.name);
            switch (cmbSortBy.Text)
            {
                case "My Rating":
                    if (!Properties.Settings.Default.ShowRatingStars)
                    {
                        if (myrating > 0) text = myrating.ToString();
                    }
                    break;
                case "Height":
                    decimal decheight = Convert.ToDecimal(card.height);
                    text = Math.Floor(decheight) + "'" + (int)(24*(decheight-Math.Floor(decheight)))/2.0M + "''";
                    break;
                case "":
                case "Model Name":
                    text = "";
                    break;
                case "Rating":
                    text = (Convert.ToDecimal(card.rating)-5m).ToString();
                    break;
                case "Age":
                    text = (card.modelAge ?? "");    
                    break;
                case "Ethnicity":
                    text = (card.ethnicity ?? "");        
                    break;
                case "Breast Size":
                case "Breast Size (Descending)":
                    text = (card.bust ?? 0).ToString();
                    break;
                case "Date Purchased":
                case "Date Purchased (Descending)":
                    if (card.datePurchased != null)
                        text = ((DateTime)card.datePurchased).ToShortDateString();
                    break;
                default:
                    break;
            }

            StringFormat stringFormat = new StringFormat();
            stringFormat.Alignment = StringAlignment.Center;
            stringFormat.LineAlignment = StringAlignment.Far;

            Rectangle rectName = new Rectangle(e.Bounds.Left, e.Bounds.Bottom-(int)((e.Graphics.DpiY/192)*45), e.Bounds.Width, (int)((e.Graphics.DpiY/192)*30));
            int sztitle=10;
            Font fontName = new Font("Segoe UI", sztitle);
            string[] nameoutfit = e.Item.Text.Split("\r\n");
            var textSizeName = e.Graphics.MeasureString(nameoutfit[0], fontName);
            while (textSizeName.Width > rectName.Width)
            { 
                fontName = new Font("Segoe UI", sztitle--);
                textSizeName = e.Graphics.MeasureString(nameoutfit[0], fontName);
            }
            e.Graphics.DrawString(nameoutfit[0], fontName, new SolidBrush(Color.Black), rectName, stringFormat);


            Rectangle rectOutfit = new Rectangle(e.Bounds.Left, e.Bounds.Bottom-(int)((e.Graphics.DpiY/192)*20), e.Bounds.Width, (int)((e.Graphics.DpiY/192)*30));
            int szoutfit=9;
            Font fontOutfit = new Font("Segoe UI", szoutfit);
            var textSizeOutfit = e.Graphics.MeasureString(nameoutfit[1], fontOutfit);
            while (textSizeOutfit.Width > rectOutfit.Width)
            { 
                fontOutfit = new Font("Segoe UI", szoutfit--);
                textSizeOutfit = e.Graphics.MeasureString(nameoutfit[1], fontOutfit);
            }
            e.Graphics.DrawString(nameoutfit[1], fontOutfit, new SolidBrush(Color.Black), rectOutfit, stringFormat);


            if (card.exclusive != null && (bool)card.exclusive)
            {
                e.Graphics.InterpolationMode = InterpolationMode.High;
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
                               
                GraphicsPath p = new GraphicsPath(); 
                p.AddString(
                    "*",            
                    new FontFamily("Verdana"), 
                    (int) FontStyle.Bold,     
                    e.Graphics.DpiY * 16 / 72,      
                    new Point(e.Bounds.Left + (int)((e.Graphics.DpiY/192)*28), e.Bounds.Top -(int)((e.Graphics.DpiY/192)*2)),            
                    new StringFormat());         
                e.Graphics.DrawPath(new Pen(Color.Yellow, 1), p);
                e.Graphics.FillPath(Brushes.Red, p);     
            }

            if (myData != null && myData.GetCardFavourite(card.name))
            {
                e.Graphics.InterpolationMode = InterpolationMode.High;
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

                GraphicsPath p = new GraphicsPath(); 
                if (fontInstalled)
                     p.AddString(
                        "\uE00B",            
                        new FontFamily("Segoe Fluent Icons"), 
                        (int) FontStyle.Bold,     
                        e.Graphics.DpiY * 14 / 72,      
                        new Point(e.Bounds.Right - (int)((e.Graphics.DpiY/192)*80), e.Bounds.Top +(int)((e.Graphics.DpiY/192)*4) ),            
                        new StringFormat());  
                else
                    p.AddString(
                        "+",            
                        new FontFamily("Verdana"), 
                        (int) FontStyle.Bold,     
                        e.Graphics.DpiY * 16 / 72,      
                        new Point(e.Bounds.Left - (int)((e.Graphics.DpiY/192)*80), e.Bounds.Top -(int)((e.Graphics.DpiY/192)*2)),            
                        new StringFormat());         
                e.Graphics.DrawPath(new Pen(Color.Black, 3), p);
                e.Graphics.FillPath(Brushes.LightGreen, p);     
            }
            int szstar=14;
            if (fontInstalled && Properties.Settings.Default.ShowRatingStars)
            {
                e.Graphics.InterpolationMode = InterpolationMode.High;
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

                string ratingstr = "".PadLeft(5,'\uE0B4');
                Rectangle rect = new Rectangle(e.Bounds.Left+38, e.Bounds.Top + 10, e.Bounds.Width - 38, 40);

                Font font = new Font("Segoe Fluent Icons", 14);
                var textSize = e.Graphics.MeasureString(ratingstr, font);
                while (textSize.Width > rect.Width)
                { 
                   font = new Font("Verdana", szstar--);
                   textSize = e.Graphics.MeasureString(ratingstr, font);
                }
                          
                GraphicsPath p = new GraphicsPath(); 
                p.AddString(
                    ratingstr,            
                    new FontFamily("Segoe Fluent Icons"), 
                    (int) FontStyle.Bold,     
                    e.Graphics.DpiY * szstar / 72,      
                    new Point(e.Bounds.Left + 28, e.Bounds.Top + 114),            
                    new StringFormat());         
                //e.Graphics.DrawPath(new Pen(Color.Black, 3), p);
                e.Graphics.FillPath(new SolidBrush(Color.FromArgb(180, Color.Black)), p);     
            }
            if (myrating > 0 && fontInstalled && Properties.Settings.Default.ShowRatingStars)
            {
                e.Graphics.InterpolationMode = InterpolationMode.High;
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

                string ratingstr = "".PadLeft((int)myrating/2,'\uE0B4');
                if (myrating%2 > 0)
                    ratingstr += '\uE7C6';
                Rectangle rect = new Rectangle(e.Bounds.Left+38, e.Bounds.Top + 10, e.Bounds.Width - 38, 40);          
                GraphicsPath p = new GraphicsPath(); 
                p.AddString(
                    ratingstr,            
                    new FontFamily("Segoe Fluent Icons"), 
                    (int) FontStyle.Bold,     
                    e.Graphics.DpiY * szstar / 72,      
                    new Point(e.Bounds.Left + 28, e.Bounds.Top + 114),            
                    new StringFormat());         
                e.Graphics.DrawPath(new Pen(Color.Black, 3), p);
                e.Graphics.FillPath(Brushes.Yellow, p);     
            }

            if (text != "" )
            {                         
                e.Graphics.InterpolationMode = InterpolationMode.High;
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

                Rectangle rect = new Rectangle(e.Bounds.Left+(int)((e.Graphics.DpiY/192)*34), e.Bounds.Top + (int)((e.Graphics.DpiY/192)*6), e.Bounds.Width - (int)((e.Graphics.DpiY/192)*44), (int)((e.Graphics.DpiY/192)*40));
                int sz =13;
                Font font = new Font("Verdana", sz);
                var textSize = e.Graphics.MeasureString(text, font);
                while (textSize.Width > rect.Width)
                { 
                   font = new Font("Verdana", sz--);
                   textSize = e.Graphics.MeasureString(text, font);
                }
                GraphicsPath p = new GraphicsPath(); 
                p.AddString(
                    text,            
                    new FontFamily("Verdana"), 
                    (int) FontStyle.Regular,     
                    e.Graphics.DpiY * sz / 72,      
                    new Point(e.Bounds.Left + (int)((e.Graphics.DpiY/192)*34), e.Bounds.Top + (int)((e.Graphics.DpiY/192)*6)),            
                    new StringFormat());         
                e.Graphics.DrawPath(new Pen(Color.Black, 3), p);
                e.Graphics.FillPath(Brushes.White, p);            

             
            }

            if (nowPlayingTag == card.modelName + "\r\n" + card.outfit)
            {
                e.Graphics.InterpolationMode = InterpolationMode.High;
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

                Rectangle rect = new Rectangle(imgrect.Left+4, e.Bounds.Top + 10, imgrect.Width-20, 40);  
                int szname =14;
                Font font = new Font("Verdana", szname);
                var textSize = e.Graphics.MeasureString("Playing", font);                
                while (textSize.Width > rect.Width)
                { 
                   font = new Font("Verdana", szname--);
                   textSize = e.Graphics.MeasureString("Playing", font);
                }
                GraphicsPath p = new GraphicsPath(); 
                p.AddString(
                    "Playing",            
                    new FontFamily("Verdana"), 
                    (int) FontStyle.Bold,     
                    e.Graphics.DpiY * szname / 72,      
                    new Point(imgrect.Left+4, e.Bounds.Top +60),            
                    new StringFormat());         
                e.Graphics.FillRectangle(Brushes.Green, new Rectangle(imgrect.Left-6, e.Bounds.Top+60,imgrect.Width-20, (int)textSize.Height));
                e.Graphics.DrawRectangle(new Pen(Color.Black,2), new Rectangle(imgrect.Left-6, e.Bounds.Top+60,imgrect.Width-20, (int)textSize.Height));
                e.Graphics.FillPath(Brushes.White, p);       
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
            else
                cmdClearSearch.Visible = false;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            txtSearch.Text = nowPlayingTag.Split("\r\n")[0];
            PopulateModelListview();
            string[] p = nowPlayingTag.Split("\r\n");
            ModelCard c = Datastore.modelcards.Where(t => t.modelName == p[0] && t.outfit == p[1]).First();
            listModels.SelectedIndices.Clear();
            var i = items.Where(x => x.Text == nowPlayingTag).FirstOrDefault();
            int? index = items.ToList().FindIndex(x => x.Text == nowPlayingTag);            
            if (i != null)
            {
                listModels.SelectedIndices.Add((int)index);
                listModels.FindItemWithText(nowPlayingTag);
                listModels.EnsureVisible((int)index);               
                cmdClearSearch.Visible= true;
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
            Properties.Settings.Default.Save();
            SaveMyData();
        }

        private void cmdNextClip_Click(object sender, EventArgs e)
        {
            this.BeginInvoke((Action)(() => GetNextClip()));

        }

        private void GetNextClip(ModelCard? model = null)
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", false);
            string path = "";
            bool chooseRandom = false;
            if (model != null)
            {
               chooseRandom = true;
            }
            else
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
                    Random rand = new Random();
                    mnew =  clips[rand.Next(clips.Count-1)];
                }
                else
                {
                    ModelClip modelClip = model.clips.Where(x => x.clipName == path.Split("\\")[1]).First();
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

                if (mnew != null && mnew.clipName != null)
                {
                    RegistryKey? keynew = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", true);

                    string r = mnew.clipName;
                    string p = r.Split("_")[0];
                    string full = p + "\\" + r;
                    if (keynew != null)
                    {
                        keynew.SetValue("ForceAnim", full);
                        keynew.Close();
                    }
                }
            }
        }

        private void GetNextCard()
        {
            //find a new model from the filtered cards
            if (items == null || items.Length < 1) return;
            Random r = new Random();
            
            string newtag = nowPlayingTag;
            while (newtag == nowPlayingTag)
            {
                Int64 newr = r.Next(items.Length);
                newtag = items[(int)newr].Text;
                if (items.Length == 1) break;
            }
            listModels.SelectedIndices.Clear();
            int? index = items.ToList().FindIndex(x => x.Text == newtag);
            if (index != null)
            {
                listModels.SelectedIndices.Add((int)index);
                //listModels.Items[i.Index].Selected = true;
                listModels.FindItemWithText(newtag);
                listModels.EnsureVisible((int)index);     
            }
            
            //choose a random clip from those shown
            if (listClips.Items.Count == 0) return;
            var itemnum = r.Next(listClips.Items.Count-1);
            listClips.SelectedItems.Clear();
            var j = listClips.Items[(int)itemnum];
            if (j != null)
            {
                j.Selected = true;
                listClips.Select();
                listClips.EnsureVisible(j.Index);
            }

        }

        private void ReloadStaticProperties()
        {
            StaticPropertiesLoader.loadXML();
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
            var frm = Application.OpenForms.Cast<Form>().Where(x => x.Name == "Filter").FirstOrDefault();
            if (frm == null)
            {
                frm = new Filter(filterSettings);
                frm.StartPosition = FormStartPosition.CenterParent;
                frm.Show(this);                    
                frm.TopMost = true;
            }
            frm.BringToFront();
        }
             
        private void ValidateMinSizeMB()
        {
            Properties.Settings.Default.MinSizeMB = (long)numMinSizeMB.Value;
            if (items != null && items.Length > 0 && listModels.SelectedIndices.Count > 0) loadListClips(listModels.Items[listModels.SelectedIndices[0]].Tag);
         
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
            if (mousedownCard == null)return;
            currentMenuCard=mousedownCard;
            ModelCard? c = Datastore.findCardByTag(mousedownCard.Tag.ToString());
            if (c == null) return;
            if (myData != null && myData.GetCardRating(c.name.ToString()) > 0)
                ratingSlider.Value = myData.GetCardRating(c.name.ToString());
                //cmbMenuCardRating.Text = "My Rating: " + myData.GetCardRating(c.name.ToString());
            else
                ratingSlider.Value = 0;
            if (myData != null) menuCardFavourite.Checked = myData.GetCardFavourite(c.name);
            ratingToolStripMenuItem.Text = "Rating: " + (c.rating-5M).ToString();
            statsToolStripMenuItem.Text = "Stats: " + c.bust + "/" + c.waist + "/" + c.hips;
            nameToolStripMenuItem.Text = c.modelName;
            outfitToolStripMenuItem.Text = c.outfit;
            CultureInfo cultureInfo   = Thread.CurrentThread.CurrentCulture;  
            TextInfo textInfo = cultureInfo.TextInfo;  
            if (c.hair != null) hairToolStripMenuItem.Text =  "Hair: " + textInfo.ToTitleCase(c.hair.ToLower());
            if (c.datePurchased != null) purchasedToolStripMenuItem.Text = "Purchased: " + ((DateTime)c.datePurchased).ToShortDateString();
            hotnessToolStripMenuItem.Text = "Hotness: " + ((Enums.HotnessCode)Convert.ToInt32(c.hotnessLevel)).GetDescription();
            ageToolStripMenuItem.Text = "Age: " + c.modelAge;
        }

        private ListViewItem? mousedownCard=null;
        private ListViewItem? currentMenuCard=null;
        private void listModels_MouseDown(object sender, MouseEventArgs e)
        {
            if ( e.Button == MouseButtons.Right )
            {
                //select the item under the mouse pointer
                Point localPoint = listModels.PointToClient(e.Location);
                mousedownCard = listModels.GetItemAt(e.Location.X,e.Location.Y);
                if ( mousedownCard != null)
                {
                    menuCardList.Show();   
                }        
            }
        }

        private void menuCardFavourite_CheckedChanged(object sender, EventArgs e)
        {
            if (myData==null||currentMenuCard==null)return;
            if (myData.GetCardFavourite(currentMenuCard.Tag.ToString()) != menuCardFavourite.Checked)
            { 
                myData.AddCardFavourite(currentMenuCard.Tag.ToString(), menuCardFavourite.Checked);
                listModels.Invalidate(currentMenuCard.Bounds);
            }
        }

        private void chkFavourite_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.FavouritesFilter = chkFavourite.Checked;
            PopulateModelListview();
        }

        private void cmbMenuCardRating_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (myData==null||currentMenuCard==null)return;
            myData.AddCardRating(currentMenuCard.Tag.ToString(), cmbMenuCardRating.SelectedIndex+1);
        }

        
        private void RatingSlider_ValueChanged(object sender, EventArgs e)
        {
            if (myData==null||currentMenuCard==null)return;
            if (myData.GetCardRating(currentMenuCard.Tag.ToString()) == ratingSlider.Value) return;
            myData.AddCardRating(currentMenuCard.Tag.ToString(), ratingSlider.Value);
            if ( menuShowRatingsStars.Checked)
            {
                listModels.Invalidate(currentMenuCard.Bounds);
                //listModels.Refresh();
            }
        }

        private void chkShowRatingStars_CheckedChanged(object sender, EventArgs e)
        {
            bool r = Properties.Settings.Default.ShowRatingStars;
            if (r != menuShowRatingsStars.Checked)
            {
                Properties.Settings.Default.ShowRatingStars = menuShowRatingsStars.Checked;
                listModels.Refresh();
            }
        }

        private void menuCardList_Closing(object sender, ToolStripDropDownClosingEventArgs e)
        {
            //currentMenuCard = null;
        }

        private void txtUserTags_TextChanged(object sender, EventArgs e)
        {
            if (myData==null)return;
            if (items != null && items.Length > 0)
            {
                List<string> tags = txtUserTags.Text.Split(',').ToList();
                ListView.SelectedIndexCollection col = listModels.SelectedIndices;
                myData.AddCardTags(listModels.Items[col[0]].Tag.ToString(), tags);
            }
        }

        private void listModels_SearchForVirtualItem(object sender, SearchForVirtualItemEventArgs e)
        {
            e.Index = 0;
            if (items != null)
            {       
                ListViewItem? item = items.Where(x => x.Text == e.Text).FirstOrDefault();
                if (item != null) e.Index = item.ImageIndex;
            }     
            return;
          
        }

        private async void cmdPhotos_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(clipListTag))
            {
                CardPhotos photos = new CardPhotos();
                await photos.LoadCardPhotos(client, clipListTag);
                PhotoViewer p = new PhotoViewer();
                p.photos = photos;
                p.Populate();
                p.Show();
                //photos.getPhoto(0);
            }
        }

        private void listModels_DoubleClick(object sender, EventArgs e)
        {
            FilterClips();
            GetNextClip(Datastore.findCardByText(listModels.Items[listModels.SelectedIndices[0]].Text));
        }

        private void includeDescriptionInSearchToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.ShowDescInSearch = includeDescriptionInSearchToolStripMenuItem.Checked;
        }

        private void includeShowTitleInSearchToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.ShowOutfitInSearch = includeShowTitleInSearchToolStripMenuItem.Checked;        
        }
    }
}