﻿using IStripperQuickPlayer.DataModel;
using Manina.Windows.Forms;
using System;
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
        internal CultureInfo culture = null;
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
                highlightBrush = new SolidBrush(Color.FromArgb(40, 80, 100));
                backgroundColour = Color.FromArgb(40, 40, 40);
            }
            else
            {
                labelColor = Color.Black;
                highlightBrush = new SolidBrush(Color.PaleGreen);
                backgroundColour = Color.WhiteSmoke;
            }
        }

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
                    g.FillRectangle(Brushes.White, new Rectangle(bounds.Left-3,bounds.Top-3,bounds.Width+6,bounds.Height-34+6));
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
                StringFormat stringFormat = new StringFormat();
                stringFormat.Alignment = StringAlignment.Center;
                stringFormat.LineAlignment = StringAlignment.Center;

                StringFormat stringFormatStars = new StringFormat();
                stringFormatStars.Alignment = StringAlignment.Near;
                stringFormatStars.LineAlignment = StringAlignment.Center;

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
                            if (card.dateReleased != null)
                                text = ((DateTime)card.dateReleased).ToShortDateString();
                            break;
                        default:
                            break;
                    }




                    Rectangle rectName = new Rectangle(bounds.Left, bounds.Bottom-(int)((g.DpiY/192)*52), bounds.Width, (int)((g.DpiY/192)*30));
                    int sztitle=10;
                    Font fontName = new Font("Segoe UI", sztitle);
                    string[] nameoutfit = item.Text.Split("\r\n");
                    var textSizeName = g.MeasureString(nameoutfit[0], fontName);
                    while (textSizeName.Width > rectName.Width)
                    { 
                        fontName = new Font("Segoe UI", sztitle--);
                        textSizeName = g.MeasureString(nameoutfit[0], fontName);
                    }
                    g.DrawString(nameoutfit[0], fontName, new SolidBrush(labelColor), rectName, stringFormat);


                    Rectangle rectOutfit = new Rectangle(bounds.Left, bounds.Bottom-(int)((g.DpiY/192)*27), bounds.Width, (int)((g.DpiY/192)*30));
                    int szoutfit=9;
                    Font fontOutfit = new Font("Segoe UI", szoutfit);
                    var textSizeOutfit = g.MeasureString(nameoutfit[1], fontOutfit);
                    while (textSizeOutfit.Width > rectOutfit.Width)
                    { 
                        fontOutfit = new Font("Segoe UI", szoutfit--);
                        textSizeOutfit = g.MeasureString(nameoutfit[1], fontOutfit);
                    }
                    g.DrawString(nameoutfit[1], fontOutfit, new SolidBrush(labelColor), rectOutfit, stringFormat);
                }

                if (card.exclusive != null && (bool)card.exclusive)
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                               
                    GraphicsPath p = new GraphicsPath(); 
                    p.AddString(
                        "\uEC19",            
                        new FontFamily("Segoe Fluent Icons"), 
                        (int) FontStyle.Bold,     
                        g.DpiY * cardScale *10 / 72,      
                        new Point(imgrect2.Left, bounds.Top +(int)((g.DpiY/192)*4) ),            
                        new StringFormat());         
                    g.DrawPath(new Pen(Color.Black, 1), p);
                    g.FillPath(Brushes.Yellow, p);     
                    if (card.hotnessLevel == "5")
                    {
                        p = new GraphicsPath(); 
                        p.AddString(
                            "\uEC8A",            
                            new FontFamily("Segoe Fluent Icons"), 
                            (int) FontStyle.Bold,     
                            g.DpiY * cardScale * 10 / 72,      
                            new Point(imgrect2.Left + (int)((g.DpiY/192)*30*cardScale), bounds.Top +(int)((g.DpiY/192)*4)),            
                            new StringFormat());         
                        g.DrawPath(new Pen(Color.Black, 1), p);
                        g.FillPath(Brushes.Yellow, p);     
                    }
                }

                else if (card.hotnessLevel == "5")
                {
                     g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                               
                    GraphicsPath p = new GraphicsPath(); 
                    float fntSize = g.DpiY * cardScale * 10 / 72;
                    p.AddString(
                        "\uEC8A",            
                        new FontFamily("Segoe Fluent Icons"), 
                        (int) FontStyle.Bold,     
                        fntSize,      
                        new Point(imgrect2.Left, bounds.Top +(int)((g.DpiY/192)*4*cardScale)),            
                        new StringFormat());         
                    g.DrawPath(new Pen(Color.Black, 1), p);
                    g.FillPath(Brushes.Yellow, p);     
                }

                if (myData != null && myData.GetCardFavourite(card.name))
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    GraphicsPath p = new GraphicsPath(); 
                    if (fontInstalled)
                         p.AddString(
                            "\uE00B",            
                            new FontFamily("Segoe Fluent Icons"), 
                            (int) FontStyle.Bold,     
                            g.DpiY * cardScale * 14 / 72,      
                            new Point(imgrect2.Right - (int)((g.DpiY/192)*52*cardScale), bounds.Top +(int)((g.DpiY/192)*4) ),            
                            new StringFormat());  
                    else
                        p.AddString(
                            "+",            
                            new FontFamily("Verdana"), 
                            (int) FontStyle.Bold,     
                            g.DpiY * cardScale * 16 / 72,      
                            new Point(bounds.Right - (int)((g.DpiY/192)*80), bounds.Top -(int)((g.DpiY/192)*2)),            
                            new StringFormat());         
                    g.DrawPath(new Pen(Color.Black, 3), p);
                    g.FillPath(Brushes.LightGreen, p);     
                }
                int szstar=(int)(14* cardScale);
                
                string ratingstrBlank = "".PadLeft(5,'\uE0B4');

                if (fontInstalled && Properties.Settings.Default.ShowRatingStars)
                {
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    //Rectangle rect = new Rectangle(bounds.Left+52, bounds.Top + 10, bounds.Width - (int)(52*cardScale), 40);

                    Font font = new Font("Segoe Fluent Icons", 14 * cardScale);
                    var textSize = g.MeasureString(ratingstrBlank, font);
                    while (textSize.Width > imgrect2.Width)
                    { 
                       font = new Font("Verdana", szstar--);
                       textSize = g.MeasureString(ratingstrBlank, font);
                    }
                    font = new Font("Segoe Fluent Icons", szstar, FontStyle.Bold);
                    var bsize = g.MeasureString(ratingstrBlank,font);
                    GraphicsPath p = new GraphicsPath(); 
                    p.AddString(
                        ratingstrBlank,            
                        new FontFamily("Segoe Fluent Icons"), 
                        (int) FontStyle.Bold,     
                        g.DpiY * szstar / 72,      
                        new PointF(imgrect2.Left + (imgrect2.Width - bsize.Width)/2.0f, imgrect2.Top + (imgrect2.Height/2.0f)+(bsize.Height*1.0f)),            
                        stringFormatStars);         
                    //g.DrawPath(new Pen(Color.Black, 3), p);
                    g.FillPath(new SolidBrush(Color.FromArgb(180, Color.Black)), p);     
            
                    if (myrating > 0)
                    {
                        g.InterpolationMode = InterpolationMode.High;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        g.CompositingQuality = CompositingQuality.HighQuality;

                        string ratingstr = "".PadLeft((int)myrating/2,'\uE0B4');
                        if (myrating%2 > 0)
                            ratingstr += '\uE7C6';
                        p = new GraphicsPath(); 
                        p.AddString(
                            ratingstr,            
                            new FontFamily("Segoe Fluent Icons"), 
                            (int) FontStyle.Bold,     
                            g.DpiY * szstar / 72 ,      
                            new PointF(imgrect2.Left + (imgrect2.Width - bsize.Width)/2.0f, imgrect2.Top + (imgrect2.Height/2.0f)+(bsize.Height*1.0f)),           
                            stringFormatStars);         
                        g.DrawPath(new Pen(Color.Black, 3), p);
                        g.FillPath(Brushes.Yellow, p);     
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
                    Font font = new Font("Verdana", sz);
                    var textSize = g.MeasureString(text, font);
                    while (textSize.Width > rect.Width)
                    { 
                       font = new Font("Verdana", sz--);
                       textSize = g.MeasureString(text, font);
                    }
                    GraphicsPath p = new GraphicsPath(); 
                    p.AddString(
                        text,            
                        new FontFamily("Verdana"), 
                        (int) FontStyle.Regular,     
                        g.DpiY * sz / 72,      
                        new Point(imgrect2.Left + (int)((g.DpiY/192)*18*cardScale), imgrect2.Top + (int)((g.DpiY/192)*6)),            
                        new StringFormat());         
                    g.DrawPath(new Pen(Color.Black, 3), p);
                    g.FillPath(Brushes.White, p);            

             
                }

                bool isPlaying = false;
                if (nowPlayingTag == card.modelName + "\r\n" + card.outfit)
                {
                    isPlaying = true;
                    g.InterpolationMode = InterpolationMode.High;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    Rectangle rect = new Rectangle(imgrect.Left+18, bounds.Top + 10, (int)(imgrect.Width*0.6), 40);  
                    int szname =(int)(14 * cardScale);
                    Font font = new Font("Verdana", szname);
                    var textSize = g.MeasureString("Playing", font);                
                    while (textSize.Width > rect.Width)
                    { 
                       font = new Font("Verdana", szname--);
                       textSize = g.MeasureString("Playing", font);
                    }
                    GraphicsPath p = new GraphicsPath(); 
                    p.AddString(
                        "Playing",            
                        new FontFamily("Verdana"), 
                        (int) FontStyle.Bold,     
                        g.DpiY * szname / 72,      
                        new Rectangle(imgrect.Left, bounds.Top+(int)(60*cardScale),(int)(imgrect.Width*0.7), (int)textSize.Height),            
                        stringFormat);         
                    g.FillRectangle(Brushes.Green, new Rectangle(imgrect.Left, bounds.Top+(int)(60*cardScale),(int)(imgrect.Width*0.7), (int)textSize.Height));
                    g.DrawRectangle(new Pen(Color.Black,2), new Rectangle(imgrect.Left, bounds.Top+(int)(60*cardScale),(int)(imgrect.Width*0.7), (int)textSize.Height));
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
