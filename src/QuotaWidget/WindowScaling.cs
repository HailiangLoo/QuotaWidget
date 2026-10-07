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
    bool _scaleDragging, _scaleFitPending;
    double _scaleStart;
    Point _scalePointerStart;
    Size _scaleBase;

    void InitializeScaling()
    {
        Loaded += (_, _) => QueueScaleFit();
        IsVisibleChanged += (_, _) => { if (IsVisible) QueueScaleFit(); };
        Outer.SizeChanged += (_, _) => QueueScaleFit();
    }

    void ApplyWidgetScale()
    {
        var s = _model.Settings;
        Outer.Width = s.CompactMode && SettingsPanel.Visibility != Visibility.Visible ? 240 : s.Width;
        WidgetScale.ScaleX = WidgetScale.ScaleY = s.UiScale;
        Width = Outer.Width * s.UiScale + 2 * ShadowMargin;
    }

    // Project the pointer movement onto the original diagonal. Either horizontal
    // or vertical movement works, without changing the logical layout or aspect.
    static double DragScale(double start, Size basis, double dx, double dy) =>
        start + (dx * basis.Width + dy * basis.Height) /
        Math.Max(1, basis.Width * basis.Width + basis.Height * basis.Height);

    Size NaturalSize()
    {
        // A HWND can cap the arranged height before Loaded runs. Fit against
        // the complete content, never against that already-clipped rectangle.
        Frame.Measure(new Size(Outer.Width, double.PositiveInfinity));
        return new Size(Outer.Width, Frame.DesiredSize.Height);
    }

    double LimitScale(double requested)
    {
        var max = WidgetSettings.MaxUiScale;
        if (_opts.Snapshot is null && IsLoaded)
        {
            var size = NaturalSize();
            var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            max = Math.Min(max, Math.Min((area.Width / dpi.DpiScaleX - 2 * ShadowMargin) / Outer.Width,
                (area.Height / dpi.DpiScaleY - 2 * ShadowMargin) / Math.Max(1, size.Height)));
        }
        return Math.Clamp(requested, WidgetSettings.MinUiScale, Math.Max(WidgetSettings.MinUiScale, max));
    }

    void ScaleGrip_DragStarted(object sender, DragStartedEventArgs e)
    {
        CloseUsage();
        ReleaseChart();
        _scaleDragging = true;
        _scaleStart = _model.Settings.UiScale;
        _scaleBase = NaturalSize();
        _scalePointerStart = Mouse.GetPosition(this);
    }

    void ScaleGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromPoint(Mouse.GetPosition(this));
    }

    void ResizeFromPoint(Point pointer)
    {
        if (!_scaleDragging) return;
        // Read from the fixed window origin, not Thumb's moving local origin.
        // Accumulating its deltas would stick at a limit after overshooting.
        var delta = pointer - _scalePointerStart;
        _model.Settings.UiScale = LimitScale(DragScale(_scaleStart, _scaleBase, delta.X, delta.Y));
        ApplyWidgetScale();
    }

    void ScaleGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_scaleDragging) return;
        _scaleDragging = false;
        if (e.Canceled) { _model.Settings.UiScale = _scaleStart; ApplyWidgetScale(); }
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
        if (_opts.Snapshot is not null || !IsLoaded || _scaleDragging || _scaleFitPending) return;
        _scaleFitPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scaleFitPending = false;
            if (!IsVisible || _scaleDragging) return;
            UpdateLayout();
            var scale = LimitScale(_model.Settings.UiScale);
            if (Math.Abs(scale - _model.Settings.UiScale) > .0001)
            {
                _model.Settings.UiScale = scale;
                ApplyWidgetScale();
                UpdateLayout();
            }
            NativePlacement.FitToWorkArea(new WindowInteropHelper(this).Handle);
            PersistPlacement();
        });
    }
}
