using IStripperQuickPlayer.WinUI.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using Windows.Foundation;

namespace IStripperQuickPlayer.WinUI.Controls;

public sealed partial class CardTile : UserControl
{
    public static readonly DependencyProperty TileProperty =
        DependencyProperty.Register(
            nameof(Tile),
            typeof(CardTileViewModel),
            typeof(CardTile),
            new PropertyMetadata(new CardTileViewModel(), OnTileChanged));

    public static readonly DependencyProperty CardScaleProperty =
        DependencyProperty.Register(
            nameof(CardScale),
            typeof(double),
            typeof(CardTile),
            new PropertyMetadata(1.0, OnScaleChanged));

    public static readonly DependencyProperty HoverZoomRatioProperty =
        DependencyProperty.Register(
            nameof(HoverZoomRatio),
            typeof(double),
            typeof(CardTile),
            new PropertyMetadata(0.1));

    public event EventHandler<RatingChangedEventArgs>? RatingChanged;
    public event EventHandler<CardTileEventArgs>? CardSelected;
    public event EventHandler<CardTileEventArgs>? CardDoubleClicked;
    public event EventHandler<CardTileContextRequestedEventArgs>? CardContextRequested;
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    public CardTile()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Bindings.Update();
        _hoverTimer.Tick += HoverTimer_Tick;
    }

    public CardTileViewModel Tile
    {
        get => (CardTileViewModel)GetValue(TileProperty);
        set => SetValue(TileProperty, value);
    }

    public double CardScale
    {
        get => (double)GetValue(CardScaleProperty);
        set => SetValue(CardScaleProperty, value);
    }

    public double HoverZoomRatio
    {
        get => (double)GetValue(HoverZoomRatioProperty);
        set => SetValue(HoverZoomRatioProperty, value);
    }

    public double TileWidth => 162 * CardScale;

    public double TileHeight => 242 * CardScale;

    public double StarFontSize => 14 * CardScale;

    public double OverlayIconSize => 10 * CardScale;

    public double FavouriteIconSize => 14 * CardScale;

    public double BadgeFontSize => 13 * CardScale;

    public double PlayingFontSize => 14 * CardScale;

    public double LabelFontSize => 10;

    public double OutfitFontSize => 9;

    public Brush SelectionBrush => Tile.IsSelected
        ? new SolidColorBrush(ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 40, 80, 100)
            : Windows.UI.Color.FromArgb(255, 152, 251, 152))
        : new SolidColorBrush(Colors.Transparent);

    public Brush CardSurfaceBrush => new SolidColorBrush(ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(255, 40, 40, 40)
        : Windows.UI.Color.FromArgb(255, 245, 245, 245));

    public Visibility FavouriteVisibility => Tile.IsFavourite ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExclusiveVisibility => Tile.IsExclusive ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HotnessVisibility => Tile.IsHotnessMax ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NowPlayingVisibility => Tile.IsNowPlaying ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StarsVisibility => Tile.ShowRatingStars ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SortBadgeVisibility => string.IsNullOrWhiteSpace(Tile.SortBadge) ? Visibility.Collapsed : Visibility.Visible;

    public string RatingGlyphs
    {
        get
        {
            int halfSteps = (int)Math.Clamp(Tile.UserRating, 0, 10);
            int wholeStars = halfSteps / 2;
            bool hasHalfStar = halfSteps % 2 > 0;

            return new string('\uE0B4', wholeStars) + (hasHalfStar ? "\uE7C6" : string.Empty);
        }
    }

    private static void OnTileChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        CardTile cardTile = (CardTile)dependencyObject;
        if (args.OldValue is INotifyPropertyChanged oldTile)
        {
            oldTile.PropertyChanged -= cardTile.Tile_PropertyChanged;
        }

        if (args.NewValue is INotifyPropertyChanged newTile)
        {
            newTile.PropertyChanged += cardTile.Tile_PropertyChanged;
        }

        cardTile.Bindings.Update();
    }

    private static void OnScaleChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((CardTile)dependencyObject).Bindings.Update();
    }

    private void StarsHitTarget_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pointerPosition = e.GetCurrentPoint(StarsHitTarget).Position;
        decimal rating = RatingHitTest.FromPointerX(pointerPosition.X, StarsHitTarget.ActualWidth);
        RatingChanged?.Invoke(this, new RatingChangedEventArgs(Tile.CardId, rating));
        e.Handled = true;
    }

    private void Tile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Bindings.Update();
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (HoverZoomRatio <= 0)
        {
            return;
        }

        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void HoverTimer_Tick(object? sender, object e)
    {
        _hoverTimer.Stop();
        double scale = 1 + HoverZoomRatio;
        HoverTransform.CenterX = ActualWidth / 2;
        HoverTransform.CenterY = ActualHeight / 2;
        HoverTransform.ScaleX = scale;
        HoverTransform.ScaleY = scale;
        ApplyHoverViewportClamp(scale);
        Canvas.SetZIndex(this, 1000);
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoverTimer.Stop();
        HoverTransform.ScaleX = 1;
        HoverTransform.ScaleY = 1;
        HoverTransform.TranslateX = 0;
        HoverTransform.TranslateY = 0;
        Canvas.SetZIndex(this, 0);
    }

    private void ApplyHoverViewportClamp(double scale)
    {
        ScrollViewer? scrollViewer = FindAncestor<ScrollViewer>(this);
        if (scrollViewer == null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        Rect bounds = TransformToVisual(scrollViewer).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
        double extraX = (ActualWidth * scale - ActualWidth) / 2;
        double extraY = (ActualHeight * scale - ActualHeight) / 2;
        double translateX = 0;
        double translateY = 0;

        if (bounds.Left - extraX < 0)
        {
            translateX = extraX - bounds.Left;
        }
        else if (bounds.Right + extraX > scrollViewer.ActualWidth)
        {
            translateX = scrollViewer.ActualWidth - bounds.Right - extraX;
        }

        if (bounds.Top - extraY < 0)
        {
            translateY = extraY - bounds.Top;
        }
        else if (bounds.Bottom + extraY > scrollViewer.ActualHeight)
        {
            translateY = scrollViewer.ActualHeight - bounds.Bottom - extraY;
        }

        HoverTransform.TranslateX = translateX;
        HoverTransform.TranslateY = translateY;
    }

    private static T? FindAncestor<T>(DependencyObject start)
        where T : DependencyObject
    {
        DependencyObject current = start;
        while (true)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(current);
            if (parent == null)
            {
                return null;
            }

            if (parent is T typed)
            {
                return typed;
            }

            current = parent;
        }
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        CardDoubleClicked?.Invoke(this, new CardTileEventArgs(Tile));
        e.Handled = true;
    }

    private void Root_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        CardContextRequested?.Invoke(this, new CardTileContextRequestedEventArgs(Tile, Root, e.GetPosition(Root)));
        e.Handled = true;
    }

    private void Root_Tapped(object sender, TappedRoutedEventArgs e)
    {
        CardSelected?.Invoke(this, new CardTileEventArgs(Tile));
        e.Handled = true;
    }
}

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
    public CardTileContextRequestedEventArgs(CardTileViewModel tile, FrameworkElement target, Point position)
    {
        Tile = tile;
        Target = target;
        Position = position;
    }

    public CardTileViewModel Tile { get; }

    public FrameworkElement Target { get; }

    public Point Position { get; }
}
