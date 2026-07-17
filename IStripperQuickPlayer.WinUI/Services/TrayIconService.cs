using System.Runtime.InteropServices;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class TrayIconService : IDisposable
{
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NimAdd = 0x00000000;
    private const int NimDelete = 0x00000002;
    private const int WmAppTray = 0x8000 + 42;
    private const int WmLButtonDblClk = 0x0203;
    private const int GwlWndProc = -4;
    private const int ImageIcon = 1;
    private const int LrLoadFromFile = 0x0010;

    private readonly nint _hwnd;
    private readonly WndProc _wndProc;
    private nint _oldWndProc;
    private bool _disposed;

    public TrayIconService(nint hwnd)
    {
        _hwnd = hwnd;
        _wndProc = SubclassWndProc;
        _oldWndProc = SetWindowLongPtr(_hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));
        AddIcon();
    }

    public void HideWindow()
    {
        ShowWindow(_hwnd, 0);
    }

    public void ShowWindow()
    {
        ShowWindow(_hwnd, 5);
        SetForegroundWindow(_hwnd);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        NotifyIcon(NimDelete);
        if (_oldWndProc != 0)
        {
            SetWindowLongPtr(_hwnd, GwlWndProc, _oldWndProc);
        }

        _disposed = true;
    }

    private nint SubclassWndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmAppTray && lParam.ToInt32() == WmLButtonDblClk)
        {
            ShowWindow();
            return 0;
        }

        return CallWindowProc(_oldWndProc, hwnd, message, wParam, lParam);
    }

    private void AddIcon()
    {
        NotifyIcon(NimAdd);
    }

    private void NotifyIcon(int message)
    {
        NOTIFYICONDATA data = new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmAppTray,
            hIcon = LoadAppIcon(),
            szTip = "IStripper Quick Player"
        };

        Shell_NotifyIcon(message, ref data);
    }

    private static nint LoadAppIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            nint icon = LoadImage(0, iconPath, ImageIcon, 0, 0, LrLoadFromFile);
            if (icon != 0)
            {
                return icon;
            }
        }

        return LoadIcon(0, new nint(32512));
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hInst, string name, int type, int cx, int cy, int fuLoad);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);
}
