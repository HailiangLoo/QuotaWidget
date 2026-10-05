using QuotaWidget.Core;

static class RecentUsageRateTests
{
    public static void Register(List<(string Name,Func<Task> Body)> tests)
    {
        var now=new DateTimeOffset(2026,10,2,12,0,0,TimeSpan.FromHours(8));
        void Test(string name,Action body)=>tests.Add(("compact rate: "+name,()=>{body();return Task.CompletedTask;}));
        void Check(bool ok,string why) {if(!ok) throw new Exception(why);}
        void Near(double expected,double? actual)=>Check(actual is { } n&&Math.Abs(expected-n)<1e-8,$"expected {expected}, got {actual}");
        RateSegment S(double a,double b,double delta,SegmentIssue issue=SegmentIssue.None)=>new(){Start=now.AddMinutes(a),End=now.AddMinutes(b),Delta=delta,Issue=issue};
        RecentUsageRate Build(params RateSegment[] segments)=>RecentUsageRate.Build(new SeriesData{Key=SeriesKey.Total,Segments=segments.ToList()},now,300);
        Test("uses two real endpoints without splitting integer increments or future readings",()=>
        {
            var rate=Build(S(-70,-64,9),S(-64,-5,4),S(-5,1,90));
            Near(240d/59,rate.Rate); Near(4,rate.Points); Near(59,rate.CoverageMinutes);
            Check(!rate.Partial&&rate.LastObserved==now.AddMinutes(-5),"normal latest poll treated as stale or future used");
            Check(Build(S(-125,-5,12)).Rate is null,"invented a fractional observation");
        });
        Test("missing and reset intervals are excluded from both amount and duration",()=>
        {
            var rate=Build(S(-60,-50,1),S(-50,-20,90,SegmentIssue.Gap),S(-20,-10,2),S(-10,-7.5,99,SegmentIssue.Reset),S(-7.5,-5,-5),S(-5,0,double.NaN));
            Check(rate.Rate is null,"short disjoint observations masquerade as a continuous hour");
            Check(rate.Partial,"incomplete coverage not disclosed");
        });
        Test("thin or absent coverage stays unknown, observed zero remains zero",()=>
        {
            Check(Build().Rate is null&&Build(S(-14.99,0,1)).Rate is null,"warmup jump became hourly rate");
            var zero=Build(S(-60,0,0)); Near(0,zero.Rate); Check(!zero.Partial,"observed full-hour zero missing");
            Check(Build(S(-120,-60,9)).Partial,"old cutoff appears current");
        });
        Test("small known gaps and a stale tail remain marked",()=>
        {
            var littleGap=Build(S(-60,-35,1),S(-35,-32.5,1,SegmentIssue.Gap),S(-32.5,0,2));
            Check(littleGap.Partial,"short real gap hidden by high coverage");
            var stale=Build(S(-71,-11,4)); Near(4,stale.Rate); Check(stale.Partial,"stale average appears current");
        });
        Test("without a fresh observation neither amount nor denominator moves",()=>
        {
            var data=new SeriesData{Key=SeriesKey.Total,Segments=[S(-60,0,4)]};
            var a=RecentUsageRate.Build(data,now,300);var b=RecentUsageRate.Build(data,now.AddMinutes(4),300);
            Near(a.Rate!.Value,b.Rate);Near(a.Points,b.Points);Near(a.CoverageMinutes,b.CoverageMinutes);
            Check(RatePresentation.Estimate(4.39)=="4.4","rate did not retain one decimal place");
        });
    }
}
