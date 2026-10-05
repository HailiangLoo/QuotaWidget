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
            typeof(MainWindow).GetMethod("BackButton_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[window,new RoutedEventArgs()]);
            Check(model.Settings.CompactMode&&window.Width==264&&C<StackPanel>("MainPanel").Visibility==Visibility.Visible,"return did not restore compact mode");
            window.Close();
            Console.WriteLine("PASS: first-use setup gates collection, preserves explicit provider choice, saves completion; compact gear/settings/back retains compact mode; disconnect/reconnect emits one event per action.");
        }
        finally{Directory.Delete(root,true);}
    }
}
