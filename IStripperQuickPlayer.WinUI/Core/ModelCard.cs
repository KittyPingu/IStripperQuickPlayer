namespace IStripperQuickPlayer.WinUI.Core;

public sealed class ModelCard
{
    public List<ModelClip> Clips { get; } = [];

    public string Name { get; set; } = string.Empty;

    public DateTime? DatePurchased { get; set; }

    public CardCollectionType Collection { get; set; }

    public CardResolutionType Resolution { get; set; }

    public CardResolutionType BestResolution { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Outfit { get; set; } = string.Empty;

    public string? Hair { get; set; }

    public decimal Rating { get; set; }

    public string? HotnessLevel { get; set; }

    public int? FrameCount { get; set; }

    public string? XmlSize { get; set; }

    public string? ModelName { get; set; }

    public decimal ModelAge { get; set; }

    public string? ImagePath { get; set; }

    public string[] Tags { get; set; } = [];

    public decimal? Bust { get; set; }

    public decimal? Waist { get; set; }

    public decimal? Hips { get; set; }

    public string? Ethnicity { get; set; }

    public bool? Exclusive { get; set; }

    public string? Height { get; set; }

    public string? City { get; set; }

    public string? Country { get; set; }

    public DateTime? Birthdate { get; set; }

    public int? NumGirls { get; set; }

    public DateTime DateShow { get; set; }

    public DateTime DateReleased { get; set; }

    public string? ModelId { get; set; }

    public string DisplayText => $"{ModelName}{Environment.NewLine}{Outfit}";
}
