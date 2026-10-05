namespace QuotaWidget.Core;

public enum SeriesKey { Total, Fable }

public enum SegmentIssue { None, Missing, UnknownWindow, Reset, Decrease, Gap }

/// <summary>The interval between two consecutive stored observations, for one bucket.</summary>
public sealed class RateSegment
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    /// <summary>Increase in this series' units; native in the engine, converted only in Dashboard. Meaningful when Valid.</summary>
    public double Delta { get; init; }
    public SegmentIssue Issue { get; init; }
    /// <summary>Short label for an invalid interval (休眠, 缺口, 重置, ...).</summary>
    public string? Label { get; init; }
    public int Group { get; set; }
    /// <summary>Only the counter changed: normal sampling, same account/source, no interruption evidence.
    /// Still invalid for accounting; a rate estimator may share context if work also continued.</summary>
    public bool CounterResetOnly { get; init; }
    /// <summary>The old counter was capped, so a flat reading cannot constrain further trend.</summary>
    public bool StartsAtCapacity { get; init; }
    /// <summary>Trailing time-weighted rate in own points/h; null when coverage is too thin.</summary>
    public double? Smooth { get; set; }
    /// <summary>How many minutes of data <see cref="Smooth"/> averages (≤ ~15, or one longer interval).</summary>
    public double SmoothMinutes { get; set; }

    public bool Valid => Issue == SegmentIssue.None;
    public double Minutes => (End - Start).TotalMinutes;
    public double Rate => Minutes > 0 ? Delta * 60.0 / Minutes : 0;
}

public sealed class SeriesData
{
    public required SeriesKey Key { get; init; }
    public required List<RateSegment> Segments { get; init; }

    public RateSegment? SegmentAt(DateTimeOffset t)
    {
        // Segments are in time order: binary search for the last one starting at or before t.
        int lo = 0, hi = Segments.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Segments[mid].Start <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return null;
        var s = Segments[found];
        return t < s.End || (found == Segments.Count - 1 && t == s.End) ? s : null;
    }

    (RateSegment? Before, RateSegment? After) SmoothNeighbours(DateTimeOffset t, RateSegment s)
    {
        RateSegment? before = null, after = null;
        var i = Segments.IndexOf(s);
        for (var j = i; j >= 0 && Segments[j].Group == s.Group; j--)
            if (Segments[j].Valid && Segments[j].Smooth is not null && Segments[j].End <= t) { before = Segments[j]; break; }
        for (var j = Math.Max(0, i); j < Segments.Count && Segments[j].Group == s.Group; j++)
            if (Segments[j].Valid && Segments[j].Smooth is not null && Segments[j].End >= t) { after = Segments[j]; break; }
        return (before, after);
    }

    /// <summary>Rate in the bucket's own points/h at time t; null inside gaps or before smoothing has enough coverage.</summary>
    public double? ValueAt(DateTimeOffset t, bool smooth)
    {
        var s = SegmentAt(t);
        if (s is null || !s.Valid) return null;
        if (!smooth) return s.Rate;
        var (before, after) = SmoothNeighbours(t, s);
        // The first measured interval is still usable before a full smoothing window exists.
        // Its retrospective interval average is drawn from its known start, not hidden.
        if (before is null) return after?.Smooth;
        if (after is null) return null;
        if (before.End == after.End) return before.Smooth;
        var f = (t - before.End).TotalSeconds / (after.End - before.End).TotalSeconds;
        return before.Smooth + (after.Smooth - before.Smooth) * f;
    }

    /// <summary>Span of data behind the smoothed value at t (for the tooltip label).</summary>
    public double? SmoothMinutesAt(DateTimeOffset t)
    {
        var s = SegmentAt(t);
        if (s is null || !s.Valid) return null;
        var (_, after) = SmoothNeighbours(t, s);
        return after?.SmoothMinutes;
    }
}

public sealed record RangeSum(double Delta, double CoverageMinutes);

public sealed record CumulativeAverage(DateTimeOffset From, DateTimeOffset To, double Delta, bool IncludesGap)
{
    public double Hours => (To - From).TotalHours;
    public double Rate => Hours > 0 ? Delta / Hours : 0;
}

public static class RateEngine
{
    /// <summary>An interval longer than this multiple of the poll interval in effect is a gap.</summary>
    public const double GapFactor = 1.8;
    public static readonly TimeSpan SmoothWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SmoothTolerance = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far two reports of the same reset instant may differ. Observed jitter is milliseconds;
    /// a real new window moves the reset instant by at least the window length (5 h or 7 d).
    /// </summary>
    public static readonly TimeSpan ResetJitterTolerance = TimeSpan.FromMinutes(2);
    const double Eps = 1e-9;

    public static QuotaLimit? Select(HistoryRecord r, SeriesKey key) =>
        key == SeriesKey.Total ? r.Snapshot.Limits.AllWeek : r.Snapshot.Limits.FableWeek;

    /// <summary>
    /// Same quota window: equal explicit ids when the source provides them; otherwise reset
    /// instants within the jitter tolerance. No rounding to a grid, so no boundary flips.
    /// </summary>
    public static bool SameWindow(QuotaLimit a, QuotaLimit b)
    {
        if (a.WindowId is not null || b.WindowId is not null) return a.WindowId == b.WindowId;
        return a.ResetsAt is { } ra && b.ResetsAt is { } rb && (ra - rb).Duration() <= ResetJitterTolerance;
    }

    // Codex can report 0% with a deadline ~7 days after each observation until a window
    // starts. That moving empty deadline is not evidence of a reset every five minutes.
    // Keep this exception source/profile/series-specific; never relax active-window drift
    // or explicit window IDs. The first positive reading may fix the deadline within this
    // sampled interval, so preserve that first increment too. Gaps/events still win below.
    static bool EmptyCodexContinuation(HistoryRecord a, HistoryRecord b, SeriesKey key, QuotaLimit? la, QuotaLimit? lb)
    {
        if (key != SeriesKey.Total || a.SourceId != CodexUsageSource.SourceId || b.SourceId != a.SourceId ||
            a.ProfileKey != b.ProfileKey || la is not { UsedPercent: 0, WindowId: null, WindowMode: WindowModes.Fixed } ||
            lb is not { WindowId: null, WindowMode: WindowModes.Fixed } || la.ResetsAt is not { } ra || lb.ResetsAt is not { } rb)
            return false;
        var week = TimeSpan.FromDays(7);
        var expected = a.T + week;
        return (ra - expected).Duration() <= ResetJitterTolerance &&
            rb >= expected - ResetJitterTolerance && rb >= ra - ResetJitterTolerance &&
            rb <= b.T + week + ResetJitterTolerance;
    }

    /// <summary>Power and app events always count; collection failures only for this source and profile.</summary>
    public static List<AppEvent> RelevantEvents(IEnumerable<AppEvent> events, string? sourceId, string? profileKey) =>
        events.Where(e => e.Type != EventTypes.CollectFail ||
                          ((e.SourceId is null || e.SourceId == sourceId) &&
                           (e.ProfileKey is null || e.ProfileKey == "unknown" || e.ProfileKey == profileKey)))
              .ToList();

    public static SeriesData Build(IReadOnlyList<HistoryRecord> records, SeriesKey key, IReadOnlyList<AppEvent> events)
    {
        var segments = new List<RateSegment>(Math.Max(0, records.Count - 1));
        QuotaLimit? anchor = null; // first reading of the current unbroken window
        for (var i = 1; i < records.Count; i++)
        {
            var a = records[i - 1];
            var b = records[i];
            if (b.T <= a.T) continue;
            var la = Select(a, key);
            var lb = Select(b, key);
            var emptyContinuation = EmptyCodexContinuation(a, b, key, la, lb);
            var expected = Math.Max(a.PollSeconds, b.PollSeconds);
            var longGap = (b.T - a.T).TotalSeconds > GapFactor * expected;
            var eventLabel = ClassifyEvents(events, a.T, b.T);
            if (!longGap && (eventLabel is "未运行" or "已暂停") && OnlyCoveredInterruptions(events, a.T, b.T, expected)) eventLabel = null;

            SegmentIssue issue;
            string? structural = null;
            if (la is null || lb is null) { issue = SegmentIssue.Missing; structural = "缺指标"; }
            else if (la.WindowMode != WindowModes.Fixed || lb.WindowMode != WindowModes.Fixed) { issue = SegmentIssue.UnknownWindow; structural = "口径未确认"; }
            else if (!emptyContinuation && !SameWindow(la, lb)) { issue = SegmentIssue.Reset; structural = "重置"; }
            else if (!emptyContinuation && anchor is not null && !SameWindow(anchor, lb)) { issue = SegmentIssue.Reset; structural = "周期变化"; }
            else if (lb.UsedPercent < la.UsedPercent - Eps) { issue = SegmentIssue.Decrease; structural = "异常"; }
            else if (longGap || eventLabel is not null) issue = SegmentIssue.Gap;
            else issue = SegmentIssue.None;

            // Keep the window anchor across ordinary gaps; restart it whenever the window changes.
            if (la is not null && lb is not null && la.WindowMode == WindowModes.Fixed && lb.WindowMode == WindowModes.Fixed)
            {
                anchor ??= la;
                if (emptyContinuation || issue is SegmentIssue.Reset or SegmentIssue.Decrease) anchor = lb;
            }
            else if (lb is not null) anchor = lb.WindowMode == WindowModes.Fixed ? lb : null;

            // A reset is a structural boundary even when a restart also happened there.
            string? label = issue == SegmentIssue.None ? null : structural ?? eventLabel ?? "缺口";
            var delta = la is not null && lb is not null ? lb.UsedPercent - la.UsedPercent : 0;
            segments.Add(new RateSegment { Start = a.T, End = b.T, Delta = delta, Issue = issue, Label = label,
                CounterResetOnly=issue==SegmentIssue.Reset&&structural=="重置"&&delta<0&&
                    a.SourceId==b.SourceId&&a.ProfileKey==b.ProfileKey&&a.PlanLabel==b.PlanLabel&&!longGap&&eventLabel is null&&
                    b.T-a.T<=TimeSpan.FromMinutes(10),
                StartsAtCapacity=la?.UsedPercent>=100 });
        }

        var group = 0;
        foreach (var s in segments)
        {
            if (!s.Valid) group++;
            s.Group = group;
        }

        // Trailing, time-weighted, never across a gap, never using later data. An interval that
        // alone spans the window (30/60-minute polling) is its own average and says so.
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (!s.Valid) continue;
            if (s.End - s.Start >= SmoothWindow)
            {
                s.Smooth = s.Rate;
                s.SmoothMinutes = s.Minutes;
                continue;
            }
            double sumDelta = 0, sumMinutes = 0;
            var windowStart = s.End - SmoothWindow - SmoothTolerance;
            for (var j = i; j >= 0; j--)
            {
                var p = segments[j];
                if (!p.Valid || p.Group != s.Group || p.Start < windowStart) break;
                sumDelta += p.Delta;
                sumMinutes += p.Minutes;
            }
            if (sumMinutes > 0)
            {
                s.Smooth = sumDelta * 60.0 / sumMinutes;
                s.SmoothMinutes = sumMinutes;
            }
        }

        return new SeriesData { Key = key, Segments = segments };
    }

    static bool OnlyCoveredInterruptions(IReadOnlyList<AppEvent> events, DateTimeOffset a, DateTimeOffset b,double pollSeconds)
    {
        // With normal sample spacing and matching quota windows, both counter readings still
        // measure the complete interval. Use one configured cadence as the interruption budget,
        // not a fixed 60s cutoff. Overlapping app/monitor pauses count as one interval.
        // Long sample gaps, source failures, sleep and structural quota boundaries stay invalid.
        bool exited=false,paused=false;DateTimeOffset? downSince=null;
        var pairs = 0; var seconds = 0d;
        for (var i = LowerBound(events, a); i < events.Count && events[i].T < b; i++)
        {
            var e = events[i];
            if (e.T <= a) continue;
            if (e.Type == EventTypes.AppExit && !exited) exited=true;
            else if (e.Type == EventTypes.AppStart && exited) { exited=false;pairs++; }
            else if (e.Type == EventTypes.MonitorPause && !paused) paused=true;
            else if (e.Type == EventTypes.MonitorResume && paused) { paused=false;pairs++; }
            else return false; // incomplete pair, real suspension, failure, or ambiguous sequence
            if(exited||paused)downSince??=e.T;
            else if(downSince is { } from){seconds+=(e.T-from).TotalSeconds;downSince=null;}
            if(seconds>pollSeconds) return false;
        }
        return pairs > 0 && !exited && !paused;
    }

    /// <summary>
    /// Why an interval might be unreliable, from recorded evidence only. "休眠" requires an OS
    /// suspend/resume event; flat quota never implies sleep.
    /// </summary>
    public static string? ClassifyEvents(IReadOnlyList<AppEvent> events, DateTimeOffset a, DateTimeOffset b)
    {
        var lo = LowerBound(events, a);
        bool sleep = false, run = false, auth = false, limited = false, network = false, failed = false, paused = false;
        for (var i = lo; i < events.Count && events[i].T < b; i++)
        {
            var e = events[i];
            if (e.T <= a) continue;
            switch (e.Type)
            {
                case EventTypes.MonitorPause or EventTypes.MonitorResume: paused = true; break;
                case EventTypes.Suspend or EventTypes.Resume: sleep = true; break;
                case EventTypes.AppStart or EventTypes.AppExit: run = true; break;
                case EventTypes.CollectFail:
                    failed = true;
                    if (e.Status == Statuses.AuthRequired) auth = true;
                    else if (e.Status == Statuses.RateLimited) limited = true;
                    else if (e.ErrorCode is "network" or "timeout" or "dns") network = true;
                    break;
            }
        }
        // A brief update/restart must not hide hours of explicitly recorded collection failure.
        return paused ? "已暂停" : sleep ? "休眠" : auth ? "未登录" : limited ? "限流" : network ? "断网" : failed ? "采集失败" : run ? "未运行" : null;
    }

    static int LowerBound(IReadOnlyList<AppEvent> events, DateTimeOffset t)
    {
        int lo = 0, hi = events.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (events[mid].T < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Sum of complete valid intervals inside [start, end]. Gaps are not filled with zero.</summary>
    public static RangeSum SumRange(SeriesData data, DateTimeOffset start, DateTimeOffset end)
    {
        double delta = 0, minutes = 0;
        foreach (var s in data.Segments)
        {
            if (!s.Valid || s.Start < start || s.End > end) continue;
            delta += s.Delta;
            minutes += s.Minutes;
        }
        return new RangeSum(delta, minutes);
    }

    /// <summary>
    /// Net change from the first to the latest reading of the current reliable window. It may span
    /// gaps only because both ends report the same reset instant (no reset in between).
    /// </summary>
    public static CumulativeAverage? Cumulative(IReadOnlyList<HistoryRecord> records, SeriesKey key, SeriesData data)
    {
        var last = -1;
        for (var i = records.Count - 1; i >= 0; i--)
            if (Select(records[i], key) is not null) { last = i; break; }
        if (last < 0) return null;
        var latest = Select(records[last], key)!;
        if (latest.WindowMode != WindowModes.Fixed) return null;

        var first = last;
        var laterUsed = latest.UsedPercent;
        for (var i = last - 1; i >= 0; i--)
        {
            var l = Select(records[i], key);
            if (l is null) continue;
            if (l.WindowMode != WindowModes.Fixed || !SameWindow(l, latest)) break;
            if (l.UsedPercent > laterUsed + Eps) break;
            first = i;
            laterUsed = l.UsedPercent;
        }
        if (first == last) return null;
        var from = records[first].T;
        var to = records[last].T;
        if (to - from < TimeSpan.FromMinutes(10)) return null;
        var includesGap = data.Segments.Any(s => s.Start >= from && s.End <= to && !s.Valid);
        return new CumulativeAverage(from, to, latest.UsedPercent - Select(records[first], key)!.UsedPercent, includesGap);
    }
}
