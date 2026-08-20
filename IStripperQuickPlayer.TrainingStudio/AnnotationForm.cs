namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class AnnotationForm : Form
{
    static readonly Color[] Palette = [Color.LimeGreen, Color.DeepSkyBlue, Color.Orange,
        Color.Magenta, Color.Gold, Color.Cyan, Color.HotPink, Color.SpringGreen];
    readonly AnnotationCanvas canvas = new() { Dock = DockStyle.Fill };
    readonly ListBox objects = new() { Width = 180, Dock = DockStyle.Fill };
    readonly Label status = new() { AutoSize = true, Text = "Loading SAM2…" };
    readonly Button runSam = new() { Text = "SAM2 loading…", AutoSize = true, Enabled = false };
    readonly CheckBox paintMode = new() { Text = "Paint", AutoSize = true,
        Padding = new(0, 7, 0, 0) };
    readonly TrackBar brush = new() { Minimum = 2, Maximum = 200, Value = 32, Width = 140 };
    readonly Dictionary<int, List<PointF>> samPoints = [];
    readonly Dictionary<int, List<int>> samLabels = [];
    readonly Dictionary<int, RectangleF?> samBoxes = [];
    readonly List<AnnotationObject> annotationObjects = [];
    readonly Action<int[], IReadOnlyList<AnnotationObject>, string>? saveDraft;
    readonly System.Windows.Forms.Timer draftTimer = new() { Interval = 750 };
    readonly CancellationTokenSource formCancellation = new();
    readonly SemaphoreSlim samGate = new(1, 1);
    Task draftSaveTask = Task.CompletedTask;
    readonly string temporary;
    Sam2Client? sam2;
    Task samStartup = Task.CompletedTask;
    bool samReady;
    bool ownsSam2;
    int nextId = 1;
    int draftGeneration;
    bool accepting;
    readonly System.Windows.Forms.Timer rvmTimer = new() { Interval = 300 };
    readonly Func<double, Task<string>>? refreshRvm;
    readonly Func<string, CancellationToken,
        Task<(Sam2Client Client, string Description)>>? loadSam2;
    int rvmGeneration;
    bool rvmGenerated;

    internal int[] ObjectIds => canvas.CopyObjectIds();
    internal IReadOnlyList<AnnotationObject> AnnotationObjects => annotationObjects;
    internal string Provenance { get; private set; } = "manual";
    internal double RvmThreshold { get; private set; } = .4;

    internal AnnotationForm(string framePath, int[]? initialIds = null,
        IReadOnlyList<AnnotationObject>? initialObjects = null,
        string initialProvenance = "manual",
        Action<int[], IReadOnlyList<AnnotationObject>, string>? saveDraft = null,
        string? rvmMaskPath = null, Func<double, Task<string>>? refreshRvm = null,
        Func<string, CancellationToken,
            Task<(Sam2Client Client, string Description)>>? loadSam2 = null)
    {
        Text = "Label foreground props"; WindowState = FormWindowState.Maximized;
        MinimumSize = new(1000, 700); temporary = Path.Combine(Path.GetTempPath(),
            "iqp-training-annotation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
        canvas.LoadImage(framePath); canvas.LoadRvmMask(rvmMaskPath);
        Provenance = initialProvenance; this.saveDraft = saveDraft; this.refreshRvm = refreshRvm;
        this.loadSam2 = loadSam2;
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
        AddTool(tools, "Polygon", AnnotationTool.Polygon); AddTool(tools, "SAM box", AnnotationTool.SamBox);
        Button clearSam = new() { Text = "Clear SAM prompts", AutoSize = true };
        Button undo = new() { Text = "Undo", AutoSize = true };
        Button redo = new() { Text = "Redo", AutoSize = true };
        Button fit = new() { Text = "Fit", AutoSize = true };
        Button save = new() { Text = "Accept annotation", AutoSize = true };
        Button cancel = new() { Text = "Cancel", AutoSize = true };
        Label shortcuts = new() { Text = "Wheel zoom · Ctrl+wheel brush · Middle/Space+drag pan · Ctrl+Z/Y undo/redo",
            AutoSize = true, Padding = new(8, 9, 0, 0) };
        bool hasRvmOverlay = !string.IsNullOrWhiteSpace(rvmMaskPath) && File.Exists(rvmMaskPath);
        Button generateRvm = new() { Text = "Generate RVM", AutoSize = true,
            Enabled = refreshRvm != null };
        CheckBox rvmLegend = new() { Text = hasRvmOverlay ? "RVM pink overlay" : "RVM not generated",
            Checked = hasRvmOverlay, Enabled = hasRvmOverlay,
            ForeColor = hasRvmOverlay ? Color.DeepPink : Color.DarkOrange,
            AutoSize = true, Padding = new(8, 9, 0, 0) };
        Label thresholdLabel = new() { Text = "RVM threshold: 0.40", AutoSize = true,
            Padding = new(8, 9, 0, 0) };
        TrackBar threshold = new() { Minimum = 10, Maximum = 90, Value = 40,
            TickFrequency = 10, SmallChange = 5, LargeChange = 10, Width = 130 };
        tools.Controls.AddRange([paintMode,
            new Label { Text = "Brush", AutoSize = true, Padding = new(0, 9, 0, 0) },
            brush, runSam, clearSam, undo, redo, fit, save, cancel, shortcuts,
            generateRvm, rvmLegend, thresholdLabel, threshold, status]);

        FlowLayoutPanel objectButtons = new() { Dock = DockStyle.Bottom, Height = 42 };
        Button add = new() { Text = "Add object", AutoSize = true };
        Button remove = new() { Text = "Remove object", AutoSize = true };
        objectButtons.Controls.AddRange([add, remove]);
        Panel side = new() { Dock = DockStyle.Left, Width = 190 };
        side.Controls.Add(objects); side.Controls.Add(objectButtons);
        Controls.Add(canvas); Controls.Add(side); Controls.Add(tools);

        brush.ValueChanged += (_, _) => canvas.BrushSize = brush.Value;
        rvmLegend.CheckedChanged += (_, _) => canvas.ShowRvmOverlay = rvmLegend.Checked;
        generateRvm.Click += async (_, _) => await RefreshRvmAsync(rvmLegend, generateRvm);
        threshold.ValueChanged += (_, _) =>
        {
            RvmThreshold = threshold.Value / 100d;
            thresholdLabel.Text = $"RVM threshold: {RvmThreshold:0.00}";
            if (rvmGenerated) { rvmTimer.Stop(); rvmTimer.Start(); }
        };
        rvmTimer.Tick += async (_, _) =>
        {
            rvmTimer.Stop(); await RefreshRvmAsync(rvmLegend, generateRvm);
        };
        paintMode.CheckedChanged += (_, _) => SetPaintMode();
        canvas.MouseEnter += (_, _) => canvas.Cursor = paintMode.Checked
            ? Cursors.Default : Cursors.Cross;
        canvas.BrushSizeChanged += size =>
        {
            if (brush.Value != size) brush.Value = size;
        };
        objects.SelectedIndexChanged += (_, _) => SelectObject();
        add.Click += (_, _) => AddObject();
        remove.Click += (_, _) => RemoveObject();
        undo.Click += (_, _) => canvas.Undo(); redo.Click += (_, _) => canvas.Redo();
        fit.Click += (_, _) => canvas.Fit();
        runSam.Click += async (_, _) => await RunSamAsync();
        clearSam.Click += (_, _) => ClearSamPrompts();
        save.Click += (_, _) =>
        {
            if (!ObjectIds.Any(value => value > 0))
            {
                MessageBox.Show(this, "Add at least one visible prop pixel, or use No foreground objects in the review window.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return;
            }
            accepting = true; Interlocked.Increment(ref draftGeneration);
            DialogResult = DialogResult.OK; Close();
        };
        cancel.Click += (_, _) => Close();
        canvas.SamPoint += (point, positive) =>
        {
            int id = canvas.CurrentObjectId, index = samPoints[id].Count;
            int label = positive ? 1 : 0;
            samPoints[id].Add(point); samLabels[id].Add(label); ShowSamPrompts(id);
            canvas.RecordExternalChange(
                () => RemoveSamPoint(id, index),
                () => InsertSamPoint(id, index, point, label));
            status.Text = samReady
                ? $"Object {id}: {(positive ? "positive" : "negative")} point added; double-click to apply SAM2 prompts"
                : $"Object {id}: point queued; SAM2 is still loading…";
        };
        canvas.SamApplyRequested += async () => await RunSamAsync();
        canvas.SamBoxReady += async box =>
        {
            int id = canvas.CurrentObjectId; samBoxes[id] = box; ShowSamPrompts(id);
            status.Text = "SAM2 box ready"; await RunSamAsync(id);
        };
        canvas.MaskChanged += ScheduleDraftSave;
        draftTimer.Tick += (_, _) => QueueDraftSave();
        SetPaintMode();
        Shown += async (_, _) =>
        {
            canvas.Fit();
            samStartup = StartSamAsync(framePath);
            await samStartup;
        };
    }

    async Task RefreshRvmAsync(CheckBox legend, Button generate)
    {
        if (refreshRvm == null) return;
        int generation = ++rvmGeneration; generate.Enabled = false;
        try
        {
            status.Text = rvmGenerated
                ? $"Updating cached RVM alpha at {RvmThreshold:0.00}…"
                : "Generating temporally warmed RVM preview…";
            string path = await refreshRvm(RvmThreshold);
            if (generation != rvmGeneration || IsDisposed) return;
            canvas.LoadRvmMask(path); rvmGenerated = true;
            legend.Enabled = legend.Checked = true; legend.Text = "RVM pink overlay";
            status.Text = $"RVM preview ready at threshold {RvmThreshold:0.00}.";
        }
        catch (Exception error) { if (!IsDisposed) status.Text = "RVM preview failed: " + error.Message; }
        finally { if (!IsDisposed) generate.Enabled = true; }
    }

    void RemoveSamPoint(int id, int index)
    {
        if (!samPoints.TryGetValue(id, out List<PointF>? points) ||
            index < 0 || index >= points.Count) return;
        points.RemoveAt(index); samLabels[id].RemoveAt(index);
        if (canvas.CurrentObjectId == id) ShowSamPrompts(id);
        status.Text = $"Object {id}: SAM point undone";
    }

    void InsertSamPoint(int id, int index, PointF point, int label)
    {
        if (!samPoints.TryGetValue(id, out List<PointF>? points)) return;
        index = Math.Clamp(index, 0, points.Count);
        points.Insert(index, point); samLabels[id].Insert(index, label);
        if (canvas.CurrentObjectId == id) ShowSamPrompts(id);
        status.Text = $"Object {id}: SAM point restored";
    }

    void AddTool(FlowLayoutPanel panel, string text, AnnotationTool tool)
    {
        Button button = new() { Text = text, AutoSize = true };
        button.Click += (_, _) =>
        {
            if (paintMode.Checked) paintMode.Checked = false;
            canvas.Tool = tool;
            status.Text = tool switch
            {
                AnnotationTool.SamPositive => "Click points inside the selected prop, then apply SAM2 prompts.",
                AnnotationTool.SamNegative => "Click background points to exclude, then apply SAM2 prompts.",
                AnnotationTool.SamBox => "Drag a box around the selected prop, then apply SAM2 prompts.",
                _ => tool.ToString()
            };
        };
        panel.Controls.Add(button);
    }

    void SetPaintMode()
    {
        bool paint = paintMode.Checked;
        canvas.Tool = paint ? AnnotationTool.Brush : AnnotationTool.SamPositive;
        canvas.BrushResizeEnabled = paint; brush.Enabled = paint;
        canvas.Cursor = paint ? Cursors.Default : Cursors.Cross;
        status.Text = paint
            ? "Paint mode: left-drag adds foreground; right-drag erases."
            : "Point mode: left/right-click adds positive/negative points; double-click applies SAM2 prompts.";
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
        if (objects.SelectedIndex >= 0)
        {
            canvas.CurrentObjectId = annotationObjects[objects.SelectedIndex].Id;
            ShowSamPrompts(canvas.CurrentObjectId);
        }
    }

    void ShowSamPrompts(int id) => canvas.SetSamPrompts(samPoints[id], samLabels[id], samBoxes[id]);

    void ClearSamPrompts()
    {
        int id = canvas.CurrentObjectId;
        samPoints[id].Clear(); samLabels[id].Clear(); samBoxes[id] = null;
        ShowSamPrompts(id); status.Text = $"Object {id}: SAM2 prompts cleared";
    }

    void RefreshColors()
    {
        canvas.ObjectColors = annotationObjects.ToDictionary(value => value.Id,
            value => Color.FromArgb(value.ColorArgb)); canvas.Invalidate();
    }

    async Task StartSamAsync(string framePath)
    {
        samReady = false; runSam.Enabled = false; runSam.Text = "SAM2 loading…";
        try
        {
            string ready;
            if (loadSam2 != null)
                (sam2, ready) = await loadSam2(framePath, formCancellation.Token);
            else
            {
                ownsSam2 = true; sam2 = new Sam2Client(framePath, temporary);
                ready = await sam2.StartAsync(formCancellation.Token);
            }
            samReady = true;
            runSam.Enabled = true; runSam.Text = "Apply SAM2 prompts";
            status.Text = ready + ". Add +/− points or a box, then apply SAM2 prompts.";
        }
        catch (OperationCanceledException) when (formCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            runSam.Text = "SAM2 unavailable";
            status.Text = "Manual tools ready; SAM2 unavailable: " + error.Message;
        }
    }

    async Task RunSamAsync(int? requestedObjectId = null)
    {
        if (sam2 == null) { status.Text = "SAM2 is unavailable; use the manual tools."; return; }
        int id = requestedObjectId ?? canvas.CurrentObjectId;
        if (!samPoints.ContainsKey(id)) return;
        if (samPoints[id].Count == 0 && samBoxes[id] == null && !canvas.ObjectHasPixels(id))
        {
            status.Text = "Add a positive/negative point, box, or manual seed mask first."; return;
        }
        if (!samStartup.IsCompleted)
        {
            status.Text = "Finishing SAM2 initialization, then applying prompts…";
            await samStartup;
        }
        if (!samReady)
        {
            status.Text = "SAM2 is unavailable; use the manual tools."; return;
        }
        try { await samGate.WaitAsync(formCancellation.Token); }
        catch (OperationCanceledException) when (formCancellation.IsCancellationRequested) { return; }
        try
        {
            UseWaitCursor = true; status.Text = "Running SAM2…";
            if (!samPoints.ContainsKey(id)) return;
            string seed = Path.Combine(temporary, $"seed-{id}.png"); canvas.ExportObjectMask(seed, id);
            await sam2.GenerateAsync(samPoints[id], samLabels[id], samBoxes[id], seed,
                CancellationToken.None);
            if (formCancellation.IsCancellationRequested) return;
            canvas.ApplyObjectMask(sam2.MaskPath, id); Provenance = "sam2-assisted";
            status.Text = "SAM2 mask applied; enable Paint to refine it, or use Polygon.";
        }
        catch (OperationCanceledException) when (formCancellation.IsCancellationRequested) { }
        catch (Exception error) { status.Text = "SAM2 failed: " + error.Message; }
        finally
        {
            samGate.Release(); if (!IsDisposed) UseWaitCursor = false;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (samGate.CurrentCount == 0)
        {
            e.Cancel = true; status.Text = "Finishing the active SAM2 prediction before closing…";
            return;
        }
        formCancellation.Cancel(); base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.P))
        {
            paintMode.Checked = !paintMode.Checked; return true;
        }
        if (keyData == (Keys.Control | Keys.Z))
        {
            canvas.Undo(); return true;
        }
        if (keyData == (Keys.Control | Keys.Y) ||
            keyData == (Keys.Control | Keys.Shift | Keys.Z))
        {
            canvas.Redo(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        draftTimer.Stop();
        rvmTimer.Stop(); rvmTimer.Dispose();
        if (!accepting) QueueDraftSave();
        draftTimer.Dispose(); formCancellation.Dispose();
        Sam2Client? client = sam2; string cleanup = temporary;
        _ = Task.Run(async () =>
        {
            if (ownsSam2 && client != null) await client.DisposeAsync();
            try { Directory.Delete(cleanup, true); } catch { }
        });
        base.OnFormClosed(e);
    }

    void ScheduleDraftSave()
    {
        if (saveDraft == null) return;
        draftTimer.Stop(); draftTimer.Start();
    }

    void QueueDraftSave()
    {
        draftTimer.Stop();
        if (saveDraft == null || accepting) return;
        int[] ids = ObjectIds;
        AnnotationObject[] objects = annotationObjects.Select(value => new AnnotationObject
        {
            Id = value.Id, Name = value.Name, ColorArgb = value.ColorArgb
        }).ToArray();
        string provenance = Provenance;
        int generation = Interlocked.Increment(ref draftGeneration);
        draftSaveTask = draftSaveTask.ContinueWith(_ =>
        {
            if (generation != Volatile.Read(ref draftGeneration) || accepting) return;
            saveDraft(ids, objects, provenance);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _ = draftSaveTask.ContinueWith(task =>
        {
            if (task.Exception == null || IsDisposed || Disposing) return;
            try
            {
                BeginInvoke(() => status.Text = "Draft save failed: " +
                    task.Exception.GetBaseException().Message);
            }
            catch (InvalidOperationException) { }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
