using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace QuotaWidget.Core;

/// <summary>
/// The CLI the widget will run, and why. Problem is null when usable; otherwise one of
/// cli_missing / cli_untrusted / cli_incompatible.
/// </summary>
public sealed record CliChoice(string? Path, string? Version, string? Problem, string Summary)
{
    public bool Usable => Problem is null && Path is not null;
}

/// <summary>Finding, trusting and launching the official Claude Code CLI.</summary>
[SupportedOSPlatform("windows")]
public static class ClaudeCli
{
    /// <summary>
    /// Protocol features the collector relies on, looked up in the binary itself rather than
    /// inferred from a version number: the get_usage control request, and the persisted
    /// fetch stamp (cachedUsageUtilization.fetchedAtMs) that proves an answer is fresh.
    /// 2.1.183 has get_usage but no fetch stamp; 2.1.281 and 2.1.284 have both.
    /// </summary>
    static readonly string[] RequiredMarkers = ["\"get_usage\"", "cachedUsageUtilization", "fetchedAtMs"];

    static readonly object Gate = new();
    static readonly Dictionary<string, (long Size, DateTime Mtime, bool Ok)> CapabilityCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional file that remembers capability scans across restarts (no secrets: paths, sizes, results).</summary>
    public static string? CachePath { get; set; }

    /// <summary>Candidates: PATH, ~/.local/bin, then copies bundled with Claude Desktop.</summary>
    public static IEnumerable<string> Candidates()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string p;
            try { p = Path.Combine(dir.Trim(), "claude.exe"); } catch (ArgumentException) { continue; }
            yield return p;
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code") };
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
            roots.AddRange(Directory.EnumerateDirectories(packages, "Claude_*").Select(d => Path.Combine(d, "LocalCache", "Roaming", "Claude", "claude-code")));
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var exe in BundledExecutables(root)) yield return exe;
    }

    /// <summary>Known Desktop layouts: version/claude.exe and version/payload-hash/claude.exe.
    /// Bounded discovery only; every candidate still needs the same signature and capability checks.</summary>
    public static IEnumerable<string> BundledExecutables(string root)
    {
        var result = new List<string>();
        try
        {
            foreach (var version in Directory.EnumerateDirectories(root))
            {
                if ((File.GetAttributes(version) & FileAttributes.ReparsePoint) != 0) continue;
                var flat = Path.Combine(version, "claude.exe");
                if (File.Exists(flat)) result.Add(flat);
                try
                {
                    foreach (var payload in Directory.EnumerateDirectories(version))
                    {
                        if ((File.GetAttributes(payload) & FileAttributes.ReparsePoint) != 0) continue;
                        var exe = Path.Combine(payload, "claude.exe");
                        if (File.Exists(exe)) result.Add(exe);
                    }
                }
                catch (IOException) { } // Desktop may replace a version while discovery runs.
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    public static Version? FileVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return Version.TryParse(v.ProductVersion ?? v.FileVersion, out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the CLI to run. A configured path is used only if it is signed and compatible
    /// (no silent fallback). Otherwise the newest candidate that is both is chosen; if none is,
    /// the reason names what was found.
    /// </summary>
    public static CliChoice Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Evaluate(Environment.ExpandEnvironmentVariables(configured), configuredByUser: true);

        var found = Candidates().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (Path: p, Version: FileVersion(p)))
            .OrderByDescending(x => x.Version ?? new Version(0, 0))
            .ToList();
        if (found.Count == 0) return new CliChoice(null, null, "cli_missing", "未找到 claude.exe");
        CliChoice? firstRejected = null;
        foreach (var (path, _) in found)
        {
            var c = Evaluate(path, configuredByUser: false);
            if (c.Usable) return c;
            firstRejected ??= c;
        }
        return firstRejected!;
    }

    static CliChoice Evaluate(string path, bool configuredByUser)
    {
        var ver = FileVersion(path)?.ToString(3);
        var label = $"Claude Code {ver ?? "?"}";
        if (!File.Exists(path)) return new CliChoice(null, null, "cli_missing", configuredByUser ? "claudeExePath 指向的文件不存在" : "未找到 claude.exe");
        if (!Authenticode.IsSignedBy(path)) return new CliChoice(path, ver, "cli_untrusted", $"{label} 签名校验未通过");
        if (!HasRequiredMarkers(path)) return new CliChoice(path, ver, "cli_incompatible", $"{label} 不支持所需的额度接口，请更新");
        return new CliChoice(path, ver, null, label);
    }

    /// <summary>Streams the binary once per (path, size, write time) looking for the protocol markers.</summary>
    public static bool HasRequiredMarkers(string path)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); } catch { return false; }
        if (!fi.Exists) return false;
        lock (Gate)
        {
            LoadCache();
            if (CapabilityCache.TryGetValue(fi.FullName, out var c) && c.Size == fi.Length && c.Mtime == fi.LastWriteTimeUtc) return c.Ok;
        }
        var ok = ScanFor(fi.FullName, RequiredMarkers.Select(m => Encoding.ASCII.GetBytes(m)).ToArray());
        lock (Gate)
        {
            CapabilityCache[fi.FullName] = (fi.Length, fi.LastWriteTimeUtc, ok);
            SaveCache();
        }
        return ok;
    }

    static bool ScanFor(string path, byte[][] patterns)
    {
        var found = new bool[patterns.Length];
        var overlap = patterns.Max(p => p.Length) - 1;
        var buffer = new byte[4 << 20];
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            var carry = 0;
            int read;
            while ((read = fs.Read(buffer, carry, buffer.Length - carry)) > 0)
            {
                var span = buffer.AsSpan(0, carry + read);
                for (var i = 0; i < patterns.Length; i++)
                    if (!found[i] && span.IndexOf(patterns[i]) >= 0) found[i] = true;
                if (found.All(f => f)) return true;
                carry = Math.Min(overlap, span.Length);
                span[^carry..].CopyTo(buffer);
            }
        }
        catch
        {
            return false;
        }
        return found.All(f => f);
    }

    static bool _cacheLoaded;

    static void LoadCache()
    {
        if (_cacheLoaded || CachePath is null) return;
        _cacheLoaded = true;
        try
        {
            if (AtomicFile.TryReadAllText(CachePath) is not { } text) return;
            using var doc = JsonDocument.Parse(text);
            foreach (var e in doc.RootElement.EnumerateArray())
                CapabilityCache[e.GetProperty("path").GetString()!] =
                    (e.GetProperty("size").GetInt64(), new DateTime(e.GetProperty("mtimeTicks").GetInt64(), DateTimeKind.Utc), e.GetProperty("ok").GetBoolean());
        }
        catch { /* a bad cache only costs a rescan */ }
    }

    static void SaveCache()
    {
        if (CachePath is null) return;
        try
        {
            var rows = CapabilityCache.Select(kv => new { path = kv.Key, size = kv.Value.Size, mtimeTicks = kv.Value.Mtime.Ticks, ok = kv.Value.Ok });
            AtomicFile.WriteAllText(CachePath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// Child environment: no inherited Claude/Anthropic variables (a parent session's token or
    /// base URL must not leak in), the widget's own config dir, and telemetry/update suppression.
    /// </summary>
    public static void PrepareEnvironment(ProcessStartInfo psi, string configDir, bool allowUsage = false)
    {
        foreach (var key in psi.Environment.Keys.ToList())
            if (key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(key);
        psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;
        // 2.1.284 gates /api/oauth/usage behind this blanket switch too. For usage queries,
        // disable telemetry/reporting separately; the blanket switch silently yields no data.
        if (!allowUsage) psi.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        psi.Environment["DISABLE_TELEMETRY"] = "1";
        psi.Environment["DISABLE_ERROR_REPORTING"] = "1";
        psi.Environment["DISABLE_AUTOUPDATER"] = "1";
    }

    /// <summary>
    /// Creates the login directory readable only by the current user and SYSTEM (no inherited
    /// entries). Returns false when an existing directory grants anyone else access and could
    /// not be tightened.
    /// </summary>
    public static bool EnsurePrivateDirectory(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists) info.Create();
            var me = WindowsIdentity.GetCurrent().User!;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            if (IsPrivate(info, me, system)) return true;
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.SetOwner(me);
            info.SetAccessControl(sec);
            return IsPrivate(info, me, system);
        }
        catch
        {
            return false;
        }
    }

    static bool IsPrivate(DirectoryInfo info, SecurityIdentifier me, SecurityIdentifier system)
    {
        var rules = info.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule r in rules)
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)r.IdentityReference;
            if (sid != me && sid != system) return false;
        }
        return true;
    }

    /// <summary>
    /// The single login/logout path (tray menu, note bar and the double-click script all end
    /// here): resolve a signed, compatible CLI, make the config dir private, then open a console
    /// running "claude auth login|logout" with a clean environment. Returns an error message
    /// instead of starting anything when a check fails.
    /// </summary>
    public static string? LaunchAuthConsole(DataPaths paths, CliChoice choice, string configDir, bool logout,
        Func<string, bool>? ensurePrivate = null, Func<ProcessStartInfo, Process?>? start = null)
    {
        if (AuthPreflight(choice, configDir, ensurePrivate) is { } error) return error;
        using var executable = start is null ? Authenticode.OpenVerified(choice.Path!) : null;
        if (start is null && executable is null) return "CLI 签名校验失败，已停止";
        // Start the official executable directly: no generated shell script or interpolated command.
        var psi = new ProcessStartInfo(choice.Path!) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = paths.Root };
        psi.ArgumentList.Add("auth"); psi.ArgumentList.Add(logout ? "logout" : "login");
        if (!logout) psi.ArgumentList.Add("--claudeai");
        PrepareEnvironment(psi, configDir);
        (start ?? Process.Start)(psi);
        return null;
    }

    public static string? AuthPreflight(CliChoice choice, string configDir, Func<string, bool>? ensurePrivate = null)
    {
        if (!choice.Usable) return choice.Summary;
        return (ensurePrivate ?? EnsurePrivateDirectory)(configDir) ? null : "无法保护登录目录，已停止";
    }

    /// <summary>The same auth gate as interactive login. Only the official CLI removes its credentials.</summary>
    public static async Task<string?> LogoutQuietlyAsync(CliChoice choice, string configDir, string workDir,
        CancellationToken ct, Func<string, bool>? ensurePrivate = null,
        Func<ProcessStartInfo, CancellationToken, Task<int>>? run = null)
    {
        ct.ThrowIfCancellationRequested();
        if (AuthPreflight(choice, configDir, ensurePrivate) is { } error) return error;
        ct.ThrowIfCancellationRequested();
        using var executable = run is null ? Authenticode.OpenVerified(choice.Path!) : null;
        if (run is null && executable is null) return "CLI 签名校验失败，未退出登录";
        var psi = new ProcessStartInfo(choice.Path!)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("auth"); psi.ArgumentList.Add("logout");
        PrepareEnvironment(psi, configDir);
        try
        {
            var code = await (run ?? RunQuietAuth)(psi, ct).ConfigureAwait(false);
            if (code != 0) return "官方 CLI 退出登录失败";
            return ClaudeConfigFiles.CredentialStamp(configDir) is null ? null : "本机凭证仍存在，未确认退出登录";
        }
        catch (OperationCanceledException) { return "退出登录超时，本机凭证可能仍在"; }
        catch { return "退出登录失败，本机凭证可能仍在"; }
    }

    static async Task<int> RunQuietAuth(ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        ct.ThrowIfCancellationRequested();
        process.Start();
        using var kill = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        // Drain and discard: never persist CLI auth output.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }
}
