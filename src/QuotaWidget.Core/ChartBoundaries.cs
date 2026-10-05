namespace QuotaWidget.Core;

/// <summary>Display grouping only; original intervals remain available for inspection and accounting.</summary>
public static class ChartBoundaries
{
    public static IReadOnlyList<GapRegion> Coalesce(IEnumerable<GapRegion> regions)
    {
        var result = new List<GapRegion>();
        foreach (var region in regions.Where(g => g.End > g.Start).OrderBy(g => g.Start).ThenBy(g => g.End))
        {
            if (result.Count > 0 && result[^1] is { } previous && previous.Label == region.Label && region.Start <= previous.End)
                result[^1] = previous with { End = region.End > previous.End ? region.End : previous.End };
            else result.Add(region);
        }
        return result;
    }
}
