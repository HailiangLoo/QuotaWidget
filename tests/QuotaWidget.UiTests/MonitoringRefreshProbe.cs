using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QuotaWidget.App;
using QuotaWidget.Core;

static class MonitoringRefreshProbe
{
    public static void Run(App app)
    {
        var root=Path.Combine(Path.GetTempPath(),"qw-mode-refresh-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var now=DateTimeOffset.Now;
            var opts=AppOptions.Parse(["--snapshot","unused"]);
            var model=new WidgetModel(new DataPaths(root),false){ReadOnly=true};model.Initialize();model.Settings.CompactMode=false;
            model.Settings.ClaudeChartMode=model.Settings.CodexChartMode="rate";model.Settings.RangeMinutes=300;
            model.Settings.ClaudeChartCollapsed=model.Settings.CodexChartCollapsed=false;
            var codex=new WidgetModel(new DataPaths(Path.Combine(root,"codex")),false){ReadOnly=true};codex.Initialize();
            var logs=Path.Combine(root,"cx","sessions");Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs,"rollout-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl"),
                JsonSerializer.Serialize(new{timestamp=now.AddHours(-3),type="event_msg",payload=new{type="task_started",turn_id="turn"}})+"\n");
            using var index=new TokenIndex(Path.Combine(root,"idx"),Path.Combine(root,"cx"),Path.Combine(root,"cl"),now);
            index.Poll(now);
            void Set(string name,object? value)=>typeof(App).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(app,value);
            Set("_opts",opts);Set("_model",model);Set("_codex",codex);Set("_paths",model.Paths);Set("_chatHistory",new ChatSessionHistory(root));
            Set("_tokens",index);Set("_trendActivity",null);Set("_modelActivity",null);
            for(var i=0;i<=36;i++)
            {
                var at=now.AddMinutes(-180+i*5);var used=i<20?94+Math.Floor(i/4d):Math.Floor((i-20)/4d);
                var snapshot=new QuotaSnapshot("fixture-"+i,at,new(null,UsageParser.Limit(used,now.AddDays(i<20?4:7)),null));
                codex.Records.Add(new(CodexUsageSource.SourceId,"fixture","Codex Plus",at,Statuses.Partial,300,snapshot));
            }
            var window=new MainWindow(app,model,codex,opts);Set("_window",window);
            T C<T>(string name)=>(T)window.FindName(name);
            void Check(bool yes,string why){if(!yes)throw new Exception(why);}
            void Click(string name)=>C<ButtonBase>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.Render();var probeAt=now.AddMinutes(-75);var baseline=C<RateChart>("Chart").View!.CodexAt(probeAt);
            Check(baseline>0,"baseline continuity unavailable");
            // All transitions happen within the activity cache's five-second lifetime.
            foreach(var mode in new[]{1,0,2,0,1,2,0})
            {
                C<ComboBox>("MonitoringCombo").SelectedIndex=mode;
                var view=C<RateChart>("Chart").View!;
                if(mode!=1)Check(view.Activity.Codex.Count>0&&view.CodexAt(probeAt)==baseline,"rapid mode switch reused empty activity or changed reset rate");
                else Check(view.Activity.Codex.Count==0,"disabled provider retained visible activity");
            }
            File.WriteAllText(Path.Combine(logs,"rollout-bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl"),
                string.Concat(Enumerable.Repeat("{\"type\":\"ordinary\",\"body\":\""+new string('x',10000)+"\"}\n",800)));
            index.Poll(now,claude:false,codex:true);Check(index.PendingFiles>0,"backfill fixture was not pending");
            C<ComboBox>("MonitoringCombo").SelectedIndex=2;window.Render();
            var beforeClick=C<RateChart>("Chart").View!.CodexAt(probeAt);
            Click("CodexCumulativeMode");Click("CodexRateMode");
            Check(beforeClick==baseline&&C<RateChart>("Chart").View!.CodexAt(probeAt)==baseline,"clicking chart mode repairs a transiently broken rate");
            // Optional 5h capability follows data immediately, including removal.
            void Windows(bool five,bool week=true)
            {
                var last=codex.Records[^1];codex.Records[^1]=last with{Snapshot=last.Snapshot with{Limits=new(five?UsageParser.Limit(28,now.AddMinutes(155)):null,week?last.Snapshot.Limits.AllWeek:null,null)}};
                codex.RecordEvent(new(now,EventTypes.AppStart));window.Render();
            }
            Windows(true);
            Check(C<QuotaRing>("CodexFiveMeter").Visibility==Visibility.Visible&&!C<QuotaRing>("CodexMeter").SingleProvider,"Codex 5h did not become a separate gauge");
            C<ComboBox>("MonitoringCombo").SelectedIndex=0;
            model.Settings.Width=240;window.Width=264;window.Render();
            Check(C<Grid>("MeterGrid").RowDefinitions.Count==2,"narrow full view squeezes five gauges together");
            model.Settings.Width=300;window.Width=324;window.Render();
            Check(C<Grid>("MeterGrid").RowDefinitions.Count==0,"wide full view kept unnecessary quota rows");
            Click("CompactButton");
            Check(C<Grid>("MeterGrid").RowDefinitions.Count==2&&Grid.GetRow(C<QuotaRing>("CodexFiveMeter"))==1,"five compact quotas are squeezed into one row");
            Check(C<QuotaRing>("CodexFiveMeter").ShowPlatformIcon&&!C<QuotaRing>("CodexMeter").ShowPlatformIcon,"Codex icons duplicated");
            C<ComboBox>("MonitoringCombo").SelectedIndex=2;
            Check(C<Grid>("MeterGrid").RowDefinitions.Count==0&&C<Grid>("CodexCompactSummary").Visibility==Visibility.Collapsed,"single Codex dual quota kept wrong compact layout");
            Windows(false);
            Check(C<Grid>("CodexCompactSummary").Visibility==Visibility.Visible&&C<QuotaRing>("CodexFiveMeter").Visibility==Visibility.Collapsed,"week-only account retained a fabricated 5h window");
            Windows(true,false);
            Check(C<QuotaRing>("CodexMeter").Visibility==Visibility.Collapsed&&C<QuotaRing>("CodexFiveMeter").Visibility==Visibility.Visible,"5h-only account shown as weekly");
            Set("_tokens",null);window.Close();
            Console.WriteLine("PASS: actual WPF selector rapidly cycles Claude/both/Codex; activity cache follows mode immediately; backlog preserves reset trend; rate/cumulative clicks do not change it; optional 5h/week-only/5h-only expanded and compact layouts follow live capability.");
        }
        finally{Directory.Delete(root,true);}
    }
}
