using System.Diagnostics;
using WindowMonitor.Models;

namespace WindowMonitor.Interop;

/// <summary>
/// 列舉可作為擷取目標的最上層視窗。
/// </summary>
public static class WindowEnumerator
{
    public static List<WindowInfo> Enumerate(IntPtr excludeHandle = default)
    {
        var results = new List<WindowInfo>();
        IntPtr shell = NativeMethods.GetShellWindow();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == excludeHandle || hWnd == shell)
            {
                return true;
            }

            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                return true;
            }

            // 無標題的多半是背景／輔助視窗，對使用者也無法辨識
            string title = NativeMethods.GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            if (NativeMethods.HasExStyle(hWnd, NativeMethods.WS_EX_TOOLWINDOW))
            {
                return true;
            }

            // 這條不可省略，否則清單會混入大量 UWP 隱形幽靈視窗
            if (NativeMethods.IsCloaked(hWnd))
            {
                return true;
            }

            results.Add(new WindowInfo
            {
                Handle = hWnd,
                Title = title,
                ProcessName = GetProcessName(hWnd)
            });

            return true;
        }, IntPtr.Zero);

        return results
            .OrderBy(w => w.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string GetProcessName(IntPtr hWnd)
    {
        // 程序可能已結束，或屬於權限較高的工作階段而無法查詢，
        // 這種情況下退回只顯示標題。
        try
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0)
            {
                return string.Empty;
            }

            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }
}
