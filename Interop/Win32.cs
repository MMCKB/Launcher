using System.Runtime.InteropServices;

namespace WinUI3Desktop;

/// <summary>user32 / dwmapi 互操作集中声明，只暴露窗口管理需要的最小面。</summary>
internal static class Win32
{
    public delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    // 窗口样式 / 布局
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;
    public const int GwlpWndProc = -4;
    public const int WsBorder = 0x00800000;
    public const int WsDlgFrame = 0x00400000;
    public const int WsThickFrame = 0x00040000;
    public const int WsExClientEdge = 0x00000200;
    public const int WsExWindowEdge = 0x00000100;
    public const int WsExStaticEdge = 0x00020000;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpFrameChanged = 0x0020;
    public static readonly IntPtr HwndBottom = new(1);

    // DWM
    public const int DwmwaNcRenderingPolicy = 2;
    public const int DwmNcrpDisabled = 1;
    public const int DwmwaCornerPreference = 33;
    public const int DwmwcpDoNotRound = 1;
    public const int DwmwaBorderColor = 34;
    public const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    // 系统度量 / 壁纸
    public const int SmCxScreen = 0;
    public const int SmCyScreen = 1;
    public const int SmMaximumTouches = 95;
    public const uint SpiGetDeskWallpaper = 0x0073;
    public const int MaxPath = 260;

    // ShowWindow
    public const int SwHide = 0;
    public const int SwShow = 5;
    public const int SwRestore = 9;

    // 全局热键
    public const uint WmHotKey = 0x0312;
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, System.Text.StringBuilder pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>WNDPROC 句柄在 x86 上是 32 位，x64 上是 64 位，按指针宽度分发。</summary>
    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong(hWnd, nIndex));

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll")]
    public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    public static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct Margins
    {
        public int LeftWidth;
        public int RightWidth;
        public int TopHeight;
        public int BottomHeight;
    }
}
