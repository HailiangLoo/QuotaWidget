using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QuotaWidget.Core;

/// <summary>One stored observation. Only successful (ok/partial) snapshots become history.</summary>
public sealed record HistoryRecord(
    string SourceId,
    string ProfileKey,
    string? PlanLabel,
    DateTimeOffset AttemptedAt,
    string Status,
    int PollSeconds,
    QuotaSnapshot Snapshot)
{
    public DateTimeOffset T => Snapshot.ObservedAt;
    public string Key => SourceId + "|" + ProfileKey + "|" + Snapshot.Id;

    public static HistoryRecord FromEnvelope(LatestEnvelope e) =>
        new(e.SourceId, e.ProfileKey, e.PlanLabel, e.AttemptedAt, e.Status, e.EffectivePollIntervalSeconds, e.Snapshot!);
}

public enum AppendResult { Added, Duplicate, Conflict, Rejected, TooOld }

/// <summary>
/// Daily JSONL files, one directory per source and profile (account + plan), so a
/// different account or plan never mixes into the same curve.
/// </summary>
public sealed class HistoryStore
{
    readonly DataPaths _paths;
    readonly bool _demoMode;
    readonly Dictionary<string, (string Canon, DateTimeOffset T)> _index = new(); // key -> canonical snapshot
    readonly HashSet<string> _indexedProfiles = new();

    public HistoryStore(DataPaths paths, bool demoMode)
    {
        _paths = paths;
        _demoMode = demoMode;
    }

    public int LoadedDays { get; set; } = 8;
    public int SkippedLines { get; private set; }
    public string? LastError { get; private set; }

    public static bool IsDemoSource(string sourceId) => sourceId.StartsWith("demo", StringComparison.OrdinalIgnoreCase);

    public string ProfileDir(string sourceId, string profileKey) =>
        Path.Combine(_paths.HistoryDir, Sanitize(sourceId), Sanitize(profileKey));

    static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var r = sb.ToString().Trim('.');
        return r.Length == 0 ? "_" : r;
    }

    public List<HistoryRecord> Load(string sourceId, string profileKey, DateTimeOffset since, bool index = true, DateTimeOffset? until = null)
    {
        var list = new List<HistoryRecord>();
        var dir = ProfileDir(sourceId, profileKey);
        if (index) _indexedProfiles.Add(sourceId + "|" + profileKey);
        if (!Directory.Exists(dir)) return list;
        var sinceDate = since.ToLocalTime().Date.AddDays(-1);
        var seen = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            if (day < sinceDate || until is { } edge && day > edge.ToLocalTime().Date.AddDays(1)) continue;
            foreach (var line in ReadLines(file))
            {
                var rec = ParseLine(line);
                if (rec is null) { SkippedLines++; continue; }
                if (rec.SourceId != sourceId || rec.ProfileKey != profileKey) { SkippedLines++; continue; }
                var canon = SnapshotJson.Canonical(rec.Snapshot);
                if (rec.T < since || until is { } limit && rec.T > limit) continue;
                if (_index.TryGetValue(rec.Key, out var existing) && existing.Canon != canon)
                {
                    SkippedLines++; // same id, different payload: keep the first one seen
                    continue;
                }
                if (index) _index[rec.Key] = (canon, rec.T);
                if (seen.Add(rec.Key)) list.Add(rec);
            }
        }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        return list;
    }

    /// <summary>Read just the first and last nonempty day. No permanent dedupe entries or history load.</summary>
    public HistoryBounds? Bounds(string sourceId, string profileKey, DateTimeOffset now)
    {
        var dir = ProfileDir(sourceId, profileKey);
        if (!Directory.Exists(dir)) return null;
        var files = Directory.EnumerateFiles(dir, "*.jsonl")
            .Where(f => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        DateTimeOffset? Edge(IEnumerable<string> ordered, bool first)
        {
            foreach (var file in ordered)
            {
                var times = ReadLines(file).Select(ParseLine)
                    .Where(r => r is not null && r.SourceId == sourceId && r.ProfileKey == profileKey && r.T <= now)
                    .Select(r => r!.T).ToArray();
                if (times.Length > 0) return first ? times.Min() : times.Max();
            }
            return null;
        }
        var a = Edge(files, true); var b = Edge(files.Reverse(), false);
        return a is { } start && b is { } end ? new(start, end) : null;
    }

    static IEnumerable<string> ReadLines(string file)
    {
        string? text = AtomicFile.TryReadAllText(file);
        if (text is null) yield break;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) yield return line;
        }
    }

    /// <summary>
    /// Appends one observation. Snapshots older than the retained window are refused: their ids
    /// are no longer indexed, so a duplicate could not be detected.
    /// </summary>
    public AppendResult Append(HistoryRecord rec, DateTimeOffset? now = null)
    {
        LastError = null;
        var cutoff = (now ?? DateTimeOffset.Now).AddDays(-LoadedDays);
        if (rec.T < cutoff)
        {
            LastError = "snapshot older than the retained window";
            return AppendResult.TooOld;
        }
        if (IsDemoSource(rec.SourceId) != _demoMode)
        {
            LastError = _demoMode ? "real data refused in demo store" : "demo data refused in real history";
            return AppendResult.Rejected;
        }
        var profile = rec.SourceId + "|" + rec.ProfileKey;
        if (!_indexedProfiles.Contains(profile))
            Load(rec.SourceId, rec.ProfileKey, cutoff);

        var canon = SnapshotJson.Canonical(rec.Snapshot);
        if (_index.TryGetValue(rec.Key, out var existing))
        {
            if (existing.Canon == canon) return AppendResult.Duplicate;
            LastError = "snapshot id reused with different content: " + rec.Snapshot.Id;
            return AppendResult.Conflict;
        }

        var dir = ProfileDir(rec.SourceId, rec.ProfileKey);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, rec.T.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        // A crash can leave a line without its newline; start fresh so the next line stays parseable.
        var line = (EndsWithNewline(file) ? "" : "\n") + FormatLine(rec) + "\n";
        using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        _index[rec.Key] = (canon, rec.T);
        return AppendResult.Added;
    }

    /// <summary>Drops dedupe entries older than the cutoff so a long-running process stays bounded.</summary>
    public void Prune(DateTimeOffset cutoff)
    {
        foreach (var key in _index.Where(kv => kv.Value.T < cutoff).Select(kv => kv.Key).ToList()) _index.Remove(key);
    }

    public int IndexCount => _index.Count;

    static bool EndsWithNewline(string file)
    {
        if (!File.Exists(file)) return true;
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0) return true;
        fs.Seek(-1, SeekOrigin.End);
        return fs.ReadByte() == '\n';
    }

    public static string FormatLine(HistoryRecord r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, SnapshotJson.Plain))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("sourceId", r.SourceId);
            w.WriteString("profileKey", r.ProfileKey);
            if (r.PlanLabel is null) w.WriteNull("planLabel"); else w.WriteString("planLabel", r.PlanLabel);
            w.WriteString("attemptedAt", SnapshotJson.FormatTimestamp(r.AttemptedAt));
            w.WriteString("status", r.Status);
            w.WriteNumber("pollSec", r.PollSeconds);
            w.WritePropertyName("snapshot");
            SnapshotJson.WriteSnapshot(w, r.Snapshot);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static HistoryRecord? ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var el = doc.RootElement;
            if (el.GetProperty("v").GetInt32() != 1) return null;
            var errors = new List<string>();
            var snap = SnapshotJson.ParseSnapshot(el.GetProperty("snapshot"), "", errors);
            if (snap is null) return null;
            var status = el.GetProperty("status").GetString()!;
            if (!Statuses.HasSnapshot(status)) return null;
            var plan = el.TryGetProperty("planLabel", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            return new HistoryRecord(
                el.GetProperty("sourceId").GetString()!,
                el.GetProperty("profileKey").GetString()!,
                plan,
                DateTimeOffset.Parse(el.GetProperty("attemptedAt").GetString()!, CultureInfo.InvariantCulture),
                status,
                el.GetProperty("pollSec").GetInt32(),
                snap);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>CSV with full dates and offsets; missing buckets stay empty, not zero.</summary>
    public static string ToCsv(IEnumerable<HistoryRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("observedAt,attemptedAt,status,pollSec,sourceId,profileKey,fiveHourUsed,fiveHourResetsAt,allWeekUsed,allWeekResetsAt,fableWeekUsed,fableWeekResetsAt,snapshotId");
        static string N(QuotaLimit? l) => l is null ? "" : l.UsedPercent.ToString(CultureInfo.InvariantCulture);
        static string R(QuotaLimit? l) => l?.ResetsAt is { } t ? SnapshotJson.FormatTimestamp(t) : "";
        foreach (var r in records)
        {
            var l = r.Snapshot.Limits;
            sb.Append(SnapshotJson.FormatTimestamp(r.T)).Append(',')
              .Append(SnapshotJson.FormatTimestamp(r.AttemptedAt)).Append(',')
              .Append(r.Status).Append(',').Append(r.PollSeconds).Append(',')
              .Append(r.SourceId).Append(',').Append(r.ProfileKey).Append(',')
              .Append(N(l.FiveHour)).Append(',').Append(R(l.FiveHour)).Append(',')
              .Append(N(l.AllWeek)).Append(',').Append(R(l.AllWeek)).Append(',')
              .Append(N(l.FableWeek)).Append(',').Append(R(l.FableWeek)).Append(',')
              .Append(r.Snapshot.Id).AppendLine();
        }
        return sb.ToString();
    }
}
