using System;
using System.IO;
using System.Linq;
using System.Reflection;
using QuotaWidget.App;
using QuotaWidget.Core;

static class HistoricalRateProbe
{
    public static void Run(App app)
    {
        var root=Path.Combine(Path.GetTempPath(),"qw-old-rate-ui-"+Guid.NewGuid().ToString("N"));
        var fields=new[]{"_opts","_model","_codex","_paths","_tokens","_snapshotTokens","_chatHistory","_currentChatSession","_window","_trendActivity"};
        var saved=fields.ToDictionary(k=>k,k=>typeof(App).GetField(k,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(app));
        var language=Loc.Language;
        TokenStore? store=null;
        try
        {
            var paths=new DataPaths(root);new WidgetSettings{SetupCompleted=true,Language=language,Monitoring="codex",RangeMinutes=ChartRanges.Month}.Save(paths.Settings);
            var cx=new DataPaths(Path.Combine(root,"codex"));new WidgetSettings{LastSourceId=CodexUsageSource.SourceId,LastProfileKey="p"}.Save(cx.Settings);
            var now=DateTimeOffset.Now;var old=now.AddDays(-20);
            var dir=new HistoryStore(cx,false).ProfileDir(CodexUsageSource.SourceId,"p");Directory.CreateDirectory(dir);
            void Row(DateTimeOffset t,double used)
            {
                var r=new HistoryRecord(CodexUsageSource.SourceId,"p","Codex pro",t,Statuses.Partial,3600,new(t.ToString("O"),t,new(null,UsageParser.Limit(used,now.AddDays(7)),null)));
                File.AppendAllText(Path.Combine(dir,t.ToLocalTime().ToString("yyyy-MM-dd")+".jsonl"),HistoryStore.FormatLine(r)+"\n");
            }
            Row(old,10);Row(old.AddHours(1),12);Row(now.AddHours(-2),30);Row(now.AddHours(-1),32);
            store=new TokenStore(Path.Combine(root,"tokens.sqlite"));
            foreach(var (stream,start) in new[]{("old",old),("new",now.AddHours(-2))})
            {store.PutWorkEvent("Codex",new(stream,start,"start",stream,null));store.PutWorkEvent("Codex",new(stream,start.AddHours(1),"end",stream,null));}
            var model=new WidgetModel(paths,false){ReadOnly=true};model.Initialize();
            var codex=new WidgetModel(cx,false){ReadOnly=true};codex.Initialize();
            var opts=AppOptions.Parse(["--snapshot","unused","--expanded"]);
            void Set(string name,object? value)=>typeof(App).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(app,value);
            Set("_opts",opts);Set("_model",model);Set("_codex",codex);Set("_paths",paths);Set("_tokens",null);Set("_snapshotTokens",store);Set("_trendActivity",null);
            Set("_chatHistory",new ChatSessionHistory(root));Set("_currentChatSession",null);
            var window=new MainWindow(app,model,codex,opts);Set("_window",window);
            void Check(bool yes,string why){if(!yes)throw new Exception(why);}
            foreach(var range in new[]{ChartRanges.Month,ChartRanges.All})
            {
                model.Settings.RangeMinutes=range;window.Render();
                var chart=((RateChart)window.FindName("Chart")).View!;
                Check(chart.CodexTrend!.Runs.Any(r=>r.Delta>0&&r.Points.Any(p=>p.Time<now-WidgetModel.Lookback&&p.Rate>0)),"long chart counted old usage but failed to paint its rate");
                Check(RateEngine.SumRange(chart.Codex!,chart.Start,chart.End).Delta==4,"history rate read changed quota accounting");
            }
            Check(app.WorkTrendActivity(now).Codex.All(s=>s.Start>=now-WidgetModel.Lookback),"long chart cache leaked into the compact/live activity scope");
            model.Settings.CodexChartMode="cumulative";
            Check(app.WorkTrendActivity(now,old).Codex.All(s=>s.Start>=now-WidgetModel.Lookback),"cumulative view loaded unneeded archive activity");
            model.Settings.CodexChartMode="rate";
            window.Render();
            Check(((RateChart)window.FindName("Chart")).View!.CodexTrend!.Runs.Any(r=>r.Delta>0&&r.Points.Any(p=>p.Time<old.AddHours(2))),"switching back reused the short activity cache");
            window.Close();
            Console.WriteLine("PASS: actual long-range Render paints twenty-day-old rate paths, conserves totals, and isolates month/all activity caches from compact/live reads.");
        }
        finally
        {
            store?.Dispose();foreach(var p in saved)typeof(App).GetField(p.Key,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(app,p.Value);
            Loc.Configure(language);Translate.RefreshResources();Directory.Delete(root,true);
        }
    }
}
