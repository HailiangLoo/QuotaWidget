namespace QuotaWidget.Core;

public sealed record TokenSlice(string Model,string Chat,TokenSummary Usage);
public sealed record TokenChatModel(string Model,TokenSummary Usage,int Subagents);
public sealed record TokenGroup(string Key,TokenSummary Usage,int Members,int Subagents=0,TokenSummary? DirectUsage=null)
{
    public IReadOnlyList<TokenChatModel> ModelBreakdown {get;init;}=[];
}
public sealed record TokenBreakdown(DateTimeOffset Start,DateTimeOffset End,TokenSummary Total,
    IReadOnlyList<TokenGroup> Models,IReadOnlyList<TokenGroup> Chats,string? Error=null)
{
    public static TokenBreakdown Build(DateTimeOffset start,DateTimeOffset end,IReadOnlyList<TokenSlice> rows,IReadOnlyDictionary<string,string>? parents=null)
    {
        TokenSummary Sum(IEnumerable<TokenSummary> items)
        {
            var a=items.ToArray();
            return new(a.Sum(x=>x.Input),a.Sum(x=>x.Cached),a.Sum(x=>x.Output),a.Sum(x=>x.Written),a.Sum(x=>x.Requests),a.Sum(x=>x.Conflicts),a.Sum(x=>x.MissingWrites));
        }
        var roots=rows.Select(r=>r.Chat).Distinct().ToDictionary(c=>c,c=>parents is null?c:ChatOwnership.Root(c,parents));
        IReadOnlyList<TokenGroup> Group(bool models)=>rows.GroupBy(r=>models?r.Model:roots[r.Chat])
            .Select(g=>new TokenGroup(g.Key,Sum(g.Select(r=>r.Usage)),g.Select(r=>models?roots[r.Chat]:r.Model).Distinct().Count(),
                models?0:g.Where(r=>r.Chat!=g.Key).Select(r=>r.Chat).Distinct().Count(),
                models?null:Sum(g.Where(r=>r.Chat==g.Key).Select(r=>r.Usage)))
            {
                // Keep actual model identities inside each owning chat, including descendants.
                ModelBreakdown=models?[]:g.GroupBy(r=>r.Model)
                    .Select(m=>new TokenChatModel(m.Key,Sum(m.Select(r=>r.Usage)),m.Where(r=>r.Chat!=g.Key).Select(r=>r.Chat).Distinct().Count()))
                    .OrderByDescending(m=>m.Usage.Input+m.Usage.Cached+m.Usage.Output).ThenBy(m=>m.Model,StringComparer.Ordinal).ToArray()
            })
            .OrderByDescending(g=>g.Usage.Input+g.Usage.Cached+g.Usage.Output).ThenBy(g=>g.Key,StringComparer.Ordinal).ToArray();
        return new(start,end,Sum(rows.Select(r=>r.Usage)),Group(true),Group(false));
    }
}
