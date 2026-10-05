namespace QuotaWidget.Core;

/// <summary>Display-only pairing of nearby quota counter updates. Never rewrites observations.</summary>
public sealed record QuotaAlignment(SeriesData Total,SeriesData Fable,int MatchedBatches,double PendingDifference)
{
    public const double MaxBatchMinutes=30;

    public static QuotaAlignment Build(SeriesData total,SeriesData fable,DateTimeOffset start,DateTimeOffset end,
        IReadOnlyList<DateTimeOffset>? boundaries = null)
    {
        var t=total.Segments.ToArray();var f=fable.Segments.ToArray();
        var batch=new List<(int T,int F)>();var ti=0;var fi=0;var matched=0;
        double sumT=0,sumF=0;double pending=0;
        DateTimeOffset? previousEnd=null;int? groupT=null,groupF=null;
        void Clear(){batch.Clear();sumT=sumF=0;}
        void Break(){pending=sumT-sumF;Clear();previousEnd=null;groupT=groupF=null;}
        while(ti<t.Length&&fi<f.Length)
        {
            var a=t[ti];var b=f[fi];
            if(a.Start!=b.Start||a.End!=b.End)
            {
                Break();
                if(a.End<=b.End)ti++;
                if(b.End<=a.End)fi++;
                continue;
            }
            if(!a.Valid||!b.Valid||a.Start<start||a.End>end||a.Minutes<=0||a.Delta<0||b.Delta<0)
            {Break();ti++;fi++;continue;}
            // Counter alignment must not transfer usage across a known session edge either.
            if (boundaries?.Any(at => at > a.Start && at < a.End) == true)
            {Break();ti++;fi++;continue;}
            if (boundaries?.Contains(a.Start) == true) Break();
            if(previousEnd!=a.Start||groupT!=a.Group||groupF!=b.Group)Break();
            previousEnd=a.End;groupT=a.Group;groupF=b.Group;
            if(batch.Count>0&&(a.End-t[batch[0].T].Start).TotalMinutes>MaxBatchMinutes)Clear();
            if(batch.Count>0||a.Delta>0||b.Delta>0)
            {
                batch.Add((ti,fi));sumT+=a.Delta;sumF+=b.Delta;
                if(sumT>0&&sumF>0&&Math.Abs(sumT-sumF)<=1e-9&&
                    (a.End-t[batch[0].T].Start).TotalMinutes<=MaxBatchMinutes)
                {
                    if(batch.Any(p=>Math.Abs(t[p.T].Delta-f[p.F].Delta)>1e-9))
                    {
                        // Use the same observed-time weights, but conserve each counter's own mass.
                        var weights=batch.Select(p=>(t[p.T].Delta+f[p.F].Delta)/(sumT+sumF)).ToArray();
                        for(var i=0;i<batch.Count;i++)
                        {
                            var p=batch[i];t[p.T]=Copy(t[p.T],sumT*weights[i]);f[p.F]=Copy(f[p.F],sumF*weights[i]);
                        }
                        matched++;
                    }
                    Clear();
                }
            }
            pending=sumT-sumF;ti++;fi++;
        }
        return new(new(){Key=total.Key,Segments=t.ToList()},new(){Key=fable.Key,Segments=f.ToList()},matched,pending);
    }

    static RateSegment Copy(RateSegment s,double delta)=>new()
    {
        Start=s.Start,End=s.End,Delta=delta,Issue=s.Issue,Label=s.Label,Group=s.Group,
        Smooth=s.Smooth,SmoothMinutes=s.SmoothMinutes,
    };
}
