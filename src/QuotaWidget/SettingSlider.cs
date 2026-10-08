using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using QuotaWidget.Core;

namespace QuotaWidget.App;

/// <summary>A labelled, discrete WPF slider. Drag previews locally; release commits once.</summary>
public sealed class SettingSlider : StackPanel
{
    readonly TextBlock _title = new(), _value = new() { FontWeight = FontWeights.SemiBold };
    readonly Canvas _marks = new() { IsHitTestVisible = false };
    readonly List<TextBlock> _labels = [];
    readonly List<Ellipse> _dots = [];
    int[] _choices = [];
    string _caption = "";
    bool _seconds, _off, _configuring, _dragging;
    int _dragStart;
    public Slider Input { get; } = new()
    {
        Minimum = 0, Maximum = 3, TickFrequency = 1, IsSnapToTickEnabled = true,
        SmallChange = 1, LargeChange = 1, IsMoveToPointEnabled = true, Height = 24,
        VerticalAlignment = VerticalAlignment.Top
    };
    public int Selected
    {
        get => _choices.Length == 0 ? 0 : _choices[Math.Clamp((int)Math.Round(Input.Value), 0, _choices.Length - 1)];
        set { var index = Array.IndexOf(_choices, value); if (index >= 0) Input.Value = index; }
    }
    public event EventHandler? SelectionChanged;

    public SettingSlider()
    {
        _value.SetResourceReference(TextBlock.ForegroundProperty, "Blue");
        var header = new DockPanel(); DockPanel.SetDock(_value, Dock.Right);
        header.Children.Add(_value); header.Children.Add(_title); Children.Add(header);
        var axis = new Grid { Height = 44, Margin = new Thickness(0, 3, 0, 0) };
        Input.SetResourceReference(StyleProperty, "SettingSliderInput");
        axis.Children.Add(Input); axis.Children.Add(_marks); Children.Add(axis);
        axis.SizeChanged += (_, _) => PlaceMarks();
        Input.ValueChanged += (_, _) =>
        {
            if (_configuring) return;
            RefreshLanguage();
            if (!_dragging) SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        Input.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
        { _dragStart = Selected; _dragging = true; }));
        Input.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) =>
        {
            if (e.Canceled) Selected = _dragStart;
            _dragging = false;
            if (Selected != _dragStart) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }));
    }

    public void Configure(string caption, IEnumerable<int> choices, int selected, bool seconds = false, bool off = false)
    {
        _configuring = true; _caption = caption; _seconds = seconds; _off = off;
        // Preserve custom polling cadences from older settings without rounding them away.
        _choices = choices.Append(selected).Distinct().Order().ToArray();
        Input.Maximum = _choices.Length - 1; Selected = selected;
        _marks.Children.Clear(); _labels.Clear(); _dots.Clear();
        foreach (var choice in _choices)
        {
            var dot = new Ellipse { Width = 4, Height = 4 };
            var label = new TextBlock { FontSize = 10 };
            _marks.Children.Add(dot); _marks.Children.Add(label); _dots.Add(dot); _labels.Add(label);
        }
        RefreshLanguage(); _configuring = false;
    }

    public void RefreshLanguage()
    {
        if (_choices.Length == 0) return;
        string Number(int choice) => (_seconds ? choice / 60d : choice).ToString("0.#", CultureInfo.InvariantCulture);
        _title.Text = Loc.T(_caption);
        _value.Text = _off && Selected == 0 ? Loc.T("关闭") : Loc.F($"{(_seconds ? Selected / 60d : Selected):0.#} 分钟");
        AutomationProperties.SetName(Input, _title.Text + " · " + _value.Text);
        for (var i = 0; i < _labels.Count; i++)
        {
            _labels[i].Text = _off && _choices[i] == 0 ? Loc.T("关闭") : Number(_choices[i]);
            var color = _choices[i] == Selected ? "Blue" : "Muted";
            _labels[i].SetResourceReference(TextBlock.ForegroundProperty, color);
            _dots[i].SetResourceReference(Shape.FillProperty, color);
        }
        PlaceMarks();
    }

    void PlaceMarks()
    {
        var width = Input.ActualWidth;
        for (var i = 0; i < _labels.Count; i++)
        {
            var x = 7 + Math.Max(0, width - 14) * i / Math.Max(1, _choices.Length - 1);
            Canvas.SetLeft(_dots[i], x - 2); Canvas.SetTop(_dots[i], 10);
            var label = _labels[i]; label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(x - label.DesiredSize.Width / 2, 0, Math.Max(0, width - label.DesiredSize.Width)));
            Canvas.SetTop(label, 25);
        }
    }
}
