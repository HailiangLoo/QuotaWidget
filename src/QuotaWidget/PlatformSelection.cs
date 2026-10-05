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
        CodexMeter.SingleProvider=codexOnly&&!five;
        CodexMeter.ShowPlatformIcon=!five;
        CodexCompactSummary.Visibility=Show(inline);
        MeterGrid.Visibility=Show(!inline);
        CompactRates.Visibility=Show(!inline);
        foreach(var meter in new[]{FiveMeter,WeekMeter,FableMeter}) meter.Visibility=Show(claude);
        CodexFiveMeter.Visibility=Show(five);CodexMeter.Visibility=Show(week);
        // Give each provider a row when five quotas would squeeze the compact or
        // narrow full view. Accounts without 5h retain their existing geometry.
        var stacked=(s.CompactMode||s.Width<280)&&claude&&five;
        var columns=stacked?6:4+(five?1:0);
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
        _app.MonitoringChanged(previous);
        ApplyPlatformLayout();Chart.ClearInspect();SaveAndRender();
    }
}
