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
            this.cmbSortBy = new System.Windows.Forms.ComboBox();
            this.label1 = new System.Windows.Forms.Label();
            this.txtSearch = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.cmdClearSearch = new System.Windows.Forms.Button();
            this.menuStrip1 = new System.Windows.Forms.MenuStrip();
            this.fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.reloadModelslstToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.exitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.settingsToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.hotkeysToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.enforceCardFilterToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.cardScaleToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.trackBarCardScale = new IStripperQuickPlayer.TrackBarMenuItem();
            this.menuShowRatingsStars = new System.Windows.Forms.ToolStripMenuItem();
            this.includeDescriptionInSearchToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.includeShowTitleInSearchToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.wallpaperToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.automaticWallpaperToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.trackbarWallpaperBrightness = new IStripperQuickPlayer.TrackBarMenuItem();
            this.showTextToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.showKittyToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.cmdFilter = new System.Windows.Forms.Button();
            this.errorProvider1 = new System.Windows.Forms.ErrorProvider(this.components);
            this.chkFavourite = new System.Windows.Forms.CheckBox();
            this.cmdPhotos = new System.Windows.Forms.Button();
            this.cmbFilter = new System.Windows.Forms.ComboBox();
            this.panelModelDetails = new System.Windows.Forms.Panel();
            this.txtUserTags = new System.Windows.Forms.TextBox();
            this.lblUserTags = new System.Windows.Forms.Label();
            this.lblStats = new System.Windows.Forms.Label();
            this.lblTags = new System.Windows.Forms.Label();
            this.lblResolution = new System.Windows.Forms.Label();
            this.txtDescription = new System.Windows.Forms.TextBox();
            this.lblRatingScore = new System.Windows.Forms.Label();
            this.lblCollection = new System.Windows.Forms.Label();
            this.lblAge = new System.Windows.Forms.Label();
            this.panelClip = new System.Windows.Forms.Panel();
            this.label4 = new System.Windows.Forms.Label();
            this.button2 = new System.Windows.Forms.Button();
            this.txtClipType = new System.Windows.Forms.TextBox();
            this.numMinSizeMB = new System.Windows.Forms.NumericUpDown();
            this.label3 = new System.Windows.Forms.Label();
            this.lblCipListDetails = new System.Windows.Forms.Label();
            this.cmdNextClip = new System.Windows.Forms.Button();
            this.button1 = new System.Windows.Forms.Button();
            this.lblNowPlaying = new System.Windows.Forms.Label();
            this.chkDemo = new System.Windows.Forms.CheckBox();
            this.chkXXX = new System.Windows.Forms.CheckBox();
            this.chkFullNudity = new System.Windows.Forms.CheckBox();
            this.chkNudity = new System.Windows.Forms.CheckBox();
            this.chkTopless = new System.Windows.Forms.CheckBox();
            this.chkNoNudity = new System.Windows.Forms.CheckBox();
            this.chkPublic = new System.Windows.Forms.CheckBox();
            this.listModelsNew = new Manina.Windows.Forms.ImageListView();
            this.menuCardList.SuspendLayout();
            this.menuStrip1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.errorProvider1)).BeginInit();
            this.panelModelDetails.SuspendLayout();
            this.panelClip.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numMinSizeMB)).BeginInit();
            this.SuspendLayout();
            // 
            // lblModelsLoaded
            // 
            this.lblModelsLoaded.AutoSize = true;
            this.lblModelsLoaded.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblModelsLoaded.Location = new System.Drawing.Point(30, 35);
            this.lblModelsLoaded.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.lblModelsLoaded.Name = "lblModelsLoaded";
            this.lblModelsLoaded.Size = new System.Drawing.Size(135, 28);
            this.lblModelsLoaded.TabIndex = 1;
            this.lblModelsLoaded.Text = "Cards Loaded:";
            this.lblModelsLoaded.Click += new System.EventHandler(this.lblModelsLoaded_Click);
            // 
            // listModels
            // 
            this.listModels.ContextMenuStrip = this.menuCardList;
            this.listModels.Location = new System.Drawing.Point(30, 116);
            this.listModels.Margin = new System.Windows.Forms.Padding(5, 4, 5, 5);
            this.listModels.Name = "listModels";
            this.listModels.OwnerDraw = true;
            this.listModels.Size = new System.Drawing.Size(1028, 1175);
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
            this.menuCardList.Size = new System.Drawing.Size(169, 321);
            this.menuCardList.Closing += new System.Windows.Forms.ToolStripDropDownClosingEventHandler(this.menuCardList_Closing);
            this.menuCardList.Opening += new System.ComponentModel.CancelEventHandler(this.menuCardList_Opening);
            // 
            // menuCardFavourite
            // 
            this.menuCardFavourite.CheckOnClick = true;
            this.menuCardFavourite.Name = "menuCardFavourite";
            this.menuCardFavourite.Size = new System.Drawing.Size(168, 28);
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
            this.nameToolStripMenuItem.Size = new System.Drawing.Size(168, 28);
            this.nameToolStripMenuItem.Text = "Name:";
            // 
            // outfitToolStripMenuItem
            // 
            this.outfitToolStripMenuItem.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.outfitToolStripMenuItem.Name = "outfitToolStripMenuItem";
            this.outfitToolStripMenuItem.Size = new System.Drawing.Size(168, 28);
            this.outfitToolStripMenuItem.Text = "Outfit:";
            // 
            // ratingToolStripMenuItem
            // 
            this.ratingToolStripMenuItem.AutoToolTip = true;
            this.ratingToolStripMenuItem.Name = "ratingToolStripMenuItem";
            this.ratingToolStripMenuItem.Size = new System.Drawing.Size(168, 28);
            this.ratingToolStripMenuItem.Tag = "Rating";
            this.ratingToolStripMenuItem.Text = "Rating:";
            this.ratingToolStripMenuItem.ToolTipText = "Official Rating";
            // 
            // hotnessToolStripMenuItem
            // 
            this.hotnessToolStripMenuItem.Name = "hotnessToolStripMenuItem";
            this.hotnessToolStripMenuItem.Size = new System.Drawing.Size(168, 28);
            this.hotnessToolStripMenuItem.Text = "Hotness:";
            // 
            // statsToolStripMenuItem
            // 
            this.statsToolStripMenuItem.AutoToolTip = true;
            this.statsToolStripMenuItem.Name = "statsToolStripMenuItem";
            this.statsToolStripMenuItem.Size = new System.Drawing.Size(168, 28);
            this.statsToolStripMenuItem.Tag = "Stats";
            this.statsToolStripMenuItem.Text = "Stats:";
            this.statsToolStripMenuItem.ToolTipText = "Model\'s Stats";
            // 
            // ageToolStripMenuItem
            // 
            this.ageToolStripMenuItem.Name = "ageToolStripMenuItem";
            this.ageToolStripMenuItem.Size = new System.Drawing.Size(168, 28);
            this.ageToolStripMenuItem.Text = "Age:";
            // 
            // hairToolStripMenuItem
            // 
            this.hairToolStripMenuItem.Name = "hairToolStripMenuItem";
            this.hairToolStripMenuItem.Size = new System.Drawing.Size(168, 28);
            this.hairToolStripMenuItem.Text = "Hair:";
            // 
            // purchasedToolStripMenuItem
            // 
            this.purchasedToolStripMenuItem.Name = "purchasedToolStripMenuItem";
            this.purchasedToolStripMenuItem.Size = new System.Drawing.Size(168, 28);
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
            this.listClips.Location = new System.Drawing.Point(1134, 189);
            this.listClips.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.listClips.MultiSelect = false;
            this.listClips.Name = "listClips";
            this.listClips.Size = new System.Drawing.Size(1018, 724);
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
            this.cmbSortBy.Location = new System.Drawing.Point(91, 84);
            this.cmbSortBy.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.cmbSortBy.Name = "cmbSortBy";
            this.cmbSortBy.Size = new System.Drawing.Size(204, 28);
            this.cmbSortBy.TabIndex = 4;
            this.cmbSortBy.SelectedIndexChanged += new System.EventHandler(this.comboBox1_SelectedIndexChanged);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(30, 89);
            this.label1.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(56, 20);
            this.label1.TabIndex = 3;
            this.label1.Text = "Sort By";
            // 
            // txtSearch
            // 
            this.txtSearch.Location = new System.Drawing.Point(751, 84);
            this.txtSearch.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.txtSearch.Name = "txtSearch";
            this.txtSearch.PlaceholderText = "model name or tag";
            this.txtSearch.Size = new System.Drawing.Size(306, 27);
            this.txtSearch.TabIndex = 20;
            this.txtSearch.TextChanged += new System.EventHandler(this.txtSearch_TextChanged);
            this.txtSearch.Enter += new System.EventHandler(this.txtSearch_Enter);
            this.txtSearch.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtSearch_KeyDown);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(691, 89);
            this.label2.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(53, 20);
            this.label2.TabIndex = 21;
            this.label2.Text = "Search";
            // 
            // cmdClearSearch
            // 
            this.cmdClearSearch.BackColor = System.Drawing.SystemColors.Window;
            this.cmdClearSearch.FlatAppearance.BorderSize = 0;
            this.cmdClearSearch.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.cmdClearSearch.Image = global::IStripperQuickPlayer.Properties.Resources.kindpng_4040161;
            this.cmdClearSearch.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.cmdClearSearch.Location = new System.Drawing.Point(1054, 83);
            this.cmdClearSearch.Margin = new System.Windows.Forms.Padding(0);
            this.cmdClearSearch.Name = "cmdClearSearch";
            this.cmdClearSearch.Size = new System.Drawing.Size(56, 31);
            this.cmdClearSearch.TabIndex = 23;
            this.cmdClearSearch.UseVisualStyleBackColor = false;
            this.cmdClearSearch.Visible = false;
            this.cmdClearSearch.Click += new System.EventHandler(this.cmdClearSearch_Click);
            // 
            // menuStrip1
            // 
            this.menuStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
            this.menuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.fileToolStripMenuItem,
            this.settingsToolStripMenuItem});
            this.menuStrip1.Location = new System.Drawing.Point(0, 0);
            this.menuStrip1.Name = "menuStrip1";
            this.menuStrip1.Padding = new System.Windows.Forms.Padding(8, 4, 0, 4);
            this.menuStrip1.Size = new System.Drawing.Size(2165, 32);
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
            // settingsToolStripMenuItem
            // 
            this.settingsToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.hotkeysToolStripMenuItem,
            this.enforceCardFilterToolStripMenuItem,
            this.cardScaleToolStripMenuItem,
            this.menuShowRatingsStars,
            this.includeDescriptionInSearchToolStripMenuItem,
            this.includeShowTitleInSearchToolStripMenuItem,
            this.wallpaperToolStripMenuItem,
            this.showKittyToolStripMenuItem});
            this.settingsToolStripMenuItem.Name = "settingsToolStripMenuItem";
            this.settingsToolStripMenuItem.Size = new System.Drawing.Size(76, 24);
            this.settingsToolStripMenuItem.Text = "Settings";
            // 
            // hotkeysToolStripMenuItem
            // 
            this.hotkeysToolStripMenuItem.Name = "hotkeysToolStripMenuItem";
            this.hotkeysToolStripMenuItem.Size = new System.Drawing.Size(284, 26);
            this.hotkeysToolStripMenuItem.Text = "Hotkeys..";
            this.hotkeysToolStripMenuItem.Click += new System.EventHandler(this.hotkeysToolStripMenuItem_Click);
            // 
            // enforceCardFilterToolStripMenuItem
            // 
            this.enforceCardFilterToolStripMenuItem.Checked = true;
            this.enforceCardFilterToolStripMenuItem.CheckOnClick = true;
            this.enforceCardFilterToolStripMenuItem.CheckState = System.Windows.Forms.CheckState.Checked;
            this.enforceCardFilterToolStripMenuItem.Name = "enforceCardFilterToolStripMenuItem";
            this.enforceCardFilterToolStripMenuItem.Size = new System.Drawing.Size(284, 26);
            this.enforceCardFilterToolStripMenuItem.Text = "Enforce Card Filter";
            this.enforceCardFilterToolStripMenuItem.Click += new System.EventHandler(this.enforceCardFilterToolStripMenuItem_Click);
            // 
            // cardScaleToolStripMenuItem
            // 
            this.cardScaleToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.trackBarCardScale});
            this.cardScaleToolStripMenuItem.Name = "cardScaleToolStripMenuItem";
            this.cardScaleToolStripMenuItem.Size = new System.Drawing.Size(284, 26);
            this.cardScaleToolStripMenuItem.Text = "Card Scale";
            // 
            // trackBarCardScale
            // 
            this.trackBarCardScale.BackColor = System.Drawing.Color.Transparent;
            this.trackBarCardScale.ClientSize = new System.Drawing.Size(200, 48);
            this.trackBarCardScale.Font = new System.Drawing.Font("Microsoft Sans Serif", 6F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.trackBarCardScale.ForeColor = System.Drawing.Color.White;
            this.trackBarCardScale.Has2Values = false;
            this.trackBarCardScale.LargeChange = new decimal(new int[] {
            25,
            0,
            0,
            131072});
            this.trackBarCardScale.Maximum = new decimal(new int[] {
            2,
            0,
            0,
            0});
            this.trackBarCardScale.Minimum = new decimal(new int[] {
            5,
            0,
            0,
            65536});
            this.trackBarCardScale.Name = "trackBarCardScale";
            this.trackBarCardScale.ScaleDivisions = new decimal(new int[] {
            10,
            0,
            0,
            0});
            this.trackBarCardScale.Size = new System.Drawing.Size(200, 48);
            this.trackBarCardScale.SmallChange = new decimal(new int[] {
            5,
            0,
            0,
            131072});
            this.trackBarCardScale.Text = "trackBarMenuItem1";
            this.trackBarCardScale.TickColor = System.Drawing.Color.White;
            this.trackBarCardScale.TickStyle = System.Windows.Forms.TickStyle.TopLeft;
            this.trackBarCardScale.TrackbarColor = System.Drawing.Color.Transparent;
            this.trackBarCardScale.Value = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.trackBarCardScale.Value2 = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.trackBarCardScale.ValueChanged += new System.EventHandler(this.trackBarCardScale_ValueChanged);
            // 
            // menuShowRatingsStars
            // 
            this.menuShowRatingsStars.CheckOnClick = true;
            this.menuShowRatingsStars.Name = "menuShowRatingsStars";
            this.menuShowRatingsStars.Size = new System.Drawing.Size(284, 26);
            this.menuShowRatingsStars.Text = "Show MyRating Stars";
            this.menuShowRatingsStars.CheckedChanged += new System.EventHandler(this.chkShowRatingStars_CheckedChanged);
            // 
            // includeDescriptionInSearchToolStripMenuItem
            // 
            this.includeDescriptionInSearchToolStripMenuItem.CheckOnClick = true;
            this.includeDescriptionInSearchToolStripMenuItem.Name = "includeDescriptionInSearchToolStripMenuItem";
            this.includeDescriptionInSearchToolStripMenuItem.Size = new System.Drawing.Size(284, 26);
            this.includeDescriptionInSearchToolStripMenuItem.Text = "Include Description in Search";
            this.includeDescriptionInSearchToolStripMenuItem.Click += new System.EventHandler(this.includeDescriptionInSearchToolStripMenuItem_Click);
            // 
            // includeShowTitleInSearchToolStripMenuItem
            // 
            this.includeShowTitleInSearchToolStripMenuItem.CheckOnClick = true;
            this.includeShowTitleInSearchToolStripMenuItem.Name = "includeShowTitleInSearchToolStripMenuItem";
            this.includeShowTitleInSearchToolStripMenuItem.Size = new System.Drawing.Size(284, 26);
            this.includeShowTitleInSearchToolStripMenuItem.Text = "Include Show Title in Search";
            this.includeShowTitleInSearchToolStripMenuItem.Click += new System.EventHandler(this.includeShowTitleInSearchToolStripMenuItem_Click);
            // 
            // wallpaperToolStripMenuItem
            // 
            this.wallpaperToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.automaticWallpaperToolStripMenuItem,
            this.trackbarWallpaperBrightness,
            this.showTextToolStripMenuItem});
            this.wallpaperToolStripMenuItem.Name = "wallpaperToolStripMenuItem";
            this.wallpaperToolStripMenuItem.Size = new System.Drawing.Size(284, 26);
            this.wallpaperToolStripMenuItem.Text = "Wallpaper";
            // 
            // automaticWallpaperToolStripMenuItem
            // 
            this.automaticWallpaperToolStripMenuItem.CheckOnClick = true;
            this.automaticWallpaperToolStripMenuItem.Name = "automaticWallpaperToolStripMenuItem";
            this.automaticWallpaperToolStripMenuItem.Size = new System.Drawing.Size(274, 26);
            this.automaticWallpaperToolStripMenuItem.Text = "Automatic Wallpaper";
            this.automaticWallpaperToolStripMenuItem.CheckedChanged += new System.EventHandler(this.automaticWallpaperToolStripMenuItem_CheckedChanged);
            // 
            // trackbarWallpaperBrightness
            // 
            this.trackbarWallpaperBrightness.BackColor = System.Drawing.Color.Transparent;
            this.trackbarWallpaperBrightness.ClientSize = new System.Drawing.Size(200, 48);
            this.trackbarWallpaperBrightness.Font = new System.Drawing.Font("Microsoft Sans Serif", 6F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.trackbarWallpaperBrightness.ForeColor = System.Drawing.Color.White;
            this.trackbarWallpaperBrightness.Has2Values = false;
            this.trackbarWallpaperBrightness.LargeChange = new decimal(new int[] {
            10,
            0,
            0,
            0});
            this.trackbarWallpaperBrightness.Maximum = new decimal(new int[] {
            100,
            0,
            0,
            0});
            this.trackbarWallpaperBrightness.Minimum = new decimal(new int[] {
            0,
            0,
            0,
            0});
            this.trackbarWallpaperBrightness.Name = "trackbarWallpaperBrightness";
            this.trackbarWallpaperBrightness.ScaleDivisions = new decimal(new int[] {
            10,
            0,
            0,
            0});
            this.trackbarWallpaperBrightness.Size = new System.Drawing.Size(200, 48);
            this.trackbarWallpaperBrightness.SmallChange = new decimal(new int[] {
            5,
            0,
            0,
            0});
            this.trackbarWallpaperBrightness.Text = "trackBarMenuItem1";
            this.trackbarWallpaperBrightness.TickColor = System.Drawing.Color.White;
            this.trackbarWallpaperBrightness.TickStyle = System.Windows.Forms.TickStyle.TopLeft;
            this.trackbarWallpaperBrightness.TrackbarColor = System.Drawing.Color.Transparent;
            this.trackbarWallpaperBrightness.Value = new decimal(new int[] {
            60,
            0,
            0,
            0});
            this.trackbarWallpaperBrightness.Value2 = new decimal(new int[] {
            60,
            0,
            0,
            0});
            this.trackbarWallpaperBrightness.ValueChanged += new System.EventHandler(this.trackbarWallpaperBrightness_ValueChanged);
            // 
            // showTextToolStripMenuItem
            // 
            this.showTextToolStripMenuItem.CheckOnClick = true;
            this.showTextToolStripMenuItem.Name = "showTextToolStripMenuItem";
            this.showTextToolStripMenuItem.Size = new System.Drawing.Size(274, 26);
            this.showTextToolStripMenuItem.Text = "Show Text";
            this.showTextToolStripMenuItem.Click += new System.EventHandler(this.showTextToolStripMenuItem_Click);
            // 
            // showKittyToolStripMenuItem
            // 
            this.showKittyToolStripMenuItem.CheckOnClick = true;
            this.showKittyToolStripMenuItem.Name = "showKittyToolStripMenuItem";
            this.showKittyToolStripMenuItem.Size = new System.Drawing.Size(284, 26);
            this.showKittyToolStripMenuItem.Text = "Show Kitty";
            this.showKittyToolStripMenuItem.CheckedChanged += new System.EventHandler(this.showKittyToolStripMenuItem_CheckedChanged);
            // 
            // cmdFilter
            // 
            this.cmdFilter.Location = new System.Drawing.Point(464, 84);
            this.cmdFilter.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.cmdFilter.Name = "cmdFilter";
            this.cmdFilter.Size = new System.Drawing.Size(62, 29);
            this.cmdFilter.TabIndex = 28;
            this.cmdFilter.Text = "Filter...";
            this.cmdFilter.UseVisualStyleBackColor = true;
            this.cmdFilter.Click += new System.EventHandler(this.cmdFilter_Click);
            // 
            // errorProvider1
            // 
            this.errorProvider1.ContainerControl = this;
            // 
            // chkFavourite
            // 
            this.chkFavourite.AutoSize = true;
            this.chkFavourite.Location = new System.Drawing.Point(314, 89);
            this.chkFavourite.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.chkFavourite.Name = "chkFavourite";
            this.chkFavourite.Size = new System.Drawing.Size(131, 24);
            this.chkFavourite.TabIndex = 32;
            this.chkFavourite.Text = "Only Favourites";
            this.chkFavourite.UseVisualStyleBackColor = true;
            this.chkFavourite.CheckedChanged += new System.EventHandler(this.chkFavourite_CheckedChanged);
            // 
            // cmdPhotos
            // 
            this.cmdPhotos.Location = new System.Drawing.Point(2043, 189);
            this.cmdPhotos.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.cmdPhotos.Name = "cmdPhotos";
            this.cmdPhotos.Size = new System.Drawing.Size(109, 35);
            this.cmdPhotos.TabIndex = 35;
            this.cmdPhotos.Text = "Photos";
            this.cmdPhotos.UseVisualStyleBackColor = true;
            this.cmdPhotos.Click += new System.EventHandler(this.cmdPhotos_Click);
            // 
            // cmbFilter
            // 
            this.cmbFilter.FormattingEnabled = true;
            this.cmbFilter.Location = new System.Drawing.Point(534, 84);
            this.cmbFilter.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.cmbFilter.Name = "cmbFilter";
            this.cmbFilter.Size = new System.Drawing.Size(149, 28);
            this.cmbFilter.TabIndex = 36;
            this.cmbFilter.DropDown += new System.EventHandler(this.AdjustWidthComboBox_DropDown);
            this.cmbFilter.SelectedIndexChanged += new System.EventHandler(this.cmbFilter_SelectedIndexChanged);
            // 
            // panelModelDetails
            // 
            this.panelModelDetails.Controls.Add(this.txtUserTags);
            this.panelModelDetails.Controls.Add(this.lblUserTags);
            this.panelModelDetails.Controls.Add(this.lblStats);
            this.panelModelDetails.Controls.Add(this.lblTags);
            this.panelModelDetails.Controls.Add(this.lblResolution);
            this.panelModelDetails.Controls.Add(this.txtDescription);
            this.panelModelDetails.Controls.Add(this.lblRatingScore);
            this.panelModelDetails.Controls.Add(this.lblCollection);
            this.panelModelDetails.Controls.Add(this.lblAge);
            this.panelModelDetails.Location = new System.Drawing.Point(1110, 925);
            this.panelModelDetails.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.panelModelDetails.Name = "panelModelDetails";
            this.panelModelDetails.Size = new System.Drawing.Size(1047, 364);
            this.panelModelDetails.TabIndex = 39;
            // 
            // txtUserTags
            // 
            this.txtUserTags.BackColor = System.Drawing.SystemColors.ControlLightLight;
            this.txtUserTags.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.txtUserTags.Location = new System.Drawing.Point(153, 93);
            this.txtUserTags.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.txtUserTags.Name = "txtUserTags";
            this.txtUserTags.Size = new System.Drawing.Size(878, 31);
            this.txtUserTags.TabIndex = 43;
            this.txtUserTags.TextChanged += new System.EventHandler(this.txtUserTags_TextChanged);
            // 
            // lblUserTags
            // 
            this.lblUserTags.AutoSize = true;
            this.lblUserTags.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblUserTags.Location = new System.Drawing.Point(15, 100);
            this.lblUserTags.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.lblUserTags.Name = "lblUserTags";
            this.lblUserTags.Size = new System.Drawing.Size(91, 25);
            this.lblUserTags.TabIndex = 42;
            this.lblUserTags.Text = "User Tags:";
            // 
            // lblStats
            // 
            this.lblStats.AutoSize = true;
            this.lblStats.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblStats.Location = new System.Drawing.Point(155, 9);
            this.lblStats.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.lblStats.Name = "lblStats";
            this.lblStats.Size = new System.Drawing.Size(54, 25);
            this.lblStats.TabIndex = 41;
            this.lblStats.Text = "Stats:";
            // 
            // lblTags
            // 
            this.lblTags.AutoSize = true;
            this.lblTags.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblTags.Location = new System.Drawing.Point(15, 55);
            this.lblTags.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.lblTags.MaximumSize = new System.Drawing.Size(1019, 36);
            this.lblTags.Name = "lblTags";
            this.lblTags.Size = new System.Drawing.Size(51, 25);
            this.lblTags.TabIndex = 40;
            this.lblTags.Text = "Tags:";
            // 
            // lblResolution
            // 
            this.lblResolution.AutoSize = true;
            this.lblResolution.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblResolution.Location = new System.Drawing.Point(661, 9);
            this.lblResolution.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.lblResolution.Name = "lblResolution";
            this.lblResolution.Size = new System.Drawing.Size(99, 25);
            this.lblResolution.TabIndex = 39;
            this.lblResolution.Text = "Resolution:";
            // 
            // txtDescription
            // 
            this.txtDescription.Location = new System.Drawing.Point(15, 143);
            this.txtDescription.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.txtDescription.Multiline = true;
            this.txtDescription.Name = "txtDescription";
            this.txtDescription.ReadOnly = true;
            this.txtDescription.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtDescription.Size = new System.Drawing.Size(1018, 209);
            this.txtDescription.TabIndex = 38;
            // 
            // lblRatingScore
            // 
            this.lblRatingScore.AutoSize = true;
            this.lblRatingScore.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblRatingScore.Location = new System.Drawing.Point(863, 9);
            this.lblRatingScore.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.lblRatingScore.Name = "lblRatingScore";
            this.lblRatingScore.Size = new System.Drawing.Size(81, 25);
            this.lblRatingScore.TabIndex = 37;
            this.lblRatingScore.Text = "Hotness:";
            // 
            // lblCollection
            // 
            this.lblCollection.AutoSize = true;
            this.lblCollection.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblCollection.Location = new System.Drawing.Point(369, 9);
            this.lblCollection.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.lblCollection.Name = "lblCollection";
            this.lblCollection.Size = new System.Drawing.Size(94, 25);
            this.lblCollection.TabIndex = 36;
            this.lblCollection.Text = "Collection:";
            // 
            // lblAge
            // 
            this.lblAge.AutoSize = true;
            this.lblAge.Font = new System.Drawing.Font("Segoe UI", 10.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblAge.Location = new System.Drawing.Point(18, 9);
            this.lblAge.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.lblAge.Name = "lblAge";
            this.lblAge.Size = new System.Drawing.Size(48, 25);
            this.lblAge.TabIndex = 35;
            this.lblAge.Text = "Age:";
            // 
            // panelClip
            // 
            this.panelClip.Controls.Add(this.label4);
            this.panelClip.Controls.Add(this.button2);
            this.panelClip.Controls.Add(this.txtClipType);
            this.panelClip.Controls.Add(this.numMinSizeMB);
            this.panelClip.Controls.Add(this.label3);
            this.panelClip.Controls.Add(this.lblCipListDetails);
            this.panelClip.Controls.Add(this.cmdNextClip);
            this.panelClip.Controls.Add(this.button1);
            this.panelClip.Controls.Add(this.lblNowPlaying);
            this.panelClip.Controls.Add(this.chkDemo);
            this.panelClip.Controls.Add(this.chkXXX);
            this.panelClip.Controls.Add(this.chkFullNudity);
            this.panelClip.Controls.Add(this.chkNudity);
            this.panelClip.Controls.Add(this.chkTopless);
            this.panelClip.Controls.Add(this.chkNoNudity);
            this.panelClip.Controls.Add(this.chkPublic);
            this.panelClip.Location = new System.Drawing.Point(1125, 44);
            this.panelClip.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.panelClip.Name = "panelClip";
            this.panelClip.Size = new System.Drawing.Size(1037, 145);
            this.panelClip.TabIndex = 40;
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(626, 115);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(103, 20);
            this.label4.TabIndex = 53;
            this.label4.Text = "Filter ClipType";
            // 
            // button2
            // 
            this.button2.Location = new System.Drawing.Point(670, 4);
            this.button2.Name = "button2";
            this.button2.Size = new System.Drawing.Size(109, 44);
            this.button2.TabIndex = 41;
            this.button2.Text = "Wallpaper";
            this.button2.UseVisualStyleBackColor = true;
            this.button2.Click += new System.EventHandler(this.button2_Click);
            // 
            // txtClipType
            // 
            this.txtClipType.Location = new System.Drawing.Point(735, 109);
            this.txtClipType.Name = "txtClipType";
            this.txtClipType.Size = new System.Drawing.Size(186, 27);
            this.txtClipType.TabIndex = 52;
            this.txtClipType.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtClipType_KeyDown);
            // 
            // numMinSizeMB
            // 
            this.numMinSizeMB.Increment = new decimal(new int[] {
            10,
            0,
            0,
            0});
            this.numMinSizeMB.Location = new System.Drawing.Point(934, 75);
            this.numMinSizeMB.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.numMinSizeMB.Maximum = new decimal(new int[] {
            9999,
            0,
            0,
            0});
            this.numMinSizeMB.Name = "numMinSizeMB";
            this.numMinSizeMB.Size = new System.Drawing.Size(96, 27);
            this.numMinSizeMB.TabIndex = 51;
            this.numMinSizeMB.ValueChanged += new System.EventHandler(this.numMinSizeMB_ValueChanged);
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(800, 76);
            this.label3.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(101, 20);
            this.label3.TabIndex = 50;
            this.label3.Text = "Min Size (MB)";
            // 
            // lblCipListDetails
            // 
            this.lblCipListDetails.AutoSize = true;
            this.lblCipListDetails.Font = new System.Drawing.Font("Nirmala UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblCipListDetails.Location = new System.Drawing.Point(6, 76);
            this.lblCipListDetails.Margin = new System.Windows.Forms.Padding(0);
            this.lblCipListDetails.Name = "lblCipListDetails";
            this.lblCipListDetails.Size = new System.Drawing.Size(0, 28);
            this.lblCipListDetails.TabIndex = 49;
            // 
            // cmdNextClip
            // 
            this.cmdNextClip.Location = new System.Drawing.Point(787, 4);
            this.cmdNextClip.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.cmdNextClip.Name = "cmdNextClip";
            this.cmdNextClip.Size = new System.Drawing.Size(109, 44);
            this.cmdNextClip.TabIndex = 48;
            this.cmdNextClip.Text = "Next Clip";
            this.cmdNextClip.UseVisualStyleBackColor = true;
            this.cmdNextClip.Click += new System.EventHandler(this.cmdNextClip_Click);
            // 
            // button1
            // 
            this.button1.Location = new System.Drawing.Point(904, 4);
            this.button1.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(126, 44);
            this.button1.TabIndex = 47;
            this.button1.Text = "Show Model";
            this.button1.UseVisualStyleBackColor = true;
            this.button1.Click += new System.EventHandler(this.button1_Click);
            // 
            // lblNowPlaying
            // 
            this.lblNowPlaying.AutoSize = true;
            this.lblNowPlaying.Font = new System.Drawing.Font("Nirmala UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblNowPlaying.Location = new System.Drawing.Point(6, 9);
            this.lblNowPlaying.Margin = new System.Windows.Forms.Padding(0);
            this.lblNowPlaying.Name = "lblNowPlaying";
            this.lblNowPlaying.Size = new System.Drawing.Size(126, 28);
            this.lblNowPlaying.TabIndex = 46;
            this.lblNowPlaying.Text = "Now Playing:";
            this.lblNowPlaying.TextChanged += new System.EventHandler(this.lblNowPlaying_TextChanged);
            this.lblNowPlaying.Click += new System.EventHandler(this.lblNowPlaying_Click);
            this.lblNowPlaying.MouseEnter += new System.EventHandler(this.lblNowPlaying_MouseEnter);
            this.lblNowPlaying.MouseLeave += new System.EventHandler(this.lblNowPlaying_MouseLeave);
            // 
            // chkDemo
            // 
            this.chkDemo.AutoSize = true;
            this.chkDemo.Location = new System.Drawing.Point(945, 115);
            this.chkDemo.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.chkDemo.Name = "chkDemo";
            this.chkDemo.Size = new System.Drawing.Size(72, 24);
            this.chkDemo.TabIndex = 45;
            this.chkDemo.Text = "Demo";
            this.chkDemo.UseVisualStyleBackColor = true;
            this.chkDemo.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkXXX
            // 
            this.chkXXX.AutoSize = true;
            this.chkXXX.Checked = true;
            this.chkXXX.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkXXX.Location = new System.Drawing.Point(512, 115);
            this.chkXXX.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.chkXXX.Name = "chkXXX";
            this.chkXXX.Size = new System.Drawing.Size(58, 24);
            this.chkXXX.TabIndex = 44;
            this.chkXXX.Text = "XXX";
            this.chkXXX.UseVisualStyleBackColor = true;
            this.chkXXX.Click += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkFullNudity
            // 
            this.chkFullNudity.AutoSize = true;
            this.chkFullNudity.Checked = true;
            this.chkFullNudity.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkFullNudity.Location = new System.Drawing.Point(393, 115);
            this.chkFullNudity.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.chkFullNudity.Name = "chkFullNudity";
            this.chkFullNudity.Size = new System.Drawing.Size(102, 24);
            this.chkFullNudity.TabIndex = 43;
            this.chkFullNudity.Text = "Full Nudity";
            this.chkFullNudity.UseVisualStyleBackColor = true;
            this.chkFullNudity.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkNudity
            // 
            this.chkNudity.AutoSize = true;
            this.chkNudity.Checked = true;
            this.chkNudity.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkNudity.Location = new System.Drawing.Point(306, 115);
            this.chkNudity.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.chkNudity.Name = "chkNudity";
            this.chkNudity.Size = new System.Drawing.Size(75, 24);
            this.chkNudity.TabIndex = 42;
            this.chkNudity.Text = "Nudity";
            this.chkNudity.UseVisualStyleBackColor = true;
            this.chkNudity.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkTopless
            // 
            this.chkTopless.AutoSize = true;
            this.chkTopless.Checked = true;
            this.chkTopless.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkTopless.Location = new System.Drawing.Point(213, 115);
            this.chkTopless.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.chkTopless.Name = "chkTopless";
            this.chkTopless.Size = new System.Drawing.Size(80, 24);
            this.chkTopless.TabIndex = 41;
            this.chkTopless.Text = "Topless";
            this.chkTopless.UseVisualStyleBackColor = true;
            this.chkTopless.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkNoNudity
            // 
            this.chkNoNudity.AutoSize = true;
            this.chkNoNudity.Checked = true;
            this.chkNoNudity.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkNoNudity.Location = new System.Drawing.Point(98, 115);
            this.chkNoNudity.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.chkNoNudity.Name = "chkNoNudity";
            this.chkNoNudity.Size = new System.Drawing.Size(99, 24);
            this.chkNoNudity.TabIndex = 40;
            this.chkNoNudity.Text = "No Nudity";
            this.chkNoNudity.UseVisualStyleBackColor = true;
            this.chkNoNudity.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // chkPublic
            // 
            this.chkPublic.AutoSize = true;
            this.chkPublic.Location = new System.Drawing.Point(11, 115);
            this.chkPublic.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.chkPublic.Name = "chkPublic";
            this.chkPublic.Size = new System.Drawing.Size(71, 24);
            this.chkPublic.TabIndex = 39;
            this.chkPublic.Text = "Public";
            this.chkPublic.UseVisualStyleBackColor = true;
            this.chkPublic.CheckedChanged += new System.EventHandler(this.chk_CheckedChanged);
            // 
            // listModelsNew
            // 
            this.listModelsNew.Location = new System.Drawing.Point(464, 153);
            this.listModelsNew.Name = "listModelsNew";
            this.listModelsNew.PersistentCacheDirectory = "";
            this.listModelsNew.PersistentCacheSize = ((long)(100));
            this.listModelsNew.Size = new System.Drawing.Size(539, 537);
            this.listModelsNew.TabIndex = 41;
            this.listModelsNew.UseWIC = true;
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(2165, 1308);
            this.Controls.Add(this.listModelsNew);
            this.Controls.Add(this.panelClip);
            this.Controls.Add(this.panelModelDetails);
            this.Controls.Add(this.cmbFilter);
            this.Controls.Add(this.txtSearch);
            this.Controls.Add(this.cmdPhotos);
            this.Controls.Add(this.chkFavourite);
            this.Controls.Add(this.cmdFilter);
            this.Controls.Add(this.cmdClearSearch);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.cmbSortBy);
            this.Controls.Add(this.listClips);
            this.Controls.Add(this.listModels);
            this.Controls.Add(this.lblModelsLoaded);
            this.Controls.Add(this.menuStrip1);
            this.DoubleBuffered = true;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MainMenuStrip = this.menuStrip1;
            this.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.MinimumSize = new System.Drawing.Size(1972, 795);
            this.Name = "Form1";
            this.Text = "iStripper QuickPlayer";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Form1_FormClosing);
            this.Load += new System.EventHandler(this.Form1_Load);
            this.Shown += new System.EventHandler(this.Form1_Shown);
            this.Resize += new System.EventHandler(this.Form1_Resize);
            this.menuCardList.ResumeLayout(false);
            this.menuStrip1.ResumeLayout(false);
            this.menuStrip1.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.errorProvider1)).EndInit();
            this.panelModelDetails.ResumeLayout(false);
            this.panelModelDetails.PerformLayout();
            this.panelClip.ResumeLayout(false);
            this.panelClip.PerformLayout();
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
        private ComboBox cmbSortBy;
        private Label label1;
        private ColumnHeader ClipNumber;
        private TextBox txtSearch;
        private Label label2;
        private Button cmdClearSearch;
        private MenuStrip menuStrip1;
        private ToolStripMenuItem fileToolStripMenuItem;
        private ToolStripMenuItem reloadModelslstToolStripMenuItem;
        private ToolStripMenuItem exitToolStripMenuItem;
        private ToolStripMenuItem settingsToolStripMenuItem;
        private ToolStripMenuItem hotkeysToolStripMenuItem;
        private Button cmdFilter;
        private ErrorProvider errorProvider1;
        private ToolStripMenuItem enforceCardFilterToolStripMenuItem;
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
        private Button cmdPhotos;
        private ToolStripMenuItem includeDescriptionInSearchToolStripMenuItem;
        private ToolStripMenuItem includeShowTitleInSearchToolStripMenuItem;
        private ComboBox cmbFilter;
        private Panel panelModelDetails;
        private TextBox txtUserTags;
        private Label lblUserTags;
        private Label lblStats;
        private Label lblTags;
        private Label lblResolution;
        private TextBox txtDescription;
        private Label lblRatingScore;
        private Label lblCollection;
        private Label lblAge;
        private Panel panelClip;
        private Label label4;
        private TextBox txtClipType;
        private NumericUpDown numMinSizeMB;
        private Label label3;
        internal Label lblCipListDetails;
        private Button cmdNextClip;
        private Button button1;
        internal Label lblNowPlaying;
        private CheckBox chkDemo;
        private CheckBox chkXXX;
        private CheckBox chkFullNudity;
        private CheckBox chkNudity;
        private CheckBox chkTopless;
        private CheckBox chkNoNudity;
        private CheckBox chkPublic;
        private Button button2;
        private ToolStripMenuItem wallpaperToolStripMenuItem;
        private ToolStripMenuItem automaticWallpaperToolStripMenuItem;
        private TrackBarMenuItem trackbarWallpaperBrightness;
        private ToolStripMenuItem showTextToolStripMenuItem;
        private ToolStripMenuItem showKittyToolStripMenuItem;
        private ToolStripMenuItem cardScaleToolStripMenuItem;
        private TrackBarMenuItem trackBarCardScale;
        private Manina.Windows.Forms.ImageListView listModelsNew;
        //private Microsoft.Web.WebView2.WinForms.WebView2 webModels;
    }
}