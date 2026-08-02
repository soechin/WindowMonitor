using System.Runtime.InteropServices;

namespace WindowMonitor.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

/// <summary>WM_WINDOWPOSCHANGING 的 lParam。欄位順序必須與 Win32 一致。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPOS
{
    public IntPtr hwnd;
    public IntPtr hwndInsertAfter;
    public int x;
    public int y;
    public int cx;
    public int cy;
    public int flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public int dwFlags;
}

internal static partial class NativeMethods
{
    // 視窗擴充樣式
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // SetLayeredWindowAttributes
    public const int LWA_ALPHA = 0x00000002;

    // DwmGetWindowAttribute
    public const int DWMWA_CLOAKED = 14;

    // 命中測試
    public const int WM_NCHITTEST = 0x0084;
    public const int HTCLIENT = 1;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    // 縮放與移動
    public const int WM_SIZING = 0x0214;
    public const int WM_WINDOWPOSCHANGING = 0x0046;

    // WM_SIZING 的 wParam：使用者正在拖哪一條邊或哪一個角
    public const int WMSZ_LEFT = 1;
    public const int WMSZ_RIGHT = 2;
    public const int WMSZ_TOP = 3;
    public const int WMSZ_TOPLEFT = 4;
    public const int WMSZ_TOPRIGHT = 5;
    public const int WMSZ_BOTTOM = 6;
    public const int WMSZ_BOTTOMLEFT = 7;
    public const int WMSZ_BOTTOMRIGHT = 8;

    // WINDOWPOS.flags
    public const int SWP_NOSIZE = 0x0001;
    public const int SWP_NOMOVE = 0x0002;

    // MonitorFromRect
    public const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, int dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromRect(ref RECT lprc, int dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetShellWindow();

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>
    /// 讀取視窗標題。GetWindowTextLength 可能回報 0（無標題）或偶爾略小於實際長度，
    /// 因此緩衝區多留一些空間。
    /// </summary>
    public static string GetWindowTitle(IntPtr hWnd)
    {
        int length = GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[length + 2];
        int copied = GetWindowText(hWnd, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    /// <summary>
    /// 判斷視窗是否被 DWM 標記為 cloaked（隱形）。UWP／Store 應用會留下大量
    /// 這類看不見的幽靈視窗，不過濾掉的話會塞滿選單清單。
    /// </summary>
    public static bool IsCloaked(IntPtr hWnd)
    {
        // 呼叫失敗時（例如舊版系統）視為未 cloaked，交由其他條件過濾
        return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    /// <summary>
    /// 取得與指定矩形最接近之螢幕的完整範圍與工作區（皆為實體像素）。
    /// 取不到時回傳 false，呼叫端應直接放棄箝制，不要拿未初始化的數值硬算。
    /// </summary>
    public static bool TryGetMonitorBounds(RECT reference, out RECT monitor, out RECT work)
    {
        monitor = default;
        work = default;

        IntPtr handle = MonitorFromRect(ref reference, MONITOR_DEFAULTTONEAREST);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(handle, ref info))
        {
            return false;
        }

        monitor = info.rcMonitor;
        work = info.rcWork;
        return true;
    }

    public static bool HasExStyle(IntPtr hWnd, int style)
    {
        return ((long)GetWindowLongPtr(hWnd, GWL_EXSTYLE) & (uint)style) != 0;
    }

    public static void SetExStyle(IntPtr hWnd, int style, bool enabled)
    {
        long current = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        long mask = (uint)style;
        long updated = enabled ? current | mask : current & ~mask;
        if (updated != current)
        {
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, (IntPtr)updated);
        }
    }
}
