using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuotaWidget.Core;

public static class WindowModes
{
    public const string Fixed = "fixed";
    public const string Rolling = "rolling";
    public const string Unknown = "unknown";
    public static readonly string[] All = [Fixed, Rolling, Unknown];
}

public static class Statuses
{
    public const string Ok = "ok";
    public const string Partial = "partial";
    public const string Error = "error";
    public const string AuthRequired = "auth_required";
    public const string RateLimited = "rate_limited";
    public static readonly string[] All = [Ok, Partial, Error, AuthRequired, RateLimited];
    public static bool HasSnapshot(string s) => s is Ok or Partial;
}

/// <summary>One quota bucket. UsedPercent is 0–100; a missing bucket is null, never 0.</summary>
public sealed record QuotaLimit(double UsedPercent, DateTimeOffset? ResetsAt, string? WindowId, string WindowMode);

public sealed record QuotaLimits(QuotaLimit? FiveHour, QuotaLimit? AllWeek, QuotaLimit? FableWeek);

public sealed record QuotaSnapshot(string Id, DateTimeOffset ObservedAt, QuotaLimits Limits);

/// <summary>The latest.json exchange file (schema v1).</summary>
public sealed record LatestEnvelope(
    int SchemaVersion,
    string SourceId,
    string ProfileKey,
    string? PlanLabel,
    DateTimeOffset AttemptedAt,
    string Status,
    int EffectivePollIntervalSeconds,
    int? RetryAfterSeconds,
    string? ErrorCode,
    QuotaSnapshot? Snapshot);

public static class SnapshotJson
{
    public const int SchemaVersion = 1;
    static readonly Regex TimestampPattern = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant);

    static readonly HashSet<string> RootProps = ["schemaVersion", "sourceId", "profileKey", "planLabel", "attemptedAt", "status", "effectivePollIntervalSeconds", "retryAfterSeconds", "errorCode", "snapshot"];
    static readonly HashSet<string> RootRequired = ["schemaVersion", "sourceId", "profileKey", "attemptedAt", "status", "effectivePollIntervalSeconds", "snapshot"];
    static readonly HashSet<string> SnapshotProps = ["id", "observedAt", "limits"];
    static readonly HashSet<string> LimitsProps = ["fiveHour", "allWeek", "fableWeek"];
    static readonly HashSet<string> LimitProps = ["usedPercent", "resetsAt", "windowId", "windowMode"];

    // Keep "+08:00" readable for other tools; the default encoder would write 002B.
    static readonly JavaScriptEncoder Relaxed = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    public static readonly JsonWriterOptions Plain = new() { Encoder = Relaxed };

    public static string FormatTimestamp(DateTimeOffset t) =>
        t.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    // ---------- parsing with validation ----------

    public static LatestEnvelope? ParseEnvelope(string json, List<string> errors)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { errors.Add("invalid JSON: " + e.Message); return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { errors.Add("root must be an object"); return null; }
            CheckProps(root, RootProps, RootRequired, "", errors);

            var version = ReadInt(root, "schemaVersion", "", errors);
            if (version is not null && version != SchemaVersion) errors.Add("schemaVersion must be 1");
            var sourceId = ReadString(root, "sourceId", "", 1, 80, false, errors);
            var profileKey = ReadString(root, "profileKey", "", 1, 80, false, errors);
            var planLabel = root.TryGetProperty("planLabel", out _) ? ReadString(root, "planLabel", "", 0, 40, true, errors) : null;
            var attemptedAt = ReadTimestamp(root, "attemptedAt", "", false, errors);
            var status = ReadString(root, "status", "", 1, 40, false, errors);
            if (status is not null && !Statuses.All.Contains(status)) errors.Add("status is not a known value");
            var poll = ReadInt(root, "effectivePollIntervalSeconds", "", errors);
            if (poll is not null && poll < 60) errors.Add("effectivePollIntervalSeconds must be >= 60");
            int? retryAfter = null;
            if (root.TryGetProperty("retryAfterSeconds", out var ra) && ra.ValueKind != JsonValueKind.Null)
            {
                retryAfter = ReadInt(root, "retryAfterSeconds", "", errors);
                if (retryAfter < 0) errors.Add("retryAfterSeconds must be >= 0");
            }
            var errorCode = root.TryGetProperty("errorCode", out _) ? ReadString(root, "errorCode", "", 0, 100, true, errors) : null;

            QuotaSnapshot? snapshot = null;
            if (root.TryGetProperty("snapshot", out var snapEl))
            {
                if (snapEl.ValueKind == JsonValueKind.Object) snapshot = ParseSnapshot(snapEl, "snapshot.", errors);
                else if (snapEl.ValueKind != JsonValueKind.Null) errors.Add("snapshot must be an object or null");
            }

            if (status is not null)
            {
                if (Statuses.HasSnapshot(status) && snapshot is null && !errors.Any(e => e.StartsWith("snapshot")))
                    errors.Add($"status {status} requires a snapshot");
                if (!Statuses.HasSnapshot(status) && snapEl.ValueKind == JsonValueKind.Object)
                    errors.Add($"status {status} must carry snapshot=null");
                if (snapshot is not null && status == Statuses.Ok &&
                    (snapshot.Limits.FiveHour is null || snapshot.Limits.AllWeek is null || snapshot.Limits.FableWeek is null))
                    errors.Add("status ok requires all three limits; use partial");
                if (snapshot is not null && status == Statuses.Partial &&
                    snapshot.Limits.FiveHour is null && snapshot.Limits.AllWeek is null && snapshot.Limits.FableWeek is null)
                    errors.Add("status partial requires at least one limit");
            }

            if (errors.Count > 0) return null;
            return new LatestEnvelope(version!.Value, sourceId!, profileKey!, string.IsNullOrEmpty(planLabel) ? null : planLabel,
                attemptedAt!.Value, status!, poll!.Value, retryAfter, errorCode, snapshot);
        }
    }

    public static QuotaSnapshot? ParseSnapshot(JsonElement el, string path, List<string> errors)
    {
        var before = errors.Count;
        CheckProps(el, SnapshotProps, SnapshotProps, path, errors);
        var id = ReadString(el, "id", path, 1, 150, false, errors);
        var observedAt = ReadTimestamp(el, "observedAt", path, false, errors);
        QuotaLimits? limits = null;
        if (el.TryGetProperty("limits", out var limEl))
        {
            if (limEl.ValueKind != JsonValueKind.Object) errors.Add(path + "limits must be an object");
            else
            {
                CheckProps(limEl, LimitsProps, LimitsProps, path + "limits.", errors);
                limits = new QuotaLimits(
                    ParseLimit(limEl, "fiveHour", path + "limits.", errors),
                    ParseLimit(limEl, "allWeek", path + "limits.", errors),
                    ParseLimit(limEl, "fableWeek", path + "limits.", errors));
            }
        }
        if (errors.Count > before || id is null || observedAt is null || limits is null) return null;
        return new QuotaSnapshot(id, observedAt.Value, limits);
    }

    static QuotaLimit? ParseLimit(JsonElement parent, string name, string path, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        var p = path + name + ".";
        if (el.ValueKind != JsonValueKind.Object) { errors.Add(p + " must be an object or null"); return null; }
        var before = errors.Count;
        CheckProps(el, LimitProps, LimitProps, p, errors);
        double? used = null;
        if (el.TryGetProperty("usedPercent", out var u))
        {
            if (u.ValueKind != JsonValueKind.Number || !u.TryGetDouble(out var d) || !double.IsFinite(d)) errors.Add(p + "usedPercent must be a number");
            else if (d < 0 || d > 100) errors.Add(p + "usedPercent must be within 0–100");
            else used = d;
        }
        var resetsAt = ReadTimestamp(el, "resetsAt", p, true, errors);
        var windowId = ReadString(el, "windowId", p, 1, 150, true, errors);
        var mode = ReadString(el, "windowMode", p, 1, 20, false, errors);
        if (mode is not null && !WindowModes.All.Contains(mode)) errors.Add(p + "windowMode is not a known value");
        if (errors.Count > before || used is null || mode is null) return null;
        return new QuotaLimit(used.Value, resetsAt, windowId, mode);
    }

    static void CheckProps(JsonElement el, HashSet<string> allowed, HashSet<string> required, string path, List<string> errors)
    {
        foreach (var prop in el.EnumerateObject())
            if (!allowed.Contains(prop.Name)) errors.Add($"{path}{prop.Name} is not allowed");
        foreach (var r in required)
            if (!el.TryGetProperty(r, out _)) errors.Add($"{path}{r} is required");
    }

    static int? ReadInt(JsonElement el, string name, string path, List<string> errors)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var i)) { errors.Add(path + name + " must be an integer"); return null; }
        return i;
    }

    static string? ReadString(JsonElement el, string name, string path, int min, int max, bool nullable, List<string> errors)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Null)
        {
            if (!nullable) errors.Add(path + name + " must not be null");
            return null;
        }
        if (v.ValueKind != JsonValueKind.String) { errors.Add(path + name + " must be a string"); return null; }
        var s = v.GetString()!;
        if (s.Length < min || s.Length > max) { errors.Add($"{path}{name} length must be {min}–{max}"); return null; }
        return s;
    }

    static DateTimeOffset? ReadTimestamp(JsonElement el, string name, string path, bool nullable, List<string> errors)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Null)
        {
            if (!nullable) errors.Add(path + name + " must not be null");
            return null;
        }
        if (v.ValueKind != JsonValueKind.String) { errors.Add(path + name + " must be a timestamp string"); return null; }
        var s = v.GetString()!;
        if (!TimestampPattern.IsMatch(s) ||
            !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
        {
            errors.Add(path + name + " must be ISO 8601 with a time zone");
            return null;
        }
        return t;
    }

    // ---------- writing ----------

    public static string WriteEnvelope(LatestEnvelope e)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = Relaxed }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", e.SchemaVersion);
            w.WriteString("sourceId", e.SourceId);
            w.WriteString("profileKey", e.ProfileKey);
            if (e.PlanLabel is null) w.WriteNull("planLabel"); else w.WriteString("planLabel", e.PlanLabel);
            w.WriteString("attemptedAt", FormatTimestamp(e.AttemptedAt));
            w.WriteString("status", e.Status);
            w.WriteNumber("effectivePollIntervalSeconds", e.EffectivePollIntervalSeconds);
            if (e.RetryAfterSeconds is null) w.WriteNull("retryAfterSeconds"); else w.WriteNumber("retryAfterSeconds", e.RetryAfterSeconds.Value);
            if (e.ErrorCode is null) w.WriteNull("errorCode"); else w.WriteString("errorCode", e.ErrorCode);
            w.WritePropertyName("snapshot");
            if (e.Snapshot is null) w.WriteNullValue(); else WriteSnapshot(w, e.Snapshot);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void WriteSnapshot(Utf8JsonWriter w, QuotaSnapshot s)
    {
        w.WriteStartObject();
        w.WriteString("id", s.Id);
        w.WriteString("observedAt", FormatTimestamp(s.ObservedAt));
        w.WritePropertyName("limits");
        w.WriteStartObject();
        WriteLimit(w, "fiveHour", s.Limits.FiveHour);
        WriteLimit(w, "allWeek", s.Limits.AllWeek);
        WriteLimit(w, "fableWeek", s.Limits.FableWeek);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    static void WriteLimit(Utf8JsonWriter w, string name, QuotaLimit? l)
    {
        w.WritePropertyName(name);
        if (l is null) { w.WriteNullValue(); return; }
        w.WriteStartObject();
        w.WriteNumber("usedPercent", l.UsedPercent);
        if (l.ResetsAt is null) w.WriteNull("resetsAt"); else w.WriteString("resetsAt", FormatTimestamp(l.ResetsAt.Value));
        if (l.WindowId is null) w.WriteNull("windowId"); else w.WriteString("windowId", l.WindowId);
        w.WriteString("windowMode", l.WindowMode);
        w.WriteEndObject();
    }

    /// <summary>Stable text form of a snapshot, used to detect two different payloads sharing one id.</summary>
    public static string Canonical(QuotaSnapshot s)
    {
        using var ms = new MemoryStream();
        static QuotaLimit? U(QuotaLimit? l) => l is null ? null : l with { ResetsAt = l.ResetsAt?.ToUniversalTime() };
        var normalized = new QuotaSnapshot(s.Id, s.ObservedAt.ToUniversalTime(),
            new QuotaLimits(U(s.Limits.FiveHour), U(s.Limits.AllWeek), U(s.Limits.FableWeek)));
        using (var w = new Utf8JsonWriter(ms, SnapshotJson.Plain)) WriteSnapshot(w, normalized);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
