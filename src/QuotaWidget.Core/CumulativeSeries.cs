namespace QuotaWidget.Core;

public sealed record CumulativeSegment(DateTimeOffset Start, DateTimeOffset End, double From, double To, int Group);

/// <summary>Running sum of observed, complete, valid intervals in the selected range.
/// Gaps carry no assumed consumption; groups stay separate rather than drawing a false plateau.</summary>
public sealed class CumulativeSeries
{
    public List<CumulativeSegment> Segments { get; } = [];
    public static CumulativeSeries Build(SeriesData source, DateTimeOffset start, DateTimeOffset end)
    {
        var result = new CumulativeSeries();
        double sum = 0;
        foreach (var s in source.Segments)
        {
            if (!s.Valid || s.Start < start || s.End > end) continue;
            result.Segments.Add(new(s.Start, s.End, sum, sum + s.Delta, s.Group));
            sum += s.Delta;
        }
        return result;
    }
    public double? ValueAt(DateTimeOffset time)
    {
        // Interpolation is solely between two known ends of a valid sampling interval.
        foreach (var s in Segments)
            if (time >= s.Start && time <= s.End)
                return s.From + (s.To - s.From) * (time - s.Start).TotalSeconds / (s.End - s.Start).TotalSeconds;
        return null;
    }
}
