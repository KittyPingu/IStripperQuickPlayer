namespace IStripperQuickPlayer
{
    partial class Hotkeys
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            chkNextClip = new CheckBox();
            chkNextCard = new CheckBox();
            txtNextClip = new TextBox();
            txtNextCard = new TextBox();
            cmdOK = new Button();
            txtToggleLock = new TextBox();
            chkToggleLock = new CheckBox();
            txtPause = new TextBox();
            chkPause = new CheckBox();
            txtRewind = new TextBox();
            chkRewind = new CheckBox();
            txtFastForward = new TextBox();
            chkFastForward = new CheckBox();
            txtRestartClip = new TextBox();
            chkRestartClip = new CheckBox();
            SuspendLayout();
            // 
            // chkNextClip
            // 
            chkNextClip.AutoSize = true;
            chkNextClip.Location = new Point(70, 84);
            chkNextClip.Margin = new Padding(4, 4, 4, 4);
            chkNextClip.Name = "chkNextClip";
            chkNextClip.Size = new Size(109, 29);
            chkNextClip.TabIndex = 0;
            chkNextClip.Text = "Next Clip";
            chkNextClip.UseVisualStyleBackColor = true;
            // 
            // chkNextCard
            // 
            chkNextCard.AutoSize = true;
            chkNextCard.Location = new Point(70, 136);
            chkNextCard.Margin = new Padding(4, 4, 4, 4);
            chkNextCard.Name = "chkNextCard";
            chkNextCard.Size = new Size(116, 29);
            chkNextCard.TabIndex = 1;
            chkNextCard.Text = "Next Card";
            chkNextCard.UseVisualStyleBackColor = true;
            // 
            // txtNextClip
            // 
            txtNextClip.Location = new Point(229, 80);
            txtNextClip.Margin = new Padding(4, 4, 4, 4);
            txtNextClip.Name = "txtNextClip";
            txtNextClip.Size = new Size(263, 31);
            txtNextClip.TabIndex = 2;
            // 
            // txtNextCard
            // 
            txtNextCard.Location = new Point(229, 133);
            txtNextCard.Margin = new Padding(4, 4, 4, 4);
            txtNextCard.Name = "txtNextCard";
            txtNextCard.Size = new Size(263, 31);
            txtNextCard.TabIndex = 3;
            // 
            // cmdOK
            // 
            cmdOK.Location = new Point(229, 477);
            cmdOK.Margin = new Padding(4, 4, 4, 4);
            cmdOK.Name = "cmdOK";
            cmdOK.Size = new Size(109, 55);
            cmdOK.TabIndex = 4;
            cmdOK.Text = "OK";
            cmdOK.UseVisualStyleBackColor = true;
            cmdOK.Click += cmdOK_Click;
            // 
            // txtToggleLock
            // 
            txtToggleLock.Location = new Point(229, 186);
            txtToggleLock.Margin = new Padding(4, 4, 4, 4);
            txtToggleLock.Name = "txtToggleLock";
            txtToggleLock.Size = new Size(263, 31);
            txtToggleLock.TabIndex = 6;
            // 
            // chkToggleLock
            // 
            chkToggleLock.AutoSize = true;
            chkToggleLock.Location = new Point(70, 190);
            chkToggleLock.Margin = new Padding(4, 4, 4, 4);
            chkToggleLock.Name = "chkToggleLock";
            chkToggleLock.Size = new Size(132, 29);
            chkToggleLock.TabIndex = 5;
            chkToggleLock.Text = "Toggle Lock";
            chkToggleLock.UseVisualStyleBackColor = true;
            //
            // txtPause
            //
            txtPause.Location = new Point(229, 239);
            txtPause.Name = "txtPause";
            txtPause.Size = new Size(263, 31);
            txtPause.TabIndex = 8;
            //
            // chkPause
            //
            chkPause.AutoSize = true;
            chkPause.Location = new Point(70, 243);
            chkPause.Name = "chkPause";
            chkPause.Size = new Size(136, 29);
            chkPause.TabIndex = 7;
            chkPause.Text = "Pause / Play";
            chkPause.UseVisualStyleBackColor = true;
            //
            // txtRewind
            //
            txtRewind.Location = new Point(229, 292);
            txtRewind.Name = "txtRewind";
            txtRewind.Size = new Size(263, 31);
            txtRewind.TabIndex = 10;
            //
            // chkRewind
            //
            chkRewind.AutoSize = true;
            chkRewind.Location = new Point(70, 296);
            chkRewind.Name = "chkRewind";
            chkRewind.Size = new Size(91, 29);
            chkRewind.TabIndex = 9;
            chkRewind.Text = "-10%";
            chkRewind.UseVisualStyleBackColor = true;
            //
            // txtFastForward
            //
            txtFastForward.Location = new Point(229, 345);
            txtFastForward.Name = "txtFastForward";
            txtFastForward.Size = new Size(263, 31);
            txtFastForward.TabIndex = 12;
            //
            // chkFastForward
            //
            chkFastForward.AutoSize = true;
            chkFastForward.Location = new Point(70, 349);
            chkFastForward.Name = "chkFastForward";
            chkFastForward.Size = new Size(97, 29);
            chkFastForward.TabIndex = 11;
            chkFastForward.Text = "+10%";
            chkFastForward.UseVisualStyleBackColor = true;
            //
            // txtRestartClip
            //
            txtRestartClip.Location = new Point(229, 398);
            txtRestartClip.Name = "txtRestartClip";
            txtRestartClip.Size = new Size(263, 31);
            txtRestartClip.TabIndex = 14;
            //
            // chkRestartClip
            //
            chkRestartClip.AutoSize = true;
            chkRestartClip.Location = new Point(70, 402);
            chkRestartClip.Name = "chkRestartClip";
            chkRestartClip.Size = new Size(128, 29);
            chkRestartClip.TabIndex = 13;
            chkRestartClip.Text = "Restart Clip";
            chkRestartClip.UseVisualStyleBackColor = true;
            // 
            // Hotkeys
            // 
            AutoScaleDimensions = new SizeF(10F, 25F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(578, 553);
            Controls.Add(txtRestartClip);
            Controls.Add(chkRestartClip);
            Controls.Add(txtFastForward);
            Controls.Add(chkFastForward);
            Controls.Add(txtRewind);
            Controls.Add(chkRewind);
            Controls.Add(txtPause);
            Controls.Add(chkPause);
            Controls.Add(txtToggleLock);
            Controls.Add(chkToggleLock);
            Controls.Add(cmdOK);
            Controls.Add(txtNextCard);
            Controls.Add(txtNextClip);
            Controls.Add(chkNextCard);
            Controls.Add(chkNextClip);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Margin = new Padding(4, 4, 4, 4);
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "Hotkeys";
            Padding = new Padding(4, 80, 4, 4);
            StartPosition = FormStartPosition.CenterParent;
            Text = "Hotkeys";
            Load += Hotkeys_Load;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private CheckBox chkNextClip;
        private CheckBox chkNextCard;
        private TextBox txtNextClip;
        private TextBox txtNextCard;
        private Button cmdOK;
        private TextBox txtToggleLock;
        private CheckBox chkToggleLock;
        private TextBox txtPause;
        private CheckBox chkPause;
        private TextBox txtRewind;
        private CheckBox chkRewind;
        private TextBox txtFastForward;
        private CheckBox chkFastForward;
        private TextBox txtRestartClip;
        private CheckBox chkRestartClip;
    }
}
