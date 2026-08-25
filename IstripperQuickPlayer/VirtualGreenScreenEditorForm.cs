using System.Diagnostics;
using System.Globalization;

namespace IStripperQuickPlayer;

internal sealed class VirtualGreenScreenEditorForm : Form
{
    const int PaneMaximumWidth = 640;
    const int PaneMaximumHeight = 560;
    const int ControlPanelWidth = 455;
    const int ControlWidth = 420;
    readonly CustomShowStore store;
    readonly CustomShowManifest show;
    readonly int fullOpacityThreshold;
    readonly CustomShowClip? editedClip;
    readonly CustomVirtualGreenScreen showSettings;
    CustomVirtualGreenScreen clipSettings;
    CustomVirtualGreenScreen renderSettings = new() { Enabled = false };
    readonly ComboBox clipSelector = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly ComboBox clipMode = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340,
          DropDownWidth = 340 };
    readonly CheckBox showEnabled = new()
        { Text = "Enable virtual green screen", AutoSize = true };
    readonly DxgiMaskPreviewControl preview = new()
        { Dock = DockStyle.Fill, AccessibleName = "Virtual green-screen comparison" };
    readonly ListBox keyColors = new()
        { Width = ControlWidth, Height = 220, DrawMode = DrawMode.OwnerDrawFixed,
          ItemHeight = 28 };
    readonly ListBox lockedColors = new()
        { Width = ControlWidth, Height = 220, DrawMode = DrawMode.OwnerDrawFixed,
          ItemHeight = 28 };
    readonly ToolTip matchToolTip = new()
        { InitialDelay = 150, ReshowDelay = 50, AutoPopDelay = 10_000,
          ShowAlways = true };
    readonly Controls.PlaybackSeekBar tolerance = new()
    {
        Minimum = 0, Maximum = 100, Value = 18, Width = ControlWidth, Height = 42,
        ShowValueText = true, ShowTimeToolTip = true,
        AccessibleName = "Selected green-screen colour tolerance"
    };
    readonly Controls.PlaybackSeekBar feather = new()
    {
        Minimum = 0, Maximum = 100, Value = 5, Width = ControlWidth, Height = 42,
        ShowValueText = true, ShowTimeToolTip = true,
        AccessibleName = "Selected green-screen colour edge feather"
    };
    readonly Controls.PlaybackSeekBar smallPatchSize = new()
    {
        Minimum = 0, Maximum = CustomVirtualGreenScreen.MaximumSmallPatchSize,
        Value = 0, Width = ControlWidth, Height = 42, ShowValueText = true,
        ShowTimeToolTip = true,
        AccessibleName = "Small non-transparent patch sampling radius"
    };
    readonly Controls.PlaybackSeekBar minimumTransparentArea = new()
    {
        Minimum = 0, Maximum = CustomVirtualGreenScreen.MaximumSmallPatchSize,
        Value = 0, Width = ControlWidth, Height = 42, ShowValueText = true,
        ShowTimeToolTip = true,
        AccessibleName = "Minimum transparent-area sampling radius"
    };
    readonly Button repick = new() { Text = "Repick selected", AutoSize = true };
    readonly Button remove = new() { Text = "Remove", AutoSize = true };
    readonly Controls.PlaybackSeekBar timeline = new()
    {
        Dock = DockStyle.Fill, Minimum = 0, Maximum = 1,
        ShowTimeToolTip = true, AccessibleName = "Green-screen preview position"
    };
    readonly Button previous = new() { Text = "◀ 1 frame", AutoSize = true };
    readonly Button play = new() { Text = "Play", AutoSize = true };
    readonly Button next = new() { Text = "1 frame ▶", AutoSize = true };
    readonly ComboBox speed = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 78 };
    readonly Label position = new()
        { Text = "0:00 / 0:00", AutoSize = true, Padding = new Padding(8, 7, 0, 0) };
    readonly Label framePosition = new()
        { Text = "Frame 0 / 0", AutoSize = true, Padding = new Padding(12, 7, 0, 0) };
    readonly Label status = new()
        { Text = "Left-click to key a colour; right-click to lock it.", AutoSize = true };
    readonly object frameSync = new();
    CancellationTokenSource? decodeCancellation;
    byte[]? currentRaw;
    int frameWidth, frameHeight;
    bool playing, loadingColorControls, loadingPatchSize,
        loadingMinimumTransparentArea, syncingColorSelection;
    int lastKeyTolerance = 18, lastKeyFeather = 5,
        lastLockTolerance = 18, lastLockFeather = 5;
    CustomVirtualGreenScreenColor? replaceColor;
    Point lastTooltipPixel = new(-1, -1);
    bool lastTooltipOutput;
    double fps = 25, playbackSpeed = 1;
    CustomShowClip currentClip;
    string foreground = "", alpha = "";
    long rangeStartMs, rangeEndMs;

    internal bool Saved { get; private set; }
    internal bool PreviewReady => preview.HasImage;

    internal VirtualGreenScreenEditorForm(CustomShowConfiguration configuration,
        string showId, string? clipId = null)
    {
        store = new(configuration.LibraryRoot);
        fullOpacityThreshold = configuration.FullOpacityThreshold;
        show = store.LoadManifest(showId);
        editedClip = clipId == null ? null : show.Clips.FirstOrDefault(value =>
            value.Included && value.Media != null && string.Equals(value.Id,
                clipId, StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidDataException("The selected custom clip no longer exists.");
        showSettings = show.VirtualGreenScreen?.Clone() ??
            new CustomVirtualGreenScreen { Enabled = false };
        clipSettings = editedClip?.VirtualGreenScreen?.Clone() ??
            showSettings.Clone();
        currentClip = editedClip ?? show.Clips.First(value =>
            value.Included && value.Media != null);

        Text = editedClip == null
            ? "Virtual Green Screen — " + show.Title
            : "Virtual Green Screen — Clip " +
                (Array.IndexOf(show.Clips, editedClip) + 1);
        ClientSize = new Size(1585, 880);
        MinimumSize = new Size(1165, 680);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        clipMode.Items.AddRange([
            "Inherit show settings", "Custom palette", "Disabled for this clip",
            "Apply palette to whole show"]);
        clipMode.SelectedIndex = editedClip?.VirtualGreenScreen == null ? 0 :
            editedClip.VirtualGreenScreen.Enabled ? 1 : 2;
        InitializeRoleDefaults(EffectiveSettings);
        showEnabled.Checked = showSettings.Enabled;
        speed.Items.AddRange(["0.25×", "0.5×", "1×", "2×"]);
        speed.SelectedIndex = 2;
        tolerance.ToolTipFormatter = value => $"Tolerance {value}";
        feather.ToolTipFormatter = value => $"Edge feather {value}";
        smallPatchSize.ToolTipFormatter = value => value == 0
            ? "Small-patch removal off"
            : $"Sample within a {value} source-pixel radius";
        minimumTransparentArea.ToolTipFormatter = value => value == 0
            ? "Large-area-only transparency off"
            : $"Require a keyed area within a {value} source-pixel radius";

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        FlowLayoutPanel scope = new()
            { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        scope.Controls.Add(new Label { Text = "Preview clip", AutoSize = true,
            Padding = new Padding(0, 7, 0, 0) });
        scope.Controls.Add(clipSelector);
        if (editedClip == null) scope.Controls.Add(showEnabled);
        else
        {
            scope.Controls.Add(new Label { Text = "Clip behaviour", AutoSize = true,
                Padding = new Padding(16, 7, 0, 0) });
            scope.Controls.Add(clipMode);
        }
        root.Controls.Add(scope, 0, 0);

        TableLayoutPanel headings = new()
            { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        headings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        headings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        headings.Controls.Add(Heading("Before key (existing alpha)"), 0, 0);
        headings.Controls.Add(Heading("With virtual green screen"), 1, 0);
        root.Controls.Add(headings, 0, 1);

        TableLayoutPanel editor = new()
            { Dock = DockStyle.Fill, ColumnCount = 2 };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,
            ControlPanelWidth));
        editor.Controls.Add(preview, 0, 0);
        FlowLayoutPanel palette = new()
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true,
            Padding = new Padding(10, 0, 0, 0)
        };
        palette.Controls.Add(new Label { Text = "Transparency key colours",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) });
        palette.Controls.Add(keyColors);
        palette.Controls.Add(new Label { Text = "Locked colours", AutoSize = true,
            Margin = new Padding(3, 6, 3, 0),
            Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) });
        palette.Controls.Add(lockedColors);
        palette.Controls.Add(new Label { Text = "Small-patch radius (source px)",
            AutoSize = true, Margin = new Padding(3, 10, 3, 0) });
        palette.Controls.Add(smallPatchSize);
        palette.Controls.Add(new Label
        {
            Text = "Minimum transparent-area radius (source px)",
            AutoSize = true, Margin = new Padding(3, 4, 3, 0)
        });
        palette.Controls.Add(minimumTransparentArea);
        palette.Controls.Add(new Label { Text = "Selected colour tolerance",
            AutoSize = true, Margin = new Padding(3, 10, 3, 0) });
        palette.Controls.Add(tolerance);
        palette.Controls.Add(new Label { Text = "Selected colour edge feather",
            AutoSize = true, Margin = new Padding(3, 4, 3, 0) });
        palette.Controls.Add(feather);
        FlowLayoutPanel paletteButtons = new()
            { AutoSize = true, WrapContents = true, Width = ControlWidth };
        paletteButtons.Controls.AddRange([repick, remove]);
        palette.Controls.Add(paletteButtons);
        palette.Controls.Add(status);
        editor.Controls.Add(palette, 1, 0);
        root.Controls.Add(editor, 0, 2);
        root.Controls.Add(timeline, 0, 3);

        FlowLayoutPanel transport = new()
            { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        transport.Controls.AddRange([previous, play, next,
            new Label { Text = "Speed", AutoSize = true,
                Padding = new Padding(12, 7, 0, 0) }, speed,
            position, framePosition]);
        root.Controls.Add(transport, 0, 4);

        Button save = new() { Text = "Save", AutoSize = true };
        Button cancel = new() { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        FlowLayoutPanel actions = new()
            { Dock = DockStyle.Fill, AutoSize = true,
              FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange([cancel, save]);
        root.Controls.Add(actions, 0, 5);
        Controls.Add(root);
        CancelButton = cancel;

        foreach ((CustomShowClip clip, int index) in show.Clips
                     .Select((value, index) => (value, index))
                     .Where(value => value.value.Included && value.value.Media != null &&
                         (editedClip == null || value.value.Id == editedClip.Id)))
            clipSelector.Items.Add(new ClipChoice(clip, index + 1));
        clipSelector.SelectedIndex = 0;
        clipSelector.Enabled = editedClip == null;

        keyColors.DrawItem += DrawColor;
        lockedColors.DrawItem += DrawColor;
        keyColors.SelectedIndexChanged += (_, _) =>
            ColorSelectionChanged(keyColors, lockedColors);
        lockedColors.SelectedIndexChanged += (_, _) =>
            ColorSelectionChanged(lockedColors, keyColors);
        smallPatchSize.Scroll += (_, _) => SmallPatchSizeChanged();
        minimumTransparentArea.Scroll += (_, _) =>
            MinimumTransparentAreaChanged();
        tolerance.Scroll += (_, _) => ToleranceChanged();
        feather.Scroll += (_, _) => FeatherChanged();
        repick.Click += (_, _) => BeginPick();
        remove.Click += (_, _) => RemoveSelected();
        preview.MouseClick += PreviewClicked;
        preview.MouseMove += PreviewMouseMoved;
        preview.MouseLeave += (_, _) => HideMatchToolTip();
        clipSelector.SelectedIndexChanged += (_, _) => SelectClip();
        clipMode.SelectedIndexChanged += (_, _) => ModeChanged();
        showEnabled.CheckedChanged += (_, _) =>
        {
            showSettings.Enabled = showEnabled.Checked;
            RefreshPalette(); RenderCurrentFrame();
        };
        timeline.Scroll += (_, _) => Seek(timeline.Value);
        previous.Click += (_, _) => Step(-1);
        next.Click += (_, _) => Step(1);
        play.Click += (_, _) => TogglePlayback();
        speed.SelectedIndexChanged += (_, _) => playbackSpeed =
            speed.SelectedIndex switch { 0 => .25, 1 => .5, 3 => 2, _ => 1 };
        save.Click += (_, _) => SaveChanges();
        Shown += (_, _) => { SelectClip(); RefreshPalette(); };
        FormClosing += (_, _) => CancelDecode();
        FormClosed += (_, _) => matchToolTip.Dispose();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space) { TogglePlayback(); e.Handled = true;
                e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Left) { Step(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { Step(1); e.Handled = true; }
        };
        AppTheme.Apply(this);
    }

    sealed record ClipChoice(CustomShowClip Clip, int Number)
    {
        public override string ToString() => $"Clip {Number}: " +
            $"{FormatTime(Clip.Media!.PlaybackStartMs)}–" +
            $"{FormatTime(CustomShowStore.PlaybackEnd(Clip.Media))}";
    }

    static Label Heading(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, AutoSize = true,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
        Padding = new Padding(0, 2, 0, 5)
    };

    CustomVirtualGreenScreen EffectiveSettings => editedClip == null
        ? showSettings
        : clipMode.SelectedIndex switch
        {
            0 => showSettings,
            1 => clipSettings,
            3 => showSettings,
            _ => new CustomVirtualGreenScreen { Enabled = false }
        };

    CustomVirtualGreenScreen? EditableSettings => editedClip == null
        ? showSettings : clipMode.SelectedIndex switch
        {
            1 => clipSettings,
            3 => showSettings,
            _ => null
        };

    void SelectClip()
    {
        if (clipSelector.SelectedItem is not ClipChoice choice) return;
        bool resume = playing;
        CancelDecode(); playing = false; play.Text = "Play";
        currentClip = choice.Clip;
        CustomClipMedia media = currentClip.Media!;
        string folder = Path.Combine(store.ShowsFolder, show.Id);
        foreground = CustomShowStore.ResolveRelative(folder, media.Foreground);
        alpha = CustomShowStore.ResolvePlaybackAlpha(folder, media.Alpha);
        rangeStartMs = media.PlaybackStartMs;
        rangeEndMs = CustomShowStore.PlaybackEnd(media);
        fps = CustomShowStore.TryFrameRate(media.FrameRate, out double value)
            ? value : 25;
        double scale = Math.Min(PaneMaximumWidth / (double)media.Width,
            PaneMaximumHeight / (double)media.Height);
        scale = Math.Min(1, scale);
        frameWidth = Math.Max(2, (int)Math.Round(media.Width * scale));
        frameHeight = Math.Max(2, (int)Math.Round(media.Height * scale));
        timeline.Minimum = checked((int)Math.Min(int.MaxValue, rangeStartMs));
        timeline.Maximum = checked((int)Math.Min(int.MaxValue,
            Math.Max(rangeStartMs + 1, rangeEndMs)));
        timeline.SmallChange = Math.Max(1, (int)Math.Round(1000 / fps));
        timeline.LargeChange = Math.Min(5_000,
            Math.Max(1, timeline.Maximum - timeline.Minimum));
        timeline.Value = timeline.Minimum;
        timeline.ToolTipFormatter = value => FormatTime(value - rangeStartMs);
        UpdatePosition();
        if (resume) StartPlayback(); else _ = ShowFrameAsync(timeline.Value);
    }

    void ModeChanged()
    {
        if (clipMode.SelectedIndex == 1 && clipSettings.Colors.Length == 0)
        {
            clipSettings = showSettings.Clone();
            clipSettings.Enabled = true;
        }
        if (clipMode.SelectedIndex == 3)
            showSettings.Enabled = true;
        RefreshPalette(); RenderCurrentFrame();
    }

    void RefreshPalette()
    {
        CustomVirtualGreenScreenColor? selected = SelectedColor;
        keyColors.BeginUpdate(); lockedColors.BeginUpdate();
        keyColors.Items.Clear(); lockedColors.Items.Clear();
        CustomVirtualGreenScreen settings = EffectiveSettings;
        Volatile.Write(ref renderSettings, settings.Clone());
        foreach (CustomVirtualGreenScreenColor color in settings.Colors)
            (color.Locked ? lockedColors : keyColors).Items.Add(color);
        keyColors.EndUpdate(); lockedColors.EndUpdate();
        if (selected != null && settings.Colors.Contains(selected))
            SelectColor(selected);
        else if (keyColors.Items.Count > 0)
            SelectColor((CustomVirtualGreenScreenColor)keyColors.Items[0]);
        else if (lockedColors.Items.Count > 0)
            SelectColor((CustomVirtualGreenScreenColor)lockedColors.Items[0]);
        else SelectColor(null);
        bool editable = EditableSettings != null;
        bool hasSelection = SelectedColor != null;
        repick.Enabled = remove.Enabled = editable && hasSelection;
        tolerance.Enabled = feather.Enabled = editable && hasSelection;
        loadingPatchSize = true;
        smallPatchSize.Value = VirtualGreenScreenMath.SmallPatchSize(settings);
        loadingPatchSize = false;
        smallPatchSize.Enabled = editable;
        loadingMinimumTransparentArea = true;
        minimumTransparentArea.Value =
            VirtualGreenScreenMath.MinimumTransparentAreaRadius(settings);
        loadingMinimumTransparentArea = false;
        minimumTransparentArea.Enabled = editable;
        preview.Cursor = editable ? Cursors.Cross : Cursors.Default;
        LoadSelectedColorControls();
    }

    void DrawColor(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (sender is not ListBox list || e.Index < 0 ||
            e.Index >= list.Items.Count) return;
        CustomVirtualGreenScreenColor item =
            (CustomVirtualGreenScreenColor)list.Items[e.Index];
        Color color = VirtualGreenScreenMath.ParseColor(item.Color);
        Rectangle swatch = new(e.Bounds.Left + 4, e.Bounds.Top + 4, 38,
            e.Bounds.Height - 8);
        using Brush brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, swatch);
        e.Graphics.DrawRectangle(Pens.Gray, swatch);
        TextRenderer.DrawText(e.Graphics,
            $"{item.Color}   Tolerance {item.Tolerance}   Feather " +
                VirtualGreenScreenMath.EffectiveFeather(item), e.Font,
            new Rectangle(swatch.Right + 8, e.Bounds.Top,
                e.Bounds.Width - swatch.Width - 16, e.Bounds.Height),
            e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        e.DrawFocusRectangle();
    }

    CustomVirtualGreenScreenColor? SelectedColor =>
        keyColors.SelectedItem as CustomVirtualGreenScreenColor ??
        lockedColors.SelectedItem as CustomVirtualGreenScreenColor;

    void InitializeRoleDefaults(CustomVirtualGreenScreen settings)
    {
        CustomVirtualGreenScreenColor? key = settings.Colors.FirstOrDefault(
            color => !color.Locked);
        if (key != null)
        {
            lastKeyTolerance = Math.Clamp(key.Tolerance, 0, 100);
            lastKeyFeather = VirtualGreenScreenMath.EffectiveFeather(key);
        }
        CustomVirtualGreenScreenColor? locked = settings.Colors.FirstOrDefault(
            color => color.Locked);
        if (locked != null)
        {
            lastLockTolerance = Math.Clamp(locked.Tolerance, 0, 100);
            lastLockFeather = VirtualGreenScreenMath.EffectiveFeather(locked);
        }
    }

    void ColorSelectionChanged(ListBox source, ListBox other)
    {
        if (syncingColorSelection || source.SelectedIndex < 0) return;
        syncingColorSelection = true;
        other.ClearSelected();
        syncingColorSelection = false;
        LoadSelectedColorControls();
    }

    void SelectColor(CustomVirtualGreenScreenColor? color)
    {
        syncingColorSelection = true;
        keyColors.ClearSelected(); lockedColors.ClearSelected();
        if (color != null)
        {
            ListBox list = color.Locked ? lockedColors : keyColors;
            int index = list.Items.IndexOf(color);
            if (index >= 0) list.SelectedIndex = index;
        }
        syncingColorSelection = false;
        LoadSelectedColorControls();
    }

    void LoadSelectedColorControls()
    {
        loadingColorControls = true;
        if (SelectedColor is CustomVirtualGreenScreenColor color)
        {
            tolerance.Value = Math.Clamp(color.Tolerance, 0, 100);
            feather.Value = VirtualGreenScreenMath.EffectiveFeather(color);
            if (color.Locked)
            {
                lastLockTolerance = tolerance.Value;
                lastLockFeather = feather.Value;
            }
            else
            {
                lastKeyTolerance = tolerance.Value;
                lastKeyFeather = feather.Value;
            }
        }
        loadingColorControls = false;
        bool editable = EditableSettings != null && SelectedColor != null;
        tolerance.Enabled = feather.Enabled = repick.Enabled = remove.Enabled = editable;
    }

    void ToleranceChanged()
    {
        if (loadingColorControls || EditableSettings == null ||
            SelectedColor is not CustomVirtualGreenScreenColor color) return;
        color.Tolerance = tolerance.Value;
        if (color.Locked) lastLockTolerance = tolerance.Value;
        else lastKeyTolerance = tolerance.Value;
        Volatile.Write(ref renderSettings, EffectiveSettings.Clone());
        keyColors.Invalidate(); lockedColors.Invalidate();
        RenderCurrentFrame();
    }

    void FeatherChanged()
    {
        if (loadingColorControls || EditableSettings == null ||
            SelectedColor is not CustomVirtualGreenScreenColor color) return;
        color.Feather = feather.Value;
        if (color.Locked) lastLockFeather = feather.Value;
        else lastKeyFeather = feather.Value;
        Volatile.Write(ref renderSettings, EffectiveSettings.Clone());
        keyColors.Invalidate(); lockedColors.Invalidate();
        RenderCurrentFrame();
    }

    void SmallPatchSizeChanged()
    {
        if (loadingPatchSize || EditableSettings is not
            CustomVirtualGreenScreen settings) return;
        settings.SmallPatchSize = smallPatchSize.Value;
        Volatile.Write(ref renderSettings, EffectiveSettings.Clone());
        RenderCurrentFrame();
    }

    void MinimumTransparentAreaChanged()
    {
        if (loadingMinimumTransparentArea || EditableSettings is not
            CustomVirtualGreenScreen settings) return;
        settings.MinimumTransparentAreaRadius = minimumTransparentArea.Value;
        Volatile.Write(ref renderSettings, EffectiveSettings.Clone());
        RenderCurrentFrame();
    }

    void BeginPick()
    {
        if (EditableSettings == null || SelectedColor is not
            CustomVirtualGreenScreenColor selected) return;
        if (ReferenceEquals(EditableSettings, showSettings) &&
            !showSettings.Enabled)
        {
            showSettings.Enabled = true;
            showEnabled.Checked = true;
        }
        replaceColor = selected;
        preview.Cursor = Cursors.Cross;
        status.Text = $"Click either preview to repick the selected " +
            (selected.Locked ? "locked colour." : "key colour.");
    }

    void PreviewClicked(object? sender, MouseEventArgs e)
    {
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right) ||
            preview.ImagePoint(e.Location) is not PointF point) return;
        bool locked = replaceColor?.Locked ?? e.Button == MouseButtons.Right;
        CustomVirtualGreenScreen? editable = EditableSettings;
        if (editable == null)
        {
            status.Text = "Choose Custom palette or Apply palette to whole show " +
                "before adding colours.";
            return;
        }
        if (ReferenceEquals(editable, showSettings) &&
            !showSettings.Enabled)
        {
            showSettings.Enabled = true;
            showEnabled.Checked = true;
        }
        int x = (int)point.X, y = (int)point.Y;
        if (x >= frameWidth) x -= frameWidth;
        if (x < 0 || x >= frameWidth || y < 0 || y >= frameHeight)
        {
            status.Text = "Choose a pixel inside either preview.";
            return;
        }
        byte[]? raw;
        lock (frameSync) raw = currentRaw;
        if (raw == null) return;
        int offset = (y * frameWidth + x) * 4;
        Color sampled = Color.FromArgb(raw[offset + 2], raw[offset + 1],
            raw[offset]);
        string value = VirtualGreenScreenMath.FormatColor(sampled);
        CustomVirtualGreenScreen settings = editable;
        int duplicate = Array.FindIndex(settings.Colors, item =>
            string.Equals(item.Color, value, StringComparison.OrdinalIgnoreCase));
        CustomVirtualGreenScreenColor? selectedAfter = null;
        if (duplicate >= 0 && !ReferenceEquals(settings.Colors[duplicate],
                replaceColor))
        {
            selectedAfter = settings.Colors[duplicate];
            CustomVirtualGreenScreenColor existing = settings.Colors[duplicate];
            if (existing.Locked != locked)
            {
                existing.Locked = locked;
                existing.Tolerance = locked ? lastLockTolerance : lastKeyTolerance;
                existing.Feather = locked ? lastLockFeather : lastKeyFeather;
                status.Text = locked
                    ? value + " is now locked and cannot be keyed transparent."
                    : value + " is now a transparency key colour.";
            }
            else status.Text = value + " is already in the palette.";
        }
        else if (replaceColor != null && settings.Colors.Contains(replaceColor))
        {
            replaceColor.Color = value;
            selectedAfter = replaceColor;
            status.Text = $"Replaced selected colour with {value} as " +
                (locked ? "locked." : "a transparency key.");
        }
        else if (settings.Colors.Length < CustomVirtualGreenScreen.MaximumColors)
        {
            int initialTolerance = locked ? lastLockTolerance : lastKeyTolerance;
            int initialFeather = locked ? lastLockFeather : lastKeyFeather;
            CustomVirtualGreenScreenColor added = new()
                { Color = value, Tolerance = initialTolerance,
                    Feather = initialFeather, Locked = locked };
            settings.Colors = [.. settings.Colors,
                added];
            selectedAfter = added;
            status.Text = $"Added {value} as " +
                (locked ? "locked" : "a transparency key") +
                $" with tolerance {initialTolerance} and feather " +
                $"{initialFeather}.";
        }
        else status.Text = $"The palette already contains the maximum of " +
            $"{CustomVirtualGreenScreen.MaximumColors} colours.";
        replaceColor = null;
        RefreshPalette();
        if (selectedAfter != null) SelectColor(selectedAfter);
        RenderCurrentFrame();
    }

    void RemoveSelected()
    {
        CustomVirtualGreenScreen? settings = EditableSettings;
        CustomVirtualGreenScreenColor? selected = SelectedColor;
        if (settings == null || selected == null) return;
        settings.Colors = settings.Colors.Where(item =>
            !ReferenceEquals(item, selected)).ToArray();
        RefreshPalette(); RenderCurrentFrame();
    }

    void TogglePlayback()
    {
        if (playing) PausePlayback(); else StartPlayback();
    }

    void StartPlayback()
    {
        if (timeline.Value >= timeline.Maximum) timeline.Value = timeline.Minimum;
        CancelDecode();
        playing = true; play.Text = "Pause";
        CancellationTokenSource cancellation = new();
        decodeCancellation = cancellation;
        _ = StreamAsync(timeline.Value, cancellation);
    }

    void PausePlayback()
    {
        playing = false; play.Text = "Play"; CancelDecode();
        _ = ShowFrameAsync(timeline.Value);
    }

    void Seek(int milliseconds)
    {
        bool resume = playing;
        CancelDecode(); playing = false; play.Text = "Play";
        timeline.Value = Math.Clamp(milliseconds, timeline.Minimum, timeline.Maximum);
        UpdatePosition();
        if (resume) StartPlayback(); else _ = ShowFrameAsync(timeline.Value);
    }

    void Step(int direction)
    {
        int frameMs = Math.Max(1, (int)Math.Round(1000 / fps));
        Seek(timeline.Value + direction * frameMs);
    }

    async Task ShowFrameAsync(long atMs)
    {
        CancelDecode();
        CancellationTokenSource cancellation = new();
        decodeCancellation = cancellation;
        try
        {
            using Process process = StartDecoder(atMs, oneFrame: true);
            using CancellationTokenRegistration registration =
                cancellation.Token.Register(() => Kill(process));
            _ = process.StandardError.ReadToEndAsync(cancellation.Token);
            byte[] raw = new byte[checked(frameWidth * frameHeight * 4)];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(raw,
                cancellation.Token);
            lock (frameSync) currentRaw = raw;
            Present(raw);
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { status.Text = "No frame at this position."; }
        catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
        finally
        {
            if (ReferenceEquals(decodeCancellation, cancellation))
                decodeCancellation = null;
            cancellation.Dispose();
        }
    }

    async Task StreamAsync(long atMs, CancellationTokenSource cancellation)
    {
        try
        {
            using Process process = StartDecoder(atMs, oneFrame: false);
            using CancellationTokenRegistration registration =
                cancellation.Token.Register(() => Kill(process));
            _ = process.StandardError.ReadToEndAsync(cancellation.Token);
            int length = checked(frameWidth * frameHeight * 4);
            long positionMs = atMs;
            double frameMs = 1000 / fps;
            while (!cancellation.IsCancellationRequested &&
                positionMs < rangeEndMs)
            {
                byte[] raw = new byte[length];
                await process.StandardOutput.BaseStream.ReadExactlyAsync(raw,
                    cancellation.Token);
                lock (frameSync) currentRaw = raw;
                Present(raw);
                positionMs += (long)Math.Max(1, Math.Round(frameMs));
                int shown = (int)Math.Min(timeline.Maximum, positionMs);
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(() =>
                    {
                        if (!ReferenceEquals(decodeCancellation, cancellation)) return;
                        timeline.Value = shown; UpdatePosition();
                    });
                await Task.Delay(TimeSpan.FromMilliseconds(
                    frameMs / playbackSpeed), cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        finally
        {
            bool current = ReferenceEquals(decodeCancellation, cancellation);
            if (current && IsHandleCreated && !IsDisposed)
                BeginInvoke(() =>
                {
                    if (!ReferenceEquals(decodeCancellation, cancellation)) return;
                    decodeCancellation = null; playing = false; play.Text = "Play";
                });
            cancellation.Dispose();
        }
    }

    Process StartDecoder(long atMs, bool oneFrame)
    {
        ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory,
            "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-v"); start.ArgumentList.Add("error");
        start.ArgumentList.Add("-nostdin");
        AddInput(start, atMs, foreground); AddInput(start, atMs, alpha);
        start.ArgumentList.Add("-filter_complex");
        start.ArgumentList.Add(
            $"[0:v]scale={frameWidth}:{frameHeight}:flags=bilinear,setsar=1[fg];" +
            $"[1:v]scale={frameWidth}:{frameHeight}:flags=bilinear," +
            "format=gray,setsar=1[a];[fg][a]alphamerge,format=bgra[out]");
        start.ArgumentList.Add("-map"); start.ArgumentList.Add("[out]");
        if (oneFrame)
        {
            start.ArgumentList.Add("-frames:v"); start.ArgumentList.Add("1");
        }
        start.ArgumentList.Add("-an"); start.ArgumentList.Add("-f");
        start.ArgumentList.Add("rawvideo"); start.ArgumentList.Add("-pix_fmt");
        start.ArgumentList.Add("bgra"); start.ArgumentList.Add("pipe:1");
        return Process.Start(start) ?? throw new InvalidOperationException(
            "FFmpeg preview could not start.");
    }

    static void AddInput(ProcessStartInfo start, long atMs, string path)
    {
        start.ArgumentList.Add("-ss");
        start.ArgumentList.Add((atMs / 1000d).ToString("0.###",
            CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-i"); start.ArgumentList.Add(path);
    }

    void Present(byte[] raw)
    {
        byte[] rendered = Render(raw);
        if (!IsDisposed) preview.SetSourceBgraOwned(rendered,
            frameWidth * 2, frameHeight);
    }

    void RenderCurrentFrame()
    {
        byte[]? raw;
        lock (frameSync) raw = currentRaw;
        if (raw != null) _ = Task.Run(() => Present(raw));
    }

    byte[] Render(byte[] raw)
    {
        int pixels = frameWidth * frameHeight;
        byte[] result = new byte[checked(pixels * 8)];
        byte[] choked = new byte[pixels];
        int radius = currentClip.EdgeChokePixels <= 0 ? 0 : Math.Max(1,
            (int)Math.Ceiling(currentClip.EdgeChokePixels * frameWidth /
                currentClip.Media!.Width));
        for (int y = 0; y < frameHeight; y++)
            for (int x = 0; x < frameWidth; x++)
            {
                byte value = 255;
                for (int dy = -radius; dy <= radius; dy += Math.Max(1, radius))
                    for (int dx = -radius; dx <= radius; dx += Math.Max(1, radius))
                    {
                        int sx = Math.Clamp(x + dx, 0, frameWidth - 1);
                        int sy = Math.Clamp(y + dy, 0, frameHeight - 1);
                        value = Math.Min(value, raw[(sy * frameWidth + sx) * 4 + 3]);
                    }
                choked[y * frameWidth + x] = value;
            }
        VirtualGreenScreenSample[] samples =
            VirtualGreenScreenMath.Samples(Volatile.Read(ref renderSettings));
        byte[] keyed = new byte[pixels];
        byte[] support = new byte[pixels];
        byte[] removed = new byte[pixels];
        byte[] before = new byte[pixels];
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            int source = pixel * 4;
            byte blue = raw[source], green = raw[source + 1], red = raw[source + 2];
            float cb = Math.Clamp(128 + (-.114572f * red - .385428f * green +
                .5f * blue), 0, 255) / 255f;
            float cr = Math.Clamp(128 + (.5f * red - .454153f * green -
                .045847f * blue), 0, 255) / 255f;
            float keep = VirtualGreenScreenMath.Keep(cb, cr, samples);
            before[pixel] = Threshold(choked[pixel]);
            keyed[pixel] = Threshold((byte)Math.Clamp((int)Math.Round(
                choked[pixel] * keep), 0, 255));
            byte sourceSupport = Threshold(raw[source + 3]);
            support[pixel] = Threshold((byte)Math.Clamp((int)Math.Round(
                raw[source + 3] * keep), 0, 255));
            removed[pixel] = sourceSupport > 0 && support[pixel] == 0
                ? (byte)1 : (byte)0;
        }
        int patchSize = VirtualGreenScreenMath.SmallPatchSize(
            Volatile.Read(ref renderSettings));
        int patchRadius = patchSize == 0 ? 0 : Math.Max(1,
            (int)Math.Round(patchSize * frameWidth /
                (double)currentClip.Media!.Width));
        int areaSize = VirtualGreenScreenMath.MinimumTransparentAreaRadius(
            Volatile.Read(ref renderSettings));
        int areaRadius = areaSize == 0 ? 0 : Math.Max(1,
            (int)Math.Round(areaSize * frameWidth /
                (double)currentClip.Media!.Width));
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            byte after = RemoveSmallPatch(keyed, support, pixel, patchRadius);
            if (areaRadius > 0 && before[pixel] > 0 && keyed[pixel] == 0 &&
                DiskSupport(removed, pixel, areaRadius) <
                    VirtualGreenScreenMath.MinimumTransparentAreaDiskSupport)
                after = before[pixel];
            int source = pixel * 4;
            int x = pixel % frameWidth, y = pixel / frameWidth;
            int left = (y * frameWidth * 2 + x) * 4;
            int right = (y * frameWidth * 2 + frameWidth + x) * 4;
            Composite(result, left, raw, source, before[pixel], pixel, frameWidth);
            Composite(result, right, raw, source, after, pixel, frameWidth);
        }
        return result;
    }

    byte RemoveSmallPatch(byte[] keyed, byte[] support, int pixel, int radius)
    {
        if (radius <= 0 || keyed[pixel] == 0) return keyed[pixel];
        int x = pixel % frameWidth, y = pixel / frameWidth;
        int surviving = 0;
        foreach (PointF direction in VirtualGreenScreenMath.SmallPatchDiskOffsets)
        {
            int sx = Math.Clamp(x + (int)Math.Round(direction.X * radius),
                0, frameWidth - 1);
            int sy = Math.Clamp(y + (int)Math.Round(direction.Y * radius),
                0, frameHeight - 1);
            if (support[sy * frameWidth + sx] > 0) surviving++;
            if (surviving >= VirtualGreenScreenMath.SmallPatchMinimumDiskSupport)
                return keyed[pixel];
        }
        return 0;
    }

    int DiskSupport(byte[] values, int pixel, int radius)
    {
        int x = pixel % frameWidth, y = pixel / frameWidth, surviving = 0;
        foreach (PointF direction in VirtualGreenScreenMath.SmallPatchDiskOffsets)
        {
            int sx = Math.Clamp(x + (int)Math.Round(direction.X * radius),
                0, frameWidth - 1);
            int sy = Math.Clamp(y + (int)Math.Round(direction.Y * radius),
                0, frameHeight - 1);
            if (values[sy * frameWidth + sx] > 0) surviving++;
        }
        return surviving;
    }

    byte Threshold(byte alphaValue) => alphaValue < currentClip.AlphaThreshold
        ? (byte)0 : alphaValue >= fullOpacityThreshold
            ? (byte)255 : alphaValue;

    static void Composite(byte[] output, int destination, byte[] source,
        int offset, byte alpha, int pixel, int width)
    {
        int x = pixel % width, y = pixel / width;
        int checker = ((x / 16 + y / 16) & 1) == 0 ? 190 : 125;
        output[destination] = (byte)((source[offset] * alpha +
            checker * (255 - alpha)) / 255);
        output[destination + 1] = (byte)((source[offset + 1] * alpha +
            checker * (255 - alpha)) / 255);
        output[destination + 2] = (byte)((source[offset + 2] * alpha +
            checker * (255 - alpha)) / 255);
        output[destination + 3] = 255;
    }

    void UpdatePosition()
    {
        long elapsed = Math.Max(0, timeline.Value - rangeStartMs);
        long duration = Math.Max(1, rangeEndMs - rangeStartMs);
        position.Text = $"{FormatTime(elapsed)} / {FormatTime(duration)}";
        int frame = Math.Min((int)Math.Ceiling(duration * fps / 1000d),
            (int)Math.Floor(elapsed * fps / 1000d) + 1);
        int total = Math.Max(1, (int)Math.Ceiling(duration * fps / 1000d));
        framePosition.Text = $"Frame {Math.Max(1, frame)} / {total}";
    }

    void SaveChanges()
    {
        if (editedClip == null) show.VirtualGreenScreen = showSettings.Clone();
        else
        {
            CustomShowClip savedClip = show.Clips.First(value =>
                value.Id == editedClip.Id);
            if (clipMode.SelectedIndex == 3)
            {
                show.VirtualGreenScreen = showSettings.Clone();
                savedClip.VirtualGreenScreen = null;
            }
            else savedClip.VirtualGreenScreen = clipMode.SelectedIndex switch
            {
                0 => null,
                1 => clipSettings.Clone(),
                _ => new CustomVirtualGreenScreen
                {
                    Enabled = false,
                    SmallPatchSize = clipSettings.SmallPatchSize,
                    MinimumTransparentAreaRadius =
                        clipSettings.MinimumTransparentAreaRadius,
                    Colors = clipSettings.Colors.Select(value =>
                        new CustomVirtualGreenScreenColor
                        { Color = value.Color, Tolerance = value.Tolerance,
                            Feather = value.Feather,
                            Locked = value.Locked }).ToArray()
                }
            };
        }
        store.SaveManifest(show);
        Saved = true;
        DialogResult = DialogResult.OK;
    }

    void CancelDecode()
    {
        CancellationTokenSource? cancellation = decodeCancellation;
        decodeCancellation = null;
        cancellation?.Cancel();
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
    }

    static string FormatTime(long milliseconds)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") :
            value.ToString(@"m\:ss");
    }
}
