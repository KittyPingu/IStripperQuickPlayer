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
            components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            lblModelsLoaded = new Label();
            menuCardList = new ContextMenuStrip(components);
            menuCardFavourite = new ToolStripMenuItem();
            ratingSlider = new TrackBarMenuItem();
            toolStripSeparator1 = new ToolStripSeparator();
            nameToolStripMenuItem = new ToolStripMenuItem();
            outfitToolStripMenuItem = new ToolStripMenuItem();
            ratingToolStripMenuItem = new ToolStripMenuItem();
            hotnessToolStripMenuItem = new ToolStripMenuItem();
            statsToolStripMenuItem = new ToolStripMenuItem();
            ageToolStripMenuItem = new ToolStripMenuItem();
            hairToolStripMenuItem = new ToolStripMenuItem();
            purchasedToolStripMenuItem = new ToolStripMenuItem();
            toolStripSeparator2 = new ToolStripSeparator();
            showInBrowserToolStripMenuItem = new ToolStripMenuItem();
            deleteFromDiskToolStripMenuItem = new ToolStripMenuItem();
            cmbMenuCardRating = new ToolStripComboBox();
            listClips = new ListView();
            ClipNumber = new ColumnHeader();
            ClipName = new ColumnHeader();
            Hotness = new ColumnHeader();
            ClipType = new ColumnHeader();
            ClipSize = new ColumnHeader();
            cmbSortBy = new ComboBox();
            label1 = new Label();
            txtSearch = new TextBox();
            label2 = new Label();
            cmdClearSearch = new Button();
            menuStrip1 = new MenuStrip();
            fileToolStripMenuItem = new ToolStripMenuItem();
            reloadModelslstToolStripMenuItem = new ToolStripMenuItem();
            exportFiltersToolStripMenuItem = new ToolStripMenuItem();
            loadPlaylistToolStripMenuItem = new ToolStripMenuItem();
            importFiltersToolStripMenuItem = new ToolStripMenuItem();
            exitToolStripMenuItem = new ToolStripMenuItem();
            settingsToolStripMenuItem = new ToolStripMenuItem();
            hotkeysToolStripMenuItem = new ToolStripMenuItem();
            enforceCardFilterToolStripMenuItem = new ToolStripMenuItem();
            randomPlayOrderToolStripMenuItem = new ToolStripMenuItem();
            cardScaleToolStripMenuItem = new ToolStripMenuItem();
            trackBarCardScale = new TrackBarMenuItem();
            zoomOnHoverToolStripMenuItem = new ToolStripMenuItem();
            trackBarZoomOnHover = new TrackBarMenuItem();
            menuShowRatingsStars = new ToolStripMenuItem();
            includeDescriptionInSearchToolStripMenuItem = new ToolStripMenuItem();
            includeShowTitleInSearchToolStripMenuItem = new ToolStripMenuItem();
            wallpaperToolStripMenuItem = new ToolStripMenuItem();
            automaticWallpaperToolStripMenuItem = new ToolStripMenuItem();
            trackbarWallpaperBrightness = new TrackBarMenuItem();
            showTextToolStripMenuItem = new ToolStripMenuItem();
            blurImageToolStripMenuItem = new ToolStripMenuItem();
            trackBarBlur = new TrackBarMenuItem();
            hideDesktopIconsToolStripMenuItem = new ToolStripMenuItem();
            showKittyToolStripMenuItem = new ToolStripMenuItem();
            lockPlayerToolStripMenuItem = new ToolStripMenuItem();
            minimizeToTrayToolStripMenuItem = new ToolStripMenuItem();
            darkModeToolStripMenuItem = new ToolStripMenuItem();
            cmdFilter = new Button();
            errorProvider1 = new ErrorProvider(components);
            chkFavourite = new CheckBox();
            cmdPhotos = new Button();
            cmbFilter = new ComboBox();
            panelModelDetails = new Panel();
            txtUserTags = new TextBox();
            lblUserTags = new Label();
            lblStats = new Label();
            lblTags = new Label();
            lblResolution = new Label();
            txtDescription = new TextBox();
            lblRatingScore = new Label();
            lblCollection = new Label();
            lblAge = new Label();
            panelClip = new Panel();
            cmdRewind = new Button();
            cmdPlayPause = new Button();
            cmdFastForward = new Button();
            lblPlaybackSpeed = new Label();
            cmbPlaybackSpeed = new ComboBox();
            trkPlaybackPosition = new TrackBar();
            lblPlaybackTime = new Label();
            lblFilterClip = new Label();
            cmdWallpaper = new Button();
            txtClipType = new TextBox();
            numMinSizeMB = new NumericUpDown();
            lblMinSize = new Label();
            cmdNextClip = new Button();
            cmdShowModel = new Button();
            lblNowPlaying = new Label();
            chkDemo = new CheckBox();
            chkXXX = new CheckBox();
            chkFullNudity = new CheckBox();
            chkNudity = new CheckBox();
            chkTopless = new CheckBox();
            chkNoNudity = new CheckBox();
            chkPublic = new CheckBox();
            listModelsNew = new Manina.Windows.Forms.ImageListView();
            splitContainer1 = new SplitContainer();
            cmbSortDirection = new ComboBox();
            notifyIcon1 = new NotifyIcon(components);
            menuCardList.SuspendLayout();
            menuStrip1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)errorProvider1).BeginInit();
            panelModelDetails.SuspendLayout();
            panelClip.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)trkPlaybackPosition).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numMinSizeMB).BeginInit();
            ((System.ComponentModel.ISupportInitialize)splitContainer1).BeginInit();
            splitContainer1.Panel1.SuspendLayout();
            splitContainer1.Panel2.SuspendLayout();
            splitContainer1.SuspendLayout();
            SuspendLayout();
            // 
            // lblModelsLoaded
            // 
            lblModelsLoaded.AutoSize = true;
            lblModelsLoaded.Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point);
            lblModelsLoaded.Location = new Point(11, 42);
            lblModelsLoaded.Margin = new Padding(6, 0, 6, 0);
            lblModelsLoaded.Name = "lblModelsLoaded";
            lblModelsLoaded.Size = new Size(163, 32);
            lblModelsLoaded.TabIndex = 1;
            lblModelsLoaded.Text = "Cards Loaded:";
            lblModelsLoaded.Click += lblModelsLoaded_Click;
            // 
            // menuCardList
            // 
            menuCardList.ImageScalingSize = new Size(20, 20);
            menuCardList.Items.AddRange(new ToolStripItem[] { menuCardFavourite, ratingSlider, toolStripSeparator1, nameToolStripMenuItem, outfitToolStripMenuItem, ratingToolStripMenuItem, hotnessToolStripMenuItem, statsToolStripMenuItem, ageToolStripMenuItem, hairToolStripMenuItem, purchasedToolStripMenuItem, toolStripSeparator2, showInBrowserToolStripMenuItem, deleteFromDiskToolStripMenuItem });
            menuCardList.Name = "menuCardList";
            menuCardList.Size = new Size(218, 451);
            menuCardList.Closing += menuCardList_Closing;
            menuCardList.Opening += menuCardList_Opening;
            // 
            // menuCardFavourite
            // 
            menuCardFavourite.CheckOnClick = true;
            menuCardFavourite.Name = "menuCardFavourite";
            menuCardFavourite.Size = new Size(217, 34);
            menuCardFavourite.Text = "Favourite";
            menuCardFavourite.CheckedChanged += menuCardFavourite_CheckedChanged;
            // 
            // ratingSlider
            // 
            ratingSlider.BackColor = Color.White;
            ratingSlider.ClientSize = new Size(108, 56);
            ratingSlider.Font = new Font("Microsoft Sans Serif", 6F, FontStyle.Regular, GraphicsUnit.Point);
            ratingSlider.ForeColor = Color.White;
            ratingSlider.Has2Values = false;
            ratingSlider.LargeChange = new decimal(new int[] { 5, 0, 0, 0 });
            ratingSlider.Maximum = new decimal(new int[] { 10, 0, 0, 0 });
            ratingSlider.Minimum = new decimal(new int[] { 0, 0, 0, 0 });
            ratingSlider.Name = "ratingSlider";
            ratingSlider.ScaleDivisions = new decimal(new int[] { 5, 0, 0, 0 });
            ratingSlider.Size = new Size(108, 56);
            ratingSlider.SmallChange = new decimal(new int[] { 1, 0, 0, 0 });
            ratingSlider.TickColor = Color.Black;
            ratingSlider.TickStyle = TickStyle.Both;
            ratingSlider.TrackbarColor = Color.White;
            ratingSlider.Value = new decimal(new int[] { 0, 0, 0, 0 });
            ratingSlider.Value2 = new decimal(new int[] { 0, 0, 0, 0 });
            ratingSlider.ValueChanged += RatingSlider_ValueChanged;
            // 
            // toolStripSeparator1
            // 
            toolStripSeparator1.Name = "toolStripSeparator1";
            toolStripSeparator1.Size = new Size(214, 6);
            // 
            // nameToolStripMenuItem
            // 
            nameToolStripMenuItem.Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
            nameToolStripMenuItem.Name = "nameToolStripMenuItem";
            nameToolStripMenuItem.Size = new Size(217, 34);
            nameToolStripMenuItem.Text = "Name:";
            // 
            // outfitToolStripMenuItem
            // 
            outfitToolStripMenuItem.Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            outfitToolStripMenuItem.Name = "outfitToolStripMenuItem";
            outfitToolStripMenuItem.Size = new Size(217, 34);
            outfitToolStripMenuItem.Text = "Outfit:";
            // 
            // ratingToolStripMenuItem
            // 
            ratingToolStripMenuItem.AutoToolTip = true;
            ratingToolStripMenuItem.Name = "ratingToolStripMenuItem";
            ratingToolStripMenuItem.Size = new Size(217, 34);
            ratingToolStripMenuItem.Tag = "Rating";
            ratingToolStripMenuItem.Text = "Rating:";
            ratingToolStripMenuItem.ToolTipText = "Official Rating";
            // 
            // hotnessToolStripMenuItem
            // 
            hotnessToolStripMenuItem.Name = "hotnessToolStripMenuItem";
            hotnessToolStripMenuItem.Size = new Size(217, 34);
            hotnessToolStripMenuItem.Text = "Hotness:";
            // 
            // statsToolStripMenuItem
            // 
            statsToolStripMenuItem.AutoToolTip = true;
            statsToolStripMenuItem.Name = "statsToolStripMenuItem";
            statsToolStripMenuItem.Size = new Size(217, 34);
            statsToolStripMenuItem.Tag = "Stats";
            statsToolStripMenuItem.Text = "Stats:";
            statsToolStripMenuItem.ToolTipText = "Model's Stats";
            // 
            // ageToolStripMenuItem
            // 
            ageToolStripMenuItem.Name = "ageToolStripMenuItem";
            ageToolStripMenuItem.Size = new Size(217, 34);
            ageToolStripMenuItem.Text = "Age:";
            // 
            // hairToolStripMenuItem
            // 
            hairToolStripMenuItem.Name = "hairToolStripMenuItem";
            hairToolStripMenuItem.Size = new Size(217, 34);
            hairToolStripMenuItem.Text = "Hair:";
            // 
            // purchasedToolStripMenuItem
            // 
            purchasedToolStripMenuItem.Name = "purchasedToolStripMenuItem";
            purchasedToolStripMenuItem.Size = new Size(217, 34);
            purchasedToolStripMenuItem.Text = "Purchased:";
            // 
            // toolStripSeparator2
            // 
            toolStripSeparator2.Name = "toolStripSeparator2";
            toolStripSeparator2.Size = new Size(214, 6);
            // 
            // showInBrowserToolStripMenuItem
            // 
            showInBrowserToolStripMenuItem.Name = "showInBrowserToolStripMenuItem";
            showInBrowserToolStripMenuItem.Size = new Size(217, 34);
            showInBrowserToolStripMenuItem.Text = "Show in Browser";
            showInBrowserToolStripMenuItem.Click += showInBrowserToolStripMenuItem_Click;
            // 
            // deleteFromDiskToolStripMenuItem
            // 
            deleteFromDiskToolStripMenuItem.Name = "deleteFromDiskToolStripMenuItem";
            deleteFromDiskToolStripMenuItem.Size = new Size(217, 34);
            deleteFromDiskToolStripMenuItem.Text = "Delete from Disk";
            deleteFromDiskToolStripMenuItem.Click += deleteFromDiskToolStripMenuItem_Click;
            // 
            // cmbMenuCardRating
            // 
            cmbMenuCardRating.Items.AddRange(new object[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10" });
            cmbMenuCardRating.Margin = new Padding(1, 0, 1, 0);
            cmbMenuCardRating.MaxDropDownItems = 10;
            cmbMenuCardRating.Name = "cmbMenuCardRating";
            cmbMenuCardRating.Size = new Size(121, 28);
            cmbMenuCardRating.Text = "My Rating";
            cmbMenuCardRating.ToolTipText = "Select a rating for this card";
            cmbMenuCardRating.SelectedIndexChanged += cmbMenuCardRating_SelectedIndexChanged;
            // 
            // listClips
            // 
            listClips.Columns.AddRange(new ColumnHeader[] { ClipNumber, ClipName, Hotness, ClipType, ClipSize });
            listClips.FullRowSelect = true;
            listClips.Location = new Point(13, 214);
            listClips.Margin = new Padding(6, 7, 6, 5);
            listClips.MultiSelect = false;
            listClips.Name = "listClips";
            listClips.Size = new Size(1222, 870);
            listClips.TabIndex = 13;
            listClips.UseCompatibleStateImageBehavior = false;
            listClips.View = View.Details;
            listClips.SelectedIndexChanged += listClips_SelectedIndexChanged;
            // 
            // ClipNumber
            // 
            ClipNumber.Text = "Clip";
            ClipNumber.Width = 34;
            // 
            // ClipName
            // 
            ClipName.Text = "ClipName";
            ClipName.Width = 200;
            // 
            // Hotness
            // 
            Hotness.Text = "Hotness";
            Hotness.Width = 120;
            // 
            // ClipType
            // 
            ClipType.Text = "ClipType";
            ClipType.Width = 280;
            // 
            // ClipSize
            // 
            ClipSize.Text = "Size";
            ClipSize.Width = 90;
            // 
            // cmbSortBy
            // 
            cmbSortBy.FormattingEnabled = true;
            cmbSortBy.Items.AddRange(new object[] { "My Rating", "Model Name", "Rating", "Age", "Breast Size", "Height", "Release Date", "Date Purchased" });
            cmbSortBy.Location = new Point(85, 101);
            cmbSortBy.Margin = new Padding(6, 5, 6, 5);
            cmbSortBy.Name = "cmbSortBy";
            cmbSortBy.Size = new Size(148, 33);
            cmbSortBy.TabIndex = 4;
            cmbSortBy.SelectedIndexChanged += comboBox1_SelectedIndexChanged;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(12, 106);
            label1.Margin = new Padding(6, 0, 6, 0);
            label1.Name = "label1";
            label1.Size = new Size(69, 25);
            label1.TabIndex = 3;
            label1.Text = "Sort By";
            // 
            // txtSearch
            // 
            txtSearch.Font = new Font("Segoe UI", 10.8F, FontStyle.Regular, GraphicsUnit.Point);
            txtSearch.Location = new Point(407, 42);
            txtSearch.Margin = new Padding(6, 5, 6, 5);
            txtSearch.Name = "txtSearch";
            txtSearch.PlaceholderText = "model name or tag";
            txtSearch.Size = new Size(388, 36);
            txtSearch.TabIndex = 20;
            txtSearch.TextChanged += txtSearch_TextChanged;
            txtSearch.Enter += txtSearch_Enter;
            txtSearch.KeyDown += txtSearch_KeyDown;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Font = new Font("Segoe UI", 10.8F, FontStyle.Regular, GraphicsUnit.Point);
            label2.Location = new Point(329, 44);
            label2.Margin = new Padding(6, 0, 6, 0);
            label2.Name = "label2";
            label2.Size = new Size(78, 30);
            label2.TabIndex = 21;
            label2.Text = "Search";
            // 
            // cmdClearSearch
            // 
            cmdClearSearch.BackColor = SystemColors.Window;
            cmdClearSearch.FlatAppearance.BorderSize = 0;
            cmdClearSearch.FlatStyle = FlatStyle.Flat;
            cmdClearSearch.Image = Properties.Resources.kindpng_4040161;
            cmdClearSearch.ImageAlign = ContentAlignment.MiddleLeft;
            cmdClearSearch.Location = new Point(791, 42);
            cmdClearSearch.Margin = new Padding(0);
            cmdClearSearch.Name = "cmdClearSearch";
            cmdClearSearch.Size = new Size(67, 37);
            cmdClearSearch.TabIndex = 23;
            cmdClearSearch.UseVisualStyleBackColor = false;
            cmdClearSearch.Visible = false;
            cmdClearSearch.Click += cmdClearSearch_Click;
            // 
            // menuStrip1
            // 
            menuStrip1.ImageScalingSize = new Size(20, 20);
            menuStrip1.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, settingsToolStripMenuItem });
            menuStrip1.Location = new Point(4, 77);
            menuStrip1.Name = "menuStrip1";
            menuStrip1.Padding = new Padding(10, 5, 0, 5);
            menuStrip1.Size = new Size(2590, 39);
            menuStrip1.TabIndex = 25;
            menuStrip1.Text = "menuStrip1";
            // 
            // fileToolStripMenuItem
            // 
            fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { reloadModelslstToolStripMenuItem, exportFiltersToolStripMenuItem, loadPlaylistToolStripMenuItem, importFiltersToolStripMenuItem, exitToolStripMenuItem });
            fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            fileToolStripMenuItem.Size = new Size(54, 29);
            fileToolStripMenuItem.Text = "File";
            // 
            // reloadModelslstToolStripMenuItem
            // 
            reloadModelslstToolStripMenuItem.Name = "reloadModelslstToolStripMenuItem";
            reloadModelslstToolStripMenuItem.ShortcutKeys = Keys.F5;
            reloadModelslstToolStripMenuItem.Size = new Size(263, 34);
            reloadModelslstToolStripMenuItem.Text = "Reload Models";
            reloadModelslstToolStripMenuItem.Click += cmdLoadModels_Click;
            // 
            // exportFiltersToolStripMenuItem
            // 
            exportFiltersToolStripMenuItem.Name = "exportFiltersToolStripMenuItem";
            exportFiltersToolStripMenuItem.Size = new Size(263, 34);
            exportFiltersToolStripMenuItem.Text = "Export Filters..";
            exportFiltersToolStripMenuItem.Click += exportFiltersToolStripMenuItem_Click;
            // 
            // loadPlaylistToolStripMenuItem
            // 
            loadPlaylistToolStripMenuItem.Name = "loadPlaylistToolStripMenuItem";
            loadPlaylistToolStripMenuItem.Size = new Size(263, 34);
            loadPlaylistToolStripMenuItem.Text = "Load Playlist..";
            loadPlaylistToolStripMenuItem.Click += loadPlaylistToolStripMenuItem_Click;
            // 
            // importFiltersToolStripMenuItem
            // 
            importFiltersToolStripMenuItem.Name = "importFiltersToolStripMenuItem";
            importFiltersToolStripMenuItem.Size = new Size(263, 34);
            importFiltersToolStripMenuItem.Text = "Import Filter..";
            importFiltersToolStripMenuItem.Click += importFiltersToolStripMenuItem_Click;
            // 
            // exitToolStripMenuItem
            // 
            exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            exitToolStripMenuItem.Size = new Size(263, 34);
            exitToolStripMenuItem.Text = "Exit";
            exitToolStripMenuItem.Click += exitToolStripMenuItem_Click;
            // 
            // settingsToolStripMenuItem
            // 
            settingsToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { hotkeysToolStripMenuItem, enforceCardFilterToolStripMenuItem, randomPlayOrderToolStripMenuItem, cardScaleToolStripMenuItem, zoomOnHoverToolStripMenuItem, menuShowRatingsStars, includeDescriptionInSearchToolStripMenuItem, includeShowTitleInSearchToolStripMenuItem, wallpaperToolStripMenuItem, showKittyToolStripMenuItem, lockPlayerToolStripMenuItem, minimizeToTrayToolStripMenuItem, darkModeToolStripMenuItem });
            settingsToolStripMenuItem.Name = "settingsToolStripMenuItem";
            settingsToolStripMenuItem.Size = new Size(92, 29);
            settingsToolStripMenuItem.Text = "Settings";
            // 
            // hotkeysToolStripMenuItem
            // 
            hotkeysToolStripMenuItem.Name = "hotkeysToolStripMenuItem";
            hotkeysToolStripMenuItem.Size = new Size(342, 34);
            hotkeysToolStripMenuItem.Text = "Hotkeys..";
            hotkeysToolStripMenuItem.Click += hotkeysToolStripMenuItem_Click;
            // 
            // enforceCardFilterToolStripMenuItem
            // 
            enforceCardFilterToolStripMenuItem.Checked = true;
            enforceCardFilterToolStripMenuItem.CheckOnClick = true;
            enforceCardFilterToolStripMenuItem.CheckState = CheckState.Checked;
            enforceCardFilterToolStripMenuItem.Name = "enforceCardFilterToolStripMenuItem";
            enforceCardFilterToolStripMenuItem.Size = new Size(342, 34);
            enforceCardFilterToolStripMenuItem.Text = "Enforce Card Filter";
            enforceCardFilterToolStripMenuItem.Click += enforceCardFilterToolStripMenuItem_Click;
            // 
            // randomPlayOrderToolStripMenuItem
            // 
            randomPlayOrderToolStripMenuItem.Checked = true;
            randomPlayOrderToolStripMenuItem.CheckOnClick = true;
            randomPlayOrderToolStripMenuItem.CheckState = CheckState.Checked;
            randomPlayOrderToolStripMenuItem.Name = "randomPlayOrderToolStripMenuItem";
            randomPlayOrderToolStripMenuItem.Size = new Size(342, 34);
            randomPlayOrderToolStripMenuItem.Text = "Random Play Order";
            randomPlayOrderToolStripMenuItem.Click += randomPlayOrderToolStripMenuItem_Click;
            // 
            // cardScaleToolStripMenuItem
            // 
            cardScaleToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { trackBarCardScale });
            cardScaleToolStripMenuItem.Name = "cardScaleToolStripMenuItem";
            cardScaleToolStripMenuItem.Size = new Size(342, 34);
            cardScaleToolStripMenuItem.Text = "Card Scale";
            // 
            // trackBarCardScale
            // 
            trackBarCardScale.BackColor = Color.Transparent;
            trackBarCardScale.ClientSize = new Size(200, 48);
            trackBarCardScale.Font = new Font("Microsoft Sans Serif", 6F, FontStyle.Regular, GraphicsUnit.Point);
            trackBarCardScale.ForeColor = Color.White;
            trackBarCardScale.Has2Values = false;
            trackBarCardScale.LargeChange = new decimal(new int[] { 25, 0, 0, 131072 });
            trackBarCardScale.Maximum = new decimal(new int[] { 2, 0, 0, 0 });
            trackBarCardScale.Minimum = new decimal(new int[] { 5, 0, 0, 65536 });
            trackBarCardScale.Name = "trackBarCardScale";
            trackBarCardScale.ScaleDivisions = new decimal(new int[] { 10, 0, 0, 0 });
            trackBarCardScale.Size = new Size(200, 48);
            trackBarCardScale.SmallChange = new decimal(new int[] { 5, 0, 0, 131072 });
            trackBarCardScale.Text = "trackBarMenuItem1";
            trackBarCardScale.TickColor = Color.White;
            trackBarCardScale.TickStyle = TickStyle.TopLeft;
            trackBarCardScale.TrackbarColor = Color.Transparent;
            trackBarCardScale.Value = new decimal(new int[] { 1, 0, 0, 0 });
            trackBarCardScale.Value2 = new decimal(new int[] { 1, 0, 0, 0 });
            trackBarCardScale.ValueChanged += trackBarCardScale_ValueChanged;
            // 
            // zoomOnHoverToolStripMenuItem
            // 
            zoomOnHoverToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { trackBarZoomOnHover });
            zoomOnHoverToolStripMenuItem.Name = "zoomOnHoverToolStripMenuItem";
            zoomOnHoverToolStripMenuItem.Size = new Size(342, 34);
            zoomOnHoverToolStripMenuItem.Text = "Zoom on Hover";
            // 
            // trackBarZoomOnHover
            // 
            trackBarZoomOnHover.BackColor = Color.Transparent;
            trackBarZoomOnHover.ClientSize = new Size(200, 48);
            trackBarZoomOnHover.Font = new Font("Microsoft Sans Serif", 6F, FontStyle.Regular, GraphicsUnit.Point);
            trackBarZoomOnHover.ForeColor = Color.White;
            trackBarZoomOnHover.Has2Values = false;
            trackBarZoomOnHover.LargeChange = new decimal(new int[] { 2, 0, 0, 65536 });
            trackBarZoomOnHover.Maximum = new decimal(new int[] { 1, 0, 0, 0 });
            trackBarZoomOnHover.Minimum = new decimal(new int[] { 0, 0, 0, 65536 });
            trackBarZoomOnHover.Name = "trackBarZoomOnHover";
            trackBarZoomOnHover.ScaleDivisions = new decimal(new int[] { 10, 0, 0, 0 });
            trackBarZoomOnHover.Size = new Size(200, 48);
            trackBarZoomOnHover.SmallChange = new decimal(new int[] { 5, 0, 0, 131072 });
            trackBarZoomOnHover.Text = "trackBarMenuItem1";
            trackBarZoomOnHover.TickColor = Color.White;
            trackBarZoomOnHover.TickStyle = TickStyle.TopLeft;
            trackBarZoomOnHover.TrackbarColor = Color.Transparent;
            trackBarZoomOnHover.Value = new decimal(new int[] { 1, 0, 0, 0 });
            trackBarZoomOnHover.Value2 = new decimal(new int[] { 1, 0, 0, 0 });
            trackBarZoomOnHover.ValueChanged += trackBarZoomOnHover_ValueChanged;
            // 
            // menuShowRatingsStars
            // 
            menuShowRatingsStars.CheckOnClick = true;
            menuShowRatingsStars.Name = "menuShowRatingsStars";
            menuShowRatingsStars.Size = new Size(342, 34);
            menuShowRatingsStars.Text = "Show MyRating Stars";
            menuShowRatingsStars.CheckedChanged += chkShowRatingStars_CheckedChanged;
            // 
            // includeDescriptionInSearchToolStripMenuItem
            // 
            includeDescriptionInSearchToolStripMenuItem.CheckOnClick = true;
            includeDescriptionInSearchToolStripMenuItem.Name = "includeDescriptionInSearchToolStripMenuItem";
            includeDescriptionInSearchToolStripMenuItem.Size = new Size(342, 34);
            includeDescriptionInSearchToolStripMenuItem.Text = "Include Description in Search";
            includeDescriptionInSearchToolStripMenuItem.Click += includeDescriptionInSearchToolStripMenuItem_Click;
            // 
            // includeShowTitleInSearchToolStripMenuItem
            // 
            includeShowTitleInSearchToolStripMenuItem.CheckOnClick = true;
            includeShowTitleInSearchToolStripMenuItem.Name = "includeShowTitleInSearchToolStripMenuItem";
            includeShowTitleInSearchToolStripMenuItem.Size = new Size(342, 34);
            includeShowTitleInSearchToolStripMenuItem.Text = "Include Show Title in Search";
            includeShowTitleInSearchToolStripMenuItem.Click += includeShowTitleInSearchToolStripMenuItem_Click;
            // 
            // wallpaperToolStripMenuItem
            // 
            wallpaperToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { automaticWallpaperToolStripMenuItem, trackbarWallpaperBrightness, showTextToolStripMenuItem, blurImageToolStripMenuItem, hideDesktopIconsToolStripMenuItem });
            wallpaperToolStripMenuItem.Name = "wallpaperToolStripMenuItem";
            wallpaperToolStripMenuItem.Size = new Size(342, 34);
            wallpaperToolStripMenuItem.Text = "Wallpaper";
            // 
            // automaticWallpaperToolStripMenuItem
            // 
            automaticWallpaperToolStripMenuItem.CheckOnClick = true;
            automaticWallpaperToolStripMenuItem.Name = "automaticWallpaperToolStripMenuItem";
            automaticWallpaperToolStripMenuItem.Size = new Size(290, 34);
            automaticWallpaperToolStripMenuItem.Text = "Automatic Wallpaper";
            automaticWallpaperToolStripMenuItem.CheckedChanged += automaticWallpaperToolStripMenuItem_CheckedChanged;
            // 
            // trackbarWallpaperBrightness
            // 
            trackbarWallpaperBrightness.BackColor = Color.Transparent;
            trackbarWallpaperBrightness.ClientSize = new Size(200, 48);
            trackbarWallpaperBrightness.Font = new Font("Microsoft Sans Serif", 6F, FontStyle.Regular, GraphicsUnit.Point);
            trackbarWallpaperBrightness.ForeColor = Color.White;
            trackbarWallpaperBrightness.Has2Values = false;
            trackbarWallpaperBrightness.LargeChange = new decimal(new int[] { 10, 0, 0, 0 });
            trackbarWallpaperBrightness.Maximum = new decimal(new int[] { 100, 0, 0, 0 });
            trackbarWallpaperBrightness.Minimum = new decimal(new int[] { 0, 0, 0, 0 });
            trackbarWallpaperBrightness.Name = "trackbarWallpaperBrightness";
            trackbarWallpaperBrightness.ScaleDivisions = new decimal(new int[] { 10, 0, 0, 0 });
            trackbarWallpaperBrightness.Size = new Size(200, 48);
            trackbarWallpaperBrightness.SmallChange = new decimal(new int[] { 5, 0, 0, 0 });
            trackbarWallpaperBrightness.Text = "trackBarMenuItem1";
            trackbarWallpaperBrightness.TickColor = Color.White;
            trackbarWallpaperBrightness.TickStyle = TickStyle.TopLeft;
            trackbarWallpaperBrightness.TrackbarColor = Color.Transparent;
            trackbarWallpaperBrightness.Value = new decimal(new int[] { 60, 0, 0, 0 });
            trackbarWallpaperBrightness.Value2 = new decimal(new int[] { 60, 0, 0, 0 });
            trackbarWallpaperBrightness.ValueChanged += trackbarWallpaperBrightness_ValueChanged;
            // 
            // showTextToolStripMenuItem
            // 
            showTextToolStripMenuItem.CheckOnClick = true;
            showTextToolStripMenuItem.Name = "showTextToolStripMenuItem";
            showTextToolStripMenuItem.Size = new Size(290, 34);
            showTextToolStripMenuItem.Text = "Show Text";
            showTextToolStripMenuItem.Click += showTextToolStripMenuItem_Click;
            // 
            // blurImageToolStripMenuItem
            // 
            blurImageToolStripMenuItem.CheckOnClick = true;
            blurImageToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { trackBarBlur });
            blurImageToolStripMenuItem.Name = "blurImageToolStripMenuItem";
            blurImageToolStripMenuItem.Size = new Size(290, 34);
            blurImageToolStripMenuItem.Text = "Blur Image";
            blurImageToolStripMenuItem.CheckStateChanged += blurImageToolStripMenuItem_CheckStateChanged;
            // 
            // trackBarBlur
            // 
            trackBarBlur.BackColor = Color.Transparent;
            trackBarBlur.ClientSize = new Size(200, 48);
            trackBarBlur.Font = new Font("Microsoft Sans Serif", 6F, FontStyle.Regular, GraphicsUnit.Point);
            trackBarBlur.ForeColor = Color.White;
            trackBarBlur.Has2Values = false;
            trackBarBlur.LargeChange = new decimal(new int[] { 5, 0, 0, 0 });
            trackBarBlur.Maximum = new decimal(new int[] { 50, 0, 0, 0 });
            trackBarBlur.Minimum = new decimal(new int[] { 0, 0, 0, 0 });
            trackBarBlur.Name = "trackBarBlur";
            trackBarBlur.ScaleDivisions = new decimal(new int[] { 10, 0, 0, 0 });
            trackBarBlur.Size = new Size(200, 48);
            trackBarBlur.SmallChange = new decimal(new int[] { 1, 0, 0, 0 });
            trackBarBlur.Text = "trackBarMenuItem1";
            trackBarBlur.TickColor = Color.White;
            trackBarBlur.TickStyle = TickStyle.TopLeft;
            trackBarBlur.TrackbarColor = Color.Transparent;
            trackBarBlur.Value = new decimal(new int[] { 50, 0, 0, 0 });
            trackBarBlur.Value2 = new decimal(new int[] { 50, 0, 0, 0 });
            trackBarBlur.ValueChanged += trackBarBlur_ValueChanged;
            // 
            // hideDesktopIconsToolStripMenuItem
            // 
            hideDesktopIconsToolStripMenuItem.CheckOnClick = true;
            hideDesktopIconsToolStripMenuItem.Name = "hideDesktopIconsToolStripMenuItem";
            hideDesktopIconsToolStripMenuItem.Size = new Size(290, 34);
            hideDesktopIconsToolStripMenuItem.Text = "Hide Desktop Icons";
            hideDesktopIconsToolStripMenuItem.CheckedChanged += hideDesktopIconsToolStripMenuItem_CheckedChanged;
            // 
            // showKittyToolStripMenuItem
            // 
            showKittyToolStripMenuItem.CheckOnClick = true;
            showKittyToolStripMenuItem.Name = "showKittyToolStripMenuItem";
            showKittyToolStripMenuItem.Size = new Size(342, 34);
            showKittyToolStripMenuItem.Text = "Show Kitty";
            showKittyToolStripMenuItem.CheckedChanged += showKittyToolStripMenuItem_CheckedChanged;
            // 
            // lockPlayerToolStripMenuItem
            // 
            lockPlayerToolStripMenuItem.CheckOnClick = true;
            lockPlayerToolStripMenuItem.Name = "lockPlayerToolStripMenuItem";
            lockPlayerToolStripMenuItem.Size = new Size(342, 34);
            lockPlayerToolStripMenuItem.Text = "Lock Player";
            lockPlayerToolStripMenuItem.Click += lockPlayerToolStripMenuItem_Click;
            // 
            // minimizeToTrayToolStripMenuItem
            // 
            minimizeToTrayToolStripMenuItem.CheckOnClick = true;
            minimizeToTrayToolStripMenuItem.Name = "minimizeToTrayToolStripMenuItem";
            minimizeToTrayToolStripMenuItem.Size = new Size(342, 34);
            minimizeToTrayToolStripMenuItem.Text = "Minimize to Tray";
            minimizeToTrayToolStripMenuItem.CheckStateChanged += minimizeToTrayToolStripMenuItem_CheckStateChanged;
            // 
            // darkModeToolStripMenuItem
            // 
            darkModeToolStripMenuItem.CheckOnClick = true;
            darkModeToolStripMenuItem.Name = "darkModeToolStripMenuItem";
            darkModeToolStripMenuItem.Size = new Size(342, 34);
            darkModeToolStripMenuItem.Text = "Dark Mode";
            darkModeToolStripMenuItem.CheckedChanged += darkModeToolStripMenuItem_CheckedChanged;
            // 
            // cmdFilter
            // 
            cmdFilter.Location = new Point(535, 101);
            cmdFilter.Margin = new Padding(6, 5, 6, 5);
            cmdFilter.Name = "cmdFilter";
            cmdFilter.Size = new Size(74, 35);
            cmdFilter.TabIndex = 28;
            cmdFilter.Text = "Filter...";
            cmdFilter.UseVisualStyleBackColor = true;
            cmdFilter.Click += cmdFilter_Click;
            // 
            // errorProvider1
            // 
            errorProvider1.ContainerControl = this;
            // 
            // chkFavourite
            // 
            chkFavourite.AutoSize = true;
            chkFavourite.Location = new Point(379, 104);
            chkFavourite.Margin = new Padding(6, 5, 6, 5);
            chkFavourite.Name = "chkFavourite";
            chkFavourite.Size = new Size(160, 29);
            chkFavourite.TabIndex = 32;
            chkFavourite.Text = "Only Favourites";
            chkFavourite.UseVisualStyleBackColor = true;
            chkFavourite.CheckedChanged += chkFavourite_CheckedChanged;
            // 
            // cmdPhotos
            // 
            cmdPhotos.Location = new Point(1105, 173);
            cmdPhotos.Margin = new Padding(6, 5, 6, 5);
            cmdPhotos.Name = "cmdPhotos";
            cmdPhotos.Size = new Size(131, 42);
            cmdPhotos.TabIndex = 35;
            cmdPhotos.Text = "Photos";
            cmdPhotos.UseVisualStyleBackColor = true;
            cmdPhotos.Click += cmdPhotos_Click;
            // 
            // cmbFilter
            // 
            cmbFilter.FormattingEnabled = true;
            cmbFilter.Location = new Point(617, 101);
            cmbFilter.Margin = new Padding(6, 5, 6, 5);
            cmbFilter.Name = "cmbFilter";
            cmbFilter.Size = new Size(178, 33);
            cmbFilter.TabIndex = 36;
            cmbFilter.DropDown += AdjustWidthComboBox_DropDown;
            cmbFilter.SelectedIndexChanged += cmbFilter_SelectedIndexChanged;
            // 
            // panelModelDetails
            // 
            panelModelDetails.Controls.Add(txtUserTags);
            panelModelDetails.Controls.Add(lblUserTags);
            panelModelDetails.Controls.Add(lblStats);
            panelModelDetails.Controls.Add(lblTags);
            panelModelDetails.Controls.Add(lblResolution);
            panelModelDetails.Controls.Add(txtDescription);
            panelModelDetails.Controls.Add(lblRatingScore);
            panelModelDetails.Controls.Add(lblCollection);
            panelModelDetails.Controls.Add(lblAge);
            panelModelDetails.Location = new Point(13, 1094);
            panelModelDetails.Margin = new Padding(4, 5, 4, 5);
            panelModelDetails.Name = "panelModelDetails";
            panelModelDetails.Size = new Size(1223, 437);
            panelModelDetails.TabIndex = 39;
            // 
            // txtUserTags
            // 
            txtUserTags.BackColor = SystemColors.ControlLightLight;
            txtUserTags.Font = new Font("Segoe UI", 10.8F, FontStyle.Regular, GraphicsUnit.Point);
            txtUserTags.Location = new Point(184, 112);
            txtUserTags.Margin = new Padding(6, 5, 6, 5);
            txtUserTags.Name = "txtUserTags";
            txtUserTags.Size = new Size(1038, 36);
            txtUserTags.TabIndex = 43;
            txtUserTags.TextChanged += txtUserTags_TextChanged;
            // 
            // lblUserTags
            // 
            lblUserTags.AutoSize = true;
            lblUserTags.Font = new Font("Segoe UI", 10.8F, FontStyle.Regular, GraphicsUnit.Point);
            lblUserTags.Location = new Point(18, 120);
            lblUserTags.Margin = new Padding(6, 0, 6, 0);
            lblUserTags.Name = "lblUserTags";
            lblUserTags.Size = new Size(111, 30);
            lblUserTags.TabIndex = 42;
            lblUserTags.Text = "User Tags:";
            // 
            // lblStats
            // 
            lblStats.AutoSize = true;
            lblStats.Font = new Font("Segoe UI", 10.8F, FontStyle.Regular, GraphicsUnit.Point);
            lblStats.Location = new Point(186, 11);
            lblStats.Margin = new Padding(6, 0, 6, 0);
            lblStats.Name = "lblStats";
            lblStats.Size = new Size(63, 30);
            lblStats.TabIndex = 41;
            lblStats.Text = "Stats:";
            // 
            // lblTags
            // 
            lblTags.AutoSize = true;
            lblTags.Font = new Font("Segoe UI", 10.8F, FontStyle.Regular, GraphicsUnit.Point);
            lblTags.Location = new Point(18, 66);
            lblTags.Margin = new Padding(6, 0, 6, 0);
            lblTags.MaximumSize = new Size(1223, 43);
            lblTags.Name = "lblTags";
            lblTags.Size = new Size(61, 30);
            lblTags.TabIndex = 40;
            lblTags.Text = "Tags:";
            // 
            // lblResolution
            // 
            lblResolution.AutoSize = true;
            lblResolution.Font = new Font("Segoe UI", 10.8F, FontStyle.Regular, GraphicsUnit.Point);
            lblResolution.Location = new Point(929, 11);
            lblResolution.Margin = new Padding(6, 0, 6, 0);
            lblResolution.Name = "lblResolution";
            lblResolution.Size = new Size(51, 30);
            lblResolution.TabIndex = 39;
            lblResolution.Text = "Res:";
            // 
            // txtDescription
            // 
            txtDescription.Location = new Point(18, 172);
            txtDescription.Margin = new Padding(6, 5, 6, 5);
            txtDescription.Multiline = true;
            txtDescription.Name = "txtDescription";
            txtDescription.ReadOnly = true;
            txtDescription.ScrollBars = ScrollBars.Vertical;
            txtDescription.Size = new Size(1204, 250);
            txtDescription.TabIndex = 38;
            // 
            // lblRatingScore
            // 
            lblRatingScore.AutoSize = true;
            lblRatingScore.Font = new Font("Segoe UI", 10.8F, FontStyle.Regular, GraphicsUnit.Point);
            lblRatingScore.Location = new Point(750, 11);
            lblRatingScore.Margin = new Padding(6, 0, 6, 0);
            lblRatingScore.Name = "lblRatingScore";
            lblRatingScore.Size = new Size(79, 30);
            lblRatingScore.TabIndex = 37;
            lblRatingScore.Text = "Rating:";
            // 
            // lblCollection
            // 
            lblCollection.AutoSize = true;
            lblCollection.Font = new Font("Segoe UI", 10.8F, FontStyle.Regular, GraphicsUnit.Point);
            lblCollection.Location = new Point(443, 11);
            lblCollection.Margin = new Padding(6, 0, 6, 0);
            lblCollection.Name = "lblCollection";
            lblCollection.Size = new Size(114, 30);
            lblCollection.TabIndex = 36;
            lblCollection.Text = "Collection:";
            // 
            // lblAge
            // 
            lblAge.AutoSize = true;
            lblAge.Font = new Font("Segoe UI", 10.8F, FontStyle.Regular, GraphicsUnit.Point);
            lblAge.Location = new Point(22, 11);
            lblAge.Margin = new Padding(6, 0, 6, 0);
            lblAge.Name = "lblAge";
            lblAge.Size = new Size(57, 30);
            lblAge.TabIndex = 35;
            lblAge.Text = "Age:";
            // 
            // panelClip
            // 
            panelClip.Controls.Add(lblPlaybackTime);
            panelClip.Controls.Add(trkPlaybackPosition);
            panelClip.Controls.Add(cmbPlaybackSpeed);
            panelClip.Controls.Add(lblPlaybackSpeed);
            panelClip.Controls.Add(cmdFastForward);
            panelClip.Controls.Add(cmdPlayPause);
            panelClip.Controls.Add(cmdRewind);
            panelClip.Controls.Add(lblFilterClip);
            panelClip.Controls.Add(cmdWallpaper);
            panelClip.Controls.Add(txtClipType);
            panelClip.Controls.Add(numMinSizeMB);
            panelClip.Controls.Add(lblMinSize);
            panelClip.Controls.Add(cmdNextClip);
            panelClip.Controls.Add(cmdShowModel);
            panelClip.Controls.Add(lblNowPlaying);
            panelClip.Controls.Add(chkDemo);
            panelClip.Controls.Add(chkXXX);
            panelClip.Controls.Add(chkFullNudity);
            panelClip.Controls.Add(chkNudity);
            panelClip.Controls.Add(chkTopless);
            panelClip.Controls.Add(chkNoNudity);
            panelClip.Controls.Add(chkPublic);
            panelClip.Location = new Point(0, 0);
            panelClip.Margin = new Padding(4, 5, 4, 5);
            panelClip.Name = "panelClip";
            panelClip.Size = new Size(1236, 215);
            panelClip.TabIndex = 40;
            //
            // cmdRewind
            //
            cmdRewind.Enabled = false;
            cmdRewind.Location = new Point(13, 55);
            cmdRewind.Margin = new Padding(4);
            cmdRewind.Name = "cmdRewind";
            cmdRewind.Size = new Size(83, 38);
            cmdRewind.TabIndex = 54;
            cmdRewind.Text = "-10 sec";
            cmdRewind.UseVisualStyleBackColor = true;
            cmdRewind.Click += cmdRewind_Click;
            //
            // cmdPlayPause
            //
            cmdPlayPause.Enabled = false;
            cmdPlayPause.Location = new Point(104, 55);
            cmdPlayPause.Margin = new Padding(4);
            cmdPlayPause.Name = "cmdPlayPause";
            cmdPlayPause.Size = new Size(123, 38);
            cmdPlayPause.TabIndex = 55;
            cmdPlayPause.Text = "Pause / Play";
            cmdPlayPause.UseVisualStyleBackColor = true;
            cmdPlayPause.Click += cmdPlayPause_Click;
            //
            // cmdFastForward
            //
            cmdFastForward.Enabled = false;
            cmdFastForward.Location = new Point(235, 55);
            cmdFastForward.Margin = new Padding(4);
            cmdFastForward.Name = "cmdFastForward";
            cmdFastForward.Size = new Size(83, 38);
            cmdFastForward.TabIndex = 56;
            cmdFastForward.Text = "+10 sec";
            cmdFastForward.UseVisualStyleBackColor = true;
            cmdFastForward.Click += cmdFastForward_Click;
            //
            // lblPlaybackSpeed
            //
            lblPlaybackSpeed.AutoSize = true;
            lblPlaybackSpeed.Location = new Point(330, 62);
            lblPlaybackSpeed.Name = "lblPlaybackSpeed";
            lblPlaybackSpeed.Size = new Size(63, 25);
            lblPlaybackSpeed.TabIndex = 57;
            lblPlaybackSpeed.Text = "Speed";
            //
            // cmbPlaybackSpeed
            //
            cmbPlaybackSpeed.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPlaybackSpeed.Enabled = false;
            cmbPlaybackSpeed.FormattingEnabled = true;
            cmbPlaybackSpeed.Items.AddRange(new object[] { "0.25x", "0.5x", "1x", "1.5x", "2x", "3x", "4x" });
            cmbPlaybackSpeed.Location = new Point(400, 58);
            cmbPlaybackSpeed.Name = "cmbPlaybackSpeed";
            cmbPlaybackSpeed.Size = new Size(88, 33);
            cmbPlaybackSpeed.TabIndex = 58;
            cmbPlaybackSpeed.SelectedIndex = 2;
            cmbPlaybackSpeed.SelectedIndexChanged += cmbPlaybackSpeed_SelectedIndexChanged;
            //
            // trkPlaybackPosition
            //
            trkPlaybackPosition.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            trkPlaybackPosition.Enabled = false;
            trkPlaybackPosition.Location = new Point(141, 94);
            trkPlaybackPosition.Maximum = 1;
            trkPlaybackPosition.Name = "trkPlaybackPosition";
            trkPlaybackPosition.Size = new Size(1081, 56);
            trkPlaybackPosition.TabIndex = 60;
            trkPlaybackPosition.TickStyle = TickStyle.None;
            trkPlaybackPosition.Scroll += trkPlaybackPosition_Scroll;
            trkPlaybackPosition.KeyDown += trkPlaybackPosition_KeyDown;
            trkPlaybackPosition.KeyUp += trkPlaybackPosition_KeyUp;
            trkPlaybackPosition.MouseDown += trkPlaybackPosition_MouseDown;
            trkPlaybackPosition.MouseUp += trkPlaybackPosition_MouseUp;
            //
            // lblPlaybackTime
            //
            lblPlaybackTime.Location = new Point(13, 102);
            lblPlaybackTime.Name = "lblPlaybackTime";
            lblPlaybackTime.Size = new Size(122, 30);
            lblPlaybackTime.TabIndex = 61;
            lblPlaybackTime.Text = "0:00 / 0:00";
            lblPlaybackTime.TextAlign = ContentAlignment.MiddleRight;
            // 
            // lblFilterClip
            // 
            lblFilterClip.AutoSize = true;
            lblFilterClip.Location = new Point(698, 179);
            lblFilterClip.Margin = new Padding(4, 0, 4, 0);
            lblFilterClip.Name = "lblFilterClip";
            lblFilterClip.Size = new Size(122, 25);
            lblFilterClip.TabIndex = 53;
            lblFilterClip.Text = "Filter ClipType";
            // 
            // cmdWallpaper
            // 
            cmdWallpaper.Location = new Point(804, 5);
            cmdWallpaper.Margin = new Padding(4);
            cmdWallpaper.Name = "cmdWallpaper";
            cmdWallpaper.Size = new Size(131, 53);
            cmdWallpaper.TabIndex = 41;
            cmdWallpaper.Text = "Wallpaper";
            cmdWallpaper.UseVisualStyleBackColor = true;
            cmdWallpaper.Click += cmdWallpaper_click;
            // 
            // txtClipType
            // 
            txtClipType.Location = new Point(829, 172);
            txtClipType.Margin = new Padding(4);
            txtClipType.Name = "txtClipType";
            txtClipType.Size = new Size(222, 31);
            txtClipType.TabIndex = 52;
            txtClipType.KeyDown += txtClipType_KeyDown;
            // 
            // numMinSizeMB
            // 
            numMinSizeMB.Increment = new decimal(new int[] { 10, 0, 0, 0 });
            numMinSizeMB.Location = new Point(935, 131);
            numMinSizeMB.Margin = new Padding(6, 5, 6, 5);
            numMinSizeMB.Maximum = new decimal(new int[] { 9999, 0, 0, 0 });
            numMinSizeMB.Name = "numMinSizeMB";
            numMinSizeMB.Size = new Size(115, 31);
            numMinSizeMB.TabIndex = 51;
            numMinSizeMB.ValueChanged += numMinSizeMB_ValueChanged;
            // 
            // lblMinSize
            // 
            lblMinSize.AutoSize = true;
            lblMinSize.Location = new Point(804, 133);
            lblMinSize.Margin = new Padding(6, 0, 6, 0);
            lblMinSize.Name = "lblMinSize";
            lblMinSize.Size = new Size(119, 25);
            lblMinSize.TabIndex = 50;
            lblMinSize.Text = "Min Size (MB)";
            // 
            // cmdNextClip
            // 
            cmdNextClip.Location = new Point(944, 5);
            cmdNextClip.Margin = new Padding(6, 5, 6, 5);
            cmdNextClip.Name = "cmdNextClip";
            cmdNextClip.Size = new Size(131, 53);
            cmdNextClip.TabIndex = 48;
            cmdNextClip.Text = "Next Clip";
            cmdNextClip.UseVisualStyleBackColor = true;
            cmdNextClip.Click += cmdNextClip_Click;
            // 
            // cmdShowModel
            // 
            cmdShowModel.Location = new Point(1085, 5);
            cmdShowModel.Margin = new Padding(6, 5, 6, 5);
            cmdShowModel.Name = "cmdShowModel";
            cmdShowModel.Size = new Size(151, 53);
            cmdShowModel.TabIndex = 47;
            cmdShowModel.Text = "Show Model";
            cmdShowModel.UseVisualStyleBackColor = true;
            cmdShowModel.Click += cmdShowModel_click;
            // 
            // lblNowPlaying
            // 
            lblNowPlaying.AutoSize = true;
            lblNowPlaying.Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point);
            lblNowPlaying.Location = new Point(11, 11);
            lblNowPlaying.Margin = new Padding(0);
            lblNowPlaying.Name = "lblNowPlaying";
            lblNowPlaying.Size = new Size(152, 32);
            lblNowPlaying.TabIndex = 46;
            lblNowPlaying.Text = "Now Playing:";
            lblNowPlaying.UseMnemonic = false;
            lblNowPlaying.TextChanged += lblNowPlaying_TextChanged;
            lblNowPlaying.Click += lblNowPlaying_Click;
            lblNowPlaying.MouseEnter += lblNowPlaying_MouseEnter;
            lblNowPlaying.MouseLeave += lblNowPlaying_MouseLeave;
            // 
            // chkDemo
            // 
            chkDemo.AutoSize = true;
            chkDemo.Location = new Point(1072, 179);
            chkDemo.Margin = new Padding(6, 0, 6, 0);
            chkDemo.Name = "chkDemo";
            chkDemo.Size = new Size(87, 29);
            chkDemo.TabIndex = 45;
            chkDemo.Text = "Demo";
            chkDemo.UseVisualStyleBackColor = true;
            chkDemo.CheckedChanged += chk_CheckedChanged;
            // 
            // chkXXX
            // 
            chkXXX.AutoSize = true;
            chkXXX.Checked = true;
            chkXXX.CheckState = CheckState.Checked;
            chkXXX.Location = new Point(614, 179);
            chkXXX.Margin = new Padding(6, 0, 6, 0);
            chkXXX.Name = "chkXXX";
            chkXXX.Size = new Size(71, 29);
            chkXXX.TabIndex = 44;
            chkXXX.Text = "XXX";
            chkXXX.UseVisualStyleBackColor = true;
            chkXXX.Click += chk_CheckedChanged;
            // 
            // chkFullNudity
            // 
            chkFullNudity.AutoSize = true;
            chkFullNudity.Checked = true;
            chkFullNudity.CheckState = CheckState.Checked;
            chkFullNudity.Location = new Point(472, 179);
            chkFullNudity.Margin = new Padding(6, 0, 6, 0);
            chkFullNudity.Name = "chkFullNudity";
            chkFullNudity.Size = new Size(123, 29);
            chkFullNudity.TabIndex = 43;
            chkFullNudity.Text = "Full Nudity";
            chkFullNudity.UseVisualStyleBackColor = true;
            chkFullNudity.CheckedChanged += chk_CheckedChanged;
            // 
            // chkNudity
            // 
            chkNudity.AutoSize = true;
            chkNudity.Checked = true;
            chkNudity.CheckState = CheckState.Checked;
            chkNudity.Location = new Point(367, 179);
            chkNudity.Margin = new Padding(6, 0, 6, 0);
            chkNudity.Name = "chkNudity";
            chkNudity.Size = new Size(91, 29);
            chkNudity.TabIndex = 42;
            chkNudity.Text = "Nudity";
            chkNudity.UseVisualStyleBackColor = true;
            chkNudity.CheckedChanged += chk_CheckedChanged;
            // 
            // chkTopless
            // 
            chkTopless.AutoSize = true;
            chkTopless.Checked = true;
            chkTopless.CheckState = CheckState.Checked;
            chkTopless.Location = new Point(256, 179);
            chkTopless.Margin = new Padding(6, 0, 6, 0);
            chkTopless.Name = "chkTopless";
            chkTopless.Size = new Size(96, 29);
            chkTopless.TabIndex = 41;
            chkTopless.Text = "Topless";
            chkTopless.UseVisualStyleBackColor = true;
            chkTopless.CheckedChanged += chk_CheckedChanged;
            // 
            // chkNoNudity
            // 
            chkNoNudity.AutoSize = true;
            chkNoNudity.Checked = true;
            chkNoNudity.CheckState = CheckState.Checked;
            chkNoNudity.Location = new Point(118, 179);
            chkNoNudity.Margin = new Padding(6, 0, 6, 0);
            chkNoNudity.Name = "chkNoNudity";
            chkNoNudity.Size = new Size(120, 29);
            chkNoNudity.TabIndex = 40;
            chkNoNudity.Text = "No Nudity";
            chkNoNudity.UseVisualStyleBackColor = true;
            chkNoNudity.CheckedChanged += chk_CheckedChanged;
            // 
            // chkPublic
            // 
            chkPublic.AutoSize = true;
            chkPublic.Location = new Point(13, 179);
            chkPublic.Margin = new Padding(6, 0, 6, 0);
            chkPublic.Name = "chkPublic";
            chkPublic.Size = new Size(85, 29);
            chkPublic.TabIndex = 39;
            chkPublic.Text = "Public";
            chkPublic.UseVisualStyleBackColor = true;
            chkPublic.CheckedChanged += chk_CheckedChanged;
            // 
            // listModelsNew
            // 
            listModelsNew.AllowCheckBoxClick = false;
            listModelsNew.AllowColumnClick = false;
            listModelsNew.AllowColumnResize = false;
            listModelsNew.AllowDuplicateFileNames = true;
            listModelsNew.AllowItemReorder = false;
            listModelsNew.AllowPaneResize = false;
            listModelsNew.CacheLimit = "0";
            listModelsNew.CacheMode = Manina.Windows.Forms.CacheMode.Continuous;
            listModelsNew.ContextMenuStrip = menuCardList;
            listModelsNew.Location = new Point(12, 139);
            listModelsNew.Margin = new Padding(4);
            listModelsNew.Name = "listModelsNew";
            listModelsNew.PersistentCacheDirectory = "";
            listModelsNew.PersistentCacheSize = 0L;
            listModelsNew.Size = new Size(1309, 1378);
            listModelsNew.TabIndex = 41;
            listModelsNew.UseWIC = true;
            listModelsNew.ItemClick += listModelsNew_ItemClick;
            listModelsNew.ItemHover += listModelsNew_ItemHover;
            listModelsNew.ItemDoubleClick += listModelsNew_ItemDoubleClick;
            listModelsNew.SelectionChanged += listModelsNew_SelectedIndexChanged;
            listModelsNew.MouseDown += listModelsNew_MouseDown;
            listModelsNew.MouseEnter += listModelsNew_MouseEnter;
            listModelsNew.MouseLeave += listModelsNew_MouseLeave;
            // 
            // splitContainer1
            // 
            splitContainer1.Dock = DockStyle.Fill;
            splitContainer1.Location = new Point(4, 116);
            splitContainer1.Margin = new Padding(4);
            splitContainer1.Name = "splitContainer1";
            // 
            // splitContainer1.Panel1
            // 
            splitContainer1.Panel1.Controls.Add(cmbSortDirection);
            splitContainer1.Panel1.Controls.Add(cmdClearSearch);
            splitContainer1.Panel1.Controls.Add(txtSearch);
            splitContainer1.Panel1.Controls.Add(label2);
            splitContainer1.Panel1.Controls.Add(label1);
            splitContainer1.Panel1.Controls.Add(lblModelsLoaded);
            splitContainer1.Panel1.Controls.Add(cmbSortBy);
            splitContainer1.Panel1.Controls.Add(cmdFilter);
            splitContainer1.Panel1.Controls.Add(chkFavourite);
            splitContainer1.Panel1.Controls.Add(cmbFilter);
            splitContainer1.Panel1.Controls.Add(listModelsNew);
            splitContainer1.Panel1MinSize = 720;
            // 
            // splitContainer1.Panel2
            // 
            splitContainer1.Panel2.Controls.Add(cmdPhotos);
            splitContainer1.Panel2.Controls.Add(listClips);
            splitContainer1.Panel2.Controls.Add(panelClip);
            splitContainer1.Panel2.Controls.Add(panelModelDetails);
            splitContainer1.Panel2MinSize = 880;
            splitContainer1.Size = new Size(2590, 1284);
            splitContainer1.SplitterDistance = 1337;
            splitContainer1.SplitterWidth = 6;
            splitContainer1.TabIndex = 42;
            splitContainer1.SplitterMoved += splitContainer1_SplitterMoved;
            // 
            // cmbSortDirection
            // 
            cmbSortDirection.FormattingEnabled = true;
            cmbSortDirection.Items.AddRange(new object[] { "Ascending", "Descending" });
            cmbSortDirection.Location = new Point(239, 101);
            cmbSortDirection.Margin = new Padding(6, 5, 6, 5);
            cmbSortDirection.Name = "cmbSortDirection";
            cmbSortDirection.Size = new Size(129, 33);
            cmbSortDirection.TabIndex = 42;
            cmbSortDirection.SelectedIndexChanged += cmbSortDirection_SelectedIndexChanged;
            // 
            // notifyIcon1
            // 
            notifyIcon1.BalloonTipText = "iStripper QuickPlayer";
            notifyIcon1.Icon = (Icon)resources.GetObject("notifyIcon1.Icon");
            notifyIcon1.Text = "QuickPlayer";
            notifyIcon1.DoubleClick += notifyIcon1_DoubleClick;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(144F, 144F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(2598, 1404);
            Controls.Add(splitContainer1);
            Controls.Add(menuStrip1);
            Icon = (Icon)resources.GetObject("$this.Icon");
            MainMenuStrip = menuStrip1;
            Margin = new Padding(6, 5, 6, 5);
            MinimumSize = new Size(2028, 950);
            Name = "Form1";
            Padding = new Padding(4, 77, 4, 4);
            Text = "iStripper QuickPlayer";
            FormClosing += Form1_FormClosing;
            Load += Form1_Load;
            Shown += Form1_Shown;
            Resize += Form1_Resize;
            menuCardList.ResumeLayout(false);
            menuStrip1.ResumeLayout(false);
            menuStrip1.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)errorProvider1).EndInit();
            panelModelDetails.ResumeLayout(false);
            panelModelDetails.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)trkPlaybackPosition).EndInit();
            panelClip.ResumeLayout(false);
            panelClip.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)numMinSizeMB).EndInit();
            splitContainer1.Panel1.ResumeLayout(false);
            splitContainer1.Panel1.PerformLayout();
            splitContainer1.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)splitContainer1).EndInit();
            splitContainer1.ResumeLayout(false);
            ResumeLayout(false);
            PerformLayout();
        }


        #endregion
        internal Label lblModelsLoaded;
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
        private Button cmdRewind;
        private Button cmdPlayPause;
        private Button cmdFastForward;
        private Label lblPlaybackSpeed;
        private ComboBox cmbPlaybackSpeed;
        private TrackBar trkPlaybackPosition;
        private Label lblPlaybackTime;
        private Label lblFilterClip;
        private TextBox txtClipType;
        private NumericUpDown numMinSizeMB;
        private Label lblMinSize;
        private Button cmdNextClip;
        private Button cmdShowModel;
        internal Label lblNowPlaying;
        private CheckBox chkDemo;
        private CheckBox chkXXX;
        private CheckBox chkFullNudity;
        private CheckBox chkNudity;
        private CheckBox chkTopless;
        private CheckBox chkNoNudity;
        private CheckBox chkPublic;
        private Button cmdWallpaper;
        private ToolStripMenuItem wallpaperToolStripMenuItem;
        private ToolStripMenuItem automaticWallpaperToolStripMenuItem;
        private TrackBarMenuItem trackbarWallpaperBrightness;
        private ToolStripMenuItem showTextToolStripMenuItem;
        private ToolStripMenuItem showKittyToolStripMenuItem;
        private ToolStripMenuItem cardScaleToolStripMenuItem;
        private TrackBarMenuItem trackBarCardScale;
        private Manina.Windows.Forms.ImageListView listModelsNew;
        private ToolStripMenuItem zoomOnHoverToolStripMenuItem;
        private TrackBarMenuItem trackBarZoomOnHover;
        private SplitContainer splitContainer1;
        private ToolStripSeparator toolStripSeparator2;
        private ToolStripMenuItem showInBrowserToolStripMenuItem;
        private ToolStripMenuItem lockPlayerToolStripMenuItem;
        private NotifyIcon notifyIcon1;
        private ToolStripMenuItem minimizeToTrayToolStripMenuItem;
        private ToolStripMenuItem blurImageToolStripMenuItem;
        private TrackBarMenuItem trackBarBlur;
        private ToolStripMenuItem hideDesktopIconsToolStripMenuItem;
        private ComboBox cmbSortDirection;
        private ToolStripMenuItem exportFiltersToolStripMenuItem;
        private ToolStripMenuItem importFiltersToolStripMenuItem;
        private ToolStripMenuItem deleteFromDiskToolStripMenuItem;
        private ToolStripMenuItem loadPlaylistToolStripMenuItem;
        private ToolStripMenuItem randomPlayOrderToolStripMenuItem;
        private ToolStripMenuItem darkModeToolStripMenuItem;
        //private Microsoft.Web.WebView2.WinForms.WebView2 webModels;
    }
}
