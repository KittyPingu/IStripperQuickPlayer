using IStripperQuickPlayer.DataModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IStripperQuickPlayer
{
    public partial class Filter : Form
    {
        ColorSlider.ColorSlider rangeRating;
        ColorSlider.ColorSlider rangeAge;
        ColorSlider.ColorSlider rangeBreastSize;
        ColorSlider.ColorSlider rangeTimesPlayed;
        bool isLoaded = false;

        public Filter()
        {

            InitializeComponent();
            rangeRating = new ColorSlider.ColorSlider();
            rangeRating.Location = new Point(110,110);
            rangeRating.Height = 60;
            rangeRating.Width = 650;
            rangeRating.ForeColor = Color.Black;
            rangeRating.Minimum = 2;
            rangeRating.Maximum = 5;
            rangeRating.ScaleDivisions = 30;
            rangeRating.SmallChange = 0.1M;
            rangeRating.LargeChange = 0.5M;
            rangeRating.Value =  FilterSettings.minRating;
            rangeRating.Value2 = FilterSettings.maxRating;
            rangeRating.BackColor = Color.Transparent;
            rangeRating.TickColor = Color.Black;
            rangeRating.ElapsedInnerColor = Color.Green;
            rangeRating.ValueChanged += Range_ValueChanged;
            this.Controls.Add(rangeRating);

            rangeBreastSize = new ColorSlider.ColorSlider();
            rangeBreastSize.Location = new Point(110,186);
            rangeBreastSize.Height = 60;
            rangeBreastSize.Width = 650;
            rangeBreastSize.ForeColor = Color.Black;
            rangeBreastSize.Minimum = 25;
            rangeBreastSize.Maximum = 50;
            rangeBreastSize.ScaleDivisions = 25;
            rangeBreastSize.SmallChange = 1M;
            rangeBreastSize.LargeChange = 2M;
            rangeBreastSize.Value = FilterSettings.minBust;
            rangeBreastSize.Value2 = FilterSettings.maxBust;
            rangeBreastSize.BackColor = Color.Transparent;
            rangeBreastSize.TickColor = Color.Black;
            rangeBreastSize.ElapsedInnerColor = Color.Green;
            rangeBreastSize.ValueChanged += Range_ValueChanged;
            this.Controls.Add(rangeBreastSize);

            rangeAge = new ColorSlider.ColorSlider();
            rangeAge.Location = new Point(110,262);
            rangeAge.Height = 60;
            rangeAge.Width = 650;
            rangeAge.ForeColor = Color.Black;
            rangeAge.Minimum = 18;
            rangeAge.Maximum = 43;
            rangeAge.ScaleDivisions = 25;
            rangeAge.SmallChange = 1M;
            rangeAge.LargeChange = 2M;
            rangeAge.Value = FilterSettings.minAge;
            rangeAge.Value2 = FilterSettings.maxAge;
            rangeAge.BackColor = Color.Transparent;
            rangeAge.TickColor = Color.Black;
            rangeAge.ElapsedInnerColor = Color.Green;
            rangeAge.ValueChanged += Range_ValueChanged;
            this.Controls.Add(rangeAge);

            rangeTimesPlayed = new ColorSlider.ColorSlider();
            rangeTimesPlayed.Location = new Point(110,340);
            rangeTimesPlayed.Height = 60;
            rangeTimesPlayed.Width = 650;
            rangeTimesPlayed.ForeColor = Color.Black;
            rangeTimesPlayed.Minimum = 0;
            rangeTimesPlayed.Maximum = Datastore.modelcards.Max(c => c.timesPlayed);
            rangeTimesPlayed.ScaleDivisions = 0;
            rangeTimesPlayed.ScaleSubDivisions = rangeTimesPlayed.Maximum;
            rangeTimesPlayed.SmallChange = 1M;
            rangeTimesPlayed.LargeChange = 2M;
            rangeTimesPlayed.Value = FilterSettings.minTimesPlayed;
            if (FilterSettings.maxTimesPlayed==0)
                FilterSettings.maxTimesPlayed = Datastore.modelcards.Max(c => c.timesPlayed);
            rangeTimesPlayed.Value2 = FilterSettings.maxTimesPlayed;
            rangeTimesPlayed.BackColor = Color.Transparent;
            rangeTimesPlayed.TickColor = Color.Black;
            rangeTimesPlayed.ElapsedInnerColor = Color.Green;
            rangeTimesPlayed.ValueChanged += Range_ValueChanged;
            this.Controls.Add(rangeTimesPlayed);

            chkDeskBabes.Checked = FilterSettings.DeskBabes;
            chkIStripper.Checked = FilterSettings.IStripper;
            chkIStripperClassic.Checked = FilterSettings.IStripperClassic;
            chkIStripperXXX.Checked = FilterSettings.IStripperXXX;
            chkVGClassic.Checked = FilterSettings.VGClassic;
            chkSpecial.Checked = FilterSettings.Special;
            chkNormal.Checked = FilterSettings.Normal;
            isLoaded = true;
        }

        private void Range_ValueChanged(object? sender, EventArgs e)
        {
            if (Control.MouseButtons != MouseButtons.Left) ApplySettings();
        }


        private void label1_Click(object sender, EventArgs e)
        {

        }

        private void Filter_Load(object sender, EventArgs e)
        {   Form1 frm = Application.OpenForms.Cast<Form1>().Where(x => x.Name == "Form1").FirstOrDefault();           
            if (frm != null)
                Location = new Point(frm.Location.X + frm.Width / 2 - Width / 2,
                    frm.Location.Y + frm.Height / 2 - Height / 2);
        }

        private void cmdOK_Click(object sender, EventArgs e)
        {           
            ApplySettings();
            this.Close();
        }

        private  void ApplySettings()
        {
            if (!isLoaded)return;
            FilterSettings.minAge = rangeAge.Value;
            FilterSettings.maxAge = rangeAge.Value2;
            FilterSettings.minBust = rangeBreastSize.Value;
            FilterSettings.maxBust = rangeBreastSize.Value2;
            FilterSettings.minRating = rangeRating.Value;
            FilterSettings.maxRating = rangeRating.Value2;
            FilterSettings.tags = txtTags.Text;

            
            FilterSettings.DeskBabes = chkDeskBabes.Checked;
            FilterSettings.IStripper = chkIStripper.Checked;
            FilterSettings.IStripperClassic = chkIStripperClassic.Checked;
            FilterSettings.IStripperXXX = chkIStripperXXX.Checked;
            FilterSettings.VGClassic = chkVGClassic.Checked;

            FilterSettings.Special = chkSpecial.Checked;
            FilterSettings.Normal = chkNormal.Checked;
            
            FilterSettings.minTimesPlayed = rangeTimesPlayed.Value;
            FilterSettings.maxTimesPlayed = rangeTimesPlayed.Value2;

            Form1 frm = Application.OpenForms.Cast<Form1>().Where(x => x.Name == "Form1").FirstOrDefault();
            if (frm != null)
                frm.PopulateModelListview();
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
    }
}
