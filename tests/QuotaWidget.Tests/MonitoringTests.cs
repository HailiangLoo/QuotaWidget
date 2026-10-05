using System.Text.Json;
using QuotaWidget.Core;

static class MonitoringTests
{
    sealed class CodexSource(Func<LatestEnvelope> result):ICodexUsageSource
    {
        public int Calls;
        public Task<LatestEnvelope> FetchAsync(CancellationToken ct){Calls++;return Task.FromResult(result());}
    }
    sealed class ClaudeSource:IUsageSource
    {
        public int Calls;
        public Task<UsageFetch> FetchAsync(string dir,CancellationToken ct){Calls++;throw new Exception("Disabled source called");}
    }
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        void Check(bool ok,string why){if(!ok)throw new Exception(why);}
        void Test(string name,Func<string,Task> body)=>tests.Add(("monitoring: "+name,async()=>
        {
            var dir=Path.Combine(Path.GetTempPath(),"qw-monitor-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            try{await body(dir);}finally{Directory.Delete(dir,true);}
        }));
        var now=DateTimeOffset.Parse("2026-10-03T10:00:00Z");
        Test("old/invalid preferences default to both; explicit mode survives restart",dir=>
        {
            var file=Path.Combine(dir,"settings.json");File.WriteAllText(file,"{}");
            var settings=WidgetSettings.Load(file,out _);Check(settings.Monitors(ChatPlatform.Claude)&&settings.Monitors(ChatPlatform.Codex),"old settings lost default");
            settings.Monitoring="claude";settings.Save(file);settings=WidgetSettings.Load(file,out _);
            Check(settings.Monitors(ChatPlatform.Claude)&&!settings.Monitors(ChatPlatform.Codex),"selection not persisted");
            settings.Monitoring="invalid";settings.Normalize();Check(settings.Monitoring=="both","invalid selection disables all");return Task.CompletedTask;
        });
        Test("Claude disabled manual and timer paths make no attempts or files",async dir=>
        {
            var paths=new DataPaths(dir);var settings=new WidgetSettings{Monitoring="codex"};var source=new ClaudeSource();
            using var collector=new ClaudeUsageCollector(paths,()=>settings,source,()=>now);
            Check(await collector.CollectOnceAsync(default) is null,"disabled manual call accepted");
            using(var cancel=new CancellationTokenSource(40))await collector.RunAsync(cancel.Token);
            Check(source.Calls==0&&!File.Exists(paths.Latest),"disabled collector read source or changed latest");
            settings.Monitoring="both";
            Check((await collector.CollectOnceAsync(default))?.Status==Statuses.AuthRequired,"reenable did not restore normal collection");
        });
        Test("Codex disabled stops requests; reenable retains server backoff",async dir=>
        {
            var clock=now;var settings=new WidgetSettings{Monitoring="claude"};var source=new CodexSource(()=>CodexUsageSource.Failure(clock,"limited",Statuses.RateLimited) with{RetryAfterSeconds=900});var paths=new DataPaths(dir);
            using var collector=new CodexUsageCollector(paths,()=>settings,source,()=>clock);
            Check(await collector.CollectOnceAsync(default) is null&&source.Calls==0&&!File.Exists(paths.Latest),"disabled source called");
            settings.Monitoring="both";await collector.CollectOnceAsync(default);var deadline=collector.NotBefore;
            settings.Monitoring="claude";clock=now.AddMinutes(1);await collector.CollectOnceAsync(default);
            settings.Monitoring="codex";await collector.CollectOnceAsync(default);
            Check(source.Calls==1&&collector.NotBefore==deadline,"toggle bypassed backoff");
            clock=now.AddMinutes(16);await collector.CollectOnceAsync(default);Check(source.Calls==2,"reenable never resumed");
        });
        Test("token and chat readers switch immediately without reading excluded logs or losing totals",dir=>
        {
            const string cxId="11111111-1111-1111-1111-111111111111",clId="22222222-2222-2222-2222-222222222222";
            var cx=Path.Combine(dir,"codex");var cl=Path.Combine(dir,"claude");Directory.CreateDirectory(Path.Combine(cx,"sessions"));Directory.CreateDirectory(Path.Combine(cl,"projects"));
            string Row(string type,object payload)=>JsonSerializer.Serialize(new{type,timestamp=now,payload});
            string Claude(string id,int amount)=>JsonSerializer.Serialize(new{type="assistant",timestamp=now,sessionId=clId,requestId=id,message=new{id,model="Fable",stop_reason="end_turn",usage=new{input_tokens=amount,cache_read_input_tokens=80,cache_creation_input_tokens=0,output_tokens=3}}});
            var cxFile=Path.Combine(cx,"sessions",cxId+".jsonl");var clFile=Path.Combine(cl,"projects",clId+".jsonl");
            File.WriteAllText(cxFile,Row("session_meta",new{id=cxId})+"\n"+Row("event_msg",new{type="task_started"})+"\n"+Row("token_usage_record",new{response_id="cx",thread_id=cxId,usage=new{input_tokens=100,cached_input_tokens=80,output_tokens=9}})+"\n");
            File.WriteAllText(clFile,Claude("c1",10)+"\n");File.WriteAllText(Path.Combine(cx,"state_5.sqlite"),"disabled database must not be queried");
            using var tokens=new TokenIndex(Path.Combine(dir,"tokens"),cx,cl,now);var chats=new ChatCacheMonitor(cx,cl);
            using(var locked=new FileStream(cxFile,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                tokens.Poll(now,claude:true,codex:false);var rows=chats.Poll(now,true,false);
                Check(tokens.Sum(now.AddDays(-1),now,"Claude").Input==10&&tokens.Sum(now.AddDays(-1),now,"Codex").Requests==0&&tokens.Skipped==0&&!tokens.Coverage.Contains("归属暂不可读"),"disabled Codex log or metadata read");
                Check(rows.Count==1&&rows[0].Platform==ChatPlatform.Claude,"disabled chats retained");
            }
            File.AppendAllText(clFile,Claude("c2",30)+"\n");
            using(var locked=new FileStream(clFile,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                tokens.Poll(now.AddSeconds(1),claude:false,codex:true);var rows=chats.Poll(now.AddSeconds(1),false,true);
                Check(tokens.Sum(now.AddDays(-1),now,"Codex").Input==20&&tokens.Sum(now.AddDays(-1),now,"Claude").Input==10&&tokens.Skipped==0,"mode change delayed or read excluded Claude");
                Check(rows.Count==1&&rows[0].Platform==ChatPlatform.Codex,"chat mode switch failed");
            }
            tokens.Poll(now.AddSeconds(2));Check(tokens.Sum(now.AddDays(-1),now,"Claude").Input==40&&tokens.Sum(now.AddDays(-1),now).Requests==3,"resume lost or duplicated existing counters");return Task.CompletedTask;
        });
        Test("pause/resume events survive disk reload independently of sample continuity",dir=>
        {
            var paths=new DataPaths(dir);var events=new EventLog(paths);
            events.Append(new(now.AddSeconds(15),EventTypes.MonitorPause));events.Append(new(now.AddSeconds(30),EventTypes.MonitorResume));
            var loaded=events.Load(now);Check(RateEngine.ClassifyEvents(loaded,now,now.AddMinutes(1))=="已暂停","raw switch event lost");
            Check(RateEngine.ClassifyEvents(loaded,now.AddMinutes(1),now.AddMinutes(2)) is null,"pause leaked into resumed interval");return Task.CompletedTask;
        });
    }
}
