namespace IStripperQuickPlayer
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.lblModelsLoaded = new System.Windows.Forms.Label();
            this.listModels = new System.Windows.Forms.ListView();
            this.listClips = new System.Windows.Forms.ListView();
            this.ClipNumber = new System.Windows.Forms.ColumnHeader();
            this.ClipName = new System.Windows.Forms.ColumnHeader();
            this.Hotness = new System.Windows.Forms.ColumnHeader();
            this.ClipType = new System.Windows.Forms.ColumnHeader();
            this.ClipSize = new System.Windows.Forms.ColumnHeader();
            this.chkPublic = new System.Windows.Forms.CheckBox();
            this.chkNoNudity = new System.Windows.Forms.CheckBox();
            this.chkTopless = new System.Windows.Forms.CheckBox();
            this.chkNudity = new System.Windows.Forms.CheckBox();
            this.chkFullNudity = new System.Windows.Forms.CheckBox();
            this.chkXXX = new System.Windows.Forms.CheckBox();
            this.chkDemo = new System.Windows.Forms.CheckBox();
            this.cmbSortBy = new System.Windows.Forms.ComboBox();
            this.label1 = new System.Windows.Forms.Label();
            this.lblNowPlaying = new System.Windows.Forms.Label();
            this.lblAge = new System.Windows.Forms.Label();
            this.lblCollection = new System.Windows.Forms.Label();
            this.lblHotness = new System.Windows.Forms.Label();
            this.txtDescription = new System.Windows.Forms.TextBox();
            this.lblResolution = new System.Windows.Forms.Label();
            this.txtSearch = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.button1 = new System.Windows.Forms.Button();
            this.cmdClearSearch = new System.Windows.Forms.Button();
            this.cmdNextClip = new System.Windows.Forms.Button();
            this.menuStrip1 = new System.Windows.Forms.MenuStrip();
            this.fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.reloadModelslstToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.exitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.lblTags = new System.Windows.Forms.Label();
            this.lblCipListDetails = new System.Windows.Forms.Label();
            this.settingsToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.hotkeysToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.menuStrip1.SuspendLayout();
            this.SuspendLayout();
            // 
            // lblModelsLoaded
            // 
            this.lblModelsLoaded.AutoSize = true;
            this.lblModelsLoaded.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblModelsLoaded.Location = new System.Drawing.Point(24, 28);
            this.lblModelsLoaded.Name = "lblModelsLoaded";
            this.lblModelsLoaded.Size = new System.Drawing.Size(135, 28);
            this.lblModelsLoaded.TabIndex = 1;
            this.lblModelsLoaded.Text = "Cards Loaded:";
            this.lblModelsLoaded.Click += new System.EventHandler(this.lblModelsLoaded_Click);
            // 
            // listModels
            // 
            this.listModels.Location = new System.Drawing.Point(24, 94);
            this.listModels.Name = "listModels";
            this.listModels.OwnerDraw = true;
            this.listModels.Size = new System.Drawing.Size(823, 931);
            this.listModels.TabIndex = 5;
            this.listModels.UseCompatibleStateImageBehavior = false;
            this.listModels.DrawItem += new System.Windows.Forms.DrawListViewItemEventHandler(this.listModels_DrawItem);
            this.listModels.SelectedIndexChanged += new System.EventHandler(this.listModels_SelectedIndexChanged);
            // 
            // listClips
            // 
            this.listClips.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.ClipNumber,
            this.ClipName,
            this.Hotness,
            this.ClipType,
            this.ClipSize});
            this.listClips.FullRowSelect = true;
            this.listClips.Location = new System.Drawing.Point(907, 151);
            this.listClips.MultiSelect = false;
            this.listClips.Name = "listClips";
            this.listClips.Size = new System.Drawing.Size(815, 623);
            this.listClips.TabIndex = 13;
            this.listClips.UseCompatibleStateImageBehavior = false;
            this.listClips.View = System.Windows.Forms.View.Details;
            this.listClips.SelectedIndexChanged += new System.EventHandler(this.listClips_SelectedIndexChanged);
            // 
            // ClipNumber
            // 
            this.ClipNumber.Text = "Clip";
            this.ClipNumber.Width = 40;
            // 
            // ClipName
            // 
            this.ClipName.Text = "ClipName";
            this.ClipName.Width = 210;
            // 
            // Hotness
            // 
            this.Hotness.Text = "Hotness";
            this.Hotness.Width = 130;
            // 
            // ClipType
            // 
            this.ClipType.Text = "ClipType";
            this.ClipType.Width = 300;
            // 
            // ClipSize
            // 
            this.ClipSize.Text = "Size";
            this.ClipSize.Width = 110;
            // 
            // chkPublic
            // 
            this.chkPublic.AutoSize = true;
            this.chkPublic.Location = new System.Drawing.Point(907, 124);
            this.chkPublic.Margin = new System.Windows.Forms.Padding(3, 0, 3, 0);
            this.chkPublic.Name = "chkPublic";
            this.chkPublic.Size = new System.Drawing.Size(71, 24);
            this.chkPublic.TabIndex = 6;
            this.chkPublic.Text = "Public";
            this.chkPublic.UseVisualStyleBackColor = true;
            this.chkPublic.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkNoNudity
            // 
            this.chkNoNudity.AutoSize = true;
            this.chkNoNudity.Checked = true;
            this.chkNoNudity.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkNoNudity.Location = new System.Drawing.Point(1019, 124);
            this.chkNoNudity.Margin = new System.Windows.Forms.Padding(3, 0, 3, 0);
            this.chkNoNudity.Name = "chkNoNudity";
            this.chkNoNudity.Size = new System.Drawing.Size(99, 24);
            this.chkNoNudity.TabIndex = 7;
            this.chkNoNudity.Text = "No Nudity";
            this.chkNoNudity.UseVisualStyleBackColor = true;
            this.chkNoNudity.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkTopless
            // 
            this.chkTopless.AutoSize = true;
            this.chkTopless.Checked = true;
            this.chkTopless.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkTopless.Location = new System.Drawing.Point(1150, 124);
            this.chkTopless.Margin = new System.Windows.Forms.Padding(3, 0, 3, 0);
            this.chkTopless.Name = "chkTopless";
            this.chkTopless.Size = new System.Drawing.Size(80, 24);
            this.chkTopless.TabIndex = 8;
            this.chkTopless.Text = "Topless";
            this.chkTopless.UseVisualStyleBackColor = true;
            this.chkTopless.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkNudity
            // 
            this.chkNudity.AutoSize = true;
            this.chkNudity.Checked = true;
            this.chkNudity.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkNudity.Location = new System.Drawing.Point(1284, 124);
            this.chkNudity.Margin = new System.Windows.Forms.Padding(3, 0, 3, 0);
            this.chkNudity.Name = "chkNudity";
            this.chkNudity.Size = new System.Drawing.Size(75, 24);
            this.chkNudity.TabIndex = 9;
            this.chkNudity.Text = "Nudity";
            this.chkNudity.UseVisualStyleBackColor = true;
            this.chkNudity.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkFullNudity
            // 
            this.chkFullNudity.AutoSize = true;
            this.chkFullNudity.Checked = true;
            this.chkFullNudity.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkFullNudity.Location = new System.Drawing.Point(1403, 124);
            this.chkFullNudity.Margin = new System.Windows.Forms.Padding(3, 0, 3, 0);
            this.chkFullNudity.Name = "chkFullNudity";
            this.chkFullNudity.Size = new System.Drawing.Size(102, 24);
            this.chkFullNudity.TabIndex = 10;
            this.chkFullNudity.Text = "Full Nudity";
            this.chkFullNudity.UseVisualStyleBackColor = true;
            this.chkFullNudity.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkXXX
            // 
            this.chkXXX.AutoSize = true;
            this.chkXXX.Checked = true;
            this.chkXXX.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkXXX.Location = new System.Drawing.Point(1538, 124);
            this.chkXXX.Margin = new System.Windows.Forms.Padding(3, 0, 3, 0);
            this.chkXXX.Name = "chkXXX";
            this.chkXXX.Size = new System.Drawing.Size(58, 24);
            this.chkXXX.TabIndex = 11;
            this.chkXXX.Text = "XXX";
            this.chkXXX.UseVisualStyleBackColor = true;
            this.chkXXX.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkDemo
            // 
            this.chkDemo.AutoSize = true;
            this.chkDemo.Location = new System.Drawing.Point(1650, 123);
            this.chkDemo.Margin = new System.Windows.Forms.Padding(3, 0, 3, 0);
            this.chkDemo.Name = "chkDemo";
            this.chkDemo.Size = new System.Drawing.Size(72, 24);
            this.chkDemo.TabIndex = 12;
            this.chkDemo.Text = "Demo";
            this.chkDemo.UseVisualStyleBackColor = true;
            this.chkDemo.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // cmbSortBy
            // 
            this.cmbSortBy.FormattingEnabled = true;
            this.cmbSortBy.Items.AddRange(new object[] {
            "Model Name",
            "Rating",
            "Age",
            "Breast Size",
            "Breast Size (Descending)",
            "Height",
            "Ethnicity",
            "Date Purchased",
            "Date Purchased (Descending)"});
            this.cmbSortBy.Location = new System.Drawing.Point(86, 65);
            this.cmbSortBy.Name = "cmbSortBy";
            this.cmbSortBy.Size = new System.Drawing.Size(164, 28);
            this.cmbSortBy.TabIndex = 4;
            this.cmbSortBy.SelectedIndexChanged += new System.EventHandler(this.comboBox1_SelectedIndexChanged);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(24, 69);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(56, 20);
            this.label1.TabIndex = 3;
            this.label1.Text = "Sort By";
            // 
            // lblNowPlaying
            // 
            this.lblNowPlaying.AutoSize = true;
            this.lblNowPlaying.Font = new System.Drawing.Font("Nirmala UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblNowPlaying.Location = new System.Drawing.Point(902, 39);
            this.lblNowPlaying.Margin = new System.Windows.Forms.Padding(0);
            this.lblNowPlaying.Name = "lblNowPlaying";
            this.lblNowPlaying.Size = new System.Drawing.Size(126, 28);
            this.lblNowPlaying.TabIndex = 14;
            this.lblNowPlaying.Text = "Now Playing:";
            this.lblNowPlaying.Click += new System.EventHandler(this.lblNowPlaying_Click);
            // 
            // lblAge
            // 
            this.lblAge.AutoSize = true;
            this.lblAge.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblAge.Location = new System.Drawing.Point(910, 784);
            this.lblAge.Name = "lblAge";
            this.lblAge.Size = new System.Drawing.Size(48, 25);
            this.lblAge.TabIndex = 15;
            this.lblAge.Text = "Age:";
            // 
            // lblCollection
            // 
            this.lblCollection.AutoSize = true;
            this.lblCollection.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblCollection.Location = new System.Drawing.Point(1070, 784);
            this.lblCollection.Name = "lblCollection";
            this.lblCollection.Size = new System.Drawing.Size(94, 25);
            this.lblCollection.TabIndex = 16;
            this.lblCollection.Text = "Collection:";
            // 
            // lblHotness
            // 
            this.lblHotness.AutoSize = true;
            this.lblHotness.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblHotness.Location = new System.Drawing.Point(1586, 784);
            this.lblHotness.Name = "lblHotness";
            this.lblHotness.Size = new System.Drawing.Size(81, 25);
            this.lblHotness.TabIndex = 17;
            this.lblHotness.Text = "Hotness:";
            // 
            // txtDescription
            // 
            this.txtDescription.Location = new System.Drawing.Point(907, 855);
            this.txtDescription.Multiline = true;
            this.txtDescription.Name = "txtDescription";
            this.txtDescription.ReadOnly = true;
            this.txtDescription.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtDescription.Size = new System.Drawing.Size(815, 170);
            this.txtDescription.TabIndex = 18;
            // 
            // lblResolution
            // 
            this.lblResolution.AutoSize = true;
            this.lblResolution.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblResolution.Location = new System.Drawing.Point(1376, 784);
            this.lblResolution.Name = "lblResolution";
            this.lblResolution.Size = new System.Drawing.Size(99, 25);
            this.lblResolution.TabIndex = 19;
            this.lblResolution.Text = "Resolution:";
            // 
            // txtSearch
            // 
            this.txtSearch.Location = new System.Drawing.Point(601, 66);
            this.txtSearch.Name = "txtSearch";
            this.txtSearch.PlaceholderText = "model name or tag";
            this.txtSearch.Size = new System.Drawing.Size(246, 27);
            this.txtSearch.TabIndex = 20;
            this.txtSearch.TextChanged += new System.EventHandler(this.txtSearch_TextChanged);
            this.txtSearch.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtSearch_KeyDown);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(545, 69);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(53, 20);
            this.label2.TabIndex = 21;
            this.label2.Text = "Search";
            // 
            // button1
            // 
            this.button1.Location = new System.Drawing.Point(1621, 34);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(101, 35);
            this.button1.TabIndex = 22;
            this.button1.Text = "Show Model";
            this.button1.UseVisualStyleBackColor = true;
            this.button1.Click += new System.EventHandler(this.button1_Click);
            // 
            // cmdClearSearch
            // 
            this.cmdClearSearch.FlatAppearance.BorderSize = 0;
            this.cmdClearSearch.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.cmdClearSearch.Image = global::IStripperQuickPlayer.Properties.Resources.kindpng_4040161;
            this.cmdClearSearch.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.cmdClearSearch.Location = new System.Drawing.Point(802, 67);
            this.cmdClearSearch.Name = "cmdClearSearch";
            this.cmdClearSearch.Size = new System.Drawing.Size(45, 25);
            this.cmdClearSearch.TabIndex = 23;
            this.cmdClearSearch.UseVisualStyleBackColor = true;
            this.cmdClearSearch.Visible = false;
            this.cmdClearSearch.Click += new System.EventHandler(this.button2_Click);
            // 
            // cmdNextClip
            // 
            this.cmdNextClip.Location = new System.Drawing.Point(1518, 35);
            this.cmdNextClip.Name = "cmdNextClip";
            this.cmdNextClip.Size = new System.Drawing.Size(97, 34);
            this.cmdNextClip.TabIndex = 24;
            this.cmdNextClip.Text = "Next Clip";
            this.cmdNextClip.UseVisualStyleBackColor = true;
            this.cmdNextClip.Click += new System.EventHandler(this.cmdNextClip_Click);
            // 
            // menuStrip1
            // 
            this.menuStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
            this.menuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.fileToolStripMenuItem,
            this.settingsToolStripMenuItem});
            this.menuStrip1.Location = new System.Drawing.Point(0, 0);
            this.menuStrip1.Name = "menuStrip1";
            this.menuStrip1.Size = new System.Drawing.Size(1760, 28);
            this.menuStrip1.TabIndex = 25;
            this.menuStrip1.Text = "menuStrip1";
            // 
            // fileToolStripMenuItem
            // 
            this.fileToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.reloadModelslstToolStripMenuItem,
            this.exitToolStripMenuItem});
            this.fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            this.fileToolStripMenuItem.Size = new System.Drawing.Size(46, 24);
            this.fileToolStripMenuItem.Text = "File";
            // 
            // reloadModelslstToolStripMenuItem
            // 
            this.reloadModelslstToolStripMenuItem.Name = "reloadModelslstToolStripMenuItem";
            this.reloadModelslstToolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.F5;
            this.reloadModelslstToolStripMenuItem.Size = new System.Drawing.Size(216, 26);
            this.reloadModelslstToolStripMenuItem.Text = "Reload Models";
            this.reloadModelslstToolStripMenuItem.Click += new System.EventHandler(this.cmdLoadModels_Click);
            // 
            // exitToolStripMenuItem
            // 
            this.exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            this.exitToolStripMenuItem.Size = new System.Drawing.Size(216, 26);
            this.exitToolStripMenuItem.Text = "Exit";
            this.exitToolStripMenuItem.Click += new System.EventHandler(this.exitToolStripMenuItem_Click);
            // 
            // lblTags
            // 
            this.lblTags.AutoSize = true;
            this.lblTags.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblTags.Location = new System.Drawing.Point(907, 819);
            this.lblTags.Name = "lblTags";
            this.lblTags.Size = new System.Drawing.Size(51, 25);
            this.lblTags.TabIndex = 26;
            this.lblTags.Text = "Tags:";
            // 
            // lblCipListDetails
            // 
            this.lblCipListDetails.AutoSize = true;
            this.lblCipListDetails.Font = new System.Drawing.Font("Nirmala UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblCipListDetails.Location = new System.Drawing.Point(902, 94);
            this.lblCipListDetails.Margin = new System.Windows.Forms.Padding(0);
            this.lblCipListDetails.Name = "lblCipListDetails";
            this.lblCipListDetails.Size = new System.Drawing.Size(0, 28);
            this.lblCipListDetails.TabIndex = 27;
            // 
            // settingsToolStripMenuItem
            // 
            this.settingsToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.hotkeysToolStripMenuItem});
            this.settingsToolStripMenuItem.Name = "settingsToolStripMenuItem";
            this.settingsToolStripMenuItem.Size = new System.Drawing.Size(76, 24);
            this.settingsToolStripMenuItem.Text = "Settings";
            // 
            // hotkeysToolStripMenuItem
            // 
            this.hotkeysToolStripMenuItem.Name = "hotkeysToolStripMenuItem";
            this.hotkeysToolStripMenuItem.Size = new System.Drawing.Size(224, 26);
            this.hotkeysToolStripMenuItem.Text = "Hotkeys..";
            this.hotkeysToolStripMenuItem.Click += new System.EventHandler(this.hotkeysToolStripMenuItem_Click);
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1760, 1033);
            this.Controls.Add(this.lblCipListDetails);
            this.Controls.Add(this.lblTags);
            this.Controls.Add(this.cmdNextClip);
            this.Controls.Add(this.cmdClearSearch);
            this.Controls.Add(this.button1);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.txtSearch);
            this.Controls.Add(this.lblResolution);
            this.Controls.Add(this.txtDescription);
            this.Controls.Add(this.lblHotness);
            this.Controls.Add(this.lblCollection);
            this.Controls.Add(this.lblAge);
            this.Controls.Add(this.lblNowPlaying);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.cmbSortBy);
            this.Controls.Add(this.chkDemo);
            this.Controls.Add(this.chkXXX);
            this.Controls.Add(this.chkFullNudity);
            this.Controls.Add(this.chkNudity);
            this.Controls.Add(this.chkTopless);
            this.Controls.Add(this.chkNoNudity);
            this.Controls.Add(this.chkPublic);
            this.Controls.Add(this.listClips);
            this.Controls.Add(this.listModels);
            this.Controls.Add(this.lblModelsLoaded);
            this.Controls.Add(this.menuStrip1);
            this.DoubleBuffered = true;
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MainMenuStrip = this.menuStrip1;
            this.MaximizeBox = false;
            this.Name = "Form1";
            this.SizeGripStyle = System.Windows.Forms.SizeGripStyle.Hide;
            this.Text = "IStripper QuickPlayer";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Form1_FormClosing);
            this.Load += new System.EventHandler(this.Form1_Load);
            this.menuStrip1.ResumeLayout(false);
            this.menuStrip1.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        internal Label lblModelsLoaded;
        internal ListView listModels;
        internal ListView listClips;
        private ColumnHeader ClipName;
        private ColumnHeader Hotness;
        private ColumnHeader ClipSize;
        private ColumnHeader ClipType;
        private CheckBox chkPublic;
        private CheckBox chkNoNudity;
        private CheckBox chkTopless;
        private CheckBox chkNudity;
        private CheckBox chkFullNudity;
        private CheckBox chkXXX;
        private CheckBox chkDemo;
        private ComboBox cmbSortBy;
        private Label label1;
        internal Label lblNowPlaying;
        private ColumnHeader ClipNumber;
        private Label lblAge;
        private Label lblCollection;
        private Label lblHotness;
        private TextBox txtDescription;
        private Label lblResolution;
        private TextBox txtSearch;
        private Label label2;
        private Button button1;
        private Button cmdClearSearch;
        private Button cmdNextClip;
        private MenuStrip menuStrip1;
        private ToolStripMenuItem fileToolStripMenuItem;
        private ToolStripMenuItem reloadModelslstToolStripMenuItem;
        private Label lblTags;
        private ToolStripMenuItem exitToolStripMenuItem;
        internal Label lblCipListDetails;
        private ToolStripMenuItem settingsToolStripMenuItem;
        private ToolStripMenuItem hotkeysToolStripMenuItem;
    }
}