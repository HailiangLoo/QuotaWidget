namespace QuotaWidget.Core;

/// <summary>One estimate per continuous work/observation context, independent of the viewport.
/// A clean counter reset may share rate context without joining its accounting periods.
/// Integer increments are spread between observations on the shared active clock, not
/// assigned as separate bills to individual tasks. No token-to-quota conversion.</summary>
public static class ActiveRateEstimator
{
    sealed record Span(DateTimeOffset Start,DateTimeOffset End,bool HardStart,bool HardEnd,double U)
    {public double Minutes=>(End-Start).TotalMinutes;public double V=>U+Minutes;}

    public static RateTrend Build(SeriesData source,DateTimeOffset asOf,IReadOnlyList<WorkSpan>? activity=null,
        double maxWindowMinutes=120,double quantum=1,IReadOnlyList<DateTimeOffset>? fallbackEdges=null,IReadOnlyList<DateTimeOffset>? contextEdges=null)
    {
        var contexts=(contextEdges??[]).Where(t=>t<asOf).Distinct().Order().ToArray();
        activity=WorkActivity.Episodes(activity??[],asOf,contexts);
        // Only the estimation copy may span a counter reset. The caller's original
        // source still owns cumulative points, coverage, reset labels and recent-hour facts.
        var continuity=TrendContinuity.Prepare(source,asOf,activity);
        var separated=RateContexts.Separate(continuity.Source,contexts,activity);
        source=separated.Source;
        var result=new RateTrend();var run=new List<RateSegment>();var afterBreak=false;
        foreach(var s in source.Segments)
        {
            if(s.End>asOf)break;
            if(!s.Valid||s.Minutes<=0||!double.IsFinite(s.Delta)||s.Delta<0)
            {
                Flush(closedByReset:s.Issue==SegmentIssue.Reset,closedByBreak:true);
                afterBreak=true;continue;
            }
            if(run.Count>0&&(run[^1].End!=s.Start||run[^1].Group!=s.Group)){Flush(closedByBreak:true);afterBreak=true;}
            run.Add(s);
        }
        Flush();
        for(var i=0;i<result.Runs.Count;i++)
            if(separated.Ambiguous.Any(s=>s.Start<result.Runs[i].Points[^1].Time&&s.End>result.Runs[i].Points[0].Time))
                result.Runs[i]=result.Runs[i] with{LimitedSupport=true};
        result.Runs.Sort((a,b)=>a.Points[0].Time.CompareTo(b.Points[0].Time));return result;

        void Flush(bool closedByReset=false,bool closedByBreak=false)
        {
            if(run.Count==0)return;
            var origin=run[0].Start;var end=run[^1].End;
            var spans=new List<Span>();double activeMinutes=0;
            if(activity is {Count:>0})
            {
                // Unknown edges use the OBSERVATION segment, never the selected view.
                var support=activity.Where(a=>a.End>origin&&a.Start<end).Select(a=>a with
                {Start=a.KnownStart&&a.Start>origin?a.Start:origin,End=a.KnownEnd&&a.End<end?a.End:end,
                 KnownStart=a.KnownStart&&a.Start>=origin,KnownEnd=a.KnownEnd&&a.End<=end});
                foreach(var a in WorkActivity.Merge(support,TimeSpan.Zero))
                {
                    spans.Add(new(a.Start,a.End,a.KnownStart,a.KnownEnd,activeMinutes));
                    activeMinutes+=(a.End-a.Start).TotalMinutes;
                }
            }
            if(spans.Count==0)
            {
                if(activity.Count==0){Fallback();return;}
                result.Unlocated.AddRange(run.Where(s=>s.Delta>0));
                Zero(origin,end);run.Clear();return;
            }
            var jumps=new List<(double U,double Delta)>();
            foreach(var s in run.Where(s=>s.Delta>0))
            {
                var overlap=spans.LastOrDefault(a=>a.End>s.Start&&a.Start<s.End);
                double? u=null;
                if(overlap is not null)
                    u=overlap.U+((overlap.End<s.End?overlap.End:s.End)-overlap.Start).TotalMinutes;
                else
                {
                    var previous=spans.LastOrDefault(a=>a.HardEnd&&a.End<=s.Start);
                    // Only the first post-completion observation, plus a short cache grace.
                    // A long backoff is not evidence for arbitrarily delayed attribution.
                    var grace=TimeSpan.FromSeconds(Math.Clamp(s.Minutes*60,60,600)+60);
                    if(previous is not null&&s.Start-previous.End<=TimeSpan.FromSeconds(60)&&s.End-previous.End<=grace)
                        u=previous.V;
                }
                if(u is not { } at||at<=0){result.Unlocated.Add(s);continue;}
                if(jumps.Count>0&&Math.Abs(at-jumps[^1].U)<1e-8)jumps[^1]=(at,jumps[^1].Delta+s.Delta);
                else jumps.Add((at,s.Delta));
            }
            // Unmatched increments stay at their observed sample intervals. Do not
            // spread them across hours of confirmed idle, or let one unmatched sample
            // switch the entire continuous context back to wall-clock smoothing.
            var idleFrom=origin;
            foreach(var span in spans)
            {
                if(span.HardStart&&span.Start>idleFrom)Zero(idleFrom,span.Start);
                idleFrom=span.End;
            }
            if(spans[^1].HardEnd&&idleFrom<end)Zero(idleFrom,end);
            if(jumps.Count==0)
            {
                Zero(origin,end);run.Clear();return;
            }
            var clock=DateTimeOffset.UnixEpoch;var bins=new List<RateSegment>();double from=0;
            foreach(var jump in jumps)
            {
                bins.Add(new(){Start=clock.AddMinutes(from),End=clock.AddMinutes(jump.U),Delta=jump.Delta});from=jump.U;
            }
            // Roughly 2–3 counter updates on the active clock, capped by the user's
            // smoothing preference. Idle wall time never flattens an entire day's work.
            var lengths=bins.Select(s=>s.Minutes).Order().ToArray();
            var adaptive=Math.Min(Math.Clamp(maxWindowMinutes,30,180),Math.Max(30,3*lengths[lengths.Length/2]));
            // A completed episode followed by a full, valid idle observation closes the
            // unreported tail even if the integer counter never changes again. Require
            // actual samples (including the CLI's short cache grace), not elapsed time.
            // Preserve this closure when a later episode starts with no new counter jump.
            // A reset closes the old counter: do not leave an unreported prediction
            // permanently appended to a completed accounting period. The reset interval
            // itself remains unknown and is never assigned to either side.
            // An observation break also closes the estimate of still-open work at
            // the last valid sample. Settle its trailing zero increments instead of
            // freezing a live prediction next to a now-historical gap. This does not
            // settle a completed task whose reporting delay lacks an idle sample.
            // After any break, fewer than three quanta cannot identify a local peak.
            // Include unchanged readings, but do not pool them into the positive
            // prefix: that would erase all shape until the third quantum arrived.
            var warmingUp=afterBreak&&jumps.Sum(j=>j.Delta)<3*Math.Max(.0001,quantum)-1e-8;
            var observedThrough=closedByReset||(closedByBreak&&!spans[^1].HardEnd)||warmingUp?activeMinutes:from;var idleSample=0;
            for(var i=0;i<spans.Count;i++)
            {
                var span=spans[i];if(!span.HardEnd||span.V<=observedThrough)continue;
                var next=i+1<spans.Count?spans[i+1].Start:end;
                while(idleSample<run.Count&&(run[idleSample].Start<span.End||run[idleSample].End<span.End.AddMinutes(1)))idleSample++;
                if(idleSample<run.Count&&run[idleSample].End<=next)observedThrough=span.V;
            }
            // Zero is the observed counter increment, not a claim that each active
            // minute cost nothing. Smooth this final interval with the preceding bins;
            // its area is redistributed from the observed total, never added to it.
            if(observedThrough>from+1e-8)bins.Add(new(){Start=clock.AddMinutes(from),End=clock.AddMinutes(observedThrough),Delta=0});
            if(afterBreak)bins=RegularizeStart(bins,quantum);
            if(warmingUp)bins=SettleSparseTail(bins,quantum);
            var confirmed=RateTrend.Build(new(){Key=source.Key,Segments=bins},clock,clock.AddMinutes(observedThrough),adaptive);
            var points=confirmed.Runs.Single().Points;
            Map(points,0,observedThrough,false,confirmed.Runs.Single().KernelMinutes);
            // Only the remaining, unsettled activity may use a bounded live estimate.
            // This internal flag excludes unobserved quota from totals and peak labels;
            // the UI uses one solid stroke for the entire estimated rate curve.
            if(activeMinutes>observedThrough+1e-8)
            {
                // An unchanged integer reading is not a completion event. Continue
                // from the observed endpoint with a diminishing estimate, with no
                // finite artificial zero. Widening steps keep long tails inexpensive.
                // The infinite trapezoid series sums to one quantum:
                // rate * step * (1+decay) / (120 * (1-growth*decay)).
                var rate=points[^1].Rate;
                var pendingFrom=observedThrough;
                // Until the first new-period increment arrives, a saturated old
                // counter cannot imply deceleration. Carry its estimated endpoint
                // through the reset; only the new counter's unchanged readings use
                // the usual one-quantum decay budget. Subsequent increments replace
                // this prediction with the shared, area-constrained estimate above.
                foreach(var reset in continuity.Resets.Where(r=>r.StartsAtCapacity&&r.Start>=origin&&r.End<=end))
                {
                    var span=spans.FirstOrDefault(a=>a.Start<=reset.End&&a.End>=reset.End);
                    if(span is not null)pendingFrom=Math.Max(pendingFrom,span.U+(reset.End-span.Start).TotalMinutes);
                }
                var pending=new List<TrendPoint>{new(clock.AddMinutes(observedThrough),rate)};
                if(pendingFrom>observedThrough)pending.Add(new(clock.AddMinutes(pendingFrom),rate));
                pending.AddRange(DecayTail(clock.AddMinutes(pendingFrom),clock.AddMinutes(activeMinutes),rate,quantum).Skip(1));
                Map(pending,observedThrough,activeMinutes,true,0);
            }
            run.Clear();

            void Map(IReadOnlyList<TrendPoint> values,double a,double b,bool provisional,double kernel)
            {
                foreach(var span in spans)
                {
                    var left=Math.Max(a,span.U);var right=Math.Min(b,span.V);if(right<=left)continue;
                    var cut=ChartPath.Clip([values],clock.AddMinutes(left),clock.AddMinutes(right)).Single();
                    var mapped=cut.Select(p=>new TrendPoint(span.Start.AddMinutes((p.Time-clock).TotalMinutes-span.U),p.Rate)).ToArray();
                    if(left==span.U)mapped[0]=mapped[0] with{Time=span.Start};
                    if(right==span.V)mapped[^1]=mapped[^1] with{Time=span.End};
                    result.Runs.Add(new(mapped,Area(mapped),kernel,span.HardStart&&left==span.U,span.HardEnd&&right==span.V,provisional,warmingUp));
                }
            }
            void Fallback()
            {
                // A coarse session marker is only a hint. If observations on both sides
                // contain consumption, it cannot establish an inactive boundary.
                var cuts=(fallbackEdges??[]).Where(t=>t>=origin&&t<=end).Distinct().Order().ToArray();
                var inner=cuts.Where(t=>t>origin&&t<end).ToArray();
                var limits=new[]{origin}.Concat(inner).Append(end).ToArray();
                var consumed=limits.Zip(limits.Skip(1),(a,b)=>run.Any(s=>s.Delta>0&&s.End>a&&s.Start<b)).ToArray();
                var supported=cuts.Where(t=>t==origin||t==end||!consumed[Array.BinarySearch(inner,t)]||!consumed[Array.BinarySearch(inner,t)+1]).ToArray();
                var limited=afterBreak&&run.Sum(s=>s.Delta)<3*Math.Max(.0001,quantum)-1e-8;
                var bins=afterBreak?RegularizeStart(run,quantum):run.ToList();
                if(limited)bins=SettleSparseTail(bins,quantum);
                var estimate=RateTrend.Build(new(){Key=source.Key,Segments=bins},origin,end,maxWindowMinutes,supported);
                result.Runs.AddRange(estimate.Runs.Select(r=>r with{LimitedSupport=limited}));run.Clear();
            }
            void Zero(DateTimeOffset a,DateTimeOffset b)=>result.Runs.Add(new([new(a,0),new(b,0)],0,0));
        }
    }
    // The first counter jump may start part-way through one quantum. Use up to the
    // following two quanta to reduce only an unusually fast first interval, by at
    // most one quantum. Keep every later inter-jump duration/rate contrast instead
    // of collapsing three observations to a rectangular average. Renormalizing the
    // prefix conserves its observed amount; no rate is borrowed across a break.
    static List<RateSegment> RegularizeStart(IReadOnlyList<RateSegment> bins,double quantum)
    {
        quantum=Math.Max(.0001,quantum);
        var count=0;double delta=0;
        var lastPositive=bins.Count-1;
        while(lastPositive>=0&&bins[lastPositive].Delta<=0)lastPositive--;
        while(count<=lastPositive&&delta<3*quantum-1e-8)delta+=bins[count++].Delta;
        if(count==0)return bins.ToList();
        var prefix=new List<RateSegment>();var from=bins[0].Start;
        foreach(var bin in bins.Take(count))
        {
            if(bin.Delta<=0)continue;
            prefix.Add(new(){Start=from,End=bin.End,Delta=bin.Delta,Group=bin.Group});from=bin.End;
        }
        if(prefix.Count>1)
        {
            var first=prefix[0];var remaining=(prefix[^1].End-first.End).TotalMinutes;
            var expected=(delta-first.Delta)/remaining*first.Minutes;
            var correction=Math.Min(quantum,Math.Max(0,first.Delta-expected));
            if(correction>=delta)correction=0; // sub-ULP input must not produce an infinite scale
            var factor=delta/(delta-correction);
            prefix=prefix.Select((s,i)=>new RateSegment{Start=s.Start,End=s.End,Delta=(s.Delta-(i==0?correction:0))*factor,Group=s.Group}).ToList();
        }
        return prefix.Concat(bins.Skip(count)).ToList();
    }
    // A sparse context has no reliable instantaneous rate. Retain the information
    // in later unchanged samples using the same bounded decay as a live tail, then
    // redistribute (not add) quota over the whole observed active context. This is a
    // display estimate: the ledger stays untouched. A lone jump with no subsequent
    // samples remains flat; work edges never manufacture within-task variation.
    static List<RateSegment> SettleSparseTail(List<RateSegment> bins,double quantum)
    {
        var last=bins.FindLastIndex(s=>s.Delta>0);
        if(last<0||last==bins.Count-1)return bins;
        var total=bins.Sum(s=>s.Delta);var head=bins[last];
        var tail=DecayTail(head.End,bins[^1].End,head.Rate,quantum);
        var factor=total/(total+Area(tail));
        var result=bins.Take(last+1).Select(s=>new RateSegment{Start=s.Start,End=s.End,Delta=s.Delta*factor,Group=s.Group}).ToList();
        result.AddRange(tail.Zip(tail.Skip(1),(a,b)=>new RateSegment{Start=a.Time,End=b.Time,
            Delta=(a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours*factor,Group=head.Group}));
        return result;
    }
    static List<TrendPoint> DecayTail(DateTimeOffset start,DateTimeOffset end,double rate,double quantum)
    {
        var duration=(end-start).TotalMinutes;
        const double decay=.8,growth=1.1;
        var step=rate>0?120*Math.Max(.0001,quantum)*(1-growth*decay)/(rate*(1+decay)):duration;
        var points=new List<TrendPoint>{new(start,rate)};
        double elapsed=0;
        while(elapsed<duration)
        {
            var dt=Math.Min(step,duration-elapsed);
            rate*=1-(1-decay)*dt/step;elapsed+=dt;
            points.Add(new(elapsed==duration?end:start.AddMinutes(elapsed),rate));step*=growth;
        }
        return points;
    }
    static double Area(IReadOnlyList<TrendPoint> points)=>points.Zip(points.Skip(1),(a,b)=>(a.Rate+b.Rate)/2*(b.Time-a.Time).TotalHours).Sum();
}
