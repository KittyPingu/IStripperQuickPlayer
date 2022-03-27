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
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            this.lblModelsLoaded = new System.Windows.Forms.Label();
            this.listModels = new System.Windows.Forms.ListView();
            this.menuCardList = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.menuCardFavourite = new System.Windows.Forms.ToolStripMenuItem();
            this.ratingSlider = new IStripperQuickPlayer.TrackBarMenuItem();
            this.toolStripSeparator1 = new System.Windows.Forms.ToolStripSeparator();
            this.nameToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.outfitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.ratingToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.hotnessToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.statsToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.ageToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.hairToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.purchasedToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.cmbMenuCardRating = new System.Windows.Forms.ToolStripComboBox();
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
            this.lblRatingScore = new System.Windows.Forms.Label();
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
            this.settingsToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.hotkeysToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.enforceCardFilterToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.menuShowRatingsStars = new System.Windows.Forms.ToolStripMenuItem();
            this.includeDescriptionInSearchToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.includeShowTitleInSearchToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.lblTags = new System.Windows.Forms.Label();
            this.lblCipListDetails = new System.Windows.Forms.Label();
            this.cmdFilter = new System.Windows.Forms.Button();
            this.label3 = new System.Windows.Forms.Label();
            this.errorProvider1 = new System.Windows.Forms.ErrorProvider(this.components);
            this.numMinSizeMB = new System.Windows.Forms.NumericUpDown();
            this.lblStats = new System.Windows.Forms.Label();
            this.chkFavourite = new System.Windows.Forms.CheckBox();
            this.lblUserTags = new System.Windows.Forms.Label();
            this.txtUserTags = new System.Windows.Forms.TextBox();
            this.cmdPhotos = new System.Windows.Forms.Button();
            this.cmbFilter = new System.Windows.Forms.ComboBox();
            this.txtClipType = new System.Windows.Forms.TextBox();
            this.label4 = new System.Windows.Forms.Label();
            this.menuCardList.SuspendLayout();
            this.menuStrip1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.errorProvider1)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numMinSizeMB)).BeginInit();
            this.SuspendLayout();
            // 
            // lblModelsLoaded
            // 
            this.lblModelsLoaded.AutoSize = true;
            this.lblModelsLoaded.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblModelsLoaded.Location = new System.Drawing.Point(26, 26);
            this.lblModelsLoaded.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lblModelsLoaded.Name = "lblModelsLoaded";
            this.lblModelsLoaded.Size = new System.Drawing.Size(108, 21);
            this.lblModelsLoaded.TabIndex = 1;
            this.lblModelsLoaded.Text = "Cards Loaded:";
            this.lblModelsLoaded.Click += new System.EventHandler(this.lblModelsLoaded_Click);
            // 
            // listModels
            // 
            this.listModels.ContextMenuStrip = this.menuCardList;
            this.listModels.Location = new System.Drawing.Point(26, 87);
            this.listModels.Margin = new System.Windows.Forms.Padding(4, 3, 4, 4);
            this.listModels.Name = "listModels";
            this.listModels.OwnerDraw = true;
            this.listModels.Size = new System.Drawing.Size(900, 882);
            this.listModels.TabIndex = 5;
            this.listModels.UseCompatibleStateImageBehavior = false;
            this.listModels.DrawItem += new System.Windows.Forms.DrawListViewItemEventHandler(this.listModels_DrawItem);
            this.listModels.RetrieveVirtualItem += new System.Windows.Forms.RetrieveVirtualItemEventHandler(this.listModels_RetrieveVirtualItem);
            this.listModels.SearchForVirtualItem += new System.Windows.Forms.SearchForVirtualItemEventHandler(this.listModels_SearchForVirtualItem);
            this.listModels.SelectedIndexChanged += new System.EventHandler(this.listModels_SelectedIndexChanged);
            this.listModels.DoubleClick += new System.EventHandler(this.listModels_DoubleClick);
            this.listModels.MouseDown += new System.Windows.Forms.MouseEventHandler(this.listModels_MouseDown);
            // 
            // menuCardList
            // 
            this.menuCardList.ImageScalingSize = new System.Drawing.Size(20, 20);
            this.menuCardList.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.menuCardFavourite,
            this.ratingSlider,
            this.toolStripSeparator1,
            this.nameToolStripMenuItem,
            this.outfitToolStripMenuItem,
            this.ratingToolStripMenuItem,
            this.hotnessToolStripMenuItem,
            this.statsToolStripMenuItem,
            this.ageToolStripMenuItem,
            this.hairToolStripMenuItem,
            this.purchasedToolStripMenuItem});
            this.menuCardList.Name = "menuCardList";
            this.menuCardList.Size = new System.Drawing.Size(169, 285);
            this.menuCardList.Closing += new System.Windows.Forms.ToolStripDropDownClosingEventHandler(this.menuCardList_Closing);
            this.menuCardList.Opening += new System.ComponentModel.CancelEventHandler(this.menuCardList_Opening);
            // 
            // menuCardFavourite
            // 
            this.menuCardFavourite.CheckOnClick = true;
            this.menuCardFavourite.Name = "menuCardFavourite";
            this.menuCardFavourite.Size = new System.Drawing.Size(168, 24);
            this.menuCardFavourite.Text = "Favourite";
            this.menuCardFavourite.CheckedChanged += new System.EventHandler(this.menuCardFavourite_CheckedChanged);
            // 
            // ratingSlider
            // 
            this.ratingSlider.BackColor = System.Drawing.Color.White;
            this.ratingSlider.ClientSize = new System.Drawing.Size(108, 56);
            this.ratingSlider.Font = new System.Drawing.Font("Microsoft Sans Serif", 6F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.ratingSlider.ForeColor = System.Drawing.Color.White;
            this.ratingSlider.Has2Values = false;
            this.ratingSlider.LargeChange = new decimal(new int[] {
            5,
            0,
            0,
            0});
            this.ratingSlider.Maximum = new decimal(new int[] {
            10,
            0,
            0,
            0});
            this.ratingSlider.Minimum = new decimal(new int[] {
            0,
            0,
            0,
            0});
            this.ratingSlider.Name = "ratingSlider";
            this.ratingSlider.ScaleDivisions = new decimal(new int[] {
            5,
            0,
            0,
            0});
            this.ratingSlider.Size = new System.Drawing.Size(108, 56);
            this.ratingSlider.SmallChange = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.ratingSlider.TickColor = System.Drawing.Color.Black;
            this.ratingSlider.TickStyle = System.Windows.Forms.TickStyle.Both;
            this.ratingSlider.TrackbarColor = System.Drawing.Color.White;
            this.ratingSlider.Value = new decimal(new int[] {
            0,
            0,
            0,
            0});
            this.ratingSlider.Value2 = new decimal(new int[] {
            0,
            0,
            0,
            0});
            this.ratingSlider.ValueChanged += new System.EventHandler(this.RatingSlider_ValueChanged);
            // 
            // toolStripSeparator1
            // 
            this.toolStripSeparator1.Name = "toolStripSeparator1";
            this.toolStripSeparator1.Size = new System.Drawing.Size(165, 6);
            // 
            // nameToolStripMenuItem
            // 
            this.nameToolStripMenuItem.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.nameToolStripMenuItem.Name = "nameToolStripMenuItem";
            this.nameToolStripMenuItem.Size = new System.Drawing.Size(168, 24);
            this.nameToolStripMenuItem.Text = "Name:";
            // 
            // outfitToolStripMenuItem
            // 
            this.outfitToolStripMenuItem.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.outfitToolStripMenuItem.Name = "outfitToolStripMenuItem";
            this.outfitToolStripMenuItem.Size = new System.Drawing.Size(168, 24);
            this.outfitToolStripMenuItem.Text = "Outfit:";
            // 
            // ratingToolStripMenuItem
            // 
            this.ratingToolStripMenuItem.AutoToolTip = true;
            this.ratingToolStripMenuItem.Name = "ratingToolStripMenuItem";
            this.ratingToolStripMenuItem.Size = new System.Drawing.Size(168, 24);
            this.ratingToolStripMenuItem.Tag = "Rating";
            this.ratingToolStripMenuItem.Text = "Rating:";
            this.ratingToolStripMenuItem.ToolTipText = "Official Rating";
            // 
            // hotnessToolStripMenuItem
            // 
            this.hotnessToolStripMenuItem.Name = "hotnessToolStripMenuItem";
            this.hotnessToolStripMenuItem.Size = new System.Drawing.Size(168, 24);
            this.hotnessToolStripMenuItem.Text = "Hotness:";
            // 
            // statsToolStripMenuItem
            // 
            this.statsToolStripMenuItem.AutoToolTip = true;
            this.statsToolStripMenuItem.Name = "statsToolStripMenuItem";
            this.statsToolStripMenuItem.Size = new System.Drawing.Size(168, 24);
            this.statsToolStripMenuItem.Tag = "Stats";
            this.statsToolStripMenuItem.Text = "Stats:";
            this.statsToolStripMenuItem.ToolTipText = "Model\'s Stats";
            // 
            // ageToolStripMenuItem
            // 
            this.ageToolStripMenuItem.Name = "ageToolStripMenuItem";
            this.ageToolStripMenuItem.Size = new System.Drawing.Size(168, 24);
            this.ageToolStripMenuItem.Text = "Age:";
            // 
            // hairToolStripMenuItem
            // 
            this.hairToolStripMenuItem.Name = "hairToolStripMenuItem";
            this.hairToolStripMenuItem.Size = new System.Drawing.Size(168, 24);
            this.hairToolStripMenuItem.Text = "Hair:";
            // 
            // purchasedToolStripMenuItem
            // 
            this.purchasedToolStripMenuItem.Name = "purchasedToolStripMenuItem";
            this.purchasedToolStripMenuItem.Size = new System.Drawing.Size(168, 24);
            this.purchasedToolStripMenuItem.Text = "Purchased:";
            // 
            // cmbMenuCardRating
            // 
            this.cmbMenuCardRating.Items.AddRange(new object[] {
            "1",
            "2",
            "3",
            "4",
            "5",
            "6",
            "7",
            "8",
            "9",
            "10"});
            this.cmbMenuCardRating.MaxDropDownItems = 10;
            this.cmbMenuCardRating.Name = "cmbMenuCardRating";
            this.cmbMenuCardRating.Size = new System.Drawing.Size(121, 28);
            this.cmbMenuCardRating.Text = "My Rating";
            this.cmbMenuCardRating.ToolTipText = "Select a rating for this card";
            this.cmbMenuCardRating.SelectedIndexChanged += new System.EventHandler(this.cmbMenuCardRating_SelectedIndexChanged);
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
            this.listClips.Location = new System.Drawing.Point(992, 142);
            this.listClips.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.listClips.MultiSelect = false;
            this.listClips.Name = "listClips";
            this.listClips.Size = new System.Drawing.Size(891, 544);
            this.listClips.TabIndex = 13;
            this.listClips.UseCompatibleStateImageBehavior = false;
            this.listClips.View = System.Windows.Forms.View.Details;
            this.listClips.SelectedIndexChanged += new System.EventHandler(this.listClips_SelectedIndexChanged);
            // 
            // ClipNumber
            // 
            this.ClipNumber.Text = "Clip";
            this.ClipNumber.Width = 34;
            // 
            // ClipName
            // 
            this.ClipName.Text = "ClipName";
            this.ClipName.Width = 200;
            // 
            // Hotness
            // 
            this.Hotness.Text = "Hotness";
            this.Hotness.Width = 120;
            // 
            // ClipType
            // 
            this.ClipType.Text = "ClipType";
            this.ClipType.Width = 280;
            // 
            // ClipSize
            // 
            this.ClipSize.Text = "Size";
            this.ClipSize.Width = 90;
            // 
            // chkPublic
            // 
            this.chkPublic.AutoSize = true;
            this.chkPublic.Location = new System.Drawing.Point(992, 116);
            this.chkPublic.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.chkPublic.Name = "chkPublic";
            this.chkPublic.Size = new System.Drawing.Size(59, 19);
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
            this.chkNoNudity.Location = new System.Drawing.Point(1068, 116);
            this.chkNoNudity.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.chkNoNudity.Name = "chkNoNudity";
            this.chkNoNudity.Size = new System.Drawing.Size(81, 19);
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
            this.chkTopless.Location = new System.Drawing.Point(1168, 116);
            this.chkTopless.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.chkTopless.Name = "chkTopless";
            this.chkTopless.Size = new System.Drawing.Size(64, 19);
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
            this.chkNudity.Location = new System.Drawing.Point(1250, 116);
            this.chkNudity.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.chkNudity.Name = "chkNudity";
            this.chkNudity.Size = new System.Drawing.Size(62, 19);
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
            this.chkFullNudity.Location = new System.Drawing.Point(1326, 116);
            this.chkFullNudity.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.chkFullNudity.Name = "chkFullNudity";
            this.chkFullNudity.Size = new System.Drawing.Size(84, 19);
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
            this.chkXXX.Location = new System.Drawing.Point(1430, 116);
            this.chkXXX.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.chkXXX.Name = "chkXXX";
            this.chkXXX.Size = new System.Drawing.Size(47, 19);
            this.chkXXX.TabIndex = 11;
            this.chkXXX.Text = "XXX";
            this.chkXXX.UseVisualStyleBackColor = true;
            this.chkXXX.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkDemo
            // 
            this.chkDemo.AutoSize = true;
            this.chkDemo.Location = new System.Drawing.Point(1809, 116);
            this.chkDemo.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.chkDemo.Name = "chkDemo";
            this.chkDemo.Size = new System.Drawing.Size(58, 19);
            this.chkDemo.TabIndex = 12;
            this.chkDemo.Text = "Demo";
            this.chkDemo.UseVisualStyleBackColor = true;
            this.chkDemo.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // cmbSortBy
            // 
            this.cmbSortBy.FormattingEnabled = true;
            this.cmbSortBy.Items.AddRange(new object[] {
            "My Rating",
            "Model Name",
            "Rating",
            "Age",
            "Breast Size",
            "Breast Size (Descending)",
            "Height",
            "Release Date",
            "Release Date (Descending)",
            "Date Purchased",
            "Date Purchased (Descending)"});
            this.cmbSortBy.Location = new System.Drawing.Point(80, 63);
            this.cmbSortBy.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cmbSortBy.Name = "cmbSortBy";
            this.cmbSortBy.Size = new System.Drawing.Size(179, 23);
            this.cmbSortBy.TabIndex = 4;
            this.cmbSortBy.SelectedIndexChanged += new System.EventHandler(this.comboBox1_SelectedIndexChanged);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(26, 67);
            this.label1.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(44, 15);
            this.label1.TabIndex = 3;
            this.label1.Text = "Sort By";
            // 
            // lblNowPlaying
            // 
            this.lblNowPlaying.AutoSize = true;
            this.lblNowPlaying.Font = new System.Drawing.Font("Nirmala UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblNowPlaying.Location = new System.Drawing.Point(987, 37);
            this.lblNowPlaying.Margin = new System.Windows.Forms.Padding(0);
            this.lblNowPlaying.Name = "lblNowPlaying";
            this.lblNowPlaying.Size = new System.Drawing.Size(101, 21);
            this.lblNowPlaying.TabIndex = 14;
            this.lblNowPlaying.Text = "Now Playing:";
            this.lblNowPlaying.Click += new System.EventHandler(this.lblNowPlaying_Click);
            // 
            // lblAge
            // 
            this.lblAge.AutoSize = true;
            this.lblAge.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblAge.Location = new System.Drawing.Point(996, 702);
            this.lblAge.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lblAge.Name = "lblAge";
            this.lblAge.Size = new System.Drawing.Size(39, 20);
            this.lblAge.TabIndex = 15;
            this.lblAge.Text = "Age:";
            // 
            // lblCollection
            // 
            this.lblCollection.AutoSize = true;
            this.lblCollection.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblCollection.Location = new System.Drawing.Point(1302, 702);
            this.lblCollection.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lblCollection.Name = "lblCollection";
            this.lblCollection.Size = new System.Drawing.Size(79, 20);
            this.lblCollection.TabIndex = 16;
            this.lblCollection.Text = "Collection:";
            // 
            // lblRatingScore
            // 
            this.lblRatingScore.AutoSize = true;
            this.lblRatingScore.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblRatingScore.Location = new System.Drawing.Point(1734, 702);
            this.lblRatingScore.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lblRatingScore.Name = "lblRatingScore";
            this.lblRatingScore.Size = new System.Drawing.Size(65, 20);
            this.lblRatingScore.TabIndex = 17;
            this.lblRatingScore.Text = "Hotness:";
            // 
            // txtDescription
            // 
            this.txtDescription.Location = new System.Drawing.Point(992, 802);
            this.txtDescription.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.txtDescription.Multiline = true;
            this.txtDescription.Name = "txtDescription";
            this.txtDescription.ReadOnly = true;
            this.txtDescription.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtDescription.Size = new System.Drawing.Size(891, 158);
            this.txtDescription.TabIndex = 18;
            // 
            // lblResolution
            // 
            this.lblResolution.AutoSize = true;
            this.lblResolution.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblResolution.Location = new System.Drawing.Point(1557, 702);
            this.lblResolution.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lblResolution.Name = "lblResolution";
            this.lblResolution.Size = new System.Drawing.Size(82, 20);
            this.lblResolution.TabIndex = 19;
            this.lblResolution.Text = "Resolution:";
            // 
            // txtSearch
            // 
            this.txtSearch.Location = new System.Drawing.Point(657, 63);
            this.txtSearch.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.txtSearch.Name = "txtSearch";
            this.txtSearch.PlaceholderText = "model name or tag";
            this.txtSearch.Size = new System.Drawing.Size(268, 23);
            this.txtSearch.TabIndex = 20;
            this.txtSearch.TextChanged += new System.EventHandler(this.txtSearch_TextChanged);
            this.txtSearch.Enter += new System.EventHandler(this.txtSearch_Enter);
            this.txtSearch.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtSearch_KeyDown);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(605, 67);
            this.label2.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(42, 15);
            this.label2.TabIndex = 21;
            this.label2.Text = "Search";
            // 
            // button1
            // 
            this.button1.Location = new System.Drawing.Point(1773, 33);
            this.button1.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(110, 33);
            this.button1.TabIndex = 22;
            this.button1.Text = "Show Model";
            this.button1.UseVisualStyleBackColor = true;
            this.button1.Click += new System.EventHandler(this.button1_Click);
            // 
            // cmdClearSearch
            // 
            this.cmdClearSearch.BackColor = System.Drawing.SystemColors.Window;
            this.cmdClearSearch.FlatAppearance.BorderSize = 0;
            this.cmdClearSearch.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.cmdClearSearch.Image = global::IStripperQuickPlayer.Properties.Resources.kindpng_4040161;
            this.cmdClearSearch.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.cmdClearSearch.Location = new System.Drawing.Point(922, 62);
            this.cmdClearSearch.Margin = new System.Windows.Forms.Padding(0);
            this.cmdClearSearch.Name = "cmdClearSearch";
            this.cmdClearSearch.Size = new System.Drawing.Size(49, 23);
            this.cmdClearSearch.TabIndex = 23;
            this.cmdClearSearch.UseVisualStyleBackColor = false;
            this.cmdClearSearch.Visible = false;
            this.cmdClearSearch.Click += new System.EventHandler(this.cmdClearSearch_Click);
            // 
            // cmdNextClip
            // 
            this.cmdNextClip.Location = new System.Drawing.Point(1657, 33);
            this.cmdNextClip.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cmdNextClip.Name = "cmdNextClip";
            this.cmdNextClip.Size = new System.Drawing.Size(95, 33);
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
            this.menuStrip1.Padding = new System.Windows.Forms.Padding(7, 3, 0, 3);
            this.menuStrip1.Size = new System.Drawing.Size(1894, 25);
            this.menuStrip1.TabIndex = 25;
            this.menuStrip1.Text = "menuStrip1";
            // 
            // fileToolStripMenuItem
            // 
            this.fileToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.reloadModelslstToolStripMenuItem,
            this.exitToolStripMenuItem});
            this.fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            this.fileToolStripMenuItem.Size = new System.Drawing.Size(37, 19);
            this.fileToolStripMenuItem.Text = "File";
            // 
            // reloadModelslstToolStripMenuItem
            // 
            this.reloadModelslstToolStripMenuItem.Name = "reloadModelslstToolStripMenuItem";
            this.reloadModelslstToolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.F5;
            this.reloadModelslstToolStripMenuItem.Size = new System.Drawing.Size(171, 22);
            this.reloadModelslstToolStripMenuItem.Text = "Reload Models";
            this.reloadModelslstToolStripMenuItem.Click += new System.EventHandler(this.cmdLoadModels_Click);
            // 
            // exitToolStripMenuItem
            // 
            this.exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            this.exitToolStripMenuItem.Size = new System.Drawing.Size(171, 22);
            this.exitToolStripMenuItem.Text = "Exit";
            this.exitToolStripMenuItem.Click += new System.EventHandler(this.exitToolStripMenuItem_Click);
            // 
            // settingsToolStripMenuItem
            // 
            this.settingsToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.hotkeysToolStripMenuItem,
            this.enforceCardFilterToolStripMenuItem,
            this.menuShowRatingsStars,
            this.includeDescriptionInSearchToolStripMenuItem,
            this.includeShowTitleInSearchToolStripMenuItem});
            this.settingsToolStripMenuItem.Name = "settingsToolStripMenuItem";
            this.settingsToolStripMenuItem.Size = new System.Drawing.Size(61, 19);
            this.settingsToolStripMenuItem.Text = "Settings";
            // 
            // hotkeysToolStripMenuItem
            // 
            this.hotkeysToolStripMenuItem.Name = "hotkeysToolStripMenuItem";
            this.hotkeysToolStripMenuItem.Size = new System.Drawing.Size(227, 22);
            this.hotkeysToolStripMenuItem.Text = "Hotkeys..";
            this.hotkeysToolStripMenuItem.Click += new System.EventHandler(this.hotkeysToolStripMenuItem_Click);
            // 
            // enforceCardFilterToolStripMenuItem
            // 
            this.enforceCardFilterToolStripMenuItem.Checked = true;
            this.enforceCardFilterToolStripMenuItem.CheckOnClick = true;
            this.enforceCardFilterToolStripMenuItem.CheckState = System.Windows.Forms.CheckState.Checked;
            this.enforceCardFilterToolStripMenuItem.Name = "enforceCardFilterToolStripMenuItem";
            this.enforceCardFilterToolStripMenuItem.Size = new System.Drawing.Size(227, 22);
            this.enforceCardFilterToolStripMenuItem.Text = "Enforce Card Filter";
            this.enforceCardFilterToolStripMenuItem.Click += new System.EventHandler(this.enforceCardFilterToolStripMenuItem_Click);
            // 
            // menuShowRatingsStars
            // 
            this.menuShowRatingsStars.CheckOnClick = true;
            this.menuShowRatingsStars.Name = "menuShowRatingsStars";
            this.menuShowRatingsStars.Size = new System.Drawing.Size(227, 22);
            this.menuShowRatingsStars.Text = "Show MyRating Stars";
            this.menuShowRatingsStars.CheckedChanged += new System.EventHandler(this.chkShowRatingStars_CheckedChanged);
            // 
            // includeDescriptionInSearchToolStripMenuItem
            // 
            this.includeDescriptionInSearchToolStripMenuItem.CheckOnClick = true;
            this.includeDescriptionInSearchToolStripMenuItem.Name = "includeDescriptionInSearchToolStripMenuItem";
            this.includeDescriptionInSearchToolStripMenuItem.Size = new System.Drawing.Size(227, 22);
            this.includeDescriptionInSearchToolStripMenuItem.Text = "Include Description in Search";
            this.includeDescriptionInSearchToolStripMenuItem.Click += new System.EventHandler(this.includeDescriptionInSearchToolStripMenuItem_Click);
            // 
            // includeShowTitleInSearchToolStripMenuItem
            // 
            this.includeShowTitleInSearchToolStripMenuItem.CheckOnClick = true;
            this.includeShowTitleInSearchToolStripMenuItem.Name = "includeShowTitleInSearchToolStripMenuItem";
            this.includeShowTitleInSearchToolStripMenuItem.Size = new System.Drawing.Size(227, 22);
            this.includeShowTitleInSearchToolStripMenuItem.Text = "Include Show Title in Search";
            this.includeShowTitleInSearchToolStripMenuItem.Click += new System.EventHandler(this.includeShowTitleInSearchToolStripMenuItem_Click);
            // 
            // lblTags
            // 
            this.lblTags.AutoSize = true;
            this.lblTags.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblTags.Location = new System.Drawing.Point(992, 736);
            this.lblTags.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lblTags.MaximumSize = new System.Drawing.Size(892, 27);
            this.lblTags.Name = "lblTags";
            this.lblTags.Size = new System.Drawing.Size(41, 20);
            this.lblTags.TabIndex = 26;
            this.lblTags.Text = "Tags:";
            // 
            // lblCipListDetails
            // 
            this.lblCipListDetails.AutoSize = true;
            this.lblCipListDetails.Font = new System.Drawing.Font("Nirmala UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblCipListDetails.Location = new System.Drawing.Point(987, 87);
            this.lblCipListDetails.Margin = new System.Windows.Forms.Padding(0);
            this.lblCipListDetails.Name = "lblCipListDetails";
            this.lblCipListDetails.Size = new System.Drawing.Size(0, 21);
            this.lblCipListDetails.TabIndex = 27;
            // 
            // cmdFilter
            // 
            this.cmdFilter.Location = new System.Drawing.Point(406, 63);
            this.cmdFilter.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cmdFilter.Name = "cmdFilter";
            this.cmdFilter.Size = new System.Drawing.Size(54, 22);
            this.cmdFilter.TabIndex = 28;
            this.cmdFilter.Text = "Filter...";
            this.cmdFilter.UseVisualStyleBackColor = true;
            this.cmdFilter.Click += new System.EventHandler(this.cmdFilter_Click);
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(1682, 87);
            this.label3.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(80, 15);
            this.label3.TabIndex = 29;
            this.label3.Text = "Min Size (MB)";
            // 
            // errorProvider1
            // 
            this.errorProvider1.ContainerControl = this;
            // 
            // numMinSizeMB
            // 
            this.numMinSizeMB.Increment = new decimal(new int[] {
            10,
            0,
            0,
            0});
            this.numMinSizeMB.Location = new System.Drawing.Point(1799, 86);
            this.numMinSizeMB.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.numMinSizeMB.Maximum = new decimal(new int[] {
            9999,
            0,
            0,
            0});
            this.numMinSizeMB.Name = "numMinSizeMB";
            this.numMinSizeMB.Size = new System.Drawing.Size(84, 23);
            this.numMinSizeMB.TabIndex = 30;
            this.numMinSizeMB.ValueChanged += new System.EventHandler(this.numMinSizeMB_ValueChanged);
            // 
            // lblStats
            // 
            this.lblStats.AutoSize = true;
            this.lblStats.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblStats.Location = new System.Drawing.Point(1115, 702);
            this.lblStats.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lblStats.Name = "lblStats";
            this.lblStats.Size = new System.Drawing.Size(44, 20);
            this.lblStats.TabIndex = 31;
            this.lblStats.Text = "Stats:";
            // 
            // chkFavourite
            // 
            this.chkFavourite.AutoSize = true;
            this.chkFavourite.Location = new System.Drawing.Point(275, 67);
            this.chkFavourite.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.chkFavourite.Name = "chkFavourite";
            this.chkFavourite.Size = new System.Drawing.Size(108, 19);
            this.chkFavourite.TabIndex = 32;
            this.chkFavourite.Text = "Only Favourites";
            this.chkFavourite.UseVisualStyleBackColor = true;
            this.chkFavourite.CheckedChanged += new System.EventHandler(this.chkFavourite_CheckedChanged);
            // 
            // lblUserTags
            // 
            this.lblUserTags.AutoSize = true;
            this.lblUserTags.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblUserTags.Location = new System.Drawing.Point(992, 770);
            this.lblUserTags.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lblUserTags.Name = "lblUserTags";
            this.lblUserTags.Size = new System.Drawing.Size(74, 20);
            this.lblUserTags.TabIndex = 33;
            this.lblUserTags.Text = "User Tags:";
            // 
            // txtUserTags
            // 
            this.txtUserTags.BackColor = System.Drawing.SystemColors.ControlLightLight;
            this.txtUserTags.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.txtUserTags.Location = new System.Drawing.Point(1113, 765);
            this.txtUserTags.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.txtUserTags.Name = "txtUserTags";
            this.txtUserTags.Size = new System.Drawing.Size(769, 27);
            this.txtUserTags.TabIndex = 34;
            this.txtUserTags.TextChanged += new System.EventHandler(this.txtUserTags_TextChanged);
            // 
            // cmdPhotos
            // 
            this.cmdPhotos.Location = new System.Drawing.Point(1788, 142);
            this.cmdPhotos.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cmdPhotos.Name = "cmdPhotos";
            this.cmdPhotos.Size = new System.Drawing.Size(95, 26);
            this.cmdPhotos.TabIndex = 35;
            this.cmdPhotos.Text = "Photos";
            this.cmdPhotos.UseVisualStyleBackColor = true;
            this.cmdPhotos.Click += new System.EventHandler(this.cmdPhotos_Click);
            // 
            // cmbFilter
            // 
            this.cmbFilter.FormattingEnabled = true;
            this.cmbFilter.Location = new System.Drawing.Point(467, 63);
            this.cmbFilter.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cmbFilter.Name = "cmbFilter";
            this.cmbFilter.Size = new System.Drawing.Size(131, 23);
            this.cmbFilter.TabIndex = 36;
            this.cmbFilter.DropDown += new System.EventHandler(this.AdjustWidthComboBox_DropDown);
            this.cmbFilter.SelectedIndexChanged += new System.EventHandler(this.cmbFilter_SelectedIndexChanged);
            // 
            // txtClipType
            // 
            this.txtClipType.Location = new System.Drawing.Point(1625, 112);
            this.txtClipType.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.txtClipType.Name = "txtClipType";
            this.txtClipType.Size = new System.Drawing.Size(163, 23);
            this.txtClipType.TabIndex = 37;
            this.txtClipType.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtClipType_KeyDown);
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(1530, 116);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(81, 15);
            this.label4.TabIndex = 38;
            this.label4.Text = "Filter ClipType";
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1894, 981);
            this.Controls.Add(this.label4);
            this.Controls.Add(this.txtClipType);
            this.Controls.Add(this.cmbFilter);
            this.Controls.Add(this.txtSearch);
            this.Controls.Add(this.cmdPhotos);
            this.Controls.Add(this.txtUserTags);
            this.Controls.Add(this.lblUserTags);
            this.Controls.Add(this.chkFavourite);
            this.Controls.Add(this.lblStats);
            this.Controls.Add(this.numMinSizeMB);
            this.Controls.Add(this.label3);
            this.Controls.Add(this.cmdFilter);
            this.Controls.Add(this.lblCipListDetails);
            this.Controls.Add(this.lblTags);
            this.Controls.Add(this.cmdNextClip);
            this.Controls.Add(this.cmdClearSearch);
            this.Controls.Add(this.button1);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.lblResolution);
            this.Controls.Add(this.txtDescription);
            this.Controls.Add(this.lblRatingScore);
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
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MainMenuStrip = this.menuStrip1;
            this.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.MaximizeBox = false;
            this.Name = "Form1";
            this.SizeGripStyle = System.Windows.Forms.SizeGripStyle.Hide;
            this.Text = "IStripper QuickPlayer";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Form1_FormClosing);
            this.Load += new System.EventHandler(this.Form1_Load);
            this.menuCardList.ResumeLayout(false);
            this.menuStrip1.ResumeLayout(false);
            this.menuStrip1.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.errorProvider1)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numMinSizeMB)).EndInit();
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
        private Label lblRatingScore;
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
        private Button cmdFilter;
        private Label label3;
        private ErrorProvider errorProvider1;
        private NumericUpDown numMinSizeMB;
        private ToolStripMenuItem enforceCardFilterToolStripMenuItem;
        private Label lblStats;
        private ContextMenuStrip menuCardList;
        private ToolStripMenuItem menuCardFavourite;
        private ToolStripComboBox cmbMenuCardRating;
        private ToolStripSeparator toolStripSeparator1;
        private ToolStripMenuItem ratingToolStripMenuItem;
        private ToolStripMenuItem statsToolStripMenuItem;
        private CheckBox chkFavourite;
        private ToolStripMenuItem menuShowRatingsStars;
        private TrackBarMenuItem ratingSlider;
        private ToolStripMenuItem nameToolStripMenuItem;
        private ToolStripMenuItem outfitToolStripMenuItem;
        private ToolStripMenuItem hotnessToolStripMenuItem;
        private ToolStripMenuItem ageToolStripMenuItem;
        private ToolStripMenuItem hairToolStripMenuItem;
        private ToolStripMenuItem purchasedToolStripMenuItem;
        private Label lblUserTags;
        private TextBox txtUserTags;
        private Button cmdPhotos;
        private ToolStripMenuItem includeDescriptionInSearchToolStripMenuItem;
        private ToolStripMenuItem includeShowTitleInSearchToolStripMenuItem;
        private ComboBox cmbFilter;
        private Label label4;
        private TextBox txtClipType;
    }
}