using IStripperQuickPlayer.BLL;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms.Design;
using ColorSlider;

namespace IStripperQuickPlayer
{
[ToolStripItemDesignerAvailability(ToolStripItemDesignerAvailability.MenuStrip | 
                                   ToolStripItemDesignerAvailability.ContextMenuStrip)]
public class TrackBarMenuItem : ToolStripControlHost
{
    private ColorSlider.ColorSlider? trackBar;

    public TrackBarMenuItem():base(new ColorSlider.ColorSlider())
    {
        if (this.Control != null && this.Control is ColorSlider.ColorSlider)
            this.trackBar = (ColorSlider.ColorSlider)this.Control;
    }

        // Add properties, events etc. you want to expose...
        #region Properties
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public decimal Value
        {
            get
            {
                if (trackBar == null) return 0;
                return trackBar.Value;
            }
            set { if (trackBar!=null) trackBar.Value = value; }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public decimal Value2
        {
            get
            {
                if (trackBar == null) return 0;
                return trackBar.Value;
            }
            set { if (trackBar!=null) trackBar.Value = value; }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public decimal Maximum
        {
            get
            {
                 if (trackBar == null) return 0;
                return trackBar.Maximum;
            }
            set { if (trackBar!=null) trackBar.Maximum = value; }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public decimal Minimum
        {
            get
            {
                 if (trackBar == null) return 0;
                return trackBar.Minimum;
            }
            set { if (trackBar!=null) trackBar.Minimum = value; }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public TickStyle TickStyle
        {
            get
            {
                if (trackBar == null) return TickStyle.None;
                return trackBar.TickStyle;
            }
            set { if (trackBar!=null) trackBar.TickStyle = value; }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Color TrackbarColor
        {get { if (trackBar == null) return Color.White; return trackBar.BackColor; } set {if (trackBar!=null) trackBar.BackColor = value; } }

        
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public decimal SmallChange
        {get { if (trackBar == null) return 0;return trackBar.SmallChange; } set {if (trackBar!=null) trackBar.SmallChange = value; } }

                
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public decimal LargeChange
        {get { if (trackBar == null) return 0;return trackBar.LargeChange; } set {if (trackBar!=null) trackBar.LargeChange = value; } }

                        
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Size ClientSize
        {get { if (trackBar == null) return new Size();return trackBar.ClientSize; } set {if (trackBar!=null) trackBar.ClientSize = value; } }
 

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool Has2Values
        {get { if (trackBar == null) return false;return trackBar.Has2Values; } set {if (trackBar!=null) trackBar.Has2Values = value; } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public decimal ScaleDivisions
        {get { if (trackBar == null) return 0M;return trackBar.ScaleDivisions; } set {if (trackBar!=null) trackBar.ScaleDivisions = value; } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Color TickColor
        {get { if (trackBar == null) return Color.Black;return trackBar.TickColor; } set {if (trackBar!=null) trackBar.TickColor = value; } }

        #endregion Properties

        #region Events

        protected override void OnSubscribeControlEvents(Control? c)
        {
            // Call the base so the base events are connected.
            base.OnSubscribeControlEvents(c);
            if (c is ColorSlider.ColorSlider trackBar)
                trackBar.ValueChanged += OnValueChanged;
        }

        protected override void OnUnsubscribeControlEvents(Control? c)
        {
            // Call the base method so the basic events are unsubscribed.
            base.OnUnsubscribeControlEvents(c);
            if (c is ColorSlider.ColorSlider trackBar)
                trackBar.ValueChanged -= OnValueChanged;
        }

        public event EventHandler? ValueChanged;

        // Raise the DateChanged event.
        private void OnValueChanged(object? sender, EventArgs e)
        {
            if (ValueChanged != null)
            {
                ValueChanged(this, e);
            }
        }

        #endregion Events
    }
}
