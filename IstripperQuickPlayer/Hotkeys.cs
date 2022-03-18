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
    public partial class Hotkeys : Form
    {
        public Hotkeys()
        {
            InitializeComponent();
        }

        private void Hotkeys_Load(object sender, EventArgs e)
        {
            chkNextClip.Checked = Properties.Settings.Default.NextClipEnabled;
            chkNextCard.Checked = Properties.Settings.Default.NextCardEnabled;
            txtNextClip.Text = Properties.Settings.Default.NextClipString;
            txtNextCard.Text = Properties.Settings.Default.NextCardString;
        }

        private void cmdOK_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.NextClipEnabled =   chkNextClip.Checked; 
            Properties.Settings.Default.NextCardEnabled = chkNextCard.Checked; 
            Properties.Settings.Default.NextClipString =   txtNextClip.Text; 
            Properties.Settings.Default.NextCardString =  txtNextCard.Text;
            this.Close();
        }
    }
}
