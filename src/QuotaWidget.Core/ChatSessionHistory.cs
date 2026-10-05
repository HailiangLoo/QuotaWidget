using System.Text.Json;

namespace QuotaWidget.Core;

public sealed class SessionChat
{
    public required ChatCacheEntry Last { get; set; }
    public DateTimeOffset FirstAt { get; set; }
    public List<DateTimeOffset> Compactions { get; set; } = [];
    public string Key => Last.Platform + ":" + Last.Id;
}

public sealed class ChatSession
{
    public int Version { get; set; } = 1;
    public required string Id { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public bool Recovered { get; set; }
    public List<SessionChat> Chats { get; set; } = [];
}

/// <summary>Local metadata-only journal. Session boundaries follow activity, never midnight or process restarts.</summary>
public sealed class ChatSessionHistory
{
    public static readonly TimeSpan BreakAfter = TimeSpan.FromHours(4);
    readonly string _directory;
    readonly object _gate = new();
    readonly List<ChatSession> _recent = [];
    readonly HashSet<string> _dirty = [];
    DateTimeOffset _savedAt;
    public long Version { get; private set; }
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public ChatSessionHistory(string dataRoot)
    {
        _directory = Path.Combine(dataRoot, "chat-sessions");
        if (Directory.Exists(_directory))
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json").OrderByDescending(Path.GetFileName).Take(8).Reverse())
                if (Read(file) is { } s) _recent.Add(s);
    }

    static ChatSession? Read(string file)
    {
        try
        {
            var text = AtomicFile.TryReadAllText(file);
            var s = text is null ? null : JsonSerializer.Deserialize<ChatSession>(text, Json);
            return s is { Version: 1, Chats: not null } && s.Start <= s.End && s.Id == Path.GetFileNameWithoutExtension(file)
                && s.Chats.All(c => c is not null && c.Last is { Id: not null, Title: not null, Basis: not null } && c.Compactions is not null
                    && c.FirstAt <= s.End && c.Last.RequestAt <= (c.Last.ActivityAt ?? c.Last.RequestAt)) ? s : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Capture(IEnumerable<ChatCacheEntry> entries, DateTimeOffset now)
    {
        lock (_gate)
        {
            bool urgent = false;
            // Replay the known request/compact/activity timestamps in chronological order.
            // A cold scan may discover a long request after a later, short chat: sorting only
            // by completion/activity would invent an idle gap and split the usage session.
            var observations = entries.SelectMany(e =>
            {
                var result = new List<ChatCacheEntry>();
                var at = e.ActivityAt ?? e.RequestAt;
                if (e.RequestAt < at)
                    result.Add(e with { ActivityAt = e.RequestAt, Running = false, ActivityUncertain = false, Compacted = false,
                        CompactedAt = e.CompactedAt <= e.RequestAt ? e.CompactedAt : null });
                if (e.CompactedAt is { } compact && compact < at && compact != e.RequestAt)
                    result.Add(e with { RequestAt = e.RequestAt < compact ? e.RequestAt : compact, ActivityAt = compact,
                        Running = false, ActivityUncertain = false, AwaitingUsage = false, Compacted = true });
                result.Add(e); return result;
            }).OrderBy(e => e.ActivityAt ?? e.RequestAt);
            foreach (var e in observations)
            {
                var at = e.ActivityAt ?? e.RequestAt;
                if (at > now.AddMinutes(1) || e.RequestAt > at || now - at > TimeSpan.FromHours(6)) continue;
                var key = e.Platform + ":" + e.Id;
                var prior = _recent.SelectMany(s => s.Chats).Where(c => c.Key == key).MaxBy(c => c.Last.ActivityAt ?? c.Last.RequestAt);
                var priorAt = prior?.Last.ActivityAt ?? prior?.Last.RequestAt;
                if (priorAt > at) continue; // late replay must not change session boundaries or resurrect work
                if (priorAt == at && prior?.Last == e) continue;
                // Metadata/status changes without new activity belong to the original session.
                var session = priorAt == at ? _recent.First(s => s.Chats.Contains(prior!)) : _recent.LastOrDefault();
                if (session is not null && at < session.Start) continue;
                if (session is null || at - session.End >= BreakAfter)
                {
                    var start = at - e.RequestAt < BreakAfter ? e.RequestAt : at;
                    if (session is not null && start <= session.End) start = at;
                    session = new() { Id = start.UtcDateTime.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8],
                        Start = start, End = at, Recovered = at < now.AddMinutes(-1) };
                    _recent.Add(session); urgent = true;
                }
                var chat = session.Chats.FirstOrDefault(c => c.Key == key);
                if (chat is null)
                {
                    chat = new() { Last = e, FirstAt = e.RequestAt < session.Start ? session.Start : e.RequestAt };
                    session.Chats.Add(chat);
                }
                else chat.Last = e;
                if (e.CompactedAt is { } c && c >= session.Start && c <= at && !chat.Compactions.Contains(c))
                { chat.Compactions.Add(c); urgent = true; }
                if (at > session.End) session.End = at;
                _dirty.Add(session.Id);
                Version++;
            }
            if (urgent || now - _savedAt >= TimeSpan.FromSeconds(10)) Flush(now);
            while (_recent.Count > 8 && !_dirty.Contains(_recent[0].Id)) _recent.RemoveAt(0);
        }
    }

    public void Flush(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var session in _recent.Where(s => _dirty.Contains(s.Id)))
                AtomicFile.WriteAllText(Path.Combine(_directory, session.Id + ".json"), JsonSerializer.Serialize(session, Json));
            _dirty.Clear(); _savedAt = now;
        }
    }

    // A frozen clone crosses from the scanner thread to the UI; callers cannot mutate the journal.
    static ChatSession Copy(ChatSession s) => JsonSerializer.Deserialize<ChatSession>(JsonSerializer.Serialize(s, Json), Json)!;
    public ChatSession? Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            var last = _recent.LastOrDefault();
            return last is not null && now - last.End < BreakAfter ? Copy(last) : null;
        }
    }

    public IReadOnlyList<ChatCacheEntry> RecentCompacted(DateTimeOffset now)
    {
        lock (_gate)
            return _recent.SelectMany(s => s.Chats).GroupBy(c => c.Key)
                .Select(g => g.MaxBy(c => ChatListPolicy.LastActivity(c.Last))!.Last)
                .Where(e => ChatListPolicy.RecentCompact(e, now)).OrderByDescending(e => e.CompactedAt).ToArray();
    }

    public IReadOnlyList<ChatSession> ForDay(DateTime day)
        => ForRange(day.Date,day.Date.AddDays(1));

    public IReadOnlyList<ChatSession> ForRange(DateTime first,DateTime lastExclusive)
    {
        lock (_gate)
        {
            var all = new Dictionary<string, ChatSession>();
            if (Directory.Exists(_directory))
                foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
                    if (Read(file) is { } s && Includes(s, first,lastExclusive)) all[s.Id] = s;
            foreach (var s in _recent) if (Includes(s, first,lastExclusive)) all[s.Id] = Copy(s);
            return all.Values.OrderByDescending(s => s.Start).ToArray();
        }
    }
    static bool Includes(ChatSession s, DateTime first,DateTime lastExclusive) => s.Start.ToLocalTime().Date < lastExclusive.Date && s.End.ToLocalTime().Date >= first.Date;
}
