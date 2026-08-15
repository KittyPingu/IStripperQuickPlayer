using System.Text.Json;

namespace IStripperQuickPlayer;

internal sealed record CustomShowCleanupEntry(
    string Key,
    string Show,
    string Status,
    string Location,
    string? QueueJobId,
    bool CanDelete,
    string[] Paths,
    int FileCount,
    long SizeBytes,
    DateTime LastWriteUtc);

internal static class CustomShowFailedCleanup
{
    internal static IReadOnlyList<CustomShowCleanupEntry> Find(
        CustomShowStore store, IReadOnlyList<CustomShowQueueJob> jobs)
    {
        string queueRoot = Path.Combine(store.Root, "queue");
        string jobsRoot = Path.Combine(queueRoot, "jobs");
        string workRoot = Path.Combine(queueRoot, "work");
        string stagingRoot = Path.Combine(store.Root, ".staging");
        Dictionary<string, string> jobFolders = DirectoriesByName(jobsRoot);
        Dictionary<string, string> workFolders = DirectoriesByName(workRoot);
        Dictionary<string, string> stagingFolders = DirectoriesByName(stagingRoot);
        HashSet<string> knownJobIds = jobs.Select(job => job.Id).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);
        List<CustomShowCleanupEntry> result = [];

        IEnumerable<CustomShowQueueJob> relevant = jobs.Where(job =>
                job.Status is CustomShowQueueStatus.Pending or
                    CustomShowQueueStatus.Running or
                    CustomShowQueueStatus.NeedsAttention)
            .Concat(jobs.Where(job => job.Status == CustomShowQueueStatus.Failed));
        foreach (CustomShowQueueJob job in relevant)
        {
            List<string> paths = [];
            Add(jobFolders, job.Id, paths, claimed);
            Add(workFolders, job.Id, paths, claimed);
            foreach (string showId in ShowIds(job))
                Add(stagingFolders, showId, paths, claimed);
            if (paths.Count == 0) continue;

            bool canDelete = job.Status == CustomShowQueueStatus.Failed;
            string status = job.Status switch
            {
                CustomShowQueueStatus.Pending => "Queued — protected",
                CustomShowQueueStatus.Running => "Running — protected",
                CustomShowQueueStatus.NeedsAttention =>
                    "Needs attention — protected",
                _ => "Failed"
            };
            result.Add(Create("queue:" + job.Id,
                DisplayName(job.Manifest.Title, job.Id), status,
                Location(paths, queueRoot, stagingRoot), job.Id, canDelete,
                paths));
        }

        foreach (string id in jobFolders.Keys.Concat(workFolders.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (knownJobIds.Contains(id)) continue;
            List<string> paths = [];
            Add(jobFolders, id, paths, claimed);
            Add(workFolders, id, paths, claimed);
            if (paths.Count == 0) continue;
            string? title = paths.Select(ReadShowTitle)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            result.Add(Create("orphan-queue:" + id,
                DisplayName(title, id), "Orphaned queue files", "Queue files",
                null, true, paths));
        }

        foreach ((string id, string folder) in stagingFolders)
        {
            if (claimed.Contains(folder)) continue;
            result.Add(Create("staging:" + id,
                DisplayName(ReadShowTitle(folder), id),
                "Failed/incomplete — not queued", "Staged output", null, true,
                [folder]));
        }

        return result.OrderByDescending(entry => entry.CanDelete)
            .ThenBy(entry => entry.Status, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Show, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    internal static bool Delete(CustomShowStore store,
        CustomShowQueueManager manager, CustomShowCleanupEntry entry,
        out string? error)
    {
        error = null;
        if (!entry.CanDelete)
        {
            error = "This item is still queued and is protected.";
            return false;
        }
        if (entry.QueueJobId != null && !manager.DeleteFailed(entry.QueueJobId))
        {
            error = "Its queue status changed, so it was not deleted.";
            return false;
        }
        foreach (string path in entry.Paths)
        {
            if (!SafeCleanupPath(store.Root, path))
            {
                error = "A cleanup path was outside the permitted folders.";
                return false;
            }
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (Exception failure) when (failure is IOException or
                UnauthorizedAccessException)
            {
                error = failure.Message;
                return false;
            }
        }
        return true;
    }

    static IEnumerable<string> ShowIds(CustomShowQueueJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.Manifest.Id)) yield return job.Manifest.Id;
        if (!string.IsNullOrWhiteSpace(job.TargetShowId) &&
            !string.Equals(job.TargetShowId, job.Manifest.Id,
                StringComparison.OrdinalIgnoreCase))
            yield return job.TargetShowId;
    }

    static Dictionary<string, string> DirectoriesByName(string root)
    {
        if (!Directory.Exists(root))
            return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return Directory.EnumerateDirectories(root).ToDictionary(
                value => new DirectoryInfo(value).Name, value => value,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (UnauthorizedAccessException)
        { return new(StringComparer.OrdinalIgnoreCase); }
    }

    static void Add(IReadOnlyDictionary<string, string> folders, string id,
        ICollection<string> paths, ISet<string> claimed)
    {
        if (!folders.TryGetValue(id, out string? path) || !claimed.Add(path)) return;
        paths.Add(path);
    }

    static CustomShowCleanupEntry Create(string key, string show, string status,
        string location, string? queueJobId, bool canDelete,
        IReadOnlyCollection<string> paths)
    {
        int files = 0;
        long bytes = 0;
        DateTime modified = DateTime.MinValue;
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (string path in paths)
        {
            try
            {
                DateTime folderWrite = Directory.GetLastWriteTimeUtc(path);
                if (folderWrite > modified) modified = folderWrite;
                foreach (string file in Directory.EnumerateFiles(path, "*", options))
                {
                    try
                    {
                        FileInfo info = new(file);
                        files++;
                        bytes = bytes > long.MaxValue - info.Length
                            ? long.MaxValue : bytes + info.Length;
                        if (info.LastWriteTimeUtc > modified)
                            modified = info.LastWriteTimeUtc;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new(key, show, status, location, queueJobId, canDelete,
            paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), files,
            bytes, modified);
    }

    static string Location(IReadOnlyCollection<string> paths,
        string queueRoot, string stagingRoot)
    {
        bool queue = paths.Any(path => IsBelow(queueRoot, path));
        bool staging = paths.Any(path => IsBelow(stagingRoot, path));
        return queue && staging ? "Queue + staged output" :
            queue ? "Queue files" : "Staged output";
    }

    static string? ReadShowTitle(string folder)
    {
        string manifest = Path.Combine(folder, "show.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.TryGetProperty("title", out JsonElement title)
                ? title.GetString() : null;
        }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    static string DisplayName(string? title, string id) =>
        string.IsNullOrWhiteSpace(title)
            ? "Unknown show (" + ShortId(id) + ")" : title.Trim();

    static string ShortId(string id) => id.Length <= 10 ? id : id[..10] + "…";

    static bool SafeCleanupPath(string root, string path)
    {
        string[] permitted =
        [
            Path.Combine(root, ".staging"),
            Path.Combine(root, "queue", "jobs"),
            Path.Combine(root, "queue", "work")
        ];
        return permitted.Any(folder => IsBelow(folder, path));
    }

    static bool IsBelow(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool VerifyProtection()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "iqp-failed-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            CustomShowStore store = new(root);
            CustomShowQueueJob failed = new()
            {
                Id = "failed-job", Status = CustomShowQueueStatus.Failed,
                Manifest = new() { Id = "failed-show", Title = "Failed Show" }
            };
            CustomShowQueueJob pending = new()
            {
                Id = "pending-job", Status = CustomShowQueueStatus.Pending,
                Manifest = new() { Id = "pending-show", Title = "Queued Show" }
            };
            CustomShowQueueJob completed = new()
            {
                Id = "completed-job", Status = CustomShowQueueStatus.Completed,
                Manifest = new() { Id = "completed-show", Title = "Done" }
            };
            foreach (string path in new[]
            {
                Path.Combine(root, "queue", "jobs", failed.Id),
                Path.Combine(root, "queue", "jobs", pending.Id),
                Path.Combine(root, ".staging", failed.Manifest.Id),
                Path.Combine(root, ".staging", pending.Manifest.Id),
                Path.Combine(root, ".staging", "orphan-show")
            })
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "leftover.tmp"), "test");
            }
            IReadOnlyList<CustomShowCleanupEntry> entries = Find(store,
                [failed, pending, completed]);
            CustomShowCleanupEntry failedEntry = entries.Single(entry =>
                entry.Key == "queue:" + failed.Id);
            CustomShowCleanupEntry pendingEntry = entries.Single(entry =>
                entry.Key == "queue:" + pending.Id);
            return failedEntry.CanDelete && failedEntry.Paths.Length == 2 &&
                !pendingEntry.CanDelete && pendingEntry.Paths.Length == 2 &&
                entries.Single(entry => entry.Key == "staging:orphan-show").CanDelete &&
                entries.All(entry => entry.QueueJobId != completed.Id);
        }
        finally { CustomShowQueueStore.TryDelete(root); }
    }
}

internal sealed class CustomShowFailedCleanupForm : Form
{
    readonly CustomShowStore store;
    readonly CustomShowQueueManager manager;
    readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        MultiSelect = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    readonly Label summary = new()
    {
        AutoSize = true,
        Dock = DockStyle.Fill,
        Text = "Scanning for failed-show files…"
    };
    readonly Button selectAll = new() { Text = "Select All Cleanable", AutoSize = true };
    readonly Button clear = new() { Text = "Clear Selection", AutoSize = true };
    readonly Button delete = new()
    {
        Text = "Delete Selected", AutoSize = true, Enabled = false
    };
    bool loading;

    internal CustomShowFailedCleanupForm(CustomShowStore store,
        CustomShowQueueManager manager)
    {
        this.store = store;
        this.manager = manager;
        Text = "Clean Up Failed Custom Shows";
        ClientSize = new Size(1000, 520);
        MinimumSize = new Size(760, 380);
        StartPosition = FormStartPosition.CenterParent;

        grid.Columns.AddRange(
            new DataGridViewCheckBoxColumn
            {
                HeaderText = "Delete", Width = 55, SortMode =
                    DataGridViewColumnSortMode.NotSortable
            },
            Column("Show", 230), Column("Status", 180),
            Column("Location", 150), Column("Files", 70),
            Column("Size", 90), Column("Last modified", 135));

        Label explanation = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "Select failed or orphaned files to permanently delete. " +
                "Queued, running, and recoverable jobs are shown but protected.",
            Padding = new Padding(0, 0, 0, 8)
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
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        };
        Button close = new()
        {
            Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel
        };
        buttons.Controls.AddRange([selectAll, clear, delete, close]);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        CancelButton = close;

        Shown += async (_, _) => await ReloadAsync();
        selectAll.Click += (_, _) => SetChecks(true);
        clear.Click += (_, _) => SetChecks(false);
        delete.Click += async (_, _) => await DeleteSelectedAsync();
        grid.CellBeginEdit += (_, eventArgs) =>
        {
            if (eventArgs.ColumnIndex != 0 || eventArgs.RowIndex < 0 ||
                grid.Rows[eventArgs.RowIndex].Tag is not CustomShowCleanupEntry entry ||
                !entry.CanDelete)
                eventArgs.Cancel = true;
        };
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, eventArgs) =>
        {
            if (eventArgs.ColumnIndex == 0) RefreshDeleteButton();
        };
        AppTheme.Apply(this);
    }

    static DataGridViewTextBoxColumn Column(string title, int width) => new()
    {
        HeaderText = title,
        Width = width,
        ReadOnly = true,
        SortMode = DataGridViewColumnSortMode.NotSortable
    };

    async Task ReloadAsync()
    {
        if (loading) return;
        loading = true;
        SetBusy(true);
        try
        {
            CustomShowQueueJob[] jobs = manager.Jobs.ToArray();
            IReadOnlyList<CustomShowCleanupEntry> entries = await Task.Run(() =>
                CustomShowFailedCleanup.Find(store, jobs));
            if (IsDisposed) return;
            grid.Rows.Clear();
            foreach (CustomShowCleanupEntry entry in entries)
            {
                int index = grid.Rows.Add(false, entry.Show, entry.Status,
                    entry.Location, entry.FileCount.ToString("N0"),
                    FormatSize(entry.SizeBytes),
                    entry.LastWriteUtc == DateTime.MinValue ? "" :
                        entry.LastWriteUtc.ToLocalTime().ToString("g"));
                DataGridViewRow row = grid.Rows[index];
                row.Tag = entry;
                row.Cells[0].ReadOnly = !entry.CanDelete;
                if (!entry.CanDelete)
                {
                    row.Cells[0].Style.NullValue = false;
                    row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                }
                row.Cells[1].ToolTipText = string.Join(Environment.NewLine,
                    entry.Paths);
            }
            int cleanable = entries.Count(entry => entry.CanDelete);
            int protectedCount = entries.Count - cleanable;
            long bytes = entries.Where(entry => entry.CanDelete)
                .Aggregate(0L, (total, entry) => total > long.MaxValue - entry.SizeBytes
                    ? long.MaxValue : total + entry.SizeBytes);
            summary.Text = entries.Count == 0
                ? "No failed-show leftovers or protected queued files were found."
                : $"{cleanable:N0} cleanable • {protectedCount:N0} queued/protected • " +
                    $"{FormatSize(bytes)} can be recovered";
        }
        catch (Exception error)
        {
            summary.Text = "Could not scan failed-show files: " + error.Message;
        }
        finally
        {
            loading = false;
            SetBusy(false);
            RefreshDeleteButton();
        }
    }

    void SetChecks(bool value)
    {
        foreach (DataGridViewRow row in grid.Rows)
            if (row.Tag is CustomShowCleanupEntry { CanDelete: true })
                row.Cells[0].Value = value;
        RefreshDeleteButton();
    }

    void RefreshDeleteButton() => delete.Enabled = !loading && grid.Rows
        .Cast<DataGridViewRow>().Any(row =>
            row.Tag is CustomShowCleanupEntry { CanDelete: true } &&
            row.Cells[0].Value is true);

    async Task DeleteSelectedAsync()
    {
        grid.EndEdit();
        CustomShowCleanupEntry[] selected = grid.Rows.Cast<DataGridViewRow>()
            .Where(row => row.Cells[0].Value is true)
            .Select(row => row.Tag)
            .OfType<CustomShowCleanupEntry>()
            .Where(entry => entry.CanDelete).ToArray();
        if (selected.Length == 0) return;
        long bytes = selected.Aggregate(0L,
            (total, entry) => total > long.MaxValue - entry.SizeBytes
                ? long.MaxValue : total + entry.SizeBytes);
        string noun = selected.Length == 1 ? "item" : "items";
        if (MessageBox.Show(this,
                $"Permanently delete {selected.Length:N0} selected {noun} " +
                $"({FormatSize(bytes)})?\n\nFailed queue entries and their processing " +
                "logs will also be removed. This cannot be undone.",
                "Delete Failed Show Files", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        SetBusy(true);
        string[] failures = await Task.Run(() => selected.Select(entry =>
        {
            bool deleted = CustomShowFailedCleanup.Delete(store, manager,
                entry, out string? error);
            return deleted ? null : entry.Show + ": " + error;
        }).OfType<string>().ToArray());
        await ReloadAsync();
        if (failures.Length > 0)
            MessageBox.Show(this,
                "Some items could not be deleted:\n\n" + string.Join("\n", failures),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    void SetBusy(bool busy)
    {
        grid.Enabled = !busy;
        selectAll.Enabled = !busy;
        clear.Enabled = !busy;
        delete.Enabled = !busy && delete.Enabled;
        UseWaitCursor = busy;
    }

    static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:N0} {units[unit]}" :
            $"{value:N1} {units[unit]}";
    }
}
