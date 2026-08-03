using IStripperQuickPlayer.BLL;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace IStripperQuickPlayer.DataModel
{
    internal class CardProperties2
    {
        internal DateTime daterel;
        internal bool HasReleaseDate;

        public CardProperties2(XmlNode? element)
        {
            string d = element?.SelectSingleNode("rd")?.InnerText ?? "";
            HasReleaseDate = DateTime.TryParseExact(
                d.Split(' ')[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out daterel) &&
                daterel.Year is >= 2007 and <= 2100;
            if (!HasReleaseDate)
                daterel = new DateTime(2007, 1, 1);
        }
    }
}
