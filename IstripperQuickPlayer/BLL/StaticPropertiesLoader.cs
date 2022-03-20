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
        internal static XmlDocument PropertiesXML = new XmlDocument();
        internal static XmlNodeList? cnode;
        internal static XmlNodeList? mnode;

        internal static void loadXML()
        {
            var path = findXMLFile();
            if (path == null) return;
            string fulltext = System.IO.File.ReadAllText(path.FullName);
            int start = fulltext.IndexOf('<');
            fulltext = fulltext.Substring(start);
            PropertiesXML = new XmlDocument();
            PropertiesXML.LoadXml(fulltext);
            if (PropertiesXML != null && PropertiesXML.ChildNodes != null)
            {
#pragma warning disable CS8601 // Possible null reference assignment.
                mnode = PropertiesXML.SelectNodes("/root/m");
#pragma warning restore CS8601 // Possible null reference assignment.
#pragma warning disable CS8601 // Possible null reference assignment.
                cnode = PropertiesXML.SelectNodes("/root/c");
#pragma warning restore CS8601 // Possible null reference assignment.
            }
        }

        internal static ModelProperties? getModelByID(string ID)
        {
            if (mnode == null) return null;
            foreach(XmlNode n in mnode)
            {
                if (n.Attributes != null)
                {
                var attribute = n.Attributes["id"];
                if (attribute != null && attribute.Value == ID)
                {
                    ModelProperties model = new ModelProperties(n); 
                    return model;
                }
                }
            }
            return null;
           
        }

        internal static CardProperties? getCardByID(string ID)
        {
            if (cnode == null) return null;
            foreach(XmlNode n in cnode)
            {
                if (n.Attributes != null)
                {
                    var attribute = n.Attributes["id"];
                    if (attribute != null && attribute.Value == ID)
                    {
                         CardProperties card = new CardProperties(n);         
                        return card;
                    }
                }
            }
            return null;
        }

        private static FileInfo? findXMLFile()
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
