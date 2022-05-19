using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IStripperQuickPlayer.BLL
{
    internal class PlaylistLoader
    {

        internal static List<string> LoadPlaylist(string filename)
        {
            List<string> playlist = new List<string>{ };         
            //ImageList largeimagelist = new ImageList();
            //largeimagelist.ImageSize = new Size(130,180);
            //largeimagelist.ColorDepth = ColorDepth.Depth32Bit;
         
            var style = NumberStyles.AllowDecimalPoint;
            var culture = CultureInfo.CreateSpecificCulture(CultureInfo.CurrentCulture.Name);
            culture.NumberFormat.NumberDecimalSeparator = ".";
            if (!string.IsNullOrEmpty(filename))
            {
                
                Form1? frm = Utils.GetMainForm();
                using (var stream = File.Open(filename, FileMode.Open))
                {
                    using (var reader = new BinaryReader(stream, Encoding.UTF8, false))
                    {
                        var version = getInt32(reader);
                        var number = getInt32(reader);
                        for (int i = 0; i < number; i++)
                        {
                            var j = getInt32(reader);
                            var s = getStringUnicode(reader, j);
                            playlist.Add(s);
                        }
                    }
                }
            }
            return playlist;
        }

        private static int getInt32(BinaryReader reader)
        {
            byte[] b = reader.ReadBytes(4);
            return b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3];
        }

        private static string getString(BinaryReader reader, int strlen)
        {
            byte[] b = reader.ReadBytes(strlen);
            return System.Text.Encoding.Default.GetString(b);
        }
               
        private static string getStringUnicode(BinaryReader reader, int strlen)
        {
            byte[] b = reader.ReadBytes(strlen);
            return System.Text.Encoding.Default.GetString(b.Where((x, i) => i % 2== 1).ToArray());
        }
    }
}
