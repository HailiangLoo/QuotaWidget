using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using QuotaWidget.Core;

namespace QuotaWidget.App;

/// <summary>A frozen local-usage snapshot with both complete groupings visible together.</summary>
public sealed class PlatformUsageCard : Border
{
    readonly TokenBreakdown _data;
    readonly IReadOnlyDictionary<string,ChatCacheEntry> _names;
    public StackPanel ModelRows {get;}=new();
    public StackPanel ChatRows {get;}=new();
    public ScrollViewer TableScroll {get;}
    public UniformGrid RangeTabs {get;}=new(){Columns=6,Margin=new Thickness(0,9,0,1)};
    readonly string _accent;
    Action _refresh,_close;
    Action<int>? _selectRange;
    readonly TextBlock _mode;
    readonly TextBlock _quotaNote=Text(Loc.T("额度分摊 · 正在校准本机记录"),10,"Muted");
    readonly List<(TextBlock Cell,string? Chat,string Model)> _quotaCells=new();
    public ChatQuotaEstimate? QuotaEstimate {get;private set;}
    public TokenBreakdown Data=>_data;
    public ChatPlatform Platform {get;}
    public int Minutes {get;}
    public FrameworkElement DragHandle {get;}
    public bool IsPinned {get;private set;}
    public void SetActions(Action refresh,Action close,Action<int>? selectRange=null){_refresh=refresh;_close=close;_selectRange=selectRange;}
    public void SetPinned(bool value){IsPinned=value;_mode.Text=value?Loc.T("已固定 · 拖动窗口"):Loc.T("点击卡片固定");_mode.Foreground=Theme.Brush(value?_accent:"Muted");}
    public bool IsCommandHit(DependencyObject? source)
    {
        while(source is not null&&source!=this)
        {
            if(source is ButtonBase) return true;
            source=source is Visual?VisualTreeHelper.GetParent(source):LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    static Style ResourceStyle(string key)=>(Style)Application.Current.FindResource(key);
    static TextBlock Text(string value,double size=11,string color="Ink")=>new(){Text=value,FontSize=size,Foreground=Theme.Brush(color),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
    static Grid Columns()
    {
        var g=new Grid();g.ColumnDefinitions.Add(new ColumnDefinition());
        for(var i=0;i<3;i++) g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(75)});
        g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(70)});
        return g;
    }
    public void ShowQuotaEstimate(ChatQuotaEstimate estimate)
    {
        QuotaEstimate=estimate;
        _quotaNote.Text=estimate.Synthetic?Loc.F($"额度分摊 · {estimate.Observed:0.0} 点 · 演示估算"):
            estimate.Available?Loc.F($"额度分摊 · {estimate.Observed:0.0} 点 · 估算"):Loc.T("额度分摊 · ")+Loc.T(estimate.Reason);
        _quotaNote.ToolTip=estimate.Synthetic?Loc.T("合成演示数值，不参与实际额度校准。"):Loc.T("仅分摊完整观测区间。本机以外的消耗无法识别；估算不是官方账单。")+
            (estimate.Available?Loc.F($"\n校准 {estimate.CalibrationHours} 个时段 · 验证平均误差 {estimate.ValidationError:0.0} 点/时段"):"");
        foreach(var (cell,chat,model) in _quotaCells)
        {
            var shares=estimate.Shares.Where(s=>s.Model==model&&(chat is null||s.Chat==chat)).ToArray();
            cell.Text=estimate.Available&&shares.Length>0?shares.Sum(s=>s.Points).ToString("0.0"):"—";
            cell.ToolTip=_quotaNote.ToolTip;
        }
    }
    public PlatformUsageCard(ChatPlatform platform,TokenBreakdown data,IReadOnlyDictionary<string,ChatCacheEntry> names,
        int minutes,Action refresh,Action close,string coverage,string? rateNote=null,Action<int>? selectRange=null)
    {
        _data=data;_names=names;Platform=platform;Minutes=minutes;_accent=platform==ChatPlatform.Claude?"Claude":"Violet";_refresh=refresh;_close=close;_selectRange=selectRange;
        Width=730;Padding=new Thickness(13,11,13,10);CornerRadius=new CornerRadius(9);Background=Theme.Brush("Bg");BorderBrush=Theme.Brush("Line");BorderThickness=new Thickness(1);
        TextElement.SetFontFamily(this,new FontFamily("Segoe UI, Microsoft YaHei UI"));
        var body=new StackPanel();Child=body;
        var header=new DockPanel{Background=Brushes.Transparent};DragHandle=header;
        var exit=new Button{Content="×",Style=ResourceStyle("FlatButton"),Width=23,Height=24,Padding=new Thickness(0),FontSize=18,ToolTip=Loc.T("关闭明细")};exit.Click+=(_,_)=>_close();DockPanel.SetDock(exit,Dock.Right);header.Children.Add(exit);
        var reload=new Button{Content=Loc.T("刷新"),FontSize=10,Style=ResourceStyle("FlatButton"),Padding=new Thickness(5,3,5,3),Margin=new Thickness(0,0,5,0),ToolTip=Loc.T("刷新本地记录")};reload.Click+=(_,_)=>_refresh();DockPanel.SetDock(reload,Dock.Right);header.Children.Add(reload);
        _mode=Text(Loc.T("点击卡片固定"),10,"Muted");_mode.Margin=new Thickness(0,0,9,0);DockPanel.SetDock(_mode,Dock.Right);header.Children.Add(_mode);
        var title=Text(platform+Loc.T(" 用量"),16,_accent);title.FontWeight=FontWeights.SemiBold;header.Children.Add(title);body.Children.Add(header);
        foreach(var range in WidgetSettings.RangeChoices)
        {
            var tab=new ToggleButton{Content=range==0?"all":WidgetModel.RangeLabel(range),Tag=range,
                IsChecked=range==minutes,Style=ResourceStyle("TabButton"),Height=24,ToolTip=ChartRanges.Label(range)};
            tab.Click+=(_,_)=>{tab.IsChecked=range==Minutes;if(range!=Minutes)_selectRange?.Invoke(range);};
            RangeTabs.Children.Add(tab);
        }
        body.Children.Add(RangeTabs);
        var period=Text(Loc.F($"{data.Start.ToLocalTime():M/d HH:mm} – {data.End.ToLocalTime():M/d HH:mm:ss} · {data.Total.Requests:N0} 请求"),10,"Muted");period.Margin=new Thickness(0,5,0,0);body.Children.Add(period);
        if(!string.IsNullOrEmpty(rateNote)){var rate=Text(rateNote,10,"Muted");rate.Margin=new Thickness(0,4,0,0);body.Children.Add(rate);}
        _quotaNote.Margin=new Thickness(0,4,0,0);body.Children.Add(_quotaNote);
        var summary=new UniformGrid{Columns=3,Margin=new Thickness(0,13,0,12)};
        foreach(var (label,number) in new[]{("IN",data.Total.Input),("CACHE",data.Total.Cached),("OUT",data.Total.Output)})
        {
            var cell=new StackPanel();cell.Children.Add(Text(label,10,"Muted"));
            var value=Text(data.Total.Requests==0?"—":TokenSummary.Number(number),23);value.FontWeight=FontWeights.SemiBold;value.ToolTip=$"{number:N0} token";cell.Children.Add(value);summary.Children.Add(cell);
        }
        body.Children.Add(summary);
        var tables=new StackPanel();
        AddSection(tables,Loc.T("按模型"),data.Models.Count,ModelRows);FillRows(ModelRows,false);
        AddSection(tables,Loc.T("按 chat / 模型"),data.Chats.Count,ChatRows);FillRows(ChatRows,true);
        TableScroll=new ScrollViewer{MaxHeight=Math.Max(200,Math.Min(640,SystemParameters.WorkArea.Height-270)),VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,PanningMode=PanningMode.VerticalOnly,Content=tables};body.Children.Add(TableScroll);
        var footer=Text(Loc.T("本机记录 · IN 未缓存 / CACHE 命中 / OUT 输出"),10,"Muted");footer.Margin=new Thickness(0,9,0,0);footer.ToolTip=Loc.T("详细口径：设置 → 数据诊断");body.Children.Add(footer);
        if(data.Total.Conflicts>0){var warning=Text(Loc.F($"{data.Total.Conflicts} 条记录存在冲突 · 详见数据诊断"),10,"Muted");body.Children.Add(warning);}
    }
    void AddSection(StackPanel parent,string title,int count,StackPanel rows)
    {
        var labels=Columns();labels.Margin=new Thickness(0,parent.Children.Count==0?0:14,0,5);
        var name=Text($"{title} · {count}",13,_accent);name.FontWeight=FontWeights.SemiBold;name.ToolTip=Loc.T("按 token 用量排序");labels.Children.Add(name);
        for(var i=0;i<3;i++){var label=Text(new[]{"IN","CACHE","OUT"}[i],11,"Muted");label.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(label,i+1);labels.Children.Add(label);}
        var points=Text(Loc.T("估算点数"),11,"Muted");points.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(points,4);labels.Children.Add(points);
        parent.Children.Add(labels);parent.Children.Add(rows);
    }
    void FillRows(StackPanel rows,bool byChat)
    {
        var groups=byChat?_data.Chats:_data.Models;
        if(_data.Error is not null||groups.Count==0){rows.Children.Add(new TextBlock{Text=_data.Error??Loc.T("这个时间范围暂无已记录用量"),Margin=new Thickness(0,16,0,16),Foreground=Theme.Brush("Muted"),FontSize=12});return;}
        foreach(var group in groups)
        {
            var title=byChat?group.Key:Loc.T(group.Key);string? project=null;
            if(byChat)
            {
                if(_names.TryGetValue(group.Key,out var chat)){title=chat.Title;project=chat.Project;}
                else title=Loc.T("未命名 chat · ")+(group.Key.Length==0?Loc.T("未知 ID"):group.Key[..Math.Min(10,group.Key.Length)]);
            }
            var entries=byChat?group.ModelBreakdown:new[]{new TokenChatModel(group.Key,group.Usage,0)};
            var first=true;
            foreach(var entry in entries)
            {
                var row=Columns();row.MinHeight=32;row.Background=Brushes.Transparent;
                var name=new DockPanel{Margin=new Thickness(0,5,9,5),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};
                var label=Text(title,14);label.ToolTip=title+(project is {Length:>0}?"\n"+project:"");
                var metadata=Loc.F($"{entry.Usage.Requests:N0} 请求")+(byChat?(entry.Subagents>0?$" · {entry.Subagents} subagent":""):$" · {group.Members} chats");
                var detail=Text(" · "+metadata,13,"Muted");detail.MaxWidth=190;detail.ToolTip=metadata;
                DockPanel.SetDock(detail,Dock.Right);name.Children.Add(detail);
                if(byChat)
                {
                    // Reserve model and metadata widths; only the remaining title space can shrink.
                    var model=Text(" · "+Loc.T(entry.Model),14,_accent);model.MaxWidth=145;model.ToolTip=entry.Model;
                    DockPanel.SetDock(model,Dock.Right);name.Children.Add(model);
                }
                name.Children.Add(label);row.Children.Add(name);
                var values=new[]{entry.Usage.Input,entry.Usage.Cached,entry.Usage.Output};
                for(var i=0;i<3;i++){var number=Text(TokenSummary.Number(values[i]),14,i==1?_accent:"Ink");number.HorizontalAlignment=HorizontalAlignment.Right;number.ToolTip=$"{values[i]:N0} token";Grid.SetColumn(number,i+1);row.Children.Add(number);}
                var quota=Text("—",14,_accent);quota.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(quota,4);row.Children.Add(quota);
                _quotaCells.Add((quota,byChat?group.Key:null,entry.Model));
                row.MouseEnter+=(_,_)=>row.Background=Theme.Brush("Raised");row.MouseLeave+=(_,_)=>row.Background=Brushes.Transparent;
                rows.Children.Add(new Border{Margin=new Thickness(0,byChat&&first&&rows.Children.Count>0?5:0,0,0),BorderBrush=Theme.Brush("Line"),BorderThickness=new Thickness(0,0,0,1),Child=row});
                first=false;
            }
        }
    }
}
