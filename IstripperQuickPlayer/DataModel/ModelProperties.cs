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
    internal class ModelProperties
    {
        internal string Name="";
        internal string Height="";
        internal decimal Bust=30;
        internal decimal Waist=30;
        internal decimal Hips=30;
        internal decimal Weight;
        internal string City="";
        internal string Country="";
        internal DateTime Birthdate;

        public ModelProperties(XmlNode? element)
        {
            var style = NumberStyles.AllowDecimalPoint;
            var culture = CultureInfo.CreateSpecificCulture(CultureInfo.CurrentCulture.Name);
            culture.NumberFormat.NumberDecimalSeparator = ".";
            if (element == null) return;
            if (element.Attributes == null) return;
            
            Name = element.GetAttribute("id");;
            Height = element.GetAttribute("heig");
            string meas = element.GetAttribute("stat");
            if (!string.IsNullOrEmpty(meas))    
            {
                string[] measurements = meas.Split('/');
                if (measurements.Length > 2)
                {
                    decimal.TryParse(measurements[0], style, culture, out Bust);
                    decimal.TryParse(measurements[1], style, culture, out Waist);                    
                    decimal.TryParse(measurements[2], style, culture, out Hips);
                }
            }
            decimal.TryParse(element.GetAttribute("weig"), style, culture, out Weight);
            City = element.GetAttribute("city");;
            Country = element.GetAttribute("cntry");;
            DateTime.TryParse(element.GetAttribute("birth"), out Birthdate);

        }
    }
}
