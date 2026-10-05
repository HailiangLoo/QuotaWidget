using QuotaWidget.Core;

static class RangeTests
{
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        void Test(string name, Action body) => tests.Add(("ranges: " + name, () => { body(); return Task.CompletedTask; }));
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        void Choices(double minutes, params int[] expected) => Check(ChartRanges.Available(new HistoryBounds(now.AddMinutes(-minutes), now)).SequenceEqual(expected), "wrong tabs at " + minutes);
        Test("empty and short history have a usable all view", () =>
        {
            Check(ChartRanges.Available().SequenceEqual(new[] { 0 }), "empty choices");
            Choices(0, 0); Choices(59.99, 0); Choices(60, 60, 0); Choices(179, 60, 0);
            Check(ChartRanges.Select(300, ChartRanges.Available(new HistoryBounds(now.AddHours(-2), now))) == 0, "hidden tab remained selected");
        });
        Test("3d becomes available after 24h with partial history and all remains available", () =>
        {
            Choices(299.99, 60, 0); Choices(300, 60, 300, 0);
            Choices(720, 60, 300, 720, 0); Choices(1439.99, 60, 300, 720, 0);
            Choices(1440, 60, 300, 720, 1440, 4320, 0);
            Choices(4319.99, 60, 300, 720, 1440, 4320, 0);
            Choices(4320, 60, 300, 720, 1440, 4320, 0);
            Choices(20000, 60, 300, 720, 1440, 4320, 0);
            Check(WidgetModel.RangeLabel(4320)=="3d"&&WidgetModel.RangeLabel(0)=="all","long-range labels wrong");
        });
        Test("idle time and unrelated provider observations do not unlock long ranges", () =>
        {
            var a = new HistoryBounds(now.AddDays(-9), now.AddDays(-9).AddHours(2));
            var b = new HistoryBounds(now.AddHours(-1), now);
            Check(ChartRanges.Available(a, b).SequenceEqual(new[] { 60, 0 }), "wall clock or cross-provider span was used");
        });
        Test("all selection survives settings save and reload", () => WithRoot(paths =>
        {
            var settings = new WidgetSettings { RangeMinutes = 0 };
            settings.Save(paths.Settings);
            Check(WidgetSettings.Load(paths.Settings, out _).RangeMinutes == 0, "all was normalized away");
        }));
        Test("restart restores older-than-8-days archive, without expanding live index", () => WithRoot(paths =>
        {
            Seed(paths);
            var m = new WidgetModel(paths, false, () => now); m.Initialize();
            Check(m.Records.Count == 2 && m.Store.IndexCount == 2, "live retention changed");
            Check(m.Bounds?.Span.TotalDays > 9, "old disk history not reflected in tabs");
            var all = m.BuildView(now, 0);
            Check(Math.Abs(all.SummaryTotal - 3) < 1e-9, "old cumulative missing or gap counted");
            Check(all.Chart.Start == now.AddDays(-10), "all truncated to live window");
            Check(m.Store.IndexCount == 2 && m.Records.Count == 2 && m.ArchiveLoaded, "archive leaked into live store");
            m.BuildView(now, 60);
            Check(!m.ArchiveLoaded, "archive retained after leaving all view");
            var restarted = new WidgetModel(paths, false, () => now); restarted.Initialize();
            Check(Math.Abs(restarted.BuildView(now, 0).SummaryTotal - 3) < 1e-9, "history changed on restart");
        }));
        Test("all view respects old suspend evidence and profile isolation", () => WithRoot(paths =>
        {
            Seed(paths);
            new EventLog(paths).Append(new AppEvent(now.AddDays(-10).AddMinutes(2), EventTypes.Suspend));
            var m = new WidgetModel(paths, false, () => now); m.Initialize();
            Check(Math.Abs(m.BuildView(now, 0).SummaryTotal - 1) < 1e-9, "old suspend event lost");
            var settings = m.Settings; settings.LastProfileKey = "other"; settings.Save(paths.Settings);
            var other = new WidgetModel(paths, false, () => now); other.Initialize();
            Check(other.Bounds is null && other.BuildView(now, 0).SummaryTotal == 0, "another account's history leaked");
        }));
        Test("all and 5h keep the same tail across a live restart with sub-millisecond timestamps", () => WithRoot(paths =>
        {
            Seed(paths);
            new EventLog(paths).Append(new AppEvent(now.AddMinutes(-3).AddTicks(1234), EventTypes.AppExit));
            var m = new WidgetModel(paths, false, () => now); m.Initialize();
            m.RecordEvent(new AppEvent(now.AddMinutes(-3).AddSeconds(2).AddTicks(4321), EventTypes.AppStart));
            Check(m.EventList[^1] == m.Events.Load(now.AddMinutes(-5))[^1], "new event differs from its disk identity");
            // Also exercise archive merging with a legacy/high-precision in-memory copy.
            m.EventList[^1] = m.EventList[^1] with { T = m.EventList[^1].T.AddTicks(4321) };
            var live = m.BuildView(now, 300).Chart;
            Check(live.Total.Segments[^1].Valid, "fixture restart should not miss a sample");
            var all = m.BuildView(now, 0).Chart;
            Check(all.Total.Segments[^1].Valid, "disk and in-memory startup duplicated into a false gap");
            for (var seconds = 0; seconds <= 300; seconds += 30)
            {
                var at = now.AddSeconds(-seconds);
                Check(Math.Abs(all.TotalTrend.ValueAt(at)!.Value - live.TotalTrend.ValueAt(at)!.Value) < 1e-9,
                    "same measured tail changed with range selection");
            }
            Check(Math.Abs(m.BuildView(now, 0).SummaryTotal - 3) < 1e-9, "archive lost recorded consumption");
            var restarted = new WidgetModel(paths, false, () => now); restarted.Initialize();
            Check(restarted.BuildView(now, 0).Chart.Total.Segments[^1].Valid, "restart changed archived tail");
        }));
        Test("deduping restart copies does not hide a real outage or concurrent failure", () =>
        {
            foreach (var kind in new[] { "long-restart", "failure", "suspend" }) WithRoot(paths =>
            {
                Seed(paths,kind=="long-restart"?10:5);
                var log = new EventLog(paths);
                log.Append(new AppEvent(now.AddMinutes(kind=="long-restart"?-8:-4).AddTicks(1234), EventTypes.AppExit));
                var m = new WidgetModel(paths, false, () => now); m.Initialize();
                var started = now.AddMinutes(kind == "long-restart" ? -2 : -4).AddSeconds(2).AddTicks(4321);
                m.RecordEvent(new AppEvent(started, EventTypes.AppStart));
                if (kind == "failure") m.RecordEvent(new AppEvent(started, EventTypes.CollectFail, Statuses.Error, "network", "test-usage", "p"));
                if (kind == "suspend") m.RecordEvent(new AppEvent(started, EventTypes.Suspend));
                foreach (var range in new[] { 0, 300 })
                {
                    var chart = m.BuildView(now, range).Chart;
                    Check(!chart.Total.Segments[^1].Valid, "real evidence removed: " + kind);
                    Check(chart.TotalTrend.ValueAt(now.AddMinutes(-2)) is null, "real gap interpolated: " + kind);
                }
            });
        });
        Test("bounds ignore malformed lines, wrong profiles and future observations", () => WithRoot(paths =>
        {
            Seed(paths);
            var store = new HistoryStore(paths, false);
            var dir = store.ProfileDir("test-usage", "p");
            File.WriteAllText(Path.Combine(dir, "2026-01-01.jsonl"), "bad json\n");
            Write(paths, Record(now.AddDays(-20), 99) with { ProfileKey = "wrong" }, forceProfile: "p");
            Write(paths, Record(now.AddDays(1), 99));
            var bounds = store.Bounds("test-usage", "p", now);
            Check(bounds?.First == now.AddDays(-10) && bounds.Last == now, "invalid edge selected");
            Check(store.IndexCount == 0, "bounds populated dedupe index");
        }));

        HistoryRecord Record(DateTimeOffset t, double used) => new("test-usage", "p", "test", t, Statuses.Partial, 300,
            new QuotaSnapshot(t.ToString("O"), t, new QuotaLimits(null, UsageParser.Limit(used, t.Date.AddDays(3)), null)));
        void Write(DataPaths paths, HistoryRecord rec, string? forceProfile = null)
        {
            var dir = new HistoryStore(paths, false).ProfileDir(rec.SourceId, forceProfile ?? rec.ProfileKey);
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, rec.T.ToLocalTime().ToString("yyyy-MM-dd") + ".jsonl"), HistoryStore.FormatLine(rec) + "\n");
        }
        void Seed(DataPaths paths,int tailMinutes=5)
        {
            new WidgetSettings { LastSourceId = "test-usage", LastProfileKey = "p" }.Save(paths.Settings);
            Write(paths, Record(now.AddDays(-10), 10)); Write(paths, Record(now.AddDays(-10).AddMinutes(5), 12));
            Write(paths, Record(now.AddMinutes(-tailMinutes), 30)); Write(paths, Record(now, 31));
        }
        void WithRoot(Action<DataPaths> body)
        {
            var root = Path.Combine(Path.GetTempPath(), "qw-range-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { body(new DataPaths(root)); } finally { Directory.Delete(root, true); }
        }
    }
}
