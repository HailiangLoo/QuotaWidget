using System.Text.Json;

namespace QuotaWidget.Core;

/// <summary>Persist coverage facts; format them in the reader's current language.</summary>
public sealed record TokenCoverage(DateTimeOffset Since, int PendingFiles, bool LimitedDiscovery, int Skipped, bool ParentReadFailed)
{
    public const string MetaKey = "coverage.v1";
    public string Text(bool english) => Loc.F($"本机日志 · 自 {Since.ToLocalTime():M/d HH:mm}；仅已记录用量，非账号账单。",english) +
        (PendingFiles > 0 ? Loc.F($"\n正在补读 {PendingFiles} 个文件。",english) : "") +
        (LimitedDiscovery ? Loc.T("\n跟踪最近 256 个日志，其余未包含。",english) : "") +
        (Skipped > 0 ? Loc.F($"\n有 {Skipped} 条缺失、损坏或过长记录未计入。",english) : "") +
        Loc.T("\n已确认归属的 Codex 子代理（含多层）并入所属 chat；未知或冲突关系保持单列。模型分组仍按实际模型，平台总量不变。",english) +
        (ParentReadFailed ? "\n" + Loc.T("子代理归属暂不可读；保留已确认关系，稍后重试。",english) : "") +
        Loc.T("\n包含能识别的子代理请求；IN 含缓存写入，CACHE 为命中输入，OUT 已含推理输出。\n按首次用量记录时间记账；不代表每秒实际生成速度。",english);

    public static string? ReadText(TokenStore store, bool english)
    {
        if (store.Meta(MetaKey) is { } json)
            try
            {
                if (JsonSerializer.Deserialize<TokenCoverage>(json) is { } facts && facts.Since != default && facts.PendingFiles >= 0 && facts.Skipped >= 0)
                    return facts.Text(english);
            }
            catch (JsonException) { }
        // Older read-only snapshots remain usable before the first new importer poll.
        return store.Meta(english ? "coverage.en" : "coverage");
    }
}
