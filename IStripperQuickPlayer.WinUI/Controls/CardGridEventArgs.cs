using IStripperQuickPlayer.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace IStripperQuickPlayer.WinUI.Controls;

public sealed class RatingChangedEventArgs : EventArgs
{
    public RatingChangedEventArgs(string cardId, decimal rating)
    {
        CardId = cardId;
        Rating = rating;
    }

    public string CardId { get; }

    public decimal Rating { get; }
}

public sealed class CardTileEventArgs : EventArgs
{
    public CardTileEventArgs(CardTileViewModel tile)
    {
        Tile = tile;
    }

    public CardTileViewModel Tile { get; }
}

public sealed class CardTileContextRequestedEventArgs : EventArgs
{
    public CardTileContextRequestedEventArgs(
        CardTileViewModel tile, FrameworkElement target, Point position)
    {
        Tile = tile;
        Target = target;
        Position = position;
    }

    public CardTileViewModel Tile { get; }

    public FrameworkElement Target { get; }

    public Point Position { get; }
}
