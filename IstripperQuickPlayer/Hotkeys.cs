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
            chkToggleLock.Checked = Properties.Settings.Default.ToggleLockEnabled;
            txtNextClip.Text = Properties.Settings.Default.NextClipString;
            txtNextCard.Text = Properties.Settings.Default.NextCardString;
            txtToggleLock.Text = Properties.Settings.Default.ToggleLockString;
        }

        private void cmdOK_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.NextClipEnabled =   chkNextClip.Checked; 
            Properties.Settings.Default.NextCardEnabled = chkNextCard.Checked; 
            Properties.Settings.Default.ToggleLockEnabled = chkToggleLock.Checked;
            Properties.Settings.Default.NextClipString =   txtNextClip.Text; 
            Properties.Settings.Default.NextCardString =  txtNextCard.Text;
            Properties.Settings.Default.ToggleLockString = txtToggleLock.Text;
            this.Close();
        }
    }
}
