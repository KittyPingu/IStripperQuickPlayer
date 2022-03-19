using IStripperQuickPlayer.DataModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace IStripperQuickPlayer.BLL
{
    internal static class StaticPropertiesLoader
    {
        internal static XmlDocument PropertiesXML;
        internal static XmlNodeList cnode;
        internal static XmlNodeList mnode;

        internal static void loadXML()
        {
            string fulltext = System.IO.File.ReadAllText(findXMLFile().FullName);
            int start = fulltext.IndexOf('<');
            fulltext = fulltext.Substring(start);
            PropertiesXML = new XmlDocument();
            PropertiesXML.LoadXml(fulltext);
            mnode = PropertiesXML.SelectNodes("/root/m");
            cnode = PropertiesXML.SelectNodes("/root/c");
            //var m = XDocument.Parse(fulltext);
            //var tallest = m.Descendants("m").Where(c => c.Attribute("heig").Value != "").OrderByDescending(c =>  Convert.ToDecimal(c.Attribute("heig").Value)).Take(20);
            //var biggest = m.Descendants("m").Where(c => c.Attribute("stat").Value.Contains("/")).OrderByDescending(c =>  Convert.ToDecimal((c.Attribute("stat").Value.Split('/')[2]))).Take(20);
        }

        internal static ModelProperties getModelByID(string ID)
        {
            foreach(XmlNode n in mnode)
            {
               if (n.Attributes["id"].Value == ID)
                {
                     ModelProperties model = new ModelProperties(n); 
                    return model;
                }
            }
            return null;
           
        }

        internal static CardProperties getCardByID(string ID)
        {
            foreach(XmlNode n in cnode)
            {
               if (n.Attributes["id"].Value == ID)
               {
                     CardProperties card = new CardProperties(n);         
                    return card;
                }
            }
            return null;
        }

        private static FileInfo findXMLFile()
        {
            string localapp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string fullpath = Path.Combine(localapp, "vghd\\data\\staticProperties loaded from server.xml");
            if (File.Exists(fullpath))
            {
                return new FileInfo(fullpath);
            }
            else
                return null;
        }
    }
}
