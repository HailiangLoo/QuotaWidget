using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuotaWidget.Core;

// Persist only metadata/counters, never partial JSON or message bodies.
public sealed class TokenCursor
{
    public int ActivityVersion { get; set; }
    public string ActivityStream { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Chat { get; set; } = "";
    public string? Model { get; set; }
    public string Turn { get; set; } = "unknown";
    public string Epoch { get; set; } = "initial";
    public bool NativeTurn { get; set; }
    public bool Excluded { get; set; }
    public long LastInput { get; set; } = -1;
    public long LastCached { get; set; } = -1;
    public string? LastKey { get; set; }
    public long Offset { get; set; }
    public long CreatedTicks { get; set; }
    public long Length { get; set; }
    public long WriteTicks { get; set; }
    public bool Skipping { get; set; }
    public int Skipped { get; set; }
    public bool TailSeeded { get; set; }
    public bool NativeOnly { get; set; }
    public TokenCursor? Recent { get; set; }
}

public static class TokenParser
{
    static JsonElement O(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) ? v : default;
    static string? S(JsonElement e, string n) => O(e, n) is { ValueKind: JsonValueKind.String } v && v.GetString() is {Length: >0 and <=256} s ? s : null;
    static long? N(JsonElement e, string n) => O(e, n) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var x) && x is >= 0 and < 1_000_000_000_000 ? x : null;
    public static bool Read(string json, TokenCursor c, TokenStore store, DateTimeOffset since, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json); var r = doc.RootElement;
            var type = S(r, "type"); var p = O(r, "payload");
            if (c.Platform == "Codex" && type == "session_meta")
            {
                c.Chat = S(p, "id") ?? c.Chat;
                c.Excluded = S(p, "model_provider") is { } provider && provider != "openai";
                return !c.Excluded&&ChatOwnership.CodexParent(O(p,"source")) is { } parent&&store.SetChatParent("Codex",c.Chat,parent);
            }
            if (c.Platform == "Codex" && type == "turn_context")
            {
                c.Model = S(p, "model") ?? c.Model;
                if (S(p, "turn_id") is { } turn && turn != c.Turn) { c.Turn = turn; c.NativeTurn = false; }
                return false;
            }
            if (c.Platform == "Codex" && type == "event_msg" && S(p, "type") == "task_started")
            {
                if (S(p, "turn_id") is { } turn && turn != c.Turn) { c.Turn = turn; c.NativeTurn = false; }
                return false;
            }
            if (c.Excluded || !DateTimeOffset.TryParse(S(r, "timestamp"), out var at) || at > now.AddMinutes(1)) return false;
            TokenSample? sample = null;
            if (c.Platform == "Claude" && type == "assistant")
            {
                var message = O(r, "message"); var u = O(message, "usage");
                var key = S(r, "requestId") ?? S(message, "id");
                c.Chat = S(r, "sessionId") ?? c.Chat;
                if (key is null) return false;
                if (N(u,"input_tokens") is not { } input || N(u,"cache_read_input_tokens") is not { } cached || N(u,"cache_creation_input_tokens") is not { } written || N(u,"output_tokens") is not { } output)
                { if (u.ValueKind == JsonValueKind.Object) c.Skipped++; return false; }
                sample = new("Claude", key, c.Chat, S(message,"model"), at, input + written, cached, output, written);
            }
            else if (c.Platform == "Codex" && type == "token_usage_record")
            {
                var u = O(p, "usage"); var key = S(p,"response_id");
                if (key is null) return false;
                sample = Codex(u, key, true);
                if (sample is not null)
                {
                    var turn = S(p,"turn_id") ?? c.Turn;
                    sample = sample with { Chat = S(p,"thread_id") ?? c.Chat, Turn=turn };
                    if (turn == c.Turn) c.NativeTurn = true;
                    if(at>=since) store.PreferNative(sample.Chat, turn);
                }
            }
            else if (c.Platform == "Codex" && type == "event_msg" && S(p,"type") == "token_count")
            {
                if(c.NativeOnly) return false;
                var info = O(p,"info"); var total = O(info,"total_token_usage");
                if (N(total,"input_tokens") is not { } totalIn || N(total,"cached_input_tokens") is not { } totalCache) return false;
                if (totalIn < c.LastInput || totalCache < c.LastCached) c.Epoch = at.ToUnixTimeMilliseconds().ToString();
                var same = totalIn == c.LastInput && totalCache == c.LastCached;
                c.LastInput = totalIn; c.LastCached = totalCache;
                if (c.NativeTurn) { c.LastKey = null; return false; }
                if (same && c.LastKey is null) return false;
                var key = same ? c.LastKey! : $"fallback:{c.Chat}:{c.Turn}:{c.Epoch}:{totalIn}:{totalCache}";
                sample = Codex(O(info,"last_token_usage"), key, false);
                if (sample is not null) c.LastKey = key;
            }
            if (sample is null) return false;
            return store.Put(sample,existingOnly:at<since);

            TokenSample? Codex(JsonElement u, string key, bool native)
            {
                if (N(u,"input_tokens") is not { } input || N(u,"cached_input_tokens") is not { } cached || N(u,"output_tokens") is not { } output || cached > input)
                { c.Skipped++; return null; }
                return new("Codex", key, c.Chat, c.Model, at, input - cached, cached, output, N(u,"cache_write_input_tokens"), N(u,"reasoning_output_tokens") ?? 0, native, c.Turn);
            }
        }
        catch (JsonException) { c.Skipped++; return false; }
    }
}

/// <summary>Bounded background import, resumable byte cursors, local logs only.</summary>
public sealed class TokenIndex : IDisposable
{
    readonly object _gate = new();
    readonly TokenStore _store;
    readonly string _codexHome, _claudeHome;
    readonly Dictionary<string, TokenCursor> _sources;
    string[] _active = [];
    DateTimeOffset _discovered;
    DateTimeOffset _parentsRead;
    string? _parentWarning;
    (bool Claude,bool Codex)? _monitoring;
    // Publish complete lifecycle generations. A partial log import is not an empty
    // work history and must never replace the evidence used by the rate estimator.
    readonly Dictionary<string,IReadOnlyList<WorkEvent>> _publishedWork = new();
    readonly HashSet<string> _dirtyWork = new();
    int _next;
    public DateTimeOffset Since { get; }
    public long Version { get; private set; }
    public long LastReadBytes { get; private set; }
    public int PendingFiles { get; private set; }
    public bool LimitedDiscovery { get; private set; }
    public int Skipped { get { lock(_gate) return _sources.Values.Sum(c => c.Skipped); } }
    public TokenIndex(string root, string codexHome, string claudeHome, DateTimeOffset now)
    {
        Directory.CreateDirectory(root);
        _store = new(Path.Combine(root,"tokens.sqlite")); _codexHome = codexHome; _claudeHome = claudeHome;
        if (!DateTimeOffset.TryParse(_store.Meta("since"), out var since))
        { since = now.AddDays(-1); _store.SetMeta("since", since.ToString("O")); }
        Since = since; _sources = new(StringComparer.OrdinalIgnoreCase);
        foreach(var group in _store.Sources().Values.GroupBy(s=>s.Platform))
            if(group.All(s=>s.ActivityVersion>=1&&s.Offset>=s.Length))
                _publishedWork[group.Key]=_store.WorkActivityEvents(now,group.Key);
    }
    public static TokenIndex Local(string root, DateTimeOffset now) => new(root,
        Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude"), now);
    public TokenSummary Sum(DateTimeOffset start, DateTimeOffset end, string? platform = null, string? chat = null)
    { lock (_gate) return _store.Sum(start, end, platform, chat); }
    public TokenBreakdown Breakdown(DateTimeOffset start,DateTimeOffset end,string platform)
    {lock(_gate) return _store.Breakdown(start,end,platform);}
    public IReadOnlyList<ModelActivity> ClaudeModelActivity(DateTimeOffset start,DateTimeOffset end)
    {
        lock(_gate)
        {
            if(_active.Any(p=>_sources[p].Platform=="Claude"&&_sources[p].Offset<_sources[p].Length))return [];
            return _store.ModelActivity(start,end,"Claude");
        }
    }
    public IReadOnlyList<WorkSpan> WorkActivitySpans(DateTimeOffset end,string platform)
    {
        lock(_gate)
        {
            return WorkActivity.Build(_publishedWork.GetValueOrDefault(platform)??[],end);
        }
    }
    public bool WorkActivityReady(string platform) { lock(_gate) return _publishedWork.ContainsKey(platform); }
    public string Coverage { get { lock(_gate) return $"本机日志 · 自 {Since.ToLocalTime():M/d HH:mm}；仅已记录用量，非账号账单。" +
        (PendingFiles > 0 ? $"\n正在补读 {PendingFiles} 个文件。" : "") +
        (LimitedDiscovery ? "\n跟踪最近 256 个日志，其余未包含。" : "") +
        (Skipped > 0 ? $"\n有 {Skipped} 条缺失、损坏或过长记录未计入。" : "") +
        "\n已确认归属的 Codex 子代理（含多层）并入所属 chat；未知或冲突关系保持单列。模型分组仍按实际模型，平台总量不变。"+
        (_parentWarning is null?"":"\n"+_parentWarning)+
        "\n包含能识别的子代理请求；IN 含缓存写入，CACHE 为命中输入，OUT 已含推理输出。\n按首次用量记录时间记账；不代表每秒实际生成速度。"; } }

    public void Poll(DateTimeOffset now, CancellationToken ct = default, bool claude = true, bool codex = true)
    {
        lock (_gate)
        {
            LastReadBytes = 0;
            if (_monitoring != (claude,codex) || now - _discovered >= TimeSpan.FromMinutes(1) || now < _discovered)
            { Discover(now,claude,codex); _discovered = now; _monitoring=(claude,codex); _parentsRead=default; }
            IReadOnlyList<ChatParent> parents=[];
            if(codex&&(now-_parentsRead>=TimeSpan.FromMinutes(1)||now<_parentsRead))
            {
                try {parents=CodexChatParents.Read(_codexHome,_store.ChatIds("Codex"));_parentWarning=null;}
                catch(Exception e) when(e is IOException or UnauthorizedAccessException)
                {_parentWarning="子代理归属暂不可读；保留已确认关系，稍后重试。";}
                _parentsRead=now;
            }
            var watch = Stopwatch.StartNew(); var changed = false;
            var failedPlatforms=new HashSet<string>();
            // Include unread growth in files that this bounded batch will not reach.
            // Otherwise their old lengths could make a partial generation look complete.
            foreach(var path in _active)
                try { _sources[path].Length=new FileInfo(path).Length; }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {failedPlatforms.Add(_sources[path].Platform);}
            _store.Begin();
            try
            {
                foreach(var parent in parents) changed|=_store.SetChatParent("Codex",parent.Child,parent.Parent);
                for (var n = 0; n < _active.Length && LastReadBytes < 8 * 1024 * 1024 && watch.ElapsedMilliseconds < 100; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = _active[_next]; _next=(_next+1)%_active.Length;
                    try { changed |= ReadBatch(path, now, (int)Math.Min(2*1024*1024,8*1024*1024-LastReadBytes)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _sources[path].Skipped++;failedPlatforms.Add(_sources[path].Platform); }
                }
                _store.Commit();
            }
            catch
            {
                _store.Rollback(); _sources.Clear();
                _discovered=_parentsRead=default; _active=[]; throw;
            }
            var pending = _active.Count(p => _sources[p].Offset < _sources[p].Length);
            foreach(var platform in new[]{"Claude","Codex"})
                if((platform=="Claude"?claude:codex)&&!failedPlatforms.Contains(platform)&&
                    !_active.Any(p=>_sources[p].Platform==platform&&_sources[p].Offset<_sources[p].Length)&&
                    (!_publishedWork.ContainsKey(platform)||_dirtyWork.Contains(platform)))
                {
                    _publishedWork[platform]=_store.WorkActivityEvents(now,platform);
                    _dirtyWork.Remove(platform);changed=true;
                }
            if (changed || pending != PendingFiles) Version++;
            PendingFiles = pending;
            var coverage=Coverage;
            if(_store.Meta("coverage")!=coverage) _store.SetMeta("coverage",coverage);
        }
    }
    void Discover(DateTimeOffset now,bool claude,bool codex)
    {
        var files = new List<(string Path, string Platform, long Write)>();
        foreach (var (root, platform) in new[] { (Path.Combine(_codexHome,"sessions"),"Codex"), (Path.Combine(_codexHome,"archived_sessions"),"Codex"), (Path.Combine(_claudeHome,"projects"),"Claude") })
        {
            if(platform=="Codex"?!codex:!claude) continue;
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root,"*.jsonl",new EnumerationOptions { RecurseSubdirectories=true, IgnoreInaccessible=true, AttributesToSkip=FileAttributes.ReparsePoint }))
            {
                var f = new FileInfo(path);
                files.Add((path,platform,f.LastWriteTimeUtc.Ticks));
            }
        }
        LimitedDiscovery = files.Count > 256;
        _active = files.OrderByDescending(f=>f.Write).Take(256).Select(f=>
        {
            if (!_sources.ContainsKey(f.Path))
            {
                var name=Path.GetFileNameWithoutExtension(f.Path);
                var match=Regex.Match(name,@"[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}$");
                _sources[f.Path]=_store.Source(f.Path) ?? new() { Platform=f.Platform, Chat=match.Success?match.Value:name, Length=new FileInfo(f.Path).Length };
                if(_sources[f.Path].ActivityVersion<1)
                    _sources[f.Path]=new(){Platform=f.Platform,Chat=match.Success?match.Value:name,Length=new FileInfo(f.Path).Length};
                _sources[f.Path].ActivityVersion=1;
                _sources[f.Path].ActivityStream=f.Platform=="Codex"?match.Value:name;
            }
            return f.Path;
        }).ToArray();
        var keep=_active.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var path in _sources.Keys.Where(p=>!keep.Contains(p)).ToArray()) _sources.Remove(path);
        _next=0;
    }
    bool ReadBatch(string path, DateTimeOffset now, int budget, TokenCursor? recent = null)
    {
        var c = recent ?? _sources[path]; var f = new FileInfo(path);
        if (!f.Exists) return false;
        if (c.CreatedTicks != 0 && (c.CreatedTicks != f.CreationTimeUtc.Ticks || f.Length < c.Offset || f.Length == c.Offset && f.LastWriteTimeUtc.Ticks != c.WriteTicks))
        { c.Offset=0; c.Skipping=false; c.LastInput=c.LastCached=-1; c.LastKey=null; c.NativeTurn=false; c.Turn="unknown"; c.Epoch="initial"; c.Model=null; c.Recent=null; c.TailSeeded=false; c.Skipped++; }
        c.CreatedTicks=f.CreationTimeUtc.Ticks; c.Length=f.Length; c.WriteTicks=f.LastWriteTimeUtc.Ticks;
        if(c.Offset>=f.Length) return false;
        var changed=false;
        if(recent is null)
        {
            // Show current requests promptly even when a long log needs many bounded
            // backfill batches. Native IDs dedupe this tail against the full-file scan.
            if(!c.TailSeeded && f.Length-c.Offset>1024*1024)
            {
                c.Recent=new() {Platform=c.Platform,Chat=c.Chat,Model=c.Model,ActivityStream=c.ActivityStream,ActivityVersion=1,Offset=f.Length-1024*1024,Skipping=true,NativeOnly=true};
                c.TailSeeded=true;
            }
            if(c.Recent is { } tail)
            {
                var before=LastReadBytes;
                changed|=ReadBatch(path,now,Math.Min(budget,1024*1024),tail);
                budget-=(int)(LastReadBytes-before);
            }
        }
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        stream.Position=c.Offset;
        var buffer=new byte[256*1024]; using var line=new MemoryStream(); var read=0;
        const int maxLine=1024*1024;
        while (read < budget && stream.Position < c.Length)
        {
            var n=stream.Read(buffer,0,(int)Math.Min(Math.Min(buffer.Length,budget-read),c.Length-stream.Position)); if(n==0) break;
            var blockStart=stream.Position-n; read+=n; LastReadBytes+=n;
            var start=0;
            while(start<n)
            {
                var end=Array.IndexOf(buffer,(byte)10,start,n-start); var len=(end<0?n:end)-start;
                if(!c.Skipping)
                {
                    if(line.Length+len>maxLine) { line.SetLength(0); c.Skipping=true; c.Skipped++; }
                    else line.Write(buffer,start,len);
                }
                if(end>=0)
                {
                    if(!c.Skipping && line.Length>0)
                    {
                        // Fast reject ordinary messages before constructing JSON DOMs.
                        var bytes=line.GetBuffer().AsSpan(0,(int)line.Length); var head=Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length,700)]);
                        bool Has(string text)=>head.Contains(text,StringComparison.Ordinal);
                        if(c.Platform=="Claude" ? Has("\"assistant\"") || Has("\"user\"") : Has("\"token_count\"") || Has("\"token_usage_record\"") || Has("\"session_meta\"") || Has("\"turn_context\"") || Has("\"task_started\"") || Has("\"task_complete\"") || Has("\"turn_aborted\""))
                        {
                            var json=Encoding.UTF8.GetString(bytes);
                            changed |= TokenParser.Read(json,c,_store,Since,now);
                            if(WorkActivity.Read(json,c,_store,Since,now)) {changed=true;_dirtyWork.Add(c.Platform);}
                        }
                    }
                    line.SetLength(0); c.Skipping=false; c.Offset=blockStart+end+1;
                }
                else if(c.Skipping) c.Offset=blockStart+n;
                start+=len+(end>=0?1:0);
            }
        }
        if(recent is null)
        {
            if(c.Offset>=c.Length) c.Recent=null;
            _store.SaveSource(path,c);
        }
        return changed;
    }
    public void Dispose() { lock(_gate) _store.Dispose(); }
}
