namespace QuotaWidget.Core;

/// <summary>Display conversion only. Stored observations and quota rings stay in native units.</summary>
public static class QuotaUnits
{
    // Verified 2026-10-01: Max Fable allowance consumes up to half of the regular weekly limit.
    // https://support.claude.com/en/articles/15424964-claude-fable-models-on-your-plan
    // Generic Team/Enterprise labels do not establish the required premium seat entitlement.
    public static double? FableToClaude(string? plan) => plan is not null &&
        (plan.Equals("Max", StringComparison.OrdinalIgnoreCase) || plan.StartsWith("Max (", StringComparison.OrdinalIgnoreCase))
        ? .5 : null;

    public static SeriesData Scale(SeriesData source, double factor) => new()
    {
        Key = source.Key,
        Segments = source.Segments.Select(s => new RateSegment
        {
            Start = s.Start, End = s.End, Delta = s.Delta * factor, Smooth = s.Smooth * factor,
            Issue = s.Issue, Label = s.Label, Group = s.Group, SmoothMinutes = s.SmoothMinutes,
            CounterResetOnly=s.CounterResetOnly,StartsAtCapacity=s.StartsAtCapacity,
        }).ToList(),
    };
}
