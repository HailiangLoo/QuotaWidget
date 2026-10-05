using QuotaWidget.Core;

static class FableDisplayTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        var t=DateTimeOffset.Parse("2026-10-03T10:00:00+08:00");
        void Check(bool ok,string why){if(!ok)throw new Exception(why);}
        void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-8,$"{a} != {b}");
        void Test(string name,Action body)=>tests.Add(("Fable display: "+name,()=>{body();return Task.CompletedTask;}));
        SeriesData Series(double delta=0.5)=>new(){Key=SeriesKey.Total,Segments=Enumerable.Range(0,60).Select(i=>new RateSegment{Start=t.AddMinutes(i*5),End=t.AddMinutes(i*5+5),Delta=delta}).ToList()};
        ModelActivity Use(int minute,string? model)=>new(t.AddMinutes(minute),t.AddMinutes(minute),model);
        bool Contains(IReadOnlyList<ChartSpan> spans,int minute)=>spans.Any(s=>s.Start<=t.AddMinutes(minute)&&s.End>t.AddMinutes(minute));
        double Area(RateTrend trend)=>trend.Runs.Sum(r=>r.Points.Zip(r.Points.Skip(1),(a,b)=>(a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours).Sum());

        Test("classifies fragments inside the same session and keeps mixed smoothing shoulders",()=>
        {
            var spans=FableDisplay.Build(Series(),Series(),[Use(30,"claude-fable-5-1"),Use(90,"claude-opus-5-5"),Use(150,"claude-fable-5-1")],t,t.AddMinutes(300),60);
            Check(Contains(spans,25)&&Contains(spans,155),"Fable fragments not identified");
            Check(!Contains(spans,65)&&!Contains(spans,115)&&!Contains(spans,250),"mixed or unevidenced period classified as Fable");
        });
        Test("empty, unknown, ambiguous model names and large unexplained changes retain both curves",()=>
        {
            foreach(var model in new string?[]{null,"","claude-opus-5-5","not-fable","claude-fableish"})
            {
                var spans=FableDisplay.Build(Series(),Series(),[Use(30,"claude-fable-5-1"),Use(30,model)],t,t.AddMinutes(300),60);
                Check(spans.Count==0,"unknown or other model hidden");
            }
            Check(FableDisplay.Build(Series(),Series(),[],t,t.AddMinutes(300),120).Count==0,"no records classified as Fable");
            Check(FableDisplay.Build(Series(5),Series(.5),[Use(30,"claude-fable-5-1")],t,t.AddMinutes(300),120).Count==0,"large non-Fable quota delta hidden");
            Check(FableDisplay.Build(Series(1),Series(.5),[Use(30,"claude-fable-5-1")],t,t.AddMinutes(300),120).Count==0,"many small unexplained differences were hidden");
        });
        Test("missing quota coverage and reset boundaries are not bridged",()=>
        {
            var f=Series();f.Segments[6]=new(){Start=t.AddMinutes(30),End=t.AddMinutes(35),Issue=SegmentIssue.Reset};
            var spans=FableDisplay.Build(Series(),f,[Use(30,"fable")],t,t.AddMinutes(300),120);
            Check(Contains(spans,25)&&!Contains(spans,32)&&Contains(spans,40),"reset covered by merged stroke");
        });
        Test("stroke clipping interpolates edges without joining hidden fragments",()=>
        {
            var points=new[]{new TrendPoint(t,2),new TrendPoint(t.AddMinutes(10),4),new TrendPoint(t.AddMinutes(20),6)};
            var visible=FableDisplay.Omit([points],[new(t.AddMinutes(5),t.AddMinutes(15))]);
            Check(visible.Count==2&&visible[0][^1].Time==t.AddMinutes(5)&&visible[1][0].Time==t.AddMinutes(15),"clipping connected across hidden curve");
            Near(3,visible[0][^1].Rate);Near(5,visible[1][0].Rate);Check(points.Length==3&&points[1].Rate==4,"source curve mutated");
        });
        Test("native totals stay distinct while either display mode can share the Fable curve",()=>
        {
            var total=Series(.1);var fable=Series(5.5/60);var spans=new[]{new ChartSpan(t,t.AddMinutes(300))};
            ChartView View(bool cumulative=false,bool visible=true)=>new(){Start=t,End=t.AddMinutes(300),Total=total,Fable=fable,Smooth=true,Gaps=[],FableToClaudeFactor=.5,FableOnlySpans=spans,Cumulative=cumulative,FableVisible=visible,FableCumulative=CumulativeSeries.Build(fable,t,t.AddMinutes(300))};
            var v=View();Check(v.MergesFable&&v.FableOnlyAt(t.AddMinutes(30)),"display classification absent");
            Near(6,RateEngine.SumRange(v.Total,v.Start,v.End).Delta);Near(5.5,RateEngine.SumRange(v.Fable,v.Start,v.End).Delta);
            Near(5.5,Area(v.DisplayFableTrend));Near(6,Area(v.TotalTrend));
            Check(View(true).MergesFable&&!View(visible:false).MergesFable,"cumulative did not merge, or explicitly hidden Fable was substituted");
        });
        Test("cumulative keeps an unchanged plateau merged, but respects unknown models and resets",()=>
        {
            var total=Series(0);var fable=Series(0);
            total.Segments[5]=new(){Start=t.AddMinutes(25),End=t.AddMinutes(30),Delta=1};fable.Segments[5]=new(){Start=t.AddMinutes(25),End=t.AddMinutes(30),Delta=.5};
            var activity=new[]{Use(27,"fable")};
            var rate=FableDisplay.Build(total,fable,activity,t,t.AddMinutes(300),30);
            var cumulative=FableDisplay.Build(total,fable,activity,t,t.AddMinutes(300),30,cumulative:true);
            Check(!Contains(rate,200)&&Contains(cumulative,200),"flat cumulative interval drew two redundant strokes");
            var mixed=FableDisplay.Build(total,fable,[..activity,Use(200,"opus")],t,t.AddMinutes(300),30,cumulative:true);
            Check(!Contains(mixed,200),"unknown/mixed model hidden in a plateau");
            fable.Segments[40]=new(){Start=t.AddMinutes(200),End=t.AddMinutes(205),Issue=SegmentIssue.Reset};
            var reset=FableDisplay.Build(total,fable,activity,t,t.AddMinutes(300),30,cumulative:true);
            Check(!Contains(reset,202),"reset interval covered");
            Near(1,RateEngine.SumRange(total,t,t.AddMinutes(300)).Delta);Near(.5,RateEngine.SumRange(fable,t,t.AddMinutes(300)).Delta);
        });
        Test("mixed cumulative history retains a continuous total; an isolated Fable viewport can share",()=>
        {
            var total=Series(.1);var fable=Series(.1);
            total.Segments[0]=new(){Start=t,End=t.AddMinutes(5),Delta=8};
            var evidence=new[]{new ChartSpan(t.AddMinutes(5),t.AddMinutes(300))};
            ChartView View(DateTimeOffset start,bool cumulative)=>new(){Start=start,End=t.AddMinutes(300),Total=total,Fable=fable,Gaps=[],Smooth=false,
                ClaudeCumulativeMode=cumulative,FableToClaudeFactor=.5,FableOnlySpans=evidence,FableCumulative=CumulativeSeries.Build(fable,start,t.AddMinutes(300))};
            Check(!View(t,true).MergesFable,"cumulative punched holes after an earlier non-Fable baseline");
            Check(View(t,false).MergesFable,"rate lost fragment alignment");
            Check(View(t.AddMinutes(5),true).MergesFable,"all-Fable viewport did not share");
            var isolated=View(t.AddMinutes(5),true);Check(isolated.MergesFable,"coverage cache setup");
            isolated.FableOnlySpans=[new(t.AddMinutes(10),t.AddMinutes(300))];
            Check(!isolated.MergesFable,"changed evidence retained stale cumulative eligibility");
            Near(13.9,RateEngine.SumRange(total,t,t.AddMinutes(300)).Delta);
            Near(6,RateEngine.SumRange(fable,t,t.AddMinutes(300)).Delta);
        });
        Test("cumulative fragments do not hide unknown intervals or accumulate large counter discrepancies",()=>
        {
            var total=Series(.1);var fable=Series(.1);
            Check(!FableDisplay.CoversCumulativeRange(total,fable,[new(t,t.AddMinutes(10)),new(t.AddMinutes(15),t.AddMinutes(300))],t,t.AddMinutes(300)),"uncovered interval was hidden");
            Check(!FableDisplay.CoversCumulativeRange(Series(.2),fable,[new(t,t.AddMinutes(300))],t,t.AddMinutes(300)),"large cumulative mismatch was hidden");
        });
        Test("model query isolates provider and includes ongoing requests across the window edge",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),"qw-model-"+Guid.NewGuid().ToString("N")+".sqlite");
            try
            {
                using var store=new TokenStore(path);
                store.Put(new("Claude","f","chat","claude-fable-5-1",t,10,0,1));
                store.Put(new("Codex","x","chat","gpt-6-astra",t.AddMinutes(1),10,0,1));
                store.Put(new("Claude","f","chat","claude-fable-5-1",t.AddMinutes(10),10,0,2));
                var rows=store.ModelActivity(t.AddMinutes(5),t.AddMinutes(15),"Claude");
                Check(rows.Count==1&&rows[0].Start==t&&rows[0].End==t.AddMinutes(10)&&FableDisplay.IsFable(rows[0].Model),"ongoing request or provider isolation wrong");
                store.Put(new("Claude","f","chat","claude-fable-5-1",t.AddMinutes(11),20,0,3));
                Check(store.ModelActivity(t,t.AddMinutes(15),"Claude").Single().Model is null,"conflicted request treated as confirmed Fable");
            }
            finally{foreach(var file in new[]{path,path+"-wal",path+"-shm"})if(File.Exists(file))File.Delete(file);}
        });
    }
}
