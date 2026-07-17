namespace IStripperQuickPlayer.WinUI.Core;

public sealed class ClipFilterSettings
{
    public bool Public { get; set; }

    public bool NoNudity { get; set; } = true;

    public bool Topless { get; set; } = true;

    public bool Nudity { get; set; } = true;

    public bool FullNudity { get; set; } = true;

    public bool Xxx { get; set; } = true;

    public bool Demo { get; set; }

    public string ClipTypeSearch { get; set; } = string.Empty;
}
