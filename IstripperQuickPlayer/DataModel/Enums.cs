using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IStripperQuickPlayer.DataModel
{
    internal class Enums
    {
        internal enum CollectionType
        {
            [Description("iStripper Classic")]
            IStripperClassic,
            [Description("Virtua Guy")]
            VirtuaGuy,
            [Description("Desk Babes")]
            DeskBabes,
            [Description("VG Classic")]
            VGClassic,
            [Description("iStripper")]
            IStripper,
            [Description("iStripper XXX")]
            IStripperXXX,
            Undefined
        }

        internal enum CardResolutionType
        {
            [Description("480p")]
            lowest,
            [Description("720p")]
            low,
            [Description("1080p")]
            medium,
            [Description("3k")]
            high,
            [Description("4k")]
            highest,
            [Description("Unknown")]
            unknown
        }

        internal enum HotnessCode
        {
            [Description("Public")]
            publ,
            [Description("No Nudity")]
            nonudity,
            [Description("Topless")]
            topless,
            [Description("Nudity")]
            nudity,
            [Description("Full Nudity")]
            fullnudity,
            [Description("XXX")]
            xxx
        }
    }
}
