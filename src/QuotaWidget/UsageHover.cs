using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public partial class MainWindow
{
    readonly Popup _usagePopup=new(){AllowsTransparency=true,Placement=PlacementMode.Right,HorizontalOffset=7,StaysOpen=true,PopupAnimation=PopupAnimation.Fade};
    readonly DispatcherTimer _usageOpenTimer=new(){Interval=TimeSpan.FromMilliseconds(280)};
    readonly DispatcherTimer _usageCloseTimer=new(){Interval=TimeSpan.FromMilliseconds(350)};
    FrameworkElement? _usageOwner;
    ChatPlatform _usagePlatform;
    int _usageMinutes;
    readonly Dictionary<ChatPlatform,UsageDetailWindow> _usageWindows=new();
    PlatformUsageCard? _hoverCard;

    void InitializeUsageHover()
    {
        foreach(var (host,platform) in new[]{(CompactClaudeRate,ChatPlatform.Claude),(CompactCodexRate,ChatPlatform.Codex)}) BindUsage(host,platform,true);
        BindUsage(CodexInlineUsage,ChatPlatform.Codex,true);
        BindUsage(ClaudeTokenHitArea,ChatPlatform.Claude,true);BindUsage(CodexTokenHitArea,ChatPlatform.Codex,true);
        BindUsage(TotalLegend,ChatPlatform.Claude);BindUsage(CodexLegend,ChatPlatform.Codex);
        BindUsage(ClaudeUsageHotspot,ChatPlatform.Claude);BindUsage(CodexUsageHotspot,ChatPlatform.Codex);
        _usagePopup.Closed+=(_,_)=>{if(!_usagePopup.IsOpen) _hoverCard=null;};
        _usageOpenTimer.Tick+=(_,_)=>{_usageOpenTimer.Stop();if(_usageOwner?.IsMouseOver==true) ShowUsage();};
        _usageCloseTimer.Tick+=(_,_)=>
        {
            _usageCloseTimer.Stop();
            if(_usageOwner?.IsMouseOver!=true&&_usagePopup.Child?.IsMouseOver!=true) CloseUsage();
        };
        IsVisibleChanged+=(_,_)=>{if(!IsVisible) CloseUsage();};
        LocationChanged+=(_,_)=>CloseUsage();
        Deactivated+=(_,_)=>{if(_usagePopup.Child?.IsMouseOver!=true) CloseUsage();};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape&&_usagePopup.IsOpen){CloseUsage();e.Handled=true;}};
        PreviewMouseDown+=(_,_)=>{if(_usagePopup.IsOpen&&_usageOwner?.IsMouseOver!=true&&_usagePopup.Child?.IsMouseOver!=true) CloseUsage();};
    }
    int UsageMinutes()=>_model.Settings.CompactMode?RecentUsageRate.WindowMinutes:ChartRanges.Select(_model.Settings.RangeMinutes,AvailableRanges());
    void BindUsage(FrameworkElement host,ChatPlatform platform,bool clickToPin=false)
    {
        System.Windows.Controls.ToolTipService.SetIsEnabled(host,false);
        host.MouseEnter+=(_,_)=>
        {
            _usageCloseTimer.Stop();
            if(_usageWindows.ContainsKey(platform)){CloseUsage();return;}
            if(_usagePopup.IsOpen&&_usageOwner==host) return;
            if(_usagePopup.IsOpen) CloseUsage();
            _usageOpenTimer.Stop();_usageOwner=host;_usagePlatform=platform;
            _usageMinutes=UsageMinutes();
            _usageOpenTimer.Start();
        };
        host.MouseLeave+=(_,_)=>{_usageOpenTimer.Stop();QueueUsageClose();};
        host.Unloaded+=(_,_)=>{if(_usageOwner==host) CloseUsage();};
        host.IsVisibleChanged+=(_,_)=>{if(!host.IsVisible&&_usageOwner==host) CloseUsage();};
        if(clickToPin)
        {
            host.Cursor=Cursors.Hand;host.Focusable=true;
            host.PreviewMouseLeftButtonDown+=(_,e)=>{PinUsageFromHost(host,platform);e.Handled=true;};
            host.KeyDown+=(_,e)=>{if(e.Key is Key.Enter or Key.Space){PinUsageFromHost(host,platform);e.Handled=true;}};
        }
    }
    void QueueUsageClose(){_usageCloseTimer.Stop();_usageCloseTimer.Start();}
    void CloseUsage(){_usageOpenTimer.Stop();_usageCloseTimer.Stop();_usagePopup.IsOpen=false;_usagePopup.Child=null;_usageOwner=null;_hoverCard=null;}
    void ShowUsage()
    {
        if(_usageOwner is null||_usageWindows.ContainsKey(_usagePlatform)) return;
        _usagePopup.PlacementTarget=_usageOwner;
        var card=CreateUsageCard(_usagePlatform,_usageMinutes);
        card.MouseEnter+=UsageCardEnter;card.MouseLeave+=UsageCardLeave;card.PreviewMouseLeftButtonDown+=UsageCardClick;
        _usagePopup.Child=card;_hoverCard=card;_usagePopup.IsOpen=true;
        RenderTokens();
    }
    void UsageCardEnter(object sender,MouseEventArgs e)=>_usageCloseTimer.Stop();
    void UsageCardLeave(object sender,MouseEventArgs e)=>QueueUsageClose();
    void UsageCardClick(object sender,MouseButtonEventArgs e)
    {
        if(sender is not PlatformUsageCard card||card.IsCommandHit(e.OriginalSource as DependencyObject)) return;
        PinUsage(card,card);e.Handled=true;
    }
    void PinUsageFromHost(FrameworkElement host,ChatPlatform platform)
    {
        var minutes=UsageMinutes();
        // Reuse an already visible snapshot; a quick click never needs to open the popup first.
        var card=_hoverCard is { } hover&&hover.Platform==platform&&hover.Minutes==minutes?hover:CreateUsageCard(platform,minutes);
        PinUsage(card,host);
    }
    void PinUsage(PlatformUsageCard card,FrameworkElement anchor)
    {
        var at=IsVisible?(_usagePopup.Child==card?card.PointToScreen(new Point(0,0)):anchor.PointToScreen(new Point(anchor.ActualWidth+7,0))):(Point?)null;
        var platform=card.Platform;
        card.MouseEnter-=UsageCardEnter;card.MouseLeave-=UsageCardLeave;card.PreviewMouseLeftButtonDown-=UsageCardClick;
        CloseUsage();
        if(_usageWindows.TryGetValue(platform,out var existing))
        {
            existing.ReplaceCard(card);if(IsVisible){existing.Show();existing.Activate();}return;
        }
        var window=new UsageDetailWindow(platform,card,range=>CreateUsageCard(platform,range)){Topmost=Topmost};
        _usageWindows[platform]=window;window.Closed+=(_,_)=>_usageWindows.Remove(platform);
        // A hidden or unloading owner must not bring a detail window onto the desktop.
        if(at is null) return;
        window.Owner=this;window.PlaceAt(at.Value);window.Show();window.Activate();
    }
    TokenSummary? HoverTokenTotal(ChatPlatform platform,int minutes)
        =>_hoverCard is { } card&&card.Platform==platform&&card.Minutes==minutes?card.Data.Total:null;
    internal PlatformUsageCard CreateUsageCard(ChatPlatform platform,int minutes)
    {
        var now=DateTimeOffset.Now;var start=minutes==0?DateTimeOffset.UnixEpoch:now.AddMinutes(-minutes);
        var data=_app.TokenBreakdown(platform,start,now);
        var names=_app.TokenChatNames(platform,data.Start,data.End,data.Chats.Select(g=>g.Key).ToHashSet());
        var rate=_model.Settings.CompactMode?
            (platform==ChatPlatform.Claude?CompactClaudePoints:CompactCodexPoints).Text+Loc.T(" · 均速 ")+
            (platform==ChatPlatform.Claude?CompactClaudeRateValue:CompactCodexRateValue).Text+Loc.T(" 点/h"):null;
        if(rate?.Contains('*')==true)rate+=Loc.T(" · 记录不全");
        return new(platform,data,names,minutes,ShowUsage,CloseUsage,
            (_model.Settings.TokenTrackingEnabled?"":Loc.T("统计已暂停；仅展示已保存记录。\n"))+_app.TokenDetail,rate);
    }
}
