using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuotaWidget.App;
using QuotaWidget.Core;

static class ExpandedChatListProbe
{
    public static void Run(App app,Action<MainWindow,string>? inspect=null)
    {
        var root=Path.Combine(Path.GetTempPath(),"qw-expanded-chats-"+Guid.NewGuid());
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        void Set(string name,object? value)=>typeof(App).GetField(name,flags)!.SetValue(app,value);
        void Check(bool yes,string message){if(!yes)throw new Exception("Expanded chat list: "+message);}
        MainWindow? window=null;
        try
        {
            var now=DateTimeOffset.Now;var opts=AppOptions.Parse(["--snapshot","unused"]);
            var paths=new DataPaths(Path.Combine(root,"demo"));DemoData.Generate(paths,now,"showcase");
            var model=new WidgetModel(paths,true){ReadOnly=true};model.Initialize();
            var xp=new DataPaths(Path.Combine(root,"codex/demo"));DemoData.Generate(xp,now,"showcase",codex:true);
            var codex=new WidgetModel(xp,true){ReadOnly=true};codex.Initialize();
            model.Settings.CompactMode=false;model.Settings.Width=290;model.Settings.Height=700;
            Set("_opts",opts);Set("_model",model);Set("_codex",codex);Set("_paths",paths);Set("_tokens",null);Set("_snapshotTokens",null);
            Set("_chatHistory",new ChatSessionHistory(root));Set("_currentChatSession",null);Set("_chatLifecycle",ChatLifecycleSnapshot.Empty);
            ChatCacheEntry Entry(string id,double age,int ttl=30,bool running=false)=>new(ChatPlatform.Codex,id,id,now.AddMinutes(-age),ttl,"fixture",running);
            var data=new[]{Entry("codex-warning",88),Entry("claude-warning",4,5) with{Platform=ChatPlatform.Claude},
                Entry("claude-hour",25,60) with{Platform=ChatPlatform.Claude},Entry("codex-warm",45),Entry("codex-young",4),Entry("running",100,running:true)};
            Set("_cacheEntries",data);
            window=new MainWindow(app,model,codex,opts){Left=-10000,Top=-10000,ShowActivated=false};Set("_window",window);
            window.Show();
            T C<T>(string name)=>(T)window.FindName(name);
            var rows=C<StackPanel>("CacheRows");var viewport=C<ScrollViewer>("ChatViewport");var body=C<ScrollViewer>("BodyViewport");
            void Layout(bool render=true)
            {
                if(render)window.Render();window.UpdateLayout();
                window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            }
            string Name(UIElement row)=>((StackPanel)((ToolTip)((FrameworkElement)row).ToolTip).Content).Children.OfType<TextBlock>().First().Text;
            string[] Order()=>rows.Children.Cast<UIElement>().Select(Name).ToArray();
            void Tick(int seconds)
            {
                Set("_cacheEntries",data.Select(e=>e with{RequestAt=e.RequestAt.AddSeconds(seconds)}).ToArray());
                typeof(MainWindow).GetMethod("RenderCacheAt",flags)!.Invoke(window,[now.AddSeconds(seconds)]);Layout(false);
            }
            Layout();Tick(0);
            inspect?.Invoke(window,"priority");
            Check(Order().SequenceEqual(new[]{"codex-warning","codex-warm","claude-hour","claude-warning","codex-young","running"}),"idle minutes did not override provider windows, preserve ties or place running last");
            // A refreshed request changes the idle order only after the shared
            // cooldown. Running age never outranks an idle chat.
            data[0]=data[0] with{RequestAt=now.AddMinutes(-10)};
            Tick(20);
            Check(Order().Take(2).SequenceEqual(new[]{"codex-warning","codex-warm"}),"list reordered within a minute");
            Tick(61);
            Check(Order().Take(2).SequenceEqual(new[]{"codex-warm","claude-hour"}),"refreshed timer retained its old position");
            Check(Order().Last()=="running","running age became urgent");
            var first=rows.Children[0];Tick(62);
            Check(ReferenceEquals(first,rows.Children[0]),"unchanged refresh rebuilt the list");
            viewport.Focusable=true;Keyboard.Focus(viewport);Layout(false);
            Check(viewport.IsKeyboardFocusWithin,"focus fixture failed");
            data[2]=data[2] with{RequestAt=now.AddMinutes(-59)};Tick(123);
            Check(Order().First()=="codex-warm","focused list reordered during interaction");
            Keyboard.Focus(C<System.Windows.Controls.Primitives.ButtonBase>("SettingsButton"));Tick(124);
            Check(Order().First()=="claude-hour","deferred order did not apply after interaction");
            // Changing layouts must reuse the same order and minute budget,
            // rather than restarting a second sort policy or cooldown.
            var compactRows=C<StackPanel>("CompactChatRows");
            string[] CompactOrder()=>compactRows.Children.Cast<UIElement>().Select(Name).ToArray();
            void Mode(bool compact,int seconds)
            {
                model.Settings.CompactMode=compact;
                typeof(MainWindow).GetMethod("ApplyCompactLayout",flags)!.Invoke(window,null);Tick(seconds);
            }
            var fullOrder=Order();Mode(true,130);
            Check(CompactOrder().SequenceEqual(fullOrder),"compact changed the shared order");
            data[0]=data[0] with{RequestAt=now.AddMinutes(-119.6)};Tick(150);
            Check(CompactOrder().SequenceEqual(fullOrder),"switching to compact reset the minute budget");
            body.Focusable=true;Keyboard.Focus(body);Layout(false);
            Check(body.IsKeyboardFocusWithin,"compact focus fixture failed");Tick(185);
            Check(CompactOrder().SequenceEqual(fullOrder),"focused compact list reordered during interaction");
            Keyboard.Focus(C<System.Windows.Controls.Primitives.ButtonBase>("SettingsButton"));Tick(186);
            Check(CompactOrder().First()=="codex-warning"&&CompactOrder().Last()=="running","compact did not use the same idle-duration policy");
            var firstCompact=compactRows.Children[0];Tick(187);
            Check(ReferenceEquals(firstCompact,compactRows.Children[0]),"unchanged compact refresh rebuilt rows");
            var compactOrder=CompactOrder();Mode(false,190);
            Check(Order().SequenceEqual(compactOrder),"returning to expanded changed the shared order");
            data[0]=data[0] with{RequestAt=now.AddMinutes(-1)};Tick(200);
            Check(Order().SequenceEqual(compactOrder),"returning to expanded reset the minute budget");
            Tick(247);Check(Order().First()=="claude-hour","shared cooldown did not expire across layouts");
            foreach(var count in new[]{8,20})foreach(var height in new[]{360d,600d,700d,1000d})foreach(var scale in new[]{1d,1.25d})
            {
                model.Settings.Height=height;model.Settings.UiScale=scale;
                Set("_cacheEntries",Enumerable.Range(0,count).Select(i=>Entry("work-"+i,10,running:true)).ToArray());
                Layout();
                if(height==700&&scale==1){viewport.ScrollToTop();Layout(false);inspect?.Invoke(window,$"{count}-top");}
                Check(rows.Children.Count==count&&Math.Abs(rows.ActualHeight-count*30)<1,"active rows were truncated");
                Check(viewport.ActualHeight<=181&&viewport.ActualHeight>=59,"chat viewport did not stay bounded");
                if(height>=600)Check(body.ScrollableHeight<1&&body.ComputedVerticalScrollBarVisibility==Visibility.Collapsed,"extra chats made the entire widget scroll");
                var chart=C<FrameworkElement>("Chart");var meter=C<FrameworkElement>("MeterGrid");
                var chartPosition=chart.TransformToAncestor(window).Transform(new Point());
                var meterPosition=meter.TransformToAncestor(window).Transform(new Point());
                viewport.ScrollToBottom();Layout(false);
                if(height==700&&scale==1)inspect?.Invoke(window,$"{count}-bottom");
                var last=(FrameworkElement)rows.Children[^1];var bounds=last.TransformToAncestor(viewport).TransformBounds(new Rect(last.RenderSize));
                Check(bounds.Top>=-1&&bounds.Bottom<=viewport.ViewportHeight+1,"last active chat cannot be fully reached by scrolling");
                Check(viewport.ComputedVerticalScrollBarVisibility==(viewport.ScrollableHeight>0?Visibility.Visible:Visibility.Collapsed),"overflow has no visible scrollbar");
                var offset=viewport.VerticalOffset;
                window.RenderCache();Layout(false);
                Check(Math.Abs(viewport.VerticalOffset-offset)<1,"refresh reset chat scroll position");
                // Even at the local boundary, wheel input must not move the body.
                var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,0,-120){RoutedEvent=Mouse.MouseWheelEvent};viewport.RaiseEvent(wheel);Layout(false);
                Check(wheel.Handled,"local scroll did not consume wheel input");
                Check(chart.TransformToAncestor(window).Transform(new Point())==chartPosition&&meter.TransformToAncestor(window).Transform(new Point())==meterPosition,"scrolling chats moved meters/charts");
            }
            model.Settings.Height=700;model.Settings.UiScale=1;
            Set("_cacheEntries",Enumerable.Range(0,20).Select(i=>Entry("archived-"+i,180)).Concat(Enumerable.Range(0,8).Select(i=>Entry("live-"+i,10,running:true))).ToArray());Layout();
            C<Expander>("ExpiredChats").IsExpanded=true;Layout(false);viewport.ScrollToBottom();Layout(false);
            var archived=C<StackPanel>("ExpiredRows");var final=(FrameworkElement)archived.Children[^1];
            var finalBounds=final.TransformToAncestor(viewport).TransformBounds(new Rect(final.RenderSize));
            Check(finalBounds.Top>=-1&&finalBounds.Bottom<=viewport.ViewportHeight+1,"last archived chat is clipped");
            Check(body.ScrollableHeight<1,"archive expansion made the entire widget scroll");
            inspect?.Invoke(window,"archive-bottom");
            data=new[]{Entry("recent",119.9),Entry("folded",120.1),Entry("long-running",180,running:true)};Tick(0);
            Check(Order().Contains("recent")&&Order().Contains("long-running")&&!Order().Contains("folded"),"Codex 2h boundary or running retention wrong");
            Check(archived.Children.Cast<UIElement>().Select(Name).SequenceEqual(new[]{"folded"}),"Codex older than 2h did not fold");
            Mode(true,0);Check(CompactOrder().Contains("recent")&&!CompactOrder().Contains("folded"),"compact differs at Codex 2h boundary");
            // Capacity bypasses the idle fold/sort cooldown in both layouts; normal
            // Codex timers remain green. New response status must redraw immediately.
            var blocked=Entry("Capacity task",180) with{CapacityBlockedAt=now.AddSeconds(-1),ActivityAt=now.AddSeconds(-1)};
            data=[Entry("normal",10),blocked];Tick(0);
            string Status(Grid row)=>new System.Windows.Documents.TextRange(row.Children.OfType<TextBlock>().Single().ContentStart,row.Children.OfType<TextBlock>().Single().ContentEnd).Text;
            void CheckCapacity(StackPanel panel)
            {
                Check(Name(panel.Children[0])==blocked.Title,"capacity chat did not move above idle list");
                var row=(Grid)panel.Children[0];
                Check(Status(row)==Loc.T("容量已满")&&row.Children.OfType<TextBlock>().Single().Foreground==Theme.Brush("Red"),"capacity status missing or not red");
                Check(((Border)row.Children[0]).Background==Theme.Brush("Red"),"capacity marker is not red");
                Check(((Grid)panel.Children[1]).Children.OfType<TextBlock>().Single().Foreground==Theme.Brush("Green"),"normal Codex timer lost green");
            }
            CheckCapacity(C<StackPanel>("CompactChatRows"));inspect?.Invoke(window,"capacity-compact");
            Mode(false,0);CheckCapacity(rows);Check(!archived.Children.Cast<UIElement>().Select(Name).Contains(blocked.Title),"blocked chat also appeared in archive");
            inspect?.Invoke(window,"capacity-full");
            void Disclosure(string name)
            {
                var button=C<System.Windows.Controls.Primitives.ToggleButton>(name);
                button.IsChecked=button.IsChecked!=true;
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Layout(false);
            }
            var fullHeight=C<Grid>("Outer").ActualHeight;
            var readOnly=model.ReadOnly;model.ReadOnly=false;
            Disclosure("ChatListToggle");model.ReadOnly=readOnly;
            Check(model.Settings.ChatListCollapsed&&WidgetSettings.Load(paths.Settings,out _).ChatListCollapsed,"collapse choice did not persist");
            Check(viewport.Visibility==Visibility.Collapsed&&C<System.Windows.Controls.Primitives.ToggleButton>("ChatListToggle").Visibility==Visibility.Visible,"full disclosure hides its own arrow or leaves chats visible");
            Check(Math.Abs(C<Grid>("Outer").ActualHeight-fullHeight)<1,"collapse changed saved full height");
            Check(C<System.Windows.Controls.Primitives.ToggleButton>("ChatListToggle").Foreground==Theme.Brush("Red"),"collapsed arrow hid unresolved capacity warning");
            inspect?.Invoke(window,"chat-list-full-collapsed");
            Mode(true,1);var foldedHeight=C<Grid>("Outer").ActualHeight;
            Check(C<StackPanel>("CompactChatBody").Visibility==Visibility.Collapsed&&C<System.Windows.Controls.Primitives.ToggleButton>("CompactChatListToggle").IsChecked==true,"compact did not share collapsed state");
            inspect?.Invoke(window,"chat-list-compact-collapsed");
            Disclosure("CompactChatListToggle");
            Check(C<StackPanel>("CompactChatBody").Visibility==Visibility.Visible&&C<Grid>("Outer").ActualHeight>foldedHeight+30,"compact expansion did not restore rows/height");
            Mode(false,2);Check(viewport.Visibility==Visibility.Visible&&!model.Settings.ChatListCollapsed,"full mode did not share expansion");
            data[1]=blocked with{CapacityBlockedAt=null,Running=true,ActivityAt=now};Tick(1);
            Check(!rows.Children.Cast<Grid>().Any(r=>Status(r)==Loc.T("容量已满")),"recovered status did not redraw inside minute cooldown");
            // Exercise the actual notification selection with no native popup or
            // workers: startup/rescan dedupe, recovery, recurrence and batch alerts.
            var take=typeof(App).GetMethod("TakeCapacityAlerts",flags)!;
            IReadOnlyList<ChatCacheEntry> Alerts()=>(IReadOnlyList<ChatCacheEntry>)take.Invoke(app,[now])!;
            Set("_cacheEntries",new[]{blocked});Check(Alerts().Count==0,"snapshot triggered a system notification");
            Set("_opts",new AppOptions());
            model.Settings.ChatListCollapsed=true;
            Set("_cacheEntries",new[]{blocked,blocked with{Id="second",Title="Second"}});Check(Alerts().Count==2,"simultaneous errors were lost or collapsing suppressed notifications");
            Check(Alerts().Count==0,"same failure repeated the popup");
            Set("_cacheEntries",new[]{blocked with{CapacityBlockedAt=null,Running=true,ActivityAt=now}});Check(Alerts().Count==0,"recovery produced an alert");
            Set("_cacheEntries",new[]{blocked with{CapacityBlockedAt=now}});Check(Alerts().Count==1&&Alerts().Count==0,"new failure did not alert exactly once");
            model.Settings.CacheRemindersEnabled=false;Set("_cacheEntries",new[]{blocked with{Id="disabled"}});Check(Alerts().Count==0,"disabled reminders still notified");
            model.Settings.CacheRemindersEnabled=true;
            model.Settings.ChatListCollapsed=false;
            Set("_opts",opts);
            Console.WriteLine("PASS: arrow collapses/expands only chat region, persisted shared state across layouts, compact auto-height/full height preserved, red capacity arrow and hidden-list notification selection passed.");
            Console.WriteLine("PASS: capacity stays red/visible in full and compact, recovery redraws immediately, normal timers stay green; isolated notification batching/deduplication/recurrence and snapshot/disabled suppression passed.");
            Console.WriteLine("PASS: chats scroll locally; 8/20 rows × four heights × two scales stay reachable, meters/charts stay fixed, wheel stops locally, archive stays reachable; both layouts share longest-idle-first ordering, stable ties and minute cooldown, preserve them across switches, and defer during interaction.");
        }
        finally{window?.Close();Set("_window",null);Set("_cacheEntries",Array.Empty<ChatCacheEntry>());if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
