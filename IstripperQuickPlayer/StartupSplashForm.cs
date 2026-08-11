using System.Drawing.Drawing2D;
using System.Diagnostics;

namespace IStripperQuickPlayer;

internal sealed class StartupSplashController : IDisposable
{
    private readonly ManualResetEventSlim created = new(false);
    private Thread? thread;
    private volatile StartupSplashForm? form;

    private StartupSplashController()
    {
    }

    internal static StartupSplashController Start()
    {
        StartupSplashController controller = new();
        controller.thread = new Thread(controller.Run)
        {
            IsBackground = true,
            Name = "QuickPlayer startup splash"
        };
        controller.thread.SetApartmentState(ApartmentState.STA);
        controller.thread.Start();
        controller.created.Wait(TimeSpan.FromSeconds(3));
        return controller;
    }

    private void Run()
    {
        try
        {
            using StartupSplashForm splash = new();
            form = splash;
            splash.HandleCreated += (_, _) => created.Set();
            Application.Run(splash);
        }
        finally
        {
            form = null;
            created.Set();
        }
    }

    internal void Close()
    {
        StartupSplashForm? splash = form;
        if (splash == null || splash.IsDisposed)
            return;
        try
        {
            if (splash.InvokeRequired)
                splash.BeginInvoke((Action)splash.AnimateClose);
            else
                splash.AnimateClose();
        }
        catch (InvalidOperationException)
        {
            // The splash completed while the close request was queued.
        }
    }

    internal void Cover(Rectangle bounds)
    {
        StartupSplashForm? splash = form;
        if (splash == null || splash.IsDisposed)
            return;
        try
        {
            using ManualResetEventSlim completed = new(false);
            void BeginCover()
            {
                splash.AnimateCover(bounds, () =>
                {
                    try
                    {
                        completed.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
            }

            if (splash.InvokeRequired)
                splash.BeginInvoke((Action)BeginCover);
            else
                BeginCover();
            completed.Wait(TimeSpan.FromSeconds(2));
        }
        catch (InvalidOperationException)
        {
            // The splash completed while the cover request was queued.
        }
    }

    public void Dispose()
    {
        Close();
        if (thread is { IsAlive: true } &&
            Thread.CurrentThread != thread)
            thread.Join(TimeSpan.FromSeconds(2));
        created.Dispose();
    }
}

internal sealed class StartupSplashForm : Form
{
    private const int DesignWidth = 560;
    private const int DesignHeight = 238;
    private static readonly (byte Y, byte X, byte Width)[] DancerMaskRuns =
    [
        (6,27,2), (7,26,3), (8,26,2), (9,25,2), (10,24,3),
        (11,24,3), (11,29,4), (12,24,2), (12,28,6),
        (13,23,3), (13,27,8), (14,23,3), (14,27,9),
        (15,23,3), (15,27,9), (16,24,13), (17,24,3),
        (17,28,10), (18,24,4), (18,29,11), (19,25,3),
        (19,29,11), (20,25,7), (20,33,8), (21,26,16),
        (22,25,18), (23,25,18), (24,25,13), (24,39,4),
        (25,26,7), (25,35,4), (25,40,1), (26,27,5),
        (26,36,3), (26,40,1), (27,27,5), (27,36,3),
        (28,27,6), (28,35,3), (29,27,10), (30,27,9),
        (31,27,9), (32,27,10), (33,26,11), (34,26,11),
        (35,26,11), (36,25,6), (36,32,5), (37,25,6),
        (37,32,5), (38,25,5), (38,32,5), (39,24,5),
        (39,32,5), (40,24,5), (40,33,4), (41,24,4),
        (41,33,4), (42,24,4), (42,33,5), (43,23,5),
        (43,34,5), (44,23,5), (44,34,5), (45,23,5),
        (45,34,6), (46,23,5), (46,35,5), (47,23,5),
        (47,35,6), (48,23,6), (48,35,6), (49,23,6),
        (49,35,7), (50,23,6), (50,36,6), (51,22,7),
        (51,36,6), (52,22,7), (52,36,6), (53,22,7),
        (53,36,6), (54,21,7), (54,37,5), (55,20,8),
        (55,37,5), (56,20,5), (56,37,4)
    ];
    private readonly Font titleFont =
        new("Segoe UI", 19, FontStyle.Regular);
    private readonly Font statusFont =
        new("Segoe UI", 11, FontStyle.Regular);
    private readonly ProgressBar progress = new()
    {
        Style = ProgressBarStyle.Marquee,
        MarqueeAnimationSpeed = 24,
        Size = new Size(488, 5),
        Location = new Point(36, 205)
    };
    private readonly Stopwatch danceClock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer danceAnimation = new()
    {
        Interval = 40
    };
    private Rectangle iconBounds;
    private System.Windows.Forms.Timer? coverAnimation;
    private System.Windows.Forms.Timer? closeAnimation;
    private bool closing;

    internal StartupSplashForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(26, 27, 30);
        ClientSize = new Size(DesignWidth, DesignHeight);
        ControlBox = false;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        Icon = Properties.Resources.df2284943cc77e7e1a5fa6a0da8ca265;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "iStripper QuickPlayer";
        TopMost = true;
        Controls.Add(progress);
        danceAnimation.Tick += (_, _) => AdvanceDance();
        danceAnimation.Start();
        PositionContentControls();
    }

    private Rectangle ContentBounds => new(
        Math.Max(0, (ClientSize.Width - DesignWidth) / 2),
        Math.Max(0, (ClientSize.Height - DesignHeight) / 2),
        Math.Min(DesignWidth, ClientSize.Width),
        Math.Min(DesignHeight, ClientSize.Height));

    internal void AnimateCover(Rectangle target, Action completed)
    {
        coverAnimation?.Stop();
        coverAnimation?.Dispose();
        StartPosition = FormStartPosition.Manual;
        Opacity = 0;
        Bounds = target;
        BringToFront();
        Refresh();

        Stopwatch elapsed = Stopwatch.StartNew();
        coverAnimation = new System.Windows.Forms.Timer
        {
            Interval = 15
        };
        coverAnimation.Tick += (_, _) =>
        {
            double progress = Math.Min(
                1, elapsed.Elapsed.TotalMilliseconds / 160);
            double eased = 1 - Math.Pow(1 - progress, 3);
            Opacity = eased;
            if (progress < 1)
                return;

            coverAnimation.Stop();
            coverAnimation.Dispose();
            coverAnimation = null;
            Opacity = 1;
            Refresh();
            completed();
        };
        coverAnimation.Start();
    }

    internal void AnimateClose()
    {
        if (closing || IsDisposed)
            return;
        closing = true;
        progress.Style = ProgressBarStyle.Continuous;
        progress.Value = progress.Maximum;
        Stopwatch elapsed = Stopwatch.StartNew();
        closeAnimation = new System.Windows.Forms.Timer
        {
            Interval = 15
        };
        closeAnimation.Tick += (_, _) =>
        {
            double progress = Math.Min(
                1, elapsed.Elapsed.TotalMilliseconds / 320);
            double eased = progress * progress * (3 - 2 * progress);
            Opacity = 1 - eased;
            if (progress < 1)
                return;

            closeAnimation.Stop();
            closeAnimation.Dispose();
            closeAnimation = null;
            Close();
        };
        closeAnimation.Start();
    }

    private void PositionContentControls()
    {
        Rectangle content = ContentBounds;
        iconBounds = new Rectangle(
            content.Left + 36, content.Top + 42, 64, 64);
        progress.Location = new Point(
            content.Left + 36, content.Top + 205);
    }

    private void AdvanceDance()
    {
        Rectangle dirty = iconBounds;
        dirty.Inflate(2, 2);
        Invalidate(dirty);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionContentControls();
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Rectangle content = ContentBounds;
        using LinearGradientBrush background = new(
            content,
            Color.FromArgb(40, 42, 48),
            Color.FromArgb(20, 21, 24),
            LinearGradientMode.Vertical);
        e.Graphics.Clear(Color.FromArgb(26, 27, 30));
        e.Graphics.FillRectangle(background, content);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle content = ContentBounds;

        using Pen border = new(Color.FromArgb(85, 196, 255), 2);
        e.Graphics.DrawRectangle(
            border, content.Left + 1, content.Top + 1,
            content.Width - 3, content.Height - 3);
        DrawDancingIcon(e.Graphics);
        TextRenderer.DrawText(
            e.Graphics,
            "iStripper QuickPlayer",
            titleFont,
            new Rectangle(content.Left + 118, content.Top + 42,
                content.Width - 146, 48),
            Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            e.Graphics,
            "Loading card library…",
            statusFont,
            new Rectangle(content.Left + 120, content.Top + 96,
                content.Width - 150, 30),
            Color.FromArgb(190, 200, 210),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);
    }

    private void DrawDancingIcon(Graphics graphics)
    {
        graphics.DrawIcon(Icon, iconBounds);
        using GraphicsPath silhouette = CreateDancerPath();

        graphics.SmoothingMode = SmoothingMode.None;
        using SolidBrush erase = new(Color.FromArgb(255, 24, 48, 58));
        using Pen eraseEdge = new(erase.Color, 1.2f);
        graphics.FillPath(erase, silhouette);
        graphics.DrawPath(eraseEdge, silhouette);

        double phase = danceClock.Elapsed.TotalSeconds * 4.4;
        float beat = (float)Math.Sin(phase);
        float hipShift = beat * 2.4f;
        float shoulderShift = -beat * 1.35f +
            (float)Math.Sin(phase * 2) * .35f;
        float bounce = -(float)Math.Abs(beat) * .55f;
        PointF Deform(PointF point)
        {
            const float shoulderY = 20;
            const float hipY = 38;
            const float footY = 56;
            float shift;
            if (point.Y <= shoulderY)
            {
                shift = shoulderShift;
            }
            else if (point.Y <= hipY)
            {
                float amount = (point.Y - shoulderY) /
                    (hipY - shoulderY);
                amount = amount * amount * (3 - 2 * amount);
                shift = shoulderShift +
                    (hipShift - shoulderShift) * amount;
            }
            else
            {
                float amount = Math.Clamp(
                    (footY - point.Y) / (footY - hipY), 0, 1);
                shift = hipShift * amount;
            }

            float verticalInfluence = Math.Clamp(
                (footY - point.Y) / (footY - 6), 0, 1);
            return new PointF(
                point.X + shift,
                point.Y + bounce * verticalInfluence);
        }

        using GraphicsPath animatedSilhouette =
            CreateDancerPath(Deform);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using SolidBrush dancer = new(Color.WhiteSmoke);
        graphics.FillPath(dancer, animatedSilhouette);
    }

    private GraphicsPath CreateDancerPath(
        Func<PointF, PointF>? deform = null)
    {
        float scaleX = iconBounds.Width / 64f;
        float scaleY = iconBounds.Height / 64f;
        PointF Map(float x, float y)
        {
            PointF point = deform?.Invoke(new PointF(x, y)) ??
                new PointF(x, y);
            return new PointF(
                iconBounds.Left + point.X * scaleX,
                iconBounds.Top + point.Y * scaleY);
        }

        GraphicsPath path = new(FillMode.Winding);
        foreach ((byte y, byte x, byte width) in DancerMaskRuns)
        {
            path.AddPolygon(
            [
                Map(x, y),
                Map(x + width, y),
                Map(x + width, y + 1),
                Map(x, y + 1)
            ]);
        }
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            danceAnimation.Stop();
            danceAnimation.Dispose();
            coverAnimation?.Dispose();
            closeAnimation?.Dispose();
            titleFont.Dispose();
            statusFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
