using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IStripperQuickPlayer.BLL
{
    public static class ListViewExtensions
    {
        /// <summary>
        /// Sets the double buffered property of a list view to the specified value
        /// </summary>
        /// <param name="listView">The List view</param>
        /// <param name="doubleBuffered">Double Buffered or not</param>
        public static void SetDoubleBuffered(this System.Windows.Forms.ListView listView, bool doubleBuffered = true)
        {
            var prop = listView
                .GetType()
                .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (prop != null)
                prop.SetValue(listView, doubleBuffered, null);
        }
    }
}
