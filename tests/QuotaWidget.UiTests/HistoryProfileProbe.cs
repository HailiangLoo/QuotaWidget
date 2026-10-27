using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using QuotaWidget.App;
using QuotaWidget.Core;

static class HistoryProfileProbe
{
    public static void Run(App app,string? output=null)
    {
        var root=Path.Combine(Path.GetTempPath(),"qw-profile-ui-"+Guid.NewGuid().ToString("N"));
        var language=Loc.Language;
        var saved=new[]{"_opts","_model","_codex","_paths","_tokens","_chatHistory","_currentChatSession","_window"}
            .ToDictionary(k=>k,k=>typeof(App).GetField(k,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(app));
        Directory.CreateDirectory(root);
        try
        {
            var paths=new DataPaths(root);new WidgetSettings{SetupCompleted=true,Language=language}.Save(paths.Settings);
            var cx=new DataPaths(Path.Combine(root,"codex"));
            new WidgetSettings{LastSourceId=CodexUsageSource.SourceId,LastProfileKey="new"}.Save(cx.Settings);
            var now=DateTimeOffset.Now;var first=now.AddMinutes(-55);
            void Write(string profile,string plan,DateTimeOffset at,double used)
            {
                var r=new HistoryRecord(CodexUsageSource.SourceId,profile,plan,at,Statuses.Partial,300,
                    new(at.ToString("O"),at,new(null,UsageParser.Limit(used,now.AddDays(7)),null)));
                var dir=new HistoryStore(cx,false).ProfileDir(r.SourceId,profile);Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir,at.ToLocalTime().ToString("yyyy-MM-dd")+".jsonl"),HistoryStore.FormatLine(r)+"\n");
            }
            for(var i=0;i<10;i++)Write("old","Codex plus",first.AddMinutes(i*5),10+i);
            Write("new","Codex promax",now.AddMinutes(-5),38);Write("new","Codex promax",now,42);
            var model=new WidgetModel(paths,false){ReadOnly=true};model.Initialize();model.Settings.CompactMode=false;
            var codex=new WidgetModel(cx,false){ReadOnly=true};codex.Initialize();
            var opts=AppOptions.Parse(["--snapshot","unused"]);
            void Set(string key,object? value)=>typeof(App).GetField(key,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(app,value);
            Set("_opts",opts);Set("_model",model);Set("_codex",codex);Set("_paths",paths);Set("_tokens",null);
            Set("_chatHistory",new ChatSessionHistory(root));Set("_currentChatSession",null);
            var window=new MainWindow(app,model,codex,opts);Set("_window",window);
            T C<T>(string key)=>(T)window.FindName(key);
            void Check(bool yes,string why){if(!yes)throw new Exception(why);}
            C<Button>("HistoryButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            string Summary()=>string.Concat(C<TextBlock>("MonthQuotaSummary").Inlines.OfType<Run>().Select(r=>r.Text));
            void Snapshot(string name)
            {
                if(output is null)return;
                Directory.CreateDirectory(output);
                var content=(FrameworkElement)window.Content;
                content.Measure(new Size(window.Width,760));content.Arrange(new Rect(0,0,window.Width,760));content.UpdateLayout();
                window.SaveSnapshot(Path.Combine(output,name+".png"),2);
            }
            Check(window.FindName("HistoryCodexProfile") is null,"archive still requires profile switching");
            Check(Summary().Contains("Codex 9 / 4"),"all-history calendar lost a profile or combined its units");
            window.Render();
            Check(Summary().Contains("Codex 9 / 4")&&C<TextBlock>("HistoryProfileNote").Visibility==Visibility.Visible,"render lost timeline or boundary legend");
            Snapshot("synthetic-calendar");
            Check(codex.ProfileKey=="new"&&codex.BuildView(now).SummaryTotal==4,"archive changed live model");
            model.Settings.RangeMinutes=ChartRanges.All;model.Settings.Smoothing=false;
            C<Button>("HistoryButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));window.Render();
            var timeline=C<RateChart>("Chart").View!.Codex!;
            Check(timeline.Segments.Any(s=>s.ProfileKey=="old"&&s.Valid)&&timeline.Segments.Any(s=>s.ProfileKey=="new"&&s.Valid),"full chart omitted old or current history");
            Check(timeline.Segments.Any(s=>s.Issue==SegmentIssue.ProfileChange),"full chart has no profile boundary");
            Snapshot("synthetic-timeline");
            model.Settings.CodexChartMode="cumulative";window.Render();
            Check(C<RateChart>("Chart").View!.CodexCumulative!.Segments.Last().To==4,"cumulative full chart crossed subscription units");
            Snapshot("synthetic-cumulative");
            window.Close();
            Console.WriteLine("PASS: calendar and full chart show all profiles together, expose subscription boundaries, retain separate amounts, and leave live meters/collection unchanged.");
        }
        finally
        {
            foreach(var pair in saved)typeof(App).GetField(pair.Key,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(app,pair.Value);
            Loc.Configure(language);Translate.RefreshResources();
            Directory.Delete(root,true);
        }
    }
}
