using Cyotek.Windows.Forms;
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
        Cyotek.Windows.Forms.ImageBox? viewer;
        public ImageView()
        {
            InitializeComponent();
            SetSkin();
            _ = TooltipManager.Attach(this, components,
                Properties.Settings.Default.TooltipInitialDelay);
        }
        private void SetSkin()
        {
            var materialSkinManager = MaterialSkinManager.Instance;
            materialSkinManager.RemoveFormToManage(this);
            materialSkinManager.AddFormToManage(this);
            materialSkinManager.Theme = MaterialSkinManager.Themes.LIGHT;
            if (Properties.Settings.Default.DarkMode) materialSkinManager.Theme = MaterialSkinManager.Themes.DARK;
            materialSkinManager.ColorScheme = new ColorScheme(Primary.BlueGrey800, Primary.BlueGrey900, Primary.BlueGrey500, Accent.LightBlue200, TextShade.WHITE);
            contextMenuStrip1.ShowImageMargin = false;
            contextMenuStrip1.BackColor = Properties.Settings.Default.DarkMode
                ? Color.FromArgb(48, 48, 48)
                : SystemColors.Menu;
            contextMenuStrip1.ForeColor =
                Properties.Settings.Default.DarkMode
                    ? Color.White : SystemColors.MenuText;
            foreach (ToolStripItem item in contextMenuStrip1.Items)
                item.ForeColor = contextMenuStrip1.ForeColor;
        }
        internal void LoadImage(Image? image)
        {
            if (image == null) return;
            viewer = new Cyotek.Windows.Forms.ImageBox();
            viewer.Dock = DockStyle.Fill;
            viewer.AccessibleDescription =
                "View the card image; right-click to copy or save it.";
            viewer.Image = new Bitmap(image);
            viewer.ContextMenuStrip = contextMenuStrip1;
            viewer.Refresh();
            this.Controls.Add(viewer);
        }

        // To Copy to Clipboard
        private void copyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (viewer?.Image != null)
            {
                Clipboard.SetImage(viewer.Image);
            }
        }

        // To Save to File
        private void saveAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (viewer?.Image != null)
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp";
                    if (sfd.ShowDialog() == DialogResult.OK)
                    {
                        viewer.Image.Save(sfd.FileName);
                    }
                }
            }
        }

        private void contextMenuStrip1_Opening(object sender, CancelEventArgs e)
        {

        }
    }
}
