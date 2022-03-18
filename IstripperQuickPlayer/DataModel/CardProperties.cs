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
            modelID = element.Attributes["mo"].Value;
            hair = element.Attributes["ha"].Value;
            tags = element.Attributes["ca"].Value.Split(",");
            date = element.Attributes["da"].Value;
            datesh = element.Attributes["dsh"].Value;
            switch (element.Attributes["na"].Value)
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
            ethnicity = element.Attributes["ty"].Value;
            exclusive = element.Attributes["exclusive"].Value == "1";
        }
    }
}
