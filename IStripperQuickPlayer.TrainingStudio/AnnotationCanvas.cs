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
    float zoom = 1;
    PointF pan;
    Point lastMouse;
    bool drawing, panning;
    RectangleF samBox;

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
        if (drawing && Tool == AnnotationTool.SamBox)
        {
            RectangleF box = ToClient(samBox);
            using Pen pen = new(Color.DeepSkyBlue, 2); e.Graphics.DrawRectangle(pen, box);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus(); lastMouse = e.Location;
        if (e.Button == MouseButtons.Middle) { panning = true; return; }
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
        if (e.Button == MouseButtons.Middle) { panning = false; return; }
        if (drawing && Tool == AnnotationTool.SamBox && samBox.Width >= 2 && samBox.Height >= 2)
            SamBoxReady?.Invoke(samBox);
        drawing = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e); if (source == null) return;
        PointF? before = ToImage(e.Location);
        zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.15f : 1 / 1.15f), .05f, 20);
        if (before is PointF point) pan = new(e.X - point.X * zoom, e.Y - point.Y * zoom);
        Invalidate();
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
        Changed();
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
        if (undo.Count <= 20) return;
        int[][] newest = undo.Take(20).Reverse().ToArray();
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
