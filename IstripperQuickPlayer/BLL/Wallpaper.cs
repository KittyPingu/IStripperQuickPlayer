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
                string wpfilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "wallpaper" + monitorNumber.ToString() + ".png");
                Bitmap m = await GetImageBitmapFromUrl(url);
                if (initialImages.ContainsKey(monitorNumber))
                    initialImages[monitorNumber] = m;
                else
                    initialImages.Add(monitorNumber, m);
                m = AdjustBrightness(m, (float)((double)Properties.Settings.Default.WallpaperBrightness/100.0));
                m.Save(wpfilepath);
                wallpaper.SetWallpaper(monitorId.ToString(), wpfilepath);
            }
            catch (Exception ex){}
        }

        public static async void ChangeBrightness()
        {
            try
            {
                var wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());  

                foreach(var kvp in originalWallpaper)
                {
                    var monitorId = wallpaper.GetMonitorDevicePathAt(kvp.Key);
                   
                    Bitmap o = AdjustBrightness(initialImages[kvp.Key], (float)((double)Properties.Settings.Default.WallpaperBrightness/100.0));                    
                    o.Save(kvp.Value);
                    o.Dispose();
                    wallpaper.SetWallpaper(monitorId.ToString(), kvp.Value);
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
    }
}
