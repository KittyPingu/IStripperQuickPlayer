using IStripperQuickPlayer.BLL;
using System;
using System.Collections.Generic;
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
        internal decimal Bust;
        internal decimal Waist;
        internal decimal Hips;
        internal decimal Weight;
        internal string City="";
        internal string Country="";
        internal DateTime Birthdate;

        public ModelProperties(XmlNode? element)
        {
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
                    Bust = decimal.Parse(measurements[0]);
                    Waist = decimal.Parse(measurements[1]);                    
                    Hips = decimal.Parse(measurements[2]);
                }
            }
            Weight = decimal.Parse(element.GetAttribute("weig"));
            City = element.GetAttribute("city");;
            Country = element.GetAttribute("cntry");;
            DateTime.TryParse(element.GetAttribute("birth"), out Birthdate);

        }
    }
}
