using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using QuotaWidget.App;
using QuotaWidget.Core;

static class CompactCreditsProbe
{
    public static void Run(App app, string? output = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "qw-compact-credits-" + Guid.NewGuid().ToString("N"));
        void Check(bool ok, string why) { if (!ok) throw new Exception("Compact credits: " + why); }
        try
        {
            foreach (var scenario in new[] { "quota", "credits", "mixed", "missing", "unlimited", "topup", "stale", "short" })
            foreach (var five in new[] { false, true })
            {
                var now = DateTimeOffset.Now.AddSeconds(-1);
                var paths = new DataPaths(Path.Combine(root, scenario + five));
                var cxPaths = new DataPaths(Path.Combine(paths.Root, "codex"));
                var store = new HistoryStore(cxPaths, false);
                HistoryRecord? last = null;
                for (var minute = -70; minute <= 0; minute += 5)
                {
                    if (scenario == "short" && minute < -35) continue;
                    var at = now.AddMinutes(minute - (scenario == "stale" ? 120 : 0));
                    var reset = scenario == "mixed" && minute >= -15;
                    double used = scenario == "quota" ? minute < -15 ? 10 : minute == -15 ? 11 : minute == -10 ? 12 : minute == -5 ? 13 : 15
                        : reset ? minute == -15 ? 0 : minute == -10 ? 1 : minute == -5 ? 3 : 5 : 100;
                    decimal? balance = (scenario == "topup" && minute >= -35 ? 2000m : 1000m) - (scenario == "quota" || minute < -25 ? 0 : minute == -25 ? 100 : minute == -20 ? 200 : 325);
                    if (scenario == "missing" && minute < 0 || scenario == "unlimited") balance = null;
                    last = new(CodexUsageSource.SourceId, "fixture", "Codex pro", at, Statuses.Partial, 300,
                        new("s" + minute, at, new(five ? UsageParser.Limit(used == 100 ? 100 : 20, now.AddHours(4)) : null,
                            UsageParser.Limit(used, now.AddDays(reset ? 14 : 7)), null), new(true, scenario == "unlimited", balance)));
                    store.Append(last, now);
                }
                AtomicFile.WriteAllText(cxPaths.Latest, SnapshotJson.WriteEnvelope(new(1,last!.SourceId,last.ProfileKey,last.PlanLabel,last.T,last.Status,300,null,null,last.Snapshot)));
                var model = new WidgetModel(paths,false) { ReadOnly=true }; model.Initialize();
                var codex = new WidgetModel(cxPaths,false) { ReadOnly=true }; codex.Initialize();
                model.Settings.SetupCompleted=true;model.Settings.CollectorEnabled=true;model.Settings.CodexConnected=true;model.Settings.ClaudeConnected=true;
                model.Settings.CompactMode=true;model.Settings.Width=model.Settings.CompactWidth=240;model.Settings.CacheRemindersEnabled=false;model.Settings.TokenTrackingEnabled=false;
                if(scenario=="short")model.Settings.PollIntervalSeconds=3600;
                var opts=AppOptions.Parse(["--compact","--snapshot","unused"]);
                void Set(string name,object? value)=>typeof(App).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app,value);
                Set("_opts",opts);Set("_paths",paths);Set("_model",model);Set("_codex",codex);Set("_tokens",null);Set("_snapshotTokens",null);
                Set("_chatHistory",new ChatSessionHistory(paths.Root));Set("_currentChatSession",null);Set("_cacheEntries",Array.Empty<ChatCacheEntry>());
                var window=new MainWindow(app,model,codex,opts);Set("_window",window);
                T C<T>(string name)=>(T)window.FindName(name);
                foreach(var language in new[]{"zh-CN","en"})
                foreach(var mode in new[]{"both","codex","claude","both"})
                {
                    Loc.Configure(language);Translate.RefreshResources();model.Settings.Language=language;model.Settings.Monitoring=mode;
                    window.Render();
                    var content=(FrameworkElement)window.Content;content.Measure(new Size(window.Width,double.PositiveInfinity));content.Arrange(new Rect(0,0,window.Width,content.DesiredSize.Height));content.UpdateLayout();
                    if(mode=="claude"){Check(C<Grid>("CreditsLine").Visibility==Visibility.Collapsed,"Claude-only showed credits");continue;}
                    var text=C<TextBlock>("CompactCodexRateValue").Text;
                    if(scenario=="quota")Check(text=="5.0"&&C<TextBlock>("CreditsUsedText").Visibility==Visibility.Visible,"quota-only changed semantics or retained credits layout");
                    else
                    {
                        Check(C<TextBlock>("CompactCodexRateUnit").Visibility==Visibility.Collapsed&&C<TextBlock>("CreditsUsedText").Visibility==Visibility.Collapsed,"amounts labelled /h or duplicated");
                        Check(C<TextBlock>("CompactCodexRateHeading").Text.Contains(Loc.T("近1h")),"missing visible time range");
                        if(scenario=="credits")Check(text=="325 cr",$"credit-only total wrong: {text}; {language}/{mode}/5h={five}");
                        if(scenario=="mixed")Check(text=="5"+Loc.T("点")+"* + 325 cr","mixed/reset accounting wrong: "+text);
                        if(scenario=="missing"||scenario=="unlimited")Check(text=="— cr*","unknown credits fabricated: "+text);
                        if(scenario=="topup")Check(text=="325 cr*","topup offset or coverage hidden: "+text);
                        if(scenario=="short")Check(text=="0"+Loc.T("点")+"* + 325 cr*","short coverage hidden by slow polling: "+text);
                        if(scenario=="stale")Check(text.Contains("—")&&text.Contains("*"),"stale usage became fresh zero");
                        Check(C<StackPanel>("CompactCodexRate").ActualHeight<=C<Grid>("CompactRates").ActualHeight||mode=="codex","summary clipped vertically");
                        if(mode=="codex"&&!five)Check(C<TextBlock>("CodexInlineRateValue").Text==text,"single-provider summary differs");
                    }
                    if(output is not null&&mode is "both" or "codex"&&scenario is "credits" or "mixed")
                    {
                        Directory.CreateDirectory(output);window.SaveSnapshot(Path.Combine(output,$"{scenario}-{language}-{mode}-5h{five}.png"),1);
                    }
                    model.Settings.CompactMode=false;window.Render();Check(C<TextBlock>("CreditsUsedText").Visibility==Visibility.Visible,"full view lost credit total");
                    model.Settings.CompactMode=true;window.Render();
                }
                window.Close();
            }
            Console.WriteLine("PASS: compact credits-only/mixed/reset, missing/unlimited/topup/stale data, units, duplicate removal and full/single/both/language transitions.");
        }
        finally { Loc.Configure("zh-CN");Translate.RefreshResources();Directory.Delete(root,true); }
    }
}
