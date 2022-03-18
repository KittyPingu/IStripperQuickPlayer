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
        internal string Name;
        internal string Height;
        internal decimal Bust;
        internal decimal Waist;
        internal decimal Hips;
        internal decimal Weight;
        internal string City;
        internal string Country;
        internal DateTime Birthdate;

        public ModelProperties(XmlNode? element)
        {
            Name = element.Attributes["na"].Value;
            Height = element.Attributes["heig"].Value;
            string meas = element.Attributes["stat"].Value;
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
            Weight = decimal.Parse(element.Attributes["weig"].Value);
            City = element.Attributes["city"].Value;
            Country = element.Attributes["cntry"].Value;
            DateTime.TryParse(element.Attributes["birth"].Value, out Birthdate);
        }
    }
}
