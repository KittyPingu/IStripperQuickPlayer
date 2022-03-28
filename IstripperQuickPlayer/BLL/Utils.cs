using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace IStripperQuickPlayer.BLL
{
    internal static class Utils
    {
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

    internal class ControlScrollListener : NativeWindow, IDisposable
        {
        public event ControlScrolledEventHandler ControlScrolled;
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
            if (m.Msg == WM_HSCROLL | m.Msg == WM_VSCROLL | m.Msg == WM_MOUSEWHEEL)
            {
                if (ControlScrolled != null)
                {
                    ControlScrolled(_control, new EventArgs());
                }
            }
        }
    }
}
