namespace IStripperQuickPlayer.WinUI.Controls;

internal static class RatingHitTest
{
    internal static decimal FromPointerX(double pointerX, double starBoundsWidth)
    {
        if (starBoundsWidth <= 0)
        {
            return 0;
        }

        double halfStars = Math.Round((pointerX / starBoundsWidth) * 10.0, MidpointRounding.AwayFromZero);
        halfStars = Math.Max(0, Math.Min(10, halfStars));

        return (decimal)halfStars;
    }
}
