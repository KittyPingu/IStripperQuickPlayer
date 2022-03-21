using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IStripperQuickPlayer
{
    public partial class ImageView : Form
    {
        KaiwaProjects.KpImageViewer viewer;
        public ImageView()
        {
            InitializeComponent();
         
        }

        internal void LoadImage(Image? image)
        {
            if (image == null) return;
            viewer = new KaiwaProjects.KpImageViewer();
            viewer.Dock = DockStyle.Fill;            
            viewer.Image = new Bitmap(image);
            viewer.ShowPreview = false;
            viewer.OpenButton = false;
            viewer.FitToScreen();
            viewer.Refresh();
            this.Controls.Add(viewer);
        }
    }
}
