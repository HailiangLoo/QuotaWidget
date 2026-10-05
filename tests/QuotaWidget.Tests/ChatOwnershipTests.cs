using System.Reflection;
using System.Text.Json;
using QuotaWidget.Core;

static class ChatOwnershipTests
{
    sealed class Database : IDisposable
    {
        readonly Type _type=typeof(TokenStore).Assembly.GetType("QuotaWidget.Core.MiniSqlite")!;
        readonly object _db;
        public Database(string path)=>_db=Activator.CreateInstance(_type,path,false)!;
        public void Exec(string sql)=>_type.GetMethod("Exec")!.Invoke(_db,[sql]);
        public void Dispose()=>((IDisposable)_db).Dispose();
    }
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        const string root="11111111-1111-1111-1111-111111111111",child="22222222-2222-2222-2222-222222222222",
            grandchild="33333333-3333-3333-3333-333333333333",other="44444444-4444-4444-4444-444444444444";
        var now=DateTimeOffset.Parse("2026-10-03T12:00:00Z");
        void Check(bool value,string why){if(!value)throw new Exception(why);}
        void Test(string name,Action<string> action)=>tests.Add(("ownership: "+name,()=>
        {
            var dir=Path.Combine(Path.GetTempPath(),"qw-ownership-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            try{action(dir);}finally{Directory.Delete(dir,true);}return Task.CompletedTask;
        }));
        string Source(string parent)=>JsonSerializer.Serialize(new{subagent=new{thread_spawn=new{parent_thread_id=parent,depth=1}}});
        void Put(TokenStore store,string id,string model,long amount=10,string platform="Codex",int offset=-1)=>store.Put(new(platform,id,id,model,now.AddMinutes(offset),amount,amount*10,amount*2));
        Test("multi-level agents roll into root without changing model or platform totals",dir=>
        {
            using var store=new TokenStore(Path.Combine(dir,"tokens.sqlite"));
            Put(store,root,"Astra");Put(store,child,"Luna",20);Put(store,grandchild,"Luna",30);Put(store,other,"Luna",40);Put(store,child,"Opus",90,"Claude");
            var before=store.Breakdown(now.AddHours(-1),now,"Codex");
            store.SetChatParent("Codex",grandchild,child);store.SetChatParent("Codex",child,root);
            var after=store.Breakdown(now.AddHours(-1),now,"Codex");var parent=after.Chats.Single(c=>c.Key==root);
            Check(after.Total==before.Total&&after.Chats.Count==2,"total changed or duplicate child row retained");
            Check(parent.Subagents==2&&parent.Members==2&&parent.Usage.Input==60&&parent.DirectUsage?.Input==10,"parent composition wrong");
            Check(after.Models.All(g=>g.Usage==before.Models.Single(b=>b.Key==g.Key).Usage),"model amounts changed");
            Check(after.Chats.Sum(c=>c.Usage.Input)==after.Total.Input&&after.Chats.Sum(c=>c.Usage.Cached)==after.Total.Cached&&after.Chats.Sum(c=>c.Usage.Output)==after.Total.Output,"chat totals do not conserve");
            Check(store.Sum(now.AddHours(-1),now,"Codex",root)==parent.Usage,"chat tip and platform row disagree");
            Check(store.Sum(now.AddHours(-1),now,"Codex",child).Input==50,"nested family sum missing grandchildren");
            Check(store.Sum(now.AddHours(-1),now,"Claude",child).Input==90&&store.Parents("Claude").Count==0,"cross-platform attribution leak");
            Check(store.ChatIds("Codex").Count==4,"raw request chat IDs were rewritten");
        });
        Test("parent absent from time window still receives children; timestamps and scope stay unchanged",dir=>
        {
            using var store=new TokenStore(Path.Combine(dir,"tokens.sqlite"));store.SetMeta("since",now.AddMinutes(-30).ToString("O"));
            Put(store,root,"Astra",10,offset:-60);Put(store,child,"Luna",20);store.SetChatParent("Codex",child,root);
            var result=store.Breakdown(now.AddHours(-2),now,"Codex");var group=result.Chats.Single();
            Check(result.Total.Input==20&&group.Key==root&&group.DirectUsage==TokenSummary.Empty&&group.Subagents==1,"out-of-window parent required or old usage leaked");
            Check(group.ModelBreakdown.Count==1&&group.ModelBreakdown[0].Model=="Luna"&&group.ModelBreakdown[0].Usage==group.Usage,"out-of-window parent model leaked into visible split");
            Check(store.Sum(now.AddSeconds(-10),now,"Codex",root).Requests==0,"child timestamp moved");
        });
        Test("each owning chat splits actual models across self and multiple levels of agents",dir=>
        {
            using var store=new TokenStore(Path.Combine(dir,"tokens.sqlite"));
            Put(store,root,"Astra");Put(store,child,"Luna",20);Put(store,grandchild,"Luna",30);Put(store,other,"Luna",40);
            store.Put(new("Codex","root-luna",root,"Luna",now.AddMinutes(-1),4,40,8));
            store.Put(new("Codex","grandchild-astra",grandchild,"Astra",now.AddMinutes(-1),5,50,10));
            store.Put(new("Codex","unknown-model",child,null,now.AddMinutes(-1),7,70,14));
            Put(store,child,"Opus",90,"Claude");
            store.SetChatParent("Codex",child,root);store.SetChatParent("Codex",grandchild,child);
            var data=store.Breakdown(now.AddHours(-1),now,"Codex");var split=data.Chats.Single(c=>c.Key==root).ModelBreakdown;
            Check(split.Select(m=>m.Model).SequenceEqual(new[]{"Luna","Astra","未标注模型"}),"missing, reordered or guessed model identity");
            Check(split[0].Usage.Input==54&&split[0].Usage.Requests==3&&split[0].Subagents==2,"Luna self/descendant attribution wrong");
            Check(split[1].Usage.Input==15&&split[1].Usage.Requests==2&&split[1].Subagents==1,"Astra subagent merged into Luna or omitted");
            Check(split[2].Usage.Input==7&&split[2].Subagents==1,"unknown model lost");
            long[] Counters(TokenSummary s)=>[s.Input,s.Cached,s.Output,s.Written,s.Requests,s.Conflicts,s.MissingWrites];
            void Conserved(IEnumerable<TokenSummary> rows,TokenSummary expected)
            {
                var totals=rows.Select(Counters).ToArray();var target=Counters(expected);
                Check(Enumerable.Range(0,target.Length).All(i=>totals.Sum(t=>t[i])==target[i]),"split counters fail conservation");
            }
            foreach(var chat in data.Chats)Conserved(chat.ModelBreakdown.Select(m=>m.Usage),chat.Usage);
            Conserved(data.Chats.SelectMany(c=>c.ModelBreakdown).Select(m=>m.Usage),data.Total);
            foreach(var model in data.Models)Conserved(data.Chats.SelectMany(c=>c.ModelBreakdown).Where(m=>m.Model==model.Key).Select(m=>m.Usage),model.Usage);
            var claude=store.Breakdown(now.AddHours(-1),now,"Claude").Chats.Single().ModelBreakdown.Single();
            Check(claude.Model=="Opus"&&claude.Usage.Input==90&&claude.Subagents==0,"model split crossed platform or adopted other provider parents");
        });
        Test("unknown, conflicting, cyclic and oversized ancestry is not guessed",dir=>
        {
            using var store=new TokenStore(Path.Combine(dir,"tokens.sqlite"));Put(store,child,"Luna");Put(store,grandchild,"Luna");
            Check(!store.SetChatParent("Codex",child,"not-an-id")&&!store.SetChatParent("Claude",child,root),"invalid relation accepted");
            store.SetChatParent("Codex",child,root);store.SetChatParent("Codex",child,other);store.SetChatParent("Codex",grandchild,child);
            Check(store.Breakdown(now.AddHours(-1),now,"Codex").Chats.Count==2,"conflicting parent or its descendants guessed");
            Check(store.Sum(now.AddHours(-1),now,"Codex",root).Requests==0,"conflict included in unrelated parent");
            var cycle=new Dictionary<string,string>{{child,grandchild},{grandchild,child},{other,child}};
            Check(ChatOwnership.Root(child,cycle)==child&&ChatOwnership.Root(other,cycle)==other&&!ChatOwnership.BelongsTo(other,child,cycle),"cycle arbitrarily merged");
            var deep=Enumerable.Range(0,140).ToDictionary(i=>i.ToString(),i=>(i+1).ToString());Check(ChatOwnership.Root("0",deep)=="0","unbounded ancestry accepted");
        });
        Test("session metadata records only explicit spawn, never fork or guardian ancestry",dir=>
        {
            using var store=new TokenStore(Path.Combine(dir,"tokens.sqlite"));var cursor=new TokenCursor{Platform="Codex"};
            string Meta(string id,object source,string? fork=null)=>JsonSerializer.Serialize(new{type="session_meta",payload=new{id,source,forked_from_id=fork,model_provider="openai"}});
            using var source=JsonDocument.Parse(Source(root));
            Check(TokenParser.Read(Meta(child,source.RootElement),cursor,store,now.AddDays(-1),now),"spawn relation not parsed");
            TokenParser.Read(Meta(other,"vscode",root),cursor,store,now.AddDays(-1),now);
            TokenParser.Read(Meta(grandchild,new{subagent=new{other="guardian"}}),cursor,store,now.AddDays(-1),now);
            Check(store.Parents("Codex").Count==1&&store.Parents("Codex")[child]==root,"fork or unknown subagent guessed");
        });
        Test("state lookup walks ancestors, supports closed edges and detects contradictory sources",dir=>
        {
            using var native=new Database(Path.Combine(dir,"state_5.sqlite"));
            native.Exec($"CREATE TABLE threads(id TEXT PRIMARY KEY,source TEXT); CREATE TABLE thread_spawn_edges(parent_thread_id TEXT,child_thread_id TEXT,status TEXT);"+
                $"INSERT INTO threads VALUES('{grandchild}','{Source(child)}'),('{child}','{Source(root)}');"+
                $"INSERT INTO thread_spawn_edges VALUES('{child}','{grandchild}','closed'),('{root}','{child}','open');");
            var rows=CodexChatParents.Read(dir,[grandchild]);Check(rows.Count==2&&rows.Contains(new(child,root)),"ancestor absent from requested usage not loaded or closed child ignored");
            native.Exec($"INSERT INTO thread_spawn_edges VALUES('{other}','{child}','open');");
            using var store=new TokenStore(Path.Combine(dir,"t.sqlite"));
            foreach(var row in CodexChatParents.Read(dir,[grandchild]))store.SetChatParent("Codex",row.Child,row.Parent);
            Check(ChatOwnership.Root(grandchild,store.Parents("Codex"))==grandchild,"conflicting native metadata silently prioritized");
            File.WriteAllText(Path.Combine(dir,"state_6.sqlite"),"broken");
            var failed=false;try{CodexChatParents.Read(dir,[child]);}catch(IOException){failed=true;}Check(failed,"fallback to stale native database");
        });
        Test("existing cursor backfills metadata without replaying requests and retains it across failure/restart",dir=>
        {
            var tokens=Path.Combine(dir,"index");Directory.CreateDirectory(tokens);var codex=Path.Combine(dir,"codex");Directory.CreateDirectory(codex);
            using(var old=new TokenStore(Path.Combine(tokens,"tokens.sqlite"))){Put(old,child,"Luna");old.SetMeta("since",now.AddDays(-1).ToString("O"));}
            using(var native=new Database(Path.Combine(codex,"state_5.sqlite")))native.Exec($"CREATE TABLE thread_spawn_edges(parent_thread_id TEXT,child_thread_id TEXT);INSERT INTO thread_spawn_edges VALUES('{root}','{child}');");
            using(var index=new TokenIndex(tokens,codex,Path.Combine(dir,"claude"),now))
            {
                index.Poll(now);Check(index.LastReadBytes==0&&index.Sum(now.AddHours(-1),now,"Codex",root).Input==10,"old data needs full transcript replay");
                File.WriteAllText(Path.Combine(codex,"state_6.sqlite"),"broken");index.Poll(now.AddMinutes(2));
                Check(index.Coverage.Contains("归属暂不可读")&&index.Sum(now.AddHours(-1),now,"Codex",root).Input==10,"failure erased existing lineage");
            }
            using var read=new TokenStore(Path.Combine(tokens,"tokens.sqlite"),readOnly:true);
            Check(read.Breakdown(now.AddHours(-1),now,"Codex").Chats.Single().Key==root,"read-only restart lost attribution");
        });
        Test("relation updates are transactional and duplicate native usage stays deduplicated",dir=>
        {
            using var store=new TokenStore(Path.Combine(dir,"t.sqlite"));Put(store,child,"Luna");Put(store,child,"Luna");
            store.Begin();store.SetChatParent("Codex",child,root);store.Rollback();Check(store.Parents("Codex").Count==0,"rolled back relation retained");
            store.Begin();store.SetChatParent("Codex",child,root);store.Commit();
            Check(!store.SetChatParent("Codex",child,root)&&store.Sum(now.AddHours(-1),now,"Codex",root).Requests==1,"replayed relation or request double counted");
        });
        Test("read-only legacy index without ownership table still works",dir=>
        {
            var file=Path.Combine(dir,"t.sqlite");using(var store=new TokenStore(file)){Put(store,child,"Luna");}
            using(var db=new Database(file))db.Exec("DROP TABLE chat_parents");
            using var read=new TokenStore(file,readOnly:true);Check(read.Breakdown(now.AddHours(-1),now,"Codex").Chats.Single().Key==child,"legacy snapshot needs a write migration");
        });
    }
}
