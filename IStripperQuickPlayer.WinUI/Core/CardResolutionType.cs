using System.ComponentModel;

namespace IStripperQuickPlayer.WinUI.Core;

public enum CardResolutionType
{
    [Description("480p")]
    Lowest,

    [Description("720p")]
    Low,

    [Description("1080p")]
    Medium,

    [Description("3k")]
    High,

    [Description("4k")]
    Highest,

    [Description("Unknown")]
    Unknown
}
