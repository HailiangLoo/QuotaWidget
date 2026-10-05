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
    public PlatformUsageCard Card {get;private set;}=null!;
    public UsageDetailWindow(ChatPlatform platform,PlatformUsageCard card,Func<int,PlatformUsageCard> reload)
    {
        _reload=reload;Title=platform+Loc.T(" 用量 · 已固定");WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        ResizeMode=ResizeMode.NoResize;SizeToContent=SizeToContent.WidthAndHeight;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.Manual;
        ReplaceCard(card);
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
    }
    public void ReplaceCard(PlatformUsageCard card)
    {
        Card=card;card.SetPinned(true);card.SetActions(()=>ReplaceCard(_reload(Card.Minutes)),Close);
        card.DragHandle.Cursor=Cursors.SizeAll;
        card.DragHandle.MouseLeftButtonDown+=(_,e)=>
        {
            if(card.IsCommandHit(e.OriginalSource as DependencyObject)||e.ButtonState!=MouseButtonState.Pressed) return;
            try {DragMove();} catch(InvalidOperationException) {} e.Handled=true;
        };
        Content=card;
        if(IsLoaded) Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>NativePlacement.FitToWorkArea(new WindowInteropHelper(this).Handle));
    }
    public void PlaceAt(Point physical)
    {
        SourceInitialized+=(_,_)=>NativePlacement.Move(new WindowInteropHelper(this).Handle,(int)Math.Round(physical.X),(int)Math.Round(physical.Y));
        Loaded+=(_,_)=>NativePlacement.FitToWorkArea(new WindowInteropHelper(this).Handle);
    }
}
