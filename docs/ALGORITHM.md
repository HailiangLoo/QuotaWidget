# What the charts mean

**English** · [简体中文](ALGORITHM.zh-CN.md)

QuotaWidget receives intermittent quota counters, not a stream of the provider's instantaneous consumption. **Smoothing estimates when observed quota was consumed; it does not make the underlying counter more precise.**

## Readings, totals, and rates

| Display | Calculation | Interpretation |
| --- | --- | --- |
| Gauge | Latest official quota reading | Remaining or used quota in that window |
| Header total / cumulative | Sum of complete, valid observed increments in the selected range | Recorded usage; resets, missing samples, and account changes do not invent increments |
| Compact hourly rate | Difference between actual readings roughly an hour apart, divided by their actual elapsed time | An observed average over about one hour, not a live speedometer |
| Smoothed rate curve | Observed increments distributed across supported work intervals, then smoothed | An estimate of the consumption pattern |

The recent-hour rate ends at the latest reading, then picks a continuous earlier reading closest to 60 minutes away, within 45–75 minutes. It does not proportionally split a partial sample interval or drift just because the clock advances. A `*` marks stale readings or coverage outside 55–65 minutes. If there is too little continuous data, the rate is unavailable.

The cumulative stroke connects known sampling endpoints. It does not reveal the exact moment an increment occurred inside an interval.

## How smoothing works

1. Keep providers and quota windows separate. Preserve missing readings, real gaps, account changes, and resets in the accounting data.
2. Use local task start/end events as evidence of work. Remove confirmed idle time from the estimation clock so small integer jumps can be spread across adjacent work fragments instead of being charged entirely to one short fragment.
3. Spread each counter increment between its surrounding observations on that active clock. Allow a short reporting delay after a completed task: only its first following sample, bounded by a polling interval capped at ten minutes, plus a one-minute grace period.
4. Smooth with a raised-cosine kernel. The target span adapts to roughly 2–3 counter updates, normally at least 30 active minutes and capped by the selected 60/90/120-minute preference. Mirror the edges of continuous estimation runs and normalize their confirmed area to the observed total. Very long histories may use a wider effective kernel when display sampling becomes coarse.
5. Map back to real time. Known work starts and ends have hard boundaries; idle periods are not filled by smoothing. Compute against the full available context, then crop for 1h/5h/12h/24h/3d/all views.

A clean quota reset may share rate context across the boundary when local work is demonstrably continuous, while accounting periods remain separate. An unfinished tail can temporarily extend the last estimate with a diminishing prediction, bounded by one counter step; it stops at the latest observation. A saturated pre-reset counter has special continuity handling until the first new-period increment. A completed task settles after a valid post-completion observation; elapsed wall time alone is not evidence. New readings can revise recent curve shape.

When local activity cannot explain an observed increment, the estimator falls back to observation intervals. It does not silently attribute other-device or web usage to an unrelated local chat.

Implementation: [ActiveRateEstimator](../src/QuotaWidget.Core/ActiveRateEstimator.cs), [RateTrend](../src/QuotaWidget.Core/RateTrend.cs), [TrendContinuity](../src/QuotaWidget.Core/TrendContinuity.cs), and [RecentUsageRate](../src/QuotaWidget.Core/RecentUsageRate.cs).

## How far can it differ from the real rate?

There is no provider-supplied instantaneous ground truth here, so the app cannot honestly claim a measured percentage accuracy. A counter can stay unchanged while work consumes quota, then jump later. An abrupt real burst and steady work can produce the same sampled readings; smoothing cannot distinguish them perfectly.

For scale, **if a counter is quantized in 1-point steps**, endpoint quantization alone can cause a difference of about 1 point/hour in an hour-long average, or about 6 points/hour over ten minutes. This assumes a consistent quantizer and excludes reporting delay and missing data; it is an illustration, not an error guarantee. A converted Fable counter moving in half-point steps has correspondingly finer quantization. Sampling more often reduces detection delay, but cannot recover precision absent from the counter.

Use gauges and header totals for observed quota, the hourly average for recent consumption, and the curve for broad changes in intensity. Do not treat a curve peak as an exact bill for that minute. Local token records help identify work and models; QuotaWidget does not convert tokens into supposedly exact per-chat quota costs.

## Why Fable-only lines can coincide

On supported plans, Fable is expressed in Claude weekly units for comparison. When local model evidence identifies a Fable-only fragment and the counter difference stays within the combined observed step sizes, the **rate and cumulative displays share the converted Fable stroke**. The redundant Claude stroke is omitted in that fragment. An unchanged cumulative plateau can retain that display without reintroducing a parallel line.

Mixed models, unknown models, missing coverage, reset gaps, or unexplained divergence retain separate strokes. Hiding Fable explicitly restores the Claude stroke. Header totals and stored readings remain independent and unchanged, so they may still differ even where only one line is visible. In a cumulative view, the shared line is the Fable total for the selected range, not a corrected Claude bill.
