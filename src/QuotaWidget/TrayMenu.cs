using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QuotaWidget.App;

/// <summary>Only the three everyday tray actions, using the widget's light/dark palette.</summary>
public sealed class TrayMenu : ContextMenuStrip
{
    readonly ToolStripMenuItem _show;
    readonly Font _font = new("Microsoft YaHei UI",9f);
    public TrayMenu(Action toggle,Action settings,Action exit)
    {
        ShowImageMargin=ShowCheckMargin=false;
        DropShadowEnabled=true;
        Font=_font;
        _show=new ToolStripMenuItem("显示小窗",null,(_,_)=>toggle());
        Items.Add(_show);
        Items.Add(new ToolStripMenuItem("设置",null,(_,_)=>settings()));
        Items.Add(new ToolStripMenuItem("退出",null,(_,_)=>exit()));
        Prepare(true,false);
    }
    public void Prepare(bool dark,bool windowVisible)
    {
        var scale=DeviceDpi/96f;
        Padding=new Padding((int)(4*scale),(int)(5*scale),(int)(4*scale),(int)(5*scale));
        _show.Text=windowVisible?"隐藏小窗":"显示小窗";
        BackColor=dark?Color.FromArgb(32,32,32):Color.FromArgb(250,250,250);
        ForeColor=dark?Color.FromArgb(238,238,239):Color.FromArgb(32,33,36);
        Renderer=new MenuRenderer(BackColor,ForeColor,dark?Color.FromArgb(53,55,60):Color.FromArgb(235,237,239),dark?Color.FromArgb(63,65,70):Color.FromArgb(218,219,221));
        foreach(ToolStripItem item in Items)
        {
            item.AutoSize=false;item.Size=new Size((int)(148*scale),(int)(30*scale));
            item.Margin=Padding.Empty;item.ForeColor=ForeColor;
        }
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing);if(disposing)_font.Dispose(); }

    sealed class MenuRenderer(Color background,Color ink,Color selected,Color border):ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(background);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen=new Pen(border);e.Graphics.DrawRectangle(pen,0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if(!e.Item.Selected&&!e.Item.Pressed)return;
            var r=new RectangleF(1,1,e.Item.Width-2,e.Item.Height-2);var d=6*(e.ToolStrip?.DeviceDpi??96)/96f;
            using var shape=new GraphicsPath();
            shape.AddArc(r.X,r.Y,d,d,180,90);shape.AddArc(r.Right-d,r.Y,d,d,270,90);
            shape.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);shape.AddArc(r.X,r.Bottom-d,d,d,90,90);shape.CloseFigure();
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using var brush=new SolidBrush(selected);e.Graphics.FillPath(brush,shape);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var inset=(int)(11*(e.ToolStrip?.DeviceDpi??96)/96f);
            TextRenderer.DrawText(e.Graphics,e.Text,e.TextFont,new Rectangle(inset,0,e.Item.Width-2*inset,e.Item.Height),ink,
                TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
        }
    }
}
