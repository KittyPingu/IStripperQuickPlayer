using System;
using System.Collections.Generic;
namespace IStripperQuickPlayer.DataModel
{
    [Serializable]
    internal class PlaybackHistoryEntry
    {
        internal string AnimationPath = "";
        internal DateTime PlayedUtc;
    }

    [Serializable]
    internal class MyData
    {
        internal Dictionary<string, decimal> CardRating = new Dictionary<string, decimal>();
        internal Dictionary<string, decimal> ClipRating = new Dictionary<string, decimal>();
        internal Dictionary<string, List<string>> CardTags = new Dictionary<string, List<string>>();
        internal Dictionary<string, List<string>> ClipTags = new Dictionary<string, List<string>>();
        internal Dictionary<string, bool> CardFavourite = new Dictionary<string, bool>();
        internal Dictionary<string, bool> ClipFavourite = new Dictionary<string, bool>();
        internal List<PlaybackHistoryEntry> PlaybackHistory = new();
        [field: NonSerialized]
        internal event Action? Changed;

        internal void AddCardRating(string tag, decimal rating)
        {
            if (CardRating.ContainsKey(tag))
                CardRating[tag] = rating;
            else
                CardRating.Add(tag, rating);
            Changed?.Invoke();
        }

        internal decimal GetCardRating(string tag)
        {
            return CardRating.TryGetValue(tag, out decimal rating) ? rating : 0;
        }

        internal void AddClipRating(string tag, decimal rating)
        {
            if (ClipRating.ContainsKey(tag))
                ClipRating[tag] = rating;
            else
                ClipRating.Add(tag, rating);
            Changed?.Invoke();
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
            Changed?.Invoke();
        }

        internal bool GetCardFavourite(string tag)
        {
            if (CardFavourite == null) return false;
            return CardFavourite.TryGetValue(tag, out bool favourite) && favourite;
        }

        internal void AddCardTags(string tag, List<string> tags)
        {
            if (CardTags.ContainsKey(tag))
            { 
                    CardTags[tag] = tags; 
            }
            else
                CardTags.Add(tag, tags);
            Changed?.Invoke();
        }

        internal List<string> GetCardTags(string tag)
        {
            if (CardTags.ContainsKey(tag))
                return CardTags[tag];
            else
                return new List<string>{ };
        }

        internal void AddPlayback(string animationPath, DateTime playedUtc)
        {
            if (string.IsNullOrWhiteSpace(animationPath))
                return;

            PlaybackHistory.Add(new PlaybackHistoryEntry
            {
                AnimationPath = animationPath.Replace('/', '\\'),
                PlayedUtc = playedUtc
            });
            if (PlaybackHistory.Count > 1000)
                PlaybackHistory.RemoveRange(0, PlaybackHistory.Count - 1000);
            Changed?.Invoke();
        }

        internal HashSet<string> RecentPlaybackPaths(int count)
        {
            return PlaybackHistory
                .TakeLast(count)
                .Select(entry => entry.AnimationPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        internal void Normalize()
        {
            CardRating ??= new();
            ClipRating ??= new();
            CardTags ??= new();
            ClipTags ??= new();
            CardFavourite ??= new();
            ClipFavourite ??= new();
            PlaybackHistory ??= new();
        }
    }

    [Serializable]
    internal class QuickPlayerBackup
    {
        internal int FormatVersion = 1;
        internal DateTime CreatedUtc = DateTime.UtcNow;
        internal MyData UserData = new();
        internal Dictionary<string, FilterSettings> Filters = new();
        internal Dictionary<string, string?> Settings = new();
    }
}
