using QuotaWidget.Core;

static class IdleLinkTests
{
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        var t = DateTimeOffset.Parse("2026-10-08T07:00:00+08:00");
        void Test(string name, Action body) => tests.Add(("idle links: " + name, () => { body(); return Task.CompletedTask; }));
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        SeriesData Source() => new() { Key = SeriesKey.Total, Segments = Enumerable.Range(0, 18)
            .Select(i => new RateSegment { Start = t.AddMinutes(i * 5), End = t.AddMinutes((i + 1) * 5), Delta = i is 2 or 5 or 10 or 13 ? 1 : 0 }).ToList() };
        WorkSpan[] Work(double gap) => [new(t, t.AddMinutes(30), true, true, "model"), new(t.AddMinutes(30 + gap), t.AddMinutes(75), true, true, "model")];
        Test("threshold is inclusive, off stays off, with no rate or ledger mutation", () =>
        {
            foreach (var gap in new[] { 5d, 10, 10.001, 15 })
            {
                var source = Source(); var work = Work(gap); var trend = ActiveRateEstimator.Build(source, t.AddMinutes(90), work);
                var before = trend.Runs.SelectMany(r => r.Points).ToArray(); var amount = trend.Delta;
                foreach (var minutes in new[] { 0, 5, 10, 15 })
                {
                    var links = ChartPath.IdleLinks(trend, source, work, minutes);
                    Check(links.Count == (gap <= minutes ? 1 : 0), $"wrong threshold for {gap}/{minutes}");
                    if (links.Count > 0) Check(links[0][0].Time == work[0].End && links[0][1].Time == work[1].Start, "link shifted real lifecycle edges");
                    Check(trend.Runs.SelectMany(r => r.Points).SequenceEqual(before) && trend.Delta == amount, "display option modified estimator");
                    Check(trend.ValueAt(t.AddMinutes(30 + gap / 2)) == 0, "idle gained a numeric rate");
                    Check(RateEngine.SumRange(source, t, t.AddMinutes(90)).Delta == 4, "ledger changed");
                }
            }
        });
        Test("sampling gaps, resets, account groups and absent coverage cannot be bridged", () =>
        {
            var work = Work(10); var trend = ActiveRateEstimator.Build(Source(), t.AddMinutes(90), work);
            foreach (var issue in new[] { SegmentIssue.Gap, SegmentIssue.Reset, SegmentIssue.Missing, SegmentIssue.Decrease, SegmentIssue.UnknownWindow })
            {
                var source = Source(); var s = source.Segments[6];
                source.Segments[6] = new() { Start = s.Start, End = s.End, Delta = s.Delta, Issue = issue };
                Check(ChartPath.IdleLinks(trend, source, work, 15).Count == 0, "bridged " + issue);
            }
            var missing = Source(); missing.Segments.RemoveAt(6);
            Check(ChartPath.IdleLinks(trend, missing, work, 15).Count == 0, "bridged absent sample");
            var account = Source(); foreach (var s in account.Segments.Skip(8)) s.Group = 1;
            Check(ChartPath.IdleLinks(trend, account, work, 15).Count == 0, "bridged account boundary");
            var edge = Source(); var last = edge.Segments[8]; edge.Segments[8] = new() { Start = last.Start, End = last.End, Issue = SegmentIssue.Reset };
            Check(ChartPath.IdleLinks(trend, edge, work, 15).Count == 0, "bridged reset at exact restart");
        });
        Test("unknown ends, model switches, omitted Fable fragments and unmatched quota remain separate", () =>
        {
            var source = Source(); var work = Work(10); var trend = ActiveRateEstimator.Build(source, t.AddMinutes(90), work);
            Check(ChartPath.IdleLinks(trend, source, [work[0] with { KnownEnd = false }, work[1]], 15).Count == 0, "guessed completion");
            Check(ChartPath.IdleLinks(trend, source, [work[0], work[1] with { Model = "other" }], 15).Count == 0, "joined models");
            foreach (var boundary in new[] { 30, 35, 40 })
                Check(ChartPath.IdleLinks(trend, source, work, 15, [t.AddMinutes(boundary)]).Count == 0, "joined context boundary");
            Check(ChartPath.IdleLinks(trend, source, work, 15, omitted: [new(t.AddMinutes(40), t.AddMinutes(45))]).Count == 0, "recreated hidden total stroke");
            trend.Unlocated.Add(new() { Start = t.AddMinutes(35), End = t.AddMinutes(40), Delta = 1 });
            Check(ChartPath.IdleLinks(trend, source, work, 15).Count == 0, "hid unlocated usage");
        });
        Test("completed tail never extends while waiting for another task", () =>
        {
            var source = Source(); var work = Work(10).Take(1).ToArray(); var trend = ActiveRateEstimator.Build(source, t.AddMinutes(90), work);
            Check(ChartPath.IdleLinks(trend, source, work, 15).Count == 0, "extended completed task");
        });
        Test("default and saved choices survive upgrade and normalization", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "qw-idle-" + Guid.NewGuid() + ".json");
            try
            {
                File.WriteAllText(file, "{\"trendMinutes\":150}");
                Check(WidgetSettings.Load(file, out _).IdleLinkMinutes == 10, "old settings did not get 10 minute default");
                foreach (var minutes in WidgetSettings.IdleLinkChoices)
                {
                    var settings = new WidgetSettings { IdleLinkMinutes = minutes }; settings.Normalize(); settings.Save(file);
                    Check(WidgetSettings.Load(file, out _).IdleLinkMinutes == minutes, "lost user preference");
                }
                var invalid = new WidgetSettings { IdleLinkMinutes = -5 }; invalid.Normalize();
                Check(invalid.IdleLinkMinutes == 10, "invalid setting not normalized");
            }
            finally { File.Delete(file); }
        });
    }
}
