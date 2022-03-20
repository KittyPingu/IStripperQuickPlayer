using EnumDescription;
using Gma.System.MouseKeyHook;
using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using Microsoft.Win32;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace IStripperQuickPlayer
{
    public partial class Form1 : Form
    {
        private static RegistryWatcher? watcher;
        private string nowPlayingTag = "";
        private int nowPlayingClipNumber;
        private bool changesort = false;
        private MyData? myData = null;
        private bool fontInstalled = false;
        internal FilterSettings filterSettings = new FilterSettings();

        //global hotkeys
        Combination? nextClip;// = Combination.FromString("Control+Alt+N");
        Action? actionNextClip = null;
        Combination? nextCard;// = Combination.FromString("Control+Alt+C");
        Action? actionNextCard = null;

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

        internal void PopulateModelListview()
        {
            listModels.BeginUpdate();
            listModels.Items.Clear();
            if (Datastore.modelcards == null)
            {
                listModels.EndUpdate();
                return;
            }

            List<ModelCard>? currentCards;
            if (txtSearch.Text != "") 
                currentCards = Datastore.modelcards.Where(i => i != null).Where(c => c.modelName != null && c.modelName.Contains(txtSearch.Text, StringComparison.CurrentCultureIgnoreCase)
                || c.tags.Contains(txtSearch.Text)).ToList();
            else
                currentCards = Datastore.modelcards;

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


            ListViewItem[] items = new ListViewItem[currentCards.Count()];
            int idx = 0;
            foreach (var card in currentCards)
            {
                if (card.clips != null && card.clips.Count > 0)
                {   
                    items[idx] = new ListViewItem(card.modelName + Environment.NewLine + card.outfit, 0);
                    items[idx].Tag = card.name;
                    idx++;
                    //listModels.Items.Add(card.name, card.modelName + Environment.NewLine + card.outfit, largeimagelist.Images.Count - 1);
                }
            }

            ImageList blankimagelist = new ImageList();
            blankimagelist.ImageSize = new Size(130, 180);
            blankimagelist.ColorDepth = ColorDepth.Depth32Bit;
            Image newblankimage = new Bitmap(130,180, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            blankimagelist.Images.Add(newblankimage);

            listModels.Items.AddRange(items);
            listModels.LargeImageList = blankimagelist;
            listModels.EndUpdate();
            lblModelsLoaded.Text = "Cards Shown: " + listModels.Items.Count + "/" + Datastore.modelcards.Where(c => c.clips != null && c.clips.Count > 0).Count();
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
                    currentCards = currentCards.Where(c => myData != null && myData.GetCardTags(c.name).Any(x => taglist.Contains(x.Trim())) || c.tags.Any(y => taglist.Contains(y))).ToList();
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
              if (listModels.SelectedItems.Count > 0)
                loadListClips(listModels.SelectedItems[0].Tag);
        }

        private void loadListClips(object tag)
        {
            ModelCard? card = Datastore.findCardByTag(tag.ToString());           
            if (card == null) return; 
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
                key.SetValue("ForceAnim", full);  
                key.Close(); 
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
            AssignHooks();
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
            if (Properties.Settings.Default.MinSizeMB != 0)
            {
                numMinSizeMB.Value = Properties.Settings.Default.MinSizeMB;
            }
            LoadDefaultFilters();
            myData = RetrieveMyData();
            listModels.SetDoubleBuffered();
            string REG_KEY = @"HKEY_CURRENT_USER\Software\Totem\vghd\parameters";
            watcher = new RegistryWatcher(new Tuple<string, string>(REG_KEY, "CurrentAnim"));
            watcher.RegistryChange += RegistryChanged;
            clickingNowPlaying = true;
            GetNowPlaying();
            RetrieveModels();
            GetNowPlaying();
            clickingNowPlaying = false;
        }

        private void AssignHooks()
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

        private void RegistryChanged(object? sender, RegistryWatcher.RegistryChangeEventArgs e)
        {
            string newcardstring = e.Value.ToString() ?? "";
            if (string.IsNullOrEmpty(newcardstring))
            {
                this.BeginInvoke((Action)(() => lblNowPlaying.Text = ""));
                return;
            }
            if (!EnforceNowPlaying(newcardstring))
                ShowNowPlaying(newcardstring);
        }

        private bool EnforceNowPlaying(string nowplaying)
        {
            ModelCard? model = Datastore.findCardByTag(nowplaying.Split("\\")[0]);   
            ListViewItem? res = null;
            if (model == null) return false;
            this.Invoke((Action)(() => res = listModels.FindItemWithText(model.modelName + "\r\n" + model.outfit)));
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
                listModels.SelectedItems.Clear();
                var i = listModels.FindItemWithText(nowPlayingTag);
                if (i != null)
                {
                    i.Selected = true;
                    listModels.Select();
                    listModels.EnsureVisible(i.Index);
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
            e.Graphics.DrawImage(card.image, imgrect);
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

                Rectangle rect = new Rectangle(e.Bounds.Left+38, e.Bounds.Top + 10, e.Bounds.Width - 66, 40);  
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
                    new Point(e.Bounds.Left + 38, e.Bounds.Top +60),            
                    new StringFormat());         
                e.Graphics.DrawPath(new Pen(Color.Green, 5), p);
                e.Graphics.FillPath(Brushes.White, p);       
            }

           

        }

        private void txtSearch_TextChanged(object sender, EventArgs e)
        {
            cmdClearSearch.Visible = (txtSearch.Text.Length > 0);
        }

        private void txtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
                PopulateModelListview();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            txtSearch.Text = nowPlayingTag.Split("\r\n")[0];
            PopulateModelListview();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            txtSearch.Text = "";
            PopulateModelListview();
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

        private void GetNextClip()
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", false);
            if (key == null) return;
            var a = key.GetValue("CurrentAnim", "");
            if (a == null) return;
            string path = a.ToString() ?? "";
            key.Close();
            if (path == "") return;
            if (Datastore.modelcards == null) return;
            if (Datastore.modelcards.Count > 0)
            {
                ModelCard? model = Datastore.findCardByTag(path.Split("\\")[0]);
                if (model == null) return;
                List<ModelClip> clips = new List<ModelClip>();
                if (model.clips == null) return;
                foreach (ModelClip clip in model.clips)
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
                    if (clip.clipName != null && clip.clipName.Contains("demo") && !chkDemo.Checked) addThis = false;
                    if (addThis)
                    {
                        clips.Add(clip);
                    }
                }

                ModelClip modelClip = model.clips.Where(x => x.clipName == path.Split("\\")[1]).First();

                ModelClip? mnew = null;
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
            if (listModels.Items.Count < 2) return;
            Random r = new Random();
            
            string newtag = nowPlayingTag;
            while (newtag == nowPlayingTag)
            {
                Int64 newr = r.Next(listModels.Items.Count);
                newtag = listModels.Items[(int)newr].Text;
            }
            listModels.SelectedItems.Clear();
            var i = listModels.FindItemWithText(newtag);
            if (i != null)
            {
                i.Selected = true;
                listModels.Select();
                listModels.EnsureVisible(i.Index);
            }
            
            //choose a random clip from those shown
            if (listClips.Items.Count == 0) return;
            var itemnum = r.Next(listClips.Items.Count);
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
            AssignHooks();
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
            if (listModels.SelectedItems.Count > 0) loadListClips(listModels.SelectedItems[0].Tag);
         
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
                this.BeginInvoke((Action)(() => listModels.Refresh()));
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
            myData.AddCardRating(currentMenuCard.Tag.ToString(), ratingSlider.Value);
            if ( menuShowRatingsStars.Checked)
            {
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
            if (listModels.SelectedItems.Count > 0)
            {
                List<string> tags = txtUserTags.Text.Split(',').ToList();
                myData.AddCardTags(listModels.SelectedItems[0].Tag.ToString(), tags);
            }
        }
    }
}