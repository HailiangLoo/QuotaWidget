using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuotaWidget.App;

/// <summary>
/// Window placement in physical pixels. With per-monitor DPI (here 200 % and 100 % side by side)
/// WPF's DIP Left/Top depend on which monitor's scale is applied, so the position is set and
/// saved through the HWND instead.
/// </summary>
static class NativePlacement
{
    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    public static void Move(IntPtr hwnd, int x, int y) => SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    public static (int X, int Y)? Position(IntPtr hwnd) => GetWindowRect(hwnd, out var r) ? (r.Left, r.Top) : null;
    public static System.Windows.Rect? Bounds(IntPtr hwnd) => GetWindowRect(hwnd, out var r)
        ? new System.Windows.Rect(r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top) : null;

    /// <summary>After expanding the small card, keep the window reachable on its current monitor.</summary>
    public static void FitToWorkArea(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        var area = Screen.FromHandle(hwnd).WorkingArea;
        var x = Math.Clamp(r.Left, area.Left, Math.Max(area.Left, area.Right - (r.Right - r.Left)));
        var y = Math.Clamp(r.Top, area.Top, Math.Max(area.Top, area.Bottom - (r.Bottom - r.Top)));
        if (x != r.Left || y != r.Top) Move(hwnd, x, y);
    }

    /// <summary>True when the visible frame's title area would land on some monitor's work area.</summary>
    public static bool IsOnScreen(int x, int y) =>
        Screen.AllScreens.Any(s => s.WorkingArea.Contains(x + 60, y + 30));

    /// <summary>Top-right corner of the primary work area, 16 DIPs in, accounting for the shadow margin.</summary>
    public static (int X, int Y) DefaultTopRight(double windowWidthDip, double shadowDip)
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        var scale = DpiAt(wa.Right - 1, wa.Top + 1) / 96.0;
        var x = wa.Right - (int)Math.Round((windowWidthDip - shadowDip + 16) * scale);
        var y = wa.Top + (int)Math.Round((16 - shadowDip) * scale);
        return (x, y);
    }

    static uint DpiAt(int x, int y)
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
            return GetDpiForMonitor(mon, 0, out var dx, out _) == 0 && dx > 0 ? dx : 96;
        }
        catch
        {
            return 96;
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
