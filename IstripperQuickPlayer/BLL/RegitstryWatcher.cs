using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace IStripperQuickPlayer.BLL
{
    public class RegistryWatcher : IDisposable
{
                 /// <summary>
    /// The period in ms between registry polls.
    /// </summary>
    private const int PERIOD = 100;

    /// <summary>
    /// The current reg values to be compared against.
    /// </summary>
    private readonly Dictionary<Tuple<string, string>, object> currentRegValues;

    /// <summary>
    /// Registry entries to watch.
    /// </summary>
    private readonly Tuple<string, string>[] toWatch;

    /// <summary>
    /// The timer to trigger registry polls.
    /// </summary>
    private readonly System.Threading.Timer timer;

    /// <summary>
    /// Occurs when registry value changes.
    /// </summary>
    public event EventHandler<RegistryChangeEventArgs> RegistryChange;

    /// <summary>
    /// Initializes a new instance of the <see cref="RegistryWatcher"/> class.
    /// </summary>
    /// <param name="toWatch">Registry entries to watch.</param>
    public RegistryWatcher(params Tuple<string, string>[] toWatch)
    {
        this.toWatch = toWatch;
        if (toWatch.Length > 0)
        {
            currentRegValues = toWatch.ToDictionary(key => key, key => Registry.GetValue(key.Item1, key.Item2, null));
            timer = new System.Threading.Timer(CheckRegistry, null, PERIOD, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Checks the registry.
    /// </summary>
    /// <param name="state">The state.</param>
    private void CheckRegistry(object state)
    {
        foreach (Tuple<string, string> reg in toWatch)
        {
            object newValue = Registry.GetValue(reg.Item1, reg.Item2, null);
            if (currentRegValues[reg].ToString() != newValue.ToString())
            {
                RegistryChange?.Invoke(this, new RegistryChangeEventArgs(reg.Item1, reg.Item2, newValue));
                currentRegValues[reg] = newValue;
            }
        }

        timer.Change(PERIOD, Timeout.Infinite);
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    public void Dispose()
    {
        try
        {
            timer?.Dispose();
        }
        catch { }
    }

    /// <summary>
    /// Event args provided upon registry value changing.
    /// </summary>
    public class RegistryChangeEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RegistryChangeEventArgs"/> class.
        /// </summary>
        /// <param name="keyName">Name of the key.</param>
        /// <param name="valueName">Name of the value.</param>
        /// <param name="value">The value.</param>
        public RegistryChangeEventArgs(string keyName, string valueName, object value)
        {
            KeyName = keyName;
            ValueName = valueName;
            Value = value;
        }

        /// <summary>
        /// Gets the name of the key.
        /// </summary>
        public string KeyName { get; }

        /// <summary>
        /// Gets the name of the value.
        /// </summary>
        public string ValueName { get; }

        /// <summary>
        /// Gets the value.
        /// </summary>
        public object Value { get; }
    }
}
}
