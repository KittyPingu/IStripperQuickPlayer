namespace IStripperQuickPlayer.WinUI.Core;

public sealed class UserData
{
    public Dictionary<string, decimal> CardRating { get; set; } = [];

    public Dictionary<string, List<string>> CardTags { get; set; } = [];

    public Dictionary<string, bool> CardFavourite { get; set; } = [];

    public void AddCardRating(string tag, decimal rating)
    {
        CardRating[tag] = rating;
    }

    public decimal GetCardRating(string tag)
    {
        return CardRating.TryGetValue(tag, out decimal rating) ? rating : 0;
    }

    public void AddCardFavourite(string tag, bool favourite)
    {
        CardFavourite[tag] = favourite;
    }

    public bool GetCardFavourite(string tag)
    {
        return CardFavourite.TryGetValue(tag, out bool favourite) && favourite;
    }

    public void AddCardTags(string tag, IEnumerable<string> tags)
    {
        CardTags[tag] = tags.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
    }

    public List<string> GetCardTags(string tag)
    {
        return CardTags.TryGetValue(tag, out List<string>? tags) ? tags : [];
    }
}
