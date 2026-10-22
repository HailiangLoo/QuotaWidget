using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QuotaWidget.Core;

namespace QuotaWidget.App;

/// <summary>Short-range tabs and one shared long-range menu for charts and usage cards.</summary>
public sealed class RangePicker : UniformGrid
{
    public event Action<int>? Selected;
    public ToggleButton More { get; } = new();
    int _selected = 300;

    public RangePicker()
    {
        Columns = 6;
        foreach (var range in WidgetSettings.RangeChoices.Where(m => m > 0 && m <= 4320))
        {
            var tab = new ToggleButton { Content = WidgetModel.RangeLabel(range), Tag = range, Height = 24 };
            tab.SetResourceReference(StyleProperty, "TabButton");
            tab.Click += (_, _) => { tab.IsChecked = range == _selected; if (range != _selected) Selected?.Invoke(range); };
            Children.Add(tab);
        }
        More.Height = 24;
        More.SetResourceReference(StyleProperty, "TabButton");
        var menu = new ContextMenu { PlacementTarget = More, Placement = PlacementMode.Bottom };
        menu.SetResourceReference(StyleProperty, "RangeMenu");
        menu.SetResourceReference(Control.BackgroundProperty, "Bg");
        menu.SetResourceReference(Control.ForegroundProperty, "Ink");
        foreach (var range in new[] { ChartRanges.Week, ChartRanges.Month, ChartRanges.All })
        {
            var item = new MenuItem { Header = WidgetModel.RangeLabel(range), Tag = range, IsCheckable = true };
            item.SetResourceReference(StyleProperty, "RangeMenuItem");
            item.Click += (_, _) => { item.IsChecked = range == _selected; if (range != _selected) Selected?.Invoke(range); };
            menu.Items.Add(item);
        }
        More.ContextMenu = menu;
        More.Click += (_, _) => { More.IsChecked = LongRange; menu.IsOpen = true; };
        menu.Closed += (_, _) => More.IsChecked = LongRange;
        Children.Add(More);
        Configure(WidgetSettings.RangeChoices, _selected);
    }

    bool LongRange => _selected == ChartRanges.All || _selected > 4320;

    public void Configure(IEnumerable<int> available, int selected)
    {
        _selected = selected;
        var choices = available.ToHashSet();
        foreach (var tab in Children.OfType<ToggleButton>().Where(b => b != More))
        {
            var range = (int)tab.Tag;
            tab.IsChecked = range == selected;
            tab.Visibility = choices.Contains(range) ? Visibility.Visible : Visibility.Collapsed;
            tab.ToolTip = ChartRanges.Label(range);
        }
        More.Content = LongRange ? WidgetModel.RangeLabel(selected) + " ⌄" : "··· ⌄";
        More.ToolTip = LongRange ? ChartRanges.Label(selected) : Loc.T("更多时间范围");
        System.Windows.Automation.AutomationProperties.SetName(More, Loc.T("更多时间范围"));
        More.IsChecked = LongRange;
        foreach (MenuItem item in More.ContextMenu.Items)
        {
            var range = (int)item.Tag;
            item.IsChecked = range == selected;
            item.IsEnabled = choices.Contains(range);
            item.ToolTip = ChartRanges.Label(range);
        }
        Columns = Children.OfType<FrameworkElement>().Count(c => c.Visibility == Visibility.Visible);
    }
}
