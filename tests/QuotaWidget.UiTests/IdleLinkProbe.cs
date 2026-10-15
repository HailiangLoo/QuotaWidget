using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuotaWidget.App;
using QuotaWidget.Core;

static class IdleLinkProbe
{
    public static void Run()
    {
        var t=DateTimeOffset.Parse("2026-10-08T07:00:00+08:00");
        var source=new SeriesData { Key=SeriesKey.Total, Segments=Enumerable.Range(0,18).Select(i=>new RateSegment
            { Start=t.AddMinutes(i*5),End=t.AddMinutes((i+1)*5),Delta=i is 2 or 5 or 10 or 13?1:0 }).ToList() };
        WorkSpan[] work=[new(t,t.AddMinutes(30),true,true,null),new(t.AddMinutes(40),t.AddMinutes(75),true,true,null)];
        ChartView View(int minutes,bool cumulative=false,bool smooth=true)=>new()
        {
            Start=t,End=t.AddMinutes(90),Total=source,Fable=new(){Key=SeriesKey.Fable,Segments=[]},Codex=source,Gaps=[],
            Smooth=smooth,Cumulative=cumulative,IdleLinkMinutes=minutes,TotalVisible=false,FableVisible=false,Activity=new([],[],work)
        };
        var chart=new RateChart { Width=260,Margin=new Thickness(0,100,0,0),VerticalAlignment=VerticalAlignment.Top };
        var overlay=new ChartInspectOverlay();chart.AttachInspectOverlay(overlay);
        var host=new Grid { Width=260,Height=500 };host.Children.Add(chart);host.Children.Add(overlay);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        void Check(bool ok,string why){if(!ok)throw new Exception("Idle links UI: "+why);}
        T Field<T>(object o,string name)=>(T)o.GetType().GetProperty(name)!.GetValue(o)!;
        System.Collections.Generic.IEnumerable<GeometryDrawing> Shapes(Drawing d)
        {
            if(d is GeometryDrawing shape)yield return shape;
            if(d is DrawingGroup group)foreach(var child in group.Children)foreach(var s in Shapes(child))yield return s;
        }
        foreach(var smooth in new[]{false,true})foreach(var cumulative in new[]{false,true})foreach(var minutes in new[]{0,5,10,15})
        {
            chart.View=View(minutes,cumulative,smooth);
            host.Measure(new Size(260,500));host.Arrange(new Rect(0,0,260,500));host.UpdateLayout();
            var links=Shapes(VisualTreeHelper.GetDrawing(chart)).Where(s=>s.Pen?.DashStyle.Dashes.Count>0).ToArray();
            var expected=smooth&&!cumulative&&minutes>=10;
            Check(links.Length==(expected?1:0),"incorrect mode/threshold rendering");
            Check(links.All(s=>s.Brush is null),"idle link has consumption fill");
            chart.PinInspect(t.AddMinutes(35),true);
            var card=typeof(RateChart).GetMethod("BuildInspectCard",flags)!.Invoke(chart,[overlay])!;
            var value=Field<string>(Field<IEnumerable>(card,"Rows").Cast<object>().Single(),"Value");
            Check(value==Loc.T("空闲 · 仅连接")==expected,"idle hover falsely reports interpolated rate");
            if(expected)
            {
                var lane=((IEnumerable)typeof(RateChart).GetField("_lanes",flags)!.GetValue(chart)!).Cast<object>().Single();
                var points=(System.Collections.Generic.List<System.Collections.Generic.IReadOnlyList<TrendPoint>>)typeof(RateChart).GetMethod("Points",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[lane,chart.View])!;
                Check(!points.SelectMany(p=>p.Zip(p.Skip(1))).Any(p=>p.First.Time==p.Second.Time&&(p.First.Time==t.AddMinutes(30)||p.First.Time==t.AddMinutes(40))),"inner vertical edges retained");
            }
        }
        Console.WriteLine("PASS: short idle links paint dashed/unfilled, retain idle hover and obey threshold/smoothing/cumulative modes without vertical edge strokes.");
    }
}
