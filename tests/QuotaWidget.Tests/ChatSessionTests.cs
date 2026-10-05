using QuotaWidget.Core;

static class ChatSessionTests
{
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        var t = new DateTimeOffset(2026, 10, 1, 23, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 1)));
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        void Test(string name, Action<string> body) => tests.Add(("chat sessions: " + name, () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "qw-session-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try { body(root); } finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }));
        ChatCacheEntry Entry(string id, DateTimeOffset at, bool compacted = false) =>
            new(ChatPlatform.Claude, id, "chat " + id, at, 60, "local metadata", false, "project", CompactedAt: compacted ? at : null, Compacted: compacted, ActivityAt: at);

        Test("cross midnight and restart preserve one session; both dates find it", root =>
        {
            var store = new ChatSessionHistory(root);
            store.Capture([Entry("a", t)], t); store.Flush(t);
            var original = store.Current(t)!.Id;
            var restarted = new ChatSessionHistory(root);
            restarted.Capture([Entry("b", t.AddHours(2))], t.AddHours(2)); restarted.Flush(t.AddHours(2));
            var a = restarted.ForDay(t.LocalDateTime.Date).Single();
            var b = restarted.ForDay(t.AddDays(1).LocalDateTime.Date).Single();
            Check(a.Id == original && a.Id == b.Id && a.Chats.Count == 2, "midnight or restart split history");
        });
        Test("polling or old replay never extends a session; new activity after four hours starts another", root =>
        {
            var store = new ChatSessionHistory(root); var e = Entry("a", t);
            store.Capture([e], t); var id = store.Current(t)!.Id;
            store.Capture([e], t.AddHours(3));
            Check(store.Current(t.AddHours(3))!.End == t && store.Current(t.AddHours(4)) is null, "poll time masqueraded as use");
            store.Capture([Entry("a", t.AddHours(4))], t.AddHours(4));
            Check(store.Current(t.AddHours(4))!.Id != id, "idle gap did not split");
            store.Capture([e], t.AddHours(5));
            Check(store.Current(t.AddHours(5))!.End == t.AddHours(4), "old replay extended or rewound session");
        });
        Test("compact survives restart and later reuse, without duplicate markers or retaining bodies", root =>
        {
            var store = new ChatSessionHistory(root);
            store.Capture([Entry("a", t)], t);
            var compact = Entry("a", t.AddMinutes(10), true);
            store.Capture([compact], t.AddMinutes(10));
            store.Capture([compact], t.AddMinutes(11));
            var restarted = new ChatSessionHistory(root);
            var active = Entry("a", t.AddMinutes(20)) with { Running = true, CompactedAt = compact.CompactedAt };
            restarted.Capture([active], t.AddMinutes(20)); restarted.Flush(t.AddMinutes(20));
            var chat = restarted.ForDay(t.LocalDateTime.Date).Single().Chats.Single();
            Check(chat.Compactions.SequenceEqual(new[] { t.AddMinutes(10) }) && !chat.Last.Compacted, "reuse erased or duplicated compact");
            var json = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "chat-sessions")).Single());
            Check(!json.Contains("content") && !json.Contains("token") && !json.Contains("credential"), "journal stores more than metadata");
        });
        Test("returned UI copies cannot mutate saved metadata; corrupted days do not erase good history", root =>
        {
            var store = new ChatSessionHistory(root); store.Capture([Entry("a", t)], t);
            store.Current(t)!.Chats.Clear();
            Check(store.Current(t)!.Chats.Count == 1, "UI mutated journal");
            File.WriteAllText(Path.Combine(root, "chat-sessions", "99999999-broken.json"), "{");
            File.WriteAllText(Path.Combine(root, "chat-sessions", "99999998-null.json"), "{\"version\":1,\"id\":\"99999998-null\",\"start\":\"2026-10-01T00:00:00Z\",\"end\":\"2026-10-02T00:00:00Z\",\"chats\":[{\"last\":null}]}");
            var restarted = new ChatSessionHistory(root);
            Check(restarted.ForDay(t.LocalDateTime.Date).Count == 1, "torn file erased good data");
            restarted.Capture([Entry("bad", t.AddHours(2))], t);
            Check(restarted.Current(t)!.Chats.Count == 1, "future record accepted");
        });
        Test("cold scan orders request boundaries before later output and preserves an earlier compact", root =>
        {
            var store = new ChatSessionHistory(root);
            var late = Entry("long", t.AddHours(5)) with { RequestAt = t.AddHours(3), CompactedAt = t.AddHours(2), Running = true };
            store.Capture([Entry("first", t), Entry("short", t.AddHours(4.5)), late], t.AddHours(5));
            var sessions = store.ForDay(t.AddHours(5).LocalDateTime.Date);
            Check(sessions.Count == 1 && sessions[0].Start == t, "late response invented a four-hour gap");
            Check(sessions[0].Chats.Single(c => c.Last.Id == "long").Compactions.Contains(t.AddHours(2)), "earlier compact not recovered");
            store.Flush(t.AddHours(5)); var version = store.Version;
            store.Capture([late], t.AddHours(5).AddSeconds(5));
            Check(store.Version == version, "repeated scan rewrote history via synthetic boundary events");
        });
        Test("24h compact carry-over survives new sessions and restart but never deletes history", root =>
        {
            var store = new ChatSessionHistory(root);
            store.Capture([Entry("a", t, true)], t);
            store.Capture([Entry("b", t.AddHours(5))], t.AddHours(5)); store.Flush(t.AddHours(5));
            var restarted = new ChatSessionHistory(root);
            Check(restarted.RecentCompacted(t.AddHours(23)).Single().Id == "a", "old session compact vanished before 24h");
            Check(restarted.RecentCompacted(t.AddHours(24)).Count == 0, "compact remained after cutoff");
            Check(restarted.ForDay(t.LocalDateTime.Date).Single().Chats.Single().Last.Compacted, "history was erased");
            restarted.Capture([Entry("a", t.AddHours(5.5))], t.AddHours(5.5));
            Check(restarted.RecentCompacted(t.AddHours(6)).Count == 0, "reuse did not supersede prior session compact");
        });
    }
}
