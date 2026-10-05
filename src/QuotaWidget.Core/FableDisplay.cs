namespace QuotaWidget.Core;

public sealed record ModelActivity(DateTimeOffset Start,DateTimeOffset End,string? Model);
public sealed record ChartSpan(DateTimeOffset Start,DateTimeOffset End);

/// <summary>Local model evidence controls visual redundancy only; never changes quota counters.</summary>
public static class FableDisplay
{
    public static bool IsFable(string? model)=>model is not null&&
        (model.Equals("fable",StringComparison.OrdinalIgnoreCase)||model.StartsWith("claude-fable-",StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<ChartSpan> Build(SeriesData total,SeriesData fable,IReadOnlyList<ModelActivity> activity,
        DateTimeOffset start,DateTimeOffset end,double smoothingMinutes,bool cumulative=false)
    {
        if(activity.Count==0)return [];
        // A mixed-model smoothing neighbourhood must keep both curves. Isolated known Fable
        // activity can cover its smoothing shoulders, not an arbitrary session or whole day.
        var radius=TimeSpan.FromMinutes(Math.Clamp(smoothingMinutes,0,180)/2);
        var known=Merge(activity.Where(a=>IsFable(a.Model)).Select(a=>new ChartSpan(a.Start-radius,a.End+radius)));
        var blocked=Merge(activity.Where(a=>!IsFable(a.Model)).Select(a=>new ChartSpan(a.Start-radius,a.End+radius)));
        var valid=total.Segments.Where(s=>s.Valid).ToDictionary(s=>(s.Start,s.End));
        if(cumulative)
        {
            // A zero-increment plateau adds no new model attribution. Carry confirmed
            // Fable-only display through that plateau, without crossing an unexplained
            // increment, a reset, missing coverage or mixed/unknown model evidence.
            var quiet=fable.Segments.Where(s=>s.Valid&&s.Start>=start&&s.End<=end&&Math.Abs(s.Delta)<1e-9&&
                valid.TryGetValue((s.Start,s.End),out var t)&&Math.Abs(t.Delta)<1e-9).Select(s=>new ChartSpan(s.Start,s.End));
            var candidates=Merge(known.Concat(quiet));
            known=candidates.Where(s=>known.Any(k=>k.Start<s.End&&k.End>s.Start)).ToList();
        }
        var output=new List<ChartSpan>();
        foreach(var s in fable.Segments)
        {
            if(!s.Valid||s.Start<start||s.End>end||!valid.TryGetValue((s.Start,s.End),out var t)||
                Math.Abs(t.Delta-s.Delta)>1.5)continue; // mismatched coverage or unexplained large quota change
            foreach(var k in known.Where(k=>k.End>s.Start&&k.Start<s.End))
            {
                var a=k.Start>s.Start?k.Start:s.Start;var b=k.End<s.End?k.End:s.End;
                foreach(var veto in blocked.Where(v=>v.End>a&&v.Start<b))
                {
                    if(veto.Start>a)output.Add(new(a,veto.Start));
                    if(veto.End>a)a=veto.End;
                    if(a>=b)break;
                }
                if(a<b)output.Add(new(a,b));
            }
        }
        // A series of small unobserved non-Fable increments must not evade the per-bin
        // guard. Keep both curves if a whole candidate fragment diverges beyond the
        // combined integer-counter precision (1 total point + .5 converted Fable point).
        return Merge(output).Where(span=>Math.Abs(RateEngine.SumRange(total,span.Start,span.End).Delta-
            RateEngine.SumRange(fable,span.Start,span.End).Delta)<=1.5).ToArray();
    }

    static List<ChartSpan> Merge(IEnumerable<ChartSpan> spans)
    {
        var result=new List<ChartSpan>();
        foreach(var span in spans.Where(s=>s.End>s.Start).OrderBy(s=>s.Start))
        {
            if(result.Count>0&&span.Start<=result[^1].End)
            { if(span.End>result[^1].End)result[^1]=result[^1] with{End=span.End}; }
            else result.Add(span);
        }
        return result;
    }

    /// <summary>Cumulative values retain earlier non-Fable consumption. Omitting only local
    /// Fable fragments would punch holes at a different vertical baseline. Share a cumulative
    /// stroke only when every recorded interval in the viewport is covered by that evidence.</summary>
    public static bool CoversCumulativeRange(SeriesData total, SeriesData fable,
        IReadOnlyList<ChartSpan> spans, DateTimeOffset start, DateTimeOffset end)
    {
        var visible=total.Segments.Concat(fable.Segments)
            .Where(s=>s.Valid&&s.Start>=start&&s.End<=end).ToArray();
        if(visible.Length==0||spans.Count==0)return false;
        if(Math.Abs(RateEngine.SumRange(total,start,end).Delta-RateEngine.SumRange(fable,start,end).Delta)>1.5)return false;
        var merged=Merge(spans);
        return visible.All(s=>merged.Any(span=>span.Start<=s.Start&&span.End>=s.End));
    }

    public static List<IReadOnlyList<TrendPoint>> Omit(IEnumerable<IReadOnlyList<TrendPoint>> runs,IReadOnlyList<ChartSpan> spans)
    {
        var result=new List<IReadOnlyList<TrendPoint>>();
        foreach(var run in runs)
        {
            List<TrendPoint>? current=null;var cursor=0;
            void Flush(){if(current is {Count:>1})result.Add(current);current=null;}
            for(var i=1;i<run.Count;i++)
            {
                var a=run[i-1];var b=run[i];if(b.Time<a.Time){Flush();continue;}
                if(b.Time==a.Time)
                {
                    if(spans.Any(s=>a.Time>=s.Start&&a.Time<s.End))Flush();
                    else {current??=[a];current.Add(b);}
                    continue;
                }
                TrendPoint At(DateTimeOffset time)=>new(time,a.Rate+(b.Rate-a.Rate)*(time-a.Time).TotalSeconds/(b.Time-a.Time).TotalSeconds);
                void Keep(DateTimeOffset from,DateTimeOffset to){if(to<=from)return;current??=[At(from)];current.Add(At(to));}
                while(cursor<spans.Count&&spans[cursor].End<=a.Time)cursor++;
                var from=a.Time;
                for(var j=cursor;j<spans.Count&&spans[j].Start<b.Time;j++)
                {
                    var span=spans[j];if(span.Start>from)Keep(from,span.Start);Flush();
                    if(span.End>from)from=span.End;if(from>=b.Time)break;
                }
                if(from<b.Time)Keep(from,b.Time);
            }
            Flush();
        }
        return result;
    }
}
