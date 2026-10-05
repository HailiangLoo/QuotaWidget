using System.Globalization;

namespace QuotaWidget.Core;

public sealed record ChartScale(double Top, double Step)
{
    /// <summary>One scale per panel, using only that panel's visible drawn values.</summary>
    public static Dictionary<int, ChartScale> ForPanels(IEnumerable<(int Panel, IEnumerable<double> Values)> series) =>
        series.GroupBy(s => s.Panel).ToDictionary(g => g.Key, g =>
        {
            var peak = g.SelectMany(s => s.Values).Where(double.IsFinite).DefaultIfEmpty(0).Max();
            return Create(peak > 0 ? peak * 1.12 : 1);
        });

    public string Label(double value)
    {
        var digits = 0;
        while (digits < 8 && Math.Abs(Math.Round(Step, digits) - Step) > Step * 1e-8) digits++;
        return value.ToString(digits == 0 ? "0" : "0." + new string('#', digits), CultureInfo.InvariantCulture);
    }

    public static ChartScale Create(double maximum, int desiredTicks = 3)
    {
        if (!(maximum > 0) || !double.IsFinite(maximum)) maximum = 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(maximum / Math.Max(1, desiredTicks))));
        var choices = new List<(ChartScale Scale, double Score)>();
        foreach (var factor in new[] { .1, .2, .25, .5, 1, 2, 2.5, 5, 10 })
        {
            var step = power * factor;
            var ticks = Math.Ceiling(maximum / step);
            if (ticks < 2 || ticks > 4) continue;
            var top = ticks * step;
            choices.Add((new(top, step), (top / maximum - 1) + Math.Abs(ticks - desiredTicks) * .1));
        }
        return choices.OrderBy(c => c.Score).First().Scale;
    }
}
