using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IStripperQuickPlayer
{
    [Serializable]
    internal class Model
    {
        internal string ModelName = "";
        internal Int16 ModelID = -1;
        internal List<ModelCard>? Cards;
    }
}
