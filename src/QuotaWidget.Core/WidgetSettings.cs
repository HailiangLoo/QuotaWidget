using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaWidget.Core;

/// <summary>
/// settings.json. pollIntervalSeconds is the single source for the collection interval.
/// Legacy Fable conversion entries are retained for compatibility, not used by the dashboard.
/// </summary>
public sealed class WidgetSettings
{
    public static readonly int[] PollChoices = [300, 600, 1800, 3600];
    public static readonly int[] RangeChoices = [60, 300, 720, 1440, 4320, ChartRanges.All];

    public int SettingsVersion { get; set; } = 1;
    public int PollIntervalSeconds { get; set; } = 300;
    public bool CollectorEnabled { get; set; } = true;
    public string Monitoring { get; set; } = "both";
    public bool Monitors(ChatPlatform platform) => Monitoring == "both" || Monitoring == (platform == ChatPlatform.Claude ? "claude" : "codex");
    public bool AutoLogoutOnExit { get; set; } = false;
    public bool CacheRemindersEnabled { get; set; } = true;
    public bool TokenTrackingEnabled { get; set; } = true;
    public int TrendMinutes { get; set; } = 120;
    public string? ClaudeConfigDir { get; set; }
    public string? ClaudeExePath { get; set; }
    public string FableModelName { get; set; } = "Fable";

    public string Display { get; set; } = "remaining";
    public bool Smoothing { get; set; } = true;
    public string ChartMode { get; set; } = "rate";
    public string? ClaudeChartMode { get; set; }
    public string? CodexChartMode { get; set; }
    public string ChartModeFor(ChatPlatform platform) => (platform==ChatPlatform.Codex?CodexChartMode:ClaudeChartMode) ?? ChartMode;
    /// <summary>
    /// Legacy q per "sourceId|profileKey". Since 0.3 all lines use their own quota units;
    /// these values are no longer displayed or used for chart calculations.
    /// </summary>
    public Dictionary<string, double> FableToWeekByProfile { get; set; } = new();
    public int RangeMinutes { get; set; } = 300;
    public bool TotalVisible { get; set; } = true;
    public bool FableVisible { get; set; } = true;
    public bool CodexVisible { get; set; } = true;
    public bool CompactMode { get; set; }
    public bool ClaudeChartCollapsed { get; set; }
    public bool CodexChartCollapsed { get; set; }

    public double Width { get; set; } = 300;
    public bool Topmost { get; set; } = true;
    /// <summary>Window top-left in physical pixels (monitors may differ in scaling, so DIPs are ambiguous).</summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public string? LastSourceId { get; set; }
    public string? LastProfileKey { get; set; }

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static WidgetSettings Load(string path, out string? warning)
    {
        warning = null;
        var text = AtomicFile.TryReadAllText(path);
        if (text is null) return new WidgetSettings();
        try
        {
            var s = JsonSerializer.Deserialize<WidgetSettings>(text, Options) ?? new WidgetSettings();
            s.Normalize();
            return s;
        }
        catch (JsonException)
        {
            var bad = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(path, bad, true); } catch { }
            warning = "settings.json 无法解析，已备份并使用默认值";
            return new WidgetSettings();
        }
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    public void Normalize()
    {
        if (Monitoring is not ("both" or "claude" or "codex")) Monitoring = "both";
        if (PollIntervalSeconds < 60) PollIntervalSeconds = 60;
        if (PollIntervalSeconds > 86400) PollIntervalSeconds = 86400;
        if (!RangeChoices.Contains(RangeMinutes)) RangeMinutes = 300;
        if (Display is not ("used" or "remaining")) Display = "remaining";
        if (ChartMode is not ("rate" or "cumulative")) ChartMode = "rate";
        if (ClaudeChartMode is not ("rate" or "cumulative")) ClaudeChartMode = ChartMode;
        if (CodexChartMode is not ("rate" or "cumulative")) CodexChartMode = ChartMode;
        if (TrendMinutes is not (60 or 90 or 120)) TrendMinutes = 120;
        FableToWeekByProfile ??= new();
        foreach (var key in FableToWeekByProfile.Where(kv => !ValidQ(kv.Value)).Select(kv => kv.Key).ToList())
            FableToWeekByProfile.Remove(key);
        if (!double.IsFinite(Width) || Width < 240 || Width > 340) Width = 300;
        if (string.IsNullOrWhiteSpace(FableModelName)) FableModelName = "Fable";
    }

    public static bool ValidQ(double q) => double.IsFinite(q) && q > 0 && q <= 100;

    public static string ProfileId(string sourceId, string profileKey) => sourceId + "|" + profileKey;

    public double? QFor(string? sourceId, string? profileKey) =>
        sourceId is not null && profileKey is not null && FableToWeekByProfile.TryGetValue(ProfileId(sourceId, profileKey), out var q) ? q : null;

    public void SetQ(string sourceId, string profileKey, double? q)
    {
        var id = ProfileId(sourceId, profileKey);
        if (q is { } v && ValidQ(v)) FableToWeekByProfile[id] = v;
        else FableToWeekByProfile.Remove(id);
    }

    public string ResolveClaudeConfigDir(DataPaths paths) =>
        string.IsNullOrWhiteSpace(ClaudeConfigDir) ? paths.DefaultClaudeConfigDir : Environment.ExpandEnvironmentVariables(ClaudeConfigDir);
}
