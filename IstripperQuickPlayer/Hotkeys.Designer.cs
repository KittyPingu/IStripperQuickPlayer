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
            this.chkNextClip = new System.Windows.Forms.CheckBox();
            this.chkNextCard = new System.Windows.Forms.CheckBox();
            this.txtNextClip = new System.Windows.Forms.TextBox();
            this.txtNextCard = new System.Windows.Forms.TextBox();
            this.cmdOK = new System.Windows.Forms.Button();
            this.txtToggleLock = new System.Windows.Forms.TextBox();
            this.chkToggleLock = new System.Windows.Forms.CheckBox();
            this.SuspendLayout();
            // 
            // chkNextClip
            // 
            this.chkNextClip.AutoSize = true;
            this.chkNextClip.Location = new System.Drawing.Point(41, 24);
            this.chkNextClip.Name = "chkNextClip";
            this.chkNextClip.Size = new System.Drawing.Size(92, 24);
            this.chkNextClip.TabIndex = 0;
            this.chkNextClip.Text = "Next Clip";
            this.chkNextClip.UseVisualStyleBackColor = true;
            // 
            // chkNextCard
            // 
            this.chkNextCard.AutoSize = true;
            this.chkNextCard.Location = new System.Drawing.Point(41, 66);
            this.chkNextCard.Name = "chkNextCard";
            this.chkNextCard.Size = new System.Drawing.Size(97, 24);
            this.chkNextCard.TabIndex = 1;
            this.chkNextCard.Text = "Next Card";
            this.chkNextCard.UseVisualStyleBackColor = true;
            // 
            // txtNextClip
            // 
            this.txtNextClip.Location = new System.Drawing.Point(168, 21);
            this.txtNextClip.Name = "txtNextClip";
            this.txtNextClip.Size = new System.Drawing.Size(211, 27);
            this.txtNextClip.TabIndex = 2;
            // 
            // txtNextCard
            // 
            this.txtNextCard.Location = new System.Drawing.Point(168, 63);
            this.txtNextCard.Name = "txtNextCard";
            this.txtNextCard.Size = new System.Drawing.Size(211, 27);
            this.txtNextCard.TabIndex = 3;
            // 
            // cmdOK
            // 
            this.cmdOK.Location = new System.Drawing.Point(168, 171);
            this.cmdOK.Name = "cmdOK";
            this.cmdOK.Size = new System.Drawing.Size(87, 44);
            this.cmdOK.TabIndex = 4;
            this.cmdOK.Text = "OK";
            this.cmdOK.UseVisualStyleBackColor = true;
            this.cmdOK.Click += new System.EventHandler(this.cmdOK_Click);
            // 
            // txtToggleLock
            // 
            this.txtToggleLock.Location = new System.Drawing.Point(168, 106);
            this.txtToggleLock.Name = "txtToggleLock";
            this.txtToggleLock.Size = new System.Drawing.Size(211, 27);
            this.txtToggleLock.TabIndex = 6;
            // 
            // chkToggleLock
            // 
            this.chkToggleLock.AutoSize = true;
            this.chkToggleLock.Location = new System.Drawing.Point(41, 109);
            this.chkToggleLock.Name = "chkToggleLock";
            this.chkToggleLock.Size = new System.Drawing.Size(111, 24);
            this.chkToggleLock.TabIndex = 5;
            this.chkToggleLock.Text = "Toggle Lock";
            this.chkToggleLock.UseVisualStyleBackColor = true;
            // 
            // Hotkeys
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(449, 242);
            this.Controls.Add(this.txtToggleLock);
            this.Controls.Add(this.chkToggleLock);
            this.Controls.Add(this.cmdOK);
            this.Controls.Add(this.txtNextCard);
            this.Controls.Add(this.txtNextClip);
            this.Controls.Add(this.chkNextCard);
            this.Controls.Add(this.chkNextClip);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "Hotkeys";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Hotkeys";
            this.Load += new System.EventHandler(this.Hotkeys_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

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