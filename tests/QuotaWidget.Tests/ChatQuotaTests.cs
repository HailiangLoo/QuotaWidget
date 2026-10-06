using QuotaWidget.Core;

static class ChatQuotaTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        void Test(string name,Action body)=>tests.Add(("chat quota: "+name,()=>{body();return Task.CompletedTask;}));
        void Check(bool b,string why){if(!b)throw new Exception(why);}
        var t=DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        (SeriesData Quota,List<QuotaToken> Tokens) Fixture(bool ambiguous=false)
        {
            var random=new Random(48);var q=new SeriesData{Key=SeriesKey.Total,Segments=[]};var tokens=new List<QuotaToken>();double cumulative=0;
            for(var h=0;h<36;h++)
            {
                double points=0;
                for(var m=0;m<2;m++)
                {
                    var input=ambiguous?1e6:(.1+random.NextDouble())*1e6;
                    var cache=ambiguous?10e6:(.1+random.NextDouble()*12)*1e6;
                    var output=ambiguous?.1e6:(.01+random.NextDouble()*.3)*1e6;
                    var row=new QuotaToken(t.AddHours(h).AddMinutes(15+m*20),t.AddHours(h).AddMinutes(16+m*20),"chat-"+m,"model-"+m,input,cache,output);
                    tokens.Add(row);points+=(input*(m==0?2:1)+cache*(m==0?.1:.03)+output*(m==0?8:4))/1e6;
                }
                var old=Math.Floor(cumulative);cumulative+=points;
                q.Segments.Add(new(){Start=t.AddHours(h),End=t.AddHours(h+1),Delta=Math.Floor(cumulative)-old});
            }
            return(q,tokens);
        }
        Test("learns relative model and cache costs, preserves the observed total",()=>
        {
            var(q,rows)=Fixture();var e=ChatQuotaEstimator.Build(q,rows,t.AddHours(31),t.AddHours(36));
            Check(e.Available,"stable five-hour mixture rejected: "+e.Reason);
            Check(Math.Abs(e.Shares.Sum(x=>x.Points)-e.Observed)<1e-8,"allocation does not reconcile");
            double Truth(QuotaToken r)=> (r.Input*(r.Model=="model-0"?2:1)+r.Cached*(r.Model=="model-0"?.1:.03)+r.Output*(r.Model=="model-0"?8:4))/1e6;
            var recent=rows.Where(r=>r.At>t.AddHours(31)).ToArray();
            foreach(var s in e.Shares)
            {
                var truth=e.Observed*recent.Where(r=>r.Chat==s.Chat).Sum(Truth)/recent.Sum(Truth);
                Check(Math.Abs(s.Points-truth)<.8,"known mixture share inaccurate");
            }
        });
        Test("refuses ambiguous model attribution despite a perfect account fit",()=>
        {
            var(q,rows)=Fixture(true);var e=ChatQuotaEstimator.Build(q,rows,t.AddHours(31),t.AddHours(36));
            Check(!e.Available,"collinear models fabricated per-chat certainty");
        });
        Test("small totals, incomplete import, conflicts and out-of-model account usage remain unknown",()=>
        {
            var(q,rows)=Fixture();
            Check(!ChatQuotaEstimator.Build(q,rows,t,t.AddHours(36),false).Available,"incomplete index accepted");
            var bad=rows.Select(r=>r with{Conflict=true}).ToArray();
            Check(!ChatQuotaEstimator.Build(q,bad,t,t.AddHours(36)).Available,"conflicts allocated");
            var spike=new SeriesData{Key=SeriesKey.Total,Segments=q.Segments.Select((s,i)=>new RateSegment{Start=s.Start,End=s.End,Delta=i%3==0?90:0}).ToList()};
            Check(!ChatQuotaEstimator.Build(spike,rows,t,t.AddHours(36)).Available,"unexplained account increments allocated");
            var tiny=new SeriesData{Key=SeriesKey.Total,Segments=[new(){Start=t,End=t.AddHours(1),Delta=2}]};
            Check(!ChatQuotaEstimator.Build(tiny,rows,t,t.AddHours(1)).Available,"two integer points look precise");
        });
        Test("gap/reset intervals are not billed or crossed by allocation",()=>
        {
            var(q,rows)=Fixture();var s=q.Segments[33];
            q.Segments[33]=new(){Start=s.Start,End=s.End,Delta=99,Issue=SegmentIssue.Reset};
            var e=ChatQuotaEstimator.Build(q,rows,t.AddHours(31),t.AddHours(36));
            Check(e.Available,"known reset prevented valid independent runs: "+e.Reason);
            Check(Math.Abs(e.Observed-q.Segments.Skip(31).Where(s=>s.Valid).Sum(s=>s.Delta))<1e-8,"reset billed");
            Check(Math.Abs(e.Shares.Sum(s=>s.Points)-e.Observed)<1e-8,"gap allocation does not conserve");
        });
        Test("Fable closes independently of Opus and concurrent Fable workers",()=>
        {
            var a=new WorkSpan(t,t.AddHours(1),true,true,"claude-fable-5-1");
            var opus=new WorkSpan(t,t.AddHours(2),true,false,"claude-opus-5-5");
            Check(!WorkActivity.FableRunning([a,opus],t.AddHours(2)),"Opus extends completed Fable");
            Check(!WorkActivity.FableRunning([a,opus with{Model=null}],t.AddHours(2)),"unknown input extends Fable");
            Check(WorkActivity.FableRunning([a,opus with{Model="claude-fable-5-1"}],t.AddHours(2)),"concurrent Fable hidden");
        });
        Test("numeric-only attribution query preserves provider, parent, conflict and time bounds",()=>
        {
            var folder=Path.Combine(Path.GetTempPath(),"qw-attribution-"+Guid.NewGuid());Directory.CreateDirectory(folder);
            try
            {
                using var db=new TokenStore(Path.Combine(folder,"tokens.sqlite"));
                var child=Guid.NewGuid().ToString();var parent=Guid.NewGuid().ToString();
                Check(db.SetChatParent("Codex",child,parent),"fixture parent rejected");
                db.Put(new("Codex","r1",child,"model-a",t.AddMinutes(5),10,20,30));
                db.Put(new("Claude","r2","child","model-a",t.AddMinutes(5),99,99,99));
                db.Put(new("Codex","r3","elsewhere","model-b",t.AddHours(2),99,99,99));
                var r=db.QuotaTokens(t,t.AddHours(1),"Codex")!.Single();
                Check(r.Chat==parent&&r.Input==10&&r.Output==30&&!r.Conflict,"query lost parent or scope");
                db.Put(new("Codex","r1",child,"model-a",t.AddMinutes(6),11,20,40));
                Check(db.QuotaTokens(t,t.AddHours(1),"Codex")!.Single().Conflict,"conflicting correction hidden");
            }
            finally{Directory.Delete(folder,true);}
        });
        Test("discovery cap accepts completed old logs but refuses relevant omitted growth",()=>
        {
            var folder=Path.Combine(Path.GetTempPath(),"qw-quota-coverage-"+Guid.NewGuid());Directory.CreateDirectory(folder);
            try
            {
                var now=t.AddDays(5);var logs=Path.Combine(folder,"claude","projects");Directory.CreateDirectory(logs);
                var old=Path.Combine(logs,"old.jsonl");File.WriteAllText(old,"{}\n");File.SetLastWriteTimeUtc(old,now.AddHours(-3).UtcDateTime);
                using var index=new TokenIndex(Path.Combine(folder,"db"),Path.Combine(folder,"codex"),Path.Combine(folder,"claude"),now);
                index.Poll(now,claude:true,codex:false);
                for(var i=0;i<256;i++){var p=Path.Combine(logs,"new-"+i+".jsonl");File.WriteAllText(p,"{}\n");File.SetLastWriteTimeUtc(p,now.UtcDateTime);}
                void Poll(DateTimeOffset at){for(var i=0;i<100;i++){index.Poll(at,claude:true,codex:false);if(index.PendingFiles==0)return;}throw new Exception("fixture did not settle");}
                Poll(now.AddMinutes(2));
                Check(index.LimitedDiscovery&&index.QuotaTokens(now.AddDays(-1),now,"Claude") is not null,"fully imported omitted log disabled estimates");
                File.AppendAllText(old,"{}\n");File.SetLastWriteTimeUtc(old,now.AddHours(-2).UtcDateTime);
                Poll(now.AddMinutes(4));
                Check(index.QuotaTokens(now.AddDays(-1),now,"Claude") is null,"omitted unindexed growth presented as complete");
                Check(index.QuotaTokens(now.AddHours(-1),now,"Claude") is not null,"irrelevant older log blocks recent scope");
            }
            finally{Directory.Delete(folder,true);}
        });
    }
}
