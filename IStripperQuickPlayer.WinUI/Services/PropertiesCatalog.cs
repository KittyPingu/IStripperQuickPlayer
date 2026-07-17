using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Xml;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class PropertiesCatalog
{
    private const string DynamicPropertiesUrl = "http://www.istripper.com/bof/properties/properties_iStripper.xml.gz";
    private const string StaticPropertiesUrl = "http://www.istripper.com/bof/mselistGenerator/staticProperties_iStripper.xml.gz";

    private readonly IstripperPaths _paths;
    private readonly HttpClient _httpClient = new();

    private XmlNodeList? _staticCards;
    private XmlNodeList? _staticModels;
    private XmlNodeList? _staticBios;
    private XmlNodeList? _dynamicCards;

    public PropertiesCatalog(IstripperPaths paths)
    {
        _paths = paths;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        XmlDocument staticXml = await LoadXmlAsync(
            Path.Combine(_paths.DataPath, "staticProperties loaded from server.xml"),
            StaticPropertiesUrl,
            cancellationToken);
        _staticModels = staticXml.SelectNodes("/root/m");
        _staticCards = staticXml.SelectNodes("/root/c");
        _staticBios = staticXml.SelectNodes("/root/d");

        XmlDocument dynamicXml = await LoadXmlAsync(
            Path.Combine(_paths.DataPath, "Properties loaded from server.xml"),
            DynamicPropertiesUrl,
            cancellationToken);
        _dynamicCards = dynamicXml.SelectNodes("/root/c");
    }

    public StaticCardProperties? GetStaticCard(string id)
    {
        return FindByAttribute(_staticCards, "id", id) is { } node
            ? new StaticCardProperties(node)
            : null;
    }

    public ModelProperties? GetModel(string id)
    {
        return FindByAttribute(_staticModels, "id", id) is { } node
            ? new ModelProperties(node)
            : null;
    }

    public BioProperties? GetBio(string id)
    {
        return FindByAttribute(_staticBios, "id", id) is { } node
            ? new BioProperties(node)
            : null;
    }

    public DynamicCardProperties? GetDynamicCard(string id)
    {
        return FindByAttribute(_dynamicCards, "i", id) is { } node
            ? new DynamicCardProperties(node)
            : null;
    }

    private async Task<XmlDocument> LoadXmlAsync(string localPath, string url, CancellationToken cancellationToken)
    {
        if (!File.Exists(localPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await DownloadGzipXmlAsync(url, localPath, cancellationToken);
        }

        string fullText = await File.ReadAllTextAsync(localPath, cancellationToken);
        int start = fullText.IndexOf('<');
        if (start > 0)
        {
            fullText = fullText[start..];
        }

        XmlDocument document = new();
        document.LoadXml(fullText);
        return document;
    }

    private async Task DownloadGzipXmlAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        await using Stream source = await _httpClient.GetStreamAsync(url, cancellationToken);
        await using FileStream destination = File.Create(destinationPath);
        await using GZipStream decompressor = new(source, CompressionMode.Decompress);
        await decompressor.CopyToAsync(destination, cancellationToken);
    }

    private static XmlNode? FindByAttribute(XmlNodeList? nodes, string attributeName, string id)
    {
        if (nodes == null)
        {
            return null;
        }

        foreach (XmlNode node in nodes)
        {
            if (node.GetAttribute(attributeName) == id)
            {
                return node;
            }
        }

        return null;
    }
}

public sealed class StaticCardProperties
{
    public StaticCardProperties(XmlNode? node)
    {
        ModelId = node.GetAttribute("mo");
        Hair = node.GetAttribute("ha");
        Tags = node.GetAttribute("ca").Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Date = node.GetAttribute("da");
        DateShow = node.GetAttribute("dsh");
        Ethnicity = node.GetAttribute("ty");
        Exclusive = node.GetAttribute("exclusive") == "1";

        if (node.GetAttribute("na") == "Duo")
        {
            NumGirls = 2;
        }

        if (Tags.Contains("trio"))
        {
            NumGirls = 3;
        }

        if (Tags.Contains("duo"))
        {
            NumGirls = 3;
        }
    }

    public string ModelId { get; }

    public string Hair { get; }

    public string[] Tags { get; }

    public string Date { get; }

    public string DateShow { get; }

    public string Ethnicity { get; }

    public bool Exclusive { get; }

    public int NumGirls { get; } = 1;
}

public sealed class DynamicCardProperties
{
    public DynamicCardProperties(XmlNode? node)
    {
        string releaseDate = node?.SelectSingleNode("rd")?.FirstChild?.Value ?? string.Empty;
        string[] parts = releaseDate.Split("-");
        if (parts.Length <= 2)
        {
            ReleaseDate = new DateTime(2007, 1, 1);
            return;
        }

        int year = ParseBounded(parts[0], 2007, 2100, 2007);
        int month = ParseBounded(parts[1], 1, 12, 1);
        int day = ParseBounded(parts[2].Split(" ")[0], 1, 31, 1);
        ReleaseDate = new DateTime(year, month, day);
    }

    public DateTime ReleaseDate { get; }

    private static int ParseBounded(string value, int min, int max, int fallback)
    {
        return int.TryParse(value, out int parsed) && parsed >= min && parsed <= max
            ? parsed
            : fallback;
    }
}

public sealed class ModelProperties
{
    public ModelProperties(XmlNode? node)
    {
        NumberStyles style = NumberStyles.AllowDecimalPoint;
        CultureInfo culture = CultureInfo.CreateSpecificCulture(CultureInfo.CurrentCulture.Name);
        culture.NumberFormat.NumberDecimalSeparator = ".";

        Name = node.GetAttribute("id");
        Height = node.GetAttribute("heig");

        string measurements = node.GetAttribute("stat");
        if (!string.IsNullOrEmpty(measurements))
        {
            string[] values = measurements.Split('/');
            if (values.Length > 2)
            {
                decimal.TryParse(values[0], style, culture, out decimal bust);
                decimal.TryParse(values[1], style, culture, out decimal waist);
                decimal.TryParse(values[2], style, culture, out decimal hips);
                Bust = bust;
                Waist = waist;
                Hips = hips;
            }
        }

        City = node.GetAttribute("city");
        Country = node.GetAttribute("cntry");
        DateTime.TryParse(node.GetAttribute("birth"), out DateTime birthdate);
        Birthdate = birthdate;
    }

    public string Name { get; } = string.Empty;

    public string Height { get; } = string.Empty;

    public decimal Bust { get; } = 30;

    public decimal Waist { get; } = 30;

    public decimal Hips { get; } = 30;

    public string City { get; } = string.Empty;

    public string Country { get; } = string.Empty;

    public DateTime Birthdate { get; }
}

public sealed class BioProperties
{
    public BioProperties(XmlNode? node)
    {
        Name = node.GetAttribute("dir");
    }

    public string Name { get; } = string.Empty;
}
