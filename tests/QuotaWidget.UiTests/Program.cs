using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QuotaWidget.Core;
using QuotaWidget.App;

static class Probe
{
    [STAThread]
    static int Main()
    {
        // Exercise actual WPF handlers against isolated metadata. Never start App workers/auth.
        var root=Path.Combine(Path.GetTempPath(),"qw-compact-ui-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            // Dispatcher-based probes must never start collectors or open the user's data.
            var app=(App)Activator.CreateInstance(typeof(App),BindingFlags.Instance|BindingFlags.NonPublic,null,[false],null)!;
            app.InitializeComponent(); Theme.Apply(true);
            var opts=AppOptions.Parse(["--compact","--snapshot","unused"]);
            var model=new WidgetModel(new DataPaths(root),false){ReadOnly=true};model.Initialize();model.Settings.Language="zh-CN";model.Settings.CompactMode=true;model.Settings.Width=318;
            var codex=new WidgetModel(new DataPaths(Path.Combine(root,"codex")),false){ReadOnly=true};codex.Initialize();
            void Set(string name,object value)=>typeof(App).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(app,value);
            Set("_opts",opts);Set("_model",model);Set("_codex",codex);Set("_paths",model.Paths);Set("_chatHistory",new ChatSessionHistory(root));
            var now=DateTimeOffset.Now;
            ChatCacheEntry Entry(string id,int minutes,int ttl=30,bool running=false)=>new(ChatPlatform.Codex,id,id,now.AddMinutes(-minutes),ttl,"fixture",running);
            Set("_cacheEntries",new[]{Entry("urgent-x",26),Entry("urgent-c",51,60) with{Platform=ChatPlatform.Claude},Entry("running",80,running:true),Entry("idle",10),Entry("expired",80),Entry("compacted",1) with{Compacted=true,CompactedAt=now},Entry("unknown",1) with{ActivityUncertain=true}});
            var window=new MainWindow(app,model,codex,opts);Set("_window",window);window.Render();
            T Control<T>(string name)=>(T)window.FindName(name);
            void Check(bool ok,string why){if(!ok) throw new Exception(why);}
            void Click(string name)=>Control<ButtonBase>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            string Title(UIElement row)=>( ((StackPanel)((ToolTip)((FrameworkElement)row).ToolTip).Content).Children[0] as TextBlock)!.Text;
            var rows=Control<StackPanel>("CompactChatRows");
            Check(rows.Children.Count==4,"compact active list truncated");
            Check(rows.Children.Cast<UIElement>().Select(Title).SequenceEqual(new[]{"urgent-x","urgent-c","running","idle"}),"priority or archived filtering wrong");
            Check(Control<TextBlock>("CompactClaudeRateValue").Text=="—"&&Control<TextBlock>("CompactCodexRateValue").Text=="—","empty data became zero rate");
            Check(Control<TextBlock>("CompactClaudePoints").Text=="近1h · —","missing amount became zero");
            Set("_currentChatSession",new ChatSession{Id="compact-title",Start=now.AddHours(-2),End=now});window.RenderCache();
            var compactStart=Control<TextBlock>("SessionStartText").Text;
            Check(Control<TextBlock>("SessionStartText").Visibility==Visibility.Visible&&compactStart.Contains(now.AddHours(-2).ToLocalTime().ToString("HH:mm")),"compact session start is hidden or stale");
            void CompactAmount(params RateSegment[] segments)=>typeof(MainWindow).GetMethod("RenderCompactRate",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,
                new object[]{Control<StackPanel>("CompactClaudeRate"),Control<TextBlock>("CompactClaudeRateValue"),Control<TextBlock>("CompactClaudePoints"),"Claude",new SeriesData{Key=SeriesKey.Total,Segments=segments.ToList()},model,now});
            RateSegment S(int start,int end,double delta,SegmentIssue issue=SegmentIssue.None)=>new(){Start=now.AddMinutes(start),End=now.AddMinutes(end),Delta=delta,Issue=issue};
            CompactAmount(S(-120,-60,99),S(-60,0,7));Check(Control<TextBlock>("CompactClaudeRateValue").Text=="7.0"&&Control<TextBlock>("CompactClaudePoints").Text=="近1h · 7点","previous hour leaked into amount/rate");
            CompactAmount(S(-60,-30,90,SegmentIssue.Gap),S(-30,0,1.5));Check(Control<TextBlock>("CompactClaudeRateValue").Text=="—"&&Control<TextBlock>("CompactClaudePoints").Text=="近1h · —","half an hour presented as a full-hour estimate");
            CompactAmount(S(-10,0,1));Check(Control<TextBlock>("CompactClaudeRateValue").Text=="—"&&Control<TextBlock>("CompactClaudePoints").Text=="近1h · —","warm-up extrapolated");
            CompactAmount(S(-60,0,0));Check(Control<TextBlock>("CompactClaudeRateValue").Text=="0.0"&&Control<TextBlock>("CompactClaudePoints").Text=="近1h · 0点","observed zero became missing");
            var first=rows.Children[0];window.RenderCache();Check(ReferenceEquals(first,rows.Children[0]),"unchanged poll replaced hover rows");
            Check(!model.ArchiveLoaded&&!codex.ArchiveLoaded,"compact loaded all history");
            Click("CompactButton");Check(!model.Settings.CompactMode&&window.Width==342,"toggle did not expand at saved width");
            Check(Control<TextBlock>("SessionStartText").Text==compactStart,"compact/expanded use different session starts");
            Check(Control<RateChart>("Chart").ToolTip is null&&Control<FrameworkElement>("LegendBar").ToolTip is null,"blanket chart essay tooltip returned");
            Check(Control<ToggleButton>("CumulativeMode").ToolTip.ToString()!.Length<30,"mode button contains calculation essay");
            Click("CumulativeMode");
            Check(Control<RateChart>("Chart").View is {ClaudeCumulativeMode:true,CodexCumulativeMode:false},"Claude mode affected Codex");
            Click("CumulativeMode");Check(Control<ToggleButton>("CumulativeMode").IsChecked==true,"active mode became unselected");
            Click("CodexCumulativeMode");Click("RateMode");
            Check(Control<RateChart>("Chart").View is {ClaudeCumulativeMode:false,CodexCumulativeMode:true},"Codex/Claude choices are coupled");
            Click("CodexRateMode");
            Check(Control<ToggleButton>("RateMode").IsChecked==true&&Control<ToggleButton>("CodexRateMode").IsChecked==true,"rate buttons did not restore");
            Check(Control<UniformGrid>("RangeTabs").Children.Cast<ToggleButton>().Select(b=>b.Content.ToString()).SequenceEqual(new[]{"1h","5h","12h","24h","3d","all"}),"range labels or ordering wrong");
            Check(!Control<Expander>("TokenDiagnostics").IsExpanded&&!Control<Expander>("ChartCalculation").IsExpanded&&Control<TextBlock>("ChartDiagnosticsText").Text.Contains("平滑"),"calculation details lost or exposed by default");
            Check(Control<StackPanel>("CompactDetails").Visibility==Visibility.Collapsed&&Control<StackPanel>("CacheRows").Children.Count==4,"expanded mode changed active list");
            void Collapse(string name,bool value) {Control<ToggleButton>(name).IsChecked=value;Click(name);}
            var tokens=Control<Grid>("TokenPanel");
            Check(tokens.Visibility==Visibility.Visible,"expanded tokens missing");
            Collapse("ClaudeCollapseButton",true);
            Check(Control<TextBlock>("ClaudeInput").Visibility==Visibility.Collapsed&&tokens.RowDefinitions[1].Height.Value==0,"Claude row or its blank space retained");
            Check(Control<TextBlock>("CodexInput").Visibility==Visibility.Visible&&tokens.Visibility==Visibility.Visible,"Claude collapse hid Codex tokens");
            Collapse("CodexCollapseButton",true);Check(tokens.Visibility==Visibility.Collapsed,"both collapsed retained token block");
            Collapse("ClaudeCollapseButton",false);Check(tokens.Visibility==Visibility.Visible&&Control<TextBlock>("ClaudeInput").Visibility==Visibility.Visible&&Control<TextBlock>("CodexInput").Visibility==Visibility.Collapsed,"single-provider restore wrong");
            model.Settings.TokenTrackingEnabled=false;Collapse("CodexCollapseButton",false);Check(tokens.Visibility==Visibility.Collapsed,"expanding a chart enabled disabled token tracking");
            model.Settings.TokenTrackingEnabled=true;window.RenderTokens();Check(tokens.Visibility==Visibility.Visible&&tokens.RowDefinitions[1].Height.Value==22&&tokens.RowDefinitions[2].Height.Value==22,"full tokens did not restore");
            Click("CompactButton");Check(model.Settings.CompactMode&&window.Width==264,"toggle did not restore narrow mode");
            model.Settings.CacheRemindersEnabled=false;window.Render();Check(Control<Border>("CompactChats").Visibility==Visibility.Collapsed,"disabled reminders retained panel");
            model.Settings.CacheRemindersEnabled=true;Set("_cacheEntries",Array.Empty<ChatCacheEntry>());window.Render();
            Check(rows.Children.Count==0&&Control<TextBlock>("CompactChatsEmpty").Visibility==Visibility.Visible,"empty active list failed");
            Set("_cacheEntries",Enumerable.Range(0,20).Select(i=>Entry("working-"+i,80,running:true)).ToArray());window.Render();Check(rows.Children.Count==20,"large running list truncated");
            var retained=new ChatSession {Id="fixture",Start=now.AddMinutes(-2),End=now,Chats=[new SessionChat {Last=Entry("deleted-retained",1),FirstAt=now.AddMinutes(-2)}]};
            Set("_currentChatSession",retained);
            Set("_chatLifecycle",new ChatLifecycleSnapshot(new HashSet<string>{ChatLifecycleSnapshot.Key(ChatPlatform.Codex,"working-0"),ChatLifecycleSnapshot.Key(ChatPlatform.Codex,"deleted-retained")}));
            window.RenderCache();Check(rows.Children.Count==19,"compact lifecycle filter failed or retained journal row resurrected");
            Click("CompactButton");Check(Control<StackPanel>("CacheRows").Children.Count==19,"full list lifecycle filter differs");
            Check(retained.Chats.Count==1,"display filtering deleted history");
            Set("_chatLifecycle",ChatLifecycleSnapshot.Empty);window.RenderCache();Check(Control<StackPanel>("CacheRows").Children.Count==21,"restoration not rendered");
            Set("_currentChatSession",null!);Click("CompactButton");Check(rows.Children.Count==20,"retention fixture leaked");
            Console.WriteLine("Lifecycle UI: both layouts hide live and retained rows; restoration refreshes; journal remains intact.");
            var details=TokenBreakdown.Build(now.AddHours(-2),now,Enumerable.Range(0,80).Select(i=>new TokenSlice(i%2==0?"model-a":"model-b","chat-"+i,new TokenSummary(i+1,1000,7,0,1,0))).ToArray());
            var labels=new Dictionary<string,ChatCacheEntry>{{"chat-79",Entry("named chat",1) with{Title="可核对的标题",Project="项目"}}};
            var refreshed=0;var closed=0;
            var card=new PlatformUsageCard(ChatPlatform.Codex,details,labels,120,()=>refreshed++,()=>closed++,"本地测试");
            card.Measure(new Size(card.Width,double.PositiveInfinity));card.Arrange(new Rect(card.DesiredSize));card.UpdateLayout();
            Check(card.ModelRows.Children.Count==2&&card.ChatRows.Children.Count==80,"card hides a grouping or truncates entries");
            Check(card.ModelRows.Visibility==Visibility.Visible&&card.ChatRows.Visibility==Visibility.Visible,"grouping requires switching");
            var family=TokenBreakdown.Build(now.AddHours(-2),now,[new("Astra","parent",new(1,2,3,0,1,0)),new("Luna","child",new(10,20,30,0,1,0)),new("Astra","child",new(4,8,12,0,1,0)),new("Luna","child2",new(20,40,60,0,1,0))],new Dictionary<string,string>{{"child","parent"},{"child2","parent"}});
            var familyCard=new PlatformUsageCard(ChatPlatform.Codex,family,new Dictionary<string,ChatCacheEntry>{{"parent",Entry("Parent chat",1)}},120,()=>{},()=>{},"fixture");
            string ChatName(DockPanel name)=>name.Children.OfType<TextBlock>().Last().Text;
            familyCard.Measure(new Size(familyCard.Width,double.PositiveInfinity));familyCard.Arrange(new Rect(familyCard.DesiredSize));familyCard.UpdateLayout();
            Check(familyCard.ChatRows.Children.Count==2&&familyCard.ModelRows.Children.Count==2,"chat/model rows missing or raw agent rows retained");
            for(var i=0;i<2;i++)
            {
                var familyRow=(Grid)((Border)familyCard.ChatRows.Children[i]).Child;
                var name=(DockPanel)familyRow.Children[0];var slice=family.Chats.Single().ModelBreakdown[i];
                Check(ChatName(name)=="Parent chat","model row lost owning chat name");
                var modelLabel=(TextBlock)name.Children[1];
                Check(modelLabel.Text==" · "+slice.Model&&modelLabel.FontSize==14,"model missing from large chat heading");
                var visible=((TextBlock)name.Children[0]).Text;
                Check(!visible.Contains(slice.Model)&&visible.Contains($"{slice.Subagents} subagent"),"model retained in small metadata or subagents hidden: "+visible);
                Check(name.Children.OfType<TextBlock>().All(t=>Math.Abs(t.TransformToAncestor(name).Transform(new Point(0,t.ActualHeight/2)).Y-name.ActualHeight/2)<1)&&familyRow.ActualHeight<=32,"fields wrap or are not centered on one line");
                Check(familyRow.ToolTip is null,"nested breakdown tooltip retained");
                Check(familyRow.Children.OfType<TextBlock>().Where(t=>Grid.GetColumn(t)<4).Select(t=>t.Text).SequenceEqual(new[]{slice.Usage.Input,slice.Usage.Cached,slice.Usage.Output}.Select(TokenSummary.Number)),"model row shows mixed totals");
            }
            var multi=TokenBreakdown.Build(now.AddHours(-2),now,[new("Opus","chat-a",new(30,40,50,0,1,0)),new("Fable","chat-a",new(1,2,3,0,1,0)),new("Opus","chat-b",new(10,20,30,0,1,0))]);
            var multiCard=new PlatformUsageCard(ChatPlatform.Claude,multi,new Dictionary<string,ChatCacheEntry>{{"chat-a",Entry("Chat A",1)},{"chat-b",Entry("Chat B",1)}},120,()=>{},()=>{},"fixture");
            Check(multiCard.ChatRows.Children.Cast<Border>().Select(b=>ChatName((DockPanel)((Grid)b.Child).Children[0])).SequenceEqual(new[]{"Chat A","Chat A","Chat B"}),"models of one chat scattered or lost on Claude");
            Console.WriteLine("Family UI: each chat/model has an adjacent visible row, exact model counters and per-model agent counts; both platforms; no nested usage tooltip.");
            var topRow=(Grid)((Border)card.ChatRows.Children[0]).Child;
            Check(ChatName((DockPanel)topRow.Children[0])=="可核对的标题","chat title mapping failed");
            var header=(DockPanel)((StackPanel)card.Child).Children[0];
            Check(!header.Children.OfType<ComboBox>().Any(),"unrequested range switch retained");
            foreach(var button in header.Children.OfType<Button>()) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(refreshed==1&&closed==1,"card refresh/close actions failed");
            var hover=Control<StackPanel>("CompactCodexRate");
            hover.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.MouseEnterEvent});
            var openTimer=(System.Windows.Threading.DispatcherTimer)typeof(MainWindow).GetField("_usageOpenTimer",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            Check(openTimer.IsEnabled,"provider hover did not schedule card");
            hover.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.MouseLeaveEvent});Check(!openTimer.IsEnabled,"leaving provider did not cancel pending hover");
            typeof(MainWindow).GetMethod("CloseUsage",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);
            model.Settings.CompactMode=false;window.Render();
            var tokenGrid=Control<Grid>("TokenPanel");tokenGrid.Measure(new Size(290,double.PositiveInfinity));tokenGrid.Arrange(new Rect(0,0,290,tokenGrid.DesiredSize.Height));tokenGrid.UpdateLayout();
            foreach(var prefix in new[]{"Claude","Codex"})
            {
                var hit=Control<Border>(prefix+"TokenHitArea");var bound=hit.TransformToAncestor(tokenGrid).TransformBounds(new Rect(hit.RenderSize));
                foreach(var field in new[]{"TokenLabel","Input","Cached","Output"})
                {
                    var label=Control<TextBlock>(prefix+field);var point=label.TransformToAncestor(tokenGrid).Transform(new Point(label.ActualWidth/2,label.ActualHeight/2));
                    Check(bound.Contains(point),prefix+" row hover does not cover "+field);
                }
                hit.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.MouseEnterEvent});
                Check(openTimer.IsEnabled&&ReferenceEquals(typeof(MainWindow).GetField("_usageOwner",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window),hit),"whole row does not activate hover");
                typeof(MainWindow).GetMethod("CloseUsage",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);
            }
            PlatformUsageCard Make(int minutes)=>new(ChatPlatform.Codex,details,labels,minutes,()=>{},()=>{},"本地测试");
            var allCard=Make(0);typeof(MainWindow).GetField("_hoverCard",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,allCard);window.RenderTokens();
            Check(Control<TextBlock>("CodexInput").Text==TokenSummary.Number(details.Total.Input)&&Control<TextBlock>("ClaudeInput").Text=="—","hover snapshot mismatches table or leaks to another platform");
            typeof(MainWindow).GetField("_hoverCard",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,Make(120));window.RenderTokens();
            Check(Control<TextBlock>("CodexInput").Text=="—","snapshot from a different scope reused");
            typeof(MainWindow).GetMethod("CloseUsage",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);
            var windows=(Dictionary<ChatPlatform,UsageDetailWindow>)typeof(MainWindow).GetField("_usageWindows",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            var popup=(Popup)typeof(MainWindow).GetField("_usagePopup",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            void Pin(string name)
            {
                var click=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent};
                Control<FrameworkElement>(name).RaiseEvent(click);Check(click.Handled,"direct pin did not handle click");
                Check(!popup.IsOpen&&!openTimer.IsEnabled,"direct pin flashed or left a pending hover");
            }
            foreach(var source in new[]{ChatPlatform.Claude,ChatPlatform.Codex})
            {
                var name=source+"TokenHitArea";Pin(name);var fixedWindow=windows[source];
                Check(fixedWindow.Card.IsPinned&&fixedWindow.Card.Platform==source&&fixedWindow.Card.Minutes==0,"cold click pins wrong source/scope");
                Pin(name);Check(ReferenceEquals(fixedWindow,windows[source]),"repeat click duplicated fixed window");
                Control<Border>(name).RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.MouseEnterEvent});
                Check(!openTimer.IsEnabled,"pinned provider spawned another hover");
                fixedWindow.Close();
                Control<Border>(name).RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.MouseEnterEvent});
                Check(openTimer.IsEnabled,"closing fixed window did not restore hover");
                Pin(name);Check(!openTimer.IsEnabled,"click during hover delay failed");windows[source].Close();
            }
            var frozen=Make(0);popup.Child=frozen;
            typeof(MainWindow).GetField("_hoverCard",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,frozen);
            Pin("CodexTokenHitArea");Check(ReferenceEquals(windows[ChatPlatform.Codex].Card,frozen),"click discarded visible hover snapshot");
            model.Settings.CompactMode=true;window.Render();Pin("CompactCodexRate");
            Check(windows[ChatPlatform.Codex].Card.Minutes==0,"compact re-open reset the independent pinned scope");
            Check(windows.Values.All(w=>!w.IsVisible),"isolated handler probe displayed a native window");
            var hiddenSnapshot=windows[ChatPlatform.Codex].Card;
            window.RefreshPinnedUsage(now.AddDays(1));
            Check(ReferenceEquals(windows[ChatPlatform.Codex].Card,hiddenSnapshot),"app clock refreshed an invisible pinned window");
            foreach(var w in windows.Values.ToArray()) w.Close();
            Console.WriteLine("Direct pin: cold click, click during delay, hover snapshot reuse, per-platform reuse, pinned-hover suppression, close/reopen, compact 1h scope. No native windows shown.");
            Console.WriteLine("Recent amount: previous hour excluded / full 1h / partial / thin / zero / missing values; no extrapolation; shared compact/expanded start time.");
            var requestedRange=-1;var pinned=new UsageDetailWindow(ChatPlatform.Codex,allCard,m=>{requestedRange=m;return Make(m);}){Topmost=true};
            Check(allCard.IsPinned&&pinned.Topmost&&ReferenceEquals(pinned.Content,allCard),"pinned snapshot not preserved");
            pinned.ReplaceCard(Make(120));var pinnedHeader=(DockPanel)pinned.Card.DragHandle;
            Check(pinned.Card.IsPinned&&pinned.Card.DragHandle.Cursor==System.Windows.Input.Cursors.SizeAll,"fixed window not marked draggable");
            pinnedHeader.Children.OfType<Button>().Single(b=>b.Content.ToString()=="刷新").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(requestedRange==120&&pinned.Card.Minutes==120,"reused pinned window refreshed the old scope");
            var didClose=false;pinned.Closed+=(_,_)=>didClose=true;
            ((DockPanel)pinned.Card.DragHandle).Children.OfType<Button>().Single(b=>b.Content.ToString()=="×").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(didClose,"fixed window close button failed");
            Set("_opts",AppOptions.Parse(["--demo","--snapshot","unused"]));
            foreach(var source in new[]{ChatPlatform.Claude,ChatPlatform.Codex}) Check(app.Tokens(now.AddHours(-2),now,platform:source)==app.TokenBreakdown(source,now.AddHours(-2),now).Total,"demo table and card mismatch");
            Click("TokenScope");Check(Control<StackPanel>("SettingsPanel").Visibility==Visibility.Visible&&Control<Expander>("TokenDiagnostics").IsExpanded&&Control<Expander>("TokenCalculation").IsExpanded,"diagnostic entry no longer opens token details");
            Console.WriteLine("Concise UI: no blanket chart/legend tooltip; brief mode tip; calculation details opt-in; token diagnostic entry works.");
            if(model.Settings.CompactMode)Click("CompactButton");model.Settings.ClaudeChartCollapsed=model.Settings.CodexChartCollapsed=false;window.Render();
            Control<ComboBox>("MonitoringCombo").SelectedIndex=1;
            Check(model.Settings.Monitoring=="claude"&&Control<QuotaRing>("CodexMeter").Visibility==Visibility.Collapsed&&Control<TextBlock>("CodexInput").Visibility==Visibility.Collapsed&&Control<RateChart>("Chart").HeaderTop(true) is null,"Claude-only retained Codex live UI");
            Check(Control<StackPanel>("CodexChartModes").Visibility==Visibility.Collapsed&&Control<StackPanel>("ClaudeChartModes").Visibility==Visibility.Visible,"Claude-only mode controls wrong");
            Check(app.CacheEntries(now).All(e=>e.Platform==ChatPlatform.Claude)&&codex.EventList.Any(e=>e.Type==EventTypes.MonitorPause),"Claude-only retained Codex chats or lost pause boundary");
            Control<ComboBox>("MonitoringCombo").SelectedIndex=2;
            Check(model.Settings.Monitoring=="codex"&&Control<QuotaRing>("FiveMeter").Visibility==Visibility.Collapsed&&Control<TextBlock>("ClaudeInput").Visibility==Visibility.Collapsed&&Control<RateChart>("Chart").HeaderTop(false) is null,"Codex-only retained Claude live UI");
            Check(Control<StackPanel>("ClaudeChartModes").Visibility==Visibility.Collapsed&&Control<StackPanel>("CodexChartModes").Visibility==Visibility.Visible,"Codex-only mode controls wrong");
            Check(app.CacheEntries(now).All(e=>e.Platform==ChatPlatform.Codex)&&!Control<TextBlock>("NoteText").Text.Contains("Claude"),"disabled provider leaked reminders or status");
            Check(Control<QuotaRing>("CodexMeter").SingleProvider&&Control<QuotaRing>("CodexMeter").Height==66,"Codex-only full header retains the four-column height");
            Click("CompactButton");
            Check(Control<StackPanel>("CompactClaudeRate").Visibility==Visibility.Collapsed&&Control<StackPanel>("CompactCodexRate").Visibility==Visibility.Visible,"single-provider compact layout wrong");
            Check(Control<Grid>("CodexCompactSummary").Visibility==Visibility.Visible&&Control<Grid>("MeterGrid").Visibility==Visibility.Collapsed&&Control<Grid>("CompactRates").Visibility==Visibility.Collapsed,"Codex-only compact still has separated meter/rate rows");
            Check(Control<TextBlock>("CodexInlineRateValue").Text==Control<TextBlock>("CompactCodexRateValue").Text&&Control<TextBlock>("CodexInlinePoints").Text==Control<TextBlock>("CompactCodexPoints").Text,"inline usage changed source or values");
            Control<StackPanel>("CodexInlineUsage").RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.MouseEnterEvent});
            Check(openTimer.IsEnabled,"inline usage lost hover binding");
            Pin("CodexInlineUsage");Check(windows[ChatPlatform.Codex].Card.Platform==ChatPlatform.Codex&&windows[ChatPlatform.Codex].Card.Minutes==60,"inline pin lost Codex or 1h scope");windows[ChatPlatform.Codex].Close();
            Click("CompactButton");Control<ComboBox>("MonitoringCombo").SelectedIndex=0;
            Check(model.Settings.Monitoring=="both"&&Control<QuotaRing>("FiveMeter").Visibility==Visibility.Visible&&Control<QuotaRing>("CodexMeter").Visibility==Visibility.Visible&&Control<Grid>("TokenPanel").RowDefinitions[2].Height.Value==22,"both mode failed to restore");
            Check(!Control<QuotaRing>("CodexMeter").SingleProvider&&Control<QuotaRing>("CodexMeter").Height==104&&Control<Grid>("CodexCompactSummary").Visibility==Visibility.Collapsed&&Control<Grid>("MeterGrid").Visibility==Visibility.Visible&&Control<Grid>("CompactRates").Visibility==Visibility.Visible,"single-provider layout leaked into both mode");
            Console.WriteLine("Codex-only layout: 66px horizontal full header, one compact summary row, same rate/amount, hover/direct pin and both-mode restoration passed.");
            var monitoringEvents=model.EventList.Count+codex.EventList.Count;
            window.Render();window.OnThemeChanged();window.Render();
            var wheel=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,-120){RoutedEvent=UIElement.PreviewMouseWheelEvent};
            Control<ComboBox>("MonitoringCombo").RaiseEvent(wheel);
            Check(wheel.Handled&&model.Settings.Monitoring=="both"&&model.EventList.Count+codex.EventList.Count==monitoringEvents,"closed monitoring selector scroll or ordinary redraw changed source");
            window.OpenSettings();Check(Control<StackPanel>("SettingsPanel").Visibility==Visibility.Visible,"tray settings entry failed");
            Check(Control<QuotaRing>("FiveMeter").Accent=="Blue"&&Control<QuotaRing>("WeekMeter").Accent=="Claude"&&Control<QuotaRing>("FableMeter").Accent=="Fable","quota palette roles wrong");
            Check(Control<QuotaRing>("FiveMeter").ShowPlatformIcon&&Control<Grid>("MeterGrid").RowDefinitions.Count==0,"extra platform-icon row returned");
            var axisSource=new SeriesData{Key=SeriesKey.Total,Segments=[]};
            ChartView AxisView(bool c,bool x)=>new(){Start=now.AddDays(-3),End=now,Smooth=true,Total=axisSource,Fable=axisSource,Codex=axisSource,Gaps=[],ClaudeCollapsed=c,CodexCollapsed=x};
            var axisChart=new RateChart{View=AxisView(false,true)};
            Check(axisChart.AxisBounds(false)!.Value.Bottom<=axisChart.HeaderTop(true)&&axisChart.AxisBounds(true) is null,"collapsed lower header overlaps Claude axis");
            axisChart.View=AxisView(false,false);
            Check(axisChart.AxisBounds(false)!.Value.Bottom<=axisChart.HeaderTop(true)&&axisChart.AxisBounds(true)!.Value.Bottom<=axisChart.Height,"provider axes overlap a plot/header or leave the chart");
            axisChart.View=AxisView(true,false);Check(axisChart.AxisBounds(false) is null&&axisChart.AxisBounds(true) is not null,"upper collapsed layout kept its axis or lost Codex axis");
            axisChart.View=AxisView(true,true);Check(axisChart.Height==52&&axisChart.AxisBounds(false) is null&&axisChart.AxisBounds(true) is null,"fully collapsed chart kept blank axis space");
            Console.WriteLine("Chart axes: each expanded provider has its own axis; collapsed providers reserve no axis space; headers never overlap either axis.");
            ChartInspectProbe.Run();
            MonitoringRefreshProbe.Run(app);
            FableTransitionProbe.Run(app);
            LocalizationProbe.Run(app);
            ConnectionProbe.Run(app);
            UsageRefreshProbe.Run();
            UsageInteractionProbe.Run();
            Console.WriteLine("Monitoring UI: real selector transitions both/Claude/Codex; live meters/chart/tokens/chats and compact rows follow; paused-provider status suppressed; both restores; corner icons and palette roles verified.");
            Console.WriteLine("PASS: full-row geometry and hover, table/card same snapshot with platform/scope isolation, fixed window snapshot/drag handle/close, reused-window refresh scope, demo totals consistent. No native windows shown.");
            Console.WriteLine("PASS: both usage groupings visible (2 models / 80 chats), title mapping, no switches, refresh/close actions, provider hover entry/leave wiring.");
            Console.WriteLine("PASS: all active rows (4 and 20), near-threshold/running priority, unknown rates, stable hover rows, archive-free rendering, full-width restore, per-platform and both-token collapse/restore, disabled token setting, compact toggle, disabled/empty reminders. Isolated metadata only.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{Directory.Delete(root,true);}
    }
}
