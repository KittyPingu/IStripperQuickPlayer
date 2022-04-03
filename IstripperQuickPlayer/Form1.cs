using DesktopWallpaper;
using EnumDescription;
using Gma.System.MouseKeyHook;
using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using Manina.Windows.Forms;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Taskbar;
using Nektra.Deviare2;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using Size = System.Drawing.Size;
//using WebView2.DevTools.Dom;

namespace IStripperQuickPlayer
{
    public partial class Form1 : Form
    {

        [DllImport("dwmapi.dll")]
        static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);
        private float cardScale = 1.0f;
        private bool isAutoSelecting = false;
        private string nowPlayingPath = "";
        private string nowPlayingTag = "";
        private string nowPlayingTagShort = "";
        private string nowPlaying = "";
        private string wallpaperTag = "";
        private int nowPlayingClipNumber;
        private string clipListTag = "";
        private bool changesort = false;
        private MyData? myData = null;
        private bool fontInstalled = false;
        public CardRenderer cardRenderer = null;
        internal FilterSettings filterSettings = new FilterSettings();
        static readonly HttpClient client = new HttpClient();
        private NumberStyles style = NumberStyles.AllowDecimalPoint;
        private CultureInfo culture = CultureInfo.CreateSpecificCulture(CultureInfo.CurrentCulture.Name);
        private Bitmap thumbnail = null;
        //global hotkeys
        Combination? nextClip;// = Combination.FromString("Control+Alt+N");
        Action? actionNextClip = null;
        Combination? nextCard;// = Combination.FromString("Control+Alt+C");
        Action? actionNextCard = null;
        //deviare2 hooking
        private NktSpyMgr _spyMgr;
        private Int32 vghd_procID=0;
        private ControlScrollListener _processListViewScrollListener;
        private int spaceRightOfListModel = 0;
        private int spaceBelowClipList = 0;
        //private WebView2DevToolsContext devtoolsContext = null;

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
            listModelsNew.Items.Clear();
            if (Datastore.modelcards != null)
                Datastore.modelcards.Clear();
            lstLoader.LoadModels();
            this.BeginInvoke((Action)(() => { PopulateModelListview();}));
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
                Datastore.modelcards = Deserialize(modelfilepath);
                this.BeginInvoke((Action)(() => { PopulateModelListview();}));
            }
            else
            {
                this.BeginInvoke((Action)(() => { ReloadModels();}));
            }
        }

        ListViewItem[]? items; //stores the list of virtualized cards for modelList operations
        internal async void PopulateModelListview()
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

            List<ModelCard>? currentCards = Datastore.modelcards;
            if (txtSearch.Text != "")
            {

                string[] parts = txtSearch.Text.ToLower().Split(" and ").Select(p => p.Trim()).ToArray();
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
                            poslist = currentCards.Where(c => (c.modelName != null && c.modelName.ContainsWithNot(tag))
                             || (c.description != null && Properties.Settings.Default.ShowDescInSearch && taglist.Any(d => c.description.ContainsWithNot(d)))
                             || (c.outfit != null && Properties.Settings.Default.ShowOutfitInSearch && taglist.Any(d => c.outfit.ContainsWithNot(d)))
                             || (myData != null && string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(tag)) || string.Join(",", c.tags).ContainsWithNot(tag)).ToList();
                        }
                        if (poslist == null) poslist = new List<ModelCard> { };
                        foreach (string tag in taglist.Where(x => x.Contains("!")))
                        {
                            neglist = neglist.Where(c => (c.modelName != null && c.modelName.ContainsWithNot(tag))
                            && (c.description != null && Properties.Settings.Default.ShowDescInSearch && taglist.Any(d => c.description.ContainsWithNot(d)))
                            && (c.outfit != null && Properties.Settings.Default.ShowOutfitInSearch && taglist.Any(d => c.outfit.ContainsWithNot(d)))
                            && (myData != null && string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(tag)) && string.Join(",", c.tags).ContainsWithNot(tag)).ToList();
                        }
                        if (poslist == null) currentCards = new List<ModelCard> { };
                        else
                            if (neglist == null) neglist = new List<ModelCard> { };
                        currentCards = poslist.Union(neglist).ToList();
                    }
                    else
                    {
                        currentCards = currentCards.Where(c => (c.modelName != null && taglist.Any(y => c.modelName.ContainsWithNot(y)))
                            || (c.description != null && Properties.Settings.Default.ShowDescInSearch && taglist.Any(d => c.description.ContainsWithNot(d)))
                            || (c.outfit != null && Properties.Settings.Default.ShowOutfitInSearch && taglist.Any(d => c.outfit.ContainsWithNot(d)))
                            || myData != null && taglist.Any(x => string.Join(",", myData.GetCardTags(c.name)).ContainsWithNot(x.Trim())) || taglist.Any(y => string.Join(",", c.tags).ContainsWithNot(y))).ToList();
                    }


                }


            }
            else
                currentCards = Datastore.modelcards;

            if (currentCards == null || currentCards.Count == 0) return;
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
                case "Release Date (Descending)":
                    currentCards = currentCards.OrderByDescending(i => i.dateReleased).ToList();
                    break;
                case "Release Date":
                    currentCards = currentCards.OrderBy(i => i.dateReleased).ToList();
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

                //AddCardToWebView(card);
            }
            SetModelNewImageList();
            //FinaliseWebView();
        
            lblModelsLoaded.Text = "Cards Shown: " + listModelsNew.Items.Count + "/" + Datastore.modelcards.Where(c => c.clips != null && c.clips.Count > 0).Count();

            //set the selected card back to what we had selected at start of the function
            if (currentText != "")
            {
                listModelsNew.ClearSelection();
                int? index = items.ToList().FindIndex(x => x.Text == currentText);
                if (index != null && index > 0)
                {
                    listModelsNew.SelectWhere(x => x.Text == currentText);
                    //listModels.Items[i.Index].Selected = true;
                    listModelsNew.EnsureVisible((int)index);
                }
                listModelsNew.Refresh();
            }
            this.BeginInvoke((Action)(() => TaskbarThumbnail()));
        }

        private async void AddCardToWebView(ModelCard card)
        {
            //var r = await devtoolsContext.EvaluateExpressionAsync("document.body.appendChild(document.createElement('img'))");
        }

        private async void FinaliseWebView()
        {
            //var imgs = await devtoolsContext.QuerySelectorAllAsync("img");
            //int i = 0;
            //foreach(var im in imgs)
            //{
            //    ModelCard? card = Datastore.findCardByTag(items[i].Tag.ToString());
            //    ImageConverter _imageConverter = new ImageConverter();
            //    Bitmap m = new Bitmap(card.image.Width, card.image.Height+20);                
            //    Graphics g = Graphics.FromImage(m);
            //    g.DrawImage(card.image, 0, 0);
            //    g.DrawString(card.modelName.ToString(), new Font("Segoe UI", 12), Brushes.Black, 10,card.image.Height-5);
            //    g.Dispose();
            //    byte[] imageArray = (byte[])_imageConverter.ConvertTo(m, typeof(byte[]));
            //    string base64ImageRepresentation = Convert.ToBase64String(imageArray);
            //    var val = $"data: image/png; base64,{base64ImageRepresentation}";
            //    im.SetAttributeAsync("src" , val);
            //    im.SetAttributeAsync("width", card.image.Width);
            //    im.SetAttributeAsync("height", card.image.Height+20);
            //    i++;
            //}

        }


         private void SetModelNewImageList()
         {           
            var itemsNew = new ImageListViewItem[items.Count()];
            int idx = 0;

            cardRenderer.updating = true;
            listModelsNew.SuspendLayout();
            listModelsNew.Items.Clear();
            listModelsNew.ThumbnailSize = new Size((int)(cardScale*162),(int)(242*cardScale));
            foreach(var i in items)
            {
                ModelCard? card = Datastore.findCardByTag(i.Tag.ToString());
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
            currentCards = currentCards.Where(c => c.dateReleased >= filterSettings.minDate && c.dateReleased <= filterSettings.maxDate).ToList();
            if ((filterSettings.minMyRating > 0 || filterSettings.maxMyRating < 10) && myData != null)
                currentCards = currentCards.Where(c => myData.GetCardRating(c.name) >= filterSettings.minMyRating 
                && myData.GetCardRating(c.name) <= filterSettings.maxMyRating).ToList();  
            currentCards = currentCards.Where(c => ((c.modelAge >= filterSettings.minAge && c.modelAge <= filterSettings.maxAge) || c.modelAge == 0 || c.modelAge > 99)
                && ((c.bust >= filterSettings.minBust && c.bust <= filterSettings.maxBust) || c.bust == 0 || c.bust > 99)     
                && c.rating-5M >= filterSettings.minRating && c.rating-5M <= filterSettings.maxRating           
                ).ToList();

            if (!String.IsNullOrEmpty(filterSettings.tags))
            {
                string[] parts = filterSettings.tags.ToLower().Split(" and ").Select(p => p.Trim()).ToArray();


                foreach(string p in parts)
                { 
                    
                    List<string> taglist = p.Split(" or ").Select(p => p.Trim()).ToList();
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

            try
            {
            if (Properties.Settings.Default.ShowKitty)
                currentCards.Add(Datastore.modelcards.Where(c=>c.name=="f9998").First());
            }
            catch (Exception ex){ }

            return currentCards;
        }

        internal void setFilter(string v)
        {
            this.BeginInvoke((Action)(() => { PopulateFilterList(); cmbFilter.SelectedItem = v;}));
        }

        internal List<ModelCard>? Deserialize(String filename)  
        {  
            //Format the object as Binary  
            try
            {
            BinaryFormatter formatter = new BinaryFormatter();  
   
            //Reading the file from the server  
            FileStream fs = System.IO.File.Open(filename, FileMode.Open);   
            object obj = formatter.Deserialize(fs);  
            List<ModelCard>? emps = (List<ModelCard>?)obj;  
            fs.Flush();  
            fs.Close();  
            fs.Dispose(); 

            return emps;
            }
            catch (Exception ex)
            {
                return new List<ModelCard>{ };
            }
        }  

        internal void Serialize(List<ModelCard>? emps, String filename)  
        {  
            //Create the stream to add object into it.  
            System.IO.Stream ms = System.IO.File.OpenWrite(filename);   
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
            
            System.IO.Stream ms = System.IO.File.OpenWrite(mdatafilepath);     
            BinaryFormatter formatter = new BinaryFormatter();              
            formatter.Serialize(ms, myData);  
            ms.Flush();  
            ms.Close();  
            ms.Dispose();  
        }

        internal MyData RetrieveMyData()
        {
            
            try
            {
                string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "mydata.bin");
                if (!System.IO.File.Exists(mdatafilepath)) return new MyData();
                BinaryFormatter formatter = new BinaryFormatter();  
   
                //Reading the file from the server  
                FileStream fs = System.IO.File.Open(mdatafilepath, FileMode.Open);   
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
            ModelCard? card = Datastore.findCardByTag(tag.ToString());           
            if (card == null) return; 
            clipListTag = tag.ToString();
            lblCipListDetails.Text = card.modelName + ": " +card.outfit;
            if (myData != null) 
                txtUserTags.Text = string.Join(",", myData.GetCardTags(tag.ToString()));
            listClips.BeginUpdate();
            listClips.Items.Clear();
            if (card.clips == null) return;

            var currentClips = card.clips;

            string[] parts = txtClipType.Text.ToLower().Split(" and ").Select(p => p.Trim()).ToArray();
            foreach(string p in parts)
            { 
                    
                List<string> taglist = p.Split(" or ").Select(p => p.Trim()).ToList();
                if (p.Contains("!"))
                {
                        
                    List<ModelClip>? poslist=null;
                    List<ModelClip>? neglist=currentClips;
                    foreach (string t in taglist.Where(x => !x.Contains("!")))
                    {
                        //do all the positives first
                        poslist = currentClips.Where(c => (c.clipType != null && c.clipType.ContainsWithNot(t))).ToList();
                    }
                    if (poslist == null) poslist = new List<ModelClip>{ };
                    foreach (string t in taglist.Where(x => x.Contains("!")))
                    {
                        neglist = neglist.Where(c => (c.clipType != null && c.clipType.ContainsWithNot(t))).ToList();
                                         }
                    if (poslist == null) currentClips = new List<ModelClip>{ };
                    else
                        if (neglist == null) neglist = new List<ModelClip>{ };
                        currentClips = poslist.Union(neglist).ToList();
                }
                else
                {
                    currentClips = currentClips.Where(c => (c.clipType != null && taglist.Any(y => c.clipType.ContainsWithNot(y)))).ToList();
                }

                    
            }
                

            foreach(ModelClip clip in currentClips)
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
                //if (!string.IsNullOrEmpty(txtClipType.Text) && !clip.clipType.ToLower().Contains(txtClipType.Text.ToLower())) addThis = false;
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

        private async void Form1_Load(object sender, EventArgs e)
        {
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
            cmbSortBy.Text = Properties.Settings.Default.SortBy;
            chkFavourite.Checked = Properties.Settings.Default.FavouritesFilter;
            menuShowRatingsStars.Checked = Properties.Settings.Default.ShowRatingStars;
            includeDescriptionInSearchToolStripMenuItem.Checked = Properties.Settings.Default.ShowDescInSearch;
            includeShowTitleInSearchToolStripMenuItem.Checked = Properties.Settings.Default.ShowOutfitInSearch;
            trackBarCardScale.Value = (decimal)(Properties.Settings.Default.CardScale);
            if (Properties.Settings.Default.MinSizeMB != 0)
            {
                numMinSizeMB.Value = Properties.Settings.Default.MinSizeMB;
            }

            //get number of monitors for wallpaper
            try
            {
                var wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());
                string[] monitorsChecked = Properties.Settings.Default.WallpaperMonitors.Split(",", StringSplitOptions.TrimEntries);
                for (uint i = 0; i < wallpaper.GetMonitorDevicePathCount(); i++)
                {
                    ToolStripMenuItem newitem = new ToolStripMenuItem("Monitor " + (i+1).ToString());
                    newitem.CheckOnClick = true;
                    newitem.CheckedChanged += WallpaperMonitor_CheckedChanged;
                    newitem.Tag = i;
                    if (monitorsChecked.Contains((i+1).ToString())) newitem.Checked = true;
                    this.wallpaperToolStripMenuItem.DropDownItems.Add(newitem);
                }
            }
            catch { }
            trackbarWallpaperBrightness.Value = Properties.Settings.Default.WallpaperBrightness;
            automaticWallpaperToolStripMenuItem.Checked = Properties.Settings.Default.AutoWallpaper;
            showTextToolStripMenuItem.Checked = Properties.Settings.Default.WallpaperDetails;
            showKittyToolStripMenuItem.Checked = Properties.Settings.Default.ShowKitty;
            myData = RetrieveMyData();
            FilterSettingsList.Load();
            PopulateFilterList();
            if (cardRenderer == null)
            {
                cardRenderer = new CardRenderer(myData, cmbSortBy.Text, cardScale, culture, fontInstalled, style);  
                listModelsNew.SetRenderer(cardRenderer);
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
            Task.Run(() => SetupRegHooks());       
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
                (senderComboBox.Items.Count>senderComboBox.MaxDropDownItems)
                ?SystemInformation.VerticalScrollBarWidth:0;

            int newWidth;
            foreach (string s in senderComboBox.Items)
            {
                newWidth = (int) g.MeasureString(s, font).Width 
                    + vertScrollBarWidth;
                if (width < newWidth )
                {
                    width = newWidth;
                }
            }
            senderComboBox.DropDownWidth = width;
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
                hook = _spyMgr.CreateHook("KernelBase.dll!RegSetValueExW", (int)(eNktHookFlags.flgAutoHookChildProcess | eNktHookFlags.flgOnlyPreCall));
                hook.Hook(true);
                if (tempProcess.Name.Equals("vghd.exe", StringComparison.InvariantCultureIgnoreCase) && tempProcess.PlatformBits == 32)
                {
                    hook.Attach(tempProcess, true);
                    vghd_procID = tempProcess.Id;
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
                    while ((res == null || res2 == null) && !found)
                    {
                        //play a clip from a filtered card instead
                        //find a new model from the filtered cards
                        if (items == null || items.Length < 1) return;
                        string newtag = nowPlayingTag;
                        Random r = new Random();  
                        if (res == null) //choose a different card
                        {                                      
                            while (newtag == nowPlayingTag)
                            {
                                Int64 newr = r.Next(items.Length);
                                newtag = items[(int)newr].Text;
                                if (items.Length == 1) break;
                            }
                            listModelsNew.Invoke((Action)(() => listModelsNew.ClearSelection()));
                            int? index = items.ToList().FindIndex(x => x.Text == newtag);
                            if (index != null)
                            {
                                listModelsNew.Invoke((Action)(() => listModelsNew.SelectWhere(x => x.Tag == newtag)));
                            }
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
            if (found) this.BeginInvoke((Action)(() => TaskbarThumbnail()));
            isAutoSelecting = true;
            return;
        }

        private List<ModelClip>? FilterClipList(List<ModelClip> clips)
        {
            var currentClips = clips;

            string[] parts = txtClipType.Text.ToLower().Split(" and ").Select(p => p.Trim()).ToArray();
            foreach(string p in parts)
            { 
                    
                List<string> taglist = p.Split(" or ").Select(p => p.Trim()).ToList();
                if (p.Contains("!"))
                {
                        
                    List<ModelClip>? poslist=null;
                    List<ModelClip>? neglist=currentClips;
                    foreach (string t in taglist.Where(x => !x.Contains("!")))
                    {
                        //do all the positives first
                        poslist = currentClips.Where(c => (c.clipType != null && c.clipType.ContainsWithNot(t))).ToList();
                    }
                    if (poslist == null) poslist = new List<ModelClip>{ };
                    foreach (string t in taglist.Where(x => x.Contains("!")))
                    {
                        neglist = neglist.Where(c => (c.clipType != null && c.clipType.ContainsWithNot(t))).ToList();
                                         }
                    if (poslist == null) currentClips = new List<ModelClip>{ };
                    else
                        if (neglist == null) neglist = new List<ModelClip>{ };
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
                if (path == nowPlayingPath) return;
                nowPlayingPath = path;
                nowPlaying = "";
                if (path == "") return;
                if (Datastore.modelcards == null) return;
                if (Datastore.modelcards.Count > 0)
                {
                    ModelCard? model = Datastore.findCardByTag(path.Split("\\")[0]);
                    if (model == null) return;
                    ModelClip? modelClip = model.clips.Where(x => x.clipName == path.Split("\\")[1]).FirstOrDefault();
                    if (modelClip == null) return;
                    nowPlaying = model.modelName + ", " + model.outfit + " (Clip " + modelClip.clipNumber + ")";
                    nowPlayingTagShort = path.Split("\\")[0];
                    nowPlayingTag = model.modelName + "\r\n" + model.outfit;
                    nowPlayingClipNumber = Convert.ToInt32(modelClip.clipNumber);
                }
                if (lblNowPlaying != null) lblNowPlaying.BeginInvoke((Action)(() => { lblNowPlaying.Text = "Now Playing: " + nowPlaying;}));
                if (listClips.Items.Count == 0)
                    this.BeginInvoke((Action)(() => NowPlayingClick(true)));
                cardRenderer.nowPlayingTag = nowPlayingTag;
                listModelsNew.BeginInvoke((Action)(() => listModelsNew.Refresh()));
            }
            catch { }
            this.BeginInvoke((Action)(() => TaskbarThumbnail()));
            if (doWallpaper) lblNowPlaying.BeginInvoke((Action)(() => { lblNowPlaying.Text = "Now Playing: " + nowPlaying;}));
            if (Properties.Settings.Default.AutoWallpaper && doWallpaper) this.BeginInvoke((Action)(() => ChangeWallpaper()));
        }

        private void chk_CheckedChanged(object sender, EventArgs e)
        {
            changesort = true;
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
            Rectangle imgrect = new Rectangle(e.Bounds.Left + (int)((e.Graphics.DpiY/192)*42), e.Bounds.Top, e.Bounds.Width - (int)((e.Graphics.DpiY/192)*69), e.Bounds.Height - (int)((e.Graphics.DpiY/192)*54));
                
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
                    decimal decheight = 0;
                    decimal.TryParse(card.height, style, culture, out decheight);
                    if (RegionInfo.CurrentRegion.IsMetric && CultureInfo.CurrentCulture.Name != "en-GB")
                       text = (((Math.Floor(decheight)*12) + (decheight-Math.Floor(decheight))*10) * 2.54M).ToString("N1") + "cm";
                    else
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
                    text = (card.modelAge.ToString() ?? "");    
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
                case "Release Date":
                case "Release Date (Descending)":
                    if (card.dateReleased != null)
                        text = ((DateTime)card.dateReleased).ToShortDateString();
                    break;
                default:
                    break;
            }

            StringFormat stringFormat = new StringFormat();
            stringFormat.Alignment = StringAlignment.Center;
            stringFormat.LineAlignment = StringAlignment.Far;

            Rectangle rectName = new Rectangle(e.Bounds.Left, e.Bounds.Bottom-(int)((e.Graphics.DpiY/192)*52), e.Bounds.Width, (int)((e.Graphics.DpiY/192)*30));
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


            Rectangle rectOutfit = new Rectangle(e.Bounds.Left, e.Bounds.Bottom-(int)((e.Graphics.DpiY/192)*27), e.Bounds.Width, (int)((e.Graphics.DpiY/192)*30));
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
                    "\uEC19",            
                    new FontFamily("Segoe Fluent Icons"), 
                    (int) FontStyle.Bold,     
                    e.Graphics.DpiY * cardScale *10 / 72,      
                    new Point(e.Bounds.Left + (int)((e.Graphics.DpiY/192)*38), e.Bounds.Top +(int)((e.Graphics.DpiY/192)*4) ),            
                    new StringFormat());         
                e.Graphics.DrawPath(new Pen(Color.Black, 1), p);
                e.Graphics.FillPath(Brushes.Yellow, p);     
                if (card.hotnessLevel == "5")
                {
                    p = new GraphicsPath(); 
                    p.AddString(
                        "\uEC8A",            
                        new FontFamily("Segoe Fluent Icons"), 
                        (int) FontStyle.Bold,     
                        e.Graphics.DpiY * cardScale * 10 / 72,      
                        new Point(e.Bounds.Left + (int)((e.Graphics.DpiY/192)*38) + (int)((e.Graphics.DpiY/192)*30*cardScale), e.Bounds.Top +(int)((e.Graphics.DpiY/192)*4)),            
                        new StringFormat());         
                    e.Graphics.DrawPath(new Pen(Color.Black, 1), p);
                    e.Graphics.FillPath(Brushes.Yellow, p);     
                }
            }

            else if (card.hotnessLevel == "5")
            {
                 e.Graphics.InterpolationMode = InterpolationMode.High;
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
                               
                GraphicsPath p = new GraphicsPath(); 
                p.AddString(
                    "\uEC8A",            
                    new FontFamily("Segoe Fluent Icons"), 
                    (int) FontStyle.Bold,     
                    e.Graphics.DpiY * cardScale * 10 / 72,      
                    new Point(e.Bounds.Left + (int)((e.Graphics.DpiY/192)*38), e.Bounds.Top +(int)((e.Graphics.DpiY/192)*4)),            
                    new StringFormat());         
                e.Graphics.DrawPath(new Pen(Color.Black, 1), p);
                e.Graphics.FillPath(Brushes.Yellow, p);     
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
                        e.Graphics.DpiY * cardScale * 14 / 72,      
                        new Point(e.Bounds.Right - (int)((e.Graphics.DpiY/192)*34) - (int)((e.Graphics.DpiY/192)*46*cardScale), e.Bounds.Top +(int)((e.Graphics.DpiY/192)*4) ),            
                        new StringFormat());  
                else
                    p.AddString(
                        "+",            
                        new FontFamily("Verdana"), 
                        (int) FontStyle.Bold,     
                        e.Graphics.DpiY * cardScale * 16 / 72,      
                        new Point(e.Bounds.Left - (int)((e.Graphics.DpiY/192)*80), e.Bounds.Top -(int)((e.Graphics.DpiY/192)*2)),            
                        new StringFormat());         
                e.Graphics.DrawPath(new Pen(Color.Black, 3), p);
                e.Graphics.FillPath(Brushes.LightGreen, p);     
            }
            int szstar=(int)(14* cardScale);
            if (fontInstalled && Properties.Settings.Default.ShowRatingStars)
            {
                e.Graphics.InterpolationMode = InterpolationMode.High;
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

                string ratingstr = "".PadLeft(5,'\uE0B4');
                Rectangle rect = new Rectangle(e.Bounds.Left+52, e.Bounds.Top + 10, e.Bounds.Width - (int)(52*cardScale), 40);

                Font font = new Font("Segoe Fluent Icons", 14 * cardScale);
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
                    new Point(e.Bounds.Left + 52, e.Bounds.Top + (int)(114*cardScale)),            
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
                Rectangle rect = new Rectangle(e.Bounds.Left+52, e.Bounds.Top + (int)(10*cardScale), e.Bounds.Width - (int)(52*cardScale), 40);          
                GraphicsPath p = new GraphicsPath(); 
                p.AddString(
                    ratingstr,            
                    new FontFamily("Segoe Fluent Icons"), 
                    (int) FontStyle.Bold,     
                    e.Graphics.DpiY * szstar / 72,      
                    new Point(e.Bounds.Left + 52, e.Bounds.Top + (int)(114*cardScale)),            
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

                Rectangle rect = new Rectangle(e.Bounds.Left+(int)((e.Graphics.DpiY/192)*48), e.Bounds.Top + (int)((e.Graphics.DpiY/192)*6), e.Bounds.Width - (int)((e.Graphics.DpiY/192)*58), (int)((e.Graphics.DpiY/192)*40));
                int sz =(int)(13* cardScale);
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
                    new Point(e.Bounds.Left + (int)((e.Graphics.DpiY/192)*48), e.Bounds.Top + (int)((e.Graphics.DpiY/192)*6)),            
                    new StringFormat());         
                e.Graphics.DrawPath(new Pen(Color.Black, 3), p);
                e.Graphics.FillPath(Brushes.White, p);            

             
            }

            bool isPlaying = false;
            if (nowPlayingTag == card.modelName + "\r\n" + card.outfit)
            {
                isPlaying = true;
                e.Graphics.InterpolationMode = InterpolationMode.High;
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

                Rectangle rect = new Rectangle(imgrect.Left+18, e.Bounds.Top + 10, imgrect.Width-34, 40);  
                int szname =(int)(14 * cardScale);
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
                    new Point(imgrect.Left+18, e.Bounds.Top + (int)(60*cardScale)),            
                    new StringFormat());         
                e.Graphics.FillRectangle(Brushes.Green, new Rectangle(imgrect.Left-6, e.Bounds.Top+(int)(60*cardScale),imgrect.Width-20, (int)textSize.Height));
                e.Graphics.DrawRectangle(new Pen(Color.Black,2), new Rectangle(imgrect.Left-6, e.Bounds.Top+(int)(60*cardScale),imgrect.Width-20, (int)textSize.Height));
                e.Graphics.FillPath(Brushes.White, p);       
            }

                  
            //if (isPlaying)
            //{
            //      thumbnailclip = new Rectangle(listModels.Location.X + e.Bounds.X, listModels.Location.Y + e.Bounds.Y, e.Bounds.Width, e.Bounds.Height);
            //      this.BeginInvoke((Action)(() => TaskbarThumbnail()));
            //}

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
            Wallpaper.RestoreWallpaper();
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
                    if (clips.Count == 0) return;
                    Random rand = new Random();
                    mnew =  clips[rand.Next(clips.Count-1)];
                }
                else
                {
                    ModelClip modelClip = clips.Where(x => x.clipName == path.Split("\\")[1]).First();
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
            this.BeginInvoke((Action)(() => TaskbarThumbnail()));
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
            listModelsNew.ClearSelection();
            int? index = items.ToList().FindIndex(x => x.Text == newtag);
            if (index != null)
            {
                listModelsNew.SelectWhere(x => x.Text == newtag);
                listModelsNew.EnsureVisible((int)index);     
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
                if (cmbFilter.SelectedItem != null) f = cmbFilter.SelectedItem.ToString();
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

        private ImageListViewItem? mousedownCard=null;
        private ImageListViewItem? currentMenuCard=null;
        private Rectangle? thumbnailclip;

        private void listModels_MouseDown(object sender, MouseEventArgs e)
        {
            if ( e.Button == MouseButtons.Right )
            {
                //select the item under the mouse pointer
                Point localPoint = listModelsNew.PointToClient(e.Location);
                //mousedownCard = listModelsNew.GetItemAt(e.Location.X,e.Location.Y);
                //if ( mousedownCard != null)
                //{
                //    menuCardList.Show();   
                //}        
            }
        }

        private void menuCardFavourite_CheckedChanged(object sender, EventArgs e)
        {
            if (myData==null||currentMenuCard==null)return;
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
                currentMenuCard.Update();
                //listModels.Refresh();
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
        }

        private void txtUserTags_TextChanged(object sender, EventArgs e)
        {
            if (myData==null)return;
            if (items != null && items.Length > 0)
            {
                List<string> tags = txtUserTags.Text.Split(',').ToList();
                if (clipListTag != "") myData.AddCardTags(clipListTag, tags);
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
                this.Cursor = Cursors.WaitCursor;
                CardPhotos photos = new CardPhotos();
                await photos.LoadCardPhotos(client, clipListTag);
                PhotoViewer p = new PhotoViewer();
                p.photos = photos;
                p.Populate();
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
            filterSettings = FilterSettingsList.GetFilter(cmbFilter.SelectedItem.ToString());
            PopulateModelListview();
        }

        private void txtClipType_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                changesort = true;
                FilterClips();
            }
        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            spaceBelowClipList = this.Height - listClips.Bottom;
            spaceRightOfListModel = this.Width - listModelsNew.Right;
            try
            {
                TaskbarThumbnail();

                string cmdPath = Assembly.GetEntryAssembly().Location;
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
            catch (Exception ex)
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

        private void nextclipButton_click(object sender, ThumbnailButtonClickedEventArgs e)
        {
            this.BeginInvoke((Action)(() => GetNextClip()));
        }

        private void nextclipModel_click(object sender, ThumbnailButtonClickedEventArgs e)
        {
            GetNextCard();
        }

        private void lblNowPlaying_TextChanged(object sender, EventArgs e)
        {
            string t = "iStripper QuickPlayer";
            if (lblNowPlaying.Text.Length > 14) t =lblNowPlaying.Text.Substring(13);
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
            if (spaceRightOfListModel == 0) return;
            listModelsNew.Width = this.Width - listModelsNew.Left - this.spaceRightOfListModel;
            txtSearch.Width = listModelsNew.Right - txtSearch.Left;
            listClips.Left = listModelsNew.Right + 66;
            listClips.Height = this.Height - this.spaceBelowClipList - listClips.Top;
            listModelsNew.Height = this.Height - listModelsNew.Top - 60;
            //lblNowPlaying.Left = listModels.Right + 66;
            //lblCipListDetails.Left = listModels.Right + 66;
            cmdClearSearch.Left = listModelsNew.Right;
            cmdPhotos.Left = listClips.Right - cmdPhotos.Width;
            panelModelDetails.Left = listModelsNew.Right + 45;
            panelModelDetails.Top = listClips.Bottom + 8;
            panelClip.Left = listModelsNew.Right + 58;
            this.BeginInvoke(new Action(() => listModelsNew.Refresh()));
        }

        private async void button2_Click(object sender, EventArgs e)
        {
            ChangeWallpaper();
        }

        private async Task ChangeWallpaper(bool NotFromCheck = true)
        {
            System.Diagnostics.Debug.WriteLine("ChangeWallpaper called with nowPlayingTagShort=" + nowPlayingTagShort  +", lbl=" + lblNowPlaying.Text);
            if (nowPlayingTagShort == null || nowPlayingTagShort.Length == 0) return;
            if (string.IsNullOrEmpty(lblNowPlaying.Text)) return;
            foreach (var item in wallpaperToolStripMenuItem.DropDownItems)
            {
                if (item is ToolStripMenuItem)
                if (((ToolStripMenuItem)item).Checked && ((ToolStripMenuItem)item).Tag != null)
                {
                    if ((NotFromCheck || Properties.Settings.Default.AutoWallpaper))
                    {
                        CardPhotos photos = new CardPhotos();
                        await photos.LoadCardPhotos(client, nowPlayingTagShort);
                        Random r = new Random();
                        Wallpaper.ChangeWallpaper((uint)((ToolStripMenuItem)item).Tag, photos.getRandomWidescreenURL());
                    }
                }
                else if (((ToolStripMenuItem)item).Tag != null)
                {
                    Wallpaper.RestoreWallpaperByID((uint)((ToolStripMenuItem)item).Tag);
                }

            }
        }

        private void WallpaperMonitor_CheckedChanged(object? sender, EventArgs e)
        {
            string m = "";
            foreach (var item in wallpaperToolStripMenuItem.DropDownItems)
            {
                if (item is ToolStripMenuItem)
                {
                    if (((ToolStripMenuItem)item).Checked && ((ToolStripMenuItem)item).Tag != null)
                    {
                        if (m == "")
                            m += ((uint)((ToolStripMenuItem)item).Tag+1).ToString();
                        else
                            m += "," + ((uint)((ToolStripMenuItem)item).Tag+1).ToString();                    
                    }
                }
            }
            Properties.Settings.Default.WallpaperMonitors = m;
            if (nowPlayingTag != "") this.BeginInvoke((Action)(() => ChangeWallpaper(false)));
        }

        private void automaticWallpaperToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.AutoWallpaper = automaticWallpaperToolStripMenuItem.Checked;
        }

        private void trackbarWallpaperBrightness_ValueChanged(object sender, EventArgs e)
        {
            trackbarWallpaperBrightness.MouseUp += trackbarWallpaperBrightness_MouseUp;
            trackbarWallpaperBrightness.ValueChanged -= trackbarWallpaperBrightness_ValueChanged;
        }

        private async void trackbarWallpaperBrightness_MouseUp(object sender, EventArgs e)
        {
            trackbarWallpaperBrightness.MouseUp -= trackbarWallpaperBrightness_MouseUp;
            trackbarWallpaperBrightness.ValueChanged += trackbarWallpaperBrightness_ValueChanged;

            Properties.Settings.Default.WallpaperBrightness = trackbarWallpaperBrightness.Value;
            this.BeginInvoke((Action)(() => Wallpaper.ChangeBrightness()));
        }

        private void showTextToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.WallpaperDetails = showTextToolStripMenuItem.Checked;     
            this.BeginInvoke((Action)(() => ChangeWallpaper()));
        }

        private void showKittyToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ShowKitty = showKittyToolStripMenuItem.Checked;
            this.BeginInvoke((Action)(() => { PopulateModelListview();}));
        }

        private void trackBarCardScale_ValueChanged(object sender, EventArgs e)
        {
            cardScale = (float)(trackBarCardScale.Value);
            if (listModelsNew.Items.Count > 0)
            {
                if (cardRenderer != null)
                    cardRenderer.cardScale = cardScale;
                listModelsNew.ThumbnailSize = new Size((int)(cardScale*162),(int)(242*cardScale));
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
            
        }

        private void listModelsNew_MouseDown(object sender, MouseEventArgs e)
        {
            if ( e.Button == MouseButtons.Right )
            {
                ImageListView.HitInfo hit;
                listModelsNew.HitTest(e.Location,out hit);
                if (hit.ItemHit) mousedownCard = listModelsNew.Items[hit.ItemIndex];
                else mousedownCard = null;
                if (mousedownCard != null)
                {
                    menuCardList.Show();
                }
            }
        }
    }
}