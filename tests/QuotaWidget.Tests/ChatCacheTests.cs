using System.Diagnostics;
using System.Text;
using System.Text.Json;
using QuotaWidget.Core;

static class ChatCacheTests
{
    static readonly DateTimeOffset T = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    static string Line(double min, string type, object? payload = null) => JsonSerializer.Serialize(new { timestamp = T.AddMinutes(min), type, payload });
    static string Usage(double min, string id) => Line(min, "token_usage_record", new { response_id = id, usage = new { input_tokens = 1000, cached_input_tokens = 900 } });
    static string Claude(double min, string id, int hour = 0, int five = 0, int read = 10, string model = "claude", string stop = "end_turn") => JsonSerializer.Serialize(new
    {
        timestamp = T.AddMinutes(min), type = "assistant", requestId = id,
        message = new { id, model, stop_reason = stop, usage = new { input_tokens = 2, cache_creation_input_tokens = hour + five, cache_read_input_tokens = read,
            cache_creation = new { ephemeral_1h_input_tokens = hour, ephemeral_5m_input_tokens = five } } }
    });
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static string Temp() { var p = Path.Combine(Path.GetTempPath(), "qw-cache-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }

    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        void Test(string name, Action body) => tests.Add((name, () => { body(); return Task.CompletedTask; }));
        Test("cache: request start, duplicate outputs, title changes, completion do not renew", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Codex, "abc");
            s.Read(Line(0, "event_msg", new { type = "task_started" }));
            s.Read(Usage(2, "r1"));
            Check(s.RequestAt == T, "generation time belongs to age");
            s.Read(Usage(10, "r1")); s.SetTitle("new title");
            s.Read(Line(11, "event_msg", new { type = "task_complete" }));
            Check(s.RequestAt == T && !s.View(T.AddMinutes(11))!.Running, "metadata/duplicate renewed timer");
            s.Read(Line(12, "response_item", new { type = "function_call_output" })); s.Read(Usage(13, "r2"));
            Check(s.RequestAt == T.AddMinutes(12), "next model request should renew");
        });
        Test("cache: compact clears old segment and rejects late/duplicate pre-compact usage", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Codex, "abc"); s.Read(Usage(1, "r1"));
            s.Read(Line(2, "compacted")); s.Read(Usage(1, "old")); s.Read(Usage(3, "r1"));
            Check(s.View(T.AddMinutes(4)) is { Compacted: true, WorkPending: false } && s.RequestAt is null, "old cache survived or compact review row disappeared");
            s.Read(Line(4, "response_item", new { type = "function_call_output" })); s.Read(Usage(5, "r2"));
            Check(s.RequestAt == T.AddMinutes(4), "new segment failed");
        });
        Test("cache: Claude 1h evidence, reuse, mixed TTL, zero cache, compact, model switch", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Claude, "abc");
            s.Read(Claude(1, "a", hour: 10)); Check(s.View(T.AddMinutes(2))!.WindowMinutes == 60, "1h evidence");
            s.Read(Claude(3, "b")); s.Read(Claude(5, "b")); Check(s.RequestAt == T.AddMinutes(3), "duplicate request renewed");
            Check(s.View(T.AddMinutes(5))!.WindowMinutes == 60, "reuse should carry previous evidence");
            s.Read(Claude(6, "c", hour: 10, five: 10)); Check(s.View(T.AddMinutes(6))!.WindowMinutes == 5, "mixed uses shortest");
            s.Read(Claude(7, "d", read: 0)); Check(s.View(T.AddMinutes(7)) is { AwaitingUsage: true }, "unconfirmed request should retain its timer with a clear basis");
            s.Read(Claude(8, "e", hour: 10));
            s.Read(JsonSerializer.Serialize(new { timestamp = T.AddMinutes(9), type = "system", subtype = "compact_boundary" }));
            Check(s.View(T.AddMinutes(9)) is { Compacted: true } && s.RequestAt is null, "compact did not stop old cache");
            s.Read(Claude(10, "f")); Check(s.View(T.AddMinutes(10))!.WindowMinutes == 5, "compact leaked 1h evidence");
            s.Read(Claude(11, "g", hour: 10)); s.Read(Claude(12, "h", model: "another"));
            Check(s.View(T.AddMinutes(12))!.WindowMinutes == 5, "model change leaked evidence");
        });
        Test("cache: unknown, malformed, future records and subagents do not appear", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Codex, "abc");
            foreach (var x in new[] { "{}", "[]", "broken", "{\"timestamp\":true}", Line(1, "unrecognized") }) s.Read(x);
            Check(s.View(T) is null, "unknown became usage");
            s.Read(Usage(100, "r")); Check(s.View(T) is null, "future timestamp appeared");
            s.Read(Line(0, "session_meta", new { source = new { subagent = new { } } }));
            Check(s.View(T.AddMinutes(101)) is null, "subagent appeared");
            var c = new ChatCacheState(ChatPlatform.Claude, "abc"); c.Read(Claude(1, "a", hour: 1));
            c.Read("{\"type\":\"assistant\",\"isSidechain\":true}"); Check(c.View(T.AddMinutes(2)) is null, "Claude sidechain appeared");
        });
        Test("cache: thresholds and six-hour retention", () =>
        {
            var e = new ChatCacheEntry(ChatPlatform.Codex, "a", "a", T, 30, "", false);
            Check(e.Urgency(T.AddMinutes(9)) == 0 && e.Urgency(T.AddMinutes(10)) == 1 && e.Urgency(T.AddMinutes(20)) == 2 && e.Urgency(T.AddMinutes(30)) == 3, "Codex colors");
            e = e with { WindowMinutes = 60 }; Check(e.Urgency(T.AddMinutes(40)) == 2 && !e.Expired(T.AddMinutes(59)), "Claude colors");
            var s = new ChatCacheState(ChatPlatform.Codex, "a"); s.Read(Usage(0, "r")); Check(s.View(T.AddHours(7)) is null, "old retention");
        });
        Test("cache: paginated rollouts keep one canonical main chat and hide obsolete rows", () =>
        {
            var root=Temp();var folder=Path.Combine(root,"sessions");Directory.CreateDirectory(folder);
            const string chat="11111111-1111-1111-1111-111111111111",page="22222222-2222-2222-2222-222222222222",
                page2="33333333-3333-3333-3333-333333333333",agent="44444444-4444-4444-4444-444444444444",fork="55555555-5555-5555-5555-555555555555";
            try
            {
                string Header(string id,string kind="user")=>Line(0,"session_meta",new{id,source="vscode",thread_source=kind,history_base=new{thread_id=chat}});
                var first=Path.Combine(folder,"rollout-"+chat+"_"+page+".jsonl");var second=Path.Combine(folder,"rollout-"+chat+"_"+page2+".jsonl");
                File.WriteAllText(first,Header(chat)+"\n"+Line(0,"event_msg",new{type="task_started",turn_id="turn"})+"\n"+Usage(1,"r1")+"\n");
                File.WriteAllText(second,Header(chat)+"\n"+Line(2,"event_msg",new{type="task_complete",turn_id="turn"})+"\n");
                File.SetLastWriteTimeUtc(first,T.UtcDateTime);File.SetLastWriteTimeUtc(second,T.AddMinutes(2).UtcDateTime);
                File.WriteAllText(Path.Combine(folder,"rollout-"+agent+".jsonl"),Header(agent,"subagent")+"\n"+Usage(1,"agent-request")+"\n");
                File.WriteAllText(Path.Combine(folder,"rollout-"+fork+".jsonl"),Header(fork)+"\n"+Usage(1,"fork-request")+"\n");
                File.WriteAllText(Path.Combine(root,"session_index.jsonl"),JsonSerializer.Serialize(new{id=chat,thread_name="Main chat"})+"\n");
                var monitor=new ChatCacheMonitor(root,Path.Combine(root,"claude"));var now=T.AddMinutes(3);var live=monitor.Poll(now);
                var main=live.Single(e=>e.Id==chat);
                Check(live.Count==2&&main.Title=="Main chat"&&main.RequestAt==T&&!main.WorkPending,"resumed identity lost its title, original request or completion");
                Check(live.Any(e=>e.Id==fork),"history_base was mistaken for subagent ownership");
                var retained=new[]{page,page2,agent}.Select(id=>main with{Id=id,Running=true}).ToArray();
                var history=new ChatSessionHistory(Path.Combine(root,"data"));history.Capture(retained,now);history.Flush(now);
                var saved=Directory.GetFiles(Path.Combine(root,"data","chat-sessions")).ToDictionary(p=>p,File.ReadAllText);
                var policy=new ChatLifecycleMonitor(root,[]).Poll(live.Concat(retained),now,monitor.HiddenRows);
                Check(ChatListPolicy.Merge(live,retained,now,policy).Select(e=>e.Id).ToHashSet().SetEquals(new[]{chat,fork}),"archived aliases or subagents reappeared");
                Check(saved.All(p=>File.ReadAllText(p.Key)==p.Value),"display policy rewrote history");
                Check(new ChatCacheMonitor(root,Path.Combine(root,"claude")).Poll(now).Single(e=>e.Id==chat)==main,"cold restart changed canonical activity");
                monitor.Poll(now.AddSeconds(5));Check(monitor.LastReadBytes==0,"unchanged headers reread on every poll");
            }
            finally { Directory.Delete(root,true); }
        });
        Test("cache: canonical metadata is bounded and incomplete headers are retried", () =>
        {
            var root=Temp();var folder=Path.Combine(root,"sessions");Directory.CreateDirectory(folder);
            const string chat="11111111-1111-1111-1111-111111111111",page="22222222-2222-2222-2222-222222222222";
            var path=Path.Combine(folder,"rollout-"+page+".jsonl");
            try
            {
                File.WriteAllText(path,"{\"type\":\"session_meta\"");
                var monitor=new ChatCacheMonitor(root,Path.Combine(root,"claude"));
                Check(monitor.Poll(T.AddMinutes(2)).Count==0,"partial header invented a filename chat");
                var header=Line(0,"session_meta",new{session_id=chat,source="vscode",thread_source="user",base_instructions=new string('x',80000)});
                File.WriteAllText(path,header+"\n"+Usage(1,"r1")+"\n");
                Check(monitor.Poll(T.AddMinutes(3)).Single().Id==chat,"large complete header or session_id fallback lost");
                File.WriteAllText(path,new string('x',2*1024*1024));
                var head=MetadataTail.FirstLine(path);
                Check(head.Text is null&&head.Bytes<=1024*1024+1,"unbounded metadata read");
            }
            finally { Directory.Delete(root,true); }
        });
        Test("cache: new input resets immediately on both platforms and survives restart replay", () =>
        {
            foreach (var platform in new[] { ChatPlatform.Codex, ChatPlatform.Claude })
            {
                var usage = platform == ChatPlatform.Codex ? Usage(1, "a") : Claude(1, "a", hour: 10);
                var input = platform == ChatPlatform.Codex ? Line(21, "event_msg", new { type = "task_started" })
                    : JsonSerializer.Serialize(new { timestamp = T.AddMinutes(21), type = "user" });
                var state = new ChatCacheState(platform, "abc"); state.Read(usage); state.Read(input);
                var view = state.View(T.AddMinutes(21));
                Check(view is { AwaitingUsage: true } && view.AgeMinutes(T.AddMinutes(21)) == 0, "row disappeared before usage");
                state.Read(input); Check(state.RequestAt == T.AddMinutes(21), "duplicate input renewed timer");
                var replay = new ChatCacheState(platform, "abc"); replay.Read(usage); replay.Read(input);
                Check(replay.View(T.AddMinutes(22))!.RequestAt == view!.RequestAt, "restart lost pending timer");
            }
        });
        Test("cache: partial Claude usage keeps row; later evidence enriches without resetting time", () =>
        {
            var state = new ChatCacheState(ChatPlatform.Claude, "abc");
            state.Read(Claude(1, "a", hour: 10));
            state.Read(JsonSerializer.Serialize(new { timestamp = T.AddMinutes(20), type = "user" }));
            state.Read(Claude(21, "b", read: 0, stop: ""));
            Check(state.View(T.AddMinutes(21)) is { AwaitingUsage: true } && state.RequestAt == T.AddMinutes(20), "partial usage removed row");
            state.Read(Claude(22, "b", hour: 10));
            Check(state.View(T.AddMinutes(22)) is { AwaitingUsage: false, WindowMinutes: 60 } && state.RequestAt == T.AddMinutes(20), "streaming update renewed timer or lost evidence");
            state.Read(JsonSerializer.Serialize(new { timestamp = T.AddMinutes(30), type = "user" }));
            state.Read(Claude(31, "b", hour: 10));
            Check(state.View(T.AddMinutes(31)) is { AwaitingUsage: true } && state.RequestAt == T.AddMinutes(30), "old duplicate replaced new input");
        });
        Test("cache: compact during pending request retains row but drops old TTL evidence", () =>
        {
            var state = new ChatCacheState(ChatPlatform.Claude, "abc");
            state.Read(Claude(1, "a", hour: 10));
            state.Read(JsonSerializer.Serialize(new { timestamp = T.AddMinutes(20), type = "user" }));
            state.Read(JsonSerializer.Serialize(new { timestamp = T.AddMinutes(21), type = "system", subtype = "compact_boundary" }));
            Check(state.View(T.AddMinutes(21)) is { AwaitingUsage: true, WindowMinutes: 5 }, "pending input disappeared or old TTL survived");
            state.Read(Claude(22, "b", hour: 10));
            Check(state.RequestAt == T.AddMinutes(20) && state.View(T.AddMinutes(22)) is { AwaitingUsage: false, WindowMinutes: 60 }, "new evidence not applied");
        });
        Test("cache: manual compact output and summaries cannot resurrect an unconfirmed request", () =>
        {
            var s = new ChatCacheState(ChatPlatform.Claude, "abc"); s.Read(Claude(1, "a", hour: 10, stop: "tool_use"));
            s.Read(JsonSerializer.Serialize(new { timestamp = T.AddMinutes(2), type = "user", message = new { content = "<command-name>/compact</command-name>" } }));
            s.Read(JsonSerializer.Serialize(new { timestamp = T.AddMinutes(3), type = "system", subtype = "compact_boundary", compactMetadata = new { trigger = "manual" } }));
            s.Read(JsonSerializer.Serialize(new { timestamp = T.AddMinutes(4), type = "user", isCompactSummary = true, message = new { content = "synthetic summary" } }));
            s.Read(JsonSerializer.Serialize(new { timestamp = T.AddMinutes(5), type = "user", message = new { content = "<local-command-stdout>Compacted session</local-command-stdout>" } }));
            var entry = s.View(T.AddHours(5))!;
            Check(entry is { Compacted: true, Running: false, ActivityUncertain: false } && entry.ActivityAt == T.AddMinutes(3), "local output restarted cache/work");
            s.Read(JsonSerializer.Serialize(new { timestamp = T.AddMinutes(6), type = "user", message = new { content = "please discuss compact behavior" } }));
            Check(s.View(T.AddMinutes(6)) is { Compacted: false, Running: true }, "real follow-up hidden by old compact");
            Check(s.View(T.AddMinutes(6))!.CompactedAt == T.AddMinutes(3), "lost past compact marker");
        });
        Test("cache: long Codex thinking remains running, completion reveals the original request age", () =>
        {
            var s=new ChatCacheState(ChatPlatform.Codex,"a");
            s.Read(Line(0,"event_msg",new{type="task_started",turn_id="turn-a"}));
            var during=s.View(T.AddMinutes(12))!;
            Check(during.Running && !during.Expired(T.AddMinutes(12)),"long thinking became idle");
            Check(during.AgeMinutes(T.AddMinutes(12))==12,"running falsified cache age");
            s.Read(Line(13,"event_msg",new{type="task_complete",turn_id="turn-a"}));
            var ended=s.View(T.AddMinutes(14))!;
            Check(!ended.WorkPending && ended.AgeMinutes(T.AddMinutes(14))==14,"completion renewed cache");
        });
        Test("cache: active and unconfirmed work never moves into the expired-idle list", () =>
        {
            var s=new ChatCacheState(ChatPlatform.Codex,"a");
            s.Read(Line(0,"event_msg",new{type="task_started",turn_id="turn-a"}));
            var uncertain=s.View(T.AddMinutes(40))!;
            Check(uncertain.ActivityUncertain && !uncertain.Running && !uncertain.Expired(T.AddMinutes(40)),"silence claimed idle or active forever");
            s.Read(Line(41,"response_item",new{type="reasoning"}));
            var active=s.View(T.AddMinutes(42))!;
            Check(active.Running && !active.ActivityUncertain && !active.Expired(T.AddMinutes(42)) && active.AgeMinutes(T.AddMinutes(42))==42,"activity renewed cache or failed to restore running");
            s.Read(Line(43,"event_msg",new{type="turn_aborted",turn_id="turn-a"}));
            Check(s.View(T.AddMinutes(44))!.Expired(T.AddMinutes(44)),"abort pretended cache still fresh");
        });
        Test("cache: late completion for an old turn cannot stop a new Codex turn", () =>
        {
            var s=new ChatCacheState(ChatPlatform.Codex,"a");
            s.Read(Line(0,"event_msg",new{type="task_started",turn_id="old"}));
            s.Read(Line(2,"event_msg",new{type="task_started",turn_id="new"}));
            s.Read(Line(3,"event_msg",new{type="task_complete",turn_id="old"}));
            Check(s.View(T.AddMinutes(4))!.Running && s.RequestAt==T.AddMinutes(2),"old turn stopped new work");
            s.Read(Line(5,"event_msg",new{type="task_complete",turn_id="new"}));
            s.Read(Line(6,"event_msg",new{type="task_started",turn_id="old"}));
            Check(!s.View(T.AddMinutes(7))!.WorkPending && s.RequestAt==T.AddMinutes(2),"replayed closed turn restarted timer");
        });
        Test("cache: duplicate completed turn does not stop a new pending input", () =>
        {
            var s=new ChatCacheState(ChatPlatform.Codex,"a");
            s.Read(Line(0,"event_msg",new{type="task_started",turn_id="old"}));
            s.Read(Line(1,"event_msg",new{type="task_complete",turn_id="old"}));
            s.Read(Line(2,"response_item",new{type="message",role="user"}));
            s.Read(Line(3,"event_msg",new{type="task_complete",turn_id="old"}));
            Check(s.View(T.AddMinutes(4))!.Running && s.RequestAt==T.AddMinutes(2),"duplicate finish stopped pending input");
        });
        Test("cache: Claude tool waiting remains active until end_turn, with original cache age", () =>
        {
            var s=new ChatCacheState(ChatPlatform.Claude,"a");
            s.Read(JsonSerializer.Serialize(new{timestamp=T,type="user"}));
            s.Read(Claude(1,"r",hour:10,stop:"tool_use"));
            Check(s.View(T.AddMinutes(12))!.Running,"Claude tool wait became idle after 2 minutes");
            s.Read(Claude(13,"r",hour:10,stop:"end_turn"));
            Check(!s.View(T.AddMinutes(14))!.WorkPending && s.RequestAt==T,"Claude completion renewed timer");
            s.Read(Claude(15,"r",hour:10,stop:"tool_use"));
            Check(!s.View(T.AddMinutes(16))!.WorkPending,"late streamed tool_use reopened completed request");
        });
        Test("cache: old Claude output after new input cannot finish the new request", () =>
        {
            var s=new ChatCacheState(ChatPlatform.Claude,"a");
            s.Read(Claude(1,"old",hour:10));
            s.Read(JsonSerializer.Serialize(new{timestamp=T.AddMinutes(5),type="user"}));
            s.Read(Claude(6,"old",hour:10));
            Check(s.View(T.AddMinutes(7))!.Running && s.RequestAt==T.AddMinutes(5),"late old end_turn stopped new input");
            s.Read(Claude(8,"new",hour:10));
            Check(!s.View(T.AddMinutes(9))!.WorkPending && s.RequestAt==T.AddMinutes(5),"current response did not finish normally");
        });
        Test("cache: Claude final metadata without repeated usage still ends the current request", () =>
        {
            var s=new ChatCacheState(ChatPlatform.Claude,"a");
            s.Read(Claude(1,"r",hour:10,stop:"tool_use"));
            s.Read(JsonSerializer.Serialize(new{timestamp=T.AddMinutes(5),type="assistant",requestId="r",message=new{id="r",model="claude",stop_reason="end_turn"}}));
            Check(!s.View(T.AddMinutes(6))!.WorkPending && s.RequestAt==T.AddMinutes(1),"final metadata ignored or renewed age");
        });
        Test("cache: restart replay restores ongoing status without resetting request age", () =>
        {
            var lines=new[]{Line(0,"event_msg",new{type="task_started",turn_id="a"}),Usage(1,"r"),Line(8,"response_item",new{type="function_call"})};
            var first=new ChatCacheState(ChatPlatform.Codex,"a");var replay=new ChatCacheState(ChatPlatform.Codex,"a");
            foreach(var line in lines){first.Read(line);replay.Read(line);}
            Check(first.View(T.AddMinutes(15))!.Running && replay.View(T.AddMinutes(15))!.Running,"replayed running status lost");
            Check(first.RequestAt==T && replay.RequestAt==T,"replay reset request clock");
        });
        Test("cache: project label uses explicit assignment, nearest root, or directory name", () =>
        {
            var root = Temp();
            try
            {
                File.WriteAllText(Path.Combine(root, ".codex-global-state.json"), """
                {"local-projects":{"project-a":{"name":"Project A","rootPaths":["D:/repo"]}},
                "electron-workspace-root-labels":{"D:/repo/tools":"Tools"},
                "thread-project-assignments":{"chat-a":{"projectKind":"local","projectId":"project-a"}}}
                """);
                var projects = new ChatProjects(root); projects.Refresh();
                Check(projects.Name(ChatPlatform.Codex, "chat-a", "D:/elsewhere") == "Project A", "assignment ignored");
                Check(projects.Name(ChatPlatform.Claude, "other", "D:\\repo\\tools\\sub") == "Tools", "nearest root ignored");
                Check(projects.Name(ChatPlatform.Claude, "other", "D:/repo-extra") == "repo-extra", "root prefix overmatched");
                Check(projects.Name(ChatPlatform.Codex, "other", null) is null, "invented project name");
            }
            finally { Directory.Delete(root, true); }
            var codex = new ChatCacheState(ChatPlatform.Codex, "a"); codex.Read(Line(0, "session_meta", new { cwd = "D:/repo", source = "cli" }));
            Check(codex.WorkingDirectory == "D:/repo", "Codex cwd missing");
            var claude = new ChatCacheState(ChatPlatform.Claude, "a"); claude.Read("{\"type\":\"custom-title\",\"customTitle\":\"Name\",\"cwd\":\"D:/repo\"}");
            Check(claude.WorkingDirectory == "D:/repo", "Claude cwd missing");
        });
        Test("cache: shared project roots never assign a chat by configuration order", () =>
        {
            var root=Temp();
            try
            {
                var projects=new ChatProjects(root);
                var desktop="\"desktop\":{\"name\":\"Desktop project\",\"rootPaths\":[\"D:/Desktop\"]}";
                var other="\"other\":{\"name\":\"Other project\",\"rootPaths\":[\"D:/other\",\"d:\\\\desktop\\\\\"]}";
                foreach(var order in new[]{desktop+","+other,other+","+desktop})
                {
                    var path=Path.Combine(root,".codex-global-state.json");
                    File.WriteAllText(path,"{\"local-projects\":{"+order+"},\"electron-workspace-root-labels\":{\"D:/Desktop/tools\":\"Tools\"},\"thread-project-assignments\":{\"assigned\":{\"projectKind\":\"local\",\"projectId\":\"other\"}}}");
                    File.SetLastWriteTimeUtc(path,order.StartsWith(desktop)?T.UtcDateTime:T.AddSeconds(1).UtcDateTime);
                    projects.Refresh();
                    Check(projects.Name(ChatPlatform.Claude,"unassigned","D:/Desktop/repo/src")=="Desktop","shared root chose a project or a changing subdirectory");
                    Check(projects.Name(ChatPlatform.Codex,"unassigned","D:/Desktop")=="Desktop","unassigned Codex chat inherited another chat's project");
                    Check(projects.Name(ChatPlatform.Codex,"assigned","D:/Desktop")=="Other project","explicit assignment lost");
                    Check(projects.Name(ChatPlatform.Claude,"assigned","D:/Desktop")=="Desktop","Codex assignment leaked across platforms");
                    Check(projects.Name(ChatPlatform.Claude,"unassigned","D:/other/sub")=="Other project","unique root lost");
                    Check(projects.Name(ChatPlatform.Claude,"unassigned","D:/Desktop/tools/sub")=="Tools","more specific explicit label lost");
                }
            }
            finally { Directory.Delete(root,true); }
        });
        Test("cache: project refresh discards stale or partially parsed assignments", () =>
        {
            var root=Temp();
            try
            {
                var path=Path.Combine(root,".codex-global-state.json");var projects=new ChatProjects(root);
                void Write(string json) {File.WriteAllText(path,json);projects.Refresh();}
                Write("""{"local-projects":{"a":{"name":"Alias","rootPaths":["D:/Desktop"]}}}""");
                Check(projects.Name(ChatPlatform.Claude,"chat","D:/Desktop")=="Alias","initial mapping missing");
                Write("{torn");
                Check(projects.Name(ChatPlatform.Claude,"chat","D:/Desktop")=="Desktop","invalid file retained stale mapping");
                Write("""{"local-projects":{"a":{"name":"New alias","rootPaths":["D:/Desktop"]}}}""");
                Check(projects.Name(ChatPlatform.Claude,"chat","D:/Desktop")=="New alias","corrected file not reloaded");
                File.Delete(path);projects.Refresh();
                Check(projects.Name(ChatPlatform.Claude,"chat","D:/Desktop")=="Desktop","removed config retained stale mapping");
            }
            finally { Directory.Delete(root,true); }
        });
        Test("cache: Claude-only refresh corrects retained project metadata without changing activity", () =>
        {
            var root=Temp();
            try
            {
                var claude=Path.Combine(root,"claude");Directory.CreateDirectory(Path.Combine(claude,"projects"));
                var path=Path.Combine(claude,"projects",Guid.NewGuid()+".jsonl");
                File.WriteAllText(path,"""{"type":"custom-title","customTitle":"Fixture","cwd":"D:/Desktop/repo"}"""+"\n"+Claude(1,"r1")+"\n");
                var state=Path.Combine(root,".codex-global-state.json");
                File.WriteAllText(state,"""{"local-projects":{"a":{"name":"Alias","rootPaths":["D:/Desktop"]}}}""");
                var monitor=new ChatCacheMonitor(root,claude);var now=T.AddMinutes(2);
                var before=monitor.Poll(now,claude:true,codex:false).Single();
                Check(before.Project=="Alias","Claude-only never loads path labels");
                var data=Path.Combine(root,"data");var history=new ChatSessionHistory(data);history.Capture([before],now);history.Flush(now);
                File.WriteAllText(state,"""{"local-projects":{"a":{"name":"Alias","rootPaths":["D:/Desktop"]},"b":{"name":"Other","rootPaths":["D:/Desktop"]}}}""");
                var after=monitor.Poll(now.AddMinutes(1),claude:true,codex:false).Single();
                Check(after.Project=="Desktop"&&after with{Project=before.Project}==before,"label refresh changed request or work state");
                history.Capture([after],now.AddMinutes(1));history.Flush(now.AddMinutes(1));
                var saved=new ChatSessionHistory(data).Current(now.AddMinutes(1))!.Chats.Single().Last;
                Check(saved==after,"retained metadata kept the wrong project");
            }
            finally { Directory.Delete(root,true); }
        });
        Test("cache: partial UTF8 lines, append-only reads, truncation, oversized lines", () =>
        {
            var root = Temp(); var path = Path.Combine(root, "tail.jsonl");
            try
            {
                var reader = new MetadataTail(); var lines = new List<string>(); var gaps = 0;
                var bytes = Encoding.UTF8.GetBytes("{\"标题\":\"中文\"}\n");
                File.WriteAllBytes(path, bytes[..9]); reader.Read(path, lines.Add, () => gaps++); Check(lines.Count == 0, "partial parsed");
                using (var f = new FileStream(path, FileMode.Append)) f.Write(bytes[9..]);
                reader.Read(path, lines.Add, () => gaps++); Check(lines.Single() == "{\"标题\":\"中文\"}", "split UTF8 corrupted");
                Check(reader.Read(path, lines.Add, () => gaps++) == 0, "unchanged file reread");
                File.WriteAllText(path, "{}\n"); reader.Read(path, lines.Add, () => gaps++); Check(lines.Last() == "{}" && gaps == 1, "truncate reset");
                File.AppendAllText(path, new string('x', 1100000) + "\n{\"ok\":1}\n"); reader.Read(path, lines.Add, () => gaps++);
                Check(gaps == 2 && lines.Last() == "{\"ok\":1}", "oversize recovery");
            }
            finally { Directory.Delete(root, true); }
        });
        Test("cache: monitor deduplicates resumed files, finds old-folder sessions, indexes names, idle reads zero", () =>
        {
            var root = Temp(); var co = Path.Combine(root, "codex"); var cl = Path.Combine(root, "claude");
            var folder = Path.Combine(co, "sessions", "2020"); Directory.CreateDirectory(folder);
            const string id = "00000000-0000-0000-0000-000000000001";
            try
            {
                File.WriteAllText(Path.Combine(co, "session_index.jsonl"), JsonSerializer.Serialize(new { id, thread_name = "Name" }) + "\n");
                File.WriteAllText(Path.Combine(folder, "rollout-one-" + id + ".jsonl"), Usage(1, "r1") + "\n");
                File.WriteAllText(Path.Combine(folder, "rollout-two-" + id + ".jsonl"), Usage(2, "r2") + "\n");
                var m = new ChatCacheMonitor(co, cl); var entries = m.Poll(T.AddMinutes(3));
                Check(entries.Count == 1 && entries[0].Title == "Name" && entries[0].RequestAt == T.AddMinutes(2), "resumed sessions grouped");
                Check(new ChatCacheMonitor(co, cl).Poll(T.AddMinutes(3)).Single().RequestAt == entries[0].RequestAt, "restart changed timer");
                m.Poll(T.AddMinutes(3).AddSeconds(5)); Check(m.LastReadBytes == 0, "idle reread");
                File.AppendAllText(Path.Combine(co, "session_index.jsonl"), "{\"id\":null,\"thread_name\":\"malformed\"}\n" + JsonSerializer.Serialize(new { id, thread_name = "Rename" }) + "\n");
                Check(m.Poll(T.AddMinutes(4)).Single().Title == "Rename", "rename not applied");
            }
            finally { Directory.Delete(root, true); }
        });
        tests.Add(("auth: auto logout default off; clean official command; ACL fail and timeout are safe", async () =>
        {
            Check(!new WidgetSettings().AutoLogoutOnExit, "default must be off");
            var root = Temp(); var choice = new CliChoice("official.exe", "2.1.284", null, "official"); var calls = 0;
            try
            {
                async Task<int> Run(ProcessStartInfo psi, CancellationToken ct)
                {
                    calls++; Check(psi.ArgumentList.SequenceEqual(new[] { "auth", "logout" }), "wrong command");
                    Check(psi.CreateNoWindow && !psi.UseShellExecute && psi.Environment["CLAUDE_CONFIG_DIR"] == root, "wrong environment");
                    Check(!psi.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"), "inherited token"); await Task.Yield(); return 0;
                }
                var error = await ClaudeCli.LogoutQuietlyAsync(choice, root, root, CancellationToken.None, _ => false, Run);
                Check(error is not null && calls == 0, "ACL failure launched process");
                error = await ClaudeCli.LogoutQuietlyAsync(choice with { Problem = "cli_untrusted" }, root, root, CancellationToken.None, _ => true, Run);
                Check(error is not null && calls == 0, "untrusted launch");
                error = await ClaudeCli.LogoutQuietlyAsync(choice, root, root, CancellationToken.None, _ => true, Run);
                Check(error is null && calls == 1, "logout command");
                using var cancel = new CancellationTokenSource(20);
                error = await ClaudeCli.LogoutQuietlyAsync(choice, root, root, cancel.Token, _ => true, async (_, ct) => { await Task.Delay(10000, ct); return 0; });
                Check(error is not null, "timeout reported success");
            }
            finally { Directory.Delete(root, true); }
        }));
        tests.Add(("auth: signed real CLI logout in an empty private directory completes without an account", async () =>
        {
            TestEnvironment.RequireLive();
            var root = Temp();
            try
            {
                var choice = ClaudeCli.Resolve(null);
                Check(choice.Usable, "signed compatible CLI required on this Windows QA host");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var result = await ClaudeCli.LogoutQuietlyAsync(choice, root, root, timeout.Token);
                Check(result is null, result ?? "");
                Check(!File.Exists(ClaudeConfigFiles.CredentialsPath(root)), "unexpected credential created");
            }
            finally { Directory.Delete(root, true); }
        }));
        Test("usage: diagnostics retain only fixed classes, never raw auth output", () =>
        {
            Check(ClaudeCliUsageSource.DiagnosticClass("Authorization: Bearer test-secret") is null, "secret retained");
            Check(ClaudeCliUsageSource.DiagnosticClass("Request failed with status code 429; private body") == "http_429", "status classification");
            Check(ClaudeCliUsageSource.DiagnosticClass("fetchUtilization: GET /api/oauth/usage (attempt 1)") == "usage_requested", "usage classification");
        });
        Test("usage: allow the official quota endpoint while disabling telemetry and inherited credentials", () =>
        {
            var psi = new ProcessStartInfo("official.exe");
            psi.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            psi.Environment["ANTHROPIC_AUTH_TOKEN"] = "fixture-secret";
            psi.Environment["ANTHROPIC_BASE_URL"] = "https://invalid.example";
            ClaudeCli.PrepareEnvironment(psi, "private-dir", allowUsage: true);
            Check(!psi.Environment.ContainsKey("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"), "usage endpoint locally blocked");
            Check(!psi.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN") && !psi.Environment.ContainsKey("ANTHROPIC_BASE_URL"), "inherited credential/address");
            Check(psi.Environment["DISABLE_TELEMETRY"] == "1" && psi.Environment["DISABLE_ERROR_REPORTING"] == "1" && psi.Environment["DISABLE_AUTOUPDATER"] == "1", "privacy options lost");
        });
        Test("paths: canonical directory preserves files and normalizes directory aliases", () =>
        {
            var root = Temp();
            try
            {
                File.WriteAllText(Path.Combine(root, "marker"), "fixture");
                var actual = DataPaths.CanonicalDirectory(root);
                Check(Path.IsPathFullyQualified(actual) && File.ReadAllText(Path.Combine(actual, "marker")) == "fixture", "wrong physical directory");
                Check(DataPaths.CanonicalDirectory(Path.Combine(root, ".")) == actual, "alias differs");
            }
            finally { Directory.Delete(root, true); }
        });
        Test("cache: Windows stale mtime does not hide a live long-running chat", () =>
        {
            var root = Temp(); var folder = Path.Combine(root, "sessions", "2020"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "rollout-00000000-0000-0000-0000-000000000099.jsonl");
            try
            {
                File.WriteAllText(path, Usage(1, "r1") + "\n"); File.SetLastWriteTimeUtc(path, T.AddDays(-20).UtcDateTime);
                var monitor = new ChatCacheMonitor(root, Path.Combine(root, "claude"));
                Check(monitor.Poll(T.AddMinutes(2)).Single().RequestAt == T.AddMinutes(1), "cold start excluded live content with old mtime");
                File.AppendAllText(path, Usage(3, "r2") + "\n"); File.SetLastWriteTimeUtc(path, T.AddDays(-20).UtcDateTime);
                Check(monitor.Poll(T.AddMinutes(4)).Single().RequestAt == T.AddMinutes(3), "rediscovery discarded active file");
            }
            finally { Directory.Delete(root, true); }
        });
    }

    public static void Probe()
    {
        var m = ChatCacheMonitor.Local(); var timer = Stopwatch.StartNew();
        var list = m.Poll(DateTimeOffset.Now); var cold = timer.Elapsed.TotalMilliseconds; var bytes = m.LastReadBytes;
        timer.Restart(); m.Poll(DateTimeOffset.Now);
        Console.WriteLine(JsonSerializer.Serialize(new { coldMs = cold, warmMs = timer.Elapsed.TotalMilliseconds, coldBytes = bytes,
            warmBytes = m.LastReadBytes, files = m.TrackedFiles, warning = m.Warning,
            rows = list.Select(e => new { platform = e.Platform.ToString(), e.Id, e.Title, e.WindowMinutes, ageMinutes = e.AgeMinutes(DateTimeOffset.Now), e.Running, e.Basis }) }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
