namespace QuotaWidget.Core;

public sealed record HistoryBounds(DateTimeOffset First, DateTimeOffset Last)
{
    public TimeSpan Span => Last - First;
    public HistoryBounds Include(DateTimeOffset t) => new(t < First ? t : First, t > Last ? t : Last);
}

public static class ChartRanges
{
    // Zero means every recorded day of the current account/plan, not the last 24 hours.
    public const int All = 0;
    public static int[] Available(params HistoryBounds?[] histories)
    {
        var span = histories.Where(b => b is not null).Select(b => b!.Span).DefaultIfEmpty(TimeSpan.Zero).Max();
        // Offer the next longer view once a day is available, even before the entire
        // three-day window has filled. Its missing prefix is never fabricated.
        return WidgetSettings.RangeChoices.Where(m => m == All || TimeSpan.FromMinutes(m==4320?1440:m) <= span).ToArray();
    }
    public static int Select(int preferred, IReadOnlyCollection<int> available) => available.Contains(preferred) ? preferred : All;
    public static string Label(int minutes) => minutes == All ? Loc.T("全部") : Loc.T("近") + WidgetModel.RangeLabel(minutes);
}
