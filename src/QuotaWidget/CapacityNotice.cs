using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuotaWidget.Core;

namespace QuotaWidget.App;

/// <summary>Persistent passive notice; no activation, no fullscreen overlay, no extra collector.</summary>
public sealed class CapacityNotice : Window
{
    readonly StackPanel _names = new();
    readonly Dictionary<string, ChatCacheEntry> _chats = new();
    readonly DispatcherTimer _timer;
    readonly Func<bool> _defer;
    readonly Action<string> _log;
    bool _closed, _placing;

    public CapacityNotice(Action openWidget, Func<bool>? defer = null, Action<string>? log = null)
    {
        _defer=defer??NotificationEnvironment.DeferVisual;_log=log??(_=>{});
        Title=Loc.T("Codex · 容量已满");Width=340;SizeToContent=SizeToContent.Height;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;
        ShowActivated=false;ShowInTaskbar=false;Focusable=false;Topmost=true;Left=Top=-20000;
        var body=new StackPanel();var header=new DockPanel();
        var close=new Button{Content="×",Style=(Style)Application.Current.FindResource("FlatButton"),Padding=new Thickness(6,0,6,0),FontSize=18};
        close.ToolTip=Loc.T("关闭");close.Click+=(_,_)=>{_log("visual dismissed");Close();};DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
        header.Children.Add(new TextBlock{Text=Title,Foreground=Theme.Brush("Red"),FontSize=14,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        body.Children.Add(header);_names.Margin=new Thickness(0,8,0,8);body.Children.Add(_names);
        body.Children.Add(new TextBlock{Text=Loc.T("任务因模型容量已满而停止。请在 Codex 中重试或切换模型。"),TextWrapping=TextWrapping.Wrap,Foreground=Theme.Brush("Ink"),FontSize=12});
        var open=new Button{Content=Loc.T("显示小窗"),Style=(Style)Application.Current.FindResource("FlatButton"),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};
        open.Click+=(_,_)=>{Close();openWidget();};body.Children.Add(open);
        Content=new Border{Background=Theme.Brush("Raised"),BorderBrush=Theme.Brush("Red"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Padding=new Thickness(14),Child=body};
        SourceInitialized+=(_,_)=>NotificationEnvironment.NoActivate(new WindowInteropHelper(this).Handle);
        Loaded+=(_,_)=>Place();SizeChanged+=(_,_)=>{if(IsVisible)Place();};
        _timer=new DispatcherTimer(TimeSpan.FromMilliseconds(250),DispatcherPriority.Background,(_,_)=>Present(),Dispatcher);
        _timer.Stop();Closed+=(_,_)=>{_closed=true;_timer.Stop();};
    }

    public void Add(IReadOnlyList<ChatCacheEntry> entries)
    {
        foreach(var e in entries)_chats[e.Id]=e;
        RenderNames();
    }

    public void Synchronize(IReadOnlyList<ChatCacheEntry> current)
    {
        foreach(var id in _chats.Keys.Where(id=>!current.Any(e=>e.Id==id&&e.CapacityBlockedAt==_chats[id].CapacityBlockedAt)).ToArray())_chats.Remove(id);
        if(_chats.Count==0){_log("visual cleared");Close();return;}RenderNames();
    }

    void RenderNames()
    {
        _names.Children.Clear();
        foreach(var e in _chats.Values.Take(4))_names.Children.Add(new TextBlock{Text=e.Title,TextTrimming=TextTrimming.CharacterEllipsis,Foreground=Theme.Brush("Ink"),FontSize=13});
        if(_chats.Count>4)_names.Children.Add(new TextBlock{Text=Loc.F($"还有 {_chats.Count-4} 个 chat"),Foreground=Theme.Brush("Muted"),FontSize=12});
    }

    public bool Present()
    {
        if(_closed||_chats.Count==0)return false;
        if(_defer())
        {
            if(IsVisible){Hide();_log("visual hidden presentation");}
            return false;
        }
        if(!IsVisible)
        {
            // Check immediately before Show; ShowActivated + WS_EX_NOACTIVATE
            // keep the current application/game in the foreground.
            Show();Place();_log("visual visible");
        }
        if(!_timer.IsEnabled)_timer.Start();
        return true;
    }

    void Place()
    {
        if(_placing)return;
        var handle=new WindowInteropHelper(this).Handle;
        if(handle==IntPtr.Zero||NativePlacement.Bounds(handle) is not {} bounds)return;
        _placing=true;
        try
        {
            var area=System.Windows.Forms.Screen.FromHandle(NotificationEnvironment.Foreground).WorkingArea;
            // Native toasts can stack vertically. Use the adjacent column so
            // neither a single toast nor its stack hides the capacity notice.
            var toastLane=(int)Math.Round(400*VisualTreeHelper.GetDpi(this).DpiScaleX);
            NativePlacement.Move(handle,Math.Max(area.Left,area.Right-(int)bounds.Width-toastLane-16),Math.Max(area.Top,area.Bottom-(int)bounds.Height-16));
        }
        finally{_placing=false;}
    }
}
