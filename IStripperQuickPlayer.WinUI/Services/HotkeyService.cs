using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int GwlWndProc = -4;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    private readonly nint _hwnd;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Dictionary<int, Action> _actions = [];
    private readonly WndProc _wndProc;
    private nint _oldWndProc;
    private bool _disposed;
    private int _nextId = 100;

    public HotkeyService(nint hwnd, DispatcherQueue dispatcherQueue)
    {
        _hwnd = hwnd;
        _dispatcherQueue = dispatcherQueue;
        _wndProc = SubclassWndProc;
        _oldWndProc = SetWindowLongPtr(_hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));
    }

    public void Register(string combination, Action action)
    {
        if (!TryParse(combination, out uint modifiers, out uint virtualKey))
        {
            return;
        }

        int id = _nextId++;
        if (RegisterHotKey(_hwnd, id, modifiers, virtualKey))
        {
            _actions[id] = action;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (int id in _actions.Keys)
        {
            UnregisterHotKey(_hwnd, id);
        }

        if (_oldWndProc != 0)
        {
            SetWindowLongPtr(_hwnd, GwlWndProc, _oldWndProc);
        }

        _disposed = true;
    }

    private nint SubclassWndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out Action? action))
        {
            _dispatcherQueue.TryEnqueue(() => action());
            return 0;
        }

        return CallWindowProc(_oldWndProc, hwnd, message, wParam, lParam);
    }

    private static bool TryParse(string combination, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        string[] parts = combination.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (string part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "control":
                case "ctrl":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
            }
        }

        string key = parts[^1].ToUpperInvariant();
        if (key.Length == 1)
        {
            virtualKey = key[0];
            return true;
        }

        if (key.StartsWith('F') && int.TryParse(key[1..], out int functionKey) && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            return true;
        }

        return false;
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);
}
