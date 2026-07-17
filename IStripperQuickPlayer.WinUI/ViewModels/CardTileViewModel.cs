using IStripperQuickPlayer.WinUI.Core;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IStripperQuickPlayer.WinUI.ViewModels;

public sealed class CardTileViewModel : ObservableObject
{
    private decimal _userRating;
    private bool _isSelected;
    private bool _isFavourite;
    private bool _isNowPlaying;
    private BitmapImage? _imageSource;
    private bool _imageSourceLoaded;

    public ModelCard? Card { get; init; }

    public string CardId { get; init; } = string.Empty;

    public string ModelName { get; init; } = string.Empty;

    public string Outfit { get; init; } = string.Empty;

    public string? ImagePath { get; init; }

    public BitmapImage? ImageSource
    {
        get
        {
            if (_imageSourceLoaded)
            {
                return _imageSource;
            }

            _imageSourceLoaded = true;
            if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath))
            {
                return null;
            }

            _imageSource = new BitmapImage
            {
                DecodePixelWidth = 180,
                UriSource = new Uri(ImagePath)
            };
            return _imageSource;
        }
    }

    public string? SortBadge { get; init; }

    public decimal UserRating
    {
        get => _userRating;
        set => SetProperty(ref _userRating, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsFavourite
    {
        get => _isFavourite;
        set => SetProperty(ref _isFavourite, value);
    }

    public bool IsExclusive { get; init; }

    public bool IsHotnessMax { get; init; }

    public bool IsNowPlaying
    {
        get => _isNowPlaying;
        set => SetProperty(ref _isNowPlaying, value);
    }

    public bool ShowRatingStars { get; init; } = true;
}
