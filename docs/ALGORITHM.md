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
4. Smooth with a raised-cosine kernel. The target span adapts to roughly 2–3 counter updates, normally at least 30 active minutes and capped by the selected 60/90/120/150-minute preference. Mirror the edges of continuous estimation runs and normalize their confirmed area to the observed total. Very long histories may use a wider effective kernel when display sampling becomes coarse.
5. Map back to real time. Known work starts and ends have hard boundaries; idle periods are not filled by smoothing. Compute against the full available context, then crop for 1h/5h/12h/24h/3d/all views.

A clean quota reset may share rate context across the boundary when local work is demonstrably continuous, while accounting periods remain separate. An unfinished tail can temporarily extend the last estimate with a diminishing prediction, bounded by one counter step; it stops at the latest observation. A saturated pre-reset counter has special continuity handling until the first new-period increment. A completed task settles after a valid post-completion observation; elapsed wall time alone is not evidence. New readings can revise recent curve shape.

After a collection gap or another unbridgeable observation break, pool the first three counter steps before estimating their local shape. Until that much usage is observed, distribute the recorded amount across all observed active time in the new context, including unchanged readings. Show this startup average as a solid curve with normal hover values, without a peak label or an extra predicted tail. Keep the initial steps pooled as later observations arrive. This is an evidence heuristic, not a rate ceiling: a large observed burst remains visible. A known gap also settles the preceding still-open work context through its last valid sample; it does not imply task completion, fill the gap, or borrow consumption from the other side. Idle time remains excluded.

When local activity cannot explain an observed increment, the estimator falls back to observation intervals. It does not silently attribute other-device or web usage to an unrelated local chat.

Task boundaries depend on the local log import: lifecycle events → complete published work history → activity intervals → rate estimate → chart. Import runs in bounded batches and skips oversized output rows without blocking later completion events. An unchanged quota counter alone is not a task completion; confirmed completion cuts the curve at its recorded timestamp.

Implementation: [ActiveRateEstimator](../src/QuotaWidget.Core/ActiveRateEstimator.cs), [RateTrend](../src/QuotaWidget.Core/RateTrend.cs), [TrendContinuity](../src/QuotaWidget.Core/TrendContinuity.cs), and [RecentUsageRate](../src/QuotaWidget.Core/RecentUsageRate.cs).

## How far can it differ from the real rate?

There is no provider-supplied instantaneous ground truth here, so the app cannot honestly claim a measured percentage accuracy. A counter can stay unchanged while work consumes quota, then jump later. An abrupt real burst and steady work can produce the same sampled readings; smoothing cannot distinguish them perfectly.

For scale, **if a counter is quantized in 1-point steps**, endpoint quantization alone can cause a difference of about 1 point/hour in an hour-long average, or about 6 points/hour over ten minutes. This assumes a consistent quantizer and excludes reporting delay and missing data; it is an illustration, not an error guarantee. A converted Fable counter moving in half-point steps has correspondingly finer quantization. Sampling more often reduces detection delay, but cannot recover precision absent from the counter.

Use gauges and header totals for observed quota, the hourly average for recent consumption, and the curve for broad changes in intensity. Do not treat a curve peak as an exact bill for that minute. Local token records help identify work and models. The per-chat estimate below is an allocation, not exact billing.

## Per-chat quota allocation

Observed weekly increments supply the total to allocate. A nonnegative fit learns separate IN, CACHE and OUT weights per model from up to eight days of local history. IN includes cache writes. Public API prices are not subscription quota prices; speed, context and provider policy can change the relationship.

Calibration uses roughly hourly blocks bounded by actual readings, without crossing gaps, resets or saturated counters. It requires at least eight informative blocks (four per model when larger) and twelve observed points. Three contiguous held-out folds test predictions; twelve component/model order and regularization variants test whether chat shares depend on an arbitrary fit. Validation mean error must be at most max(0.8 points, 25% of mean observed hourly usage). An individual held-out error above max(2 points, 50% of that block plus 1 point) rejects a candidate. These are acceptance heuristics, not confidence intervals.

Within each continuous valid part of the selected range, weights distribute the observed total according to each chat/model's tokens. Confirmed subagents use the token table's parent mapping. Unrounded shares sum to the observed total; one-decimal values may differ slightly after rounding. At least three points and 45 minutes are required. Incomplete imports, conflicting records, history beyond the eight-day horizon, prediction disagreement or a share spread above max(0.75 points, 30% of that share) produce a dash. Unknown is never zero.

This assumes relevant consumption is represented in local logs. Correlated off-device activity can still pass validation; account-level validation error does not establish each chat's accuracy. Partial observation intervals and pending consumption are not allocated. Calibration runs off the UI thread when details open or refresh, without provider requests. Token columns retain their original scope and counters.

Compact Fable shows an observed recent-hour average, converted to Claude weekly units where supported, otherwise in Fable's own units. Zero, stale or insufficient recent-hour coverage hides it, as does the end of all explicitly identified Fable work. Opus activity does not extend a completed Fable turn. Historical consumption stays intact; missing completion is not replaced with an invented timeout.

## Sharing the Fable stroke

On supported plans, Fable is converted to Claude weekly units. A locally confirmed Fable-only fragment can share its converted rate stroke when the counter difference fits the combined quantization tolerance.

Cumulative charts also carry earlier consumption. After other models have been used, later Fable-only activity still sits on different cumulative baselines. Hiding those Claude fragments would punch holes in an otherwise continuous total. A cumulative view therefore shares a stroke only when every recorded interval in the selected range has Fable-only evidence, coverage agrees, and the total discrepancy remains within quantization tolerance. Mixed history, unknown attribution or unexplained differences retain both complete cumulative curves.

An unchanged plateau may continue adjacent confirmed Fable evidence. Actual resets and observation gaps remain separate. Hiding Fable restores Claude. Header totals and stored records stay independent: a shared cumulative stroke represents Fable's recorded total, not a corrected Claude bill.
