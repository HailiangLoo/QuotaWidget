using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public partial class MainWindow : Window
{
    const double ShadowMargin = 12;
    readonly App _app;
    readonly WidgetModel _model;
    readonly WidgetModel _codex;
    readonly AppOptions _opts;
    readonly DispatcherTimer _saveTimer;
    bool _suppress = true;
    NoteAction _noteAction;
    string? _flash;
    DateTimeOffset _flashUntil;
    readonly DateTimeOffset _demoSessionStart = DateTimeOffset.Now.AddHours(-3);
    DateTime _historyDay=DateTime.Today;
    DateTime _historyMonth=new(DateTime.Today.Year,DateTime.Today.Month,1);

    public MainWindow(App app, WidgetModel model, WidgetModel codex, AppOptions opts)
    {
        _app = app;
        _model = model;
        _codex = codex;
        _opts = opts;
        Loc.Configure(opts.Language ?? model.Settings.Language);
        Translate.RefreshResources();
        InitializeComponent();
        WindowDrag.Attach(this,Frame,moved:PersistPlacement);
        InitializeConnections();
        Chart.AttachInspectOverlay(ChartInspection);
        Chart.SizeChanged += (_,_) => PlaceChartButtons();
        if (opts.QaWindow) ShowInTaskbar = true; // permits native UI inspection of this otherwise tool-only window
        var s = model.Settings;

        Width = s.Width + 2 * ShadowMargin;
        Topmost = s.Topmost;
        PinButton.IsChecked = s.Topmost;
        if (opts.HistoryOpen) s.CompactMode = false;
        ApplyCompactLayout();
        PlaceInitially();

        DisplayCombo.SelectedIndex = s.Display == "remaining" ? 1 : 0;
        LanguageCombo.SelectedIndex = Array.IndexOf(new[]{"auto","zh-CN","en"},s.Language);
        MonitoringCombo.SelectedIndex=Array.IndexOf(new[]{"both","claude","codex"},s.Monitoring);
        var poll = Array.IndexOf(WidgetSettings.PollChoices, s.PollIntervalSeconds);
        if (poll < 0)
        {
            IntervalCombo.Items.Add(new ComboBoxItem { Tag = s.PollIntervalSeconds.ToString(CultureInfo.InvariantCulture), Content = Loc.F($"{s.PollIntervalSeconds / 60.0:0.#} 分钟") });
            poll = IntervalCombo.Items.Count - 1;
        }
        IntervalCombo.SelectedIndex = poll;
        SmoothCheck.IsChecked = s.Smoothing;
        TrendCombo.SelectedIndex = Array.IndexOf(new[] {60,90,120,150}, s.TrendMinutes);
        TokenCheck.IsChecked = s.TokenTrackingEnabled;
        CacheCheck.IsChecked = s.CacheRemindersEnabled;
        AutoLogoutCheck.IsChecked = s.AutoLogoutOnExit;
        if(opts.HistoryDay is { } historyDay) { _historyDay=historyDay.Date; _historyMonth=new(historyDay.Year,historyDay.Month,1); }
        ExpiredChats.IsExpanded = opts.ExpandArchive;
        if (opts.SettingsOpen || !s.SetupCompleted) ShowSettings(true);
        if (opts.HistoryOpen) ShowHistory(true);
        OnThemeChanged();
        _suppress = false;

        _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { _saveTimer!.Stop(); PersistPlacement(); }, Dispatcher);
        _saveTimer.Stop();
        InitializeUsageHover();
        Loaded+=(_,_)=>{if(SettingsPanel.Visibility==Visibility.Visible){_ = _app.CheckConnection(ChatPlatform.Claude);_ = _app.CheckConnection(ChatPlatform.Codex);}};
        IsVisibleChanged += (_, _) => { if (!IsVisible) ReleaseChart(); };
        LocationChanged += (_, _) => { if (!_suppress && opts.Snapshot is null) { _saveTimer.Stop(); _saveTimer.Start(); } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && SettingsPanel.Visibility == Visibility.Visible)
            {
                ShowSettings(false);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && HistoryPanel.Visibility == Visibility.Visible)
            { ShowHistory(false); e.Handled = true; }
        };
    }

    void PlaceInitially()
    {
        if (_opts.Snapshot is not null) return;
        // Position in physical pixels once the HWND exists, before the first show.
        SourceInitialized += (_, _) =>
        {
            var s = _model.Settings;
            var hwnd = new WindowInteropHelper(this).Handle;
            var (x, y) = s.WindowX is { } sx && s.WindowY is { } sy && NativePlacement.IsOnScreen(sx, sy)
                ? (sx, sy)
                : NativePlacement.DefaultTopRight(Width, ShadowMargin);
            _suppress = true;
            NativePlacement.Move(hwnd, x, y);
            _suppress = false;
        };
    }

    void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        _model.Settings.Language = language;
        _model.SaveSettings();
        Loc.Configure(language);
        Translate.RefreshResources();
        CloseUsage();
        CacheDiagnosticsText.Text = _app.CacheWarning ?? Loc.T("状态读取正常");
        _flash = null;
        _cacheSignature = _compactChatSignature = _historySignature = null;
        ApplyCompactLayout();
        foreach (var (platform, window) in _usageWindows)
        {
            window.Title = platform + Loc.T(" 用量 · 已固定");
            window.ReplaceCard(CreateUsageCard(platform,window.Card.Minutes));
        }
        Render();
        _app.Render();
        Chart.RefreshLanguage();
        UpdateLayout();
        if (_opts.Snapshot is null && IsVisible)
            NativePlacement.FitToWorkArea(new WindowInteropHelper(this).Handle);
    }

    public void PersistPlacement()
    {
        if (_opts.Snapshot is not null || !IsLoaded) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || NativePlacement.Position(hwnd) is not { } p) return;
        var s = _model.Settings;
        s.WindowX = p.X;
        s.WindowY = p.Y;
        _model.SaveSettings();
    }

    public WidgetView Render()
    {
        RenderConnections();
        var now = DateTimeOffset.Now;
        var s = _model.Settings;
        ApplyPlatformLayout();
        foreach(var meter in new[]{FiveMeter,WeekMeter,FableMeter})meter.Opacity=s.ClaudeConnected?1:.45;
        foreach(var meter in new[]{CodexFiveMeter,CodexMeter,CodexInlineMeter})meter.Opacity=s.CodexConnected?1:.45;
        if (s.CompactMode) return RenderCompact(now);
        RenderSessionStart(now);
        if (HistoryPanel.Visibility == Visibility.Visible)
        { RenderHistory(); return _model.BuildView(now, 60); }
        var watchClaude=s.Monitors(ChatPlatform.Claude);var watchCodex=s.Monitors(ChatPlatform.Codex);
        var available = AvailableRanges();
        var range = ChartRanges.Select(s.RangeMinutes, available);
        var v = _model.BuildView(now, watchClaude?range:60);
        _codex.Settings.RangeMinutes = s.RangeMinutes;
        _codex.Settings.Smoothing = s.Smoothing;
        _codex.Settings.TrendMinutes = s.TrendMinutes;
        _codex.Settings.Display = s.Display;
        _codex.Settings.PollIntervalSeconds = s.PollIntervalSeconds;
        var cv = _codex.BuildView(now, watchCodex?range:60);
        var historyStart = v.Chart.EstimationStart < cv.Chart.EstimationStart ? v.Chart.EstimationStart : cv.Chart.EstimationStart;
        var dashboard = Dashboard.Combine(v, cv, s, now, _codex.LastEnvelope, range,
            _app.TrendSessionEdges(historyStart, now), _app.WorkTrendActivity(now));
        if(watchClaude&&dashboard.Chart.FableToClaudeFactor is not null)
        {
            var chart=dashboard.Chart;
            var window=s.Smoothing?s.TrendMinutes:10;
            chart.FableOnlySpans=FableDisplay.Build(chart.Total,chart.Fable,
                _app.ClaudeModelActivity(chart.EstimationStart.AddMinutes(-window/2d),now),chart.EstimationStart,chart.End,window,chart.ClaudeCumulativeMode,chart.Activity.ClaudeModels);
        }
        _suppress = true;
        TagText.Text = v.Tag ?? "";
        TagText.Visibility = v.Tag is null||_model.DemoMode ? Visibility.Collapsed : Visibility.Visible;
        DisplayLabelText.Text = _model.DemoMode?Loc.T("演示 %"):s.Display == "used" ? Loc.T("已用 %") : Loc.T("剩余 %");
        DisplayLabelText.ToolTip = string.Join(" · ",new[]{watchClaude?v.PlanLabel:null,watchCodex?Loc.T(cv.PlanLabel):null}.Where(p=>p is not null));
        FiveMeter.Show(v.Five);
        WeekMeter.Show(v.Week);
        FableMeter.Show(v.Fable);
        CodexFiveMeter.Show(cv.Five, dashboard.CodexStatus);
        CodexMeter.Show(cv.Week, dashboard.CodexStatus);

        var flashing = _flash is not null && _flashUntil > now;
        var note = flashing ? _flash : DisconnectedNote() ?? (watchClaude?v.Note:null) ?? (watchCodex?dashboard.CodexStatus:null);
        _noteAction = flashing || !watchClaude || DisconnectedNote() is not null ? NoteAction.None : v.NoteAction;
        NoteBar.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        NoteText.Text = note ?? "";
        NoteBar.Cursor = _noteAction == NoteAction.None ? null : Cursors.Hand;
        NoteBar.ToolTip = _noteAction switch
        {
            NoteAction.Login => Loc.T("登录 Claude"),
            NoteAction.Retry => Loc.T("点击立即重试"),
            _ => null,
        };

        TotalLegend.IsChecked = s.TotalVisible;
        FableLegend.IsChecked = s.FableVisible;
        CodexLegend.IsChecked = s.CodexVisible;
        RateMode.IsChecked = s.ChartModeFor(ChatPlatform.Claude) == "rate";
        CumulativeMode.IsChecked = s.ChartModeFor(ChatPlatform.Claude) == "cumulative";
        CodexRateMode.IsChecked = s.ChartModeFor(ChatPlatform.Codex) == "rate";
        CodexCumulativeMode.IsChecked = s.ChartModeFor(ChatPlatform.Codex) == "cumulative";
        FableLegendText.Text = dashboard.Chart.FableToClaudeFactor is not null ? v.FableLegend : v.FableLegend + Loc.T(" 自身");
        foreach (var tab in RangeTabs.Children.OfType<ToggleButton>())
        {
            var minutes = int.Parse((string)tab.Tag, CultureInfo.InvariantCulture);
            tab.Visibility = available.Contains(minutes) ? Visibility.Visible : Visibility.Collapsed;
            tab.IsChecked = minutes == range;
        }
        RangeTabs.Columns = available.Length;

        Chart.View = dashboard.Chart;
        PlaceChartButtons();
        RenderTokens();
        // The chart already draws a point-readout card; never cover it with a second tooltip.
        Chart.ToolTip = null;
        LegendBar.ToolTip = null;
        ChartDiagnosticsText.Text = dashboard.Detail+(dashboard.Chart.MergesFable?Loc.T("\n仅 Fable 片段在速率图中共线；累计图仅在整个所选记录范围都确认只用 Fable 时共线，混合历史保留两条完整曲线。顶部累计和原始记录不变，本机日志不覆盖其他设备。"):"");
        CumulativeMode.ToolTip = Loc.T("所选时段累计 · 点");
        RateMode.ToolTip = Loc.T("消耗速率 · 点/h");
        CollectorText.Text = _model.DemoMode ? Loc.T("演示数据") : string.Join("\n",new[]{watchClaude?"Claude · "+v.CollectorLine.Split('\n')[0]:null,watchCodex?dashboard.CodexStatus??Loc.T("Codex · 采集正常"):null}.Where(p=>p is not null));
        CollectorDiagnosticsText.Text="Claude · "+v.CollectorLine+"\n"+(dashboard.CodexStatus??Loc.T("Codex · 采集正常"));
        Title = watchClaude?v.TrayText:"Codex · "+cv.Week.Value;
        RenderCache();
        _suppress = false;
        return v;
    }

    public void ReleaseChart()
    {
        Chart.View = null;
        _model.ReleaseArchive();
        _codex.ReleaseArchive();
    }

    WidgetView RenderCompact(DateTimeOffset now)
    {
        RenderSessionStart(now);
        // Use recent observations already in memory; no archive, trend geometry or new collector.
        var s = _model.Settings;
        var view = _model.BuildView(now, RecentUsageRate.WindowMinutes);
        _codex.Settings.Display = s.Display;
        var codex = _codex.BuildView(now, RecentUsageRate.WindowMinutes);
        var codexStatus = Dashboard.CodexStatus(codex, _codex.LastEnvelope);
        FiveMeter.Show(view.Five, view.Note);
        WeekMeter.Show(view.Week, view.Note);
        FableMeter.Show(view.Fable, view.Note);
        CodexFiveMeter.Show(codex.Five, codexStatus);
        CodexMeter.Show(codex.Week, codexStatus);
        CodexInlineMeter.Show(codex.Week,codexStatus);
        DisplayLabelText.Text = s.Display == "used" ? Loc.T("已用 %") : Loc.T("剩余 %");
        if(_model.DemoMode) DisplayLabelText.Text=Loc.T("演示 %");
        DisplayLabelText.ToolTip = string.Join(" · ",new[]{s.Monitors(ChatPlatform.Claude)?view.PlanLabel:null,s.Monitors(ChatPlatform.Codex)?Loc.T(codex.PlanLabel):null}.Where(p=>p is not null));
        var note = _flashUntil > now && _flash is not null ? _flash : DisconnectedNote() ?? (s.Monitors(ChatPlatform.Claude)?view.Note:null) ?? (s.Monitors(ChatPlatform.Codex)?codexStatus:null);
        CompactStatus.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        CompactStatus.ToolTip = note is null ? null : note + Loc.T("\n点击展开查看");
        System.Windows.Automation.AutomationProperties.SetName(CompactStatus, note ?? Loc.T("采集正常"));
        RenderCompactRate(CompactClaudeRate,CompactClaudeRateValue,CompactClaudePoints,"Claude",view.Chart.Total,_model,now);
        RenderCompactFable(view,now);
        RenderCompactRate(CompactCodexRate,CompactCodexRateValue,CompactCodexPoints,"Codex",codex.Chart.Total,_codex,now);
        if(CodexCompactSummary.Visibility==Visibility.Visible)
            RenderCompactRate(CodexInlineUsage,CodexInlineRateValue,CodexInlinePoints,"Codex",codex.Chart.Total,_codex,now);
        CompactRates.ToolTip=null;
        RenderCompactChats(now);
        Title = s.Monitors(ChatPlatform.Claude)?view.TrayText:"Codex · "+codex.Week.Value;
        return view;
    }

    void RenderCompactRate(FrameworkElement host,TextBlock text,TextBlock points,string platform,SeriesData series,WidgetModel model,DateTimeOffset now)
    {
        var rate=RecentUsageRate.Build(series,now,model.LastEnvelope?.EffectivePollIntervalSeconds??model.Settings.PollIntervalSeconds);
        text.Text=rate.Rate is { } number ? RatePresentation.Estimate(number)+(rate.Partial?"*":"") : "—";
        points.Text=Loc.T("近1h · ")+(rate.CoverageMinutes>0?rate.Points.ToString("0.#",CultureInfo.InvariantCulture)+Loc.T("点")+(rate.Partial?"*":""):"—");
        host.ToolTip=Loc.F($"{platform} · 近 1h\n")+(rate.Rate is { } r?Loc.F($"{RatePresentation.Estimate(r)} 点/h · {rate.Points:0.##} 点 / {rate.CoverageMinutes:0}m"):Loc.T("等待约一小时的连续采样"))
            +(rate.LastObserved is { } at?Loc.F($"\n截至 {at.ToLocalTime():HH:mm}"):"")+(rate.Partial?Loc.T(" · 记录不全"):"");
        System.Windows.Automation.AutomationProperties.SetName(host,host.ToolTip.ToString());
    }

    void RenderCompactFable(WidgetView view,DateTimeOffset now)
    {
        var activity=_app.WorkTrendActivity(now);
        var factor=QuotaUnits.FableToClaude(view.PlanLabel);
        var source=factor is {} scale?QuotaUnits.Scale(view.Chart.Fable,scale):view.Chart.Fable;
        var rate=RecentUsageRate.Build(source,now,_model.LastEnvelope?.EffectivePollIntervalSeconds??_model.Settings.PollIntervalSeconds);
        var visible=_model.Settings.Listens(ChatPlatform.Claude)&&activity.ClaudeReady&&activity.FableRunning&&rate.Rate is >=.05&&!rate.Partial;
        CompactFableRate.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
        CompactFableRateValue.Text=visible?RatePresentation.Estimate(rate.Rate!.Value):"";
        var note=Loc.T("Fable · 近1h均速；确认工作结束后隐藏，历史消耗保留。")+"\n"+
            (factor is not null?Loc.T("已折合 Claude 周额度点/h"):Loc.T("Fable 自身周额度点/h"));
        CompactFableRate.ToolTip=note;
        System.Windows.Automation.AutomationProperties.SetName(CompactFableRate,note);
        CompactClaudeRateColumn.Width=new GridLength(_model.Settings.Monitors(ChatPlatform.Claude)?visible?2.1:1:0,GridUnitType.Star);
    }

    string? _compactChatSignature;
    void RenderCompactChats(DateTimeOffset now)
    {
        CompactChats.Visibility=_model.Settings.CacheRemindersEnabled?Visibility.Visible:Visibility.Collapsed;
        if(!_model.Settings.CacheRemindersEnabled) return;
        var active=_app.CacheEntries(now).Where(e=>!e.Compacted&&!e.Expired(now)&&!e.ActivityUncertain)
            .OrderByDescending(e=>e.Urgency(now)==2?2:e.Running?1:0)
            .ThenByDescending(e=>e.Running?0:e.AgeMinutes(now)/Math.Max(1,e.WindowMinutes)).ToArray();
        var signature=Theme.IsDark+"|"+_app.CacheWarning+"|"+string.Join(";",active.Select(e=>$"{e.Platform}:{e.Id}:{e.Title}:{e.Project}:{e.Running}:{Math.Floor(e.AgeMinutes(now))}:{e.WindowMinutes}"));
        if(signature==_compactChatSignature) return;
        _compactChatSignature=signature;
        CompactChatRows.Children.Clear();
        foreach(var entry in active) CompactChatRows.Children.Add(CacheRow(entry,now,dense:true));
        CompactChatsEmpty.Visibility=active.Length==0?Visibility.Visible:Visibility.Collapsed;
        CompactChatsEmpty.Text=_app.CacheWarning is null?Loc.T("暂无计时 chat"):Loc.T("chat 记录暂不可用");
        CompactChatsEmpty.ToolTip=_app.CacheWarning is null?null:Loc.T("部分 chat 状态暂不可读");
        if(_opts.Snapshot is null&&IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>NativePlacement.FitToWorkArea(new WindowInteropHelper(this).Handle));
    }

    void ApplyCompactLayout()
    {
        var compact = _model.Settings.CompactMode;
        Width = (compact && SettingsPanel.Visibility!=Visibility.Visible ? 240 : _model.Settings.Width) + 2 * ShadowMargin;
        CompactButton.IsChecked = compact;
        CompactButton.ToolTip = compact ? Loc.T("展开") : Loc.T("精简模式");
        System.Windows.Automation.AutomationProperties.SetName(CompactButton, compact ? Loc.T("展开完整模式") : Loc.T("切换精简模式"));
        CompactGlyph.Data = Geometry.Parse(compact ? "M4,4 H20 V20 H4 Z M4,10 H20 M9,17 L12,14 L15,17" : "M4,4 H20 V20 H4 Z M4,10 H20 M9,14 L12,17 L15,14");
        HistoryButton.Visibility = WidthGrip.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility=Visibility.Visible;
        SessionStartText.Visibility = Visibility.Visible;
        ChartPanel.Visibility = CachePanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactDetails.Visibility=compact?Visibility.Visible:Visibility.Collapsed;
        TagText.Visibility = compact || !_model.DemoMode ? Visibility.Collapsed : Visibility.Visible;
        TitleBar.Padding = compact ? new Thickness(8,2,6,2) : new Thickness(9,3,9,3);
        foreach (var button in new ButtonBase[] { CompactButton, PinButton, SettingsButton, HideButton })
        {
            if (compact) { button.Width = 22; button.Height = 22; }
            else { button.Width = 24; button.Height = 26; }
        }
        MainPanel.Margin = compact ? new Thickness(4,0,4,4) : new Thickness(9,0,9,8);
        MeterGrid.Margin = compact ? new Thickness(0,3,0,0) : new Thickness(-3,8,-3,0);
        foreach (var meter in new[] { FiveMeter, WeekMeter, FableMeter, CodexFiveMeter, CodexMeter }) meter.Compact = compact;
        ApplyPlatformLayout();
        NoteBar.Visibility = CompactStatus.Visibility = Visibility.Collapsed;
        if (compact) ReleaseChart();
    }

    void SetCompactMode(bool compact)
    {
        CloseUsage();
        _model.Settings.CompactMode = compact;
        HistoryPanel.Visibility = SettingsPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
        HistoryButton.Background = SettingsButton.Background = Brushes.Transparent;
        ApplyCompactLayout();
        _model.SaveSettings();
        _app.Render();
        if (_opts.Snapshot is null)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                NativePlacement.FitToWorkArea(new WindowInteropHelper(this).Handle);
                PersistPlacement();
            });
    }

    void CompactButton_Click(object sender, RoutedEventArgs e) => SetCompactMode(!_model.Settings.CompactMode);
    void CompactStatus_Click(object sender, RoutedEventArgs e) => SetCompactMode(false);

    string? _cacheSignature;
    readonly Dictionary<string, long> _chatOrder = new();
    long _chatSequence;
    public void RenderCache()
    {
        RenderSessionStart(DateTimeOffset.Now);
        if (_model.Settings.CompactMode) { CachePanel.Visibility = Visibility.Collapsed; RenderCompactChats(DateTimeOffset.Now); return; }
        if (HistoryPanel.Visibility == Visibility.Visible) { RenderHistory(); return; }
        CachePanel.Visibility = _model.Settings.CacheRemindersEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!_model.Settings.CacheRemindersEnabled) return;
        var now = DateTimeOffset.Now;
        var entries = _app.CacheEntries(now);
        string Key(ChatCacheEntry e) => e.Platform + ":" + e.Id;
        var keys = entries.Select(Key).ToHashSet();
        foreach (var key in _chatOrder.Keys.Where(k => !keys.Contains(k)).ToArray()) _chatOrder.Remove(key);
        foreach (var entry in entries.OrderByDescending(e => e.AgeMinutes(now) / e.WindowMinutes))
            if (!_chatOrder.ContainsKey(Key(entry))) _chatOrder[Key(entry)] = _chatSequence++;
        var active = entries.Where(e => !e.Compacted && !e.Expired(now) && !e.ActivityUncertain).OrderBy(e => _chatOrder[Key(e)]).ToList();
        var expired = entries.Where(e => e.Compacted || e.Expired(now) || e.ActivityUncertain)
            .OrderByDescending(e => e.Compacted ? e.CompactedAt ?? e.RequestAt : e.ActivityAt ?? e.RequestAt).ToList();
        // Usually only the minute changes: avoid replacing controls every polling tick.
        var signature = Theme.IsDark + "|" + _app.CacheWarning + "|" + string.Join("|", entries.Select(e => $"{e.Platform}:{e.Id}:{e.Title}:{e.Project}:{Math.Floor(e.AgeMinutes(now))}:{Math.Floor((now - (e.CompactedAt ?? now)).TotalMinutes)}:{e.WindowMinutes}:{e.Running}:{e.ActivityUncertain}:{e.Compacted}:{e.CompactedAt}:{e.Basis}"));
        if (signature == _cacheSignature) return;
        _cacheSignature = signature;
        CachePanel.ToolTip = _app.CacheWarning is null?null:Loc.T("部分 chat 状态暂不可读");
        CacheEmpty.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CacheRows.Children.Clear(); ExpiredRows.Children.Clear();
        foreach (var entry in active) CacheRows.Children.Add(CacheRow(entry, now));
        foreach (var entry in expired) ExpiredRows.Children.Add(CacheRow(entry, now));
        ExpiredChats.Visibility = expired.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExpiredChats.Header = Loc.F($"已收起 · {expired.Count}");
        ExpiredChats.ToolTip = Loc.T("compact / 超时 / 状态未确认");
    }

    FrameworkElement CacheRow(ChatCacheEntry e, DateTimeOffset now, bool historical = false,bool dense=false)
    {
        var age = e.AgeMinutes(now);
        var urgency = e.Urgency(now);
        var fraction = Math.Clamp(age / e.WindowMinutes, 0, 1);
        const double warningAt = 2.0 / 3;
        var from = ((SolidColorBrush)Theme.Brush(fraction < warningAt ? "Green" : "OrangeRed")).Color;
        var to = ((SolidColorBrush)Theme.Brush(fraction < warningAt ? "OrangeRed" : "Red")).Color;
        var t = fraction < warningAt ? fraction / warningAt : (fraction - warningAt) / (1 - warningAt);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        Brush color = e.Expired(now) ? Theme.Brush("Muted") : new SolidColorBrush(Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B)));
        if (e.WorkPending) color = Theme.Brush(e.Running ? "Green" : "Muted");
        if (e.Compacted || historical) color = Theme.Brush("Muted");
        var row = new Grid { Height = dense?22:30, Background = Brushes.Transparent, ToolTip = new ToolTip { Content = ChatTip(e, now, historical) } };
        // Refresh on hover; token polling must not replace every row and close an open card.
        row.ToolTipOpening += (_, _) => ((ToolTip)row.ToolTip).Content = ChatTip(e, DateTimeOffset.Now, historical);
        ToolTipService.SetInitialShowDelay(row,250);
        ToolTipService.SetShowDuration(row,30000);
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(dense?20:24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new Border { Background = color, Width = 2, Height = 12, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left });
        var platform = new Image { Source = ProviderIcon.Get(e.Platform.ToString()), Width = dense?16:17, Height = dense?16:17, VerticalAlignment = VerticalAlignment.Center, ToolTip = e.Platform.ToString() };
        Grid.SetColumn(platform, 1); row.Children.Add(platform);
        var title = new TextBlock { Text = e.Title, FontSize = 11, Foreground = Theme.Brush(urgency == 3 ? "Muted" : "Ink"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2,0,5,0) };
        var names = new DockPanel { VerticalAlignment = VerticalAlignment.Center, LastChildFill = dense, Margin = new Thickness(0,0,dense?6:18,0) };
        DockPanel.SetDock(title, Dock.Left);
        names.Children.Add(title);
        if (!dense && e.Project is { Length: > 0 } project)
        {
            var projectText = new TextBlock { Text = project, FontSize = 9, Foreground = Theme.Brush("Muted"), MaxWidth = 65,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(projectText, Dock.Left); names.Children.Add(projectText);
            projectText.Measure(new Size(65, double.PositiveInfinity));
            var projectWidth = projectText.DesiredSize.Width;
            names.SizeChanged += (_, _) => title.MaxWidth = Math.Max(24, names.ActualWidth - projectWidth - 8);
        }
        Grid.SetColumn(names, 2); row.Children.Add(names);
        var minutes = new TextBlock { Foreground = color, VerticalAlignment = VerticalAlignment.Center };
        void ShowAge(DateTimeOffset at,double size)
        {
            var elapsed=ChatListPolicy.ElapsedAge(at,now);
            minutes.Inlines.Add(new Run(elapsed.Number) { FontSize=size,FontWeight=FontWeights.SemiBold });
            minutes.Inlines.Add(new Run(elapsed.Unit) { FontSize=10 });
            if(elapsed.Minutes is { } remainder)
            {
                minutes.Inlines.Add(new Run(remainder) { FontSize=size,FontWeight=FontWeights.SemiBold });
                minutes.Inlines.Add(new Run("m") { FontSize=10 });
            }
        }
        if (e.Compacted)
        {
            if (historical) minutes.Inlines.Add(new Run(e.CompactedAt?.ToLocalTime().ToString("HH:mm") ?? "—") { FontSize = 12 });
            else if (e.CompactedAt is { } compact)
                ShowAge(compact,16);
        }
        else if (historical)
            minutes.Inlines.Add(new Run((e.ActivityAt ?? e.RequestAt).ToLocalTime().ToString("HH:mm")) { FontSize = 12 });
        else if (e.WorkPending)
            minutes.Inlines.Add(new Run(e.Running ? (dense?Loc.T("运行"):Loc.T("进行中")) : Loc.T("未确认")) { FontSize = 11, FontWeight = FontWeights.SemiBold });
        else
            ShowAge(e.RequestAt,dense?14:age>=60?16:20);
        if (e.Compacted)
        {
            var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"compact · {e.CompactedAt?.ToLocalTime():M/d HH:mm}" };
            status.Children.Add(CompactIcon(12)); status.Children.Add(minutes);
            Grid.SetColumn(status, 3); row.Children.Add(status);
        }
        else { Grid.SetColumn(minutes, 3); row.Children.Add(minutes); }
        return row;
    }

    FrameworkElement ChatTip(ChatCacheEntry entry, DateTimeOffset now, bool historical)
    {
        var panel=new StackPanel {Width=232};
        TextBlock Text(string value,double size,string color="Ink") => new() {Text=value,FontSize=size,Foreground=Theme.Brush(color),TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis};
        var title=Text(entry.Title,12); title.FontWeight=FontWeights.SemiBold; panel.Children.Add(title);
        panel.Children.Add(Text(entry.Platform + (entry.Project is {Length:>0} project ? " · "+project : ""),10,"Muted"));
        var status=entry.Compacted ? Loc.F($"已 compact · {entry.CompactedAt?.ToLocalTime():M/d HH:mm}")
            : entry.Running ? Loc.T("进行中") : entry.ActivityUncertain ? Loc.T("状态未确认") : Loc.T("本轮已结束");
        if(historical) status=Loc.T("末次状态 · ")+status;
        else if(!entry.Compacted)
        {
            var elapsed=ChatListPolicy.ElapsedAge(entry.RequestAt,now);
            var requestAge=elapsed.Number+elapsed.Unit+(elapsed.Minutes is { } m?m+"m":"");
            status+=Loc.F($" · 最近请求 {requestAge} 前");
        }
        var statusText=Text(status,10,"Muted"); statusText.Margin=new Thickness(0,5,0,7); panel.Children.Add(statusText);
        if (_model.Settings.TokenTrackingEnabled)
        {
            var tokens=_app.Tokens(DateTimeOffset.UnixEpoch,now,entry);
            var grid=new System.Windows.Controls.Primitives.UniformGrid {Columns=3};
            foreach(var (label,value) in new[]{("IN",tokens.Input),("CACHE",tokens.Cached),("OUT",tokens.Output)})
            {
                var cell=new StackPanel(); cell.Children.Add(Text(label,9,"Muted"));
                var number=Text(tokens.Requests==0?"—":TokenSummary.Number(value),16); number.FontWeight=FontWeights.SemiBold; cell.Children.Add(number); grid.Children.Add(cell);
            }
            panel.Children.Add(grid);
            var footer=Text(tokens.Requests==0 ? Loc.T("暂无用量记录") : Loc.F($"本机累计 · {tokens.Requests:N0} 请求"),10,"Muted");
            footer.Margin=new Thickness(0,7,0,0); panel.Children.Add(footer);
            if(tokens.Conflicts>0) panel.Children.Add(Text(Loc.T("记录冲突 · 详见数据诊断"),10,"Muted"));
        }
        return panel;
    }

    void PlaceChartButtons()
    {
        foreach(var (button,codex) in new[]{(ClaudeCollapseButton,false),(CodexCollapseButton,true)})
        {
            var top=Chart.HeaderTop(codex);
            var hotspot=codex?CodexUsageHotspot:ClaudeUsageHotspot;
            var modes=codex?CodexChartModes:ClaudeChartModes;
            var bounds=Chart.ModeBounds(codex);
            modes.Visibility=bounds is null?Visibility.Collapsed:Visibility.Visible;
            if(bounds is { } rect) {Canvas.SetLeft(modes,rect.Left);Canvas.SetTop(modes,rect.Top);hotspot.Width=Math.Max(0,rect.Left-3);}
            hotspot.Visibility=top is null?Visibility.Collapsed:Visibility.Visible;
            if(top is { } y0) Canvas.SetTop(hotspot,y0);
            button.Visibility=top is null ? Visibility.Collapsed : Visibility.Visible;
            if(top is { } y) Canvas.SetTop(button,y);
            var collapsed=codex ? _model.Settings.CodexChartCollapsed : _model.Settings.ClaudeChartCollapsed;
            button.IsChecked=collapsed; button.Content=collapsed ? "›" : "⌄";
            button.ToolTip=(collapsed?Loc.T("展开 "):Loc.T("收起 "))+(codex?"Codex":"Claude / Fable")+Loc.T(" 图表");
            System.Windows.Automation.AutomationProperties.SetName(button,button.ToolTip.ToString());
        }
    }

    void ChartCollapse_Click(object sender,RoutedEventArgs e)
    {
        if(sender==CodexCollapseButton) _model.Settings.CodexChartCollapsed=CodexCollapseButton.IsChecked==true;
        else _model.Settings.ClaudeChartCollapsed=ClaudeCollapseButton.IsChecked==true;
        Chart.ClearInspect(); SaveAndRender();
    }

    public void RenderTokens()
    {
        var detail=_app.TokenDetail;
        TokenDiagnosticsText.Text=(_model.Settings.TokenTrackingEnabled?"":Loc.T("统计已暂停。\n"))+Loc.T("IN：未缓存输入，含已报告的缓存写入。\nCACHE：各请求累计读取的缓存 token，不是缓存占用大小。\nOUT：输出，已包含推理。\n1B = 10亿，1M = 100万，1k = 1000 token。\n平台表跟随所选时间范围；chat 卡片为该 chat 累计，含已确认归属的子代理，不混入无关 chat。\n两种分组使用同一批记录，不能重复相加。\n\n")+detail;
        if (_model.Settings.CompactMode) return;
        var claudeVisible=_model.Settings.Monitors(ChatPlatform.Claude)&&!_model.Settings.ClaudeChartCollapsed;
        var codexVisible=_model.Settings.Monitors(ChatPlatform.Codex)&&!_model.Settings.CodexChartCollapsed;
        ClaudeTokenHitArea.Visibility=claudeVisible?Visibility.Visible:Visibility.Collapsed;
        CodexTokenHitArea.Visibility=codexVisible?Visibility.Visible:Visibility.Collapsed;
        TokenPanel.Visibility = _model.Settings.TokenTrackingEnabled && (claudeVisible||codexVisible) ? Visibility.Visible : Visibility.Collapsed;
        TokenPanel.RowDefinitions[1].Height=new GridLength(claudeVisible?22:0);
        TokenPanel.RowDefinitions[2].Height=new GridLength(codexVisible?22:0);
        if (!_model.Settings.TokenTrackingEnabled) return;
        var now=DateTimeOffset.Now;
        var range=ChartRanges.Select(_model.Settings.RangeMinutes,AvailableRanges());
        var start=range==0 ? DateTimeOffset.UnixEpoch : now.AddMinutes(-range);
        var suffix=detail.Contains(Loc.T("正在补读"))?"…":detail.Contains(Loc.T("未计入"))||detail.Contains(Loc.T("未包含"))?"*":"";
        TokenScope.Content="TOKEN"+suffix;
        foreach(var (platform,label,input,cached,output) in new[] {
            (ChatPlatform.Claude,ClaudeTokenLabel,ClaudeInput,ClaudeCached,ClaudeOutput),
            (ChatPlatform.Codex,CodexTokenLabel,CodexInput,CodexCached,CodexOutput)})
        {
            var visible=platform==ChatPlatform.Claude?claudeVisible:codexVisible;
            foreach(var element in new[]{label,input,cached,output}) element.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
            if(!visible) continue;
            var totals=HoverTokenTotal(platform,range)??_app.Tokens(start,now,platform:platform);
            input.Text=totals.Requests==0?"—":TokenSummary.Number(totals.Input);
            cached.Text=totals.Requests==0?"—":TokenSummary.Number(totals.Cached);
            output.Text=totals.Requests==0?"—":TokenSummary.Number(totals.Output);
            label.ToolTip=Loc.F($"{platform} · {ChartRanges.Label(range)} · {totals.Requests:N0} 请求");
            input.ToolTip=$"IN · {totals.Input:N0} token";
            cached.ToolTip=$"CACHE · {totals.Cached:N0} token";
            output.ToolTip=$"OUT · {totals.Output:N0} token";
        }
    }

    void TokenDiagnostics_Click(object sender,RoutedEventArgs e) { ShowSettings(true); TokenDiagnostics.IsExpanded=true; TokenCalculation.IsExpanded=true; RenderTokens(); }

    void TrendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if(_suppress || TrendCombo.SelectedItem is not ComboBoxItem item) return;
        _model.Settings.TrendMinutes=int.Parse((string)item.Tag,CultureInfo.InvariantCulture); _model.SaveSettings(); Render();
    }
    void TokenCheck_Click(object sender, RoutedEventArgs e)
    {
        _model.Settings.TokenTrackingEnabled=TokenCheck.IsChecked==true; _model.SaveSettings(); _cacheSignature=null; RenderTokens(); RenderCache();
    }

    FrameworkElement CompactIcon(double size) => new System.Windows.Shapes.Path
    {
        Data = Geometry.Parse("M3,3 L9,9 M3,9 H9 V3 M21,21 L15,15 M15,21 V15 H21"),
        Width = size, Height = size, Stretch = Stretch.Uniform, Stroke = Theme.Brush("Muted"), StrokeThickness = 1.6,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        Margin = new Thickness(0,0,4,0), VerticalAlignment = VerticalAlignment.Center,
    };

    void RenderSessionStart(DateTimeOffset now)
    {
        var session = _app.CurrentSession(now);
        var start = _model.DemoMode ? _demoSessionStart : session?.Start;
        if (start is not { } at) { SessionStartText.Text = ""; return; }
        var local = at.ToLocalTime(); var today = now.ToLocalTime().Date;
        var iconOnly=Loc.IsEnglish||_model.Settings.Width<280;
        HistoryLabel.Visibility=iconOnly?Visibility.Collapsed:Visibility.Visible;
        HistoryGlyph.Margin=new Thickness(0,0,iconOnly?0:4,0);
        HistoryButton.Margin=new Thickness(iconOnly?3:6,0,0,0);
        HistoryButton.Padding=new Thickness(3,3,3,3);
        SessionStartText.Margin=new Thickness(3,0,0,0);
        SessionStartText.Text = Loc.IsEnglish ? local.ToString(local.Date==today?"HH:mm":"M/d HH:mm",CultureInfo.InvariantCulture)
            : local.Date == today ? Loc.F($"{local:HH:mm} 起") : local.Date == today.AddDays(-1) ? Loc.F($"昨{local:HH:mm} 起") : Loc.F($"{local:M/d HH:mm} 起");
        SessionStartText.ToolTip = Loc.F($"开始 · {local:M/d HH:mm}") + (session?.Recovered == true ? Loc.T(" · 恢复记录") : "");
    }

    string? _historySignature, _selectedSession;
    void ShowHistory(bool open)
    {
        CloseUsage();
        HistoryPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        MainPanel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
        NoteBar.Visibility = Visibility.Collapsed;
        HistoryButton.Background = open ? Theme.Brush("Raised") : Brushes.Transparent;
        SettingsButton.Background = Brushes.Transparent;
        DisplayLabelText.Text = open ? Loc.T("回顾") : _model.Settings.Display == "used" ? Loc.T("已用 %") : Loc.T("剩余 %");
        if (open) { ReleaseChart(); RenderHistory(force: true); }
        else { _model.ReleaseCalendar(); _codex.ReleaseCalendar(); if (!_suppress) _app.Render(); }
    }

    void RenderHistory(bool force = false)
    {
        RenderSessionStart(DateTimeOffset.Now);
        TagText.Text = _model.DemoMode ? Loc.T("演示数据") : "";
        TagText.Visibility = _model.DemoMode ? Visibility.Visible : Visibility.Collapsed;
        var now=DateTimeOffset.Now;
        var date = _historyDay;
        var claudeDays=_model.CalendarMonth(_historyMonth,now);
        var codexDays=_codex.CalendarMonth(_historyMonth,now);
        if(_opts.Demo && _opts.Scenario=="calendar")
        {
            IReadOnlyList<DayQuota> DemoDays(int salt) => Enumerable.Range(1,DateTime.DaysInMonth(_historyMonth.Year,_historyMonth.Month)).Select(d=>
            {
                var day=new DateTime(_historyMonth.Year,_historyMonth.Month,d);
                return day>DateTime.Today || d%7==0 ? DayQuota.Empty(day) : new DayQuota(day,d%5==0?0:Math.Round((d*13+salt)%39+(d%4)*.2,1),720,d%8==0,false);
            }).ToArray();
            claudeDays=DemoDays(3); codexDays=DemoDays(11);
        }
        var monthSessions=_app.SessionsForMonth(_historyMonth);
        var current = _app.CurrentSession(DateTimeOffset.Now);
        var signature = _historyMonth + "|" + date.Date + "|" + string.Join(";",monthSessions.Select(s=>$"{s.Id}:{s.End:O}:{s.Chats.Count}")) + "|" + _selectedSession + "|" + Theme.IsDark
            + "|" + string.Join(";",claudeDays.Concat(codexDays).Select(d=>$"{d.Points}:{d.CoverageMinutes}:{d.HasGap}"));
        if (!force && signature == _historySignature) return;
        _historySignature = signature;
        RenderCalendar(claudeDays,codexDays,monthSessions);
        var sessions = monthSessions.Where(s=>s.Start.ToLocalTime().Date<=date && s.End.ToLocalTime().Date>=date).ToArray();
        HistoryDayTitle.Text=date.ToString(Loc.T("M月d日"),CultureInfo.InvariantCulture)+(date==DateTime.Today?Loc.T(" · 今天"):"");
        HistoryDayCount.Text=sessions.Length==0?Loc.T("无使用段"):Loc.F($"{sessions.Length} 个使用段");
        var selected = sessions.FirstOrDefault(s => s.Id == _selectedSession) ?? sessions.FirstOrDefault();
        _selectedSession = selected?.Id;
        HistorySessions.Children.Clear(); HistoryChats.Children.Clear();
        foreach (var session in sessions)
        {
            var start = session.Start.ToLocalTime(); var end = session.End.ToLocalTime();
            var period = start.Date == end.Date ? $"{start:HH:mm} – {end:HH:mm}" : $"{start:M/d HH:mm} → {end:M/d HH:mm}";
            var button = new Button { Style = (Style)FindResource("FlatButton"), FontSize = 11,
                HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,1,0,1),
                Background = Theme.Brush(session.Id == selected?.Id ? "Raised" : "Bg"),
                Content = new TextBlock { Text = $"{period} · {session.Chats.Count} chats", TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = $"{period} · {session.Chats.Count} chats" };
            button.Click += (_, _) => { _selectedSession = session.Id; RenderHistory(force: true); };
            HistorySessions.Children.Add(button);
        }
        if (selected is null) { HistoryPeriod.Text = Loc.T("当天暂无可回顾的 chat 记录"); HistoryPeriod.ToolTip=null; return; }
        HistoryPeriod.Text = (selected.Start.ToLocalTime().Date!=selected.End.ToLocalTime().Date ? Loc.T("跨日使用段 · ") : Loc.T("使用段 · ")) + (selected.Id == current?.Id ? Loc.T("本次使用"):Loc.T("已保存")) + (selected.Recovered ? Loc.T(" · 含恢复记录"):"");
        HistoryPeriod.ToolTip=$"{selected.Start.ToLocalTime():M/d HH:mm} → {selected.End.ToLocalTime():M/d HH:mm}";
        foreach (var chat in selected.Chats.OrderBy(c => c.FirstAt))
        {
            var block = new StackPanel { Margin = new Thickness(0,3,0,5) };
            block.Children.Add(CacheRow(chat.Last, selected.End, historical: true));
            var last = chat.Last.ActivityAt ?? chat.Last.RequestAt;
            var detail = new WrapPanel { Margin = new Thickness(29,0,0,0),
                ToolTip = Loc.T("首次 / 最近活动") + (chat.Compactions.Count > 0 ? Loc.F($"\ncompact {chat.Compactions.Count} 次 · 最近 {chat.Compactions.Max().ToLocalTime():M/d HH:mm}") : "") };
            detail.Children.Add(new TextBlock { Text = $"{chat.FirstAt.ToLocalTime():M/d HH:mm} – {last.ToLocalTime():M/d HH:mm}", FontSize = 9, Foreground = Theme.Brush("Muted") });
            if (chat.Compactions.Count > 0)
            {
                var marker = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6,0,0,0) };
                marker.Children.Add(CompactIcon(9));
                marker.Children.Add(new TextBlock { Text = "×" + chat.Compactions.Count, FontSize = 9, Foreground = Theme.Brush("Muted") });
                detail.Children.Add(marker);
            }
            block.Children.Add(detail);
            HistoryChats.Children.Add(block);
        }
    }

    void RenderCalendar(IReadOnlyList<DayQuota> claude,IReadOnlyList<DayQuota> codex,IReadOnlyList<ChatSession> sessions)
    {
        HistoryMonthTitle.Text=_historyMonth.ToString(Loc.T("yyyy年M月"),CultureInfo.InvariantCulture);
        PreviousMonthButton.IsEnabled=_historyMonth>new DateTime(1,1,1);
        NextMonthButton.IsEnabled=_historyMonth<new DateTime(DateTime.Today.Year,DateTime.Today.Month,1);
        PreviousMonthButton.Opacity=PreviousMonthButton.IsEnabled?1:.3;
        NextMonthButton.Opacity=NextMonthButton.IsEnabled?1:.3;
        string Number(double? value) => value is not { } v ? "—" : v>=100 ? v.ToString("0",CultureInfo.InvariantCulture) : v.ToString("0.#",CultureInfo.InvariantCulture);
        double? Total(IReadOnlyList<DayQuota> days) => days.Any(d=>d.Points is not null) ? days.Sum(d=>d.Points??0) : null;
        MonthQuotaSummary.Inlines.Clear();
        MonthQuotaSummary.Inlines.Add(new Run(Loc.T("已记录  ")){Foreground=Theme.Brush("Muted")});
        MonthQuotaSummary.Inlines.Add(new Run($"Claude {Number(Total(claude))}"){Foreground=Theme.Brush("Claude")});
        MonthQuotaSummary.Inlines.Add(new Run("   "));
        MonthQuotaSummary.Inlines.Add(new Run($"Codex {Number(Total(codex))}"){Foreground=Theme.Brush("Violet")});
        MonthQuotaSummary.ToolTip=Loc.T("本月已记录 · 各平台周额度点");
        var c=claude.ToDictionary(d=>d.Day); var x=codex.ToDictionary(d=>d.Day);
        var offset=((int)_historyMonth.DayOfWeek+6)%7;
        var first=_historyMonth.AddDays(-offset);
        var count=((DateTime.DaysInMonth(_historyMonth.Year,_historyMonth.Month)+offset+6)/7)*7;
        HistoryCalendar.Rows=count/7; HistoryCalendar.Children.Clear();
        for(var i=0;i<count;i++)
        {
            var day=first.AddDays(i); var own=day.Month==_historyMonth.Month; var future=day>DateTime.Today;
            var a=c.GetValueOrDefault(day)??DayQuota.Empty(day); var b=x.GetValueOrDefault(day)??DayQuota.Empty(day);
            var linked=sessions.Count(s=>s.Start.ToLocalTime().Date<=day&&s.End.ToLocalTime().Date>=day);
            var hasRecord=a.Points is not null || b.Points is not null || linked>0;
            var selected=day==_historyDay; var today=day==DateTime.Today;
            // Explicit text rows leave room for each baseline, including the selected border at fractional DPI.
            var stack=new Grid {Height=46};
            stack.RowDefinitions.Add(new RowDefinition {Height=new GridLength(14)});
            stack.RowDefinitions.Add(new RowDefinition {Height=new GridLength(16)});
            stack.RowDefinitions.Add(new RowDefinition {Height=new GridLength(16)});
            TextBlock Text(string text,string brush,double size) => new() {Text=text,Foreground=Theme.Brush(brush),FontSize=size,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.NoWrap};
            var header=new Grid();
            var date=Text(day.Day.ToString(),own?"Ink":"Muted",11); date.FontWeight=hasRecord||today?FontWeights.SemiBold:FontWeights.Normal; header.Children.Add(date);
            if(linked>0&&own) header.Children.Add(new System.Windows.Shapes.Ellipse {Width=3,Height=3,Fill=Theme.Brush("Muted"),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,3,3,0)});
            stack.Children.Add(header);
            var claudeNumber=Text(!own||future?" ":Number(a.Points),a.Points is null?"Muted":"Claude",11);
            var codexNumber=Text(!own||future?" ":Number(b.Points),b.Points is null?"Muted":"Violet",11);
            Grid.SetRow(claudeNumber,1); Grid.SetRow(codexNumber,2); stack.Children.Add(claudeNumber); stack.Children.Add(codexNumber);
            string Metric(string name,DayQuota d) => d.Points is { } points
                ? Loc.F($"{name} {(d.SplitAtMidnight?"≈":"")}{points:0.##} 点 · {d.CoverageMinutes/60:0.#}h记录")+(d.HasGap?Loc.T(" · 缺采样"):"")
                : name+Loc.T(" — · 无记录");
            var button=new Button {Style=(Style)FindResource("FlatButton"),Content=stack,Padding=new Thickness(0,2,0,2),HorizontalContentAlignment=HorizontalAlignment.Stretch,
                IsEnabled=!future, ToolTip=Loc.F($"{day:yyyy/M/d} · {linked} 个使用段\n{Metric("Claude",a)}\n{Metric("Codex",b)}")};
            System.Windows.Automation.AutomationProperties.SetName(button,Loc.F($"{day:yyyy年M月d日}，{Metric("Claude",a)}，{Metric("Codex",b)}，{linked}个使用段"));
            button.Click+=(_,_)=>SelectHistoryDay(day);
            HistoryCalendar.Children.Add(new Border {Child=button,Height=54,Margin=new Thickness(1),CornerRadius=new CornerRadius(5),BorderThickness=new Thickness(selected?1.4:1),
                BorderBrush=Theme.Brush(selected?"Ink":today?"Blue":"Line"),Background=Theme.Brush(hasRecord&&own?"Raised":"Bg"),Opacity=future ? .32 : own ? 1 : .5});
        }
    }

    void HistoryButton_Click(object sender, RoutedEventArgs e) => ShowHistory(HistoryPanel.Visibility != Visibility.Visible);
    void HistoryBack_Click(object sender, RoutedEventArgs e) => ShowHistory(false);
    void SelectHistoryDay(DateTime day)
    {
        _historyDay=day.Date; _historyMonth=new(day.Year,day.Month,1); _selectedSession=null; RenderHistory(force:true);
    }
    void PreviousMonth_Click(object sender,RoutedEventArgs e) => SelectHistoryDay(_historyMonth.AddMonths(-1));
    void NextMonth_Click(object sender,RoutedEventArgs e) => SelectHistoryDay(_historyMonth.AddMonths(1));
    void HistoryToday_Click(object sender,RoutedEventArgs e) => SelectHistoryDay(DateTime.Today);

    public void Flash(string message)
    {
        _flash = message;
        _flashUntil = DateTimeOffset.Now.AddSeconds(4);
        _app.Render();
        var t = new DispatcherTimer(TimeSpan.FromSeconds(4.2), DispatcherPriority.Background, (s, _) => { ((DispatcherTimer)s!).Stop(); _app.Render(); }, Dispatcher);
        t.Start();
    }

    public void OnThemeChanged()
    {
        CloseUsage();
        Shadow.Opacity = Theme.IsDark ? 0.25 : 0.09;
        Chart.InvalidateVisual();
    }

    public void SetTopmost(bool on)
    {
        foreach(var window in _usageWindows.Values) window.Topmost=on;
        _model.Settings.Topmost = on;
        Topmost = on;
        PinButton.IsChecked = on;
        _model.SaveSettings();
    }

    public void Inspect(TimeSpan ago)
    {
        if (Chart.View is { } v)
        {
            if (_opts.InspectPlatform is { } platform) Chart.PinInspect(v.End - ago, platform == "codex");
            else Chart.Pinned = v.End - ago;
        }
    }

    public void OpenSettings()
    {
        ShowSettings(true);
    }

    void ClaudeLogin_Click(object sender,RoutedEventArgs e) => _app.LaunchLogin();
    void ClaudeLogout_Click(object sender,RoutedEventArgs e) => _app.LaunchLogout();

    void ShowSettings(bool open)
    {
        CloseUsage();
        CacheDiagnosticsText.Text=_app.CacheWarning??Loc.T("状态读取正常");
        ClaudeLoginButton.IsEnabled=ClaudeLogoutButton.IsEnabled=_app.CollectorAvailable&&_model.Settings.Monitors(ChatPlatform.Claude);
        HistoryPanel.Visibility = Visibility.Collapsed;
        HistoryButton.Background = Brushes.Transparent;
        DisplayLabelText.Text = _model.Settings.Display == "used" ? Loc.T("已用 %") : Loc.T("剩余 %");
        MainPanel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        SettingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ApplyCompactLayout();
        RenderConnections();
        SettingsButton.Background = open ? Theme.Brush("Raised") : Brushes.Transparent;
    }

    void SaveAndRender()
    {
        _model.SaveSettings();
        _app.Render();
    }

    // ---------- handlers ----------

    void PinButton_Click(object sender, RoutedEventArgs e) => SetTopmost(PinButton.IsChecked == true);

    void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsPanel.Visibility != Visibility.Visible);

    void BackButton_Click(object sender, RoutedEventArgs e) => ShowSettings(false);

    void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    void NoteBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        switch (_noteAction)
        {
            case NoteAction.Login: _app.LaunchLogin(); break;
            case NoteAction.Retry: _app.TriggerCollect(); break;
        }
    }

    void Legend_Click(object sender, RoutedEventArgs e)
    {
        var s = _model.Settings;
        s.TotalVisible = TotalLegend.IsChecked == true;
        s.FableVisible = FableLegend.IsChecked == true;
        s.CodexVisible = CodexLegend.IsChecked == true;
        Chart.ClearInspect();
        SaveAndRender();
    }

    void Range_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton b) return;
        _model.Settings.RangeMinutes = int.Parse((string)b.Tag, CultureInfo.InvariantCulture);
        Chart.ClearInspect();
        SaveAndRender();
    }

    void ChartMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button) return;
        if(button==CodexRateMode||button==CodexCumulativeMode) _model.Settings.CodexChartMode=(string)button.Tag;
        else _model.Settings.ClaudeChartMode=(string)button.Tag;
        Chart.ClearInspect();
        SaveAndRender();
    }

    void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || DisplayCombo.SelectedItem is not ComboBoxItem item) return;
        _model.Settings.Display = (string)item.Tag;
        SaveAndRender();
    }

    void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || IntervalCombo.SelectedItem is not ComboBoxItem item) return;
        _model.Settings.PollIntervalSeconds = int.Parse((string)item.Tag, CultureInfo.InvariantCulture);
        SaveAndRender();
    }

    void SmoothCheck_Click(object sender, RoutedEventArgs e)
    {
        _model.Settings.Smoothing = SmoothCheck.IsChecked == true;
        Chart.ClearInspect();
        SaveAndRender();
    }

    void CacheCheck_Click(object sender, RoutedEventArgs e)
    {
        _model.Settings.CacheRemindersEnabled = CacheCheck.IsChecked == true;
        SaveAndRender();
    }

    void AutoLogoutCheck_Click(object sender, RoutedEventArgs e)
    {
        _model.Settings.AutoLogoutOnExit = AutoLogoutCheck.IsChecked == true;
        SaveAndRender();
    }

    void WidthGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_model.Settings.CompactMode) return;
        var s = _model.Settings;
        s.Width = Math.Clamp(Math.Round(s.Width + e.HorizontalChange), 240, 340);
        Width = s.Width + 2 * ShadowMargin;
    }

    void WidthGrip_DragCompleted(object sender, DragCompletedEventArgs e) => SaveAndRender();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 tucks the widget into the tray; "退出" in the tray menu really quits.
        if (!_app.Exiting && _opts.Snapshot is null)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // ---------- QA snapshot ----------

    public void SaveSnapshot(string path, double scale)
    {
        if(_opts.UsageCard is { } source&&Enum.TryParse<ChatPlatform>(source,true,out var usagePlatform))
        {
            var card=CreateUsageCard(usagePlatform,_opts.UsageMinutes);
            if(_opts.UsagePinned) card.SetPinned(true);
            card.Measure(new Size(card.Width,double.PositiveInfinity));card.Arrange(new Rect(card.DesiredSize));card.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth*scale),(int)Math.Ceiling(card.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            bitmap.Render(card);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);png.Save(file);return;
        }
        if (_opts.ChatCard)
        {
            var now=DateTimeOffset.Now;
            var entry=_app.CacheEntries(now).FirstOrDefault(e=>e.Running) ?? _app.CacheEntries(now).FirstOrDefault();
            if(entry is null) throw new InvalidOperationException("No chat available for card preview.");
            var border=new Border {Background=Theme.Brush("Bg"),BorderBrush=Theme.Brush("Line"),BorderThickness=new Thickness(1),Padding=new Thickness(9,7,9,7),CornerRadius=new CornerRadius(6),Child=ChatTip(entry,now,false)};
            border.Measure(new Size(252,double.PositiveInfinity)); border.Arrange(new Rect(border.DesiredSize)); border.UpdateLayout();
            var bmp=new RenderTargetBitmap((int)Math.Ceiling(border.ActualWidth*scale),(int)Math.Ceiling(border.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            bmp.Render(border); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using var file=File.Create(path); encoder.Save(file); return;
        }
        var root = (FrameworkElement)Content;
        root.UpdateLayout();
        var w = root.ActualWidth + root.Margin.Left + root.Margin.Right;
        var h = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var backdrop = new SolidColorBrush(Theme.IsDark ? Color.FromRgb(0x18, 0x18, 0x18) : Color.FromRgb(0xee, 0xef, 0xf1));
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, w, h));
            // Absolute viewbox: the shadow's overflow must not shift the content.
            dc.DrawRectangle(new VisualBrush(root)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                // The arranged visual already includes its margin offset. Applying
                // it again shifts the frame right/down and crops the opposite edges.
                Viewbox = new Rect(0, 0, w, h),
            }, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
