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

        internal static ModelCard findCardByTag(string tag)
        {
            foreach(ModelCard card in modelcards)
            {
                if (card.name == tag)
                    return card;
            }
            return null;
        }
    }
}
