namespace QuotaWidget.Core;

public sealed record TrendPoint(DateTimeOffset Time, double Rate);
public sealed record TrendRun(IReadOnlyList<TrendPoint> Points, double Delta, double KernelMinutes,
    bool HardStart = false, bool HardEnd = false, bool Provisional = false, bool LimitedSupport = false);

/// <summary>
/// Retrospective display estimate, NOT a more precise server observation.
/// Convolve measured interval rates with a configurable raised-cosine kernel.
/// Reflect at each contiguous run's boundaries: constants and integrated consumption
/// are preserved without leaking through gaps, resets or the selected time range.
/// The rendered polyline is normalized to the observed sum, so its area is that sum.
/// </summary>
public sealed class RateTrend
{
    public List<TrendRun> Runs { get; } = [];
    public double Delta => Runs.Where(r=>!r.Provisional).Sum(r => r.Delta);
    public bool IsProvisional(DateTimeOffset time)=>Runs.Any(r=>r.Provisional&&time>=r.Points[0].Time&&time<=r.Points[^1].Time);
    // A startup average conserves observed quota, but sparse readings cannot yet
    // establish a local peak. Keep its curve and hover without promoting it to a label.
    public bool CanLabelPeak(DateTimeOffset time)=>Runs.Any(r=>time>=r.Points[0].Time&&time<=r.Points[^1].Time)&&
        !Runs.Any(r=>(r.Provisional||r.LimitedSupport)&&time>=r.Points[0].Time&&time<=r.Points[^1].Time);

    public static RateTrend Build(SeriesData source, DateTimeOffset start, DateTimeOffset end, double windowMinutes = 120,
        IReadOnlyList<DateTimeOffset>? boundaries = null, IReadOnlyList<WorkSpan>? activity = null)
    {
        var result = new RateTrend();
        var run = new List<RateSegment>();
        if(activity is {Count:>0})
        {
            source=WorkActivity.Constrain(source,activity,start,end);
            boundaries=WorkActivity.Edges(activity);
        }
        var cuts = (boundaries ?? []).Where(t => t >= start && t <= end).Distinct().Order().ToArray();
        var cutIndex = 0;
        foreach (var s in source.Segments)
        {
            // Match SumRange exactly. Never guess a partial interval's consumption.
            if (!s.Valid || s.Start < start || s.End > end || s.Minutes <= 0)
            { Flush(); continue; }
            if (run.Count > 0 && (run[^1].End != s.Start || run[^1].Group != s.Group)) Flush();
            while (cutIndex < cuts.Length && cuts[cutIndex] <= s.Start)
            {
                if (cuts[cutIndex] == s.Start) Flush();
                cutIndex++;
            }
            var from = s.Start;
            double assigned = 0;
            while (cutIndex < cuts.Length && cuts[cutIndex] < s.End)
            {
                var to = cuts[cutIndex++];
                var delta = s.Delta * (to - from).TotalSeconds / (s.End - s.Start).TotalSeconds;
                AddPiece(from, to, delta); assigned += delta;
                Flush(); from = to;
            }
            AddPiece(from, s.End, s.Delta - assigned);

            // Splitting an observation interval preserves its measured amount. A boundary
            // does not prove account-wide inactivity: never erase usage outside local sessions.
            void AddPiece(DateTimeOffset a, DateTimeOffset b, double delta) => run.Add(new()
                { Start = a, End = b, Delta = delta, Group = s.Group });
        }
        Flush();
        return result;

        void Flush()
        {
            if (run.Count == 0) return;
            var origin = run[0].Start;
            var duration = (run[^1].End - origin).TotalMinutes;
            var count = (int)Math.Clamp(Math.Ceiling(duration), 32, 2048);
            // Long archive views use a coarser display estimate. Widen the kernel with the
            // vertex spacing so a short burst between vertices cannot vanish entirely.
            var radius = Math.Min(duration, Math.Max(Math.Clamp(windowMinutes, 30, 180) / 2, 2 * duration / count));
            var bins = run.Where(s => s.Rate > 0).SelectMany(s =>
            {
                var a = (s.Start - origin).TotalMinutes;
                var b = (s.End - origin).TotalMinutes;
                return new[] { (a, b, s.Rate), (-b, -a, s.Rate), (2 * duration - b, 2 * duration - a, s.Rate) };
            }).OrderBy(b => b.Item1).ToArray();
            var rates = new double[count + 1];
            var left = 0;
            for (var i = 0; i <= count; i++)
            {
                var x = duration * i / count;
                while (left < bins.Length && bins[left].Item2 <= x - radius) left++;
                for (var j = left; j < bins.Length && bins[j].Item1 < x + radius; j++)
                {
                    var (a, b, rate) = bins[j];
                    rates[i] += rate * (Cdf(x - a, radius) - Cdf(x - b, radius));
                }
                rates[i] = Math.Max(0, rates[i]); // protect from floating-point cancellation
            }
            var delta = run.Sum(s => s.Delta);
            var area = (rates.Sum() - (rates[0] + rates[^1]) / 2) * duration / count / 60;
            var factor = area > 0 ? delta / area : 0;
            var points = Enumerable.Range(0, count + 1)
                .Select(i => new TrendPoint(i == 0 ? origin : i == count ? run[^1].End : origin.AddMinutes(duration * i / count), rates[i] * factor)).ToArray();
            result.Runs.Add(new(points, delta, 2 * radius,
                Array.BinarySearch(cuts, origin) >= 0, Array.BinarySearch(cuts, run[^1].End) >= 0));
            run.Clear();
        }
    }

    static double Cdf(double x, double radius)
    {
        if (x <= -radius) return 0;
        if (x >= radius) return 1;
        return (x + radius) / (2 * radius) + Math.Sin(Math.PI * x / radius) / (2 * Math.PI);
    }

    public double? ValueAt(DateTimeOffset time)
    {
        // At a hard boundary the right-hand segment owns the timestamp.
        foreach (var run in Runs.AsEnumerable().Reverse())
        {
            var p = run.Points;
            if (time < p[0].Time || time > p[^1].Time) continue;
            if (time == p[^1].Time && run.HardEnd) return 0;
            int lo = 0, hi = p.Count - 1;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) / 2;
                if (p[mid].Time <= time) lo = mid; else hi = mid;
            }
            var fraction = (time - p[lo].Time).TotalSeconds / (p[hi].Time - p[lo].Time).TotalSeconds;
            return p[lo].Rate + (p[hi].Rate - p[lo].Rate) * fraction;
        }
        return null;
    }
}
