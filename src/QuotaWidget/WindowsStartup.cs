using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace QuotaWidget.App;

/// <summary>One per-user startup entry; the registry, not a second settings flag,
/// owns this preference. Only an explicit user action writes it.</summary>
public static class WindowsStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "QuotaWidget";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public static string Command(string executable, string dataRoot)
    {
        string Quote(string path)
        {
            path = Path.GetFullPath(path);
            if (path.Contains('"') || path.Contains('\0')) throw new ArgumentException("Invalid startup path.");
            // Windows command-line parsing doubles trailing backslashes before a quote.
            return "\"" + path + new string('\\', path.Reverse().TakeWhile(c => c == '\\').Count()) + "\"";
        }
        var command = Quote(executable) + " --data-dir " + Quote(dataRoot);
        // Documented Run-key command-line limit; do not silently register a broken launch.
        if (command.Length > 260) throw new IOException("Startup command exceeds the Windows Run-key limit.");
        return command;
    }

    public static void SetEnabled(bool enabled, string executable, string dataRoot)
    {
        if (enabled)
        {
            if (!File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Startup requires an existing executable.");
            var command = Command(executable, dataRoot);
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
