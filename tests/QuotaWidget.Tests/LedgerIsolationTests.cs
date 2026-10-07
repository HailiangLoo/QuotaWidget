using System.Text.Json;
using QuotaWidget.Core;

static class LedgerIsolationTests
{
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        void Test(string name, Action body) => tests.Add(("ledger isolation: " + name, () => { body(); return Task.CompletedTask; }));
        void Check(bool value, string why) { if (!value) throw new Exception(why); }
        void Near(double a, double b) => Check(Math.Abs(a - b) < 1e-6, $"expected {a}, got {b}");
        var t = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        double Area(TrendRun r) => r.Points.Zip(r.Points.Skip(1), (a, b) => (a.Rate + b.Rate) / 2 * (b.Time - a.Time).TotalHours).Sum();

        Test("independent counters stay unchanged through raw/smoothed views and ranges", () =>
        {
            SeriesData Series(double[] values) => new() { Key = SeriesKey.Total, Segments = values.Select((v, i) => new RateSegment
                { Start = t.AddMinutes(i * 5), End = t.AddMinutes((i + 1) * 5), Delta = v }).ToList() };
            var total = Series([1, 0, 0, 0]); var fable = Series([.5, 0, 0, .5]);
            var before = JsonSerializer.Serialize(new[] { total, fable });
            foreach (var smooth in new[] { true, false })
            foreach (var start in new[] { t, t.AddMinutes(5) })
            {
                var view = new ChartView { Start = start, End = t.AddMinutes(20), Smooth = smooth, Total = total, Fable = fable, Gaps = [], FableToClaudeFactor = .5 };
                Near(1, view.TotalTrend.Delta); Near(1, view.FableTrend.Delta);
                Near(view.TotalAt(t.AddMinutes(7))!.Value, smooth ? ActiveRateEstimator.Build(total, view.End).ValueAt(t.AddMinutes(7))!.Value : 0);
                _ = view.TotalCumulative; _ = view.FableCumulative; _ = view.DisplayFableTrend;
            }
            Check(before == JsonSerializer.Serialize(new[] { total, fable }), "presentation mutated original observations");
        });

        Test("seeded mixed activity conserves observed mass without crossing invalid intervals", () =>
        {
            var random = new Random(20261008);
            for (var sample = 0; sample < 80; sample++)
            {
                var segments = new List<RateSegment>(); var work = new List<WorkSpan>(); var at = t; var group = 0;
                for (var i = 0; i < 36; i++)
                {
                    var end = at.AddMinutes(random.Next(1, 7) * 5);
                    var issue = i % 9 == 8 ? (SegmentIssue)(1 + random.Next(5)) : SegmentIssue.None;
                    if (issue != SegmentIssue.None) group++;
                    segments.Add(new() { Start = at, End = end, Delta = random.Next(5) * .5, Group = group, Issue = issue });
                    if (i % 3 != 2) work.Add(new(at.AddSeconds(20), end.AddSeconds(-20), true, true, "fixture"));
                    at = end;
                }
                var source = new SeriesData { Key = SeriesKey.Total, Segments = segments };
                var before = JsonSerializer.Serialize(source);
                var endAt = sample % 2 == 0 ? at : segments[29].End;
                var curve = ActiveRateEstimator.Build(source, endAt, sample % 3 == 0 ? [] : work, 60 + 30 * (sample % 4), .5);
                var observed = RateEngine.SumRange(source, t, endAt).Delta;
                Near(observed, curve.Delta);
                Near(observed, curve.Runs.Where(r => !r.Provisional).Sum(Area) + curve.Unlocated.Sum(s => s.Delta));
                Check(curve.Runs.SelectMany(r => r.Points).All(p => double.IsFinite(p.Rate) && p.Rate >= 0 && p.Time <= endAt), "invalid or future trend vertex");
                foreach (var gap in segments.Where(s => !s.Valid && s.End <= endAt))
                    Check(curve.ValueAt(gap.Start + (gap.End - gap.Start) / 2) is null, "trend filled missing evidence");
                var cumulative = CumulativeSeries.Build(source, t, endAt).Segments;
                Check(cumulative.All(s => s.To >= s.From) && cumulative.Zip(cumulative.Skip(1), (a, b) => b.From >= a.To).All(v => v), "cumulative points decreased");
                Check(before == JsonSerializer.Serialize(source), "estimator changed ledger");
            }
        });
    }
}
