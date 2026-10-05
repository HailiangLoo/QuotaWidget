using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using QuotaWidget.App;
using QuotaWidget.Core;

static class LocalizationProbe
{
    public static void Run(App app)
    {
        var root=Path.Combine(Path.GetTempPath(),"qw-language-ui-"+Guid.NewGuid());
        try
        {
            var now=DateTimeOffset.Now;var paths=new DataPaths(Path.Combine(root,"demo"));
            DemoData.Generate(paths,now,"codex-plus");
            var model=new WidgetModel(paths,true);model.Initialize();model.Settings.Language="zh-CN";
            var codexPaths=new DataPaths(Path.Combine(root,"codex","demo"));DemoData.Generate(codexPaths,now,"codex-plus",codex:true);
            var codex=new WidgetModel(codexPaths,true){ReadOnly=true};codex.Initialize();
            var opts=AppOptions.Parse(["--demo","--snapshot","unused"]);
            void Set(string name,object? value)=>typeof(App).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(app,value);
            Set("_opts",opts);Set("_model",model);Set("_codex",codex);Set("_paths",paths);Set("_tokens",null);
            Set("_chatHistory",new ChatSessionHistory(root));Set("_currentChatSession",null);Set("_window",null);
            var window=new MainWindow(app,model,codex,opts);Set("_window",window);window.Render();
            T C<T>(string name)=>(T)window.FindName(name);
            void Check(bool yes,string why){if(!yes)throw new Exception(why);}
            void Layout(){var content=(FrameworkElement)window.Content;content.Measure(new Size(window.Width,double.PositiveInfinity));content.Arrange(new Rect(0,0,window.Width,content.DesiredSize.Height));content.UpdateLayout();}
            void Click(string name)=>C<ButtonBase>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var before=model.EventList.Count+codex.EventList.Count;
            foreach(var language in new[]{"en","zh-CN","en"})
            {
                C<ComboBox>("LanguageCombo").SelectedIndex=language=="en"?2:1;Layout();
                Check(WidgetSettings.Load(paths.Settings,out _).Language==language,"selection not persisted");
                Check(C<ToggleButton>("RateMode").Content.ToString()==(language=="en"?"Rate":"速率"),"static resource failed to refresh");
                Check(C<TextBlock>("DisplayLabelText").Text==(language=="en"?"Demo %":"演示 %"),"dynamic label failed to refresh");
                Check(C<TextBlock>("ChartDiagnosticsText").Text.Contains(language=="en"?"Smoothing":"平滑"),"diagnostics failed to refresh");
                Check(C<StackPanel>("CacheRows").Children.Count>0,"chat list disappeared");
            }
            Check(model.EventList.Count+codex.EventList.Count==before,"language emitted monitoring events");
            using(var menu=new TrayMenu(()=>{},()=>{},()=>{}))
            {
                menu.Prepare(true,true);Check(menu.Items[0].Text=="Hide widget"&&menu.Items[1].Text=="Settings"&&menu.Items[2].Text=="Quit","tray not English");
                Loc.Configure("zh-CN");menu.Prepare(true,false);Check(menu.Items[0].Text=="显示小窗"&&menu.Items[1].Text=="设置","tray round trip failed");
                Loc.Configure("en");
            }
            foreach(var width in new[]{240,300,340})
            foreach(var mode in new[]{"both","claude","codex"})
            {
                model.Settings.Width=width;model.Settings.Monitoring=mode;model.Settings.CompactMode=false;
                typeof(MainWindow).GetMethod("ApplyCompactLayout",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);
                window.Render();Layout();
                foreach(var name in new[]{"RateMode","CumulativeMode","CodexRateMode","CodexCumulativeMode"})
                {
                    var button=C<ToggleButton>(name);
                    var text=new FormattedText(button.Content.ToString()!,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(button.FontFamily,button.FontStyle,button.FontWeight,button.FontStretch),button.FontSize,Brushes.White,1);
                    Check(text.Width<button.Width,"chart mode text clipped: "+name);
                }
                var chart=C<RateChart>("Chart");
                foreach(var provider in new[]{false,true})if(chart.ModeBounds(provider) is { } bounds)
                    Check(bounds.Left>=0&&bounds.Right<=chart.ActualWidth-21,$"mode overlaps collapse control: width={width}, mode={mode}, compact={model.Settings.CompactMode}, chart={chart.ActualWidth}, bounds={bounds}");
                Click("CompactButton");Layout();Check(window.Width==264&&C<StackPanel>("CompactDetails").Visibility==Visibility.Visible,"compact layout changed");
                Click("CompactButton");Layout();
            }
            window.OpenSettings();Layout();
            var languageBox=C<ComboBox>("LanguageCombo");
            Check(languageBox.ActualWidth>=90&&languageBox.ActualHeight>0,"language selector unusable");
            foreach(var source in Loc.Keys)
                Check((string)app.Resources["Loc."+source]==Loc.T(source),"resource dictionary is stale");
            C<ComboBox>("LanguageCombo").SelectedIndex=1;Layout();
            Check(C<ToggleButton>("CumulativeMode").Content.ToString()=="累计","open settings did not restore Chinese");
            Check(C<StackPanel>("SettingsPanel").Visibility==Visibility.Visible,"language switch closed settings");
            Click("HistoryButton");Layout();
            Check(C<TextBlock>("HistoryMonthTitle").Text.Contains("月"),"Chinese history date missing");
            C<ComboBox>("LanguageCombo").SelectedIndex=2;Layout();
            Check(!C<TextBlock>("HistoryMonthTitle").Text.Contains("月"),"cached calendar did not change language");
            window.Close();
            Console.WriteLine("PASS: language persists and switches live across XAML, dynamic labels, diagnostics, cached chat rows and tray; 240/300/340 widths, all providers, compact/full; no monitoring events.");
        }
        finally{Loc.Configure("zh-CN");Translate.RefreshResources();Directory.Delete(root,true);}
    }
}
