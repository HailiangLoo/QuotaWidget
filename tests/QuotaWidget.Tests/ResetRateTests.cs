using QuotaWidget.Core;

static class ResetRateTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        var t=DateTimeOffset.Parse("2026-10-05T01:12:00+08:00");
        void Test(string name,Action body)=>tests.Add(("reset rate: "+name,()=>{body();return Task.CompletedTask;}));
        void Check(bool value,string why){if(!value)throw new Exception(why);}
        void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-6,$"expected {a}, got {b}");
        RateSegment S(double a,double b,double d,SegmentIssue issue=SegmentIssue.None)=>new(){Start=t.AddMinutes(a),End=t.AddMinutes(b),Delta=d,Issue=issue,Group=a>=0?1:0};
        SeriesData Data(params RateSegment[] items)=>new(){Key=SeriesKey.Total,Segments=items.ToList()};
        WorkSpan[] Work(double end)=>[new(t.AddHours(-1),t.AddMinutes(end),true,false,null)];
        double Area(IEnumerable<TrendRun> runs)=>runs.Sum(r=>r.Points.Zip(r.Points.Skip(1),(a,b)=>(a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours).Sum());
        RateSegment[] Prefix()=>[S(-60,-20,2),S(-20,-5,0),S(-5,0,-100,SegmentIssue.Reset)];

        Test("old period closes without a permanent predicted tail or a fabricated work stop",()=>
        {
            var data=Data(Prefix());
            var curve=ActiveRateEstimator.Build(data,t,Work(30));
            Near(2,curve.Delta);Near(2,Area(curve.Runs));
            Check(curve.Runs.All(r=>!r.Provisional&&!r.HardEnd),"reset left a prediction or invented completion");
            Check(curve.ValueAt(t.AddMinutes(-10))>0,"closed tail was erased");
            Check(curve.ValueAt(t.AddMinutes(-2)) is null,"quota reset interval filled in");
            var before=ActiveRateEstimator.Build(data,t.AddMinutes(-4),Work(30));
            Check(before.IsProvisional(t.AddMinutes(-10)),"future reset was used before its observation");
        });
        Test("sparse new-period readings conserve quota without first-poll spikes or forced flat tails",()=>
        {
            foreach(var quantum in new[]{.5,1d})
            {
                var data=Data(Prefix().Concat(new[]{S(0,5,quantum),S(5,25,0),S(25,30,quantum),S(30,35,0)}).ToArray());
                var curve=ActiveRateEstimator.Build(data,t.AddMinutes(35),Work(35),quantum:quantum);
                var points=curve.Runs.Where(r=>r.Points[0].Time>=t).SelectMany(r=>r.Points).ToArray();
                Check(points.All(p=>p.Rate>0&&p.Rate<12*quantum),"first-poll spike or an invented work stop");
                Check(curve.ValueAt(t.AddMinutes(4))>curve.ValueAt(t.AddMinutes(34)),"later unchanged readings forced a constant rate");
                Near(2+2*quantum,curve.Delta);Near(2+2*quantum,Area(curve.Runs));
                Check(curve.ValueAt(t.AddMinutes(-2)) is null,"reset was bridged");
            }
        });
        Test("first-jump phase is pooled even after subsequent readings establish the curve",()=>
        {
            var newPeriod=new[]{S(0,5,1),S(5,25,1),S(25,45,1),S(45,65,1)};
            var curve=ActiveRateEstimator.Build(Data(Prefix().Concat(newPeriod).ToArray()),t.AddMinutes(65),Work(65));
            var points=curve.Runs.Where(r=>r.Points[0].Time>=t).SelectMany(r=>r.Points).ToArray();
            Check(points.Max(p=>p.Rate)<=4.01&&points.Min(p=>p.Rate)>=2.99,"isolated first 5-minute jump reappeared as a 12/h peak");
            Near(4,Area(curve.Runs.Where(r=>r.Points[0].Time>=t)));
        });
        Test("a large measured burst is retained instead of a fixed rate ceiling",()=>
        {
            var curve=ActiveRateEstimator.Build(Data(Prefix().Append(S(0,5,5)).ToArray()),t.AddMinutes(5),Work(5));
            Near(60,curve.ValueAt(t.AddMinutes(2))!.Value);Near(7,curve.Delta);
        });
        Test("new-period treatment also works without local activity and is bounded by observations",()=>
        {
            var data=Data(Prefix().Concat(new[]{S(0,5,1),S(5,30,0)}).ToArray());
            foreach(var activity in new[]{Array.Empty<WorkSpan>(),Work(100)})
            {
                var first=ActiveRateEstimator.Build(data,t.AddMinutes(30),activity);
                var later=ActiveRateEstimator.Build(data,t.AddMinutes(100),activity);
                Check(first.ValueAt(t.AddMinutes(15)) is >0 and <12,"sparse reset created a spike or stop");
                Near(first.ValueAt(t.AddMinutes(15))!.Value,later.ValueAt(t.AddMinutes(15))!.Value);
                Check(later.ValueAt(t.AddMinutes(31)) is null,"wall-clock passage added coverage");
                Near(3,later.Delta);
            }
        });
        Test("known inactivity and later missing observations still interrupt a new period",()=>
        {
            var data=Data(Prefix().Concat(new[]{S(0,5,1),S(5,10,0),S(10,15,0),S(15,20,1),S(20,25,0,SegmentIssue.Gap),S(25,30,1)}).ToArray());
            WorkSpan[] work=[new(t.AddHours(-1),t.AddMinutes(5),true,true,null),new(t.AddMinutes(15),t.AddMinutes(30),true,false,null)];
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(31),work);
            Near(0,curve.ValueAt(t.AddMinutes(12))!.Value);
            Check(curve.ValueAt(t.AddMinutes(22)) is null,"missing samples bridged during warm-up");
            Near(5,curve.Delta);
            var paths=ChartPath.PositiveRuns(curve.Runs.Select(ChartPath.HardEdges));
            Check(paths.Any(p=>p[^1].Time==t.AddMinutes(5)&&p[^1].Rate==0),"actual completion lost");
        });
        Test("all view ranges share the same reset estimate and original cumulative amounts",()=>
        {
            var data=Data(Prefix().Concat(new[]{S(0,5,1),S(5,25,0),S(25,30,1),S(30,35,0)}).ToArray());
            var activity=Work(35);
            ChartView View(int minutes)=>new(){Start=t.AddMinutes(35-minutes),End=t.AddMinutes(35),Smooth=true,Total=data,Fable=data,Codex=data,Gaps=[],Activity=new(activity,activity,activity)};
            var expected=ActiveRateEstimator.Build(data,t.AddMinutes(35),activity).ValueAt(t.AddMinutes(20))!.Value;
            foreach(var minutes in new[]{30,60,1440,4320})
            {
                var view=View(minutes);Near(expected,view.CodexAt(t.AddMinutes(20))!.Value);
                Near(view.CodexAt(t.AddMinutes(20))!.Value,view.TotalAt(t.AddMinutes(20))!.Value);
                Near(2,RateEngine.SumRange(data,t,t.AddMinutes(35)).Delta);
            }
        });
    }
}
