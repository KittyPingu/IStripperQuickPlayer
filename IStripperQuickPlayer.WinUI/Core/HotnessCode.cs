using System.ComponentModel;

namespace IStripperQuickPlayer.WinUI.Core;

public enum HotnessCode
{
    [Description("Public")]
    Public,

    [Description("No Nudity")]
    NoNudity,

    [Description("Topless")]
    Topless,

    [Description("Nudity")]
    Nudity,

    [Description("Full Nudity")]
    FullNudity,

    [Description("XXX")]
    Xxx
}
