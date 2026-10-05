using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using QuotaWidget.App;
using QuotaWidget.Core;

static class ChartInspectProbe
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        void Check(bool ok,string why) { if(!ok) throw new Exception("Chart inspection: "+why); }
        var now = DateTimeOffset.Now;
        SeriesData Source(double points) => new() { Key=SeriesKey.Total,Segments=[new(){Start=now.AddHours(-2),End=now,Delta=points}] };
        ChartView View(bool claude=true,bool codex=true,bool collapseClaude=false,bool collapseCodex=false,bool cumulative=false,bool fable=true,bool? codexCumulative=null) => new()
        {
            Start=now.AddHours(-2),End=now,Total=Source(4),Fable=Source(2),Codex=Source(10),Gaps=[],Smooth=false,
            TotalVisible=claude,FableVisible=claude&&fable,CodexVisible=codex,ClaudeCollapsed=collapseClaude,CodexCollapsed=collapseCodex,ClaudeCumulativeMode=cumulative,CodexCumulativeMode=codexCumulative??cumulative,FableToClaudeFactor=.5,
            FableCumulative=CumulativeSeries.Build(Source(2),now.AddHours(-2),now),
        };
        var chart = new RateChart { Margin=new Thickness(8,170,8,0),VerticalAlignment=VerticalAlignment.Top };
        var overlay = new ChartInspectOverlay(); chart.AttachInspectOverlay(overlay);
        var host = new Grid { Width=240,Height=520 };host.Children.Add(chart);host.Children.Add(overlay);
        void Set(ChartView view) { chart.View=view;host.Measure(new Size(240,520));host.Arrange(new Rect(0,0,240,520));host.UpdateLayout(); }
        void Hover(double x,double y) => typeof(RateChart).GetMethod("HoverAt",Private)!.Invoke(chart,[new Point(x,y)]);
        object? Card() => typeof(RateChart).GetMethod("BuildInspectCard",Private)!.Invoke(chart,[overlay]);
        T Field<T>(object value,string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
        void Verify(bool codex,string[] names)
        {
            var card=Card();Check(card is not null,"card missing");
            var rect=Field<Rect>(card!,"Bounds");var plot=Field<Rect>(card!,"Plot");
            Check(!rect.IntersectsWith(plot),"card covers hovered plot");
            Check(codex?rect.Bottom<plot.Top:rect.Top>plot.Bottom,"wrong preferred side");
            Check(new Rect(0,0,240,520).Contains(rect),"card overflows window");
            var rows=Field<IEnumerable>(card!,"Rows").Cast<object>().ToArray();
            Check(rows.Select(r=>Field<string>(r,"Name")).SequenceEqual(names),"another graph leaked into rows");
        }
        foreach(var cumulative in new[]{false,true})
        foreach(var codexCumulative in new[]{false,true})
        foreach(var layout in new[]{"both","claude-only","codex-only","claude-collapsed","codex-collapsed"})
        {
            Set(View(claude:layout!="codex-only",codex:layout!="claude-only",collapseClaude:layout=="claude-collapsed",collapseCodex:layout=="codex-collapsed",cumulative:cumulative,codexCumulative:codexCumulative));
            foreach(var codex in new[]{false,true})
            {
                if(chart.HeaderTop(codex) is not { } top || codex&&layout=="codex-collapsed" || !codex&&layout=="claude-collapsed") continue;
                foreach(var x in new[]{2d,chart.ActualWidth/2,chart.ActualWidth-2})
                {
                    Hover(x,top+35);Verify(codex,codex?["Codex"]:["Claude","Fable"]);
                    var caption=Field<string>(Card()!,"Caption");Check(caption.Contains((codex?codexCumulative:cumulative)?"累计点":"点/h"),"provider mode lost");
                }
            }
            chart.ClearInspect();Check(Card() is null,"clear left stale overlay");
        }
        Set(View());
        foreach(var width in new[]{216d,276d,316d})
        {
            host.Width=width;host.Measure(new Size(width,520));host.Arrange(new Rect(0,0,width,520));host.UpdateLayout();
            foreach(var codex in new[]{false,true})
            {
                var bounds=chart.ModeBounds(codex)!.Value;
                var content=typeof(RateChart).GetMethod("Header",Private)!.Invoke(chart,[codex?1:0])!;
                var size=Field<double>(content,"FontSize");
                double TextWidth(string text)=>new System.Windows.Media.FormattedText(text,System.Globalization.CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface("Segoe UI, Microsoft YaHei UI"),size,System.Windows.Media.Brushes.White,1).Width;
                Check(bounds.Left>1+TextWidth(Field<string>(content,"Left"))+4&&bounds.Right<chart.ActualWidth-24-TextWidth(Field<string>(content,"Right"))-4,"mode buttons overlap heading or totals");
            }
        }
        host.Width=240;Set(View());
        Hover(chart.ActualWidth/2,35);
        var amounts=Field<IEnumerable>(Card()!,"Rows").Cast<object>().Select(r=>Field<string>(r,"Value")).ToArray();
        Check(amounts.SequenceEqual(new[]{"2.0","1.0"}),"source values changed");
        chart.PinInspect(now.AddHours(-1),true);Verify(true,["Codex"]);
        Hover(50,35);Verify(false,["Claude","Fable"]);
        Hover(-1,-1);Verify(true,["Codex"]);
        Set(View(claude:false));Verify(true,["Codex"]); // Panel index changes, provider identity must not.
        Set(View(codex:false));Check(Card() is null&&chart.Pinned is null,"removed pinned source was reinterpreted");
        Hover(50,35);Verify(false,["Claude","Fable"]);
        Set(View(collapseClaude:true));Check(Card() is null,"collapsed hovered graph retains card");
        Hover(50,10);Check(Card() is null,"header triggers chart inspection");
        Set(View(fable:false));Hover(50,35);Verify(false,["Claude"]);
        var solo=View();solo.FableOnlySpans=[new(now.AddMinutes(-90),now.AddMinutes(-30))];Set(solo);
        Hover(chart.ActualWidth/2,35);Verify(false,["Fable"]);
        var lanes=(IEnumerable)typeof(RateChart).GetField("_lanes",Private)!.GetValue(chart)!;
        var totalLane=lanes.Cast<object>().Single(l=>Field<string>(l,"Name")=="Claude");
        var paths=(List<IReadOnlyList<TrendPoint>>)typeof(RateChart).GetMethod("Points",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[totalLane,solo])!;
        Check(paths.Count==2&&paths[0][^1].Time<=now.AddMinutes(-90)&&paths[1][0].Time>=now.AddMinutes(-30),"Claude stroke still crosses the Fable-only fragment");
        Hover(5,35);Verify(false,["Claude","Fable"]);
        var cumulativeView=View(cumulative:true);cumulativeView.FableOnlySpans=solo.FableOnlySpans;Set(cumulativeView);
        Hover(chart.ActualWidth/2,35);Verify(false,["Fable"]);
        var origin=now.AddHours(-12); var edge=origin.AddHours(3).AddMinutes(37).AddMilliseconds(408);
        SeriesData boundedSource = new() { Key=SeriesKey.Total, Segments=Enumerable.Range(0,144)
            .Select(i=>new RateSegment{Start=origin.AddMinutes(i*5),End=origin.AddMinutes((i+1)*5),Delta=i==48?1:0}).ToList() };
        var boundedView=new ChartView { Start=origin,End=now,Total=Source(0),Fable=Source(0),Codex=boundedSource,
            Gaps=[],Smooth=true,TotalVisible=false,FableVisible=false,CodexVisible=true,TrendBoundaries=[edge] };
        Set(boundedView);
        var axis=(IReadOnlyList<AxisTick>)typeof(RateChart).GetMethod("AxisTicks",Private)!.Invoke(chart,[0])!;
        Check(axis.Any(p=>p.Time==edge),"axis omits the exact session start in favor of an arbitrary tick");
        var boundedLane=((IEnumerable)typeof(RateChart).GetField("_lanes",Private)!.GetValue(chart)!).Cast<object>().Single();
        var boundedPaths=(List<IReadOnlyList<TrendPoint>>)typeof(RateChart).GetMethod("Points",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[boundedLane,boundedView])!;
        Check(boundedPaths[0][0].Time==edge && boundedPaths[0][1].Time==edge && boundedPaths[0][0].Rate==0 && boundedPaths[0][1].Rate>0,"painted curve rounds the start");
        chart.PinInspect(edge.AddSeconds(-1),true);Verify(true,["Codex"]);
        Check(Field<IEnumerable>(Card()!,"Rows").Cast<object>().All(r=>Field<string>(r,"Value")=="0.0"),"hover invents pre-session consumption");
        chart.PinInspect(edge,true);Verify(true,["Codex"]);
        Check(Field<IEnumerable>(Card()!,"Rows").Cast<object>().All(r=>Field<string>(r,"Value")!="0.0"),"hover and vertical start disagree");
        Console.WriteLine("Session edges: exact axis tick, vertical rendered start, no pre-session smoothing and consistent edge hover passed.");
        var workStart=now.AddMinutes(-80);var workEnd=now.AddMinutes(-25);
        SeriesData WorkSource(double delta)=>new(){Key=SeriesKey.Total,Segments=Enumerable.Range(0,24).Select(i=>
            new RateSegment{Start=now.AddMinutes(-120+i*5),End=now.AddMinutes(-115+i*5),Delta=i==16?delta:0}).ToList()};
        var activityView=new ChartView{Start=now.AddHours(-2),End=now,Total=WorkSource(1),Fable=WorkSource(.5),Codex=Source(4),Gaps=[],Smooth=true,
            FableToClaudeFactor=.5,Activity=new([new(workStart,workEnd,true,true,"fable")],[new(workStart,workEnd,true,true,"fable")],[new(now.AddHours(-2),now,true,false,null)])};
        Set(activityView);
        foreach(var lane in ((IEnumerable)typeof(RateChart).GetField("_lanes",Private)!.GetValue(chart)!).Cast<object>())
        {
            var name=Field<string>(lane,"Name");
            var workPaths=(List<IReadOnlyList<TrendPoint>>)typeof(RateChart).GetMethod("Points",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[lane,activityView])!;
            if(name=="Codex")Check(workPaths[^1][^1].Rate>0,"Claude completion cut Codex off");
            else Check(workPaths[0][0].Time==workStart&&workPaths[^1][^1].Time==workEnd&&workPaths[^1][^1].Rate==0&&workPaths[^1][^2].Time==workEnd,"provider/model start or finish has a smooth shoulder");
        }
        chart.PinInspect(workEnd.AddSeconds(1),false);Verify(false,["Claude","Fable"]);
        Check(Field<IEnumerable>(Card()!,"Rows").Cast<object>().All(r=>Field<string>(r,"Value")=="0.0"),"completed Claude/Fable still consume on hover");
        chart.PinInspect(workEnd.AddSeconds(1),true);Verify(true,["Codex"]);
        Check(Field<IEnumerable>(Card()!,"Rows").Cast<object>().All(r=>Field<string>(r,"Value")!="0.0"),"ongoing Codex hover stopped");
        chart.PinInspect(workEnd.AddMinutes(-5),false);Verify(false,["Claude","Fable"]);
        Check(Field<IEnumerable>(Card()!,"Rows").Cast<object>().All(r=>!Field<string>(r,"Value").Contains("暂估")),"settled rate estimate still has a special tail label");
        chart.PinInspect(workEnd.AddSeconds(1),false);
        Check(Field<IEnumerable>(Card()!,"Rows").Cast<object>().All(r=>!Field<string>(r,"Value").Contains("暂估")),"idle zero marked tentative");
        var liveTail=new ChartView{Start=now.AddHours(-2),End=now,Total=Source(0),Fable=Source(0),Codex=WorkSource(1),Gaps=[],Smooth=true,
            TotalVisible=false,FableVisible=false,CodexVisible=true,Activity=new([],[],[new(now.AddHours(-2),now,true,false,null)])};
        Set(liveTail);chart.PinInspect(now.AddMinutes(-5),true);Verify(true,["Codex"]);
        Check(liveTail.CodexTrend!.IsProvisional(now.AddMinutes(-5)),"test no longer reaches the live tail");
        Check(Field<IEnumerable>(Card()!,"Rows").Cast<object>().All(r=>System.Text.RegularExpressions.Regex.IsMatch(Field<string>(r,"Value"),@"^\d+\.\d$")),"live rate has a special label or lost one-decimal precision");
        Console.WriteLine("Provider work edges: Claude/Fable both exact start AND completed end; ongoing Codex remains live; paths and hover agree.");
        foreach(var seconds in new[]{.052,5.193,60d})
        {
            var handoff=now.AddMinutes(-50);
            var joined=new ChartView{Start=now.AddHours(-2),End=now,Total=Source(0),Fable=Source(0),Codex=WorkSource(1),Gaps=[],Smooth=true,
                TotalVisible=false,FableVisible=false,CodexVisible=true,Activity=new([],[],[new(now.AddMinutes(-80),handoff,true,true,null),new(handoff.AddSeconds(seconds),workEnd,true,true,null)])};
            Set(joined);
            var lane=((IEnumerable)typeof(RateChart).GetField("_lanes",Private)!.GetValue(chart)!).Cast<object>().Single();
            var pathsAtHandoff=(List<IReadOnlyList<TrendPoint>>)typeof(RateChart).GetMethod("Points",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[lane,joined])!;
            var hasZero=pathsAtHandoff.SelectMany(p=>p).Any(p=>p.Time==handoff&&p.Rate==0);
            Check(hasZero==(seconds>10),"turn marker leaked into the rendered episode boundary");
            chart.PinInspect(handoff.AddSeconds(seconds/2),true);Verify(true,["Codex"]);
            var value=Field<IEnumerable>(Card()!,"Rows").Cast<object>().Select(r=>Field<string>(r,"Value")).Single();
            Check((value=="0.0")== (seconds>10),"hover and rendered continuity disagree");
        }
        Console.WriteLine("Activity pipeline: 52ms/5.193s handoffs have no interior zero stroke and consistent hover; 60s real pause stays blank.");
        var separate=new RateChart{Width=340};
        var period=now.AddHours(-3);
        SeriesData History(double amount)=>new(){Key=SeriesKey.Total,Segments=[new(){Start=period,End=now,Delta=amount}]};
        var cStart=now.AddMinutes(-140);var cEnd=now.AddMinutes(-55);var xStart=now.AddMinutes(-100);var xEnd=now.AddMinutes(-30);
        void Separate(bool cumulative)
        {
            separate.View=new ChartView{Start=period,End=now,Smooth=true,Total=History(4),Fable=History(0),Codex=History(8),Gaps=[],FableVisible=false,
                CodexCumulativeMode=cumulative,Activity=new([new(cStart,cEnd,true,true,null)],[],[new(xStart,xEnd,true,true,null)])};
            separate.Measure(new Size(340,600));separate.Arrange(new Rect(0,0,340,separate.Height));separate.UpdateLayout();
        }
        IReadOnlyList<AxisTick> Ticks(int panel)=>(IReadOnlyList<AxisTick>)typeof(RateChart).GetMethod("AxisTicks",Private)!.Invoke(separate,[panel])!;
        Separate(false);var claudeTicks=Ticks(0).ToArray();var codexTicks=Ticks(1).ToArray();
        Check(claudeTicks.Any(p=>p.Kind==AxisLandmarkKind.Stage)&&codexTicks.Any(p=>p.Kind==AxisLandmarkKind.Stage),"provider stages were not offered to axes");
        Check(claudeTicks.Where(p=>p.Kind==AxisLandmarkKind.Stage).All(p=>p.Time==cStart||p.Time==cEnd)&&codexTicks.Where(p=>p.Kind==AxisLandmarkKind.Stage).All(p=>p.Time==xStart||p.Time==xEnd),"axis borrowed another provider's stage");
        Check(claudeTicks.Concat(codexTicks).All(p=>Math.Abs(p.X-(p.Time-period).TotalHours/3*(separate.ActualWidth-2))<1e-6),"provider axes do not share the same time mapping");
        Separate(true);
        Check(Ticks(0).SequenceEqual(claudeTicks)&&!Ticks(1).SequenceEqual(codexTicks),"mode selection changed another provider's axis or reused rate landmarks for cumulative");
        Console.WriteLine("Axis snapping: shared time mapping, provider-local stages, cumulative/rate strategies isolated, and mode changes invalidate only the relevant landmark semantics.");
        chart.View=null;Check(Card() is null,"release leaves overlay");
        Check(!overlay.IsHitTestVisible&&!overlay.Focusable,"overlay steals pointer or keyboard input");
        Console.WriteLine("Chart inspection: both graphs, single-provider/collapsed, rate/cumulative, edge positions, source-only values, no active-plot overlap, pinned-source identity, leave/clear/release and noninteractive overlay passed.");
    }
}
