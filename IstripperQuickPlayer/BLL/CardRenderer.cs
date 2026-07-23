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
        internal bool fontInstalled = true;
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
        private readonly FontFamily fluentIcons =
            new("Segoe Fluent Icons");
        private readonly FontFamily verdana = new("Verdana");
        private Font? starFont;
        private SizeF starTextSize;
        private int starImageWidth = -1;
        private float starScale = -1;
        private float starDpi = -1;

        internal CardRenderer(MyData? myData, string sortBy, float cardScale, CultureInfo culture, bool fontInstalled, NumberStyles style)
        {
            this.cardScale = cardScale;
            this.myData = myData;
            this.sortBy = sortBy;
            this.culture = culture;
            this.fontInstalled = fontInstalled;
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

        private void EnsureStarMetrics(Graphics graphics, int imageWidth)
        {
            if (starFont != null && starImageWidth == imageWidth &&
                starScale == cardScale && starDpi == graphics.DpiY)
                return;

            int size = Math.Max(1, (int)(14 * cardScale));
            Font font;
            SizeF measured;
            do
            {
                font = GetFont("Segoe Fluent Icons", size, FontStyle.Bold);
                measured = graphics.MeasureString("\uE0B4\uE0B4\uE0B4\uE0B4\uE0B4",
                    font);
                size--;
            }
            while (measured.Width > imageWidth && size > 0);

            starFont = font;
            starTextSize = measured;
            starImageWidth = imageWidth;
            starScale = cardScale;
            starDpi = graphics.DpiY;
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

                if (card.exclusive != null && (bool)card.exclusive)
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                               
                    using GraphicsPath exclusivePath = new();
                    exclusivePath.AddString(
                        "\uEC19",            
                        fluentIcons,
                        (int) FontStyle.Bold,     
                        g.DpiY * cardScale *10 / 72,      
                        new Point(imgrect2.Left, bounds.Top +(int)((g.DpiY/192)*4) ),            
                        StringFormat.GenericDefault);
                    using Pen iconOutline = new(Color.Black, 1);
                    g.DrawPath(iconOutline, exclusivePath);
                    g.FillPath(Brushes.Yellow, exclusivePath);
                    if (card.hotnessLevel == "5")
                    {
                        using GraphicsPath hotnessPath = new();
                        hotnessPath.AddString(
                            "\uEC8A",            
                            fluentIcons,
                            (int) FontStyle.Bold,     
                            g.DpiY * cardScale * 10 / 72,      
                            new Point(imgrect2.Left + (int)((g.DpiY/192)*30*cardScale), bounds.Top +(int)((g.DpiY/192)*4)),            
                            StringFormat.GenericDefault);
                        g.DrawPath(iconOutline, hotnessPath);
                        g.FillPath(Brushes.Yellow, hotnessPath);
                    }
                }

                else if (card.hotnessLevel == "5")
                {
                     g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                               
                    using GraphicsPath hotnessPath = new();
                    float fntSize = g.DpiY * cardScale * 10 / 72;
                    hotnessPath.AddString(
                        "\uEC8A",            
                        fluentIcons,
                        (int) FontStyle.Bold,     
                        fntSize,      
                        new Point(imgrect2.Left, bounds.Top +(int)((g.DpiY/192)*4*cardScale)),            
                        StringFormat.GenericDefault);
                    using Pen hotnessOutline = new(Color.Black, 1);
                    g.DrawPath(hotnessOutline, hotnessPath);
                    g.FillPath(Brushes.Yellow, hotnessPath);
                }

                if (myData != null && myData.GetCardFavourite(card.name))
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    using GraphicsPath favouritePath = new();
                    if (fontInstalled)
                         favouritePath.AddString(
                            "\uE00B",            
                            fluentIcons,
                            (int) FontStyle.Bold,     
                            g.DpiY * cardScale * 14 / 72,      
                            new Point(imgrect2.Right - (int)((g.DpiY/192)*52*cardScale), bounds.Top +(int)((g.DpiY/192)*4) ),            
                            StringFormat.GenericDefault);
                    else
                        favouritePath.AddString(
                            "+",            
                            verdana,
                            (int) FontStyle.Bold,     
                            g.DpiY * cardScale * 16 / 72,      
                            new Point(bounds.Right - (int)((g.DpiY/192)*80), bounds.Top -(int)((g.DpiY/192)*2)),            
                            StringFormat.GenericDefault);
                    using Pen favouriteOutline = new(Color.Black, 3);
                    g.DrawPath(favouriteOutline, favouritePath);
                    g.FillPath(Brushes.LightGreen, favouritePath);
                }
                if (fontInstalled && Properties.Settings.Default.ShowRatingStars)
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    const string ratingstrBlank =
                        "\uE0B4\uE0B4\uE0B4\uE0B4\uE0B4";
                    EnsureStarMetrics(g, imgrect2.Width);
                    Font font = starFont!;
                    SizeF bsize = starTextSize;
                    using GraphicsPath p = new();
                    p.AddString(
                        ratingstrBlank,            
                        font.FontFamily,
                        (int) FontStyle.Bold,     
                        g.DpiY * font.SizeInPoints / 72,
                        new PointF(imgrect2.Left + (imgrect2.Width - bsize.Width)/2.0f, imgrect2.Top + (imgrect2.Height/2.0f)+(bsize.Height*1.0f)),            
                        leftCenteredText);
                    using SolidBrush blankStarBrush =
                        new(Color.FromArgb(180, Color.Black));
                    g.FillPath(blankStarBrush, p);

                    Rectangle starbounds = new Rectangle((int)(imgrect2.Left + (imgrect2.Width - bsize.Width) / 2.0f), (int)(imgrect2.Top + (imgrect2.Height / 2.0f) + (bsize.Height * 0.5f)), (int)bsize.Width, (int)bsize.Height);
                    _starBoundsByIndex[item.Index] = starbounds;

                    //g.DrawRectangle(new Pen(Color.Red, 2), starbounds);
                    if (myrating > 0)
                    {
                        g.InterpolationMode = InterpolationMode.High;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        g.CompositingQuality = CompositingQuality.HighQuality;

                        string ratingstr = "".PadLeft((int)myrating/2,'\uE0B4');
                        if (myrating%2 > 0)
                            ratingstr += '\uE7C6';
                        using GraphicsPath ratingPath = new();
                        ratingPath.AddString(
                            ratingstr,            
                            font.FontFamily,
                            (int) FontStyle.Bold,     
                            g.DpiY * font.SizeInPoints / 72,
                            new PointF(imgrect2.Left + (imgrect2.Width - bsize.Width)/2.0f, imgrect2.Top + (imgrect2.Height/2.0f)+(bsize.Height*1.0f)),           
                            leftCenteredText);
                        using Pen ratingOutline = new(Color.Black, 3);
                        g.DrawPath(ratingOutline, ratingPath);
                        g.FillPath(Brushes.Yellow, ratingPath);

                    }
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
