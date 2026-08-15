using System.Diagnostics;

namespace IStripperQuickPlayer;

internal sealed class CustomShowCheckForm : Form
{
    readonly CustomShowStore store;
    readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        MultiSelect = true,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    readonly Label summary = new() { AutoSize = true, Dock = DockStyle.Fill };
    readonly Button fix = new() { Text = "Fix Selected...", AutoSize = true };
    readonly Button delete = new() { Text = "Delete Selected", AutoSize = true };
    readonly int validShows;
    internal bool DeletedAny { get; private set; }
    internal string? RepairShowId { get; private set; }

    internal CustomShowCheckForm(CustomShowStore store,
        CustomShowCheckReport report)
    {
        this.store = store;
        validShows = report.ValidShows;
        Text = "Custom Show Check Results";
        ClientSize = new Size(1000, 500);
        MinimumSize = new Size(720, 360);
        StartPosition = FormStartPosition.CenterParent;
        grid.Columns.AddRange(Column("Show", 220), Column("Problem", 500),
            Column("Folder", 230));

        Label explanation = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 0, 8),
            Text = "These shows failed manifest or foreground/alpha media checks. " +
                "Repairable shows can be opened for reprocessing; malformed shows " +
                "can be opened in Explorer for manual repair."
        };
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 4,
            ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(explanation, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(summary, 0, 2);

        FlowLayoutPanel buttons = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 0)
        };
        Button close = new()
        {
            Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel
        };
        buttons.Controls.AddRange([fix, delete, close]);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        CancelButton = close;

        foreach (CustomShowCheckIssue issue in report.Issues)
        {
            int index = grid.Rows.Add(issue.Show, issue.Error, issue.Folder);
            grid.Rows[index].Tag = issue;
            grid.Rows[index].Cells[1].ToolTipText = issue.Error;
        }
        if (grid.Rows.Count > 0) grid.Rows[0].Selected = true;
        grid.SelectionChanged += (_, _) => RefreshButtons();
        grid.CellDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0) FixSelected();
        };
        fix.Click += (_, _) => FixSelected();
        delete.Click += (_, _) => DeleteSelected();
        RefreshSummary();
        RefreshButtons();
        AppTheme.Apply(this);
    }

    static DataGridViewTextBoxColumn Column(string title, int width) => new()
    {
        HeaderText = title,
        Width = width,
        MinimumWidth = 60,
        AutoSizeMode = title == "Problem"
            ? DataGridViewAutoSizeColumnMode.Fill
            : DataGridViewAutoSizeColumnMode.None
    };

    CustomShowCheckIssue[] SelectedIssues() => grid.SelectedRows
        .Cast<DataGridViewRow>()
        .Select(row => row.Tag)
        .OfType<CustomShowCheckIssue>()
        .ToArray();

    void RefreshButtons()
    {
        CustomShowCheckIssue[] selected = SelectedIssues();
        delete.Enabled = selected.Length > 0;
        fix.Enabled = selected.Length == 1;
        fix.Text = selected.Length == 1 && selected[0].CanRepair
            ? "Repair / Reprocess..." : "Open Folder to Fix";
    }

    void FixSelected()
    {
        if (SelectedIssues() is not [CustomShowCheckIssue issue]) return;
        if (issue.CanRepair && issue.ShowId != null)
        {
            RepairShowId = issue.ShowId;
            DialogResult = DialogResult.Retry;
            Close();
            return;
        }
        if (Directory.Exists(issue.Folder))
            Process.Start(new ProcessStartInfo(issue.Folder)
                { UseShellExecute = true });
    }

    void DeleteSelected()
    {
        CustomShowCheckIssue[] selected = SelectedIssues();
        if (selected.Length == 0) return;
        string noun = selected.Length == 1 ? "show" : "shows";
        if (MessageBox.Show(this,
                $"Move {selected.Length:N0} selected custom {noun} to the Recycle Bin?",
                "Delete Bad Custom Shows", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        List<string> failures = [];
        foreach (CustomShowCheckIssue issue in selected)
        {
            try
            {
                store.DeleteCheckedShowFolder(issue.Folder);
                DataGridViewRow? row = grid.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(value => ReferenceEquals(value.Tag, issue));
                if (row != null) grid.Rows.Remove(row);
                DeletedAny = true;
            }
            catch (Exception error)
            {
                failures.Add(issue.Show + ": " + error.Message);
            }
        }
        RefreshSummary();
        RefreshButtons();
        if (failures.Count > 0)
            MessageBox.Show(this,
                "Some shows could not be deleted:\n\n" + string.Join("\n", failures),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    void RefreshSummary() => summary.Text =
        $"{validShows:N0} passed • {grid.Rows.Count:N0} failed";
}
