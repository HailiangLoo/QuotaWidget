using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public partial class MainWindow
{
    sealed record CompactCreditUsage(string? Quota, string Credits, string Detail)
    {
        public string Text => Quota is null ? Credits : Quota + " + " + Credits;
    }

    CompactCreditUsage? CreditUsage(ChartView view, DateTimeOffset now)
    {
        var start = now.AddHours(-1);
        var credits = view.Credits is { } data ? RateEngine.SumRange(data, start, now) : new RangeSum(0, 0);
        var limits = _codex.Records.LastOrDefault()?.Snapshot.Limits;
        var exhausted = limits?.AllWeek?.UsedPercent >= 100 || limits?.FiveHour?.UsedPercent >= 100;
        if (credits.Delta <= 0) return null;
        var quota = RateEngine.SumRange(view.Total, start, now);
        var fresh = _model.Settings.Listens(ChatPlatform.Codex) && _codex.LastEnvelope is { } latest && Statuses.HasSnapshot(latest.Status) &&
            now - latest.AttemptedAt < TimeSpan.FromSeconds(Math.Max(60, latest.EffectivePollIntervalSeconds) * RateEngine.GapFactor);
        var edgeTolerance = Math.Min(10, Math.Max(60, _model.Settings.PollIntervalSeconds) * RateEngine.GapFactor / 60);
        bool Partial(SeriesData? series, RangeSum sum) => !fresh || sum.CoverageMinutes <= 0 ||
            sum.CoverageMinutes < 60 - edgeTolerance ||
            series?.Segments.Any(s => !s.Valid && s.End > start && s.Start < now) == true;
        string Amount(RangeSum sum, string unit, bool partial) => (sum.CoverageMinutes > 0 ? sum.Delta.ToString("0.#", CultureInfo.InvariantCulture) : "—") + unit + (partial ? "*" : "");
        var quotaPartial = Partial(view.Total, quota); var creditPartial = Partial(view.Credits, credits);
        // Zero quota is omitted only when exhaustion explains it. Missing quota
        // remains unknown; credit balance changes are never converted into points.
        var quotaText = quota.Delta > 0 || (!exhausted && (limits?.AllWeek is not null || quota.CoverageMinutes > 0)) || (limits?.AllWeek is not null && quotaPartial)
            ? Amount(quota, Loc.T("点"), quotaPartial) : null;
        var creditText = Amount(credits, " cr", creditPartial);
        var detail = "Codex · " + Loc.T("近1h") + "\n" + (quotaText is null ? "" : quotaText + " + ") + creditText + "\n" +
            (view.CreditsObservedAt is { } observed ? Loc.F($"截至 {observed.ToLocalTime():M/d HH:mm}") + "\n" : "") +
            Loc.T("仅累计近一小时已记录用量，不折算成每小时速率。") + "\n" +
            Loc.F($"周额度有效采样 {quota.CoverageMinutes:0.#} 分钟") + "\nCredits · " + Loc.F($"所选范围有效采样 {credits.CoverageMinutes:0.#} 分钟") +
            (quotaPartial || creditPartial ? "\n" + Loc.T("* 记录不全，只累计已观测区间。") : "") + "\n" +
            Loc.T("Credits 为余额净减少，可能受充值、退款或过期影响。");
        return new(quotaText, creditText, detail);
    }

    void RenderCompactCredits(CompactCreditUsage usage)
    {
        var single = !_model.Settings.Monitors(ChatPlatform.Claude);
        CompactCodexPrimaryRate.Orientation = Orientation.Vertical;
        CompactCodexRateHeading.Text = single ? Loc.T("近1h") : "Codex · " + Loc.T("近1h") + " ↗";
        CompactCodexRateHeading.FontSize = 9;
        CompactCodexRateHeading.Foreground = Theme.Brush("Muted");
        CompactCodexRateHeading.Margin = new Thickness(0);
        CompactCodexRateHeading.HorizontalAlignment = CompactCodexRateValue.HorizontalAlignment = single ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        CompactCodexRateUnit.Visibility = CompactCodexRateArrow.Visibility = Visibility.Collapsed;
        foreach (var text in new[] { CompactCodexRateValue, CodexInlineRateValue })
        {
            text.Text = usage.Text;
            text.Inlines.Clear(); text.FontSize = single ? 14 : 12;
            if (usage.Quota is { } quota)
            {
                text.Inlines.Add(new Run(quota) { Foreground = Theme.Brush("Violet") });
                text.Inlines.Add(new Run(" + ") { Foreground = Theme.Brush("Muted") });
            }
            text.Inlines.Add(new Run(usage.Credits) { Foreground = Theme.Brush("Credits") });
        }
        CompactCodexPoints.Text = CodexInlinePoints.Text = Loc.T("近1h · ") + usage.Text;
        foreach (var host in new FrameworkElement[] { CompactCodexRate, CodexInlineUsage })
        {
            host.ToolTip = usage.Detail;
            System.Windows.Automation.AutomationProperties.SetName(host, usage.Detail);
        }
        // The usage now lives beside Codex; retain the balance without a duplicate total.
        CreditsUsedText.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(CreditsBalanceText, 2);
    }
}
