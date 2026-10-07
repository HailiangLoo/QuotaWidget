using QuotaWidget.Core;

static class GapRateTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        var t=DateTimeOffset.Parse("2026-10-07T07:55:00+08:00");
        void Test(string name,Action body)=>tests.Add(("gap rate: "+name,()=>{body();return Task.CompletedTask;}));
        void Check(bool b,string why){if(!b)throw new Exception(why);}
        void Near(double a,double? b)=>Check(b is {} v&&Math.Abs(a-v)<1e-6,$"expected {a}, got {b}");
        RateSegment S(double a,double b,double d,SegmentIssue issue=SegmentIssue.None)=>new(){Start=t.AddMinutes(a),End=t.AddMinutes(b),Delta=d,Issue=issue,Group=a>=0?1:0};
        SeriesData Data(IEnumerable<RateSegment> items)=>new(){Key=SeriesKey.Total,Segments=items.ToList()};
        RateSegment[] Before()=>[S(-60,-30,2),S(-30,-15,0),S(-15,0,99,SegmentIssue.Gap)];
        WorkSpan[] Open(double end)=>[new(t.AddHours(-1),t.AddMinutes(end),true,false,null)];
        double Area(IEnumerable<TrendRun> runs)=>runs.Sum(r=>r.Points.Zip(r.Points.Skip(1),(a,b)=>(a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours).Sum());

        Test("later unchanged readings retain a taper instead of flattening the entire sparse context",()=>
        {
            var data=Data(Before().Concat([S(0,5,1),S(5,10,1),S(10,40,0)]));
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(40),Open(40));
            Check(curve.ValueAt(t.AddMinutes(3))>1.2*curve.ValueAt(t.AddMinutes(37)),"zero-observation tail was pooled into a constant rate");
            Check(curve.ValueAt(t.AddMinutes(39))>0,"unchanged counter invented a work stop");
            Near(4,curve.Delta);Near(4,Area(curve.Runs));
            Check(!curve.IsProvisional(t.AddMinutes(30))&&!curve.CanLabelPeak(t.AddMinutes(3)),"sparse shape added quota or a peak label");
        });
        Test("sparse completed tasks retain changing intensity and exact cuts after later idle samples",()=>
        {
            var data=Data(Before().Concat(Enumerable.Range(0,12).Select(i=>S(i*5,(i+1)*5,i is 0 or 2?1:0))));
            WorkSpan[] work=[new(t.AddHours(-1),t.AddMinutes(-15),true,true,null),new(t,t.AddMinutes(13),true,true,null),new(t.AddMinutes(15),t.AddMinutes(17),true,true,null),new(t.AddMinutes(25),t.AddMinutes(43),true,true,null),new(t.AddMinutes(52),t.AddMinutes(54),true,true,null)];
            var curve=ActiveRateEstimator.Build(data,t.AddHours(1),work);
            Check(curve.ValueAt(t.AddMinutes(3))>curve.ValueAt(t.AddMinutes(53)),"all completed tasks forced to the same rate");
            foreach(var m in new[]{14d,20,47,56})Near(0,curve.ValueAt(t.AddMinutes(m)));
            Near(4,curve.Delta);Near(2,Area(curve.Runs.Where(r=>r.Points[0].Time>=t)));
            Check(curve.Runs.Where(r=>r.Points[0].Time>=t).All(r=>!r.Provisional),"settled work remained a prediction");
        });
        Test("one jump without later observations cannot invent within-task variation",()=>
        {
            var data=Data(Before().Concat([S(0,5,0),S(5,10,0),S(10,15,0),S(15,20,1)]));
            WorkSpan[] work=[new(t.AddHours(-1),t.AddMinutes(-15),true,true,null),new(t,t.AddMinutes(10),true,true,null),new(t.AddMinutes(12),t.AddMinutes(13),true,true,null),new(t.AddMinutes(16),t.AddMinutes(20),true,false,null)];
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(20),work);
            foreach(var m in new[]{1d,9,12.5,18})Near(4,curve.ValueAt(t.AddMinutes(m)));
            Near(0,curve.ValueAt(t.AddMinutes(11)));Near(3,curve.Delta);
        });

        Test("sparse restart redistributes observed quota through unchanged readings without adding a predicted tail",()=>
        {
            foreach(var quantum in new[]{.5,1d})
            {
                var data=Data(Before().Concat([S(0,5,quantum),S(5,15,0),S(15,20,quantum),S(20,30,0)]));
                var curve=ActiveRateEstimator.Build(data,t.AddMinutes(30),Open(30),quantum:quantum);
                Check(curve.ValueAt(t.AddMinutes(1))>curve.ValueAt(t.AddMinutes(29)),"unchanged tail lost its effect");
                Check(curve.Runs.Where(r=>r.Points[0].Time>=t).SelectMany(r=>r.Points).All(p=>p.Rate>0&&p.Rate<12*quantum),"first-poll spike or invented stop");
                Near(2+2*quantum,curve.Delta);Near(curve.Delta,Area(curve.Runs));
                Check(curve.ValueAt(t.AddMinutes(-5)) is null,"gap was painted or its 99 points assigned");
                Check(!curve.IsProvisional(t.AddMinutes(25)),"restart invented additional quota beyond observed total");
                Check(!curve.CanLabelPeak(t.AddMinutes(2))&&!curve.CanLabelPeak(t.AddMinutes(29)),"sparse startup average promoted to a peak");
            }
        });
        Test("a known observation break settles the preceding open context without inventing work completion",()=>
        {
            var data=Data(Before());var curve=ActiveRateEstimator.Build(data,t,Open(30));
            Check(curve.Runs.All(r=>!r.Provisional&&!r.HardEnd),"pre-gap endpoint retained live extrapolation or became a work stop");
            Near(2,Area(curve.Runs));Near(2,curve.Delta);
            Check(ActiveRateEstimator.Build(data,t.AddMinutes(-1),Open(30)).IsProvisional(t.AddMinutes(-20)),"future gap altered earlier live estimate");
        });
        Test("restart pooling keeps actual idle time out and preserves exact task cuts",()=>
        {
            var data=Data(Before().Concat([S(0,5,1),S(5,10,0),S(10,15,0),S(15,20,1),S(20,30,0)]));
            WorkSpan[] work=[new(t.AddHours(-1),t.AddMinutes(-15),true,false,null),new(t,t.AddMinutes(5),true,true,null),new(t.AddMinutes(15),t.AddMinutes(20),true,true,null)];
            // The first span is historical metadata; its end cannot extend into the new context.
            work[0]=work[0] with{KnownEnd=true};
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(30),work);
            Near(12,curve.ValueAt(t.AddMinutes(2)));Near(12,curve.ValueAt(t.AddMinutes(17)));Near(0,curve.ValueAt(t.AddMinutes(12)));
            var paths=ChartPath.PositiveRuns(curve.Runs.Select(ChartPath.HardEdges));
            Check(paths.Any(p=>p[^1].Time==t.AddMinutes(5)&&p[^1].Rate==0)&&paths.Any(p=>p[0].Time==t.AddMinutes(15)&&p[0].Rate==0),"work boundaries shifted");
        });
        Test("initial phase remains pooled after later observations establish the restart shape",()=>
        {
            var data=Data(Before().Concat([S(0,5,1),S(5,25,1),S(25,45,1),S(45,65,1)]));
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(65),Open(65));
            var points=curve.Runs.Where(r=>r.Points[0].Time>=t).SelectMany(r=>r.Points).ToArray();
            Check(points.Max(p=>p.Rate)<=4.01&&points.Min(p=>p.Rate)>=2.99,"first five-minute jump became a lasting false peak");
            Near(4,Area(curve.Runs.Where(r=>r.Points[0].Time>=t)));
            Check(curve.CanLabelPeak(t.AddMinutes(10)),"established restart never regained peak eligibility");
        });
        Test("large observed bursts remain visible, with no rate ceiling or borrowed pre-gap values",()=>
        {
            var data=Data(Before().Append(S(0,5,5)));
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(5),Open(5));
            Near(60,curve.ValueAt(t.AddMinutes(2)));Near(7,curve.Delta);
            Check(curve.CanLabelPeak(t.AddMinutes(2)),"large observed burst lost its peak");
        });
        Test("fallback and missing samples use the same startup policy without view or wall-clock dependence",()=>
        {
            foreach(var issue in new[]{SegmentIssue.Gap,SegmentIssue.Missing,SegmentIssue.Decrease})
            foreach(var work in new[]{Array.Empty<WorkSpan>(),Open(60)})
            {
                var data=Data([S(-5,0,50,issue),S(0,5,1),S(5,15,0)]);
                var curve=ActiveRateEstimator.Build(data,t.AddMinutes(15),work);
                var later=ActiveRateEstimator.Build(data,t.AddHours(1),work);
                var value=curve.ValueAt(t.AddMinutes(12));Check(value is >0 and <12,"sparse estimate became a spike or a stop");
                Near(value!.Value,later.ValueAt(t.AddMinutes(12)));Near(1,Area(curve.Runs));
                Check(later.ValueAt(t.AddMinutes(16)) is null,"advanced past last observation");
                Check(!later.CanLabelPeak(t.AddMinutes(12)),"fallback or missing data bypassed sparse peak protection");
                foreach(var lookback in new[]{10,60,1440})
                {
                    var view=new ChartView{Start=t.AddMinutes(15-lookback),End=t.AddMinutes(15),Smooth=true,Total=data,Fable=data,Codex=data,Gaps=[],Activity=new(work,work,work)};
                    Near(value.Value,view.TotalAt(t.AddMinutes(12)));Near(value.Value,view.CodexAt(t.AddMinutes(12)));
                }
            }
        });
        Test("implicit interval and group discontinuities also restart without crossing or borrowing",()=>
        {
            foreach(var gap in new[]{0,15})
            {
                var before=S(-60,-gap,10);
                var after=S(0,5,1);after.Group=gap==0?1:0;
                var zero=S(5,15,0);zero.Group=after.Group;
                var data=Data([before,after,zero]);
                var curve=ActiveRateEstimator.Build(data,t.AddMinutes(15),Open(15));
                var independent=ActiveRateEstimator.Build(Data([S(-5,0,0,SegmentIssue.Gap),after,zero]),t.AddMinutes(15),Open(15));
                Near(independent.ValueAt(t.AddMinutes(12))!.Value,curve.ValueAt(t.AddMinutes(12)));Near(11,curve.Delta);
                Check(!curve.CanLabelPeak(t.AddMinutes(12)),"implicit break retained prior support");
                if(gap>0)Check(curve.ValueAt(t.AddMinutes(-5)) is null,"implicit gap was connected");
            }
        });
        Test("long sparse open tails stay positive and bounded while no new quota is added",()=>
        {
            foreach(var quantum in new[]{.5,1d})
            {
                var data=Data([S(-5,0,0,SegmentIssue.Gap),S(0,5,quantum),S(5,720,0)]);
                var curve=ActiveRateEstimator.Build(data,t.AddMinutes(720),[new(t,t.AddMinutes(720),true,false,null)],quantum:quantum);
                Near(quantum,curve.Delta);Near(quantum,Area(curve.Runs));
                Check(curve.Runs.SelectMany(r=>r.Points).All(p=>p.Rate>0&&double.IsFinite(p.Rate)),"unchanged counter fabricated a stop");
                Check(curve.ValueAt(t.AddMinutes(5))>curve.ValueAt(t.AddMinutes(700)),"long zero-observation tail still flat");
                Check(curve.Runs.Sum(r=>r.Points.Count)<2100,"unbounded rendering geometry");
            }
        });
        Test("three differently spaced jumps retain their rate changes instead of becoming a rectangle",()=>
        {
            var data=Data([S(-5,0,0,SegmentIssue.Gap),S(0,20,1),S(20,30,1),S(30,45,1)]);
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(45),[new(t,t.AddMinutes(45),true,false,null)]);
            Check(curve.ValueAt(t.AddMinutes(5))<curve.ValueAt(t.AddMinutes(30))-.5,"three observed jump intervals were flattened");
            Near(3,curve.Delta);Near(3,Area(curve.Runs));
        });
        Test("first-phase protection keeps later acceleration and deceleration within the initial prefix",()=>
        {
            var data=Data([S(-5,0,0,SegmentIssue.Gap),S(0,2,1),S(2,32,1),S(32,42,1)]);
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(42),[new(t,t.AddMinutes(42),true,false,null)],30);
            Check(curve.ValueAt(t.AddMinutes(37))>curve.ValueAt(t.AddMinutes(20))+.5,"protecting the first jump erased subsequent changes");
            Check(curve.ValueAt(t.AddMinutes(1))<10,"first two-minute jump became a 30/h spike");
            Near(3,curve.Delta);Near(3,Area(curve.Runs));
        });
    }
}
