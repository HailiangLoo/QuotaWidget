namespace QuotaWidget.Core;

/// <summary>
/// Synthetic history for previewing the window. It is written only under the demo root with
/// sourceId "demo-only", which the real history store refuses. q=0.5 here is a demo assumption.
/// </summary>
public static class DemoData
{
    public const string SourceId = "demo-only";
    public const string ProfileKey = "demo-max-5x";
    public const double DemoQ = 0.5;

    public static void Generate(DataPaths paths, DateTimeOffset now, string scenario, bool codex = false)
    {
        if (!paths.Root.TrimEnd('\\', '/').EndsWith("demo", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("demo data may only be generated under the demo root");
        foreach (var dir in new[] { paths.HistoryDir, paths.EventsDir })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        if (File.Exists(paths.Latest)) File.Delete(paths.Latest);

        var settings = WidgetSettings.Load(paths.Settings, out _);
        settings.CollectorEnabled = false;
        settings.FableToWeekByProfile.Clear();
        settings.SetQ(SourceId, ProfileKey, scenario == "unknown-q" ? null : DemoQ);
        settings.LastSourceId = SourceId;
        settings.LastProfileKey = ProfileKey;
        settings.Save(paths.Settings);
        if (scenario == "empty") return;

        var store = new HistoryStore(paths, demoMode: true);
        var events = new EventLog(paths);
        var rnd = new Random(7);
        var plan = codex ? scenario=="codex-plus"?"Codex Plus（演示）":"Codex Pro（演示）" : "Max (5x)";
        var lastT = scenario == "stale" ? now.AddMinutes(-22) : now.AddMinutes(-1);
        var t0 = lastT.AddHours(-26);
        var weekReset = lastT.AddHours(-15);
        var sleep = (lastT.AddHours(-3.2), lastT.AddHours(-2.6));
        var unexplained = (lastT.AddHours(-8), lastT.AddHours(-7.65));
        var notRunning = (lastT.AddHours(-11), lastT.AddHours(-10.6));
        var offline = (lastT.AddHours(-18), lastT.AddHours(-17.7));
        var flat = (lastT.AddHours(-5.5), lastT.AddHours(-5.15));

        double week = 52, fab = 18, five = 10;
        var weekResetAt = weekReset;
        var fiveResetAt = t0.AddHours(3);
        DateTimeOffset? prev = null;
        bool In((DateTimeOffset a, DateTimeOffset b) w, DateTimeOffset t) => t >= w.a && t < w.b;

        events.Append(new AppEvent(notRunning.Item1.AddSeconds(30), EventTypes.AppExit));
        events.Append(new AppEvent(notRunning.Item2.AddSeconds(-40), EventTypes.AppStart));
        events.Append(new AppEvent(sleep.Item1.AddSeconds(20), EventTypes.Suspend));
        events.Append(new AppEvent(sleep.Item2.AddSeconds(-30), EventTypes.Resume));
        for (var f = offline.Item1.AddMinutes(1); f < offline.Item2; f = f.AddMinutes(5))
            events.Append(new AppEvent(f, EventTypes.CollectFail, Statuses.Error, "network", SourceId, ProfileKey));

        var step = scenario == "slow" ? 30 : 5; // "slow": 30-minute polling
        settings.PollIntervalSeconds = step * 60;
        settings.Save(paths.Settings);
        for (var t = t0; t <= lastT; t = t.AddMinutes(step))
        {
            var obs = t.AddSeconds(rnd.Next(-8, 9));
            if (In(sleep, obs) || In(unexplained, obs) || In(notRunning, obs) || In(offline, obs)) continue;
            if (prev is { } p)
            {
                var hours = (obs - p).TotalHours;
                var h = (obs - t0).TotalHours;
                var rate = h % 6 < 2 ? 1.6 + 1.1 * Math.Sin(h * 3.3) : h % 6 < 4 ? 4.2 + 1.4 * Math.Sin(h * 4.6) : 2.2 + 1.2 * Math.Sin(h * 5.4);
                if (In(flat, obs)) rate = 0;
                if (codex) rate = 0.9 + 0.75 * Math.Sin(h * 1.7);
                rate = Math.Max(0, rate);
                var share = 0.15 + 0.35 * (1 + Math.Sin(h / 2.4)) / 2;
                if (p < weekResetAt && obs >= weekResetAt) { week = 0; fab = 0; weekResetAt = weekResetAt.AddDays(7); }
                if (obs >= fiveResetAt) { five = 0; fiveResetAt = obs.AddHours(5); }
                week = Math.Min(100, week + rate * hours);
                fab = Math.Min(100, fab + rate * share * hours / DemoQ);
                five = Math.Min(100, five + rate * hours * 4.5);
            }
            prev = obs;
            var fableMissing = obs < t0.AddMinutes(40);
            var limits = new QuotaLimits(
                codex&&scenario!="codex-plus"?null:UsageParser.Limit(Math.Round(five, 2), fiveResetAt),
                UsageParser.Limit(Math.Round(week, 2), weekResetAt),
                codex||fableMissing ? null : UsageParser.Limit(Math.Round(fab, 2), weekResetAt));
            var snap = new QuotaSnapshot("demo-" + obs.UtcDateTime.ToString("yyyyMMddTHHmmss"), obs, limits);
            var rec = new HistoryRecord(SourceId, ProfileKey, plan, obs.AddSeconds(-1),
                codex||fableMissing ? Statuses.Partial : Statuses.Ok, step * 60, snap);
            store.Append(rec);
            if (t.AddMinutes(step) > lastT)
            {
                var env = scenario == "stale"
                    ? new LatestEnvelope(1, SourceId, ProfileKey, plan, now.AddMinutes(-2), Statuses.Error, 600, null, "network", null)
                    : new LatestEnvelope(1, SourceId, ProfileKey, plan, rec.AttemptedAt, rec.Status, step * 60, null, null, snap);
                AtomicFile.WriteAllText(paths.Latest, SnapshotJson.WriteEnvelope(env));
            }
        }
    }
}
