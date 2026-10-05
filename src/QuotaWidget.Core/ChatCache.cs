using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuotaWidget.Core;

public enum ChatPlatform { Codex, Claude }
public sealed record ChatCacheEntry(ChatPlatform Platform, string Id, string Title, DateTimeOffset RequestAt,
    int WindowMinutes, string Basis, bool Running, string? Project = null, bool AwaitingUsage = false, bool ActivityUncertain = false,
    DateTimeOffset? CompactedAt = null, bool Compacted = false, DateTimeOffset? ActivityAt = null)
{
    public double AgeMinutes(DateTimeOffset now) => Math.Max(0, (now - RequestAt).TotalMinutes);
    public bool WorkPending => !Compacted && (Running || ActivityUncertain);
    public bool Expired(DateTimeOffset now) => !WorkPending && AgeMinutes(now) >= WindowMinutes;
    public int Urgency(DateTimeOffset now) => WorkPending ? 0 : Expired(now) ? 3 : AgeMinutes(now) / WindowMinutes >= 2.0 / 3 ? 2 : AgeMinutes(now) / WindowMinutes >= 1.0 / 3 ? 1 : 0;
}

/// <summary>Metadata only. No body, token, prompt or raw JSON is retained in these states.</summary>
public sealed class ChatCacheState(ChatPlatform platform, string id)
{
    public ChatPlatform Platform { get; } = platform;
    public string Id { get; } = id;
    public string? Title { get; private set; }
    public string? WorkingDirectory { get; private set; }
    public DateTimeOffset? RequestAt { get; private set; }
    public DateTimeOffset LastObserved { get; private set; }
    public DateTimeOffset Boundary { get; private set; }
    public DateTimeOffset ResetAt { get; private set; }
    public DateTimeOffset? CompactedAt { get; private set; }
    DateTimeOffset? _beforeCompactRequest;
    public bool Excluded { get; private set; }
    DateTimeOffset _activity;
    public DateTimeOffset ActivityObserved => _activity > LastObserved ? _activity : LastObserved;
    DateTimeOffset _workEndedAt;
    bool _running, _nativeUsage;
    string? _turnId, _claudeFinishedRequest;
    readonly HashSet<string> _endedTurns = new(StringComparer.Ordinal);
    readonly Queue<string> _endedTurnOrder = new();
    DateTimeOffset? _pendingAt;
    bool _awaitingUsage;
    string? _latestRequestId;
    int _ttl = platform == ChatPlatform.Codex ? 30 : 5;
    string _basis = platform == ChatPlatform.Codex ? "Codex 30m 提醒阈值；本地请求时间估计" : "未确认 1h；按 5m 保守提醒";
    string? _model;
    long _fallbackTotal = -1;
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly Queue<string> _order = new();

    public void SetTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        Title = new string(title.Where(c => !char.IsControl(c)).Take(120).ToArray());
    }

    public ChatCacheEntry? View(DateTimeOffset now)
    {
        var compacted = CompactedAt is { } compactAt && compactAt == ResetAt && compactAt >= LastObserved && RequestAt is null;
        var request = RequestAt ?? (compacted ? _beforeCompactRequest ?? CompactedAt : null);
        if (Excluded || request is not { } at || at > now.AddMinutes(1) || _activity > now.AddMinutes(1) || now - (_activity > at ? _activity : at) > TimeSpan.FromHours(6)) return null;
        // Silence is not an end event. A very old unmatched start becomes unconfirmed,
        // never an idle/expired claim. Long thinking and tool waits survive the old 2m cutoff.
        var uncertain = _running && now - _activity >= TimeSpan.FromMinutes(30);
        return new(Platform, Id, Title ?? $"{Platform} · {Id[..Math.Min(8, Id.Length)]}", at, _ttl,
            _awaitingUsage ? "新请求已开始，缓存用量待确认；此处先按请求时间计时。\n" + _basis : _basis,
            !compacted && _running && !uncertain, AwaitingUsage: !compacted && _awaitingUsage, ActivityUncertain: !compacted && uncertain,
            CompactedAt: CompactedAt, Compacted: compacted, ActivityAt: _activity > LastObserved ? _activity : LastObserved > at ? LastObserved : at);
    }

    void Compact(DateTimeOffset at, bool preservePending)
    {
        if (at < ResetAt || at < LastObserved || CompactedAt >= at) return;
        _beforeCompactRequest = RequestAt ?? _beforeCompactRequest;
        CompactedAt = at;
        Gap(at, preservePending);
        if (at > _activity) _activity = at;
        if (RequestAt is null) { _running = false; _activity = _workEndedAt = at; }
    }

    public void Gap(DateTimeOffset at, bool preservePending = false)
    {
        if (at < ResetAt) return;
        var pending = preservePending && _pendingAt is { } p && p <= at && at - p < TimeSpan.FromHours(2) ? _pendingAt : null;
        ResetAt = at;
        if (LastObserved <= at) RequestAt = null;
        Boundary = default;
        _pendingAt = pending;
        _awaitingUsage = pending is not null;
        if (!preservePending) { _running = false; _turnId = null; }
        if (pending is { } start) { RequestAt = start; Boundary = start; }
        if (Platform == ChatPlatform.Claude) { _ttl = 5; _basis = "未确认 1h；按 5m 保守提醒"; }
    }

    public void Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var type = S(r, "type");
            var p = O(r, "payload");
            if (Platform == ChatPlatform.Codex && type == "session_meta")
            {
                WorkingDirectory = S(p, "cwd") ?? WorkingDirectory;
                var src = O(p, "source");
                if (Has(src, "subagent") || S(p, "source") == "subagent") Excluded = true;
                return;
            }
            if (Platform == ChatPlatform.Claude)
            {
                WorkingDirectory = S(r, "cwd") ?? WorkingDirectory;
                if (O(r, "isSidechain").ValueKind == JsonValueKind.True) { Excluded = true; return; }
                if (type == "custom-title") { SetTitle(S(r, "customTitle")); return; }
            }
            if (!DateTimeOffset.TryParse(S(r, "timestamp"), out var at)) return;
            if (Platform == ChatPlatform.Codex)
            {
                if (type == "compacted") { Compact(at, preservePending: true); return; }
                if (type == "turn_context") { WorkingDirectory = S(p, "cwd") ?? WorkingDirectory; Model(S(p, "model"), at); return; }
                var kind = S(p, "type");
                if (type == "event_msg" && kind is "task_started" or "task_complete" or "turn_aborted")
                {
                    var turnId = S(p, "turn_id");
                    if (kind == "task_started")
                    {
                        if (at >= _activity && (turnId is null || !_endedTurns.Contains(turnId)))
                        { _turnId = turnId; _activity = at; _running = true; Input(at); }
                    }
                    else FinishWork(at, turnId);
                }
                if (type == "response_item")
                {
                    if (kind is "function_call_output" or "custom_tool_call_output" || kind == "message" && S(p, "role") == "user") Input(at);
                    // First response activity gives an estimate if the preceding request boundary was outside the tail.
                    else if (kind is "reasoning" or "function_call" or "custom_tool_call" || kind == "message" && S(p, "role") == "assistant")
                    {
                        if (Boundary <= LastObserved) Boundary = at;
                        if (_running || _workEndedAt == default) WorkActivity(at);
                    }
                }
                if (type == "token_usage_record")
                {
                    _nativeUsage = true;
                    var usage = O(p, "usage");
                    if (N(usage, "input_tokens") > 0 && Observe(at, S(p, "response_id"), usage) && _running) WorkActivity(at);
                }
                else if (!_nativeUsage && type == "event_msg" && kind == "token_count")
                {
                    var info = O(p, "info");
                    var total = N(O(info, "total_token_usage"), "input_tokens");
                    if (total > _fallbackTotal && total > 0)
                    { _fallbackTotal = total; if (Observe(at, "fallback-" + total, O(info, "last_token_usage")) && _running) WorkActivity(at); }
                }
            }
            else
            {
                if (type == "system" && S(r, "subtype") == "compact_boundary")
                { Compact(at, preservePending: S(O(r, "compactMetadata"), "trigger") != "manual"); _ttl = 5; return; }
                if (type == "user" && !TranscriptOnly(r)) Input(at);
                if (type != "assistant") return;
                var message = O(r, "message");
                var changedModel = Model(S(message, "model"), at);
                var usage = O(message, "usage");
                var requestId = S(r, "requestId") ?? S(message, "id");
                var hasUsage = N(usage, "input_tokens") + N(usage, "cache_read_input_tokens") + N(usage, "cache_creation_input_tokens") > 0;
                var current = hasUsage ? Observe(at, requestId, usage, changedModel)
                    : RequestAt is not null && requestId is not null && requestId == _latestRequestId && at >= LastObserved && (_pendingAt is null || _pendingAt <= LastObserved);
                if (current && at >= _activity)
                {
                    if (S(message, "stop_reason") is "end_turn" or "stop_sequence")
                    { _claudeFinishedRequest = requestId; FinishWork(at); }
                    else if (requestId != _claudeFinishedRequest) WorkActivity(at);
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { /* changed/malformed records are ignored */ }
    }

    internal static bool TranscriptOnly(JsonElement root)
    {
        if (O(root, "isCompactSummary").ValueKind == JsonValueKind.True || O(root, "isVisibleInTranscriptOnly").ValueKind == JsonValueKind.True) return true;
        var content = O(O(root, "message"), "content");
        // CLI local command output is stored as user text but is not a model request.
        // Match only its explicit leading wrappers; never search arbitrary prose for 'compact'.
        static bool Local(string? s)
        {
            var text = s?.TrimStart();
            return text is not null && (text.StartsWith("<local-command-stdout>", StringComparison.Ordinal)
                || text.StartsWith("<local-command-stderr>", StringComparison.Ordinal)
                || text.StartsWith("<local-command-caveat>", StringComparison.Ordinal)
                || text.StartsWith("<command-name>/compact</command-name>", StringComparison.Ordinal));
        }
        if (content.ValueKind == JsonValueKind.String) return Local(content.GetString());
        return content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0
            && content.EnumerateArray().All(x => S(x, "type") == "text" && Local(S(x, "text")));
    }

    bool Model(string? model, DateTimeOffset at)
    {
        if (model is null || at < LastObserved) return false;
        var changed = _model is not null && model != _model;
        if (changed) { Gap(at, preservePending: true); _ttl = Platform == ChatPlatform.Codex ? 30 : 5; }
        _model = model;
        return changed;
    }

    void WorkActivity(DateTimeOffset at)
    {
        if (at < _activity || at < _workEndedAt) return;
        if (!_running) _turnId = null;
        _activity = at;
        _running = true;
    }

    void FinishWork(DateTimeOffset at, string? turnId = null)
    {
        if (turnId is not null)
        {
            if (!_endedTurns.Add(turnId)) return;
            _endedTurnOrder.Enqueue(turnId);
            while (_endedTurnOrder.Count > 128) _endedTurns.Remove(_endedTurnOrder.Dequeue());
        }
        // A late completion from an older turn must not stop a newer one.
        if (at < _activity || turnId is not null && _turnId is not null && turnId != _turnId) return;
        _activity = _workEndedAt = at;
        _running = false;
        _turnId = null;
        // RequestAt is deliberately untouched: finishing work does not renew server cache.
    }

    void Input(DateTimeOffset at)
    {
        if (at <= Boundary || at <= LastObserved || at <= ResetAt) return;
        Boundary = at;
        RequestAt = at; // Display immediately; do not wait for the first model usage record.
        _pendingAt = at;
        _awaitingUsage = true;
        WorkActivity(at);
    }

    bool Observe(DateTimeOffset at, string? requestId, JsonElement usage, bool newModelRequest = false)
    {
        // Request identity is mandatory: output-stream updates must never renew a timer.
        if (requestId is null || at < LastObserved || at < ResetAt || at == ResetAt && !newModelRequest || _pendingAt is { } pendingTime && at < pendingTime) return false;
        if (!_seen.Add(requestId))
        {
            // Streaming updates may add cache evidence to this request. They must neither
            // restart its clock nor replace a newer pending input with an older response.
            var current = RequestAt is not null && requestId == _latestRequestId && (_pendingAt is null || _pendingAt <= LastObserved);
            if (Platform == ChatPlatform.Claude && current && ApplyCacheEvidence(usage))
            { _awaitingUsage = false; _pendingAt = null; }
            return current;
        }
        _order.Enqueue(requestId);
        while (_order.Count > 512) _seen.Remove(_order.Dequeue());
        var start = _pendingAt is { } pending && pending <= at && at - pending < TimeSpan.FromHours(2) ? pending
            : Boundary > LastObserved && Boundary <= at && at - Boundary < TimeSpan.FromHours(2) ? Boundary : at;
        LastObserved = at;
        _latestRequestId = requestId;
        _awaitingUsage = Platform == ChatPlatform.Claude && !ApplyCacheEvidence(usage);
        RequestAt = start;
        _pendingAt = _awaitingUsage ? start : null;
        return true;
    }

    bool ApplyCacheEvidence(JsonElement usage)
    {
        var creation = O(usage, "cache_creation");
        var hour = N(creation, "ephemeral_1h_input_tokens");
        var five = N(creation, "ephemeral_5m_input_tokens");
        if (hour > 0 && five == 0) { _ttl = 60; _basis = "日志确认过 1h 缓存写入；后续命中沿用；请求时间估计"; }
        else if (five > 0) { _ttl = 5; _basis = hour > 0 ? "混合 5m / 1h 缓存；按较短的 5m 提醒" : "日志确认 5m 缓存写入"; }
        return N(usage, "cache_creation_input_tokens") + N(usage, "cache_read_input_tokens") > 0;
    }

    static bool Has(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out _);
    static JsonElement O(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) ? v : default;
    static string? S(JsonElement o, string n) => O(o, n) is var v && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static long N(JsonElement o, string n) => O(o, n) is var v && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : 0;
}

/// <summary>Bounded incremental JSONL tailer, shared-read only. Oversize lines are skipped without retaining bodies.</summary>
public sealed class MetadataTail
{
    public const int TailBytes = 2 * 1024 * 1024;
    const int MaxLine = 1024 * 1024;
    long _offset;
    DateTime _write, _created;
    bool _initialized, _skip;
    readonly MemoryStream _partial = new();

    public long Read(string path, Action<string> line, Action gap)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || _initialized && fi.Length == _offset && fi.LastWriteTimeUtc == _write) return 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!_initialized || fs.Length < _offset || fi.CreationTimeUtc != _created || fs.Length == _offset && fi.LastWriteTimeUtc != _write)
        {
            if (_initialized) gap();
            _offset = Math.Max(0, fs.Length - TailBytes); _partial.SetLength(0); _skip = _offset > 0;
            _initialized = true;
        }
        if (fs.Length - _offset > TailBytes) { gap(); _offset = fs.Length - TailBytes; _partial.SetLength(0); _skip = true; }
        fs.Position = _offset;
        var buffer = new byte[64 * 1024];
        var read = 0L;
        int n;
        while (read < TailBytes && (n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, TailBytes - read))) > 0)
        {
            read += n;
            var start = 0;
            while (start < n)
            {
                var end = Array.IndexOf(buffer, (byte)10, start, n - start);
                var length = (end < 0 ? n : end) - start;
                if (!_skip)
                {
                    if (_partial.Length + length > MaxLine) { _partial.SetLength(0); _skip = true; gap(); }
                    else _partial.Write(buffer, start, length);
                }
                if (end >= 0)
                {
                    if (!_skip && _partial.Length > 0) line(Encoding.UTF8.GetString(_partial.GetBuffer(), 0, (int)_partial.Length));
                    _partial.SetLength(0); _skip = false;
                    if (_partial.Capacity > 64 * 1024) _partial.Capacity = 64 * 1024;
                }
                start += length + (end >= 0 ? 1 : 0);
            }
        }
        _offset = fs.Position; _write = fi.LastWriteTimeUtc; _created = fi.CreationTimeUtc;
        return read;
    }
}

public sealed class ChatCacheMonitor(string codexHome, string claudeHome)
{
    (bool Claude,bool Codex)? _monitoring;
    const int MaxFiles = 96;
    readonly Dictionary<string, (MetadataTail Tail, ChatCacheState State)> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ChatCacheState> _states = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset> _indexUpdates = new(StringComparer.Ordinal);
    readonly MetadataTail _index = new();
    readonly ChatProjects _projects = new(codexHome);
    DateTimeOffset _discovered;
    public long LastReadBytes { get; private set; }
    public int TrackedFiles => _files.Count;
    public string? Warning { get; private set; }
    public static ChatCacheMonitor Local() => new(
        Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));

    public IReadOnlyList<ChatCacheEntry> Poll(DateTimeOffset now,bool claude=true,bool codex=true)
    {
        LastReadBytes = 0;
        if(codex) try
        {
            LastReadBytes += _index.Read(Path.Combine(codexHome, "session_index.jsonl"), json =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(json); var r = doc.RootElement;
                    if (r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } key &&
                        r.TryGetProperty("thread_name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        _titles.Remove(key); _titles[key] = new string(name.GetString()!.Take(120).ToArray());
                        if (r.TryGetProperty("updated_at", out var updated) && updated.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(updated.GetString(), out var time) && time <= now.AddMinutes(1)) _indexUpdates[key] = time;
                        if (_titles.Count > 2048) { var oldest = _titles.Keys.First(); _titles.Remove(oldest); _indexUpdates.Remove(oldest); }
                    }
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException) { }
            }, () => { _titles.Clear(); _indexUpdates.Clear(); });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Warning = "部分本地记录暂不可读"; }
        if (_monitoring!=(claude,codex) || now - _discovered >= TimeSpan.FromSeconds(30) || now < _discovered)
        { _discovered = now; Discover(now,claude,codex); _monitoring=(claude,codex); }
        foreach (var (path, tracked) in _files)
        {
            try { LastReadBytes += tracked.Tail.Read(path, tracked.State.Read, () => tracked.State.Gap(tracked.State.LastObserved)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Warning = "部分本地记录暂不可读"; }
        }
        foreach (var s in _states.Values) if (s.Platform == ChatPlatform.Codex && _titles.TryGetValue(s.Id, out var title)) s.SetTitle(title);
        return _states.Values.Select(s => s.View(now) is { } entry ? entry with { Project = _projects.Name(s.Platform, s.Id, s.WorkingDirectory) } : null)
            .OfType<ChatCacheEntry>().OrderByDescending(e => e.RequestAt).ToArray();
    }

    void Discover(DateTimeOffset now,bool claude,bool codex)
    {
        Warning = null;
        if(codex) _projects.Refresh();
        var candidates = new List<(string Path, ChatPlatform Platform, DateTime Write)>();
        foreach (var (root, platform) in new[] { (Path.Combine(codexHome, "sessions"), ChatPlatform.Codex), (Path.Combine(claudeHome, "projects"), ChatPlatform.Claude) })
        {
            if(platform==ChatPlatform.Codex?!codex:!claude) continue;
            if (!Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", options))
                {
                    if (path.Split(Path.DirectorySeparatorChar).Contains("subagents", StringComparer.OrdinalIgnoreCase)) continue;
                    var fi = new FileInfo(path);
                    // Windows can leave mtime unchanged while a writer keeps its handle open.
                    // Never use mtime as a hard 6h cutoff: rank bounded candidates, then use the
                    // record timestamps for eligibility. Recent index/observed activity wins.
                    var rank = fi.LastWriteTimeUtc;
                    if (_files.TryGetValue(path, out var tracked) && tracked.State.ActivityObserved <= now && tracked.State.ActivityObserved.UtcDateTime > rank)
                        rank = tracked.State.ActivityObserved.UtcDateTime;
                    if (platform == ChatPlatform.Codex && Path.GetFileNameWithoutExtension(path) is { Length: >= 36 } name && _indexUpdates.TryGetValue(name[^36..], out var indexed) && indexed.UtcDateTime > rank)
                        rank = indexed.UtcDateTime;
                    candidates.Add((path, platform, rank));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Warning = "部分本地记录暂不可读"; }
        }
        if (candidates.Count > MaxFiles) Warning = "最多跟踪 96 个日志；按已知活动时间选择，历史记录可能不完整";
        var selected = candidates.OrderByDescending(x => x.Write).Take(MaxFiles).ToList();
        var keep = selected.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in _files.Keys.Where(p => !keep.Contains(p)).ToArray()) _files.Remove(old);
        foreach (var c in selected.OrderBy(x => x.Write))
        {
            if (_files.ContainsKey(c.Path)) continue;
            var match = Regex.Match(Path.GetFileNameWithoutExtension(c.Path), @"[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}$");
            if (!match.Success) continue;
            var key = c.Platform + ":" + match.Value;
            if (!_states.TryGetValue(key, out var state)) _states[key] = state = new(c.Platform, match.Value);
            // Codex metadata at the head identifies sidechains even when the tail starts much later.
            if (c.Platform == ChatPlatform.Codex)
            {
                try
                {
                    using var fs = new FileStream(c.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var head = new byte[64 * 1024]; var n = fs.Read(head); LastReadBytes += n;
                    var end = Array.IndexOf(head, (byte)10, 0, n);
                    if (end >= 0) state.Read(Encoding.UTF8.GetString(head, 0, end));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            _files[c.Path] = (new MetadataTail(), state);
        }
        var active = _files.Values.Select(v => v.State).ToHashSet();
        foreach (var key in _states.Where(kv => !active.Contains(kv.Value)).Select(kv => kv.Key).ToArray()) _states.Remove(key);
    }
}
