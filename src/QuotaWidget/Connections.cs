using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Microsoft.Win32;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public partial class App
{
    readonly Dictionary<ChatPlatform,bool> _connectionChecks=new();
    readonly Dictionary<ChatPlatform,bool> _dependencies=new();
    readonly Dictionary<ChatPlatform,DateTimeOffset> _dependencyCheckedAt=new();
    readonly Dictionary<ChatPlatform,DateTimeOffset> _verifyAfter=new();
    public ConnectionStatus Connection(ChatPlatform platform)
    {
        var latest=(platform==ChatPlatform.Claude?_model:_codex).LastEnvelope;
        if(latest is not null&&Statuses.HasSnapshot(latest.Status)&&_dependencyCheckedAt.TryGetValue(platform,out var checkedAt)&&latest.AttemptedAt>checkedAt)
            _dependencies[platform]=true;
        if(_verifyAfter.TryGetValue(platform,out var after)&&latest?.AttemptedAt<after)latest=null;
        return Connections.Describe(platform,_model.Settings,latest,
            _opts.Demo?true:_dependencies.TryGetValue(platform,out var ready)?ready:null,DateTimeOffset.Now,_connectionChecks.GetValueOrDefault(platform));
    }
    public void CompleteSetup()
    {
        _model.Settings.SetupCompleted=true;_model.Settings.CollectorEnabled=true;
        _model.SaveSettings();
        foreach(var platform in new[]{ChatPlatform.Claude,ChatPlatform.Codex})
            if(_model.Settings.Listens(platform))_ = CheckConnection(platform);
    }
    public void ToggleConnection(ChatPlatform platform)
    {
        var s=_model.Settings;var was=s.Listens(platform)&&s.CollectorEnabled;
        if(platform==ChatPlatform.Claude)s.ClaudeConnected=!was;else s.CodexConnected=!was;
        if(!was)
        {
            s.CollectorEnabled=true;
            if(!s.Monitors(platform))s.Monitoring="both";
            _verifyAfter[platform]=DateTimeOffset.Now;
        }
        var model=platform==ChatPlatform.Claude?_model:_codex;
        if(s.SetupCompleted)model.RecordEvent(new AppEvent(DateTimeOffset.Now,was?EventTypes.MonitorPause:EventTypes.MonitorResume));
        model.ReleaseArchive();_trendActivity=null;_modelActivity=null;
        _model.SaveSettings();
        if(!was)_ = CheckConnection(platform);
        Render();
    }
    public async Task CheckConnection(ChatPlatform platform)
    {
        if(_opts.Demo||_opts.Snapshot is not null||_connectionChecks.GetValueOrDefault(platform))return;
        _connectionChecks[platform]=true;_window.RenderConnections();
        try
        {
            var configured=_model.Settings.ClaudeExePath;
            var ready=await Task.Run(()=>platform==ChatPlatform.Claude?ClaudeCli.Resolve(configured).Usable:CodexUsageSource.Resolve() is not null);
            _dependencies[platform]=ready;
            _dependencyCheckedAt[platform]=DateTimeOffset.Now;
            if(ready&&_model.Settings.Collects(platform))
            {
                if(platform==ChatPlatform.Claude&&_collector is not null)
                    await Task.Run(()=>_collector.CollectOnceAsync(_cts.Token,CollectTrigger.Manual));
                else if(platform==ChatPlatform.Codex&&_codexCollector is not null)
                    await Task.Run(()=>_codexCollector.RecheckConnectionAsync(_cts.Token));
                (platform==ChatPlatform.Claude?_model:_codex).PollLatest();
            }
        }
        catch(OperationCanceledException) { }
        catch { _window.Flash(Loc.T("检查暂未完成，请稍后重试。")); }
        finally{_connectionChecks[platform]=false;if(!_exiting){Render();_window.RenderConnections();}}
    }
    public void OpenProviderLogin(ChatPlatform platform)
    {
        if(_opts.Demo||_opts.Snapshot is not null)return;
        _verifyAfter[platform]=DateTimeOffset.Now;
        try
        {
            if(platform==ChatPlatform.Claude)
            {
                var choice=ClaudeCli.Resolve(_model.Settings.ClaudeExePath);
                if(choice.Usable)LaunchLogin();else OpenUrl("https://code.claude.com/docs/en/setup");
            }
            else if(OfficialAppLaunch.CodexTarget() is { } target)
            {
                var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")){UseShellExecute=true};
                start.ArgumentList.Add("shell:AppsFolder\\"+target);Process.Start(start);
            }
            else OpenUrl("https://learn.chatgpt.com/docs/app");
        }
        catch { _window.Flash(Loc.T("未能打开官方程序，请按安装说明手动打开。")); }
    }
    static void OpenUrl(string url)=>Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
}

public static class OfficialAppLaunch
{
    public static string? CodexTarget()
    {
        try
        {
            using var packages=Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            foreach(var name in (packages?.GetSubKeyNames()??[]).Where(n=>n.StartsWith("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)).OrderDescending())
            {
                using var package=packages!.OpenSubKey(name);
                if(package?.GetValue("PackageRootFolder") is not string root||!Path.IsPathFullyQualified(root))continue;
                var manifest=XDocument.Load(Path.Combine(root,"AppxManifest.xml"));
                var application=manifest.Descendants().FirstOrDefault(e=>e.Name.LocalName=="Application"&&
                    e.Attribute("Executable") is { } file&&Path.GetFileName(file.Value.Replace('/',Path.DirectorySeparatorChar)) is "ChatGPT.exe" or "Codex.exe");
                var id=application?.Attribute("Id")?.Value;
                if(application?.Attribute("Executable")?.Value is not string executable)continue;
                var full=Path.GetFullPath(Path.Combine(root,executable.Replace('/',Path.DirectorySeparatorChar)));
                if(!full.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
                    !Authenticode.IsSignedBy(full,CodexUsageSource.Signer))continue;
                var target="OpenAI.Codex_"+name.Split('_')[^1]+"!"+id;
                if(id is not null&&Regex.IsMatch(target,@"^[A-Za-z0-9._-]+![A-Za-z0-9._-]+$"))return target;
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Xml.XmlException){ }
        return null;
    }
}

public sealed class ConnectionCard : Border
{
    public TextBlock State {get;}=new(){FontSize=11};
    public TextBlock Detail {get;}=new(){FontSize=10,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,4)};
    public Button Login {get;}=new();
    public Button Check {get;}=new();
    public Button Toggle {get;}=new();
    readonly ChatPlatform _platform;
    public ConnectionCard(ChatPlatform platform,Action login,Action check,Action toggle)
    {
        _platform=platform;CornerRadius=new CornerRadius(7);Padding=new Thickness(8);Margin=new Thickness(0,5,0,0);
        SetResourceReference(BackgroundProperty,"Raised");
        var body=new StackPanel();var header=new DockPanel();
        DockPanel.SetDock(State,Dock.Right);header.Children.Add(State);
        var name=new TextBlock{Text=platform==ChatPlatform.Claude?"Claude Code":"Codex / ChatGPT",FontWeight=FontWeights.SemiBold,FontSize=12};
        name.SetResourceReference(TextBlock.ForegroundProperty,platform==ChatPlatform.Claude?"Claude":"Violet");header.Children.Add(name);
        body.Children.Add(header);Detail.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(Detail);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal};
        foreach(var button in new[]{Login,Check,Toggle})
        {button.Style=(Style)Application.Current.FindResource("FlatButton");button.FontSize=10;button.Padding=new Thickness(4,3,4,3);button.Margin=new Thickness(0,0,4,0);buttons.Children.Add(button);}
        Login.Click+=(_,_)=>login();Check.Click+=(_,_)=>check();Toggle.Click+=(_,_)=>toggle();
        body.Children.Add(buttons);Child=body;
    }
    public void Render(ConnectionStatus status,WidgetSettings settings)
    {
        State.Text=status.Text;State.SetResourceReference(TextBlock.ForegroundProperty,status.Color);Detail.Text=status.Detail;
        Login.Content=Loc.T(_platform==ChatPlatform.Claude?"登录 / 安装":"打开 / 安装");
        Login.ToolTip=Loc.T(_platform==ChatPlatform.Claude?"使用官方 Claude 登录；未安装时打开安装说明。":"在官方 Codex / ChatGPT 应用中登录 ChatGPT 账号。");
        Check.Content=Loc.T("检查连接");Toggle.Content=Loc.T(settings.Listens(_platform)&&settings.CollectorEnabled?"断开":"连接");
        Toggle.ToolTip=Loc.T("断开仅停止挂件监听，不退出官方账号或删除历史。");
        Toggle.Visibility=settings.SetupCompleted?Visibility.Visible:Visibility.Collapsed;
    }
}

public partial class MainWindow
{
    ConnectionCard _claudeConnection=null!,_codexConnection=null!;
    string? DisconnectedNote()
    {
        var s=_model.Settings;
        var names=new[]{ChatPlatform.Claude,ChatPlatform.Codex}.Where(p=>s.Monitors(p)&&!s.Connected(p)).ToArray();
        return names.Length==0?null:string.Join(" / ",names)+" · "+Loc.T("已断开");
    }
    void InitializeConnections()
    {
        ConnectionCard Card(ChatPlatform platform)=>new(platform,()=>_app.OpenProviderLogin(platform),()=>_ = _app.CheckConnection(platform),()=>
        {
            _app.ToggleConnection(platform);_suppress=true;
            MonitoringCombo.SelectedIndex=Array.IndexOf(new[]{"both","claude","codex"},_model.Settings.Monitoring);_suppress=false;
            RenderConnections();RenderCache();
        });
        ConnectionRows.Children.Add(_claudeConnection=Card(ChatPlatform.Claude));
        ConnectionRows.Children.Add(_codexConnection=Card(ChatPlatform.Codex));
        RenderConnections();
    }
    public void RenderConnections()
    {
        if(_claudeConnection is null)return;
        var s=_model.Settings;
        SetupIntro.Visibility=SetupStart.Visibility=s.SetupCompleted?Visibility.Collapsed:Visibility.Visible;
        AppearanceSettings.Visibility=s.SetupCompleted?Visibility.Visible:Visibility.Collapsed;
        SettingsTitle.Text=Loc.T(s.SetupCompleted?"显示设置":"连接平台");
        _claudeConnection.Visibility=!s.SetupCompleted&&!s.Monitors(ChatPlatform.Claude)?Visibility.Collapsed:Visibility.Visible;
        _codexConnection.Visibility=!s.SetupCompleted&&!s.Monitors(ChatPlatform.Codex)?Visibility.Collapsed:Visibility.Visible;
        _claudeConnection.Render(_app.Connection(ChatPlatform.Claude),s);
        _codexConnection.Render(_app.Connection(ChatPlatform.Codex),s);
    }
    void SetupStart_Click(object sender,RoutedEventArgs e){_app.CompleteSetup();RenderConnections();ShowSettings(false);_app.Render();}
}
