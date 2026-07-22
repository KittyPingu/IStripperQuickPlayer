using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;

namespace IStripperQuickPlayer;

internal sealed class LockStateOverlay : Form
{
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExLayered = 0x80000;
    private const int WsExNoActivate = 0x08000000;
    private static LockStateOverlay? current;

    private readonly bool locked;
    private readonly Image? image;
    private readonly System.Windows.Forms.Timer closeTimer = new() { Interval = 900 };

    private LockStateOverlay(Rectangle bounds, bool locked, Image? image = null)
    {
        this.locked = locked;
        this.image = image;
        if (image != null) closeTimer.Interval = 4_000;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = image != null ? Color.Black : locked
            ? Color.FromArgb(145, 25, 40)
            : Color.FromArgb(15, 115, 75);
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0.88;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Bounds = bounds;

        closeTimer.Tick += (_, _) => Close();
        Shown += (_, _) =>
        {
            ApplyRoundedRegion();
            closeTimer.Start();
        };
        FormClosed += (_, _) =>
        {
            closeTimer.Dispose();
            if (ReferenceEquals(current, this))
                current = null;
        };
    }

    protected override bool ShowWithoutActivation => true;

    private void ApplyRoundedRegion()
    {
        using GraphicsPath path = new();
        float corner = ClientSize.Width * 0.14f;
        path.AddArc(0, 0, corner, corner, 180, 90);
        path.AddArc(ClientSize.Width - corner, 0, corner, corner, 270, 90);
        path.AddArc(ClientSize.Width - corner, ClientSize.Height - corner,
            corner, corner, 0, 90);
        path.AddArc(0, ClientSize.Height - corner, corner, corner, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExTransparent | WsExToolWindow |
                WsExLayered | WsExNoActivate;
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x84) // WM_NCHITTEST
        {
            message.Result = new IntPtr(-1); // HTTRANSPARENT
            return;
        }
        base.WndProc(ref message);
    }

    internal static void ShowForProcess(int processId, bool locked)
    {
        if (processId == 0 || !TryGetMovieWindowBounds(processId,
            out Rectangle movieBounds, out int movieDpi))
            return;

        current?.Close();
        current = new LockStateOverlay(CalculateBounds(movieBounds, movieDpi), locked);
        current.Show();
    }

    internal static bool ShowImageForProcess(int processId, Image image)
    {
        if (processId == 0 || !TryGetMovieWindowBounds(processId,
            out Rectangle movieBounds, out int movieDpi))
            return false;

        Rectangle bounds = CalculateImageBounds(movieBounds, movieDpi,
            image.Size);
        current?.Close();
        current = new LockStateOverlay(bounds, false, image);
        current.Show();
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        if (image != null)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(image, ClientRectangle);
            return;
        }

        float size = Math.Min(ClientSize.Width, ClientSize.Height);
        float verticalOffset = size * 0.06f;
        float bodyTop = size * 0.45f + verticalOffset;
        RectangleF body = new(size * 0.18f, bodyTop,
            size * 0.64f, size * 0.38f);
        using SolidBrush white = new(Color.White);
        FillRoundedRectangle(e.Graphics, white, body, size * 0.07f);

        using Pen shackle = new(Color.White, size * 0.09f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        RectangleF arc = locked
            ? new(size * 0.30f, size * 0.10f + verticalOffset, size * 0.40f, size * 0.48f)
            : new(size * 0.30f, size * 0.08f + verticalOffset, size * 0.40f, size * 0.42f);
        e.Graphics.DrawArc(shackle, arc, 180, 180);
        e.Graphics.DrawLine(shackle, arc.Left, arc.Top + arc.Height / 2,
            arc.Left, bodyTop);
        if (locked)
        {
            e.Graphics.DrawLine(shackle, arc.Right, arc.Top + arc.Height / 2,
                arc.Right, bodyTop);
        }

        using SolidBrush keyhole = new(BackColor);
        e.Graphics.FillEllipse(keyhole, size * 0.45f, size * 0.56f + verticalOffset,
            size * 0.10f, size * 0.10f);
        e.Graphics.FillRectangle(keyhole, size * 0.48f, size * 0.63f + verticalOffset,
            size * 0.04f, size * 0.10f);
    }

    private static Rectangle CalculateBounds(Rectangle movieBounds, int movieDpi)
    {
        int available = Math.Min(movieBounds.Width, movieBounds.Height);
        int size = available * 3 * movieDpi / (10 * (int)GetDpiForSystem());
        return new Rectangle(
            movieBounds.Left + (movieBounds.Width - size) / 2,
            movieBounds.Top + (movieBounds.Height - size) / 2,
            size, size);
    }

    private static Rectangle CalculateImageBounds(Rectangle movieBounds,
        int movieDpi, Size imageSize)
    {
        Rectangle bounds = CalculateBounds(movieBounds, movieDpi);
        int height = bounds.Width * imageSize.Height / imageSize.Width;
        return new Rectangle(
            movieBounds.Left + (movieBounds.Width - bounds.Width) / 2,
            movieBounds.Top + (movieBounds.Height - height) / 2,
            bounds.Width, height);
    }

    private static void FillRoundedRectangle(Graphics graphics, Brush brush,
        RectangleF rectangle, float radius)
    {
        float diameter = radius * 2;
        graphics.FillRectangle(brush, rectangle.Left + radius, rectangle.Top,
            rectangle.Width - diameter, rectangle.Height);
        graphics.FillRectangle(brush, rectangle.Left, rectangle.Top + radius,
            rectangle.Width, rectangle.Height - diameter);
        graphics.FillEllipse(brush, rectangle.Left, rectangle.Top, diameter, diameter);
        graphics.FillEllipse(brush, rectangle.Right - diameter, rectangle.Top, diameter, diameter);
        graphics.FillEllipse(brush, rectangle.Left, rectangle.Bottom - diameter, diameter, diameter);
        graphics.FillEllipse(brush, rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter);
    }

    private static bool TryGetMovieWindowBounds(int processId, out Rectangle bounds,
        out int dpi)
    {
        Rectangle foundBounds = Rectangle.Empty;
        int foundDpi = 96;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out int ownerProcessId);
            if (ownerProcessId != processId || !IsWindowVisible(window))
                return true;

            StringBuilder className = new(128);
            if (GetClassName(window, className, className.Capacity) == 0 ||
                !className.ToString().Contains("QWindowToolSaveBitsOwnDC",
                    StringComparison.Ordinal))
                return true;

            if (GetWindowRect(window, out NativeRectangle rectangle))
            {
                foundBounds = Rectangle.FromLTRB(rectangle.Left, rectangle.Top,
                    rectangle.Right, rectangle.Bottom);
                foundDpi = (int)GetDpiForWindow(window);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        bounds = foundBounds;
        dpi = foundDpi;
        return bounds.Width > 0 && bounds.Height > 0;
    }

#if DEBUG
    static LockStateOverlay()
    {
        Rectangle bounds = CalculateBounds(new Rectangle(100, 200, 300, 600),
            (int)GetDpiForSystem());
        Debug.Assert(bounds == new Rectangle(205, 455, 90, 90));
        Rectangle imageBounds = CalculateImageBounds(
            new Rectangle(100, 200, 300, 600), (int)GetDpiForSystem(),
            new Size(100, 150));
        Debug.Assert(imageBounds == new Rectangle(205, 432, 90, 135));
    }
#endif

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
