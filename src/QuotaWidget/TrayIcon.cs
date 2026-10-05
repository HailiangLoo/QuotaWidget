using System;
using System.Linq;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using QuotaWidget.Core;

namespace QuotaWidget.App;

/// <summary>Quota companion: two eyes show each provider's remaining weekly quota.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    readonly TrayMenu _menu;
    IntPtr _hicon;
    string _lastKey = "";
    readonly Bitmap _claudeLogo=ProviderIcon.TrayBitmap("Claude");
    readonly Bitmap _codexLogo=ProviderIcon.TrayBitmap("Codex");

    public TrayIcon(App app)
    {
        _menu = new TrayMenu(app.ToggleWindow,app.OpenSettings,app.ExitApp);
        _menu.Opening += (_, _) => _menu.Prepare(Theme.IsDark,app.WindowVisible);

        _icon = new NotifyIcon { ContextMenuStrip = _menu, Text = Loc.T("额度"), Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) app.ToggleWindow(); };
        SetIcon(null,null,app.Monitors(ChatPlatform.Claude),app.Monitors(ChatPlatform.Codex));
    }

    public void Update(WidgetView view,WidgetView codex,WidgetSettings settings)
    {
        var claudeEnabled=settings.Collects(ChatPlatform.Claude);var codexEnabled=settings.Collects(ChatPlatform.Codex);
        var claudeRemaining=Remaining(view.Week);var codexRemaining=Remaining(codex.Week);
        string P(double? value)=>value is { } n?$"{n:0.#}%":"—";
        string Old(WidgetView v)=>v.TrayText.Contains(Loc.T("旧数据"),StringComparison.Ordinal)?Loc.T("（旧）"):"";
        var text=Loc.T("周剩余 · ")+string.Join(" · ",new[]{claudeEnabled?$"Claude {P(claudeRemaining)}{Old(view)}":null,codexEnabled?$"Codex {P(codexRemaining)}{Old(codex)}":null}.Where(s=>s is not null));
        if(!claudeEnabled&&!codexEnabled)text=Loc.T("已停止挂件监听，保留登录和历史。");
        if(claudeEnabled) text+=Loc.F($"\n5h 剩余 {P(Remaining(view.Five))} · Fable 剩余 {P(Remaining(view.Fable))}");
        if(codexEnabled&&!codex.Five.Missing)text+=Loc.F($"\nCodex 5h 剩余 {P(Remaining(codex.Five))}");
        _icon.Text = text.Length > 127 ? text[..127] : text;
        SetIcon(claudeRemaining,codexRemaining,claudeEnabled,codexEnabled);
    }

    static double? Remaining(MeterView meter)=>!meter.Missing&&meter.UsedPercent is { } used&&double.IsFinite(used)?Math.Clamp(100-used,0,100):null;

    void SetIcon(double? claudeRemaining,double? codexRemaining,bool claudeEnabled,bool codexEnabled)
    {
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        var key = $"{size}|{claudeEnabled}|{codexEnabled}|{claudeRemaining:0}|{codexRemaining:0}";
        if (key == _lastKey) return;
        using var bmp = QuotaIcon.Render(size,claudeRemaining,codexRemaining,claudeEnabled,codexEnabled,_claudeLogo,_codexLogo);
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
