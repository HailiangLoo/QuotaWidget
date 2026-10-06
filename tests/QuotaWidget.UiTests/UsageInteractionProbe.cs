using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QuotaWidget.App;
using QuotaWidget.Core;

static class UsageInteractionProbe
{
    public static void Run()
    {
        void Check(bool b,string why){if(!b)throw new Exception("Usage interaction: "+why);}
        var now=DateTimeOffset.Now;var scope=-1;
        PlatformUsageCard Make(int m)=>new(ChatPlatform.Claude,TokenBreakdown.Build(now.AddHours(-5),now,
            [new("model-a","chat-a",new(100,200,300,0,2,0)),new("model-b","chat-a",new(400,500,600,0,3,0))]),
            new Dictionary<string,ChatCacheEntry>(),m,()=>{},()=>{},"fixture",selectRange:r=>scope=r);
        var card=Make(60);
        Check(card.RangeTabs.Children.Cast<ToggleButton>().Select(b=>b.Content.ToString()).SequenceEqual(new[]{"1h","5h","12h","24h","3d","all"}),"wrong range choices");
        ((ToggleButton)card.RangeTabs.Children[4]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(scope==4320,"hover range did not dispatch");
        var window=new UsageDetailWindow(ChatPlatform.Claude,card,m=>{scope=m;return Make(m);});
        ((ToggleButton)window.Card.RangeTabs.Children[1]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(scope==300&&window.Card.Minutes==300&&window.Card.IsPinned,"range change lost fixed window or scope");
        Check(window.Card.RangeTabs.Children.Cast<ToggleButton>().Count(b=>b.IsChecked==true)==1,"selection ambiguous");
        var button=window.Card.RangeTabs.Children[1];
        Check(WindowDrag.IsControl(button,window.Card),"range button starts dragging");
        Check(!WindowDrag.IsControl(window.Card.ChatRows,window.Card),"table body not draggable");
        Check(!WindowDrag.PassedThreshold(new(5,5),new(6,6))&&WindowDrag.PassedThreshold(new(5,5),new(30,40)),"click/drag threshold wrong");
        window.Card.ShowQuotaEstimate(new(3,[new("chat-a","model-a",1.25,.1),new("chat-a","model-b",1.75,.2)],"",20,.3));
        var points=window.Card.ChatRows.Children.Cast<Border>().Select(b=>((Grid)b.Child).Children.OfType<TextBlock>().Single(t=>Grid.GetColumn(t)==4).Text).ToArray();
        Check(points.SequenceEqual(new[]{"1.8","1.3"}),"model estimate associated with wrong row or precision");
        window.Card.ShowQuotaEstimate(ChatQuotaEstimate.Unknown("本机记录尚未完整"));
        Check(window.Card.ChatRows.Children.Cast<Border>().All(b=>((Grid)b.Child).Children.OfType<TextBlock>().Single(t=>Grid.GetColumn(t)==4).Text=="—"),"unknown rendered as zero or retained old estimate");
        foreach(var language in new[]{"en","zh-CN"})
        {
            Loc.Configure(language);var localized=Make(300);localized.SetPinned(true);
            localized.Measure(new Size(localized.Width,double.PositiveInfinity));localized.Arrange(new Rect(localized.DesiredSize));localized.UpdateLayout();
            Check(localized.RangeTabs.ActualWidth>500&&localized.RangeTabs.ActualHeight>=24,"range strip collapsed");
            foreach(var row in localized.ChatRows.Children.Cast<Border>().Select(b=>(Grid)b.Child))
                Check(row.Children.OfType<TextBlock>().All(c=>c.ActualWidth>0),"token or estimate column squeezed out");
        }
        window.Close();
        Console.WriteLine("PASS: independent detail ranges in both layouts/languages, pinned scope changes, body drag eligibility/control exclusion/threshold, estimates by chat-model and unknown fallback.");
    }
}
