using System.Globalization;
using IStripperQuickPlayer.WinUI.Core;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class CardQueryService
{
    public IReadOnlyList<ModelCard> QueryCards(
        IReadOnlyList<ModelCard> sourceCards,
        UserData userData,
        FilterSettings filterSettings,
        AppSettings settings,
        string searchText)
    {
        IEnumerable<ModelCard> cards = sourceCards;

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            cards = ApplyCardSearch(cards.ToList(), userData, settings, searchText);
        }

        cards = ApplyCardFilters(cards.ToList(), userData, filterSettings, settings);
        cards = ApplySort(cards, userData, settings);

        return cards.Where(c => c.Clips.Count > 0).ToList();
    }

    public IReadOnlyList<ModelClip> QueryClips(IEnumerable<ModelClip> clips, AppSettings settings)
    {
        IEnumerable<ModelClip> currentClips = clips;
        if (!string.IsNullOrWhiteSpace(settings.ClipFilter.ClipTypeSearch))
        {
            currentClips = ApplyClipSearch(currentClips.ToList(), settings.ClipFilter.ClipTypeSearch);
        }

        return currentClips.Where(clip => ShouldShowClip(clip, settings)).ToList();
    }

    public string GetSortBadge(ModelCard card, UserData userData, AppSettings settings)
    {
        return settings.SortBy switch
        {
            "My Rating" when !settings.ShowRatingStars => userData.GetCardRating(card.Name) > 0
                ? userData.GetCardRating(card.Name).ToString(CultureInfo.CurrentCulture)
                : string.Empty,
            "Height" => FormatHeight(card.Height),
            "Rating" => (card.Rating - 5m).ToString(CultureInfo.CurrentCulture),
            "Age" => card.ModelAge.ToString(CultureInfo.CurrentCulture),
            "Ethnicity" => card.Ethnicity ?? string.Empty,
            "Breast Size" => (card.Bust ?? 0).ToString(CultureInfo.CurrentCulture),
            "Date Purchased" => card.DatePurchased?.ToShortDateString() ?? string.Empty,
            "Release Date" => card.DateReleased == default ? string.Empty : card.DateReleased.ToShortDateString(),
            _ => string.Empty
        };
    }

    private static IEnumerable<ModelCard> ApplyCardSearch(
        IReadOnlyList<ModelCard> sourceCards,
        UserData userData,
        AppSettings settings,
        string searchText)
    {
        IEnumerable<ModelCard> currentCards = sourceCards;
        string[] parts = searchText.ToLower().Split(" and ").Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

        foreach (string part in parts)
        {
            List<string> tagList = part.Split(" or ").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (part.Contains('!'))
            {
                List<ModelCard>? positive = null;
                IEnumerable<ModelCard> negative = currentCards;
                foreach (string tag in tagList.Where(x => !x.Contains('!')))
                {
                    positive = currentCards.Where(c => CardSearchMatches(c, userData, settings, tagList, tag)).ToList();
                }

                positive ??= [];
                foreach (string tag in tagList.Where(x => x.Contains('!')))
                {
                    negative = negative.Where(c => CardSearchMatches(c, userData, settings, tagList, tag));
                }

                currentCards = positive.Union(negative).ToList();
            }
            else
            {
                currentCards = currentCards.Where(c => CardSearchMatches(c, userData, settings, tagList, null)).ToList();
            }
        }

        return currentCards;
    }

    private static bool CardSearchMatches(
        ModelCard card,
        UserData userData,
        AppSettings settings,
        IReadOnlyList<string> tagList,
        string? singleTag)
    {
        if (singleTag != null)
        {
            return card.ModelName.ContainsWithNot(singleTag)
                || tagList.Any(tag => card.Name.ContainsWithNot(tag))
                || (settings.ShowDescInSearch && card.Description.ContainsWithNot(singleTag))
                || (settings.ShowOutfitInSearch && card.Outfit.ContainsWithNot(singleTag))
                || string.Join(",", userData.GetCardTags(card.Name)).ContainsWithNot(singleTag)
                || string.Join(",", card.Tags).ContainsWithNot(singleTag);
        }

        return tagList.Any(tag => card.ModelName.ContainsWithNot(tag))
            || tagList.Any(tag => card.Name.ContainsWithNot(tag))
            || (settings.ShowDescInSearch && tagList.Any(tag => card.Description.ContainsWithNot(tag)))
            || (settings.ShowOutfitInSearch && tagList.Any(tag => card.Outfit.ContainsWithNot(tag)))
            || tagList.Any(tag => string.Join(",", userData.GetCardTags(card.Name)).ContainsWithNot(tag))
            || tagList.Any(tag => string.Join(",", card.Tags).ContainsWithNot(tag));
    }

    private static IEnumerable<ModelCard> ApplyCardFilters(
        IReadOnlyList<ModelCard> sourceCards,
        UserData userData,
        FilterSettings filterSettings,
        AppSettings settings)
    {
        IEnumerable<ModelCard> cards = sourceCards;
        if (settings.FavouritesFilter)
        {
            cards = cards.Where(c => userData.GetCardFavourite(c.Name));
        }

        cards = cards.Where(c => (c.DateReleased >= filterSettings.MinDate && c.DateReleased <= filterSettings.MaxDate) || c.DateReleased == default);
        cards = cards.Where(c => userData.GetCardRating(c.Name) >= filterSettings.MinMyRating && userData.GetCardRating(c.Name) <= filterSettings.MaxMyRating);
        cards = cards.Where(c =>
            (((c.ModelAge >= filterSettings.MinAge && c.ModelAge <= filterSettings.MaxAge) || c.ModelAge == 0 || c.ModelAge > 99)
            && (c.Bust == null || (c.Bust >= filterSettings.MinBust && c.Bust <= filterSettings.MaxBust) || c.Bust == 0 || c.Bust > 99)
            && (c.Rating - 5m >= filterSettings.MinRating && c.Rating - 5m <= filterSettings.MaxRating))
            || c.Rating == 0);

        if (!string.IsNullOrWhiteSpace(filterSettings.Tags))
        {
            cards = ApplyFilterTags(cards.ToList(), userData, filterSettings.Tags);
        }

        HashSet<CardCollectionType> enabledCollections = [];
        if (filterSettings.IStripperXXX) enabledCollections.Add(CardCollectionType.IStripperXXX);
        if (filterSettings.DeskBabes) enabledCollections.Add(CardCollectionType.DeskBabes);
        if (filterSettings.IStripperClassic) enabledCollections.Add(CardCollectionType.IStripperClassic);
        if (filterSettings.VGClassic) enabledCollections.Add(CardCollectionType.VGClassic);
        if (filterSettings.IStripper) enabledCollections.Add(CardCollectionType.IStripper);
        if (filterSettings.VirtuaGuy) enabledCollections.Add(CardCollectionType.VirtuaGuy);
        if (filterSettings.TradingCard) enabledCollections.Add(CardCollectionType.TradingCard);

        if (filterSettings.Normal && !filterSettings.Special)
        {
            cards = cards.Where(c => c.Exclusive != null && !c.Exclusive.Value);
        }
        else if (filterSettings.Special && !filterSettings.Normal)
        {
            cards = cards.Where(c => c.Exclusive != null && c.Exclusive.Value);
        }

        cards = cards.Where(c => enabledCollections.Contains(c.Collection) || c.Collection == CardCollectionType.Undefined);
        return cards;
    }

    private static IEnumerable<ModelCard> ApplyFilterTags(IReadOnlyList<ModelCard> currentCards, UserData userData, string tags)
    {
        IEnumerable<ModelCard> cards = currentCards;
        string[] parts = tags.ToLower().Split(" and ").Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

        foreach (string part in parts)
        {
            List<string> tagList = part.Split(" or ").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (part.Contains('!'))
            {
                List<ModelCard>? positive = null;
                IEnumerable<ModelCard> negative = cards;
                foreach (string tag in tagList.Where(x => !x.Contains('!')))
                {
                    positive = cards.Where(c => string.Join(",", userData.GetCardTags(c.Name)).ContainsWithNot(tag)
                        || string.Join(",", c.Tags).ContainsWithNot(tag)
                        || c.Name.ContainsWithNot(tag)).ToList();
                }

                positive ??= [];
                foreach (string tag in tagList.Where(x => x.Contains('!')))
                {
                    negative = negative.Where(c => string.Join(",", userData.GetCardTags(c.Name)).ContainsWithNot(tag)
                        && string.Join(",", c.Tags).ContainsWithNot(tag)
                        && c.Name.ContainsWithNot(tag));
                }

                cards = positive.Union(negative).ToList();
            }
            else
            {
                cards = cards.Where(c => tagList.Any(tag => c.ModelName.ContainsWithNot(tag))
                    || tagList.Any(tag => c.Name.ContainsWithNot(tag))
                    || tagList.Any(tag => string.Join(",", userData.GetCardTags(c.Name)).ContainsWithNot(tag))
                    || tagList.Any(tag => string.Join(",", c.Tags).ContainsWithNot(tag))).ToList();
            }
        }

        return cards;
    }

    private static IEnumerable<ModelCard> ApplySort(IEnumerable<ModelCard> cards, UserData userData, AppSettings settings)
    {
        bool desc = settings.SortDirection.StartsWith("Desc", StringComparison.OrdinalIgnoreCase);

        return settings.SortBy switch
        {
            "My Rating" => desc ? cards.OrderByDescending(c => userData.GetCardRating(c.Name)) : cards.OrderBy(c => userData.GetCardRating(c.Name)),
            "Rating" => desc ? cards.OrderByDescending(c => c.Rating) : cards.OrderBy(c => c.Rating),
            "Age" => desc ? cards.OrderByDescending(c => c.ModelAge) : cards.OrderBy(c => c.ModelAge),
            "Breast Size" => desc ? cards.OrderByDescending(c => c.Bust) : cards.OrderBy(c => c.Bust),
            "Ethnicity" => desc ? cards.OrderByDescending(c => c.Ethnicity) : cards.OrderBy(c => c.Ethnicity),
            "Height" => desc ? cards.OrderByDescending(c => c.Height) : cards.OrderBy(c => c.Height),
            "Date Purchased" => desc ? cards.OrderByDescending(c => c.DatePurchased) : cards.OrderBy(c => c.DatePurchased),
            "Release Date" => desc ? cards.OrderByDescending(c => c.DateReleased) : cards.OrderBy(c => c.DateReleased),
            _ => desc ? cards.OrderByDescending(c => c.ModelName) : cards.OrderBy(c => c.ModelName)
        };
    }

    private static IEnumerable<ModelClip> ApplyClipSearch(IReadOnlyList<ModelClip> sourceClips, string searchText)
    {
        IEnumerable<ModelClip> currentClips = sourceClips;
        string[] parts = searchText.ToLower().Split(" and ").Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

        foreach (string part in parts)
        {
            List<string> tagList = part.Split(" or ").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (part.Contains('!'))
            {
                List<ModelClip>? positive = null;
                IEnumerable<ModelClip> negative = currentClips;
                foreach (string tag in tagList.Where(x => !x.Contains('!')))
                {
                    positive = currentClips.Where(c => c.ClipType.ContainsWithNot(tag)).ToList();
                }

                positive ??= [];
                foreach (string tag in tagList.Where(x => x.Contains('!')))
                {
                    negative = negative.Where(c => c.ClipType.ContainsWithNot(tag));
                }

                currentClips = positive.Union(negative).ToList();
            }
            else
            {
                currentClips = currentClips.Where(c => tagList.Any(tag => c.ClipType.ContainsWithNot(tag))).ToList();
            }
        }

        return currentClips;
    }

    private static bool ShouldShowClip(ModelClip clip, AppSettings settings)
    {
        bool addThis = clip.HotnessCode switch
        {
            HotnessCode.Public => settings.ClipFilter.Public,
            HotnessCode.NoNudity => settings.ClipFilter.NoNudity,
            HotnessCode.Topless => settings.ClipFilter.Topless,
            HotnessCode.Nudity => settings.ClipFilter.Nudity,
            HotnessCode.FullNudity => settings.ClipFilter.FullNudity,
            HotnessCode.Xxx => settings.ClipFilter.Xxx,
            _ => false
        };

        if (settings.MinSizeMB > 0 && settings.MinSizeMB > clip.SizeMb)
        {
            addThis = false;
        }

        if (clip.ClipName != null && clip.ClipName.Contains("demo", StringComparison.OrdinalIgnoreCase) && !settings.ClipFilter.Demo)
        {
            addThis = false;
        }

        return addThis;
    }

    private static string FormatHeight(string? height)
    {
        if (!decimal.TryParse(height, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out decimal parsed))
        {
            return string.Empty;
        }

        if (RegionInfo.CurrentRegion.IsMetric && CultureInfo.CurrentCulture.Name != "en-GB")
        {
            return ((((Math.Floor(parsed) * 12) + ((parsed - Math.Floor(parsed)) * 10)) * 2.54m).ToString("N1", CultureInfo.CurrentCulture)) + "cm";
        }

        return $"{Math.Floor(parsed)}'{(int)(24 * (parsed - Math.Floor(parsed))) / 2.0m}''";
    }
}
