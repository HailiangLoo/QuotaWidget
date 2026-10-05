using System.Text;
using System.Text.Json;
using QuotaWidget.Core;

static class TokenTests
{
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        var now=DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        void Check(bool yes,string why) {if(!yes) throw new Exception(why);}
        void Test(string name,Action body)=>tests.Add(("tokens: "+name,()=>{body();return Task.CompletedTask;}));
        void Temp(Action<string> body)
        {
            var path=Path.Combine(Path.GetTempPath(),"qw-token-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
            try {body(path);} finally {Directory.Delete(path,true);}
        }
        string Row(string type,object payload)=>JsonSerializer.Serialize(new {type,timestamp=now.AddMinutes(-1).ToString("O"),payload});
        object Usage(long i=100,long c=80,long o=12)=>new {input_tokens=i,cached_input_tokens=c,output_tokens=o,reasoning_output_tokens=7};
        string Native(string key="r1",long output=12)=>Row("token_usage_record",new {turn_id="t1",thread_id="chat",response_id=key,usage=Usage(o:output)});
        string Counter(long total=100,long output=12)=>Row("event_msg",new {type="token_count",info=new {last_token_usage=Usage(o:output),total_token_usage=Usage(i:total,c:total*8/10,o:output)}});
        TokenCursor Cursor()=>new() {Platform="Codex",Chat="chat",Turn="t1"};
        void Read(string line,TokenCursor cursor,TokenStore store)=>TokenParser.Read(line,cursor,store,now.AddDays(-1),now);
        TokenSummary Sum(TokenStore s)=>s.Sum(now.AddDays(-1),now);
        Test("model and chat breakdowns conserve totals with account range and platform isolation",()=>Temp(root=>
        {
            using var db=new TokenStore(Path.Combine(root,"group.db"));db.SetMeta("since",now.AddHours(-1).ToString("O"));
            db.Put(new("Codex","same","shared","model-a",now.AddMinutes(-5),10,80,3));
            db.Put(new("Codex","b","shared","model-b",now.AddMinutes(-4),20,40,7));
            db.Put(new("Codex","c","other",null,now.AddMinutes(-3),30,60,9));
            db.Put(new("Codex","old","old","model-a",now.AddHours(-2),999,999,999));
            db.Put(new("Claude","same","shared","model-a",now.AddMinutes(-5),1000,8000,300));
            var grouped=db.Breakdown(now.AddDays(-1),now,"Codex");
            Check(grouped.Total==db.Sum(now.AddDays(-1),now,"Codex"),"breakdown total differs from platform table");
            Check(grouped.Start==now.AddHours(-1)&&grouped.Models.Count==3&&grouped.Chats.Count==2,"range or unknown model grouping wrong");
            var chat=grouped.Chats.Single(g=>g.Key=="shared");Check(chat.Members==2&&chat.Usage.Input==30&&chat.Usage.Cached==120&&chat.Usage.Output==10,"multi-model chat duplicated or leaked platforms");
            foreach(var rows in new[]{grouped.Models,grouped.Chats})
                Check(rows.Sum(r=>r.Usage.Input)==grouped.Total.Input&&rows.Sum(r=>r.Usage.Cached)==grouped.Total.Cached&&rows.Sum(r=>r.Usage.Output)==grouped.Total.Output&&rows.Sum(r=>r.Usage.Requests)==grouped.Total.Requests,"grouping failed conservation");
            Check(grouped.Models.Any(g=>g.Key=="未标注模型"),"missing model guessed");
            Check(db.Breakdown(now.AddMinutes(-2),now,"Codex").Chats.Count==0,"out-of-range chat retained");
        }));
        Test("breakdown lists all chats without a top-N truncation and supports read-only access",()=>Temp(root=>
        {
            var path=Path.Combine(root,"group.db");
            using(var db=new TokenStore(path)) for(var i=0;i<80;i++) db.Put(new("Codex","r"+i,"chat"+i,i%2==0?"A":"B",now,i+1,2,3));
            using var read=new TokenStore(path,readOnly:true);var grouped=read.Breakdown(now.AddHours(-1),now,"Codex");
            Check(grouped.Chats.Count==80&&grouped.Models.Count==2&&grouped.Chats[0].Key=="chat79","complete ordering lost");
            Check(grouped.Total.Requests==80&&grouped.Total.Input==3240&&grouped.Models.Sum(m=>m.Members)==80,"aggregate wrong");
        }));

        Test("native replaces counters and duplicate stream updates count once",()=>Temp(root=>
        {
            using var db=new TokenStore(Path.Combine(root,"t.db"));var c=Cursor();
            Read(Counter(),c,db); Read(Native(),c,db); Read(Native(output:20),c,db); Read(Counter(output:20),c,db);
            var sum=Sum(db); Check(sum.Requests==1&&sum.Input==20&&sum.Cached==80&&sum.Output==20,"double counted protocols or reasoning");
            Check(sum.MissingWrites==1,"missing optional cache-write field was silently zero");
            Read(Native(),Cursor(),db); Check(Sum(db)==sum,"replay regressed output");
        }));
        Test("legacy counter echoes at the next turn do not create a new request",()=>Temp(root=>
        {
            using var db=new TokenStore(Path.Combine(root,"t.db"));var c=Cursor();
            Read(Counter(),c,db); Read(Counter(output:20),c,db);
            Read(Row("event_msg",new {type="task_started",turn_id="t2"}),c,db); Read(Counter(output:20),c,db);
            Check(Sum(db).Requests==1&&Sum(db).Output==20,"counted the previous turn echo");
            Read(Counter(total:200),c,db); Check(Sum(db).Requests==2,"lost next request");
            Read(Counter(total:100),c,db); Check(Sum(db).Requests==3,"counter reset reused old key");
        }));
        Test("duplicate native responses across a fork have one identity",()=>Temp(root=>
        {
            using var db=new TokenStore(Path.Combine(root,"t.db"));
            Read(Native(),Cursor(),db); Read(Native(),new(){Platform="Codex",Chat="fork",Turn="t1"},db);
            Check(Sum(db).Requests==1,"fork replay doubled usage");
        }));
        Test("Claude distinguishes cache write from hit and updates one request",()=>Temp(root=>
        {
            using var db=new TokenStore(Path.Combine(root,"t.db"));var c=new TokenCursor{Platform="Claude",Chat="parent"};
            string Claude(long output)=>JsonSerializer.Serialize(new {type="assistant",timestamp=now.ToString("O"),sessionId="parent",requestId="req",message=new {id="m",model="fable",content="PRIVATE_BODY_DO_NOT_STORE",usage=new {input_tokens=2,cache_read_input_tokens=900,cache_creation_input_tokens=98,output_tokens=output}}});
            Read(Claude(10),c,db);Read(Claude(20),c,db);Read(Claude(20),c,db);
            var sum=Sum(db);Check(sum.Requests==1&&sum.Input==100&&sum.Cached==900&&sum.Written==98&&sum.Output==20,"Claude totals wrong");
            db.Dispose();
            Check(!File.ReadAllText(Path.Combine(root,"t.db"),Encoding.Latin1).Contains("PRIVATE_BODY"),"body persisted");
        }));
        Test("missing, negative and future counts are not invented zeros",()=>Temp(root=>
        {
            using var db=new TokenStore(Path.Combine(root,"t.db"));var c=Cursor();
            Read(Row("token_usage_record",new {response_id="bad",usage=new {input_tokens=-1,cached_input_tokens=0,output_tokens=1}}),c,db);
            Read(Row("token_usage_record",new {response_id="missing",usage=new {input_tokens=100,output_tokens=1}}),c,db);
            Read(JsonSerializer.Serialize(new {type="token_usage_record",timestamp=now.AddDays(1).ToString("O"),payload=new {response_id="future",usage=Usage()}}),c,db);
            Check(Sum(db).Requests==0&&c.Skipped==2,"invalid usage accepted");
        }));
        Test("conflicting inputs are flagged, never silently added",()=>Temp(root=>
        {
            using var db=new TokenStore(Path.Combine(root,"t.db"));
            db.Put(new("Codex","r","chat",null,now,20,80,10)); db.Put(new("Codex","r","chat",null,now,30,90,11));
            var sum=Sum(db);Check(sum.Conflicts==1&&sum.Input==20&&sum.Cached==80&&sum.Output==11,"conflict lost");
        }));
        Test("transactions and byte cursors survive restart; rollback keeps counts unchanged",()=>Temp(root=>
        {
            var file=Path.Combine(root,"t.db");
            using(var db=new TokenStore(file))
            {
                db.Begin();db.Put(new("Codex","r","chat",null,now,20,80,10));db.SaveSource("log",new(){Offset=123});db.Commit();
                db.Begin();db.Put(new("Codex","bad","chat",null,now,20,80,10));db.SaveSource("log",new(){Offset=999});db.Rollback();
            }
            using(var db=new TokenStore(file)) Check(Sum(db).Requests==1&&db.Source("log")?.Offset==123,"crash transaction lost consistency");
            using(var db=new TokenStore(file,readOnly:true)) Check(Sum(db).Requests==1,"read-only snapshot changed data");
        }));
        Test("incremental reader finishes partial tails and replay after truncation is idempotent",()=>Temp(root=>
        {
            var logs=Path.Combine(root,"cx","sessions"); Directory.CreateDirectory(logs);
            var file=Path.Combine(logs,"chat.jsonl"); var json=Native(); File.WriteAllText(file,json[..30]);
            var dir=Path.Combine(root,"index");
            using(var index=new TokenIndex(dir,Path.Combine(root,"cx"),Path.Combine(root,"cl"),now))
            {
                index.Poll(now);Check(index.Sum(now.AddDays(-1),now).Requests==0,"partial tail counted");
                File.AppendAllText(file,json[30..]+"\n");index.Poll(now);Check(index.Sum(now.AddDays(-1),now).Requests==1,"tail not resumed");
                index.Poll(now);Check(index.LastReadBytes==0,"unchanged file re-read");
            }
            using(var index=new TokenIndex(dir,Path.Combine(root,"cx"),Path.Combine(root,"cl"),now))
            {
                index.Poll(now);Check(index.LastReadBytes==0&&index.Sum(now.AddDays(-1),now).Requests==1,"restart re-read or lost data");
                File.WriteAllText(file,json+"\n"); File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddSeconds(2)); index.Poll(now);
                Check(index.Sum(now.AddDays(-1),now).Requests==1,"replacement duplicate counted");
                File.AppendAllText(file,Native("r2")+"\n");index.Poll(now);Check(index.Sum(now.AddDays(-1),now).Requests==2,"append lost");
            }
        }));
        Test("oversize bodies are skipped under a byte budget, later usage is recovered",()=>Temp(root=>
        {
            var logs=Path.Combine(root,"cx","sessions");Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs,"large.jsonl"),new string('x',3*1024*1024)+"\n"+Native()+"\n");
            using var index=new TokenIndex(Path.Combine(root,"idx"),Path.Combine(root,"cx"),Path.Combine(root,"cl"),now);
            for(var i=0;i<3;i++) {index.Poll(now);Check(index.LastReadBytes<=8*1024*1024,"unbounded read");}
            Check(index.Skipped>0&&index.Sum(now.AddDays(-1),now).Requests==1,"oversize recovery failed");
        }));
        Test("time range and chat grouping exclude unrelated usage",()=>Temp(root=>
        {
            using var db=new TokenStore(Path.Combine(root,"t.db"));
            db.Put(new("Codex","a","chat",null,now.AddHours(-2),1,2,3));db.Put(new("Claude","b","other",null,now,4,5,6));
            Check(db.Sum(now.AddHours(-1),now).Input==4,"range ignored");
            Check(db.Sum(now.AddDays(-1),now,"Codex","chat").Input==1,"chat mixed");
            Check(db.Sum(now.AddDays(-1),now,"Codex","unknown").Requests==0,"missing became another chat");
        }));
        Test("platform totals and same-ID chat cards remain isolated",()=>Temp(root=>
        {
            using var db=new TokenStore(Path.Combine(root,"platforms.db"));
            db.Put(new("Claude","same-request","same-chat",null,now,10,20,30));
            db.Put(new("Codex","same-request","same-chat",null,now,100,200,300));
            db.Put(new("Codex","other","other-chat",null,now,1000,2000,3000));
            var claude=db.Sum(now.AddHours(-1),now,"Claude"); var codex=db.Sum(now.AddHours(-1),now,"Codex");
            Check(claude.Input==10&&claude.Cached==20&&claude.Output==30,"Claude includes other platform");
            Check(codex.Input==1100&&codex.Cached==2200&&codex.Output==3300,"Codex platform aggregate wrong");
            var chat=db.Sum(now.AddDays(-1),now,"Codex","same-chat");
            Check(chat.Input==100&&chat.Cached==200&&chat.Output==300,"chat card leaks same-ID platform or unrelated chat");
            Check(db.Sum(now.AddHours(-1),now).Requests==claude.Requests+codex.Requests,"platform split lost requests");
        }));
        Test("recent tail is visible before long backfill and is not counted twice",()=>Temp(root=>
        {
            var logs=Path.Combine(root,"cx","sessions");Directory.CreateDirectory(logs);
            var body=string.Concat(Enumerable.Repeat("{\"type\":\"ordinary\",\"body\":\""+new string('x',10000)+"\"}\n",600));
            File.WriteAllText(Path.Combine(logs,"large.jsonl"),body+Native()+"\n");
            using var index=new TokenIndex(Path.Combine(root,"idx"),Path.Combine(root,"cx"),Path.Combine(root,"cl"),now);
            index.Poll(now);Check(index.Sum(now.AddDays(-1),now).Requests==1&&index.PendingFiles>0,"hot tail waited for cold backfill");
            for(var i=0;i<10&&index.PendingFiles>0;i++)index.Poll(now);
            Check(index.PendingFiles==0&&index.Sum(now.AddDays(-1),now).Requests==1,"backfill duplicate counted");
        }));
        Test("late replay restores original accounting time without adding a second request",()=>Temp(root=>
        {
            using var db=new TokenStore(Path.Combine(root,"t.db"));db.SetMeta("since",now.AddDays(-1).ToString("O"));
            var s=new TokenSample("Codex","r","chat",null,now,20,80,10);
            db.Put(s);db.Put(s with {At=now.AddHours(-2),Model="model"});
            Check(db.Sum(now.AddHours(-1),now).Requests==0&&Sum(db).Requests==1,"request attributed to last echo");
            db.Put(s with {At=now.AddDays(-2)},existingOnly:true);
            Check(db.Sum(DateTimeOffset.UnixEpoch,now).Requests==0,"pre-scope request leaked into all");
        }));
    }
}
