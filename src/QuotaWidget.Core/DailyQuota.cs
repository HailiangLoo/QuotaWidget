namespace QuotaWidget.Core;

public sealed record DayQuota(DateTime Day, double? Points, double CoverageMinutes, bool HasGap, bool SplitAtMidnight)
{
    public static DayQuota Empty(DateTime day) => new(day.Date,null,0,false,false);
}

/// <summary>Daily sums in native total-week points. Split valid boundary intervals by elapsed time,
/// never by the smoothed display curve, and never invent zero consumption for missing coverage.</summary>
public static class DailyQuota
{
    public static DateTimeOffset Midnight(DateTime day, TimeZoneInfo zone)
    {
        var local=DateTime.SpecifyKind(day.Date,DateTimeKind.Unspecified);
        return new(local,zone.GetUtcOffset(local));
    }

    public static IReadOnlyList<DayQuota> Build(SeriesData data, DateTime first, DateTime lastExclusive, TimeZoneInfo zone, DateTimeOffset now)
    {
        var result=new List<DayQuota>();
        for(var day=first.Date;day<lastExclusive.Date;day=day.AddDays(1))
        {
            var from=Midnight(day,zone); var to=Midnight(day.AddDays(1),zone);
            double points=0,minutes=0; bool gap=false,split=false;
            foreach(var s in data.Segments)
            {
                if(s.End<=from || s.Start>=to || s.End>now || s.Minutes<=0) continue;
                var a=s.Start>from?s.Start:from; var b=s.End<to?s.End:to;
                if(b<=a) continue;
                if(!s.Valid || !double.IsFinite(s.Delta) || s.Delta<0) {gap=true;continue;}
                var overlap=(b-a).TotalMinutes;
                points+=s.Delta*overlap/s.Minutes; minutes+=overlap;
                split|=a!=s.Start || b!=s.End;
            }
            result.Add(new(day,minutes>0?points:null,minutes,gap,split));
        }
        return result;
    }
}
