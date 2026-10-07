using QuotaWidget.Core;

static class ConnectionTests
{
    sealed class Source(Func<Task<LatestEnvelope>> fetch):ICodexUsageSource
    {public int Calls;public Task<LatestEnvelope> FetchAsync(CancellationToken ct){Calls++;return fetch();}}
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        void Check(bool yes,string why){if(!yes)throw new Exception(why);}
        void Test(string name,Func<string,Task> run)=>tests.Add(("connections: "+name,async()=>
        {
            var dir=Path.Combine(Path.GetTempPath(),"qw-connection-"+Guid.NewGuid());Directory.CreateDirectory(dir);
            try{await run(dir);}finally{Directory.Delete(dir,true);}
        }));
        var now=DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        Test("legacy settings stay connected; setup and provider opt-outs persist",dir=>
        {
            var path=Path.Combine(dir,"settings.json");File.WriteAllText(path,"{\"monitoring\":\"codex\"}");
            var s=WidgetSettings.Load(path,out _);Check(s.SetupCompleted&&s.Collects(ChatPlatform.Codex)&&!s.Listens(ChatPlatform.Claude),"legacy choices changed");
            s.SetupCompleted=false;s.Save(path);s=WidgetSettings.Load(path,out _);
            Check(!s.Listens(ChatPlatform.Codex)&&!s.Listens(ChatPlatform.Claude),"setup started local readers");
            s.SetupCompleted=true;s.CodexConnected=false;s.Save(path);
            Check(!WidgetSettings.Load(path,out _).Listens(ChatPlatform.Codex),"disconnect not persistent");return Task.CompletedTask;
        });
        Test("setup blocks calls; disconnect discards an in-flight result without rewriting history",async dir=>
        {
            var settings=new WidgetSettings{SetupCompleted=false};var paths=new DataPaths(dir);
            var result=new TaskCompletionSource<LatestEnvelope>();var source=new Source(()=>result.Task);
            using var collector=new CodexUsageCollector(paths,()=>settings,source,()=>now);
            Check(await collector.CollectOnceAsync(default) is null&&source.Calls==0,"setup queried account");
            settings.SetupCompleted=true;var pending=collector.CollectOnceAsync(default);
            Check(source.Calls==1,"enabled connection never queried");settings.CodexConnected=false;
            result.SetResult(CodexUsageSource.Failure(now,"not_logged_in",Statuses.AuthRequired));
            Check(await pending is null&&!File.Exists(paths.Latest),"disconnected result entered latest/history");
            Check(await collector.CollectOnceAsync(default) is null&&source.Calls==1,"disconnected account queried again");
        });
        Test("display changes preserve explicit connections, live results and independent opt-outs",async dir=>
        {
            var settings=new WidgetSettings();var file=Path.Combine(dir,"settings.json");
            foreach(var mode in new[]{"claude","codex","both"})
            {
                settings.Monitoring=mode;settings.Save(file);settings=WidgetSettings.Load(file,out _);
                Check(settings.Collects(ChatPlatform.Claude)&&settings.Collects(ChatPlatform.Codex),"display change paused or lost a connection after restart");
            }
            settings.Monitoring="codex";settings.ClaudeConnected=false;settings.Save(file);settings=WidgetSettings.Load(file,out _);
            settings.Monitoring="both";
            Check(!settings.Listens(ChatPlatform.Claude)&&settings.Listens(ChatPlatform.Codex),"showing both reconnected an explicitly disconnected account");
            var result=new TaskCompletionSource<LatestEnvelope>();var source=new Source(()=>result.Task);var paths=new DataPaths(Path.Combine(dir,"pending"));
            using var collector=new CodexUsageCollector(paths,()=>settings,source,()=>now);
            var pending=collector.CollectOnceAsync(default);settings.Monitoring="claude";
            result.SetResult(CodexUsageSource.Failure(now,"not_logged_in",Statuses.AuthRequired));
            Check(await pending is not null&&source.Calls==1&&File.Exists(paths.Latest),"hiding a connected provider discarded its pending reading");
        });
        Test("connection check can recover sign-in but preserves network and server retry floors",async dir=>
        {
            foreach(var code in new[]{"not_logged_in","cli_missing_or_untrusted","network","limited"})
            {
                var clock=now;var settings=new WidgetSettings();
                var source=new Source(()=>Task.FromResult(CodexUsageSource.Failure(clock,code,code=="not_logged_in"?Statuses.AuthRequired:code=="limited"?Statuses.RateLimited:Statuses.Error) with{RetryAfterSeconds=code=="limited"?900:null}));
                using var collector=new CodexUsageCollector(new DataPaths(Path.Combine(dir,code)),()=>settings,source,()=>clock);
                await collector.CollectOnceAsync(default);var deadline=collector.NotBefore;clock=now.AddSeconds(61);
                await collector.RecheckConnectionAsync(default);
                Check(source.Calls==(code is "not_logged_in" or "cli_missing_or_untrusted"?2:1),"unsafe retry or failed local recovery: "+code);
                if(code is "network" or "limited")Check(collector.NotBefore==deadline,"backoff was shortened");
            }
        });
        Test("status needs a fresh successful reading and does not equate installation with authentication",dir=>
        {
            var s=new WidgetSettings();var platform=ChatPlatform.Codex;
            Check(Connections.Describe(platform,s,null,true,now).Text==Loc.T("等待验证"),"installed means connected");
            var env=new LatestEnvelope(1,CodexUsageSource.SourceId,"fixture","Codex Plus",now,Statuses.Partial,300,null,null,
                new("fixture",now,new(null,UsageParser.Limit(12,now.AddDays(7)),null)));
            Check(Connections.Describe(platform,s,env,true,now).Text==Loc.T("已连接"),"valid reading not connected");
            s.Monitoring="claude";
            Check(Connections.Describe(platform,s,env,true,now).Text==Loc.T("已连接"),"hiding Codex replaced its real connection status");
            Check(Connections.Describe(platform,s,env,true,now.AddHours(2)).Text==Loc.T("数据陈旧"),"old data claimed connected");
            s.CodexConnected=false;Check(Connections.Describe(platform,s,env,true,now).Text==Loc.T("已断开"),"old success overrode disconnect");return Task.CompletedTask;
        });
    }
}
