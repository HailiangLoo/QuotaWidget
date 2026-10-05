using System.Globalization;
using System.Text.Json;

namespace QuotaWidget.Core;

public sealed record ParsedUsage(QuotaLimits Limits, bool OutOfRange);

/// <summary>
/// Maps the plan-usage body (as the CLI returns it) to the three buckets. The server's limits[]
/// rows are authoritative (kind session / weekly_all / weekly_scoped + model display name);
/// legacy five_hour / seven_day and the CLI's model_scoped projection are fallbacks. A bucket
/// the body does not carry, or carries with an impossible value, stays null — never 0.
/// </summary>
public static class UsageParser
{
    public static ParsedUsage Parse(JsonElement root, string fableName)
    {
        QuotaLimit? five = null, week = null, fable = null;
        var outOfRange = false;
        QuotaLimit? Take(double? percent, DateTimeOffset? resets)
        {
            if (percent is null) return null;
            var l = Limit(percent.Value, resets);
            if (l is null) outOfRange = true;
            return l;
        }
        bool IsFable(string? name) => name is not null && string.Equals(name.Trim(), fableName, StringComparison.OrdinalIgnoreCase);

        if (root.TryGetProperty("limits", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var kind = Str(row, "kind");
                if (kind is null || Num(row, "percent") is not { } percent) continue;
                var resets = Time(row, "resets_at");
                switch (kind)
                {
                    case "session" when five is null:
                        five = Take(percent, resets);
                        break;
                    case "weekly_all" when week is null:
                        week = Take(percent, resets);
                        break;
                    case "weekly_scoped" when fable is null:
                        var model = row.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object &&
                                    scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object
                            ? Str(m, "display_name") : null;
                        if (IsFable(model)) fable = Take(percent, resets);
                        break;
                }
            }
        }

        five ??= Legacy(root, "five_hour", Take);
        week ??= Legacy(root, "seven_day", Take);
        if (fable is null && root.TryGetProperty("model_scoped", out var scoped) && scoped.ValueKind == JsonValueKind.Array)
            foreach (var row in scoped.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Object && IsFable(Str(row, "display_name")))
                {
                    fable = Take(Num(row, "utilization"), Time(row, "resets_at"));
                    break;
                }
        return new ParsedUsage(new QuotaLimits(five, week, fable), outOfRange);
    }

    static QuotaLimit? Legacy(JsonElement root, string name, Func<double?, DateTimeOffset?, QuotaLimit?> take)
    {
        if (!root.TryGetProperty(name, out var o) || o.ValueKind != JsonValueKind.Object) return null;
        return take(Num(o, "utilization"), Time(o, "resets_at"));
    }

    /// <summary>
    /// A bucket with an absolute reset instant is treated as a fixed window (usage only grows
    /// until that instant); the rate engine verifies this at run time and refuses intervals
    /// where it does not hold. The source carries no window id, so none is invented here.
    /// Values outside 0–100 are rejected rather than clamped.
    /// </summary>
    public static QuotaLimit? Limit(double percent, DateTimeOffset? resetsAt)
    {
        if (!double.IsFinite(percent) || percent < 0 || percent > 100) return null;
        return new QuotaLimit(percent, resetsAt, null, resetsAt is null ? WindowModes.Unknown : WindowModes.Fixed);
    }

    static string? Str(JsonElement o, string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? Num(JsonElement o, string n) =>
        o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    static DateTimeOffset? Time(JsonElement o, string n)
    {
        if (!o.TryGetProperty(n, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            return t.ToLocalTime();
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var secs) && secs > 1e9)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(secs < 1e11 ? secs * 1000 : secs)).ToLocalTime();
        return null;
    }
}
