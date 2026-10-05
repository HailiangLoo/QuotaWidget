using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QuotaWidget.App;

/// <summary>A small quota companion: two week-quota cells, a listening antenna and a smile.</summary>
public static class QuotaIcon
{
    // Fixed colours belong to the dark icon body, independently of the widget/taskbar theme.
    static readonly Color Claude = Color.FromArgb(0xe3, 0xa0, 0x80);
    static readonly Color Codex = Color.FromArgb(0xbc, 0x9d, 0xf0);

    public static Bitmap Render(int size, double? claudeRemaining, double? codexRemaining, bool claudeEnabled = true, bool codexEnabled = true)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(size / 32f, size / 32f);

        using var stem = new Pen(Color.FromArgb(0x91, 0xac, 0xc0), 2) { StartCap=LineCap.Round, EndCap=LineCap.Round };
        g.DrawLine(stem, 16, 4, 16, 8);
        using var antenna = new SolidBrush(Color.FromArgb(0x69, 0xb8, 0xf3));
        g.FillEllipse(antenna, 13.5f, .5f, 5, 5);

        var face = new RectangleF(1.5f, 7, 29, 23.5f);
        using var shape = Rounded(face, 6.5f);
        using var shell = new LinearGradientBrush(face, Color.FromArgb(0x36, 0x40, 0x4d), Color.FromArgb(0x1e, 0x25, 0x30), 90);
        using var rim = new Pen(Color.FromArgb(0x88, 0x95, 0xa7), 1);
        g.FillPath(shell, shape); g.DrawPath(rim, shape);

        Cell(new RectangleF(7.5f, 11.5f, 5.5f, 10), Claude, claudeRemaining, claudeEnabled);
        Cell(new RectangleF(19, 11.5f, 5.5f, 10), Codex, codexRemaining, codexEnabled);

        using var smile = new Pen(Color.FromArgb(0xc6, 0xd9, 0xdf), 1.6f) { StartCap=LineCap.Round, EndCap=LineCap.Round };
        g.DrawBezier(smile, 12, 25, 14.5f, 27, 17.5f, 27, 20, 25);
        return bitmap;

        void Cell(RectangleF rect, Color color, double? remaining, bool enabled)
        {
            if (!enabled)
            {
                // A sleeping eye denotes an unmonitored source; it is not an empty quota.
                using var sleeping = new Pen(Color.FromArgb(0x79, 0x83, 0x92), 1.6f) { StartCap=LineCap.Round, EndCap=LineCap.Round };
                g.DrawLine(sleeping, rect.Left, rect.Top+rect.Height/2, rect.Right, rect.Top+rect.Height/2);
                return;
            }
            using var path = Rounded(rect, 2.6f);
            using var track = new SolidBrush(Color.FromArgb(0x48, 0x4e, 0x5b));
            g.FillPath(track, path);
            if (remaining is { } amount && double.IsFinite(amount))
            {
                var fill = (float)Math.Clamp(amount, 0, 100) / 100 * rect.Height;
                if (fill > 0)
                {
                    var state = g.Save(); g.SetClip(path, CombineMode.Intersect);
                    using var ink = new SolidBrush(color);
                    g.FillRectangle(ink, rect.Left, rect.Bottom-fill, rect.Width, fill);
                    g.Restore(state);
                }
            }
            else
            {
                // An unknown reading gets a dot; never pretend that it is zero or full.
                using var unknown = new SolidBrush(color);
                g.FillEllipse(unknown, rect.Left+1.5f, rect.Top+3.75f, 2.5f, 2.5f);
            }
            using var edge = new Pen(Color.FromArgb(150, color), .65f);
            g.DrawPath(edge, path);
        }
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); var d = radius*2;
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);
        p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);
        p.CloseFigure();return p;
    }
}
