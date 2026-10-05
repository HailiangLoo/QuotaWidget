using System.Text.Json;
using QuotaWidget.Core;

static class WorkActivityTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        var t=DateTimeOffset.Parse("2026-10-03T12:00:00+08:00");
        void Check(bool value,string message){if(!value)throw new Exception(message);}
        void Test(string name,Action action)=>tests.Add(("work activity: "+name,()=>{action();return Task.CompletedTask;}));
        WorkEvent E(double m,string kind,string? id="turn",string? model=null,string stream="one")=>new(stream,t.AddMinutes(m),kind,id,model);
        double Area(RateTrend r)=>r.Runs.Select(ChartPath.HardEdges).Sum(p=>p.Zip(p.Skip(1),(a,b)=>(a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours).Sum());
        Test("native turn handoffs form one episode without rewriting the exact turn spans",()=>
        {
            var raw=WorkActivity.Build([E(0,"start","a"),E(20,"end","a"),
                E(20+.052/60,"start","b"),E(40,"end","b"),
                E(40+5.193/60,"start","c",stream:"child"),E(60,"end","c",stream:"child")],t.AddHours(2));
            Check(raw.Count==3,"raw turns were merged during parsing");
            var phase=WorkActivity.Episodes(raw,t.AddHours(2)).Single();
            Check(phase.Start==t&&phase.End==t.AddHours(1)&&phase.KnownStart&&phase.KnownEnd,"handoff changed outer boundaries");
            Check(raw[0].End==t.AddMinutes(20)&&raw[1].Start==t.AddMinutes(20).AddMilliseconds(52),"episode derivation mutated raw metadata");
        });
        Test("handoff debounce confirms original completion after ten seconds and never follows polling cadence",()=>
        {
            var exact=new[]{new WorkSpan(t,t.AddMinutes(5),true,true,null)};
            var pending=WorkActivity.Episodes(exact,t.AddMinutes(5).AddSeconds(5)).Single();
            Check(!pending.KnownEnd&&pending.End==exact[0].End,"completion published before queued handoff settled");
            var confirmed=WorkActivity.Episodes(exact,t.AddMinutes(5).AddSeconds(10)).Single();
            Check(confirmed.KnownEnd&&confirmed.End==exact[0].End,"end drifted to confirmation time");
            foreach(var gap in new[]{10.001,60,300,3600})
            {
                var next=new WorkSpan(exact[0].End.AddSeconds(gap),t.AddHours(2),true,true,null);
                Check(WorkActivity.Episodes(exact.Append(next),t.AddHours(3)).Count==2,"merged real pause: "+gap);
            }
        });
        Test("provider and model episodes have independent exact starts and explicit ends",()=>
        {
            var rows=WorkActivity.Build([E(0,"input"),E(1,"activity","r1","claude-fable-5-1"),E(10,"finish","r1","claude-fable-5-1"),
                E(30,"input"),E(31,"activity","r2","claude-opus-5-5"),E(45,"finish","r2","claude-opus-5-5")],t.AddHours(2));
            Check(rows.Count==2&&rows.All(s=>s.KnownStart&&s.KnownEnd),"lost explicit start/end");
            Check(rows[0].Start==t&&rows[0].End==t.AddMinutes(10),"rounded work edges");
            Check(WorkActivity.Merge(rows.Where(s=>FableDisplay.IsFable(s.Model)),TimeSpan.FromMinutes(5)).Single().End==t.AddMinutes(10),"Opus extends Fable");
        });
        Test("concurrent chats and subagents remain active until the last worker finishes",()=>
        {
            var spans=WorkActivity.Build([E(0,"start","a",stream:"parent"),E(10,"end","a",stream:"parent"),
                E(5,"start","b",stream:"child"),E(20,"end","b",stream:"child")],t.AddHours(1));
            var merged=WorkActivity.Merge(spans,TimeSpan.FromMinutes(5)).Single();
            Check(merged.Start==t&&merged.End==t.AddMinutes(20)&&merged.KnownEnd,"parent finish cut off child");
            var active=WorkActivity.Build([E(0,"start","a"),E(30,"end","old")],t.AddHours(1)).Single();
            Check(!active.KnownEnd&&active.End==t.AddHours(1),"late completion stopped current turn");
        });
        Test("duplicate streamed final messages cannot reopen a completed Claude request",()=>
        {
            var rows=WorkActivity.Build([E(0,"input"),E(1,"activity","r","fable"),E(10,"finish","r","fable"),
                E(11,"activity","r","fable"),E(11,"finish","r","fable")],t.AddHours(1));
            Check(rows.Count==1&&rows[0].End==t.AddMinutes(10)&&rows[0].KnownEnd,"duplicate reopened work");
            var unknown=WorkActivity.Build([E(1,"activity","x","fable"),E(2,"input")],t.AddHours(1)).Single();
            Check(!unknown.KnownStart&&!unknown.KnownEnd,"missing boundary invented from a tool result");
        });
        Test("closed work clips both smoothing shoulders while preserving complete observed amounts",()=>
        {
            var data=new SeriesData{Key=SeriesKey.Total,Segments=Enumerable.Range(0,36).Select(i=>new RateSegment{Start=t.AddMinutes(i*5),End=t.AddMinutes((i+1)*5),Delta=i is 10 or 18?1:0}).ToList()};
            var spans=new[]{new WorkSpan(t.AddMinutes(43.408),t.AddMinutes(94.321),true,true,"fable")};
            var curve=RateTrend.Build(data,t,t.AddHours(3),activity:spans);
            Check(curve.ValueAt(t.AddMinutes(43))==0&&curve.ValueAt(t.AddMinutes(95))==0,"start/end leaked");
            Check(Math.Abs(Area(curve)-2)<1e-7,"clipping lost consumption");
            var paths=ChartPath.PositiveRuns(curve.Runs.Select(ChartPath.HardEdges));
            Check(paths.First()[0].Time==spans[0].Start&&paths.Last()[^1].Time==spans[0].End,"rendered edges differ from recorded work");
            var raw=RateEngine.SumRange(data,t,t.AddHours(3));Check(raw.Delta==2&&data.Segments.Count==36,"raw history changed");
        });
        Test("real quota outside local activity, gaps, and range partials remain accounted for",()=>
        {
            var source=new SeriesData{Key=SeriesKey.Total,Segments=[new(){Start=t,End=t.AddMinutes(5),Delta=1},new(){Start=t.AddMinutes(5),End=t.AddMinutes(10),Issue=SegmentIssue.Gap},new(){Start=t.AddMinutes(10),End=t.AddMinutes(15),Delta=2}]};
            var spans=new[]{new WorkSpan(t.AddMinutes(1),t.AddMinutes(4),true,true,null)};
            var curve=RateTrend.Build(source,t,t.AddMinutes(15),activity:spans);
            Check(Math.Abs(Area(curve)-3)<1e-7&&curve.ValueAt(t.AddMinutes(7)) is null,"erased unexplained usage or real gap");
            var partial=RateTrend.Build(source,t.AddMinutes(2),t.AddMinutes(14),activity:spans);
            Check(Area(partial)==0,"guessed a range-cut observation");
        });
        Test("Fable display filtering preserves vertical edges of visible Claude fragments",()=>
        {
            IReadOnlyList<TrendPoint> path=[new(t,0),new(t,2),new(t.AddMinutes(10),2),new(t.AddMinutes(10),0)];
            var kept=FableDisplay.Omit([path],[new(t.AddMinutes(30),t.AddMinutes(40))]).Single();
            Check(kept[0].Time==kept[1].Time&&kept[0].Rate==0&&kept[^1].Time==kept[^2].Time&&kept[^1].Rate==0,"Fable masking stripped vertical Claude edges");
        });
        Test("old numeric index gains activity metadata idempotently without losing token counts",()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"qw-work-migrate-"+Guid.NewGuid().ToString("N"));
            try
            {
                var logs=Path.Combine(root,"claude","projects","project");Directory.CreateDirectory(logs);
                var dbRoot=Path.Combine(root,"db");Directory.CreateDirectory(dbRoot);
                var path=Path.Combine(logs,"chat.jsonl");
                var rows=new[]{JsonSerializer.Serialize(new{type="user",timestamp=t,uuid="u",message=new{content="start"}}),
                    JsonSerializer.Serialize(new{type="assistant",timestamp=t.AddMinutes(10),sessionId="chat",requestId="r",message=new{model="claude-fable-5-1",stop_reason="end_turn",usage=new{input_tokens=2,cache_read_input_tokens=30,cache_creation_input_tokens=3,output_tokens=4}}})};
                File.WriteAllLines(path,rows);
                using(var old=new TokenStore(Path.Combine(dbRoot,"tokens.sqlite")))
                {
                    old.SetMeta("since",t.AddHours(-1).ToString("O"));
                    var cursor=new TokenCursor{Platform="Claude",Chat="chat",Offset=new FileInfo(path).Length};
                    TokenParser.Read(rows[1],cursor,old,t.AddHours(-1),t.AddHours(1));old.SaveSource(path,cursor);
                }
                for(var pass=0;pass<2;pass++)
                using(var index=new TokenIndex(dbRoot,Path.Combine(root,"codex"),Path.Combine(root,"claude"),t.AddHours(1)))
                {
                    index.Poll(t.AddHours(1));var sum=index.Sum(t.AddHours(-1),t.AddHours(1),"Claude");
                    Check(sum.Requests==1&&sum.Input==5&&sum.Cached==30&&sum.Output==4,"migration changed token totals");
                    var span=index.WorkActivitySpans(t.AddHours(1),"Claude").Single();
                    Check(span.KnownStart&&span.KnownEnd&&span.Start==t&&span.End==t.AddMinutes(10),"backfill or restart lost work boundaries");
                }
            }
            finally{if(Directory.Exists(root))Directory.Delete(root,true);}
        });
        Test("metadata parser persists only explicit activity and ignores local transcript commands",()=>
        {
            var path=Path.Combine(Path.GetTempPath(),"qw-work-"+Guid.NewGuid().ToString("N")+".sqlite");
            try
            {
                using var db=new TokenStore(path);var c=new TokenCursor{Platform="Claude",Chat="chat",ActivityStream="agent-a"};
                bool Read(object row)=>WorkActivity.Read(JsonSerializer.Serialize(row),c,db,t.AddDays(-1),t.AddHours(1));
                Check(!Read(new{type="user",timestamp=t,message=new{content="<local-command-stdout>done</local-command-stdout>"}}),"local command starts work");
                Read(new{type="user",timestamp=t,uuid="u",message=new{content="start"}});
                var final=new{type="assistant",timestamp=t.AddMinutes(10),requestId="r",message=new{id="m",model="claude-fable-5-1",stop_reason="end_turn"}};
                Check(Read(final)&&!Read(final),"metadata replay not idempotent");
                var span=db.WorkActivitySpans(t.AddHours(1),"Claude").Single();
                Check(span.KnownStart&&span.KnownEnd&&span.Start==t&&span.End==t.AddMinutes(10),"parser lost completion");
                Check(db.WorkActivitySpans(t.AddHours(1),"Codex").Count==0,"cross-provider events");
            }
            finally{File.Delete(path);File.Delete(path+"-wal");File.Delete(path+"-shm");}
        });
    }
}
