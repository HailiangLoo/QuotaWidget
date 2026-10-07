using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QuotaWidget.App;
using QuotaWidget.Core;

static class ConnectionProbe
{
    public static void Run(App app)
    {
        var root=Path.Combine(Path.GetTempPath(),"qw-setup-ui-"+Guid.NewGuid());
        try
        {
            var model=new WidgetModel(new DataPaths(root),false);model.Initialize();model.Settings.Language="zh-CN";model.Settings.SetupCompleted=false;model.Settings.CompactMode=true;
            var codex=new WidgetModel(new DataPaths(Path.Combine(root,"codex")),false){ReadOnly=true};codex.Initialize();
            var opts=AppOptions.Parse(["--snapshot","unused"]);
            void Set(string key,object? value)=>typeof(App).GetField(key,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app,value);
            Set("_opts",opts);Set("_model",model);Set("_codex",codex);Set("_paths",model.Paths);Set("_tokens",null);Set("_chatHistory",new ChatSessionHistory(root));Set("_currentChatSession",null);
            var window=new MainWindow(app,model,codex,opts);Set("_window",window);
            T C<T>(string key)=>(T)window.FindName(key);
            void Check(bool yes,string why){if(!yes)throw new Exception(why);}
            void Click(string key)=>C<ButtonBase>(key).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(C<StackPanel>("SettingsPanel").Visibility==Visibility.Visible&&C<TextBlock>("SetupIntro").Visibility==Visibility.Visible,"first launch skipped setup");
            Check(!model.Settings.Listens(ChatPlatform.Claude)&&!model.Settings.Listens(ChatPlatform.Codex),"setup enabled readers");
            C<ComboBox>("MonitoringCombo").SelectedIndex=2;
            Check(model.EventList.Count==0&&codex.EventList.Count==0,"choosing provider during setup wrote pause/resume events");
            Click("SetupStart");
            Check(model.Settings.SetupCompleted&&model.Settings.Listens(ChatPlatform.Codex)&&!model.Settings.Listens(ChatPlatform.Claude),"setup ignored provider choice");
            Check(WidgetSettings.Load(model.Paths.Settings,out _).SetupCompleted,"setup not saved");
            Check(model.Settings.CompactMode&&C<Button>("SettingsButton").Visibility==Visibility.Visible,"compact settings gear absent");
            Click("SettingsButton");Check(model.Settings.CompactMode&&C<StackPanel>("SettingsPanel").Visibility==Visibility.Visible,"opening settings changed mode");
            var cards=C<StackPanel>("ConnectionRows").Children.Cast<ConnectionCard>().ToArray();
            var count=codex.EventList.Count;cards[1].Toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!model.Settings.CodexConnected&&cards[1].State.Text=="已断开"&&codex.EventList.Count==count+1,"disconnect state or event wrong");
            cards[1].Toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(model.Settings.CodexConnected&&codex.EventList.Count==count+2,"reconnect duplicated lifecycle events");
            Check(cards.All(c=>c.Login.Content.ToString()=="登录"),"connection cards retain install/open actions");
            var displayCount=model.EventList.Count+codex.EventList.Count;
            C<ComboBox>("MonitoringCombo").SelectedIndex=1;
            Check(model.Settings.CodexConnected&&model.Settings.Collects(ChatPlatform.Codex)&&cards[1].Toggle.Content.ToString()=="断开","hiding Codex changed its connection/toggle state");
            cards[0].Toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(model.Settings.ClaudeConnected&&model.Settings.Monitoring=="claude","connecting changed the display selection");
            displayCount++;
            foreach(var choice in new[]{2,0,1,2})C<ComboBox>("MonitoringCombo").SelectedIndex=choice;
            Check(model.Settings.ClaudeConnected&&model.Settings.CodexConnected&&model.EventList.Count+codex.EventList.Count==displayCount,"display switching disconnected accounts or wrote pause/resume events");
            cards[0].Toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!model.Settings.ClaudeConnected&&model.Settings.Monitoring=="codex"&&cards[0].State.Text=="已断开","hidden account disconnect changed display or affected another account");
            cards[0].Toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(model.Settings.ClaudeConnected&&model.Settings.Monitoring=="codex","hidden account reconnect forced both mode");
            var stamp=DateTimeOffset.Now;
            foreach(var sourceModel in new[]{model,codex})
                typeof(WidgetModel).GetProperty("LastEnvelope")!.SetValue(sourceModel,new LatestEnvelope(1,"fixture","fixture","test",stamp,Statuses.Partial,300,null,null,
                    new("fixture",stamp,new(null,UsageParser.Limit(12,stamp.AddDays(7)),null))));
            displayCount=model.EventList.Count+codex.EventList.Count;
            foreach(var choice in new[]{1,2,0})
            {
                C<ComboBox>("MonitoringCombo").SelectedIndex=choice;
                Check(cards.All(c=>c.State.Text=="已连接"&&c.Toggle.Content.ToString()=="断开"),"display selection masked a verified connection");
            }
            Check(model.EventList.Count+codex.EventList.Count==displayCount,"verified connections gained fake display-switch gaps");
            C<ComboBox>("LanguageCombo").SelectedIndex=2;
            Check(cards.All(c=>c.State.Text=="Connected"&&c.Login.Content.ToString()=="Sign in"),"English cards changed connection semantics or retained install labels");
            C<ComboBox>("LanguageCombo").SelectedIndex=1;
            typeof(MainWindow).GetMethod("BackButton_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[window,new RoutedEventArgs()]);
            Check(model.Settings.CompactMode&&window.Width==264&&C<StackPanel>("MainPanel").Visibility==Visibility.Visible,"return did not restore compact mode");
            window.Close();
            Console.WriteLine("PASS: first-use setup gates collection, preserves explicit provider choice, saves completion; compact gear/settings/back retains compact mode; disconnect/reconnect emits one event per action.");
        }
        finally{Directory.Delete(root,true);}
    }
}
