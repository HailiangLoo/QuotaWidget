using System.Globalization;
using System.Text.Json;

namespace QuotaWidget.Core;

/// <summary>Observed account balance decreases, in credits, never subscription points.
/// This is net change, not a billing ledger: expiry or concurrent grants can affect it.</summary>
public static class CreditsUsage
{
    public static CreditBalance? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("hasCredits", out var has) || has.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !value.TryGetProperty("unlimited", out var unlimited) || unlimited.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
        decimal? balance = null;
        if (value.TryGetProperty("balance", out var raw) && raw.ValueKind != JsonValueKind.Null)
        {
            decimal amount;
            var valid = raw.ValueKind == JsonValueKind.Number ? raw.TryGetDecimal(out amount) :
                decimal.TryParse(raw.ValueKind == JsonValueKind.String ? raw.GetString() : null,
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
            if (!valid || amount < 0) return null;
            balance = amount;
        }
        return new(has.GetBoolean(), unlimited.GetBoolean(), balance);
    }

    public static SeriesData Build(IReadOnlyList<HistoryRecord> records, IReadOnlyList<AppEvent> events)
    {
        var result = new SeriesData { Key = SeriesKey.Credits, Segments = [] };
        var group = 0;
        for (var i = 1; i < records.Count; i++)
        {
            var a = records[i - 1]; var b = records[i];
            if (b.T <= a.T) continue;
            var ca = a.Snapshot.Credits; var cb = b.Snapshot.Credits;
            var issue = SegmentIssue.None; string? label = null; double delta = 0;
            if (a.SourceId != b.SourceId || a.ProfileKey != b.ProfileKey || a.PlanLabel != b.PlanLabel)
            { issue = SegmentIssue.Gap; label = "账号或套餐变化"; }
            else if (ca is not { Unlimited: false, Balance: not null } || cb is not { Unlimited: false, Balance: not null })
            { issue = SegmentIssue.Missing; label = "余额未提供"; }
            else if (cb.Balance > ca.Balance)
            { issue = SegmentIssue.Reset; label = "余额增加"; }
            else if ((b.T - a.T).TotalSeconds > RateEngine.GapFactor * Math.Max(a.PollSeconds, b.PollSeconds) ||
                RateEngine.ClassifyEvents(events, a.T, b.T) is { })
            { issue = SegmentIssue.Gap; label = "缺口"; }
            else delta = (double)(ca.Balance.Value - cb.Balance.Value);
            if (issue != SegmentIssue.None) group++;
            result.Segments.Add(new() { Start = a.T, End = b.T, Delta = delta, Issue = issue, Label = label, Group = group });
        }
        return result;
    }
}
