using QuotaWidget.Core;

static class TrendTests
{
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        var t = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        void Test(string name, Action action) => tests.Add(("trend: " + name, () => { action(); return Task.CompletedTask; }));
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        void Near(double expected, double actual, double tolerance = 1e-7) => Check(Math.Abs(expected - actual) < tolerance, $"expected {expected}, actual {actual}");
        RateSegment S(double a, double b, double delta, int group = 0, SegmentIssue issue = SegmentIssue.None) =>
            new() { Start = t.AddMinutes(a), End = t.AddMinutes(b), Delta = delta, Group = group, Issue = issue };
        SeriesData Data(params RateSegment[] segments) => new() { Key = SeriesKey.Total, Segments = segments.ToList() };
        double Area(TrendRun run) => run.Points.Zip(run.Points.Skip(1), (a,b) => (a.Rate + b.Rate) / 2 * (b.Time - a.Time).TotalHours).Sum();

        Test("known session edges stop smoothing from inventing early starts and late tails", () =>
        {
            var source = Data(Enumerable.Range(0, 60).Select(i => S(i*5, (i+1)*5, i is 13 or 25 or 33 ? 1 : 0)).ToArray());
            var start = t.AddMinutes(43.408); var end = t.AddMinutes(183.123);
            var before = RateTrend.Build(source, t, t.AddMinutes(300));
            var after = RateTrend.Build(source, t, t.AddMinutes(300), boundaries: [end, start, start]);
            Check(before.ValueAt(start.AddMinutes(-1)) > 0 && before.ValueAt(end.AddMinutes(1)) > 0, "fixture does not reproduce spill");
            Near(0, after.ValueAt(start.AddTicks(-1))!.Value); Near(0, after.ValueAt(end)!.Value);
            Check(after.ValueAt(start) > 0, "right side of exact start lost its value");
            Check(after.Runs.Count == 3, "lost session splits");
            var active = after.Runs[1];
            Check(active.Points[0].Time == start && active.Points[^1].Time == end && active.HardStart && active.HardEnd, "rounded the recorded edges");
            var path = ChartPath.HardEdges(active);
            Check(path[0].Time == path[1].Time && path[0].Rate == 0 && path[1].Rate > 0, "no vertical onset");
            Check(path[^1].Time == path[^2].Time && path[^1].Rate == 0 && path[^2].Rate > 0, "no vertical cutoff");
            Near(3, after.Runs.Sum(Area));
            Near(3, after.Runs.Select(ChartPath.HardEdges).Sum(p => Area(new(p, 0, 0))));
            Check(source.Segments.Count == 60 && source.Segments[8].Start == t.AddMinutes(40), "modified raw intervals");
        });
        Test("boundaries preserve measured usage on both sides of a crossing sample", () =>
        {
            var source = Data(S(0,5,0), S(5,10,1), S(10,15,0));
            var trend = RateTrend.Build(source, t, t.AddMinutes(15), boundaries: [t.AddMinutes(7)]);
            Near(1, trend.Runs.Sum(Area)); Near(.4, trend.Runs[0].Delta); Near(.6, trend.Runs[1].Delta);
            Check(trend.ValueAt(t.AddMinutes(6)) > 0, "local session erased an observed account increment");
            var restricted = RateTrend.Build(source, t.AddMinutes(2), t.AddMinutes(13), boundaries: [t.AddMinutes(7)]);
            Near(1, restricted.Runs.Sum(Area));
            Check(restricted.ValueAt(t.AddMinutes(4)) is null && restricted.ValueAt(t.AddMinutes(11)) is null, "invented partial sample coverage");
        });
        Test("session cuts never restore missing data or bridge quota resets", () =>
        {
            foreach (var issue in new[] { SegmentIssue.Gap, SegmentIssue.Reset, SegmentIssue.Missing })
            {
                var trend = RateTrend.Build(Data(S(0,5,1), S(5,10,0,1,issue), S(10,15,1,1)), t, t.AddMinutes(15),
                    boundaries: [t.AddMinutes(3), t.AddMinutes(7), t.AddMinutes(12)]);
                Near(2, trend.Runs.Sum(Area));
                Check(trend.ValueAt(t.AddMinutes(7)) is null, "session cut filled a real hole");
            }
        });
        Test("session ends require a closed session, never the last token of ongoing work", () =>
        {
            ChatSession Session(double from, double to) => new() { Id="test", Start=t.AddMinutes(from), End=t.AddMinutes(to) };
            var edges = SessionTrendBoundaries.Build([Session(0,60), Session(400,500)], t.AddMinutes(510));
            Check(edges.SequenceEqual(new[] { t, t.AddMinutes(60), t.AddMinutes(400) }), "active session has a fake end");
            var closed = SessionTrendBoundaries.Build([Session(400,500)], t.AddMinutes(740));
            Check(closed.SequenceEqual(new[] { t.AddMinutes(400), t.AddMinutes(500) }), "confirmed end missing");
        });
        Test("long archive keeps short bursts and exact area with bounded display vertices", () =>
        {
            var source = Data(Enumerable.Range(0, 365 * 24 * 12).Select(i => S(i * 5, (i + 1) * 5, i is 11 or 57033 or 100103 ? 1 : 0)).ToArray());
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var trend = RateTrend.Build(source, t, t.AddDays(365));
            Near(3, trend.Runs.Sum(Area));
            Check(trend.Runs[0].Points.Count <= 2049, "unbounded archive drawing");
            foreach (var i in new[] { 11, 57033, 100103 })
                Check(trend.ValueAt(t.AddMinutes(i * 5 + 2)) > 0, "short burst disappeared between display vertices");
            Check(trend.Runs[0].KernelMinutes > 60, "long-range coarsening undisclosed");
            Check(timer.Elapsed < TimeSpan.FromSeconds(5), "archive kernel still scans all bins at every vertex");
        });

        Test("integer observations yield continuous fractional rates and preserve exact area", () =>
        {
            var source = Data(Enumerable.Range(0, 36).Select(i => S(i * 5, (i+1) * 5, i is 7 or 14 or 21 or 28 ? 1 : 0)).ToArray());
            var trend = RateTrend.Build(source, t, t.AddMinutes(180));
            Near(4, trend.Runs.Sum(Area));
            var values = trend.Runs[0].Points.Select(p => p.Rate).ToArray();
            Check(values.All(v => double.IsFinite(v) && v >= 0), "negative or invalid rate");
            Check(values.Count(v => v > .05 && Math.Abs(v - Math.Round(v)) > .01) > 80, "retained quantized plateaus");
            Check(values.Zip(values.Skip(1), (a,b) => Math.Abs(a-b)).Max() < .3, "discontinuous 1 minute transition");
            Near(0, source.Segments[6].Delta); Near(1, source.Segments[7].Delta);
        });
        Test("stable default removes fake hills from constant load quantized every 20, 40 or 60 minutes", () =>
        {
            foreach(var period in new[] {20,40,60})
            {
                var data=Data(Enumerable.Range(0,96).Select(i=>S(i*5,(i+1)*5,Math.Floor((i+1)*5d/period)-Math.Floor(i*5d/period))).ToArray());
                var trend=RateTrend.Build(data,t,t.AddHours(8));
                var interior=trend.Runs.SelectMany(r=>r.Points).Where(p=>p.Time>=t.AddHours(2)&&p.Time<=t.AddHours(6)).ToList();
                Check(interior.Max(p=>p.Rate)-interior.Min(p=>p.Rate)<.01,"integer steps produced fake oscillation");
                Near(60d/period,interior.Average(p=>p.Rate),1e-5); Near(RateEngine.SumRange(data,t,t.AddHours(8)).Delta,trend.Runs.Sum(Area));
            }
        });
        Test("stable smoothing keeps isolated bursts localized and does not invent steady usage", () =>
        {
            var data=Data(Enumerable.Range(0,96).Select(i=>S(i*5,(i+1)*5,i==48?1:0)).ToArray());
            var trend=RateTrend.Build(data,t,t.AddHours(8));
            Near(0,trend.ValueAt(t.AddMinutes(175))!.Value);Near(0,trend.ValueAt(t.AddMinutes(310))!.Value);
            Check(trend.ValueAt(t.AddMinutes(242))>0,"lost real burst");Near(1,trend.Runs.Sum(Area));
        });
        Test("constant rate remains constant, including both boundaries", () =>
        {
            var trend = RateTrend.Build(Data(Enumerable.Range(0, 24).Select(i => S(i*5,(i+1)*5,.5)).ToArray()), t, t.AddMinutes(120));
            foreach (var p in trend.Runs[0].Points) Near(6, p.Rate);
            Near(12, trend.Runs.Sum(Area));
        });
        Test("gaps, resets and missing data split the kernel", () =>
        {
            foreach (var issue in new[] { SegmentIssue.Gap, SegmentIssue.Reset, SegmentIssue.Missing })
            {
                var trend = RateTrend.Build(Data(S(0,5,1), S(5,10,0,1,issue), S(10,15,0,1), S(15,20,0,1)), t, t.AddMinutes(20));
                Check(trend.Runs.Count == 2, "bridged a missing interval");
                Check(trend.ValueAt(t.AddMinutes(7)) is null, "value inside gap");
                Near(0, trend.ValueAt(t.AddMinutes(12))!.Value); Near(1, trend.Runs.Sum(Area));
            }
        });
        Test("unlabelled time discontinuity also splits runs", () =>
        {
            var trend = RateTrend.Build(Data(S(0,5,1), S(10,15,0)), t, t.AddMinutes(15));
            Check(trend.Runs.Count == 2 && trend.ValueAt(t.AddMinutes(8)) is null, "implicit gap bridged");
        });
        Test("selected range area matches complete intervals only", () =>
        {
            var source = Data(S(0,5,8), S(5,10,1), S(10,15,2), S(15,20,9));
            var trend = RateTrend.Build(source, t.AddMinutes(2), t.AddMinutes(18));
            Near(RateEngine.SumRange(source,t.AddMinutes(2),t.AddMinutes(18)).Delta, trend.Runs.Sum(Area));
            Check(trend.ValueAt(t.AddMinutes(4)) is null && trend.ValueAt(t.AddMinutes(16)) is null, "invented partial interval");
        });
        Test("irregular intervals are time weighted, not sample weighted", () =>
        {
            var source = Data(S(0,2.3,.23), S(2.3,7.6,.53), S(7.6,13.9,.63), S(13.9,20.2,.63));
            var trend = RateTrend.Build(source, t, t.AddMinutes(21));
            foreach(var p in trend.Runs[0].Points) Near(6,p.Rate);
            Near(2.02, trend.Runs.Sum(Area));
        });
        Test("30 and 60 minute single samples retain interval average", () =>
        {
            foreach(var minutes in new[] { 5,30,60 })
            {
                var trend = RateTrend.Build(Data(S(0,minutes,1)), t, t.AddMinutes(minutes));
                foreach (var p in trend.Runs[0].Points) Near(60d/minutes,p.Rate);
                Near(1, trend.Runs.Sum(Area));
            }
        });
        Test("zero is zero, empty and outside coverage remain unknown", () =>
        {
            var zero = RateTrend.Build(Data(S(0,5,0), S(5,10,0)), t, t.AddMinutes(20));
            Check(zero.Runs.SelectMany(r=>r.Points).All(p=>p.Rate==0), "invented usage");
            Check(zero.ValueAt(t.AddMinutes(-1)) is null && zero.ValueAt(t.AddMinutes(11)) is null, "extrapolated history");
            Check(RateTrend.Build(Data(),t,t.AddMinutes(20)).Runs.Count==0, "empty invented");
        });
        Test("hover and rendered line use identical interpolation", () =>
        {
            var trend = RateTrend.Build(Data(S(0,5,0),S(5,10,1),S(10,15,0)),t,t.AddMinutes(15));
            var p=trend.Runs[0].Points;
            for(int i=1;i<p.Count;i++)
                Near((p[i-1].Rate+p[i].Rate)/2,trend.ValueAt(p[i-1].Time+(p[i].Time-p[i-1].Time)/2)!.Value);
        });
        Test("bursts at either boundary preserve mass without extrapolation", () =>
        {
            foreach(var index in new[] {0,23})
            {
                var trend=RateTrend.Build(Data(Enumerable.Range(0,24).Select(i=>S(i*5,(i+1)*5,i==index?1:0)).ToArray()),t,t.AddMinutes(120));
                Near(1,trend.Runs.Sum(Area));
                Check(trend.Runs[0].Points.All(p=>p.Time>=t && p.Time<=t.AddMinutes(120)),"extrapolated");
            }
        });
        Test("shared scale adapts to tiny and large rates without duplicate decimal labels", () =>
        {
            foreach (var maximum in new[] { .00004, .004, .04, .4, 2.9, 4.48, 99d, 40000d })
            {
                var scale = ChartScale.Create(maximum);
                Check(scale.Top >= maximum && scale.Step > 0, "clipped peak");
                var labels = new List<string>();
                for (double tick=0;tick<=scale.Top+scale.Step/2;tick+=scale.Step) labels.Add(scale.Label(tick));
                Check(labels.Distinct().Count()==labels.Count,"duplicate axis labels");
                Check(scale.Top / maximum < 1.6,"excessive empty vertical range");
            }
            Check(new ChartScale(.75,.25).Label(.25)=="0.25","lost quarter step");
        });
        Test("Fable zero at beginning, middle and end draws only positive lobes without changing area", () =>
        {
            IReadOnlyList<TrendPoint> points = new double[] {0,0,1,2,0,0,3,0,0}.Select((rate,i)=>new TrendPoint(t.AddMinutes(i),rate)).ToArray();
            var paths = ChartPath.PositiveRuns([points]);
            Check(paths.Count==2,"joined zero interval between two lobes");
            Check(paths[0][0].Time==t.AddMinutes(1) && paths[0][^1].Time==t.AddMinutes(4),"wrong first lobe boundary");
            Check(paths[1][0].Time==t.AddMinutes(5) && paths[1][^1].Time==t.AddMinutes(7),"wrong second lobe boundary");
            Check(paths.All(p=>p.Zip(p.Skip(1),(a,b)=>a.Rate>0||b.Rate>0).All(x=>x)),"drew a zero baseline");
            Near(Area(new TrendRun(points,0,0)),paths.Sum(p=>Area(new TrendRun(p,0,0))));
        });
        Test("zero clipping preserves tiny positive values, raw step boundaries and separate missing runs", () =>
        {
            IReadOnlyList<TrendPoint> first=[new(t,0),new(t.AddMinutes(5),0),new(t.AddMinutes(5),.000001),new(t.AddMinutes(10),.000001),new(t.AddMinutes(10),0),new(t.AddMinutes(15),0)];
            IReadOnlyList<TrendPoint> second=[new(t.AddMinutes(30),1),new(t.AddMinutes(35),0)];
            var paths=ChartPath.PositiveRuns([first,second]);
            Check(paths.Count==2,"lost positive run or crossed missing interval");
            Near(.000001*5/60+.5*5/60,paths.Sum(p=>Area(new TrendRun(p,0,0))));
            Check(ChartPath.PositiveRuns([new[]{new TrendPoint(t,0),new TrendPoint(t.AddMinutes(5),0)}]).Count==0,"all-zero path drawn");
        });
        Test("cumulative zero prefix hides but later nonzero plateau remains", () =>
        {
            IReadOnlyList<TrendPoint> p=[new(t,0),new(t.AddMinutes(5),0),new(t.AddMinutes(10),2),new(t.AddMinutes(15),2)];
            var paths=ChartPath.PositiveRuns([p]);
            Check(paths.Count==1 && paths[0][0].Time==t.AddMinutes(5) && paths[0][^1].Rate==2,"hid a nonzero cumulative total");
        });
        Test("Codex scale follows its own visible peak, not the Claude/Fable peak", () =>
        {
            var scales=ChartScale.ForPanels(new (int Panel,IEnumerable<double> Values)[] {(0,new[]{0d,12}),(0,new[]{0d,7}),(1,new[]{0d,1.6})});
            Check(scales[0].Top>=12 && scales[1].Top>=1.6 && scales[1].Top<3,"shared high scale flattened Codex");
            var withHighFable=ChartScale.ForPanels(new (int Panel,IEnumerable<double> Values)[] {(0,new[]{12d}),(0,new[]{20d}),(1,new[]{1.6})});
            Check(withHighFable[0].Top>=20,"upper panel clipped Fable");
            Near(scales[1].Top,withHighFable[1].Top);
            var later=ChartScale.ForPanels(new (int Panel,IEnumerable<double> Values)[] {(1,new[]{.1})});
            Check(later[1].Top>=.1 && later[1].Top<scales[1].Top,"scale did not shrink with visible peak");
        });
        Test("interval average uses actual seconds rather than rounded polling minutes", () =>
        {
            var segment=S(0,307d/60,.25);
            Near(.25*3600/307,segment.Rate);
            var trend=RateTrend.Build(Data(segment),t,segment.End);
            Near(.25,trend.Runs.Sum(Area));
            Near(segment.Rate,trend.ValueAt(t.AddSeconds(123))!.Value);
        });
    }
}
