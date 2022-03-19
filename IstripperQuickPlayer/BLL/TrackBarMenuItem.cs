using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms.Design;

namespace IStripperQuickPlayer
{
[ToolStripItemDesignerAvailability(ToolStripItemDesignerAvailability.MenuStrip | 
                                   ToolStripItemDesignerAvailability.ContextMenuStrip)]
public class TrackBarMenuItem : ToolStripControlHost
{
    private ColorSlider.ColorSlider trackBar = null;

    public TrackBarMenuItem():base(new ColorSlider.ColorSlider())
    {
        this.trackBar = this.Control as ColorSlider.ColorSlider;
    }

        // Add properties, events etc. you want to expose...
        #region Properties
        public decimal Value
        {
            get
            {
                return trackBar.Value;
            }
            set { trackBar.Value = value; }
        }

        public decimal Value2
        {
            get
            {
                return trackBar.Value;
            }
            set { trackBar.Value = value; }
        }

        public decimal Maximum
        {
            get
            {
                return trackBar.Maximum;
            }
            set { trackBar.Maximum = value; }
        }

        public decimal Minimum
        {
            get
            {
                return trackBar.Minimum;
            }
            set { trackBar.Minimum = value; }
        }

        public TickStyle TickStyle
        {
            get
            {
                return trackBar.TickStyle;
            }
            set { trackBar.TickStyle = value; }
        }

        public Color TrackbarColor
        {get {return trackBar.BackColor; } set {trackBar.BackColor = value; } }

        
        public decimal SmallChange
        {get {return trackBar.SmallChange; } set {trackBar.SmallChange = value; } }

                
        public decimal LargeChange
        {get {return trackBar.LargeChange; } set {trackBar.LargeChange = value; } }

                        
        public Size ClientSize
        {get {return trackBar.ClientSize; } set {trackBar.ClientSize = value; } }
 

        public bool Has2Values
        {get {return trackBar.Has2Values; } set {trackBar.Has2Values = value; } }

        public decimal ScaleDivisions
        {get {return trackBar.ScaleDivisions; } set {trackBar.ScaleDivisions = value; } }

        public Color TickColor
        {get {return trackBar.TickColor; } set {trackBar.TickColor = value; } }

        #endregion Properties

        #region Events

        protected override void OnSubscribeControlEvents(Control c)
{
            // Call the base so the base events are connected.
            base.OnSubscribeControlEvents(c);
            // Cast the control to a ColorSlider control.
            ColorSlider.ColorSlider trackBar = (ColorSlider.ColorSlider) c;
            // Add the event.
            trackBar.ValueChanged +=
                new EventHandler(OnValueChanged);
        }

        protected override void OnUnsubscribeControlEvents(Control c)
        {
            // Call the base method so the basic events are unsubscribed.
            base.OnUnsubscribeControlEvents(c);
            // Cast the control to a ColorSlider control.
            ColorSlider.ColorSlider trackBar = (ColorSlider.ColorSlider) c;
            // Remove the event.
            trackBar.ValueChanged -=
                new EventHandler(OnValueChanged);
        }

        public event EventHandler ValueChanged;

        // Raise the DateChanged event.
        private void OnValueChanged(object sender, EventArgs e)
        {
            if (ValueChanged != null)
            {
                ValueChanged(this, e);
            }
        }

        #endregion Events
    }
}
