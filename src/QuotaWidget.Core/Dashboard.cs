namespace QuotaWidget.Core;

public sealed record DashboardView(ChartView Chart, string Summary, string Detail, string? CodexStatus);

public static class Dashboard
{
    public static DashboardView Combine(WidgetView claude, WidgetView codex, WidgetSettings settings, DateTimeOffset now, LatestEnvelope? codexEnvelope, int? rangeMinutes = null,
        IReadOnlyList<DateTimeOffset>? trendBoundaries = null, TrendActivity? activity = null)
    {
        // One shared clock/range, independently segmented provider histories. A gap in Claude
        // must never hide valid Codex data. Missing intervals are not interpolated or summed.
        var end = now;
        var range = rangeMinutes ?? settings.RangeMinutes;
        var c = claude.Chart;
        var fableFactor = QuotaUnits.FableToClaude(claude.PlanLabel);
        var fable = fableFactor is { } factor ? QuotaUnits.Scale(c.Fable, factor) : c.Fable;
        var x = codex.Chart.Total;
        var first = (settings.Monitors(ChatPlatform.Claude)?c.Total.Segments.Concat(c.Fable.Segments):[])
            .Concat(settings.Monitors(ChatPlatform.Codex)?x.Segments:[]).Select(s => (DateTimeOffset?)s.Start).Min();
        var start = range == ChartRanges.All ? first ?? end.AddHours(-1) : end.AddMinutes(-range);
        var gaps = first is { } firstAt && firstAt > start ? new List<GapRegion> { new(start, firstAt, "尚未记录") } : [];
        bool Has(SeriesData s) => s.Segments.Any(p => p.Valid && p.End > start && p.Start < end);
        var claudeCumulative=settings.ChartModeFor(ChatPlatform.Claude)=="cumulative";
        var codexCumulative=settings.ChartModeFor(ChatPlatform.Codex)=="cumulative";
        var chart = new ChartView
        {
            Start = start, End = end, Smooth = settings.Smoothing, TrendMinutes = settings.TrendMinutes,
            TrendBoundaries = trendBoundaries ?? [],
            Activity = activity ?? TrendActivity.Empty,
            Total = c.Total, Fable = fable, Codex = x, Gaps = gaps, FableToClaudeFactor = fableFactor,
            ClaudeCollapsed = settings.ClaudeChartCollapsed, CodexCollapsed = settings.CodexChartCollapsed,
            ClaudeGaps = c.Gaps, CodexGaps = codex.Chart.Gaps,
            ClaudeCumulativeMode = claudeCumulative, CodexCumulativeMode = codexCumulative,
            TotalCumulative = claudeCumulative ? CumulativeSeries.Build(c.Total, start, end) : null,
            FableCumulative = claudeCumulative ? CumulativeSeries.Build(fable, start, end) : null,
            CodexCumulative = codexCumulative ? CumulativeSeries.Build(x, start, end) : null,
            TotalVisible = settings.TotalVisible && settings.Monitors(ChatPlatform.Claude), FableVisible = settings.FableVisible && settings.Monitors(ChatPlatform.Claude), CodexVisible = settings.CodexVisible && settings.Monitors(ChatPlatform.Codex),
            BlockedText = Has(c.Total) || Has(c.Fable) || Has(x) ? null : Loc.T("等待连续采样"),
        };
        var totals = new[] { ("Claude", c.Total), ("Fable", fable), ("Codex", x) };
        var summaries = new List<string>();
        var details = new List<string> { Loc.F($"{ChartRanges.Label(range)} · 周额度点"),
            Loc.T("累计取实际读数；断开、重置与缺口不补零。"),
            Loc.F($"平滑按活跃时间分摊整数跳点，跨度自适应，上限 {settings.TrendMinutes}m；切换范围只裁剪。"),
            Loc.T("已收口的连续段按已观测总量约束；进行中的尾段和局部曲线面积可能与累计不同。"),
            Loc.T("曲线为趋势估算；工作结束并采样确认后收口，新读数可能修正曲线。") };
        details.Add(fableFactor is not null
            ? Loc.T("Fable 点数折合为 Claude 周额度（×0.5），已含在 Claude 中；顶部 Fable 圆环按自身额度。")
            : Loc.T("此套餐的 Fable 换算比例未确认，仍按 Fable 自身周额度计，不与 Claude 对齐或相加。"));
        details.Add(Loc.T("Codex 按自己的周额度计，不能与 Claude 相加。"));
        details.Add(Loc.T("Claude/Fable 共用动态刻度；Codex 按自己的峰值独立缩放。上下图的高度/面积不能直接比较，需读各自刻度与累计。"));
        foreach (var (name, series) in totals)
        {
            var sum = RateEngine.SumRange(series, start, end);
            summaries.Add(sum.CoverageMinutes > 0 ? sum.Delta.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "—");
            details.Add(sum.CoverageMinutes > 0
                ? Loc.F($"{name}：{sum.Delta:0.##} 点 / {WidgetModel.Duration(sum.CoverageMinutes)} · 平均 {sum.Delta * 60 / sum.CoverageMinutes:0.##} 点/h")
                : Loc.F($"{name}：所选时段没有连续记录"));
        }
        // Preserve current-cycle cumulative averages as additional hover detail.
        details.AddRange(claude.SummaryDetail.Split('\n').Where(line => line.Contains(Loc.T("累计平均"))).Select(line => "Claude · " + line.Split(" · Fable")[0]));
        if (claude.FableAverage is { } avg)
            details.Add(Loc.F($"Fable · 自{avg.From.ToLocalTime():M/d HH:mm}累计平均 {avg.Rate * (fableFactor ?? 1):0.##} 点/h（{(fableFactor is null ? Loc.T("自身") : Loc.T("Claude 总"))}周额度，同周期{(avg.IncludesGap ? Loc.T("，含缺口") : "")}）"));
        details.AddRange(codex.SummaryDetail.Split('\n').Where(line => line.Contains(Loc.T("累计平均"))).Select(line => "Codex · " + line.Split(" · Fable")[0]));
        return new(chart, Loc.F($"{ChartRanges.Label(range)} 累计   {string.Join(" / ", summaries)} 点"), string.Join("\n", details), CodexStatus(codex, codexEnvelope));
    }

    public static string? CodexStatus(WidgetView codex, LatestEnvelope? codexEnvelope) => codexEnvelope switch
        {
            null => Loc.T("Codex 等待首次采样"),
            { Status: Statuses.AuthRequired } => Loc.T("Codex 未登录 · 请在官方应用登录"),
            { Status: Statuses.Error } => Loc.T("Codex 采集暂不可用 · ") + (codexEnvelope.ErrorCode switch
            {
                "cli_missing_or_untrusted" => Loc.T("未找到可信官方 CLI"),
                "identity_unknown" => Loc.T("账号标识缺失"),
                "timeout" => Loc.T("查询超时"),
                "no_codex_bucket" or "no_weekly_window" or "no_supported_window" or "bad_output" or "invalid_weekly_window" or "ambiguous_weekly_window" or "invalid_five_hour_window" or "ambiguous_five_hour_window" => Loc.T("额度格式变化"),
                _ => Loc.T("稍后自动重试"),
            }),
            {ErrorCode:"invalid_weekly_window" or "ambiguous_weekly_window" or "invalid_five_hour_window" or "ambiguous_five_hour_window"} => Loc.T("Codex · 部分额度暂不可读"),
            _ when codex.Note is not null => "Codex · " + codex.Note,
            _ => null,
        };
}
