namespace IStripperQuickPlayer.WinUI.Core;

public sealed class ModelClip
{
    public string? ClipName { get; set; }

    public int Size { get; set; }

    public int ScCode { get; set; }

    public bool IsEnabled { get; set; }

    public HotnessCode HotnessCode { get; set; }

    public string? ClipType { get; set; }

    public int ClipNumber { get; set; }

    public int SizeMb => Size / 1024 / 1024;
}
