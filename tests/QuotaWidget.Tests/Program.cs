using System.Text.Json;
using QuotaWidget.Core;

if (args.Contains("--cache-probe")) { ChatCacheTests.Probe(); return 0; }
if (args.Contains("--lifecycle-probe")) { ChatLifecycleTests.Probe(); return 0; }
if (args.Contains("--codex-probe")) { await CodexTests.Probe(); return 0; }

// Small self-contained test runner (no NuGet needed): dotnet run --project tests/QuotaWidget.Tests

var tests = new List<(string Name, Func<Task> Body)>();
void Test(string name, Action body) => tests.Add((name, () => { body(); return Task.CompletedTask; }));
void TestAsync(string name, Func<Task> body) => tests.Add((name, body));

static void Eq<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what} expected <{expected}> got <{actual}>");
}
static void Near(double expected, double? actual, string what = "", double tol = 1e-6)
{
    if (actual is null || Math.Abs(expected - actual.Value) > tol) throw new Exception($"{what} expected ≈{expected} got {actual?.ToString() ?? "null"}");
}
static void True(bool cond, string what) { if (!cond) throw new Exception(what); }

var T0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(8));
var R1 = T0.AddDays(3);           // weekly reset of window 1
var R2 = R1.AddDays(7);           // next weekly window
var F1 = T0.AddHours(3);          // 5h reset

HistoryRecord Rec(double minutes, double? week, double? fable = null, int poll = 300, DateTimeOffset? weekReset = null, DateTimeOffset? fiveReset = null, string mode = WindowModes.Fixed, double five = 10)
{
    var t = T0.AddMinutes(minutes);
    QuotaLimit? L(double? v, DateTimeOffset r) => v is null ? null : mode == WindowModes.Fixed ? UsageParser.Limit(v.Value, r) : new QuotaLimit(v.Value, null, null, mode);
    var limits = new QuotaLimits(L(five, fiveReset ?? F1), L(week, weekReset ?? R1), L(fable, weekReset ?? R1));
    var status = limits.FiveHour is not null && limits.AllWeek is not null && limits.FableWeek is not null ? Statuses.Ok : Statuses.Partial;
    return new HistoryRecord("claude-oauth-usage", "p1", "Max (5x)", t.AddSeconds(-1), status, poll, new QuotaSnapshot("s-" + minutes.ToString("0.###"), t, limits));
}

List<HistoryRecord> Steady(int count, double perStep, double start = 40, double step = 5, double fableFactor = 0.5)
{
    var list = new List<HistoryRecord>();
    for (var i = 0; i < count; i++) list.Add(Rec(i * step, start + i * perStep, 10 + i * perStep * fableFactor));
    return list;
}

string FindRepoFile(string rel)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, rel))) dir = dir.Parent;
    return dir is null ? throw new FileNotFoundException(rel) : Path.Combine(dir.FullName, rel);
}

string TempDir()
{
    var d = Path.Combine(Path.GetTempPath(), "qw-test-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(d);
    return d;
}

// ---------------- schema ----------------

Test("schema: design sample parses", () =>
{
    var errors = new List<string>();
    var env = SnapshotJson.ParseEnvelope(File.ReadAllText(FindRepoFile(Path.Combine("schema", "latest.sample.json"))), errors);
    True(env is not null, "sample rejected: " + string.Join("; ", errors));
    Eq("demo-only", env!.SourceId);
    Near(44, env.Snapshot!.Limits.AllWeek!.UsedPercent);
});

Test("schema: invalid inputs are rejected", () =>
{
    var good = SnapshotJson.WriteEnvelope(new LatestEnvelope(1, "src", "p", null, T0, Statuses.Ok, 300, null, null, Rec(0, 40, 10).Snapshot));
    True(SnapshotJson.ParseEnvelope(good, new()) is not null, "good envelope rejected");
    string Mutate(Action<Dictionary<string, object?>> f)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, object?>>(good)!;
        f(d);
        return JsonSerializer.Serialize(d);
    }
    var cases = new Dictionary<string, string>
    {
        ["usedPercent > 100"] = good.Replace("\"usedPercent\": 40", "\"usedPercent\": 120"),
        ["timestamp without zone"] = good.Replace(SnapshotJson.FormatTimestamp(T0), "2026-10-01T00:00:00"),
        ["ok with a null bucket"] = good.Replace("\"fableWeek\": {", "\"fableWeek\": null, \"x\": {"),
        ["error with snapshot"] = good.Replace("\"status\": \"ok\"", "\"status\": \"error\""),
        ["unknown property"] = Mutate(d => d["extra"] = 1),
        ["poll below 60"] = good.Replace("\"effectivePollIntervalSeconds\": 300", "\"effectivePollIntervalSeconds\": 30"),
        ["schemaVersion 2"] = good.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"),
        ["bad windowMode"] = good.Replace("\"windowMode\": \"fixed\"", "\"windowMode\": \"weekly\""),
    };
    foreach (var (name, json) in cases)
        True(SnapshotJson.ParseEnvelope(json, new()) is null, "accepted: " + name);
});

Test("schema: write/parse round trip keeps nulls as null", () =>
{
    var snap = new QuotaSnapshot("x", T0, new QuotaLimits(null, UsageParser.Limit(12.5, R1), null));
    var env = new LatestEnvelope(1, "src", "p", "Max (5x)", T0, Statuses.Partial, 300, null, null, snap);
    var back = SnapshotJson.ParseEnvelope(SnapshotJson.WriteEnvelope(env), new())!;
    True(back.Snapshot!.Limits.FiveHour is null && back.Snapshot.Limits.FableWeek is null, "missing became a value");
    Near(12.5, back.Snapshot.Limits.AllWeek!.UsedPercent);
    Eq(T0, back.Snapshot.ObservedAt);
    Eq("Max (5x)", back.PlanLabel);
});

// ---------------- rate engine ----------------

Test("rate: steady 6 pt/h, raw and 15m smooth, range sum", () =>
{
    var recs = Steady(13, 0.5); // 12 intervals of 5 min, +0.5 each = 6 pt/h
    var s = RateEngine.Build(recs, SeriesKey.Total, []);
    Eq(12, s.Segments.Count);
    True(s.Segments.All(x => x.Valid), "all valid");
    Near(6, s.Segments[0].Rate);
    Near(6, s.Segments[0].Smooth, "first measured interval is visible");
    Near(5, s.Segments[0].SmoothMinutes, "do not call warmup 15m");
    Near(6, s.ValueAt(T0.AddMinutes(2), true), "first interval has an average before next sample");
    Near(6, s.Segments[1].Smooth);
    Near(6, s.Segments[11].Smooth);
    var sum = RateEngine.SumRange(s, T0, T0.AddHours(1));
    Near(6, sum.Delta);
    Near(60, sum.CoverageMinutes);
});

Test("rate: uses the real elapsed time, not a nominal 5 min", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 40), Rec(7, 40.7), Rec(12.5, 41.25) };
    var s = RateEngine.Build(recs, SeriesKey.Total, []);
    Near(6, s.Segments[0].Rate);
    Near(6, s.Segments[1].Rate);
});

Test("rate: zero change is a real zero, not a gap", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 40), Rec(5, 40), Rec(10, 40), Rec(15, 40) };
    var s = RateEngine.Build(recs, SeriesKey.Total, []);
    True(s.Segments.All(x => x.Valid), "flat segments must stay valid");
    Near(0, s.Segments[2].Smooth);
    Near(0, s.ValueAt(T0.AddMinutes(12), smooth: false));
});

Test("rate: a hole is a gap; no smoothing across; sums skip it; cumulative spans it", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 40), Rec(5, 40.5), Rec(10, 41), Rec(40, 44), Rec(45, 44.5), Rec(50, 45), Rec(55, 45.5) };
    var s = RateEngine.Build(recs, SeriesKey.Total, []);
    var gap = s.Segments[2];
    Eq(SegmentIssue.Gap, gap.Issue);
    Eq("缺口", gap.Label);
    Near(6, s.Segments[3].Smooth, "first interval after a gap is displayed independently");
    Near(5, s.Segments[3].SmoothMinutes, "warmup excludes the earlier group");
    Near(6, s.Segments[4].Smooth);
    True(s.ValueAt(T0.AddMinutes(25), true) is null, "value inside a gap must be unknown");
    var sum = RateEngine.SumRange(s, T0, T0.AddMinutes(55));
    Near(25, sum.CoverageMinutes, "coverage excludes the gap");
    Near(2.5, sum.Delta, "gap increment is not counted as recorded");
    var cum = RateEngine.Cumulative(recs, SeriesKey.Total, s)!;
    True(cum.IncludesGap, "cumulative must say it spans a gap");
    Near(5.5, cum.Delta);
    Near(6, cum.Rate);
});

Test("rate: only OS suspend/resume labels a gap as 休眠", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 40), Rec(5, 40.5), Rec(40, 40.5), Rec(45, 41) };
    var plain = RateEngine.Build(recs, SeriesKey.Total, []);
    Eq("缺口", plain.Segments[1].Label, "flat quota across a hole is still just a gap");
    var events = new List<AppEvent> { new(T0.AddMinutes(8), EventTypes.Suspend), new(T0.AddMinutes(38), EventTypes.Resume) };
    Eq("休眠", RateEngine.Build(recs, SeriesKey.Total, events).Segments[1].Label);
    var net = new List<AppEvent> { new(T0.AddMinutes(10), EventTypes.CollectFail, Statuses.Error, "network") };
    Eq("断网", RateEngine.Build(recs, SeriesKey.Total, net).Segments[1].Label);
    var run = new List<AppEvent> { new(T0.AddMinutes(9), EventTypes.AppExit), new(T0.AddMinutes(39), EventTypes.AppStart) };
    Eq("未运行", RateEngine.Build(recs, SeriesKey.Total, run).Segments[1].Label);
});

Test("rate: short restart without missed samples keeps curve and cumulative continuous", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 40), Rec(5, 40.5), Rec(10, 41) };
    var brief = new List<AppEvent> { new(T0.AddMinutes(2), EventTypes.AppExit), new(T0.AddMinutes(2).AddSeconds(2), EventTypes.AppStart) };
    var data = RateEngine.Build(recs, SeriesKey.Total, brief);
    True(data.Segments.All(s => s.Valid), "two-second restart created a false gap");
    Near(1, RateEngine.SumRange(data, T0, T0.AddMinutes(10)).Delta);
    Near(6, data.ValueAt(T0.AddMinutes(4), true));
    var missingPair = new List<AppEvent> { new(T0.AddMinutes(2), EventTypes.AppStart) };
    Eq(SegmentIssue.Gap, RateEngine.Build(recs, SeriesKey.Total, missingPair).Segments[0].Issue);
    var failure = brief.Append(new AppEvent(T0.AddMinutes(3), EventTypes.CollectFail, Statuses.Error, "network")).ToList();
    Eq(SegmentIssue.Gap, RateEngine.Build(recs, SeriesKey.Total, failure).Segments[0].Issue);
    var slept = new List<AppEvent> { new(T0.AddMinutes(2), EventTypes.Suspend), new(T0.AddMinutes(2).AddSeconds(2), EventTypes.Resume) };
    Eq(SegmentIssue.Gap, RateEngine.Build(recs, SeriesKey.Total, slept).Segments[0].Issue);
    var longInterval = new List<HistoryRecord> { Rec(0, 40), Rec(20, 41) };
    Eq(SegmentIssue.Gap, RateEngine.Build(longInterval, SeriesKey.Total, brief).Segments[0].Issue);
});

Test("rate: paired brief monitoring changes keep observed mass and the full smoothing window", () =>
{
    var recs=Enumerable.Range(0,25).Select(i=>Rec(i*5,40+Math.Floor(i/4d))).ToList();
    var events=new List<AppEvent>{new(T0.AddMinutes(107),EventTypes.MonitorPause),new(T0.AddMinutes(107).AddSeconds(35),EventTypes.MonitorResume),new(T0.AddMinutes(108),EventTypes.AppExit),new(T0.AddMinutes(108).AddSeconds(2),EventTypes.AppStart)};
    var normal=RateEngine.Build(recs,SeriesKey.Total,[]);var switched=RateEngine.Build(recs,SeriesKey.Total,events);
    True(switched.Segments.All(s=>s.Valid),"short complete switch cut a normally sampled interval");
    Near(6,RateEngine.SumRange(switched,T0,T0.AddMinutes(120)).Delta,"observed counter increment lost");
    var expected=RateTrend.Build(normal,T0,T0.AddMinutes(120));var actual=RateTrend.Build(switched,T0,T0.AddMinutes(120));
    Eq(1,actual.Runs.Count);Near(120,actual.Runs[0].KernelMinutes);
    for(var i=0;i<expected.Runs[0].Points.Count;i++)Near(expected.Runs[0].Points[i].Rate,actual.Runs[0].Points[i].Rate,"switch fabricated a short-run peak");
});

Test("rate: brief switch exception retains long, incomplete, failed and structural gaps", () =>
{
    AppEvent E(double seconds,string type)=>new(T0.AddSeconds(seconds),type);
    var recs=new List<HistoryRecord>{Rec(0,40),Rec(5,41)};
    var brief=new[]{E(60,EventTypes.MonitorPause),E(90,EventTypes.MonitorResume)};
    void Gap(params AppEvent[] events)=>Eq(SegmentIssue.Gap,RateEngine.Build(recs,SeriesKey.Total,events.OrderBy(e=>e.T).ToArray()).Segments.Single().Issue);
    True(RateEngine.Build(recs,SeriesKey.Total,[E(60,EventTypes.MonitorPause),E(121,EventTypes.MonitorResume)]).Segments.Single().Valid,"fixed 60-second cutoff returned");
    Gap(E(60,EventTypes.MonitorPause));Gap(E(90,EventTypes.MonitorResume));
    Gap(E(60,EventTypes.MonitorPause),E(70,EventTypes.MonitorPause),E(90,EventTypes.MonitorResume));
    Eq(SegmentIssue.Gap,RateEngine.Build([Rec(0,40),Rec(8,41)],SeriesKey.Total,[E(20,EventTypes.MonitorPause),E(190,EventTypes.MonitorResume),E(220,EventTypes.MonitorPause),E(370,EventTypes.MonitorResume)]).Segments.Single().Issue);
    Gap(brief.Concat(new[]{new AppEvent(T0.AddMinutes(2),EventTypes.CollectFail,Statuses.Error,"network")}).ToArray());
    Gap(brief.Concat(new[]{E(120,EventTypes.Suspend),E(121,EventTypes.Resume)}).ToArray());
    Eq(SegmentIssue.Gap,RateEngine.Build([Rec(0,40),Rec(20,41)],SeriesKey.Total,brief).Segments.Single().Issue);
    Eq(SegmentIssue.Reset,RateEngine.Build([Rec(0,40),Rec(5,1,weekReset:R2)],SeriesKey.Total,brief).Segments.Single().Issue);
    Eq(SegmentIssue.Decrease,RateEngine.Build([Rec(0,40),Rec(5,39)],SeriesKey.Total,brief).Segments.Single().Issue);
    Eq(SegmentIssue.Missing,RateEngine.Build([Rec(0,40),Rec(5,null)],SeriesKey.Total,brief).Segments.Single().Issue);
});

Test("rate: 104-second update inside normal 5-minute observations does not make a smoothing cliff", () =>
{
    var records=Steady(25,.25);
    var events=new[]{new AppEvent(T0.AddMinutes(62).AddSeconds(49),EventTypes.AppExit),new AppEvent(T0.AddMinutes(64).AddSeconds(33),EventTypes.AppStart)};
    var fixedData=RateEngine.Build(records,SeriesKey.Total,events);var baseline=RateEngine.Build(records,SeriesKey.Total,[]);
    True(fixedData.Segments.All(s=>s.Valid),"normal observed update interval cut in two");
    var fixedTrend=RateTrend.Build(fixedData,T0,T0.AddHours(2));var expected=RateTrend.Build(baseline,T0,T0.AddHours(2));
    Eq(1,fixedTrend.Runs.Count);Near(expected.Delta,fixedTrend.Delta);
    for(var i=0;i<expected.Runs[0].Points.Count;i++)Near(expected.Runs[0].Points[i].Rate,fixedTrend.Runs[0].Points[i].Rate,"restart changes smooth shape");
    var reset=new[]{Rec(60,99),Rec(65,0,weekReset:R2)};
    Eq(SegmentIssue.Reset,RateEngine.Build(reset,SeriesKey.Total,events).Segments.Single().Issue,"concurrent real reset hidden");
});

Test("rate: interruption budget follows cadence and overlapping outages are counted once", () =>
{
    foreach(var poll in new[]{60,300,600})
    {
        var records=new[]{Rec(0,40,poll:poll),Rec(poll/60d*1.5,41,poll:poll)};
        var start=T0.AddSeconds(1);
        True(RateEngine.Build(records,SeriesKey.Total,[new(start,EventTypes.AppExit),new(start.AddSeconds(poll-1),EventTypes.AppStart)]).Segments.Single().Valid,"one cadence did not apply");
        Eq(SegmentIssue.Gap,RateEngine.Build(records,SeriesKey.Total,[new(start,EventTypes.AppExit),new(start.AddSeconds(poll+1),EventTypes.AppStart)]).Segments.Single().Issue,"long outage accepted");
    }
    var overlap=new[]{new AppEvent(T0.AddSeconds(60),EventTypes.MonitorPause),new AppEvent(T0.AddSeconds(120),EventTypes.AppExit),new AppEvent(T0.AddSeconds(240),EventTypes.MonitorResume),new AppEvent(T0.AddSeconds(290),EventTypes.AppStart)};
    True(RateEngine.Build([Rec(0,40),Rec(5,41)],SeriesKey.Total,overlap).Segments.Single().Valid,"same outage counted twice");
    Eq(SegmentIssue.Gap,RateEngine.Build([Rec(0,40),Rec(10,41)],SeriesKey.Total,overlap).Segments.Single().Issue,"missed observation hidden");
});

Test("rate: weekly reset breaks the line; no negative consumption; cumulative restarts", () =>
{
    var recs = new List<HistoryRecord>
    {
        Rec(0, 88, 30), Rec(5, 89, 30.5), Rec(10, 90, 31),
        Rec(15, 0, 0, weekReset: R2), Rec(20, 0.5, 0.2, weekReset: R2), Rec(25, 1, 0.4, weekReset: R2), Rec(30, 1.5, 0.6, weekReset: R2),
    };
    var s = RateEngine.Build(recs, SeriesKey.Total, []);
    Eq(SegmentIssue.Reset, s.Segments[2].Issue);
    True(s.Segments.Where(x => x.Valid).All(x => x.Delta >= 0), "no negative valid delta");
    Near(3.5, RateEngine.SumRange(s, T0, T0.AddMinutes(30)).Delta);
    var cum = RateEngine.Cumulative(recs, SeriesKey.Total, s)!;
    Eq(T0.AddMinutes(15), cum.From);
    Near(1.5, cum.Delta);
    True(!cum.IncludesGap, "no gap in the new window");
});

Test("rate: a 5-hour reset does not touch the weekly curve", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 40, five: 95), Rec(5, 40.5, five: 98), Rec(10, 41, five: 1, fiveReset: F1.AddHours(5)), Rec(15, 41.5, five: 3, fiveReset: F1.AddHours(5)) };
    var s = RateEngine.Build(recs, SeriesKey.Total, []);
    True(s.Segments.All(x => x.Valid), "weekly segments stay valid across a 5h reset");
});

Test("rate: decrease inside one window is refused", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 40), Rec(5, 41), Rec(10, 39), Rec(15, 39.5) };
    var s = RateEngine.Build(recs, SeriesKey.Total, []);
    Eq(SegmentIssue.Decrease, s.Segments[1].Issue);
    Eq("异常", s.Segments[1].Label);
    Eq(T0.AddMinutes(10), RateEngine.Cumulative(recs, SeriesKey.Total, s)?.From ?? T0.AddMinutes(10));
});

Test("rate: interval change 5→10 min does not create a gap", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 40), Rec(5, 40.5), Rec(15, 41.5, poll: 600), Rec(25, 42.5, poll: 600) };
    var s = RateEngine.Build(recs, SeriesKey.Total, []);
    True(s.Segments.All(x => x.Valid), "a legitimate longer interval is not a gap");
});

Test("rate: unknown window semantics produce no rate", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 40, mode: WindowModes.Rolling), Rec(5, 41, mode: WindowModes.Rolling) };
    var s = RateEngine.Build(recs, SeriesKey.Total, []);
    Eq(SegmentIssue.UnknownWindow, s.Segments[0].Issue);
    True(RateEngine.Cumulative(recs, SeriesKey.Total, s) is null, "no cumulative for rolling/unknown windows");
});

Test("rate: missing Fable bucket is unknown, not zero", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 40, null), Rec(5, 40.5, null), Rec(10, 41, 12), Rec(15, 41.5, 12.5), Rec(20, 42, 13) };
    var f = RateEngine.Build(recs, SeriesKey.Fable, []);
    Eq(SegmentIssue.Missing, f.Segments[0].Issue);
    Eq(SegmentIssue.Missing, f.Segments[1].Issue);
    True(f.Segments[2].Valid && f.Segments[3].Valid, "valid once present");
    Near(10, RateEngine.SumRange(f, T0, T0.AddMinutes(20)).CoverageMinutes);
    True(RateEngine.Build(recs, SeriesKey.Total, []).Segments.All(x => x.Valid), "total unaffected");
});

Test("rate: smoothing tolerates a few seconds of jitter", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 40), Rec(5.1, 40.5), Rec(10.15, 41), Rec(15.2, 41.5) };
    var s = RateEngine.Build(recs, SeriesKey.Total, []);
    var last = s.Segments[2];
    Near(1.5 * 60 / 15.2, last.Smooth, "three intervals used", 1e-6);
});

// ---------------- history ----------------

Test("history: append, dedupe, conflict, restart, torn line", () =>
{
    var root = TempDir();
    var paths = new DataPaths(root);
    var store = new HistoryStore(paths, demoMode: false);
    var a = Rec(0, 40, 10);
    var b = Rec(5, 40, 10); // same values, new observation time: must be kept
    Eq(AppendResult.Added, store.Append(a));
    Eq(AppendResult.Duplicate, store.Append(a));
    Eq(AppendResult.Added, store.Append(b));
    var conflict = a with { Snapshot = a.Snapshot with { Limits = a.Snapshot.Limits with { AllWeek = UsageParser.Limit(99, R1) } } };
    Eq(AppendResult.Conflict, store.Append(conflict));
    var demo = a with { SourceId = "demo-only", Snapshot = a.Snapshot with { Id = "d1" } };
    Eq(AppendResult.Rejected, store.Append(demo));

    // simulate a crash mid-line, then keep appending
    var file = Directory.GetFiles(store.ProfileDir("claude-oauth-usage", "p1"), "*.jsonl").Single();
    File.AppendAllText(file, "{\"v\":1,\"sourceId\":\"claude-oau");
    Eq(AppendResult.Added, new HistoryStore(paths, false).Append(Rec(10, 40.5, 10.2)));

    var reloaded = new HistoryStore(paths, false).Load("claude-oauth-usage", "p1", T0.AddDays(-1));
    Eq(3, reloaded.Count, "records after restart");
    Near(40, reloaded[0].Snapshot.Limits.AllWeek!.UsedPercent);
    True(reloaded.Select(r => r.T).SequenceEqual(new[] { a.T, b.T, T0.AddMinutes(10) }), "order and times kept");
    Directory.Delete(root, true);
});

Test("history: accounts/plans are stored apart", () =>
{
    var root = TempDir();
    var store = new HistoryStore(new DataPaths(root), false);
    store.Append(Rec(0, 40, 10));
    store.Append(Rec(5, 41, 10) with { ProfileKey = "p2" });
    Eq(1, store.Load("claude-oauth-usage", "p1", T0.AddDays(-1)).Count);
    Eq(1, store.Load("claude-oauth-usage", "p2", T0.AddDays(-1)).Count);
    Directory.Delete(root, true);
});

// ---------------- widget model (latest.json → history → view) ----------------

Test("model: ingest, failures keep old observation time, restart keeps history", () =>
{
    var root = TempDir();
    var paths = new DataPaths(root);
    var now = T0.AddMinutes(16);
    var model = new WidgetModel(paths, false, () => now);
    model.Initialize();
    void Write(LatestEnvelope e) { AtomicFile.WriteAllText(paths.Latest, SnapshotJson.WriteEnvelope(e)); }
    LatestEnvelope Ok(HistoryRecord r) => new(1, r.SourceId, r.ProfileKey, r.PlanLabel, r.AttemptedAt, r.Status, 300, null, null, r.Snapshot);

    foreach (var r in new[] { Rec(0, 40, 10), Rec(5, 40.5, 10.25), Rec(10, 41, 10.5) })
    {
        Write(Ok(r));
        True(model.PollLatest(), "change detected");
    }
    Eq(3, model.Records.Count);
    Write(new LatestEnvelope(1, "claude-oauth-usage", "p1", null, T0.AddMinutes(15), Statuses.Error, 600, null, "network", null));
    model.PollLatest();
    Eq(3, model.Records.Count, "failure adds no history");
    Eq(T0.AddMinutes(10), model.Records[^1].T, "failure must not refresh observedAt");
    True(model.EventList.Any(e => e.Type == EventTypes.CollectFail && e.ErrorCode == "network"), "failure logged as an event");
    var view = model.BuildView(now);
    True(view.Note!.StartsWith("采集失败（网络）"), "note: " + view.Note);
    Eq("59%", view.Week.Value); // default displays the remainder of the observed 41% used

    // out-of-order snapshot must not replace the current value
    Write(Ok(Rec(2.5, 40.2, 10.1)));
    model.PollLatest();
    Eq("59%", model.BuildView(now).Week.Value);

    var restarted = new WidgetModel(paths, false, () => now);
    restarted.Initialize();
    Eq(4, restarted.Records.Count, "history survives restart");
    Directory.Delete(root, true);
});

Test("model: Fable uses own weekly points, independent of legacy q", () =>
{
    var root = TempDir();
    var paths = new DataPaths(root);
    var now = T0.AddMinutes(60);
    var model = new WidgetModel(paths, false, () => now);
    model.Initialize();
    foreach (var r in Steady(13, 0.5))
    {
        AtomicFile.WriteAllText(paths.Latest, SnapshotJson.WriteEnvelope(new LatestEnvelope(1, r.SourceId, r.ProfileKey, r.PlanLabel, r.AttemptedAt, r.Status, 300, null, null, r.Snapshot)));
        model.PollLatest();
    }
    var v = model.BuildView(now);
    True(v.Summary.Contains("Fable 3.0点"), "summary: " + v.Summary);
    True(v.Summary.StartsWith("近5h · 已记录 6.0点"), "summary: " + v.Summary);
    Near(3, v.SummaryFable);
    Near(3, v.Chart.FableAt(T0.AddMinutes(30)));
    model.SetCurrentQ(0.5);
    var v2 = model.BuildView(now);
    Near(3, v2.SummaryFable, "legacy q does not affect own points");
    Near(3, v2.Chart.FableAt(T0.AddMinutes(30)), "orange = own rate");
    Near(6, v2.Chart.TotalAt(T0.AddMinutes(30)), "blue unaffected by q");
    True(v2.SummaryDetail.Contains("累计平均 6.00 点/h"), v2.SummaryDetail);
    Directory.Delete(root, true);
});

Test("model: stale data is flagged and the tail is shaded 断开", () =>
{
    var root = TempDir();
    var paths = new DataPaths(root);
    var now = T0.AddMinutes(60 + 30);
    var model = new WidgetModel(paths, false, () => now);
    model.Initialize();
    foreach (var r in Steady(13, 0.5))
    {
        AtomicFile.WriteAllText(paths.Latest, SnapshotJson.WriteEnvelope(new LatestEnvelope(1, r.SourceId, r.ProfileKey, r.PlanLabel, r.AttemptedAt, r.Status, 300, null, null, r.Snapshot)));
        model.PollLatest();
    }
    var v = model.BuildView(now);
    True(v.Note!.Contains("分钟未更新"), "note: " + v.Note);
    True(v.Chart.Gaps.Any(g => g.Label == "断开" && g.End == now), "disconnected tail");
    Eq(now, v.Chart.End);
    Directory.Delete(root, true);
});

Test("model: demo files never enter real history", () =>
{
    var root = TempDir();
    var paths = new DataPaths(root);
    File.Copy(FindRepoFile(Path.Combine("schema", "latest.sample.json")), paths.Latest);
    var model = new WidgetModel(paths, false);
    model.Initialize();
    Eq(0, model.Records.Count);
    True(model.LatestError!.Contains("演示"), "error: " + model.LatestError);
    True(!Directory.Exists(paths.HistoryDir), "no history written");
    Directory.Delete(root, true);
});

Test("view: countdown text", () =>
{
    Eq("1时00分重置", WidgetModel.Countdown(T0.AddHours(1).AddSeconds(30), T0));
    Eq("11时59分重置", WidgetModel.Countdown(T0.AddHours(12).AddSeconds(-30), T0));
    Eq("6天8时重置", WidgetModel.Countdown(T0.AddDays(6).AddHours(8).AddMinutes(5), T0));
    Eq("31分重置", WidgetModel.Countdown(T0.AddMinutes(31.5), T0));
    Eq("重置时间未知", WidgetModel.Countdown(null, T0));
});

// ---------------- usage parsing ----------------

Test("parser: limits[] rows map to the three buckets", () =>
{
    var json = """
    {"five_hour":{"utilization":93.0,"resets_at":"2026-10-01T00:09:59.973Z"},
     "seven_day":{"utilization":48.0,"resets_at":"2026-10-01T08:59:59.973Z"},
     "limits":[
       {"kind":"session","group":"session","percent":93,"resets_at":"2026-10-01T00:09:59.973Z","severity":"warning","is_active":true},
       {"kind":"weekly_all","group":"weekly","percent":48,"resets_at":"2026-10-01T08:59:59.973Z","severity":"normal","is_active":false},
       {"kind":"weekly_scoped","group":"weekly","percent":20,"resets_at":"2026-10-01T08:59:59.973Z","scope":{"model":{"display_name":"Fable"}},"severity":"normal","is_active":false},
       {"kind":"weekly_scoped","group":"weekly","percent":5,"resets_at":"2026-10-01T08:59:59.973Z","scope":{"model":{"display_name":"Sonnet"}}}
     ]}
    """;
    using var doc = JsonDocument.Parse(json);
    var p = UsageParser.Parse(doc.RootElement, "Fable");
    var l = p.Limits;
    Near(93, l.FiveHour!.UsedPercent);
    Near(48, l.AllWeek!.UsedPercent);
    Near(20, l.FableWeek!.UsedPercent);
    True(l.AllWeek.WindowId is null, "no invented window id");
    Eq(new DateTimeOffset(2026, 10, 1, 8, 59, 59, 973, TimeSpan.Zero), l.AllWeek.ResetsAt!.Value.ToUniversalTime());
    Eq(WindowModes.Fixed, l.FableWeek.WindowMode);
    True(!p.OutOfRange, "nothing out of range");
});

Test("parser: legacy-only body leaves Fable unknown; epoch resets; out-of-range → missing, not clamped", () =>
{
    var json = """{"five_hour":{"utilization":104,"resets_at":1790726999},"seven_day":{"utilization":12.5,"resets_at":null},"seven_day_opus":{"utilization":3,"resets_at":null}}""";
    using var doc = JsonDocument.Parse(json);
    var p = UsageParser.Parse(doc.RootElement, "Fable");
    True(p.Limits.FiveHour is null && p.OutOfRange, "104% must be rejected, not clamped to 100");
    Eq(WindowModes.Unknown, p.Limits.AllWeek!.WindowMode);
    True(p.Limits.FableWeek is null, "Opus is not Fable");
    using var neg = JsonDocument.Parse("""{"seven_day":{"utilization":-20,"resets_at":"2026-10-04T08:00:00Z"}}""");
    var n = UsageParser.Parse(neg.RootElement, "Fable");
    True(n.Limits.AllWeek is null && n.OutOfRange, "-20% must not become 0");
});

Test("parser: CLI model_scoped projection is a Fable fallback", () =>
{
    using var doc = JsonDocument.Parse("""{"seven_day":{"utilization":30,"resets_at":"2026-10-04T09:00:00Z"},"model_scoped":[{"display_name":"Fable","utilization":7,"resets_at":"2026-10-04T09:00:00Z"}]}""");
    Near(7, UsageParser.Parse(doc.RootElement, "Fable").Limits.FableWeek!.UsedPercent);
});

// ---------------- collector (official CLI as the source; faked here) ----------------

const string UsageBody = """
{"limits":[{"kind":"session","group":"session","percent":12,"resets_at":"2026-10-01T03:00:00Z"},
{"kind":"weekly_all","group":"weekly","percent":30,"resets_at":"2026-10-04T09:00:00Z"},
{"kind":"weekly_scoped","group":"weekly","percent":7,"resets_at":"2026-10-04T09:00:00Z","scope":{"model":{"display_name":"Fable"}}}]}
""";

JsonElement Body(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

(DataPaths Paths, WidgetSettings Settings, string ConfigDir) CollectorEnv(bool loggedIn, string? account = "acc-1")
{
    var root = TempDir();
    var paths = new DataPaths(root);
    var s = new WidgetSettings();
    var cfg = s.ResolveClaudeConfigDir(paths);
    Directory.CreateDirectory(cfg);
    if (loggedIn)
    {
        // The widget only stats this file; its content is the CLI's business.
        File.WriteAllText(ClaudeConfigFiles.CredentialsPath(cfg), "{\"opaque\":true}");
        File.WriteAllText(ClaudeConfigFiles.GlobalConfigPath(cfg), account is null
            ? """{"userID":"x"}"""
            : $$$"""{"oauthAccount":{"accountUuid":"{{{account}}}","organizationUuid":"org-1","emailAddress":"x@example.com"}}""");
    }
    return (paths, s, cfg);
}

TestAsync("collector: no login file → auth_required, CLI not even started", async () =>
{
    var (paths, s, _) = CollectorEnv(loggedIn: false);
    var src = new FakeSource(_ => throw new Exception("must not run the CLI"));
    using var c = new ClaudeUsageCollector(paths, () => s, src);
    var env = await c.CollectOnceAsync(CancellationToken.None);
    Eq(Statuses.AuthRequired, env.Status);
    Eq("not_logged_in", env.ErrorCode);
    Eq(0, src.Calls);
    True(SnapshotJson.ParseEnvelope(File.ReadAllText(paths.Latest), new()) is not null, "latest.json valid");
});

TestAsync("collector: fresh CLI answer → snapshot observed at the CLI's fetch time", async () =>
{
    var (paths, s, _) = CollectorEnv(loggedIn: true);
    var fetched = DateTimeOffset.Now.AddSeconds(-3);
    var clock = DateTimeOffset.Now;
    using var c = new ClaudeUsageCollector(paths, () => s, new FakeSource(_ => new UsageFetch(UsageFetchKind.Ok, Body(UsageBody), "max", fetched)), () => clock);
    var env = (await c.CollectOnceAsync(CancellationToken.None))!;
    Eq(Statuses.Ok, env.Status);
    Eq(fetched, env.Snapshot!.ObservedAt);
    Eq("Max", env.PlanLabel);
    True(env.ProfileKey.StartsWith("claude-max-") && env.ProfileKey.Length > 15, "profile from account: " + env.ProfileKey);
    Near(30, env.Snapshot.Limits.AllWeek!.UsedPercent);
    Eq(300, env.EffectivePollIntervalSeconds);
    // The same fetch time again is not a new observation.
    clock = clock.AddMinutes(5);
    var again = (await c.CollectOnceAsync(CancellationToken.None, CollectTrigger.Timer))!;
    Eq(Statuses.Error, again.Status);
    Eq("not_fresh", again.ErrorCode);
});

TestAsync("collector: verified local CLI repair recovers without bypassing network or explicit waits", async () =>
{
    foreach (var (code,status,floor,shouldRecover) in new[] {
        ("cli_missing",Statuses.Error,(int?)null,true), ("cli_incompatible",Statuses.Error,(int?)null,true),
        ("cli_incompatible",Statuses.Error,(int?)86400,false), ("cli_untrusted",Statuses.Error,(int?)null,false),
        ("timeout",Statuses.Error,(int?)null,false), ("unavailable",Statuses.Error,(int?)null,false),
        ("rate_limit",Statuses.RateLimited,(int?)86400,false) })
    {
        var (paths,settings,_)=CollectorEnv(loggedIn:true); var now=T0.AddHours(4);
        var saved=new LatestEnvelope(1,ClaudeUsageCollector.SourceId,"p",null,now.AddMinutes(-2),status,3600,floor,code,null);
        AtomicFile.WriteAllText(paths.Latest,SnapshotJson.WriteEnvelope(saved));
        var source=new RepairSource(()=>new UsageFetch(UsageFetchKind.Ok,Body(UsageBody),"max",now)) { Ready=true };
        using var collector=new ClaudeUsageCollector(paths,()=>settings,source,()=>now);
        var result=await collector.CollectOnceAsync(default,CollectTrigger.Timer);
        Eq(shouldRecover,result?.Snapshot is not null,code+" "+floor);
        Eq(shouldRecover?1:0,source.Calls,"usage fetch count");
        if(!shouldRecover) Eq(SnapshotJson.WriteEnvelope(saved),File.ReadAllText(paths.Latest),"saved wait mutated");
    }
});

TestAsync("collector: local repair probes are bounded and a missing dependency does not clear the wait", async () =>
{
    var (paths,settings,_)=CollectorEnv(loggedIn:true); var now=T0.AddHours(4);
    AtomicFile.WriteAllText(paths.Latest,SnapshotJson.WriteEnvelope(new LatestEnvelope(1,ClaudeUsageCollector.SourceId,"p",null,now.AddMinutes(-2),Statuses.Error,3600,null,"cli_missing",null)));
    var source=new RepairSource(()=>new UsageFetch(UsageFetchKind.Ok,Body(UsageBody),"max",now));
    using var collector=new ClaudeUsageCollector(paths,()=>settings,source,()=>now);
    var before=collector.NotBefore;
    for(var i=0;i<10;i++) True(await collector.CollectOnceAsync(default,CollectTrigger.Timer) is null,"premature fetch");
    Eq(1,source.Checks);Eq(0,source.Calls);Eq(before,collector.NotBefore);
    source.Ready=true;now=now.AddMinutes(5);
    True((await collector.CollectOnceAsync(default,CollectTrigger.Timer))?.Snapshot is not null,"local repair never resumed");
});

TestAsync("R1: no account id → no snapshot, nothing shared; different accounts never share a key", async () =>
{
    var (paths, s, _) = CollectorEnv(loggedIn: true, account: null);
    using var c = new ClaudeUsageCollector(paths, () => s, new FakeSource(_ => new UsageFetch(UsageFetchKind.Ok, Body(UsageBody), "max", DateTimeOffset.Now)));
    var env = (await c.CollectOnceAsync(CancellationToken.None))!;
    Eq(Statuses.AuthRequired, env.Status);
    Eq("identity_unknown", env.ErrorCode);
    True(env.Snapshot is null, "no snapshot without identity");
    True(ClaudeProfiles.Resolve(null, "max") is null, "no fallback bucket");
    var a = ClaudeProfiles.Resolve(new ClaudeIdentity("acc-a", "org", null), "max")!.ProfileKey;
    var b = ClaudeProfiles.Resolve(new ClaudeIdentity("acc-b", "org", null), "max")!.ProfileKey;
    True(a != b, "same plan, different accounts must differ");
    Eq("claude-max5x-" + a[^10..], ClaudeProfiles.Resolve(new ClaudeIdentity("acc-a", "org", "default_claude_max_5x"), "max")!.ProfileKey, "tier changes the key");
});

TestAsync("collector: CLI states map to honest statuses", async () =>
{
    var cases = new (UsageFetchKind Kind, string Status, string Code)[]
    {
        (UsageFetchKind.NotLoggedIn, Statuses.AuthRequired, "not_logged_in"),
        (UsageFetchKind.CliMissing, Statuses.Error, "cli_missing"),
        (UsageFetchKind.CliUntrusted, Statuses.Error, "cli_untrusted"),
        (UsageFetchKind.CliIncompatible, Statuses.Error, "cli_incompatible"),
        (UsageFetchKind.Unavailable, Statuses.Error, "unavailable"),
        (UsageFetchKind.NotFresh, Statuses.Error, "not_fresh"),
        (UsageFetchKind.Timeout, Statuses.Error, "timeout"),
        (UsageFetchKind.BadOutput, Statuses.Error, "bad_output"),
    };
    foreach (var (kind, status, code) in cases)
    {
        var (paths, s, _) = CollectorEnv(loggedIn: true);
        using var c = new ClaudeUsageCollector(paths, () => s, new FakeSource(_ => new UsageFetch(kind)));
        var env = (await c.CollectOnceAsync(CancellationToken.None))!;
        Eq(status, env.Status, kind.ToString());
        Eq(code, env.ErrorCode, kind.ToString());
        True(env.Snapshot is null, "failure carries no snapshot");
    }
});

TestAsync("collector: Fable row missing → partial; impossible value → partial + flagged", async () =>
{
    var (paths, s, _) = CollectorEnv(loggedIn: true);
    var noFable = UsageBody.Replace("\"display_name\":\"Fable\"", "\"display_name\":\"Opus\"");
    using var c = new ClaudeUsageCollector(paths, () => s, new FakeSource(_ => new UsageFetch(UsageFetchKind.Ok, Body(noFable), "max", DateTimeOffset.Now)));
    var env = (await c.CollectOnceAsync(CancellationToken.None))!;
    Eq(Statuses.Partial, env.Status);
    True(env.Snapshot!.Limits.FableWeek is null, "Fable must be null");

    var (paths2, s2, _) = CollectorEnv(loggedIn: true);
    var bad = UsageBody.Replace("\"percent\":30", "\"percent\":-20");
    using var c2 = new ClaudeUsageCollector(paths2, () => s2, new FakeSource(_ => new UsageFetch(UsageFetchKind.Ok, Body(bad), "max", DateTimeOffset.Now)));
    var env2 = (await c2.CollectOnceAsync(CancellationToken.None))!;
    Eq(Statuses.Partial, env2.Status);
    Eq("value_out_of_range", env2.ErrorCode);
    True(env2.Snapshot!.Limits.AllWeek is null, "-20 must not be stored as 0");
});

TestAsync("R4: back-off doubles to 1 h; a stored long wait (24 h) is honoured after restart, by timer and by click", async () =>
{
    var (paths, s, _) = CollectorEnv(loggedIn: true);
    var src = new FakeSource(_ => new UsageFetch(UsageFetchKind.Unavailable));
    var clock = DateTimeOffset.Now.AddDays(-1);
    using (var c = new ClaudeUsageCollector(paths, () => s, src, () => clock))
    {
        var waits = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            var e = (await c.CollectOnceAsync(CancellationToken.None, CollectTrigger.Timer))!;
            waits.Add(e.EffectivePollIntervalSeconds);
            clock = c.NotBefore; // the timer fires exactly when the back-off ends
        }
        True(waits.SequenceEqual(new[] { 300, 600, 1200, 2400, 3600, 3600 }), "back-off: " + string.Join(",", waits));
    }
    // A previous run was told to wait 24 h.
    var attempted = DateTimeOffset.Now.AddHours(-1);
    AtomicFile.WriteAllText(paths.Latest, SnapshotJson.WriteEnvelope(new LatestEnvelope(1, ClaudeUsageCollector.SourceId, "p", null, attempted, Statuses.RateLimited, 86400, 86400, "http_429", null)));
    var src2 = new FakeSource(_ => new UsageFetch(UsageFetchKind.Ok, Body(UsageBody), "max", DateTimeOffset.Now));
    using var c2 = new ClaudeUsageCollector(paths, () => s, src2);
    True((c2.NotBefore - (attempted + TimeSpan.FromHours(24))).Duration() < TimeSpan.FromMilliseconds(2), "restored wait " + c2.NotBefore);
    True(!c2.TriggerNow(), "click refused inside the wait");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
    await c2.RunAsync(cts.Token);
    Eq(0, src2.Calls, "timer must not poll before the wait ends");
});

TestAsync("collector: interval change applies without a restart", async () =>
{
    var (paths, s, _) = CollectorEnv(loggedIn: true);
    var now = DateTimeOffset.Now;
    var src = new FakeSource(_ => new UsageFetch(UsageFetchKind.Ok, Body(UsageBody), "max", now = now.AddSeconds(1)));
    using var c = new ClaudeUsageCollector(paths, () => s, src);
    s.PollIntervalSeconds = 1800;
    Eq(1800, (await c.CollectOnceAsync(CancellationToken.None))!.EffectivePollIntervalSeconds);
});

// ---------------- review v2 regressions ----------------

TestAsync("V2-3: --collect-once (manual) and concurrent calls cannot break a stored wait; nothing is rewritten", async () =>
{
    var (paths, s, _) = CollectorEnv(loggedIn: true);
    var attempted = DateTimeOffset.Now.AddHours(-1);
    AtomicFile.WriteAllText(paths.Latest, SnapshotJson.WriteEnvelope(new LatestEnvelope(1, ClaudeUsageCollector.SourceId, "p", null, attempted, Statuses.RateLimited, 86400, 86400, "http_429", null)));
    var before = File.ReadAllBytes(paths.Latest);
    var src = new FakeSource(_ => new UsageFetch(UsageFetchKind.Ok, Body(UsageBody), "max", DateTimeOffset.Now));
    using var c = new ClaudeUsageCollector(paths, () => s, src);
    var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
        c.CollectOnceAsync(CancellationToken.None, i % 2 == 0 ? CollectTrigger.Manual : CollectTrigger.Timer)));
    True(results.All(r => r is null), "every call refused inside the 23 h wait");
    Eq(0, src.Calls, "source never ran");
    True(File.ReadAllBytes(paths.Latest).SequenceEqual(before), "latest.json (and its deadline) untouched");
    True((c.NotBefore - (attempted + TimeSpan.FromHours(24))).Duration() < TimeSpan.FromMilliseconds(2), "deadline unchanged");
});

TestAsync("V2-3: outside a wait, parallel manual calls run the source once", async () =>
{
    var (paths, s, _) = CollectorEnv(loggedIn: true);
    var src = new FakeSource(_ => new UsageFetch(UsageFetchKind.Ok, Body(UsageBody), "max", DateTimeOffset.Now));
    using var c = new ClaudeUsageCollector(paths, () => s, src);
    var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => c.CollectOnceAsync(CancellationToken.None)));
    Eq(1, src.Calls);
    Eq(1, results.Count(r => r is not null));
});

Test("V2-5: changed field types become BadOutput, never an exception", () =>
{
    string Wrap(string response) => "{\"type\":\"control_response\",\"response\":" + response + "}";
    var cases = new[]
    {
        Wrap("""{"subtype":7,"request_id":"quota-widget-usage"}"""),
        Wrap("""{"subtype":[],"request_id":"quota-widget-usage"}"""),
        Wrap("[]"),
        Wrap("""{"subtype":"success","request_id":"quota-widget-usage","response":[]}"""),
        Wrap("""{"subtype":"success","request_id":"quota-widget-usage","response":{"rate_limits_available":"yes"}}"""),
        Wrap("""{"subtype":"success","request_id":"quota-widget-usage","response":{"rate_limits_available":true,"subscription_type":5,"rate_limits":{}}}"""),
        Wrap("""{"subtype":"success","request_id":"quota-widget-usage","response":{"rate_limits_available":true,"rate_limits":[1,2]}}"""),
        Wrap("""{"subtype":"success","request_id":"someone-else","response":{"rate_limits_available":true,"rate_limits":{}}}"""),
        """{"type":"assistant","response":{"subtype":"success","request_id":"quota-widget-usage"}}""",
        "[]", "7", "null",
    };
    foreach (var line in cases)
        Eq(UsageFetchKind.BadOutput, ClaudeCliUsageSource.ParseControlResponse(line).Kind, line);
    True(ClaudeCliUsageSource.FreshFetchTime(null, long.MaxValue, DateTimeOffset.Now) is null, "absurd stamp ignored, no throw");
    True(ClaudeCliUsageSource.FreshFetchTime(null, -5, DateTimeOffset.Now) is null, "negative stamp ignored");
});

Test("V2-1: CLI chosen by protocol capability, not by being first on PATH", () =>
{
    TestEnvironment.RequireLive();
    var all = ClaudeCli.Candidates().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (all.Count == 0) { Console.WriteLine("       (skipped: no claude.exe)"); return; }
    foreach (var p in all)
    {
        var v = ClaudeCli.FileVersion(p);
        var ok = ClaudeCli.HasRequiredMarkers(p);
        if (v == new Version(2, 1, 183, 0)) True(!ok, "2.1.183 lacks the fetch stamp and must be rejected");
        if (v is not null && v >= new Version(2, 1, 281)) True(ok, $"{v} should be compatible");
    }
    var choice = ClaudeCli.Resolve(null);
    if (all.Any(ClaudeCli.HasRequiredMarkers))
    {
        True(choice.Usable, "a compatible signed CLI exists, so one must be chosen: " + choice.Summary);
        True(ClaudeCli.HasRequiredMarkers(choice.Path!), "chosen CLI is compatible");
    }
    var old = all.FirstOrDefault(p => !ClaudeCli.HasRequiredMarkers(p));
    if (old is not null)
    {
        var forced = ClaudeCli.Resolve(old);
        Eq("cli_incompatible", forced.Problem, "a configured old CLI is reported, not silently replaced");
    }
});

Test("V2-2: one login path — script delegates to the app; failed checks start nothing", () =>
{
    var script = File.ReadAllText(FindRepoFile("登录Claude.cmd"));
    True(script.Contains("QuotaWidget.exe\" --login"), "script must call the app's --login");
    True(!script.Contains("auth login") && !script.Contains("where claude"), "script must not run claude.exe itself");

    var paths = new DataPaths(TempDir());
    var cfg = Path.Combine(paths.Root, "claude-auth");
    var started = new List<System.Diagnostics.ProcessStartInfo>();
    System.Diagnostics.Process? Start(System.Diagnostics.ProcessStartInfo p) { started.Add(p); return null; }
    var bad = new CliChoice("x.exe", "2.1.183", "cli_incompatible", "Claude Code 2.1.183 不支持所需的额度接口，请更新");
    True(ClaudeCli.LaunchAuthConsole(paths, bad, cfg, false, _ => true, Start) is not null, "incompatible CLI refused");
    var good = new CliChoice(@"C:\fake\claude.exe", "2.1.284", null, "Claude Code 2.1.284");
    True(ClaudeCli.LaunchAuthConsole(paths, good, cfg, false, _ => false, Start) is not null, "ACL failure must abort");
    Eq(0, started.Count, "nothing started after a failed check");

    Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "should-not-leak");
    Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "http://elsewhere.invalid");
    try
    {
        True(ClaudeCli.LaunchAuthConsole(paths, good, cfg, false, _ => true, Start) is null, "good path starts");
    }
    finally
    {
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", null);
    }
    var psi = started.Single();
    True(!psi.Environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN") && !psi.Environment.ContainsKey("ANTHROPIC_BASE_URL"), "inherited Claude/Anthropic variables removed");
    Eq(cfg, psi.Environment["CLAUDE_CONFIG_DIR"]);
    Eq(@"C:\fake\claude.exe",psi.FileName,"runs exactly the verified binary");
    True(psi.ArgumentList.SequenceEqual(new[] {"auth","login","--claudeai"}),"auth arguments must not pass through a shell");
    True(!File.Exists(Path.Combine(paths.Root,"login-claude.cmd")),"no generated command script");
});

// ---------------- the real CLI, without any account ----------------

TestAsync("cli: control response parsing", () =>
{
    var notLogged = """{"type":"control_response","response":{"subtype":"success","request_id":"quota-widget-usage","response":{"session":{},"subscription_type":null,"rate_limits_available":false,"rate_limits":null,"behaviors":null}}}""";
    Eq(UsageFetchKind.NotLoggedIn, ClaudeCliUsageSource.ParseControlResponse(notLogged).Kind);
    var ok = """{"type":"control_response","response":{"subtype":"success","request_id":"quota-widget-usage","response":{"subscription_type":"max","rate_limits_available":true,"rate_limits":{"limits":[]}}}}""";
    var r = ClaudeCliUsageSource.ParseControlResponse(ok);
    Eq(UsageFetchKind.Ok, r.Kind);
    Eq("max", r.SubscriptionType);
    var down = """{"type":"control_response","response":{"subtype":"success","request_id":"quota-widget-usage","response":{"subscription_type":"max","rate_limits_available":true,"rate_limits":null}}}""";
    Eq(UsageFetchKind.Unavailable, ClaudeCliUsageSource.ParseControlResponse(down).Kind);
    var err = """{"type":"control_response","response":{"subtype":"error","request_id":"quota-widget-usage","error":"nope"}}""";
    Eq(UsageFetchKind.CliFailed, ClaudeCliUsageSource.ParseControlResponse(err).Kind);
    Eq(UsageFetchKind.BadOutput, ClaudeCliUsageSource.ParseControlResponse("not json").Kind);
    return Task.CompletedTask;
});

Test("cli: only a fetch stamped during this run counts as fresh", () =>
{
    var started = DateTimeOffset.Now;
    var ms = started.AddSeconds(1).ToUnixTimeMilliseconds();
    True(ClaudeCliUsageSource.FreshFetchTime(null, ms, started) is not null, "first fetch");
    True(ClaudeCliUsageSource.FreshFetchTime(ms, ms, started) is null, "stamp unchanged = cached answer");
    True(ClaudeCliUsageSource.FreshFetchTime(null, started.AddMinutes(-5).ToUnixTimeMilliseconds(), started) is null, "old stamp");
    True(ClaudeCliUsageSource.FreshFetchTime(null, null, started) is null, "no stamp");
});

Test("cli: Anthropic signature accepted, unsigned binary refused", () =>
{
    TestEnvironment.RequireLive();
    var claude = ClaudeCli.Candidates().FirstOrDefault(File.Exists);
    if (claude is null) { Console.WriteLine("       (skipped: no claude.exe)"); return; }
    True(Authenticode.IsSignedBy(claude), "official CLI should verify: " + claude);
    True(!Authenticode.IsSignedBy(Environment.ProcessPath!), "unsigned test host must be refused");
    var copy = Path.Combine(TempDir(), "claude.exe");
    File.WriteAllBytes(copy, File.ReadAllBytes(Environment.ProcessPath!));
    True(!Authenticode.IsSignedBy(copy), "renamed unsigned file must be refused");
});

Test("cli: login directory is private to this user", () =>
{
    var dir = Path.Combine(TempDir(), "claude-auth");
    True(ClaudeCli.EnsurePrivateDirectory(dir), "private ACL applied");
    var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
    var sys = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
    foreach (System.Security.AccessControl.FileSystemAccessRule r in new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        True(r.IdentityReference.Equals(me) || r.IdentityReference.Equals(sys), "unexpected ACE " + r.IdentityReference);
});

TestAsync("cli: real claude.exe in an empty config dir answers 'not logged in' (no account, no prompt)", async () =>
{
    TestEnvironment.RequireLive();
    if (ClaudeCli.Candidates().FirstOrDefault(File.Exists) is null) { Console.WriteLine("       (skipped: no claude.exe)"); return; }
    var root = TempDir();
    var cfg = Path.Combine(root, "cfg");
    var src = new ClaudeCliUsageSource(() => null, Path.Combine(root, "work"));
    var r = await src.FetchAsync(cfg, CancellationToken.None);
    Eq(UsageFetchKind.NotLoggedIn, r.Kind, "kind");
    True(!File.Exists(ClaudeConfigFiles.CredentialsPath(cfg)), "no credentials created");
});

// ---------------- regression coverage ----------------

WidgetModel ModelWith(DataPaths paths, Func<DateTimeOffset> clock)
{
    var m = new WidgetModel(paths, false, clock);
    m.Initialize();
    return m;
}

void Feed(WidgetModel m, DataPaths paths, HistoryRecord r)
{
    AtomicFile.WriteAllText(paths.Latest, SnapshotJson.WriteEnvelope(new LatestEnvelope(1, r.SourceId, r.ProfileKey, r.PlanLabel, r.AttemptedAt, r.Status, r.PollSeconds, null, null, r.Snapshot)));
    m.PollLatest();
}

Test("dashboard: Max Fable is half native points throughout rate, cumulative and average displays", () =>
{
    var root = TempDir();
    var paths = new DataPaths(root);
    var now = T0.AddHours(1);
    var m = ModelWith(paths, () => now);
    foreach (var r in Steady(13, 1, fableFactor: 2)) Feed(m, paths, r);
    var raw = m.BuildView(now, 60);
    var nativeJson = SnapshotJson.WriteEnvelope(m.LastEnvelope!);
    foreach (var mode in new[] { "rate", "cumulative" })
    {
        var settings = new WidgetSettings { ChartMode = mode, RangeMinutes = 60 };
        settings.SetQ(m.SourceId!, m.ProfileKey!, 8); // obsolete user values cannot silently alter the unit
        var d = Dashboard.Combine(raw, raw, settings, now, null);
        Near(.5, d.Chart.FableToClaudeFactor);
        Near(12, RateEngine.SumRange(d.Chart.Fable, T0, now).Delta);
        Near(24, RateEngine.SumRange(raw.Chart.Fable, T0, now).Delta, "native model was mutated");
        Near(12, d.Chart.FableTrend.Delta);
        Near(12, d.Chart.TotalTrend.Delta);
        Near(12, d.Chart.CodexTrend!.Delta, "Codex scaled by mistake");
        Near(mode == "rate" ? 12 : 6, d.Chart.FableAt(T0.AddMinutes(30)));
        True(d.Detail.Contains("累计平均 12 点/h（Claude 总周额度"), "average kept native Fable units");
        True(d.Summary.Contains("12 / 12 / 12"), "summary units differ from graph");
    }
    Near(34, raw.Fable.UsedPercent, "ring must retain own quota percentage");
    Eq(nativeJson, SnapshotJson.WriteEnvelope(m.LastEnvelope!), "raw stored snapshot changed");
});

Test("dashboard: conversion requires a known Max plan, not another account's legacy factor", () =>
{
    foreach (var plan in new[] { "Max", "Max (5x)", "Max (20x)", "max (5x)" }) Near(.5, QuotaUnits.FableToClaude(plan));
    foreach (var plan in new string?[] { null, "Claude", "Pro", "Team", "Enterprise", "Maxwell" })
        True(QuotaUnits.FableToClaude(plan) is null, "invented entitlement for " + plan);
    var root = TempDir(); var paths = new DataPaths(root); var now = T0.AddHours(1);
    var m = ModelWith(paths, () => now);
    foreach (var r in Steady(13, 1, fableFactor: 2)) Feed(m, paths, r with { PlanLabel = "Team", ProfileKey = "another-account" });
    var raw = m.BuildView(now,60);
    var settings = new WidgetSettings { RangeMinutes = 60 };
    settings.SetQ(m.SourceId!, m.ProfileKey!, .5);
    var d = Dashboard.Combine(raw,raw,settings,now,null);
    True(d.Chart.FableToClaudeFactor is null && d.Detail.Contains("换算比例未确认"), "unknown plan treated as Max");
    Near(24, d.Chart.FableTrend.Delta);
});

Test("chart: reset wins over concurrent restart, and display scaling preserves every real boundary", () =>
{
    var records = new[] { Rec(0,80,28), Rec(5,0,0,weekReset:R2), Rec(10,1,2,weekReset:R2) };
    var events = new[] { new AppEvent(T0.AddMinutes(2),EventTypes.AppExit), new AppEvent(T0.AddMinutes(4),EventTypes.AppStart) };
    var raw = RateEngine.Build(records,SeriesKey.Fable,events);
    var display = QuotaUnits.Scale(raw,.5);
    Eq(SegmentIssue.Reset,display.Segments[0].Issue);
    Eq("重置",display.Segments[0].Label);
    Eq(raw.Segments[1].Group,display.Segments[1].Group);
    var trend = RateTrend.Build(display,T0,T0.AddMinutes(10));
    True(trend.ValueAt(T0.AddMinutes(2)) is null,"reset bridged");
    Near(1,trend.Delta);
    Eq(-28d,raw.Segments[0].Delta,"reset history changed");
});

Test("CLI: Desktop flat and hashed payload layouts are discovered without arbitrary recursion", () =>
{
    var root = TempDir();
    string Exe(string relative) { var p=Path.Combine(root,relative,"claude.exe"); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p,"not an executable"); return p; }
    var flat=Exe("2.1.284"); var nested=Exe(Path.Combine("2.1.286","635c1867224a"));
    var deep=Exe(Path.Combine("2.1.286","635c1867224a","unrelated"));
    var found=ClaudeCli.BundledExecutables(root).ToArray();
    Eq(2,found.Length); True(found.Contains(flat)&&found.Contains(nested)&&!found.Contains(deep),"wrong discovery depth");
    True(!ClaudeCli.Resolve(nested).Usable,"discovery bypassed signature verification");
    True(!ClaudeCli.BundledExecutables(Path.Combine(root,"gone")).Any(),"removed root should be tolerated");
});

Test("chart: a brief repair restart does not mask the preceding collection failures", () =>
{
    var events=new[]{new AppEvent(T0.AddHours(1),EventTypes.CollectFail,Statuses.Error,"cli_incompatible"),
        new AppEvent(T0.AddHours(5),EventTypes.AppExit),new AppEvent(T0.AddHours(5).AddSeconds(2),EventTypes.AppStart)};
    Eq("采集失败",RateEngine.ClassifyEvents(events,T0,T0.AddHours(6)));
});

Test("R2: Fable q belongs to one account/plan; a new profile starts unknown", () =>
{
    var root = TempDir();
    var paths = new DataPaths(root);
    var now = T0.AddMinutes(30);
    var m = ModelWith(paths, () => now);
    foreach (var r in Steady(4, 0.5)) Feed(m, paths, r);
    m.SetCurrentQ(0.5);
    Near(0.5, m.CurrentQ);
    var version = m.ProfileVersion;
    foreach (var r in Steady(4, 0.5)) Feed(m, paths, r with { ProfileKey = "p2", Snapshot = r.Snapshot with { Id = r.Snapshot.Id + "-b", ObservedAt = r.T.AddSeconds(5) } });
    Eq("p2", m.ProfileKey);
    True(m.ProfileVersion > version, "window is told to refresh the q box");
    True(m.CurrentQ is null, "account B must not inherit A's q");
    var v = m.BuildView(now);
    Near(0.75, v.SummaryFable, "new profile uses its own raw Fable increment");
    var reloaded = WidgetSettings.Load(paths.Settings, out _);
    Near(0.5, reloaded.QFor("claude-oauth-usage", "p1"), "A's q persisted for A only");
    True(reloaded.QFor("claude-oauth-usage", "p2") is null, "nothing stored for B");
});

Test("R3: 30/60-minute polling still draws, labelled as interval averages", () =>
{
    foreach (var minutes in new[] { 5, 10, 30, 60 })
    {
        var recs = Enumerable.Range(0, 7).Select(i => Rec(i * minutes, 10 + i, 10 + i / 2.0, poll: minutes * 60)).ToList();
        var s = RateEngine.Build(recs, SeriesKey.Total, []);
        var valid = s.Segments.Count(x => x.Valid);
        var smooth = s.Segments.Count(x => x.Smooth is not null);
        Eq(6, valid, $"{minutes}m valid");
        True(smooth >= 5, $"{minutes}m: only {smooth} smoothed points");
        var mid = T0.AddMinutes(minutes * 3.5);
        Near(60.0 / minutes, s.ValueAt(mid, smooth: true), $"{minutes}m value");
        var span = s.SmoothMinutesAt(mid);
        if (minutes >= 30) Near(minutes, span, $"{minutes}m span is the interval itself");
        else True(span <= 15.5, $"{minutes}m span {span} should be ≤ 15 min");
    }
});

Test("R5: millisecond jitter across a grid line is the same window; a real reset is not; slow drift breaks", () =>
{
    var grid = new DateTimeOffset(2026, 10, 4, 0, 5, 0, TimeSpan.Zero);
    var a = UsageParser.Limit(10, grid.AddMilliseconds(-1))!;
    var b = UsageParser.Limit(11, grid.AddMilliseconds(1))!;
    True(RateEngine.SameWindow(a, b), "2 ms apart must match");
    True(!RateEngine.SameWindow(a, UsageParser.Limit(0, grid.AddDays(7))!), "a week apart is a new window");
    True(!RateEngine.SameWindow(a, UsageParser.Limit(0, grid.AddHours(5))!), "5 h apart is a new window");
    // resets_at creeping 1 min per sample: each step within tolerance, but not the window as a whole
    var recs = Enumerable.Range(0, 6).Select(i => Rec(i * 5, 40 + i * 0.5, weekReset: R1.AddMinutes(i))).ToList();
    var s = RateEngine.Build(recs, SeriesKey.Total, []);
    True(s.Segments.Any(x => x.Issue == SegmentIssue.Reset && x.Label == "周期变化"), "drift must break the line");
    True(RateEngine.Cumulative(recs, SeriesKey.Total, s) is { } c && c.From > recs[0].T, "cumulative must not span the drift");
});

Test("O2: another account's failure does not mark this account's interval", () =>
{
    var recs = new List<HistoryRecord> { Rec(0, 10), Rec(5, 11) };
    var other = new AppEvent(T0.AddMinutes(2), EventTypes.CollectFail, Statuses.Error, "network", "claude-oauth-usage", "someone-else");
    var mine = other with { ProfileKey = "p1" };
    var sleep = new AppEvent(T0.AddMinutes(2), EventTypes.Suspend);
    True(RateEngine.Build(recs, SeriesKey.Total, RateEngine.RelevantEvents([other], "claude-oauth-usage", "p1")).Segments[0].Valid, "foreign failure ignored");
    Eq("断网", RateEngine.Build(recs, SeriesKey.Total, RateEngine.RelevantEvents([mine], "claude-oauth-usage", "p1")).Segments[0].Label);
    Eq("休眠", RateEngine.Build(recs, SeriesKey.Total, RateEngine.RelevantEvents([sleep], "claude-oauth-usage", "p1")).Segments[0].Label, "power events are global");
});

Test("E1: a long-running model drops what fell out of the 8-day window", () =>
{
    var root = TempDir();
    var paths = new DataPaths(root);
    var now = T0.AddMinutes(20);
    var m = ModelWith(paths, () => now);
    foreach (var r in Steady(4, 0.5)) Feed(m, paths, r);
    m.RecordEvent(new AppEvent(T0.AddMinutes(1), EventTypes.Suspend));
    Eq(4, m.Records.Count);
    now = T0.AddDays(9);
    Feed(m, paths, Rec(9 * 1440, 60, 30) with { Snapshot = Rec(9 * 1440, 60, 30).Snapshot with { Id = "late" } });
    Eq(1, m.Records.Count, "only the new record remains in memory");
    True(m.EventList.All(e => e.T >= now - WidgetModel.Lookback), "old events dropped");
    True(m.Store.IndexCount <= 1, "dedupe index bounded: " + m.Store.IndexCount);
    // a snapshot older than the window is refused, not appended
    Feed(m, paths, Rec(10, 40.5, 10.25) with { Snapshot = Rec(10, 40.5, 10.25).Snapshot with { Id = "ancient" } });
    Eq("快照早于保留期 · 已忽略", m.LatestError);
    Eq(1, m.Records.Count);
});

Test("E2: curves are rebuilt only when data changes", () =>
{
    var root = TempDir();
    var paths = new DataPaths(root);
    var now = T0.AddMinutes(60);
    var m = ModelWith(paths, () => now);
    foreach (var r in Steady(13, 0.5)) Feed(m, paths, r);
    var v1 = m.BuildView(now);
    now = now.AddSeconds(15);
    var v2 = m.BuildView(now);
    True(ReferenceEquals(v1.Chart.Total, v2.Chart.Total), "clock tick reused the cached series");
    Feed(m, paths, Rec(65, 46.5, 13.25));
    True(!ReferenceEquals(v2.Chart.Total, m.BuildView(now).Chart.Total), "new data rebuilt the series");
});

// ---------------- atomic file ----------------

Test("atomic: concurrent reader never sees a torn latest.json", () =>
{
    var root = TempDir();
    var path = Path.Combine(root, "latest.json");
    var big = SnapshotJson.WriteEnvelope(new LatestEnvelope(1, "src", "p", new string('x', 40), T0, Statuses.Ok, 300, null, null, Rec(0, 40, 10).Snapshot));
    AtomicFile.WriteAllText(path, big);
    var stop = false;
    var bad = 0;
    var reads = 0;
    var reader = new Thread(() =>
    {
        while (!Volatile.Read(ref stop))
        {
            var t = AtomicFile.TryReadAllText(path);
            if (t is null) continue;
            reads++;
            try { JsonDocument.Parse(t).Dispose(); } catch { bad++; }
        }
    });
    reader.Start();
    for (var i = 0; i < 300; i++) AtomicFile.WriteAllText(path, big.Replace("\"usedPercent\": 40", $"\"usedPercent\": {i % 100}"));
    Volatile.Write(ref stop, true);
    reader.Join();
    Eq(0, bad, "torn reads");
    True(reads > 0, "reader ran");
    Directory.Delete(root, true);
});

// ---------------- run ----------------

ChatCacheTests.Register(tests);
CodexTests.Register(tests);
CumulativeTests.Register(tests);
TrendTests.Register(tests);
RangeTests.Register(tests);
PeakLabelTests.Register(tests);
ChatSessionTests.Register(tests);
ChatListTests.Register(tests);
ChatLifecycleTests.Register(tests);
FloatingResetTests.Register(tests);
TokenTests.Register(tests);
ChatOwnershipTests.Register(tests);
CalendarTests.Register(tests);
RecentUsageRateTests.Register(tests);
MonitoringTests.Register(tests);
ActivityPublicationTests.Register(tests);
FableDisplayTests.Register(tests);
AlignmentTests.Register(tests);
WorkActivityTests.Register(tests);
ActiveRateEstimatorTests.Register(tests);
ResetRateTests.Register(tests);
ContinuousResetTests.Register(tests);
TimeAxisTests.Register(tests);
LocalizationTests.Register(tests);
ConnectionTests.Register(tests);
ChatQuotaTests.Register(tests);
var failed = 0; var skipped = 0;
foreach (var (name, body) in tests)
{
    try
    {
        await body();
        Console.WriteLine("  ok   " + name);
    }
    catch (SkipTestException e) { skipped++; Console.WriteLine("  skip "+name+": "+e.Message); }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine("  FAIL " + name + "\n       " + e.Message.Replace("\n", "\n       "));
    }
}
Console.WriteLine($"{tests.Count - failed - skipped}/{tests.Count - skipped} passed; {skipped} optional checks skipped");
return failed == 0 ? 0 : 1;

sealed class RepairSource(Func<UsageFetch> answer) : IUsageSource, ILocalUsageRecovery
{
    public bool Ready { get; set; }
    public int Checks { get; private set; }
    public int Calls { get; private set; }
    public bool IsLocalDependencyReady() { Checks++; return Ready; }
    public Task<UsageFetch> FetchAsync(string configDir,CancellationToken ct) { Calls++;return Task.FromResult(answer()); }
}

sealed class FakeSource(Func<string, UsageFetch> answer) : IUsageSource
{
    public int Calls { get; private set; }

    public Task<UsageFetch> FetchAsync(string configDir, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(answer(configDir));
    }
}
