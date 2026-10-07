using QuotaWidget.Core;

static class ModelContextTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        var t=DateTimeOffset.Parse("2026-10-07T10:00:00+08:00");
        void Test(string name,Action body)=>tests.Add(("model context: "+name,()=>{body();return Task.CompletedTask;}));
        void Check(bool b,string why){if(!b)throw new Exception(why);}
        void Near(double a,double? b)=>Check(b is {} v&&Math.Abs(a-v)<1e-6,$"expected {a}, got {b}");
        RateSegment S(double a,double b,double d)=>new(){Start=t.AddMinutes(a),End=t.AddMinutes(b),Delta=d};
        SeriesData Data(params RateSegment[] s)=>new(){Key=SeriesKey.Total,Segments=s.ToList()};
        WorkSpan W(double a,double b,string model,bool done=true)=>new(t.AddMinutes(a),t.AddMinutes(b),true,done,model);
        RateTrend Build(SeriesData source,WorkSpan[] work,double end=60)=>ActiveRateEstimator.Build(source,t.AddMinutes(end),WorkActivity.Merge(work,TimeSpan.Zero),
            contextEdges:WorkActivity.ModelChanges(work,t,t.AddMinutes(end)));
        double Area(RateTrend curve)=>curve.Runs.Where(r=>!r.Provisional).Sum(r=>r.Points.Zip(r.Points.Skip(1),(a,b)=>(a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours).Sum());

        Test("both model-switch directions isolate new rates from earlier consumption without hiding a curve",()=>
        {
            foreach(var models in new[]{new[]{"opus","fable"},new[]{"fable","opus"}})
            {
                WorkSpan[] work=[W(0,20,models[0]),W(25,60,models[1],false)];
                SeriesData Source(double old)=>Data(S(0,5,old),S(5,20,0),S(20,25,0),S(25,30,1),S(30,60,0));
                var a=Build(Source(1),work);var b=Build(Source(50),work);
                foreach(var m in new[]{26d,32,45,59})Near(a.ValueAt(t.AddMinutes(m))!.Value,b.ValueAt(t.AddMinutes(m)));
                Near(0,a.ValueAt(t.AddMinutes(22)));Near(2,a.Delta);Near(51,b.Delta);Near(2,Area(a));
                Check(a.Runs.Any(r=>r.HardEnd&&r.Points[^1].Time==t.AddMinutes(20)),"old task completion was erased");
            }
        });
        Test("concurrent-model entry and exit each separate estimation while work stays continuous",()=>
        {
            WorkSpan[] work=[W(0,30,"opus"),W(20,60,"fable",false)];
            var edges=WorkActivity.ModelChanges(work,t,t.AddHours(1));
            Check(edges.SequenceEqual(new[]{t.AddMinutes(20),t.AddMinutes(30)}),"concurrent model-set changes missing");
            SeriesData Source(double old)=>Data(S(0,20,old),S(20,30,2),S(30,40,1),S(40,60,0));
            var a=Build(Source(1),work);var b=Build(Source(50),work);
            Near(a.ValueAt(t.AddMinutes(45))!.Value,b.ValueAt(t.AddMinutes(45)));
            Check(!a.Runs.Any(r=>r.HardEnd&&edges.Contains(r.Points[^1].Time)||r.HardStart&&edges.Contains(r.Points[0].Time)),"model composition created a fake work stop");
            Near(4,a.Delta);
        });
        Test("positive observations spanning two model phases stay whole and independent of neighbouring rates",()=>
        {
            WorkSpan[] work=[W(0,24,"opus"),W(26,60,"fable",false)];
            SeriesData Source(double old)=>Data(S(0,20,old),S(20,30,5),S(30,40,1),S(40,60,0));
            var data=Source(1);var a=Build(data,work);var b=Build(Source(50),work);
            foreach(var m in new[]{23d,27,45})Near(a.ValueAt(t.AddMinutes(m))!.Value,b.ValueAt(t.AddMinutes(m)));
            Near(0,a.ValueAt(t.AddMinutes(25)));Near(7,a.Delta);Near(7,Area(a));
            Check(!a.CanLabelPeak(t.AddMinutes(23))&&!a.CanLabelPeak(t.AddMinutes(27)),"ambiguous crossing observation became a model peak");
            Check(data.Segments.Count==4&&data.Segments[1].Delta==5&&data.Segments[1].Group==0,"raw quota was split or rewritten");
        });
        Test("a straddling observation supported only by the new model retains subsequent zero evidence",()=>
        {
            WorkSpan[] work=[W(0,15,"opus"),W(26,60,"fable",false)];
            var a=Build(Data(S(0,5,1),S(5,20,0),S(20,30,1),S(30,60,0)),work);
            Check(a.ValueAt(t.AddMinutes(45))>0,"first new-model jump was isolated from its own following observations");
            Near(2,a.Delta);Near(2,Area(a));
        });
        Test("short handoff merging never fills idle between different models",()=>
        {
            foreach(var next in new[]{"opus","fable"})
            {
                WorkSpan[] work=[W(0,24,"opus"),W(24.05,60,next,false)];
                var curve=Build(Data(S(0,5,1),S(5,25,0),S(25,30,1),S(30,60,0)),work);
                var value=curve.ValueAt(t.AddMinutes(24.025));
                if(next=="fable")Near(0,value);else Check(value>0,"same-model handoff no longer coalesces");
            }
        });
        Test("model contexts and estimates are invariant across chart ranges and never use future work",()=>
        {
            WorkSpan[] work=[W(0,20,"opus"),W(25,60,"fable",false),W(70,90,"opus")];
            var data=Data(S(0,5,1),S(5,25,0),S(25,30,1),S(30,60,0));
            ChartView View(int minutes)=>new(){Start=t.AddMinutes(60-minutes),End=t.AddHours(1),Total=data,Fable=Data(),Gaps=[],Smooth=true,
                Activity=new(WorkActivity.Merge(work,TimeSpan.Zero),[],[]){ClaudeModels=work}};
            var reference=Build(data,work[..2]);
            foreach(var range in new[]{30,60,720,1440})
            {
                var v=View(range);Near(reference.ValueAt(t.AddMinutes(45))!.Value,v.TotalAt(t.AddMinutes(45)));
                Check(v.ClaudeModelChanges.SequenceEqual(new[]{t.AddMinutes(25)}),"viewport or future model changed context");
                Near(2,RateEngine.SumRange(v.Total,t,t.AddHours(1)).Delta);
            }
        });
        Test("a confirmed concurrent model completion outranks the provider handoff grace",()=>
        {
            WorkSpan[] work=[W(0,20,"opus"),W(10,60,"fable",false)];
            var edges=WorkActivity.ModelChanges(work,t,t.AddMinutes(20.05));
            Check(edges.SequenceEqual(new[]{t.AddMinutes(10),t.AddMinutes(20)}),"completed Opus remained in the active model set for the grace period");
        });
        Test("a clean counter reset cannot carry an old model's rate into a new model",()=>
        {
            WorkSpan[] work=[W(0,7,"opus"),W(7,60,"fable",false)];
            SeriesData Source(double old)=>Data(S(0,5,old),new(){Start=t.AddMinutes(5),End=t.AddMinutes(10),Delta=-100,Issue=SegmentIssue.Reset,CounterResetOnly=true,StartsAtCapacity=true},S(10,20,1),S(20,60,0));
            var a=Build(Source(1),work);var b=Build(Source(50),work);
            foreach(var minute in new[]{12d,25,45})Near(a.ValueAt(t.AddMinutes(minute))!.Value,b.ValueAt(t.AddMinutes(minute)));
            Near(2,a.Delta);Near(51,b.Delta);
        });
    }
}
