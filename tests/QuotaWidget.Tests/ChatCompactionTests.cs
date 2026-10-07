using System.Text.Json;
using QuotaWidget.Core;

static class ChatCompactionTests
{
    static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    static string Line(double minute, string type, object? payload = null) =>
        JsonSerializer.Serialize(new { timestamp = T.AddMinutes(minute), type, payload });
    static string Usage(double minute, string id) => Line(minute, "token_usage_record",
        new { response_id = id, usage = new { input_tokens = 1000 } });
    static string TaskEvent(double minute, string type, string turn = "turn-a") =>
        Line(minute, "event_msg", new { type, turn_id = turn });
    static string Completed(double minute, double start = 1, string id = "compact-a", string turn = "turn-a", double? delivered = null) =>
        Line(delivered ?? minute, "event_msg", new { type = "item_completed", turn_id = turn,
            item = new { type = "ContextCompaction", id }, started_at_ms = T.AddMinutes(start).ToUnixTimeMilliseconds(),
            completed_at_ms = T.AddMinutes(minute).ToUnixTimeMilliseconds() });
    static string Count(double minute, long total, long input) => Line(minute, "event_msg", new { type = "token_count",
        info = new { total_token_usage = new { input_tokens = total }, last_token_usage = new { input_tokens = input } } });
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }

    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        void Test(string name, Action body) => tests.Add(("compact: " + name, () => { body(); return Task.CompletedTask; }));
        Test("small completion event alone recovers compacted status", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Codex, "a");
            s.Read(Completed(2)); s.Read(TaskEvent(2.01, "task_complete"));
            Check(s.View(T.AddMinutes(30)) is { Compacted: true, WorkPending: false, AwaitingUsage: false }, "completion ignored");
            Check(s.CompactedAt == T.AddMinutes(2) && s.RequestAt is null, "wrong boundary");
        });
        Test("post-compact zero-input totals are not a new cache request", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Codex, "a");
            s.Read(Count(1, 1000, 0)); Check(s.View(T.AddMinutes(1)) is null, "summary invented a request");
            s.Read(Count(2, 1200, 200)); Check(s.RequestAt == T.AddMinutes(2), "legacy positive usage lost");
            s.Read(Line(3, "compacted")); s.Read(Count(3.01, 1500, 0));
            Check(s.View(T.AddMinutes(4)) is { Compacted: true }, "summary restarted compacted cache");
            s.Read(Count(4, 1500, 200));
            Check(s.View(T.AddMinutes(4)) is { Compacted: true }, "unchanged total became a new request");
            s.Read(Count(5, 1700, 200)); Check(s.RequestAt == T.AddMinutes(5), "new real usage not restored");
        });
        Test("completion closes compact-only pending work but preserves real follow-ups", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Codex, "a");
            s.Read(TaskEvent(1, "task_started")); s.Read(Completed(2)); s.Read(TaskEvent(2.01, "task_complete"));
            Check(s.View(T.AddMinutes(3)) is { Compacted: true, AwaitingUsage: false }, "compact-only turn kept its pending input");
            s.Read(TaskEvent(4, "task_started", "turn-b")); s.Read(Usage(5, "new"));
            Check(s.View(T.AddMinutes(5)) is { Compacted: false, Running: true } && s.RequestAt == T.AddMinutes(4), "real follow-up hidden");
            s.Read(TaskEvent(6, "task_complete", "turn-b"));
            Check(s.View(T.AddMinutes(6)) is { Compacted: false, WorkPending: false }, "later completion restored old compact");
        });
        Test("automatic compact retains a pending request that actually continues", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Codex, "a");
            s.Read(TaskEvent(1, "task_started")); s.Read(Completed(2)); s.Read(Usage(3, "continued"));
            s.Read(TaskEvent(4, "task_complete"));
            Check(s.View(T.AddMinutes(5)) is { Compacted: false, WorkPending: false } && s.RequestAt == T.AddMinutes(1), "continuation erased or age renewed");
        });
        Test("two representations and duplicate completions do not reset subsequent input", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Codex, "a"); var root = Path.Combine(Path.GetTempPath(), "qw-compact-" + Guid.NewGuid().ToString("N"));
            try
            {
                var journal = new ChatSessionHistory(root);
                s.Read(Usage(1, "before")); s.Read(Line(2, "compacted")); journal.Capture([s.View(T.AddMinutes(2))!], T.AddMinutes(2));
                s.Read(Line(2.001, "response_item", new { type = "function_call_output" }));
                s.Read(Completed(2.002)); journal.Capture([s.View(T.AddMinutes(2.002))!], T.AddMinutes(2.002));
                Check(s.CompactedAt == T.AddMinutes(2) && s.RequestAt == T.AddMinutes(2.001), "acknowledgement cleared fresh input");
                s.Read(Completed(2.002)); s.Read(Usage(3, "after")); s.Read(Completed(2.002));
                Check(s.RequestAt == T.AddMinutes(2.001), "duplicate reset current request");
                Check(journal.Current(T.AddMinutes(3))!.Chats.Single().Compactions.Count == 1, "same compaction journaled twice");
                s.Read(Completed(5, 4, "compact-b"));
                Check(s.View(T.AddMinutes(5)) is { Compacted: true } && s.CompactedAt == T.AddMinutes(5), "second compact in same turn ignored");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        Test("unrelated output and late old-turn completion cannot compact a new turn", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Codex, "a");
            s.Read(TaskEvent(1, "task_started", "new"));
            s.Read(Line(2, "event_msg", new { type = "item_completed", item = new { type = "AgentMessage", text = "ContextCompaction" } }));
            s.Read(Completed(3, 0, turn: "old"));
            Check(s.CompactedAt is null && s.View(T.AddMinutes(3))!.Running, "non-current event compacted new work");
        });
        Test("delayed notification uses original time and cannot clear later usage", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Codex, "a"); s.Read(Usage(4, "new"));
            s.Read(Completed(2, delivered: 5));
            Check(s.RequestAt == T.AddMinutes(4) && !s.View(T.AddMinutes(5))!.Compacted, "delivery time replaced original completion time");
        });
        Test("oversized compact is recovered live and after restart through the real tail and journal", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "qw-compact-" + Guid.NewGuid().ToString("N"));
            var sessions = Path.Combine(root, "sessions"); Directory.CreateDirectory(sessions);
            var path = Path.Combine(sessions, "rollout-00000000-0000-0000-0000-000000000001.jsonl");
            try
            {
                File.WriteAllText(path, TaskEvent(0, "task_started") + "\n" + Usage(1, "before") + "\n");
                var monitor = new ChatCacheMonitor(root, Path.Combine(root, "claude")); monitor.Poll(T.AddMinutes(1));
                var oversized = Line(2, "compacted", new { replacement_history = new string('x', 2_658_000) });
                // A partial write followed by a >2 MB catch-up must recover on the small completion event.
                File.AppendAllText(path, oversized[..1_100_000]); monitor.Poll(T.AddMinutes(2));
                File.AppendAllText(path, oversized[1_100_000..] + "\n" + Count(2.001, 3000, 0) + "\n" + Completed(2.002) + "\n" + TaskEvent(2.003, "task_complete") + "\n");
                var live = monitor.Poll(T.AddMinutes(3)).Single();
                Check(live.Compacted && !live.WorkPending, "live oversized compact missed");
                // The same event can land in one write larger than the catch-up budget.
                var jumpPath = Path.Combine(root, "one-write.jsonl"); File.WriteAllText(jumpPath, Usage(1, "before") + "\n");
                var jump = new ChatCacheState(ChatPlatform.Codex, "jump"); var tail = new MetadataTail();
                tail.Read(jumpPath, jump.Read, () => jump.Gap(jump.LastObserved));
                File.AppendAllText(jumpPath, oversized + "\n" + Count(2.001, 3000, 0) + "\n" + Completed(2.002) + "\n" + TaskEvent(2.003, "task_complete") + "\n");
                tail.Read(jumpPath, jump.Read, () => jump.Gap(jump.LastObserved));
                Check(jump.View(T.AddMinutes(3)) is { Compacted: true }, "catch-up seek skipped completion");
                var cold = new ChatCacheMonitor(root, Path.Combine(root, "claude"));
                var recovered = cold.Poll(T.AddMinutes(3)).Single();
                Check(recovered.Compacted && recovered.CompactedAt == live.CompactedAt, "restart lost compact");
                cold.Poll(T.AddMinutes(3)); Check(cold.LastReadBytes == 0, "unchanged file rescanned");
                var journal = new ChatSessionHistory(Path.Combine(root, "journal"));
                var stale = recovered with { Compacted = false, CompactedAt = null, RequestAt = T.AddMinutes(2.001) };
                journal.Capture([stale], T.AddMinutes(3)); journal.Capture([recovered], T.AddMinutes(3)); journal.Flush(T.AddMinutes(3));
                var restored = new ChatSessionHistory(Path.Combine(root, "journal"));
                var merged = ChatListPolicy.Merge([recovered], restored.Current(T.AddMinutes(3))!.Chats.Select(c => c.Last), T.AddMinutes(3)).Single();
                Check(merged.Compacted && restored.RecentCompacted(T.AddMinutes(3)).Single().Compacted, "stale journal shadowed recovery");
            }
            finally { Directory.Delete(root, true); }
        });
    }
}
