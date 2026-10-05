namespace QuotaWidget.Core;

/// <summary>Display retention only: the journal is never deleted or rewritten by this policy.</summary>
public static class ChatListPolicy
{
    public static readonly TimeSpan CompactRetention = TimeSpan.FromHours(24);
    public static DateTimeOffset LastActivity(ChatCacheEntry e) =>
        e.Compacted && e.CompactedAt is { } c && c > (e.ActivityAt ?? e.RequestAt) ? c : e.ActivityAt ?? e.RequestAt;
    public static bool RecentCompact(ChatCacheEntry e, DateTimeOffset now) =>
        e.Compacted && e.CompactedAt is { } c && c <= now && now - c < CompactRetention;

    public static IReadOnlyList<ChatCacheEntry> Merge(IEnumerable<ChatCacheEntry> live, IEnumerable<ChatCacheEntry> retained, DateTimeOffset now,
        ChatLifecycleSnapshot? lifecycle = null)
    {
        var latest = new Dictionary<string, ChatCacheEntry>();
        foreach (var e in retained.OrderBy(LastActivity))
            latest[e.Platform + ":" + e.Id] = e with { Running = false, ActivityUncertain = e.WorkPending };
        foreach (var e in live)
        {
            var key = e.Platform + ":" + e.Id;
            if (!latest.TryGetValue(key, out var old) || LastActivity(e) >= LastActivity(old)) latest[key] = e;
        }
        return latest.Values.Where(e => (!e.Compacted || RecentCompact(e, now)) && lifecycle?.IsHidden(e) != true)
            .OrderByDescending(LastActivity).ToArray();
    }

    public static (string Number, string Unit, string? Minutes) CompactAge(DateTimeOffset at, DateTimeOffset now)
    {
        var minutes = (int)Math.Max(0, Math.Floor((now - at).TotalMinutes));
        return minutes < 60 ? (minutes.ToString(), "m", null)
            : ((minutes / 60).ToString(), "h", minutes % 60 == 0 ? null : (minutes % 60).ToString("00"));
    }
}
