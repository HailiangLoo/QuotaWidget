using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public sealed class AppOptions
{
    public bool Demo { get; private set; }
    public string Scenario { get; private set; } = "normal";
    public string? DataDir { get; private set; }
    public string? Snapshot { get; private set; }
    public double Scale { get; private set; } = 1;
    public string? Theme { get; private set; }
    public int? Range { get; private set; }
    public string? ChartMode { get; private set; }
    public string? ClaudeChartMode { get; private set; }
    public string? CodexChartMode { get; private set; }
    public double? InspectMinutesAgo { get; private set; }
    public string? InspectPlatform { get; private set; }
    public bool SettingsOpen { get; private set; }
    public bool Setup { get; private set; }
    public bool HistoryOpen { get; private set; }
    public bool ExpandArchive { get; private set; }
    public double? Width { get; private set; }
    public bool? Compact { get; private set; }
    public bool CollapseClaude { get; private set; }
    public bool CollapseCodex { get; private set; }
    public bool ChatCard { get; private set; }
    public string? UsageCard {get;private set;}
    public string? Monitoring {get;private set;}
    public string? Language {get;private set;}
    public bool UsagePinned {get;private set;}
    public int UsageMinutes {get;private set;}
    public DateTime? HistoryDay { get; private set; }
    public bool CollectOnce { get; private set; }
    public bool Hidden { get; private set; }
    public bool Exit { get; private set; }
    public bool QaWindow { get; private set; }
    public bool UsageDiagnostics { get; private set; }
    public string? ExportLicenses { get; private set; }
    /// <summary>"login" or "logout": run the shared auth path and exit (used by 登录Claude.cmd).</summary>
    public string? Auth { get; private set; }

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--demo": o.Demo = true; break;
                case "--scenario": o.Scenario = Next(); break;
                case "--data-dir": o.DataDir = Next(); break;
                case "--snapshot": o.Snapshot = Next(); break;
                case "--scale": o.Scale = double.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1; break;
                case "--theme": o.Theme = Next(); break;
                case "--range": o.Range = int.TryParse(Next(), out var r) ? r : null; break;
                case "--chart-mode": o.ChartMode = Next(); break;
                case "--claude-chart-mode": o.ClaudeChartMode = Next(); break;
                case "--codex-chart-mode": o.CodexChartMode = Next(); break;
                case "--inspect": o.InspectMinutesAgo = double.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : null; break;
                case "--inspect-platform": var platform = Next(); o.InspectPlatform = platform is "claude" or "codex" ? platform : null; break;
                case "--settings": o.SettingsOpen = true; break;
                case "--setup": o.Setup = true; o.SettingsOpen = true; break;
                case "--history": o.HistoryOpen = true; break;
                case "--expand-archive": o.ExpandArchive = true; break;
                case "--width": o.Width = double.TryParse(Next(), out var w) ? w : null; break;
                case "--compact": o.Compact = true; break;
                case "--expanded": o.Compact = false; break;
                case "--collapse-claude": o.CollapseClaude = true; break;
                case "--collapse-codex": o.CollapseCodex = true; break;
                case "--chat-card": o.ChatCard = true; break;
                case "--usage-card": o.UsageCard=Next();break;
                case "--monitor": o.Monitoring=Next();break;
                case "--language": o.Language=Next();break;
                case "--usage-pinned": o.UsagePinned=true;break;
                case "--usage-range": o.UsageMinutes=int.TryParse(Next(),out var um)&&new[]{0,60,120,300,720,1440,4320}.Contains(um)?um:0;break;
                case "--history-date": o.HistoryDay = DateTime.TryParseExact(Next(),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var day) ? day : null; break;
                case "--collect-once": o.CollectOnce = true; break;
                case "--hidden": o.Hidden = true; break;
                case "--exit": o.Exit = true; break;
                case "--qa-window": o.QaWindow = true; break;
                case "--diagnose-usage": o.UsageDiagnostics = true; break;
                case "--export-licenses": o.ExportLicenses = Next(); break;
                case "--login": o.Auth = "login"; break;
                case "--logout": o.Auth = "logout"; break;
            }
        }
        return o;
    }
}

public partial class App : Application
{
    readonly bool _startRuntime=true;
    public App() { }
    // Isolated WPF probes can pump the dispatcher without starting collectors or opening real data.
    internal App(bool startRuntime) { _startRuntime=startRuntime; }
    AppOptions _opts = new();
    DataPaths _paths = null!;
    WidgetModel _model = null!;
    WidgetModel _codex = null!;
    CodexUsageCollector? _codexCollector;
    Task? _codexTask;
    MainWindow _window = null!;
    TrayIcon? _tray;
    ClaudeUsageCollector? _collector;
    CancellationTokenSource _cts = new();
    Task? _collectorTask;
    Task? _cacheTask;
    Task? _tokenTask;
    TokenIndex? _tokens;
    TokenStore? _snapshotTokens;
    volatile string? _tokenWarning;
    volatile IReadOnlyList<ChatCacheEntry> _cacheEntries = Array.Empty<ChatCacheEntry>();
    volatile string? _cacheWarning;
    volatile ChatLifecycleSnapshot _chatLifecycle = ChatLifecycleSnapshot.Empty;
    ChatSessionHistory _chatHistory = null!;
    volatile ChatSession? _currentChatSession;
    volatile IReadOnlyList<ChatCacheEntry> _recentCompacts = Array.Empty<ChatCacheEntry>();
    Mutex? _mutex;
    EventWaitHandle? _showSignal;
    EventWaitHandle? _exitSignal;
    DispatcherTimer? _fileTimer, _clockTimer;
    bool _exiting;
    bool _themeForced;
    int _hiddenTicks;
    string? _tempRoot;

    public bool WindowVisible => _window.IsVisible;
    public bool IsTopmost => _model.Settings.Topmost;
    public bool CollectorAvailable => _collector is not null;
    public bool Monitors(ChatPlatform platform)=>_model.Settings.Monitors(platform);
    public bool Listens(ChatPlatform platform)=>_model.Settings.Listens(platform);
    public bool Exiting => _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        if(!_startRuntime)return;
        base.OnStartup(e);
        _opts = AppOptions.Parse(e.Args);
        Loc.Configure(_opts.Language);
        if (_opts.ExportLicenses is { } licensePath)
        {
            File.WriteAllText(licensePath, LicenseWindow.Notices);
            Shutdown(); return;
        }
        var snapshot = _opts.Snapshot is not null;
        // A demo snapshot renders from a throwaway copy, so it never disturbs a running demo.
        var root = _opts.DataDir ?? (_opts.Demo
            ? snapshot ? Path.Combine(Path.GetTempPath(), "QuotaWidget-snap-" + Guid.NewGuid().ToString("N")[..8], "demo") : DataPaths.DemoRoot
            : DataPaths.DefaultRoot);
        _paths = new DataPaths(root);
        _paths = new DataPaths(DataPaths.CanonicalDirectory(_paths.Root));
        root = _paths.Root;
        if (_opts.DataDir is null && !_opts.Demo && !snapshot)
        {
            DataPaths.RememberLocation(_paths.Root);
        }
        if (snapshot && _opts.Demo && _opts.DataDir is null) _tempRoot = Path.GetDirectoryName(root);
        ClaudeCli.CachePath = Path.Combine(_paths.Root, "cli-compat.json");

        if (_opts.Exit)
        {
            if (EventWaitHandle.TryOpenExisting($"Local\\QuotaWidget-exit-{InstanceId(root)}", out var signal))
            { signal.Set(); signal.Dispose(); }
            Shutdown(); return;
        }

        if (_opts.Auth is { } auth)
        {
            // Login needs no instance lock: it only starts the official CLI; a running widget
            // notices the new login by itself.
            var settings = WidgetSettings.Load(_paths.Settings, out _);
            Loc.Configure(_opts.Language ?? settings.Language);
            var error = RunAuth(settings, logout: auth == "logout", confirm: auth == "logout");
            Shutdown(error is null ? 0 : 4);
            return;
        }

        if (!snapshot && !AcquireSingleInstance(root))
        {
            if (_opts.CollectOnce) Console.Error.WriteLine("widget is running and already collecting; not polling twice");
            Shutdown(3);
            return;
        }
        if (_opts.CollectOnce)
        {
            RunCollectOnce();
            return;
        }

        var firstUse=!File.Exists(_paths.Settings);
        if (_opts.Demo) DemoData.Generate(_paths, DateTimeOffset.Now, _opts.Scenario);
        _themeForced = _opts.Theme is "dark" or "light";
        Theme.Apply(_opts.Theme switch { "dark" => true, "light" => false, _ => Theme.SystemPrefersDark() });

        _model = new WidgetModel(_paths, _opts.Demo) { ReadOnly = snapshot };
        _model.Initialize();
        if (_opts.Setup || (firstUse&&!_opts.Demo&&_opts.Snapshot is null))
        { _model.Settings.SetupCompleted=false;_model.SaveSettings(); }
        if (_opts.Language is "auto" or "en" or "zh-CN") _model.Settings.Language = _opts.Language;
        Loc.Configure(_model.Settings.Language);
        if (_opts.Range is { } range && WidgetSettings.RangeChoices.Contains(range)) _model.Settings.RangeMinutes = range;
        if (_opts.ChartMode is "rate" or "cumulative") _model.Settings.ClaudeChartMode = _model.Settings.CodexChartMode = _opts.ChartMode;
        if (_opts.ClaudeChartMode is "rate" or "cumulative") _model.Settings.ClaudeChartMode = _opts.ClaudeChartMode;
        if (_opts.CodexChartMode is "rate" or "cumulative") _model.Settings.CodexChartMode = _opts.CodexChartMode;
        if (_opts.Width is { } width) { _model.Settings.Width = width; _model.Settings.Normalize(); }
        if (_opts.Compact is { } compact) _model.Settings.CompactMode = compact;
        if (_opts.Monitoring is "both" or "claude" or "codex") _model.Settings.Monitoring=_opts.Monitoring;
        if (_opts.CollapseClaude) _model.Settings.ClaudeChartCollapsed = true;
        if (_opts.CollapseCodex) _model.Settings.CodexChartCollapsed = true;

        var codexPaths = new DataPaths(Path.Combine(_paths.Root, _opts.Demo ? "codex/demo" : "codex"));
        if (_opts.Demo) DemoData.Generate(codexPaths, DateTimeOffset.Now, _opts.Scenario, codex: true);
        _codex = new WidgetModel(codexPaths, _opts.Demo) { ReadOnly = snapshot };
        _codex.Initialize();
        _chatHistory = new ChatSessionHistory(_paths.Root);
        _currentChatSession = _chatHistory.Current(DateTimeOffset.Now);
        _recentCompacts = _chatHistory.RecentCompacted(DateTimeOffset.Now);
        _window = new MainWindow(this, _model, _codex, _opts);
        if (snapshot)
        {
            var db = Path.Combine(_paths.Root, "tokens", "tokens.sqlite");
            if (!_opts.Demo && File.Exists(db)) try { _snapshotTokens = new TokenStore(db, readOnly: true); } catch { _tokenWarning = "token 索引暂不可读"; }
            RunSnapshot();
            return;
        }

        _model.RecordEvent(new AppEvent(DateTimeOffset.Now, EventTypes.AppStart));
        _codex.RecordEvent(new AppEvent(DateTimeOffset.Now, EventTypes.AppStart));
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SessionEnding += (_, _) =>
        {
            if (_exiting) return;
            RecordExit(); _cts.Cancel();
            // Windows may terminate us early; this is best effort, never a revocation guarantee.
            try { Task.Run(StopWorkersAndLogoutAsync).Wait(TimeSpan.FromSeconds(11)); } catch { }
        };

        if (!_opts.Demo)
        {
            _collector = new ClaudeUsageCollector(_paths, () => _model.Settings, NewUsageSource(_model.Settings));
            _collectorTask = Task.Run(() => _collector.RunAsync(_cts.Token));
            _codexCollector = new CodexUsageCollector(codexPaths, () => _model.Settings, new CodexUsageSource(Path.Combine(_paths.Root, "codex-work")));
            _codexTask = Task.Run(() => _codexCollector.RunAsync(_cts.Token));
            _cacheTask = Task.Run(WatchChatsAsync);
            _tokenTask = Task.Run(WatchTokensAsync);
            // Show which CLI will be used (or why none) even before the first login.
            var configured = _model.Settings.ClaudeExePath;
            if(Monitors(ChatPlatform.Claude)) Task.Run(() => ClaudeCli.Resolve(configured)).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) Dispatcher.BeginInvoke(() => { _startupChoice = t.Result; Render(); });
            });
        }

        _tray = new TrayIcon(this);
        _fileTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { if ((Monitors(ChatPlatform.Claude)&&_model.PollLatest()) | (Monitors(ChatPlatform.Codex)&&_codex.PollLatest())) Render(); }, Dispatcher);
        // Cheap clock checks; visible detail windows reload local usage only when their cadence is due.
        // Curves stay cached until data changes. Hidden widget: update the tray less often.
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) =>
        {
            _window.RefreshPinnedUsage(DateTimeOffset.Now);
            if (_window.IsVisible || ++_hiddenTicks % 4 == 0) Render();
        }, Dispatcher);
        _fileTimer.Start();
        _clockTimer.Start();
        Render();
        if (!_opts.Hidden || !_model.Settings.SetupCompleted) { _window.Show(); Render(); }
        var logoutResult = AtomicFile.TryReadAllText(Path.Combine(_paths.Root, "logout-result.txt"));
        if (!_opts.Demo && logoutResult is { Length: > 0 }) _window.Flash(Loc.T("上次退出未确认清除登录，可在设置的数据诊断中重试"));
    }

    static string InstanceId(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant())))[..12];

    bool AcquireSingleInstance(string root)
    {
        var id = InstanceId(root);
        _mutex = new Mutex(true, $"Local\\QuotaWidget-{id}", out var created);
        var signalName = $"Local\\QuotaWidget-show-{id}";
        if (!created)
        {
            if (!_opts.CollectOnce && !_opts.Hidden && EventWaitHandle.TryOpenExisting(signalName, out var other))
            {
                other.Set();
                other.Dispose();
            }
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\QuotaWidget-exit-{id}");
        new Thread(() => { _exitSignal.WaitOne(); if (!_exiting) Dispatcher.BeginInvoke(ExitApp); }) { IsBackground = true, Name = "exit-signal" }.Start();
        var t = new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                if (_exiting) break;
                Dispatcher.BeginInvoke(ShowWindow);
            }
        }) { IsBackground = true, Name = "show-signal" };
        t.Start();
        return true;
    }

    void RunCollectOnce()
    {
        var settings = WidgetSettings.Load(_paths.Settings, out _);
        using var collector = new ClaudeUsageCollector(_paths, () => settings, NewUsageSource(settings));
        // Off the dispatcher thread: blocking it while awaiting would deadlock.
        var env = Task.Run(() => collector.CollectOnceAsync(CancellationToken.None)).GetAwaiter().GetResult();
        if (env is null)
        {
            // Same rule as the timer and the button: no request inside a back-off window.
            Console.Out.WriteLine(JsonSerializer.Serialize(new { skipped = "back-off or spacing", notBefore = collector.NotBefore.ToString("o"), latest = _paths.Latest },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.Out.Flush();
            ReleaseInstance();
            Shutdown(5);
            return;
        }
        var l = env.Snapshot?.Limits;
        object? L(QuotaLimit? x) => x is null ? null : new { x.UsedPercent, resetsAt = x.ResetsAt?.ToString("o"), x.WindowId, x.WindowMode };
        var summary = new
        {
            env.Status, env.ErrorCode, env.ProfileKey, env.PlanLabel, env.EffectivePollIntervalSeconds, env.RetryAfterSeconds,
            rateLimitsAvailable = _source?.LastRateLimitsAvailable,
            sessionCost = _source?.LastSessionCost, modelUsageCount = _source?.LastModelUsageCount,
            fetchMilliseconds = _source?.LastFetchMilliseconds, cliPeakWorkingSetBytes = _source?.LastPeakWorkingSetBytes,
            diagnosticClasses = _source?.DiagnosticClasses,
            fiveHour = L(l?.FiveHour), allWeek = L(l?.AllWeek), fableWeek = L(l?.FableWeek),
            latest = _paths.Latest,
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.Out.Flush();
        ReleaseInstance();
        Shutdown(Statuses.HasSnapshot(env.Status) ? 0 : 2);
    }

    void RunSnapshot()
    {
        if (!_opts.Demo && _model.Settings.CacheRemindersEnabled)
        {
            var monitor = ChatCacheMonitor.Local(); var now = DateTimeOffset.Now;
            _cacheEntries = monitor.Poll(now,Monitors(ChatPlatform.Claude),Monitors(ChatPlatform.Codex));
            _chatLifecycle = ChatLifecycleMonitor.Local().Poll(LifecycleCandidates(now), now);
            _cacheWarning = monitor.Warning ?? _chatLifecycle.Warning;
        }
        _window.Left = -20000;
        _window.Top = -20000;
        _window.ShowActivated = false;
        if (!_opts.Demo&&Monitors(ChatPlatform.Claude)) _startupChoice = ClaudeCli.Resolve(_model.Settings.ClaudeExePath); // read-only check, shown in settings
        Render();
        _window.Show();
        _window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            Render();
            if (_opts.InspectMinutesAgo is { } ago) _window.Inspect(TimeSpan.FromMinutes(ago));
            _window.UpdateLayout();
            _window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                try { _window.SaveSnapshot(_opts.Snapshot!, _opts.Scale); }
                catch (Exception ex) { File.WriteAllText(_opts.Snapshot + ".error.txt", ex.ToString()); }
                if (_tempRoot is not null) try { Directory.Delete(_tempRoot, recursive: true); } catch { }
                Shutdown(0);
            });
        });
    }

    ClaudeCliUsageSource? _source;
    CliChoice? _startupChoice;

    ClaudeCliUsageSource NewUsageSource(WidgetSettings settings) =>
        _source = new(() => settings.ClaudeExePath, Path.Combine(_paths.Root, "cli-work"), _opts.UsageDiagnostics);

    /// <summary>Visible: full window render. Hidden: only the tray text/icon (cached curves, no drawing).</summary>
    public void Render()
    {
        _codex.Settings.Display=_model.Settings.Display;
        if ((_source?.LastChoice ?? _startupChoice) is { } c) _model.CliSummary = c.Usable ? Loc.F($"{c.DisplaySummary} · 已校验签名") : c.DisplaySummary;
        // A never-shown --hidden window is not IsLoaded: do not mistake that for a reason
        // to build its chart. Background mode never loads the on-demand archive.
        var view = _window.IsVisible || _opts.Snapshot is not null ? _window.Render() : _model.BuildView(DateTimeOffset.Now, 60);
        _tray?.Update(view,_codex.BuildView(DateTimeOffset.Now,60),_model.Settings);
    }

    async Task WatchChatsAsync()
    {
        var monitor = ChatCacheMonitor.Local();
        var lifecycle = ChatLifecycleMonitor.Local();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_model.Settings.CacheRemindersEnabled && (Listens(ChatPlatform.Claude)||Listens(ChatPlatform.Codex)))
                {
                    try
                    {
                        var now = DateTimeOffset.Now;
                        var mode=_model.Settings.Monitoring;
                        _cacheEntries = monitor.Poll(now,Listens(ChatPlatform.Claude),Listens(ChatPlatform.Codex)); _cacheWarning = monitor.Warning;
                        try
                        {
                            _chatHistory.Capture(_cacheEntries, now);
                            _currentChatSession = _chatHistory.Current(now);
                            _recentCompacts = _chatHistory.RecentCompacted(now);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { _cacheWarning = "使用记录保存失败 · 稍后自动重试"; }
                        _chatLifecycle = lifecycle.Poll(LifecycleCandidates(now), now);
                        _cacheWarning ??= _chatLifecycle.Warning;
                    }
                    catch { _cacheWarning = "本地记录暂不可读"; }
                }
                else { _cacheEntries = Array.Empty<ChatCacheEntry>(); _cacheWarning = null; }
                await Dispatcher.InvokeAsync(() => { if (!_exiting && _window.IsVisible) _window.RenderCache(); });
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { try { _chatHistory.Flush(DateTimeOffset.Now); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    public string? CacheWarning => _cacheWarning is not { } warning ? null :
        Loc.T(warning) != warning ? Loc.T(warning) : string.Join(" · ",warning.Split(" · ").Select(s=>Loc.T(s)));
    public long TokenVersion => _tokens?.Version ?? 0;
    (long Version,string Monitoring,DateTimeOffset At,TrendActivity Value)? _trendActivity;
    public TrendActivity WorkTrendActivity(DateTimeOffset now)
    {
        if(!_model.Settings.TokenTrackingEnabled||_opts.Demo)return TrendActivity.Empty;
        var mode=_model.Settings.Monitoring;
        var version=TokenVersion;
        if(_trendActivity is { } cache&&cache.Version==version&&cache.Monitoring==mode&&now>=cache.At&&now-cache.At<TimeSpan.FromSeconds(5))return cache.Value;
        try
        {
            IReadOnlyList<WorkSpan> Read(string platform)=>_tokens?.WorkActivitySpans(now,platform)??_snapshotTokens?.WorkActivitySpans(now,platform)??[];
            var claude=Monitors(ChatPlatform.Claude)?Read("Claude"):[];
            var codex=Monitors(ChatPlatform.Codex)?Read("Codex"):[];
            var cadence=TimeSpan.Zero; // Collection backoff never turns real idle time into work.
            bool Ready(string platform)=>_tokens?.WorkActivityReady(platform)??(_snapshotTokens is not null);
            var value=new TrendActivity(WorkActivity.Merge(claude,cadence),
                WorkActivity.Merge(claude.Where(s=>s.Model is null||FableDisplay.IsFable(s.Model)),cadence),WorkActivity.Merge(codex,cadence),
                !Monitors(ChatPlatform.Claude)||Ready("Claude"),!Monitors(ChatPlatform.Codex)||Ready("Codex"))
                {FableRunning=WorkActivity.FableRunning(claude,now),ClaudeModels=claude};
            _trendActivity=(version,mode,now,value);return value;
        }
        catch{return _trendActivity is {} last?last.Value with{FableRunning=false}:new TrendActivity([],[],[],false,false);}
    }
    (DateTimeOffset Start,DateTimeOffset End,long Version,IReadOnlyList<ModelActivity> Rows)? _modelActivity;
    public IReadOnlyList<ModelActivity> ClaudeModelActivity(DateTimeOffset start,DateTimeOffset end)
    {
        if(!_model.Settings.TokenTrackingEnabled||!Monitors(ChatPlatform.Claude)||_tokenWarning is not null||_opts.Demo)return [];
        var a=DateTimeOffset.FromUnixTimeSeconds(start.ToUnixTimeSeconds()/60*60);
        var b=DateTimeOffset.FromUnixTimeSeconds(end.ToUnixTimeSeconds()/60*60);
        var version=TokenVersion;
        if(_modelActivity is { } cache&&cache.Start==a&&cache.End==b&&cache.Version==version)return cache.Rows;
        try
        {
            var rows=_tokens?.ClaudeModelActivity(a,end)??_snapshotTokens?.ModelActivity(a,end,"Claude")??[];
            _modelActivity=(a,b,version,rows);return rows;
        }
        catch{return [];}
    }
    public TokenSummary Tokens(DateTimeOffset start, DateTimeOffset end, ChatCacheEntry? chat = null, ChatPlatform? platform = null)
    {
        if (_opts.Demo)
        {
            var sourcePlatform=chat?.Platform??platform;
            var rows=(sourcePlatform is { } selected?new[]{selected}:new[]{ChatPlatform.Claude,ChatPlatform.Codex}).SelectMany(p=>DemoTokenRows(p,end))
                .Where(r=>chat is null||r.Chat==chat.Id).ToArray();
            return Core.TokenBreakdown.Build(start,end,rows).Total;
        }
        var source = (chat?.Platform ?? platform)?.ToString();
        try { return _tokens?.Sum(start,end,source,chat?.Id) ?? _snapshotTokens?.Sum(start,end,source,chat?.Id) ?? TokenSummary.Empty; }
        catch { return TokenSummary.Empty; }
    }
    public string TokenDetail => (_tokenWarning is { } warning ? Loc.T(warning) : null) ?? _tokens?.Coverage ?? _snapshotTokens?.Meta(Loc.IsEnglish?"coverage.en":"coverage") ?? (_opts.Demo ? Loc.T("演示 token 数值") : Loc.T("本机已记录日志；非账号账单。IN 为未命中输入（含缓存写入），CACHE 为命中输入，OUT 已含推理输出。首次补读可能尚未完成。"));
    public Task<ChatQuotaEstimate> EstimateChatQuota(ChatPlatform platform,SeriesData quota,DateTimeOffset start,DateTimeOffset end)
    {
        if(_opts.Demo)
        {
            // Gallery-only fixture, explicitly labelled. Never calibrate against or
            // import demo usage, and never represent these as validated real weights.
            var rows=DemoTokenRows(platform,end);var points=RateEngine.SumRange(quota,start,end).Delta;
            var weight=rows.Sum(r=>(double)r.Usage.Input+r.Usage.Cached*.1+r.Usage.Output*5);
            return Task.FromResult(new ChatQuotaEstimate(points,rows.Select(r=>new ChatQuotaShare(r.Chat,r.Model,
                weight>0?points*(r.Usage.Input+r.Usage.Cached*.1+r.Usage.Output*5)/weight:0,0)).ToArray(),""){Synthetic=true});
        }
        if(!_model.Settings.TokenTrackingEnabled)return Task.FromResult(ChatQuotaEstimate.Unknown("本机记录尚未完整"));
        return Task.Run(()=>
        {
            try
            {
                var rows=_tokens?.QuotaTokens(end.AddDays(-8),end,platform.ToString());
                return rows is null?ChatQuotaEstimate.Unknown("本机记录尚未完整"):
                    ChatQuotaEstimator.Build(quota,rows,start,end);
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {return ChatQuotaEstimate.Unknown("本地用量索引暂不可读");}
        });
    }
    public TokenBreakdown TokenBreakdown(ChatPlatform platform,DateTimeOffset start,DateTimeOffset end)
    {
        if(_opts.Demo)
        {
            var first=end.AddHours(-26);
            return Core.TokenBreakdown.Build(start<first?first:start,end,DemoTokenRows(platform,end));
        }
        try {return _tokens?.Breakdown(start,end,platform.ToString()) ?? _snapshotTokens?.Breakdown(start,end,platform.ToString()) ?? Core.TokenBreakdown.Build(start,end,[]);}
        catch {return Core.TokenBreakdown.Build(start,end,[]) with {Error=Loc.T("本地用量索引暂不可读")};}
    }
    TokenSlice[] DemoTokenRows(ChatPlatform platform,DateTimeOffset end)=>DemoCacheEntries(end).Where(e=>e.Platform==platform).Select((e,i)=>new TokenSlice(
        platform==ChatPlatform.Claude?(i%2==0?"claude-opus-5-5":"claude-fable-5-1"):(i%2==0?"gpt-6-astra":"gpt-6-luna"),e.Id,
        new TokenSummary(120000*(i+1),8600000*(i+1),43000*(i+1),0,20*(i+1),0))).ToArray();
    public IReadOnlyDictionary<string,ChatCacheEntry> TokenChatNames(ChatPlatform platform,DateTimeOffset start,DateTimeOffset end,IReadOnlySet<string>? ids=null)
    {
        var names=new Dictionary<string,ChatCacheEntry>(StringComparer.Ordinal);
        if(!_opts.Demo)
            foreach(var chat in _chatHistory.ForRange(start.LocalDateTime.Date,end.LocalDateTime.Date.AddDays(1)).SelectMany(s=>s.Chats).OrderBy(c=>ChatListPolicy.LastActivity(c.Last)))
                if(chat.Last.Platform==platform) names[chat.Last.Id]=chat.Last;
        if(!_opts.Demo&&platform==ChatPlatform.Codex)
        {
            // Metadata-only bounded index tail: older/sidechain titles need not be active cache reminders.
            var home=Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
            try {new MetadataTail().Read(Path.Combine(home,"session_index.jsonl"),line=>
            {
                try
                {
                    using var doc=System.Text.Json.JsonDocument.Parse(line);var item=doc.RootElement;
                    if(item.ValueKind!=System.Text.Json.JsonValueKind.Object) return;
                    if(item.TryGetProperty("id",out var id)&&id.ValueKind==System.Text.Json.JsonValueKind.String&&id.GetString() is { } key&&(ids is null||ids.Contains(key))
                        &&item.TryGetProperty("thread_name",out var title)&&title.ValueKind==System.Text.Json.JsonValueKind.String&&title.GetString() is {Length:>0} value)
                        names[key]=new ChatCacheEntry(platform,key,new string(value.Where(c=>!char.IsControl(c)).Take(120).ToArray()),end,30,Loc.T("本地标题索引"),false,names.GetValueOrDefault(key)?.Project);
                } catch(System.Text.Json.JsonException) {}
            },()=>{});} catch(Exception e) when(e is IOException or UnauthorizedAccessException) {}
        }
        foreach(var chat in CacheEntries(end).Where(c=>c.Platform==platform)) names[chat.Id]=chat;
        return names;
    }
    async Task WatchTokensAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_model.Settings.TokenTrackingEnabled && (Listens(ChatPlatform.Claude)||Listens(ChatPlatform.Codex)))
                {
                    var previousVersion=TokenVersion;
                    try
                    {
                        _tokens ??= TokenIndex.Local(Path.Combine(_paths.Root,"tokens"),DateTimeOffset.Now);
                        var mode=_model.Settings.Monitoring;
                        _tokens.Poll(DateTimeOffset.Now, _cts.Token,Listens(ChatPlatform.Claude),Listens(ChatPlatform.Codex)); _tokenWarning=null;
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested) { throw; }
                    catch { _tokenWarning="本地 token 索引暂不可读 · 稍后重试"; }
                    await Dispatcher.InvokeAsync(() => { if (!_exiting && _window.IsVisible) {
                        if(TokenVersion!=previousVersion&&!_model.Settings.CompactMode) Render();
                        else { _window.RenderTokens(); _window.RenderCache(); }
                    } });
                }
                await Task.Delay(TimeSpan.FromSeconds(5),_cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    IEnumerable<ChatCacheEntry> RetainedCacheEntries(DateTimeOffset now) =>
        (CurrentSession(now)?.Chats.Select(c => c.Last) ?? []).Concat(_recentCompacts);
    IEnumerable<ChatCacheEntry> LifecycleCandidates(DateTimeOffset now) => _cacheEntries.Concat(RetainedCacheEntries(now)).Where(e=>Listens(e.Platform));
    public IReadOnlyList<ChatCacheEntry> CacheEntries(DateTimeOffset now) => (_opts.Demo ? DemoCacheEntries(now)
        : ChatListPolicy.Merge(_cacheEntries, RetainedCacheEntries(now), now, _chatLifecycle)).Where(e=>_opts.Demo?Monitors(e.Platform):Listens(e.Platform)).ToArray();

    IReadOnlyList<ChatCacheEntry> DemoCacheEntries(DateTimeOffset now)
    {
        var entries = new List<ChatCacheEntry>
        {
        new(ChatPlatform.Codex, "demo-a", Loc.T("额度挂件"), now.AddMinutes(-6), 30, Loc.T("演示数据"), true, "QuotaWidget"),
        new(ChatPlatform.Claude, "demo-b", Loc.T("规则核对"), now.AddMinutes(-34), 60, Loc.T("演示 1h 缓存"), false, "Demo project"),
        new(ChatPlatform.Codex, "demo-c", Loc.T("界面审查"), now.AddMinutes(-26), 30, Loc.T("演示数据"), false, "QuotaWidget"),
        new(ChatPlatform.Claude, "demo-d", Loc.T("历史数据检查"), now.AddMinutes(-51), 60, Loc.T("演示 1h 缓存"), false, "Demo project"),
        new(ChatPlatform.Codex, "demo-e", Loc.T("已结束的记录"), now.AddMinutes(-83), 30, Loc.T("演示数据"), false),
        new(ChatPlatform.Claude, "demo-f", Loc.T("已经 compact 的对话"), now.AddMinutes(-95), 60, Loc.T("演示 compact 记录"), false, "QuotaWidget", CompactedAt: now.AddMinutes(-70), Compacted: true, ActivityAt: now.AddMinutes(-70)),
        };
        if (_opts.Scenario == "many-chats")
            for (var i = 0; i < 15; i++)
            {
                var compact = now.AddMinutes(-(i == 14 ? 1500 : 5 + i * 110));
                entries.Add(new(ChatPlatform.Claude, "review-" + i, Loc.T("回顾记录 ") + (i + 1), compact.AddMinutes(-5), 60,
                    Loc.T("离线演示"), false, "QuotaWidget", CompactedAt: compact, Compacted: true, ActivityAt: compact));
            }
        return ChatListPolicy.Merge(entries, [], now);
    }

    public ChatSession? CurrentSession(DateTimeOffset now) => _currentChatSession is { } s && now - s.End < ChatSessionHistory.BreakAfter ? s : null;
    public long SessionHistoryVersion => _chatHistory.Version;
    (DateTime First, DateTime Last, long Version, DateTimeOffset At, IReadOnlyList<DateTimeOffset> Edges)? _trendSessionEdges;
    public IReadOnlyList<DateTimeOffset> TrendSessionEdges(DateTimeOffset start, DateTimeOffset now)
    {
        if (_opts.Demo) return [];
        var first = start.ToLocalTime().Date;
        var last = now.ToLocalTime().Date.AddDays(1);
        if (_trendSessionEdges is not { } cache || cache.First != first || cache.Last != last ||
            now - cache.At >= TimeSpan.FromSeconds(cache.Version == _chatHistory.Version ? 60 : 10))
            _trendSessionEdges = (first, last, _chatHistory.Version, now,
                SessionTrendBoundaries.Build(_chatHistory.ForRange(first, last), now));
        return _trendSessionEdges.Value.Edges;
    }
    DateTime _sessionMonth;
    DateTimeOffset _sessionMonthAt;
    long _sessionMonthVersion=-1;
    IReadOnlyList<ChatSession> _monthSessions=[];
    public IReadOnlyList<ChatSession> SessionsForMonth(DateTime month)
    {
        month=new DateTime(month.Year,month.Month,1);
        if(_opts.Demo) return Enumerable.Range(0,DateTime.DaysInMonth(month.Year,month.Month)).Select(i=>month.AddDays(i))
            .Where(day=>day<=DateTime.Today && day.Day%3!=0).SelectMany(SessionsForDay).ToArray();
        var now=DateTimeOffset.Now;
        if(_sessionMonth!=month || (_sessionMonthVersion!=_chatHistory.Version && now-_sessionMonthAt>=TimeSpan.FromSeconds(10)))
        {
            _monthSessions=_chatHistory.ForRange(month,month.AddMonths(1));
            _sessionMonth=month;_sessionMonthVersion=_chatHistory.Version;_sessionMonthAt=now;
        }
        return _monthSessions;
    }
    public IReadOnlyList<ChatSession> SessionsForDay(DateTime day)
    {
        if (!_opts.Demo) return _chatHistory.ForDay(day);
        var now = DateTimeOffset.Now;
        var date = new DateTimeOffset(day.Date, TimeZoneInfo.Local.GetUtcOffset(day.Date));
        var chats = CacheEntries(now).Take(4).Select((e, i) => new SessionChat { Last = e with { RequestAt = date.AddHours(i + 1),
            ActivityAt = date.AddHours(i + 2), Running = false, Compacted = i == 1, CompactedAt = i == 1 ? date.AddHours(i + 2) : null },
            FirstAt = date.AddHours(i), Compactions = i == 1 ? [date.AddHours(i + 2)] : [] }).ToList();
        return [new ChatSession { Id = "demo-night-"+day.ToString("yyyyMMdd"), Start = date.AddHours(-1), End = date.AddHours(5), Recovered = true, Chats = chats }];
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // The only evidence that may label a gap as "休眠".
        var type = e.Mode switch { PowerModes.Suspend => EventTypes.Suspend, PowerModes.Resume => EventTypes.Resume, _ => null };
        if (type is null) return;
        var at = DateTimeOffset.Now;
        Dispatcher.BeginInvoke(() =>
        {
            _model.RecordEvent(new AppEvent(at, type));
            _codex.RecordEvent(new AppEvent(at, type));
            if (type == EventTypes.Resume) Render();
        });
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_themeForced || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        Dispatcher.BeginInvoke(() =>
        {
            var dark = Theme.SystemPrefersDark();
            if (dark == Theme.IsDark) return;
            Theme.Apply(dark);
            _window.OnThemeChanged();
            Render();
        });
    }

    public void ShowWindow()
    {
        if (_exiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        Render();
    }

    public void ToggleWindow()
    {
        if (_window.IsVisible) _window.Hide();
        else ShowWindow();
    }

    public void ToggleTopmost() => _window.SetTopmost(!_model.Settings.Topmost);
    public void OpenSettings() { ShowWindow();_window.OpenSettings(); }

    public void TriggerCollect()
    {
        if (Monitors(ChatPlatform.Codex)&&_codexCollector is not null) _ = Task.Run(() => _codexCollector.CollectOnceAsync(_cts.Token));
        if (!Monitors(ChatPlatform.Claude)||_collector is null) return;
        if (_collector.LastStatus == Statuses.AuthRequired || _collector.TriggerNow()) return;
        _window.Flash(Loc.T("刚采集过或处于限流期，稍后再试"));
    }

    public void LaunchLogin() => RunAuth(_model.Settings, logout: false, confirm: false);

    public void MonitoringChanged(string previous)
    {
        var s=_model.Settings;
        var before=new WidgetSettings{Monitoring=previous,SetupCompleted=s.SetupCompleted,ClaudeConnected=s.ClaudeConnected,CodexConnected=s.CodexConnected};var now=DateTimeOffset.Now;
        foreach(var (platform,model) in new[]{(ChatPlatform.Claude,_model),(ChatPlatform.Codex,_codex)})
            if(before.Listens(platform)!=Listens(platform))
            {
                model.RecordEvent(new AppEvent(now,Listens(platform)?EventTypes.MonitorResume:EventTypes.MonitorPause));
                model.ReleaseArchive();
            }
    }

    public void LaunchLogout() => RunAuth(_model.Settings, logout: true, confirm: true);

    /// <summary>
    /// The one auth path for the tray, the note bar and 登录Claude.cmd (--login): the same
    /// signed + compatible CLI the collector uses, a private config dir, a clean environment.
    /// </summary>
    string? RunAuth(WidgetSettings settings, bool logout, bool confirm)
    {
        if (confirm && MessageBox.Show(Loc.T("退出额度小挂件专用的 Claude 登录？\n官方 CLI 会删除本机凭证，之后需要重新登录才能采集。\n服务端是否撤销令牌尚未验证。"),
                Loc.T("额度"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return "cancelled";
        var choice = ClaudeCli.Resolve(settings.ClaudeExePath);
        var error = ClaudeCli.LaunchAuthConsole(_paths, choice, settings.ResolveClaudeConfigDir(_paths), logout);
        if (error is not null)
        {
            var hint = choice.Problem switch
            {
                "cli_missing" => Loc.T("\n请安装 Claude Code，或在 settings.json 的 claudeExePath 填写路径。"),
                "cli_untrusted" => Loc.T("\n这个 claude.exe 没有有效的 Anthropic 数字签名，已拒绝运行。"),
                "cli_incompatible" => Loc.T("\n本机可用的 claude.exe 版本太旧，缺少额度接口所需的功能。请更新 Claude Code 或 Claude 桌面版。"),
                _ => "",
            };
            MessageBox.Show(error + hint, Loc.T("额度"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return error;
    }

    public void OpenDataFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.Root}\"") { UseShellExecute = true });

    public void ExportCsv()
    {
        var model=Monitors(ChatPlatform.Claude)?_model:_codex;
        if (model.Records.Count == 0)
        {
            _window.Flash(Loc.T("还没有可导出的历史"));
            return;
        }
        Directory.CreateDirectory(_paths.ExportDir);
        var file = Path.Combine(_paths.ExportDir, $"quota-{model.ProfileKey}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        File.WriteAllText(file, HistoryStore.ToCsv(model.Records), new UTF8Encoding(true));
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }

    void RecordExit()
    {
        if (_exiting) return;
        _exiting = true;
        try { _model.RecordEvent(new AppEvent(DateTimeOffset.Now, EventTypes.AppExit)); } catch { }
        try { _codex.RecordEvent(new AppEvent(DateTimeOffset.Now, EventTypes.AppExit)); } catch { }
        try { _window.PersistPlacement(); } catch { }
    }

    public async void ExitApp()
    {
        if (_exiting) return;
        RecordExit();
        _fileTimer?.Stop();
        _clockTimer?.Stop();
        _cts.Cancel();
        _window.Hide();
        try { await Task.Run(StopWorkersAndLogoutAsync); } catch { /* a failed status-file write must not trap app exit */ }
        if (_collectorTask?.IsCompleted != false) _collector?.Dispose();
        if (_codexTask?.IsCompleted != false) _codexCollector?.Dispose();
        _tray?.Dispose();
        _tray = null;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _showSignal?.Set();
        _exitSignal?.Set();
        ReleaseInstance();
        Shutdown(0);
    }

    async Task StopWorkersAndLogoutAsync()
    {
        _cts.Cancel();
        var stopped = true;
        try { if (_collectorTask is not null) await _collectorTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { stopped = _collectorTask?.IsCompleted != false; }
        try { if (_codexTask is not null) await _codexTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        // The journal's final atomic write must finish before the process shuts down.
        try { if (_cacheTask is not null) await _cacheTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        try { if (_tokenTask is not null) await _tokenTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        if (_tokenTask?.IsCompleted != false) { _tokens?.Dispose(); _tokens=null; }
        if (_opts.Demo || !_model.Settings.AutoLogoutOnExit) return;
        var resultPath = Path.Combine(_paths.Root, "logout-result.txt");
        // Write intent first so interruption/crash does not silently look like a successful logout.
        AtomicFile.WriteAllText(resultPath, Loc.T("退出登录尚未完成"));
        string? error = Loc.T("采集器未停止，未并发执行退出登录");
        if (stopped)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                var config = _model.Settings.ResolveClaudeConfigDir(_paths);
                if (ClaudeConfigFiles.CredentialStamp(config) is null) error = null;
                else
                {
                    var choice = await Task.Run(() => ClaudeCli.Resolve(_model.Settings.ClaudeExePath), timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                    error = await ClaudeCli.LogoutQuietlyAsync(choice, config, _paths.Root, timeout.Token).ConfigureAwait(false);
                }
            }
            catch { error = Loc.T("退出登录未完成，本机凭证可能仍在"); }
        }
        AtomicFile.WriteAllText(resultPath, error ?? "");
    }

    void ReleaseInstance()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
