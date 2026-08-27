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
using IStripperQuickPlayer.BLL;
using DG.UI.Helpers;

namespace IStripperQuickPlayer
{
    public partial class Filter : Form
    {
        private string _filterName = "";
        ColorSlider.ColorSlider? rangeRating;
        ColorSlider.ColorSlider? rangeAge;
        ColorSlider.ColorSlider? rangeBreastSize;
        ColorSlider.ColorSlider? rangeWaist;
        ColorSlider.ColorSlider? rangeHips;
        ColorSlider.ColorSlider? rangeMyRating;
        bool isLoaded = false;
        bool ok = false;
        public FilterSettings? filterSettings;
        FilterSettings? savedSettings;
        bool deleting = false;
        readonly GroupBox cardTypeGroup = new()
        {
            Text = "Card Type",
            Location = new Point(21, 680),
            Size = new System.Drawing.Size(757, 112)
        };
        readonly GroupBox genderGroup = new()
        {
            Text = "Gender",
            Location = new Point(21, 803),
            Size = new System.Drawing.Size(757, 72)
        };
        readonly CheckedListBox customGenders = new()
        {
            CheckOnClick = true,
            MultiColumn = true,
            ColumnWidth = 120,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            Size = new System.Drawing.Size(730, 32)
        };
        readonly CheckBox chkCustom = new()
        {
            AutoSize = true,
            Location = new Point(630, 748),
            Text = "Custom"
        };
        readonly CheckBox chkRealtimeCustom = new()
        {
            AutoSize = true,
            Name = "chkRealtimeCustom",
            Text = "Real-time"
        };
        internal Filter(FilterSettings filter, string filterName)
        {
            filterSettings = (FilterSettings)filter.Clone();
            _filterName = filterName;

            Save();
            InitializeComponent();
            AddCardTypeFilter();
            AddGenderFilter();
            this.dateTimePickerMin.CustomFormat = Application.CurrentCulture.DateTimeFormat.ShortDatePattern;
            this.dateTimePickerMax.CustomFormat = Application.CurrentCulture.DateTimeFormat.ShortDatePattern;
            ReadValues();
            this.Controls.Add(rangeRating);
            this.Controls.Add(rangeMyRating);
            this.Controls.Add(rangeBreastSize);
            this.Controls.Add(rangeWaist);
            this.Controls.Add(rangeHips);
            this.Controls.Add(rangeAge);
            chkCustom.CheckedChanged += (_, e) => chk_CheckedChanged(chkCustom, e);
            chkRealtimeCustom.CheckedChanged += (_, e) =>
                chk_CheckedChanged(chkRealtimeCustom, e);
            AppTheme.Apply(this);
            isLoaded = true;
            if (string.IsNullOrEmpty(_filterName) || _filterName == "Default") button1.Enabled = false;
            else button1.Enabled = true;
            _ = TooltipManager.Attach(this, components,
                Properties.Settings.Default.TooltipInitialDelay);
        }

        void AddCardTypeFilter()
        {
            TableLayoutPanel choices = new()
            {
                Dock = DockStyle.Fill,
                ColumnCount = 6,
                RowCount = 2,
                Padding = new Padding(8, 14, 8, 8)
            };
            for (int column = 0; column < choices.ColumnCount; column++)
                choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,
                    100f / choices.ColumnCount));
            choices.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            choices.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            CheckBox[] cardTypes =
            [
                chkIStripperClassic, chkDeskBabes, chkVGClassic, chkIStripper,
                chkIStripperXXX, chkNormal, chkSpecial, chkVirtuaGuy,
                chkTradingCard, chkCustom, chkRealtimeCustom
            ];
            for (int index = 0; index < cardTypes.Length; index++)
            {
                cardTypes[index].Location = Point.Empty;
                cardTypes[index].Anchor = AnchorStyles.None;
                choices.Controls.Add(cardTypes[index], index % 6, index / 6);
            }
            cardTypeGroup.Controls.Add(choices);
            Controls.Add(cardTypeGroup);
        }

        void AddGenderFilter()
        {
            customGenders.Items.AddRange(CustomShowStore.GenderValues);
            customGenders.ItemCheck += (_, _) =>
            {
                if (isLoaded) BeginInvoke(ApplySettings);
            };
            customGenders.Location = new Point(
                (genderGroup.ClientSize.Width - customGenders.Width) / 2,
                (genderGroup.ClientSize.Height - customGenders.Height) / 2 + 4);
            genderGroup.Controls.Add(customGenders);
            Controls.Add(genderGroup);
            foreach (Button button in new[]
                { cmdSaveDefault, button1, cmdSaveAs, cmdRevert, cmdOK })
                button.Top += 95;
            ClientSize = new System.Drawing.Size(ClientSize.Width, ClientSize.Height + 95);
        }

        private void ReadValues()
        {
            float dx;

            Graphics g = this.CreateGraphics();
            try
            {
                dx = 120;//g.DpiX;
            }
            finally
            {
                g.Dispose();
            }
            if (filterSettings == null) return;
            rangeRating = new ColorSlider.ColorSlider();
            rangeRating.Location = new Point(Convert.ToInt32(110 * dx / 120), Convert.ToInt32(140 * dx / 120));
            rangeRating.Height = Convert.ToInt32(70 * dx / 120);
            rangeRating.Width = Convert.ToInt32(650 * dx / 120);
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
            rangeRating.AccessibleName = "Official rating range";
            rangeRating.AccessibleDescription =
                "Include cards whose official rating is within this range.";
            rangeRating.ValueChanged += Range_ValueChanged;


            rangeBreastSize = CreateMeasurementRange(
                dx, 226, card => card.bust,
                filterSettings.minBust, filterSettings.maxBust);
            rangeWaist = CreateMeasurementRange(
                dx, 302, card => card.waist,
                filterSettings.minWaist, filterSettings.maxWaist);
            rangeHips = CreateMeasurementRange(
                dx, 378, card => card.hips,
                filterSettings.minHips, filterSettings.maxHips);
            rangeBreastSize.AccessibleName = "Bust measurement range";
            rangeWaist.AccessibleName = "Waist measurement range";
            rangeHips.AccessibleName = "Hip measurement range";
            rangeBreastSize.AccessibleDescription =
                "Include cards whose model bust measurement is within this range.";
            rangeWaist.AccessibleDescription =
                "Include cards whose model waist measurement is within this range.";
            rangeHips.AccessibleDescription =
                "Include cards whose model hip measurement is within this range.";


            rangeAge = new ColorSlider.ColorSlider();
            rangeAge.Location = new Point(Convert.ToInt32(110 * dx / 120), Convert.ToInt32(454 * dx / 120));
            rangeAge.Height = Convert.ToInt32(70 * dx / 120);
            rangeAge.Width = Convert.ToInt32(650 * dx / 120);
            rangeAge.ForeColor = Color.Black;
            var amin = Datastore.modelcards.Where(a => a.modelAge >= 18).Min(x => x.modelAge);
            rangeAge.Minimum = Math.Floor((decimal)amin);
            rangeAge.Maximum = 99;
            var amax = Datastore.modelcards.Where(a => a.modelAge <= 99).Max(x => x.modelAge);
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
            rangeAge.AccessibleName = "Model age range";
            rangeAge.AccessibleDescription =
                "Include cards whose model age is within this range.";
            rangeAge.ValueChanged += Range_ValueChanged;


            rangeMyRating = new ColorSlider.ColorSlider();
            rangeMyRating.Location = new Point(Convert.ToInt32(110 * dx / 120), Convert.ToInt32(538 * dx / 120));
            rangeMyRating.Height = Convert.ToInt32(70 * dx / 120);
            rangeMyRating.Width = Convert.ToInt32(650 * dx / 120);
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
            rangeMyRating.AccessibleName = "Personal rating range";
            rangeMyRating.AccessibleDescription =
                "Include cards whose personal rating is within this range.";
            rangeMyRating.ValueChanged += Range_ValueChanged;

            chkDeskBabes.Checked = filterSettings.DeskBabes;
            chkIStripper.Checked = filterSettings.IStripper;
            chkIStripperClassic.Checked = filterSettings.IStripperClassic;
            chkIStripperXXX.Checked = filterSettings.IStripperXXX;
            chkVGClassic.Checked = filterSettings.VGClassic;
            chkSpecial.Checked = filterSettings.Special;
            chkNormal.Checked = filterSettings.Normal;
            chkVirtuaGuy.Checked = filterSettings.VirtuaGuy;
            chkTradingCard.Checked = filterSettings.TradingCard;
            chkCustom.Checked = filterSettings.Custom;
            chkRealtimeCustom.Checked = filterSettings.RealtimeCustom;
            HashSet<string> selectedGenders = filterSettings.genders.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < customGenders.Items.Count; index++)
                customGenders.SetItemChecked(index,
                    selectedGenders.Contains(customGenders.Items[index]!.ToString()!));
            dateTimePickerMin.Value = filterSettings.minDate;
            dateTimePickerMax.Value = filterSettings.maxDate;
            dateTimePickerMin.ValueChanged += Range_ValueChanged;
            dateTimePickerMax.ValueChanged += Range_ValueChanged;
            txtTags.Text = filterSettings.tags;
        }

        private ColorSlider.ColorSlider CreateMeasurementRange(
            float scale, int y, Func<ModelCard, decimal?> selector,
            decimal minimum, decimal maximum)
        {
            var bounds = Form1.MeasurementBounds(
                Datastore.modelcards.Select(selector));
            ColorSlider.ColorSlider range = new()
            {
                Location = new Point(
                    Convert.ToInt32(110 * scale / 120),
                    Convert.ToInt32(y * scale / 120)),
                Height = Convert.ToInt32(70 * scale / 120),
                Width = Convert.ToInt32(650 * scale / 120),
                ForeColor = Color.Black,
                Minimum = bounds.Minimum,
                Maximum = bounds.Maximum,
                SmallChange = 1M,
                LargeChange = 2M,
                BackColor = Color.Transparent,
                TickColor = Color.Black,
                ElapsedInnerColor = Color.Green
            };
            range.Value = Math.Clamp(
                minimum, range.Minimum, range.Maximum);
            range.Value2 = Math.Clamp(
                maximum, range.Minimum, range.Maximum);
            range.ScaleDivisions = range.Maximum - range.Minimum;
            range.ValueChanged += Range_ValueChanged;
            return range;
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
            this.Text = "Filter: " + _filterName;
            EnhancedDateTimePickerHelper.AttachDateTimePicker(dateTimePickerMin);
            EnhancedDateTimePickerHelper.AttachDateTimePicker(dateTimePickerMax);
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

        private void ApplySettings()
        {
            if (!isLoaded) return;
            if (filterSettings == null) return;
            if (rangeAge != null)
            {
                filterSettings.minAge = PreserveOriginalOutsideRange(
                    rangeAge.Value, rangeAge, savedSettings?.minAge);
                filterSettings.maxAge = PreserveOriginalOutsideRange(
                    rangeAge.Value2, rangeAge, savedSettings?.maxAge);
            }
            if (rangeBreastSize != null)
            {
                filterSettings.minBust = PreserveOriginalOutsideRange(
                    rangeBreastSize.Value, rangeBreastSize,
                    savedSettings?.minBust);
                filterSettings.maxBust = PreserveOriginalOutsideRange(
                    rangeBreastSize.Value2, rangeBreastSize,
                    savedSettings?.maxBust);
            }
            if (rangeWaist != null)
            {
                filterSettings.minWaist = PreserveOriginalOutsideRange(
                    rangeWaist.Value, rangeWaist,
                    savedSettings?.minWaist);
                filterSettings.maxWaist = PreserveOriginalOutsideRange(
                    rangeWaist.Value2, rangeWaist,
                    savedSettings?.maxWaist);
            }
            if (rangeHips != null)
            {
                filterSettings.minHips = PreserveOriginalOutsideRange(
                    rangeHips.Value, rangeHips,
                    savedSettings?.minHips);
                filterSettings.maxHips = PreserveOriginalOutsideRange(
                    rangeHips.Value2, rangeHips,
                    savedSettings?.maxHips);
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

            filterSettings.minDate = dateTimePickerMin.Value;
            filterSettings.maxDate = dateTimePickerMax.Value;

            filterSettings.tags = txtTags.Text;


            filterSettings.DeskBabes = chkDeskBabes.Checked;
            filterSettings.IStripper = chkIStripper.Checked;
            filterSettings.IStripperClassic = chkIStripperClassic.Checked;
            filterSettings.IStripperXXX = chkIStripperXXX.Checked;
            filterSettings.VGClassic = chkVGClassic.Checked;

            filterSettings.Special = chkSpecial.Checked;
            filterSettings.Normal = chkNormal.Checked;
            filterSettings.VirtuaGuy = chkVirtuaGuy.Checked;
            filterSettings.TradingCard = chkTradingCard.Checked;
            filterSettings.Custom = chkCustom.Checked;
            filterSettings.RealtimeCustom = chkRealtimeCustom.Checked;
            filterSettings.genders = string.Join(',',
                customGenders.CheckedItems.Cast<object>().Select(value => value.ToString()));

            Form1? frm = Utils.GetMainForm();
            if (frm != null)
            {
                FilterSettings applied =
                    (FilterSettings)filterSettings.Clone();
                if (!applied.HasSameValues(frm.filterSettings))
                {
                    frm.filterSettings = applied;
                    frm.PopulateModelListview();
                }
            }
        }

        private static decimal PreserveOriginalOutsideRange(decimal value,
            ColorSlider.ColorSlider range, decimal? original)
        {
            if (!original.HasValue)
                return value;
            if (value == range.Minimum && original.Value < range.Minimum)
                return original.Value;
            if (value == range.Maximum && original.Value > range.Maximum)
                return original.Value;
            return value;
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
            if (!deleting) FilterSettingsList.Save(_filterName, filterSettings);
        }

        internal void Save()
        {
            if (filterSettings == null) return;
            savedSettings = (FilterSettings)filterSettings.Clone();
        }


        internal void Restore()
        {
            if (savedSettings is null) return;
            filterSettings = (FilterSettings)savedSettings.Clone();
            Form1? frm = Utils.GetMainForm();
            if (frm != null)
            {
                FilterSettings restored =
                    (FilterSettings)filterSettings.Clone();
                if (!restored.HasSameValues(frm.filterSettings))
                {
                    frm.filterSettings = restored;
                    frm.PopulateModelListview();
                }
            }
        }

        private void cmdRevert_Click(object sender, EventArgs e)
        {
            if (savedSettings is null) return;
            filterSettings = (FilterSettings)savedSettings.Clone();
            isLoaded = false;
            foreach (Control control in new Control?[] { rangeRating, rangeMyRating,
                rangeBreastSize, rangeWaist, rangeHips, rangeAge }.OfType<Control>())
                Controls.Remove(control);
            ReadValues();

            this.Controls.Add(rangeRating);
            this.Controls.Add(rangeMyRating);
            this.Controls.Add(rangeBreastSize);
            this.Controls.Add(rangeWaist);
            this.Controls.Add(rangeHips);
            this.Controls.Add(rangeAge);
            isLoaded = true;
            ApplySettings();
        }

        private void cmdSaveDefault_Click(object sender, EventArgs e)
        {
            FilterSettingsList.Save("Default", filterSettings);

            _filterName = "Default";
            Form1? frm = Utils.GetMainForm();
            frm?.setFilter(_filterName);

        }

        private void cmdSaveAs_Click(object sender, EventArgs e)
        {
            //this.TopMost = false;
            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter a name for this filter", "Save filter");
            if (!string.IsNullOrEmpty(name) && name != "Default")
            {
                FilterSettingsList.Save(name, filterSettings);
                _filterName = name;
                if (_filterName == "Default") button1.Enabled = false;
                else button1.Enabled = true;
                Form1? frm = Utils.GetMainForm();
                frm?.setFilter(name);
            }
            else if (name == "Default")
            {
                MessageBox.Show("Use the Save Default button to save a default filter");
            }
            //this.TopMost = true;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            var r = MessageBox.Show("Delete " + _filterName, "Delete Filter?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
            {
                deleting = true;
                Form1? frm = Utils.GetMainForm();
                FilterSettingsList.Delete(_filterName);
                frm?.setFilter("Default");
                this.Close();
            }
        }
    }
}
