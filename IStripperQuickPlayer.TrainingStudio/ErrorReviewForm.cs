using System.Text.Json;

namespace IStripperQuickPlayer.TrainingStudio;

internal sealed record ErrorReviewItem(string Kind, string SampleId, int ErrorPixels, string ImagePath,
    bool Hidden);
internal sealed record ErrorReviewChoice(string SampleId, bool CreateBurst);

internal sealed class ErrorReviewForm : Form
{
    readonly ListView errors = new() { Dock = DockStyle.Fill, View = View.Details,
        FullRowSelect = true, MultiSelect = false, HideSelection = false };
    readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(30, 30, 30) };
    readonly Button reopen = new() { Text = "Reopen sample", AutoSize = true, Enabled = false };
    readonly Button burst = new() { Text = "Reopen + create 5-frame burst", AutoSize = true, Enabled = false };
    readonly CheckBox showHidden = new() { Text = "Show hidden", AutoSize = true };
    readonly string reviewFolder;
    readonly IReadOnlyList<ErrorReviewItem> reviewItems;

    internal ErrorReviewChoice? Choice { get; private set; }

    internal ErrorReviewForm(string reviewFolder)
    {
        this.reviewFolder = reviewFolder;
        reviewItems = LoadItems(reviewFolder);
        Text = "Training error review"; Width = 1200; Height = 760;
        MinimumSize = new(850, 560); StartPosition = FormStartPosition.CenterParent;
        errors.Columns.Add("Error", 120); errors.Columns.Add("Pixels", 90);
        errors.Columns.Add("Sample", 290);
        PopulateErrors();
        Label explanation = new()
        {
            Dock = DockStyle.Top, Height = 48, Padding = new Padding(8),
            Text = "Red shows false-positive pixels; blue shows false-negative pixels. " +
                "Reopen an error to correct its label, or create a nearby burst for extra examples."
        };
        Panel left = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
        left.Controls.Add(errors); left.Controls.Add(explanation);
        Panel right = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
        right.Controls.Add(preview);
        SplitContainer split = new() { Dock = DockStyle.Fill, SplitterDistance = 520 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
        Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 52,
            Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange([cancel, burst, reopen, showHidden]);
        Controls.Add(split); Controls.Add(actions); CancelButton = cancel;

        errors.SelectedIndexChanged += (_, _) => SelectCurrent();
        errors.DoubleClick += (_, _) => AcceptSelection(false);
        reopen.Click += (_, _) => AcceptSelection(false);
        burst.Click += (_, _) => AcceptSelection(true);
        showHidden.CheckedChanged += (_, _) => PopulateErrors();
        split.SizeChanged += (_, _) => SizeReviewPanels(split);
        if (errors.Items.Count > 0) errors.Items[0].Selected = true;
    }

    static void SizeReviewPanels(SplitContainer split)
    {
        int available = split.ClientSize.Width - split.SplitterWidth;
        if (available < 700) return;
        split.SplitterDistance = Math.Clamp((int)Math.Round(available * .32), 360,
            Math.Max(360, available - 420));
    }

    void SelectCurrent()
    {
        preview.Image?.Dispose(); preview.Image = null;
        ErrorReviewItem? item = SelectedItem();
        reopen.Enabled = burst.Enabled = item != null;
        if (item != null && File.Exists(item.ImagePath))
            preview.Image = TrainingStudioForm.LoadUnlockedImage(item.ImagePath);
    }

    ErrorReviewItem? SelectedItem() => errors.SelectedItems.Count == 1
        ? errors.SelectedItems[0].Tag as ErrorReviewItem : null;

    void PopulateErrors()
    {
        preview.Image?.Dispose(); preview.Image = null;
        errors.BeginUpdate(); errors.Items.Clear();
        foreach (ErrorReviewItem item in reviewItems.Where(item => showHidden.Checked || !item.Hidden))
        {
            ListViewItem row = new([DisplayKind(item.Kind), item.ErrorPixels.ToString("N0"), item.SampleId])
                { Tag = item };
            errors.Items.Add(row);
        }
        errors.EndUpdate();
        reopen.Enabled = burst.Enabled = false;
        if (errors.Items.Count > 0) errors.Items[0].Selected = true;
    }

    void AcceptSelection(bool createBurst)
    {
        if (SelectedItem() is not ErrorReviewItem item) return;
        MarkHidden(reviewFolder, item.Kind, item.SampleId);
        Choice = new ErrorReviewChoice(item.SampleId, createBurst);
        DialogResult = DialogResult.OK; Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        preview.Image?.Dispose(); base.OnFormClosed(e);
    }

    static string DisplayKind(string value) => value switch
    {
        "false-positive" => "False positive",
        "false-negative" => "False negative",
        _ => value
    };

    internal static IReadOnlyList<ErrorReviewItem> LoadItems(string reviewFolder)
    {
        string root = Path.GetFullPath(reviewFolder);
        HashSet<string> hiddenItems = LoadHiddenItems(root);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "review.json")));
        List<ErrorReviewItem> result = [];
        foreach (string kind in new[] { "false-positive", "false-negative" })
        {
            if (!document.RootElement.TryGetProperty(kind, out JsonElement values)) continue;
            foreach (JsonElement value in values.EnumerateArray())
            {
                string sampleId = value.GetProperty("sampleId").GetString() ?? "";
                string relativeImage = value.GetProperty("image").GetString() ?? "";
                string image = Path.GetFullPath(Path.Combine(root, relativeImage));
                if (sampleId.Length == 0 || !image.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The error review contains an unsafe sample entry.");
                bool hidden = hiddenItems.Contains(HiddenKey(kind, sampleId)) ||
                    value.TryGetProperty("hidden", out JsonElement hiddenValue) &&
                    hiddenValue.ValueKind == JsonValueKind.True;
                result.Add(new ErrorReviewItem(kind, sampleId,
                    value.GetProperty("errorPixelsAt512").GetInt32(), image, hidden));
            }
        }
        return result;
    }

    internal static void MarkHidden(string reviewFolder, string kind, string sampleId)
    {
        string root = Path.GetFullPath(reviewFolder);
        string path = Path.Combine(root, "hidden.json");
        HashSet<string> hidden = LoadHiddenItems(root);
        foreach (ErrorReviewItem item in LoadItems(root).Where(item => item.SampleId == sampleId))
            hidden.Add(HiddenKey(item.Kind, item.SampleId));
        if (hidden.Count == 0) hidden.Add(HiddenKey(kind, sampleId));
        string temporary = path + ".saving-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { hidden = hidden.Order().ToArray() },
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    static HashSet<string> LoadHiddenItems(string reviewFolder)
    {
        string path = Path.Combine(reviewFolder, "hidden.json");
        if (!File.Exists(path)) return [];
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("hidden", out JsonElement values)) return [];
        return values.EnumerateArray().Select(value => value.GetString()).OfType<string>().ToHashSet();
    }

    static string HiddenKey(string kind, string sampleId) => kind + "|" + sampleId;

    internal static string? FindLatest(string datasetRoot)
    {
        string runs = Path.Combine(datasetRoot, "runs");
        if (!Directory.Exists(runs)) return null;
        return Directory.EnumerateDirectories(runs)
            .Select(run => Path.Combine(run, "package", "review"))
            .Where(review => File.Exists(Path.Combine(review, "review.json")))
            .OrderByDescending(review => File.GetLastWriteTimeUtc(Path.Combine(review, "review.json")))
            .FirstOrDefault();
    }
}
