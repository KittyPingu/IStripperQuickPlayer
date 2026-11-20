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
using System.Diagnostics;

namespace IStripperQuickPlayer.BLL
{
    public static class Wallpaper
    {
        private static readonly HttpClient client = new();
        public static Dictionary<uint, string> originalWallpaper = new Dictionary<uint, string>();
        private static readonly Dictionary<uint, WallpaperImage> initialImages = [];
        public static string _modelname = "";
        public static string _outfit = "";
        private static volatile bool suspended;
        private static readonly SemaphoreSlim renderGate = new(1, 1);
        private static readonly object initialImagesLock = new();
        private static readonly object blurBuffersLock = new();
        private static readonly Dictionary<Size, DirectBitmap> blurBuffers = [];
        private static int[] blurScratch = [];
        private static int redrawVersion;

        private sealed class WallpaperImage(string url, Bitmap original, bool legacyLogo = false) : IDisposable
        {
            public string Url { get; } = url;
            public Bitmap Original { get; } = original;
            public Bitmap? Cleaned { get; set; }
            public bool LegacyLogo { get; } = legacyLogo;
            public bool HasLogo { get; } = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https") &&
                (uri.Host.Equals("istripper.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.EndsWith(".istripper.com", StringComparison.OrdinalIgnoreCase));

            public void Dispose()
            {
                Cleaned?.Dispose();
                Original.Dispose();
            }
        }

        private static void SetWallpaperImage(uint monitor, WallpaperImage image)
        {
            lock (initialImagesLock)
            {
                initialImages.TryGetValue(monitor, out WallpaperImage? previous);
                initialImages[monitor] = image;
                if (previous != null && !initialImages.ContainsValue(previous))
                    previous.Dispose();
            }
        }

        private static void ReleaseWallpaperImage(uint monitor)
        {
            lock (initialImagesLock)
                if (initialImages.Remove(monitor, out WallpaperImage? image) &&
                    !initialImages.ContainsValue(image))
                    image.Dispose();
        }

        // Called under renderGate: monitors share one download and cleanup for each active iStripper URL.
        private static async Task<WallpaperImage?> LoadWallpaperImage(uint monitor, string url, bool legacyLogo = false)
        {
            lock (initialImagesLock)
            {
                WallpaperImage? existing = initialImages.Values.FirstOrDefault(image =>
                    image.HasLogo && image.Url == url && image.LegacyLogo == legacyLogo);
                if (existing != null)
                {
                    SetWallpaperImage(monitor, existing);
                    return existing;
                }
            }
            Bitmap? downloaded = await GetImageBitmapFromUrl(url).ConfigureAwait(false);
            if (downloaded == null) return null;
            WallpaperImage image = new(url, downloaded, legacyLogo);
            SetWallpaperImage(monitor, image);
            return image;
        }

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

        public static async Task ChangeWallpaper(uint monitorNumber, string? url, string modelname, string outfit,
            bool legacyLogo)
        {
            if (suspended) return;
            bool hideDesktopIcons =
                Properties.Settings.Default.HideDesktopIcons;
            await Task.Run(() =>
            {
                if (hideDesktopIcons)
                    hideIcons();
                else
                    showIcons();
            }).ConfigureAwait(false);
            if (url == null) return;
            _modelname = modelname;
            _outfit = outfit;
            int version = Interlocked.Increment(ref redrawVersion);
            await renderGate.WaitAsync().ConfigureAwait(false);
            try
            {
                WallpaperImage? image = await LoadWallpaperImage(monitorNumber, url, legacyLogo).ConfigureAwait(false);
                if (image == null) return;
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
                        using Bitmap rendered = CreateRenderedWallpaper(
                            image, monitorRect, modelname, outfit);
                        rendered.Save(wpfilepath, ImageFormat.Jpeg);
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

        private static Bitmap CreateRenderedWallpaper(WallpaperImage source, Rect rect,
            string modelname, string outfit)
        {
            using Bitmap resized = PrepareWallpaper(source, rect);
            Bitmap rendered;
            float brightness = (float)(
                (double)Properties.Settings.Default.WallpaperBrightness / 100);
            if (Properties.Settings.Default.BlurRadius > 0)
            {
                lock (blurBuffersLock)
                    rendered = AdjustBrightness(
                        AddBlur(resized,
                            Convert.ToInt32(
                                Properties.Settings.Default.BlurRadius)),
                        brightness);
            }
            else
            {
                ReleaseBlurCache();
                rendered = AdjustBrightness(resized, brightness);
            }

            if (Properties.Settings.Default.WallpaperDetails)
                AddDetails(rendered, rect, modelname, outfit);
            return rendered;
        }

        private static Bitmap PrepareWallpaper(WallpaperImage source, Rect rect)
        {
            if (!Properties.Settings.Default.WallpaperRemoveLogo || !source.HasLogo)
                return ResizeBitmap(source.Original, rect);
            if (source.Cleaned == null)
            {
                try { source.Cleaned = RemoveLogo(source.Original, source.LegacyLogo); }
                catch (Exception error)
                {
                    Trace.TraceWarning("Wallpaper logo removal failed: " + error.Message);
                    source.Cleaned = new Bitmap(source.Original);
                }
            }
            return ResizeBitmap(source.Cleaned, rect);
        }

        internal static Rectangle LogoRectangle(Size size, bool legacyLogo = false)
        {
            int shorter = Math.Min(size.Width, size.Height);
            if (shorter < 100) return Rectangle.Empty;
            // shortcut: fixed VirtuaGirlHD bounds for Classic cards; revisit if branding changes.
            if (legacyLogo)
                return Rectangle.FromLTRB(12, Math.Max(1, size.Height - 295),
                    74, size.Height - 18);
            int left = Math.Max(1, (int)(shorter * .014));
            int top = size.Height - (int)Math.Ceiling(shorter * .222);
            int right = (int)Math.Ceiling(shorter * .057);
            int bottom = size.Height - Math.Max(1, (int)(shorter * .014));
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        internal static Bitmap RemoveLogo(Bitmap source, bool legacyLogo = false)
        {
            Rectangle area = LogoRectangle(source.Size, legacyLogo);
            if (area.IsEmpty) return new Bitmap(source);
            string temporary = Path.Combine(Path.GetTempPath(), "iqp-wallpaper-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                string input = Path.Combine(temporary, "source.png");
                string output = Path.Combine(temporary, "clean.png");
                source.Save(input, ImageFormat.Png);
                ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true
                };
                foreach (string argument in new[] { "-nostdin", "-v", "error", "-i", input,
                    "-vf", $"delogo=x={area.X}:y={area.Y}:w={area.Width}:h={area.Height}:show=0",
                    "-frames:v", "1", "-pix_fmt", "rgb24", output })
                    start.ArgumentList.Add(argument);
                using Process process = Process.Start(start) ?? throw new IOException("FFmpeg did not start.");
                Task<string> errors = process.StandardError.ReadToEndAsync();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(15000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    throw new TimeoutException("FFmpeg logo removal timed out.");
                }
                if (process.ExitCode != 0)
                    throw new IOException("FFmpeg logo removal failed: " + errors.GetAwaiter().GetResult());
                stdout.GetAwaiter().GetResult();
                using Bitmap cleaned = new(output);
                Bitmap result = new(source);
                using Graphics graphics = Graphics.FromImage(result);
                graphics.DrawImage(cleaned, area, area, GraphicsUnit.Pixel);
                return result;
            }
            finally { Directory.Delete(temporary, recursive: true); }
        }

        internal static void VerifyLogoRemoval()
        {
            if (!LogoRectangle(new Size(20, 30)).IsEmpty)
                throw new InvalidOperationException("Tiny images must be skipped.");
            foreach (Size legacySize in new[] { new Size(4500, 3003), new Size(1200, 1800) })
            {
                Rectangle legacyLogo = new(16, legacySize.Height - 290, 54, 268);
                if (!LogoRectangle(legacySize, true).Contains(legacyLogo))
                    throw new InvalidOperationException("Legacy VirtuaGirlHD logo falls outside the patch.");
            }
            if (LogoRectangle(new Size(3375, 2252)) != Rectangle.FromLTRB(31, 1752, 129, 2221))
                throw new InvalidOperationException("Modern logo bounds changed.");
            foreach (var (size, legacy) in new[] {
                (new Size(600, 400), false), (new Size(400, 600), false),
                (new Size(600, 400), true), (new Size(400, 600), true) })
            {
                using Bitmap source = new(size.Width, size.Height);
                Rectangle area = LogoRectangle(size, legacy);
                using (Graphics graphics = Graphics.FromImage(source))
                {
                    graphics.Clear(Color.White);
                    graphics.FillRectangle(Brushes.Black, area.X + 3, area.Y + 3,
                        area.Width - 6, area.Height - 6);
                }
                using Bitmap cleaned = RemoveLogo(source, legacy);
                if (cleaned.Size != size ||
                    cleaned.GetPixel(area.X + area.Width / 2, area.Y + area.Height / 2).R < 240 ||
                    source.GetPixel(area.X + area.Width / 2, area.Y + area.Height / 2).R != 0)
                    throw new InvalidOperationException("Logo removal failed or changed the source.");
                for (int y = 0; y < size.Height; y++)
                    for (int x = 0; x < size.Width; x++)
                        if (!area.Contains(x, y) && source.GetPixel(x, y) != cleaned.GetPixel(x, y))
                            throw new InvalidOperationException("Logo removal changed pixels outside the patch.");

                bool previous = Properties.Settings.Default.WallpaperRemoveLogo;
                const uint monitor = uint.MaxValue;
                const uint secondMonitor = uint.MaxValue - 1;
                Rect rect = new() { Right = size.Width, Bottom = size.Height };
                WallpaperImage image = new("https://www.istripper.com/test.jpg", new Bitmap(source), legacy);
                try
                {
                    SetWallpaperImage(monitor, image);
                    Properties.Settings.Default.WallpaperRemoveLogo = true;
                    using Bitmap enabled = PrepareWallpaper(image, rect);
                    Bitmap? cached = image.Cleaned;
                    WallpaperImage shared = LoadWallpaperImage(secondMonitor, image.Url, legacy).GetAwaiter().GetResult()
                        ?? throw new InvalidOperationException("Could not reuse the active photo.");
                    using Bitmap repeated = PrepareWallpaper(shared,
                        new Rect { Right = size.Width / 2, Bottom = size.Height / 2 });
                    if (!ReferenceEquals(image, shared) || !ReferenceEquals(cached, shared.Cleaned) ||
                        repeated.Size != new Size(size.Width / 2, size.Height / 2) ||
                        enabled.GetPixel(area.X + 4, area.Y + 4).R < 240)
                        throw new InvalidOperationException("Monitors did not share cleanup with independent sizes.");
                    Properties.Settings.Default.WallpaperRemoveLogo = false;
                    using Bitmap disabled = PrepareWallpaper(shared, rect);
                    if (disabled.GetPixel(area.X + 4, area.Y + 4).R != 0)
                        throw new InvalidOperationException("Disabling removal did not restore the logo.");
                    Properties.Settings.Default.WallpaperRemoveLogo = true;
                    WallpaperImage localImage = new("test-local.jpg", new Bitmap(source));
                    SetWallpaperImage(monitor, localImage);
                    using Bitmap retained = PrepareWallpaper(shared, rect);
                    if (retained.GetPixel(area.X + 4, area.Y + 4).R < 240)
                        throw new InvalidOperationException("Replacing one monitor disposed the shared photo.");
                    using Bitmap local = PrepareWallpaper(localImage, rect);
                    if (local.GetPixel(area.X + 4, area.Y + 4).R != 0)
                        throw new InvalidOperationException("Non-iStripper photos must be unchanged.");
                    ReleaseWallpaperImage(secondMonitor);
                    foreach (Bitmap released in new[] { image.Original, image.Cleaned! })
                    {
                        bool disposed = false;
                        try { _ = released.GetPixel(0, 0); }
                        catch (ArgumentException) { disposed = true; }
                        if (!disposed)
                            throw new InvalidOperationException("Unused shared photo was not disposed.");
                    }
                }
                finally
                {
                    Properties.Settings.Default.WallpaperRemoveLogo = previous;
                    ReleaseWallpaperImage(monitor);
                    ReleaseWallpaperImage(secondMonitor);
                }
            }
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
                        KeyValuePair<uint, WallpaperImage>[] sources;
                        lock (initialImagesLock)
                            sources = initialImages.ToArray();

                        foreach (KeyValuePair<uint, WallpaperImage> source in sources)
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
            renderGate.Wait();
            IDesktopWallpaper? wallpaper = null;
            try
            {
                wallpaper =
                    (IDesktopWallpaper)(new DesktopWallpaperClass());
             
                if (originalWallpaper.ContainsKey(monitorNumber))
                {
                    var monitorId = wallpaper.GetMonitorDevicePathAt(monitorNumber);
                    wallpaper.SetWallpaper(monitorId.ToString(), originalWallpaper[monitorNumber]);
                }
            }
            catch (Exception){}
            finally
            {
                ReleaseWallpaperImage(monitorNumber);
                ReleaseDesktopWallpaper(wallpaper);
                renderGate.Release();
            }
        }

        private static void ReleaseDesktopWallpaper(
            IDesktopWallpaper? wallpaper)
        {
            if (wallpaper != null && Marshal.IsComObject(wallpaper))
                Marshal.FinalReleaseComObject(wallpaper);
        }
    }
}
