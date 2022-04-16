using IStripperQuickPlayer.BLL;
using System.Globalization;

namespace IStripperQuickPlayer
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            //CultureInfo.CurrentCulture = new CultureInfo("en-GB", false);
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
    }
}