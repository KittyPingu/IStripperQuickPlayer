using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;

namespace IStripperQuickPlayer.DataModel
{
    [Serializable]
    public class FilterSettings : ICloneable
    {
        internal decimal minAge=18;
        internal decimal maxAge=43;
        internal decimal minBust=0;
        internal decimal maxBust=99;
        internal decimal minRating=0;
        internal string tags="";
        internal decimal maxRating=5;
        internal bool IStripper=true;
        internal bool IStripperClassic=true;
        internal bool IStripperXXX=true;
        internal bool VGClassic=true;
        internal bool DeskBabes =true;
        internal bool Special=true;
        internal bool Normal=true;
        internal bool VirtuaGuy = true;
        internal decimal minMyRating=0;
        internal decimal maxMyRating=10;
        internal DateTime minDate=new DateTime(2007,1,1);
        internal DateTime maxDate=DateTime.Now;

        public object Clone()
        {
            return this.MemberwiseClone();
        }
    }

    [Serializable]
    internal static class FilterSettingsList
    {
        internal static Dictionary<string, FilterSettings> filters = new Dictionary<string, FilterSettings>{ };
               
        internal static void Save(string settingsName, FilterSettings? filterSettings)
        {
            if (string.IsNullOrEmpty(settingsName))
            {
                MessageBox.Show("You must enter a name for the filter", "No name supplied", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (filters.ContainsKey(settingsName))
                filters[settingsName] = (FilterSettings)filterSettings.Clone();
            else
                filters.Add(settingsName, (FilterSettings)filterSettings.Clone());
            Persist();
        }

        internal static void Persist()
        {
            
            string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "filters.bin");
            string mdatafolder = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer");
            if (!Directory.Exists(mdatafolder))
                Directory.CreateDirectory(mdatafolder);
            System.IO.Stream ms = File.OpenWrite(mdatafilepath);     
            BinaryFormatter formatter = new BinaryFormatter();              
            formatter.Serialize(ms, filters);  
            ms.Flush();  
            ms.Close();  
            ms.Dispose();  
        }

        internal static void Load()
        {
            string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "filters.bin");
            if (File.Exists(mdatafilepath))
            {
                FileStream fs = null;
                try
                {
                    BinaryFormatter formatter = new BinaryFormatter();  
   
                    //Reading the file from the server  
                    fs = File.Open(mdatafilepath, FileMode.Open);   
                    object obj = formatter.Deserialize(fs);  
                    filters = (Dictionary<string, FilterSettings>)obj;  
                    fs.Flush();  
                    fs.Close();  
                    fs.Dispose(); 
                }
                catch (Exception ex)
                {
                    fs.Flush();  
                    fs.Close();  
                    fs.Dispose(); 
                    string backupfile = mdatafilepath + "." + DateTime.Now.Ticks.ToString();
                    File.Copy(mdatafilepath, backupfile, true);
                    File.Delete(mdatafilepath);
                    MessageBox.Show("Could not read filters.bin - maybe the data model has changed. Filters.bin has been backed up to " + backupfile + " and filters reset");
                }
            }

        }

        internal static FilterSettings GetFilter(string filterName)
        {
            if (filters.ContainsKey(filterName))
               return filters[filterName];
            else
                return new FilterSettings();
        }

        internal static void Delete(string filterName)
        {
             if (filters.ContainsKey(filterName))
                filters.Remove(filterName);
             Persist();
        }
    }
}
