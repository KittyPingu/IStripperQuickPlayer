using IStripperQuickPlayer.WinUI.Core;
using IStripperQuickPlayer.WinUI.Services;

namespace IStripperQuickPlayer.WinUI.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettingsStore _settingsStore;
    private readonly FilterSettingsStore _filterSettingsStore;
    private readonly UserDataStore _userDataStore;
    private readonly ModelsListLoader _modelsListLoader;
    private readonly CardQueryService _queryService;
    private readonly PlayerControlService _playerControlService;
    private readonly WallpaperService _wallpaperService;
    private readonly CardDeleteService _cardDeleteService;

    private IReadOnlyList<ModelCard> _allCards = [];
    private Dictionary<string, FilterSettings> _filters;
    private FilterSettings _currentFilter;
    private CardTileViewModel? _selectedCard;
    private ClipViewModel? _selectedClip;
    private string _status = "Ready";
    private string _nowPlayingText = "Now Playing:";
    private string _searchText = string.Empty;
    private string _userTags = string.Empty;
    private string? _lastWallpaperCardId;
    private bool _settingWallpaper;
    private AppSettings _settings;
    private UserData _userData;
    private string _selectedFilterName = "Default";
    private IReadOnlyList<ModelCard> _filteredCards = [];
    private CardTileViewModel? _nowPlayingCard;
    private IReadOnlyList<CardTileViewModel> _cards = [];

    public MainViewModel(
        AppSettingsStore settingsStore,
        FilterSettingsStore filterSettingsStore,
        UserDataStore userDataStore,
        ModelsListLoader modelsListLoader,
        CardQueryService queryService,
        PlayerControlService playerControlService,
        WallpaperService wallpaperService,
        CardDeleteService cardDeleteService)
    {
        _settingsStore = settingsStore;
        _filterSettingsStore = filterSettingsStore;
        _userDataStore = userDataStore;
        _modelsListLoader = modelsListLoader;
        _queryService = queryService;
        _playerControlService = playerControlService;
        _wallpaperService = wallpaperService;
        _cardDeleteService = cardDeleteService;
        _settings = _settingsStore.Load();
        _userData = _userDataStore.Load();
        _filters = _filterSettingsStore.Load();
        _currentFilter = _filters.TryGetValue(_selectedFilterName, out FilterSettings? filter) ? filter : new FilterSettings();
    }

    public IReadOnlyList<CardTileViewModel> Cards
    {
        get => _cards;
        private set => SetProperty(ref _cards, value);
    }

    public System.Collections.ObjectModel.ObservableCollection<ClipViewModel> Clips { get; } = [];

    public System.Collections.ObjectModel.ObservableCollection<string> FilterNames { get; } = [];

    public IReadOnlyList<string> SortOptions { get; } =
    [
        "Model Name",
        "My Rating",
        "Rating",
        "Age",
        "Breast Size",
        "Ethnicity",
        "Height",
        "Date Purchased",
        "Release Date"
    ];

    public IReadOnlyList<string> SortDirections { get; } = ["Ascending", "Descending"];

    public AppSettings Settings
    {
        get => _settings;
        private set => SetProperty(ref _settings, value);
    }

    public FilterSettings CurrentFilter
    {
        get => _currentFilter;
        set => SetProperty(ref _currentFilter, value);
    }

    public string SelectedFilterName
    {
        get => _selectedFilterName;
        set
        {
            if (SetProperty(ref _selectedFilterName, value) && _filters.TryGetValue(value, out FilterSettings? filter))
            {
                CurrentFilter = (FilterSettings)filter.Clone();
                RefreshCards();
                OnPropertyChanged(nameof(CurrentFilter));
            }
        }
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string NowPlayingText
    {
        get => _nowPlayingText;
        set => SetProperty(ref _nowPlayingText, value);
    }

    public string CardsShownText => $"Cards Shown: {_filteredCards.Count}/{_allCards.Count(c => c.Clips.Count > 0)}";

    public FilterEnforcementState CreateFilterEnforcementState()
    {
        return new FilterEnforcementState(_allCards.ToList(), _filteredCards.ToList(), Settings);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshCards();
            }
        }
    }

    public CardTileViewModel? SelectedCard
    {
        get => _selectedCard;
        set
        {
            if (_selectedCard != null)
            {
                _selectedCard.IsSelected = false;
            }

            if (SetProperty(ref _selectedCard, value))
            {
                if (_selectedCard != null)
                {
                    _selectedCard.IsSelected = true;
                }

                LoadSelectedCardDetails();
            }
        }
    }

    public ClipViewModel? SelectedClip
    {
        get => _selectedClip;
        set
        {
            if (SetProperty(ref _selectedClip, value) && value != null)
            {
                _playerControlService.PlayClip(value.Clip);
            }
        }
    }

    public string SelectedCardTitle => SelectedCard?.Card is { } card ? $"{card.ModelName}: {card.Outfit}" : string.Empty;

    public string SelectedDescription => SelectedCard?.Card?.Description ?? string.Empty;

    public string SelectedTags => SelectedCard?.Card == null ? string.Empty : $"Tags: {string.Join(",", SelectedCard.Card.Tags)}";

    public string UserTags
    {
        get => _userTags;
        set
        {
            if (SetProperty(ref _userTags, value) && SelectedCard?.Card != null)
            {
                _userData.AddCardTags(SelectedCard.Card.Name, value.Split(','));
                _userDataStore.Save(_userData);
            }
        }
    }

    public string SelectedCollection => SelectedCard?.Card?.Collection.GetDescription() ?? string.Empty;

    public string SelectedResolution => SelectedCard?.Card?.Resolution.GetDescription() ?? string.Empty;

    public string SelectedRating => SelectedCard?.Card == null ? string.Empty : (SelectedCard.Card.Rating - 5m).ToString();

    public string SelectedCollectionText => $"CardType: {SelectedCollection}";

    public string SelectedResolutionText => $"Res: {SelectedResolution}";

    public string SelectedRatingText => $"Rating: {SelectedRating}";

    public string SelectedAge => SelectedCard?.Card == null ? string.Empty : $"Age: {SelectedCard.Card.ModelAge}";

    public string SelectedStats => SelectedCard?.Card == null ? string.Empty : $"Stats: {SelectedCard.Card.Bust}/{SelectedCard.Card.Waist}/{SelectedCard.Card.Hips}";

    public async Task LoadAsync()
    {
        Status = "Loading cards...";
        try
        {
            _allCards = await _modelsListLoader.LoadAsync();
            RefreshFilterNames();
            RefreshCards();
            Status = $"Loaded {_allCards.Count} cards";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    public void Reload()
    {
        _ = LoadAsync();
    }

    public void SaveSettings()
    {
        _settingsStore.Save(Settings);
        RefreshCards();
        LoadSelectedCardDetails();
    }

    public void ApplyFilterChanges()
    {
        RefreshCards();
    }

    public void SaveCurrentFilter(string? name = null)
    {
        string filterName = string.IsNullOrWhiteSpace(name) ? SelectedFilterName : name.Trim();
        if (string.IsNullOrWhiteSpace(filterName))
        {
            filterName = "Default";
        }

        _filters[filterName] = (FilterSettings)CurrentFilter.Clone();
        _filterSettingsStore.Save(_filters);
        RefreshFilterNames();
        _selectedFilterName = filterName;
        OnPropertyChanged(nameof(SelectedFilterName));
        RefreshCards();
    }

    public void DeleteCurrentFilter()
    {
        if (SelectedFilterName == "Default")
        {
            CurrentFilter = new FilterSettings();
            _filters["Default"] = CurrentFilter;
        }
        else
        {
            _filters.Remove(SelectedFilterName);
            SelectedFilterName = "Default";
        }

        _filterSettingsStore.Save(_filters);
        RefreshFilterNames();
        RefreshCards();
    }

    public void ExportCurrentFilter(string path)
    {
        _filterSettingsStore.Export(path, CurrentFilter);
    }

    public void ImportFilter(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        _filters[name] = _filterSettingsStore.Import(path);
        _filterSettingsStore.Save(_filters);
        RefreshFilterNames();
        SelectedFilterName = name;
    }

    public void ApplyPlaylist(string playlistPath)
    {
        List<string> cards = PlaylistLoader.LoadPlaylist(playlistPath);
        SearchText = string.Join(" or ", cards);
        Status = $"Loaded playlist with {cards.Count} cards";
    }

    public void SetRating(string cardId, decimal rating)
    {
        _userData.AddCardRating(cardId, rating);
        _userDataStore.Save(_userData);

        CardTileViewModel? tile = Cards.FirstOrDefault(c => c.CardId == cardId);
        if (tile != null)
        {
            tile.UserRating = rating;
        }
    }

    public void ToggleFavourite()
    {
        if (SelectedCard?.Card == null)
        {
            return;
        }

        bool favourite = !_userData.GetCardFavourite(SelectedCard.Card.Name);
        _userData.AddCardFavourite(SelectedCard.Card.Name, favourite);
        _userDataStore.Save(_userData);
        SelectedCard.IsFavourite = favourite;

        if (Settings.FavouritesFilter)
        {
            RefreshCards();
        }
    }

    public void PlayNextClip()
    {
        if (SelectedCard?.Card == null)
        {
            return;
        }

        _playerControlService.PlayNextClip(SelectedCard.Card, Clips.Select(c => c.Clip).ToList());
    }

    public void PlayNextCard()
    {
        if (Cards.Count == 0)
        {
            return;
        }

        int currentIndex = SelectedCard == null ? -1 : Cards.ToList().IndexOf(SelectedCard);
        CardTileViewModel? nextCard;
        if (Settings.Randomize && Cards.Count > 1)
        {
            do
            {
                nextCard = Cards[Random.Shared.Next(Cards.Count)];
            }
            while (nextCard == SelectedCard);
        }
        else
        {
            int nextIndex = currentIndex + 1;
            if (nextIndex >= Cards.Count)
            {
                nextIndex = 0;
            }

            nextCard = Cards[nextIndex];
        }

        SelectedCard = nextCard;
        PlayNextClip();
    }

    public void SelectCardById(string cardId)
    {
        CardTileViewModel? card = Cards.FirstOrDefault(c => c.CardId.Equals(cardId, StringComparison.OrdinalIgnoreCase));
        if (card != null)
        {
            SelectedCard = card;
        }
    }

    public void ShowSelectedModelInBrowser()
    {
        if (SelectedCard?.Card == null)
        {
            return;
        }

        ModelCard card = SelectedCard.Card;
        string url = @"https://www.istripper.com/models/" + $"{card.ModelName}/{card.Outfit} {card.Name}".Replace(" ", "-");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    public string? ShowNowPlayingModel()
    {
        string? currentAnimation = _playerControlService.CurrentAnimation;
        if (string.IsNullOrWhiteSpace(currentAnimation))
        {
            Status = "No now-playing card is available.";
            return null;
        }

        string cardId = currentAnimation.Split('\\').FirstOrDefault() ?? string.Empty;
        ModelCard? card = _allCards.FirstOrDefault(c => c.Name.Equals(cardId, StringComparison.OrdinalIgnoreCase));
        if (card == null)
        {
            Status = "The now-playing card is not in the loaded card list.";
            return null;
        }

        _searchText = card.ModelName ?? card.Name;
        OnPropertyChanged(nameof(SearchText));
        RefreshCards(card.Name);
        CardTileViewModel? tile = Cards.FirstOrDefault(c => c.CardId.Equals(card.Name, StringComparison.OrdinalIgnoreCase));
        if (tile != null)
        {
            SelectedCard = tile;
        }

        Status = $"Filtered to now-playing model: {card.ModelName}";
        return tile?.CardId;
    }

    public async Task SetWallpaperAsync()
    {
        if (SelectedCard?.Card == null)
        {
            return;
        }

        Status = "Setting wallpaper...";
        try
        {
            await _wallpaperService.SetSelectedCardWallpaperAsync(SelectedCard.Card, Settings);
            Status = "Wallpaper updated";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    public void RestoreWallpaperIfChanged()
    {
        try
        {
            _wallpaperService.RestoreOriginalDesktopState();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    public void DeleteSelectedCardFromDisk()
    {
        if (SelectedCard?.Card == null)
        {
            return;
        }

        ModelCard card = SelectedCard.Card;
        _cardDeleteService.DeleteLocalCardFolders(card);
        _allCards = _allCards.Where(c => c.Name != card.Name).ToList();
        RefreshCards();
        Status = "Local card folders deleted. You may need to restart or synchronize iStripper.";
    }

    public string? UpdateNowPlaying()
    {
        string? currentAnimation = _playerControlService.CurrentAnimation;
        if (string.IsNullOrWhiteSpace(currentAnimation))
        {
            return null;
        }

        string cardId = currentAnimation.Split('\\').FirstOrDefault() ?? string.Empty;
        CardTileViewModel? nowPlayingCard = Cards.FirstOrDefault(c => c.CardId == cardId);
        bool nowPlayingChanged = _nowPlayingCard != nowPlayingCard;
        if (_nowPlayingCard != nowPlayingCard)
        {
            if (_nowPlayingCard != null)
            {
                _nowPlayingCard.IsNowPlaying = false;
            }

            _nowPlayingCard = nowPlayingCard;
            if (_nowPlayingCard != null)
            {
                _nowPlayingCard.IsNowPlaying = true;
            }
        }

        if (nowPlayingCard?.Card != null)
        {
            string clipName = currentAnimation.Split('\\').LastOrDefault() ?? string.Empty;
            NowPlayingText = $"Now Playing: {nowPlayingCard.Card.ModelName}, {nowPlayingCard.Card.Outfit} ({clipName})";
            if (Settings.AutoWallpaper && _lastWallpaperCardId != nowPlayingCard.Card.Name && !_settingWallpaper)
            {
                _lastWallpaperCardId = nowPlayingCard.Card.Name;
                _ = SetWallpaperForCardAsync(nowPlayingCard.Card);
            }
        }

        return nowPlayingChanged && nowPlayingCard != null ? nowPlayingCard.CardId : null;
    }

    private async Task SetWallpaperForCardAsync(ModelCard card)
    {
        _settingWallpaper = true;
        try
        {
            await _wallpaperService.SetSelectedCardWallpaperAsync(card, Settings);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            _settingWallpaper = false;
        }
    }

    private void RefreshCards(string? selectedIdOverride = null)
    {
        string? selectedId = selectedIdOverride ?? SelectedCard?.CardId;
        IReadOnlyList<ModelCard> queried = _queryService.QueryCards(_allCards, _userData, CurrentFilter, Settings, SearchText);
        _filteredCards = queried;

        Cards = queried.Select(CreateCardTile).ToList();
        _nowPlayingCard = null;
        OnPropertyChanged(nameof(CardsShownText));
        SelectedCard = Cards.FirstOrDefault(c => c.CardId == selectedId) ?? Cards.FirstOrDefault();
    }

    private CardTileViewModel CreateCardTile(ModelCard card)
    {
        return new CardTileViewModel
        {
            Card = card,
            CardId = card.Name,
            ModelName = card.ModelName ?? string.Empty,
            Outfit = card.Outfit,
            ImagePath = card.ImagePath,
            SortBadge = _queryService.GetSortBadge(card, _userData, Settings),
            UserRating = _userData.GetCardRating(card.Name),
            IsFavourite = _userData.GetCardFavourite(card.Name),
            IsExclusive = card.Exclusive == true,
            IsHotnessMax = card.HotnessLevel == "5",
            ShowRatingStars = Settings.ShowRatingStars
        };
    }

    private void LoadSelectedCardDetails()
    {
        Clips.Clear();
        if (SelectedCard?.Card == null)
        {
            OnSelectedCardChanged();
            return;
        }

        foreach (ModelClip clip in _queryService.QueryClips(SelectedCard.Card.Clips, Settings))
        {
            Clips.Add(new ClipViewModel(clip));
        }

        _userTags = string.Join(",", _userData.GetCardTags(SelectedCard.Card.Name));
        OnSelectedCardChanged();
    }

    private void OnSelectedCardChanged()
    {
        OnPropertyChanged(nameof(SelectedCardTitle));
        OnPropertyChanged(nameof(SelectedDescription));
        OnPropertyChanged(nameof(SelectedTags));
        OnPropertyChanged(nameof(UserTags));
        OnPropertyChanged(nameof(SelectedCollection));
        OnPropertyChanged(nameof(SelectedResolution));
        OnPropertyChanged(nameof(SelectedRating));
        OnPropertyChanged(nameof(SelectedCollectionText));
        OnPropertyChanged(nameof(SelectedResolutionText));
        OnPropertyChanged(nameof(SelectedRatingText));
        OnPropertyChanged(nameof(SelectedAge));
        OnPropertyChanged(nameof(SelectedStats));
    }

    private void RefreshFilterNames()
    {
        FilterNames.Clear();
        foreach (string name in _filters.Keys.OrderBy(x => x))
        {
            FilterNames.Add(name);
        }
    }
}
