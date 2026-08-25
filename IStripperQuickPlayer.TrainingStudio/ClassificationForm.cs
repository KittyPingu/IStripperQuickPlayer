namespace IStripperQuickPlayer.TrainingStudio;

internal sealed class ClassificationForm : Form
{
    internal static readonly string[] Categories =
        ["dildo", "butt-plug", "strap-on", "choker", "necklace", "ears", "chains", "restraints", "other", "gloves", "mask", "diaper", "fluids", "chastity", "unclassified"];
    internal static readonly string[] States = ["held", "inserted"];
    static readonly Color[] CategoryColors =
    [
        Color.FromArgb(35, 210, 90), Color.FromArgb(35, 170, 245),
        Color.FromArgb(250, 145, 30), Color.FromArgb(215, 65, 225),
        Color.FromArgb(115, 90, 235), Color.FromArgb(245, 205, 35),
        Color.FromArgb(30, 210, 205), Color.FromArgb(235, 70, 85),
        Color.FromArgb(125, 215, 40), Color.FromArgb(40, 145, 105),
        Color.FromArgb(255, 85, 135), Color.FromArgb(185, 130, 55),
        Color.FromArgb(40, 190, 255), Color.FromArgb(240, 110, 35),
        Color.FromArgb(170, 120, 255)
    ];

    readonly DatasetStore store;
    readonly TrainingSample[] samples;
    readonly AnnotationCanvas canvas = new() { Dock = DockStyle.Fill };
    readonly List<ClassifiedObject> objects = [];
    readonly ListBox objectList = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed,
        ItemHeight = 22 };
    readonly CheckedListBox stateList = new() { CheckOnClick = true, Dock = DockStyle.Fill,
        IntegralHeight = false };
    readonly Dictionary<string, RadioButton> categoryButtons = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ClassifiedObject[]> reviewedObjects = new(StringComparer.Ordinal);
    readonly Label imageProgress = new() { AutoSize = true, Padding = new(8, 8, 0, 0) };
    readonly Label coverage = new() { AutoSize = true, Padding = new(8, 8, 0, 0) };
    readonly Label totals = new() { Dock = DockStyle.Fill, AutoSize = false };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 28, AutoEllipsis = true };
    readonly CheckBox continueObject = new() { Text = "Continue selected object", AutoSize = true,
        Padding = new(6, 7, 0, 0) };
    readonly TrackBar brush = new() { Minimum = 2, Maximum = 200, Value = 40, Width = 130 };
    int sampleIndex;
    int nextObjectId = 1;
    int lastClassificationObjectId;
    bool loading, dirty, reviewed;
    string selectedCategory = Categories[0];

    internal ClassificationForm(DatasetStore store)
    {
        this.store = store;
        samples = store.Dataset.Samples.Where(value => value.Decision == "positive" &&
                value.FramePath != null && value.PropMaskPath != null && value.Width > 0 && value.Height > 0)
            .OrderBy(value => value.PresentedUtc).ToArray();
        foreach (TrainingSample sample in samples.Where(value => value.ClassificationPath != null))
        {
            try
            {
                var saved = store.LoadObjectClassificationMetadata(sample, normalizeStates: true);
                if (saved.Reviewed) reviewedObjects[sample.Id] = saved.Objects;
            }
            catch { }
        }
        Text = "Classify training masks"; WindowState = FormWindowState.Maximized;
        MinimumSize = new(1100, 720); KeyPreview = true;

        FlowLayoutPanel toolbar = new() { Dock = DockStyle.Top, Height = 44, AutoScroll = true };
        Button previous = new() { Text = "Previous", AutoSize = true };
        Button next = new() { Text = "Next", AutoSize = true };
        Button nextUnreviewed = new() { Text = "Next unclassified", AutoSize = true };
        Button save = new() { Text = "Save", AutoSize = true };
        Button complete = new() { Text = "Mark image classified + next", AutoSize = true };
        Button markRemainder = new() { Text = "Mark remainder unclassified", AutoSize = true };
        RadioButton rectangle = ToolButton("Rectangle (Alt+R)", AnnotationTool.ClassifyRectangle, true);
        RadioButton paint = ToolButton("Paint (Alt+P)", AnnotationTool.ClassifyPaint, false);
        RadioButton connected = ToolButton("Connected (Alt+C)", AnnotationTool.ClassifyConnected, false);
        toolbar.Controls.AddRange([previous, next, nextUnreviewed, save, complete, markRemainder,
            rectangle, paint, connected, new Label { Text = "Brush", AutoSize = true,
                Padding = new(6, 9, 0, 0) }, brush, continueObject, imageProgress, coverage]);

        TableLayoutPanel categoryPanel = new() { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 1, Padding = new Padding(8) };
        categoryPanel.Controls.Add(new Label { Text = "Category (1–9; G/M/D/F/C; 0 = ignore)", AutoSize = true,
            Font = new Font(Font, FontStyle.Bold) });
        for (int index = 0; index < Categories.Length; index++)
        {
            string category = Categories[index];
            string shortcut = index < 9 ? (index + 1).ToString() : category switch
            {
                "gloves" => "G", "mask" => "M", "diaper" => "D", "fluids" => "F",
                "chastity" => "C", _ => "0"
            };
            RadioButton button = new() { Text = $"{shortcut}  {category}", AutoSize = true,
                Checked = index == 0, ForeColor = CategoryColors[index] };
            button.CheckedChanged += (_, _) => { if (button.Checked) SelectCategory(category); };
            categoryButtons[category] = button; categoryPanel.Controls.Add(button);
        }
        stateList.Items.AddRange(States.Select((value, index) =>
            $"Alt+{index + 1}  {value}").Cast<object>().ToArray());
        stateList.ItemCheck += (_, _) => BeginInvoke(UpdateSelectedObjectMetadata);

        Button newObject = new() { Text = "New object", AutoSize = true };
        Button removeObject = new() { Text = "Remove selected", AutoSize = true };
        FlowLayoutPanel objectActions = new() { Dock = DockStyle.Bottom, Height = 40 };
        objectActions.Controls.AddRange([newObject, removeObject]);
        Panel objectPanel = new() { Dock = DockStyle.Fill };
        objectPanel.Controls.Add(objectList); objectPanel.Controls.Add(objectActions);
        TableLayoutPanel side = new() { Dock = DockStyle.Right, Width = 310, RowCount = 6,
            ColumnCount = 1, Padding = new Padding(6) };
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        side.Controls.Add(categoryPanel, 0, 0);
        side.Controls.Add(new Label { Text = "States (toggle independently)", AutoSize = true,
            Font = new Font(Font, FontStyle.Bold), Padding = new Padding(8, 5, 0, 2) }, 0, 1);
        side.Controls.Add(stateList, 0, 2);
        side.Controls.Add(objectPanel, 0, 3);
        side.Controls.Add(new Label { Text = "Classification progress", AutoSize = true,
            Font = new Font(Font, FontStyle.Bold), Padding = new Padding(8, 8, 0, 2) }, 0, 4);
        side.Controls.Add(totals, 0, 5);

        Controls.Add(canvas); Controls.Add(side); Controls.Add(toolbar); Controls.Add(status);
        canvas.ClassificationObjectRequested += EnsureObject;
        canvas.ClassificationGestureCompleted += ClassificationGestureCompleted;
        canvas.MaskChanged += () =>
        {
            if (loading) return;
            dirty = true;
        };
        canvas.BrushSizeChanged += value => { if (brush.Value != value) brush.Value = value; };
        brush.ValueChanged += (_, _) => canvas.BrushSize = brush.Value;
        objectList.SelectedIndexChanged += (_, _) => SelectObject();
        objectList.DrawItem += DrawObjectItem;
        newObject.Click += (_, _) => BeginNewObject();
        removeObject.Click += (_, _) => RemoveSelectedObject();
        continueObject.CheckedChanged += (_, _) =>
        {
            if (!continueObject.Checked) BeginNewObject();
        };
        previous.Click += (_, _) => NavigateRelative(-1);
        next.Click += (_, _) => NavigateRelative(1);
        nextUnreviewed.Click += (_, _) => NavigateUnreviewed();
        save.Click += (_, _) => SaveCurrent();
        complete.Click += (_, _) => CompleteAndNext();
        markRemainder.Click += (_, _) => MarkRemainderUnclassified();
        Shown += (_, _) =>
        {
            if (samples.Length == 0)
            {
                status.Text = "This dataset has no accepted positive masks to classify.";
                toolbar.Enabled = side.Enabled = false; return;
            }
            int first = Array.FindIndex(samples, value => value.ClassifiedUtc == null);
            LoadSample(first >= 0 ? first : 0);
        };
    }

    RadioButton ToolButton(string text, AnnotationTool tool, bool selected)
    {
        RadioButton button = new() { Text = text, AutoSize = true, Appearance = Appearance.Button,
            Checked = selected };
        button.CheckedChanged += (_, _) => { if (button.Checked) SetTool(tool); };
        if (selected) canvas.Tool = tool;
        return button;
    }

    void SetTool(AnnotationTool tool)
    {
        canvas.Tool = tool;
        canvas.BrushResizeEnabled = tool == AnnotationTool.ClassifyPaint;
        status.Text = tool switch
        {
            AnnotationTool.ClassifyRectangle => "Drag around an object; only existing mask pixels inside are selected. Right-drag clears.",
            AnnotationTool.ClassifyPaint => "Paint across existing mask pixels. Right-drag clears. Ctrl+wheel changes brush size.",
            _ => "Click a masked area to select its complete connected component. Right-click clears it."
        };
        status.Text += " Hold Shift to add this gesture to the previous object.";
    }

    void LoadSample(int index)
    {
        SaveCurrent(); loading = true;
        try
        {
            sampleIndex = Math.Clamp(index, 0, samples.Length - 1);
            TrainingSample sample = samples[sampleIndex];
            canvas.LoadImage(store.Resolve(sample.FramePath!));
            canvas.LoadSelectableMask(store.Resolve(sample.PropMaskPath!));
            ObjectClassification saved = store.LoadObjectClassification(sample);
            objects.Clear(); objects.AddRange(saved.Objects);
            canvas.ObjectColors = objects.ToDictionary(value => value.Id,
                value => Color.FromArgb(value.ColorArgb));
            canvas.LoadObjectIds(saved.Ids); reviewed = saved.Reviewed;
            nextObjectId = Math.Max(1, objects.Select(value => value.Id).DefaultIfEmpty().Max() + 1);
            RefreshObjectList(); lastClassificationObjectId = 0; BeginNewObject(); dirty = false;
            imageProgress.Text = $"Image {sampleIndex + 1:N0}/{samples.Length:N0}";
            UpdateCoverage(); UpdateTotals(); canvas.Fit();
            status.Text = reviewed ? "This image is classified; edits will update it."
                : "Choose a category, then classify every white mask pixel.";
        }
        finally { loading = false; }
    }

    int EnsureObject()
    {
        if (objectList.SelectedIndex >= 0)
            return objects[objectList.SelectedIndex].Id;
        if ((ModifierKeys & Keys.Shift) != 0 && objects.Count > 0)
        {
            int previous = objects.FindIndex(value => value.Id == lastClassificationObjectId);
            objectList.SelectedIndex = previous >= 0 ? previous : objects.Count - 1;
            return objects[objectList.SelectedIndex].Id;
        }
        ClassifiedObject item = new()
        {
            Id = nextObjectId++, Category = selectedCategory,
            States = SelectedStates(), ColorArgb = ClassificationColor(selectedCategory, SelectedStates()).ToArgb()
        };
        objects.Add(item); RefreshObjectList();
        objectList.SelectedIndex = objects.Count - 1; dirty = true;
        return item.Id;
    }

    void ClassificationGestureCompleted()
    {
        int currentId = canvas.CurrentObjectId;
        RemoveEmptyObjects();
        if (objects.Any(value => value.Id == currentId)) lastClassificationObjectId = currentId;
        int selected = continueObject.Checked ? objects.FindIndex(value => value.Id == currentId) : -1;
        RefreshObjectList(selected); UpdateCoverage(); dirty = true;
        if (selected < 0) BeginNewObject();
    }

    void BeginNewObject()
    {
        objectList.ClearSelected(); canvas.CurrentObjectId = 0;
    }

    void SelectObject()
    {
        if (loading || objectList.SelectedIndex < 0) return;
        ClassifiedObject item = objects[objectList.SelectedIndex];
        canvas.CurrentObjectId = item.Id; loading = true;
        try
        {
            categoryButtons[item.Category].Checked = true;
            for (int index = 0; index < States.Length; index++)
                stateList.SetItemChecked(index, item.States.Contains(States[index],
                    StringComparer.OrdinalIgnoreCase));
        }
        finally { loading = false; }
    }

    void SelectCategory(string category)
    {
        selectedCategory = category;
        if (!loading) UpdateSelectedObjectMetadata();
    }

    void UpdateSelectedObjectMetadata()
    {
        if (loading || objectList.SelectedIndex < 0) return;
        ClassifiedObject item = objects[objectList.SelectedIndex];
        item.Category = selectedCategory; item.States = SelectedStates();
        item.ColorArgb = ClassificationColor(item.Category, item.States).ToArgb();
        canvas.ObjectColors = objects.ToDictionary(value => value.Id,
            value => Color.FromArgb(value.ColorArgb));
        canvas.Invalidate(); RefreshObjectList(objectList.SelectedIndex); dirty = true;
    }

    string[] SelectedStates() => Enumerable.Range(0, States.Length)
        .Where(stateList.GetItemChecked).Select(index => States[index]).ToArray();

    void RemoveSelectedObject()
    {
        if (objectList.SelectedIndex < 0) return;
        int index = objectList.SelectedIndex;
        canvas.CurrentObjectId = objects[index].Id; canvas.ClearCurrentObject();
        objects.RemoveAt(index); RefreshObjectList(); BeginNewObject(); dirty = true; UpdateCoverage();
    }

    void RemoveEmptyObjects()
    {
        int[] ids = canvas.CopyObjectIds();
        objects.RemoveAll(item => !ids.Contains(item.Id));
    }

    void RefreshObjectList(int selected = -1)
    {
        objectList.BeginUpdate(); objectList.Items.Clear();
        foreach (ClassifiedObject item in objects)
            objectList.Items.Add($"#{item.Id}  {DisplayClass(item)}");
        objectList.EndUpdate();
        if (selected >= 0 && selected < objectList.Items.Count) objectList.SelectedIndex = selected;
        canvas.ObjectColors = objects.ToDictionary(value => value.Id,
            value => Color.FromArgb(value.ColorArgb));
        canvas.Invalidate();
    }

    void DrawObjectItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= objects.Count) return;
        ClassifiedObject item = objects[e.Index];
        Rectangle swatch = new(e.Bounds.Left + 3, e.Bounds.Top + 3, 16, 16);
        using SolidBrush color = new(Color.FromArgb(item.ColorArgb));
        e.Graphics.FillRectangle(color, swatch); e.Graphics.DrawRectangle(Pens.DimGray, swatch);
        TextRenderer.DrawText(e.Graphics, $"#{item.Id}  {DisplayClass(item)}", e.Font,
            new Rectangle(e.Bounds.Left + 24, e.Bounds.Top, e.Bounds.Width - 24, e.Bounds.Height),
            e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    void UpdateCoverage()
    {
        int total = canvas.SelectablePixelCount, assigned = canvas.AssignedSelectablePixelCount;
        if (!loading && assigned != total) reviewed = false;
        coverage.Text = total == 0 ? "No mask pixels" :
            $"Coverage: {assigned * 100d / total:0.00}% ({total - assigned:N0} remaining)";
    }

    void UpdateTotals()
    {
        Dictionary<string, int> categories = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> states = new(StringComparer.OrdinalIgnoreCase);
        int objectCount = 0;
        foreach (ClassifiedObject item in reviewedObjects.Values.SelectMany(value => value))
        {
            objectCount++; categories[item.Category] = categories.GetValueOrDefault(item.Category) + 1;
            foreach (string state in item.States) states[state] = states.GetValueOrDefault(state) + 1;
        }
        List<string> lines =
        [
            $"Images: {reviewedObjects.Count:N0}/{samples.Length:N0}",
            $"Objects: {objectCount:N0}", ""
        ];
        foreach (string category in Categories)
            lines.Add($"{category}: {categories.GetValueOrDefault(category):N0}");
        lines.Add("");
        foreach (string state in States)
            lines.Add($"{state}: {states.GetValueOrDefault(state):N0}");
        totals.Text = string.Join("\r\n", lines);
    }

    void SaveCurrent()
    {
        if (samples.Length == 0 || !dirty) return;
        RemoveEmptyObjects();
        store.SaveObjectClassification(samples[sampleIndex], canvas.CopyObjectIds(), objects, reviewed);
        NormalizeObjectStates(objects);
        RefreshObjectList(objectList.SelectedIndex);
        if (reviewed)
            reviewedObjects[samples[sampleIndex].Id] = objects.Select(CloneObject).ToArray();
        else reviewedObjects.Remove(samples[sampleIndex].Id);
        dirty = false; UpdateTotals(); status.Text = reviewed ? "Classified image saved." : "Draft classification saved.";
    }

    void CompleteAndNext()
    {
        if (canvas.SelectablePixelCount == 0 ||
            canvas.AssignedSelectablePixelCount != canvas.SelectablePixelCount)
        {
            MessageBox.Show(this, "Classify every white mask pixel before marking this image complete. Use Other for uncertain objects, or Mark remainder unclassified for mask noise.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return;
        }
        reviewed = true; dirty = true; SaveCurrent(); NavigateUnreviewed();
    }

    void MarkRemainderUnclassified()
    {
        int remaining = canvas.SelectablePixelCount - canvas.AssignedSelectablePixelCount;
        if (remaining <= 0) { status.Text = "There are no unclassified mask pixels remaining."; return; }
        ClassifiedObject item = new()
        {
            Id = nextObjectId++, Category = "unclassified", States = [],
            ColorArgb = ClassificationColor("unclassified", []).ToArgb()
        };
        objects.Add(item);
        canvas.ObjectColors = objects.ToDictionary(value => value.Id,
            value => Color.FromArgb(value.ColorArgb));
        int assigned = canvas.AssignAllUnassignedSelectablePixels(item.Id);
        if (assigned == 0) { objects.Remove(item); return; }
        dirty = true; RefreshObjectList(); BeginNewObject(); UpdateCoverage();
        status.Text = $"Marked {assigned:N0} remaining mask pixel(s) as unclassified/ignored.";
    }

    void NavigateRelative(int offset) => LoadSample((sampleIndex + offset + samples.Length) % samples.Length);

    void NavigateUnreviewed()
    {
        SaveCurrent();
        for (int offset = 1; offset <= samples.Length; offset++)
        {
            int index = (sampleIndex + offset) % samples.Length;
            if (samples[index].ClassifiedUtc == null) { LoadSample(index); return; }
        }
        status.Text = "All positive training images have been classified.";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { SaveCurrent(); }
        catch (Exception error)
        {
            e.Cancel = true; MessageBox.Show(this, error.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys code = keyData & Keys.KeyCode;
        Keys modifiers = keyData & Keys.Modifiers;
        if (modifiers == Keys.None && code >= Keys.D1 && code <= Keys.D9)
        {
            categoryButtons[Categories[(int)code - (int)Keys.D1]].Checked = true; return true;
        }
        if (modifiers == Keys.None && code == Keys.D0)
        {
            categoryButtons["unclassified"].Checked = true; return true;
        }
        if (modifiers == Keys.None && code == Keys.G)
        {
            categoryButtons["gloves"].Checked = true; return true;
        }
        if (modifiers == Keys.None && code == Keys.M)
        {
            categoryButtons["mask"].Checked = true; return true;
        }
        if (modifiers == Keys.None && code == Keys.D)
        {
            categoryButtons["diaper"].Checked = true; return true;
        }
        if (modifiers == Keys.None && code == Keys.F)
        {
            categoryButtons["fluids"].Checked = true; return true;
        }
        if (modifiers == Keys.None && code == Keys.C)
        {
            categoryButtons["chastity"].Checked = true; return true;
        }
        if (modifiers == Keys.Alt && code is Keys.D1 or Keys.D2)
        {
            int index = (int)code - (int)Keys.D1;
            stateList.SetItemChecked(index, !stateList.GetItemChecked(index)); return true;
        }
        if (modifiers == Keys.Alt && code == Keys.R) { SetCheckedTool(AnnotationTool.ClassifyRectangle); return true; }
        if (modifiers == Keys.Alt && code == Keys.P) { SetCheckedTool(AnnotationTool.ClassifyPaint); return true; }
        if (modifiers == Keys.Alt && code == Keys.C) { SetCheckedTool(AnnotationTool.ClassifyConnected); return true; }
        if (keyData == (Keys.Control | Keys.S)) { SaveCurrent(); return true; }
        if (keyData == (Keys.Control | Keys.Z)) { canvas.Undo(); UpdateCoverage(); return true; }
        if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z))
        { canvas.Redo(); UpdateCoverage(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void SetCheckedTool(AnnotationTool tool)
    {
        foreach (RadioButton button in Controls.OfType<FlowLayoutPanel>().SelectMany(value =>
                     value.Controls.OfType<RadioButton>()))
        {
            AnnotationTool candidate = button.Text.StartsWith("Rectangle", StringComparison.Ordinal) ? AnnotationTool.ClassifyRectangle :
                button.Text.StartsWith("Paint", StringComparison.Ordinal) ? AnnotationTool.ClassifyPaint :
                button.Text.StartsWith("Connected", StringComparison.Ordinal) ? AnnotationTool.ClassifyConnected : (AnnotationTool)(-1);
            if (candidate == tool) { button.Checked = true; return; }
        }
        SetTool(tool);
    }

    internal static string DisplayClass(ClassifiedObject item) => item.States.Length == 0
        ? item.Category : item.Category + " · " + string.Join(" + ", item.States);

    internal static Color ClassificationColor(string category, IReadOnlyCollection<string> states)
    {
        int index = Array.FindIndex(Categories, value => value.Equals(category, StringComparison.OrdinalIgnoreCase));
        Color color = CategoryColors[Math.Max(0, index)];
        if (!CategorySupportsStates(category)) states = Array.Empty<string>();
        if (states.Contains("held", StringComparer.OrdinalIgnoreCase)) color = Blend(color, Color.Gold, .38);
        if (states.Contains("inserted", StringComparer.OrdinalIgnoreCase)) color = Blend(color, Color.HotPink, .42);
        return color;
    }

    internal static bool CategorySupportsStates(string category) =>
        category.Equals("dildo", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("butt-plug", StringComparison.OrdinalIgnoreCase);

    static void NormalizeObjectStates(IEnumerable<ClassifiedObject> values)
    {
        foreach (ClassifiedObject item in values)
        {
            if (!CategorySupportsStates(item.Category)) item.States = [];
            item.ColorArgb = ClassificationColor(item.Category, item.States).ToArgb();
        }
    }

    static Color Blend(Color first, Color second, double amount) => Color.FromArgb(
        (int)Math.Round(first.R * (1 - amount) + second.R * amount),
        (int)Math.Round(first.G * (1 - amount) + second.G * amount),
        (int)Math.Round(first.B * (1 - amount) + second.B * amount));

    static ClassifiedObject CloneObject(ClassifiedObject value) => new()
    {
        Id = value.Id, Category = value.Category, States = [.. value.States],
        ColorArgb = value.ColorArgb
    };
}
