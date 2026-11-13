using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuotaWidget.App;
using QuotaWidget.Core;

static class FableTransitionProbe
{
    public static void Run(App app)
    {
        var root=Path.Combine(Path.GetTempPath(),"qw-fable-transition-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        void Set(string name,object? value)=>typeof(App).GetField(name,flags)!.SetValue(app,value);
        void Check(bool ok,string why){if(!ok)throw new Exception("Fable transition: "+why);}
        try
        {
            var now=DateTimeOffset.Now;
            var opts=AppOptions.Parse(["--snapshot","unused"]);
            var model=new WidgetModel(new DataPaths(root),false){ReadOnly=true};model.Initialize();
            model.Settings.CompactMode=false;model.Settings.ClaudeChartMode="rate";model.Settings.RangeMinutes=300;
            var codex=new WidgetModel(new DataPaths(Path.Combine(root,"codex")),false){ReadOnly=true};codex.Initialize();
            using var store=new TokenStore(Path.Combine(root,"tokens.sqlite"));
            void Event(string stream,int minutes,string kind,string id,string? name=null)=>store.PutWorkEvent("Claude",new(stream,now.AddMinutes(minutes),kind,id,name));
            Event("opus",-120,"input","u1");Event("opus",-119,"activity","r1","claude-opus-5-5");Event("opus",-30,"finish","r1","claude-opus-5-5");
            Event("fable",-35,"input","u2");Event("fable",-34,"activity","r2","claude-fable-5-1");
            // Keep lifecycle evidence aligned with the recent request below: this
            // fixture represents continuing Fable work, not an abandoned response.
            Event("fable",-20,"activity","r2","claude-fable-5-1");
            store.Put(new("Claude","r1","opus","claude-opus-5-5",now.AddMinutes(-40),10,20,30));
            store.Put(new("Claude","r2","fable","claude-fable-5-1",now.AddMinutes(-20),10,20,30));
            for(var i=0;i<=24;i++)
            {
                var at=now.AddMinutes(-120+i*5);
                var snapshot=new QuotaSnapshot("fixture-"+i,at,new(null,UsageParser.Limit(10+(i>=12?1:0)+(i>=21?1:0),now.AddDays(4)),UsageParser.Limit(i>=21?2:0,now.AddDays(4))));
                model.Records.Add(new("claude-oauth-usage","fixture","Max (5x)",at,Statuses.Partial,300,snapshot));
            }
            Set("_opts",opts);Set("_model",model);Set("_codex",codex);Set("_paths",model.Paths);Set("_chatHistory",new ChatSessionHistory(root));
            Set("_tokens",null);Set("_snapshotTokens",store);Set("_trendActivity",null);Set("_modelActivity",null);
            var window=new MainWindow(app,model,codex,opts);Set("_window",window);
            var chart=(RateChart)window.FindName("Chart");
            var combo=(ComboBox)window.FindName("MonitoringCombo");
            foreach(var mode in new[]{0,1,2,0})
            {
                combo.SelectedIndex=mode;window.Render();var view=chart.View!;
                if(mode==2){Check(view.Activity.ClaudeModels.Count==0,"disabled provider retained model ownership");continue;}
                Check(view.Activity.Claude.Count==1&&view.Activity.ClaudeModels.Count==2,"provider union erased raw concurrent model identity");
                Check(view.MergesFable&&view.FableOnlyAt(now.AddMinutes(-10)),"real Render path retained the old Opus smoothing veto");
                Check(!view.FableOnlyAt(now.AddMinutes(-32)),"overlapping Opus work was hidden");
                var lanes=((IEnumerable)typeof(RateChart).GetField("_lanes",flags)!.GetValue(chart)!).Cast<object>();
                var totalLane=lanes.Single(l=>(string)l.GetType().GetProperty("Name")!.GetValue(l)! == "Claude");
                var paths=(List<IReadOnlyList<TrendPoint>>)typeof(RateChart).GetMethod("Points",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[totalLane,view])!;
                Check(!paths.Any(p=>p.Any(v=>v.Time>now.AddMinutes(-30)&&v.Rate>0)),"painted total line continued into pure Fable work");
            }
            window.Close();
            VerifyBoundaryRendering();
            Console.WriteLine("PASS: raw model lifecycle survives provider union; actual Render and rapid provider switches merge only post-Opus Fable work, retaining concurrent work and removing the stale painted total stroke.");
        }
        finally{Set("_snapshotTokens",null);Set("_trendActivity",null);Set("_modelActivity",null);Directory.Delete(root,true);}
    }

    static void VerifyBoundaryRendering()
    {
        var t=DateTimeOffset.Parse("2026-10-01T12:00:00+08:00");
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        void Check(bool ok,string why){if(!ok)throw new Exception("Fragment rendering: "+why);}
        var total=new RateTrend();var fable=new RateTrend();
        // A nearly level estimate is not a peak just because work starts/stops.
        TrendRun Run(double a,double b,double value)=>new([new(t.AddMinutes(a),value),new(t.AddMinutes((a+b)/2),value*.995),new(t.AddMinutes(b),value)],value*(b-a)/60,30,true,true);
        total.Runs.Add(Run(0,10,9));total.Runs.Add(Run(10,30,2));
        fable.Runs.Add(Run(0,10,7));fable.Runs.Add(Run(29,31,7));
        var source=new SeriesData{Key=SeriesKey.Total,Segments=[new(){Start=t,End=t.AddMinutes(40),Delta=3}]};
        var view=new ChartView{Start=t,End=t.AddMinutes(40),Smooth=true,Total=source,Fable=source,Codex=source,Gaps=[],FableToClaudeFactor=.5,
            FableOnlySpans=[new(t,t.AddMinutes(5)),new(t.AddMinutes(30),t.AddMinutes(31))]};
        typeof(ChartView).GetField("_totalTrend",flags)!.SetValue(view,total);
        typeof(ChartView).GetField("_fableTrend",flags)!.SetValue(view,fable);
        typeof(ChartView).GetField("_codexTrend",flags)!.SetValue(view,fable);
        Check(view.FableAt(t.AddMinutes(29.5))>view.TotalAt(t.AddMinutes(29.5)),"independent mixed-model estimate was hidden at its crossing");
        foreach(var width in new[]{140d,300d,600d})
        {
            var chart=new RateChart{Width=width,View=view};chart.Measure(new Size(width,500));chart.Arrange(new Rect(0,0,width,chart.DesiredSize.Height));chart.UpdateLayout();
            var lanes=((IEnumerable)typeof(RateChart).GetField("_lanes",flags)!.GetValue(chart)!).Cast<object>();
            foreach(var lane in lanes)
            {
                var name=(string)lane.GetType().GetProperty("Name")!.GetValue(lane)!;
                var paths=(List<IReadOnlyList<TrendPoint>>)typeof(RateChart).GetMethod("Points",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[lane,view])!;
                Check(paths.SelectMany(p=>p.Zip(p.Skip(1))).All(p=>p.First.Time!=p.Second.Time||p.First.Rate==p.Second.Rate),name+" painted area closure as a rate spike");
                Check(PeakLabels.Find(paths).Count==0,name+" labelled a lifecycle edge as a peak");
                if(name=="Fable")Check(paths.Any(p=>p[0].Time==t.AddMinutes(29)&&p[^1].Time==t.AddMinutes(31)),"crossing discarded part of the supported short turn");
                if(name=="Claude")Check(paths[0][0].Time==t.AddMinutes(5),"exclusive Fable retained a redundant Claude stroke");
            }
            IEnumerable<GeometryDrawing> Shapes(Drawing d)=>d is GeometryDrawing shape?[shape]:d is DrawingGroup group?group.Children.SelectMany(Shapes):[];
            var shapes=Shapes(VisualTreeHelper.GetDrawing(chart)).ToArray();
            Check(shapes.Any(s=>s.Brush is LinearGradientBrush&&s.Pen is null&&s.Geometry.Bounds.Height>20),"removing edge strokes lost the area fill");
        }
        Console.WriteLine("PASS: Claude/Fable/Codex strokes and peaks exclude area-closing edges; clipped short Fable tails, exclusive sharing and area fills survive at three widths.");
    }
}
