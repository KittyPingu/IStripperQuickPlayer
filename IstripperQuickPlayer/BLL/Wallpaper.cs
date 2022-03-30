using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DesktopWallpaper;
using System.Net;

namespace IStripperQuickPlayer.BLL
{
    public static class Wallpaper
    {
        public static Dictionary<uint, string> originalWallpaper = new Dictionary<uint, string>();
        public static void ChangeWallpaper(uint monitorNumber, string url)
        {
            if (url == null)return;
            var wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());  
            try
            {
                var monitorId = wallpaper.GetMonitorDevicePathAt(monitorNumber);
               
                if (!originalWallpaper.ContainsKey(monitorNumber)) originalWallpaper.Add(monitorNumber, wallpaper.GetWallpaper(monitorId.ToString()));

                string wpfilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "wallpaper" + monitorNumber.ToString() + ".png");
                using(WebClient client = new WebClient())
                {
                  client.DownloadFile(url,wpfilepath);
                }


                wallpaper.SetWallpaper(monitorId.ToString(), wpfilepath);
            }
            catch (Exception ex){}
        }

        public static void RestoreWallpaper()
        {
            var wallpaper = (IDesktopWallpaper)(new DesktopWallpaperClass());  
            foreach(KeyValuePair<uint,string> paper in originalWallpaper)
            {
                try
                {
                    var monitorId = wallpaper.GetMonitorDevicePathAt(paper.Key);
                    wallpaper.SetWallpaper(monitorId.ToString(), paper.Value);
                }
                catch(Exception ex){}
            }
        }
    }
}
