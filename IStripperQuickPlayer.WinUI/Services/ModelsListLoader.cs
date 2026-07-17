using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using IStripperQuickPlayer.WinUI.Core;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class ModelsListLoader
{
    private readonly IstripperPaths _paths;
    private readonly PropertiesCatalog _propertiesCatalog;

    private readonly NumberStyles _style = NumberStyles.AllowDecimalPoint;
    private readonly CultureInfo _culture = CultureInfo.CreateSpecificCulture(CultureInfo.CurrentCulture.Name);

    public ModelsListLoader(IstripperPaths paths, PropertiesCatalog propertiesCatalog)
    {
        _paths = paths;
        _propertiesCatalog = propertiesCatalog;
        _culture.NumberFormat.NumberDecimalSeparator = ".";
    }

    public async Task<IReadOnlyList<ModelCard>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _propertiesCatalog.LoadAsync(cancellationToken);

        if (!File.Exists(_paths.ModelsListPath))
        {
            throw new FileNotFoundException("Could not find models.lst.", _paths.ModelsListPath);
        }

        List<ModelCard> cards = [];
        await using FileStream stream = File.Open(_paths.ModelsListPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using BinaryReader reader = new(stream, Encoding.UTF8, false);

        int versionNumber = ReadInt32(reader);
        int numberOfCards = ReadInt32(reader);

        ReadCards(reader, cards, numberOfCards, versionNumber, false);

        int marketCards = ReadInt32(reader);
        if (marketCards > 0)
        {
            ReadCards(reader, cards, marketCards, versionNumber, true);
        }

        return cards;
    }

    private void ReadCards(BinaryReader reader, List<ModelCard> cards, int count, int versionNumber, bool marketCards)
    {
        char sourceApostrophe = '\u0092';
        char apostrophe = '\'';

        for (int c = 0; c < count; c++)
        {
            ModelCard card = new();
            int cardTextLength = ReadInt32(reader);
            card.Name = ReadString(reader, cardTextLength).Replace(sourceApostrophe, apostrophe);
            card.DatePurchased = ReadDate(reader);
            card.Collection = GetCollectionType(card.Name);

            reader.ReadBytes(2);
            reader.ReadBytes(2);
            reader.ReadBytes(4);
            if (versionNumber > 281)
            {
                reader.ReadInt32();
            }

            ReadInt32(reader);
            reader.ReadBytes(9);
            if (versionNumber > 281)
            {
                reader.ReadByte();
            }

            ReadInt32(reader);
            card.Resolution = GetResolution(ReadInt32(reader));
            card.BestResolution = GetBestResolution(ReadInt32(reader));
            ReadInt32(reader);
            reader.ReadBytes(4);

            string xmlCardId = marketCards ? card.Name.Split('-').First() : card.Name;
            XmlDocument xml = LoadCardXml(xmlCardId);
            if (xml.DocumentElement != null)
            {
                card.Description = GetXmlValue(xml, "description");
                card.Outfit = GetXmlValue(xml, "outfit").Replace(sourceApostrophe, apostrophe);
                card.Hair = GetXmlValue(xml, "hair");
                decimal.TryParse(GetXmlValue(xml, "rate"), _style, _culture, out decimal rating);
                card.Rating = rating;
                card.HotnessLevel = GetXmlValue(xml, "level");
                int.TryParse(GetXmlValue(xml, "duration"), out int frameCount);
                card.FrameCount = frameCount;
                card.XmlSize = GetXmlValue(xml, "size");
                card.ModelName = GetXmlValue(xml, "name");
                decimal.TryParse(GetXmlValue(xml, "age"), _style, _culture, out decimal modelAge);
                card.ModelAge = modelAge > 50 ? 50 : modelAge;
                card.ImagePath = FindCardImage(card);
            }

            EnrichFromProperties(card);
            ReadClips(reader, card);

            if (marketCards)
            {
                card.Name = card.Name.Split("-")[0];
            }

            if (card.Clips.Count > 0)
            {
                cards.Add(card);
            }
            else
            {
                Debug.WriteLine($"Skipping card with no clips: {card.Name}");
            }
        }
    }

    private void EnrichFromProperties(ModelCard card)
    {
        StaticCardProperties? staticCard = _propertiesCatalog.GetStaticCard(card.Name);
        if (staticCard != null)
        {
            card.Tags = staticCard.Tags;
            card.Ethnicity = staticCard.Ethnicity;
            card.Exclusive = staticCard.Exclusive;
            card.NumGirls = staticCard.NumGirls;
            card.ModelId = staticCard.ModelId;
            card.ModelName = GetModelsString(staticCard.ModelId);

            DateTime.TryParse(staticCard.DateShow, out DateTime showDate);
            card.DateShow = showDate;

            string modelId = staticCard.ModelId.Contains(',') ? staticCard.ModelId.Split(',')[0] : staticCard.ModelId;
            ModelProperties? model = _propertiesCatalog.GetModel(modelId);
            if (model != null)
            {
                card.Bust = model.Bust;
                card.Waist = model.Waist;
                card.Hips = model.Hips;
                card.Height = model.Height;
                card.City = model.City;
                card.Country = model.Country;
                card.Birthdate = model.Birthdate;
            }
        }

        DynamicCardProperties? dynamicCard = _propertiesCatalog.GetDynamicCard(card.Name);
        if (dynamicCard != null)
        {
            card.DateReleased = dynamicCard.ReleaseDate;
            if (card.DateReleased == new DateTime(2007, 1, 1) && card.DateShow != default)
            {
                card.DateReleased = card.DateShow.AddMonths(2);
            }
        }

        if (card.ModelAge == 0 && card.Birthdate != null && card.DateReleased != default)
        {
            int age = card.DateReleased.Year - card.Birthdate.Value.Year;
            if (card.Birthdate.Value.Date > card.DateReleased.AddYears(-age))
            {
                age--;
            }

            card.ModelAge = age;
        }
    }

    private void ReadClips(BinaryReader reader, ModelCard card)
    {
        int clipCount = ReadInt32(reader);
        for (int i = 0; i < clipCount; i++)
        {
            ModelClip clip = new();
            int stringLength = ReadInt32(reader);
            clip.ClipNumber = i;
            clip.ClipName = ReadStringUnicode(reader, stringLength);
            ReadInt32(reader);
            ReadInt32(reader);
            clip.HotnessCode = ConvertHotness(ReadInt32(reader));

            byte byte4 = reader.ReadByte();
            byte codeByte = reader.ReadByte();
            bool glass = Convert.ToBoolean(codeByte & 1);
            codeByte = reader.ReadByte();
            bool cageClip = Convert.ToBoolean(codeByte & 2);
            bool onTop = Convert.ToBoolean(codeByte & 4);
            codeByte = reader.ReadByte();
            bool onBar = Convert.ToBoolean(codeByte & 1);
            bool behindBar = Convert.ToBoolean(codeByte & 2);
            bool poleDance = Convert.ToBoolean(codeByte & 4);
            bool fullLegs = Convert.ToBoolean(codeByte & 8);
            bool withSign = Convert.ToBoolean(codeByte & 16);
            bool withProp = Convert.ToBoolean(codeByte & 32);
            bool fromSide = Convert.ToBoolean(codeByte & 64);

            clip.Size = ReadInt32(reader);
            clip.ScCode = ReadInt32(reader);
            clip.IsEnabled = Convert.ToBoolean(ReadInt32(reader));

            List<string> clipTypes = [];
            bool standingClip = !(cageClip || onTop || onBar || behindBar);
            if (standingClip) clipTypes.Add("Standing");
            if (cageClip) clipTypes.Add("Cage");
            if (onBar) clipTypes.Add("Table");
            if (behindBar) clipTypes.Add("BehindTable");
            if (onTop) clipTypes.Add("Swing");
            if (poleDance) clipTypes.Add("Pole");
            if (glass) clipTypes.Add("Glass");
            if (withSign) clipTypes.Add("With Sign");
            if (withProp) clipTypes.Add("With Prop");
            if (fullLegs) clipTypes.Add("Full Legs");
            if (fromSide) clipTypes.Add("From Side");
            clip.ClipType = string.Join(", ", clipTypes);

            ReadInt32(reader);
            card.Clips.Add(clip);
        }
    }

    private string GetModelsString(string? cardModelId)
    {
        if (string.IsNullOrEmpty(cardModelId))
        {
            return string.Empty;
        }

        string modelName = string.Empty;
        foreach (string modelId in cardModelId.Split(','))
        {
            BioProperties? bio = _propertiesCatalog.GetBio(modelId);
            if (bio == null)
            {
                continue;
            }

            modelName = modelName.Length > 0 ? $"{modelName} &_{bio.Name}" : bio.Name;
        }

        return PascalCase(modelName);
    }

    private static string PascalCase(string word)
    {
        return string.Join(
            " ",
            word.Split('_')
                .Select(w => w.Trim())
                .Where(w => w.Length > 0)
                .Select(w => w[..1].ToUpper() + w[1..].ToLower()));
    }

    private string? FindCardImage(ModelCard card)
    {
        string cardFolder = Path.Combine(_paths.DataPath, card.Name.Split('-').First());
        string jpg = Path.Combine(cardFolder, $"{card.Name}.jpg");
        if (File.Exists(jpg)) return jpg;

        string cardJpg = Path.Combine(cardFolder, $"{card.Name}c.jpg");
        if (File.Exists(cardJpg)) return cardJpg;

        string png = Path.Combine(cardFolder, $"{card.Name}.png");
        return File.Exists(png) ? png : null;
    }

    private XmlDocument LoadCardXml(string cardId)
    {
        string fullPath = Path.Combine(_paths.DataPath, cardId, $"{cardId}.xml");
        XmlDocument document = new();
        if (File.Exists(fullPath))
        {
            document.Load(fullPath);
        }

        return document;
    }

    private static string GetXmlValue(XmlDocument document, string variable)
    {
        try
        {
            XmlNodeList elements = document.GetElementsByTagName(variable);
            return elements.Count > 0 ? elements[0]?.InnerText ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static CardResolutionType GetResolution(int code)
    {
        return code switch
        {
            1 => CardResolutionType.Lowest,
            2 => CardResolutionType.Low,
            3 => CardResolutionType.Medium,
            4 => CardResolutionType.High,
            5 => CardResolutionType.Highest,
            _ => CardResolutionType.Unknown
        };
    }

    private static CardResolutionType GetBestResolution(int code)
    {
        return code switch
        {
            >= 16 => CardResolutionType.Highest,
            >= 8 => CardResolutionType.High,
            >= 4 => CardResolutionType.Medium,
            >= 2 => CardResolutionType.Low,
            1 => CardResolutionType.Lowest,
            _ => CardResolutionType.Unknown
        };
    }

    private static CardCollectionType GetCollectionType(string cardNumber)
    {
        return cardNumber[0] switch
        {
            'a' => CardCollectionType.IStripperClassic,
            'b' => CardCollectionType.VirtuaGuy,
            'c' => CardCollectionType.DeskBabes,
            'd' => CardCollectionType.VGClassic,
            'e' => CardCollectionType.IStripper,
            'f' => CardCollectionType.IStripperXXX,
            'g' => CardCollectionType.TradingCard,
            _ => CardCollectionType.Undefined
        };
    }

    private static HotnessCode ConvertHotness(int code)
    {
        return code switch
        {
            0 => HotnessCode.Public,
            1 => HotnessCode.NoNudity,
            2 => HotnessCode.Topless,
            3 => HotnessCode.Nudity,
            4 => HotnessCode.FullNudity,
            5 => HotnessCode.Xxx,
            _ => HotnessCode.NoNudity
        };
    }

    private static int ReadInt32(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        return bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3];
    }

    private static string ReadString(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        return Encoding.Default.GetString(bytes);
    }

    private static string ReadStringUnicode(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        return Encoding.Default.GetString(bytes.Where((_, index) => index % 2 == 1).ToArray());
    }

    private static DateTime ReadDate(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        DateTime baseDate = new(1926, 11, 12);
        return baseDate.AddDays(bytes[2] << 8 | bytes[3]);
    }
}
