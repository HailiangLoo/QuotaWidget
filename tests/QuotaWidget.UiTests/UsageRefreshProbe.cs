using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using QuotaWidget.App;
using QuotaWidget.Core;

static class UsageRefreshProbe
{
    public static void Run()
    {
        void Check(bool ok,string why){if(!ok)throw new Exception("Usage auto refresh: "+why);}
        var origin=DateTimeOffset.Parse("2026-10-05T12:00:00+08:00");var now=origin;
        var calls=0;var scope=-1;var error=false;var throws=false;
        PlatformUsageCard Card(int minutes)
        {
            var data=TokenBreakdown.Build(minutes==0?origin.AddDays(-1):now.AddMinutes(-minutes),now,
                Enumerable.Range(0,80).Select(i=>new TokenSlice(i%2==0?"model-a":"model-b","fixture-"+i,new TokenSummary(100+i,500,20,0,1,0))).ToArray());
            if(error)data=data with{Error="fixture read error"};
            return new(ChatPlatform.Codex,data,new Dictionary<string,ChatCacheEntry>(),minutes,()=>{},()=>{},"fixture");
        }
        var window=new UsageDetailWindow(ChatPlatform.Codex,Card(60),minutes=>
        {calls++;scope=minutes;if(throws)throw new System.IO.IOException("fixture unavailable");return Card(minutes);},()=>now){Left=100,Top=150,Topmost=true};
        void Tick(int seconds,int cadence=300,bool enabled=true){now=origin.AddSeconds(seconds);window.RefreshIfDue(now,cadence,enabled);}
        void Layout(PlatformUsageCard card){card.Measure(new Size(660,double.PositiveInfinity));card.Arrange(new Rect(card.DesiredSize));card.UpdateLayout();}
        void Drain()
        {
            var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);
            Dispatcher.PushFrame(frame);
        }
        Tick(299);Check(calls==0,"refresh before cadence");
        Tick(300,enabled:false);Check(calls==0,"disabled/hidden window queried");
        Layout(window.Card);window.Card.TableScroll.ScrollToVerticalOffset(180);window.Card.UpdateLayout();
        var offset=window.Card.TableScroll.VerticalOffset;Check(offset>0,"scroll fixture did not scroll");
        Tick(300);Layout(window.Card);Drain();window.Card.UpdateLayout();
        Check(calls==1&&scope==60&&window.Card.Data.End==now,"due refresh lost platform/range or end time");
        Check(Math.Abs(window.Card.TableScroll.VerticalOffset-offset)<1,$"refresh reset scroll position: {offset} -> {window.Card.TableScroll.VerticalOffset}; viewport={window.Card.TableScroll.ViewportHeight}, extent={window.Card.TableScroll.ExtentHeight}");
        Check(window.Left==100&&window.Top==150&&window.Topmost&&!window.IsVisible,"refresh moved, activated or displayed a window");
        Tick(315);Check(calls==1,"clock tick queried again");
        Tick(600,600);Check(calls==1,"increased sampling interval ignored");
        Tick(900,600);Check(calls==2,"changed cadence did not refresh");
        Tick(1200);Check(calls==3,"decreased sampling interval ignored");
        window.ReplaceCard(Card(0));
        Tick(1500);Check(calls==4&&scope==0,"reused window kept old range");
        now=origin.AddSeconds(1600);
        ((DockPanel)window.Card.DragHandle).Children.OfType<Button>().Single(b=>b.Content.ToString()=="刷新").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(calls==5,"manual refresh unavailable");Tick(1800);Check(calls==5,"manual refresh did not reset cadence");
        typeof(UsageDetailWindow).GetField("_dragging",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true);
        Tick(1900);Check(calls==5,"refresh during dragging");
        typeof(UsageDetailWindow).GetField("_dragging",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,false);
        typeof(UsageDetailWindow).GetField("_lastInteraction",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,now);
        Tick(1904);Check(calls==5,"refresh during recent interaction");Tick(1905);Check(calls==6,"deferred refresh never resumed");
        var last=window.Card;error=true;Tick(2205);Check(calls==7&&ReferenceEquals(window.Card,last),"failed local read erased the last snapshot");
        Tick(2220);Check(calls==7,"failed read busy-polled");error=false;throws=true;Tick(2505);Check(calls==8&&ReferenceEquals(window.Card,last),"I/O failure destroyed snapshot");
        throws=false;Tick(2805);Check(calls==9&&!ReferenceEquals(window.Card,last),"did not recover on next cadence");
        Tick(12000);Check(calls==10,"resume replayed every missed interval");
        window.Close();Tick(13000);Check(calls==10,"closed window refreshed");
        Console.WriteLine("PASS: pinned usage auto-refresh cadence, setting changes, manual reset, reused scope, scroll/position, interaction deferral, retained error snapshot, no catch-up burst and close cleanup. No native windows shown.");
    }
}
