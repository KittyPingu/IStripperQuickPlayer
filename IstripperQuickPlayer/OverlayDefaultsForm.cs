using EnumDescription;
using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using MaterialSkin;
using MaterialSkin.Controls;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace IStripperQuickPlayer;

internal sealed class OverlayDefaultsForm : MaterialForm
{
    private sealed class PreviewPanel : Panel
    {
        internal PreviewPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    private readonly DataGridView grid = new()
    {
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        MultiSelect = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly PreviewPanel previewSurface = new()
    {
        Dock = DockStyle.Fill,
        Margin = new Padding(10)
    };
    private readonly Label previewLabel = new()
    {
        Dock = DockStyle.Top,
        Height = 34,
        Text = "Select an overlay",
        TextAlign = ContentAlignment.MiddleCenter
    };
    private readonly System.Windows.Forms.Timer previewTimer = new();
    private readonly Dictionary<string, CardOverlayLoader.Preview> previews =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<CardOverlayChoice> choices;
    private DirectCompositionCardOverlayControl? composition;
    private CardOverlayLoader.Preview? currentPreview;
    private int previewFrame = -1;

    internal CardOverlayRules Rules { get; private set; }

    internal OverlayDefaultsForm(
        IReadOnlyList<CardOverlayChoice> availableChoices,
        CardOverlayRules rules,
        DirectCompositionCardOverlayControl? composition = null)
    {
        this.composition = composition;
        choices = [new("", "(None)", ""), .. availableChoices];
        Rules = rules;
        Text = "Overlay defaults";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new System.Drawing.Size(820, 560);
        Size = new System.Drawing.Size(1040, 700);
        ShowIcon = false;
        ShowInTaskbar = false;
        ApplyTheme();

        Label explanation = new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Official iStripper card/person overlays always win. " +
                "Default rules are evaluated from top to bottom; select a " +
                "row and use Move up/down to change priority. A value of 0 " +
                "disables a count or rating rule."
        };

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Rule",
            ReadOnly = true,
            Width = 230
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Value",
            Width = 75
        });
        grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            DataSource = choices.ToArray(),
            DisplayMember = nameof(CardOverlayChoice.Name),
            ValueMember = nameof(CardOverlayChoice.Id),
            HeaderText = "Overlay",
            Width = 210,
            FlatStyle = FlatStyle.Flat
        });
        grid.DataError += (_, _) => { };
        AddRuleRows(rules);

        Button moveUp = new()
        {
            AutoSize = true,
            Name = "moveUpButton",
            Text = "Move up"
        };
        Button moveDown = new()
        {
            AutoSize = true,
            Name = "moveDownButton",
            Text = "Move down"
        };
        Button save = new()
        {
            AutoSize = true,
            DialogResult = DialogResult.OK,
            Name = "saveButton",
            Text = "Save"
        };
        Button cancel = new()
        {
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Name = "cancelButton",
            Text = "Cancel"
        };
        moveUp.Click += (_, _) => MoveSelectedRow(-1);
        moveDown.Click += (_, _) => MoveSelectedRow(1);

        TableLayoutPanel buttons = new()
        {
            AutoSize = true,
            ColumnCount = 5,
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };
        buttons.ColumnStyles.Add(new(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new(SizeType.AutoSize));
        buttons.Controls.Add(moveUp, 0, 0);
        buttons.Controls.Add(moveDown, 1, 0);
        buttons.Controls.Add(save, 3, 0);
        buttons.Controls.Add(cancel, 4, 0);

        TableLayoutPanel previewLayout = new()
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        previewLayout.RowStyles.Add(new(SizeType.Absolute, 34));
        previewLayout.RowStyles.Add(new(SizeType.Percent, 100));
        previewLayout.Controls.Add(previewLabel, 0, 0);
        previewLayout.Controls.Add(previewSurface, 0, 1);

        SplitContainer content = new()
        {
            Size = new System.Drawing.Size(900, 500),
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
            Panel1MinSize = 480,
            Panel2MinSize = 230,
            SplitterDistance = 650
        };
        content.Panel1.Controls.Add(grid);
        content.Panel2.Controls.Add(previewLayout);

        TableLayoutPanel layout = new()
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 3
        };
        layout.RowStyles.Add(new(SizeType.Absolute, 68));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(explanation, 0, 0);
        layout.Controls.Add(content, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.SelectionChanged += (_, _) => UpdatePreview();
        grid.CellValueChanged += (_, e) =>
        {
            if (e.ColumnIndex == 2)
                UpdatePreview();
        };
        previewSurface.Paint += previewSurface_Paint;
        previewTimer.Tick += (_, _) => AdvancePreview();
        Shown += (_, _) =>
        {
            if (composition?.SetPreview(
                    previewSurface,
                    () => currentPreview?.Frame()) == false)
                composition = null;
            if (grid.Rows.Count > 0)
                grid.CurrentCell = grid.Rows[0].Cells[0];
            UpdatePreview();
        };
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void AddRuleRows(CardOverlayRules rules)
    {
        Dictionary<string, (
            string Label, string Value, string? Overlay, bool ReadOnly)> rows =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["recent"] = (
                    "Newest releases",
                    rules.RecentCount.ToString(CultureInfo.CurrentCulture),
                    rules.RecentOverlayId, false),
                ["purchase"] = (
                    "Most recent purchases",
                    rules.RecentPurchaseCount.ToString(
                        CultureInfo.CurrentCulture),
                    rules.RecentPurchaseOverlayId, false),
                ["rating"] = (
                    "My Rating at least (0-5)",
                    rules.MinimumMyRating.ToString(
                        "0.0", CultureInfo.CurrentCulture),
                    rules.RatingOverlayId, false)
            };
        foreach (Enums.HotnessCode hotness in
                 Enum.GetValues<Enums.HotnessCode>())
        {
            string value = ((int)hotness).ToString(
                CultureInfo.InvariantCulture);
            rows[$"hotness:{value}"] = (
                $"Hotness - {hotness.GetDescription()}", "",
                rules.Hotness.GetValueOrDefault(value), true);
        }
        foreach (Enums.CollectionType type in
                 Enum.GetValues<Enums.CollectionType>()
                     .Where(type =>
                         type != Enums.CollectionType.Undefined))
        {
            rows[$"type:{type}"] = (
                $"Card type - {type.GetDescription()}", "",
                rules.CardTypes.GetValueOrDefault(type.ToString()), true);
        }
        foreach (string key in CardOverlayLoader.GetRulePriority(rules))
        {
            var row = rows[key];
            AddRow(row.Label, row.Value, row.Overlay, key, row.ReadOnly);
        }
    }

    internal static bool VerifyLayout()
    {
        using OverlayDefaultsForm form = new(
            [new("test", "Test overlay", "test")], new());
        form.CreateControl();
        form.PerformLayout();
        form.Controls[0].PerformLayout();
        Control save = form.Controls.Find("saveButton", true).Single();
        Control cancel = form.Controls.Find("cancelButton", true).Single();
        string second = form.grid.Rows[1].Tag?.ToString() ?? "";
        form.grid.CurrentCell = form.grid.Rows[1].Cells[0];
        form.MoveSelectedRow(-1);
        return form.grid.Columns[2].Width == 210 &&
            form.grid.Rows[0].Tag?.ToString() == second &&
            form.grid.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Tag?.ToString() == "purchase") &&
            !form.previewTimer.Enabled &&
            form.previewSurface.Width > 0 &&
            save.Right <= save.Parent!.ClientSize.Width &&
            cancel.Right <= cancel.Parent!.ClientSize.Width &&
            save.Bottom <= save.Parent.ClientSize.Height &&
            cancel.Bottom <= cancel.Parent.ClientSize.Height;
    }

    private void ApplyTheme()
    {
        MaterialSkinManager skin = MaterialSkinManager.Instance;
        skin.RemoveFormToManage(this);
        skin.AddFormToManage(this);
        skin.Theme = Properties.Settings.Default.DarkMode
            ? MaterialSkinManager.Themes.DARK
            : MaterialSkinManager.Themes.LIGHT;
        skin.ColorScheme = new(
            Primary.BlueGrey800, Primary.BlueGrey900,
            Primary.BlueGrey500, Accent.LightBlue200, TextShade.WHITE);

        bool dark = Properties.Settings.Default.DarkMode;
        Color background = dark ? Color.FromArgb(48, 48, 48)
            : SystemColors.Window;
        Color secondary = dark ? Color.FromArgb(64, 64, 64)
            : SystemColors.Control;
        Color foreground = dark ? Color.AntiqueWhite
            : SystemColors.ControlText;
        grid.BackgroundColor = background;
        grid.GridColor = dark ? Color.FromArgb(90, 90, 90)
            : SystemColors.ControlDark;
        grid.EnableHeadersVisualStyles = false;
        grid.DefaultCellStyle.BackColor = background;
        grid.DefaultCellStyle.ForeColor = foreground;
        grid.DefaultCellStyle.SelectionBackColor =
            dark ? Color.FromArgb(55, 105, 135)
                : SystemColors.Highlight;
        grid.DefaultCellStyle.SelectionForeColor =
            dark ? Color.White : SystemColors.HighlightText;
        grid.ColumnHeadersDefaultCellStyle.BackColor = secondary;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
        grid.RowsDefaultCellStyle.BackColor = background;
        grid.RowsDefaultCellStyle.ForeColor = foreground;
        previewSurface.BackColor = dark ? Color.FromArgb(30, 30, 30)
            : Color.FromArgb(225, 225, 225);
        previewLabel.BackColor = secondary;
        previewLabel.ForeColor = foreground;
        BackColor = background;
        ForeColor = foreground;
    }

    private void AddRow(string label, string value, string? overlayId,
        string key, bool valueReadOnly)
    {
        int index = grid.Rows.Add(label, value,
            choices.Any(choice => choice.Id == overlayId) ? overlayId : "");
        DataGridViewRow row = grid.Rows[index];
        row.Tag = key;
        row.Cells[1].ReadOnly = valueReadOnly;
        if (valueReadOnly)
            row.Cells[1].Style.BackColor =
                Properties.Settings.Default.DarkMode
                    ? Color.FromArgb(64, 64, 64)
                    : SystemColors.Control;
    }

    private void MoveSelectedRow(int direction)
    {
        if (grid.CurrentRow == null)
            return;
        grid.EndEdit();
        int current = grid.CurrentRow.Index;
        int target = current + direction;
        if (target < 0 || target >= grid.Rows.Count)
            return;
        int column = grid.CurrentCell?.ColumnIndex ?? 0;
        DataGridViewRow row = grid.Rows[current];
        grid.Rows.RemoveAt(current);
        grid.Rows.Insert(target, row);
        grid.CurrentCell = row.Cells[column];
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        string id = grid.CurrentRow?.Cells[2].Value?.ToString() ?? "";
        CardOverlayChoice? choice =
            choices.FirstOrDefault(choice => choice.Id == id);
        previewLabel.Text = choice?.Name ?? "(None)";
        currentPreview = null;
        if (choice != null && choice.Id.Length > 0)
        {
            if (!previews.TryGetValue(
                    choice.Id, out CardOverlayLoader.Preview? preview))
            {
                preview = CardOverlayLoader.LoadPreview(choice);
                if (preview != null)
                    previews.Add(choice.Id, preview);
            }
            currentPreview = preview;
        }
        previewTimer.Stop();
        previewFrame = currentPreview?.CurrentFrame ?? -1;
        if (currentPreview?.Animated == true)
        {
            previewTimer.Interval = currentPreview.NextFrameDelay;
            previewTimer.Start();
        }
        previewSurface.Invalidate();
        composition?.Render();
    }

    private void AdvancePreview()
    {
        if (currentPreview?.Animated != true)
        {
            previewTimer.Stop();
            return;
        }
        int frame = currentPreview.CurrentFrame;
        if (frame != previewFrame)
        {
            previewFrame = frame;
            if (composition == null)
                previewSurface.Invalidate();
            else
                composition.Render();
        }
        previewTimer.Interval = currentPreview.NextFrameDelay;
    }

    private void previewSurface_Paint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
        CardOverlayFrame? frame = currentPreview?.Frame();
        Image? card = Datastore.modelcards?
            .FirstOrDefault(item => item.image != null)?.image;
        if (frame == null)
        {
            TextRenderer.DrawText(
                e.Graphics, "Select an overlay",
                Font, previewSurface.ClientRectangle,
                previewLabel.ForeColor,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter);
            return;
        }

        float ratio = card == null
            ? frame.Value.Source.Width /
                (float)frame.Value.Source.Height
            : card.Width / (float)card.Height;
        Rectangle destination = Fit(
            previewSurface.ClientRectangle, ratio, 14);
        if (card != null)
            e.Graphics.DrawImage(card, destination);
        else
        {
            using LinearGradientBrush background = new(
                destination,
                Color.FromArgb(95, 30, 50),
                Color.FromArgb(25, 25, 30),
                LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(background, destination);
        }
        if (composition == null)
            e.Graphics.DrawImage(
                frame.Value.Image, destination,
                frame.Value.Source, GraphicsUnit.Pixel);
        e.Graphics.DrawRectangle(Pens.DimGray, destination);
    }

    private static Rectangle Fit(
        Rectangle bounds, float ratio, int margin)
    {
        bounds.Inflate(-margin, -margin);
        int width = Math.Min(
            bounds.Width,
            (int)Math.Round(bounds.Height * ratio));
        int height = Math.Min(
            bounds.Height,
            (int)Math.Round(width / ratio));
        return new Rectangle(
            bounds.Left + (bounds.Width - width) / 2,
            bounds.Top + (bounds.Height - height) / 2,
            Math.Max(1, width), Math.Max(1, height));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            if (!TryReadRules(out CardOverlayRules rules))
            {
                e.Cancel = true;
                return;
            }
            Rules = rules;
        }
        base.OnFormClosing(e);
    }

    private bool TryReadRules(out CardOverlayRules rules)
    {
        rules = new();
        foreach (DataGridViewRow row in grid.Rows)
        {
            string key = row.Tag?.ToString() ?? "";
            string overlayId = row.Cells[2].Value?.ToString() ?? "";
            rules.Priority.Add(key);
            if (key.StartsWith("type:"))
            {
                if (overlayId.Length > 0)
                    rules.CardTypes[key[5..]] = overlayId;
            }
            else if (key.StartsWith("hotness:"))
            {
                if (overlayId.Length > 0)
                    rules.Hotness[key[8..]] = overlayId;
            }
            else if (key == "recent")
            {
                if (!TryCount(row, out int count))
                    return Invalid(
                        "Newest releases must be 0 or greater.");
                rules.RecentCount = count;
                rules.RecentOverlayId = overlayId;
            }
            else if (key == "purchase")
            {
                if (!TryCount(row, out int count))
                    return Invalid(
                        "Most recent purchases must be 0 or greater.");
                rules.RecentPurchaseCount = count;
                rules.RecentPurchaseOverlayId = overlayId;
            }
            else if (key == "rating")
            {
                if (!decimal.TryParse(
                        row.Cells[1].Value?.ToString(),
                        NumberStyles.Number, CultureInfo.CurrentCulture,
                        out decimal rating) || rating < 0 || rating > 5)
                    return Invalid(
                        "My Rating must be between 0 and 5.");
                rules.MinimumMyRating = rating;
                rules.RatingOverlayId = overlayId;
            }
        }
        return true;
    }

    private static bool TryCount(
        DataGridViewRow row, out int count) =>
        int.TryParse(
            row.Cells[1].Value?.ToString(),
            NumberStyles.Integer, CultureInfo.CurrentCulture,
            out count) && count >= 0;

    private bool Invalid(string message)
    {
        MessageBox.Show(this, message, Text,
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            composition?.ClearPreview();
            previewTimer.Stop();
            previewTimer.Dispose();
            foreach (CardOverlayLoader.Preview preview in previews.Values)
                preview.Dispose();
            previews.Clear();
        }
        base.Dispose(disposing);
    }
}
