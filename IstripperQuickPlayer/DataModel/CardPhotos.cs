using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace IStripperQuickPlayer.DataModel
{
    internal class CardPhotos
    {
        private string cardTag = "";
        internal RootPhotos data;

        public async Task<object> LoadCardPhotos(HttpClient httpClient, string nowPlayingTag)
        {
            cardTag = nowPlayingTag;
            string url = @"https://www.istripper.com/free/sets/" + cardTag + @"/photos/photos.json";
            var jsonString = await httpClient.GetStringAsync(url);
            if (jsonString == null) return false;
            data = Newtonsoft.Json.JsonConvert.DeserializeObject<RootPhotos>(jsonString);            
            //if (data == null) return false;
            //if (data.Last == null) return false;
            //JToken last = data.Last;
            //if (last.First == null) return false;
            //JToken list = last.First;
            //var listphotos = list.ToList();
            return true;
        }

        public int getNumberOfPhotos()
        {
            if (data == null || data.photos == null) return 0;
            return data.photos.Count();
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
            if (number < 0 || number > getNumberOfPhotos()) return null;
            string fullpath = "";
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
            Random rnd = new Random();
            var p = data.photos.Where(c => c.size.width > c.size.height)
                  .OrderBy(x => rnd.Next())
                  .FirstOrDefault();
            if (p == null) return null;
            return getPhotoFullPathFromPhoto(p);
        }

        public async Task<Bitmap[]> getThumbnails()
        {
            ///fileaccess/image/f0953/VGI1446P02119.jpg/6f9?filename=VGI1446P02119.jpg&private=yes&ui=m28734858&uk=EGNILAPABNIHCKLIIDKGOIPABLEBPAKJ&explicit=1&language=en
            if (getNumberOfPhotos()==0) return null;
            string fullpath = "";
           
            return (await Task.WhenAll(data.photos.Select(i => GetImageBitmapFromUrl("http://www.istripper.com/" + i.files.mini))));

           
        }

        async Task<Bitmap> GetImageBitmapFromUrl( string url)
        {
            Debug.WriteLine(url);
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
            catch (Exception ex)
            {
                //Silence is gold.
                Debug.WriteLine(ex.Message);
            }
            return imageBitmap;
        }


        private System.Drawing.Image DownloadImageFromUrl(string imageUrl)
        {
            System.Drawing.Image image = null;
 
            try
            {
                System.Net.HttpWebRequest webRequest = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(imageUrl);
                webRequest.AllowWriteStreamBuffering = true;
                webRequest.Timeout = 30000;
 
                System.Net.WebResponse webResponse = webRequest.GetResponse();
 
                System.IO.Stream stream = webResponse.GetResponseStream();
 
                image = System.Drawing.Image.FromStream(stream);
 
                webResponse.Close();
            }
            catch (Exception ex)
            {
                return null;
            }
 
            return image;
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
        public string zip { get; set; }
        public Photo[] photos { get; set; }
    }

    public class Photo
    {
        public string id { get; set; }
        public string type { get; set; }
        public string access { get; set; }
        public string name { get; set; }
        public Size size { get; set; }
        public Files files { get; set; }
        public string fullscreen { get; set; }
    }

    public class Size
    {
        public int height { get; set; }
        public int width { get; set; }
    }

    public class Files
    {
        public string mini { get; set; }
        public string full { get; set; }
    }



}
