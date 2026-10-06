using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace QuotaWidget.App;

/// <summary>Move from content, preserving clicks, controls and wheel scrolling.</summary>
public static class WindowDrag
{
    public static bool IsControl(DependencyObject? source,DependencyObject surface)
    {
        while(source is not null&&source!=surface)
        {
            if(source is ButtonBase or Thumb or TextBoxBase or ComboBox or Slider or Hyperlink)return true;
            source=source is Visual?VisualTreeHelper.GetParent(source):LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    public static bool PassedThreshold(Point start,Point current)=>
        Math.Abs(current.X-start.X)>=SystemParameters.MinimumHorizontalDragDistance||
        Math.Abs(current.Y-start.Y)>=SystemParameters.MinimumVerticalDragDistance;

    public static void Attach(Window window,UIElement surface,Action<bool>? dragging=null,Action? moved=null)
    {
        Point? origin=null;bool suppressClick=false;
        surface.PreviewMouseLeftButtonDown+=(_,e)=>
        {
            suppressClick=false;
            origin=e.ButtonState==MouseButtonState.Pressed&&!IsControl(e.OriginalSource as DependencyObject,surface)?e.GetPosition(surface):null;
        };
        surface.PreviewMouseMove+=(_,e)=>
        {
            if(e.LeftButton!=MouseButtonState.Pressed){origin=null;return;}
            if(origin is not {} start||!PassedThreshold(start,e.GetPosition(surface)))return;
            origin=null;suppressClick=true;e.Handled=true;dragging?.Invoke(true);
            try{window.DragMove();}catch(InvalidOperationException){}
            finally{dragging?.Invoke(false);moved?.Invoke();}
        };
        surface.PreviewMouseLeftButtonUp+=(_,e)=>
        {
            origin=null;
            if(suppressClick){e.Handled=true;suppressClick=false;}
        };
    }
}
