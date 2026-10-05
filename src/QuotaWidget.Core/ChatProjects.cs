using System.Text.Json;

namespace QuotaWidget.Core;

/// <summary>Only local project labels and paths, never credentials. No chat bodies retained.</summary>
public sealed class ChatProjects(string codexHome)
{
    readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _threads = new(StringComparer.Ordinal);
    (long, DateTime)? _stamp;
    static string Clean(string name) => new(name.Where(c => !char.IsControl(c)).Take(100).ToArray());
    static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    public void Refresh()
    {
        var path = Path.Combine(codexHome, ".codex-global-state.json");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 4 * 1024 * 1024 || _stamp == (file.Length, file.LastWriteTimeUtc)) return;
            var text = AtomicFile.TryReadAllText(path);
            if (text is null) return;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var projects = new Dictionary<string, string>(StringComparer.Ordinal);
            _roots.Clear(); _threads.Clear();
            if (root.TryGetProperty("local-projects", out var local) && local.ValueKind == JsonValueKind.Object)
                foreach (var item in local.EnumerateObject().Take(2048))
                {
                    var value = item.Value;
                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                    var label = Clean(name.GetString()!);
                    projects[item.Name] = label;
                    if (value.TryGetProperty("rootPaths", out var paths) && paths.ValueKind == JsonValueKind.Array)
                        foreach (var r in paths.EnumerateArray().Take(64)) if (r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } p) _roots[Normalize(p)] = label;
                }
            if (root.TryGetProperty("electron-workspace-root-labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
                foreach (var item in labels.EnumerateObject().Take(2048))
                    if (item.Value.ValueKind == JsonValueKind.String && item.Value.GetString() is { Length: > 0 } name) _roots[Normalize(item.Name)] = Clean(name);
            if (root.TryGetProperty("thread-project-assignments", out var assignments) && assignments.ValueKind == JsonValueKind.Object)
                foreach (var item in assignments.EnumerateObject().Take(4096))
                    if (item.Value.ValueKind == JsonValueKind.Object && item.Value.TryGetProperty("projectId", out var id) && id.ValueKind == JsonValueKind.String && projects.TryGetValue(id.GetString()!, out var label)) _threads[item.Name] = label;
            _stamp = (file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
    }

    public string? Name(ChatPlatform platform, string threadId, string? cwd)
    {
        if (platform == ChatPlatform.Codex && _threads.TryGetValue(threadId, out var assigned)) return assigned;
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var path = Normalize(cwd);
        foreach (var root in _roots.OrderByDescending(x => x.Key.Length))
            if (path.Equals(root.Key, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.Key + "/", StringComparison.OrdinalIgnoreCase)) return root.Value;
        var leaf = path.Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(leaf) || leaf.EndsWith(':') ? null : Clean(leaf);
    }
}
