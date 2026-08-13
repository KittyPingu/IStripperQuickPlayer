using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DesktopWallpaper;
using System.Net.Http;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;

namespace IStripperQuickPlayer.BLL
{
    public static class Wallpaper
    {
        private static readonly HttpClient client = new();
        public static Dictionary<uint, string> originalWallpaper = new Dictionary<uint, string>();
        public static Dictionary<uint, Bitmap> initialImages = new Dictionary<uint, Bitmap>();
        public static string _modelname = "";
        public static string _outfit = "";
        private static volatile bool suspended;
        private static readonly SemaphoreSlim renderGate = new(1, 1);
        private static readonly object initialImagesLock = new();
        private static readonly object blurBuffersLock = new();
        private static readonly Dictionary<Size, DirectBitmap> blurBuffers = [];
        private static int[] blurScratch = [];
        private static int redrawVersion;

        public static void CaptureOriginalDesktopState()
        {
            Utils.DefaultIconsVisible = Utils.DesktopIconsVisible();
            IDesktopWallpaper? wallpaper = null;
            try
            {
                wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());
                originalWallpaper.Clear();
                for (uint i = 0; i < wallpaper.GetMonitorDevicePathCount(); i++)
                {
                    string monitorId = wallpaper.GetMonitorDevicePathAt(i);
                    originalWallpaper[i] = wallpaper.GetWallpaper(monitorId);
                }
            }
            catch { }
            finally { ReleaseDesktopWallpaper(wallpaper); }
        }

        public static async Task ChangeWallpaper(uint monitorNumber, string? url, string modelname, string outfit)
        {
            if (suspended) return;
            if (Properties.Settings.Default.HideDesktopIcons)
                hideIcons();
            else
                showIcons();
            if (url == null)return;       
            _modelname = modelname;
            _outfit = outfit;
            Form1? form = Utils.GetMainForm();
            if (form == null) return;
            var str = form.lblNowPlaying.Text.Replace("Now Playing: ", "").Split("(")[0].Trim();
            if (string.IsNullOrEmpty(str)) return;
            using Bitmap? downloaded =
                await GetImageBitmapFromUrl(url).ConfigureAwait(false);
            if (downloaded == null) return;

            int version = Interlocked.Increment(ref redrawVersion);
            await renderGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    IDesktopWallpaper? wallpaper = null;
                    try
                    {
                        wallpaper =
                            (IDesktopWallpaper)(new DesktopWallpaperClass());
                        string monitorId =
                            wallpaper.GetMonitorDevicePathAt(monitorNumber);
                        Rect monitorRect = wallpaper.GetMonitorRECT(monitorId);
                        if (!originalWallpaper.ContainsKey(monitorNumber))
                        {
                            originalWallpaper.Add(monitorNumber,
                                wallpaper.GetWallpaper(monitorId));
                        }

                        string wpfilepath = WallpaperPath(monitorNumber);
                        using Bitmap resized =
                            ResizeBitmap(downloaded, monitorRect);
                        using Bitmap rendered = CreateRenderedWallpaper(
                            resized, monitorRect, modelname, outfit);
                        rendered.Save(wpfilepath, ImageFormat.Jpeg);

                        lock (initialImagesLock)
                        {
                            if (initialImages.Remove(monitorNumber,
                                    out Bitmap? previous))
                                previous.Dispose();
                            initialImages.Add(
                                monitorNumber, new Bitmap(resized));
                        }
                        if (!suspended &&
                            version == Volatile.Read(ref redrawVersion))
                            wallpaper.SetWallpaper(monitorId, wpfilepath);
                    }
                    finally { ReleaseDesktopWallpaper(wallpaper); }
                }).ConfigureAwait(false);
            }
            catch (Exception) { }
            finally
            {
                renderGate.Release();
            }
        }

        private static string WallpaperPath(uint monitorNumber) => Path.Join(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "IStripperQuickPlayer", $"wallpaper{monitorNumber}.jpg");

        private static Bitmap CreateRenderedWallpaper(Bitmap source, Rect rect,
            string modelname, string outfit)
        {
            Bitmap rendered;
            float brightness = (float)(
                (double)Properties.Settings.Default.WallpaperBrightness / 100);
            if (Properties.Settings.Default.BlurRadius > 0)
            {
                lock (blurBuffersLock)
                    rendered = AdjustBrightness(
                        AddBlur(source,
                            Convert.ToInt32(
                                Properties.Settings.Default.BlurRadius)),
                        brightness);
            }
            else
            {
                ReleaseBlurCache();
                rendered = AdjustBrightness(source, brightness);
            }

            if (Properties.Settings.Default.WallpaperDetails)
                AddDetails(rendered, rect, modelname, outfit);
            return rendered;
        }

        private static void showIcons()
        {

            if (!Utils.DesktopIconsVisible())
                Utils.ToggleDesktopIcons();
        }

        private static void hideIcons()
        {
            if (Utils.DesktopIconsVisible())
                Utils.ToggleDesktopIcons();
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

        private static void FastBoxBlur(
            DirectBitmap image, int[] scratch, int radius)
        {
            int kernelSize = radius % 2 == 0 ? radius + 1 : radius;
            float scale = 1f / kernelSize;

            Parallel.For(0, image.Height, y =>
            {
                float[] sum = [0, 0, 0, 0];
                float[] average = [0, 0, 0, 0];
                for (int x = 0; x < kernelSize; x++)
                {
                    Color pixel = image.GetPixel(x, y);
                    sum[0] += pixel.A;
                    sum[1] += pixel.R;
                    sum[2] += pixel.G;
                    sum[3] += pixel.B;
                }
                for (int x = 0; x < image.Width; x++)
                {
                    if (x == 0 || (x - kernelSize / 2 >= 0 &&
                        x + 1 + kernelSize / 2 < image.Width))
                    {
                        if (x != 0)
                        {
                            Color previous = image.GetPixel(
                                x - kernelSize / 2, y);
                            Color next = image.GetPixel(
                                x + 1 + kernelSize / 2, y);
                            sum[0] += next.A - previous.A;
                            sum[1] += next.R - previous.R;
                            sum[2] += next.G - previous.G;
                            sum[3] += next.B - previous.B;
                        }
                        for (int channel = 0; channel < 4; channel++)
                            average[channel] = sum[channel] * scale;
                    }
                    scratch[x + y * image.Width] = Color.FromArgb(
                        (int)average[0], (int)average[1],
                        (int)average[2], (int)average[3]).ToArgb();
                }
            });

            Parallel.For(0, image.Width, x =>
            {
                float[] sum = [0, 0, 0, 0];
                float[] average = [0, 0, 0, 0];
                for (int y = 0; y < kernelSize; y++)
                {
                    Color pixel = Color.FromArgb(
                        scratch[x + y * image.Width]);
                    sum[0] += pixel.A;
                    sum[1] += pixel.R;
                    sum[2] += pixel.G;
                    sum[3] += pixel.B;
                }
                for (int y = 0; y < image.Height; y++)
                {
                    if (y == 0 || (y - kernelSize / 2 >= 0 &&
                        y + 1 + kernelSize / 2 < image.Height))
                    {
                        if (y != 0)
                        {
                            Color previous = Color.FromArgb(scratch[
                                x + (y - kernelSize / 2) * image.Width]);
                            Color next = Color.FromArgb(scratch[
                                x + (y + 1 + kernelSize / 2) * image.Width]);
                            sum[0] += next.A - previous.A;
                            sum[1] += next.R - previous.R;
                            sum[2] += next.G - previous.G;
                            sum[3] += next.B - previous.B;
                        }
                        for (int channel = 0; channel < 4; channel++)
                            average[channel] = sum[channel] * scale;
                    }
                    image.SetPixel(x, y, Color.FromArgb(
                        (int)average[0], (int)average[1],
                        (int)average[2], (int)average[3]));
                }
            });
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

        private static Bitmap AddBlur(Bitmap source, int radius)
        {
            if (radius <= 1)
                return source;
            if (!blurBuffers.TryGetValue(
                    source.Size, out DirectBitmap? image))
            {
                image = new DirectBitmap(source.Width, source.Height);
                blurBuffers.Add(source.Size, image);
            }
            int required = source.Width * source.Height;
            if (blurScratch.Length < required)
                blurScratch = new int[required];
            using (Graphics graphics = Graphics.FromImage(
                image.Bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(source, 0, 0);
            }
            foreach (int size in boxesForGaussian(radius, 3))
                FastBoxBlur(image, blurScratch, size);
            return image.Bitmap;
        }

        public static void ReleaseBlurCache()
        {
            lock (blurBuffersLock)
            {
                foreach (DirectBitmap image in blurBuffers.Values)
                    image.Dispose();
                blurBuffers.Clear();
                blurScratch = [];
            }
        }

        internal static bool VerifyBlurBufferReuse()
        {
            using Bitmap source = new(
                8, 8, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(source))
                graphics.Clear(Color.CornflowerBlue);
            if (!ReferenceEquals(AddBlur(source, 1), source))
                return false;
            Bitmap first = AddBlur(source, 2);
            Bitmap second = AddBlur(source, 2);
            bool reused = ReferenceEquals(first, second) &&
                first.GetPixel(4, 4).ToArgb() ==
                    Color.CornflowerBlue.ToArgb();
            ReleaseBlurCache();
            return reused && blurBuffers.Count == 0 &&
                blurScratch.Length == 0;
        }
        private static Bitmap AddDetails(Bitmap b, Rect l,
            string modelname, string outfit)
        {
            string text = modelname + ", " + outfit;
            float opacity = Math.Clamp(
                (float)Properties.Settings.Default.WallpaperLabelOpacity,
                0, 100) / 100;
            if (opacity == 0)
                return b;
            int Alpha(int value) => (int)Math.Round(value * opacity);

            using Graphics g = Graphics.FromImage(b);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;

            float fontSize = Math.Clamp(
                (float)Properties.Settings.Default.WallpaperTextSize,
                10, 70);
            using Font font = new(FontFamily.GenericSansSerif, fontSize,
                FontStyle.Bold, GraphicsUnit.Pixel);
            using StringFormat format =
                (StringFormat)StringFormat.GenericTypographic.Clone();
            SizeF textSize = g.MeasureString(text, font, int.MaxValue, format);
            float margin = fontSize;
            float paddingX = fontSize * .55f;
            float paddingY = fontSize * .35f;
            RectangleF panel = new(margin, margin,
                textSize.Width + paddingX * 2,
                textSize.Height + paddingY * 2);
            float diameter = fontSize * .9f;
            using GraphicsPath path = new();
            path.AddArc(panel.Left, panel.Top, diameter, diameter, 180, 90);
            path.AddArc(panel.Right - diameter, panel.Top,
                diameter, diameter, 270, 90);
            path.AddArc(panel.Right - diameter, panel.Bottom - diameter,
                diameter, diameter, 0, 90);
            path.AddArc(panel.Left, panel.Bottom - diameter,
                diameter, diameter, 90, 90);
            path.CloseFigure();

            GraphicsState state = g.Save();
            g.TranslateTransform(fontSize * .1f, fontSize * .1f);
            using (SolidBrush shadow =
                new(Color.FromArgb(Alpha(75), 0, 0, 0)))
                g.FillPath(shadow, path);
            g.Restore(state);

            using (SolidBrush background =
                new(Color.FromArgb(Alpha(175), 18, 18, 18)))
                g.FillPath(background, path);
            using (Pen border = new(Color.FromArgb(
                Alpha(70), 255, 255, 255),
                Math.Max(1, fontSize * .025f)))
                g.DrawPath(border, path);

            PointF textPoint = new(panel.Left + paddingX,
                panel.Top + paddingY);
            using (SolidBrush textShadow =
                new(Color.FromArgb(Alpha(130), 0, 0, 0)))
                g.DrawString(text, font, textShadow,
                    new PointF(textPoint.X + fontSize * .04f,
                        textPoint.Y + fontSize * .04f), format);
            using (SolidBrush white = new(Color.FromArgb(
                Alpha(255), 255, 255, 255)))
                g.DrawString(text, font, white, textPoint, format);
            return b;
        }

        public static void RedrawImage()
        {
            if (suspended) return;
            int version = Interlocked.Increment(ref redrawVersion);
            _ = RedrawImageAsync(version);
        }

        private static async Task RedrawImageAsync(int version)
        {
            await renderGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (suspended || version != Volatile.Read(ref redrawVersion))
                    return;
                await Task.Run(() =>
                {
                    IDesktopWallpaper? wallpaper = null;
                    try
                    {
                        wallpaper =
                            (IDesktopWallpaper)(new DesktopWallpaperClass());
                        KeyValuePair<uint, Bitmap>[] sources;
                        lock (initialImagesLock)
                        {
                            sources = initialImages.Select(pair =>
                                new KeyValuePair<uint, Bitmap>(
                                    pair.Key,
                                    new Bitmap(pair.Value))).ToArray();
                        }

                        try
                        {
                            foreach (KeyValuePair<uint, Bitmap> source in sources)
                            {
                                if (suspended ||
                                    version != Volatile.Read(ref redrawVersion))
                                    break;
                                string monitorId = wallpaper
                                    .GetMonitorDevicePathAt(source.Key);
                                Rect monitorRect =
                                    wallpaper.GetMonitorRECT(monitorId);
                                using Bitmap rendered =
                                    CreateRenderedWallpaper(
                                        source.Value, monitorRect,
                                        _modelname, _outfit);
                                string path = WallpaperPath(source.Key);
                                rendered.Save(path, ImageFormat.Jpeg);
                                if (!suspended &&
                                    version == Volatile.Read(ref redrawVersion))
                                    wallpaper.SetWallpaper(monitorId, path);
                            }
                        }
                        finally
                        {
                            foreach (KeyValuePair<uint, Bitmap> source in sources)
                                source.Value.Dispose();
                        }
                    }
                    finally { ReleaseDesktopWallpaper(wallpaper); }
                }).ConfigureAwait(false);
            }
            catch (Exception) { }
            finally
            {
                renderGate.Release();
            }
        }



        static async Task<Bitmap?> GetImageBitmapFromUrl(string url)
        {
            try
            {
                if (File.Exists(url))
                {
                    using Image localImage = Image.FromFile(url);
                    return new Bitmap(localImage);
                }
                byte[] imageBytes = await client.GetByteArrayAsync(url)
                    .ConfigureAwait(false);
                using var ms = new MemoryStream(imageBytes);
                using var source = new Bitmap(ms);
                return new Bitmap(source);
            }
            catch (Exception) { return null; }
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
            using ImageAttributes attributes = new ImageAttributes();
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
            IDesktopWallpaper? wallpaper = null;
            try
            {
                wallpaper =
                    (IDesktopWallpaper)(new DesktopWallpaperClass());
                foreach(KeyValuePair<uint,string> paper in originalWallpaper)
                {
                        var monitorId = wallpaper.GetMonitorDevicePathAt(paper.Key);
                        wallpaper.SetWallpaper(monitorId.ToString(), paper.Value);
                }
            }
            catch(Exception){}
            finally { ReleaseDesktopWallpaper(wallpaper); }
        }

        public static void SuspendAndRestoreOriginalDesktop()
        {
            suspended = true;
            RestoreWallpaper();
            if (Utils.DefaultIconsVisible != Utils.DesktopIconsVisible())
                Utils.ToggleDesktopIcons();
        }

        public static void ResumeQuickPlayerDesktop()
        {
            suspended = false;
            IDesktopWallpaper? wallpaper = null;
            try
            {
                wallpaper =
                    (IDesktopWallpaper)(new DesktopWallpaperClass());
                uint[] monitorNumbers;
                lock (initialImagesLock)
                    monitorNumbers = initialImages.Keys.ToArray();
                foreach (uint monitorNumber in monitorNumbers)
                {
                    string path = WallpaperPath(monitorNumber);
                    if (File.Exists(path))
                        wallpaper.SetWallpaper(
                            wallpaper.GetMonitorDevicePathAt(monitorNumber),
                            path);
                }
                if (Properties.Settings.Default.HideDesktopIcons)
                    hideIcons();
                else
                    showIcons();
            }
            catch { }
            finally { ReleaseDesktopWallpaper(wallpaper); }
        }

        internal static void RestoreWallpaperByID(uint monitorNumber)
        {
            IDesktopWallpaper? wallpaper = null;
            try
            {
                wallpaper =
                    (IDesktopWallpaper)(new DesktopWallpaperClass());
             
                if (originalWallpaper.ContainsKey(monitorNumber))
                {
                    var monitorId = wallpaper.GetMonitorDevicePathAt(monitorNumber);
                    wallpaper.SetWallpaper(monitorId.ToString(), originalWallpaper[monitorNumber]);
                    lock (initialImagesLock)
                    {
                        if (initialImages.Remove(monitorNumber,
                                out Bitmap? removed))
                            removed.Dispose();
                    }
                }
            }
            catch (Exception){}
            finally { ReleaseDesktopWallpaper(wallpaper); }
        }

        private static void ReleaseDesktopWallpaper(
            IDesktopWallpaper? wallpaper)
        {
            if (wallpaper != null && Marshal.IsComObject(wallpaper))
                Marshal.FinalReleaseComObject(wallpaper);
        }
    }
}
