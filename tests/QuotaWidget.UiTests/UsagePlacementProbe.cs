using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using QuotaWidget.App;
using QuotaWidget.Core;

static class UsagePlacementProbe
{
    public static void Run()
    {
        void Check(bool value,string why){if(!value)throw new Exception("Usage placement: "+why);}
        var native=typeof(MainWindow).Assembly.GetType("QuotaWidget.App.NativePlacement")!;
        var method=native.GetMethod("Beside",BindingFlags.Static|BindingFlags.Public)!;
        Point Place(Rect owner,Size card,Rect work,double top,double gap)=>
            (Point)method.Invoke(null,[owner,card,work,top,gap])!;
        var now=DateTimeOffset.Now;
        PlatformUsageCard Card()=>new(ChatPlatform.Codex,TokenBreakdown.Build(now.AddHours(-5),now,
            Enumerable.Range(0,40).Select(i=>new TokenSlice("model-a","chat-"+i,new(100,200,300,0,2,0))).ToArray()),
            new Dictionary<string,ChatCacheEntry>(),300,()=>{},()=>{},"fixture");
        // Physical rectangles include negative monitor coordinates and taskbars on
        // each edge. A neighbouring display never enlarges the selected work area.
        var cases=0;
        foreach(var work in new[]{new Rect(0,0,1920,1040),new Rect(-1920,40,1920,1040),
            new Rect(1920,0,2560,1400),new Rect(0,-1080,1280,1040),new Rect(60,0,740,600)})
        foreach(var scale in new[]{1d,1.25,1.5,2d,2.5})
        {
            var card=Card();card.FitAvailable(new Size(work.Width/scale,work.Height/scale));
            var size=new Size(card.DesiredSize.Width*scale,card.DesiredSize.Height*scale);
            Check(size.Width<=work.Width+.1&&size.Height<=work.Height+.1,"card exceeds work area after fitting");
            Check(card.TableScroll.ExtentHeight==0||card.TableScroll.MaxHeight<640,"long table does not have a bounded viewport");
            foreach(var fx in new[]{0d,.25,.5,.75,1d})
            foreach(var fy in new[]{0d,.5,1d})
            {
                var owner=new Rect(work.Left+fx*(work.Width-240),work.Top+fy*(work.Height-150),240,150);
                var at=Place(owner,size,work,owner.Bottom-25,7*scale);
                Check(at.X>=work.Left-.1&&at.Y>=work.Top-.1&&at.X+size.Width<=work.Right+.1&&at.Y+size.Height<=work.Bottom+.1,"off-screen placement");
                var right=work.Right-owner.Right-7*scale;var left=owner.Left-work.Left-7*scale;
                if(right>=size.Width)Check(at.X>=owner.Right,"right-side space ignored");
                else if(left>=size.Width)Check(at.X+size.Width<=owner.Left,"right edge did not flip left");
                cases++;
            }
            // A card fitted on a small monitor must recover its natural width
            // when its pinned window is later opened/refreshed on a larger one.
            card.FitAvailable(new Size(1920,1200));
            Check(Math.Abs(card.DesiredSize.Width-730)<.1,"small-monitor scale leaked into later layout");
            card.Arrange(new Rect(new Point(),card.DesiredSize));card.UpdateLayout();
            card.TableScroll.ScrollToBottom();card.UpdateLayout();
            Check(card.TableScroll.VerticalOffset>=card.TableScroll.ScrollableHeight-.1,"last chat cannot be reached");
        }
        // Exercise the real pin-placement handler on an HWND without showing a
        // window or taking focus. This also checks WPF's measured/native size.
        var pinned=new UsageDetailWindow(ChatPlatform.Codex,Card(),_=>Card()){ShowActivated=false};
        var hwnd=new WindowInteropHelper(pinned).EnsureHandle();
        var actualWork=(Rect)native.GetMethod("WorkArea")!.Invoke(null,[hwnd])!;
        var edgeOwner=new Rect(actualWork.Right-250,actualWork.Bottom-300,240,280);
        pinned.PlaceBeside(edgeOwner,edgeOwner.Bottom-20,actualWork,7);
        pinned.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var actual=(Rect)native.GetMethod("Bounds")!.Invoke(null,[hwnd])!;
        Check(actualWork.Contains(actual),"native pinned card escaped the work area");
        if(actual.Width+7<=edgeOwner.Left-actualWork.Left)
            Check(actual.Right<=edgeOwner.Left,"native pinned card did not open to the left");
        Check(!pinned.IsVisible,"placement test showed a desktop window");
        pinned.Close();
        Console.WriteLine($"Usage placement: {cases} edge/corner/monitor/DPI cases passed.");
    }
}
