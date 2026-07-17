using System.Text.Json.Serialization;

namespace IStripperQuickPlayer.WinUI.Core;

public sealed class RootPhotos
{
    [JsonPropertyName("zip")]
    public string Zip { get; set; } = string.Empty;

    [JsonPropertyName("photos")]
    public Photo[] Photos { get; set; } = [];
}

public sealed class Photo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("access")]
    public string Access { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public PhotoSize Size { get; set; } = new();

    [JsonPropertyName("files")]
    public PhotoFiles Files { get; set; } = new();

    [JsonPropertyName("fullscreen")]
    public string Fullscreen { get; set; } = string.Empty;
}

public sealed class PhotoSize
{
    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }
}

public sealed class PhotoFiles
{
    [JsonPropertyName("mini")]
    public string Mini { get; set; } = string.Empty;

    [JsonPropertyName("full")]
    public string Full { get; set; } = string.Empty;
}
