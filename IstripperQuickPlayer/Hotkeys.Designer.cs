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
            cmdOK.Location = new Point(229, 268);
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
            // Hotkeys
            // 
            AutoScaleDimensions = new SizeF(10F, 25F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(578, 344);
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
    }
}