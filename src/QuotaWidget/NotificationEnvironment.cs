using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Forms;

namespace QuotaWidget.App;

/// <summary>Read-only Windows presentation state. Never changes focus or game/DND settings.</summary>
public static class NotificationEnvironment
{
    public static bool DeferVisual(int state, bool fullScreen) => state != 5 || fullScreen;
    public static bool CoversMonitor(Rect window, Rect monitor, bool maximized, bool caption, bool desktop) =>
        !desktop && !(maximized && caption) && window.Width > 0 && window.Height > 0 &&
        window.Left <= monitor.Left && window.Top <= monitor.Top && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    public static bool DeferVisual()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0 || DeferVisual(state, false)) return true;
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return true;
            var name = new StringBuilder(64); GetClassName(foreground, name, name.Capacity);
            var desktop = name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd";
            if (desktop) return false;
            if (NativePlacement.Bounds(foreground) is not { } bounds) return true;
            var screen = Screen.FromHandle(foreground).Bounds;
            return CoversMonitor(bounds, new Rect(screen.X, screen.Y, screen.Width, screen.Height), IsZoomed(foreground),
                (GetWindowLongPtr(foreground, -16).ToInt64() & 0x00C00000) != 0, false);
        }
        catch { return true; }
    }

    internal static void NoActivate(IntPtr window) => SetWindowLongPtr(window, -20,
        new IntPtr(GetWindowLongPtr(window, -20).ToInt64() | 0x08000000 | 0x80));
    internal static IntPtr Foreground => GetForegroundWindow();
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
