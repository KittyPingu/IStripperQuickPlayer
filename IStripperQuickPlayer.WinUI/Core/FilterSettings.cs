namespace IStripperQuickPlayer.WinUI.Core;

public sealed class FilterSettings : ICloneable
{
    public decimal MinAge { get; set; } = 18;

    public decimal MaxAge { get; set; } = 43;

    public decimal MinBust { get; set; }

    public decimal MaxBust { get; set; } = 99;

    public decimal MinRating { get; set; }

    public decimal MaxRating { get; set; } = 5;

    public decimal MinMyRating { get; set; }

    public decimal MaxMyRating { get; set; } = 10;

    public DateTime MinDate { get; set; } = new(2007, 1, 1);

    public DateTime MaxDate { get; set; } = DateTime.Now;

    public string Tags { get; set; } = string.Empty;

    public bool IStripper { get; set; } = true;

    public bool IStripperClassic { get; set; } = true;

    public bool IStripperXXX { get; set; } = true;

    public bool VGClassic { get; set; } = true;

    public bool DeskBabes { get; set; } = true;

    public bool Special { get; set; } = true;

    public bool Normal { get; set; } = true;

    public bool VirtuaGuy { get; set; } = true;

    public bool TradingCard { get; set; } = true;

    public object Clone()
    {
        return MemberwiseClone();
    }
}
