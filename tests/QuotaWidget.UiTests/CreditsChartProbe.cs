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
            chart.View=new(){Start=t,End=t.AddHours(1),Total=Quota(),Fable=Quota(),Codex=Quota(),Gaps=[],Smooth=false,TotalVisible=true,FableVisible=false,CodexVisible=false,Credits=Credits(1)};
            Check(!(bool)typeof(RateChart).GetProperty("HasCreditCurve",flags)!.GetValue(chart)!,"Claude-only showed credits");
            Console.WriteLine("PASS: credits independent scale, sampled gaps, right-axis spacing, both units/readouts and provider visibility across 96 width/height/theme/language cases.");
        }
        finally{Theme.Apply(dark);Loc.Configure(oldLanguage);}
    }
}
