using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuotaWidget.App;
using QuotaWidget.Core;

static class CreditsChartProbe
{
    public static void Run(string? output=null)
    {
        var oldLanguage=Loc.Language;var dark=Theme.IsDark;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        void Check(bool ok,string why){if(!ok)throw new Exception("Credits chart: "+why);}
        var t=DateTimeOffset.Parse("2026-10-20T12:00:00+08:00");
        SeriesData Quota()=>new(){Key=SeriesKey.Total,Segments=[new(){Start=t,End=t.AddHours(1),Delta=4}]};
        SeriesData Credits(double factor)=>new(){Key=SeriesKey.Credits,Segments=[
            new(){Start=t,End=t.AddMinutes(10),Delta=20*factor},
            new(){Start=t.AddMinutes(10),End=t.AddMinutes(20),Delta=30*factor},
            new(){Start=t.AddMinutes(20),End=t.AddMinutes(30),Issue=SegmentIssue.Reset,Label="余额增加",Group=1},
            new(){Start=t.AddMinutes(30),End=t.AddMinutes(45),Delta=0,Group=1},
            new(){Start=t.AddMinutes(45),End=t.AddMinutes(60),Delta=5*factor,Group=1}]};
        var chart=new RateChart{Margin=new Thickness(8,160,8,0),VerticalAlignment=VerticalAlignment.Top};
        var overlay=new ChartInspectOverlay();chart.AttachInspectOverlay(overlay);
        var host=new Grid{Height=600,Background=Theme.Brush("Bg")};host.Children.Add(chart);host.Children.Add(overlay);
        object? Call(string name,params object[] args)=>typeof(RateChart).GetMethod(name,flags)!.Invoke(chart,args);
        T Property<T>(object value,string name)=>(T)value.GetType().GetProperty(name)!.GetValue(value)!;
        IEnumerable<GeometryDrawing> Shapes(Drawing drawing)=>drawing is GeometryDrawing shape?[shape]:drawing is DrawingGroup group?group.Children.SelectMany(Shapes):[];
        try
        {
            foreach(var theme in new[]{true,false})
            foreach(var language in new[]{"zh-CN","en"})
            foreach(var width in new[]{216d,300d,600d})
            foreach(var cumulative in new[]{false,true})
            foreach(var height in new[]{110d,200d})
            {
                Theme.Apply(theme);Loc.Configure(language);host.Background=Theme.Brush("Bg");
                Rect[]? primary=null;
                foreach(var factor in new[]{1d,1000d})
                {
                    chart.View=new(){Start=t,End=t.AddHours(1),Total=Quota(),Fable=Quota(),Codex=Quota(),Gaps=[],Smooth=false,
                        TotalVisible=false,FableVisible=false,CodexVisible=true,CodexCumulativeMode=cumulative,Credits=Credits(factor)};
                    chart.FitHeight(height);host.Width=width;host.Measure(new Size(width,600));host.Arrange(new Rect(0,0,width,600));host.UpdateLayout();
                    var scale=(ChartScale)typeof(RateChart).GetField("CreditScale",flags)!.GetValue(chart)!;
                    var labels=(IReadOnlyList<(double Value,FormattedText Text,Rect Bounds)>)Call("AxisLabels",0,scale,true)!;
                    Check(labels.Count>=2,"right ticks disappeared at small height");
                    Check(labels.All(l=>l.Bounds.Right<=chart.ActualWidth&&l.Bounds.Left>chart.ActualWidth/2),"right ticks outside plot");
                    Check(labels.Zip(labels.Skip(1),(a,b)=>!a.Bounds.IntersectsWith(b.Bounds)).All(x=>x),"right labels overlap");
                    Check(labels.Last().Text.Text.EndsWith(cumulative?" cr":" cr/h"),"mode units wrong");
                    var plotRight=(double)typeof(RateChart).GetProperty("PlotRight",flags)!.GetValue(chart)!;
                    Check(labels.All(l=>l.Bounds.Left>plotRight),"credit-axis label can hide the newest interval");
                    Check(Property<string>(Call("Header",0)!,"Right").Contains(" cr"),"mixed-use headline omits credits");
                    var shapes=Shapes(VisualTreeHelper.GetDrawing(chart)).ToArray();
                    var quotaShapes=shapes.Where(s=>s.Pen?.Brush==Theme.Brush("Violet")).Select(s=>s.Geometry.Bounds).ToArray();
                    Check(quotaShapes.Length>0,"quota stroke lost");
                    if(primary is null)primary=quotaShapes;else Check(primary.SequenceEqual(quotaShapes),"credits changed left scale");
                    var creditShapes=shapes.Where(s=>s.Pen?.Brush==Theme.Brush("Credits")&&s.Pen.DashStyle.Dashes.Count>0).ToArray();
                    Check(creditShapes.Length==2,"topup gap joined or valid interval lost");
                    Check(creditShapes.All(s=>s.Brush is null),"credits added a misleading shared-unit area");
                    chart.PinInspect(t.AddMinutes(5),true);
                    var card=Call("BuildInspectCard",overlay);Check(card is not null,"readout lost");
                    var rows=Property<IEnumerable>(card!,"Rows").Cast<object>().ToArray();
                    Check(rows.Length==2&&Property<string>(rows[1],"Value").EndsWith(cumulative?" cr":" cr/h"),"credit readout units absent");
                    chart.PinInspect(t.AddMinutes(25),true);
                    rows=Property<IEnumerable>(Call("BuildInspectCard",overlay)!,"Rows").Cast<object>().ToArray();
                    Check(Property<string>(rows[1],"Value")==Loc.T("余额增加"),"topup invented a numeric readout");
                    chart.ClearInspect();
                }
                if(output is not null&&width==300&&height==200&&language=="zh-CN")
                {
                    Directory.CreateDirectory(output);
                    var bitmap=new RenderTargetBitmap((int)width,600,96,96,PixelFormats.Pbgra32);bitmap.Render(host);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream=File.Create(Path.Combine(output,$"credits-{(theme?"dark":"light")}-{(cumulative?"cumulative":"rate")}.png"));encoder.Save(stream);
                }
            }
            // Exercise the complete quota/credit path with continuous work over
            // exhaustion and a reset, including the actual drawn paths and hover.
            HistoryRecord R(int minute,double used,decimal balance,bool reset=false)=>new(CodexUsageSource.SourceId,"fixture","Codex pro",t.AddMinutes(minute),Statuses.Partial,300,
                new("fixture-"+minute,t.AddMinutes(minute),new(null,UsageParser.Limit(used,t.AddDays(reset?14:7)),null),new(true,false,balance)));
            var records=new[]{R(0,99,500),R(5,100,500),R(10,100,450),R(15,0,425,true),R(20,1,425,true),R(25,1,425,true),R(30,1,425,true)};
            var resetQuota=RateEngine.Build(records,SeriesKey.Total,[]);var resetCredits=CreditsUsage.Build(records,[]);
            chart.View=new(){Start=t,End=t.AddMinutes(30),Total=resetQuota,Fable=resetQuota,Codex=resetQuota,Credits=resetCredits,Gaps=[],Smooth=true,
                TotalVisible=false,FableVisible=false,CodexVisible=true,Activity=new([],[],[new(t,t.AddMinutes(30),true,false,null)])};
            chart.FitHeight(180);host.Width=300;host.Measure(new Size(300,600));host.Arrange(new Rect(0,0,300,600));host.UpdateLayout();
            var lane=((IEnumerable)typeof(RateChart).GetField("_lanes",flags)!.GetValue(chart)!).Cast<object>().Single();
            var paths=(List<IReadOnlyList<TrendPoint>>)typeof(RateChart).GetMethod("Points",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[lane,chart.View])!;
            Check(paths.Count==2&&!paths.Any(p=>p[0].Time<t.AddMinutes(15)&&p[^1].Time>t.AddMinutes(5)),"quota stroke/fill crosses credits period or reset");
            chart.PinInspect(t.AddMinutes(7),true);
            var resetRows=Property<IEnumerable>(Call("BuildInspectCard",overlay)!,"Rows").Cast<object>().ToArray();
            Check(Property<string>(resetRows[0],"Value")==RatePresentation.Estimate(0)&&Property<string>(resetRows[1],"Value")=="600 cr/h","capped hover disagrees with independent credit mean");
            chart.PinInspect(t.AddMinutes(12),true);
            resetRows=Property<IEnumerable>(Call("BuildInspectCard",overlay)!,"Rows").Cast<object>().ToArray();
            Check(Property<string>(resetRows[0],"Value")==Loc.T("重置")&&Property<string>(resetRows[1],"Value")=="300 cr/h","reset crossing erased real credit observations or fabricated quota");
            chart.ClearInspect();
            Console.WriteLine("PASS: credit-funded work, quota reset paths/fills and hover remain separate.");
            // Regression: credits start in the final five minutes of a narrow
            // chart. Its full stroke must stay outside the right-axis labels.
            foreach(var width in new[]{216d,258d,360d})
            foreach(var cumulative in new[]{false,true})
            {
                var lateCredits=new SeriesData{Key=SeriesKey.Credits,Segments=[
                    new(){Start=t,End=t.AddMinutes(50),Issue=SegmentIssue.Gap},
                    new(){Start=t.AddMinutes(55),End=t.AddMinutes(60),Delta=22.57195,Group=1}]};
                var switched=new SeriesData{Key=SeriesKey.Total,Segments=[
                    new(){Start=t,End=t.AddMinutes(45),Delta=4},
                    new(){Start=t.AddMinutes(45),End=t.AddMinutes(50),Issue=SegmentIssue.ProfileChange,Label="Codex promax → Codex free",Group=1},
                    new(){Start=t.AddMinutes(50),End=t.AddHours(1),Delta=0,Group=1}]};
                chart.View=new(){Start=t,End=t.AddHours(1),Total=Quota(),Fable=Quota(),Codex=switched,Credits=lateCredits,
                    Gaps=[],Smooth=false,TotalVisible=false,FableVisible=false,CodexVisible=true,CodexCumulativeMode=cumulative};
                chart.FitHeight(110);host.Width=width;host.Measure(new Size(width,600));host.Arrange(new Rect(0,0,width,600));host.UpdateLayout();
                var header=Property<string>(Call("Header",0)!,"Right");
                Check(header.Contains("22.6 cr*"),"new credit consumption or coverage marker missing from headline");
                var scale=(ChartScale)typeof(RateChart).GetField("CreditScale",flags)!.GetValue(chart)!;
                var labels=(IReadOnlyList<(double Value,FormattedText Text,Rect Bounds)>)Call("AxisLabels",0,scale,true)!;
                var strokes=Shapes(VisualTreeHelper.GetDrawing(chart)).Where(s=>s.Pen?.Brush==Theme.Brush("Credits")).ToArray();
                Check(strokes.Length>0&&strokes.All(s=>labels.All(l=>s.Geometry.Bounds.Right<l.Bounds.Left)),"late credit stroke obscured by axis");
                var boxes=Shapes(VisualTreeHelper.GetDrawing(chart)).Where(s=>s.Brush==Theme.Brush("Bg")).Select(s=>s.Geometry.Bounds).ToArray();
                Check(strokes.All(s=>boxes.All(b=>s.Geometry.GetWidenedPathGeometry(s.Pen).FillContainsWithDetail(new RectangleGeometry(b))==IntersectionDetail.Empty)),
                    $"caption hides late credit stroke at width {width}, cumulative={cumulative}");
                var zero=new SeriesData{Key=SeriesKey.Total,Segments=[new(){Start=t,End=t.AddHours(1),Delta=0}]};
                chart.View=new(){Start=t,End=t.AddHours(1),Total=zero,Fable=zero,Codex=zero,Credits=lateCredits,
                    Gaps=[],Smooth=false,TotalVisible=false,FableVisible=false,CodexVisible=true,CodexCumulativeMode=cumulative};
                var only=Property<string>(Call("Header",0)!,"Right");
                Check(only.Contains("22.6 cr*")&&!only.Contains("+"),"credit-only range is still headlined as subscription points");
            }
            chart.View=new(){Start=t,End=t.AddHours(1),Total=Quota(),Fable=Quota(),Codex=Quota(),Gaps=[],Smooth=false,TotalVisible=true,FableVisible=false,CodexVisible=false,Credits=Credits(1)};
            Check(!(bool)typeof(RateChart).GetProperty("HasCreditCurve",flags)!.GetValue(chart)!,"Claude-only showed credits");
            Console.WriteLine("PASS: credits independent scale, sampled gaps, right-axis spacing, both units/readouts and provider visibility across 96 width/height/theme/language cases.");
        }
        finally{Theme.Apply(dark);Loc.Configure(oldLanguage);}
    }
}
