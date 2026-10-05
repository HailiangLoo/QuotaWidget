using System.Globalization;

namespace QuotaWidget.Core;

public sealed class MeterView
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public required string Other { get; init; }
    public required string Reset { get; init; }
    public double Fill { get; init; }
    public double? UsedPercent { get; init; }
    public bool Missing { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
}

public sealed record GapRegion(DateTimeOffset Start, DateTimeOffset End, string Label);

public enum NoteAction { None, Login, Retry }

public sealed class ChartView
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required bool Smooth { get; init; }
    public int TrendMinutes { get; init; } = 120;
    public IReadOnlyList<DateTimeOffset> TrendBoundaries { get; init; } = [];
    public TrendActivity Activity { get; init; } = TrendActivity.Empty;
    TrendActivity? _episodes;
    public TrendActivity Episodes => _episodes ??= Activity.Episodes(End);
    public IReadOnlyList<DateTimeOffset> VisibleTrendEdges =>
        (TotalVisible?WorkActivity.Edges(Episodes.Claude):[]).Concat(FableDrawable?WorkActivity.Edges(Episodes.Fable):[])
            .Concat(CodexVisible?WorkActivity.Edges(Episodes.Codex):[]).Concat(TrendBoundaries).Distinct().Order().ToArray();
    public required SeriesData Total { get; init; }
    public required SeriesData Fable { get; init; }
    public required List<GapRegion> Gaps { get; init; }
    public bool TotalVisible { get; init; } = true;
    public bool FableVisible { get; init; } = true;
    public SeriesData? Codex { get; init; }
    public bool CodexVisible { get; init; } = true;
    public bool ClaudeCumulativeMode { get; init; }
    public bool CodexCumulativeMode { get; init; }
    // Compatibility for callers that explicitly choose the same mode for both.
    public bool Cumulative { get => ClaudeCumulativeMode&&CodexCumulativeMode; init { ClaudeCumulativeMode=value;CodexCumulativeMode=value; } }
    public bool IsCumulative(bool codex) => codex ? CodexCumulativeMode : ClaudeCumulativeMode;
    public CumulativeSeries? TotalCumulative { get; init; }
    public CumulativeSeries? FableCumulative { get; init; }
    public CumulativeSeries? CodexCumulative { get; init; }
    public string? BlockedText { get; init; }
    public double? FableToClaudeFactor { get; init; }
    public bool ClaudeCollapsed { get; init; }
    public bool CodexCollapsed { get; init; }
    public List<GapRegion> ClaudeGaps { get; init; } = [];
    public List<GapRegion> CodexGaps { get; init; } = [];
    public IReadOnlyList<ChartSpan> FableOnlySpans {get;set;}=[];
    public bool MergesFable => !ClaudeCumulativeMode&&TotalVisible&&FableDrawable&&FableToClaudeFactor is not null&&FableOnlySpans.Count>0;
    public bool FableOnlyAt(DateTimeOffset time)=>MergesFable&&FableOnlySpans.Any(s=>time>=s.Start&&time<s.End);

    // A zero-consumption Fable baseline adds clutter and covers other providers' zero lines.
    // Keep its quota/summary and the user's visibility preference; reappear when usage occurs.
    public bool FableDrawable => FableVisible && (ClaudeCumulativeMode
        ? FableCumulative?.Segments.Any(s => s.To > 0) == true
        : Smooth ? ChartPath.Clip(FableTrend.Runs.Select(r=>r.Points),Start,End).Any(p=>p.Any(t=>t.Rate>0))
        : Fable.Segments.Any(s => s.Valid && s.Delta > 0 && s.End > Start && s.Start < End));
    RateTrend? _totalTrend, _fableTrend, _codexTrend;
    public DateTimeOffset EstimationStart => Total.Segments.Concat(Fable.Segments).Concat(Codex?.Segments??[]).Select(s=>s.Start).DefaultIfEmpty(Start).Min();
    // No viewport-local counter pairing or work-segment renormalization in the rate path.
    public QuotaAlignment Alignment => new(Total,Fable,0,0);
    public RateTrend TotalTrend => _totalTrend ??= Activity.ClaudeReady?ActiveRateEstimator.Build(Total,End,Activity.Claude,TrendMinutes,1,TrendBoundaries):new();
    public RateTrend FableTrend => _fableTrend ??= Activity.ClaudeReady?ActiveRateEstimator.Build(Fable,End,Activity.Fable,TrendMinutes,FableToClaudeFactor??1,TrendBoundaries):new();
    public RateTrend DisplayFableTrend => FableTrend;
    public RateTrend? CodexTrend => Codex is null ? null : _codexTrend ??= Activity.CodexReady?ActiveRateEstimator.Build(Codex,End,Activity.Codex,TrendMinutes,1,TrendBoundaries):new();
    public double? TotalAt(DateTimeOffset t) => ClaudeCumulativeMode ? TotalCumulative?.ValueAt(t) : Smooth ? TotalTrend.ValueAt(t) : Total.ValueAt(t, false);
    public double? FableAt(DateTimeOffset t) => ClaudeCumulativeMode ? FableCumulative?.ValueAt(t) : Smooth ? DisplayFableTrend.ValueAt(t) : Fable.ValueAt(t, false);
    public double? CodexAt(DateTimeOffset t) => CodexCumulativeMode ? CodexCumulative?.ValueAt(t) : Smooth ? CodexTrend?.ValueAt(t) : Codex?.ValueAt(t, false);

    public GapRegion? GapAt(DateTimeOffset t)
    {
        GapRegion? hit = null;
        foreach (var g in Gaps)
            if (t >= g.Start && t < g.End) hit = g;
        return hit;
    }
}

public sealed class WidgetView
{
    public string? Tag { get; init; }
    public required string PlanLabel { get; init; }
    public required string DisplayLabel { get; init; }
    public required MeterView Five { get; init; }
    public required MeterView Week { get; init; }
    public required MeterView Fable { get; init; }
    public string? Note { get; init; }
    public NoteAction NoteAction { get; init; }
    public required ChartView Chart { get; init; }
    public required string Summary { get; init; }
    public required string SummaryDetail { get; init; }
    public required string TrayText { get; init; }
    public required string CollectorLine { get; init; }
    public required string FableLegend { get; init; }
    public double SummaryTotal { get; init; }
    public double? SummaryFable { get; init; }
    public double SummaryCoverage { get; init; }
    public CumulativeAverage? FableAverage { get; init; }
}

/// <summary>
/// The widget's data side: reads latest.json, owns history and events, and turns them into
/// what the window shows. No UI types here so it can be tested headless.
/// </summary>
public sealed class WidgetModel
{
    readonly Func<DateTimeOffset> _clock;
    (DateTime, long)? _latestStamp;
    readonly HashSet<string> _loggedFailures = new();

    public WidgetModel(DataPaths paths, bool demoMode, Func<DateTimeOffset>? clock = null)
    {
        Paths = paths;
        DemoMode = demoMode;
        _clock = clock ?? (() => DateTimeOffset.Now);
        Settings = WidgetSettings.Load(paths.Settings, out var warning);
        SettingsWarning = warning;
        Store = new HistoryStore(paths, demoMode);
        Events = new EventLog(paths);
    }

    public DataPaths Paths { get; }
    public bool DemoMode { get; }
    public WidgetSettings Settings { get; }
    public string? SettingsWarning { get; }
    public HistoryStore Store { get; }
    public EventLog Events { get; }
    public string? SourceId { get; private set; }
    public string? ProfileKey { get; private set; }
    public List<HistoryRecord> Records { get; private set; } = new();
    public List<AppEvent> EventList { get; private set; } = new();
    public HistoryBounds? Bounds { get; private set; }
    public LatestEnvelope? LastEnvelope { get; private set; }
    public string? LatestError { get; private set; }
    public int IngestedCount { get; private set; }
    /// <summary>Bumped whenever the account/plan in view changes; the window re-reads per-profile settings.</summary>
    public int ProfileVersion { get; private set; }
    /// <summary>Which CLI the collector runs (or why none), for the settings panel.</summary>
    public string? CliSummary { get; set; }

    /// <summary>Kept in memory: covers a full weekly window plus a day. Older days stay on disk.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(8);

    /// <summary>Legacy per-profile calibration, retained only for settings compatibility.</summary>
    public double? CurrentQ => Settings.QFor(SourceId, ProfileKey);

    public void SetCurrentQ(double? q)
    {
        if (SourceId is null || ProfileKey is null) return;
        Settings.SetQ(SourceId, ProfileKey, q);
        SaveSettings();
    }

    // Derived series are rebuilt only when records or events change, not on every clock tick.
    int _dataVersion, _cachedVersion = -1;
    SeriesData? _total, _fable;
    SeriesData? _archiveTotal, _archiveFable;
    int _archiveVersion = -1;
    public bool ArchiveLoaded => _archiveTotal is not null;
    CumulativeAverage? _cumTotal, _cumFable;

    void Touch() => _dataVersion++;

    (DateTime Month,int Version,IReadOnlyList<DayQuota> Days)? _calendar;
    public IReadOnlyList<DayQuota> CalendarMonth(DateTime month,DateTimeOffset now)
    {
        month=new DateTime(month.Year,month.Month,1);
        if(_calendar is { } cached && cached.Month==month && cached.Version==_dataVersion) return cached.Days;
        var next=month.AddMonths(1); var zone=TimeZoneInfo.Local;
        // One prior quota cycle provides the reset-jitter anchor; one following day covers midnight samples.
        var from=DailyQuota.Midnight(month.AddDays(-8),zone);
        var until=DailyQuota.Midnight(next.AddDays(1),zone); if(until>now) until=now;
        var records=SourceId is not null && ProfileKey is not null ? Store.Load(SourceId,ProfileKey,from,index:false,until:until) : [];
        var keys=records.Select(r=>r.Key).ToHashSet();
        records.AddRange(Records.Where(r=>r.T>=from&&r.T<=until&&keys.Add(r.Key)));
        records.Sort((a,b)=>a.T.CompareTo(b.T));
        var events=RateEngine.RelevantEvents(EventLog.Merge(Events.Load(from,until),EventList.Where(e=>e.T>=from&&e.T<=until)),SourceId,ProfileKey);
        var days=DailyQuota.Build(RateEngine.Build(records,SeriesKey.Total,events),month,next,zone,now);
        _calendar=(month,_dataVersion,days);
        return days;
    }
    public void ReleaseCalendar() => _calendar=null;

    public void ReleaseArchive()
    {
        _archiveTotal = _archiveFable = null;
        _archiveVersion = -1;
    }

    (SeriesData Total, SeriesData Fable) Archive(DateTimeOffset now)
    {
        if (_archiveVersion != _dataVersion || _archiveTotal is null || _archiveFable is null)
        {
            // A temporary read-only load. Never expand the live 8-day dedupe index.
            var records = SourceId is not null && ProfileKey is not null
                ? Store.Load(SourceId, ProfileKey, DateTimeOffset.UnixEpoch, index: false) : [];
            var keys = records.Select(r => r.Key).ToHashSet();
            records.AddRange(Records.Where(r => keys.Add(r.Key)));
            records.RemoveAll(r => r.T > now);
            records.Sort((a, b) => a.T.CompareTo(b.T));
            var events = RateEngine.RelevantEvents(EventLog.Merge(Events.Load(DateTimeOffset.UnixEpoch), EventList), SourceId, ProfileKey);
            _archiveTotal = RateEngine.Build(records, SeriesKey.Total, events);
            _archiveFable = RateEngine.Build(records, SeriesKey.Fable, events);
            _archiveVersion = _dataVersion;
        }
        return (_archiveTotal, _archiveFable);
    }

    (SeriesData Total, SeriesData Fable, CumulativeAverage? CumTotal, CumulativeAverage? CumFable) Derived()
    {
        if (_cachedVersion != _dataVersion || _total is null || _fable is null)
        {
            var events = RateEngine.RelevantEvents(EventList, SourceId, ProfileKey);
            _total = RateEngine.Build(Records, SeriesKey.Total, events);
            _fable = RateEngine.Build(Records, SeriesKey.Fable, events);
            _cumTotal = RateEngine.Cumulative(Records, SeriesKey.Total, _total);
            _cumFable = RateEngine.Cumulative(Records, SeriesKey.Fable, _fable);
            _cachedVersion = _dataVersion;
        }
        return (_total, _fable, _cumTotal, _cumFable);
    }

    /// <summary>Drops records, events and dedupe entries that fell out of the retained window.</summary>
    public void Trim(DateTimeOffset now)
    {
        var cutoff = now - Lookback;
        var oldRecords = Records.FindIndex(r => r.T >= cutoff);
        if (oldRecords < 0) oldRecords = Records.Count;
        var oldEvents = EventList.FindIndex(e => e.T >= cutoff);
        if (oldEvents < 0) oldEvents = EventList.Count;
        if (oldRecords == 0 && oldEvents == 0) return;
        Records.RemoveRange(0, oldRecords);
        EventList.RemoveRange(0, oldEvents);
        _loggedFailures.RemoveWhere(k => DateTimeOffset.Parse(k, CultureInfo.InvariantCulture) < cutoff);
        Store.Prune(cutoff);
        Touch();
    }

    public void Initialize()
    {
        var now = _clock();
        SourceId = Settings.LastSourceId;
        ProfileKey = Settings.LastProfileKey;
        EventList = Events.Load(now - Lookback);
        foreach (var e in EventList)
            if (e.Type == EventTypes.CollectFail) _loggedFailures.Add(SnapshotJson.FormatTimestamp(e.T.ToUniversalTime()));
        if (SourceId is not null && ProfileKey is not null && HistoryStore.IsDemoSource(SourceId) == DemoMode)
        {
            Records = Store.Load(SourceId, ProfileKey, now - Lookback);
            Bounds = Store.Bounds(SourceId, ProfileKey, now);
        }
        if (!File.Exists(Paths.Settings)) SaveSettings(); // leave an editable settings.json behind
        Touch();
        PollLatest();
    }

    /// <summary>Read-only views (snapshot rendering) never write history, events or settings.</summary>
    public bool ReadOnly { get; set; }

    public void SaveSettings()
    {
        if (!ReadOnly) Settings.Save(Paths.Settings);
    }

    public void RecordEvent(AppEvent e)
    {
        e = EventLog.AtStoredPrecision(e);
        if (!ReadOnly) Events.Append(e);
        InsertSorted(EventList, e, x => x.T);
        Touch();
    }

    /// <summary>Checks latest.json for a change; returns true when something new was taken in.</summary>
    public bool PollLatest()
    {
        Trim(_clock());
        var fi = new FileInfo(Paths.Latest);
        (DateTime, long)? stamp = fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : null;
        if (Equals(stamp, _latestStamp)) return false;
        _latestStamp = stamp;
        if (stamp is null) return false;
        var text = AtomicFile.TryReadAllText(Paths.Latest);
        if (text is null) return false;
        var errors = new List<string>();
        var env = SnapshotJson.ParseEnvelope(text, errors);
        if (env is null)
        {
            LatestError = "latest.json 格式错误 · 已忽略";
            return true;
        }
        if (HistoryStore.IsDemoSource(env.SourceId) != DemoMode)
        {
            LatestError = DemoMode ? "非演示数据 · 已忽略" : "latest.json 是演示数据 · 未写入历史";
            return true;
        }
        if (env.Snapshot is not null && env.Snapshot.ObservedAt > _clock() + TimeSpan.FromMinutes(5))
        {
            LatestError = "latest.json 观测时间在未来 · 已忽略";
            return true;
        }
        LatestError = null;
        LastEnvelope = env;
        Ingest(env);
        return true;
    }

    void Ingest(LatestEnvelope env)
    {
        if (env.Snapshot is null)
        {
            var key = SnapshotJson.FormatTimestamp(env.AttemptedAt.ToUniversalTime());
            if (_loggedFailures.Add(key))
                RecordEvent(new AppEvent(env.AttemptedAt, EventTypes.CollectFail, env.Status, env.ErrorCode, env.SourceId, env.ProfileKey));
            return;
        }
        if (env.SourceId != SourceId || env.ProfileKey != ProfileKey)
        {
            // New account or plan: its own history, its own curve.
            SourceId = env.SourceId;
            ProfileKey = env.ProfileKey;
            Settings.LastSourceId = SourceId;
            Settings.LastProfileKey = ProfileKey;
            SaveSettings();
            Records = Store.Load(SourceId, ProfileKey, _clock() - Lookback);
            Bounds = Store.Bounds(SourceId, ProfileKey, _clock());
            ReleaseArchive();
            ProfileVersion++;
            Touch();
        }
        var rec = HistoryRecord.FromEnvelope(env);
        if (rec.T < _clock() - Lookback)
        {
            LatestError = "快照早于保留期 · 已忽略";
            return;
        }
        if (ReadOnly)
        {
            if (!Records.Any(r => r.Key == rec.Key)) InsertSorted(Records, rec, x => x.T);
            Bounds = Bounds?.Include(rec.T) ?? new(rec.T, rec.T);
            Touch();
            return;
        }
        switch (Store.Append(rec, _clock()))
        {
            case AppendResult.Added:
                InsertSorted(Records, rec, x => x.T);
                Bounds = Bounds?.Include(rec.T) ?? new(rec.T, rec.T);
                IngestedCount++;
                Touch();
                break;
            case AppendResult.Conflict:
                LatestError = "快照 ID 冲突 · 已忽略";
                break;
        }
    }

    static void InsertSorted<T>(List<T> list, T item, Func<T, DateTimeOffset> key)
    {
        var i = list.Count;
        while (i > 0 && key(list[i - 1]) > key(item)) i--;
        list.Insert(i, item);
    }

    // ---------- view ----------

    public static string RangeLabel(int minutes) => minutes == ChartRanges.All ? "all" : minutes == 4320 ? "3d" : minutes < 60 ? minutes + "m" : minutes / 60 + "h";

    static string Pct(double v)
    {
        var r = Math.Round(v);
        return Math.Abs(v - r) < 0.05 ? r.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture);
    }

    public static string Countdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null) return Loc.T("重置时间未知");
        var left = resetsAt.Value - now;
        if (left <= TimeSpan.Zero) return Loc.T("已到重置时间");
        var totalMin = (int)Math.Floor(left.TotalMinutes);
        var d = totalMin / 1440;
        var h = totalMin % 1440 / 60;
        var m = totalMin % 60;
        if (d > 0) return Loc.F($"{d}天{h}时重置");
        if (h > 0) return Loc.F($"{h}时{m:00}分重置");
        return Loc.F($"{m}分重置");
    }

    public static string Duration(double minutes)
    {
        var total = (int)Math.Round(minutes);
        if (total < 60) return total + Loc.T("分钟");
        return total % 60 == 0 ? Loc.F($"{total / 60}小时") : Loc.F($"{total / 60}小时{total % 60}分");
    }

    static string Clock(DateTimeOffset t, DateTimeOffset now)
    {
        var l = t.ToLocalTime();
        return l.Date == now.ToLocalTime().Date ? l.ToString("HH:mm", CultureInfo.InvariantCulture) : l.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
    }

    static string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    MeterView Meter(string label, QuotaLimit? limit, bool haveRecord, DateTimeOffset now)
    {
        if (!haveRecord)
            return new MeterView { Label = label, Value = "—", Other = Loc.T("等待数据"), Reset = Loc.T("重置时间未知"), Missing = true };
        if (limit is null)
            return new MeterView { Label = label, Value = "—", Other = Loc.T("来源缺少该指标"), Reset = "", Missing = true };
        var used = limit.UsedPercent;
        var showUsed = Settings.Display == "used";
        var value = showUsed ? used : 100 - used;
        return new MeterView
        {
            Label = label,
            Value = Pct(value) + "%",
            Other = showUsed ? Loc.F($"剩 {Pct(100 - used)}%") : Loc.F($"已用 {Pct(used)}%"),
            Reset = Countdown(limit.ResetsAt, now),
            ResetsAt = limit.ResetsAt,
            Fill = Math.Clamp(value, 0, 100),
            UsedPercent = used,
        };
    }

    public bool IsStale(DateTimeOffset now)
    {
        if (Records.Count == 0) return false;
        var last = Records[^1];
        var poll = Math.Max(last.PollSeconds, Settings.PollIntervalSeconds);
        return (now - last.T).TotalSeconds > RateEngine.GapFactor * poll + 60;
    }

    public WidgetView BuildView(DateTimeOffset now, int? rangeMinutes = null)
    {
        var s = Settings;
        var last = Records.Count > 0 ? Records[^1] : null;
        var fableName = s.FableModelName;
        var (total, fable, cumT, cumF) = Derived();
        var range = rangeMinutes ?? s.RangeMinutes;
        if (range == ChartRanges.All) (total, fable) = Archive(now);
        else ReleaseArchive();
        var stale = IsStale(now);

        // Chart window
        var end = last is null || stale ? now : last.T;
        var start = range == ChartRanges.All ? Bounds?.First ?? end.AddHours(-1) : end.AddMinutes(-range);
        if (start >= end) start = end.AddMinutes(-5);
        var gaps = new List<GapRegion>();
        string? blocked = null;
        if (total.Segments.Count == 0 && fable.Segments.Count == 0 && Records.Count == 0) blocked = Loc.T("等待有效记录");
        else if (!total.Segments.Any(x => x.Valid) && !fable.Segments.Any(x => x.Valid))
            blocked = Records.Count == 1 ? Loc.T("等待下一次采样") : Loc.T("暂无可计算的连续记录");
        if (last is not null)
        {
            if (Records[0].T > start) gaps.Add(new GapRegion(start, Records[0].T, "未记录"));
            foreach (var seg in total.Segments)
                if (!seg.Valid && seg.End > start && seg.Start < end) gaps.Add(new GapRegion(seg.Start, seg.End, seg.Label ?? "缺口"));
            if (stale) gaps.Add(new GapRegion(last.T, now, "断开"));
        }
        var chart = new ChartView
        {
            Start = start, End = end, Smooth = s.Smoothing, TrendMinutes = s.TrendMinutes, Total = total, Fable = fable, Gaps = gaps,
            TotalVisible = s.TotalVisible, FableVisible = s.FableVisible, BlockedText = blocked,
        };

        // Summary row: complete valid intervals inside the chart window, gaps not filled.
        var rangeLabel = ChartRanges.Label(range);
        var sumT = RateEngine.SumRange(total, start, end);
        var sumF = RateEngine.SumRange(fable, start, end);
        string summary;
        if (blocked is not null) summary = Loc.T("等待有效记录");
        else if (sumT.CoverageMinutes <= 0) summary = Loc.T("所选时段没有有效记录");
        else
        {
            var fablePart = sumF.CoverageMinutes <= 0 ? Loc.T("Fable 无记录") : Loc.F($"Fable {F1(sumF.Delta)}点");
            summary = Loc.F($"{rangeLabel} · 已记录 {F1(sumT.Delta)}点 · {fablePart}");
        }

        var detail = new List<string>();
        if (blocked is not null) detail.Add(Loc.T("尚未取得可计算的连续数据"));
        else
        {
            detail.Add(Loc.F($"{rangeLabel}：总消耗覆盖{Duration(sumT.CoverageMinutes)}，{fableName} 覆盖{Duration(sumF.CoverageMinutes)}"));
            detail.Add(Loc.T("只累计完整有效采样区间，缺口未补零"));
            if (sumT.CoverageMinutes > 0)
            {
                var avg = Loc.F($"已采样平均 {F2(sumT.Delta * 60 / sumT.CoverageMinutes)} 点/h");
                if (sumF.CoverageMinutes > 0) avg += $" · Fable {F2(sumF.Delta * 60 / sumF.CoverageMinutes)}";
                detail.Add(avg);
            }
            if (cumT is not null)
            {
                var line = Loc.F($"自{Clock(cumT.From, now)}累计平均 {F2(cumT.Rate)} 点/h（同周期{(cumT.IncludesGap ? Loc.T("，含缺口") : "")}）");
                if (cumF is not null) line += $" · Fable {F2(cumF.Rate)}";
                detail.Add(line);
            }
            else detail.Add(Loc.T("累计平均：当前周期记录不足"));
            detail.Add(Loc.T("各自周额度点/h；不同线的 1 点不代表相同 token 数，不能相加。"));
        }

        // Note bar: one line, only when something needs saying.
        string? note = null;
        var action = NoteAction.None;
        var env = LastEnvelope;
        if (LatestError is not null) note = Loc.T(LatestError);
        else if (env is { Status: Statuses.AuthRequired })
        {
            note = env.ErrorCode switch
            {
                "not_logged_in" => Loc.T("未登录 Claude · 点此登录"),
                "identity_unknown" => Loc.T("登录信息缺少账号标识 · 点此重新登录"),
                _ => Loc.T("登录已失效 · 点此重新登录"),
            };
            action = NoteAction.Login;
        }
        else if (env is { Status: Statuses.Error, ErrorCode: "cli_untrusted" })
            note = Loc.T("Claude CLI 签名校验未通过 · 已停止调用");
        else if (env is { Status: Statuses.Error, ErrorCode: "cli_missing" })
            note = Loc.T("未找到 Claude Code CLI");
        else if (env is { Status: Statuses.Error, ErrorCode: "cli_incompatible" })
            note = Loc.T("Claude CLI 版本不支持额度接口 · 请更新");
        else if (env is { Status: Statuses.RateLimited })
        {
            var wait = env.AttemptedAt + TimeSpan.FromSeconds(Math.Max(env.RetryAfterSeconds ?? 0, env.EffectivePollIntervalSeconds)) - now;
            note = wait > TimeSpan.Zero ? Loc.F($"服务限流 · {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))}分钟后重试") : Loc.T("服务限流 · 即将重试");
        }
        else if (env is { Status: Statuses.Error })
        {
            var reason = env.ErrorCode switch
            {
                "network" or "dns" => Loc.T("网络"),
                "timeout" => Loc.T("超时"),
                "unavailable" => Loc.T("额度暂不可读"),
                "not_fresh" => Loc.T("未取得新数据"),
                "bad_output" or "cli_failed" => Loc.T("CLI 调用失败"),
                "no_known_limits" or "value_out_of_range" => Loc.T("数据格式变化"),
                _ => env.ErrorCode ?? Loc.T("未知"),
            };
            note = last is null ? Loc.F($"采集失败（{reason}）") : Loc.F($"采集失败（{reason}）· 数据停在 {Clock(last.T, now)}");
            action = NoteAction.Retry;
        }
        else if (stale) note = Loc.F($"{(int)(now - last!.T).TotalMinutes}分钟未更新 · 曲线为旧数据");
        else if (last is null) note = s.CollectorEnabled || DemoMode ? Loc.T("等待首次数据") : Loc.T("等待数据源（latest.json）");
        else if (total.Segments.Any(x => x.Issue == SegmentIssue.Decrease && x.End > now - TimeSpan.FromDays(1)))
            note = Loc.T("周额度同周期内下降 · 相关区间不计速率");
        else if (last.Snapshot.Limits.AllWeek is { WindowMode: not WindowModes.Fixed })
            note = Loc.T("周额度周期未确认 · 暂不计算速率");
        else if (env is { ErrorCode: "value_out_of_range" })
            note = Loc.T("部分额度数值越界 · 已按缺失处理");

        var l = last?.Snapshot.Limits;
        var five = Meter(Loc.T("5 小时"), l?.FiveHour, last is not null, now);
        var week = Meter(Loc.T("本周 · 全部"), l?.AllWeek, last is not null, now);
        var fab = Meter(Loc.F($"本周 · {fableName}"), l?.FableWeek, last is not null, now);

        var tray = last is null ? Loc.T("额度 · 等待数据")
            : Loc.F($"额度 · 5h {five.Value} · 周 {week.Value} · {fableName} {fab.Value}{(stale ? Loc.T(" · 旧数据") : "")}");

        string collectorLine;
        if (DemoMode) collectorLine = Loc.T("演示数据 · 不采集");
        else if (!s.CollectorEnabled) collectorLine = Loc.T("内置采集已关闭 · 读取 latest.json");
        else if (env is null) collectorLine = Loc.T("采集：启动中");
        else
        {
            var next = env.AttemptedAt + TimeSpan.FromSeconds(env.EffectivePollIntervalSeconds);
            collectorLine = env.Status switch
            {
                Statuses.Ok or Statuses.Partial => Loc.F($"采集正常 · 上次 {Clock(env.AttemptedAt, now)} · 下次 {Clock(next, now)}"),
                Statuses.AuthRequired => Loc.T("采集暂停：需要登录"),
                Statuses.RateLimited => Loc.F($"采集限流 · {Clock(next, now)} 后重试"),
                _ => Loc.F($"采集失败 · {Clock(next, now)} 后重试（本地退避）"),
            };
        }
        if (CliSummary is not null && !DemoMode) collectorLine += "\n" + CliSummary;

        return new WidgetView
        {
            Tag = DemoMode ? Loc.T("演示数据") : null,
            PlanLabel = last?.PlanLabel ?? env?.PlanLabel ?? "Claude",
            DisplayLabel = s.Display == "used" ? Loc.T("已用额度") : Loc.T("剩余额度"),
            Five = five, Week = week, Fable = fab,
            Note = note, NoteAction = action,
            Chart = chart,
            Summary = summary,
            SummaryDetail = string.Join("\n", detail),
            TrayText = tray.Length > 120 ? tray[..120] : tray,
            CollectorLine = collectorLine,
            FableLegend = fableName,
            SummaryTotal = sumT.Delta,
            SummaryFable = sumF.CoverageMinutes > 0 ? sumF.Delta : null,
            FableAverage = cumF,
            SummaryCoverage = sumT.CoverageMinutes,
        };
    }
}
