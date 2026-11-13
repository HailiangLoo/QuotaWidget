using System;
using System.Linq;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using QuotaWidget.Core;

namespace QuotaWidget.App;

/// <summary>Provider-logo face in the tray; current quota remains available in its tooltip.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    readonly TrayMenu _menu;
    IntPtr _hicon;
    string _lastKey = "";
    readonly Bitmap _claudeLogo=ProviderIcon.TrayBitmap("Claude");
    readonly Bitmap _codexLogo=ProviderIcon.TrayBitmap("Codex");
    readonly Action _showWidget;
    CapacityNotice? _capacityNotice;

    public TrayIcon(App app)
    {
        _showWidget=app.ShowWindow;
        _menu = new TrayMenu(app.ToggleWindow,app.OpenSettings,app.ExitApp);
        _menu.Opening += (_, _) => _menu.Prepare(Theme.IsDark,app.WindowVisible);

        _icon = new NotifyIcon { ContextMenuStrip = _menu, Text = Loc.T("额度"), Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) app.ToggleWindow(); };
        SetIcon(app.Monitors(ChatPlatform.Claude),app.Monitors(ChatPlatform.Codex));
    }

    public void Update(WidgetView view,WidgetView codex,WidgetSettings settings)
    {
        var claudeEnabled=settings.Collects(ChatPlatform.Claude);var codexEnabled=settings.Collects(ChatPlatform.Codex);
        var claudeRemaining=Remaining(view.Week);var codexRemaining=Remaining(codex.Week);
        string P(double? value)=>value is { } n?$"{n:0.#}%":"—";
        string Old(WidgetView v)=>v.TrayText.Contains(Loc.T("旧数据"),StringComparison.Ordinal)?Loc.T("（旧）"):"";
        var text=Loc.T("周剩余 · ")+string.Join(" · ",new[]{claudeEnabled?$"Claude {P(claudeRemaining)}{Old(view)}":null,codexEnabled?$"Codex {P(codexRemaining)}{Old(codex)}":null}.Where(s=>s is not null));
        if(!claudeEnabled&&!codexEnabled)text=Loc.T("已停止挂件监听，保留登录和历史。");
        if(claudeEnabled) text+=view.Five.Missing?Loc.F($"\nFable 剩余 {P(Remaining(view.Fable))}")
            :Loc.F($"\n5h 剩余 {P(Remaining(view.Five))} · Fable 剩余 {P(Remaining(view.Fable))}");
        if(codexEnabled&&!codex.Five.Missing)text+=Loc.F($"\nCodex 5h 剩余 {P(Remaining(codex.Five))}");
        _icon.Text = text.Length > 127 ? text[..127] : text;
        SetIcon(claudeEnabled,codexEnabled);
    }

    static double? Remaining(MeterView meter)=>!meter.Missing&&meter.UsedPercent is { } used&&double.IsFinite(used)?Math.Clamp(100-used,0,100):null;

    public bool AlertCapacity(System.Collections.Generic.IReadOnlyList<ChatCacheEntry> chats, Action<string>? log = null)
    {
        if(chats.Count==0||NotificationEnvironment.DeferVisual())return false;
        if(_capacityNotice is null)
        {
            var notice=new CapacityNotice(_showWidget,log:log);_capacityNotice=notice;
            notice.Closed+=(_,_)=>{if(ReferenceEquals(_capacityNotice,notice))_capacityNotice=null;};
        }
        _capacityNotice.Add(chats);
        return _capacityNotice.Present();
    }

    public void SynchronizeCapacity(System.Collections.Generic.IReadOnlyList<ChatCacheEntry> current)=>_capacityNotice?.Synchronize(current);

    void SetIcon(bool claudeEnabled,bool codexEnabled)
    {
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        var key = $"{size}|{claudeEnabled}|{codexEnabled}";
        if (key == _lastKey) return;
        using var bmp = QuotaIcon.Render(size,claudeEnabled,codexEnabled,_claudeLogo,_codexLogo);
        var h = bmp.GetHicon();
        var previous = _icon.Icon;
        _icon.Icon = Icon.FromHandle(h);
        previous?.Dispose();
        if (_hicon != IntPtr.Zero) DestroyIcon(_hicon);
        _hicon = h;
        _lastKey = key;
    }

    public void Dispose()
    {
        _capacityNotice?.Close();
        _icon.Visible = false;
        var image = _icon.Icon;
        _icon.Dispose();
        _menu.Dispose();
        _claudeLogo.Dispose();_codexLogo.Dispose();
        image?.Dispose();
        if (_hicon != IntPtr.Zero) DestroyIcon(_hicon);
        _hicon = IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
