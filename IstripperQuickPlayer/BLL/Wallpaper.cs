using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DesktopWallpaper;
using System.Net;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace IStripperQuickPlayer.BLL
{
    public static class Wallpaper
    {
        public static Dictionary<uint, string> originalWallpaper = new Dictionary<uint, string>();
        public static Dictionary<uint, Bitmap> initialImages = new Dictionary<uint, Bitmap>();
        public static async void ChangeWallpaper(uint monitorNumber, string url)
        {
            if (url == null)return;            
            try
            {
                var wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());  
                var monitorId = wallpaper.GetMonitorDevicePathAt(monitorNumber);
               
                if (!originalWallpaper.ContainsKey(monitorNumber)) originalWallpaper.Add(monitorNumber, wallpaper.GetWallpaper(monitorId.ToString()));

                string tempfilepath = Path.GetTempFileName();
                string wpfilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "wallpaper" + monitorNumber.ToString() + ".jpg");
                Bitmap m = await GetImageBitmapFromUrl(url);
                m = ResizeBitmap(m, wallpaper.GetMonitorRECT(monitorId));
                if (initialImages.ContainsKey(monitorNumber))
                    initialImages[monitorNumber] = m;
                else
                    initialImages.Add(monitorNumber, m);
                if (Properties.Settings.Default.WallpaperBrightness != 100m) m = AdjustBrightness(m, (float)((double)Properties.Settings.Default.WallpaperBrightness/100.0));
                if (Properties.Settings.Default.WallpaperDetails) m = AddDetails(m, wallpaper.GetMonitorRECT(monitorId));
                m.Save(wpfilepath);
                wallpaper.SetWallpaper(monitorId.ToString(), wpfilepath);
                m.Dispose();               
            }
            catch (Exception ex){}
        }

        private static Bitmap ResizeBitmap(Bitmap m, Rect rect)
        {
            double widthScale = 0, heightScale = 0;
            if (m.Width != 0)
                widthScale = (double)(rect.Right - rect.Left) / (double)m.Width;
            if (m.Height != 0)
                heightScale = (double)(rect.Bottom - rect.Top) / (double)m.Height;                

            double scale = Math.Max(widthScale, heightScale);

            Size result = new Size((int)(m.Width * scale), 
                                (int)(m.Height * scale));

            Bitmap b = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top, m.PixelFormat);
            Graphics g = Graphics.FromImage(b);
            RectangleF sourceRect;
            if (widthScale > heightScale)
            {
                float hdelta = m.Height - (float)(m.Height * heightScale /widthScale);
                sourceRect = new RectangleF(0, hdelta/2, m.Width, (float)(m.Height * heightScale /widthScale));
            }
            else
                sourceRect = new RectangleF(0, 0, (float)(m.Width * widthScale / heightScale), m.Height);
            RectangleF destinationRect = new RectangleF(0,0,b.Width, b.Height);
            g.DrawImage(m,  destinationRect, sourceRect, GraphicsUnit.Pixel);
            g.Dispose();
            return b;
        }

        private static Bitmap AddDetails(Bitmap b, Rect l, int sz = 36)
        {
            var str = Utils.GetMainForm().lblNowPlaying.Text.Replace("Now Playing: ", "").Split("(")[0].Trim();
            StringFormat stringFormat = new StringFormat();
            stringFormat.Alignment = StringAlignment.Near;
            stringFormat.LineAlignment = StringAlignment.Near;
            Graphics g = Graphics.FromImage(b);
            g.InterpolationMode = InterpolationMode.High;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;
            var p = new GraphicsPath(); 
            p.AddString(
                str.ToString(),            
                new FontFamily("Microsoft Sans Serif"), 
                (int) FontStyle.Regular,     
                sz,      
                new Point(sz, sz),            
                new StringFormat());         
            g.DrawPath(new Pen(Color.Gray, 1), p);
            g.FillPath(Brushes.Black, p);   
            g.Dispose();
            return b;
        }

        public static async void ChangeBrightness()
        {
            try
            {
                var wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());  

                foreach(var kvp in initialImages)
                {
                    var monitorId = wallpaper.GetMonitorDevicePathAt(kvp.Key);
                   
                    Bitmap o = AdjustBrightness(initialImages[kvp.Key], (float)((double)Properties.Settings.Default.WallpaperBrightness/100.0));    
                    string wpfilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "wallpaper" + kvp.Key.ToString() + ".jpg");
                    if (Properties.Settings.Default.WallpaperDetails) o = AddDetails(o, wallpaper.GetMonitorRECT(monitorId));

                    o.Save(wpfilepath);
                    o.Dispose();

                    wallpaper.SetWallpaper(monitorId.ToString(), wpfilepath);
                }        
            }
            catch (Exception ex){}           
        }



        static async Task<Bitmap> GetImageBitmapFromUrl( string url)
        {
            Bitmap imageBitmap = null;
            try
            {
                using (var webClient = new WebClient())
                {
                    var imageBytes = await webClient.DownloadDataTaskAsync (url);
                    if (imageBytes != null && imageBytes.Length > 0)
                    {
                        Bitmap bmp;
                        using (var ms = new MemoryStream(imageBytes))
                        {
                            imageBitmap = new Bitmap(ms);
                        }
                    }
                }
            }
            catch (Exception ex) {}
            return imageBitmap;
        }

        private static Bitmap AdjustBrightness(Image image, float brightness)
        {
            // Make the ColorMatrix.
            float b = brightness;
            ColorMatrix cm = new ColorMatrix(new float[][]
                {
                    new float[] {b, 0, 0, 0, 0},
                    new float[] {0, b, 0, 0, 0},
                    new float[] {0, 0, b, 0, 0},
                    new float[] {0, 0, 0, 1, 0},
                    new float[] {0, 0, 0, 0, 1},
                });
            ImageAttributes attributes = new ImageAttributes();
            attributes.SetColorMatrix(cm);

            // Draw the image onto the new bitmap while applying
            // the new ColorMatrix.
            Point[] points =
            {
                new Point(0, 0),
                new Point(image.Width, 0),
                new Point(0, image.Height),
            };
            Rectangle rect = new Rectangle(0, 0, image.Width, image.Height);

            // Make the result bitmap.
            Bitmap bm = new Bitmap(image.Width, image.Height);
            using (Graphics gr = Graphics.FromImage(bm))
            {
                gr.DrawImage(image, points, rect,
                    GraphicsUnit.Pixel, attributes);
            }

            // Return the result.
            return bm;
        }
        public static void RestoreWallpaper()
        {
            try
            {
                var wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());  
                foreach(KeyValuePair<uint,string> paper in originalWallpaper)
                {
                        var monitorId = wallpaper.GetMonitorDevicePathAt(paper.Key);
                        wallpaper.SetWallpaper(monitorId.ToString(), paper.Value);
                }
            }
            catch(Exception ex){}            
        }

        internal static void RestoreWallpaperByID(uint monitorNumber)
        {
            try
            {
                var wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());  
             
                if (originalWallpaper.ContainsKey(monitorNumber))
                {
                    var monitorId = wallpaper.GetMonitorDevicePathAt(monitorNumber);
                    wallpaper.SetWallpaper(monitorId.ToString(), originalWallpaper[monitorNumber]);
                    originalWallpaper.Remove(monitorNumber);
                    initialImages.Remove(monitorNumber);
                }
            }
            catch (Exception ex){}
        }
    }
}
