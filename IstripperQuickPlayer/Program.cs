using IStripperQuickPlayer.BLL;
using IStripperQuickPlayer.DataModel;
using System.Globalization;

namespace IStripperQuickPlayer
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {      // ***this line is added***
            if (args.Length == 2 && args[0] == "--verify-persistence")
            {
                Environment.ExitCode = Persistence.VerifyMigration(args[1]) ? 0 : 1;
                return;
            }
            if (args.Length == 1 && args[0] == "--verify-controls")
            {
                if (!Form1.TryParseHotKey("Control+Alt+N", out uint modifiers, out uint key) ||
                    modifiers != 0x4003 || key != (uint)Keys.N)
                {
                    Environment.ExitCode = 1;
                    return;
                }
                if (Form1.RestoreRequiresLayout(
                        new System.Drawing.Size(100, 100),
                        new System.Drawing.Size(100, 100),
                        96, 96, "DISPLAY1", "DISPLAY1") ||
                    !Form1.RestoreRequiresLayout(
                        new System.Drawing.Size(100, 100),
                        new System.Drawing.Size(101, 100),
                        96, 96, "DISPLAY1", "DISPLAY1"))
                {
                    Environment.ExitCode = 1;
                    return;
                }
                ApplicationConfiguration.Initialize();
                using Form1 mainWindow = new();
                using ImageView imageView = new();
                using System.Drawing.Bitmap image = new(1, 1);
                imageView.LoadImage(image);
                using Manina.Windows.Forms.ImageListView cardList = new();
                cardList.Size = new System.Drawing.Size(320, 240);
                using System.Drawing.Bitmap cardListImage =
                    new(cardList.Width, cardList.Height);
                cardList.DrawToBitmap(cardListImage,
                    cardList.ClientRectangle);
                cardList.DrawToBitmap(cardListImage,
                    cardList.ClientRectangle);
                return;
            }
            if (Environment.OSVersion.Version.Major >= 6)
                SetProcessDPIAware();

            Application.EnableVisualStyles();
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            //CultureInfo.CurrentCulture = new CultureInfo("en-GB", false);
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }

        // ***also dllimport of that function***
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }
}
