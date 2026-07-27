namespace IStripperQuickPlayer;

internal sealed class BufferedLabel : Label
{
    private string displayText = "";

    internal BufferedLabel()
    {
        SetStyle(ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.SupportsTransparentBackColor, true);
    }

    internal string GetDisplayText() => displayText;

    internal void SetDisplayText(string value)
    {
        if (displayText == value)
            return;

        ResizeForText(value);
        displayText = value;
        AccessibleName = value;
        Invalidate();
    }

    private void ResizeForText(string value)
    {
        Size preferredSize = new(
            TextRenderer.MeasureText(
                value, Font, Size.Empty, TextFormatFlags.NoPadding).Width + 2,
            Font.Height + 6);
        if (MinimumSize != preferredSize)
        {
            MinimumSize = preferredSize;
            Size = preferredSize;
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (displayText.Length > 0)
            ResizeForText(displayText);
    }

    protected override void OnPaint(PaintEventArgs e) =>
        TextRenderer.DrawText(
            e.Graphics, displayText, Font, ClientRectangle, ForeColor,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix);
}
