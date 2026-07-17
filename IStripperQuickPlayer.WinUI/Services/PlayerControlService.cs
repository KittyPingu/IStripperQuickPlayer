using Microsoft.Win32;
using IStripperQuickPlayer.WinUI.Core;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class PlayerControlService
{
    private const string ParametersKeyPath = @"Software\Totem\vghd\parameters";

    public string? CurrentAnimation
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ParametersKeyPath, false);
            return key?.GetValue("CurrentAnim", string.Empty)?.ToString();
        }
    }

    public void PlayClip(ModelClip clip)
    {
        if (string.IsNullOrWhiteSpace(clip.ClipName))
        {
            return;
        }

        string folder = clip.ClipName.Split("_")[0];
        ForceAnimation($@"{folder}\{clip.ClipName}");
    }

    public void PlayNextClip(ModelCard? card, IReadOnlyList<ModelClip> filteredClips)
    {
        if (card == null || filteredClips.Count == 0)
        {
            return;
        }

        string? currentAnimation = CurrentAnimation;
        ModelClip? nextClip = null;

        if (string.IsNullOrWhiteSpace(currentAnimation) || !currentAnimation.StartsWith(card.Name, StringComparison.OrdinalIgnoreCase))
        {
            nextClip = filteredClips[Random.Shared.Next(filteredClips.Count)];
        }
        else
        {
            string currentClipName = currentAnimation.Split('\\').LastOrDefault() ?? string.Empty;
            ModelClip? currentClip = filteredClips.FirstOrDefault(x => x.ClipName == currentClipName);
            nextClip = currentClip == null
                ? filteredClips.FirstOrDefault()
                : filteredClips.FirstOrDefault(x => x.ClipNumber > currentClip.ClipNumber) ?? filteredClips.FirstOrDefault();
        }

        if (nextClip != null)
        {
            PlayClip(nextClip);
        }
    }

    public void ForceAnimation(string animation)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ParametersKeyPath, true);
        key?.SetValue("ForceAnim", animation);
    }
}
