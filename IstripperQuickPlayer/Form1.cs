using EnumDescription;
using Gma.System.MouseKeyHook;
using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using Microsoft.Win32;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace IStripperQuickPlayer
{
    public partial class Form1 : Form
    {
        private static RegistryWatcher watcher;
        private string nowPlayingTag = "";
        private int nowPlayingClipNumber;
        private bool changesort = false;

        //global hotkeys
        Combination nextClip;// = Combination.FromString("Control+Alt+N");
        Action actionNextClip = null;
        Combination nextCard;// = Combination.FromString("Control+Alt+C");
        Action actionNextCard = null;

        private void actNextClip()
        {
            if (Properties.Settings.Default.NextClipEnabled) GetNextClip();
        }

        private  void actNextCard()
        {
           if (Properties.Settings.Default.NextCardEnabled) GetNextCard();
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
            listModels.Items.Clear();
            listModels.BeginUpdate();

            List<ModelCard> currentCards;
            if (txtSearch.Text != "") 
                currentCards = Datastore.modelcards.Where(i => i != null).Where(c => c.modelName.Contains(txtSearch.Text, StringComparison.CurrentCultureIgnoreCase)
                || c.tags.Contains(txtSearch.Text)).ToList();
            else
                currentCards = Datastore.modelcards;

            currentCards = Filter(currentCards);

            switch (cmbSortBy.Text)
            {
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
                if (card.clips.Count > 0)
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
            lblModelsLoaded.Text = "Cards Shown: " + listModels.Items.Count + "/" + Datastore.modelcards.Where(c => c.clips.Count > 0).Count();
        }

        private List<ModelCard>? Filter(List<ModelCard>? currentCards)
        {            
            currentCards = currentCards.Where(c => Decimal.Parse(c.modelAge) >= FilterSettings.minAge && Decimal.Parse(c.modelAge) <= FilterSettings.maxAge
                && c.bust >= FilterSettings.minBust && c.bust <= FilterSettings.maxBust     
                && c.rating-5M >= FilterSettings.minRating && c.rating-5M <= FilterSettings.maxRating     
                ).ToList();

            if (!String.IsNullOrEmpty(FilterSettings.tags))
            {
                List<string> taglist = FilterSettings.tags.ToLower().Split("or").Select(p => p.Trim()).ToList();
                currentCards = currentCards.Where(c => c.tags.Any(x => taglist.Contains(x))).ToList();
            }

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
            ModelCard card = Datastore.findCardByTag(tag.ToString());
            lblCipListDetails.Text = card.modelName + ": " +card.outfit;
            listClips.BeginUpdate();
            listClips.Items.Clear();
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
                if (clip.clipName.Contains("demo") && !chkDemo.Checked) addThis = false;
                if (addThis)
                {
                    ListViewItem item = new ListViewItem(new [] {clip.clipNumber.ToString(), clip.clipName, clip.hotnessCode.ToString(), clip.clipType, (clip.size/1024/1024).ToString() +"MB"});
                    listClips.Items.Add(item);
                }
            }
            listClips.EndUpdate();
            txtDescription.Text = card.description;
            lblAge.Text = "Age: "+card.modelAge;
            lblHotness.Text = "Rating: "+(Convert.ToDecimal(card.rating)-5m).ToString();
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
            RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", true); 
            
            string r = listClips.SelectedItems[0].SubItems[1].Text;
            string p = r.Split("_")[0];
            string full = p + "\\" + r;

            key.SetValue("ForceAnim", full);  
            key.Close(); 
            lastchosen = listClips.SelectedItems[0].SubItems[1].Text;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            AssignHooks();
            cmbSortBy.Text = Properties.Settings.Default.SortBy;
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
            RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", false);
            string nowp = key.GetValue("CurrentAnim", "").ToString();
            ShowNowPlaying(nowp);
            key.Close();
        }

        private void RegistryChanged(object? sender, RegistryWatcher.RegistryChangeEventArgs e)
        {
            ShowNowPlaying(e.Value.ToString());
        }

        private void ShowNowPlaying(string path)
        {
            try
            {
                string nowPlaying = path;
                if (path == "") return;
                if (Datastore.modelcards.Count > 0)
                {
                    ModelCard model = Datastore.findCardByTag(path.Split("\\")[0]);
                    if (model == null) return;
                    ModelClip modelClip = model.clips.Where(x => x.clipName == path.Split("\\")[1]).FirstOrDefault();
                    nowPlaying = model.modelName + ", " + model.outfit + " (Clip " + modelClip.clipNumber + ")";
                    //nowPlayingTag = path.Split("\\")[0]
                    nowPlayingTag = model.modelName + "\r\n" + model.outfit;
                    nowPlayingClipNumber = modelClip.clipNumber;
                }
                if (lblNowPlaying != null) lblNowPlaying.BeginInvoke((Action)(() => { lblNowPlaying.Text = "Now Playing: " + nowPlaying;}));
                if (listClips.Items.Count == 0)
                    this.BeginInvoke((Action)(() => NowPlayingClick(true)));
                listModels.BeginInvoke((Action)(() => listModels.Refresh()));
            }
            catch (Exception ex){ }
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
                if (nowPlayingClipNumber != null)
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
            Rectangle imgrect = new Rectangle(e.Bounds.Left + 28, e.Bounds.Top, e.Bounds.Width - 55, e.Bounds.Height - 47);
                
            if (e.Item.Selected)
            {
                   
                //e.DrawFocusRectangle();
                e.Graphics.FillRectangle(Brushes.Bisque, e.Bounds);
            }
            e.Graphics.DrawImage(Datastore.findCardByTag(e.Item.Tag.ToString()).image, imgrect);
            string text = "";
            ModelCard card = Datastore.findCardByTag(e.Item.Tag.ToString());
            switch (cmbSortBy.Text)
            {
                case "":
                case "Model Name":
                    text = "";
                    break;
                case "Rating":
                    text = (Convert.ToDecimal(card.rating)-5m).ToString();
                    break;
                case "Age":
                    text = card.modelAge.ToString();    
                    break;
                case "Ethnicity":
                    text = card.ethnicity.ToString();    
                    break;
                case "Breast Size":
                case "Breast Size (Descending)":
                    text = card.bust.ToString();
                    break;
                case "Date Purchased":
                case "Date Purchased (Descending)":
                    text = card.datePurchased.ToShortDateString();
                    break;
                default:
                    break;
            }

            StringFormat stringFormat = new StringFormat();
            stringFormat.Alignment = StringAlignment.Center;
            stringFormat.LineAlignment = StringAlignment.Far;

            e.Graphics.DrawString(e.Item.Text, new Font("Segoe UI", 9), new SolidBrush(Color.Black), e.Bounds, stringFormat);

            if (text != "" )
            {                         
                e.Graphics.InterpolationMode = InterpolationMode.High;
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

                Rectangle rect = new Rectangle(e.Bounds.Left+34, e.Bounds.Top + 6, e.Bounds.Width - 44, 40);
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
                    new Point(e.Bounds.Left + 34, e.Bounds.Top + 6),            
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

                Rectangle rect = new Rectangle(e.Bounds.Left+38, e.Bounds.Top + 10, e.Bounds.Width - 38, 40);          
                GraphicsPath p = new GraphicsPath(); 
                p.AddString(
                    "Playing",            
                    new FontFamily("Verdana"), 
                    (int) FontStyle.Bold,     
                    e.Graphics.DpiY * 14 / 72,      
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
        }

        private void cmdNextClip_Click(object sender, EventArgs e)
        {
            GetNextClip();

        }

        private void GetNextClip()
        {
            RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", false);
            string path = key.GetValue("CurrentAnim", "").ToString();
            key.Close();
            if (path == "") return;
            if (Datastore.modelcards.Count > 0)
            {
                ModelCard model = Datastore.findCardByTag(path.Split("\\")[0]);
                List<ModelClip> clips = new List<ModelClip>();

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
                    if (clip.clipName.Contains("demo") && !chkDemo.Checked) addThis = false;
                    if (addThis)
                    {
                        clips.Add(clip);
                    }
                }

                ModelClip modelClip = model.clips.Where(x => x.clipName == path.Split("\\")[1]).First();

                ModelClip mnew = null;
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

                if (mnew != null)
                {
                    RegistryKey keynew = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\parameters", true);

                    string r = mnew.clipName;
                    string p = r.Split("_")[0];
                    string full = p + "\\" + r;

                    keynew.SetValue("ForceAnim", full);
                    keynew.Close();
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
                frm = new Filter();
                frm.StartPosition = FormStartPosition.CenterParent;
                frm.Show(this);                    
                frm.TopMost = true;
            }
            frm.BringToFront();
        }
    }
}