using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace IStripperQuickPlayer.DataModel
{
    [Serializable]
    public class FilterSettings : ICloneable
    {
        internal decimal minAge=18;
        internal decimal maxAge=43;
        internal decimal minBust=0;
        internal decimal maxBust=99;
        internal decimal minWaist=0;
        internal decimal maxWaist=99;
        internal decimal minHips=0;
        internal decimal maxHips=99;
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
        internal bool TradingCard = true;
        internal bool Custom = true;
        internal bool NvidiaCustom = true;
        internal string genders =
            "Male,Female,Transgender,Femboy,Sissy,Non-Binary";
        internal decimal minMyRating=0;
        internal decimal maxMyRating=10;
        internal DateTime minDate=new DateTime(2000,1,1);
        internal DateTime maxDate=new DateTime(2099,1,1);

        [OnDeserializing]
        internal void SetNewFieldDefaults(StreamingContext context)
        {
            maxWaist = 99;
            maxHips = 99;
            Custom = true;
            NvidiaCustom = true;
            genders = "Male,Female,Transgender,Femboy,Sissy,Non-Binary";
        }

        public object Clone()
        {
            return this.MemberwiseClone();
        }

        internal bool HasSameValues(FilterSettings? other) =>
            other != null &&
            minAge == other.minAge &&
            maxAge == other.maxAge &&
            minBust == other.minBust &&
            maxBust == other.maxBust &&
            minWaist == other.minWaist &&
            maxWaist == other.maxWaist &&
            minHips == other.minHips &&
            maxHips == other.maxHips &&
            minRating == other.minRating &&
            maxRating == other.maxRating &&
            minMyRating == other.minMyRating &&
            maxMyRating == other.maxMyRating &&
            minDate == other.minDate &&
            maxDate == other.maxDate &&
            string.Equals(tags, other.tags,
                StringComparison.Ordinal) &&
            IStripper == other.IStripper &&
            IStripperClassic == other.IStripperClassic &&
            IStripperXXX == other.IStripperXXX &&
            VGClassic == other.VGClassic &&
            DeskBabes == other.DeskBabes &&
            Special == other.Special &&
            Normal == other.Normal &&
            VirtuaGuy == other.VirtuaGuy &&
            TradingCard == other.TradingCard &&
            Custom == other.Custom &&
            NvidiaCustom == other.NvidiaCustom &&
            string.Equals(genders, other.genders,
                StringComparison.Ordinal);
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
            if (filterSettings is null)
                return;

            if (filters.TryGetValue(settingsName, out FilterSettings? existing) &&
                existing.HasSameValues(filterSettings))
                return;
            filters[settingsName] = (FilterSettings)filterSettings.Clone();
            Persist();
        }

        internal static void Persist()
        {
            string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "filters.bin");
            Persistence.Save(mdatafilepath, filters);
        }

        internal static void Load()
        {
            string mdatafilepath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IStripperQuickPlayer", "filters.bin");
            if (File.Exists(mdatafilepath))
            {
                try
                {
                    filters = Persistence.Load<Dictionary<string, FilterSettings>>(mdatafilepath);
                }
                catch (Exception)
                {
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
