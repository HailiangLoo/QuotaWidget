using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuotaWidget.Core;

namespace QuotaWidget.App;

public sealed class LicenseWindow : Window
{
    public static string Notices
    {
        get
        {
            var assembly = typeof(LicenseWindow).Assembly;
            string Read(string name)
            {
                using var stream = assembly.GetManifestResourceStream("QuotaWidget." + name);
                if (stream is null) return "";
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            return Read("LICENSE") + "\n\n" + Read("NOTICE") + "\n\n" + Read("RuntimeNotices");
        }
    }

    public LicenseWindow()
    {
        Title = "QuotaWidget · " + Loc.T("开源许可");
        Width = 720; Height = 560; MinWidth = 360; MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg");
        var text = new TextBox
        {
            Text = Notices, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0), Margin = new Thickness(16), FontSize = 12
        };
        text.SetResourceReference(BackgroundProperty, "Bg");
        text.SetResourceReference(ForegroundProperty, "Ink");
        Content = text;
    }
}
