using IStripperQuickPlayer.DataModel;
using Manina.Windows.Forms;
using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using View = Manina.Windows.Forms.View;

namespace IStripperQuickPlayer.BLL
{
    public class CardRenderer : ImageListView.ImageListViewRenderer
    {
        private const float DesignCardWidth = 162f;

        internal readonly record struct GpuCardVisual(
            ModelCard Card,
            Rectangle Bounds,
            Rectangle ImageBounds,
            bool DrawText,
            bool Selected,
            string SortText,
            decimal MyRating,
            float NameFontSize,
            float OutfitFontSize,
            float SortFontSize,
            float PlayingFontSize,
            Rectangle NameBounds,
            Rectangle OutfitBounds,
            Rectangle SortBounds,
            Rectangle PlayingBounds);

        internal MyData? myData = null;
        internal float cardScale = 1.0f;
        internal string sortBy = "";
        internal CultureInfo culture = CultureInfo.CurrentCulture;
        internal string nowPlayingTag = "";
        internal NumberStyles style = NumberStyles.AllowDecimalPoint;
        internal bool updating = false;
        internal float mZoomRatio = 0.2f;
        internal bool MouseIsOnList;
        internal string? CardMenuText;
        public Color labelColor = Color.Black;
        public SolidBrush highlightBrush = new SolidBrush(Color.PaleGreen);
        public Color backgroundColour = Color.WhiteSmoke;
        private readonly Dictionary<int, Rectangle> _boundsByIndex = [];
        private readonly Dictionary<int, Rectangle> _imageBoundsByIndex = [];
        private readonly Dictionary<int, Rectangle> _starBoundsByIndex = [];
        private readonly Dictionary<int, GpuCardVisual> gpuCards = [];
        private readonly SolidBrush labelBrush = new(Color.Black);
        private readonly SolidBrush overlayShadowBrush =
            new(Color.FromArgb(75, Color.Black));
        private readonly SolidBrush overlayBackgroundBrush =
            new(Color.FromArgb(175, 18, 18, 18));
        private readonly Pen overlayBorderPen =
            new(Color.FromArgb(70, Color.White), 1);
        private readonly Dictionary<(string Family, int Size, FontStyle Style),
            Font> fonts = [];
        private readonly Dictionary<(string Text, string Family, int MaxSize,
            int Width, FontStyle Style), Font> fittedFonts = [];
        private readonly Dictionary<(string Kind, int Size, int Value),
            Bitmap> iconBitmaps = [];
        private bool disposed;
        internal bool DrawWithDirectComposition { get; set; }
        private readonly StringFormat centeredText = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        internal CardRenderer(MyData? myData, string sortBy, float cardScale,
            CultureInfo culture, NumberStyles style)
        {
            this.cardScale = cardScale;
            this.myData = myData;
            this.sortBy = sortBy;
            this.culture = culture;
            this.style = style;
            this.Clip = false;
            SetColours();
        }

        public void SetColours()
        {
            if (Properties.Settings.Default.DarkMode)
            {
                labelColor = Color.AntiqueWhite;
                highlightBrush.Color = Color.FromArgb(40, 80, 100);
                backgroundColour = Color.FromArgb(40, 40, 40);
            }
            else
            {
                labelColor = Color.Black;
                highlightBrush.Color = Color.PaleGreen;
                backgroundColour = Color.WhiteSmoke;
            }
            labelBrush.Color = labelColor;
        }

        internal void SetCardScale(float scale)
        {
            if (Math.Abs(cardScale - scale) < 0.001f)
                return;
            cardScale = scale;
            fittedFonts.Clear();
            ClearIconBitmaps();
        }

        internal static float CardPixels(
            Rectangle imageBounds, float pixelsAtDesignSize) =>
            imageBounds.Width * pixelsAtDesignSize / DesignCardWidth;

        internal static float CardFontPoints(
            Rectangle cardBounds, float pixelsAtDesignSize, float dpi) =>
            CardPixels(cardBounds, pixelsAtDesignSize) * 72f /
            Math.Max(1, dpi);

        internal static int CardImageBottomInset(Rectangle bounds) =>
            (int)Math.Round(CardPixels(bounds, 40));

        internal static int CardHorizontalMargin(int cardWidth) =>
            Math.Max(5, (int)Math.Round(
                cardWidth * 6f / DesignCardWidth));

        internal static bool VerifyRelativeMetrics()
        {
            Rectangle at100 = new(0, 0, 108, 161);
            Rectangle at150 = new(0, 0, 162, 242);
            Rectangle at200 = new(0, 0, 216, 323);
            float ratio = CardPixels(at150, 28) / at150.Width;
            float fontRatio =
                CardFontPoints(at150, 20, 144) * 144 / 72 / at150.Width;
            return Math.Abs(CardPixels(at100, 28) / at100.Width - ratio)
                    < 0.0001f &&
                Math.Abs(CardPixels(at200, 28) / at200.Width - ratio)
                    < 0.0001f &&
                Math.Abs(CardFontPoints(at100, 20, 96) * 96 / 72 /
                    at100.Width - fontRatio) < 0.0001f &&
                Math.Abs(CardFontPoints(at200, 20, 192) * 192 / 72 /
                    at200.Width - fontRatio) < 0.0001f &&
                Math.Abs(CardFontPoints(at100, 20, 96) - 10)
                    < 0.0001f &&
                Math.Abs(CardFontPoints(at150, 20, 144) - 10)
                    < 0.0001f &&
                Math.Abs(CardFontPoints(at200, 20, 192) - 10)
                    < 0.0001f &&
                CardImageBottomInset(at100) -
                    (int)Math.Round(CardPixels(at100, 39)) == 1 &&
                CardImageBottomInset(at150) -
                    (int)Math.Round(CardPixels(at150, 39)) == 1 &&
                CardImageBottomInset(at200) -
                    (int)Math.Round(CardPixels(at200, 39)) == 1 &&
                CardHorizontalMargin(at100.Width) == 5 &&
                CardHorizontalMargin(at150.Width) == 6 &&
                CardHorizontalMargin(at200.Width) == 8;
        }

        private Font GetFont(string family, int size,
            FontStyle style = FontStyle.Regular)
        {
            var key = (Family: family, Size: Math.Max(1, size), Style: style);
            if (!fonts.TryGetValue(key, out Font? font))
            {
                font = new Font(key.Family, key.Size, key.Style);
                fonts.Add(key, font);
            }
            return font;
        }

        private Font GetFittedFont(Graphics graphics, string text,
            string family, int maximumSize, int width,
            FontStyle style = FontStyle.Regular)
        {
            var key = (text, family, maximumSize, Math.Max(1, width), style);
            if (fittedFonts.TryGetValue(key, out Font? fitted))
                return fitted;

            int size = Math.Max(1, maximumSize);
            fitted = GetFont(family, size, style);
            while (size > 1 &&
                graphics.MeasureString(text, fitted).Width > width)
            {
                fitted = GetFont(family, --size, style);
            }
            fittedFonts[key] = fitted;
            return fitted;
        }

        private void ClearIconBitmaps()
        {
            foreach (Bitmap bitmap in iconBitmaps.Values)
                bitmap.Dispose();
            iconBitmaps.Clear();
        }

        internal Bitmap GetIconBitmap(string kind, int size, int value = 0)
        {
            size = Math.Max(1, size);
            var key = (kind, size, value);
            if (iconBitmaps.TryGetValue(key, out Bitmap? bitmap))
                return bitmap;

            int width = kind == "rating" ? size * 5 : size;
            bitmap = new Bitmap(width, size, PixelFormat.Format32bppPArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            switch (kind)
            {
                case "rating":
                    DrawRatingStarsBitmap(graphics, size, value);
                    break;
                case "heart":
                    using (GraphicsPath path = CreateHeartPath(
                        new RectangleF(1.5f, 1.5f,
                            Math.Max(1, size - 3), Math.Max(1, size - 3))))
                    using (Pen outline = new(Color.Black, 3))
                    {
                        graphics.DrawPath(outline, path);
                        graphics.FillPath(Brushes.LightGreen, path);
                    }
                    break;
                case "special":
                    DrawSpecialIcon(graphics,
                        new RectangleF(0, 0, size, size));
                    break;
                case "hotness":
                    DrawHotnessIcon(graphics,
                        new RectangleF(0, 0, size, size));
                    break;
            }
            iconBitmaps[key] = bitmap;
            return bitmap;
        }

        private static PointF[] StarPoints(RectangleF bounds)
        {
            PointF[] points = new PointF[10];
            float centerX = bounds.Left + bounds.Width / 2;
            float centerY = bounds.Top + bounds.Height / 2;
            float outerRadius = Math.Max(1,
                Math.Min(bounds.Width, bounds.Height) / 2);
            float innerRadius = outerRadius * 0.48f;
            for (int i = 0; i < points.Length; i++)
            {
                double angle = -Math.PI / 2 + i * Math.PI / 5;
                float radius = i % 2 == 0 ? outerRadius : innerRadius;
                points[i] = new PointF(
                    centerX + radius * (float)Math.Cos(angle),
                    centerY + radius * (float)Math.Sin(angle));
            }
            return points;
        }

        private static GraphicsPath CreateHeartPath(RectangleF bounds)
        {
            GraphicsPath heart = new();
            PointF Point(float x, float y) => new(
                bounds.Left + bounds.Width * x,
                bounds.Top + bounds.Height * y);
            heart.StartFigure();
            heart.AddBezier(
                Point(0.50f, 0.95f), Point(0.44f, 0.86f),
                Point(0.05f, 0.62f), Point(0.05f, 0.34f));
            heart.AddBezier(
                Point(0.05f, 0.34f), Point(0.05f, 0.10f),
                Point(0.34f, -0.02f), Point(0.50f, 0.20f));
            heart.AddBezier(
                Point(0.50f, 0.20f), Point(0.66f, -0.02f),
                Point(0.95f, 0.10f), Point(0.95f, 0.34f));
            heart.AddBezier(
                Point(0.95f, 0.34f), Point(0.95f, 0.62f),
                Point(0.56f, 0.86f), Point(0.50f, 0.95f));
            heart.CloseFigure();
            return heart;
        }

        private static GraphicsPath CreateRoundedCardPath(
            Rectangle bounds, int radius)
        {
            GraphicsPath path = new();
            int diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top,
                diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top,
                diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter,
                bounds.Bottom - diameter,
                diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter,
                diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawHotnessIcon(
            Graphics graphics, RectangleF bounds)
        {
            float size = Math.Min(bounds.Width, bounds.Height);
            PointF center = new(
                bounds.Left + bounds.Width / 2,
                bounds.Top + bounds.Height / 2);
            using Pen black = new(Color.Black, Math.Max(1, size * 0.13f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using Pen yellow = new(Color.Yellow, Math.Max(1, size * 0.06f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                PointF start = new(
                    center.X + size * 0.34f * (float)Math.Cos(angle),
                    center.Y + size * 0.34f * (float)Math.Sin(angle));
                PointF end = new(
                    center.X + size * 0.47f * (float)Math.Cos(angle),
                    center.Y + size * 0.47f * (float)Math.Sin(angle));
                graphics.DrawLine(black, start, end);
                graphics.DrawLine(yellow, start, end);
            }
            RectangleF sun = new(
                center.X - size * 0.22f, center.Y - size * 0.22f,
                size * 0.44f, size * 0.44f);
            graphics.DrawEllipse(black, sun);
            graphics.DrawEllipse(yellow, sun);
        }

        private static void DrawSpecialIcon(
            Graphics graphics, RectangleF bounds)
        {
            float size = Math.Min(bounds.Width, bounds.Height);
            PointF Point(float x, float y) => new(
                bounds.Left + size * x,
                bounds.Top + size * y);

            using GraphicsPath crown = new();
            crown.StartFigure();
            crown.AddPolygon(
            [
                Point(0.12f, 0.28f),
                Point(0.34f, 0.50f),
                Point(0.50f, 0.16f),
                Point(0.66f, 0.50f),
                Point(0.88f, 0.28f),
                Point(0.82f, 0.78f),
                Point(0.18f, 0.78f)
            ]);
            crown.CloseFigure();
            using Pen outline = new(
                Color.Black, Math.Max(1, size * 0.06f))
            {
                LineJoin = LineJoin.Round
            };
            graphics.FillPath(Brushes.Yellow, crown);
            graphics.DrawPath(outline, crown);
        }

        private void DrawOverlayLabel(Graphics graphics, string text,
            Font font, Rectangle bounds)
        {
            SizeF textSize = graphics.MeasureString(text, font);
            int padding = Math.Max(2, (int)(font.GetHeight(graphics) * .25f));
            int width = Math.Min(bounds.Width,
                (int)Math.Ceiling(textSize.Width) + padding * 2);
            int height = Math.Min(bounds.Height,
                (int)Math.Ceiling(textSize.Height) + padding);
            Rectangle panel = new(
                bounds.Left + (bounds.Width - width) / 2,
                bounds.Top, width, height);
            int radius = Math.Max(2, panel.Height / 4);
            int shadowOffset = Math.Max(1, panel.Height / 12);

            Utility.FillRoundedRectangle(graphics, overlayShadowBrush,
                new Rectangle(panel.X + shadowOffset,
                    panel.Y + shadowOffset, panel.Width, panel.Height),
                radius);
            Utility.FillRoundedRectangle(
                graphics, overlayBackgroundBrush, panel, radius);
            Utility.DrawRoundedRectangle(
                graphics, overlayBorderPen, panel, radius);
            graphics.DrawString(text, font, Brushes.White, panel,
                centeredText);
        }

        private void DrawRatingStars(Graphics graphics, Rectangle imageBounds,
            int rating, int itemIndex)
        {
            int size = Math.Max(1, (int)Math.Round(Math.Min(
                CardPixels(imageBounds, 28), imageBounds.Width / 5f)));
            float rowWidth = size * 5;
            float left = imageBounds.Left +
                (imageBounds.Width - rowWidth) / 2;
            float top = imageBounds.Top + imageBounds.Height / 2 +
                size / 2;
            _starBoundsByIndex[itemIndex] = Rectangle.Round(
                new RectangleF(left, top, rowWidth, size));

            graphics.DrawImageUnscaled(
                GetIconBitmap("rating", size, Math.Clamp(rating, 0, 10)),
                (int)Math.Round(left), (int)Math.Round(top));
        }

        private static void DrawRatingStarsBitmap(
            Graphics graphics, int size, int rating)
        {
            using SolidBrush blankBrush =
                new(Color.FromArgb(180, Color.Black));
            using Pen outline = new(Color.Black, 3);
            int halfStars = Math.Clamp(rating, 0, 10);
            for (int i = 0; i < 5; i++)
            {
                RectangleF starBounds = new(
                    i * size + 1.5f, 1.5f,
                    Math.Max(1, size - 3), Math.Max(1, size - 3));
                using GraphicsPath star = new();
                star.AddPolygon(StarPoints(starBounds));
                graphics.FillPath(blankBrush, star);

                int fill = halfStars - i * 2;
                if (fill <= 0)
                    continue;

                graphics.DrawPath(outline, star);
                if (fill == 1)
                {
                    GraphicsState state = graphics.Save();
                    graphics.SetClip(new RectangleF(
                        starBounds.Left - 2, starBounds.Top - 2,
                        starBounds.Width / 2 + 2,
                        starBounds.Height + 4));
                    graphics.FillPath(Brushes.Yellow, star);
                    graphics.Restore(state);
                }
                else
                {
                    graphics.FillPath(Brushes.Yellow, star);
                }
            }
        }

        public bool TryGetItemBounds(int itemIndex, out Rectangle bounds)
            => _boundsByIndex.TryGetValue(itemIndex, out bounds);

        public bool TryGetImageBounds(int itemIndex, out Rectangle bounds)
            => _imageBoundsByIndex.TryGetValue(itemIndex, out bounds);

        public bool TryGetStarItemBounds(int itemIndex, out Rectangle bounds)
             => _starBoundsByIndex.TryGetValue(itemIndex, out bounds);

        internal bool TryGetGpuCard(
            int itemIndex, out GpuCardVisual visual)
            => gpuCards.TryGetValue(itemIndex, out visual);

        internal void SetGpuStarBounds(int itemIndex, Rectangle bounds)
            => _starBoundsByIndex[itemIndex] = bounds;

        //public override void DrawBackground(Graphics g, Rectangle bounds)
        //{
        //    //base.DrawBackground(g, bounds);
        //    g.FillRectangle(new SolidBrush(backgroundColour), bounds);
        //}

        public override void InitializeGraphics(Graphics g)
        {
            base.InitializeGraphics(g);
            ItemDrawOrder = ItemDrawOrder.NormalSelectedHovered;
            g.InterpolationMode = InterpolationMode.Default;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;
        }

        public override System.Drawing.Size MeasureItemMargin(View view)
        {
            System.Drawing.Size margin = base.MeasureItemMargin(view);
            if (view == View.Thumbnails)
                margin.Width = CardHorizontalMargin(
                    ImageListView.ThumbnailSize.Width);
            return margin;
        }

        private string GetSortText(ModelCard card, decimal myrating)
        {
            switch (sortBy)
            {
                case "My Rating":
                    return !Properties.Settings.Default.ShowRatingStars &&
                        myrating > 0 ? myrating.ToString() : "";
                case "Height":
                    decimal height;
                    decimal.TryParse(
                        card.height, style, culture, out height);
                    return RegionInfo.CurrentRegion.IsMetric &&
                        CultureInfo.CurrentCulture.Name != "en-GB"
                        ? (((Math.Floor(height) * 12) +
                            (height - Math.Floor(height)) * 10) *
                            2.54M).ToString("N1") + "cm"
                        : Math.Floor(height) + "'" +
                            (int)(24 * (height - Math.Floor(height))) /
                            2.0M + "''";
                case "":
                case "Model Name":
                    return "";
                case "Rating":
                    return (Convert.ToDecimal(card.rating) - 5m).ToString();
                case "Age":
                    return card.modelAge.ToString() ?? "";
                case "Ethnicity":
                    return card.ethnicity ?? "";
                case "Breast Size":
                case "Breast Size (Descending)":
                    return (card.bust ?? 0).ToString();
                case "Waist":
                case "Waist (Descending)":
                    return (card.waist ?? 0).ToString();
                case "Hips":
                case "Hips (Descending)":
                    return (card.hips ?? 0).ToString();
                case "Date Purchased":
                case "Date Purchased (Descending)":
                    return card.datePurchased?.ToShortDateString() ?? "";
                case "Release Date":
                case "Release Date (Descending)":
                    return card.dateReleased.ToShortDateString();
                default:
                    return "";
            }
        }

        public override void DrawItem(Graphics g, ImageListViewItem item, ItemState state, Rectangle bounds)
        {
            _boundsByIndex[item.Index] = bounds;
            if (updating) return;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;            
            if (ImageListView.View == View.Thumbnails)
            {
                Rectangle controlBounds = ClientBounds;
                bool drawText = true;
                // Zoom on mouse over
                if (((MouseIsOnList && (state & ItemState.Hovered) != ItemState.None) || CardMenuText == item.Tag.ToString()) && mZoomRatio != 0.0f)
                {
                    bounds.Inflate((int)(bounds.Width * mZoomRatio), (int)(bounds.Height * mZoomRatio));
                    if (bounds.Bottom > controlBounds.Bottom)
                        bounds.Y = controlBounds.Bottom - bounds.Height + 34;
                    if (bounds.Top < controlBounds.Top)
                        bounds.Y = controlBounds.Top;
                    if (bounds.Right > controlBounds.Right)
                        bounds.X = controlBounds.Right - bounds.Width;
                    if (bounds.Left < controlBounds.Left)
                        bounds.X = controlBounds.Left;
                    drawText = false;
                }

                ModelCard? card = Datastore.findCardByTag(item.Tag.ToString());
                if (card == null) return;
                decimal myrating = myData?.GetCardRating(card.name) ?? 0M;
                string text = drawText &&
                    Properties.Settings.Default.ShowCardSortLabels
                    ? GetSortText(card, myrating) : "";
                Rectangle imgrect = bounds;
                Rectangle imgrect2 = bounds;
                if (card.image != null)
                {
                    double ratio =
                        (1.0 * card.image.Width) / card.image.Height;
                    int dy = CardImageBottomInset(bounds);
                    int dx = (int)(bounds.Width -
                        ((bounds.Height - 34) * ratio)) / 2;
                    imgrect2 = new Rectangle(
                        bounds.Left + dx, bounds.Top,
                        bounds.Width - dx * 2, bounds.Height - dy);
                    _imageBoundsByIndex[item.Index] = imgrect2;
                }
                float namePoints = CardFontPoints(bounds, 20, g.DpiY);
                float outfitPoints = CardFontPoints(bounds, 18, g.DpiY);
                float sortPoints = CardFontPoints(
                    imgrect2, 26, g.DpiY);
                float playingPoints = CardFontPoints(
                    imgrect2, 28, g.DpiY);
                float nameFontSize = drawText
                    ? GetFittedFont(g, card.modelName ?? "", "Segoe UI",
                        (int)Math.Round(namePoints),
                        bounds.Width).SizeInPoints : namePoints;
                float outfitFontSize = drawText
                    ? GetFittedFont(g, card.outfit ?? "", "Segoe UI",
                        (int)Math.Round(outfitPoints),
                        bounds.Width).SizeInPoints : outfitPoints;
                int sortLeft = (int)Math.Round(
                    CardPixels(imgrect2, 36));
                int sortWidth = Math.Max(1, bounds.Width -
                    (int)Math.Round(CardPixels(imgrect2, 43.5f)));
                float sortFontSize = text.Length == 0 ? sortPoints :
                    GetFittedFont(g, text, "Verdana",
                        (int)Math.Round(sortPoints),
                        sortWidth).SizeInPoints;
                float playingFontSize = GetFittedFont(
                    g, "Playing", "Verdana",
                    (int)Math.Round(playingPoints),
                    Math.Max(1, (int)(imgrect.Width * .7f)))
                    .SizeInPoints;
                Rectangle nameBounds = new(
                    bounds.Left,
                    bounds.Bottom - (int)Math.Round(
                        CardPixels(bounds, 39)),
                    bounds.Width, (int)Math.Round(
                        CardPixels(bounds, 22.5f)));
                Rectangle outfitBounds = new(
                    bounds.Left,
                    bounds.Bottom - (int)Math.Round(
                        CardPixels(bounds, 20.25f)),
                    bounds.Width, (int)Math.Round(
                        CardPixels(bounds, 22.5f)));
                Rectangle sortBounds = new(
                    bounds.Left + sortLeft,
                    bounds.Top + (int)Math.Round(
                        CardPixels(imgrect2, 4.5f)),
                    sortWidth, (int)Math.Round(
                        CardPixels(imgrect2, 30)));
                Font playingFont = GetFont(
                    "Verdana", (int)Math.Round(playingFontSize));
                Rectangle playingBounds = new(
                    imgrect.Left,
                    bounds.Top + (int)Math.Round(
                        CardPixels(imgrect2, 60)),
                    (int)(imgrect.Width * .7),
                    (int)Math.Ceiling(
                        g.MeasureString("Playing", playingFont).Height));
                gpuCards[item.Index] = new GpuCardVisual(
                    card, bounds, imgrect2, drawText,
                    (state & ItemState.Selected) != ItemState.None,
                    text, myrating,
                    nameFontSize * g.DpiY / 72f,
                    outfitFontSize * g.DpiY / 72f,
                    sortFontSize * g.DpiY / 72f,
                    playingFontSize * g.DpiY / 72f,
                    nameBounds, outfitBounds,
                    sortBounds, playingBounds);
                if (DrawWithDirectComposition)
                    return;

                if((state & ItemState.Selected) != ItemState.None)
                {
                  if (drawText)
                    g.FillRectangle(highlightBrush, new Rectangle(bounds.Left-3,bounds.Top-3,bounds.Width+6,bounds.Height+6));
                  else
                    g.FillRectangle(highlightBrush, new Rectangle(bounds.Left-3,bounds.Top-3,bounds.Width+6,bounds.Height-34+6));
                }
                if (card.image != null)
                {
                    GraphicsState cardState = g.Save();
                    g.CompositingMode = CompositingMode.SourceCopy;
                    if (cardScale == 1) g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    if (cardScale > 1 || !drawText) g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    using GraphicsPath? cardPath =
                        Properties.Settings.Default.RoundCardCorners
                            ? CreateRoundedCardPath(
                                imgrect2,
                                Math.Max(2, (int)Math.Round(
                                    CardPixels(imgrect2, 9))))
                            : null;
                    if (cardPath != null)
                        g.SetClip(cardPath, CombineMode.Intersect);
                    g.DrawImage(card.image, imgrect2);

                   

                    //int reflectionHeight=34;
                    //Region prevClip = g.Clip;
                    //g.SetClip(new Rectangle(imgrect2.Left, imgrect2.Top + imgrect2.Height , imgrect2.Width, reflectionHeight));
                    //g.DrawImage(card.image, imgrect2.Left, imgrect2.Top + imgrect2.Height + imgrect2.Height / 2 , imgrect2.Width, -imgrect2.Height / 2);
                    //g.Clip = prevClip;
                    g.CompositingMode = CompositingMode.SourceOver;
                    //using (Brush brush = new LinearGradientBrush(
                    //    new Point(imgrect2.Left, imgrect2.Top + imgrect2.Height ), new Point(imgrect2.Left, imgrect2.Top + imgrect2.Height + reflectionHeight ),
                    //    Color.FromArgb(128, 0, 0, 0), Color.White))
                    //{
                    //    g.FillRectangle(brush, imgrect2.Left, imgrect2.Top + imgrect2.Height , imgrect2.Width, reflectionHeight);
                    //}
                    //Color c = Color.FromArgb(33, Color.PaleGreen);
                    //if((state & ItemState.Selected) != ItemState.None)
                    //    using (Brush brush = new LinearGradientBrush(
                    //       new Point(imgrect2.Left, imgrect2.Top), new Point(imgrect2.Left, imgrect2.Top + imgrect2.Height),
                    //       Color.FromArgb(0, 0, 0, 0), c))
                    //    {
                    //        g.FillRectangle(brush,imgrect2.Left, imgrect2.Top, imgrect2.Width, imgrect2.Height);
                    //    }
                    CardOverlayLoader.Draw(g, card, imgrect2);
                    g.Restore(cardState);
                }

                if (drawText)
                {
                    Rectangle rectName = nameBounds;
                    string name = card.modelName ?? "";
                    Font fontName = GetFittedFont(
                        g, name, "Segoe UI",
                        (int)Math.Round(namePoints),
                        rectName.Width);
                    g.DrawString(name, fontName, labelBrush, rectName,
                        centeredText);


                    Rectangle rectOutfit = outfitBounds;
                    string outfit = card.outfit ?? "";
                    Font fontOutfit = GetFittedFont(
                        g, outfit, "Segoe UI",
                        (int)Math.Round(outfitPoints),
                        rectOutfit.Width);
                    g.DrawString(outfit, fontOutfit, labelBrush, rectOutfit,
                        centeredText);
                }

                float statusIconSize = CardPixels(imgrect2, 20);
                float statusIconTop =
                    bounds.Top + CardPixels(imgrect2, 3);
                if (card.exclusive == true)
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    int iconSize = Math.Max(1,
                        (int)Math.Round(statusIconSize));
                    g.DrawImageUnscaled(GetIconBitmap("special", iconSize),
                        imgrect2.Left, (int)Math.Round(statusIconTop));
                    if (card.hotnessLevel == "5")
                    {
                        g.DrawImageUnscaled(
                            GetIconBitmap("hotness", iconSize),
                            imgrect2.Left, (int)Math.Round(
                                statusIconTop + statusIconSize * 1.12f));
                    }
                }

                else if (card.hotnessLevel == "5")
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    int iconSize = Math.Max(1,
                        (int)Math.Round(statusIconSize));
                    g.DrawImageUnscaled(GetIconBitmap("hotness", iconSize),
                        imgrect2.Left, (int)Math.Round(statusIconTop));
                }

                if (myData != null && myData.GetCardFavourite(card.name))
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    float heartSize = CardPixels(imgrect2, 28);
                    float heartMargin = CardPixels(imgrect2, 10.5f);
                    int iconSize = Math.Max(1, (int)Math.Round(heartSize));
                    g.DrawImageUnscaled(GetIconBitmap("heart", iconSize),
                        (int)Math.Round(
                            imgrect2.Right - heartSize - heartMargin),
                        (int)Math.Round(
                            bounds.Top + CardPixels(imgrect2, 3)));
                }
                if (Properties.Settings.Default.ShowRatingStars)
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    DrawRatingStars(
                        g, imgrect2, (int)myrating, item.Index);
                }
                if (text != "" )
                {                         
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    Rectangle rect = sortBounds;
                    Font font = GetFittedFont(g, text, "Verdana",
                        (int)Math.Round(sortPoints), rect.Width);
                    DrawOverlayLabel(g, text, font, rect);
                }

                if (nowPlayingTag == card.modelName + "\r\n" + card.outfit)
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    Rectangle rect = playingBounds;
                    Font font = GetFittedFont(g, "Playing", "Verdana",
                        (int)Math.Round(playingPoints), rect.Width);
                    using GraphicsPath p = new();
                    p.AddString(
                        "Playing",            
                        font.FontFamily,
                        (int) FontStyle.Bold,     
                        g.DpiY * font.SizeInPoints / 72,
                        playingBounds,
                        centeredText);
                    g.FillRectangle(Brushes.Green, playingBounds);
                    using Pen playingOutline = new(Color.Black, 2);
                    g.DrawRectangle(playingOutline, playingBounds);
                    g.FillPath(Brushes.White, p);       
                }
            }
            else if (ImageListView.View == View.Details)
            {
                // Revert to base class
                base.DrawItem(g, item, state, bounds);
            }
        }

        internal static bool VerifyRoundedCorners()
        {
            List<ModelCard>? previous = Datastore.modelcards;
            bool previousSetting =
                Properties.Settings.Default.RoundCardCorners;
            using Bitmap cardImage = new(
                162, 242, PixelFormat.Format32bppPArgb);
            using (Graphics cardGraphics = Graphics.FromImage(cardImage))
                cardGraphics.Clear(Color.Red);
            ModelCard card = new()
            {
                name = "roundedtest",
                modelName = "Rounded test",
                outfit = "Paint state",
                image = cardImage
            };
            Datastore.modelcards = [card];
            try
            {
                using Manina.Windows.Forms.ImageListView list = new()
                {
                    Size = new System.Drawing.Size(300, 400)
                };
                using CardRenderer renderer = new(
                    null, "", 1, CultureInfo.InvariantCulture,
                    NumberStyles.AllowDecimalPoint);
                list.SetRenderer(renderer);
                ImageListViewItem item = new()
                {
                    Tag = card.name,
                    Text = card.modelName
                };
                list.Items.Add(item);
                using Bitmap output = new(
                    162, 276, PixelFormat.Format32bppPArgb);
                using Graphics graphics = Graphics.FromImage(output);
                Properties.Settings.Default.RoundCardCorners = true;
                renderer.DrawItem(graphics, item, ItemState.None,
                    new Rectangle(Point.Empty, output.Size));
                Color roundedCorner = output.GetPixel(0, 0);
                Color center = output.GetPixel(
                    output.Width / 2, output.Height / 3);
                graphics.Clear(Color.Transparent);
                Properties.Settings.Default.RoundCardCorners = false;
                renderer.DrawItem(graphics, item, ItemState.None,
                    new Rectangle(Point.Empty, output.Size));
                Color squareCorner = output.GetPixel(0, 0);
                bool valid = graphics.CompositingMode ==
                        CompositingMode.SourceOver &&
                    roundedCorner.A == 0 && center.R == 255 &&
                    squareCorner.R == 255;
                if (!valid)
                {
                    renderer.TryGetImageBounds(
                        item.Index, out Rectangle imageBounds);
                    Console.Error.WriteLine(
                        $"Mode={graphics.CompositingMode}; " +
                        $"RoundedCorner={roundedCorner}; " +
                        $"SquareCorner={squareCorner}; Center={center}; " +
                        $"View={list.View}; Index={item.Index}; " +
                        $"CardFound={Datastore.findCardByTag(card.name) != null}; " +
                        $"ImageBounds={imageBounds}");
                }
                return valid;
            }
            finally
            {
                Properties.Settings.Default.RoundCardCorners =
                    previousSetting;
                Datastore.modelcards = previous;
            }
        }

        public override void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            ClearIconBitmaps();
            foreach (Font font in fonts.Values)
                font.Dispose();
            fonts.Clear();
            fittedFonts.Clear();
            highlightBrush.Dispose();
            labelBrush.Dispose();
            overlayShadowBrush.Dispose();
            overlayBackgroundBrush.Dispose();
            overlayBorderPen.Dispose();
            centeredText.Dispose();
            base.Dispose();
        }
    }
}
