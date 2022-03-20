using IStripperQuickPlayer.BLL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace IStripperQuickPlayer.DataModel
{
    internal class CardProperties
    {
        internal string modelID;
        internal string hair;
        internal string[] tags;
        internal string date;
        internal string datesh;
        internal string ethnicity;
        internal bool exclusive;
        internal int numgirls=1;

        public CardProperties(XmlNode? element)
        {
            modelID = element.GetAttribute("mo");
            hair = element.GetAttribute("ha");
            tags = element.GetAttribute("ca").Split(",");
            date = element.GetAttribute("da");
            datesh = element.GetAttribute("dsh");
            switch (element.GetAttribute("na"))
            {
                case "Duo":
                    numgirls = 2;
                    break;
                default:
                    break;
            }
            if (tags.Contains("trio"))            
                numgirls = 3;
            if (tags.Contains("duo"))            
                numgirls = 3;
            ethnicity = element.GetAttribute("ty");
            exclusive = element.GetAttribute("exclusive") == "1";
        }
    }
}
