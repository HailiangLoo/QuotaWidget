using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using QuotaWidget.Core;

namespace QuotaWidget.App;

/// <summary>Compact quota gauge. Only the filled arc encodes the selected used/remaining percent.</summary>
public sealed class QuotaRing : FrameworkElement
{
    public string Label { get; set; } = "";
    public string Accent { get; set; } = "Blue";
    public string Platform { get; set; } = "Claude";
    public bool ShowPlatformIcon { get; set; }
    bool _compact, _singleProvider;
    public bool SingleProvider
    {
        get => _singleProvider;
        set { _singleProvider=value; UpdateHeight(); }
    }
    public bool Compact
    {
        get => _compact;
        set { _compact = value; UpdateHeight(); }
    }
    void UpdateHeight() { Height=SingleProvider?(Compact?30:66):(Compact?37:104);InvalidateVisual(); }

    MeterView? _meter;
    public QuotaRing() { Height = 104; }
    public void Show(MeterView meter, string? status = null)
    {
        _meter = meter;
        ToolTip = $"{Platform} · {Label}\n{meter.Value} · {meter.Other}\n{meter.Reset}" + (status is null ? "" : "\n" + status);
        AutomationPropertiesHelper.SetName(this, $"{Platform} {meter.Label} {meter.Value} {meter.Reset}");
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (_meter is not { } m || ActualWidth <= 0) return;
        if(SingleProvider) { DrawSingle(dc,m); return; }
        if(Compact) { DrawCompact(dc,m); return; }
        var size = Math.Min(53, ActualWidth - 8);
        var radius = size / 2 - 2;
        var center = new Point(ActualWidth / 2,45);
        var accent = Theme.Brush(Accent);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, Height));
        void Text(string s, double y, double font, Brush brush, FontWeight weight)
        {
            var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal), font, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, y));
        }
        Text(Label, 0, 10, Theme.Brush("Muted"), FontWeights.Normal);
        if (ShowPlatformIcon)
        {
            dc.DrawImage(ProviderIcon.Get(Platform), new Rect(Math.Max(0,center.X-size/2-5), 11, 14, 14));
        }
        dc.DrawEllipse(null, new Pen(Theme.Brush("Track"), 3), center, radius, radius);
        if (!m.Missing && m.Fill > 0)
        {
            var pen = new Pen(accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (m.Fill >= 100) dc.DrawEllipse(null, pen, center, radius, radius);
            else
            {
                var angle = m.Fill / 100 * 2 * Math.PI;
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    c.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                    c.ArcTo(new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius), new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, true, false);
                }
                dc.DrawGeometry(null, pen, geo);
            }
        }
        var value = m.Value.TrimEnd('%');
        var fontSize = value.Length > 3 ? 17 : size < 47 ? 20 : 23;
        Text(value, center.Y - fontSize * 0.72, fontSize, m.Missing ? Theme.Brush("Muted") : Theme.QuotaUsageBrush(m.UsedPercent), FontWeights.SemiBold);
        var left = m.ResetsAt - DateTimeOffset.Now;
        var reset = left is null ? "—" : left <= TimeSpan.Zero ? "待重置" : left.Value.TotalDays >= 1 ? $"{(int)left.Value.TotalDays}d {left.Value.Hours}h" : left.Value.TotalHours >= 1 ? $"{(int)left.Value.TotalHours}h {left.Value.Minutes}m" : $"{Math.Max(0, (int)left.Value.TotalMinutes)}m";
        DrawReset(dc, reset);
    }

    FormattedText SingleText(string text,double size,Brush color,FontWeight weight) => new(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"),FontStyles.Normal,weight,FontStretches.Normal),size,color,VisualTreeHelper.GetDpi(this).PixelsPerDip);

    void DrawSingle(DrawingContext dc,MeterView m)
    {
        dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,ActualWidth,Height));
        var icon=ProviderIcon.Get(Platform);
        var value=SingleText(m.Value.TrimEnd('%'),Compact?20:25,m.Missing?Theme.Brush("Muted"):Theme.QuotaUsageBrush(m.UsedPercent),FontWeights.SemiBold);
        var left=m.ResetsAt-DateTimeOffset.Now;
        var reset=left is null?"—":left<=TimeSpan.Zero?"待重置":left.Value.TotalDays>=1?$"{(int)left.Value.TotalDays}d {left.Value.Hours}h":left.Value.TotalHours>=1?$"{(int)left.Value.TotalHours}h {left.Value.Minutes}m":$"{Math.Max(0,(int)left.Value.TotalMinutes)}m";
        if(Compact)
        {
            dc.DrawImage(icon,new Rect(0,7,16,16));
            dc.DrawText(value,new Point(21,(Height-value.Height)/2));
            var clock=ResetText(reset,12);
            dc.DrawText(clock,new Point(21+value.Width+7,(Height-clock.Height)/2+1));
            return;
        }
        var center=new Point(32,32);const double radius=26;
        dc.DrawEllipse(null,new Pen(Theme.Brush("Track"),3),center,radius,radius);
        if(!m.Missing&&m.Fill>0)
        {
            var pen=new Pen(Theme.Brush(Accent),3){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
            if(m.Fill>=100) dc.DrawEllipse(null,pen,center,radius,radius);
            else
            {
                var angle=m.Fill/100*2*Math.PI;var path=new StreamGeometry();
                using(var g=path.Open()) { g.BeginFigure(new Point(center.X,center.Y-radius),false,false);g.ArcTo(new Point(center.X+Math.Sin(angle)*radius,center.Y-Math.Cos(angle)*radius),new Size(radius,radius),0,angle>Math.PI,SweepDirection.Clockwise,true,false); }
                dc.DrawGeometry(null,pen,path);
            }
        }
        for(var font=24d;value.Width>44&&font>=16;font--) value=SingleText(m.Value.TrimEnd('%'),font,m.Missing?Theme.Brush("Muted"):Theme.QuotaUsageBrush(m.UsedPercent),FontWeights.SemiBold);
        dc.DrawText(value,new Point(center.X-value.Width/2,center.Y-value.Height/2));
        dc.DrawImage(icon,new Rect(77,9,16,16));
        dc.DrawText(SingleText("Codex · week",12,Theme.Brush("Ink"),FontWeights.Medium),new Point(99,8));
        dc.DrawText(SingleText("重置",10,Theme.Brush("Muted"),FontWeights.Normal),new Point(77,39));
        dc.DrawText(ResetText(reset,20),new Point(106,31));
    }

    FormattedText ResetText(string text,double size)
    {
        var result=SingleText(text,size,Theme.Brush("Muted"),FontWeights.SemiBold);
        var start=0;
        for(var i=0;i<text.Length;i++)
        {
            if(text[i] is 'd' or 'h' or 'm')
            {
                result.SetForegroundBrush(Theme.Brush(text[i] switch{'d'=>"ResetDay",'h'=>"ResetHour",_=>"ResetMinute"}),start,i-start+1);
                result.SetFontSize(size*.75,i,1);start=i+1;
            }
            else if(char.IsWhiteSpace(text[i]))start=i+1;
        }
        return result;
    }

    void DrawCompact(DrawingContext dc,MeterView meter)
    {
        dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,ActualWidth,Height));
        FormattedText Format(string text,double size,Brush color,FontWeight weight) => new(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"),FontStyles.Normal,weight,FontStretches.Normal),size,color,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var label=Format(Label=="week"?"wk":Label,9,Theme.Brush("Muted"),FontWeights.Normal);
        var text=meter.Value.TrimEnd('%');
        var font=text.Length>=3?17d:19d;
        FormattedText Value() => Format(text,font,meter.Missing?Theme.Brush("Muted"):Theme.QuotaUsageBrush(meter.UsedPercent),FontWeights.SemiBold);
        var value=Value(); var iconSpace=ShowPlatformIcon?17:0;
        while(iconSpace+label.Width+3+value.Width>ActualWidth-4&&font>13) {font-=.5;value=Value();}
        var x=(ActualWidth-iconSpace-label.Width-3-value.Width)/2;
        if(ShowPlatformIcon)
        {
            dc.DrawImage(ProviderIcon.Get(Platform),new Rect(x,5,14,14));x+=17;
        }
        dc.DrawText(label,new Point(x,8)); dc.DrawText(value,new Point(x+label.Width+3,0));
        var left=meter.ResetsAt-DateTimeOffset.Now;
        var reset=left is null?"—":left<=TimeSpan.Zero?"待重置":left.Value.TotalDays>=1?$"{(int)left.Value.TotalDays}d {left.Value.Hours}h":left.Value.TotalHours>=1?$"{(int)left.Value.TotalHours}h {left.Value.Minutes}m":$"{Math.Max(0,(int)left.Value.TotalMinutes)}m";
        DrawReset(dc,reset);
    }

    void DrawReset(DrawingContext dc, string text)
    {
        if (!char.IsDigit(text[0]))
        {
            var hint = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI, Microsoft YaHei UI"), 11, Theme.Brush("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(hint, new Point((ActualWidth - hint.Width) / 2, Compact ? 22 : 80));
            return;
        }
        FormattedText Format(double numberSize)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                numberSize, Theme.Brush("Ink"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var tokenStart = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] is 'd' or 'h' or 'm')
                {
                    var color = text[i] switch { 'd' => "ResetDay", 'h' => "ResetHour", _ => "ResetMinute" };
                    // Tint the number together with its unit, so 3d and 3h differ at a glance.
                    formatted.SetForegroundBrush(Theme.Brush(color), tokenStart, i - tokenStart + 1);
                    formatted.SetFontSize(numberSize * 0.75, i, 1);
                    formatted.SetFontWeight(FontWeights.Medium, i, 1);
                    tokenStart = i + 1;
                }
                else if (char.IsWhiteSpace(text[i]))
                {
                    formatted.SetFontSize(numberSize * 0.65, i, 1);
                    tokenStart = i + 1;
                }
            }
            return formatted;
        }
        var size = Compact ? 12.0 : 18.0;
        var ft = Format(size);
        while (ft.Width > ActualWidth - 5 && size > 10) ft = Format(size -= 0.5);
        dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, Compact ? 22 : 77));
    }
}
