using System.Globalization;
using QuotaWidget.Core;

static class PeakLabelTests
{
    public static void Register(List<(string Name, Func<Task> Body)> tests)
    {
        var t = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        void Test(string name, Action body) => tests.Add(("peak labels: " + name, () => { body(); return Task.CompletedTask; }));
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        TrendPoint[] Run(params double[] values) => values.Select((v, i) => new TrendPoint(t.AddMinutes(i * 5), v)).ToArray();

        Test("two low peaks survive beside a large peak; troughs and insignificant ripples do not", () =>
        {
            var p = PeakLabels.Find([Run(0, 1, 2, 1, .7, 1.9, .8, 1, 4, 10, 7, 7.1, 7, 5)]);
            Check(p.Count == 3 && p[0].Value == 10, "wrong peak priorities or ripple retained");
            Check(p.Any(x => x.Value == 2) && p.Any(x => x.Value == 1.9), "small peaks hidden by the shared scale");
        });
        Test("flat tops become one centered label and missing runs stay independent", () =>
        {
            var p = PeakLabels.Find([Run(0, 2, 2, 2, 0)]);
            Check(p.Count == 1 && p[0].Time == t.AddMinutes(10) && p[0].Value == 2, "plateau duplicated or moved");
            var separated = PeakLabels.Find([Run(1, 1), Run(1, 1).Select(x => x with { Time = x.Time.AddHours(2) }).ToArray()]);
            Check(separated.Count == 2 && separated.All(x => x.Time < t.AddMinutes(5) || x.Time > t.AddHours(2)), "gap got a label");
        });
        Test("a broad small peak survives tiny numerical ripples on its crest", () =>
        {
            var p = PeakLabels.Find([Run(0, 1, 1.999, 1.998, 2, 1.997, 1.999, 1, .5, 5, 10, 2)]);
            Check(p.Any(x => x.Value == 2) && p.Any(x => x.Value == 10), "ripple boundaries erased the broad peak");
        });
        Test("positive boundary highs and constant runs can be read; zero Fable and empty runs stay unlabelled", () =>
        {
            Check(PeakLabels.Find([Run(0, 1, 3)]).Single() is { Value: 3, Boundary: true }, "rising right edge lost");
            Check(PeakLabels.Find([Run(3, 2, 1)]).Single().Time == t, "left edge moved");
            Check(PeakLabels.Find([Run(2, 2, 2, 2)]).Count == 1, "flat series cluttered");
            Check(PeakLabels.Find([Run(0, 0, 0), Run()]).Count == 0, "zero/empty got a label");
        });
        Test("labels keep useful decimals and never turn a small positive peak into zero", () =>
        {
            Check(PeakLabels.Format(1.94) == "1.9" && PeakLabels.Format(10) == "10", "needlessly long or integer-only labels");
            foreach (var value in new[] { .076, .00012, 1e-9 })
                Check(double.Parse(PeakLabels.Format(value), CultureInfo.InvariantCulture) > 0, "positive peak rounded to zero");
            Check(PeakLabels.Format(0) == "" && PeakLabels.Format(double.NaN) == "", "invalid label text");
        });
    }
}
