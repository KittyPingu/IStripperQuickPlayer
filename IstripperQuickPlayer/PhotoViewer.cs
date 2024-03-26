using IStripperQuickPlayer.DataModel;
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
    public partial class PhotoViewer : MaterialForm
    {
        internal CardPhotos photos;
        Bitmap[] thumbs;

        private void SetSkin()
        {
            var materialSkinManager = MaterialSkinManager.Instance;
            materialSkinManager.RemoveFormToManage(this);
            materialSkinManager.AddFormToManage(this);
            materialSkinManager.Theme = MaterialSkinManager.Themes.LIGHT;
            if (Properties.Settings.Default.DarkMode) materialSkinManager.Theme = MaterialSkinManager.Themes.DARK;
            materialSkinManager.ColorScheme = new ColorScheme(Primary.BlueGrey800, Primary.BlueGrey900, Primary.BlueGrey500, Accent.LightBlue200, TextShade.WHITE);
        }


        public PhotoViewer()
        {
            InitializeComponent();
            SetSkin();
        }

        internal async void Populate()
        {
            listView1.BeginUpdate();
            imageList1.Images.Add(new Bitmap(256,256,System.Drawing.Imaging.PixelFormat.Format24bppRgb));

            thumbs = await photos.getThumbnails();
            for (int i = 0; i < thumbs.Length; i++)
            {
                listView1.Items.Add(new ListViewItem(i.ToString(), i));
            }
            //listView1.Items.Add(new ListViewItem(i.ToString(),i));
            
            listView1.EndUpdate();
        }

        private void listView1_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            e.Graphics.DrawRectangle(new Pen(Color.Black,2), e.Bounds);
            if (e.Item.Selected)
            {                   
                //e.DrawFocusRectangle();
                e.Graphics.FillRectangle(Brushes.PaleGreen, e.Bounds);
            }
            int maxdim = Math.Max(thumbs[e.ItemIndex].Width, thumbs[e.ItemIndex].Height);
            double scale = 1.0f;
            
            scale = 256.0/maxdim;
            Rectangle imgrect = new Rectangle(0,0,1,1);
            if (thumbs[e.ItemIndex].Width > thumbs[e.ItemIndex].Height)
            {
                imgrect = new Rectangle(e.Bounds.Left + (int)((e.Bounds.Width-256)/2.0),
                e.Bounds.Top + (int)(256 - (thumbs[e.ItemIndex].Height * scale))/2 + (int)((e.Bounds.Height-256)/2.0),
                (int)(thumbs[e.ItemIndex].Width*scale),
                (int)(thumbs[e.ItemIndex].Height*scale));
            }
            else
            {
                imgrect = new Rectangle(e.Bounds.Left + (int)(256 - (thumbs[e.ItemIndex].Width * scale))/2 + (int)((e.Bounds.Width-256)/2.0),
                e.Bounds.Top + (int)((e.Bounds.Height-256)/2.0),
                (int)(thumbs[e.ItemIndex].Width*scale),
                (int)(thumbs[e.ItemIndex].Height*scale));
            }
            
            e.Graphics.DrawImage(thumbs[e.ItemIndex], imgrect);
        }

        private void listView1_DoubleClick(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0) return;
            ImageView img = new ImageView();
            img.LoadImage(photos.getPhoto(listView1.SelectedItems[0].Index));
            img.Show();
        }
    }
}
