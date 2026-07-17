using System.Collections;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using IStripperQuickPlayer.WinUI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace IStripperQuickPlayer.WinUI.Controls;

public sealed partial class CardGrid : UserControl
{
    public static readonly DependencyProperty CardsProperty =
        DependencyProperty.Register(
            nameof(Cards),
            typeof(IEnumerable),
            typeof(CardGrid),
            new PropertyMetadata(Array.Empty<CardTileViewModel>(), OnCardsChanged));

    public static readonly DependencyProperty CardScaleProperty =
        DependencyProperty.Register(
            nameof(CardScale),
            typeof(double),
            typeof(CardGrid),
            new PropertyMetadata(1.0, OnCardScaleChanged));

    public static readonly DependencyProperty HoverZoomRatioProperty =
        DependencyProperty.Register(
            nameof(HoverZoomRatio),
            typeof(double),
            typeof(CardGrid),
            new PropertyMetadata(0.1));

    private readonly Dictionary<string, System.Drawing.Bitmap?> _imageCache = [];
    private readonly DispatcherQueueTimer _hoverTimer;
    private List<CardTileViewModel> _cards = [];
    private int _columns = 1;
    private double _tileWidth = 162;
    private double _tileHeight = 242;
    private double _cellWidth = 178;
    private double _cellHeight = 258;
    private int _hoverIndex = -1;
    private DateTimeOffset _lastRender = DateTimeOffset.MinValue;
    private bool _renderQueued;
    private WriteableBitmap? _viewportBitmap;

    public event EventHandler<RatingChangedEventArgs>? RatingChanged;
    public event EventHandler<CardTileViewModel?>? SelectedCardChanged;
    public event EventHandler<CardTileEventArgs>? CardDoubleClicked;
    public event EventHandler<CardTileContextRequestedEventArgs>? CardContextRequested;

    public CardGrid()
    {
        InitializeComponent();
        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(180);
        _hoverTimer.Tick += HoverTimer_Tick;
        ActualThemeChanged += (_, _) => QueueRender();
        Loaded += (_, _) =>
        {
            UpdateMetrics();
            QueueRender();
        };
    }

    public IEnumerable Cards
    {
        get => (IEnumerable)GetValue(CardsProperty);
        set => SetValue(CardsProperty, value);
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

    public void EnsureCardVisible(string cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
        {
            return;
        }

        int index = _cards.FindIndex(card => card.CardId.Equals(cardId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        int row = index / _columns;
        double target = Math.Max(0, (row * _cellHeight) - ((ScrollHost.ViewportHeight - _cellHeight) / 2));
        ScrollHost.ChangeView(null, target, null, false);
        QueueRender();
    }

    private static void OnCardsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((CardGrid)dependencyObject).SetCards(args.NewValue as IEnumerable);
    }

    private static void OnCardScaleChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        CardGrid grid = (CardGrid)dependencyObject;
        grid.ClearImageCache();
        grid.UpdateMetrics();
        grid.QueueRender();
    }

    private void SetCards(IEnumerable? cards)
    {
        foreach (CardTileViewModel card in _cards)
        {
            card.PropertyChanged -= Card_PropertyChanged;
        }

        _cards = cards?.Cast<CardTileViewModel>().ToList() ?? [];
        foreach (CardTileViewModel card in _cards)
        {
            card.PropertyChanged += Card_PropertyChanged;
        }

        ClearImageCache();
        _hoverIndex = -1;
        UpdateMetrics();
        QueueRender();
    }

    private void Card_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CardTileViewModel.IsSelected)
            or nameof(CardTileViewModel.IsNowPlaying)
            or nameof(CardTileViewModel.IsFavourite)
            or nameof(CardTileViewModel.UserRating))
        {
            QueueRender();
        }
    }

    private void UpdateMetrics()
    {
        _tileWidth = 162 * CardScale;
        _tileHeight = 242 * CardScale;
        _cellWidth = _tileWidth + (16 * CardScale);
        _cellHeight = _tileHeight + (16 * CardScale);
        double width = Math.Max(1, ScrollHost.ViewportWidth > 0 ? ScrollHost.ViewportWidth : ActualWidth);
        _columns = Math.Max(1, (int)Math.Floor(width / _cellWidth));
        int rows = _cards.Count == 0 ? 0 : (int)Math.Ceiling(_cards.Count / (double)_columns);
        ContentCanvas.Width = width;
        ContentCanvas.Height = Math.Max(1, rows * _cellHeight);
    }

    private void QueueRender()
    {
        if (_renderQueued)
        {
            return;
        }

        _renderQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _renderQueued = false;
            RenderViewport();
        });
    }

    private void RenderViewport()
    {
        if (!IsLoaded)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        if ((now - _lastRender).TotalMilliseconds < 12)
        {
            QueueRender();
            return;
        }

        _lastRender = now;
        UpdateMetrics();

        int width = Math.Max(1, (int)Math.Ceiling(ScrollHost.ViewportWidth));
        int height = Math.Max(1, (int)Math.Ceiling(ScrollHost.ViewportHeight));
        double offsetY = ScrollHost.VerticalOffset;

        using System.Drawing.Bitmap bitmap = new(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(ToDrawingColor(ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 31, 31, 31)
            : Windows.UI.Color.FromArgb(255, 245, 245, 245)));
        graphics.InterpolationMode = CardScale <= 1.05 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int firstRow = Math.Max(0, (int)Math.Floor(offsetY / _cellHeight) - 1);
        int lastRow = Math.Min((int)Math.Ceiling((offsetY + height) / _cellHeight) + 1, _cards.Count == 0 ? 0 : ((_cards.Count - 1) / _columns) + 1);
        for (int row = firstRow; row < lastRow; row++)
        {
            for (int column = 0; column < _columns; column++)
            {
                int index = (row * _columns) + column;
                if (index >= _cards.Count)
                {
                    break;
                }

                DrawCard(graphics, _cards[index], index, column * _cellWidth + (8 * CardScale), (row * _cellHeight) - offsetY + (8 * CardScale));
            }
        }

        PublishBitmap(bitmap);
        Canvas.SetTop(ViewportImage, offsetY);
        ViewportImage.Width = width;
        ViewportImage.Height = height;
    }

    private void DrawCard(System.Drawing.Graphics graphics, CardTileViewModel tile, int index, double x, double y)
    {
        double scale = index == _hoverIndex && HoverZoomRatio > 0 ? 1 + HoverZoomRatio : 1;
        double drawWidth = _tileWidth * scale;
        double drawHeight = _tileHeight * scale;
        double drawX = x - ((drawWidth - _tileWidth) / 2);
        double drawY = y - ((drawHeight - _tileHeight) / 2);
        ClampHoveredCard(ref drawX, ref drawY, drawWidth, drawHeight);

        System.Drawing.RectangleF cardRect = new((float)drawX, (float)drawY, (float)drawWidth, (float)drawHeight);
        System.Drawing.Color surface = ActualTheme == ElementTheme.Dark
            ? System.Drawing.Color.FromArgb(40, 40, 40)
            : System.Drawing.Color.FromArgb(245, 245, 245);
        using System.Drawing.SolidBrush cardBrush = new(surface);
        graphics.FillRectangle(cardBrush, cardRect);

        if (tile.IsSelected)
        {
            using System.Drawing.Pen selectedPen = new(ActualTheme == ElementTheme.Dark
                ? System.Drawing.Color.FromArgb(40, 120, 150)
                : System.Drawing.Color.PaleGreen, Math.Max(3, (float)(3 * CardScale)));
            graphics.DrawRectangle(selectedPen, cardRect.X + 1, cardRect.Y + 1, cardRect.Width - 2, cardRect.Height - 2);
        }

        float labelHeight = (float)(34 * CardScale * scale);
        System.Drawing.RectangleF imageRect = new(cardRect.X + 3, cardRect.Y + 3, cardRect.Width - 6, cardRect.Height - labelHeight - 6);
        System.Drawing.Bitmap? cardImage = GetCardImage(tile);
        if (cardImage != null)
        {
            graphics.DrawImage(cardImage, imageRect);
        }

        DrawOverlays(graphics, tile, cardRect, imageRect, scale);
        DrawLabels(graphics, tile, new System.Drawing.RectangleF(cardRect.X + 3, imageRect.Bottom, cardRect.Width - 6, labelHeight), scale);
    }

    private void ClampHoveredCard(ref double x, ref double y, double width, double height)
    {
        x = Math.Max(0, Math.Min(x, Math.Max(0, ScrollHost.ViewportWidth - width)));
        y = Math.Max(0, Math.Min(y, Math.Max(0, ScrollHost.ViewportHeight - height)));
    }

    private void DrawOverlays(System.Drawing.Graphics graphics, CardTileViewModel tile, System.Drawing.RectangleF cardRect, System.Drawing.RectangleF imageRect, double scale)
    {
        float iconSize = (float)(14 * CardScale * scale);
        using System.Drawing.Font iconFont = new("Segoe Fluent Icons", iconSize, System.Drawing.FontStyle.Bold);
        using System.Drawing.Brush yellow = new System.Drawing.SolidBrush(System.Drawing.Color.Yellow);
        using System.Drawing.Brush green = new System.Drawing.SolidBrush(System.Drawing.Color.LightGreen);
        float x = imageRect.X + 4;
        if (tile.IsExclusive)
        {
            graphics.DrawString("\uEC19", iconFont, yellow, x, imageRect.Y + 4);
            x += iconSize + 3;
        }

        if (tile.IsHotnessMax)
        {
            graphics.DrawString("\uEC8A", iconFont, yellow, x, imageRect.Y + 4);
        }

        if (tile.IsFavourite)
        {
            graphics.DrawString("\uE00B", iconFont, green, imageRect.Right - iconSize - 5, imageRect.Y + 4);
        }

        if (tile.IsNowPlaying)
        {
            using System.Drawing.Brush playingBrush = new System.Drawing.SolidBrush(System.Drawing.Color.Green);
            using System.Drawing.Brush textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using System.Drawing.Font font = new("Segoe UI", Math.Max(8, (float)(10 * CardScale * scale)), System.Drawing.FontStyle.Bold);
            System.Drawing.RectangleF banner = new(imageRect.X, imageRect.Y + (float)(58 * CardScale * scale), (float)(80 * CardScale * scale), (float)(22 * CardScale * scale));
            graphics.FillRectangle(playingBrush, banner);
            graphics.DrawString("Playing", font, textBrush, banner.X + 7, banner.Y + 1);
        }

        if (!string.IsNullOrWhiteSpace(tile.SortBadge))
        {
            using System.Drawing.Brush textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using System.Drawing.Font font = new("Verdana", Math.Max(8, (float)(11 * CardScale * scale)), System.Drawing.FontStyle.Regular);
            graphics.DrawString(tile.SortBadge, font, textBrush, imageRect.X + 18, imageRect.Y + 6);
        }

        if (tile.ShowRatingStars)
        {
            DrawStars(graphics, tile.UserRating, imageRect, scale);
        }
    }

    private void DrawStars(System.Drawing.Graphics graphics, decimal rating, System.Drawing.RectangleF imageRect, double scale)
    {
        float starSize = Math.Max(10, (float)(14 * CardScale * scale));
        string emptyStars = "\uE0B4\uE0B4\uE0B4\uE0B4\uE0B4";
        int halfSteps = (int)Math.Clamp(rating, 0, 10);
        int wholeStars = halfSteps / 2;
        bool hasHalfStar = halfSteps % 2 > 0;
        string filledStars = new string('\uE0B4', wholeStars) + (hasHalfStar ? "\uE7C6" : string.Empty);
        using System.Drawing.Font font = new("Segoe Fluent Icons", starSize, System.Drawing.FontStyle.Bold);
        using System.Drawing.Brush emptyBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(180, 0, 0, 0));
        using System.Drawing.Brush fillBrush = new System.Drawing.SolidBrush(System.Drawing.Color.Yellow);
        System.Drawing.SizeF size = graphics.MeasureString(emptyStars, font);
        float x = imageRect.X + ((imageRect.Width - size.Width) / 2);
        float y = imageRect.Y + ((imageRect.Height - size.Height) / 2);
        graphics.DrawString(emptyStars, font, emptyBrush, x, y);
        graphics.DrawString(filledStars, font, fillBrush, x, y);
    }

    private void DrawLabels(System.Drawing.Graphics graphics, CardTileViewModel tile, System.Drawing.RectangleF labelRect, double scale)
    {
        using System.Drawing.Brush textBrush = new System.Drawing.SolidBrush(ActualTheme == ElementTheme.Dark
            ? System.Drawing.Color.AntiqueWhite
            : System.Drawing.Color.Black);
        using System.Drawing.Font modelFont = new("Segoe UI", Math.Max(7, (float)(9 * CardScale * scale)));
        using System.Drawing.Font outfitFont = new("Segoe UI", Math.Max(7, (float)(8 * CardScale * scale)));
        using System.Drawing.StringFormat centered = new()
        {
            Alignment = System.Drawing.StringAlignment.Center,
            LineAlignment = System.Drawing.StringAlignment.Center,
            Trimming = System.Drawing.StringTrimming.EllipsisCharacter
        };
        System.Drawing.RectangleF top = new(labelRect.X, labelRect.Y, labelRect.Width, labelRect.Height / 2);
        System.Drawing.RectangleF bottom = new(labelRect.X, labelRect.Y + (labelRect.Height / 2), labelRect.Width, labelRect.Height / 2);
        graphics.DrawString(tile.ModelName, modelFont, textBrush, top, centered);
        graphics.DrawString(tile.Outfit, outfitFont, textBrush, bottom, centered);
    }

    private System.Drawing.Bitmap? GetCardImage(CardTileViewModel tile)
    {
        if (string.IsNullOrWhiteSpace(tile.ImagePath) || !File.Exists(tile.ImagePath))
        {
            return null;
        }

        if (_imageCache.TryGetValue(tile.ImagePath, out System.Drawing.Bitmap? cached))
        {
            return cached;
        }

        try
        {
            using System.Drawing.Bitmap source = new(tile.ImagePath);
            int width = Math.Max(1, (int)Math.Round(_tileWidth * 1.4));
            int height = Math.Max(1, (int)Math.Round((_tileHeight - (34 * CardScale)) * 1.4));
            System.Drawing.Bitmap resized = new(width, height);
            using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(resized);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, width, height));
            _imageCache[tile.ImagePath] = resized;
            TrimImageCache();
            return resized;
        }
        catch
        {
            _imageCache[tile.ImagePath] = null;
            return null;
        }
    }

    private void TrimImageCache()
    {
        if (_imageCache.Count <= 220)
        {
            return;
        }

        foreach (string key in _imageCache.Keys.Take(_imageCache.Count - 180).ToList())
        {
            _imageCache[key]?.Dispose();
            _imageCache.Remove(key);
        }
    }

    private void ClearImageCache()
    {
        foreach (System.Drawing.Bitmap? bitmap in _imageCache.Values)
        {
            bitmap?.Dispose();
        }

        _imageCache.Clear();
    }

    private void PublishBitmap(System.Drawing.Bitmap bitmap)
    {
        if (_viewportBitmap == null || _viewportBitmap.PixelWidth != bitmap.Width || _viewportBitmap.PixelHeight != bitmap.Height)
        {
            _viewportBitmap = new WriteableBitmap(bitmap.Width, bitmap.Height);
            ViewportImage.Source = _viewportBitmap;
        }

        BitmapData data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            int length = Math.Abs(data.Stride) * data.Height;
            byte[] pixels = new byte[length];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            using Stream stream = _viewportBitmap.PixelBuffer.AsStream();
            stream.Seek(0, SeekOrigin.Begin);
            stream.Write(pixels, 0, pixels.Length);
            _viewportBitmap.Invalidate();
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private int HitTestIndex(Point position)
    {
        int column = (int)(position.X / _cellWidth);
        int row = (int)(position.Y / _cellHeight);
        if (column < 0 || column >= _columns || row < 0)
        {
            return -1;
        }

        int index = (row * _columns) + column;
        return index >= 0 && index < _cards.Count ? index : -1;
    }

    private void ContentCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Point point = e.GetCurrentPoint(ContentCanvas).Position;
        int index = HitTestIndex(point);
        if (index < 0)
        {
            return;
        }

        CardTileViewModel tile = _cards[index];
        SelectedCardChanged?.Invoke(this, tile);
        decimal? rating = HitTestRating(index, point);
        if (rating != null)
        {
            RatingChanged?.Invoke(this, new RatingChangedEventArgs(tile.CardId, rating.Value));
            e.Handled = true;
        }
    }

    private decimal? HitTestRating(int index, Point position)
    {
        if (!_cards[index].ShowRatingStars)
        {
            return null;
        }

        int row = index / _columns;
        int column = index % _columns;
        double localX = position.X - (column * _cellWidth) - (8 * CardScale);
        double localY = position.Y - (row * _cellHeight) - (8 * CardScale);
        double imageHeight = _tileHeight - (34 * CardScale);
        double starWidth = 78 * CardScale;
        double starHeight = 24 * CardScale;
        double starLeft = (_tileWidth - starWidth) / 2;
        double starTop = (imageHeight - starHeight) / 2;
        if (localX < starLeft || localX > starLeft + starWidth || localY < starTop || localY > starTop + starHeight)
        {
            return null;
        }

        return RatingHitTest.FromPointerX(localX - starLeft, starWidth);
    }

    private void ContentCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        int index = HitTestIndex(e.GetPosition(ContentCanvas));
        if (index >= 0)
        {
            CardDoubleClicked?.Invoke(this, new CardTileEventArgs(_cards[index]));
            e.Handled = true;
        }
    }

    private void ContentCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        int index = HitTestIndex(e.GetPosition(ContentCanvas));
        if (index >= 0)
        {
            CardContextRequested?.Invoke(this, new CardTileContextRequestedEventArgs(_cards[index], ContentCanvas, e.GetPosition(ContentCanvas)));
            e.Handled = true;
        }
    }

    private void ContentCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        int index = HitTestIndex(e.GetCurrentPoint(ContentCanvas).Position);
        if (index == _hoverIndex)
        {
            return;
        }

        _hoverTimer.Stop();
        _hoverIndex = -1;
        if (index >= 0)
        {
            _hoverIndex = index;
            _hoverTimer.Start();
        }
        else
        {
            QueueRender();
        }
    }

    private void ContentCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoverTimer.Stop();
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            QueueRender();
        }
    }

    private void HoverTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        QueueRender();
    }

    private void ScrollHost_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        QueueRender();
    }

    private void ScrollHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateMetrics();
        QueueRender();
    }

    private static System.Drawing.Color ToDrawingColor(Windows.UI.Color color)
    {
        return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }
}
