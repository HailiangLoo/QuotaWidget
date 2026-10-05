using QuotaWidget.Core;

static class CumulativeTests
{
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        var t = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        void Test(string name, Action body) => tests.Add((name, () => { body(); return Task.CompletedTask; }));
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        RateSegment S(int start, int end, double delta, int group = 0, SegmentIssue issue = SegmentIssue.None) =>
            new() { Start = t.AddMinutes(start), End = t.AddMinutes(end), Delta = delta, Group = group, Issue = issue, Smooth = 12345 };
        Test("cumulative: sum uses raw increments, not rate or smoothed values", () =>
        {
            var data = new SeriesData { Key = SeriesKey.Total, Segments = [S(0,5,1), S(5,10,2), S(10,15,0)] };
            var curve = CumulativeSeries.Build(data, t, t.AddMinutes(15));
            Check(curve.Segments.Select(s => s.To).SequenceEqual(new[] { 1d,3d,3d }), "wrong running sums");
            Check(curve.ValueAt(t.AddMinutes(7.5)) == 2, "valid interval interpolation");
            Check(curve.Segments[^1].To == RateEngine.SumRange(data, t, t.AddMinutes(15)).Delta, "summary mismatch");
        });
        Test("cumulative: missing/reset intervals remain gaps and never subtract consumption", () =>
        {
            var data = new SeriesData { Key = SeriesKey.Total, Segments = [S(0,5,1), S(5,10,0,1,SegmentIssue.Gap), S(10,15,2,2), S(15,20,-20,3,SegmentIssue.Reset), S(20,25,1,4)] };
            var curve = CumulativeSeries.Build(data, t, t.AddMinutes(30));
            Check(curve.ValueAt(t.AddMinutes(7)) is null && curve.ValueAt(t.AddMinutes(17)) is null && curve.ValueAt(t.AddMinutes(28)) is null, "invented plateau in missing data");
            Check(curve.Segments[^1].To == 4 && curve.Segments[1].From == 1, "reset/gap contaminated accumulated known increments");
        });
        Test("cumulative: range starts fresh, only complete covered intervals count", () =>
        {
            var data = new SeriesData { Key = SeriesKey.Fable, Segments = [S(0,5,8), S(5,10,1), S(10,15,2)] };
            var curve = CumulativeSeries.Build(data, t.AddMinutes(3), t.AddMinutes(13));
            Check(curve.Segments.Count == 1 && curve.Segments[0].From == 0 && curve.Segments[0].To == 1, "invented partial intervals");
            Check(curve.ValueAt(t.AddMinutes(4)) is null, "invented early zero");
        });
        Test("cumulative: per-provider modes do not change the other graph or Fable visibility rules", () =>
        {
            var data=new SeriesData{Key=SeriesKey.Total,Segments=[S(0,5,1),S(5,10,2)]};
            var sum=CumulativeSeries.Build(data,t,t.AddMinutes(10));
            foreach(var claude in new[]{false,true}) foreach(var codex in new[]{false,true})
            {
                var view=new ChartView{Start=t,End=t.AddMinutes(10),Smooth=false,Total=data,Fable=data,Codex=data,Gaps=[],
                    ClaudeCumulativeMode=claude,CodexCumulativeMode=codex,TotalCumulative=sum,FableCumulative=sum,CodexCumulative=sum,
                    FableToClaudeFactor=.5,FableOnlySpans=[new(t,t.AddMinutes(10))]};
                Check(view.TotalAt(t.AddMinutes(7.5))==(claude?2:24)&&view.FableAt(t.AddMinutes(7.5))==(claude?2:24),"Claude/Fable mode differs");
                Check(view.CodexAt(t.AddMinutes(7.5))==(codex?2:24),"Codex mode follows Claude");
                Check(view.MergesFable==!claude,"Codex mode changed Fable-only visibility");
            }
        });
        Test("cumulative: preference persists and invalid setting falls back to rate", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "qw-cumulative-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var file = Path.Combine(root, "settings.json"); new WidgetSettings { ChartMode = "cumulative" }.Save(file);
                Check(WidgetSettings.Load(file, out _).ChartMode == "cumulative", "lost chart mode");
                var migrated=WidgetSettings.Load(file,out _);
                Check(migrated.ClaudeChartMode=="cumulative"&&migrated.CodexChartMode=="cumulative","old global choice not migrated");
                migrated.CodexChartMode="rate";migrated.RangeMinutes=4320;migrated.Save(file);
                var restored=WidgetSettings.Load(file,out _);
                Check(restored.ClaudeChartMode=="cumulative"&&restored.CodexChartMode=="rate"&&restored.RangeMinutes==4320,"independent choices or 3d lost on restart");
                var settings = new WidgetSettings { ChartMode = "bad" }; settings.Normalize(); Check(settings.ChartMode == "rate", "invalid mode allowed");
            }
            finally { Directory.Delete(root, true); }
        });
    }
}
