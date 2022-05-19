using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IStripperQuickPlayer.BLL
{
    internal class CardFolders
    {
        internal static string findCardMetaFolder(string tag)
        {
              RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\System", false);
            string localapp = "";
            if (key != null)
            {
                var a = key.GetValue("DataPath", "");
                if (a != null)
                { 
                    localapp = a.ToString() ?? "";
                    key.Close();
                }
                else
                {
                    MessageBox.Show(@"Could not find registry key @CurrentUser\Software\Totem\vghd\System\DataPath ", "");
                }
                if (localapp == "")
                {
                    MessageBox.Show(@"Registry key @CurrentUser\Software\Totem\vghd\System\DataPath is empty?", "");
                }
            }
            else
            {
                MessageBox.Show(@"Could not find registry key @CurrentUser\Software\Totem\vghd\System", "");
            }
                       
            string fullpath = Path.Combine(localapp, tag);
            if (Directory.Exists(fullpath)) return fullpath;
            return "";
        }

          internal static string findCardFolder(string tag)
          {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\System", false);
            string localapp = "";
            if (key != null)
            {
                var a = key.GetValue("ModelsPath", "");
                if (a != null)
                { 
                    localapp = a.ToString() ?? "";
                    key.Close();
                }
                else
                {
                    MessageBox.Show(@"Could not find registry key @CurrentUser\Software\Totem\vghd\System\ModelsPath", "");
                }
                if (localapp == "")
                {
                    MessageBox.Show(@"Registry key @CurrentUser\Software\Totem\vghd\System\ModelsPath is empty?", "");
                }
            }
            else
            {
                MessageBox.Show(@"Could not find registry key @CurrentUser\Software\Totem\vghd\System", "");
            }
                       
            if (Directory.Exists(Path.Combine(localapp,tag))) return Path.Combine(localapp,tag);
            string[] localapparray=null;
            key = Registry.CurrentUser.OpenSubKey(@"Software\Totem\vghd\System", false);
            if (key != null)
            {
                var a = key.GetValue("ModelsMultiPath", "");
                if (a != null)
                { 
                    localapparray = (string[])a;
                    key.Close();
                }
                else
                {
                    MessageBox.Show(@"Could not find registry key @CurrentUser\Software\Totem\vghd\System\ModelsMultiPath", "");
                }
                if (localapp == "")
                {
                    MessageBox.Show(@"Registry key @CurrentUser\Software\Totem\vghd\System\ModelsMultiPath is empty?", "");
                }
            }
            foreach (var folder in localapparray)
            {
                if (Directory.Exists(Path.Combine(folder,tag))) return Path.Combine(folder,tag);
            }
            return "";
          
        }
    }
}
