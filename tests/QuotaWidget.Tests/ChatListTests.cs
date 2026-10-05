using QuotaWidget.Core;

static class ChatListTests
{
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        var now = new DateTimeOffset(2026, 10, 1, 16, 0, 0, TimeSpan.Zero);
        void Test(string name, Action body) => tests.Add(("chat list: " + name, () => { body(); return Task.CompletedTask; }));
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        ChatCacheEntry Compact(string id, double hours) => new(ChatPlatform.Claude, id, id, now.AddHours(-hours - 3), 60, "local", false,
            Compacted: true, CompactedAt: now.AddHours(-hours), ActivityAt: now.AddHours(-hours));

        Test("24-hour boundary uses compact time, never old request age or poll time", () =>
        {
            var entries = new[] { Compact("old", 24), Compact("recent", 23.999), Compact("fresh", 0), Compact("future", -1) };
            var shown = ChatListPolicy.Merge(entries, [], now);
            Check(shown.Select(e => e.Id).SequenceEqual(new[] { "fresh", "recent" }), "wrong 24h cutoff or order");
            Check(entries[0].Compacted && entries.Length == 4, "display filtering mutated retained records");
        });
        Test("real follow-up overrides the archived compact; old late output cannot restore it", () =>
        {
            var old = Compact("a", 2);
            var active = old with { Compacted = false, RequestAt = now.AddMinutes(-1), ActivityAt = now, Running = true };
            var merged = ChatListPolicy.Merge([active], [old], now).Single();
            Check(merged.Running && !merged.Compacted, "compact shadowed new activity");
            merged = ChatListPolicy.Merge([old], [active], now).Single();
            Check(!merged.Compacted && !merged.Running && merged.ActivityUncertain, "stale live tail resurrected compact/work");
        });
        Test("elapsed labels are from compact time and retain minutes after one hour", () =>
        {
            Check(ChatListPolicy.CompactAge(now.AddMinutes(-59.9), now) == ("59", "m", null), "minute rounding");
            Check(ChatListPolicy.CompactAge(now.AddMinutes(-65), now) == ("1", "h", "05"), "hour/minute label");
            Check(ChatListPolicy.CompactAge(now.AddHours(-23), now) == ("23", "h", null), "exact hour label");
        });
    }
}
