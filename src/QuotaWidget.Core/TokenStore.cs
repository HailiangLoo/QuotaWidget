using System.Runtime.InteropServices;
using System.Text.Json;

namespace QuotaWidget.Core;

public sealed record TokenSample(string Platform, string Key, string Chat, string? Model, DateTimeOffset At,
    long Input, long Cached, long Output, long? Written = null, long Reasoning = 0, bool Native = true, string? Turn = null);
public sealed record TokenSummary(long Input, long Cached, long Output, long Written, long Requests, long Conflicts, long MissingWrites = 0)
{
    public static readonly TokenSummary Empty = new(0, 0, 0, 0, 0, 0);
    public static string Number(long n) => n >= 1_000_000_000 ? (n / 1_000_000_000d).ToString("0.##") + "B"
        : n >= 1_000_000 ? (n / 1_000_000d).ToString("0.#") + "M"
        : n >= 1000 ? (n / 1000d).ToString("0.#") + "k" : n.ToString();
    public string Line => Requests == 0 ? "IN —   CACHE —   OUT —" : $"IN {Number(Input)}   CACHE {Number(Cached)}   OUT {Number(Output)}";
}

/// <summary>Numeric-only durable index. Windows' bundled SQLite; no service, network or NuGet runtime.</summary>
public sealed class TokenStore : IDisposable
{
    readonly MiniSqlite _db;
    readonly bool _hasParents;
    readonly bool _hasWorkEvents;
    DateTimeOffset? _scope;
    public TokenStore(string path, bool readOnly = false)
    {
        _db = new(path, readOnly);
        try
        {
        if (!readOnly) _db.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA cache_size=-2048; " +
            "CREATE TABLE IF NOT EXISTS requests(platform TEXT,key TEXT,chat TEXT,model TEXT,t INTEGER,last_t INTEGER,i INTEGER,c INTEGER,o INTEGER,w INTEGER,r INTEGER,native INTEGER,turn_id TEXT,conflict INTEGER DEFAULT 0,PRIMARY KEY(platform,key));" +
            "CREATE INDEX IF NOT EXISTS requests_time ON requests(t); CREATE INDEX IF NOT EXISTS requests_chat ON requests(platform,chat,t);" +
            "CREATE TABLE IF NOT EXISTS sources(path TEXT PRIMARY KEY,state TEXT); CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT);"+
            "CREATE TABLE IF NOT EXISTS chat_parents(platform TEXT,chat TEXT,parent TEXT,conflict INTEGER DEFAULT 0,PRIMARY KEY(platform,chat));"+
            "CREATE TABLE IF NOT EXISTS work_events(platform TEXT,stream TEXT,t INTEGER,kind TEXT,id TEXT,model TEXT,PRIMARY KEY(platform,stream,t,kind,id));"+
            "CREATE INDEX IF NOT EXISTS work_events_time ON work_events(platform,t);");
        _hasParents=_db.Query("SELECT 1 FROM sqlite_master WHERE type='table' AND name='chat_parents'").Count>0;
        _hasWorkEvents=_db.Query("SELECT 1 FROM sqlite_master WHERE type='table' AND name='work_events'").Count>0;
        if(!readOnly && Meta("write-schema")!="2")
        {
            // Earlier development probes did not distinguish missing cache-write counts.
            // Their optional breakdown is unknown; primary IN/CACHE/OUT remain valid.
            _db.Exec("UPDATE requests SET w=NULL WHERE platform='Codex'"); SetMeta("write-schema","2");
        }
        if(DateTimeOffset.TryParse(Meta("since"),out var since)) _scope=since;
        }
        catch { _db.Dispose(); throw; }
    }
    public string? Meta(string key) => _db.Query("SELECT value FROM meta WHERE key=?", key).FirstOrDefault()?.FirstOrDefault() as string;
    public void SetMeta(string key, string value)
    {
        _db.Run("INSERT OR REPLACE INTO meta VALUES(?,?)", key, value);
        if(key=="since" && DateTimeOffset.TryParse(value,out var since)) _scope=since;
    }
    public void Begin() => _db.Exec("BEGIN IMMEDIATE");
    public void Commit() => _db.Exec("COMMIT");
    public void Rollback() => _db.Exec("ROLLBACK");
    public Dictionary<string, TokenCursor> Sources() => _db.Query("SELECT path,state FROM sources").Select(row =>
        (Path: (string)row[0]!, State: JsonSerializer.Deserialize<TokenCursor>((string)row[1]!)))
        .Where(x => x.State is not null).ToDictionary(x => x.Path, x => x.State!, StringComparer.OrdinalIgnoreCase);
    public TokenCursor? Source(string path) => _db.Query("SELECT state FROM sources WHERE path=?",path).FirstOrDefault() is { } row
        ? JsonSerializer.Deserialize<TokenCursor>((string)row[0]!) : null;
    public void SaveSource(string path, TokenCursor state) => _db.Run("INSERT OR REPLACE INTO sources VALUES(?,?)", path, JsonSerializer.Serialize(state));
    public IReadOnlyList<string> ChatIds(string platform)=>_db.Query("SELECT DISTINCT chat FROM requests WHERE platform=?",platform).Select(r=>(string)r[0]!).ToArray();
    public bool SetChatParent(string platform,string child,string parent)
    {
        if(platform!="Codex"||ChatOwnership.Id(child) is not { } c||ChatOwnership.Id(parent) is not { } p) return false;
        var old=_db.Query("SELECT parent,conflict FROM chat_parents WHERE platform=? AND chat=?",platform,c).FirstOrDefault();
        if(old is not null&&((long)old[1]! != 0||(string)old[0]! == p)) return false;
        // Keep the first evidence and flag contradictions; never silently move usage between chats.
        _db.Run("INSERT INTO chat_parents(platform,chat,parent) VALUES(?,?,?) ON CONFLICT(platform,chat) DO UPDATE SET conflict=1",platform,c,p);
        return true;
    }
    public IReadOnlyDictionary<string,string> Parents(string platform)=>!_hasParents?new Dictionary<string,string>():
        _db.Query("SELECT chat,parent,conflict FROM chat_parents WHERE platform=?",platform)
            .ToDictionary(r=>(string)r[0]!,r=>(long)r[2]! == 0?(string)r[1]!:(string)r[0]!,StringComparer.Ordinal);
    public bool Put(TokenSample s, bool existingOnly = false)
    {
        if (!s.Native && _db.Query("SELECT 1 FROM requests WHERE platform=? AND chat=? AND turn_id=? AND native=1 LIMIT 1", s.Platform,s.Chat,s.Turn).Count>0) return false;
        // Stable response/request IDs dedupe copies across files and forks. Counters are
        // monotonic within a response; stream updates replace, never add a second request.
        var old = _db.Query("SELECT i,c,o,w,r,chat,t,model FROM requests WHERE platform=? AND key=?", s.Platform, s.Key).FirstOrDefault();
        if(old is null && existingOnly) return false;
        if (old is not null && (long)old[0]! == s.Input && (long)old[1]! == s.Cached && (long)old[2]! == s.Output && Equals(old[3],s.Written) && (long)old[4]! == s.Reasoning && (long)old[6]!<=s.At.ToUnixTimeMilliseconds() && (old[7] is not null || s.Model is null)) return false;
        _db.Run("INSERT INTO requests(platform,key,chat,model,t,last_t,i,c,o,w,r,native,turn_id) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?) " +
            "ON CONFLICT(platform,key) DO UPDATE SET t=MIN(t,excluded.t),model=COALESCE(model,excluded.model),last_t=MAX(last_t,excluded.last_t),o=MAX(o,excluded.o),r=MAX(r,excluded.r),w=COALESCE(w,excluded.w)," +
            "conflict=MAX(conflict,CASE WHEN i<>excluded.i OR c<>excluded.c OR (w IS NOT NULL AND excluded.w IS NOT NULL AND w<>excluded.w) THEN 1 ELSE 0 END)",
            s.Platform, s.Key, s.Chat, s.Model, s.At.ToUnixTimeMilliseconds(), s.At.ToUnixTimeMilliseconds(), s.Input, s.Cached, s.Output, s.Written, s.Reasoning, s.Native ? 1L : 0L, s.Turn);
        return true;
    }
    // When native per-response data exists for a turn, discard the counter fallback for
    // that turn. Never count both protocols. It is safe to replay after a cursor reset.
    public void PreferNative(string chat, string turn) => _db.Run("DELETE FROM requests WHERE platform='Codex' AND chat=? AND native=0 AND turn_id=?", chat, turn);
    public TokenSummary Sum(DateTimeOffset start, DateTimeOffset end, string? platform = null, string? chat = null)
    {
        if(_scope is { } scope && start<scope) start=scope;
        var where="t>=? AND t<=?"; var args=new List<object?> {start.ToUnixTimeMilliseconds(),end.ToUnixTimeMilliseconds()};
        if(platform is not null) {where+=" AND platform=?";args.Add(platform);}
        if(chat is not null)
        {
            if(platform is not null&&_hasParents)
            {
                // Same explicit family as the platform breakdown; raw request attribution is unchanged.
                var parents=Parents(platform);
                var family=parents.Keys.Where(c=>c!=chat&&ChatOwnership.BelongsTo(c,chat,parents)).Append(chat).ToArray();
                where+=" AND chat IN ("+string.Join(',',family.Select(_=>"?"))+")";args.AddRange(family);
            }
            else {where+=" AND chat=?";args.Add(chat);}
        }
        var row = _db.Query("SELECT COALESCE(SUM(i),0),COALESCE(SUM(c),0),COALESCE(SUM(o),0),COALESCE(SUM(w),0),COUNT(*),COALESCE(SUM(conflict),0),COALESCE(SUM(CASE WHEN w IS NULL THEN 1 ELSE 0 END),0) FROM requests WHERE "+where,args.ToArray())[0];
        return new((long)row[0]!, (long)row[1]!, (long)row[2]!, (long)row[3]!, (long)row[4]!, (long)row[5]!, (long)row[6]!);
    }
    public TokenBreakdown Breakdown(DateTimeOffset start,DateTimeOffset end,string platform)
    {
        if(_scope is { } scope&&start<scope) start=scope;
        // One SELECT is a consistent snapshot for totals and both groupings while import continues.
        var rows=_db.Query("SELECT COALESCE(NULLIF(TRIM(model),''),'未标注模型'),COALESCE(chat,''),SUM(i),SUM(c),SUM(o),COALESCE(SUM(w),0),COUNT(*),SUM(conflict),SUM(CASE WHEN w IS NULL THEN 1 ELSE 0 END) FROM requests WHERE platform=? AND t>=? AND t<=? GROUP BY 1,2",platform,start.ToUnixTimeMilliseconds(),end.ToUnixTimeMilliseconds());
        return TokenBreakdown.Build(start,end,rows.Select(r=>new TokenSlice((string)r[0]!, (string)r[1]!,
            new TokenSummary((long)r[2]!, (long)r[3]!, (long)r[4]!, (long)r[5]!, (long)r[6]!, (long)r[7]!, (long)r[8]!))).ToArray(),Parents(platform));
    }
    public IReadOnlyList<ModelActivity> ModelActivity(DateTimeOffset start,DateTimeOffset end,string platform)
    {
        if(_scope is { } scope&&start<scope)start=scope;
        var rows=_db.Query("SELECT t,last_t,model,conflict FROM requests WHERE platform=? AND COALESCE(last_t,t)>=? AND t<=? ORDER BY t LIMIT 20001",platform,start.ToUnixTimeMilliseconds(),end.ToUnixTimeMilliseconds());
        if(rows.Count>20000)return []; // too much history to classify cheaply: retain both curves
        return rows.Select(r=>new ModelActivity(DateTimeOffset.FromUnixTimeMilliseconds((long)r[0]!),DateTimeOffset.FromUnixTimeMilliseconds(Math.Max((long)r[0]!,r[1] is long last?last:(long)r[0]!)),(long)r[3]! == 0?r[2] as string:null)).ToArray();
    }
    public IReadOnlyList<QuotaToken>? QuotaTokens(DateTimeOffset start,DateTimeOffset end,string platform)
    {
        if(_scope is {} scope&&start<scope)start=scope;
        var rows=_db.Query("SELECT t,last_t,chat,model,i,c,o,conflict FROM requests WHERE platform=? AND t>=? AND t<=? ORDER BY t LIMIT 50001",platform,start.ToUnixTimeMilliseconds(),end.ToUnixTimeMilliseconds());
        if(rows.Count>50000)return null;
        var parents=Parents(platform);
        return rows.Select(r=>new QuotaToken(DateTimeOffset.FromUnixTimeMilliseconds((long)r[0]!),
            DateTimeOffset.FromUnixTimeMilliseconds(r[1] is long last?last:(long)r[0]!),ChatOwnership.Root((string)r[2]!,parents),
            r[3] as string??"",(long)r[4]!, (long)r[5]!, (long)r[6]!, (long)r[7]!!=0)).ToArray();
    }
    public bool PutWorkEvent(string platform,WorkEvent e)
    {
        _db.Run("INSERT OR IGNORE INTO work_events VALUES(?,?,?,?,?,?)",platform,e.Stream,e.At.ToUnixTimeMilliseconds(),e.Kind,e.Id??"",e.Model);
        return (long)_db.Query("SELECT changes()")[0][0]!>0;
    }
    public IReadOnlyList<WorkSpan> WorkActivitySpans(DateTimeOffset end,string platform) =>
        WorkActivity.Build(WorkActivityEvents(end,platform),end);

    public IReadOnlyList<WorkEvent> WorkActivityEvents(DateTimeOffset end,string platform)
    {
        if(!_hasWorkEvents)return [];
        // Match the live quota history's eight-day horizon. A truncated leading turn has
        // no known start; never promote the first response we happen to retain to a start.
        var rows=_db.Query("SELECT stream,t,kind,id,model FROM work_events WHERE platform=? AND t>=? AND t<=? ORDER BY stream,t LIMIT 200001",platform,end.Add(-WidgetModel.Lookback).ToUnixTimeMilliseconds(),end.ToUnixTimeMilliseconds());
        if(rows.Count>200000)return [];
        return rows.Select(r=>new WorkEvent((string)r[0]!,DateTimeOffset.FromUnixTimeMilliseconds((long)r[1]!),
            (string)r[2]!,r[3] is string {Length:>0} id?id:null,r[4] as string)).ToArray();
    }
    public void Dispose() => _db.Dispose();
}

sealed class MiniSqlite : IDisposable
{
    IntPtr _db;
    public MiniSqlite(string path, bool readOnly)
    {
        if (sqlite3_open_v2(path, out _db, (readOnly ? 1 : 6) | 0x10000, IntPtr.Zero) != 0) { var error=Error();Dispose();throw error; }
        sqlite3_busy_timeout(_db, 1500);
    }
    Exception Error() => new IOException("Local token index: " + Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)));
    public void Exec(string sql)
    {
        var code = sqlite3_exec(_db, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (error != IntPtr.Zero) sqlite3_free(error);
        if (code != 0) throw Error();
    }
    public void Run(string sql, params object?[] values) => Query(sql, values);
    public List<object?[]> Query(string sql, params object?[] values)
    {
        if (sqlite3_prepare_v2(_db, sql, -1, out var stmt, IntPtr.Zero) != 0) throw Error();
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var code = values[i] switch
                {
                    null => sqlite3_bind_null(stmt, i + 1),
                    string s => sqlite3_bind_text(stmt, i + 1, s, -1, new IntPtr(-1)),
                    _ => sqlite3_bind_int64(stmt, i + 1, Convert.ToInt64(values[i])),
                };
                if (code != 0) throw Error();
            }
            var result = new List<object?[]>(); int step;
            while ((step = sqlite3_step(stmt)) == 100)
            {
                var row = new object?[sqlite3_column_count(stmt)];
                for (var i = 0; i < row.Length; i++) row[i] = sqlite3_column_type(stmt, i) switch { 1 => sqlite3_column_int64(stmt, i), 3 => Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, i)), _ => null };
                result.Add(row);
            }
            if (step != 101) throw Error();
            return result;
        }
        finally { sqlite3_finalize(stmt); }
    }
    public void Dispose() { if (_db != IntPtr.Zero) { sqlite3_close_v2(_db); _db = IntPtr.Zero; } }
    const string Dll = "winsqlite3.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr cb, IntPtr arg, out IntPtr error);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void sqlite3_free(IntPtr p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_null(IntPtr stmt, int i);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_int64(IntPtr stmt, int i, long n);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_text(IntPtr stmt, int i, [MarshalAs(UnmanagedType.LPUTF8Str)] string s, int n, IntPtr destructor);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
}
