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
    public partial class Hotkeys : MaterialForm
    {
        public Hotkeys()
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

        private void Hotkeys_Load(object sender, EventArgs e)
        {
            chkNextClip.Checked = Properties.Settings.Default.NextClipEnabled;
            chkNextCard.Checked = Properties.Settings.Default.NextCardEnabled;
            chkToggleLock.Checked = Properties.Settings.Default.ToggleLockEnabled;
            chkPause.Checked = Properties.Settings.Default.PauseHotkeyEnabled;
            chkRewind.Checked = Properties.Settings.Default.RewindHotkeyEnabled;
            chkFastForward.Checked = Properties.Settings.Default.FastForwardHotkeyEnabled;
            chkRestartClip.Checked = Properties.Settings.Default.RestartClipHotkeyEnabled;
            chkLargePlayer.Checked = Properties.Settings.Default.LargePlayerHotkeyEnabled;
            chkSmallPlayer.Checked = Properties.Settings.Default.SmallPlayerHotkeyEnabled;
            chkNowPlayingInfo.Checked = Properties.Settings.Default.NowPlayingInfoHotkeyEnabled;
            txtNextClip.Text = Properties.Settings.Default.NextClipString;
            txtNextCard.Text = Properties.Settings.Default.NextCardString;
            txtToggleLock.Text = Properties.Settings.Default.ToggleLockString;
            txtPause.Text = Properties.Settings.Default.PauseHotkeyString;
            txtRewind.Text = Properties.Settings.Default.RewindHotkeyString;
            txtFastForward.Text = Properties.Settings.Default.FastForwardHotkeyString;
            txtRestartClip.Text = Properties.Settings.Default.RestartClipHotkeyString;
            txtLargePlayer.Text = Properties.Settings.Default.LargePlayerHotkeyString;
            txtSmallPlayer.Text = Properties.Settings.Default.SmallPlayerHotkeyString;
            txtNowPlayingInfo.Text = Properties.Settings.Default.NowPlayingInfoHotkeyString;
        }

        private void cmdOK_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.NextClipEnabled = chkNextClip.Checked;
            Properties.Settings.Default.NextCardEnabled = chkNextCard.Checked;
            Properties.Settings.Default.ToggleLockEnabled = chkToggleLock.Checked;
            Properties.Settings.Default.PauseHotkeyEnabled = chkPause.Checked;
            Properties.Settings.Default.RewindHotkeyEnabled = chkRewind.Checked;
            Properties.Settings.Default.FastForwardHotkeyEnabled = chkFastForward.Checked;
            Properties.Settings.Default.RestartClipHotkeyEnabled = chkRestartClip.Checked;
            Properties.Settings.Default.LargePlayerHotkeyEnabled = chkLargePlayer.Checked;
            Properties.Settings.Default.SmallPlayerHotkeyEnabled = chkSmallPlayer.Checked;
            Properties.Settings.Default.NowPlayingInfoHotkeyEnabled = chkNowPlayingInfo.Checked;
            Properties.Settings.Default.NextClipString = txtNextClip.Text;
            Properties.Settings.Default.NextCardString = txtNextCard.Text;
            Properties.Settings.Default.ToggleLockString = txtToggleLock.Text;
            Properties.Settings.Default.PauseHotkeyString = txtPause.Text;
            Properties.Settings.Default.RewindHotkeyString = txtRewind.Text;
            Properties.Settings.Default.FastForwardHotkeyString = txtFastForward.Text;
            Properties.Settings.Default.RestartClipHotkeyString = txtRestartClip.Text;
            Properties.Settings.Default.LargePlayerHotkeyString = txtLargePlayer.Text;
            Properties.Settings.Default.SmallPlayerHotkeyString = txtSmallPlayer.Text;
            Properties.Settings.Default.NowPlayingInfoHotkeyString = txtNowPlayingInfo.Text;
            this.Close();
        }
    }
}
