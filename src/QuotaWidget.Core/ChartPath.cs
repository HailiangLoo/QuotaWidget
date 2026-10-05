namespace QuotaWidget.Core;

/// <summary>Display-only clipping. Keep positive lobes and their boundary slopes,
/// but never draw a horizontal zero baseline or bridge the zero interval between lobes.</summary>
public static class ChartPath
{
    public static List<IReadOnlyList<TrendPoint>> Clip(IEnumerable<IReadOnlyList<TrendPoint>> runs,DateTimeOffset start,DateTimeOffset end)
    {
        var result=new List<IReadOnlyList<TrendPoint>>();
        foreach(var run in runs)
        {
            var points=new List<TrendPoint>();
            for(var i=1;i<run.Count;i++)
            {
                var a=run[i-1];var b=run[i];if(b.Time<start||a.Time>end)continue;
                if(a.Time==b.Time){if(a.Time>=start&&a.Time<=end){if(points.Count==0||points[^1]!=a)points.Add(a);points.Add(b);}continue;}
                var from=a.Time>start?a.Time:start;var to=b.Time<end?b.Time:end;if(to<=from)continue;
                TrendPoint At(DateTimeOffset t)=>t==a.Time?a:t==b.Time?b:new(t,a.Rate+(b.Rate-a.Rate)*(t-a.Time).TotalSeconds/(b.Time-a.Time).TotalSeconds);
                var first=At(from);if(points.Count==0||points[^1]!=first)points.Add(first);points.Add(At(to));
            }
            if(points.Count>1)result.Add(points);
        }
        return result;
    }
    public static IReadOnlyList<TrendPoint> HardEdges(TrendRun run)
    {
        var points = new List<TrendPoint>();
        if (run.HardStart && run.Points[0].Rate > 0) points.Add(new(run.Points[0].Time, 0));
        points.AddRange(run.Points);
        if (run.HardEnd && run.Points[^1].Rate > 0) points.Add(new(run.Points[^1].Time, 0));
        return points;
    }

    public static List<IReadOnlyList<TrendPoint>> PositiveRuns(IEnumerable<IReadOnlyList<TrendPoint>> runs)
    {
        var result = new List<IReadOnlyList<TrendPoint>>();
        foreach (var points in runs)
        {
            List<TrendPoint>? current = null;
            for (var i = 1; i < points.Count; i++)
            {
                var a = points[i - 1];
                var b = points[i];
                if (!double.IsFinite(a.Rate) || !double.IsFinite(b.Rate) || (a.Rate <= 0 && b.Rate <= 0))
                {
                    if (current is not null) result.Add(current);
                    current = null;
                    continue;
                }
                current ??= [a];
                current.Add(b);
            }
            if (current is not null) result.Add(current);
        }
        return result;
    }
}
