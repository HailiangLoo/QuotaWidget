using QuotaWidget.Core;

static class FloatingResetTests
{
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        var at = DateTimeOffset.Parse("2026-10-03T05:13:45.446+08:00");
        void Test(string name, Action body) => tests.Add(("floating deadline: " + name, () => { body(); return Task.CompletedTask; }));
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        HistoryRecord Sample(int minute, double used, DateTimeOffset? reset = null)
        {
            var t = at.AddMinutes(minute);
            return new(CodexUsageSource.SourceId,"fixture","Codex plus",t,Statuses.Partial,300,
                new("sample-"+minute,t,new(null,UsageParser.Limit(used,reset ?? t.AddDays(7).AddSeconds(1)),null)));
        }
        double Area(RateTrend trend) => trend.Runs.Sum(r => r.Points.Zip(r.Points.Skip(1),
            (a,b) => (a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours).Sum());

        Test("observed 100 to zero remains one reset, not ninety", () =>
        {
            var records = new[] { Sample(-10,99,at.AddDays(1)),Sample(-5,100,at.AddDays(1)) }
                .Concat(Enumerable.Range(0,90).Select(i=>Sample(i*5,0))).ToArray();
            var data=RateEngine.Build(records,SeriesKey.Total,[]);
            Check(data.Segments.Count(s=>s.Issue==SegmentIssue.Reset)==1,"empty deadlines repeatedly reset");
            Check(data.Segments.Single(s=>!s.Valid).Start==at.AddMinutes(-5),"real reset removed");
            Check(data.Segments.Where(s=>s.Start>=at).All(s=>s.Valid&&s.Delta==0),"reported zero not preserved");
            var sum=RateEngine.SumRange(data,records[0].T,records[^1].T);
            Check(sum.Delta==1&&Math.Abs(sum.CoverageMinutes-450)<1e-8,"wrong amount or coverage");
            var trend=RateTrend.Build(data,records[0].T,records[^1].T,120);
            Check(Math.Abs(Area(trend)-sum.Delta)<1e-8,"curve area changed");
            Check(!ChartPath.PositiveRuns(trend.Runs.Where(r=>r.Points[0].Time>=at).Select(r=>r.Points)).Any(),"zero baseline rendered");
        });
        Test("first positive sample fixes the deadline and preserves its increment", () =>
        {
            var fixedAt=at.AddMinutes(178).AddDays(7);
            var records=Enumerable.Range(0,36).Select(i=>Sample(i*5,0))
                .Concat(new[]{Sample(180,1,fixedAt),Sample(185,2,fixedAt),Sample(190,3,fixedAt)}).ToArray();
            var data=RateEngine.Build(records,SeriesKey.Total,[]);
            Check(data.Segments.All(s=>s.Valid),"starting a measured nonempty window creates a false break");
            Check(RateEngine.SumRange(data,at,records[^1].T).Delta==3,"first usage increment lost");
            Check(Math.Abs(Area(RateTrend.Build(data,at,records[^1].T,120))-3)<1e-8,"first usage area lost");
        });
        Test("empty readings cannot conceal real resets, active drift or other providers", () =>
        {
            var a=Sample(0,0);var b=Sample(5,0);
            void Reset(HistoryRecord x,HistoryRecord y,string why) => Check(RateEngine.Build([x,y],SeriesKey.Total,[]).Segments.Single().Issue==SegmentIssue.Reset,why);
            Reset(Sample(0,0,at.AddMinutes(1)),b,"expired empty window misclassified");
            Reset(a,Sample(5,0,at.AddDays(14)),"unrelated deadline jump ignored");
            Reset(Sample(0,2),Sample(5,3),"active deadline drift ignored");
            Reset(a with {SourceId="claude-oauth-usage"},b with {SourceId="claude-oauth-usage"},"Claude behavior changed");
            Reset(a,b with {ProfileKey="different"},"cross-profile exception");
            var x=a with {Snapshot=a.Snapshot with {Limits=a.Snapshot.Limits with {AllWeek=a.Snapshot.Limits.AllWeek! with {WindowId="first"}}}};
            var y=b with {Snapshot=b.Snapshot with {Limits=b.Snapshot.Limits with {AllWeek=b.Snapshot.Limits.AllWeek! with {WindowId="second"}}}};
            Reset(x,y,"explicit window identity ignored");
        });
        Test("failure, sleep and long gaps remain unknown even in an empty window", () =>
        {
            var gap=RateEngine.Build([Sample(0,0),Sample(60,1)],SeriesKey.Total,[]);
            Check(gap.Segments.Single().Issue==SegmentIssue.Gap&&RateEngine.SumRange(gap,at,at.AddHours(1)).Delta==0,"long gap usage invented");
            foreach(var e in new[]{new AppEvent(at.AddMinutes(2),EventTypes.Suspend),new AppEvent(at.AddMinutes(2),EventTypes.CollectFail,Statuses.Error,"network")})
            {
                var data=RateEngine.Build([Sample(0,0),Sample(5,0)],SeriesKey.Total,[e]);
                Check(data.Segments.Single().Issue==SegmentIssue.Gap,"event crossed while empty");
            }
        });
        Test("consecutive same-reason boundaries coalesce without bridging valid time", () =>
        {
            var repeated=Enumerable.Range(0,90).Select(i=>new GapRegion(at.AddMinutes(i*5),at.AddMinutes((i+1)*5),"重置")).ToArray();
            var one=ChartBoundaries.Coalesce(repeated);
            Check(one.Count==1&&one[0].Start==at&&one[0].End==at.AddMinutes(450),"dense repeated boundaries retained");
            var separate=ChartBoundaries.Coalesce([repeated[0],new(at.AddMinutes(10),at.AddMinutes(15),"重置"),new(at.AddMinutes(15),at.AddMinutes(20),"断网")]);
            Check(separate.Count==3,"merged across valid interval or changed reason");
            Check(repeated.Length==90&&repeated[0].End==at.AddMinutes(5),"original inspection intervals mutated");
        });
    }
}
