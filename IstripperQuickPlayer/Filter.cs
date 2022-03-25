using IStripperQuickPlayer.DataModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using IStripperQuickPlayer.BLL;

namespace IStripperQuickPlayer
{
    public partial class Filter : Form
    {
        ColorSlider.ColorSlider? rangeRating;
        ColorSlider.ColorSlider? rangeAge;
        ColorSlider.ColorSlider? rangeBreastSize;
        ColorSlider.ColorSlider? rangeMyRating;
        bool isLoaded = false;
        bool ok = false;
        public FilterSettings? filterSettings;
        byte[]? savedSettings;
        internal Filter(FilterSettings filter)
        {
            filterSettings = filter;
            Save();
            InitializeComponent();
            ReadValues();
            this.Controls.Add(rangeRating);
            this.Controls.Add(rangeMyRating);
            this.Controls.Add(rangeBreastSize);
            this.Controls.Add(rangeAge);
            isLoaded = true;
        }

        private void ReadValues()
        {
            float dx, dy;

            Graphics g = this.CreateGraphics();
            try
            {
                dx = g.DpiX;
                dy = g.DpiY;
            }
            finally
            {
                g.Dispose();
            }
            if (filterSettings == null) return;
            rangeRating = new ColorSlider.ColorSlider();
            rangeRating.Location = new Point(Convert.ToInt32(110*dx/120), Convert.ToInt32(100*dx/120));
            rangeRating.Height = Convert.ToInt32(70*dx/120);
            rangeRating.Width = Convert.ToInt32(650*dx/120);
            rangeRating.ForeColor = Color.Black;
            rangeRating.Minimum = 0;
            rangeRating.Maximum = 5;
            rangeRating.ScaleDivisions = 10;
            rangeRating.SmallChange = 0.1M;
            rangeRating.LargeChange = 0.5M;
            rangeRating.Value = filterSettings.minRating;
            rangeRating.Value2 = filterSettings.maxRating;
            rangeRating.BackColor = Color.Transparent;
            rangeRating.TickColor = Color.Black;
            rangeRating.ElapsedInnerColor = Color.Green;
            rangeRating.ValueChanged += Range_ValueChanged;
            

            rangeBreastSize = new ColorSlider.ColorSlider();
            rangeBreastSize.Location = new Point(Convert.ToInt32(110*dx/120), Convert.ToInt32(186*dx/120));
            rangeBreastSize.Height = Convert.ToInt32(60*dx/120);
            rangeBreastSize.Width = Convert.ToInt32(650*dx/120);
            rangeBreastSize.ForeColor = Color.Black;
            rangeBreastSize.Minimum = 0;
            var dmin = Datastore.modelcards.Min(x => x.bust);
            if (dmin != null)
                rangeBreastSize.Minimum = Math.Floor((decimal)dmin);
            rangeBreastSize.Maximum = 99;
               var dmax = Datastore.modelcards.Max(x => x.bust);
            if (dmax != null)
                rangeBreastSize.Maximum = Math.Min(125, Math.Ceiling((decimal)dmax));            
            rangeBreastSize.SmallChange = 1M;
            rangeBreastSize.LargeChange = 2M;
            rangeBreastSize.Value = Math.Min(Math.Max(filterSettings.minBust, rangeBreastSize.Minimum), rangeBreastSize.Maximum);
            rangeBreastSize.Value2 = Math.Max(Math.Min(filterSettings.maxBust, rangeBreastSize.Maximum), rangeBreastSize.Minimum);
            rangeBreastSize.ScaleDivisions = rangeBreastSize.Maximum - rangeBreastSize.Minimum;
            rangeBreastSize.BackColor = Color.Transparent;
            rangeBreastSize.TickColor = Color.Black;
            rangeBreastSize.ElapsedInnerColor = Color.Green;
            rangeBreastSize.ValueChanged += Range_ValueChanged;
            

            rangeAge = new ColorSlider.ColorSlider();
            rangeAge.Location = new Point(Convert.ToInt32(110*dx/120), Convert.ToInt32(262*dx/120));
            rangeAge.Height = Convert.ToInt32(60*dx/120);
            rangeAge.Width = Convert.ToInt32(650*dx/120);
            rangeAge.ForeColor = Color.Black;
            var amin = Datastore.modelcards.Min(x => x.modelAge);
            rangeAge.Minimum = Math.Floor((decimal)amin);
            rangeAge.Maximum = 99;
            var amax = Datastore.modelcards.Max(x => x.modelAge);            
            rangeAge.Maximum = Math.Ceiling((decimal)amax);            
            rangeAge.ScaleDivisions = rangeAge.Maximum - rangeAge.Minimum;
            rangeAge.SmallChange = 1M;
            rangeAge.LargeChange = 2M;
            rangeAge.Value = Math.Min(Math.Max(filterSettings.minAge, rangeAge.Minimum), rangeAge.Maximum);
            rangeAge.Value2 = Math.Max(Math.Min(filterSettings.maxAge, rangeAge.Maximum), rangeAge.Minimum);
            rangeAge.ScaleDivisions = rangeAge.Maximum - rangeAge.Minimum;
            rangeAge.BackColor = Color.Transparent;
            rangeAge.TickColor = Color.Black;
            rangeAge.ElapsedInnerColor = Color.Green;
            rangeAge.ValueChanged += Range_ValueChanged;
            

            rangeMyRating = new ColorSlider.ColorSlider();
            rangeMyRating.Location = new Point(Convert.ToInt32(110*dx/120), Convert.ToInt32(346*dx/120));
            rangeMyRating.Height = Convert.ToInt32(60*dx/120);
            rangeMyRating.Width = Convert.ToInt32(650*dx/120);
            rangeMyRating.ForeColor = Color.Black;
            rangeMyRating.Minimum = 0;
            rangeMyRating.Maximum = 10;
            rangeMyRating.ScaleDivisions = 0;
            rangeMyRating.ScaleSubDivisions = rangeMyRating.Maximum;
            rangeMyRating.SmallChange = 1M;
            rangeMyRating.LargeChange = 2M;
            rangeMyRating.Value = filterSettings.minMyRating; ;
            rangeMyRating.Value2 = filterSettings.maxMyRating;
            rangeMyRating.BackColor = Color.Transparent;
            rangeMyRating.TickColor = Color.Black;
            rangeMyRating.ElapsedInnerColor = Color.Green;
            rangeMyRating.ValueChanged += Range_ValueChanged;
            

            chkDeskBabes.Checked = filterSettings.DeskBabes;
            chkIStripper.Checked = filterSettings.IStripper;
            chkIStripperClassic.Checked = filterSettings.IStripperClassic;
            chkIStripperXXX.Checked = filterSettings.IStripperXXX;
            chkVGClassic.Checked = filterSettings.VGClassic;
            chkSpecial.Checked = filterSettings.Special;
            chkNormal.Checked = filterSettings.Normal;
        }

        private void Range_ValueChanged(object? sender, EventArgs e)
        {
            if (Control.MouseButtons != MouseButtons.Left) ApplySettings();
        }


        private void label1_Click(object sender, EventArgs e)
        {

        }

        private void Filter_Load(object sender, EventArgs e)
        {   
            
            Form1? frm = Utils.GetMainForm();           
            if (frm != null)
                Location = new Point(frm.Location.X + frm.Width / 2 - Width / 2,
                    frm.Location.Y + frm.Height / 2 - Height / 2);
        }

        private void cmdOK_Click(object sender, EventArgs e)
        {           
            ok = true;
            ApplySettings();
            this.Close();
        }

        private  void ApplySettings()
        {
            if (!isLoaded)return;
            if (filterSettings == null)return;
            if (rangeAge != null)
            { 
                filterSettings.minAge = rangeAge.Value;
                filterSettings.maxAge = rangeAge.Value2;
            }
            if (rangeBreastSize != null)
            {
                filterSettings.minBust = rangeBreastSize.Value;
                filterSettings.maxBust = rangeBreastSize.Value2;
            }
            if (rangeRating != null)
            {
                filterSettings.minRating = rangeRating.Value;
                filterSettings.maxRating = rangeRating.Value2;
            }
            if (rangeMyRating != null)
            {
                filterSettings.minMyRating = rangeMyRating.Value;
                filterSettings.maxMyRating = rangeMyRating.Value2;
            }
            filterSettings.tags = txtTags.Text;

            
            filterSettings.DeskBabes = chkDeskBabes.Checked;
            filterSettings.IStripper = chkIStripper.Checked;
            filterSettings.IStripperClassic = chkIStripperClassic.Checked;
            filterSettings.IStripperXXX = chkIStripperXXX.Checked;
            filterSettings.VGClassic = chkVGClassic.Checked;

            filterSettings.Special = chkSpecial.Checked;
            filterSettings.Normal = chkNormal.Checked;
            
            Form1? frm = Utils.GetMainForm();
            if (frm != null)
            {
                frm.filterSettings = filterSettings;
                frm.PopulateModelListview();
            }
        }

        private void cmdApply_Click(object sender, EventArgs e)
        {
            ApplySettings();
        }

        private void Filter_Activated(object sender, EventArgs e)
        {

        }

        private void chk_CheckedChanged(object sender, EventArgs e)
        {
            ApplySettings();
        }

        private void txtTags_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) ApplySettings();
        }

        private void Filter_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!ok) Restore();
        }

        internal void Save()
        { 
            if (filterSettings == null) return;
            using (MemoryStream memStream = new MemoryStream())
            {
                BinaryFormatter binSerializer = new BinaryFormatter();
                binSerializer.Serialize(memStream, filterSettings);
                savedSettings = memStream.ToArray();
            }
        }

        
        internal void Restore()
        { 
            BinaryFormatter binSerializer = new BinaryFormatter();
            filterSettings = (FilterSettings)binSerializer.Deserialize(new MemoryStream(savedSettings));
            Form1? frm = Utils.GetMainForm();
            if (frm != null)
            {
                frm.filterSettings = filterSettings;
                frm.PopulateModelListview();
            }
         }

        private void cmdRevert_Click(object sender, EventArgs e)
        {
            BinaryFormatter binSerializer = new BinaryFormatter();
            filterSettings = (FilterSettings)binSerializer.Deserialize(new MemoryStream(savedSettings));
            isLoaded = false;            
            ReadValues();
            this.Controls.RemoveAt(this.Controls.Count-1);
            this.Controls.RemoveAt(this.Controls.Count-1);
            this.Controls.RemoveAt(this.Controls.Count-1);
            this.Controls.RemoveAt(this.Controls.Count-1);

            this.Controls.Add(rangeRating);
            this.Controls.Add(rangeMyRating);
            this.Controls.Add(rangeBreastSize);
            this.Controls.Add(rangeAge);
            isLoaded = true;
            ApplySettings();
        }

        private void cmdSaveDefault_Click(object sender, EventArgs e)
        {
            string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "filters.bin");
            string mdatafolder = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer");
            if (!Directory.Exists(mdatafolder))
                Directory.CreateDirectory(mdatafolder);
            System.IO.Stream ms = File.OpenWrite(mdatafilepath);     
            BinaryFormatter formatter = new BinaryFormatter();              
            formatter.Serialize(ms, filterSettings);  
            ms.Flush();  
            ms.Close();  
            ms.Dispose();  
        }
    }
}
