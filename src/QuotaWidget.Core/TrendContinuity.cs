namespace QuotaWidget.Core;

/// <summary>Accounting boundaries are not necessarily changes in work intensity.
/// The original series remains the ledger. This copy shares estimation context only
/// across a normally sampled counter reset inside one continuous work episode.</summary>
static class TrendContinuity
{
    public static (SeriesData Source,List<RateSegment> Resets) Prepare(SeriesData source,
        DateTimeOffset asOf,IReadOnlyList<WorkSpan> episodes)
    {
        var resets=new List<RateSegment>();
        RateSegment? previous=null;
        foreach(var s in source.Segments)
        {
            if(s.End>asOf)break;
            if(s.Issue==SegmentIssue.Reset&&s.CounterResetOnly&&previous is {Valid:true}&&previous.End==s.Start&&
                episodes.Any(a=>a.Start<s.Start&&a.End>=s.End&&(!a.KnownEnd||a.End>s.End)))resets.Add(s);
            previous=s;
        }
        if(resets.Count==0)return(source,resets);
        var bridges=resets.ToHashSet();var group=0;previous=null;
        var segments=new List<RateSegment>();
        foreach(var s in source.Segments)
        {
            if(s.End>asOf)break;
            var bridge=bridges.Contains(s);
            if(!bridge&&(!s.Valid||previous is not null&&(previous.End!=s.Start||previous.Group!=s.Group)))group++;
            // Zero is only the ledger contribution of an unmeasured crossing, NOT a
            // zero-rate observation. The active-clock estimator spreads the surrounding
            // positive increments over this shared context. It does not create quota.
            segments.Add(new(){Start=s.Start,End=s.End,Delta=bridge?0:s.Delta,
                Issue=bridge?SegmentIssue.None:s.Issue,Label=s.Label,Group=group,
                CounterResetOnly=s.CounterResetOnly,StartsAtCapacity=s.StartsAtCapacity});
            previous=s;
        }
        return(new(){Key=source.Key,Segments=segments},resets);
    }
}
