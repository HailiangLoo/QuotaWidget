using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace QuotaWidget.App;

/// <summary>Use an installed provider's identification asset locally; never redistribute it.</summary>
public static class ProviderIcon
{
    static readonly Dictionary<string,ImageSource> Cache=new();
    public static ImageSource Get(string platform)
    {
        var key=platform+"|"+Theme.IsDark;
        if(Cache.TryGetValue(key,out var cached))return cached;
        ImageSource? image=null;
        try
        {
            using var packages=Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            var prefix=platform=="Claude"?"Claude_":"OpenAI.Codex_";
            foreach(var name in (packages?.GetSubKeyNames()??[]).Where(n=>n.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).OrderDescending())
            {
                using var package=packages!.OpenSubKey(name);
                if(package?.GetValue("PackageRootFolder") is not string root||!Path.IsPathFullyQualified(root))continue;
                var file=Path.Combine(root,"assets",platform!="Claude"&&!Theme.IsDark?
                    "Square44x44Logo.targetsize-64_altform-lightunplated.png":"Square44x44Logo.targetsize-64_altform-unplated.png");
                if(!File.Exists(file))continue;
                using var stream=File.OpenRead(file);
                var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();image=bitmap;break;
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or FormatException) { }
        return Cache[key]=image??Fallback(platform,Theme.IsDark);
    }

    public static ImageSource Fallback(string platform,bool dark)
    {
        var drawing=new DrawingGroup();
        using(var dc=drawing.Open())
        {
            var color=new SolidColorBrush((Color)ColorConverter.ConvertFromString(platform=="Claude"?"#D97757":"#7960AB"));
            dc.DrawRoundedRectangle(color,null,new Rect(0,0,32,32),7,7);
            var text=new FormattedText(platform=="Claude"?"C":">_",CultureInfo.InvariantCulture,FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"),FontStyles.Normal,FontWeights.Bold,FontStretches.Normal),platform=="Claude"?23:17,Brushes.White,1);
            dc.DrawText(text,new Point((32-text.Width)/2,(32-text.Height)/2-1));
        }
        drawing.Freeze();var result=new DrawingImage(drawing);result.Freeze();return result;
    }
}
