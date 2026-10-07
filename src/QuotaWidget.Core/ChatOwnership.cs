using System.Text.Json;

namespace QuotaWidget.Core;

public sealed record ChatParent(string Child,string Parent);

/// <summary>Only explicit agent-spawn relationships. A fork or similar title is not ownership.</summary>
public static class ChatOwnership
{
    public static string? Id(string? value)=>Guid.TryParseExact(value,"D",out var id)?id.ToString("D"):null;
    public static string? CodexThreadId(JsonElement metadata)
    {
        if(metadata.ValueKind!=JsonValueKind.Object)return null;
        foreach(var key in new[]{"id","session_id"})
            if(metadata.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String&&Id(value.GetString()) is {} id)return id;
        return null;
    }
    public static bool IsCodexSubagent(JsonElement metadata)
    {
        if(metadata.ValueKind!=JsonValueKind.Object)return false;
        if(metadata.TryGetProperty("thread_source",out var kind)&&kind.ValueKind==JsonValueKind.String&&kind.GetString()=="subagent")return true;
        return metadata.TryGetProperty("source",out var source)&&
            (source.ValueKind==JsonValueKind.Object&&source.TryGetProperty("subagent",out _)||source.ValueKind==JsonValueKind.String&&source.GetString()=="subagent");
    }
    public static string? CodexParent(JsonElement source)
    {
        if(source.ValueKind!=JsonValueKind.Object || !source.TryGetProperty("subagent",out var agent) || agent.ValueKind!=JsonValueKind.Object ||
            !agent.TryGetProperty("thread_spawn",out var spawn) || spawn.ValueKind!=JsonValueKind.Object ||
            !spawn.TryGetProperty("parent_thread_id",out var parent) || parent.ValueKind!=JsonValueKind.String) return null;
        return Id(parent.GetString());
    }
    public static string Root(string chat,IReadOnlyDictionary<string,string> parents)
    {
        var seen=new HashSet<string>(StringComparer.Ordinal);var current=chat;
        while(parents.TryGetValue(current,out var parent))
        {
            // Cycles, ambiguous ancestors (stored as self-links), and excessive depth stay separate.
            if(!seen.Add(current)||seen.Count>128) return chat;
            current=parent;
        }
        return current;
    }
    public static bool BelongsTo(string chat,string ancestor,IReadOnlyDictionary<string,string> parents)
    {
        if(chat==ancestor) return true;
        if(Root(chat,parents)==chat) return false;
        var current=chat;
        for(var n=0;n<128&&parents.TryGetValue(current,out var parent);n++)
        {if(parent==ancestor) return true;current=parent;}
        return false;
    }
}

/// <summary>Read a bounded ancestor closure from local Codex metadata, never message contents.</summary>
public static class CodexChatParents
{
    public static IReadOnlyList<ChatParent> Read(string home,IEnumerable<string> chats)
    {
        var pending=chats.Select(ChatOwnership.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if(pending.Count==0||!Directory.Exists(home)) return [];
        var path=Directory.EnumerateFiles(home,"state_*.sqlite",new EnumerationOptions{AttributesToSkip=FileAttributes.ReparsePoint})
            .Select(p=>(Path:p,Version:int.TryParse(Path.GetFileNameWithoutExtension(p).AsSpan(6),out var n)?n:-1))
            .Where(p=>p.Version>=0).OrderByDescending(p=>p.Version).FirstOrDefault().Path;
        if(path is null) return [];
        using var db=new MiniSqlite(path,readOnly:true);db.Exec("PRAGMA busy_timeout=100; BEGIN");
        var sourceColumns=db.Query("PRAGMA table_info(threads)").Select(r=>r[1] as string).ToHashSet();
        var edgeColumns=db.Query("PRAGMA table_info(thread_spawn_edges)").Select(r=>r[1] as string).ToHashSet();
        var hasSource=sourceColumns.Contains("id")&&sourceColumns.Contains("source");
        var hasEdges=edgeColumns.Contains("parent_thread_id")&&edgeColumns.Contains("child_thread_id");
        if(!hasSource&&!hasEdges) throw new IOException("Unknown Codex parent metadata schema");
        var seen=new HashSet<string>(StringComparer.Ordinal);var result=new HashSet<ChatParent>();
        for(var depth=0;pending.Count>0;depth++)
        {
            if(depth>=128||seen.Count+pending.Count>20000) throw new IOException("Codex parent metadata bounds exceeded");
            var next=new HashSet<string>(StringComparer.Ordinal);
            foreach(var batch in pending.Chunk(200))
            {
                var placeholders=string.Join(',',batch.Select(_=>"?"));var args=batch.Cast<object?>().ToArray();
                void Add(string? child,string? parent)
                {
                    if(ChatOwnership.Id(child) is not { } c||ChatOwnership.Id(parent) is not { } p) return;
                    result.Add(new(c,p));if(!seen.Contains(p)&&!pending.Contains(p)) next.Add(p);
                }
                if(hasSource)
                    foreach(var row in db.Query("SELECT id,substr(source,1,4096) FROM threads WHERE id IN ("+placeholders+")",args))
                    {
                        if(row[1] is not string source||!source.StartsWith('{')) continue;
                        try {using var doc=JsonDocument.Parse(source);Add(row[0] as string,ChatOwnership.CodexParent(doc.RootElement));}
                        catch(JsonException) { }
                    }
                if(hasEdges)
                    foreach(var row in db.Query("SELECT child_thread_id,parent_thread_id FROM thread_spawn_edges WHERE child_thread_id IN ("+placeholders+")",args))
                        Add(row[0] as string,row[1] as string);
            }
            seen.UnionWith(pending);pending=next;
        }
        return result.ToArray();
    }
}
