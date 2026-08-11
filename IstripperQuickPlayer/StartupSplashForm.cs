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
        PositionProgress();
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

    private void PositionProgress()
    {
        Rectangle content = ContentBounds;
        progress.Location = new Point(
            content.Left + 36, content.Top + 205);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionProgress();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle content = ContentBounds;
        using LinearGradientBrush background = new(
            content,
            Color.FromArgb(40, 42, 48),
            Color.FromArgb(20, 21, 24),
            LinearGradientMode.Vertical);
        e.Graphics.Clear(Color.FromArgb(26, 27, 30));
        e.Graphics.FillRectangle(background, content);

        using Pen border = new(Color.FromArgb(85, 196, 255), 2);
        e.Graphics.DrawRectangle(
            border, content.Left + 1, content.Top + 1,
            content.Width - 3, content.Height - 3);
        e.Graphics.DrawIcon(Icon, new Rectangle(
            content.Left + 36, content.Top + 42, 64, 64));

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            coverAnimation?.Dispose();
            closeAnimation?.Dispose();
            titleFont.Dispose();
            statusFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
