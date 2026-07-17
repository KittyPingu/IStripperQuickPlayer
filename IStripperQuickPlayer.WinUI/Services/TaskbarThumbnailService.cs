using System.Runtime.InteropServices;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class TaskbarThumbnailService : IDisposable
{
    private const int GwlWndProc = -4;
    private const int WmCommand = 0x0111;
    private const int ThbnClicked = 0x1800;
    private const int ImageIcon = 1;
    private const int LrLoadFromFile = 0x0010;
    private const int ThbfEnabled = 0x0000;
    private const int ThbIcon = 0x0002;
    private const int ThbTooltip = 0x0004;
    private const int NextClipId = 2001;
    private const int NextCardId = 2002;

    private readonly nint _hwnd;
    private readonly Action _nextClip;
    private readonly Action _nextCard;
    private readonly WndProc _wndProc;
    private nint _oldWndProc;
    private bool _disposed;

    public TaskbarThumbnailService(nint hwnd, Action nextClip, Action nextCard)
    {
        _hwnd = hwnd;
        _nextClip = nextClip;
        _nextCard = nextCard;
        _wndProc = SubclassWndProc;
        if (TryAddButtons())
        {
            _oldWndProc = SetWindowLongPtr(_hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_oldWndProc != 0)
        {
            SetWindowLongPtr(_hwnd, GwlWndProc, _oldWndProc);
        }

        _disposed = true;
    }

    private bool TryAddButtons()
    {
        try
        {
            ITaskbarList3 taskbar = (ITaskbarList3)new CTaskbarList();
            taskbar.HrInit();

            THUMBBUTTON[] buttons =
            [
                CreateButton(NextClipId, "Next Clip", "next_clip.ico"),
                CreateButton(NextCardId, "Next Card", "next_model.ico")
            ];

            taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private nint SubclassWndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmCommand && HiWord(wParam) == ThbnClicked)
        {
            switch (LoWord(wParam))
            {
                case NextClipId:
                    _nextClip();
                    return 0;
                case NextCardId:
                    _nextCard();
                    return 0;
            }
        }

        return CallWindowProc(_oldWndProc, hwnd, message, wParam, lParam);
    }

    private static THUMBBUTTON CreateButton(int id, string tooltip, string iconFile)
    {
        return new THUMBBUTTON
        {
            dwMask = ThbIcon | ThbTooltip,
            iId = (uint)id,
            hIcon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", iconFile), ImageIcon, 0, 0, LrLoadFromFile),
            szTip = tooltip,
            dwFlags = ThbfEnabled
        };
    }

    private static int HiWord(nint value) => (value.ToInt32() >> 16) & 0xffff;

    private static int LoWord(nint value) => value.ToInt32() & 0xffff;

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class CTaskbarList;

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        void SetProgressValue(nint hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(nint hwnd, int tbpFlags);
        void RegisterTab(nint hwndTab, nint hwndMDI);
        void UnregisterTab(nint hwndTab);
        void SetTabOrder(nint hwndTab, nint hwndInsertBefore);
        void SetTabActive(nint hwndTab, nint hwndMDI, uint dwReserved);
        void ThumbBarAddButtons(nint hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButton);
        void ThumbBarUpdateButtons(nint hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButton);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public int dwMask;
        public uint iId;
        public uint iBitmap;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public int dwFlags;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hInst, string name, int type, int cx, int cy, int fuLoad);
}
