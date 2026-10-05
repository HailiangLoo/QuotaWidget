using System.Diagnostics;
using System.Text.Json;
using QuotaWidget.Core;

static class CodexTests
{
    const string Fixture = """
    {"accountId":"account-fixture-only","rateLimits":{"limitId":"base_model_inference","primary":{"usedPercent":99,"windowDurationMins":10080,"resetsAt":1791400000}},
     "rateLimitsByLimitId":{"base_model_inference":{"primary":{"usedPercent":99,"windowDurationMins":10080,"resetsAt":1791400000}},
     "codex":{"limitId":"codex","planType":"plus","primary":{"usedPercent":18,"windowDurationMins":300,"resetsAt":1790900000},"secondary":{"usedPercent":25,"windowDurationMins":10080,"resetsAt":1791400000}}}}
    """;
    static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    static LatestEnvelope Parse(string json = Fixture, DateTimeOffset? at = null)
    {
        using var d = JsonDocument.Parse(json);
        return CodexUsageSource.Parse(d.RootElement, at ?? At);
    }
    static void Assert(bool yes, string reason) { if (!yes) throw new Exception(reason); }
    static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "QuotaWidget-codex-test-" + Guid.NewGuid().ToString("N"))).FullName;
    static void Clean(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.Combine(Path.GetTempPath(), "QuotaWidget-codex-test-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(path, true);
    }
    sealed class Source(Func<LatestEnvelope> response) : ICodexUsageSource
    {
        public int Calls;
        public Task<LatestEnvelope> FetchAsync(CancellationToken ct) { Calls++; return Task.FromResult(response()); }
    }
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        void Test(string name, Action body) => tests.Add((name, () => { body(); return Task.CompletedTask; }));
        Test("Codex: exact codex bucket and weekly duration, not model allowance", () =>
        {
            var e = Parse();
            Assert(e.Snapshot?.Limits.AllWeek?.UsedPercent == 25, "wrong bucket/window");
            Assert(e.Snapshot!.Limits.FiveHour?.UsedPercent==18 && e.Snapshot.Limits.FableWeek is null, "5h missing or invented Fable data");
            Assert(SnapshotJson.ParseEnvelope(SnapshotJson.WriteEnvelope(e), []) is not null, "schema invalid");
            Assert(e.Snapshot.Limits.AllWeek!.WindowMode == WindowModes.Fixed, "weekly fixed reset missing");
        });
        Test("Codex: weekly quota also works in primary window", () =>
        {
            var swapped = Fixture.Replace("primary", "TEMP").Replace("secondary", "primary").Replace("TEMP", "secondary");
            Assert(Parse(swapped).Snapshot?.Limits.AllWeek?.UsedPercent == 25, "primary ignored");
            Assert(Parse(swapped).Snapshot?.Limits.FiveHour?.UsedPercent==18,"secondary 5h ignored");
        });
        Test("Codex: absent bucket never falls back to another allowance", () =>
        {
            Assert(Parse(Fixture.Replace("\"codex\":", "\"other\":" )).Snapshot is null, "selected other bucket");
        });
        Test("Codex: invalid or absent weekly values stay missing", () =>
        {
            foreach (var value in new[] { "-1", "101", "null", "\"25\"" })
                Assert(Parse(Fixture.Replace("\"usedPercent\":25", "\"usedPercent\":" + value)).Snapshot?.Limits is {AllWeek:null,FiveHour:not null}, "accepted " + value);
            Assert(Parse(Fixture.Replace("10080", "300")).Snapshot is null, "invented weekly limit");
            Assert(Parse(Fixture.Replace("\"resetsAt\":1791400000", "\"resetsAt\":null")).Snapshot?.Limits.AllWeek is null, "missing reset accepted");
        });
        Test("Codex: missing identity fails closed; account/plan partition separately", () =>
        {
            Assert(Parse(Fixture.Replace("account-fixture-only", "")).Snapshot is null, "identity absent");
            var a = Parse(); var b = Parse(Fixture.Replace("account-fixture-only", "account-b")); var plan = Parse(Fixture.Replace("plus", "pro"));
            Assert(a.ProfileKey != b.ProfileKey && a.ProfileKey != plan.ProfileKey, "profile contamination");
            Assert(!SnapshotJson.WriteEnvelope(a).Contains("account-fixture-only"), "raw identity persisted");
        });
        Test("Codex: malformed response does not crash parser", () =>
        {
            foreach (var json in new[] { "null", "[]", "{}", Fixture.Replace("\"windowDurationMins\":10080", "\"windowDurationMins\":\"10080\"").Replace("\"windowDurationMins\":300", "\"windowDurationMins\":\"300\"") })
                Assert(Parse(json).Snapshot is null, "invalid schema accepted");
        });
        Test("Codex: optional 5h windows follow duration and validate independently",()=>
        {
            var pro=Parse(Fixture.Replace("\"windowDurationMins\":300","\"windowDurationMins\":60").Replace("plus","pro"));
            Assert(pro.Snapshot?.Limits is {FiveHour:null,AllWeek:not null},"invented 5h from plan or wrong duration");
            foreach(var value in new[]{"-1","101","null","\"18\""})
            {
                var bad=Parse(Fixture.Replace("\"usedPercent\":18","\"usedPercent\":"+value));
                Assert(bad.Snapshot?.Limits is {FiveHour:null,AllWeek:not null}&&bad.ErrorCode=="invalid_five_hour_window","bad 5h erased weekly or became zero");
            }
            var only=Parse(Fixture.Replace("10080","60"));
            Assert(only.Snapshot?.Limits is {FiveHour:not null,AllWeek:null}&&only.ErrorCode is null,"5h-only account rejected");
            Assert(SnapshotJson.ParseEnvelope(SnapshotJson.WriteEnvelope(only),[])?.Snapshot?.Limits.FiveHour?.UsedPercent==18,"5h lost in storage roundtrip");
            Assert(Parse(Fixture.Replace("\"resetsAt\":1790900000","\"resetsAt\":null")).Snapshot?.Limits is {FiveHour:null,AllWeek:not null},"missing 5h reset accepted");
            Assert(Parse(Fixture.Replace("\"usedPercent\":18","\"usedPercent\":0")).Snapshot?.Limits.FiveHour?.UsedPercent==0,"zero usage hidden");
        });
        tests.Add(("Codex: polling, concurrent triggers and restart preserve deadlines", async () =>
        {
            var dir = Temp();
            try
            {
                var now = At; var source = new Source(() => Parse(at: now)); var paths = new DataPaths(dir);
                var settings = new WidgetSettings();
                using (var c = new CodexUsageCollector(paths, () => settings, source, () => now))
                {
                    await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => c.CollectOnceAsync(default)));
                    Assert(source.Calls == 1, "duplicate concurrent query");
                }
                using (var restarted = new CodexUsageCollector(paths, () => settings, source, () => now))
                {
                    Assert(await restarted.CollectOnceAsync(default) is null, "restart bypassed wait");
                    now = now.AddMinutes(5);
                    Assert((await restarted.CollectOnceAsync(default))?.Snapshot is not null, "next interval blocked");
                }
                Assert(source.Calls == 2, "unexpected calls");
            }
            finally { Clean(dir); }
        }));
        tests.Add(("Codex: failure backoff persists, snapshot absent, retry floor not truncated", async () =>
        {
            var dir = Temp();
            try
            {
                var now = At; var paths = new DataPaths(dir);
                var source = new Source(() => CodexUsageSource.Failure(now, "unavailable") with { RetryAfterSeconds = 86400 });
                var settings = new WidgetSettings();
                using var c = new CodexUsageCollector(paths, () => settings, source, () => now);
                var e = await c.CollectOnceAsync(default);
                Assert(e?.Snapshot is null && c.NotBefore == At.AddDays(1), "retry floor shortened");
                using var restarted = new CodexUsageCollector(paths, () => settings, source, () => now);
                now = now.AddHours(12);
                Assert(await restarted.CollectOnceAsync(default) is null && source.Calls == 1, "restart shortened floor");
            }
            finally { Clean(dir); }
        }));
        Test("Codex: history survives restart, unchanged percentages are observations", () =>
        {
            var dir = Temp();
            try
            {
                var paths = new DataPaths(dir); var now = At.AddMinutes(10);
                var model = new WidgetModel(paths, false, () => now); model.Initialize();
                foreach (var minute in new[] { 0, 5, 10 })
                {
                    AtomicFile.WriteAllText(paths.Latest, SnapshotJson.WriteEnvelope(Parse(at: At.AddMinutes(minute))));
                    model.PollLatest();
                }
                var restarted = new WidgetModel(paths, false, () => now); restarted.Initialize();
                Assert(restarted.Records.Count == 3, "lost/deduplicated unchanged snapshots");
                Assert(restarted.BuildView(now).Chart.Total.Segments.All(s => s.Valid && s.Rate == 0), "zero burn not valid");
            }
            finally { Clean(dir); }
        });
        Test("Dashboard: Claude gaps do not mask Codex; clock and sums share selected window", () =>
        {
            var dir = Temp();
            try
            {
                var now = At.AddMinutes(20); var cp = new DataPaths(Path.Combine(dir, "claude")); var xp = new DataPaths(Path.Combine(dir, "codex"));
                var claude = new WidgetModel(cp, false, () => now); claude.Initialize();
                var codex = new WidgetModel(xp, false, () => now); codex.Initialize();
                foreach (var minute in new[] { 0, 5, 10, 15 })
                {
                    AtomicFile.WriteAllText(xp.Latest, SnapshotJson.WriteEnvelope(Parse(Fixture.Replace("\"usedPercent\":25", "\"usedPercent\":" + (25 + minute)), At.AddMinutes(minute))));
                    codex.PollLatest();
                }
                var settings = new WidgetSettings { RangeMinutes = 60, Smoothing = false };
                var result = Dashboard.Combine(claude.BuildView(now), codex.BuildView(now), settings, now, codex.LastEnvelope);
                Assert(result.Chart.BlockedText is null && result.Chart.CodexAt(At.AddMinutes(10)) == 60, "Claude blocked Codex");
                Assert(result.Chart.TotalAt(At.AddMinutes(10)) is null, "invented Claude rate");
                Assert(result.Chart.End == now && result.Chart.Start == now.AddHours(-1), "range mismatch");
                Assert(result.Summary.Contains("— / — / 15"), result.Summary);
                Assert(result.Chart.CodexAt(now) is null, "extended line beyond observation");
            }
            finally { Clean(dir); }
        });
        Test("Codex: trust cache is scoped to the requested signer", () =>
        {
            TestEnvironment.RequireLive();
            var exe = CodexUsageSource.Resolve();
            Assert(exe is not null, "official Codex executable unavailable on this host");
            Assert(Authenticode.IsSignedBy(exe!, CodexUsageSource.Signer), "OpenAI signature invalid");
            Assert(!Authenticode.IsSignedBy(exe!, Authenticode.AnthropicOrg), "cached result accepted wrong organisation");
        });
    }
    public static async Task Probe()
    {
        var root = Temp();
        try
        {
            var source = new CodexUsageSource(Path.Combine(root, "work"));
            var watch = Stopwatch.StartNew();
            var result = await source.FetchAsync(default);
            // Only non-secret normalized quota data reaches output. No RPC account response or logs.
            Console.WriteLine(JsonSerializer.Serialize(new { result.Status, result.ErrorCode, result.PlanLabel,
                weekUsed = result.Snapshot?.Limits.AllWeek?.UsedPercent,
                reset = result.Snapshot?.Limits.AllWeek?.ResetsAt,
                elapsedMs = watch.ElapsedMilliseconds,
                source.LastCreditDetailsPresent,
                observedAt = result.Snapshot?.ObservedAt }));
            if (result.Snapshot is null) throw new Exception("Live quota unavailable");
        }
        finally { Clean(root); }
    }
}
