using System.Text.Json;

namespace QuotaWidget.Core;

public sealed record WorkEvent(string Stream, DateTimeOffset At, string Kind, string? Id, string? Model);
public sealed record WorkSpan(DateTimeOffset Start, DateTimeOffset End, bool KnownStart, bool KnownEnd, string? Model);
public sealed record TrendActivity(IReadOnlyList<WorkSpan> Claude, IReadOnlyList<WorkSpan> Fable, IReadOnlyList<WorkSpan> Codex,
    bool ClaudeReady=true,bool CodexReady=true)
{
    public bool FableRunning {get;init;}
    public static readonly TrendActivity Empty = new([], [], []);
    public TrendActivity Episodes(DateTimeOffset asOf)=>new(WorkActivity.Episodes(Claude,asOf),WorkActivity.Episodes(Fable,asOf),WorkActivity.Episodes(Codex,asOf),ClaudeReady,CodexReady){FableRunning=FableRunning};
}

/// <summary>Only explicit local work metadata, not token-count gaps or quota plateaus.</summary>
public static class WorkActivity
{
    // Inspect raw model spans before provider union removes their identities.
    // An unknown input or another model is not evidence of current Fable work.
    public static bool FableRunning(IEnumerable<WorkSpan> spans,DateTimeOffset now)=>
        spans.Any(s=>FableDisplay.IsFable(s.Model)&&s.Start<=now&&s.End>=now&&!s.KnownEnd);
    // A turn completion is not necessarily the end of a provider usage episode.
    // Keep this short handoff window fixed: it is unrelated to polling or viewport size.
    public static readonly TimeSpan HandoffWindow=TimeSpan.FromSeconds(10);
    static JsonElement O(JsonElement e,string k)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(k,out var v)?v:default;
    static string? S(JsonElement e,string k)=>O(e,k) is {ValueKind:JsonValueKind.String} v&&v.GetString() is {Length:>0 and <=256} value?value:null;
    public static bool Read(string json, TokenCursor cursor, TokenStore store, DateTimeOffset since, DateTimeOffset now)
    {
        try
        {
            using var doc=JsonDocument.Parse(json);var r=doc.RootElement;var p=O(r,"payload");
            if(cursor.Excluded||!DateTimeOffset.TryParse(S(r,"timestamp"),out var at)||at<since||at>now.AddMinutes(1))return false;
            var type=S(r,"type");var stream=cursor.ActivityStream.Length>0?cursor.ActivityStream:cursor.Chat;
            bool Put(string kind,string? id=null,string? model=null)=>store.PutWorkEvent(cursor.Platform,new(stream,at,kind,id,model));
            if(cursor.Platform=="Codex")
            {
                if(type!="event_msg")return false;
                return S(p,"type") switch
                {
                    "task_started"=>Put("start",S(p,"turn_id")),
                    "task_complete" or "turn_aborted"=>Put("end",S(p,"turn_id")),
                    _=>false
                };
            }
            if(type=="user"&&!ChatCacheState.TranscriptOnly(r))return Put("input",S(r,"uuid"));
            if(type=="assistant")
            {
                var m=O(r,"message");var id=S(r,"requestId")??S(m,"id");var model=S(m,"model");
                if(id is null||model is null||model=="<synthetic>")return false;
                var changed=Put("activity",id,model);
                if(S(m,"stop_reason") is "end_turn" or "stop_sequence")changed|=Put("finish",id,model);
                return changed;
            }
            return false;
        }
        catch(JsonException){return false;}
    }

    public static IReadOnlyList<WorkSpan> Build(IEnumerable<WorkEvent> events, DateTimeOffset now)
    {
        var spans=new List<WorkSpan>();
        foreach(var stream in events.Where(e=>e.At<=now).GroupBy(e=>e.Stream))
        {
            DateTimeOffset? start=null;bool known=false;string? turn=null,model=null,request=null;
            var ended=new HashSet<string>();
            var finished=new HashSet<string>();
            void Close(DateTimeOffset at,bool knownEnd)
            {
                if(start is { } a&&at>a)spans.Add(new(a,at,known,knownEnd,model));
                start=null;model=null;turn=null;request=null;known=false;
            }
            foreach(var e in stream.OrderBy(e=>e.At).ThenBy(e=>e.Kind is "start" or "input"?0:e.Kind=="activity"?1:2))
            {
                if(e.Kind=="start")
                {
                    if(e.Id is not null&&ended.Contains(e.Id))continue;
                    if(start is not null&&turn!=e.Id)Close(e.At,false);
                    start??=e.At;known=true;turn=e.Id;
                }
                else if(e.Kind=="input") {if(start is null){start=e.At;known=true;}}
                else if(e.Kind=="activity")
                {
                    if(e.Id is not null&&finished.Contains(e.Id))continue;
                    if(start is null){start=e.At;known=false;}
                    if(model is not null&&model!=e.Model) {Close(e.At,true);start=e.At;known=true;}
                    model=e.Model;request=e.Id;
                }
                else if(e.Kind=="end")
                {
                    if(e.Id is not null)ended.Add(e.Id);
                    if(turn is not null&&e.Id is not null&&turn!=e.Id)continue;
                    Close(e.At,true);known=false;
                }
                else if(e.Kind=="finish")
                {
                    if(e.Id is not null&&!finished.Add(e.Id))continue;
                    if(request is not null&&request!=e.Id)continue;
                    Close(e.At,true);
                }
            }
            Close(now,false); // No completion: an open interval, never a made-up stop time.
        }
        return spans;
    }

    // Exact turn spans remain available for metadata/accounting. This derived timeline
    // coalesces brief handoffs; longer silence retains the ORIGINAL completion timestamp.
    // Wait out the handoff window before publishing a final boundary, avoiding a stop/start
    // flash while the next queued turn has not yet appeared. Never move an outer start/end.
    public static IReadOnlyList<WorkSpan> Episodes(IEnumerable<WorkSpan> spans,DateTimeOffset asOf) =>
        Merge(spans.Where(s=>s.Start<asOf).Select(s=>s.End>asOf?s with{End=asOf,KnownEnd=false}:s),HandoffWindow)
            .Select(s=>s.KnownEnd&&asOf-s.End<HandoffWindow?s with{KnownEnd=false}:s).ToArray();

    // Union of concurrent chats/agents. Raw provider aggregation passes zero; only
    // Episodes applies the fixed handoff rule, never a polling/backoff interval.
    public static IReadOnlyList<WorkSpan> Merge(IEnumerable<WorkSpan> spans, TimeSpan cadence)
    {
        var result=new List<WorkSpan>();
        foreach(var span in spans.Where(s=>s.End>s.Start).OrderBy(s=>s.Start))
        {
            if(result.Count==0||span.Start-result[^1].End>cadence){result.Add(span);continue;}
            var last=result[^1];
            if(span.End>last.End)result[^1]=last with{End=span.End,KnownEnd=span.KnownEnd};
            else if(span.End==last.End&&!span.KnownEnd)result[^1]=last with{KnownEnd=false};
            if(span.Start==last.Start&&!span.KnownStart)result[^1]=result[^1] with{KnownStart=false};
        }
        return result;
    }
    public static IReadOnlyList<DateTimeOffset> Edges(IEnumerable<WorkSpan> spans)=>spans.SelectMany(s=>
        (s.KnownStart?new[]{s.Start}:[]).Concat(s.KnownEnd?new[]{s.End}:[])).Distinct().Order().ToArray();

    public static SeriesData Constrain(SeriesData source,IReadOnlyList<WorkSpan> activity,DateTimeOffset start,DateTimeOffset end)
    {
        if(activity.Count==0)return source;
        var support=Merge(activity.Select(a=>a with{Start=a.KnownStart?a.Start:start,End=a.KnownEnd?a.End:end}),TimeSpan.Zero);
        var result=new List<RateSegment>();
        foreach(var s in source.Segments)
        {
            if(!s.Valid||s.Start<start||s.End>end||s.Delta<=0){result.Add(s);continue;}
            var overlap=support.Select(a=>new ChartSpan(a.Start>s.Start?a.Start:s.Start,
                a.End<s.End?a.End:s.End)).Where(a=>a.End>a.Start)
                .Where(a=>a.Start<s.End&&a.End>s.Start).ToArray();
            if(overlap.Length==0){result.Add(s);continue;} // Unexplained account usage stays visible.
            var ticks=overlap.Sum(a=>(a.End-a.Start).Ticks);var from=s.Start;double assigned=0;
            for(var i=0;i<overlap.Length;i++)
            {
                var a=overlap[i];if(a.Start>from)Add(from,a.Start,0);
                var amount=i==overlap.Length-1?s.Delta-assigned:s.Delta*(a.End-a.Start).Ticks/ticks;
                Add(a.Start,a.End,amount);assigned+=amount;from=a.End;
            }
            if(from<s.End)Add(from,s.End,0);
            void Add(DateTimeOffset a,DateTimeOffset b,double delta)=>result.Add(new(){Start=a,End=b,Delta=delta,Group=s.Group});
        }
        return new(){Key=source.Key,Segments=result};
    }
}
