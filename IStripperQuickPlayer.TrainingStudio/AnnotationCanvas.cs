using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace IStripperQuickPlayer.TrainingStudio;

internal enum AnnotationTool { Brush, Eraser, Polygon, SamPositive, SamNegative, SamBox }

internal sealed class AnnotationCanvas : Control
{
    Bitmap? source, overlay, rvmOverlay;
    int[] ids = [];
    interface IHistoryEntry
    {
        void Undo(AnnotationCanvas canvas);
        void Redo(AnnotationCanvas canvas);
    }

    sealed class MaskHistory(int[] before) : IHistoryEntry
    {
        int[] Before { get; set; } = before;
        int[]? after;
        public void Undo(AnnotationCanvas canvas)
        {
            after = (int[])canvas.ids.Clone(); canvas.ids = Before;
            canvas.RebuildOverlay(); canvas.Invalidate(); canvas.MaskChanged?.Invoke();
        }
        public void Redo(AnnotationCanvas canvas)
        {
            if (after == null) return;
            Before = (int[])canvas.ids.Clone(); canvas.ids = after;
            canvas.RebuildOverlay(); canvas.Invalidate(); canvas.MaskChanged?.Invoke();
        }
    }

    sealed class ExternalHistory(Action undo, Action redo) : IHistoryEntry
    {
        public void Undo(AnnotationCanvas canvas) => undo();
        public void Redo(AnnotationCanvas canvas) => redo();
    }

    readonly Stack<IHistoryEntry> undo = [];
    readonly Stack<IHistoryEntry> redo = [];
    readonly List<PointF> polygon = [];
    readonly List<(PointF Point, bool Positive)> samPromptPoints = [];
    float zoom = 1;
    PointF pan;
    Point lastMouse;
    bool drawing, panning, spacePressed;
    RectangleF samBox;
    RectangleF? displayedSamBox;
    Point? brushCursor;
    AnnotationTool tool = AnnotationTool.Brush;
    int brushSize = 32;
    bool showRvmOverlay = true;
    DirectCompositionCanvasRenderer? compositor;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal AnnotationTool Tool
    {
        get => tool;
        set
        {
            tool = value;
            if (tool is not (AnnotationTool.Brush or AnnotationTool.Eraser))
                SetBrushCursor(null);
            Cursor = NormalCursor;
        }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int CurrentObjectId { get; set; } = 1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int BrushSize
    {
        get => brushSize;
        set
        {
            InvalidateBrushCursor();
            brushSize = Math.Clamp(value, 2, 200);
            InvalidateBrushCursor();
        }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool BrushResizeEnabled { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ShowRvmOverlay
    {
        get => showRvmOverlay;
        set { if (showRvmOverlay != value) { showRvmOverlay = value; Invalidate(); } }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal IReadOnlyDictionary<int, Color> ObjectColors { get; set; } = new Dictionary<int, Color>();
    internal bool CanUndo => undo.Count > 0;
    internal bool CanRedo => redo.Count > 0;
    internal int ImageWidth => source?.Width ?? 0;
    internal int ImageHeight => source?.Height ?? 0;
    internal event Action<PointF, bool>? SamPoint;
    internal event Action? SamApplyRequested;
    internal event Action<RectangleF>? SamBoxReady;
    internal event Action? MaskChanged;
    internal event Action<int>? BrushSizeChanged;

    internal AnnotationCanvas()
    {
        BackColor = Color.FromArgb(32, 32, 32);
        SetStyle(ControlStyles.Opaque | ControlStyles.ResizeRedraw |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        compositor = new DirectCompositionCanvasRenderer(Handle, ClientSize);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        compositor?.Dispose(); compositor = null;
        base.OnHandleDestroyed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e); compositor?.Resize(ClientSize);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    Cursor NormalCursor => Tool is AnnotationTool.Brush or AnnotationTool.Eraser
        ? Cursors.Default : Cursors.Cross;

    internal void LoadImage(string path)
    {
        source?.Dispose(); overlay?.Dispose();
        using Image opened = Image.FromFile(path); source = new Bitmap(opened);
        ids = new int[source.Width * source.Height]; undo.Clear(); redo.Clear(); polygon.Clear();
        Fit(); RebuildOverlay(); Invalidate();
    }

    internal void LoadRvmMask(string? path)
    {
        rvmOverlay?.Dispose(); rvmOverlay = null;
        if (source == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        using Bitmap opened = new(path);
        using Bitmap resized = new(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(opened, new Rectangle(Point.Empty, resized.Size));
        }
        rvmOverlay = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        BitmapData input = resized.LockBits(new Rectangle(Point.Empty, resized.Size),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData output = rvmOverlay.LockBits(new Rectangle(Point.Empty, rvmOverlay.Size),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        int[] row = new int[source.Width];
        try
        {
            for (int y = 0; y < source.Height; y++)
            {
                Marshal.Copy(input.Scan0 + y * input.Stride, row, 0, row.Length);
                for (int x = 0; x < row.Length; x++)
                    row[x] = ((row[x] >> 16) & 255) >= 128
                        ? Color.FromArgb(105, 255, 80, 180).ToArgb() : 0;
                Marshal.Copy(row, 0, output.Scan0 + y * output.Stride, row.Length);
            }
        }
        finally { resized.UnlockBits(input); rvmOverlay.UnlockBits(output); }
        Invalidate();
    }

    internal int[] CopyObjectIds() => (int[])ids.Clone();
    internal bool CurrentObjectHasPixels => ObjectHasPixels(CurrentObjectId);
    internal bool ObjectHasPixels(int objectId) => ids.Contains(objectId);
    internal void LoadObjectIds(int[] values)
    {
        if (values.Length != ids.Length) throw new ArgumentException("Mask dimensions do not match the frame.");
        ids = (int[])values.Clone(); undo.Clear(); redo.Clear(); RebuildOverlay(); Invalidate();
    }

    internal void Undo()
    {
        if (undo.TryPop(out IHistoryEntry? entry))
        {
            entry.Undo(this); redo.Push(entry);
        }
    }

    internal void Redo()
    {
        if (redo.TryPop(out IHistoryEntry? entry))
        {
            entry.Redo(this); undo.Push(entry);
        }
    }

    internal void RecordExternalChange(Action undoAction, Action redoAction)
    {
        undo.Push(new ExternalHistory(undoAction, redoAction)); redo.Clear();
        TrimUndoHistory();
    }

    internal void ClearCurrentObject()
    {
        SaveUndo();
        for (int i = 0; i < ids.Length; i++) if (ids[i] == CurrentObjectId) ids[i] = 0;
        Changed();
    }

    internal void SetSamPrompts(IReadOnlyList<PointF> points, IReadOnlyList<int> labels,
        RectangleF? box)
    {
        samPromptPoints.Clear();
        for (int index = 0; index < Math.Min(points.Count, labels.Count); index++)
            samPromptPoints.Add((points[index], labels[index] > 0));
        displayedSamBox = box; Invalidate();
    }

    internal void ExportObjectMask(string path, int objectId)
    {
        byte[] mask = ids.Select(value => value == objectId ? (byte)255 : (byte)0).ToArray();
        PngMaskWriter.SaveGray8(path, ImageWidth, ImageHeight, mask);
    }

    internal void ApplyObjectMask(string path, int objectId)
    {
        using Bitmap loaded = new(path);
        if (loaded.Width != ImageWidth || loaded.Height != ImageHeight)
            throw new InvalidDataException("SAM2 returned a mask with the wrong dimensions.");
        using Bitmap mask = new(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(mask)) graphics.DrawImageUnscaled(loaded, 0, 0);
        SaveUndo();
        BitmapData data = mask.LockBits(new Rectangle(0, 0, mask.Width, mask.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int[] row = new int[mask.Width];
            for (int y = 0; y < mask.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < row.Length; x++)
                {
                    int index = y * mask.Width + x;
                    bool on = ((row[x] >> 16) & 255) >= 128;
                    if (on) ids[index] = objectId;
                    else if (ids[index] == objectId) ids[index] = 0;
                }
            }
        }
        finally { mask.UnlockBits(data); }
        Changed();
    }

    internal void Fit()
    {
        if (source == null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        zoom = Math.Min((float)ClientSize.Width / source.Width,
            (float)ClientSize.Height / source.Height);
        zoom = Math.Max(.05f, zoom);
        pan = new((ClientSize.Width - source.Width * zoom) / 2,
            (ClientSize.Height - source.Height * zoom) / 2);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (source == null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (compositor?.Failure != null)
        {
            DrawScene(e.Graphics); return;
        }
        Bitmap frame = new(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(frame)) DrawScene(graphics);
        if (compositor != null) compositor.Present(frame); else frame.Dispose();
    }

    void DrawScene(Graphics graphics)
    {
        if (source == null) return;
        graphics.Clear(BackColor);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        RectangleF target = new(pan.X, pan.Y, source.Width * zoom, source.Height * zoom);
        graphics.DrawImage(source, target);
        if (ShowRvmOverlay && rvmOverlay != null) graphics.DrawImage(rvmOverlay, target);
        if (overlay != null) graphics.DrawImage(overlay, target);
        if (polygon.Count > 0)
        {
            PointF[] points = polygon.Select(ToClient).ToArray();
            using Pen pen = new(Color.Yellow, 2);
            if (points.Length > 1) graphics.DrawLines(pen, points);
            foreach (PointF point in points) graphics.FillEllipse(Brushes.Yellow,
                point.X - 3, point.Y - 3, 6, 6);
        }
        RectangleF? box = drawing && Tool == AnnotationTool.SamBox ? samBox : displayedSamBox;
        if (box is RectangleF visibleBox)
        {
            RectangleF clientBox = ToClient(visibleBox);
            using Pen pen = new(Color.DeepSkyBlue, 2); graphics.DrawRectangle(pen, clientBox);
        }
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        foreach ((PointF point, bool positive) in samPromptPoints)
        {
            PointF client = ToClient(point);
            RectangleF marker = new(client.X - 7, client.Y - 7, 14, 14);
            using SolidBrush fill = new(positive ? Color.LimeGreen : Color.Red);
            using Pen outline = new(Color.White, 2);
            using Pen symbol = new(Color.Black, 2);
            graphics.FillEllipse(fill, marker); graphics.DrawEllipse(outline, marker);
            graphics.DrawLine(symbol, client.X - 4, client.Y, client.X + 4, client.Y);
            if (positive) graphics.DrawLine(symbol, client.X, client.Y - 4, client.X, client.Y + 4);
        }
        if (brushCursor is Point cursor &&
            Tool is AnnotationTool.Brush or AnnotationTool.Eraser)
        {
            float diameter = Math.Max(2, BrushSize * zoom);
            RectangleF outline = new(cursor.X - diameter / 2,
                cursor.Y - diameter / 2, diameter, diameter);
            using Pen shadow = new(Color.Black, 3);
            using Pen edge = new(Color.White, 1);
            graphics.DrawEllipse(shadow, outline);
            graphics.DrawEllipse(edge, outline);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus(); lastMouse = e.Location;
        if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Left && spacePressed)
        {
            panning = true; Cursor = Cursors.Hand; return;
        }
        PointF? image = ToImage(e.Location); if (image == null) return;
        if (Tool == AnnotationTool.SamPositive && e.Button is MouseButtons.Left or MouseButtons.Right)
        {
            if (e.Clicks > 1) { SamApplyRequested?.Invoke(); return; }
            SamPoint?.Invoke(image.Value, e.Button == MouseButtons.Left); return;
        }
        if (Tool == AnnotationTool.SamNegative)
        {
            if (e.Clicks > 1) { SamApplyRequested?.Invoke(); return; }
            SamPoint?.Invoke(image.Value, false); return;
        }
        if (Tool == AnnotationTool.Polygon)
        {
            if (e.Button == MouseButtons.Right && polygon.Count >= 3) CommitPolygon();
            else polygon.Add(image.Value);
            Invalidate(); return;
        }
        if (Tool == AnnotationTool.SamBox)
        {
            drawing = true; samBox = new(image.Value, SizeF.Empty); return;
        }
        SaveUndo(); drawing = true; PaintBrush(image.Value, Tool == AnnotationTool.Eraser || e.Button == MouseButtons.Right);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetBrushCursor(Tool is AnnotationTool.Brush or AnnotationTool.Eraser &&
            ToImage(e.Location) != null ? e.Location : null);
        if (panning)
        {
            pan = new(pan.X + e.X - lastMouse.X, pan.Y + e.Y - lastMouse.Y);
            lastMouse = e.Location; Invalidate(); return;
        }
        if (!drawing) return;
        PointF? image = ToImage(e.Location); if (image == null) return;
        if (Tool == AnnotationTool.SamBox)
        {
            PointF origin = samBox.Location;
            samBox = RectangleF.FromLTRB(Math.Min(origin.X, image.Value.X),
                Math.Min(origin.Y, image.Value.Y), Math.Max(origin.X, image.Value.X),
                Math.Max(origin.Y, image.Value.Y)); Invalidate();
        }
        else PaintBrush(image.Value, Tool == AnnotationTool.Eraser || e.Button == MouseButtons.Right);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        SetBrushCursor(null);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (panning)
        {
            panning = false; Cursor = spacePressed ? Cursors.Hand : Cursors.Default; return;
        }
        if (drawing && Tool == AnnotationTool.SamBox && samBox.Width >= 2 && samBox.Height >= 2)
        {
            displayedSamBox = samBox;
            SamBoxReady?.Invoke(samBox);
        }
        drawing = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0 && BrushResizeEnabled)
        {
            BrushSize = AdjustBrushSize(BrushSize, e.Delta);
            BrushSizeChanged?.Invoke(BrushSize); return;
        }
        if (source == null) return;
        PointF? before = ToImage(e.Location);
        zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.15f : 1 / 1.15f), .05f, 20);
        if (before is PointF point) pan = new(e.X - point.X * zoom, e.Y - point.Y * zoom);
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space)
        {
            spacePressed = true; Cursor = Cursors.Hand; e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space)
        {
            spacePressed = false; if (!panning) Cursor = NormalCursor; e.Handled = true;
        }
    }

    protected override void OnLostFocus(EventArgs e)
    {
        spacePressed = panning = false; Cursor = NormalCursor; base.OnLostFocus(e);
    }

    internal static int AdjustBrushSize(int current, int wheelDelta) => wheelDelta == 0
        ? Math.Clamp(current, 2, 200)
        : Math.Clamp(current + Math.Sign(wheelDelta) * 4, 2, 200);

    void SetBrushCursor(Point? value)
    {
        if (brushCursor == value) return;
        InvalidateBrushCursor();
        brushCursor = value;
        InvalidateBrushCursor();
    }

    void InvalidateBrushCursor()
    {
        if (brushCursor is not Point cursor) return;
        int radius = (int)Math.Ceiling(Math.Max(2, BrushSize * zoom) / 2) + 4;
        Invalidate(new Rectangle(cursor.X - radius, cursor.Y - radius,
            radius * 2 + 1, radius * 2 + 1));
    }

    void PaintBrush(PointF point, bool erase)
    {
        int radius = Math.Max(1, BrushSize / 2);
        int left = Math.Max(0, (int)point.X - radius), right = Math.Min(ImageWidth - 1, (int)point.X + radius);
        int top = Math.Max(0, (int)point.Y - radius), bottom = Math.Min(ImageHeight - 1, (int)point.Y + radius);
        int r2 = radius * radius;
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
            if ((x - point.X) * (x - point.X) + (y - point.Y) * (y - point.Y) <= r2)
            {
                int index = y * ImageWidth + x;
                if (erase) ids[index] = 0; else ids[index] = CurrentObjectId;
            }
        UpdateOverlay(new Rectangle(left, top, right - left + 1, bottom - top + 1));
        MaskChanged?.Invoke();
    }

    void CommitPolygon()
    {
        SaveUndo();
        using Bitmap mask = new(ImageWidth, ImageHeight, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(mask)) graphics.FillPolygon(Brushes.White, polygon.ToArray());
        BitmapData data = mask.LockBits(new Rectangle(0, 0, mask.Width, mask.Height), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        int[] row = new int[mask.Width];
        try
        {
            for (int y = 0; y < mask.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < row.Length; x++) if ((row[x] & 0xffffff) != 0) ids[y * mask.Width + x] = CurrentObjectId;
            }
        }
        finally { mask.UnlockBits(data); }
        polygon.Clear(); Changed();
    }

    void SaveUndo()
    {
        undo.Push(new MaskHistory((int[])ids.Clone())); redo.Clear();
        TrimUndoHistory();
    }

    void TrimUndoHistory()
    {
        int bytesPerState = Math.Max(1, ids.Length * sizeof(int));
        int limit = Math.Clamp((256 * 1024 * 1024) / bytesPerState, 2, 20);
        if (undo.Count <= limit) return;
        IHistoryEntry[] newest = undo.Take(limit).Reverse().ToArray();
        undo.Clear();
        foreach (IHistoryEntry state in newest) undo.Push(state);
    }

    void Changed() { RebuildOverlay(); Invalidate(); MaskChanged?.Invoke(); }

    void RebuildOverlay()
    {
        overlay?.Dispose(); if (source == null) return;
        overlay = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        int[] pixels = new int[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i] == 0) continue;
            Color color = ObjectColors.TryGetValue(ids[i], out Color value) ? value : Color.Lime;
            pixels[i] = Color.FromArgb(115, color).ToArgb();
        }
        BitmapData data = overlay.LockBits(new Rectangle(0, 0, overlay.Width, overlay.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < overlay.Height; y++)
                Marshal.Copy(pixels, y * overlay.Width, data.Scan0 + y * data.Stride, overlay.Width);
        }
        finally { overlay.UnlockBits(data); }
    }

    void UpdateOverlay(Rectangle region)
    {
        if (overlay == null || region.Width <= 0 || region.Height <= 0) return;
        BitmapData data = overlay.LockBits(region, ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        int[] row = new int[region.Width];
        try
        {
            for (int y = 0; y < region.Height; y++)
            {
                int sourceOffset = (region.Top + y) * ImageWidth + region.Left;
                for (int x = 0; x < row.Length; x++)
                {
                    int id = ids[sourceOffset + x];
                    if (id == 0) row[x] = 0;
                    else
                    {
                        Color color = ObjectColors.TryGetValue(id, out Color value) ? value : Color.Lime;
                        row[x] = Color.FromArgb(115, color).ToArgb();
                    }
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { overlay.UnlockBits(data); }
        Rectangle client = Rectangle.Ceiling(new RectangleF(pan.X + region.X * zoom,
            pan.Y + region.Y * zoom, region.Width * zoom, region.Height * zoom));
        client.Inflate(2, 2); Invalidate(client);
    }

    PointF ToClient(PointF point) => new(pan.X + point.X * zoom, pan.Y + point.Y * zoom);
    RectangleF ToClient(RectangleF box) => new(pan.X + box.X * zoom, pan.Y + box.Y * zoom,
        box.Width * zoom, box.Height * zoom);
    PointF? ToImage(Point point)
    {
        if (source == null) return null;
        float x = (point.X - pan.X) / zoom, y = (point.Y - pan.Y) / zoom;
        return x >= 0 && y >= 0 && x < source.Width && y < source.Height ? new PointF(x, y) : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { source?.Dispose(); overlay?.Dispose(); rvmOverlay?.Dispose(); }
        base.Dispose(disposing);
    }
}
