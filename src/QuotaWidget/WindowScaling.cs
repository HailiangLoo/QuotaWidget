using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public partial class MainWindow
{
    bool _resizeDragging, _resizeCompact, _scaleFitPending;
    string _resizeEdges = "";
    Point _resizePointerStart;
    Rect _resizeBounds;
    double _resizeWidth;
    double? _resizeHeight;

    bool CompactGeometry => _model.Settings.CompactMode && SettingsPanel.Visibility != Visibility.Visible;
    double LayoutWidth => CompactGeometry ? _model.Settings.CompactWidth : _model.Settings.Width;
    double? LayoutHeight => CompactGeometry ? _model.Settings.CompactHeight : _model.Settings.Height;

    void InitializeScaling()
    {
        Loaded += (_, _) => QueueScaleFit();
        IsVisibleChanged += (_, _) => { if (IsVisible) QueueScaleFit(); };
        Outer.SizeChanged += (_, _) => QueueScaleFit();
        BodyViewport.ScrollChanged += (_, _) => FitChartHeight();
        BodyContent.SizeChanged += (_, _) => FitChartHeight();
    }

    void ApplyWidgetScale()
    {
        var scale = _model.Settings.UiScale;
        Outer.Width = LayoutWidth;
        Outer.Height = LayoutHeight ?? double.NaN;
        // Preserve older font-scale preferences, but resizing never changes them.
        WidgetScale.ScaleX = WidgetScale.ScaleY = scale;
        SizeToContent = LayoutHeight is null ? SizeToContent.Height : SizeToContent.Manual;
        Width = LayoutWidth * scale + 2 * ShadowMargin;
        if (LayoutHeight is {} height) Height = height * scale + 2 * ShadowMargin;
        BodyViewport.MaxHeight = SettingsPanel.Visibility == Visibility.Visible ? 560 : double.PositiveInfinity;
        FitChartHeight();
    }

    void FitChartHeight()
    {
        // Resizing only changes geometry: keep the view, archive and pinned readout.
        var fit = LayoutHeight is not null && !CompactGeometry &&
            MainPanel.Visibility == Visibility.Visible && ChartPanel.Visibility == Visibility.Visible;
        Chart.FitHeight(fit && BodyViewport.ViewportHeight > 0
            ? BodyViewport.ViewportHeight - (BodyContent.DesiredSize.Height - Chart.DesiredSize.Height)
            : null);
    }

    Point ResizePointer()
    {
        var point = Mouse.GetPosition(this);
        return new WindowInteropHelper(this).Handle != IntPtr.Zero ? PointToScreen(point) : point;
    }

    void ResizeGrip_DragStarted(object sender, DragStartedEventArgs e)
    {
        CloseUsage();
        _resizeDragging = true;
        _resizeEdges = (string)((Thumb)sender).Tag;
        _resizeCompact = CompactGeometry;
        _resizeWidth = LayoutWidth;
        _resizeHeight = LayoutHeight;
        _resizePointerStart = ResizePointer();
        var dpi = VisualTreeHelper.GetDpi(this);
        _resizeBounds = NativePlacement.Bounds(new WindowInteropHelper(this).Handle) ??
            new Rect(0, 0, Width * dpi.DpiScaleX,
                (Outer.ActualHeight * _model.Settings.UiScale + 2 * ShadowMargin) * dpi.DpiScaleY);
    }

    // Physical screen coordinates keep the opposite edge fixed as the origin moves.
    // Derive from the start so reversing after overshooting a limit works immediately.
    static Rect ResizeBounds(Rect start, Vector delta, string edges, Size min, Size max)
    {
        var left = edges.Contains('L'); var top = edges.Contains('T');
        var width = left || edges.Contains('R')
            ? Math.Clamp(start.Width + (left ? -delta.X : delta.X), min.Width, Math.Max(min.Width, max.Width)) : start.Width;
        var height = top || edges.Contains('B')
            ? Math.Clamp(start.Height + (top ? -delta.Y : delta.Y), min.Height, Math.Max(min.Height, max.Height)) : start.Height;
        return new Rect(left ? start.Right - width : start.Left, top ? start.Bottom - height : start.Top, width, height);
    }

    void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e) => ResizeFromPoint(ResizePointer());

    void ResizeFromPoint(Point pointer)
    {
        if (!_resizeDragging) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = _model.Settings.UiScale;
        Size Pixels(double w, double h) => new((w * scale + 2 * ShadowMargin) * dpi.DpiScaleX,
            (h * scale + 2 * ShadowMargin) * dpi.DpiScaleY);
        var max = Pixels(WidgetSettings.MaxWidth, WidgetSettings.MaxHeight);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (_opts.Snapshot is null && hwnd != IntPtr.Zero)
        {
            var area = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
            max = new Size(Math.Min(max.Width, area.Width), Math.Min(max.Height, area.Height));
        }
        var bounds = ResizeBounds(_resizeBounds, pointer - _resizePointerStart, _resizeEdges,
            Pixels(WidgetSettings.MinWidth, WidgetSettings.MinHeight), max);
        SetLayoutSize(_resizeCompact, (bounds.Width / dpi.DpiScaleX - 2 * ShadowMargin) / scale,
            (bounds.Height / dpi.DpiScaleY - 2 * ShadowMargin) / scale);
        ApplyPlatformLayout();
        ApplyWidgetScale();
        if (hwnd != IntPtr.Zero) NativePlacement.Move(hwnd, (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y));
    }

    void SetLayoutSize(bool compact, double width, double? height)
    {
        if (compact) { _model.Settings.CompactWidth = width; _model.Settings.CompactHeight = height; }
        else { _model.Settings.Width = width; _model.Settings.Height = height; }
    }

    void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_resizeDragging) return;
        if (e.Canceled)
        {
            SetLayoutSize(_resizeCompact, _resizeWidth, _resizeHeight);
            ApplyPlatformLayout();
            ApplyWidgetScale();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) NativePlacement.Move(hwnd, (int)Math.Round(_resizeBounds.X), (int)Math.Round(_resizeBounds.Y));
        }
        _resizeDragging = false;
        _model.SaveSettings();
        QueueScaleFit();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        QueueScaleFit();
    }

    void QueueScaleFit()
    {
        if (_opts.Snapshot is not null || !IsLoaded || _resizeDragging || _scaleFitPending) return;
        _scaleFitPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scaleFitPending = false;
            if (!IsVisible || _resizeDragging) return;
            UpdateLayout();
            var hwnd = new WindowInteropHelper(this).Handle;
            var area = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            var scale = _model.Settings.UiScale;
            var maxWidth = Math.Max(WidgetSettings.MinWidth, (area.Width / dpi.DpiScaleX - 2 * ShadowMargin) / scale);
            var maxHeight = Math.Max(WidgetSettings.MinHeight, (area.Height / dpi.DpiScaleY - 2 * ShadowMargin) / scale);
            var height = LayoutHeight;
            if (Outer.ActualHeight > maxHeight + 1) height = maxHeight;
            if (LayoutWidth > maxWidth || height != LayoutHeight)
            {
                SetLayoutSize(CompactGeometry, Math.Min(LayoutWidth, maxWidth), height);
                ApplyWidgetScale();
                UpdateLayout();
                _model.SaveSettings();
            }
            NativePlacement.FitToWorkArea(hwnd);
            PersistPlacement();
        });
    }
}
