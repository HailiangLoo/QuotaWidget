namespace QuotaWidget.Core;

/// <summary>Recorded work-session edges constrain display smoothing, not quota accounting.</summary>
public static class SessionTrendBoundaries
{
    public static IReadOnlyList<DateTimeOffset> Build(IEnumerable<ChatSession> sessions, DateTimeOffset now) =>
        sessions.Where(s => s.Start <= s.End && s.Start <= now)
            .SelectMany(s => now - s.End >= ChatSessionHistory.BreakAfter
                ? new[] { s.Start, s.End } : new[] { s.Start })
            .Distinct().Order().ToArray();
}
