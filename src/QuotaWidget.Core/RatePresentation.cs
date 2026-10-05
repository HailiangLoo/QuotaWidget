using System.Globalization;
namespace QuotaWidget.Core;

public static class RatePresentation
{
    public static string Estimate(double rate)=>Math.Round(rate,1,MidpointRounding.AwayFromZero).ToString("0.0",CultureInfo.InvariantCulture);
}
