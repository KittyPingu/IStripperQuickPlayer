using ComponentOwl.BetterListView;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QPControls
{
    public class ListView : BetterListView
    {

        protected override void OnDrawItemBackground(BetterListViewDrawItemBackgroundEventArgs eventArgs)
        {
            base.OnDrawItemBackground(eventArgs);

            if (eventArgs.Item.Selected)
            {
            Brush brushSelection = new SolidBrush(Color.FromArgb(128, Color.LightGreen));
            eventArgs.Graphics.FillRectangle(brushSelection, eventArgs.ItemBounds.BoundsSelection);
            brushSelection.Dispose();
            }
        }

        protected override void OnDrawItem(BetterListViewDrawItemEventArgs eventArgs)
        {
            eventArgs.DrawSelection = false;

            //base.OnDrawItem(eventArgs);

            if (eventArgs.Item.Selected)
            {
                eventArgs.Graphics.DrawRectangle(Pens.DarkGreen, eventArgs.ItemBounds.BoundsSelection);
            }
        }
    }
    
}
