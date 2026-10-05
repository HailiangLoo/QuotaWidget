using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QuotaWidget.Core;

public static class EventTypes
{
    public const string AppStart = "app_start";
    public const string AppExit = "app_exit";
    public const string Suspend = "suspend";   // from the OS power broadcast, nothing else
    public const string Resume = "resume";
    public const string CollectFail = "collect_fail";
    public const string MonitorPause = "monitor_pause";
    public const string MonitorResume = "monitor_resume";
}

public sealed record AppEvent(DateTimeOffset T, string Type, string? Status = null, string? ErrorCode = null, string? SourceId = null, string? ProfileKey = null);

/// <summary>Machine-wide events that explain gaps: power transitions, app runs, failed attempts.</summary>
public sealed class EventLog
{
    readonly DataPaths _paths;
    readonly object _gate = new();

    public EventLog(DataPaths paths) => _paths = paths;

    // JSONL stores milliseconds. Use the same precision in memory so a live event and
    // its disk copy have one identity; retain every other field (including the source).
    internal static AppEvent AtStoredPrecision(AppEvent e) => e with
    {
        T = new DateTimeOffset(e.T.Ticks - e.T.Ticks % TimeSpan.TicksPerMillisecond, e.T.Offset),
    };

    internal static List<AppEvent> Merge(IEnumerable<AppEvent> stored, IEnumerable<AppEvent> live) =>
        stored.Concat(live).Select(AtStoredPrecision).Distinct().OrderBy(e => e.T).ToList();

    public void Append(AppEvent e)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_paths.EventsDir);
            var file = Path.Combine(_paths.EventsDir, e.T.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, SnapshotJson.Plain))
            {
                w.WriteStartObject();
                w.WriteString("t", SnapshotJson.FormatTimestamp(e.T));
                w.WriteString("type", e.Type);
                if (e.Status is not null) w.WriteString("status", e.Status);
                if (e.ErrorCode is not null) w.WriteString("errorCode", e.ErrorCode);
                if (e.SourceId is not null) w.WriteString("sourceId", e.SourceId);
                if (e.ProfileKey is not null) w.WriteString("profileKey", e.ProfileKey);
                w.WriteEndObject();
            }
            var line = Encoding.UTF8.GetString(ms.ToArray()) + "\n";
            using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = Encoding.UTF8.GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
    }

    public List<AppEvent> Load(DateTimeOffset since, DateTimeOffset? until = null)
    {
        var list = new List<AppEvent>();
        if (!Directory.Exists(_paths.EventsDir)) return list;
        var sinceDate = since.ToLocalTime().Date.AddDays(-1);
        foreach (var file in Directory.EnumerateFiles(_paths.EventsDir, "*.jsonl"))
        {
            if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day < sinceDate || until is { } edge && day>edge.ToLocalTime().Date.AddDays(1)) continue;
            var text = AtomicFile.TryReadAllText(file);
            if (text is null) continue;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var el = doc.RootElement;
                    string? S(string n) => el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var t = DateTimeOffset.Parse(S("t")!, CultureInfo.InvariantCulture);
                    if (t < since || until is { } limit && t > limit) continue;
                    list.Add(new AppEvent(t, S("type")!, S("status"), S("errorCode"), S("sourceId"), S("profileKey")));
                }
                catch { /* torn line after a crash */ }
            }
        }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        return list;
    }
}
