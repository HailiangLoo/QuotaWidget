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
| Unlocated usage readout | An observed increment with no matching local work | Included in totals; inspect its sample interval to see the amount, without an inferred task rate |

The recent-hour rate ends at the latest reading, then picks a continuous earlier reading closest to 60 minutes away, within 45–75 minutes. It does not proportionally split a partial sample interval or drift just because the clock advances. A `*` marks stale readings or coverage outside 55–65 minutes. If there is too little continuous data, the rate is unavailable.

The cumulative stroke connects known sampling endpoints. It does not reveal the exact moment an increment occurred inside an interval.

## Chat reminders and compaction

The management list shows main chats, with explicitly identified subagents excluded from both current and collapsed rows. Codex's paginated/resumed rollouts are grouped by `session_meta.id` (or `session_id`), not the extra storage ID in the filename. A fork's history reference is not subagent ownership. These display decisions do not remove token records or change confirmed subagent usage attribution.

Chat timers show time since a local model request, not confirmed server cache lifetime. Compaction ends the old reminder; a real continuation starts or preserves the new request's timer. Codex's compact record can contain several megabytes of replacement history, so the bounded log reader also recognizes its small `ContextCompaction` completion event. Duplicate completion records represent one boundary. A cumulative token summary with zero new input does not start a request. Missing log evidence remains unknown.

## How smoothing works

1. Keep providers and quota windows separate. Preserve missing readings, real gaps, account changes, and resets in the accounting data.
2. Use local task start/end events as evidence of work. Remove confirmed idle time from the estimation clock so small integer jumps can be spread across adjacent work fragments instead of being charged entirely to one short fragment.
3. Spread each counter increment between its surrounding observations on that active clock. Allow a short reporting delay after a completed task: only its first following sample, bounded by a polling interval capped at ten minutes, plus a one-minute grace period.
4. Smooth with a raised-cosine kernel. The target span adapts to roughly 2–3 counter updates, normally at least 30 active minutes and capped by the selected 60/90/120/150-minute preference. Mirror the edges of continuous estimation runs and normalize their confirmed area to the observed total. Very long histories may use a wider effective kernel when display sampling becomes coarse.
5. Map back to real time. Known work starts and ends have hard boundaries; idle periods are not filled by smoothing. Preserve raw model identities through estimation. A concurrent helper joining and leaving does not restart Claude's total-quota estimate while the original model continues: the aggregate counter still includes that ongoing work. Start an independent context when the current context's models are replaced or a different model works alone, so an old model's rate cannot carry into that exclusive work. Initially mixed work also separates before becoming exclusive. Short handoffs within the same model may still merge; the ten-second handoff rule cannot fill actual idle between different models. Compute against the full available context, then crop for 1h/5h/12h/24h/3d/all views.

When an estimation context change falls between samples, do not invent a counter reading at that instant. If only one work context supports the entire increment, keep the observation whole in that context. If work on both sides or a delayed old-task report could explain it, estimate that observation separately without borrowing neighbouring rates or labelling a local peak. Cumulative accounting retains the original complete increment. Missing completion is not treated as a model exit; concurrent-model exits use the same context rules. Current Codex lifecycle records do not establish reliable model identity, so their task boundaries remain authoritative without guessing model switches.

A clean quota reset may share rate context across the boundary when local work is demonstrably continuous and sampling follows the configured cadence, including 30/60-minute polling. Accounting periods remain separate; missed samples and failures still prevent continuity. An unfinished tail can temporarily extend the last estimate with a diminishing prediction, bounded by one counter step; it stops at the latest observation. A saturated pre-reset counter has special continuity handling until the first new-period increment. A completed task settles after a valid post-completion observation; elapsed wall time alone is not evidence. New readings can revise recent curve shape.

After a collection gap, model-context handoff or another unbridgeable observation break, retain the duration between each counter jump. Use up to the first three counter steps to regularize the first jump's unknown phase: reduce its estimated weight only if its interval rate exceeds the subsequent observed average, by at most one step, then renormalize the prefix to its observed total. Preserve the relative rates of subsequent intervals instead of flattening three increments into a rectangle. A slower first interval is not raised, and large observed bursts are not clipped by a fixed rate ceiling.

Keep subsequent unchanged observations in their original positions. With fewer than three steps, use the same bounded, diminishing tail shape as a live estimate, then rescale and smooth the head and tail together to the observed total. This redistributes points instead of adding predicted usage, without interpreting an unchanged counter as task completion. A lone jump without subsequent observations still supports only an active-time average; task boundaries do not justify invented within-task variation.

These sparse startup curves remain solid with one-decimal hover values and no peak labels. Phase regularization and diminishing tails are display estimates, not measured instantaneous changes; new readings can revise them. Retain the initial phase treatment as more readings arrive. A known gap also settles the preceding still-open work context through its last valid sample; it does not imply task completion, fill the gap, or borrow consumption from the other side. Idle time remains excluded.

When local work records exist but cannot explain an increment, retain that increment separately at its original sampling interval. Inspecting the interval shows `+points` without assigning an instantaneous rate; permanent dots and labels do not clutter overview charts. Other supported work keeps its exact lifecycle boundaries and active-clock estimate: one unmatched sample cannot switch an entire night back to wall-clock smoothing. Confirmed curve area plus these unlocated increments conserves the observed total before display-only stroke sharing/filtering. If no local work history is available at all, the fallback remains an observation-interval trend; it cannot establish task boundaries. Neither path attributes other-device or web usage to an unrelated local chat.

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

Only the Fable-colored rate stroke is drawn in that fragment. Inspection still lists both Claude and Fable with the same rate; Claude already includes Fable. Recorded header totals and cumulative readouts stay independent, so integer rounding or reporting delay may leave a small difference. Never add Fable to the Claude counter again.

Sharing uses the same confirmed ten-second Fable-to-Fable handoffs as the rate timeline, so a few-second transition cannot expose a tiny leftover Claude stroke. Longer idle gaps and intervening non-Fable or unknown work still prevent sharing.

In mixed work, independent bucket timing and active-clock estimates can conflict. Where Fable's estimate exceeds Claude's total estimate, the unsupported Fable rate fragment is omitted and inspection says **Samples unaligned**, rather than clamping it or changing the total. Its raw counter and cumulative value remain available. Filtering does not introduce a task completion or a peak at the cut.

With complete lifecycle evidence, classify exclusive Fable work by the actual model start/end times. A completed Opus task is not extended into later work by half a smoothing window. Concurrent or unknown models and conflicting records still veto sharing. Request-neighbourhood inference remains a conservative fallback when lifecycle evidence is unavailable. Claude always means the platform total including Fable, not a separate Opus bill; sharing a stroke does not rewrite either counter.

Cumulative charts also carry earlier consumption. After other models have been used, later Fable-only activity still sits on different cumulative baselines. Hiding those Claude fragments would punch holes in an otherwise continuous total. A cumulative view therefore shares a stroke only when every recorded interval in the selected range has Fable-only evidence, coverage agrees, and the total discrepancy remains within quantization tolerance. Mixed history, unknown attribution or unexplained differences retain both complete cumulative curves.

An unchanged plateau may continue adjacent confirmed Fable evidence. Actual resets and observation gaps remain separate. Hiding Fable restores Claude. Header totals and stored records stay independent: a shared cumulative stroke represents Fable's recorded total, not a corrected Claude bill.
