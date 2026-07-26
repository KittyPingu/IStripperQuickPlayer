using Cyotek.Windows.Forms;
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
            AppTheme.Apply(this);
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
            AppTheme.Apply(viewer);
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
