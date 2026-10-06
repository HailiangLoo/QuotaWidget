using System.Text.Json;
using QuotaWidget.Core;

static class ActivityPublicationTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        var now=new DateTimeOffset(2026,10,5,6,0,0,TimeSpan.Zero);
        void Check(bool yes,string message){if(!yes)throw new Exception(message);}
        string Event(DateTimeOffset at,string kind)=>JsonSerializer.Serialize(new{timestamp=at,type="event_msg",payload=new{type=kind,turn_id="turn"}})+"\n";
        string Padding()=>string.Concat(Enumerable.Repeat("{\"type\":\"ordinary\",\"body\":\""+new string('x',10000)+"\"}\n",800));
        void Test(string name,Action<string> body)=>tests.Add(("activity publication: "+name,()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"qw-publish-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{body(root);}finally{Directory.Delete(root,true);}return Task.CompletedTask;
        }));
        Test("backfill retains complete lifecycle and reset continuity until atomic replacement",root=>
        {
            var logs=Path.Combine(root,"cx","sessions");Directory.CreateDirectory(logs);
            var path=Path.Combine(logs,"rollout-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl");
            File.WriteAllText(path,Event(now.AddHours(-3),"task_started"));
            using var index=new TokenIndex(Path.Combine(root,"idx"),Path.Combine(root,"cx"),Path.Combine(root,"cl"),now);
            index.Poll(now);
            var original=index.WorkActivitySpans(now,"Codex").Single();
            Check(index.WorkActivityReady("Codex")&&!original.KnownEnd,"initial open work not published");
            var source=new SeriesData{Key=SeriesKey.Total,Segments=[]};
            for(var i=0;i<36;i++)source.Segments.Add(new(){Start=now.AddMinutes(-180+i*5),End=now.AddMinutes(-175+i*5),
                Delta=i==20?-100:i%3==0?1:0,Group=i<20?0:1,Issue=i==20?SegmentIssue.Reset:SegmentIssue.None,CounterResetOnly=i==20,StartsAtCapacity=i==20});
            var before=ActiveRateEstimator.Build(source,now,[original]);
            File.AppendAllText(path,Padding()+Event(now.AddMinutes(-10),"task_complete"));
            index.Poll(now,claude:false,codex:true);
            Check(index.PendingFiles>0,"fixture did not leave a partial import");
            var during=index.WorkActivitySpans(now,"Codex");
            Check(during.Count==1&&during[0]==original,"partial tail erased history or published a premature end");
            var transient=ActiveRateEstimator.Build(source,now,during);
            foreach(var at in Enumerable.Range(0,36).Select(i=>now.AddMinutes(-180+i*5)))
                Check(before.ValueAt(at)==transient.ValueAt(at),"monitor switch changed curve with identical published evidence");
            for(var i=0;i<30&&index.PendingFiles>0;i++)index.Poll(now,claude:i%2==0,codex:true);
            var completed=index.WorkActivitySpans(now,"Codex").Single();
            Check(index.PendingFiles==0&&completed.KnownEnd&&completed.End==now.AddMinutes(-10),"completed generation never replaced stable history");
            Check(index.WorkActivitySpans(now.AddHours(-1),"Codex").Single().KnownEnd==false,"snapshot exposed a future completion during replay");
        });
        Test("first import is pending rather than an empty ready history",root=>
        {
            var logs=Path.Combine(root,"cx","sessions");Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs,"rollout-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl"),Event(now.AddHours(-2),"task_started")+Padding());
            using var index=new TokenIndex(Path.Combine(root,"idx"),Path.Combine(root,"cx"),Path.Combine(root,"cl"),now);
            index.Poll(now);
            Check(index.PendingFiles>0&&!index.WorkActivityReady("Codex"),"partial initial import presented as ready");
            for(var i=0;i<30&&index.PendingFiles>0;i++)index.Poll(now);
            Check(index.WorkActivityReady("Codex")&&index.WorkActivitySpans(now,"Codex").Count==1,"initial publication lost activity");
        });
        Test("long rows during tail growth cannot strand completion behind two stalled cursors",root=>
        {
            var logs=Path.Combine(root,"cx","sessions");Directory.CreateDirectory(logs);
            var path=Path.Combine(logs,"rollout-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl");
            var idx=Path.Combine(root,"idx");
            string LongRow(int bytes)=>"{\"type\":\"response_item\",\"body\":\""+new string('x',bytes)+"\"}\n";
            var completedAt=now.AddHours(-11);
            File.WriteAllText(path,Event(now.AddHours(-12),"task_started"));
            using(var index=new TokenIndex(idx,Path.Combine(root,"cx"),Path.Combine(root,"cl"),now))
            {
                index.Poll(now);
                Check(!index.WorkActivitySpans(now,"Codex").Single().KnownEnd,"fixture needs a published active turn");
                // The hot-tail scan uses its entire first budget while backfill reaches
                // an oversized row. A subsequent append starts another oversized row
                // exactly at the tail cursor: both used to reread 1 MiB forever.
                File.AppendAllText(path,LongRow(1400000)+Padding());
                index.Poll(now);
                Check(index.PendingFiles>0,"fixture needs unfinished backfill");
                File.AppendAllText(path,LongRow(1800000)+JsonSerializer.Serialize(new{timestamp=completedAt,
                    type="token_usage_record",payload=new{response_id="last-response",thread_id="aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                        turn_id="turn",usage=new{input_tokens=100,cached_input_tokens=80,output_tokens=12}}})+"\n"+Event(completedAt,"task_complete"));
                for(var i=0;i<4;i++)index.Poll(now);
            }
            // Resume the same saved cursors, including a skip that can span batches.
            using var resumed=new TokenIndex(idx,Path.Combine(root,"cx"),Path.Combine(root,"cl"),now);
            for(var i=0;i<40;i++)
            {
                resumed.Poll(now);
                Check(resumed.LastReadBytes<=8*1024*1024,"completion recovery exceeded the poll byte budget");
                if(resumed.PendingFiles==0)break;
            }
            Check(resumed.PendingFiles==0,"oversized log rows left backfill permanently pending");
            var completed=resumed.WorkActivitySpans(now,"Codex").Single();
            Check(completed.KnownEnd&&completed.End==completedAt,"task_complete never reached the published lifecycle");
            Check(resumed.Sum(now.AddDays(-1),now,"Codex").Requests==1,"hot-tail/backfill replay duplicated or lost usage");
            var source=new SeriesData{Key=SeriesKey.Total,Segments=Enumerable.Range(0,144).Select(i=>new RateSegment
                {Start=now.AddMinutes(-720+5*i),End=now.AddMinutes(-715+5*i),Delta=i==0?1:0}).ToList()};
            var trend=ActiveRateEstimator.Build(source,now,[completed]);
            Check(trend.ValueAt(completedAt.AddMinutes(1))==0&&!trend.Runs.Any(r=>r.Provisional),"completed task still has an estimated idle tail");
            var paths=ChartPath.PositiveRuns(trend.Runs.Select(ChartPath.HardEdges));
            Check(paths.Count>0&&paths.All(p=>p[^1].Time<=completedAt)&&
                trend.Runs.Any(r=>r.HardEnd&&r.Points[^1].Time==completedAt),"chart continued beyond the real completion");
        });
        Test("restart restores a completed generation and monitoring choices never erase it",root=>
        {
            var logs=Path.Combine(root,"cx","sessions");Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs,"rollout-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl"),Event(now.AddHours(-2),"task_started"));
            using(var index=new TokenIndex(Path.Combine(root,"idx"),Path.Combine(root,"cx"),Path.Combine(root,"cl"),now))index.Poll(now);
            using var restored=new TokenIndex(Path.Combine(root,"idx"),Path.Combine(root,"cx"),Path.Combine(root,"cl"),now);
            Check(restored.WorkActivityReady("Codex")&&restored.WorkActivitySpans(now,"Codex").Count==1,"restart discarded completed metadata");
            restored.Poll(now,claude:true,codex:false);
            Check(restored.WorkActivitySpans(now,"Codex").Count==1,"disabled provider lost its published history");
            restored.Poll(now,claude:true,codex:true);
            Check(restored.WorkActivitySpans(now,"Codex").Single().Start==now.AddHours(-2),"re-enabled provider starts at mode switch");
        });
        Test("pending provider does not draw fallback curves or affect ready provider",root=>
        {
            var series=new SeriesData{Key=SeriesKey.Total,Segments=[new(){Start=now.AddHours(-1),End=now,Delta=2}]};
            var chart=new ChartView{Start=now.AddHours(-1),End=now,Smooth=true,Total=series,Fable=series,Codex=series,Gaps=[],
                Activity=new([],[],[],false,true)};
            Check(chart.TotalTrend.Runs.Count==0&&chart.FableTrend.Runs.Count==0&&chart.CodexTrend!.Delta==2,"pending history produced a contradictory fallback or blocked other provider");
        });
    }
}
