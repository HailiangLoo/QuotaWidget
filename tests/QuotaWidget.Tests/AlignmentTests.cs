using QuotaWidget.Core;

static class AlignmentTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        var at=DateTimeOffset.Parse("2026-10-03T10:00:00Z");
        RateSegment S(int a,int b,double delta,int group=0,SegmentIssue issue=SegmentIssue.None)=>new(){Start=at.AddMinutes(a),End=at.AddMinutes(b),Delta=delta,Group=group,Issue=issue};
        SeriesData Series(params RateSegment[] rows)=>new(){Key=SeriesKey.Total,Segments=rows.ToList()};
        void Check(bool value,string why){if(!value)throw new Exception(why);}
        void Near(double a,double b,string why)=>Check(Math.Abs(a-b)<1e-8,why+$" ({a} vs {b})");
        double Area(RateTrend trend)=>trend.Runs.Sum(run=>run.Points.Zip(run.Points.Skip(1),(a,b)=>(a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours).Sum());
        void Test(string name,Action body)=>tests.Add(("alignment: "+name,()=>{body();return Task.CompletedTask;}));
        Test("legacy pairing preserves counters while production trends retain independent observation histories",()=>
        {
            var t=Series(S(0,5,1),S(5,10,0),S(10,15,0),S(15,20,0));
            var f=Series(S(0,5,.5),S(5,10,0),S(10,15,0),S(15,20,.5));
            var aligned=QuotaAlignment.Build(t,f,at,at.AddMinutes(20));
            Check(aligned.MatchedBatches==1&&aligned.PendingDifference==0,"valid batch not paired");
            Check(t.Segments[0].Delta==1&&f.Segments[0].Delta==.5&&t.Segments[^1].Delta==0,"raw observations rewritten");
            Check(aligned.Total.Segments.Select(s=>s.Delta).SequenceEqual(new[]{.75,0,0,.25}),"shared timing weights wrong");
            var view=new ChartView{Start=at,End=at.AddMinutes(20),Smooth=true,Total=t,Fable=f,Gaps=[],FableToClaudeFactor=.5};
            Near(Area(view.TotalTrend),1,"total area changed");Near(Area(view.FableTrend),1,"Fable rescaled twice or area changed");
            Check(ReferenceEquals(view.Alignment.Total,t)&&ReferenceEquals(view.Alignment.Fable,f),"production changed the observation histories");
            Check(!ReferenceEquals(view.TotalTrend,view.FableTrend),"independent sources share an estimate");
            Near(view.TotalAt(at.AddMinutes(2))!.Value,ActiveRateEstimator.Build(t,view.End).ValueAt(at.AddMinutes(2))!.Value,"production re-paired jumps within the selected view");
        });
        Test("pending or unequal consumption stays recorded, never forced into equality",()=>
        {
            var t=Series(S(0,5,1),S(5,10,0));var f=Series(S(0,5,.5),S(5,10,0));
            var a=QuotaAlignment.Build(t,f,at,at.AddMinutes(10));
            Check(a.MatchedBatches==0&&a.PendingDifference==.5&&ReferenceEquals(a.Total.Segments[0],t.Segments[0]),"unmatched tail changed");
            Near(Area(RateTrend.Build(a.Total,at,at.AddMinutes(10))),1,"pending total area changed");
            Near(Area(RateTrend.Build(a.Fable,at,at.AddMinutes(10))),.5,"pending Fable fabricated");
            a=QuotaAlignment.Build(Series(S(0,5,2)),Series(S(0,5,1)),at,at.AddMinutes(5));
            Check(a.MatchedBatches==0&&a.Total.Segments[0].Delta==2,"other-model difference erased");
        });
        Test("selection boundaries, reset, pause, missing observations and changed groups block pairing",()=>
        {
            var t=Series(S(0,5,1),S(5,10,0),S(10,15,0));var f=Series(S(0,5,.5),S(5,10,0),S(10,15,.5));
            foreach(var (start,end) in new[]{(0,10),(5,15)})
                Check(QuotaAlignment.Build(t,f,at.AddMinutes(start),at.AddMinutes(end)).MatchedBatches==0,"borrowed consumption outside visible range");
            foreach(var issue in new[]{SegmentIssue.Reset,SegmentIssue.Gap,SegmentIssue.Missing,SegmentIssue.Decrease,SegmentIssue.UnknownWindow})
            {
                var broken=Series(S(0,5,.5),S(5,10,0,issue:issue),S(10,15,.5));
                Check(QuotaAlignment.Build(t,broken,at,at.AddMinutes(15)).MatchedBatches==0,"paired across "+issue);
            }
            Check(QuotaAlignment.Build(t,Series(S(0,5,.5),S(5,10,0,1),S(10,15,.5,1)),at,at.AddMinutes(15)).MatchedBatches==0,"paired across group boundary");
            Check(QuotaAlignment.Build(t,Series(S(0,4,.5),S(4,10,0),S(10,15,.5)),at,at.AddMinutes(15)).MatchedBatches==0,"paired mismatched observation grids");
        });
        Test("pairing stays bounded and does not use unknown conversion or alter cumulative/unsmoothed views",()=>
        {
            var t=Series(S(0,5,1),S(5,40,0),S(40,45,0));var f=Series(S(0,5,.5),S(5,40,0),S(40,45,.5));
            Check(QuotaAlignment.Build(t,f,at,at.AddMinutes(45)).MatchedBatches==0,"distant consumption moved backwards");
            t=Series(S(0,5,1),S(5,10,0));f=Series(S(0,5,.5),S(5,10,.5));
            var view=new ChartView{Start=at,End=at.AddMinutes(10),Smooth=true,Total=t,Fable=f,Gaps=[]};
            Check(view.Alignment.MatchedBatches==0&&ReferenceEquals(view.Alignment.Total,t),"unknown Fable units aligned");
            view=new(){Start=at,End=at.AddMinutes(10),Smooth=false,Total=t,Fable=f,Gaps=[],FableToClaudeFactor=.5};
            Near(view.TotalAt(at.AddMinutes(2))!.Value,12,"unsmoothed rate replaced");
            Near(view.FableAt(at.AddMinutes(2))!.Value,6,"unsmoothed Fable replaced");
            var before=CumulativeSeries.Build(t,at,at.AddMinutes(10));_=view.TotalTrend;
            Near(CumulativeSeries.Build(t,at,at.AddMinutes(10)).ValueAt(at.AddMinutes(5))!.Value,before.ValueAt(at.AddMinutes(5))!.Value,"cumulative history changed");
        });
        Test("many fractional batches conserve every original source interval group total",()=>
        {
            var random=new Random(23);var tr=new List<RateSegment>();var fr=new List<RateSegment>();
            for(var batch=0;batch<100;batch++)
            {
                var amount=(random.Next(1,10))/2d;var p=batch*20;
                tr.AddRange([S(p,p+5,amount),S(p+5,p+10,0),S(p+10,p+15,0),S(p+15,p+20,0)]);
                fr.AddRange([S(p,p+5,amount*.5),S(p+5,p+10,0),S(p+10,p+15,0),S(p+15,p+20,amount*.5)]);
            }
            var a=QuotaAlignment.Build(Series(tr.ToArray()),Series(fr.ToArray()),at,at.AddMinutes(2000));
            Check(a.MatchedBatches==100,"missed fractional paired batches");
            Near(a.Total.Segments.Sum(s=>s.Delta),tr.Sum(s=>s.Delta),"batch total not conserved");
            Near(Area(RateTrend.Build(a.Total,at,at.AddMinutes(2000))),tr.Sum(s=>s.Delta),"display integral not conserved");
            Near(Area(RateTrend.Build(a.Fable,at,at.AddMinutes(2000))),fr.Sum(s=>s.Delta),"Fable display integral not conserved");
        });
    }
}
