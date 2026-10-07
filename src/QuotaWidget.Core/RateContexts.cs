namespace QuotaWidget.Core;

/// <summary>Estimation-only model contexts. The original quota ledger remains intact.
/// A positive observation that straddles a transition is isolated, not fractionally
/// billed to either model. Its internal distribution remains uncertain.</summary>
static class RateContexts
{
    public static (SeriesData Source,IReadOnlyList<ChartSpan> Ambiguous) Separate(SeriesData source,IReadOnlyList<DateTimeOffset> boundaries,IReadOnlyList<WorkSpan> activity)
    {
        var edges=boundaries.Distinct().Order().ToArray();
        if(edges.Length==0)return(source,[]);
        var groups=new Dictionary<(int Source,int Context),int>();var nextGroup=0;
        int Context(DateTimeOffset t){var i=Array.BinarySearch(edges,t);return i>=0?i+1:~i;}
        int Group(int original,int context)
        {
            if(!groups.TryGetValue((original,context),out var n))groups[(original,context)]=n=nextGroup++;
            return n;
        }
        var segments=new List<RateSegment>();var ambiguous=new List<ChartSpan>();
        foreach(var s in source.Segments)
        {
            var from=Context(s.Start);var to=Context(s.End);
            if(to>0&&edges[to-1]==s.End)to--;
            if(!s.Valid||from==to)Add(s.Start,s.End,s.Delta,Group(s.Group,from));
            else if(s.Delta>0)
            {
                var supported=new HashSet<int>();
                for(var i=from;i<=to;i++)
                {
                    var a=i==from?s.Start:edges[i-1];var b=i==to?s.End:edges[i];
                    if(activity.Any(w=>w.End>a&&w.Start<b))supported.Add(i);
                }
                // Match the estimator's short first-post-completion reporting grace.
                // A delayed old-model report must not be declared exclusively new work.
                var grace=TimeSpan.FromSeconds(Math.Clamp(s.Minutes*60,60,600)+60);
                foreach(var w in activity.Where(w=>w.KnownEnd&&w.End<=s.Start&&s.Start-w.End<=TimeSpan.FromSeconds(60)&&s.End-w.End<=grace))
                    supported.Add(Context(w.End.AddTicks(-1)));
                if(supported.Count==1)
                    // Only one model context contains supported work: preserve the
                    // whole observation and let following readings refine that context.
                    Add(s.Start,s.End,s.Delta,Group(s.Group,supported.Single()));
                else
                {
                    // A complete observed delta is kept in one independent context.
                    // Never invent fractional per-model observations at the switch.
                    Add(s.Start,s.End,s.Delta,nextGroup++);ambiguous.Add(new(s.Start,s.End));
                }
            }
            else
            {
                // An unchanged counter adds no points on either side. Splitting
                // zero support allows exact work edges without dividing consumption.
                var a=s.Start;
                for(var i=from;i<to;i++){Add(a,edges[i],0,Group(s.Group,i));a=edges[i];}
                Add(a,s.End,0,Group(s.Group,to));
            }
            void Add(DateTimeOffset a,DateTimeOffset b,double delta,int group)=>segments.Add(new(){Start=a,End=b,Delta=delta,Group=group,
                Issue=s.Issue,Label=s.Label,CounterResetOnly=s.CounterResetOnly,StartsAtCapacity=s.StartsAtCapacity});
        }
        return(new(){Key=source.Key,Segments=segments},ambiguous);
    }
}
