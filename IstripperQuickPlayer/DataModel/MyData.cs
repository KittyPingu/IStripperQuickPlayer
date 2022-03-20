using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IStripperQuickPlayer.DataModel
{
    [Serializable]
    internal class MyData
    {
        internal Dictionary<string, decimal> CardRating = new Dictionary<string, decimal>();
        internal Dictionary<string, decimal> ClipRating = new Dictionary<string, decimal>();
        internal Dictionary<string, List<string>> CardTags = new Dictionary<string, List<string>>();
        internal Dictionary<string, List<string>> ClipTags = new Dictionary<string, List<string>>();
        internal Dictionary<string, bool> CardFavourite = new Dictionary<string, bool>();
        internal Dictionary<string, bool> ClipFavourite = new Dictionary<string, bool>();

        internal void AddCardRating(string tag, decimal rating)
        {
            if (CardRating.ContainsKey(tag))
                CardRating[tag] = rating;
            else
                CardRating.Add(tag, rating);
        }

        internal decimal GetCardRating(string tag)
        {
            if (CardRating.ContainsKey(tag))
                return CardRating[tag];
            else
                return 0;
        }

        internal void AddClipRating(string tag, decimal rating)
        {
            if (ClipRating.ContainsKey(tag))
                ClipRating[tag] = rating;
            else
                ClipRating.Add(tag, rating);
        }

        internal decimal GetClipRating(string tag)
        {
            if (ClipRating.ContainsKey(tag))
                return ClipRating[tag];
            else
                return 0;
        }

        internal void AddCardFavourite(string tag, bool favourite)
        {
            if (CardFavourite.ContainsKey(tag))
                CardFavourite[tag] = favourite;
            else
                CardFavourite.Add(tag, favourite);
        }

        internal bool GetCardFavourite(string tag)
        {
            if (CardFavourite == null) return false;
            if (CardFavourite.ContainsKey(tag))
                return CardFavourite[tag];
            else
                return false;
        }

        internal void AddCardTags(string tag, List<string> tags)
        {
            if (CardTags.ContainsKey(tag))
            { 
                    CardTags[tag] = tags; 
            }
            else
                CardTags.Add(tag, tags);
        }

        internal List<string> GetCardTags(string tag)
        {
            if (CardTags.ContainsKey(tag))
                return CardTags[tag];
            else
                return new List<string>{ };
        }
    }
}
