using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QuotaWidget.App;
using QuotaWidget.Core;

static class SettingSliderProbe
{
    public static void Run()
    {
        var control=new SettingSlider();control.Configure("采集间隔",WidgetSettings.PollChoices,450,seconds:true);
        var commits=0;control.SelectionChanged+=(_,_)=>commits++;
        var host=new Border{Width=204,Child=control};
        host.Measure(new Size(204,200));host.Arrange(new Rect(0,0,204,200));host.UpdateLayout();
        var track=(Track)control.Input.Template.FindName("PART_Track",control.Input);
        if(control.Selected!=450||control.Input.Maximum!=4)throw new Exception("Saved custom polling interval changed");
        if(!WindowDrag.IsControl(track.Thumb,host))throw new Exception("Slider drag would move window");
        track.Thumb.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
        control.Input.Value=3;control.Input.Value=4;
        if(commits!=0||control.Selected!=3600||!AutomationProperties.GetName(control.Input).Contains("60"))throw new Exception("Drag saved intermediate values or failed to preview");
        track.Thumb.RaiseEvent(new DragCompletedEventArgs(0,0,false){RoutedEvent=Thumb.DragCompletedEvent});
        if(commits!=1)throw new Exception("Drag did not commit exactly once");
        track.Thumb.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});control.Input.Value=0;
        track.Thumb.RaiseEvent(new DragCompletedEventArgs(0,0,true){RoutedEvent=Thumb.DragCompletedEvent});
        if(commits!=1||control.Selected!=3600)throw new Exception("Canceled drag changed setting");
        Slider.DecreaseSmall.Execute(null,control.Input);
        if(commits!=2||control.Selected!=1800)throw new Exception("Keyboard step does not follow adjacent option");
        control.Configure("连接短暂空闲",WidgetSettings.IdleLinkChoices,10,off:true);
        control.Selected=0;
        foreach(var language in new[]{"en","zh-CN"})
        {
            Loc.Configure(language);control.RefreshLanguage();
            if(!AutomationProperties.GetName(control.Input).EndsWith(Loc.T("关闭")))throw new Exception("Slider off value failed to localize");
        }
        Console.WriteLine("PASS: sliders preserve custom cadence, drag previews/commits once, cancel restores, keyboard steps discrete values, no window drag conflict, live language and off state.");
    }
}
