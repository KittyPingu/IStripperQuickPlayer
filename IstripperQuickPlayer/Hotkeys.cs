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
            SetSkin();
            LayoutHotkeyRows();
            _ = TooltipManager.Attach(this,
                Properties.Settings.Default.TooltipInitialDelay);
        }

        private void LayoutHotkeyRows()
        {
            (CheckBox Label, TextBox Shortcut)[] rows =
            [
                (chkNextClip, txtNextClip), (chkNextCard, txtNextCard),
                (chkToggleLock, txtToggleLock), (chkPause, txtPause),
                (chkRewind, txtRewind), (chkFastForward, txtFastForward),
                (chkRestartClip, txtRestartClip), (chkLargePlayer, txtLargePlayer),
                (chkSmallPlayer, txtSmallPlayer), (chkNowPlayingInfo, txtNowPlayingInfo),
                (chkPanic, txtPanic), (chkToggleHdr, txtToggleHdr),
                (chkToggleVsr, txtToggleVsr),
                (chkToggleAlphaAntialiasing, txtToggleAlphaAntialiasing)
            ];
            SuspendLayout();
            Controls.Clear();
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(LogicalToDeviceUnits(20));
            TableLayoutPanel table = new()
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, RowCount = rows.Length + 1,
                Dock = DockStyle.Fill, Margin = Padding.Empty
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int row = 0; row < rows.Length; row++)
            {
                var (label, shortcut) = rows[row];
                label.Anchor = AnchorStyles.Left;
                label.Margin = new Padding(0, LogicalToDeviceUnits(6), LogicalToDeviceUnits(18), LogicalToDeviceUnits(6));
                shortcut.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                shortcut.Width = LogicalToDeviceUnits(240);
                shortcut.Margin = new Padding(0, LogicalToDeviceUnits(6), 0, LogicalToDeviceUnits(6));
                table.Controls.Add(label, 0, row);
                table.Controls.Add(shortcut, 1, row);
            }
            cmdOK.Anchor = AnchorStyles.Right;
            cmdOK.Margin = new Padding(0, LogicalToDeviceUnits(12), 0, 0);
            table.Controls.Add(cmdOK, 1, rows.Length);
            AcceptButton = cmdOK;
            Controls.Add(table);
            ResumeLayout(true);
        }

        internal static bool VerifyLayout()
        {
            foreach (float size in new[] { 9f, 12f, 16f })
            {
                using Hotkeys dialog = new();
                using Font font = new(dialog.Font.FontFamily, size);
                dialog.Font = font;
                dialog.CreateControl();
                dialog.ClientSize = dialog.GetPreferredSize(Size.Empty);
                dialog.PerformLayout();
                TableLayoutPanel table = dialog.Controls.OfType<TableLayoutPanel>().Single();
                table.PerformLayout();
                for (int row = 0; row < 14; row++)
                {
                    Control label = table.GetControlFromPosition(0, row)!;
                    Control shortcut = table.GetControlFromPosition(1, row)!;
                    if (label.Right + label.Margin.Right > shortcut.Left ||
                        shortcut.Width < 100 || shortcut.Right > table.ClientSize.Width)
                        return false;
                }
            }
            return true;
        }

        private void SetSkin()
        {
            AppTheme.Apply(this);
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
            chkPanic.Checked = Properties.Settings.Default.PanicHotkeyEnabled;
            chkToggleHdr.Checked =
                Properties.Settings.Default.ToggleHdrHotkeyEnabled;
            chkToggleVsr.Checked =
                Properties.Settings.Default.ToggleVsrHotkeyEnabled;
            chkToggleAlphaAntialiasing.Checked = Properties.Settings.Default.
                ToggleAlphaAntialiasingHotkeyEnabled;
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
            txtPanic.Text = Properties.Settings.Default.PanicHotkeyString;
            txtToggleHdr.Text = Properties.Settings.Default.ToggleHdrHotkeyString;
            txtToggleVsr.Text = Properties.Settings.Default.ToggleVsrHotkeyString;
            txtToggleAlphaAntialiasing.Text = Properties.Settings.Default.
                ToggleAlphaAntialiasingHotkeyString;
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
            Properties.Settings.Default.PanicHotkeyEnabled = chkPanic.Checked;
            Properties.Settings.Default.ToggleHdrHotkeyEnabled =
                chkToggleHdr.Checked;
            Properties.Settings.Default.ToggleVsrHotkeyEnabled =
                chkToggleVsr.Checked;
            Properties.Settings.Default.ToggleAlphaAntialiasingHotkeyEnabled =
                chkToggleAlphaAntialiasing.Checked;
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
            Properties.Settings.Default.PanicHotkeyString = txtPanic.Text;
            Properties.Settings.Default.ToggleHdrHotkeyString = txtToggleHdr.Text;
            Properties.Settings.Default.ToggleVsrHotkeyString = txtToggleVsr.Text;
            Properties.Settings.Default.ToggleAlphaAntialiasingHotkeyString =
                txtToggleAlphaAntialiasing.Text;
            this.Close();
        }
    }
}
