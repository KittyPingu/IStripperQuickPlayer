using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace IStripperQuickPlayer.DataModel
{
    internal class CardPhotos
    {
        private static readonly HttpClient defaultClient = new();
        private string cardTag = "";
        private HttpClient client = defaultClient;
        private string[] localPhotos = [];
        internal RootPhotos? data;

        public bool LoadLocalPhotos(string? folder)
        {
            data = null;
            localPhotos = [];
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return false;
            HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase)
                { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };
            try
            {
                localPhotos = Directory.EnumerateFiles(folder, "*",
                        SearchOption.AllDirectories)
                    .Where(path => extensions.Contains(Path.GetExtension(path)))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                localPhotos = [];
            }
            return localPhotos.Length > 0;
        }

        public async Task<bool> LoadCardPhotos(HttpClient httpClient, string nowPlayingTag)
        {
            client = httpClient;
            cardTag = nowPlayingTag;
            string url = @"https://www.istripper.com/free/sets/" + cardTag.Split(new char[] {'-'}).First() + @"/photos/photos.json";
            var jsonString = await httpClient.GetStringAsync(url).ConfigureAwait(false);
            data = Newtonsoft.Json.JsonConvert.DeserializeObject<RootPhotos>(jsonString);
            return data != null;
        }

        public int getNumberOfPhotos()
        {
            if (localPhotos.Length > 0) return localPhotos.Length;
            return data?.photos.Length ?? 0;
        }

        public Image? getPhoto(int number)
        {
            ///fileaccess/image/f0953/VGI1446P02119.jpg/6f9?filename=VGI1446P02119.jpg&private=yes&ui=m28734858&uk=EGNILAPABNIHCKLIIDKGOIPABLEBPAKJ&explicit=1&language=en
            string? fullpath = getPhotoFullPath(number);
            if (fullpath == null) return null;
            if (File.Exists(fullpath)) return LoadLocalImage(fullpath);
            return DownloadImageFromUrl(fullpath);
        }

        public string? getPhotoFullPath(int number)
        {
            if (number >= 0 && number < localPhotos.Length)
                return localPhotos[number];
            if (data == null || number < 0 || number >= data.photos.Length)
                return null;
            var p = data.photos[number];
            return getPhotoFullPathFromPhoto(p);
        }

        private string? getPhotoFullPathFromPhoto(Photo p)
        {
            string? fullpath = null;
            if (p.access == "public")
            {
                fullpath = "http://www.istripper.com/" + p.files.full;
            }
            else
            {
                string userkey = getUserKey();
                string username = getUserName();
                fullpath = "http://www.istripper.com" + p.files.full + "?filename=" + p.name + "&private=yes&ui=" + username + "&uk=" + userkey + "&explicit=1&language=en";
            }
            return fullpath;
        }

        public string? getRandomWidescreenURL()
        {
            if (getNumberOfPhotos() == 0) return null;
            if (localPhotos.Length > 0)
            {
                string[] widescreen = localPhotos.Where(IsLandscape).ToArray();
                string[] candidates = widescreen.Length > 0 ? widescreen : localPhotos;
                return candidates[Random.Shared.Next(candidates.Length)];
            }
            if (data == null) return null;
            Random rnd = new Random();
            var p = data.photos.Where(c => c.size.width > c.size.height)
                  .OrderBy(x => rnd.Next())
                  .FirstOrDefault();
            if (p == null) return null;
            return getPhotoFullPathFromPhoto(p);
        }

        public async Task<Bitmap[]> getThumbnails()
        {
            if (localPhotos.Length > 0)
                return await Task.Run(() => localPhotos.Select(CreateThumbnail).ToArray());
            if (data == null || data.photos.Length == 0)
                return Array.Empty<Bitmap>();

            return await Task.WhenAll(data.photos.Select(i =>
                GetImageBitmapFromUrl("http://www.istripper.com/" + i.files.mini)));
        }

        private static Bitmap CreateThumbnail(string path)
        {
            using Image? source = LoadLocalImage(path);
            if (source == null) return new Bitmap(1, 1);
            double scale = Math.Min(256d / source.Width, 256d / source.Height);
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            Bitmap thumbnail = new(width, height);
            using Graphics graphics = Graphics.FromImage(thumbnail);
            graphics.InterpolationMode =
                System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, 0, 0, width, height);
            return thumbnail;
        }

        private static Bitmap? LoadLocalImage(string path)
        {
            try
            {
                using Image source = Image.FromFile(path);
                return new Bitmap(source);
            }
            catch { return null; }
        }

        private static bool IsLandscape(string path)
        {
            using Image? image = LoadLocalImage(path);
            return image != null && image.Width > image.Height;
        }

        async Task<Bitmap> GetImageBitmapFromUrl( string url)
        {
            Debug.WriteLine(url);
            try
            {
                byte[] imageBytes = await client.GetByteArrayAsync(url);
                using var ms = new MemoryStream(imageBytes);
                using var source = new Bitmap(ms);
                return new Bitmap(source);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return new Bitmap(1, 1);
            }
        }


        private Image? DownloadImageFromUrl(string imageUrl)
        {
            try
            {
                byte[] imageBytes = client.GetByteArrayAsync(imageUrl)
                    .GetAwaiter().GetResult();
                using var stream = new MemoryStream(imageBytes);
                using var source = Image.FromStream(stream);
                return new Bitmap(source);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private string getUserName()
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\DLM", false);
            string username = "";
            if (key != null)
            {
                var a = key.GetValue("username", "");
                if (a != null)
                { 
                    username = a.ToString() ?? "";
                    key.Close();
                }
            }
            return username;        
        }

        private string getUserKey()
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\DLM", false);
            string userkey = "";
            if (key != null)
            {
                var a = key.GetValue("key", "");
                if (a != null)
                { 
                    userkey = a.ToString() ?? "";
                    key.Close();
                }
            }
            return userkey;        
        }
    }

    public class RootPhotos
    {
        public string zip { get; set; } = "";
        public Photo[] photos { get; set; } = Array.Empty<Photo>();
    }

    public class Photo
    {
        public string id { get; set; } = "";
        public string type { get; set; } = "";
        public string access { get; set; } = "";
        public string name { get; set; } = "";
        public Size size { get; set; } = new();
        public Files files { get; set; } = new();
        public string fullscreen { get; set; } = "";
    }

    public class Size
    {
        public int height { get; set; }
        public int width { get; set; }
    }

    public class Files
    {
        public string mini { get; set; } = "";
        public string full { get; set; } = "";
    }



}
