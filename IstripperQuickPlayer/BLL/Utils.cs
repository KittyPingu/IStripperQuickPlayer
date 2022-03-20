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
    }
}
