using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using QuotaWidget.Core;

static class ChatLifecycleTests
{
    // Isolated SQLite fixtures use the production native wrapper; no dependency or provider installation.
    sealed class Database : IDisposable
    {
        readonly object _db;
        readonly Type _type = typeof(TokenStore).Assembly.GetType("QuotaWidget.Core.MiniSqlite")!;
        public Database(string path) => _db = Activator.CreateInstance(_type, path, false)!;
        public void Exec(string sql) => _type.GetMethod("Exec")!.Invoke(_db, [sql]);
        public void Dispose() => ((IDisposable)_db).Dispose();
    }

    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        var now = DateTimeOffset.Parse("2026-10-02T14:00:00Z");
        const string id = "12345678-1234-1234-1234-123456789abc";
        const string other = "12345678-1234-1234-1234-123456789def";
        ChatCacheEntry Chat(ChatPlatform platform, string key = id) => new(platform, key, key, now.AddMinutes(-2), 30, "fixture", false);
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        void Test(string name, Action<string> body) => tests.Add(("lifecycle: " + name, () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "qw-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { body(root); } finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }));
        string SessionDir(string root) { var p = Path.Combine(root, "desktop", "account", "org"); Directory.CreateDirectory(p); return p; }
        void Tombstone(string dir, string key = id) => File.WriteAllText(Path.Combine(dir, "deleted_" + key), now.ToUnixTimeMilliseconds().ToString());
        Test("Codex archive and unarchive are immediate and platform isolated", root =>
        {
            using var db = new Database(Path.Combine(root, "state_5.sqlite"));
            db.Exec($"CREATE TABLE threads(id TEXT PRIMARY KEY,archived INTEGER); INSERT INTO threads VALUES('{id}',0)");
            var m = new ChatLifecycleMonitor(root, []); var x = Chat(ChatPlatform.Codex); var c = Chat(ChatPlatform.Claude);
            Check(!m.Poll([x,c], now).IsHidden(x), "active hidden");
            db.Exec("UPDATE threads SET archived=1");
            var hidden = m.Poll([x,c], now.AddSeconds(5));
            Check(hidden.IsHidden(x) && !hidden.IsHidden(c), "archive/platform mismatch");
            db.Exec("UPDATE threads SET archived=0");
            Check(!m.Poll([x,c], now.AddSeconds(10)).IsHidden(x), "unarchive not restored");
            Check(hidden.IsHidden(x), "published snapshot mutated after next poll");
        });
        Test("missing rows and missing logs never become lifecycle events", root =>
        {
            using var db = new Database(Path.Combine(root, "state_5.sqlite"));
            db.Exec("CREATE TABLE threads(id TEXT PRIMARY KEY,archived INTEGER)");
            var m = new ChatLifecycleMonitor(root, [Path.Combine(root,"desktop")]);
            Check(m.Poll([Chat(ChatPlatform.Codex),Chat(ChatPlatform.Claude)],now).Hidden.Count==0, "absence interpreted as deletion");
        });
        Test("missing row or unavailable new schema retains evidence without guessing", root =>
        {
            using var db = new Database(Path.Combine(root, "state_5.sqlite"));
            db.Exec($"CREATE TABLE threads(id TEXT PRIMARY KEY,archived INTEGER); INSERT INTO threads VALUES('{id}',1)");
            var x=Chat(ChatPlatform.Codex); var m=new ChatLifecycleMonitor(root,[]);
            Check(m.Poll([x],now).IsHidden(x),"initial archive missed");
            db.Exec("DELETE FROM threads");
            Check(m.Poll([x],now.AddSeconds(5)).IsHidden(x),"missing row restored chat");
            File.WriteAllText(Path.Combine(root,"state_6.sqlite"),"not a database");
            var failed=m.Poll([x],now.AddSeconds(35));
            Check(failed.IsHidden(x)&&failed.Warning is not null,"failure lost evidence or warning");
            var cold=new ChatLifecycleMonitor(root,[]).Poll([x],now);
            Check(cold.Hidden.Count==0&&cold.Warning is not null,"silently fell back to stale schema");
        });
        Test("Claude direct transcript tombstone hides after deletion even when log remains", root =>
        {
            var dir=SessionDir(root);var c=Chat(ChatPlatform.Claude);var x=Chat(ChatPlatform.Codex);
            var m=new ChatLifecycleMonitor(root,[Path.Combine(root,"desktop")]);
            Check(!m.Poll([c,x],now).IsHidden(c),"unmarked chat hidden");
            File.WriteAllText(Path.Combine(root,id+".jsonl"),"existing transcript");
            Tombstone(dir);Tombstone(dir,other);
            Check(m.Poll([c,x],now.AddSeconds(5)).IsHidden(c),"deletion missed");
            Check(!m.Poll([c,x],now.AddSeconds(6)).IsHidden(x),"deletion crossed platform");
            var cold=new ChatLifecycleMonitor(root,[Path.Combine(root,"desktop")]);
            Check(cold.Poll([c],now).IsHidden(c),"restart resurrected deleted chat");
            File.Delete(Path.Combine(dir,"deleted_"+id));
            Check(!m.Poll([c],now.AddSeconds(10)).IsHidden(c),"provider restore did not restore reminder");
        });
        Test("Claude malformed or future tombstones fail conservatively", root =>
        {
            var dir=SessionDir(root);var c=Chat(ChatPlatform.Claude);
            var m=new ChatLifecycleMonitor(root,[Path.Combine(root,"desktop")]);
            foreach(var value in new[]{"", "bad value", now.AddDays(1).ToUnixTimeMilliseconds().ToString()})
            {
                File.WriteAllText(Path.Combine(dir,"deleted_"+id),value);
                var result=m.Poll([c],now);Check(!result.IsHidden(c)&&result.Warning is not null,"invalid evidence hid chat");
            }
            Tombstone(dir);Check(m.Poll([c],now).IsHidden(c),"valid retry failed");
            File.WriteAllText(Path.Combine(dir,"deleted_"+id),"");
            Check(m.Poll([c],now).IsHidden(c),"partial write discarded known evidence");
        });
        Test("Claude metadata unavailable is not a restoration", root =>
        {
            var dir=SessionDir(root);var c=Chat(ChatPlatform.Claude);Tombstone(dir);
            var m=new ChatLifecycleMonitor(root,[Path.Combine(root,"desktop")]);m.Poll([c],now);
            Directory.Move(Path.Combine(root,"desktop"),Path.Combine(root,"offline"));
            var result=m.Poll([c],now.AddSeconds(5));
            Check(result.IsHidden(c)&&result.Warning is not null,"unavailable metadata resurrected chat");
            Directory.Move(Path.Combine(root,"offline"),Path.Combine(root,"desktop"));
            File.Delete(Path.Combine(dir,"deleted_"+id));
            Check(!m.Poll([c],now.AddSeconds(10)).IsHidden(c),"recovered metadata still stuck hidden");
        });
        Test("retained live and compact rows stay hidden while journal and usage are preserved", root =>
        {
            var dir=SessionDir(root);Tombstone(dir);var c=Chat(ChatPlatform.Claude);
            var compact= c with {Id=other,Compacted=true,CompactedAt=now,ActivityAt=now};Tombstone(dir,other);
            var journal=new ChatSessionHistory(root);journal.Capture([c,compact],now);journal.Flush(now);
            var saved=Directory.GetFiles(Path.Combine(root,"chat-sessions"),"*.json").ToDictionary(p=>p,File.ReadAllText);
            using var tokens=new TokenStore(Path.Combine(root,"tokens.sqlite"));
            tokens.Put(new("Claude","request",id,"model",now,10,100,20));
            var before=tokens.Sum(now.AddHours(-1),now);
            var state=new ChatLifecycleMonitor(root,[Path.Combine(root,"desktop")]).Poll([c,compact],now);
            Check(ChatListPolicy.Merge([c,compact],[c,compact],now,state).Count==0,"retained list resurrected hidden row");
            Check(journal.Current(now)!.Chats.Count==2&&saved.All(p=>File.ReadAllText(p.Key)==p.Value),"journal changed");
            Check(tokens.Sum(now.AddHours(-1),now)==before,"usage removed");
        });
    }

    public static void Probe()
    {
        var now=DateTimeOffset.Now;
        var live=ChatCacheMonitor.Local().Poll(now);
        var journal=new ChatSessionHistory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".quotawidget","data"));
        var retained=(journal.Current(now)?.Chats.Select(c=>c.Last)??[]).Concat(journal.RecentCompacted(now)).ToArray();
        var candidates=live.Concat(retained).DistinctBy(c=>ChatLifecycleSnapshot.Key(c.Platform,c.Id)).ToArray();
        var m=ChatLifecycleMonitor.Local();var samples=new List<double>();ChatLifecycleSnapshot result=ChatLifecycleSnapshot.Empty;
        for(var i=0;i<6;i++) {var clock=Stopwatch.StartNew();result=m.Poll(candidates,DateTimeOffset.Now);samples.Add(clock.Elapsed.TotalMilliseconds);}
        Console.WriteLine(JsonSerializer.Serialize(new {candidates=candidates.Length,hidden=result.Hidden.Count,
            platforms=candidates.Where(result.IsHidden).GroupBy(c=>c.Platform).ToDictionary(g=>g.Key.ToString(),g=>g.Count()),
            warning=result.Warning,pollMilliseconds=samples,
            removedFromMerged=ChatListPolicy.Merge(live,retained,now).Count-ChatListPolicy.Merge(live,retained,now,result).Count},
            new JsonSerializerOptions {WriteIndented=true}));
    }
}
