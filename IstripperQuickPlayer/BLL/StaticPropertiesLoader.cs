using IStripperQuickPlayer.DataModel;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace IStripperQuickPlayer.BLL
{
    internal static class StaticPropertiesLoader
    {
        private static readonly HttpClient client = new();
        private static ILookup<string?, XmlNode>? cardsById;
        private static ILookup<string?, XmlNode>? modelsById;
        private static ILookup<string?, XmlNode>? modelsByName;
        private static ILookup<string?, XmlNode>? biosById;
        internal static void loadXML()
        {
            FileInfo? path = null;
            try
            {
            path = findXMLFile();
            if (path == null || string.IsNullOrEmpty(path.FullName)) 
            {
                MetadataDiagnostics.RecordSourceFailure(
                    "static properties", null);
                MessageBox.Show("could not find StaticProperties.xml file");
                return;
            }
            string fulltext = System.IO.File.ReadAllText(path.FullName);
            int start = fulltext.IndexOf('<');
            fulltext = fulltext.Substring(start);
            XmlDocument document = new();
            document.LoadXml(fulltext);
            var models = document.SelectNodes("/root/m")!.Cast<XmlNode>().ToArray();
            modelsById = models.ToLookup(node => node.Attributes?["id"]?.Value);
            modelsByName = models.ToLookup(node => node.Attributes?["na"]?.Value,
                StringComparer.OrdinalIgnoreCase);
            cardsById = document.SelectNodes("/root/c")!.Cast<XmlNode>()
                .ToLookup(node => node.Attributes?["id"]?.Value);
            biosById = document.SelectNodes("/root/d")!.Cast<XmlNode>()
                .ToLookup(node => node.Attributes?["id"]?.Value);
            }
            catch (Exception exception)
            {
                MetadataDiagnostics.RecordSourceFailure(
                    "static properties", path?.FullName, exception);
                throw;
            }
        }

        internal static ModelProperties? getModelByID(string ID)
        {
            XmlNode? node = getModelNodeByID(ID);
            return node == null ? null : new ModelProperties(node);
        }

        internal static ModelProperties? getModelByName(string? name)
        {
            XmlNode? node = getModelNodeByName(name);
            return node == null ? null : new ModelProperties(node);
        }

        internal static CardProperties? getCardByID(string ID)
        {
            XmlNode? node = getCardNodeByID(ID);
            return node == null ? null : new CardProperties(node);
        }

        internal static string? getCardXmlByID(string ID) =>
            getCardNodeByID(ID)?.OuterXml;

        internal static string? getModelXmlByID(string ID) =>
            getModelNodeByID(ID)?.OuterXml;

        internal static string? getModelXmlByName(string? name) =>
            getModelNodeByName(name)?.OuterXml;

        private static XmlNode? getCardNodeByID(string ID) =>
            cardsById?[ID.Split('-')[0]].FirstOrDefault();

        private static XmlNode? getModelNodeByID(string ID) =>
            modelsById?[ID].FirstOrDefault();

        private static XmlNode? getModelNodeByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            return modelsByName?[name].FirstOrDefault();
        }

        internal static BioProperties? getBioByID(string ID)
        {
            XmlNode? node = biosById?[ID].FirstOrDefault();
            return node == null ? null : new BioProperties(node);
        }

        private static FileInfo? findXMLFile()
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\System", false);
            string localapp = "";
            if (key != null)
            {
                var a = key.GetValue("DataPath", "");
                if (a != null)
                { 
                    localapp = a.ToString() ?? "";
                    key.Close();
                }
                else
                {
                    MessageBox.Show(@"Could not find registry key @CurrentUser\Software\Totem\vghd\System\DataPath ", "");
                }
                if (localapp == "")
                {
                    MessageBox.Show(@"Registry key @CurrentUser\Software\Totem\vghd\System\DataPath is empty?", "");
                }
            }
            else
            {
                MessageBox.Show(@"Could not find registry key @CurrentUser\Software\Totem\vghd\System", "");
            }
                       
            string fullpath = Path.Combine(localapp, "staticProperties loaded from server.xml");
            if (File.Exists(fullpath))
            {
                return new FileInfo(fullpath);
            }

                //we need to get it from the server
                string url = @"http://www.istripper.com/bof/mselistGenerator/staticProperties_iStripper.xml.gz";
                DownloadGZFile(url, fullpath);
                return new FileInfo(fullpath);
        }

        private static void DownloadGZFile(string url, string DecompressedFileName)
        {
            string path = Path.Join(Path.GetTempPath(), "staticProperties_iStripper.xml.gz");
            File.WriteAllBytes(path,
                client.GetByteArrayAsync(url).GetAwaiter().GetResult());
            using Stream inStream = File.OpenRead(path);
            using FileStream outputFileStream = File.Create(DecompressedFileName);
            using var decompressor = new GZipStream(inStream, CompressionMode.Decompress);
            decompressor.CopyTo(outputFileStream);
        }
    }
}
