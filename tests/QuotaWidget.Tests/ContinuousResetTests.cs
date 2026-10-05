using QuotaWidget.Core;

static class ContinuousResetTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        var t=DateTimeOffset.Parse("2026-10-05T01:12:00+08:00");
        void Test(string name,Action body)=>tests.Add(("continuous reset: "+name,()=>{body();return Task.CompletedTask;}));
        void Check(bool value,string why){if(!value)throw new Exception(why);}
        void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-6,$"expected {a}, got {b}");
        HistoryRecord Rec(int minute,double used,DateTimeOffset reset)=>new(CodexUsageSource.SourceId,"fixture","Codex plus",t.AddMinutes(minute),Statuses.Partial,300,
            new("s"+minute,t.AddMinutes(minute),new(null,UsageParser.Limit(used,reset),null)));
        HistoryRecord[] Records()=>Enumerable.Range(-24,24).Select(i=>Rec(i*5,Math.Min(100,95+(i+24)/4),t.AddDays(5)))
            .Concat(Enumerable.Range(0,17).Select(i=>Rec(i*5,i==0?0:1+(i-1)/4,t.AddDays(7)))).ToArray();
        WorkSpan[] Work(double end)=>[new(t.AddHours(-2),t.AddMinutes(end),true,false,null)];
        double Area(IEnumerable<TrendRun> runs)=>runs.Sum(r=>r.Points.Zip(r.Points.Skip(1),(a,b)=>(a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours).Sum());

        Test("counter metadata separates a clean reset from real collection interruptions",()=>
        {
            var a=Rec(-5,100,t.AddDays(5));var b=Rec(0,0,t.AddDays(7));
            var reset=RateEngine.Build([a,b],SeriesKey.Total,[]).Segments.Single();
            Check(reset.Issue==SegmentIssue.Reset&&!reset.Valid&&reset.CounterResetOnly&&reset.StartsAtCapacity,"reset accounting or estimation evidence lost");
            foreach(var e in new[]{new AppEvent(t.AddMinutes(-2),EventTypes.Suspend),new AppEvent(t.AddMinutes(-2),EventTypes.CollectFail,Statuses.Error,"network")})
                Check(!RateEngine.Build([a,b],SeriesKey.Total,[e]).Segments.Single().CounterResetOnly,"interrupted reset marked continuous");
            foreach(var other in new[]{b with{SourceId="other"},b with{ProfileKey="other"},b with{PlanLabel="other"},Rec(15,0,t.AddDays(7)),Rec(0,100,t.AddDays(7))})
                Check(!RateEngine.Build([a,other],SeriesKey.Total,[]).Segments.Single().CounterResetOnly,"unreliable reset marked continuous");
            var scaled=QuotaUnits.Scale(new(){Key=SeriesKey.Fable,Segments=[reset]},.5).Segments.Single();
            Check(scaled.CounterResetOnly&&scaled.StartsAtCapacity&&scaled.Delta==-50,"Fable conversion lost reset evidence");
        });
        Test("steady work shares smoothing across reset while raw cumulative points stay separate",()=>
        {
            var source=RateEngine.Build(Records(),SeriesKey.Total,[]);
            var curve=ActiveRateEstimator.Build(source,t.AddMinutes(35),Work(35));
            var reset=source.Segments.Single(s=>s.Issue==SegmentIssue.Reset);
            Check(!reset.Valid&&reset.Delta==-100,"raw reset was rewritten");
            Near(2,RateEngine.SumRange(source,t,t.AddMinutes(35)).Delta);
            Near(7,curve.Delta);Near(7,Area(curve.Runs.Where(r=>!r.Provisional)));
            foreach(var minute in Enumerable.Range(-15,36))
                Check(curve.ValueAt(t.AddMinutes(minute)) is >1.5 and <4,"artificial trough/spike near reset at "+minute);
            foreach(var edge in new[]{reset.Start,reset.End})
            {
                Check(Math.Abs(curve.ValueAt(edge.AddMilliseconds(-1))!.Value-curve.ValueAt(edge.AddMilliseconds(1))!.Value)<.001,"reset creates a discontinuity");
                Check(!curve.Runs.Any(r=>r.HardEnd&&r.Points[^1].Time==edge||r.HardStart&&r.Points[0].Time==edge),"reset became a work edge");
            }
        });
        Test("saturated counter carries context immediately without waiting for later increments",()=>
        {
            var source=RateEngine.Build(Records(),SeriesKey.Total,[]);
            foreach(var minute in new[]{0,5,15,35})
            {
                var curve=ActiveRateEstimator.Build(source,t.AddMinutes(minute),Work(minute));
                foreach(var near in new[]{-5d,-2,0})Check(curve.ValueAt(t.AddMinutes(near)) is >1.5 and <4,"early reset flash at "+minute);
                var expected=source.Segments.Where(s=>s.Valid&&s.End<=t.AddMinutes(minute)).Sum(s=>s.Delta);
                Near(expected,curve.Delta);
                Check(curve.Runs.All(r=>r.Points[^1].Time<=t.AddMinutes(minute)),"used future coverage");
            }
        });
        Test("real pauses, missing local coverage and collection gaps still separate resets",()=>
        {
            var records=Records();
            var ordinary=RateEngine.Build(records,SeriesKey.Total,[]);
            var interrupted=RateEngine.Build(records,SeriesKey.Total,[new(t.AddMinutes(-2),EventTypes.Suspend)]);
            WorkSpan[] paused=[new(t.AddHours(-2),t.AddMinutes(-1),true,true,null),new(t.AddMinutes(1),t.AddMinutes(35),true,false,null)];
            foreach(var pair in new[]{(ordinary,paused),(ordinary,Array.Empty<WorkSpan>()),(interrupted,Work(35))})
            {
                var curve=ActiveRateEstimator.Build(pair.Item1,t.AddMinutes(35),pair.Item2);
                Check(curve.ValueAt(t.AddMinutes(-2)) is null,"crossed a reset without continuous work and collection evidence");
            }
        });
        Test("a later missing sample and an actual completion keep their boundaries",()=>
        {
            var records=Records();
            var source=RateEngine.Build(records,SeriesKey.Total,[new(t.AddMinutes(42),EventTypes.CollectFail,Statuses.Error,"network")]);
            var curve=ActiveRateEstimator.Build(source,t.AddMinutes(80),Work(80));
            Check(curve.ValueAt(t.AddMinutes(-2))>0&&curve.ValueAt(t.AddMinutes(42)) is null,"reset continuation erased a later real gap");
            var complete=ActiveRateEstimator.Build(RateEngine.Build(records.Where(r=>r.T<=t.AddMinutes(35)).ToArray(),SeriesKey.Total,[]),t.AddMinutes(40),[new(t.AddHours(-2),t.AddMinutes(29),true,true,null)]);
            Near(0,complete.ValueAt(t.AddMinutes(31))!.Value);
        });
        Test("new-period measured acceleration is retained rather than locking to the old rate",()=>
        {
            var records=Records().Where(r=>r.T<t).Concat(Enumerable.Range(0,17).Select(i=>Rec(i*5,i*2,t.AddDays(7)))).ToArray();
            var source=RateEngine.Build(records,SeriesKey.Total,[]);
            var curve=ActiveRateEstimator.Build(source,t.AddMinutes(80),Work(80));
            Check(curve.ValueAt(t.AddMinutes(65))>20,"real acceleration was flattened");
            Near(source.Segments.Where(s=>s.Valid).Sum(s=>s.Delta),curve.Delta);
        });
        Test("counter continuity is invariant across views and does not change recent-hour accounting",()=>
        {
            var source=RateEngine.Build(Records(),SeriesKey.Total,[]);var work=Work(35);
            double? previous=null;
            foreach(var range in new[]{60,300,1440,4320})
            {
                var view=new ChartView{Start=t.AddMinutes(35-range),End=t.AddMinutes(35),Smooth=true,Total=source,Fable=QuotaUnits.Scale(source,.5),Codex=source,Gaps=[],Activity=new(work,work,work),FableToClaudeFactor=.5};
                var value=view.CodexAt(t.AddMinutes(-2));Check(value>0,"no rate through clean reset");
                if(previous is {} old)Near(old,value!.Value);previous=value;
                Near(value!.Value,view.TotalAt(t.AddMinutes(-2))!.Value);Near(value.Value/2,view.FableAt(t.AddMinutes(-2))!.Value);
            }
            Check(RecentUsageRate.Build(source,t.AddMinutes(35),300).Rate is null,"estimated reset bridge became a measured hour");
        });
        Test("repeated resets keep one work trend and each original accounting boundary",()=>
        {
            var records=Records().Concat(new[]{Rec(85,0,t.AddDays(14)),Rec(90,1,t.AddDays(14)),Rec(95,1,t.AddDays(14)),Rec(100,1,t.AddDays(14))}).ToArray();
            var source=RateEngine.Build(records,SeriesKey.Total,[]);
            var resets=source.Segments.Where(s=>s.Issue==SegmentIssue.Reset).ToArray();
            Check(resets.Length==2&&resets.All(s=>s.CounterResetOnly&&!s.Valid),"reset ledger changed");
            var curve=ActiveRateEstimator.Build(source,t.AddMinutes(100),Work(100));
            foreach(var reset in resets)Check(curve.ValueAt(reset.Start+(reset.End-reset.Start)/2)>0,"second reset split estimation context");
            Near(10,curve.Delta);Near(10,Area(curve.Runs.Where(r=>!r.Provisional)));
            Near(1,RateEngine.SumRange(source,t.AddMinutes(85),t.AddMinutes(100)).Delta);
        });
    }
}
