using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace QuotaWidget.App;

/// <summary>Follows the Windows app theme; colour values are the design's light-dark() tokens.</summary>
public static class Theme
{
    public static bool IsDark { get; private set; }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var r = Application.Current.Resources;
        void Set(string key, string light, string darkHex) =>
            r[key] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkHex : light)));
        Set("Bg", "#fafafa", "#202020");
        Set("Raised", "#ebedef", "#2c2d30");
        Set("Ink", "#202124", "#eeeeef");
        Set("Muted", "#63676d", "#acafb5");
        Set("Axis", "#48515c", "#c4c9d0");
        Set("Line", "#dadbdd", "#3b3c3e");
        Set("Blue", "#1872bf", "#69b8f3");
        Set("BlueSecondary", "#51768f", "#4c83aa");
        Set("Orange", "#b76817", "#eba552");
        Set("Claude", "#b35f42", "#e3a080");
        Set("Fable", "#925009", "#c97827");
        Set("Red", "#be3b36", "#f18076");
        Set("OrangeRed", "#c36530", "#eca074");
        Set("Violet", "#8051bd", "#bc9df0");
        // Time units have their own quiet palette, independent of provider and quota severity.
        Set("ResetDay", "#78638c", "#b9a7cc");
        Set("ResetHour", "#496e82", "#a2c0d0");
        Set("ResetMinute", "#617650", "#adbfa0");
        Set("Green", "#268259", "#70c99d");
        Set("Track", "#dfe3e8", "#383c42");
        Set("QuotaHealthy", "#51775e", "#bfd2c3");
        Set("QuotaNeutral", "#7b724d", "#d0c9af");
        Set("QuotaWarm", "#98651b", "#e6b264");
        Set("QuotaHigh", "#a45732", "#df8d63");
        Set("QuotaCritical", "#ab4744", "#d67c76");
    }

    static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Muted, continuous usage spectrum. Always based on USED quota, even in remaining mode.</summary>
    public static Brush QuotaUsageBrush(double? usedPercent)
    {
        if (usedPercent is not { } used || !double.IsFinite(used)) return Brush("Muted");
        used = Math.Clamp(used, 0, 100);
        var stops = new (double At, string Key)[]
        {
            (0, "QuotaHealthy"), (35, "QuotaNeutral"), (60, "QuotaWarm"), (80, "QuotaHigh"), (93, "QuotaCritical"), (100, "QuotaCritical"),
        };
        for (var i = 1; i < stops.Length; i++)
        {
            if (used > stops[i].At) continue;
            var a = ((SolidColorBrush)Brush(stops[i - 1].Key)).Color;
            var b = ((SolidColorBrush)Brush(stops[i].Key)).Color;
            var t = (used - stops[i - 1].At) / (stops[i].At - stops[i - 1].At);
            byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return Freeze(new SolidColorBrush(Color.FromRgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B))));
        }
        return Brush("QuotaCritical");
    }
}
