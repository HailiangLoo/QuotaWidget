using System.Text.Json;

namespace QuotaWidget.Core;

/// <summary>Only local project labels and paths, never credentials. No chat bodies retained.</summary>
public sealed class ChatProjects(string codexHome)
{
    // A path can belong to several projects. Null marks conflicting inferred labels;
    // it must not be resolved by enumeration order or by a broader ancestor root.
    Dictionary<string, string?> _roots = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _threads = new(StringComparer.Ordinal);
    (long, DateTime)? _stamp;
    static string Clean(string name) => new(name.Where(c => !char.IsControl(c)).Take(100).ToArray());
    static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
    static string? DirectoryName(string path)
    {
        var leaf = path.Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(leaf) || leaf.EndsWith(':') ? null : Clean(leaf);
    }
    void Clear() { _roots.Clear(); _threads.Clear(); _stamp = null; }

    public void Refresh()
    {
        var path = Path.Combine(codexHome, ".codex-global-state.json");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 4 * 1024 * 1024) { Clear(); return; }
            if (_stamp == (file.Length, file.LastWriteTimeUtc)) return;
            var text = AtomicFile.TryReadAllText(path);
            if (text is null) { Clear(); return; }
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var projects = new Dictionary<string, string>(StringComparer.Ordinal);
            var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var threads = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("local-projects", out var local) && local.ValueKind == JsonValueKind.Object)
                foreach (var item in local.EnumerateObject().Take(2048))
                {
                    var value = item.Value;
                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                    var label = Clean(name.GetString()!);
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    projects[item.Name] = label;
                    if (value.TryGetProperty("rootPaths", out var paths) && paths.ValueKind == JsonValueKind.Array)
                        foreach (var r in paths.EnumerateArray().Take(64))
                            if (r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } p)
                            {
                                var key = Normalize(p);
                                roots[key] = roots.TryGetValue(key, out var old) && old != label ? null : label;
                            }
                }
            if (root.TryGetProperty("electron-workspace-root-labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
                foreach (var item in labels.EnumerateObject().Take(2048))
                    if (item.Value.ValueKind == JsonValueKind.String && item.Value.GetString() is { Length: > 0 } name && !string.IsNullOrWhiteSpace(Clean(name)))
                        roots[Normalize(item.Name)] = Clean(name); // An explicit path label is not an inferred project assignment.
            if (root.TryGetProperty("thread-project-assignments", out var assignments) && assignments.ValueKind == JsonValueKind.Object)
                foreach (var item in assignments.EnumerateObject().Take(4096))
                    if (item.Value.ValueKind == JsonValueKind.Object && item.Value.TryGetProperty("projectId", out var id) && id.ValueKind == JsonValueKind.String && projects.TryGetValue(id.GetString()!, out var label)) threads[item.Name] = label;
            _roots = roots; _threads = threads;
            _stamp = (file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { Clear(); }
    }

    public string? Name(ChatPlatform platform, string threadId, string? cwd)
    {
        if (platform == ChatPlatform.Codex && _threads.TryGetValue(threadId, out var assigned)) return assigned;
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var path = Normalize(cwd);
        foreach (var root in _roots.OrderByDescending(x => x.Key.Length))
            if (path.Equals(root.Key, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.Key + "/", StringComparison.OrdinalIgnoreCase))
                return root.Value ?? DirectoryName(path[..root.Key.Length]); // Use the log's casing, not whichever project was read first.
        return DirectoryName(path);
    }
}
