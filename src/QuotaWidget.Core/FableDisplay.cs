namespace QuotaWidget.Core;

public sealed record ModelActivity(DateTimeOffset Start,DateTimeOffset End,string? Model);
public sealed record ChartSpan(DateTimeOffset Start,DateTimeOffset End);

/// <summary>Local model evidence controls visual redundancy only; never changes quota counters.</summary>
public static class FableDisplay
{
    public static bool IsFable(string? model)=>model is not null&&
        (model.Equals("fable",StringComparison.OrdinalIgnoreCase)||model.StartsWith("claude-fable-",StringComparison.OrdinalIgnoreCase));

    // Separately rounded counters and different active clocks can produce incompatible
    // component estimates. Do not clamp or invent a replacement; keep the raw counters
    // and mark only the unsupported component interval unavailable. Confirmed Fable-only
    // work uses its shared stroke/readout instead of comparing two redundant estimates.
    public static IReadOnlyList<ChartSpan> RateConflicts(RateTrend total,RateTrend fable,IReadOnlyList<ChartSpan> shared)
    {
        var knots=total.Runs.Concat(fable.Runs).SelectMany(r=>r.Points.Select(p=>p.Time))
            .Concat(shared.SelectMany(s=>new[]{s.Start,s.End}))
            .Concat(total.Unlocated.SelectMany(s=>new[]{s.Start,s.End})).Distinct().Order().ToArray();
        var conflicts=new List<ChartSpan>();
        for(var i=1;i<knots.Length;i++)
        {
            var a=knots[i-1];var b=knots[i];var mid=a+(b-a)/2;
            if(shared.Any(s=>mid>=s.Start&&mid<s.End))continue;
            // Interior probes avoid interpreting an exact lifecycle end as a
            // zero-rate sample belonging to the interval on its left.
            var p=a+(b-a)/4;var q=b-(b-a)/4;
            if(total.ValueAt(p) is not {} tp||total.ValueAt(q) is not {} tq||fable.ValueAt(p) is not {} fp||fable.ValueAt(q) is not {} fq)continue;
            var dp=fp-tp;var dq=fq-tq;var left=1.5*dp-.5*dq;var right=1.5*dq-.5*dp;
            const double epsilon=1e-6;
            if(left<=epsilon&&right<=epsilon)continue;
            if(left>epsilon&&right>epsilon){conflicts.Add(new(a,b));continue;}
            var cut=a+TimeSpan.FromTicks((long)((b-a).Ticks*Math.Clamp((epsilon-left)/(right-left),0,1)));
            conflicts.Add(left>epsilon?new(a,cut):new(cut,b));
        }
        return Merge(conflicts);
    }

    public static RateTrend Omit(RateTrend source,IReadOnlyList<ChartSpan> spans)
    {
        if(spans.Count==0)return source;
        var result=new RateTrend();result.Unlocated.AddRange(source.Unlocated);
        foreach(var run in source.Runs)
        foreach(var points in Omit([run.Points],spans))
            result.Runs.Add(run with{Points=points,Delta=points.Zip(points.Skip(1),(a,b)=>(a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours).Sum(),
                LimitedSupport=run.LimitedSupport||points[0].Time!=run.Points[0].Time||points[^1].Time!=run.Points[^1].Time,
                HardStart=run.HardStart&&points[0].Time==run.Points[0].Time,HardEnd=run.HardEnd&&points[^1].Time==run.Points[^1].Time});
        return result;
    }

    public static IReadOnlyList<ChartSpan> Build(SeriesData total,SeriesData fable,IReadOnlyList<ModelActivity> activity,
        DateTimeOffset start,DateTimeOffset end,double smoothingMinutes,bool cumulative=false,IReadOnlyList<WorkSpan>? work=null)
    {
        if(activity.Count==0)return [];
        // A mixed-model smoothing neighbourhood must keep both curves. Isolated known Fable
        // activity can cover its smoothing shoulders, not an arbitrary session or whole day.
        var radius=TimeSpan.FromMinutes(Math.Clamp(smoothingMinutes,0,180)/2);
        var known=Merge(activity.Where(a=>IsFable(a.Model)).Select(a=>new ChartSpan(a.Start-radius,a.End+radius)));
        var blocked=Merge(activity.Where(a=>!IsFable(a.Model)).Select(a=>new ChartSpan(a.Start-radius,a.End+radius)));
        if(!cumulative&&work is {Count:>0})
        {
            // Explicit lifecycle evidence outranks a request's smoothing neighbourhood.
            // A completed Opus turn cannot veto a later Fable-only turn for another hour.
            // Preserve all raw model spans: provider union may have retained just one
            // model name from concurrent work. Unknown starts/ends stay conservative.
            // Use the same confirmed short handoffs as the rate timeline. Otherwise
            // a three-second gap between two Fable turns leaves a tiny Claude stroke
            // at a different height after the redundant total line is removed.
            // Contrary model evidence below still vetoes sharing inside that handoff.
            known=Merge(WorkActivity.Merge(work.Where(s=>IsFable(s.Model)&&s.KnownStart&&s.Start<end),WorkActivity.HandoffWindow)
                .Select(s=>new ChartSpan(s.Start,s.KnownEnd&&s.End<end?s.End:end)));
            blocked=Merge(work.Where(s=>!IsFable(s.Model)&&s.Start<end)
                .Select(s=>new ChartSpan(s.KnownStart?s.Start:start,s.KnownEnd&&s.End<end?s.End:end))
                // Conflicted or contrary request metadata remains a veto even when
                // lifecycle model labels appear complete. Do not extend its time range.
                .Concat(activity.Where(s=>!IsFable(s.Model)).Select(s=>new ChartSpan(s.Start,s.End>s.Start?s.End:s.Start.AddTicks(1)))));
        }
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
            if(!cumulative&&work is {Count:>0}&&activity.Any(a=>!IsFable(a.Model)&&a.End>=s.Start&&a.Start<s.End&&
                (string.IsNullOrWhiteSpace(a.Model)||!work.Any(w=>string.Equals(w.Model,a.Model,StringComparison.OrdinalIgnoreCase)&&w.Start<=a.Start&&w.End>=a.End))))
                continue; // Contrary/unknown request evidence cannot be reduced to an invisible one-tick veto.
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
                    // A closing vertical belongs to the interval on its left; an
                    // opening vertical belongs to the right. Avoid a ghost total
                    // stroke at a shared Fable completion without erasing the next start.
                    if(spans.Any(s=>a.Rate>b.Rate?a.Time>s.Start&&a.Time<=s.End:a.Time>=s.Start&&a.Time<s.End))Flush();
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
