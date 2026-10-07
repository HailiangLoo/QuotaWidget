using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using QuotaWidget.App;
using QuotaWidget.Core;

static class ScalingProbe
{
    public static void Run(App app)
    {
        var root = Path.Combine(Path.GetTempPath(), "qw-scale-ui-" + Guid.NewGuid());
        try
        {
            var now = DateTimeOffset.Now;
            var paths = new DataPaths(Path.Combine(root, "claude", "demo"));
            var other = new DataPaths(Path.Combine(root, "codex", "demo"));
            DemoData.Generate(paths, now, "codex-plus");
            DemoData.Generate(other, now, "codex-plus", codex:true);
            var model = new WidgetModel(paths, true); model.Initialize();
            var codex = new WidgetModel(other, true) { ReadOnly = true }; codex.Initialize();
            var opts = AppOptions.Parse(["--demo", "--snapshot", "unused"]);
            void Set(string name, object? value) => typeof(App).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(app, value);
            Set("_opts", opts); Set("_paths", paths); Set("_model", model); Set("_codex", codex);
            Set("_tokens", null); Set("_chatHistory", new ChatSessionHistory(root)); Set("_currentChatSession", null); Set("_window", null);
            var window = new MainWindow(app, model, codex, opts); Set("_window", window);
            T C<T>(string name) => (T)window.FindName(name);
            var surface = (FrameworkElement)window.Content;
            void Layout()
            {
                window.Render();
                surface.Measure(new Size(window.Width, double.PositiveInfinity));
                surface.Arrange(new Rect(0, 0, window.Width, surface.DesiredSize.Height));
                surface.UpdateLayout();
            }
            void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
            void Near(double expected, double actual, string message) => Check(Math.Abs(expected-actual)<1.1, $"{message}: {expected} / {actual}");
            void Click(string name) => C<ButtonBase>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            void Invoke(string method, params object[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
            var grip = C<Thumb>("ScaleGrip");
            var frame = C<Border>("Frame");
            var logicalWidth = model.Settings.Width;
            foreach (var language in new[]{"zh-CN", "en"})
            foreach (var provider in new[]{0,1,2})
            foreach (var compact in new[]{false,true})
            {
                C<ComboBox>("LanguageCombo").SelectedIndex=language=="en"?2:1;
                C<ComboBox>("MonitoringCombo").SelectedIndex=provider;
                if(model.Settings.CompactMode!=compact) Click("CompactButton");
                model.Settings.UiScale=1; Layout();
                var normal=frame.RenderSize;
                var button=C<ButtonBase>("SettingsButton");
                var buttonSize=button.RenderSize;
                foreach(var scale in new[]{.8,1.25,2})
                {
                    model.Settings.UiScale=scale; Layout();
                    Near(normal.Width,frame.ActualWidth,"scaling changed logical width");
                    Near(normal.Height,frame.ActualHeight,"scaling reflowed content");
                    var bounds=frame.TransformToAncestor(surface).TransformBounds(new Rect(frame.RenderSize));
                    Near(normal.Width*scale,bounds.Width,"frame X scale");
                    Near(normal.Height*scale,bounds.Height,"frame Y scale");
                    Near(bounds.Width+24,window.Width,"window clips scaled width");
                    Near(bounds.Height,surface.ActualHeight,"window clips scaled height");
                    var buttonBounds=button.TransformToAncestor(surface).TransformBounds(new Rect(button.RenderSize));
                    Near(buttonSize.Width*scale,buttonBounds.Width,"control X scale");
                    Near(buttonSize.Height*scale,buttonBounds.Height,"control Y scale");
                    Check(grip.Visibility==Visibility.Visible&&grip.ActualWidth==14,"grip scaled or hidden in compact mode");
                    var gripBounds=grip.TransformToAncestor(surface).TransformBounds(new Rect(grip.RenderSize));
                    Check(gripBounds.Right<=surface.ActualWidth+12&&gripBounds.Bottom<=surface.ActualHeight+12,"grip extends outside the window");
                    if(compact&&C<StackPanel>("CompactChatRows").Children.OfType<FrameworkElement>().LastOrDefault() is {} lastRow)
                    {
                        var lastBounds=lastRow.TransformToAncestor(surface).TransformBounds(new Rect(lastRow.RenderSize));
                        Check(!lastBounds.IntersectsWith(gripBounds),"grip overlaps the last chat row");
                    }
                    var hit=VisualTreeHelper.HitTest(surface,new Point(buttonBounds.Left+buttonBounds.Width/2,buttonBounds.Top+buttonBounds.Height/2))?.VisualHit;
                    Check(hit is not null&&WindowDrag.IsControl(hit,frame),$"scaled button became a drag surface: {language}/{provider}/{compact}/{scale}, hit={hit?.GetType().Name}");
                    var chart=C<RateChart>("Chart");
                    if(!compact)
                    {
                        var origin=chart.TransformToAncestor(surface).Transform(new Point(10,20));
                        var roundtrip=surface.TransformToDescendant(chart).Transform(origin);
                        Near(10,roundtrip.X,"chart inspection X transform"); Near(20,roundtrip.Y,"chart inspection Y transform");
                    }
                }
            }
            // Fitting must use all content even if a native window/host has
            // already constrained its height (for example, a saved 200% scale).
            Click("CompactButton"); model.Settings.UiScale=2; Layout();
            var natural=(Size)typeof(MainWindow).GetMethod("NaturalSize",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null)!;
            surface.Measure(new Size(window.Width,220));surface.Arrange(new Rect(0,0,window.Width,220));surface.UpdateLayout();
            var afterClip=(Size)typeof(MainWindow).GetMethod("NaturalSize",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null)!;
            Near(natural.Height,afterClip.Height,"screen fit used the clipped height");
            Click("CompactButton");
            model.Settings.UiScale=1; Layout();
            grip.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
            var start=(Point)typeof(MainWindow).GetField("_scalePointerStart",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            var basis=C<Grid>("Outer").RenderSize;
            void Drag(double amount) { Invoke("ResizeFromPoint",start+new Vector(basis.Width*amount,basis.Height*amount)); Layout(); }
            Drag(3); Check(model.Settings.UiScale==2,"upper limit");
            Drag(.25); Check(Math.Abs(model.Settings.UiScale-1.25)<.001,"drag stuck after upper-limit overshoot");
            Drag(-3); Check(model.Settings.UiScale==.8,"lower limit");
            Drag(-.1); Check(Math.Abs(model.Settings.UiScale-.9)<.001,"drag stuck after lower-limit overshoot");
            grip.RaiseEvent(new DragCompletedEventArgs(0,0,true){RoutedEvent=Thumb.DragCompletedEvent});
            Check(model.Settings.UiScale==1,"cancel did not restore size");
            grip.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
            Drag(.25); grip.RaiseEvent(new DragCompletedEventArgs(0,0,false){RoutedEvent=Thumb.DragCompletedEvent});
            Check(Math.Abs(WidgetSettings.Load(paths.Settings,out _).UiScale-1.25)<.001,"scale was not persisted");
            Check(model.Settings.Width==logicalWidth,"resize rewrote layout width");
            grip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent});
            Check(Math.Abs(model.Settings.UiScale-1.25)<.001,"double-click unexpectedly resets size");
            model.Settings.UiScale=1.4; window.OpenSettings(); Layout();
            Near(model.Settings.Width*1.4+24,window.Width,"settings lost scale");
            Click("SettingsButton"); Layout(); Near(240*1.4+24,window.Width,"compact return lost scale");
            var loaded=new MainWindow(app,model,codex,opts);
            Near(window.Width,loaded.Width,"new window lost saved scale");
            Console.WriteLine("Scaling UI: 36 layout cases preserve proportions, controls and chart coordinates; compact grip, overshoot reversal, cancel, persistence, no double-click action and settings round trip passed.");
        }
        finally { Directory.Delete(root,true); }
    }
}
