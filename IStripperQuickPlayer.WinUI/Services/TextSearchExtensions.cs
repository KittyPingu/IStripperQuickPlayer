namespace IStripperQuickPlayer.WinUI.Services;

internal static class TextSearchExtensions
{
    internal static bool ContainsWithNot(this string? text, string find)
    {
        text ??= string.Empty;

        if (find.StartsWith('!'))
        {
            return !text.Contains(find.Replace("!", string.Empty).Trim(), StringComparison.CurrentCultureIgnoreCase);
        }

        return text.Contains(find, StringComparison.CurrentCultureIgnoreCase);
    }
}
