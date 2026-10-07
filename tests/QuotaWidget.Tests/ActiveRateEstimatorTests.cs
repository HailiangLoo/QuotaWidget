using QuotaWidget.Core;
static class ActiveRateEstimatorTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        var t=DateTimeOffset.Parse("2026-10-03T12:00:00+08:00");
        void Test(string name,Action body)=>tests.Add(("active estimate: "+name,()=>{body();return Task.CompletedTask;}));
        void Check(bool b,string why){if(!b)throw new Exception(why);}
        void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-6,$"expected {a}, got {b}");
        RateSegment S(double a,double b,double d,SegmentIssue issue=SegmentIssue.None,int group=0)=>new(){Start=t.AddMinutes(a),End=t.AddMinutes(b),Delta=d,Issue=issue,Group=group};
        SeriesData Data(params RateSegment[] s)=>new(){Key=SeriesKey.Total,Segments=s.ToList()};
        double Area(IEnumerable<TrendRun> runs)=>runs.Sum(r=>r.Points.Zip(r.Points.Skip(1),(a,b)=>(a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours).Sum());
        Test("150 minute cap persists, changes sparse-jump smoothing and preserves area and work edges",()=>
        {
            var settings=new WidgetSettings{TrendMinutes=150};settings.Normalize();Check(settings.TrendMinutes==150,"150 minute cap normalized away");
            var file=Path.Combine(Path.GetTempPath(),"qw-smoothing-"+Guid.NewGuid()+".json");
            try{settings.Save(file);Check(WidgetSettings.Load(file,out _).TrendMinutes==150,"150 minute preference lost");}finally{File.Delete(file);}
            var data=Data(Enumerable.Range(0,84).Select(i=>S(i*5,(i+1)*5,i is 11 or 23 or 35 or 47 or 59 or 71?i==35?3:1:0)).ToArray());
            WorkSpan[] work=[new(t,t.AddMinutes(360),true,true,null)];
            var a=ActiveRateEstimator.Build(data,t.AddMinutes(420),work,120);
            var b=ActiveRateEstimator.Build(data,t.AddMinutes(420),work,150);
            Near(a.Delta,b.Delta);Near(8,Area(b.Runs.Where(r=>!r.Provisional)));
            Check(Enumerable.Range(1,359).Any(m=>Math.Abs((a.ValueAt(t.AddMinutes(m))??0)-(b.ValueAt(t.AddMinutes(m))??0))>1e-4),"150 minute choice never reaches estimator");
            Check(b.ValueAt(t.AddMinutes(-1)) is null or 0&&b.ValueAt(t.AddMinutes(361)) is null or 0,"smoothing escaped work boundaries");
        });
        Test("handoff lifecycle drives both estimated paths and axis boundaries",()=>
        {
            foreach(var gap in new[]{.052,5.193,10d,10.001,60d})
            {
                var data=Data(Enumerable.Range(0,12).Select(i=>S(i*5,(i+1)*5,1)).ToArray());
                WorkSpan[] work=[new(t,t.AddMinutes(20),true,true,null),new(t.AddMinutes(20).AddSeconds(gap),t.AddHours(1),true,true,null)];
                var end=t.AddHours(1).AddSeconds(20);
                var curve=ActiveRateEstimator.Build(data,end,work);
                Near(12,curve.Delta);Near(12,Area(curve.Runs.Where(r=>!r.Provisional)));
                var center=t.AddMinutes(20).AddSeconds(gap/2);
                var paths=ChartPath.PositiveRuns(curve.Runs.Select(ChartPath.HardEdges));
                Check(paths.Count==(gap<=10?1:2),"wrong phase topology for "+gap);
                Check(gap<=10?curve.ValueAt(center)>0:curve.ValueAt(center)==0,"hover and phase topology differ");
                var view=new ChartView{Start=t,End=end,Smooth=true,Total=data,Fable=data,Codex=data,Gaps=[],Activity=new(work,work,work)};
                Check(!view.VisibleTrendEdges.Contains(work[0].End)||gap>10,"raw turn boundary leaked into chart axis");
                Near(curve.ValueAt(center)!.Value,view.CodexAt(center)!.Value);
                Near(curve.ValueAt(center)!.Value,view.TotalAt(center)!.Value);
            }
        });
        Test("short handoffs never bridge missing quota observations or resets",()=>
        {
            foreach(var issue in new[]{SegmentIssue.Gap,SegmentIssue.Reset,SegmentIssue.Missing})
            {
                var data=Data(S(0,5,1),S(5,5.1,0,issue,1),S(5.1,10,1,group:2));
                var curve=ActiveRateEstimator.Build(data,t.AddMinutes(11),[new(t,t.AddMinutes(5),true,true,null),new(t.AddMinutes(5).AddSeconds(5),t.AddMinutes(10),true,true,null)]);
                Near(2,curve.Delta);Check(curve.ValueAt(t.AddMinutes(5.05)) is null,"handoff erased quota evidence: "+issue);
            }
        });
        Test("fallback session markers cannot create hard stops inside observed continuous consumption",()=>
        {
            var data=Data(Enumerable.Range(0,12).Select(i=>S(i*5,(i+1)*5,1)).ToArray());
            var curve=ActiveRateEstimator.Build(data,t.AddHours(1),fallbackEdges:[t.AddMinutes(12),t.AddMinutes(43)]);
            Near(12,curve.Delta);
            Check(curve.Runs.Count==1&&!curve.Runs[0].HardStart&&!curve.Runs[0].HardEnd,"plain session timestamps became fake starts/stops");
            foreach(var p in new[]{12d,43d})Near(12,curve.ValueAt(t.AddMinutes(p))!.Value);
        });
        Test("integer consumption spans multiple tasks while work edges stay exact",()=>
        {
            var data=Data(Enumerable.Range(0,12).Select(i=>S(i*5,(i+1)*5,i==4?1:0)).ToArray());
            WorkSpan[] work=[new(t.AddMinutes(1),t.AddMinutes(4),true,true,null),new(t.AddMinutes(20),t.AddMinutes(23),true,true,null)];
            var curve=ActiveRateEstimator.Build(data,t.AddHours(1),work);
            Near(1,curve.Delta);Near(1,Area(curve.Runs.Where(r=>!r.Provisional)));
            Near(10,curve.ValueAt(t.AddMinutes(2))!.Value);Near(10,curve.ValueAt(t.AddMinutes(21))!.Value);
            Check(curve.ValueAt(t.AddMinutes(10))==0,"idle gap filled");
            var paths=ChartPath.PositiveRuns(curve.Runs.Select(ChartPath.HardEdges));
            Check(paths.Count==2&&paths[0][0].Time==work[0].Start&&paths[1][^1].Time==work[1].End,"lost work edges");
        });
        Test("same historical point and clipped geometry are invariant across view ranges",()=>
        {
            var data=Data(Enumerable.Range(0,144).Select(i=>S(i*5,(i+1)*5,i%7==0?1:0)).ToArray());
            ChartView View(double minutes)=>new(){Start=t.AddMinutes(720-minutes),End=t.AddMinutes(720),Smooth=true,Total=data,Fable=Data(),Gaps=[],CodexVisible=false};
            var small=View(60);var large=View(720);
            for(var i=665;i<715;i++)Near(small.TotalAt(t.AddMinutes(i))!.Value,large.TotalAt(t.AddMinutes(i))!.Value);
            var path=ChartPath.Clip(large.TotalTrend.Runs.Select(ChartPath.HardEdges),small.Start,small.End);
            Check(path.All(p=>p[0].Time>=small.Start&&p[^1].Time<=small.End),"offscreen geometry retained");
        });
        Test("first post-completion report maps back without an idle hump",()=>
        {
            var data=Data(S(0,5,0),S(5,10,.5),S(10,15,0));
            var work=new[]{new WorkSpan(t,t.AddMinutes(4.8),true,true,null)};
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(15),work,quantum:.5);
            Near(.5,curve.Delta);Near(0,curve.ValueAt(t.AddMinutes(7))!.Value);
            var positive=ChartPath.PositiveRuns(curve.Runs.Select(ChartPath.HardEdges));
            Check(positive[^1][^1].Time==work[0].End,"late report leaks past completion");
        });
        Test("gaps resets and unexplained usage are never reassigned across missing data",()=>
        {
            var data=Data(S(0,5,1),S(5,20,0,SegmentIssue.Gap,1),S(20,25,2,group:1));
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(25),[new(t,t.AddMinutes(3),true,true,null)]);
            Near(3,curve.Delta);Check(curve.ValueAt(t.AddMinutes(12)) is null,"bridged gap");
            Check(curve.UnlocatedAt(t.AddMinutes(22))?.Delta==2&&curve.ValueAt(t.AddMinutes(22)) is null,"unexplained consumption was erased or invented a task rate");
        });
        Test("one unmatched counter jump cannot smear an idle night or erase supported task boundaries",()=>
        {
            SeriesData Source(double unmatched)=>Data(Enumerable.Range(0,108).Select(i=>S(i*5,(i+1)*5,i==1||i==99?1:i==10?unmatched:0)).ToArray());
            WorkSpan[] work=[new(t,t.AddMinutes(20),true,true,"fable"),new(t.AddMinutes(480),t.AddMinutes(510),true,true,"fable")];
            var reference=ActiveRateEstimator.Build(Source(0),t.AddMinutes(540),work);
            var data=Source(1);var curve=ActiveRateEstimator.Build(data,t.AddMinutes(540),work);
            Near(3,curve.Delta);Near(2,Area(curve.Runs.Where(r=>!r.Provisional)));Near(1,curve.Unlocated.Sum(s=>s.Delta));
            Check(curve.Unlocated.Single().Start==t.AddMinutes(50)&&curve.Unlocated.Single().End==t.AddMinutes(55),"unmatched observation moved outside its sample");
            Check(curve.ValueAt(t.AddMinutes(52)) is null,"unknown timing pretends to be an instantaneous rate");
            foreach(var minute in new[]{25d,45,60,200,470,520})Near(0,curve.ValueAt(t.AddMinutes(minute))??0);
            foreach(var minute in new[]{2d,12,481,490,509})Near(reference.ValueAt(t.AddMinutes(minute))??0,curve.ValueAt(t.AddMinutes(minute))??0);
            Check(curve.Runs.Any(r=>r.HardEnd&&r.Points[^1].Time==t.AddMinutes(20)),"one unmatched reading removed a confirmed completion");
            Check(data.Segments[10].Delta==1&&data.Segments.Sum(s=>s.Delta)==3,"raw ledger changed");
        });
        Test("pending tail is marked capped and excluded from confirmed quota",()=>
        {
            var data=Data(Enumerable.Range(0,48).Select(i=>S(i*5,(i+1)*5,i==0?.5:0)).ToArray());
            var curve=ActiveRateEstimator.Build(data,t.AddHours(4),[new(t,t.AddHours(4),true,false,null)],quantum:.5);
            Near(.5,curve.Delta);Check(curve.IsProvisional(t.AddHours(3)),"pending remainder appears confirmed");
            var pending=Area(curve.Runs.Where(r=>r.Provisional));Check(pending>0&&pending<=.5+1e-8,"tail exceeds quantum cap");
            Near(curve.Runs.Last(r=>!r.Provisional).Points[^1].Rate,curve.Runs.First(r=>r.Provisional).Points[0].Rate);
            var changed=Data(data.Segments.Take(47).Append(S(235,240,.5)).ToArray());
            var after=ActiveRateEstimator.Build(changed,t.AddHours(4),[new(t,t.AddHours(4),true,false,null)],quantum:.5);
            Near(1,after.Delta);Check(!after.IsProvisional(t.AddHours(3)),"new observation did not replace pending estimate");
        });
        Test("continuous activity does not hit zero when the old triangular quota budget expires",()=>
        {
            foreach(var quantum in new[]{.5,1d})
            foreach(var duration in new[]{35d,240d,10000d})
            {
                // A 4/h observed rate, then valid unchanged readings while work continues.
                var data=Data(S(0,15,1),S(15,15+duration,0));
                var curve=ActiveRateEstimator.Build(data,t.AddMinutes(15+duration),[new(t,t.AddMinutes(15+duration),true,false,null)],quantum:quantum);
                var pending=curve.Runs.Where(r=>r.Provisional).ToArray();
                Check(pending.Length==1&&pending[0].Points.All(p=>p.Rate>0),"no-jump clock invented a stop");
                Near(curve.Runs.Last(r=>!r.Provisional).Points[^1].Rate,pending[0].Points[0].Rate);
                Check(pending[0].Points.Zip(pending[0].Points.Skip(1)).All(p=>p.First.Time<p.Second.Time&&p.First.Rate>=p.Second.Rate),"tail has a duplicate time, rise or discontinuity");
                Check(Area(pending)<=quantum+1e-8,"no-zero continuation exceeded its quota uncertainty bound");
                Near(1,curve.Delta);Check(pending[0].Points.Count<150,"long unchanging tail creates excessive geometry");
            }
        });
        Test("continuous tail only advances with samples and still stops on an actual completion",()=>
        {
            var data=Data(S(0,15,1),S(15,50,0));
            var active=ActiveRateEstimator.Build(data,t.AddMinutes(50),[new(t,t.AddMinutes(50),true,false,null)]);
            var stale=ActiveRateEstimator.Build(data,t.AddHours(3),[new(t,t.AddHours(3),true,false,null)]);
            Near(active.ValueAt(t.AddMinutes(49))!.Value,stale.ValueAt(t.AddMinutes(49))!.Value);
            Check(stale.ValueAt(t.AddMinutes(51)) is null,"advanced past the latest observation just because time passed");
            var complete=ActiveRateEstimator.Build(data,t.AddHours(3),[new(t,t.AddMinutes(45),true,true,null)]);
            Near(0,complete.ValueAt(t.AddMinutes(46))!.Value);
            Check(ChartPath.PositiveRuns(complete.Runs.Select(ChartPath.HardEdges))[^1][^1].Time==t.AddMinutes(45),"real completion no longer cuts the tail");
        });
        Test("completed historical tails settle on unchanged observations and keep observed area",()=>
        {
            var data=Data(Enumerable.Range(0,40).Select(i=>S(i*5,(i+1)*5,i==0?1:0)).ToArray());
            WorkSpan[] work=[new(t,t.AddMinutes(12),true,true,null)];
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(200),work);
            Near(1,curve.Delta);Near(1,Area(curve.Runs));
            Check(curve.Runs.All(r=>!r.Provisional),"two hours of new samples left a completed episode pending");
            Check(curve.ValueAt(t.AddMinutes(10))>0,"closing the tail abruptly discarded real work after the last jump");
            var paths=ChartPath.PositiveRuns(curve.Runs.Select(ChartPath.HardEdges));
            Check(paths[^1][^1].Time==work[0].End&&paths[^1][^1].Rate==0,"closure lost the exact completion edge");
            Near(0,curve.ValueAt(t.AddMinutes(13))!.Value);
        });
        Test("tail closure needs fresh valid idle samples rather than wall clock or a data gap",()=>
        {
            WorkSpan[] work=[new(t,t.AddMinutes(12),true,true,null)];
            var frozen=Data(S(0,5,1),S(5,10,0),S(10,15,0));
            var first=ActiveRateEstimator.Build(frozen,t.AddMinutes(20),work);
            var later=ActiveRateEstimator.Build(frozen,t.AddHours(3),work);
            Check(first.IsProvisional(t.AddMinutes(11))&&later.IsProvisional(t.AddMinutes(11)),"wall-clock passage invented a settling observation");
            Near(first.ValueAt(t.AddMinutes(11))!.Value,later.ValueAt(t.AddMinutes(11))!.Value);
            var missing=ActiveRateEstimator.Build(Data(frozen.Segments.Append(S(15,20,0,SegmentIssue.Gap)).ToArray()),t.AddHours(3),work);
            Check(missing.IsProvisional(t.AddMinutes(11)),"missing sample finalized an unobserved tail");
            var settled=ActiveRateEstimator.Build(Data(frozen.Segments.Append(S(15,20,0)).ToArray()),t.AddMinutes(20),work);
            Check(!settled.IsProvisional(t.AddMinutes(11)),"fresh idle observation failed to close the episode");
        });
        Test("new work without a new quota jump does not resurrect a settled old tail",()=>
        {
            var data=Data(Enumerable.Range(0,12).Select(i=>S(i*5,(i+1)*5,i==0?1:0)).ToArray());
            WorkSpan done=new(t,t.AddMinutes(12),true,true,null);
            var before=ActiveRateEstimator.Build(data,t.AddMinutes(60),[done]);
            var after=ActiveRateEstimator.Build(data,t.AddMinutes(60),[done,new(t.AddMinutes(50),t.AddMinutes(60),true,false,null)]);
            Near(1,after.Delta);Near(1,Area(after.Runs.Where(r=>!r.Provisional)));
            Check(!after.IsProvisional(t.AddMinutes(11))&&after.IsProvisional(t.AddMinutes(55)),"new work reopened a closed historical tail");
            Near(before.ValueAt(t.AddMinutes(11))!.Value,after.ValueAt(t.AddMinutes(11))!.Value);
            Near(0,after.ValueAt(t.AddMinutes(30))!.Value);
        });
        Test("samples overlapping the next work episode cannot establish an idle closure",()=>
        {
            var data=Data(S(0,5,1),S(5,10,0),S(10,15,0),S(15,20,0));
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(20),[new(t,t.AddMinutes(12),true,true,null),new(t.AddMinutes(14),t.AddMinutes(20),true,false,null)]);
            Check(curve.IsProvisional(t.AddMinutes(11)),"observations spanning later work were treated as a full idle sample");
            Near(1,curve.Delta);
        });
        Test("unknown activity uses fixed observation context and no future samples",()=>
        {
            var data=Data(S(0,30,1),S(30,60,1),S(60,90,999));
            var curve=ActiveRateEstimator.Build(data,t.AddHours(1),[new(t.AddMinutes(10),t.AddHours(2),false,false,null)]);
            Near(2,curve.Delta);Check(curve.ValueAt(t.AddMinutes(70)) is null,"used future data");
            Check(curve.Runs.All(r=>r.Points[0].Time>=t&&r.Points[^1].Time<=t.AddHours(1)),"unknown edges depend on viewport");
        });
        Test("constant quantized load stays flat away from unknown phase boundaries",()=>
        {
            foreach(var phase in new[]{0d,.25,.75})
            {
                var data=Data(Enumerable.Range(0,96).Select(i=>S(i*5,(i+1)*5,Math.Floor(phase+(i+1)/3d)-Math.Floor(phase+i/3d))).ToArray());
                var curve=ActiveRateEstimator.Build(data,t.AddHours(8),[new(t,t.AddHours(8),true,false,null)]);
                foreach(var minute in Enumerable.Range(120,240))Near(4,curve.ValueAt(t.AddMinutes(minute))!.Value);
            }
        });
        Test("exact observed work boundaries and positive Fable estimates survive viewport clipping",()=>
        {
            var data=Data(S(0,5,0),S(5,10,1));
            var work=new[]{new WorkSpan(t,t.AddMinutes(10),true,true,null)};
            var curve=ActiveRateEstimator.Build(data,t.AddMinutes(10).AddSeconds(10),work);
            var path=ChartPath.PositiveRuns(curve.Runs.Select(ChartPath.HardEdges)).Single();
            Check(path[0].Time==t&&path[0].Rate==0&&path[1].Time==t,"start at first observation rounded");
            Check(path[^1].Time==t.AddMinutes(10)&&path[^1].Rate==0,"completion at last observation rounded");
            Near(0,curve.ValueAt(t.AddMinutes(10))!.Value);
            var view=new ChartView{Start=t,End=t.AddMinutes(10),Smooth=true,Total=data,Fable=data,Gaps=[],Activity=new(work,work,[])};
            // A view can have no local jump and still contain a legitimate shared estimate.
            var early=new ChartView{Start=t,End=t.AddMinutes(10),Smooth=true,Total=data,Fable=data,Gaps=[],Activity=view.Activity};
            Check(early.FableDrawable,"positive Fable curve was hidden");
            var tailData=Data(S(0,5,1),S(5,10,0),S(10,15,0));
            var tailView=new ChartView{Start=t.AddMinutes(7),End=t.AddMinutes(15),Smooth=true,Total=tailData,Fable=tailData,Gaps=[],Activity=new([new(t,t.AddMinutes(15),true,false,null)],[new(t,t.AddMinutes(15),true,false,null)],[])};
            Check(tailView.FableDrawable&&tailView.FableTrend.IsProvisional(t.AddMinutes(12)),"no-jump range hides tentative Fable estimate");
        });
    }
}
