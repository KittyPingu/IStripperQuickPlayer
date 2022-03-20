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
    public class FilterSettings
    {
        internal decimal minAge=18;
        internal decimal maxAge=43;
        internal decimal minBust=25;
        internal decimal maxBust=50;
        internal decimal minRating=2;
        internal string tags="";
        internal decimal maxRating=5;
        internal bool IStripper=true;
        internal bool IStripperClassic=true;
        internal bool IStripperXXX=true;
        internal bool VGClassic=true;
        internal bool DeskBabes =true;
        internal bool Special=true;
        internal bool Normal=true;
        internal decimal minMyRating=0;
        internal decimal maxMyRating=10;

       
    }
}
