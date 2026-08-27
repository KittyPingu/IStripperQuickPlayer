using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static IStripperQuickPlayer.DataModel.Enums;

namespace IStripperQuickPlayer
{
    [Serializable]
    internal class ModelClip
    {
        internal string? clipName;
        internal long? size;
        internal int? scCode;
        internal bool? isEnabled;
        internal HotnessCode? hotnessCode;
        internal string? clipType;
        internal int? clipNumber;
        internal string? customForegroundPath;
        internal string? customAlphaPath;
        internal string customMediaMode = CustomClipMedia.PairedAlphaMode;
        internal CustomNvidiaSettings? customNvidiaSettings;
        internal CustomRvmOnnxSettings? customRvmOnnxSettings;
        internal int customAlphaThreshold = CustomShowClip.DefaultAlphaThreshold;
        internal float customEdgeChokePixels = 1;
        internal CustomVirtualGreenScreen customVirtualGreenScreen =
            new() { Enabled = false };
        internal long customStartMs;
        internal long customEndMs;
    }
}
