using IStripperQuickPlayer.BLL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace IStripperQuickPlayer.DataModel
{
    internal class CardProperties2
    {
        internal DateTime daterel;

        public CardProperties2(XmlNode? element)
        {
            string d = element.SelectSingleNode("rd").FirstChild.Value;       
            string[] dele = d.Split("-");
            if (dele.Length > 2)
                daterel = new DateTime(int.Parse(dele[0]), int.Parse(dele[1]), int.Parse(dele[2].Split(" ")[0]));
            else
                daterel = new DateTime(2000,1,1);
        }
    }
}
