using MaterialSkin;
using MaterialSkin.Controls;
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
    public partial class ImageView : MaterialForm
    {
        KaiwaProjects.KpImageViewer viewer;
        public ImageView()
        {
            InitializeComponent();
            SetSkin();
         
        }
        private void SetSkin()
        {
            var materialSkinManager = MaterialSkinManager.Instance;
            materialSkinManager.RemoveFormToManage(this);
            materialSkinManager.AddFormToManage(this);
            materialSkinManager.Theme = MaterialSkinManager.Themes.LIGHT;
            if (Properties.Settings.Default.DarkMode) materialSkinManager.Theme = MaterialSkinManager.Themes.DARK;
            materialSkinManager.ColorScheme = new ColorScheme(Primary.BlueGrey800, Primary.BlueGrey900, Primary.BlueGrey500, Accent.LightBlue200, TextShade.WHITE);
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
