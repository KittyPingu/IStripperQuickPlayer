using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace IStripperQuickPlayer.BLL
{
    internal static class Utils
    {
        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowInfo(IntPtr hwnd, ref WINDOWINFO pwi);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            private int _Left;
            private int _Top;
            private int _Right;
            private int _Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWINFO
        {
            public uint cbSize;
            public RECT rcWindow;
            public RECT rcClient;
            public uint dwStyle;
            public uint dwExStyle;
            public uint dwWindowStatus;
            public uint cxWindowBorders;
            public uint cyWindowBorders;
            public ushort atomWindowType;
            public ushort wCreatorVersion;

            public WINDOWINFO(Boolean? filler)
                : this()   // Allows automatic initialization of "cbSize" with "new WINDOWINFO(null/true/false)".
            {
                cbSize = (UInt32)(Marshal.SizeOf(typeof(WINDOWINFO)));
            }

        }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetWindow(IntPtr hWnd, GetWindow_Cmd uCmd);
        enum GetWindow_Cmd : uint
        {
            GW_HWNDFIRST = 0,
            GW_HWNDLAST = 1,
            GW_HWNDNEXT = 2,
            GW_HWNDPREV = 3,
            GW_OWNER = 4,
            GW_CHILD = 5,
            GW_ENABLEDPOPUP = 6
        }
        [DllImport("user32.dll", CharSet = CharSet.Auto)] internal static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int WM_COMMAND = 0x111;

        internal static void ToggleDesktopIcons()
        {
              IntPtr toggleDesktopCommand = new IntPtr(0x7402); 
              SendMessage(GetDesktopSHELLDLL_DefView(), WM_COMMAND, toggleDesktopCommand, IntPtr.Zero);             
        }   

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);
        [DllImport("user32.dll", SetLastError = false)]
        static extern IntPtr GetDesktopWindow();

        static IntPtr GetDesktopSHELLDLL_DefView()
        {
            var hShellViewWin = IntPtr.Zero;
            var hWorkerW = IntPtr.Zero;

            var hProgman = FindWindow("Progman", null);
            var hDesktopWnd = GetDesktopWindow();

            // If the main Program Manager window is found
            if (hProgman != IntPtr.Zero)
            {
                // Get and load the main List view window containing the icons.
                hShellViewWin = FindWindowEx(hProgman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (hShellViewWin == IntPtr.Zero)
                {
                    // When this fails (picture rotation is turned ON, toggledesktop shell cmd used ), then look for the WorkerW windows list to get the
                    // correct desktop list handle.
                    // As there can be multiple WorkerW windows, iterate through all to get the correct one
                    do
                    {
                        hWorkerW = FindWindowEx(hDesktopWnd, hWorkerW, "WorkerW", null);
                        hShellViewWin = FindWindowEx(hWorkerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                    } while (hShellViewWin == IntPtr.Zero && hWorkerW != IntPtr.Zero);
                }
            }
            return hShellViewWin;
        }
         
        public const uint WS_VISIBLE  = 0x10000000;
        internal static bool DefaultIconsVisible;

        internal static bool DesktopIconsVisible()
        {
            var hWnd = GetWindow(GetDesktopSHELLDLL_DefView(), GetWindow_Cmd.GW_CHILD);
    
            var info = new WINDOWINFO(null);
            //info.cbSize = (uint)Marshal.SizeOf(info);
            GetWindowInfo(hWnd, ref info);
            return (info.dwStyle & WS_VISIBLE) == WS_VISIBLE;
        }

        internal static void SizeLabelFont(Label lbl, int DefaultSize, bool IncludeHeight = false)
        {
            // Only bother if there's text.
            string txt = lbl.Text;
            if (txt.Length > 0)
            {
                int best_size = 100;

                // See how much room we have, allowing a bit
                // for the Label's internal margin.
                int wid = lbl.MaximumSize.Width - 3;
                int hgt = lbl.MaximumSize.Height - 3;

                // Make a Graphics object to measure the text.
                using (Graphics gr = lbl.CreateGraphics())
                {
                    for (int i = 1; i <= 100; i++)
                    {
                        using (Font test_font =
                            new Font(lbl.Font.FontFamily, i))
                        {
                            // See how much space the text would
                            // need, specifying a maximum width.
                            SizeF text_size =
                                gr.MeasureString(txt, test_font);
                            if ((text_size.Width > wid) || (IncludeHeight && text_size.Height > hgt) || i == DefaultSize+1)
                            {
                                best_size = i - 1;
                                break;
                            }
                        }
                    }
                }

                // Use that font size.
                lbl.Font = new Font(lbl.Font.FontFamily, best_size);
            }
        }

        internal static Form1? GetMainForm()
        {
            if (Application.OpenForms == null) return null;
            var frms = Application.OpenForms;
            foreach (var form in frms)
                if (form is Form1) return (Form1)form;
            return null;

        }

        public static string GetAttribute(this XmlNode? e, string AttributeName)
        {
            if (e == null) return "";
            if (e.Attributes == null) return "";
            if (string.IsNullOrEmpty(AttributeName)) return "";
            var attribute = e.Attributes[AttributeName];
            string r = attribute != null ? attribute.Value : "";
            return r;
        }

        public static bool ContainsWithNot(this string s, string find)
        {
            //s = s.Trim();
            if (find.StartsWith("!"))
            {
                return !s.Contains(find.Replace("!","").Trim(), StringComparison.CurrentCultureIgnoreCase);
            }
            else
                return s.Contains(find, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    internal readonly record struct TextSearchDocument(
        string All,
        string Model,
        string Card,
        string Title,
        string Description,
        string Tags)
    {
        internal bool Contains(string? field, string value)
        {
            string text = field switch
            {
                "model" or "performer" => Model,
                "card" or "name" or "id" => Card,
                "title" or "show" or "outfit" => Title,
                "description" or "desc" => Description,
                "tag" or "tags" => Tags,
                _ => All
            };
            return text.Contains(value, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class TextQuery
    {
        private readonly Func<TextSearchDocument, bool> matches;

        private TextQuery(Func<TextSearchDocument, bool> matches) =>
            this.matches = matches;

        internal static TextQuery Parse(string text) =>
            new(ParseExpression(text.Trim()));

        internal bool Matches(in TextSearchDocument document) =>
            matches(document);

        private static Func<TextSearchDocument, bool> ParseExpression(
            string expression)
        {
            expression = TrimParentheses(expression.Trim());
            if (expression.Length == 0)
            {
                return _ => true;
            }

            List<string> parts = SplitTopLevel(expression, "or");
            if (parts.Count > 1)
            {
                Func<TextSearchDocument, bool>[] alternatives =
                    parts.Select(ParseExpression).ToArray();
                return document =>
                    alternatives.Any(match => match(document));
            }

            parts = SplitTopLevel(expression, "and");
            if (parts.Count > 1)
            {
                Func<TextSearchDocument, bool>[] requirements =
                    parts.Select(ParseExpression).ToArray();
                return document =>
                    requirements.All(match => match(document));
            }

            if (expression[0] is '!' or '-')
            {
                Func<TextSearchDocument, bool> child =
                    ParseExpression(expression[1..]);
                return document => !child(document);
            }
            if (expression.StartsWith("not ",
                    StringComparison.OrdinalIgnoreCase))
            {
                Func<TextSearchDocument, bool> child =
                    ParseExpression(expression[4..]);
                return document => !child(document);
            }

            int separator = expression.IndexOf(':');
            string? field = null;
            string value = expression;
            if (separator > 0)
            {
                string candidate = expression[..separator].ToLowerInvariant();
                if (candidate is "model" or "performer" or "card" or "name" or
                    "id" or "title" or "show" or "outfit" or "description" or
                    "desc" or "tag" or "tags")
                {
                    field = candidate;
                    value = expression[(separator + 1)..];
                }
            }
            value = value.Trim().Trim('"');
            return document => document.Contains(field, value);
        }

        private static List<string> SplitTopLevel(
            string text, string operation)
        {
            List<string> parts = [];
            bool quoted = false;
            int depth = 0;
            int start = 0;
            for (int index = 0; index <= text.Length - operation.Length; index++)
            {
                if (text[index] == '"')
                {
                    quoted = !quoted;
                }
                else if (!quoted && text[index] == '(')
                {
                    depth++;
                }
                else if (!quoted && text[index] == ')')
                {
                    depth--;
                }
                else if (!quoted && depth == 0 &&
                    text.AsSpan(index, operation.Length)
                        .Equals(operation, StringComparison.OrdinalIgnoreCase) &&
                    IsBoundary(text, index - 1) &&
                    IsBoundary(text, index + operation.Length))
                {
                    parts.Add(text[start..index].Trim());
                    index += operation.Length - 1;
                    start = index + 1;
                }
            }
            if (parts.Count > 0)
            {
                parts.Add(text[start..].Trim());
            }
            return parts;
        }

        private static bool IsBoundary(string text, int index) =>
            index < 0 || index >= text.Length ||
            !char.IsLetterOrDigit(text[index]);

        private static string TrimParentheses(string text)
        {
            while (text.Length >= 2 && text[0] == '(' && text[^1] == ')' &&
                OuterParenthesesEncloseAll(text))
            {
                text = text[1..^1].Trim();
            }
            return text;
        }

        private static bool OuterParenthesesEncloseAll(string text)
        {
            bool quoted = false;
            int depth = 0;
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] == '"')
                {
                    quoted = !quoted;
                }
                else if (!quoted && text[index] == '(')
                {
                    depth++;
                }
                else if (!quoted && text[index] == ')' && --depth == 0)
                {
                    return index == text.Length - 1;
                }
            }
            return false;
        }
    }

    internal class ControlScrollListener : NativeWindow, IDisposable
        {
        public event ControlScrolledEventHandler? ControlScrolled;
        public delegate void ControlScrolledEventHandler(object sender, EventArgs e);

        private const uint WM_MOUSEWHEEL = 0x020A;
        private const uint WM_HSCROLL = 0x114;
        private const uint WM_VSCROLL = 0x115;
        private readonly Control _control;

        public ControlScrollListener(Control control)
        {
            _control = control;
            AssignHandle(control.Handle);
        }

        protected bool Disposed { get; set; }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (Disposed) return;

            if (disposing)
            {
                // Free other managed objects that implement IDisposable only
            }

            // release any unmanaged objects
            // set the object references to null
            ReleaseHandle();

            Disposed = true;
        }

        protected override void WndProc(ref Message m)
        {
            HandleControlScrollMessages(m);
            base.WndProc(ref m);
        }

        private void HandleControlScrollMessages(Message m)
        {
            if (m.Msg == WM_HSCROLL || m.Msg == WM_VSCROLL || m.Msg == WM_MOUSEWHEEL)
            {
                ControlScrolled?.Invoke(_control, EventArgs.Empty);
            }
        }
    }
}
