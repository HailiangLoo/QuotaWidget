using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
            Console.WriteLine("PASS: raw model lifecycle survives provider union; actual Render and rapid provider switches merge only post-Opus Fable work, retaining concurrent work and removing the stale painted total stroke.");
        }
        finally{Set("_snapshotTokens",null);Set("_trendActivity",null);Set("_modelActivity",null);Directory.Delete(root,true);}
    }
}
