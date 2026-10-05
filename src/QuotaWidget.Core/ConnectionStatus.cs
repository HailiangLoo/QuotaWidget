namespace QuotaWidget.Core;

public sealed record ConnectionStatus(string Text,string Detail,string Color);

/// <summary>Connection is confirmed by a recent successful quota query, never by the existence of a CLI alone.</summary>
public static class Connections
{
    public static ConnectionStatus Describe(ChatPlatform platform,WidgetSettings settings,LatestEnvelope? latest,
        bool? dependencyReady,DateTimeOffset now,bool checking=false)
    {
        ConnectionStatus State(string text,string detail,string color="Muted") => new(Loc.T(text),Loc.T(detail),color);
        if(!settings.SetupCompleted)return State("等待设置",dependencyReady==false?"未找到兼容的官方程序，请先安装。":"选择监听平台，完成官方登录后开始。");
        if(!settings.Connected(platform))return State("已断开","已停止挂件监听，保留登录和历史。");
        if(!settings.Monitors(platform))return State("未启用","此平台未包含在当前监听模式中。");
        if(!settings.CollectorEnabled)return State("采集已关闭","可连接此平台以恢复自动采集。");
        if(checking)return State("正在检查","正在检查本机程序与额度状态。");
        if(dependencyReady==false||latest?.ErrorCode is "cli_missing" or "cli_missing_or_untrusted")
            return State("需要安装","未找到兼容的官方程序，请先安装。","Orange");
        if(latest?.Status==Statuses.AuthRequired)return State("需要登录",platform==ChatPlatform.Claude?
            "使用挂件专用的 Claude 登录。":"在官方 Codex / ChatGPT 应用中登录 ChatGPT 账号。","Orange");
        if(latest is null)return State("等待验证","已启用监听，等待首次成功读取额度。");
        if(!Statuses.HasSnapshot(latest.Status))return State("暂时不可用","检查登录或网络，采集器会自动重试。","Orange");
        if(latest.Snapshot is not {} snapshot||now-snapshot.ObservedAt>TimeSpan.FromSeconds(Math.Max(60,latest.EffectivePollIntervalSeconds)*RateEngine.GapFactor+60))
            return State("数据陈旧","保留上次读数，等待新的额度数据。","Orange");
        return new(Loc.T("已连接"),Loc.F($"上次更新 {snapshot.ObservedAt.ToLocalTime():HH:mm}"),"Green");
    }
}
