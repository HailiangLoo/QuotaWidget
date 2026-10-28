using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuotaWidget.Core;

namespace QuotaWidget.App;

/// <summary>Owned detail window stays above the widget; leaving the pointer never closes it.</summary>
public sealed class UsageDetailWindow : Window
{
    readonly Func<int,PlatformUsageCard> _reload;
    readonly Func<DateTimeOffset> _clock;
    DateTimeOffset _lastRefresh, _lastInteraction;
    bool _closed, _dragging;
    public PlatformUsageCard Card {get;private set;}=null!;
    public UsageDetailWindow(ChatPlatform platform,PlatformUsageCard card,Func<int,PlatformUsageCard> reload,Func<DateTimeOffset>? clock=null)
    {
        _reload=reload;_clock=clock??(()=>DateTimeOffset.Now);Title=platform+Loc.T(" 用量 · 已固定");WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        ResizeMode=ResizeMode.NoResize;SizeToContent=SizeToContent.WidthAndHeight;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.Manual;
        ReplaceCard(card);
        WindowDrag.Attach(this,this,dragging=>_dragging=dragging,()=>{_lastInteraction=_clock();FitCurrentMonitor();});
        DpiChanged+=(_,_)=>Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>{if(!_dragging)FitCurrentMonitor();});
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
        PreviewMouseWheel+=(_,_)=>_lastInteraction=_clock();
        PreviewMouseDown+=(_,_)=>_lastInteraction=_clock();
        PreviewKeyDown+=(_,_)=>_lastInteraction=_clock();
        Closed+=(_,_)=>_closed=true;
    }
    // Called by the existing app clock; there is no timer or query for unopened windows.
    // 'enabled' includes current visibility and provider/token-tracking preferences.
    public void RefreshIfDue(DateTimeOffset now,int intervalSeconds,bool enabled)
    {
        if(_closed||!enabled||_dragging||Mouse.LeftButton==MouseButtonState.Pressed||
            now-_lastInteraction<TimeSpan.FromSeconds(5)||now-_lastRefresh<TimeSpan.FromSeconds(Math.Clamp(intervalSeconds,60,86400)))return;
        _lastRefresh=now; // A failed local read retries at the regular cadence, not every clock tick.
        try
        {
            var card=_reload(Card.Minutes);
            if(card.Data.Error is null)ReplaceCard(card);
        }
        catch(Exception e) when(e is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        { /* Retain the last readable snapshot; manual refresh remains available. */ }
    }
    public void ReplaceCard(PlatformUsageCard card)
    {
        if(_closed)return;
        var offset=Card is {} previous&&previous.Platform==card.Platform&&previous.Minutes==card.Minutes?previous.TableScroll.VerticalOffset:0;
        _lastRefresh=_clock();
        Card=card;card.SetPinned(true);card.SetActions(()=>ReplaceCard(_reload(Card.Minutes)),Close,range=>ReplaceCard(_reload(range)));
        card.DragHandle.Cursor=Cursors.SizeAll;
        Content=card;
        if(offset>0)Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>
        {
            if(_closed||!ReferenceEquals(Card,card))return;
            card.UpdateLayout();card.TableScroll.ScrollToVerticalOffset(offset);card.UpdateLayout();
        });
        if(IsLoaded) Dispatcher.BeginInvoke(DispatcherPriority.Loaded,FitCurrentMonitor);
    }
    void FitCurrentMonitor()
    {
        if(_closed||!IsLoaded)return;
        var handle=new WindowInteropHelper(this).Handle;
        var area=NativePlacement.WorkArea(handle);var dpi=VisualTreeHelper.GetDpi(this);
        Card.FitAvailable(new Size(area.Width/dpi.DpiScaleX,area.Height/dpi.DpiScaleY));
        UpdateLayout();NativePlacement.FitToWorkArea(handle);
    }
    public void PlaceBeside(Rect owner,double top,Rect work,double gap)
    {
        // Initialize on the owner's screen before WPF computes per-monitor size.
        SourceInitialized+=(_,_)=>NativePlacement.Move(new WindowInteropHelper(this).Handle,(int)Math.Round(owner.Left),(int)Math.Round(owner.Top));
        void Place()
        {
            if(_closed)return;
            var handle=new WindowInteropHelper(this).Handle;
            var dpi=VisualTreeHelper.GetDpi(this);
            Card.FitAvailable(new Size(work.Width/dpi.DpiScaleX,work.Height/dpi.DpiScaleY));
            UpdateLayout();
            if(NativePlacement.Bounds(handle) is not {} bounds)return;
            var at=NativePlacement.Beside(owner,bounds.Size,work,top,gap);
            NativePlacement.Move(handle,(int)Math.Round(at.X),(int)Math.Round(at.Y));
        }
        Loaded+=(_,_)=>{Place();Dispatcher.BeginInvoke(DispatcherPriority.Loaded,Place);};
    }
}
