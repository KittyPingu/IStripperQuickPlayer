using System.ComponentModel;

namespace IStripperQuickPlayer.Controls;

/// <summary>
/// Lightweight, owner-drawn seek bar. Unlike the native TrackBar it does not
/// recreate or theme a child window whenever its anchored width changes.
/// </summary>
internal sealed class PlaybackSeekBar : Control, ISupportInitialize
{
    private const int ThumbDiameter = 18;
    private const int TrackHeight = 5;

    private int minimum;
    private int maximum = 1;
    private int value;
    private bool dragging;

    public PlaybackSeekBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.UserPaint,
            true);
        AccessibleName = "Playback position";
        AccessibleRole = AccessibleRole.Slider;
        TabStop = true;
        Size = new Size(100, 32);
    }

    [DefaultValue(0)]
    public int Minimum
    {
        get => minimum;
        set
        {
            if (minimum == value)
                return;

            minimum = value;
            if (maximum < minimum)
                maximum = minimum;
            Value = this.value;
            Invalidate();
        }
    }

    [DefaultValue(1)]
    public int Maximum
    {
        get => maximum;
        set
        {
            int adjusted = Math.Max(minimum, value);
            if (maximum == adjusted)
                return;

            maximum = adjusted;
            Value = this.value;
            Invalidate();
        }
    }

    [DefaultValue(0)]
    public int Value
    {
        get => value;
        set
        {
            int adjusted = Math.Clamp(value, minimum, maximum);
            if (this.value == adjusted)
                return;

            this.value = adjusted;
            InvalidateThumbArea();
            AccessibilityNotifyClients(
                AccessibleEvents.ValueChange, -1);
        }
    }

    [DefaultValue(1)]
    public int SmallChange { get; set; } = 1;

    [DefaultValue(10)]
    public int LargeChange { get; set; } = 10;

    public event EventHandler? Scroll;

    public void BeginInit()
    {
    }

    public void EndInit()
    {
        Value = value;
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new PlaybackSeekBarAccessibleObject(this);

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or
            Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End ||
        base.IsInputKey(keyData);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left)
        {
            Focus();
            Capture = true;
            dragging = true;
            SetValueFromX(e.X);
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Enabled && dragging)
            SetValueFromX(e.X);

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        dragging = false;
        base.OnMouseUp(e);
        Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        dragging = false;
        base.OnMouseCaptureChanged(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int requested = e.KeyCode switch
        {
            Keys.Left => value - Math.Max(1, SmallChange),
            Keys.Right => value + Math.Max(1, SmallChange),
            Keys.PageDown => value - Math.Max(1, LargeChange),
            Keys.PageUp => value + Math.Max(1, LargeChange),
            Keys.Home => minimum,
            Keys.End => maximum,
            _ => value
        };

        if (requested != value)
        {
            Value = requested;
            Scroll?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        dragging = false;
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Rectangle track = GetTrackRectangle();
        int thumbCenter = ValueToX(value);
        int activeWidth = Math.Max(0, thumbCenter - track.Left);
        Color inactiveColor = Enabled
            ? Color.FromArgb(210, 216, 220)
            : Color.FromArgb(105, 108, 110);
        Color activeColor = Enabled
            ? Color.FromArgb(0, 120, 215)
            : Color.FromArgb(115, 120, 124);

        using var inactiveBrush = new SolidBrush(inactiveColor);
        using var activeBrush = new SolidBrush(activeColor);
        e.Graphics.FillRectangle(inactiveBrush, track);
        if (activeWidth > 0)
            e.Graphics.FillRectangle(
                activeBrush,
                track.Left,
                track.Top,
                activeWidth,
                track.Height);

        e.Graphics.SmoothingMode =
            System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Rectangle thumb = new(
            thumbCenter - ThumbDiameter / 2,
            (Height - ThumbDiameter) / 2,
            ThumbDiameter,
            ThumbDiameter);
        e.Graphics.FillEllipse(activeBrush, thumb);

        if (Focused && ShowFocusCues)
        {
            Rectangle focus = ClientRectangle;
            focus.Inflate(-1, -1);
            ControlPaint.DrawFocusRectangle(e.Graphics, focus);
        }
    }

    private void SetValueFromX(int x)
    {
        Rectangle track = GetTrackRectangle();
        double fraction = Math.Clamp(
            (double)(x - track.Left) / Math.Max(1, track.Width),
            0,
            1);
        int requested = minimum +
            (int)Math.Round(fraction * (maximum - minimum));
        if (requested == value)
            return;

        Value = requested;
        Scroll?.Invoke(this, EventArgs.Empty);
    }

    private Rectangle GetTrackRectangle()
    {
        int left = ThumbDiameter / 2;
        int width = Math.Max(1, ClientSize.Width - ThumbDiameter);
        return new Rectangle(
            left,
            (ClientSize.Height - TrackHeight) / 2,
            width,
            TrackHeight);
    }

    private int ValueToX(int currentValue)
    {
        Rectangle track = GetTrackRectangle();
        if (maximum <= minimum)
            return track.Left;

        double fraction =
            (double)(currentValue - minimum) / (maximum - minimum);
        return track.Left + (int)Math.Round(fraction * track.Width);
    }

    private void InvalidateThumbArea()
    {
        // Repainting this small custom control is cheaper than maintaining the
        // previous thumb bounds, and avoids seams in the active track.
        Invalidate();
    }

    private sealed class PlaybackSeekBarAccessibleObject(
        PlaybackSeekBar owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.Slider;

        public override string? Value =>
            $"{owner.value} of {owner.maximum}";
    }
}
