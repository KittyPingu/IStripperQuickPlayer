using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using static IStripperQuickPlayer.DataModel.Enums;

namespace IStripperQuickPlayer
{
    [Serializable]
    internal class ModelCard
    {
        internal List<ModelClip>? clips = new List<ModelClip>{ };
        internal string name;
        internal DateTime? datePurchased;
        internal CollectionType collection;
        internal CardResolutionType resolution;
        internal CardResolutionType bestResolution;
        internal string xmlstring;
        [NonSerialized]
        internal XmlDocument? XML;

        internal byte? a;
        internal byte? b;
        internal bool? flag10;
        internal bool? flag11;
        internal bool? cardHidden;
        internal bool? flag13;
        internal bool? flag14;
        internal bool? inCollection;
        internal bool? cardDownloaded;
        internal bool? updateAvailable;
        internal bool? flag20;
        internal bool? flag21;
        internal bool? isNew;
        internal bool? specialSelection;
        internal bool? flag25;
        internal bool? cardDownloaded2;
        internal bool? flag26;
        internal bool? cardEnabled;
        internal byte? c;
        internal byte? d;
        internal byte? e;
        internal byte? f;
        internal byte? g;
        internal byte? h;
        internal byte? i;
        internal byte? j;
        internal byte? k;
        internal byte? l;
        internal byte? m;
        internal byte? o;
        internal int? timesPlayed;
        internal byte? p;
        internal byte? r;
        internal byte? q;
        internal byte? n;
        internal byte? s;
        internal int? folderSize;
        internal string description;
        internal string outfit;
        internal string? hair;
        internal decimal? rating;
        internal string? hotnessLevel;
        internal int? frameCount;
        internal string? xmlSize;
        internal Image? image;
        internal string? modelName;
        internal decimal modelAge;
        internal string? imagefile;
        internal string[] tags;
        internal decimal? bust;
        internal decimal? waist;
        internal decimal? hips;
        internal string? ethnicity;
        internal bool? exclusive;
        internal string? height;
        internal string? city;
        internal string? country;
        internal DateTime? birthdate;
        internal int? numgirls;
        internal DateTime dateShow;
        internal DateTime dateReleased;

        public ModelCard()
        {
            name = "";
            tags = new string[] { };
            description = "";
            outfit = "";
            xmlstring = "";
            modelAge = 0;
        }
    }
}
