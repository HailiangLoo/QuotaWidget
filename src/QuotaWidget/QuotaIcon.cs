using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QuotaWidget.App;

/// <summary>Two large provider lenses and quiet quota rails, sized for the Windows tray.</summary>
public static class QuotaIcon
{
    static readonly Color Claude=Color.FromArgb(0xe3,0xa0,0x80);
    static readonly Color Codex=Color.FromArgb(0xbc,0x9d,0xf0);
    public static Bitmap Render(int size,double? claudeRemaining,double? codexRemaining,bool claudeEnabled=true,bool codexEnabled=true,
        Image? claudeLogo=null,Image? codexLogo=null)
    {
        var bitmap=new Bitmap(size,size,PixelFormat.Format32bppArgb);
        using var g=Graphics.FromImage(bitmap);g.Clear(Color.Transparent);
        g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.InterpolationMode=InterpolationMode.HighQualityBicubic;
        g.ScaleTransform(size/32f,size/32f);
        using var shell=Rounded(new RectangleF(.4f,3,31.2f,26),7);
        using var body=new SolidBrush(Color.FromArgb(0x24,0x28,0x30));
        using var rim=new Pen(Color.FromArgb(0x6c,0x73,0x80),.65f);
        g.FillPath(body,shell);g.DrawPath(rim,shell);
        Lens(new RectangleF(1,5,14.5f,14.5f),Claude,claudeRemaining,claudeEnabled,claudeLogo,"C");
        Lens(new RectangleF(16.5f,5,14.5f,14.5f),Codex,codexRemaining,codexEnabled,codexLogo,">_");
        return bitmap;

        void Lens(RectangleF rect,Color accent,double? remaining,bool enabled,Image? logo,string fallback)
        {
            using var clip=new GraphicsPath();clip.AddEllipse(rect);
            using var field=new SolidBrush(Color.FromArgb(0x34,0x39,0x43));g.FillEllipse(field,rect);
            var state=g.Save();g.SetClip(clip,CombineMode.Intersect);
            if(logo is not null)
            {
                using var attributes=new ImageAttributes();attributes.SetColorMatrix(new ColorMatrix{Matrix33=enabled?1:.3f});
                g.DrawImage(logo,Rectangle.Round(rect),0,0,logo.Width,logo.Height,GraphicsUnit.Pixel,attributes);
            }
            else
            {
                using var ink=new SolidBrush(enabled?accent:Color.Gray);using var font=new Font("Segoe UI",fallback=="C"?11:8,FontStyle.Bold,GraphicsUnit.Pixel);
                using var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center};g.DrawString(fallback,font,ink,rect,format);
            }
            g.Restore(state);
            var rail=new RectangleF(rect.X+1.7f,23,11.1f,2.4f);
            using var track=new SolidBrush(Color.FromArgb(0x4a,0x4f,0x59));using var trackShape=Rounded(rail,1.2f);g.FillPath(track,trackShape);
            using var color=new SolidBrush(enabled?accent:Color.FromArgb(0x71,0x76,0x80));
            if(!enabled){g.FillRectangle(color,rail.X+4,rail.Y,3,rail.Height);return;}
            if(remaining is not {} value||!double.IsFinite(value)){g.FillEllipse(color,rail.X+4.5f,rail.Y,2.4f,2.4f);return;}
            var width=(float)Math.Clamp(value,0,100)/100*rail.Width;
            if(width>0){var saved=g.Save();g.SetClip(trackShape,CombineMode.Intersect);g.FillRectangle(color,rail.X,rail.Y,width,rail.Height);g.Restore(saved);}
        }
    }
    static GraphicsPath Rounded(RectangleF r,float radius)
    {
        var p=new GraphicsPath();var d=radius*2;
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
}
