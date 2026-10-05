using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuotaWidget.Core;

namespace QuotaWidget.App;

/// <summary>Claude and Fable share the upper panel; Codex has its own panel.</summary>
public sealed class RateChart : FrameworkElement
{
    static readonly Typeface Face = new(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    ChartView? _view;
    sealed record InspectPoint(DateTimeOffset Time, bool Codex);
    InspectPoint? _hover, _pinned;
    ChartInspectOverlay? _inspectOverlay;
    DateTimeOffset _displayStart;
    const double _left = 1;
    List<Lane> _lanes = [];
    readonly Dictionary<int,IReadOnlyList<AxisTick>> _axisTicks=[];
    sealed record Lane(string Name, string Color, SeriesData Source, RateTrend Trend, CumulativeSeries Cumulative, int Panel)
    {
        public List<ChartPeak>? Peaks { get; set; }
    }
    int PanelCount => _lanes.Select(l => l.Panel).Distinct().Count();
    double LaneHeight => PanelCount <= 1 ? 148 : 102;
    bool Collapsed(int i) => _lanes.First(l => l.Panel == i).Name == "Codex" ? _view?.CodexCollapsed == true : _view?.ClaudeCollapsed == true;
    double PanelHeight(int i) => Collapsed(i) ? 26 : LaneHeight;
    double PanelExtent(int i) => PanelHeight(i)+(Collapsed(i)?0:TimeAxisHeight);
    double PanelTop(int i) => Enumerable.Range(0,i).Sum(PanelExtent);
    double TimeAxisHeight => AxisHasDate?32:18;
    public double? HeaderTop(bool codex) => _lanes.FirstOrDefault(l => (l.Name == "Codex") == codex) is { } lane ? PanelTop(lane.Panel) : null;
    public Rect? ModeBounds(bool codex) => _lanes.FirstOrDefault(l => (l.Name == "Codex") == codex) is { } lane
        ? new Rect(Header(lane.Panel).ModeX,PanelTop(lane.Panel),64,20) : null;
    public Rect? AxisBounds(bool codex) => _lanes.FirstOrDefault(l=>(l.Name=="Codex")==codex) is {} lane&&!Collapsed(lane.Panel)
        ? new Rect(_left,PlotBottom(lane.Panel)+1,PlotRight-_left,TimeAxisHeight+8) : null;
    bool CumulativePanel(int panel) => _view?.IsCumulative(_lanes.First(l=>l.Panel==panel).Name=="Codex")==true;
    double PlotRight => Math.Max(_left + 40, ActualWidth - 1);
    double PlotTop(int i) => 25 + PanelTop(i);
    double PlotBottom(int i) => PanelTop(i) + PanelHeight(i) - 9;

    public RateChart()
    {
        Height = 180;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Arrow;
        SnapsToDevicePixels = true;
        RefreshLanguage();
    }

    public void RefreshLanguage()
    {
        AutomationPropertiesHelper.SetName(this, Loc.T("额度消耗图；Claude 含 Fable，Codex 独立；左右方向键查看时间点，Escape关闭读数"));
        InvalidateVisual();
        _inspectOverlay?.InvalidateVisual();
    }

    public ChartView? View
    {
        get => _view;
        set
        {
            _view = value;
            _axisTicks.Clear();
            _lanes = [];
            if (value is { } v)
            {
                void Add(string name, string color, SeriesData source, RateTrend trend, CumulativeSeries? cumulative, int panel) =>
                    _lanes.Add(new(name, color, source, trend, cumulative ?? CumulativeSeries.Build(source, v.Start, v.End), panel));
                if (v.TotalVisible) Add("Claude", "Claude", v.Total, v.TotalTrend, v.TotalCumulative, 0);
                if (v.FableDrawable) Add("Fable", "Fable", v.Fable, v.DisplayFableTrend, v.FableCumulative, 0);
                if (v.CodexVisible && v.Codex is { } codex) Add("Codex", "Violet", codex, v.CodexTrend!, v.CodexCumulative, _lanes.Count > 0 ? 1 : 0);
                // Show actual coverage when recording only began recently.
                // Never compress internal gaps or extend into the unobserved tail.
                var first = _lanes.SelectMany(l => l.Trend.Runs).Select(r => (DateTimeOffset?)r.Points[0].Time).Min();
                _displayStart = first is { } f && f > v.Start ? f : v.Start;
                if (_displayStart >= v.End) _displayStart = v.Start;
            }
            if (!ValidInspect(_hover)) _hover = null;
            Cursor = _hover is null ? Cursors.Arrow : Cursors.Cross;
            if (!ValidInspect(_pinned)) _pinned = null;
            Height = PanelCount == 0 ? 72 : Enumerable.Range(0,PanelCount).Sum(PanelExtent);
            InvalidateInspect();
        }
    }

    public void AttachInspectOverlay(ChartInspectOverlay overlay) { _inspectOverlay = overlay; overlay.Chart = this; }
    public DateTimeOffset? Pinned
    {
        get => _pinned?.Time;
        set { _pinned = value is { } at ? new(at, DefaultInspectCodex) : null; InvalidateInspect(); }
    }
    bool DefaultInspectCodex => _lanes.FirstOrDefault(l => !Collapsed(l.Panel))?.Name == "Codex";
    int? InspectPanel(InspectPoint? point) => point is not null && _lanes.FirstOrDefault(l => (l.Name == "Codex") == point.Codex) is { } lane && !Collapsed(lane.Panel) ? lane.Panel : null;
    bool ValidInspect(InspectPoint? point) => _view is { } v && point is not null && point.Time >= _displayStart && point.Time <= v.End && InspectPanel(point) is not null;
    void InvalidateInspect() { InvalidateVisual(); _inspectOverlay?.InvalidateVisual(); }
    public void ClearInspect() { _pinned = _hover = null; Cursor=Cursors.Arrow; InvalidateInspect(); }
    public void PinInspect(DateTimeOffset time, bool codex) { _hover = null; _pinned = new(time, codex); InvalidateInspect(); }
    InspectPoint? InspectAt(Point p)
    {
        if (_view is null || p.X < _left || p.X > PlotRight) return null;
        var lane = _lanes.FirstOrDefault(l => !Collapsed(l.Panel) && p.Y >= PlotTop(l.Panel) && p.Y <= PlotBottom(l.Panel));
        return lane is null ? null : new(T(p.X), lane.Name == "Codex");
    }
    void HoverAt(Point p) { _hover = InspectAt(p); Cursor=_hover is null?Cursors.Arrow:Cursors.Cross; InvalidateInspect(); }
    double X(DateTimeOffset t) => _left + (t - _displayStart).TotalSeconds / Math.Max(1, (_view!.End - _displayStart).TotalSeconds) * (PlotRight - _left);
    DateTimeOffset T(double x) => _displayStart + TimeSpan.FromSeconds(Math.Clamp((x - _left) / Math.Max(1, PlotRight - _left), 0, 1) * (_view!.End - _displayStart).TotalSeconds);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        HoverAt(e.GetPosition(this));
    }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; Cursor=Cursors.Arrow; InvalidateInspect(); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_view is null) return;
        var point = InspectAt(e.GetPosition(this));
        if (point is null) return;
        Focus();
        if (_pinned is not null) _pinned = null;
        else _pinned = point;
        InvalidateInspect();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_view is null) return;
        var current = _hover ?? _pinned ?? new InspectPoint(_view.End, DefaultInspectCodex);
        switch (e.Key)
        {
            case Key.Escape: ClearInspect(); e.Handled = true; return;
            case Key.Left or Key.Right:
                var t = current.Time.AddMinutes(e.Key == Key.Left ? -5 : 5);
                _pinned = current with { Time = t < _displayStart ? _displayStart : t > _view.End ? _view.End : t };
                break;
            case Key.Home: _pinned = current with { Time = _displayStart }; break;
            case Key.End: _pinned = current with { Time = _view.End }; break;
            default: return;
        }
        _hover = null;
        e.Handled = true;
        InvalidateInspect();
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { _axisTicks.Clear();InvalidateVisual(); }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) {base.OnRenderSizeChanged(sizeInfo);_axisTicks.Clear();}
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();

    FormattedText Text(string s, Brush brush, double size = 10) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    void Label(DrawingContext dc, string s, Brush color, double x, double y, double size = 10, TextAlignment align = TextAlignment.Left)
    {
        var t = Text(s, color, size);
        dc.DrawText(t, new Point(x - (align == TextAlignment.Right ? t.Width : align == TextAlignment.Center ? t.Width / 2 : 0), y));
    }
    bool AxisHasDate => _view is { } v && _displayStart.ToLocalTime().Date != v.End.ToLocalTime().Date;
    string Clock(DateTimeOffset t)
    {
        var span = _view is { } v ? v.End - _displayStart : TimeSpan.Zero;
        var format = span.TotalDays >= 365 ? "yyyy/M/d HH:mm"
            : AxisHasDate ? "M/d HH:mm" : "HH:mm";
        return t.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
    }

    (FormattedText Main,FormattedText? Date) AxisText(DateTimeOffset at)
    {
        var span=_view!.End-_displayStart;var local=at.ToLocalTime();
        var main=Text(local.ToString(span.TotalDays>=2?"M/d":span.TotalMinutes<4?"HH:mm:ss":"HH:mm",CultureInfo.InvariantCulture),Theme.Brush("Axis"),11);
        main.SetFontWeight(FontWeights.Medium);
        var date=AxisHasDate?Text(local.ToString(span.TotalDays>=365?"yyyy":span.TotalDays>=2?"HH:mm":"M/d",CultureInfo.InvariantCulture),Theme.Brush("Muted"),10):null;
        return(main,date);
    }
    IReadOnlyList<AxisTick> AxisTicks(int panel)
    {
        if(_axisTicks.TryGetValue(panel,out var cached))return cached;
        var view=_view!;var lanes=_lanes.Where(l=>l.Panel==panel).ToArray();
        var landmarks=new List<AxisLandmark>();
        foreach(var lane in lanes)
        {
            if(CumulativePanel(panel)){landmarks.AddRange(TimeAxis.CumulativeLandmarks(lane.Cumulative));continue;}
            // Use this lane's actual stage edges, including supported fallback boundaries.
            // Never borrow the other provider's events or provisional rate peaks.
            var edges=lane.Trend.Runs.Where(r=>r.Points.Any(p=>p.Rate>0)).SelectMany(r=>
                (r.HardStart?new[]{r.Points[0].Time}:[]).Concat(r.HardEnd?new[]{r.Points[^1].Time}:[]))
                .Where(t=>!(lane.Name=="Claude"&&view.FableOnlyAt(t)));
            var peaks=(lane.Peaks??=PeakLabels.Find(Points(lane,view))).Where(p=>!lane.Trend.IsProvisional(p.Time));
            landmarks.AddRange(TimeAxis.RateLandmarks(edges,peaks));
        }
        double Measure(DateTimeOffset time){var text=AxisText(time);return Math.Max(text.Main.Width,text.Date?.Width??0);}
        return _axisTicks[panel]=TimeAxis.Select(_displayStart,view.End,PlotRight-_left,landmarks,Measure);
    }
    void DrawTimeAxis(DrawingContext dc,int panel)
    {
        var bottom=PlotBottom(panel);
        foreach(var tick in AxisTicks(panel))
        {
            var (main,date)=AxisText(tick.Time);var x=_left+tick.X;
            double Left(FormattedText text)=>_left+tick.Left+(tick.Width-text.Width)/2;
            dc.DrawLine(new Pen(Theme.Brush("Line"),.8),new Point(x,bottom+1),new Point(x,bottom+4));
            dc.DrawText(main,new Point(Left(main),bottom+6));
            if(date is not null)dc.DrawText(date,new Point(Left(date),bottom+6+main.Height));
        }
    }
    static string Number(double n, bool fixedDecimals = false)
    {
        var digits = n != 0 && Math.Abs(n) < 1 ? Math.Clamp((int)Math.Ceiling(-Math.Log10(Math.Abs(n))) + 1, 2, 8) : 2;
        var format = fixedDecimals ? "0.00" + new string('#', digits - 2) : "0." + new string('#', digits);
        return n != 0 && Math.Abs(n) < 1e-8 ? n.ToString("0.##E+0", CultureInfo.InvariantCulture) : n.ToString(format, CultureInfo.InvariantCulture);
    }

    sealed record HeaderContent(string Left,string Right,double FontSize,double ModeX);
    HeaderContent Header(int panel)
    {
        var lanes=_lanes.Where(l=>l.Panel==panel).ToArray();
        string Amount(Lane lane,bool compact)
        {
            var sum=RateEngine.SumRange(lane.Source,_view!.Start,_view.End);
            return sum.CoverageMinutes>0 ? (compact?"":Loc.T("累计 "))+Number(sum.Delta)+(compact?Loc.T("点"):Loc.T(" 点")) : Loc.T("暂无记录");
        }
        var shared=lanes.Length>1;
        var left=shared?lanes[0].Name+" · "+Amount(lanes[0],false):lanes[0].Name;
        var right=(shared?lanes[^1].Name+" · ":"")+Amount(lanes[^1],false);
        var font=10.5;
        double Width(string s)=>Text(s,Theme.Brush("Muted"),font).Width;
        var available=PlotRight-23-_left;
        if(Width(left)+Width(right)+76>available)
        {
            left=shared?lanes[0].Name+" · "+Amount(lanes[0],true):lanes[0].Name;
            right=(shared?lanes[^1].Name+" · ":"")+Amount(lanes[^1],true);
        }
        while(font>8&&Width(left)+Width(right)+76>available)font-=.5;
        var modeX=_left+(Width(left)+available-Width(right)-64)/2;
        return new(left,right,font,Math.Max(_left,modeX));
    }

    protected override void OnRender(DrawingContext dc)
    {
        _inspectOverlay?.InvalidateVisual();
        if (ActualWidth <= 0 || _view is not { } v) return;
        var muted = Theme.Brush("Muted");
        var line = Theme.Brush("Line");
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, Height));
        if (_lanes.Count == 0)
        { Label(dc, Loc.T("点图例选择来源"), muted, ActualWidth / 2, 64, 11, TextAlignment.Center); return; }

        // Each panel follows its own visible peak. Claude/Fable share a panel; Codex does not.
        var allPoints = _lanes.Select(l => Collapsed(l.Panel) ? new List<IReadOnlyList<TrendPoint>>() : Points(l, v)).ToList();
        var scales = ChartScale.ForPanels(_lanes.Select((lane, j) =>
            (lane.Panel, allPoints[j].SelectMany(points => points).Select(point => point.Rate))));
        double Y(int panel, double value) => PlotBottom(panel) - value / scales[panel].Top * (PlotBottom(panel) - PlotTop(panel));

        for (var i = 0; i < PanelCount; i++)
        {
            var labelObstacles = new List<Rect>();
            var scale = scales[i];
            var (top, step) = (scale.Top, scale.Step);
            string Tick(double n) => scale.Label(n);
            var indices = Enumerable.Range(0, _lanes.Count).Where(j => _lanes[j].Panel == i).ToArray();
            var cumulative=CumulativePanel(i);
            var drawn=indices.Where(j=>allPoints[j].Count>0).ToArray();
            var header=Header(i);
            Label(dc,header.Left,Theme.Brush(_lanes[indices[0]].Color),_left,PanelTop(i)+3,header.FontSize);
            Label(dc,header.Right,Theme.Brush(_lanes[indices[^1]].Color),PlotRight-23,PanelTop(i)+3,header.FontSize,TextAlignment.Right);
            if (Collapsed(i)) continue;
            for (double tick = 0; tick <= top + step / 2; tick += step)
            {
                var y = Y(i, tick);
                dc.DrawLine(new Pen(line, 0.6), new Point(_left, y), new Point(PlotRight, y));
            }
            dc.PushClip(new RectangleGeometry(new Rect(_left, PlotTop(i) - 2, PlotRight - _left, PlotBottom(i) - PlotTop(i) + 4)));
            // Paint all fills first, then both solid strokes so a fill never dulls a line.
            foreach (var fillPass in new[] { true, false })
            foreach (var j in indices)
            foreach (var group in allPoints[j])
            {
                var lane = _lanes[j];
                var color = Theme.Brush(lane.Color);
                var pts = group.Select(p => new Point(X(p.Time), Y(i, p.Rate))).ToArray();
                if (pts.Length < 2) continue;
                if (fillPass)
                {
                    if (cumulative) continue;
                    var area = Geometry(pts, Y(i, 0));
                    var fill = new LinearGradientBrush(((SolidColorBrush)color).Color, Colors.Transparent, new Point(0, 0), new Point(0, 1));
                    dc.PushOpacity(drawn.Length>1 ? (Theme.IsDark ? 0.14 : 0.11) : (Theme.IsDark ? 0.30 : 0.24));
                    dc.DrawGeometry(fill, null, area);
                    dc.Pop();
                    continue;
                }
                var thickness = drawn.Length>1 ? lane.Name == "Claude" ? 2.6 : 1.6 : 1.8;
                var pen = new Pen(color, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawGeometry(null, pen, Geometry(pts));
            }
            dc.Pop();
            DrawBoundaries(dc, i, indices, v, labelObstacles);
            // Put ticks inside the plot: the data reaches both edges of the narrow widget.
            // A small background keeps the labels legible if a curve crosses them.
            for (double tick = 0; tick <= top + step / 2; tick += step)
            {
                if (top / step > 3.5 && (int)Math.Round(tick / step) % 2 != 0) continue;
                var label = Text(Tick(tick), Theme.Brush("Axis"), 11.5);
                label.SetFontWeight(FontWeights.SemiBold);
                var labelY = tick == 0 ? Y(i, tick) - label.Height - 1 : tick >= top ? Y(i, tick) + 1 : Y(i, tick) - label.Height / 2;
                labelObstacles.Add(new Rect(_left, labelY, label.Width + 7, label.Height));
                dc.PushOpacity(0.88);
                dc.DrawRectangle(Theme.Brush("Bg"), null, new Rect(_left, labelY, label.Width + 7, label.Height));
                dc.Pop();
                dc.DrawText(label, new Point(_left + 3, labelY));
            }
            if (indices.All(j => !_lanes[j].Source.Segments.Any(s => s.Valid && s.Start >= v.Start && s.End <= v.End)))
                Label(dc, Loc.T("等待连续采样"), muted, (_left + PlotRight) / 2, PlotTop(i) + 18, 11, TextAlignment.Center);
            else if (!cumulative) DrawPeaks(dc, i, indices, allPoints, Y, labelObstacles);
            DrawTimeAxis(dc,i);
        }

        if (ValidInspect(_hover ?? _pinned) && (_hover ?? _pinned) is { } inspect && InspectPanel(inspect) is { } panel)
        {
            dc.DrawLine(new Pen(muted, 0.7), new Point(X(inspect.Time), PlotTop(panel)), new Point(X(inspect.Time), PlotBottom(panel)));
            foreach (var lane in _lanes.Where(l => l.Panel == panel&&!(l.Name=="Claude"&&v.FableOnlyAt(inspect.Time))))
                if (InspectValue(lane, v, inspect.Time) is { } value && value > 0)
                    dc.DrawEllipse(Theme.Brush(lane.Color), null, new Point(X(inspect.Time), Y(panel, value)), 2.7, 2.7);
        }
        if (IsKeyboardFocused) dc.DrawRoundedRectangle(null, new Pen(Theme.Brush("Blue"), 1), new Rect(1, 1, ActualWidth - 2, Height - 2), 4, 4);
    }

    public double KernelMinutes => _lanes.SelectMany(l => l.Trend.Runs).Select(r => r.KernelMinutes).DefaultIfEmpty(60).Max();

    void DrawBoundaries(DrawingContext dc, int panel, int[] indices, ChartView view, List<Rect> obstacles)
    {
        var providerGaps = _lanes[indices[0]].Name == "Codex" ? view.CodexGaps : view.ClaudeGaps;
        var gaps = indices.SelectMany(j => _lanes[j].Source.Segments)
            .Where(s => !s.Valid && s.End > _displayStart && s.Start < view.End)
            .GroupBy(s => (s.Start, s.End))
            .Select(g => g.OrderBy(s => s.Issue == SegmentIssue.Reset ? 0 : 1).First())
            .Select(s => new GapRegion(s.Start,s.End,s.Issue == SegmentIssue.Reset ? "重置" : s.Label == "未运行" ? "停采" : s.Label ?? "缺口"))
            .Concat(providerGaps.Where(g=>g.Label=="断开" && g.End>_displayStart && g.Start<view.End)).OrderBy(s => s.Start);
        var lastLabelRight = double.NegativeInfinity;
        foreach (var gap in ChartBoundaries.Coalesce(gaps))
        {
            var left = X(gap.Start < _displayStart ? _displayStart : gap.Start);
            var right = X(gap.End > view.End ? view.End : gap.End);
            var x = (left + right) / 2;
            var quiet = gap.Label == "已暂停" || (right-left<8&&gap.Label is not ("重置" or "周期变化" or "断开"));
            dc.PushOpacity(quiet ? .15 : .35);
            dc.DrawRectangle(Theme.Brush("Line"), null, new Rect(left, PlotTop(panel), Math.Max(1, right-left), PlotBottom(panel)-PlotTop(panel)));
            dc.Pop();
            var pen = new Pen(Theme.Brush("Muted"), .7) { DashStyle = DashStyles.Dot };
            var boundaryX=gap.Label=="断开" ? left : x;
            if (quiet) dc.PushOpacity(.3);
            dc.DrawLine(pen, new Point(boundaryX, PlotTop(panel)), new Point(boundaryX, PlotBottom(panel)));
            if (quiet) { dc.Pop(); continue; } // Small historical holes: explain on hover, not a large permanent label.
            var caption = gap.Label == "断开" ? Loc.F($"{gap.Start.ToLocalTime():HH:mm} 起未更新") : Loc.T(gap.Label);
            var text = Text(caption, Theme.Brush("Muted"), 10);
            var tx = Math.Clamp(x - text.Width / 2, _left + 28, Math.Max(_left + 28, PlotRight-text.Width-2));
            if (tx < lastLabelRight + 6) continue;
            var box = new Rect(tx-2, PlotTop(panel)+(gap.Label=="断开" ? 22 : 2), text.Width+4, text.Height);
            obstacles.Add(box);
            dc.DrawRectangle(Theme.Brush("Bg"), null, box);
            dc.DrawText(text, new Point(tx, box.Top));
            lastLabelRight = box.Right;
        }
    }

    void DrawPeaks(DrawingContext dc, int panel, int[] indices, List<List<IReadOnlyList<TrendPoint>>> allPoints,
        Func<int, double, double> y, List<Rect> occupied)
    {
        // Use the small band above the top gridline, while staying below the summary text.
        var bounds = new Rect(_left + 1, PlotTop(panel) - 6, PlotRight - _left - 2, PlotBottom(panel) - PlotTop(panel) + 5);
        var curves = indices.SelectMany(j => allPoints[j]).Select(run =>
            run.Select(p => new Point(X(p.Time), y(panel, p.Rate))).ToArray()).ToArray();
        var candidates = indices.SelectMany(j => (_lanes[j].Peaks ??= PeakLabels.Find(allPoints[j]))
            .Where(peak=>!_lanes[j].Trend.IsProvisional(peak.Time))
            .Select((peak, rank) => (Lane: j, Peak: peak, Rank: rank))).OrderBy(c => c.Rank).ThenByDescending(c => c.Peak.Value);
        var shown = new Dictionary<int, List<double>>();
        var count = 0;
        var panelLimit = indices.Length > 1 ? ActualWidth >= 260 ? 5 : 4 : 3;
        foreach (var candidate in candidates)
        {
            if (count >= panelLimit) break;
            var j = candidate.Lane;
            var peak = candidate.Peak;
            if (!shown.TryGetValue(j, out var xs)) shown[j] = xs = [];
            var at = new Point(X(peak.Time), y(panel, peak.Value));
            if (xs.Count >= 3 || xs.Any(x => Math.Abs(x - at.X) < 36)) continue;
            var color = Theme.Brush(_lanes[j].Color);
            var label = Text(RatePresentation.Estimate(peak.Value), color, 10.5);
            label.SetFontWeight(FontWeights.SemiBold);
            var w = label.Width + 6; var h = label.Height + 2;
            Rect? placement = null;
            foreach (var (dx, dy) in new[] { (0d, -h - 5), (0d, 6d), (0d, 13d), (-15d, -h - 5), (15d, -h - 5), (-15d, 6d), (15d, 6d) })
            {
                var rect = new Rect(Math.Clamp(at.X - w / 2 + dx, bounds.Left, Math.Max(bounds.Left, bounds.Right - w)),
                    at.Y + dy, w, h);
                var padded = rect; padded.Inflate(4, 2);
                if (!bounds.Contains(rect) || occupied.Any(o => o.IntersectsWith(padded))) continue;
                // Avoid every visible stroke, including the other provider's line in this panel.
                var clearance = rect; clearance.Inflate(2, 2);
                if (curves.Any(curve => Crosses(clearance, curve))) continue;
                placement = rect; break;
            }
            if (placement is not { } box) continue;
            var anchor = new Point(Math.Clamp(at.X, box.Left + 2, box.Right - 2), at.Y > box.Bottom ? box.Bottom : box.Top);
            if (Math.Abs(box.Left + box.Width / 2 - at.X) > 6)
            {
                dc.PushOpacity(.6);
                dc.DrawLine(new Pen(color, .7), at, anchor);
                dc.Pop();
            }
            dc.PushOpacity(.87);
            dc.DrawRoundedRectangle(Theme.Brush("Bg"), null, box, 2, 2);
            dc.Pop();
            dc.DrawText(label, new Point(box.Left + 3, box.Top + 1));
            dc.DrawEllipse(color, null, at, 1.6, 1.6);
            occupied.Add(box); xs.Add(at.X); count++;
        }
    }

    static bool Crosses(Rect box, IReadOnlyList<Point> points)
    {
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i];
            if (Math.Max(a.X, b.X) < box.Left || Math.Min(a.X, b.X) > box.Right) continue;
            if (Math.Abs(b.X - a.X) < 1e-8)
            { if (Math.Max(a.Y, b.Y) >= box.Top && Math.Min(a.Y, b.Y) <= box.Bottom) return true; continue; }
            var l = Math.Max(Math.Min(a.X, b.X), box.Left); var r = Math.Min(Math.Max(a.X, b.X), box.Right);
            var y1 = a.Y + (b.Y - a.Y) * (l - a.X) / (b.X - a.X);
            var y2 = a.Y + (b.Y - a.Y) * (r - a.X) / (b.X - a.X);
            if (Math.Max(y1, y2) >= box.Top && Math.Min(y1, y2) <= box.Bottom) return true;
        }
        return false;
    }

    static StreamGeometry Geometry(IReadOnlyList<Point> pts, double? baseline = null)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(baseline is { } b ? new Point(pts[0].X, b) : pts[0], baseline is not null, baseline is not null);
            ctx.PolyLineTo(pts.ToArray(), true, false);
            if (baseline is { } bottom) ctx.LineTo(new Point(pts[^1].X, bottom), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    static List<IReadOnlyList<TrendPoint>> Points(Lane lane, ChartView v)
    {
        List<IReadOnlyList<TrendPoint>> points;
        if (v.IsCumulative(lane.Name=="Codex"))
            points = lane.Cumulative.Segments.GroupBy(s => s.Group).Select(g => (IReadOnlyList<TrendPoint>)g.SelectMany(s => new[] { new TrendPoint(s.Start, s.From), new TrendPoint(s.End, s.To) }).ToArray()).ToList();
        else if (v.Smooth) points = lane.Trend.Runs.Select(ChartPath.HardEdges).ToList();
        else points = lane.Source.Segments.Where(s => s.Valid && s.Start >= v.Start && s.End <= v.End).GroupBy(s => s.Group)
            .Select(g => (IReadOnlyList<TrendPoint>)g.SelectMany(s => new[] { new TrendPoint(s.Start, s.Rate), new TrendPoint(s.End, s.Rate) }).ToArray()).ToList();
        if(lane.Name=="Claude"&&v.MergesFable)points=FableDisplay.Omit(points,v.FableOnlySpans);
        return ChartPath.PositiveRuns(ChartPath.Clip(points,v.Start,v.End));
    }

    double? InspectValue(Lane lane, ChartView view, DateTimeOffset time) => view.IsCumulative(lane.Name=="Codex") ? lane.Cumulative.ValueAt(time) : view.Smooth ? lane.Trend.ValueAt(time)
        : lane.Source.SegmentAt(time) is { Valid: true } s && s.Start >= view.Start && s.End <= view.End ? s.Rate : null;

    sealed record InspectRow(string Name, string Value, Brush Color);
    sealed record InspectCard(Rect Bounds, Rect Plot, string Caption, List<InspectRow> Rows);
    InspectCard? BuildInspectCard(FrameworkElement surface)
    {
        var inspect = _hover ?? _pinned;
        if (!ValidInspect(inspect) || inspect is null || _view is not { } view || InspectPanel(inspect) is not { } panel || surface.ActualWidth < 20) return null;
        var time = inspect.Time;
        var cumulative=view.IsCumulative(inspect.Codex);
        var rows = new List<InspectRow>();
        foreach (var lane in _lanes.Where(l => l.Panel == panel&&!(l.Name=="Claude"&&view.FableOnlyAt(time))))
        {
            var color = Theme.Brush(lane.Color);
            var value = InspectValue(lane, view, time);
            var observed = lane.Source.SegmentAt(time);
            var gap = (lane.Name=="Codex" ? view.CodexGaps : view.ClaudeGaps).LastOrDefault(g=>time>=g.Start && time<g.End);
            var name=lane.Name=="Fable"&&view.FableToClaudeFactor is null?Loc.T("Fable 自身"):lane.Name;
            var missing = observed?.Label ?? (gap?.Label=="断开" ? "未更新" : gap?.Label) ?? "未记录";
            if (missing == "已暂停") missing = "监听切换";
            rows.Add(new(name, value is { } reading ? cumulative?Number(reading):RatePresentation.Estimate(reading) : Loc.T(missing), color));
        }
        // The window overlay can use space beyond the chart without covering the active plot
        // or intercepting the pointer. Fall back to the other side near the window edge.
        var transform = TransformToVisual(surface);
        var plot = transform.TransformBounds(new Rect(_left, PlotTop(panel), PlotRight - _left, PlotBottom(panel) - PlotTop(panel)));
        var at = transform.Transform(new Point(X(time), 0));
        var width = Math.Min(200, Math.Min(ActualWidth, surface.ActualWidth) - 8);
        var height = 32 + rows.Count * 25;
        var below = plot.Bottom + 8; var above = plot.Top - height - 8;
        var preferBelow = !inspect.Codex;
        var top = preferBelow ? below : above;
        if (top < 4 || top + height > surface.ActualHeight - 4) top = preferBelow ? above : below;
        if (top < 4 || top + height > surface.ActualHeight - 4) return null;
        var rect = new Rect(Math.Clamp(at.X + 8, 4, surface.ActualWidth - width - 4), top, width, height);
        return new(rect, plot, Clock(time) + (cumulative ? Loc.T(" · 累计点") : view.Smooth ? Loc.T(" · 估计点/h") : Loc.T(" · 点/h")), rows);
    }
    internal void DrawInspectCard(DrawingContext dc, FrameworkElement surface)
    {
        if (BuildInspectCard(surface) is not { } card) return;
        var rect = card.Bounds;
        dc.DrawRoundedRectangle(Theme.Brush("Bg"), new Pen(Theme.Brush("Line"), 1), rect, 5, 5);
        Label(dc, card.Caption, Theme.Brush("Muted"), rect.Left + 9, rect.Top + 6, 11);
        var rowY = rect.Top + 29;
        foreach (var row in card.Rows)
        {
            Label(dc, row.Name, row.Color, rect.Left + 9, rowY, 12);
            Label(dc, row.Value, row.Color, rect.Right - 9, rowY - 1, 14, TextAlignment.Right);
            rowY += 25;
        }
    }

    public static (double Top, double Step) Nice(double max, int count)
    {
        var scale = ChartScale.Create(max, count);
        return (scale.Top, scale.Step);
    }
}

/// <summary>Top window layer for chart readings; never takes mouse input or layout space.</summary>
public sealed class ChartInspectOverlay : FrameworkElement
{
    internal RateChart? Chart { get; set; }
    public ChartInspectOverlay() { IsHitTestVisible = false; Focusable = false; }
    protected override void OnRender(DrawingContext dc) => Chart?.DrawInspectCard(dc, this);
}

static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject o, string name) => System.Windows.Automation.AutomationProperties.SetName(o, name);
}
