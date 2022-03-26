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
            {
                int year = 2007;
                int month = 1;
                int day = 1;
                int.TryParse(dele[0], out year);
                if (year < 2007 || year > 2100) year = 2007;
                int.TryParse(dele[1], out month);
                if (month < 1) month = 1;
                if (month > 12) month = 12;
                int.TryParse(dele[2].Split(" ")[0], out day);
                if (day < 1 || day > 31) day = 1;

                daterel = new DateTime(year, month, day);
            }
            else
                daterel = new DateTime(2007,1,1);
        }
    }
}
