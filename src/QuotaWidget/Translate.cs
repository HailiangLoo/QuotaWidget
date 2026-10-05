using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuotaWidget.Core;

namespace QuotaWidget.App;

/// <summary>Static XAML text uses dynamic resources so an open window can change language.</summary>
public static class Translate
{
    static DependencyProperty Register(string name, DependencyProperty target) =>
        DependencyProperty.RegisterAttached(name,typeof(string),typeof(Translate),new PropertyMetadata(null,(d,e)=>
        {
            if(d is FrameworkElement element && e.NewValue is string text)
                element.SetResourceReference(target,"Loc."+text);
        }));

    public static readonly DependencyProperty TextProperty = Register("Text",TextBlock.TextProperty);
    public static readonly DependencyProperty ContentProperty = Register("Content",ContentControl.ContentProperty);
    public static readonly DependencyProperty HeaderProperty = Register("Header",HeaderedContentControl.HeaderProperty);
    public static readonly DependencyProperty ToolTipProperty = Register("ToolTip",FrameworkElement.ToolTipProperty);
    public static readonly DependencyProperty NameProperty = Register("Name",AutomationProperties.NameProperty);
    public static void SetText(DependencyObject d,string value)=>d.SetValue(TextProperty,value);
    public static string GetText(DependencyObject d)=>(string)d.GetValue(TextProperty);
    public static void SetContent(DependencyObject d,string value)=>d.SetValue(ContentProperty,value);
    public static string GetContent(DependencyObject d)=>(string)d.GetValue(ContentProperty);
    public static void SetHeader(DependencyObject d,string value)=>d.SetValue(HeaderProperty,value);
    public static string GetHeader(DependencyObject d)=>(string)d.GetValue(HeaderProperty);
    public static void SetToolTip(DependencyObject d,string value)=>d.SetValue(ToolTipProperty,value);
    public static string GetToolTip(DependencyObject d)=>(string)d.GetValue(ToolTipProperty);
    public static void SetName(DependencyObject d,string value)=>d.SetValue(NameProperty,value);
    public static string GetName(DependencyObject d)=>(string)d.GetValue(NameProperty);

    public static void RefreshResources()
    {
        foreach(var text in Loc.Keys) System.Windows.Application.Current.Resources["Loc."+text]=Loc.T(text);
    }
}
