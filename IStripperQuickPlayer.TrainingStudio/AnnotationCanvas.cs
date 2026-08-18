using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace IStripperQuickPlayer.TrainingStudio;

internal enum AnnotationTool { Brush, Eraser, Polygon, SamPositive, SamNegative, SamBox }

internal sealed class AnnotationCanvas : Control
{
    Bitmap? source, overlay;
    int[] ids = [];
    readonly Stack<int[]> undo = [];
    readonly Stack<int[]> redo = [];
    readonly List<PointF> polygon = [];
    readonly List<(PointF Point, bool Positive)> samPromptPoints = [];
    float zoom = 1;
    PointF pan;
    Point lastMouse;
    bool drawing, panning, spacePressed;
    RectangleF samBox;
    RectangleF? displayedSamBox;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal AnnotationTool Tool { get; set; } = AnnotationTool.Brush;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int CurrentObjectId { get; set; } = 1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int BrushSize { get; set; } = 32;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal IReadOnlyDictionary<int, Color> ObjectColors { get; set; } = new Dictionary<int, Color>();
    internal bool CanUndo => undo.Count > 0;
    internal bool CanRedo => redo.Count > 0;
    internal int ImageWidth => source?.Width ?? 0;
    internal int ImageHeight => source?.Height ?? 0;
    internal event Action<PointF, bool>? SamPoint;
    internal event Action<RectangleF>? SamBoxReady;
    internal event Action? MaskChanged;
    internal event Action<int>? BrushSizeChanged;

    internal AnnotationCanvas()
    {
        DoubleBuffered = true; BackColor = Color.FromArgb(32, 32, 32);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    internal void LoadImage(string path)
    {
        source?.Dispose(); overlay?.Dispose();
        using Image opened = Image.FromFile(path); source = new Bitmap(opened);
        ids = new int[source.Width * source.Height]; undo.Clear(); redo.Clear(); polygon.Clear();
        Fit(); RebuildOverlay(); Invalidate();
    }

    internal int[] CopyObjectIds() => (int[])ids.Clone();
    internal bool CurrentObjectHasPixels => ids.Contains(CurrentObjectId);
    internal void LoadObjectIds(int[] values)
    {
        if (values.Length != ids.Length) throw new ArgumentException("Mask dimensions do not match the frame.");
        ids = (int[])values.Clone(); undo.Clear(); redo.Clear(); RebuildOverlay(); Invalidate();
    }

    internal void Undo()
    {
        if (undo.TryPop(out int[]? previous))
        {
            redo.Push((int[])ids.Clone());
            ids = previous; RebuildOverlay(); Invalidate(); MaskChanged?.Invoke();
        }
    }

    internal void Redo()
    {
        if (redo.TryPop(out int[]? next))
        {
            undo.Push((int[])ids.Clone());
            ids = next; RebuildOverlay(); Invalidate(); MaskChanged?.Invoke();
        }
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

    internal void ExportCurrentObjectMask(string path)
    {
        byte[] mask = ids.Select(value => value == CurrentObjectId ? (byte)255 : (byte)0).ToArray();
        PngMaskWriter.SaveGray8(path, ImageWidth, ImageHeight, mask);
    }

    internal void ApplyObjectMask(string path)
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
                    if (on) ids[index] = CurrentObjectId;
                    else if (ids[index] == CurrentObjectId) ids[index] = 0;
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
        base.OnPaint(e); if (source == null) return;
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        RectangleF target = new(pan.X, pan.Y, source.Width * zoom, source.Height * zoom);
        e.Graphics.DrawImage(source, target);
        if (overlay != null) e.Graphics.DrawImage(overlay, target);
        if (polygon.Count > 0)
        {
            PointF[] points = polygon.Select(ToClient).ToArray();
            using Pen pen = new(Color.Yellow, 2);
            if (points.Length > 1) e.Graphics.DrawLines(pen, points);
            foreach (PointF point in points) e.Graphics.FillEllipse(Brushes.Yellow,
                point.X - 3, point.Y - 3, 6, 6);
        }
        RectangleF? box = drawing && Tool == AnnotationTool.SamBox ? samBox : displayedSamBox;
        if (box is RectangleF visibleBox)
        {
            RectangleF clientBox = ToClient(visibleBox);
            using Pen pen = new(Color.DeepSkyBlue, 2); e.Graphics.DrawRectangle(pen, clientBox);
        }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        foreach ((PointF point, bool positive) in samPromptPoints)
        {
            PointF client = ToClient(point);
            RectangleF marker = new(client.X - 7, client.Y - 7, 14, 14);
            using SolidBrush fill = new(positive ? Color.LimeGreen : Color.Red);
            using Pen outline = new(Color.White, 2);
            using Pen symbol = new(Color.Black, 2);
            e.Graphics.FillEllipse(fill, marker); e.Graphics.DrawEllipse(outline, marker);
            e.Graphics.DrawLine(symbol, client.X - 4, client.Y, client.X + 4, client.Y);
            if (positive) e.Graphics.DrawLine(symbol, client.X, client.Y - 4, client.X, client.Y + 4);
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
        if (Tool == AnnotationTool.SamPositive || Tool == AnnotationTool.SamNegative)
        {
            SamPoint?.Invoke(image.Value, Tool == AnnotationTool.SamPositive); return;
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
        if ((ModifierKeys & Keys.Control) != 0)
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
            spacePressed = false; if (!panning) Cursor = Cursors.Default; e.Handled = true;
        }
    }

    protected override void OnLostFocus(EventArgs e)
    {
        spacePressed = panning = false; Cursor = Cursors.Default; base.OnLostFocus(e);
    }

    internal static int AdjustBrushSize(int current, int wheelDelta) => wheelDelta == 0
        ? Math.Clamp(current, 2, 200)
        : Math.Clamp(current + Math.Sign(wheelDelta) * 4, 2, 200);

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
        undo.Push((int[])ids.Clone());
        redo.Clear();
        int bytesPerState = Math.Max(1, ids.Length * sizeof(int));
        int limit = Math.Clamp((256 * 1024 * 1024) / bytesPerState, 2, 20);
        if (undo.Count <= limit) return;
        int[][] newest = undo.Take(limit).Reverse().ToArray();
        undo.Clear();
        foreach (int[] state in newest) undo.Push(state);
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
        if (disposing) { source?.Dispose(); overlay?.Dispose(); }
        base.Dispose(disposing);
    }
}
