using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
            var data=new[]{Entry("codex-warning",22),Entry("claude-warning",4,5) with{Platform=ChatPlatform.Claude},
                Entry("claude-hour",25,60) with{Platform=ChatPlatform.Claude},Entry("codex-warm",15),Entry("codex-young",4),Entry("running",100,running:true)};
            Set("_cacheEntries",data);
            window=new MainWindow(app,model,codex,opts){Left=-10000,Top=-10000,ShowActivated=false};Set("_window",window);
            window.Show();
            T C<T>(string name)=>(T)window.FindName(name);
            var rows=C<StackPanel>("CacheRows");var viewport=C<ScrollViewer>("BodyViewport");
            void Layout(bool render=true)
            {
                if(render)window.Render();window.UpdateLayout();
                window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            }
            string Name(UIElement row)=>((StackPanel)((ToolTip)((FrameworkElement)row).ToolTip).Content).Children.OfType<TextBlock>().First().Text;
            string[] Order()=>rows.Children.Cast<UIElement>().Select(Name).ToArray();
            Layout();
            inspect?.Invoke(window,"priority");
            Check(Order().Take(4).SequenceEqual(new[]{"claude-warning","codex-warning","codex-warm","claude-hour"}),"urgency did not use each platform's reminder window");
            // Within one reminder band, even a relative-age crossover must not
            // move rows around. Running age must never create a cache warning.
            data[0]=data[0] with{RequestAt=now.AddMinutes(-28)};
            Set("_cacheEntries",data);window.RenderCache();Layout(false);
            Check(Order().Take(4).SequenceEqual(new[]{"claude-warning","codex-warning","codex-warm","claude-hour"}),"same-band clocks reordered stable rows");
            data[2]=data[2] with{RequestAt=now.AddMinutes(-42)};
            Set("_cacheEntries",data);window.RenderCache();Layout(false);
            Check(Order().Take(3).SequenceEqual(new[]{"claude-warning","codex-warning","claude-hour"}),"a new warning band did not promote an existing chat");
            var first=rows.Children[0];window.RenderCache();
            Check(ReferenceEquals(first,rows.Children[0]),"unchanged refresh rebuilt the list");
            foreach(var count in new[]{8,20})foreach(var height in new[]{360d,700d,1000d})foreach(var scale in new[]{1d,1.25d})
            {
                model.Settings.Height=height;model.Settings.UiScale=scale;
                Set("_cacheEntries",Enumerable.Range(0,count).Select(i=>Entry("work-"+i,10,running:true)).ToArray());
                Layout();
                if(height==700&&scale==1){viewport.ScrollToTop();Layout(false);inspect?.Invoke(window,$"{count}-top");}
                Check(rows.Children.Count==count&&Math.Abs(rows.ActualHeight-count*30)<1,"active rows were truncated");
                var parent=VisualTreeHelper.GetParent(rows);
                while(parent is not null&&parent!=viewport)
                {Check(parent is not ScrollViewer,"hidden nested scroll viewport still clips the list");parent=VisualTreeHelper.GetParent(parent);}
                Check(parent==viewport,"chat rows do not use the body viewport");
                viewport.ScrollToBottom();Layout(false);
                if(height==700&&scale==1)inspect?.Invoke(window,$"{count}-bottom");
                var last=(FrameworkElement)rows.Children[^1];var bounds=last.TransformToAncestor(viewport).TransformBounds(new Rect(last.RenderSize));
                Check(bounds.Top>=-1&&bounds.Bottom<=viewport.ViewportHeight+1,"last active chat cannot be fully reached by scrolling");
                Check(viewport.ComputedVerticalScrollBarVisibility==(viewport.ScrollableHeight>0?Visibility.Visible:Visibility.Collapsed),"overflow has no visible scrollbar");
            }
            model.Settings.Height=700;model.Settings.UiScale=1;
            Set("_cacheEntries",Enumerable.Range(0,20).Select(i=>Entry("archived-"+i,90)).ToArray());Layout();
            C<Expander>("ExpiredChats").IsExpanded=true;Layout(false);viewport.ScrollToBottom();Layout(false);
            var archived=C<StackPanel>("ExpiredRows");var final=(FrameworkElement)archived.Children[^1];
            var finalBounds=final.TransformToAncestor(viewport).TransformBounds(new Rect(final.RenderSize));
            Check(finalBounds.Top>=-1&&finalBounds.Bottom<=viewport.ViewportHeight+1,"last archived chat is clipped");
            Console.WriteLine("PASS: expanded chats use one visible scroll viewport; 8/20 rows at 360/700/1000 heights and two scales remain reachable; archived rows too; platform-relative reminder bands reorder only at state changes, same-band order stays stable.");
        }
        finally{window?.Close();Set("_window",null);Set("_cacheEntries",Array.Empty<ChatCacheEntry>());if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
