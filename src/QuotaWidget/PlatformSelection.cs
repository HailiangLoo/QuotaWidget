using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public partial class MainWindow
{
    int[] AvailableRanges()=>ChartRanges.Available(_model.Settings.Monitors(ChatPlatform.Claude)?_model.Bounds:null,_model.Settings.Monitors(ChatPlatform.Codex)?_codex.Bounds:null);
    void ApplyPlatformLayout()
    {
        var s=_model.Settings;var claude=s.Monitors(ChatPlatform.Claude);var codex=s.Monitors(ChatPlatform.Codex);
        ClaudeLoginButton.IsEnabled=ClaudeLogoutButton.IsEnabled=_app.CollectorAvailable&&claude;
        Visibility Show(bool value)=>value?Visibility.Visible:Visibility.Collapsed;
        var codexOnly=codex&&!claude;
        var limits=_codex.Records.LastOrDefault()?.Snapshot.Limits;
        var five=codex&&limits?.FiveHour is not null;
        var week=codex&&(!five||limits?.AllWeek is not null);
        var inline=codexOnly&&s.CompactMode&&!five;
        var claudeInline=claude&&!codex&&s.CompactMode;
        var codexRateInline=codexOnly&&s.CompactMode&&five;
        CodexMeter.SingleProvider=codexOnly&&!five;
        CodexMeter.ShowPlatformIcon=!five;
        CodexCompactSummary.Visibility=Show(inline);
        MeterGrid.Visibility=Show(!inline);
        CompactRates.Visibility=Show(!inline&&!claudeInline&&!codexRateInline);
        // Reuse the same rate control so its values and usage-card interactions
        // remain identical when moving between full, single and dual-provider views.
        var rateParent=claudeInline?MeterGrid:CompactRates;
        if(CompactClaudeRate.Parent!=rateParent)
        {
            ((Panel)CompactClaudeRate.Parent).Children.Remove(CompactClaudeRate);
            rateParent.Children.Add(CompactClaudeRate);
        }
        Grid.SetColumn(CompactClaudeRate,claudeInline?3:0);
        CompactClaudeRate.Margin=claudeInline?new Thickness(3,0,2,0):new Thickness(0,0,4,0);
        CompactClaudeRateContent.Orientation=claudeInline?Orientation.Vertical:Orientation.Horizontal;
        CompactClaudeRateBox.HorizontalAlignment=claudeInline?HorizontalAlignment.Center:HorizontalAlignment.Left;
        CompactFableRate.Margin=claudeInline?new Thickness(0,2,0,0):new Thickness(6,0,0,0);
        var codexRateParent=codexRateInline?MeterGrid:CompactRates;
        if(CompactCodexRate.Parent!=codexRateParent)
        {
            ((Panel)CompactCodexRate.Parent).Children.Remove(CompactCodexRate);
            codexRateParent.Children.Add(CompactCodexRate);
        }
        Grid.SetColumn(CompactCodexRate,codexRateInline?5:1);
        CompactCodexRate.Margin=codexRateInline?new Thickness(3,0,2,0):new Thickness(4,0,0,0);
        CompactCodexRateBox.HorizontalAlignment=codexRateInline?HorizontalAlignment.Center:HorizontalAlignment.Right;
        void RateLayout(bool single,StackPanel row,TextBlock heading,TextBlock value,TextBlock unit,TextBlock arrow,string platform,string accent)
        {
            row.Orientation=single?Orientation.Vertical:Orientation.Horizontal;
            heading.Text=single?Loc.T("近1h"):platform;
            heading.FontSize=single?9:10;heading.Foreground=Theme.Brush(single?"Muted":accent);
            heading.Margin=single?new Thickness(0):new Thickness(0,0,3,0);
            heading.HorizontalAlignment=value.HorizontalAlignment=single?HorizontalAlignment.Center:HorizontalAlignment.Stretch;
            value.FontSize=single?16:12;
            unit.Visibility=arrow.Visibility=Show(!single);
        }
        RateLayout(claudeInline,CompactClaudePrimaryRate,CompactClaudeRateHeading,CompactClaudeRateValue,CompactClaudeRateUnit,CompactClaudeRateArrow,"Claude","Claude");
        RateLayout(codexOnly&&s.CompactMode,CompactCodexPrimaryRate,CompactCodexRateHeading,CompactCodexRateValue,CompactCodexRateUnit,CompactCodexRateArrow,"Codex","Violet");
        foreach(var meter in new[]{FiveMeter,WeekMeter,FableMeter}) meter.Visibility=Show(claude);
        CodexFiveMeter.Visibility=Show(five);CodexMeter.Visibility=Show(week);
        // Give each provider a row when five quotas would squeeze the compact or
        // narrow full view. Accounts without 5h retain their existing geometry.
        var stacked=(s.CompactMode||s.Width<280)&&claude&&five;
        var columns=stacked?6:4+(five?1:0)+(codexRateInline?1:0);
        if(MeterGrid.ColumnDefinitions.Count!=columns)
        {MeterGrid.ColumnDefinitions.Clear();for(var i=0;i<columns;i++)MeterGrid.ColumnDefinitions.Add(new());}
        if(MeterGrid.RowDefinitions.Count!=(stacked?2:0))
        {MeterGrid.RowDefinitions.Clear();if(stacked){MeterGrid.RowDefinitions.Add(new(){Height=GridLength.Auto});MeterGrid.RowDefinitions.Add(new(){Height=GridLength.Auto});}}
        var meters=new[]{FiveMeter,WeekMeter,FableMeter,CodexFiveMeter,CodexMeter};
        foreach(var meter in meters){Grid.SetRow(meter,0);Grid.SetColumnSpan(meter,1);}
        if(stacked)
        {
            for(var i=0;i<3;i++){Grid.SetColumn(meters[i],i*2);Grid.SetColumnSpan(meters[i],2);}
            Grid.SetRow(CodexFiveMeter,1);Grid.SetColumn(CodexFiveMeter,0);Grid.SetColumnSpan(CodexFiveMeter,week?3:6);
            Grid.SetRow(CodexMeter,1);Grid.SetColumn(CodexMeter,3);Grid.SetColumnSpan(CodexMeter,3);
            foreach(var column in MeterGrid.ColumnDefinitions)column.Width=new(1,GridUnitType.Star);
        }
        else
        {
            for(var i=0;i<3;i++)Grid.SetColumn(meters[i],i);
            Grid.SetColumn(CodexFiveMeter,3);Grid.SetColumn(CodexMeter,five?4:3);
            for(var i=0;i<columns;i++)MeterGrid.ColumnDefinitions[i].Width=new((i<3?claude:i==3&&five?five:week)?1:0,GridUnitType.Star);
            if(claudeInline)
                for(var i=0;i<4;i++)MeterGrid.ColumnDefinitions[i].Width=new(i<3?1:.85,GridUnitType.Star);
            if(codexRateInline)MeterGrid.ColumnDefinitions[5].Width=new(.85,GridUnitType.Star);
        }
        Grid.SetColumn(MeterDivider,3);
        MeterDivider.Visibility=Show(claude&&codex&&!stacked);
        TotalLegend.Visibility=FableLegend.Visibility=Show(claude);CodexLegend.Visibility=Show(codex);
        CompactClaudeRate.Visibility=Show(claude);CompactCodexRate.Visibility=Show(codex);
        CompactRates.ColumnDefinitions[0].Width=new GridLength(claude?1:0,GridUnitType.Star);
        CompactRates.ColumnDefinitions[1].Width=new GridLength(codex?1:0,GridUnitType.Star);
    }

    void MonitoringCombo_PreviewMouseWheel(object sender,MouseWheelEventArgs e)
    {
        if (!MonitoringCombo.IsDropDownOpen) e.Handled=true;
    }

    void MonitoringCombo_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_suppress||MonitoringCombo.SelectedItem is not ComboBoxItem {Tag:string selected}) return;
        var previous=_model.Settings.Monitoring;
        if(previous==selected) return;
        _model.Settings.Monitoring=selected;_model.Settings.Normalize();
        CloseUsage();
        foreach(var entry in _usageWindows.ToArray()) if(!_model.Settings.Monitors(entry.Key)) entry.Value.Close();
        _app.DisplayModeChanged();
        ApplyPlatformLayout();Chart.ClearInspect();SaveAndRender();
    }
}
