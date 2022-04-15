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
        public static string _modelname = "";
        public static string _outfit = "";
        public static async void ChangeWallpaper(uint monitorNumber, string url, string modelname, string outfit)
        {
            if (url == null)return;       
            _modelname = modelname;
            _outfit = outfit;
            var str = Utils.GetMainForm().lblNowPlaying.Text.Replace("Now Playing: ", "").Split("(")[0].Trim();
            if (string.IsNullOrEmpty(str)) return;
            try
            {
                var wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());  
                var monitorId = wallpaper.GetMonitorDevicePathAt(monitorNumber);
               
                if (!originalWallpaper.ContainsKey(monitorNumber)) originalWallpaper.Add(monitorNumber, wallpaper.GetWallpaper(monitorId.ToString()));

                string tempfilepath = Path.GetTempFileName();
                string wpfilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "wallpaper" + monitorNumber.ToString() + ".jpg");
                Bitmap m = await GetImageBitmapFromUrl(url);
                m = ResizeBitmap(m, wallpaper.GetMonitorRECT(monitorId));
                DirectBitmap direct = new DirectBitmap(m.Width, m.Height);
                Graphics g = Graphics.FromImage(direct.Bitmap);
                g.DrawImageUnscaled(m, 0,0);
                g.Dispose();
                if (initialImages.ContainsKey(monitorNumber))
                    initialImages[monitorNumber] = m;
                else
                    initialImages.Add(monitorNumber, m);
                if (Properties.Settings.Default.BlurWallpaper) direct = AddBlur(direct);
                if (Properties.Settings.Default.WallpaperBrightness != 100m) m = AdjustBrightness(direct.Bitmap, (float)((double)Properties.Settings.Default.WallpaperBrightness/100.0));
                if (Properties.Settings.Default.WallpaperDetails) m = AddDetails(m, wallpaper.GetMonitorRECT(monitorId));
                m.Save(wpfilepath);
                direct.Dispose();
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

        private static DirectBitmap FastBoxBlur(DirectBitmap img, int radius) {

             int kSize = radius; 

             if (kSize % 2 == 0) kSize++;
             DirectBitmap Hblur = new DirectBitmap(img.Width, img.Height);
             Graphics g = Graphics.FromImage(Hblur.Bitmap);
             g.InterpolationMode = InterpolationMode.NearestNeighbor;
             g.CompositingMode = CompositingMode.SourceCopy; 
             g.SmoothingMode = SmoothingMode.None;
             g.DrawImageUnscaled(img.Bitmap, 0,0);
             g.Dispose();

             float Avg = (float) 1 / kSize;

             Parallel.For (0, img.Height, j => {

              float[] hSum = new float[] {
               0f, 0f, 0f, 0f
              };

              float[] iAvg = new float[] {
               0f, 0f, 0f, 0f
              };
    
              for (int x = 0; x < kSize; x++) {
                    Color tmpColor = img.GetPixel(x, j);
                    hSum[0] += tmpColor.A;
                    hSum[1] += tmpColor.R;
                    hSum[2] += tmpColor.G;
                    hSum[3] += tmpColor.B;
              }
              iAvg[0] = hSum[0] * Avg;
              iAvg[1] = hSum[1] * Avg;
              iAvg[2] = hSum[2] * Avg;
              iAvg[3] = hSum[3] * Avg;
              for (int i = 0; i < img.Width; i++) 
              { 
                if (i - kSize / 2  >= 0 && (i + 1 + kSize / 2) < img.Width)
                {
                    Color tmp_pColor = img.GetPixel((i - kSize / 2), j);
                    hSum[0] -= tmp_pColor.A;
                    hSum[1] -= tmp_pColor.R;
                    hSum[2] -= tmp_pColor.G;
                    hSum[3] -= tmp_pColor.B;
                    Color tmp_nColor = img.GetPixel(i + 1 + kSize / 2, j);
                    hSum[0] += tmp_nColor.A;
                    hSum[1] += tmp_nColor.R;
                    hSum[2] += tmp_nColor.G;
                    hSum[3] += tmp_nColor.B;
                    //
                    iAvg[0] = hSum[0] * Avg;
                    iAvg[1] = hSum[1] * Avg;
                    iAvg[2] = hSum[2] * Avg;
                    iAvg[3] = hSum[3] * Avg;
                }
                Hblur.SetPixel(i, j, Color.FromArgb((int) iAvg[0], (int) iAvg[1], (int) iAvg[2], (int) iAvg[3]));
              }
             });
             DirectBitmap total = new DirectBitmap(Hblur.Width, Hblur.Height);
             g = Graphics.FromImage(total.Bitmap);
             g.InterpolationMode = InterpolationMode.NearestNeighbor;
             g.CompositingMode = CompositingMode.SourceCopy; 
             g.SmoothingMode = SmoothingMode.None;
             g.DrawImageUnscaled(Hblur.Bitmap, 0,0);
             g.Dispose();
             Parallel.For (0, Hblur.Width, i => {
              float[] tSum = new float[] {
               0f, 0f, 0f, 0f
              };
              float[] iAvg = new float[] {
               0f, 0f, 0f, 0f
              };
              for (int y = 0; y < kSize; y++) {
               Color tmpColor = Hblur.GetPixel(i, y);
               tSum[0] += tmpColor.A;
               tSum[1] += tmpColor.R;
               tSum[2] += tmpColor.G;
               tSum[3] += tmpColor.B;
              }
              iAvg[0] = tSum[0] * Avg;
              iAvg[1] = tSum[1] * Avg;
              iAvg[2] = tSum[2] * Avg;
              iAvg[3] = tSum[3] * Avg;

              for (int j = 0; j < Hblur.Height; j++) {
               if (j - kSize / 2 >= 0 && j + 1 + kSize / 2 < Hblur.Height) {
                Color tmp_pColor = Hblur.GetPixel(i, j - kSize / 2);
                tSum[0] -= tmp_pColor.A;
                tSum[1] -= tmp_pColor.R;
                tSum[2] -= tmp_pColor.G;
                tSum[3] -= tmp_pColor.B;
                Color tmp_nColor = Hblur.GetPixel(i, j + 1 + kSize / 2);
                tSum[0] += tmp_nColor.A;
                tSum[1] += tmp_nColor.R;
                tSum[2] += tmp_nColor.G;
                tSum[3] += tmp_nColor.B;
                //
                iAvg[0] = tSum[0] * Avg;
                iAvg[1] = tSum[1] * Avg;
                iAvg[2] = tSum[2] * Avg;
                iAvg[3] = tSum[3] * Avg;
               }
               total.SetPixel(i, j, Color.FromArgb((int) iAvg[0], (int) iAvg[1], (int) iAvg[2], (int) iAvg[3]));
              }
             });
             return total;
        }
       

        private static int[] boxesForGaussian(double sigma, int n) {

         double wIdeal = Math.Sqrt((12 * sigma * sigma / n) + 1);
         double wl = Math.Floor(wIdeal);
 
         if (wl % 2 == 0) wl--;
         double wu = wl + 2;

         double mIdeal = (12 * sigma * sigma -n *wl  * wl - 4 * n * wl -3 * n) / (-4 * wl -4);
         double m = Math.Round(mIdeal);

         int[] sizes = new int[n];
         for (int i = 0; i < n; i++) {
          if (i < m) {
           sizes[i] = (int) wl;
          } else {
           sizes[i] = (int) wu;
          }
         }
         return sizes;
        }

         private static DirectBitmap FastGaussianBlur(DirectBitmap src, int Radius) {
          var bxs = boxesForGaussian(Radius, 3);
          DirectBitmap img = FastBoxBlur(src, bxs[0]);
          DirectBitmap img_2 = FastBoxBlur(img, bxs[1]);
          DirectBitmap img_3 = FastBoxBlur(img_2, bxs[2]);
          img.Dispose();
          img_2.Dispose();
          return img_3;
         }

        private static DirectBitmap AddBlur(DirectBitmap b)
        {
            return FastGaussianBlur(b, Convert.ToInt32(Properties.Settings.Default.BlurRadius));
        }
        private static Bitmap AddDetails(Bitmap b, Rect l, int sz = 36)
        {
            var str = _modelname + ", " + _outfit;
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

        public static async void RedrawImage()
        {
            try
            {
                var wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());  

                foreach(var kvp in initialImages)
                {
                    var monitorId = wallpaper.GetMonitorDevicePathAt(kvp.Key);
                   
                    Bitmap o = AdjustBrightness(initialImages[kvp.Key], (float)((double)Properties.Settings.Default.WallpaperBrightness/100.0));    
                    string wpfilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "wallpaper" + kvp.Key.ToString() + ".jpg");
                    if (Properties.Settings.Default.BlurWallpaper)
                    {
                         DirectBitmap b = new DirectBitmap(o.Width, o.Height);
                         Graphics g = Graphics.FromImage(b.Bitmap);
                         g.InterpolationMode = InterpolationMode.NearestNeighbor;
                         g.CompositingMode = CompositingMode.SourceCopy; 
                         g.SmoothingMode = SmoothingMode.None;
                         g.DrawImageUnscaled(o, 0,0);
                         g.Dispose();
                         o = AddBlur(b).Bitmap;
                         b.Dispose();
                    }
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
