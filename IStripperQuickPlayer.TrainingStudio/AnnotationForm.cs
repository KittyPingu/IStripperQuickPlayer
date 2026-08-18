namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class AnnotationForm : Form
{
    static readonly Color[] Palette = [Color.LimeGreen, Color.DeepSkyBlue, Color.Orange,
        Color.Magenta, Color.Gold, Color.Cyan, Color.HotPink, Color.SpringGreen];
    readonly AnnotationCanvas canvas = new() { Dock = DockStyle.Fill };
    readonly ListBox objects = new() { Width = 180, Dock = DockStyle.Fill };
    readonly Label status = new() { AutoSize = true, Text = "Loading SAM2…" };
    readonly TrackBar brush = new() { Minimum = 2, Maximum = 200, Value = 32, Width = 140 };
    readonly Dictionary<int, List<PointF>> samPoints = [];
    readonly Dictionary<int, List<int>> samLabels = [];
    readonly Dictionary<int, RectangleF?> samBoxes = [];
    readonly List<AnnotationObject> annotationObjects = [];
    readonly Action<int[], IReadOnlyList<AnnotationObject>, string>? saveDraft;
    readonly System.Windows.Forms.Timer draftTimer = new() { Interval = 750 };
    readonly string temporary;
    Sam2Client? sam2;
    int nextId = 1;

    internal int[] ObjectIds => canvas.CopyObjectIds();
    internal IReadOnlyList<AnnotationObject> AnnotationObjects => annotationObjects;
    internal string Provenance { get; private set; } = "manual";

    internal AnnotationForm(string framePath, int[]? initialIds = null,
        IReadOnlyList<AnnotationObject>? initialObjects = null,
        string initialProvenance = "manual",
        Action<int[], IReadOnlyList<AnnotationObject>, string>? saveDraft = null)
    {
        Text = "Label foreground props"; WindowState = FormWindowState.Maximized;
        MinimumSize = new(1000, 700); temporary = Path.Combine(Path.GetTempPath(),
            "iqp-training-annotation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
        canvas.LoadImage(framePath); Provenance = initialProvenance; this.saveDraft = saveDraft;
        if (initialIds != null && initialObjects is { Count: > 0 })
        {
            foreach (AnnotationObject value in initialObjects)
            {
                annotationObjects.Add(value); samPoints[value.Id] = [];
                samLabels[value.Id] = []; samBoxes[value.Id] = null;
                objects.Items.Add(value.Name); nextId = Math.Max(nextId, value.Id + 1);
            }
            RefreshColors(); canvas.LoadObjectIds(initialIds); objects.SelectedIndex = 0;
        }
        else AddObject();

        FlowLayoutPanel tools = new() { Dock = DockStyle.Top, Height = 42, AutoScroll = true };
        AddTool(tools, "Brush", AnnotationTool.Brush); AddTool(tools, "Erase", AnnotationTool.Eraser);
        AddTool(tools, "Polygon", AnnotationTool.Polygon); AddTool(tools, "SAM +", AnnotationTool.SamPositive);
        AddTool(tools, "SAM −", AnnotationTool.SamNegative); AddTool(tools, "SAM box", AnnotationTool.SamBox);
        Button runSam = new() { Text = "Run SAM2", AutoSize = true };
        Button undo = new() { Text = "Undo", AutoSize = true };
        Button redo = new() { Text = "Redo", AutoSize = true };
        Button fit = new() { Text = "Fit", AutoSize = true };
        Button save = new() { Text = "Accept annotation", AutoSize = true };
        Button cancel = new() { Text = "Cancel", AutoSize = true };
        tools.Controls.AddRange([new Label { Text = "Brush", AutoSize = true, Padding = new(0, 9, 0, 0) },
            brush, runSam, undo, redo, fit, save, cancel, status]);

        FlowLayoutPanel objectButtons = new() { Dock = DockStyle.Bottom, Height = 42 };
        Button add = new() { Text = "Add object", AutoSize = true };
        Button remove = new() { Text = "Remove object", AutoSize = true };
        objectButtons.Controls.AddRange([add, remove]);
        Panel side = new() { Dock = DockStyle.Left, Width = 190 };
        side.Controls.Add(objects); side.Controls.Add(objectButtons);
        Controls.Add(canvas); Controls.Add(side); Controls.Add(tools);

        brush.ValueChanged += (_, _) => canvas.BrushSize = brush.Value;
        objects.SelectedIndexChanged += (_, _) => SelectObject();
        add.Click += (_, _) => AddObject();
        remove.Click += (_, _) => RemoveObject();
        undo.Click += (_, _) => canvas.Undo(); redo.Click += (_, _) => canvas.Redo();
        fit.Click += (_, _) => canvas.Fit();
        runSam.Click += async (_, _) => await RunSamAsync();
        save.Click += (_, _) =>
        {
            if (!ObjectIds.Any(value => value > 0))
            {
                MessageBox.Show(this, "Add at least one visible prop pixel, or use No foreground objects in the review window.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return;
            }
            DialogResult = DialogResult.OK; Close();
        };
        cancel.Click += (_, _) => Close();
        canvas.SamPoint += (point, positive) =>
        {
            int id = canvas.CurrentObjectId; samPoints[id].Add(point); samLabels[id].Add(positive ? 1 : 0);
            status.Text = $"Object {id}: {samPoints[id].Count} SAM2 prompt(s)";
        };
        canvas.SamBoxReady += box => { samBoxes[canvas.CurrentObjectId] = box; status.Text = "SAM2 box ready"; };
        canvas.MaskChanged += ScheduleDraftSave;
        draftTimer.Tick += (_, _) => SaveDraft();
        Shown += async (_, _) => await StartSamAsync(framePath);
    }

    void AddTool(FlowLayoutPanel panel, string text, AnnotationTool tool)
    {
        Button button = new() { Text = text, AutoSize = true };
        button.Click += (_, _) => { canvas.Tool = tool; status.Text = tool.ToString(); };
        panel.Controls.Add(button);
    }

    void AddObject()
    {
        int id = nextId++; Color color = Palette[(id - 1) % Palette.Length];
        AnnotationObject item = new() { Id = id, Name = $"Prop {id}", ColorArgb = color.ToArgb() };
        annotationObjects.Add(item); samPoints[id] = []; samLabels[id] = []; samBoxes[id] = null;
        objects.Items.Add(item.Name); objects.SelectedIndex = objects.Items.Count - 1; RefreshColors();
    }

    void RemoveObject()
    {
        if (objects.SelectedIndex < 0 || annotationObjects.Count == 1) return;
        int index = objects.SelectedIndex, id = annotationObjects[index].Id;
        canvas.CurrentObjectId = id; canvas.ClearCurrentObject(); annotationObjects.RemoveAt(index);
        objects.Items.RemoveAt(index); samPoints.Remove(id); samLabels.Remove(id); samBoxes.Remove(id);
        objects.SelectedIndex = Math.Min(index, objects.Items.Count - 1); RefreshColors();
    }

    void SelectObject()
    {
        if (objects.SelectedIndex >= 0) canvas.CurrentObjectId = annotationObjects[objects.SelectedIndex].Id;
    }

    void RefreshColors()
    {
        canvas.ObjectColors = annotationObjects.ToDictionary(value => value.Id,
            value => Color.FromArgb(value.ColorArgb)); canvas.Invalidate();
    }

    async Task StartSamAsync(string framePath)
    {
        try
        {
            sam2 = new Sam2Client(framePath, temporary);
            status.Text = await sam2.StartAsync(CancellationToken.None);
        }
        catch (Exception error) { status.Text = "Manual tools ready; SAM2 unavailable: " + error.Message; }
    }

    async Task RunSamAsync()
    {
        if (sam2 == null) { status.Text = "SAM2 is unavailable; use the manual tools."; return; }
        int id = canvas.CurrentObjectId;
        try
        {
            UseWaitCursor = true; status.Text = "Running SAM2…";
            string seed = Path.Combine(temporary, $"seed-{id}.png"); canvas.ExportCurrentObjectMask(seed);
            await sam2.GenerateAsync(samPoints[id], samLabels[id], samBoxes[id], seed, CancellationToken.None);
            canvas.ApplyObjectMask(sam2.MaskPath); Provenance = "sam2-assisted";
            status.Text = "SAM2 mask applied; refine it with brush, eraser, or polygon.";
        }
        catch (Exception error) { status.Text = "SAM2 failed: " + error.Message; }
        finally { UseWaitCursor = false; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        draftTimer.Stop(); SaveDraft(); draftTimer.Dispose();
        if (sam2 != null) sam2.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(temporary, true); } catch { }
        base.OnFormClosed(e);
    }

    void ScheduleDraftSave()
    {
        if (saveDraft == null) return;
        draftTimer.Stop(); draftTimer.Start();
    }

    void SaveDraft()
    {
        draftTimer.Stop();
        if (saveDraft == null) return;
        try { saveDraft(ObjectIds, annotationObjects, Provenance); }
        catch (Exception error) { status.Text = "Draft save failed: " + error.Message; }
    }
}
