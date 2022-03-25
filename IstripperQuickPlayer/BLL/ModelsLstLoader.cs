using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using IStripperQuickPlayer.DataModel;
using static IStripperQuickPlayer.DataModel.Enums;
using System.Xml;
using Microsoft.Win32;
using System.Globalization;

namespace IStripperQuickPlayer.BLL
{
    internal class ModelsLstLoader
    {
        //load the models.lst file
        internal Int16 LoadModels()
        {
            Int16 modelsLoaded = -1;            
            //ImageList largeimagelist = new ImageList();
            //largeimagelist.ImageSize = new Size(130,180);
            //largeimagelist.ColorDepth = ColorDepth.Depth32Bit;
            FileInfo? file = findModelFile();
            var style = NumberStyles.AllowDecimalPoint;
            var culture = CultureInfo.CreateSpecificCulture(CultureInfo.CurrentCulture.Name);
            culture.NumberFormat.NumberDecimalSeparator = ".";
            if (file != null)
            {
                
                Form1? frm = Utils.GetMainForm();
                //frm.listModels.BeginUpdate();
                using (var stream = File.Open(file.FullName, FileMode.Open))
                {
                    using (var reader = new BinaryReader(stream, Encoding.UTF8, false))
                    {
                        Datastore.versionnumber = getInt32(reader);
                        Datastore.numberOfCards = getInt32(reader);

                        char fc = '\u0092';  
                        char nc = '\'';

                        for (int c = 0; c < Datastore.numberOfCards; c++)
                        {
                            ModelCard card = new ModelCard();
                            int cardTextLen = getInt32(reader);
                            card.name = getString(reader, cardTextLen).Replace(fc,nc);
                            card.datePurchased = getDate(reader);
                            card.collection = getCollectionType(card.name);
                            card.a = reader.ReadByte();
                            card.b = reader.ReadByte();

                            byte codebyte = reader.ReadByte();
                            card.flag10 = Convert.ToBoolean(codebyte & 1);
                            card.flag11 =  Convert.ToBoolean(codebyte & 2);
                            card.cardHidden = Convert.ToBoolean(codebyte & 4);
                            card.flag13 = Convert.ToBoolean(codebyte & 8);
                            card.flag14 = Convert.ToBoolean(codebyte & 16);
                            card.cardDownloaded = Convert.ToBoolean(codebyte & 32);
                            card.inCollection = Convert.ToBoolean(codebyte & 64);
                            card.updateAvailable = Convert.ToBoolean(codebyte & 128);

                            
                            codebyte = reader.ReadByte();
                            card.flag20 = Convert.ToBoolean(codebyte & 1);
                            card.flag21 =  Convert.ToBoolean(codebyte & 2);
                            card.isNew = Convert.ToBoolean(codebyte & 4);
                            card.specialSelection = Convert.ToBoolean(codebyte & 8);
                            card.cardDownloaded2 = Convert.ToBoolean(codebyte & 16);
                            card.flag25 = Convert.ToBoolean(codebyte & 32);
                            card.flag26 = Convert.ToBoolean(codebyte & 64);
                            card.cardEnabled = Convert.ToBoolean(codebyte & 128);

                            card.c = reader.ReadByte();
                            card.d = reader.ReadByte();
                            card.e = reader.ReadByte();
                            card.f = reader.ReadByte();
                            card.folderSize = getInt32(reader);
                            card.g = reader.ReadByte();
                            card.h = reader.ReadByte();
                            card.i = reader.ReadByte();
                            card.j = reader.ReadByte();
                            card.k = reader.ReadByte();
                            card.l = reader.ReadByte();
                            card.m = reader.ReadByte();
                            card.n = reader.ReadByte();
                            card.o = reader.ReadByte();
                            card.timesPlayed = getInt32(reader);
                            int cardrescode = getInt32(reader);
                            card.resolution = getResolution(cardrescode); 
                            int bestrescode = getInt32(reader);
                            card.bestResolution = getBestResolution(bestrescode);
                            int newresavailable = getInt32(reader);
                            card.p = reader.ReadByte();
                            card.q = reader.ReadByte();
                            card.r = reader.ReadByte();
                            card.s = reader.ReadByte();

                            card.XML = loadCardXML(card.name);
                            if (card.XML != null)
                            {
                                card.description = getXMLValue(card, "description");
                                card.outfit = getXMLValue(card, "outfit").Replace(fc,nc);
                                card.hair =  getXMLValue(card, "hair");
                                var d = 0M;
                                Decimal.TryParse(getXMLValue(card, "rate"), style, culture, out d);
                                card.rating=d;
                                card.hotnessLevel =  getXMLValue(card, "level");
                                int fcnt = 0;
                                int.TryParse(getXMLValue(card, "duration"), out fcnt);
                                card.frameCount = fcnt;
                                card.xmlSize = getXMLValue(card, "size");
                                card.modelName = getXMLValue(card, "name");
                                decimal.TryParse(getXMLValue(card, "age"), style, culture,  out card.modelAge);
                                if (card.modelAge > 50) card.modelAge = 50;
                                card.image = loadCardImage(card);
                            }

                            //read static properties for model
                            var cardProp = StaticPropertiesLoader.getCardByID(card.name);
                            if (cardProp != null)
                            {
                                card.tags = cardProp.tags;
                                card.ethnicity = cardProp.ethnicity;
                                card.exclusive = cardProp.exclusive;
                                card.numgirls = cardProp.numgirls;
                                string modelID = cardProp.modelID;
                                if (cardProp.modelID.Contains(","))
                                    modelID = cardProp.modelID.Split(",")[0];

                                var mProp = StaticPropertiesLoader.getModelByID(modelID);
                                if (mProp != null)
                                {
                                    card.bust = mProp.Bust;
                                    card.waist = mProp.Waist;
                                    card.hips = mProp.Hips;
                                    card.height = mProp.Height;
                                    card.city = mProp.City;
                                    card.country = mProp.Country;
                                    card.birthdate = mProp.Birthdate;
                                }
                            }
                           
                            //loop through clips
                            int clipCount = getInt32(reader);
                            for (int i = 0; i < clipCount; i++)
                            {
                                ModelClip clip = new ModelClip();
                                int s = getInt32(reader);
                                clip.clipNumber = i;
                                clip.clipName = getStringUnicode(reader, s);
                                int sequence = getInt32(reader);
                                int transitionType = getInt32(reader);
                                clip.hotnessCode = (HotnessCode)getInt32(reader);

                                byte byte4 = reader.ReadByte();
                                codebyte = reader.ReadByte();  
                                byte byte3 = codebyte;
                                bool glass = Convert.ToBoolean(codebyte & 1);
                                codebyte = reader.ReadByte();
                                byte byte2 = codebyte;
                                bool isSC = Convert.ToBoolean(codebyte & 1);
                                bool cageClip = Convert.ToBoolean(codebyte & 2);
                                bool onTop = Convert.ToBoolean(codebyte & 4);
                                bool dryStart = Convert.ToBoolean(codebyte & 8);
                                bool deadEnd = Convert.ToBoolean(codebyte & 16);
                                bool magicStart = Convert.ToBoolean(codebyte & 32);
                                bool magicEnd = Convert.ToBoolean(codebyte & 64);
                                bool nudeStart = Convert.ToBoolean(codebyte & 128);
                                codebyte = reader.ReadByte();
                                byte byte1 = codebyte;
                                bool onBar = Convert.ToBoolean(codebyte & 1);
                                bool behindBar = Convert.ToBoolean(codebyte & 2);
                                bool poleDance = Convert.ToBoolean(codebyte & 4);
                                bool fullLegs = Convert.ToBoolean(codebyte & 8);
                                bool withSign = Convert.ToBoolean(codebyte & 16);
                                bool withProp = Convert.ToBoolean(codebyte & 32);
                                bool fromSide = Convert.ToBoolean(codebyte & 64);
                                clip.size = getInt32(reader);
                                clip.scCode = getInt32(reader);
                                clip.isEnabled = Convert.ToBoolean(getInt32(reader));
                                clip.clipType = "";
                                bool standingClip = !(cageClip || onTop || onBar || behindBar); 
                                if (standingClip) clip.clipType += ", Standing";
                                if (cageClip) clip.clipType += ", Cage";
                                if (onBar) clip.clipType += ", Table";
                                if (behindBar) clip.clipType += ", BehindTable";
                                if (onTop) clip.clipType += ", Swing";
                                if (poleDance) clip.clipType += ", Pole";
                                if (glass) clip.clipType += ", Glass";
                                if (withSign) clip.clipType += ", With Sign";
                                if (withProp) clip.clipType += ", With Prop";
                                if (fullLegs) clip.clipType += ", Full Legs";
                                if (fromSide) clip.clipType += ", From Side";
                                if (clip.clipType != "") clip.clipType = clip.clipType.Substring(2);
                                //skip an unused int
                                int unused = getInt32(reader);
                                if (card.clips != null) card.clips.Add(clip);
                            }
                            if (card.clips != null && card.clips.Count > 0) 
                            {
                                if (Datastore.modelcards != null)
                                { 
                                    Datastore.modelcards.Add(card);                            
                                    if (frm != null) frm.lblModelsLoaded.Text = "Models Loaded: " + Datastore.modelcards.Count;
                                    Application.DoEvents();
                                }
                            }
                            
                        }
                    }
                }
                if (frm != null) frm.listModels.EndUpdate();
            }
            
            return modelsLoaded;
        }

        private Image? loadCardImage(ModelCard card) {   

            Image? image = null;
            string localapp = getDataFolderPath();
            string fullpath = Path.Combine(localapp, card.name, card.name + ".jpg");
            card.imagefile = fullpath;
            try
            {
                image = Image.FromFile(fullpath);
            }
            catch (Exception)
            {
                //missing an image?
            }
            return image;
        }

        private string getXMLValue(ModelCard card, string variable)
        {
            try
            {
                if (card.XML == null) return "";
                var elements = card.XML.GetElementsByTagName(variable);
                if (elements == null || elements.Count == 0 || elements[0] == null) return "";
                var e = elements[0];
                if (e == null) return "";
                return e.InnerText;
            }
            catch (Exception)
            {

                return "";
            }

            
        }

        private XmlDocument loadCardXML(string cardnumber)
        {
            string localapp = getDataFolderPath();
            string fullpath = Path.Combine(localapp, cardnumber, cardnumber + ".xml");
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(fullpath);
                return doc;
            }
            catch (Exception)
            {
                Console.WriteLine("no cardXML for " + cardnumber);
            }
            return new XmlDocument();           
        }

        private CardResolutionType getResolution(int cardrescode)
        {
            switch (cardrescode)
            {
                case 1:
                    return CardResolutionType.lowest;
                case 2:
                    return CardResolutionType.low;
                case 3:
                    return CardResolutionType.medium;
                case 4:
                    return CardResolutionType.high;
                case 5:
                    return CardResolutionType.highest;
                default:
                    return CardResolutionType.unknown;
                  
            }
        }

        private CardResolutionType getBestResolution(int cardrescode)
        {
            switch (cardrescode)
            {
                case >=16:
                    return CardResolutionType.highest;
                case >=8:
                    return CardResolutionType.high;
                case >=4:
                    return CardResolutionType.medium;
                case >=2:
                    return CardResolutionType.low;
                case 1:
                    return CardResolutionType.lowest;
                default:
                    return CardResolutionType.unknown;
                  
            }
        }

        private CollectionType getCollectionType(string cardnumber)
        {
            char b = cardnumber[0];
            switch (b)
            {
                case 'a':
                    return CollectionType.IStripperClassic;
                case 'b':
                    return CollectionType.VirtuaGuy;
                case 'c':
                   return CollectionType.DeskBabes;
                case 'd':
                    return CollectionType.VGClassic;
                case 'e':  
                    return CollectionType.IStripper;
                case 'f':
                    return CollectionType.IStripperXXX;
                default:
                    return CollectionType.Undefined;
            }
        }

        private string getString(BinaryReader reader, int strlen)
        {
            byte[] b = reader.ReadBytes(strlen);
            return System.Text.Encoding.Default.GetString(b);
        }
               
        private string getStringUnicode(BinaryReader reader, int strlen)
        {
            byte[] b = reader.ReadBytes(strlen);
            return System.Text.Encoding.Default.GetString(b.Where((x, i) => i % 2== 1).ToArray());
        }
        private int getInt32(BinaryReader reader)
        {
            byte[] b = reader.ReadBytes(4);
            return b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3];
        }

        private DateTime getDate(BinaryReader reader)
        {
            //string_position = string_position + 2 ' ignore the first 2 bytes because VB doesn't handle dates BC
            //date_offset = Asc(Mid(source_string, string_position, 1)) 'Get #1, string_position, my_byte
            //string_position = string_position + 1
            //date_offset = 256 * date_offset + Asc(Mid(source_string, string_position, 1))  'Get #1, string_position, my_byte
            //string_position = string_position + 1
            //date_from_binary = #11/13/1926# + date_offset
            byte[] b = reader.ReadBytes(4);

            DateTime dt = new DateTime(1926,11,13);
            return dt.AddDays(b[2] << 8 | b[3]);
        }

        //find the models.lst file
        private FileInfo? findModelFile()
        {
            string localapp = getDataFolderPath();
            string fullpath = Path.Combine(localapp, "models.lst");
            if (File.Exists(fullpath))
            {
                return new FileInfo(fullpath);
            }
            else
                return null;
        }

        private string getDataFolderPath()
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
            }
            return localapp;
        }
    }
}
