using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace IStripperQuickPlayer.BLL
{
    class FindWindow
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        public static List<IntPtr> GetWindowHandles(string processName, string className)
        {
            List<IntPtr> handleList = new List<IntPtr>();
            Process[] processes = Process.GetProcessesByName(processName);
            Process proc = null;

            // Cycle through all top-level windows
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                // Get PID of current window
                GetWindowThreadProcessId(hWnd, out int processId);

                // Get process matching PID
                proc = processes.FirstOrDefault(p => p.Id == processId);

                if (proc != null)
                {
                    // Get class name of current window
                    StringBuilder classNameBuilder = new StringBuilder(256);
                    GetClassName(hWnd, classNameBuilder, 256);

                    // Check if class name matches what we're looking for
                    if (classNameBuilder.ToString() == className)
                    {
                        //Console.WriteLine($"{proc.ProcessName} process found with ID {proc.Id}, handle {hWnd.ToString("X")}");
                        handleList.Add(hWnd);
                    }
                }

                // return true so that we iterate through all windows
                return true;
            }, IntPtr.Zero);

            return handleList;
        }
    }
}