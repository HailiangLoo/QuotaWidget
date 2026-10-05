namespace QuotaWidget.Core;

public enum AxisLandmarkKind { Regular, Stage, Peak, Accumulation }
public sealed record AxisLandmark(DateTimeOffset Time,AxisLandmarkKind Kind,double Importance);
public sealed record AxisTick(DateTimeOffset Time,AxisLandmarkKind Kind,double X,double Left,double Width);

/// <summary>Choose labels, never move data or change the shared time mapping.</summary>
public static class TimeAxis
{
    public static IEnumerable<AxisLandmark> RateLandmarks(IEnumerable<DateTimeOffset> edges,IEnumerable<ChartPeak> peaks)
    {
        foreach(var edge in edges)yield return new(edge,AxisLandmarkKind.Stage,3);
        // A clipped endpoint or a wholly flat run is not an informative peak timestamp.
        var meaningful=peaks.Where(p=>!p.Boundary&&double.IsFinite(p.Prominence)&&p.Prominence>0).ToArray();
        var max=meaningful.Select(p=>p.Prominence).DefaultIfEmpty(1).Max();
        foreach(var p in meaningful)yield return new(p.Time,AxisLandmarkKind.Peak,1+p.Prominence/max);
    }

    // A monotone cumulative curve has no useful rate peaks. Use the observed start/end
    // of rising blocks instead: entry to a plateau and resumption of accumulation.
    public static IReadOnlyList<AxisLandmark> CumulativeLandmarks(CumulativeSeries series)
    {
        var blocks=new List<(DateTimeOffset Start,DateTimeOffset End,double Delta)>();
        DateTimeOffset? start=null;DateTimeOffset end=default;double delta=0;CumulativeSegment? previous=null;
        void Flush(){if(start is {} at)blocks.Add((at,end,delta));start=null;delta=0;}
        foreach(var s in series.Segments)
        {
            if(previous is not null&&(s.Start!=previous.End||s.Group!=previous.Group))Flush();
            if(s.To>s.From){start??=s.Start;end=s.End;delta+=s.To-s.From;}else Flush();
            previous=s;
        }
        Flush();
        var max=blocks.Select(b=>b.Delta).DefaultIfEmpty(1).Max();
        return blocks.SelectMany((b,i)=>new[]{new AxisLandmark(b.Start,AxisLandmarkKind.Accumulation,i==0?3.25:2+b.Delta/max),
            new AxisLandmark(b.End,AxisLandmarkKind.Accumulation,i==blocks.Count-1?3.25:2+b.Delta/max)}).ToArray();
    }

    sealed record Option(AxisTick Tick,double Score,int Region,double Left,double Right);
    sealed record Path(AxisTick Tick,double Score,int Region,Path? Previous);

    public static IReadOnlyList<AxisTick> Select(DateTimeOffset start,DateTimeOffset end,double width,
        IEnumerable<AxisLandmark> landmarks,Func<DateTimeOffset,double> measure)
    {
        if(end<=start||!double.IsFinite(width)||width<=0)return [];
        const double gap=12;
        // Dense, continuous curves have many legitimate peaks. Their labels should
        // remain sparse and spread out even when all the text could physically fit.
        var limit=Math.Clamp((int)Math.Round(width/75),3,6);
        var spacing=.65*width/(limit-1);
        var span=end-start;
        AxisTick? Tick(DateTimeOffset time,AxisLandmarkKind kind)
        {
            var x=(time-start).TotalSeconds/span.TotalSeconds*width;var w=measure(time);
            if(!double.IsFinite(w)||w<=0||w>width)return null;
            var left=Math.Clamp(x-w/2,0,width-w);
            return new(time,kind,x,left,w);
        }
        Option Reserve(AxisTick tick,double score,int region)=>new(tick,score,region,
            Math.Min(tick.Left-gap/2,tick.X-spacing/2),Math.Max(tick.Left+tick.Width+gap/2,tick.X+spacing/2));
        var anchors=landmarks.Where(a=>a.Time>=start&&a.Time<=end&&a.Kind!=AxisLandmarkKind.Regular&&double.IsFinite(a.Importance)&&a.Importance>0)
            .GroupBy(a=>a.Time).Select(g=>g.OrderByDescending(a=>a.Importance).ThenBy(a=>a.Kind).First())
            .OrderBy(a=>a.Time).Select(a=>(Mark:a,Tick:Tick(a.Time,a.Kind))).Where(a=>a.Tick is not null).ToArray();
        var max=anchors.Select(a=>a.Mark.Importance).DefaultIfEmpty(1).Max();
        var options=new List<Option>();var region=0;AxisTick? previous=null;
        foreach(var a in anchors)
        {
            var tick=a.Tick!;
            // Label neighborhoods, not inferred work sessions: a separated cluster gets
            // a first label before one dense cluster can consume the entire label budget.
            if(previous is not null&&tick.X-previous.X>Math.Max(tick.Width,previous.Width)+2*gap)region++;
            // Several small wiggles must not outvote two prominent crests just
            // because selecting more labels adds more score.
            options.Add(Reserve(tick,Math.Pow(a.Mark.Importance/max,2),region));previous=tick;
        }
        // Range labels provide context when there is room. A nearby actual event takes
        // precedence; omitting a label never changes the time range or stretches the plot.
        foreach(var time in new[]{start,end})
            if(!anchors.Any(a=>a.Mark.Time==time)&&Tick(time,AxisLandmarkKind.Regular) is {} tick)options.Add(Reserve(tick,.25,-1));
        var ordered=options.OrderBy(o=>o.Right).ThenBy(o=>o.Tick.Time).ToArray();
        var best=new (Path? First,Path? Second)[ordered.Length+1,limit+1];
        for(var i=0;i<ordered.Length;i++)
        {
            var option=ordered[i];var tick=option.Tick;
            // Reserved intervals enforce both text clearance and spread. Label and
            // tick positions themselves stay at the exact candidate timestamps.
            int lo=0,hi=i;
            while(lo<hi){var mid=(lo+hi)/2;if(ordered[mid].Right<=option.Left)lo=mid+1;else hi=mid;}
            for(var count=1;count<=limit;count++)
            {
                var pair=best[i,count];
                void Extend(Path? path)
                {
                    var firstInRegion=option.Region>=0&&path?.Region!=option.Region;
                    var next=new Path(tick,(path?.Score??0)+option.Score+(firstInRegion?100:0),option.Region>=0?option.Region:path?.Region??-1,path);
                    pair=Keep(pair,next);
                }
                if(count==1)Extend(null);
                else
                {
                    var prior=best[lo,count-1];
                    if(prior.First is {} first)Extend(first);
                    if(prior.Second is {} second)Extend(second);
                }
                best[i+1,count]=pair;
            }
        }
        var winner=Enumerable.Range(1,limit).Select(c=>best[ordered.Length,c].First).Where(p=>p is not null).OrderByDescending(p=>p!.Score).FirstOrDefault();
        var selected=new List<AxisTick>();for(var p=winner;p is not null;p=p.Previous)selected.Add(p.Tick);
        selected.Reverse();return selected;
    }

    // Keep the best paths for two distinct last regions. For the next event, either
    // the best path already covers its region, or the second path earns first coverage.
    // Regions are ordered in time, so this is enough; no quadratic event-pair scan.
    static (Path? First,Path? Second) Keep((Path? First,Path? Second) pair,Path next)
    {
        if(pair.First is null)return(next,null);
        if(pair.First.Region==next.Region)return(next.Score>pair.First.Score?next:pair.First,pair.Second);
        if(next.Score>pair.First.Score)return(next,pair.First);
        if(pair.Second is null||next.Score>pair.Second.Score)return(pair.First,next);
        return pair;
    }
}
