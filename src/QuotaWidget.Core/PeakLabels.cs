using System.Globalization;

namespace QuotaWidget.Core;

public sealed record ChartPeak(DateTimeOffset Time, double Value, double Prominence, bool Boundary);

/// <summary>Annotations of the drawn rate, never additional quota observations.</summary>
public static class PeakLabels
{
    public static List<ChartPeak> Find(IEnumerable<IReadOnlyList<TrendPoint>> runs)
    {
        var result = new List<ChartPeak>();
        foreach (var points in runs)
        {
            if (points.Count < 2) continue;
            var peaks = new List<(int Start, int End)>();
            for (var i = 0; i < points.Count;)
            {
                var end = i;
                var value = points[i].Rate;
                var epsilon = Math.Max(1e-12, Math.Abs(value) * 1e-9);
                while (end + 1 < points.Count && Math.Abs(points[end + 1].Rate - value) <= epsilon) end++;
                if (value > 0 && double.IsFinite(value)
                    && (i == 0 || value > points[i - 1].Rate + epsilon)
                    && (end == points.Count - 1 || value > points[end + 1].Rate + epsilon))
                    peaks.Add((i, end));
                i = end + 1;
            }
            for (var i = 0; i < peaks.Count; i++)
            {
                var (a, b) = peaks[i];
                var value = points[a].Rate;
                var leftMin = value; var rightMin = value;
                var epsilon = Math.Max(1e-12, value * 1e-9);
                // Scan to a higher crest, not merely the adjacent tiny ripple. Otherwise
                // numerical wiggles on a broad top can suppress that entire visible peak.
                for (var j = a - 1; j >= 0 && points[j].Rate <= value + epsilon; j--)
                    leftMin = Math.Min(leftMin, points[j].Rate);
                for (var j = b + 1; j < points.Count && points[j].Rate <= value + epsilon; j++)
                    rightMin = Math.Min(rightMin, points[j].Rate);
                var boundary = a == 0 || b == points.Count - 1;
                var prominence = a == 0 ? value - rightMin : b == points.Count - 1 ? value - leftMin
                    : Math.Min(value - leftMin, value - rightMin);
                // One label suffices for a wholly flat, positive run. Otherwise require
                // a meaningful local rise; do not compare a small peak to the panel's giant peak.
                if (a == 0 && b == points.Count - 1) prominence = value;
                if (prominence < Math.Max(1e-12, value * .06)) continue;
                result.Add(new(points[a].Time + (points[b].Time - points[a].Time) / 2, value, prominence, boundary));
            }
        }
        return result.OrderByDescending(p => p.Prominence * (p.Boundary ? .8 : 1)).ThenByDescending(p => p.Value).ThenBy(p => p.Time).ToList();
    }

    public static string Format(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return "";
        if (value < .001) return value.ToString("0.#E+0", CultureInfo.InvariantCulture);
        var digits = value >= 1 ? 1 : Math.Clamp((int)Math.Ceiling(-Math.Log10(value)) + 1, 1, 4);
        return value.ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);
    }
}
