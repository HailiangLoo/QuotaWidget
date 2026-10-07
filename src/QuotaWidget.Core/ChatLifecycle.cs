using System.Collections.Frozen;
using System.Globalization;

namespace QuotaWidget.Core;

/// <summary>Display-only decisions. Never modify provider state, session history or token usage.</summary>
public sealed record ChatLifecycleSnapshot(IReadOnlySet<string> Hidden, string? Warning = null)
{
    public static readonly ChatLifecycleSnapshot Empty = new(new HashSet<string>().ToFrozenSet());
    public static string Key(ChatPlatform platform, string id) => platform + ":" + id.ToLowerInvariant();
    public bool IsHidden(ChatCacheEntry chat) => Hidden.Contains(Key(chat.Platform, chat.Id));
}

/// <summary>
/// Positive lifecycle evidence only: Codex threads.archived and Claude Desktop deletion tombstones.
/// A missing transcript, index entry or database row never implies deletion. Poll off the UI thread.
/// </summary>
public sealed class ChatLifecycleMonitor(string codexHome, IEnumerable<string> claudeSessionRoots)
{
    readonly string[] _claudeRoots = claudeSessionRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    readonly HashSet<string> _seenClaudeRoots = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _hidden = new(StringComparer.Ordinal);
    string? _codexDatabase;
    DateTimeOffset _discovered;

    public static ChatLifecycleMonitor Local()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string> { Path.Combine(user, "AppData", "Roaming", "Claude", "claude-code-sessions") };
        // Store builds virtualize AppData. Locate only Claude's session metadata, never browser storage.
        var packages = Path.Combine(user, "AppData", "Local", "Packages");
        try
        {
            foreach (var package in Directory.EnumerateDirectories(packages, "Claude_*", TopOnly).Take(32))
                roots.Add(Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude-code-sessions"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(user, ".codex"), roots);
    }

    static EnumerationOptions TopOnly => new() { RecurseSubdirectories = false, IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint };

    public ChatLifecycleSnapshot Poll(IEnumerable<ChatCacheEntry> candidates, DateTimeOffset now, IReadOnlySet<string>? hiddenRows = null)
    {
        var entries = candidates.Where(c => Guid.TryParseExact(c.Id, "D", out _))
            .DistinctBy(c => ChatLifecycleSnapshot.Key(c.Platform, c.Id)).ToArray();
        var keys = entries.Select(c => ChatLifecycleSnapshot.Key(c.Platform, c.Id)).ToHashSet(StringComparer.Ordinal);
        var next = _hidden.Where(keys.Contains).ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>();
        try { ReadCodex(entries.Where(c => c.Platform == ChatPlatform.Codex).ToArray(), now, next); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { warnings.Add("Codex 归档状态暂不可读"); }
        ReadClaude(entries.Where(c => c.Platform == ChatPlatform.Claude).ToArray(), now, next, warnings);
        if(hiddenRows is not null)next.UnionWith(hiddenRows.Where(keys.Contains));
        _hidden = next;
        return new(next.ToFrozenSet(StringComparer.Ordinal), warnings.Count == 0 ? null : string.Join(" · ", warnings.Distinct()));
    }

    void ReadCodex(ChatCacheEntry[] entries, DateTimeOffset now, HashSet<string> next)
    {
        if (entries.Length == 0) return;
        if (_codexDatabase is null || now - _discovered >= TimeSpan.FromSeconds(30) || now < _discovered)
        {
            // Choose the newest schema generation, not a stale backup. Unknown schemas fail conservatively.
            _codexDatabase = Directory.EnumerateFiles(codexHome, "state_*.sqlite", TopOnly)
                .Select(p => (Path: p, Version: int.TryParse(Path.GetFileNameWithoutExtension(p).AsSpan(6), out var v) ? v : -1))
                .Where(x => x.Version >= 0).OrderByDescending(x => x.Version).Select(x => x.Path).FirstOrDefault();
            _discovered = now;
        }
        if (_codexDatabase is null) return;
        using var db = new MiniSqlite(_codexDatabase, readOnly: true);
        db.Exec("PRAGMA busy_timeout=100; BEGIN");
        var updates = new Dictionary<string, bool>();
        foreach (var batch in entries.Chunk(200))
        {
            var rows = db.Query("SELECT id,archived FROM threads WHERE id IN (" + string.Join(',', batch.Select(_ => "?")) + ")",
                batch.Select(c => (object?)c.Id.ToLowerInvariant()).ToArray());
            foreach (var row in rows)
                if (row[0] is string id && row[1] is long state && state is 0 or 1)
                    updates[ChatLifecycleSnapshot.Key(ChatPlatform.Codex, id)] = state == 1;
        }
        // Commit the display snapshot only after every batch succeeded. Dispose ends the read transaction.
        foreach (var (key, hidden) in updates) { if (hidden) next.Add(key); else next.Remove(key); }
    }

    void ReadClaude(ChatCacheEntry[] entries, DateTimeOffset now, HashSet<string> next, List<string> warnings)
    {
        if (entries.Length == 0) return;
        var directories = new List<string>();
        var complete = true;
        foreach (var root in _claudeRoots)
        {
            try
            {
                try { if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw new IOException("Not a directory"); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                { if (_seenClaudeRoots.Contains(root)) complete = false; continue; }
                _seenClaudeRoots.Add(root);
                foreach (var account in Directory.EnumerateDirectories(root, "*", TopOnly))
                    foreach (var org in Directory.EnumerateDirectories(account, "*", TopOnly))
                    {
                        if (directories.Count >= 512) throw new IOException("Session metadata directory limit");
                        directories.Add(org);
                    }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { complete = false; }
        }
        var anyFailure = !complete;
        foreach (var chat in entries)
        {
            var found = false; var readable = complete;
            foreach (var dir in directories)
            {
                try
                {
                    var path = Path.Combine(dir, "deleted_" + chat.Id.ToLowerInvariant());
                    // Desktop writes tombstones for the UI ID AND every unshared local transcript ID.
                    // Re-import removes them. No mapping from titles or missing JSONL is necessary.
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) { readable = false; continue; }
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length is < 12 or > 32) { readable = false; continue; }
                    using var reader = new StreamReader(fs);
                    var text = reader.ReadToEnd().Trim();
                    if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var at) ||
                        at < 1_000_000_000_000 || at > now.AddMinutes(1).ToUnixTimeMilliseconds()) { readable = false; continue; }
                    found = true;
                }
                catch (FileNotFoundException) { }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { readable = false; }
            }
            var key = ChatLifecycleSnapshot.Key(ChatPlatform.Claude, chat.Id);
            if (found) next.Add(key);
            else if (readable) next.Remove(key);
            anyFailure |= !readable;
        }
        if (anyFailure) warnings.Add("Claude 删除状态暂不可读");
    }
}
