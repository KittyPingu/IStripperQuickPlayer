using IStripperQuickPlayer.DataModel;
using Manina.Windows.Forms;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using View = Manina.Windows.Forms.View;

namespace IStripperQuickPlayer.BLL
{
    public class CardRenderer : ImageListView.ImageListViewRenderer
    {
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
        private readonly ConcurrentDictionary<int, Rectangle> _boundsByIndex = new();
        private readonly ConcurrentDictionary<int, Rectangle> _starBoundsByIndex = new();
        private readonly SolidBrush labelBrush = new(Color.Black);
        private readonly Dictionary<(string Family, int Size, FontStyle Style),
            Font> fonts = [];
        private readonly StringFormat centeredText = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        private readonly StringFormat leftCenteredText = new()
        {
            Alignment = StringAlignment.Near,
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

        private void DrawRatingStars(Graphics graphics, Rectangle imageBounds,
            int rating, int itemIndex)
        {
            float size = Math.Min(
                graphics.DpiY * cardScale * 14 / 72,
                imageBounds.Width / 5f);
            float rowWidth = size * 5;
            float left = imageBounds.Left +
                (imageBounds.Width - rowWidth) / 2;
            float top = imageBounds.Top + imageBounds.Height / 2 +
                size / 2;
            _starBoundsByIndex[itemIndex] = Rectangle.Round(
                new RectangleF(left, top, rowWidth, size));

            using SolidBrush blankBrush =
                new(Color.FromArgb(180, Color.Black));
            using Pen outline = new(Color.Black, 3);
            int halfStars = Math.Clamp(rating, 0, 10);
            for (int i = 0; i < 5; i++)
            {
                RectangleF starBounds = new(
                    left + i * size + 1.5f, top + 1.5f,
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

        public bool TryGetStarItemBounds(int itemIndex, out Rectangle bounds)
             => _starBoundsByIndex.TryGetValue(itemIndex, out bounds);

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
                if((state & ItemState.Selected) != ItemState.None)
                {
                  if (drawText)
                    g.FillRectangle(highlightBrush, new Rectangle(bounds.Left-3,bounds.Top-3,bounds.Width+6,bounds.Height+6));
                  else
                    g.FillRectangle(highlightBrush, new Rectangle(bounds.Left-3,bounds.Top-3,bounds.Width+6,bounds.Height-34+6));
                }
                string text = "";
                if (card == null) return;
                Rectangle imgrect = bounds;
                Rectangle imgrect2 = bounds;
                if (card.image != null)
                {
                    double ratio = (1.0*card.image.Width)/card.image.Height;
                    int dy = (int)(34*g.DpiX/120.0);
                    int dx = (int)(bounds.Width-((bounds.Height-34)*ratio))/2;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    if (cardScale == 1) g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    if (cardScale > 1 || !drawText) g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    imgrect2 = new Rectangle(bounds.Left+dx,bounds.Top,bounds.Width-(dx*2),bounds.Height-dy);                   
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
                }
                decimal myrating=0M;
                if (myData != null) myrating = myData.GetCardRating(card.name);

                if (drawText)
                {
                    switch (sortBy)
                    {
                        case "My Rating":
                            if (!Properties.Settings.Default.ShowRatingStars)
                            {
                                if (myrating > 0) text = myrating.ToString();
                            }
                            break;
                        case "Height":
                            decimal decheight = 0;
                            decimal.TryParse(card.height, style, culture, out decheight);
                            if (RegionInfo.CurrentRegion.IsMetric && CultureInfo.CurrentCulture.Name != "en-GB")
                               text = (((Math.Floor(decheight)*12) + (decheight-Math.Floor(decheight))*10) * 2.54M).ToString("N1") + "cm";
                            else
                               text = Math.Floor(decheight) + "'" + (int)(24*(decheight-Math.Floor(decheight)))/2.0M + "''";
                            break;
                        case "":
                        case "Model Name":
                            text = "";
                            break;
                        case "Rating":
                            text = (Convert.ToDecimal(card.rating)-5m).ToString();
                            break;
                        case "Age":
                            text = (card.modelAge.ToString() ?? "");    
                            break;
                        case "Ethnicity":
                            text = (card.ethnicity ?? "");        
                            break;
                        case "Breast Size":
                        case "Breast Size (Descending)":
                            text = (card.bust ?? 0).ToString();
                            break;
                        case "Date Purchased":
                        case "Date Purchased (Descending)":
                            if (card.datePurchased != null)
                                text = ((DateTime)card.datePurchased).ToShortDateString();
                            break;
                        case "Release Date":
                        case "Release Date (Descending)":
                            text = card.dateReleased.ToShortDateString();
                            break;
                        default:
                            break;
                    }




                    Rectangle rectName = new Rectangle(bounds.Left, bounds.Bottom-(int)((g.DpiY/192)*52), bounds.Width, (int)((g.DpiY/192)*30));
                    int sztitle=10;
                    Font fontName = GetFont("Segoe UI", sztitle);
                    string name = card.modelName ?? "";
                    var textSizeName = g.MeasureString(name, fontName);
                    while (textSizeName.Width > rectName.Width && sztitle > 1)
                    {
                        fontName = GetFont("Segoe UI", --sztitle);
                        textSizeName = g.MeasureString(name, fontName);
                    }
                    g.DrawString(name, fontName, labelBrush, rectName,
                        centeredText);


                    Rectangle rectOutfit = new Rectangle(bounds.Left, bounds.Bottom-(int)((g.DpiY/192)*27), bounds.Width, (int)((g.DpiY/192)*30));
                    int szoutfit=9;
                    Font fontOutfit = GetFont("Segoe UI", szoutfit);
                    string outfit = card.outfit ?? "";
                    var textSizeOutfit = g.MeasureString(outfit, fontOutfit);
                    while (textSizeOutfit.Width > rectOutfit.Width &&
                        szoutfit > 1)
                    {
                        fontOutfit = GetFont("Segoe UI", --szoutfit);
                        textSizeOutfit = g.MeasureString(outfit, fontOutfit);
                    }
                    g.DrawString(outfit, fontOutfit, labelBrush, rectOutfit,
                        centeredText);
                }

                float statusIconSize = g.DpiY * cardScale * 10 / 72;
                float statusIconTop =
                    bounds.Top + (g.DpiY / 192) * 4 * cardScale;
                if (card.exclusive == true)
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    DrawSpecialIcon(g, new(
                        imgrect2.Left, statusIconTop,
                        statusIconSize, statusIconSize));
                    if (card.hotnessLevel == "5")
                    {
                        DrawHotnessIcon(g, new(
                            imgrect2.Left,
                            statusIconTop + statusIconSize * 1.12f,
                            statusIconSize, statusIconSize));
                    }
                }

                else if (card.hotnessLevel == "5")
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    DrawHotnessIcon(g, new(
                        imgrect2.Left, statusIconTop,
                        statusIconSize, statusIconSize));
                }

                if (myData != null && myData.GetCardFavourite(card.name))
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    float heartSize = g.DpiY * cardScale * 14 / 72;
                    float heartMargin = g.DpiY * cardScale * 7 / 96;
                    using GraphicsPath favouritePath = CreateHeartPath(new(
                        imgrect2.Right - heartSize - heartMargin,
                        bounds.Top + g.DpiY * 2 / 96,
                        heartSize, heartSize));
                    using Pen favouriteOutline = new(Color.Black, 3);
                    g.DrawPath(favouriteOutline, favouritePath);
                    g.FillPath(Brushes.LightGreen, favouritePath);
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

                    Rectangle rect = new Rectangle(bounds.Left+(int)((g.DpiY/192)*48), bounds.Top + (int)((g.DpiY/192)*6), bounds.Width - (int)((g.DpiY/192)*58), (int)((g.DpiY/192)*40));
                    int sz =(int)(13* cardScale);
                    Font font = GetFont("Verdana", sz);
                    var textSize = g.MeasureString(text, font);
                    while (textSize.Width > rect.Width && sz > 1)
                    {
                       font = GetFont("Verdana", --sz);
                       textSize = g.MeasureString(text, font);
                    }
                    using GraphicsPath p = new();
                    p.AddString(
                        text,            
                        font.FontFamily,
                        (int) FontStyle.Regular,     
                        g.DpiY * font.SizeInPoints / 72,
                        new Point(imgrect2.Left + (int)((g.DpiY/192)*18*cardScale), imgrect2.Top + (int)((g.DpiY/192)*6)),            
                        StringFormat.GenericDefault);
                    using Pen textOutline = new(Color.Black, 3);
                    g.DrawPath(textOutline, p);
                    g.FillPath(Brushes.White, p);            

             
                }

                if (nowPlayingTag == card.modelName + "\r\n" + card.outfit)
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    Rectangle rect = new Rectangle(imgrect.Left+18, bounds.Top + 10, (int)(imgrect.Width*0.6), 40);  
                    int szname =(int)(14 * cardScale);
                    Font font = GetFont("Verdana", szname);
                    var textSize = g.MeasureString("Playing", font);                
                    while (textSize.Width > rect.Width && szname > 1)
                    {
                       font = GetFont("Verdana", --szname);
                       textSize = g.MeasureString("Playing", font);
                    }
                    using GraphicsPath p = new();
                    p.AddString(
                        "Playing",            
                        font.FontFamily,
                        (int) FontStyle.Bold,     
                        g.DpiY * font.SizeInPoints / 72,
                        new Rectangle(imgrect.Left, bounds.Top+(int)(60*cardScale),(int)(imgrect.Width*0.7), (int)textSize.Height),            
                        centeredText);
                    g.FillRectangle(Brushes.Green, new Rectangle(imgrect.Left, bounds.Top+(int)(60*cardScale),(int)(imgrect.Width*0.7), (int)textSize.Height));
                    using Pen playingOutline = new(Color.Black, 2);
                    g.DrawRectangle(playingOutline, new Rectangle(imgrect.Left, bounds.Top+(int)(60*cardScale),(int)(imgrect.Width*0.7), (int)textSize.Height));
                    g.FillPath(Brushes.White, p);       
                }
            }
            else if (ImageListView.View == View.Details)
            {
                // Revert to base class
                base.DrawItem(g, item, state, bounds);
            }
        }
    }
}
