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
        internal string clipName;
        internal int size;
        internal int scCode;
        internal bool isEnabled;
        internal HotnessCode hotnessCode;
        internal string clipType;
        internal int clipNumber;
        internal bool isFavourite;
        internal decimal myRating;
    }
}
