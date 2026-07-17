using IStripperQuickPlayer.WinUI.Core;

namespace IStripperQuickPlayer.WinUI.ViewModels;

public sealed class ClipViewModel
{
    public ClipViewModel(ModelClip clip)
    {
        Clip = clip;
    }

    public ModelClip Clip { get; }

    public string Number => Clip.ClipNumber.ToString();

    public string Name => Clip.ClipName ?? string.Empty;

    public string Hotness => Clip.HotnessCode.ToString().ToLowerInvariant();

    public string Type => Clip.ClipType ?? string.Empty;

    public string Size => $"{Clip.SizeMb}MB";
}
