using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QuotaWidget.Core;

/// <summary>Quota data lives under one root; a non-secret profile-level pointer makes that root stable across Windows hosts.</summary>
public sealed class DataPaths
{
    public DataPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public static string LocationFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quotawidget-location");

    public static string DefaultRoot
    {
        get
        {
            var privateData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quotawidget", "data");
            if (Directory.Exists(privateData)) return privateData;
            // Packaged desktop hosts virtualize LocalAppData differently. Reuse one physical
            // directory from shell, Claude and Codex launches, without moving any credentials.
            var saved = AtomicFile.TryReadAllText(LocationFile)?.Trim();
            if (saved is not null && Path.IsPathFullyQualified(saved) && Directory.Exists(saved)) return saved;
            return privateData;
        }
    }

    public static string CanonicalDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return Path.GetFullPath(path);
        using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) return Path.GetFullPath(path);
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) return Path.GetFullPath(path);
        var actual = buffer.ToString();
        return actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + actual[8..]
            : actual.StartsWith(@"\\?\", StringComparison.Ordinal) ? actual[4..] : actual;
    }

    public static void RememberLocation(string root) => AtomicFile.WriteAllText(LocationFile, CanonicalDirectory(root));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    /// <summary>Demo data never shares a directory with real history.</summary>
    public static string DemoRoot => Path.Combine(DefaultRoot, "demo");

    public string Root { get; }
    public string Latest => Path.Combine(Root, "latest.json");
    public string Settings => Path.Combine(Root, "settings.json");
    public string HistoryDir => Path.Combine(Root, "history");
    public string EventsDir => Path.Combine(Root, "events");
    public string LogsDir => Path.Combine(Root, "logs");
    public string ExportDir => Path.Combine(Root, "export");
    public string DefaultClaudeConfigDir => Path.Combine(Root, "claude-auth");
}
