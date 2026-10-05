using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using QuotaWidget.Core;

namespace QuotaWidget.App;

/// <summary>One quota bar: label and value, a 4px track, remaining and reset countdown.</summary>
public sealed class MeterRow : StackPanel
{
    readonly TextBlock _label = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Bottom };
    readonly TextBlock _value = new() { FontSize = 18, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Bottom, LineHeight = 18, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
    readonly TextBlock _other = new() { FontSize = 11 };
    readonly TextBlock _reset = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
    readonly Border _fill = new() { CornerRadius = new CornerRadius(2) };
    readonly ColumnDefinition _filled = new() { Width = new GridLength(0, GridUnitType.Star) };
    readonly ColumnDefinition _rest = new() { Width = new GridLength(100, GridUnitType.Star) };
    readonly Border _track;

    public MeterRow()
    {
        Margin = new Thickness(0, 10, 0, 9);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 7), LastChildFill = true };
        DockPanel.SetDock(_value, Dock.Right);
        top.Children.Add(_value);
        top.Children.Add(_label);
        Children.Add(top);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(_filled);
        grid.ColumnDefinitions.Add(_rest);
        grid.Children.Add(_fill);
        _track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Child = grid, ClipToBounds = true };
        _track.SetResourceReference(Border.BackgroundProperty, "Track");
        AutomationProperties.SetName(_track, "额度进度");
        Children.Add(_track);

        var bottom = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
        DockPanel.SetDock(_reset, Dock.Right);
        bottom.Children.Add(_reset);
        bottom.Children.Add(_other);
        _other.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _reset.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Children.Add(bottom);
        Loaded += (_, _) => _fill.SetResourceReference(Border.BackgroundProperty, IsFable ? "Orange" : "Blue");
    }

    public bool IsFable { get; set; }

    public void Show(MeterView m)
    {
        _label.Text = m.Label;
        _value.Text = m.Value;
        _other.Text = m.Other;
        _reset.Text = m.Reset;
        var v = m.Missing ? 0 : m.Fill;
        _filled.Width = new GridLength(v, GridUnitType.Star);
        _rest.Width = new GridLength(100 - v, GridUnitType.Star);
        _fill.Visibility = v > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_track, $"{m.Label} {m.Value}");
    }
}
