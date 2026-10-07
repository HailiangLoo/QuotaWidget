using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuotaWidget.App;
using QuotaWidget.Core;

static class ScalingProbe
{
    public static void Run(App app)
    {
        var root=Path.Combine(Path.GetTempPath(),"qw-resize-ui-"+Guid.NewGuid());
        MainWindow? window=null;
        try
        {
            var now=DateTimeOffset.Now;
            var paths=new DataPaths(Path.Combine(root,"claude","demo"));
            var other=new DataPaths(Path.Combine(root,"codex","demo"));
            DemoData.Generate(paths,now,"codex-plus"); DemoData.Generate(other,now,"codex-plus",codex:true);
            var model=new WidgetModel(paths,true);model.Initialize();
            var codex=new WidgetModel(other,true){ReadOnly=true};codex.Initialize();
            var opts=AppOptions.Parse(["--demo","--snapshot","unused"]);
            void Set(string name,object? value)=>typeof(App).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(app,value);
            Set("_opts",opts);Set("_paths",paths);Set("_model",model);Set("_codex",codex);Set("_tokens",null);
            Set("_chatHistory",new ChatSessionHistory(root));Set("_currentChatSession",null);Set("_window",null);
            window=new MainWindow(app,model,codex,opts){Left=-10000,Top=-10000,ShowActivated=false};Set("_window",window);
            window.Show();
            T C<T>(string name)=>(T)window.FindName(name);
            void Check(bool ok,string message){if(!ok)throw new Exception(message);}
            void Near(double expected,double actual,string message)=>Check(Math.Abs(expected-actual)<1.6,$"{message}: {expected} / {actual}");
            object? Invoke(string method,params object[] args)=>typeof(MainWindow).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,args);
            void Layout(bool render=true)
            {
                if(render)window.Render();
                window.UpdateLayout();
                window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            }
            void Click(string name)=>C<ButtonBase>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            void Start(Thumb grip)=>grip.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
            void End(Thumb grip,bool cancel)=>grip.RaiseEvent(new DragCompletedEventArgs(0,0,cancel){RoutedEvent=Thumb.DragCompletedEvent});
            Point Pointer()=>(Point)typeof(MainWindow).GetField("_resizePointerStart",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            Rect Bounds()=> (Rect)typeof(MainWindow).Assembly.GetType("QuotaWidget.App.NativePlacement")!.GetMethod("Bounds")!.Invoke(null,[new WindowInteropHelper(window).Handle])!;
            var chart=C<RateChart>("Chart");var surface=(FrameworkElement)window.Content;
            var names=new[]{"LeftGrip","RightGrip","TopGrip","BottomGrip","TopLeftGrip","TopRightGrip","BottomLeftGrip","BottomRightGrip"};
            model.Settings.RangeMinutes=ChartRanges.All;
            foreach(var compact in new[]{false,true})
            foreach(var mode in new[]{"rate","cumulative"})
            foreach(var name in names)
            foreach(var cancel in new[]{false,true})
            {
                if(model.Settings.CompactMode!=compact)Click("CompactButton");
                model.Settings.Width=model.Settings.CompactWidth=310;
                model.Settings.Height=model.Settings.CompactHeight=330;
                model.Settings.UiScale=1.25;
                model.Settings.ClaudeChartMode=model.Settings.CodexChartMode=mode;Layout();
                var grip=C<Thumb>(name);var edges=(string)grip.Tag;
                var before=Bounds();var view=chart.View;
                if(!compact)Check(view is not null&&model.ArchiveLoaded&&codex.ArchiveLoaded,"all-history fixture not loaded");
                if(!compact)chart.PinInspect(view!.End.AddMinutes(-2),false);
                var pin=chart.Pinned;
                var button=C<Button>("SettingsButton");var buttonSize=button.ActualWidth;
                var hitBounds=grip.TransformToAncestor(surface).TransformBounds(new Rect(grip.RenderSize));
                var hit=VisualTreeHelper.HitTest(surface,new Point(hitBounds.Left+hitBounds.Width/2,hitBounds.Top+hitBounds.Height/2))?.VisualHit;
                Check(hit is not null&&VisualDescendants(grip).Contains(hit),$"unreachable resize handle: {name}");
                Check(!VisualDescendants(grip).OfType<System.Windows.Shapes.Path>().Any(),"visible resize glyph returned");
                void Continuous()
                {
                    if(compact)return;
                    Check(ReferenceEquals(view,chart.View),"drag cleared/rebuilt chart before a refresh tick");
                    Check(chart.Pinned==pin&&model.ArchiveLoaded&&codex.ArchiveLoaded,"drag lost inspector/history");
                    Check(chart.Height>100&&chart.HeaderTop(false)!=null&&chart.HeaderTop(true)!=null,"provider plot disappeared");
                }
                Start(grip);Layout(false);Continuous();
                var pointer=Pointer();
                var delta=new Vector(edges.Contains('L')?-50:50,edges.Contains('T')?-36:36);
                Invoke("ResizeFromPoint",pointer+delta);Layout(false);Continuous();
                var after=Bounds();
                var horizontal=edges.Contains('L')||edges.Contains('R');var vertical=edges.Contains('T')||edges.Contains('B');
                Near(before.Width+(horizontal?50:0),after.Width,"width does not follow selected edge");
                Near(before.Height+(vertical?36:0),after.Height,"height does not follow selected edge");
                Near(edges.Contains('L')?before.Right:before.Left,edges.Contains('L')?after.Right:after.Left,"opposite horizontal edge moved");
                Near(edges.Contains('T')?before.Bottom:before.Top,edges.Contains('T')?after.Bottom:after.Top,"opposite vertical edge moved");
                Near(buttonSize,button.ActualWidth,"resize changed text/control scale");
                Near(1.25,model.Settings.UiScale,"resize modified font scale");
                End(grip,cancel);Layout(false);Continuous();
                if(cancel){Near(before.Width,Bounds().Width,"cancel width");Near(before.Height,Bounds().Height,"cancel height");Near(before.Left,Bounds().Left,"cancel position");}
                var saved=WidgetSettings.Load(paths.Settings,out _);
                Check(saved.Width==model.Settings.Width&&saved.Height==model.Settings.Height&&saved.CompactWidth==model.Settings.CompactWidth&&saved.CompactHeight==model.Settings.CompactHeight,"size not persisted");
            }
            if(model.Settings.CompactMode)Click("CompactButton");
            model.Settings.UiScale=1;model.Settings.Width=300;model.Settings.Height=null;Layout();
            var right=C<Thumb>("RightGrip");var original=Bounds();Start(right);var origin=Pointer();
            var dpi=VisualTreeHelper.GetDpi(window).DpiScaleX;
            Invoke("ResizeFromPoint",origin+new Vector(99999,0));Layout(false);Near(1200,model.Settings.Width,"upper limit");
            Invoke("ResizeFromPoint",origin+new Vector(20*dpi,0));Layout(false);Near(320,model.Settings.Width,"stuck after overshoot");Near(original.Height,Bounds().Height,"horizontal resize changed automatic height");
            End(right,true);Layout(false);Check(model.Settings.Height is null,"cancel lost automatic height");
            model.Settings.Width=370;model.Settings.Height=420;model.Settings.CompactWidth=270;model.Settings.CompactHeight=180;Layout();
            var smallChart=chart.Height;model.Settings.Height=750;Invoke("ApplyWidgetScale");Layout(false);
            Check(chart.Height>smallChart+100,$"taller window did not expand chart: {smallChart} -> {chart.Height}");
            model.Settings.Height=100;Invoke("ApplyWidgetScale");Layout(false);
            Check(C<ScrollViewer>("BodyViewport").ScrollableHeight>0,"short window clipped content without scrolling");
            Check(C<FrameworkElement>("TitleBar").ActualHeight>0,"short window lost title controls");
            Click("CompactButton");Layout();Near(270,C<Grid>("Outer").Width,"compact lost separate width");Near(180,C<Grid>("Outer").Height,"compact lost separate height");
            window.OpenSettings();Layout();Near(370,C<Grid>("Outer").Width,"settings width");
            Check(C<ScrollViewer>("BodyViewport").ScrollableHeight>0,"short settings cannot scroll");
            Click("SettingsButton");Layout();Near(270,C<Grid>("Outer").Width,"settings return width");Near(180,C<Grid>("Outer").Height,"settings return height");
            var corner=C<Thumb>("BottomRightGrip");corner.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent});
            Near(270,model.Settings.CompactWidth,"double-click reset size");
            Console.WriteLine("Resize UI: all eight handles, full/compact, rate/cumulative, completion/cancel; physical HWND dimensions/opposite anchors, hit areas, no refresh during drag, history/inspector retention, limits, font size, scrolling and per-mode persistence passed.");
        }
        finally{window?.Close();Directory.Delete(root,true);}
    }
    static IEnumerable<DependencyObject> VisualDescendants(DependencyObject node)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
        {var child=VisualTreeHelper.GetChild(node,i);yield return child;foreach(var next in VisualDescendants(child))yield return next;}
    }
}
