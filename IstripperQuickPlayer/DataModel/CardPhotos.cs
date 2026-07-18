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
        internal RootPhotos? data;

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
            return data?.photos.Length ?? 0;
        }

        public Image? getPhoto(int number)
        {
            ///fileaccess/image/f0953/VGI1446P02119.jpg/6f9?filename=VGI1446P02119.jpg&private=yes&ui=m28734858&uk=EGNILAPABNIHCKLIIDKGOIPABLEBPAKJ&explicit=1&language=en
            string? fullpath = getPhotoFullPath(number);
            if (fullpath == null) return null;
            return DownloadImageFromUrl(fullpath);
        }

        public string? getPhotoFullPath(int number)
        {
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
            if (data == null || data.photos.Length == 0)
                return Array.Empty<Bitmap>();

            return await Task.WhenAll(data.photos.Select(i =>
                GetImageBitmapFromUrl("http://www.istripper.com/" + i.files.mini)));
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
