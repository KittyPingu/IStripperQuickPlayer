using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IStripperQuickPlayer.DataModel
{
    static internal class Datastore
    {
        static internal Int32 versionnumber = 0;
        static internal List<ModelCard>? modelcards = new List<ModelCard>{ };
        internal static int numberOfCards = 0;
        private static List<ModelCard>? indexedCards;
        private static int indexedCardCount = -1;
        private static readonly Dictionary<string, ModelCard> cardsByTag = [];

        internal static ModelCard? findCardByTag(string tag)
        {
            if (modelcards == null) return null;
            if (!ReferenceEquals(indexedCards, modelcards) ||
                indexedCardCount != modelcards.Count)
            {
                cardsByTag.Clear();
                foreach (ModelCard card in modelcards)
                {
                    int separator = card.name.IndexOf('-');
                    string cardTag = separator < 0
                        ? card.name : card.name[..separator];
                    cardsByTag.TryAdd(cardTag, card);
                }
                indexedCards = modelcards;
                indexedCardCount = modelcards.Count;
            }
            return cardsByTag.GetValueOrDefault(tag);
        }

        internal static ModelCard? findCardByText(string text)
        {
            string[] parts = text.Split("\r\n");
            if (modelcards == null) return null;
            foreach(ModelCard card in modelcards)
            {
                if (card.modelName == parts[0] && card.outfit == parts[1])
                    return card;
            }
            return null;
        }
    }
}
