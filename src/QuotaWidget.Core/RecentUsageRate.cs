namespace QuotaWidget.Core;

/// <summary>Retrospective average over observed time only; never extend a stale reading to now.</summary>
public sealed record RecentUsageRate(double? Rate, double Points, double CoverageMinutes, DateTimeOffset? LastObserved, bool Partial)
{
    public const int WindowMinutes=60;
    public const int MinimumCoverageMinutes=45;

    public static RecentUsageRate Build(SeriesData series,DateTimeOffset now,int pollSeconds)
    {
        // Anchor both endpoints to actual observations. Time passing alone cannot shrink
        // the denominator; never interpolate a fraction of an integer quota increment.
        var samples=series.Segments.Where(s=>s.End<=now&&s.Minutes>0).ToArray();
        if(samples.Length==0||!samples[^1].Valid)return new(null,0,0,null,true);
        var last=samples[^1].End;var cursor=last;var group=samples[^1].Group;
        double amount=0,bestDistance=double.PositiveInfinity;RecentUsageRate? best=null;
        for(var i=samples.Length-1;i>=0;i--)
        {
            var s=samples[i];
            if(!s.Valid||s.Group!=group||s.End!=cursor||!double.IsFinite(s.Delta)||s.Delta<0)break;
            amount+=s.Delta;cursor=s.Start;
            var minutes=(last-cursor).TotalMinutes;
            if(minutes>75)break;
            if(minutes<MinimumCoverageMinutes)continue;
            var distance=Math.Abs(minutes-WindowMinutes);
            if(distance>bestDistance)continue;
            bestDistance=distance;
            var stale=(now-last).TotalSeconds>Math.Max(60,pollSeconds)*RateEngine.GapFactor+60;
            best=new(amount*60/minutes,amount,minutes,last,stale||minutes<55||minutes>65);
        }
        return best??new(null,0,0,last,true);
    }
}
