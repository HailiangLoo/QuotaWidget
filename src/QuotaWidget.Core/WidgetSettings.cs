using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaWidget.Core;

/// <summary>
/// settings.json. pollIntervalSeconds is the single source for the collection interval.
/// Unknown legacy fields are ignored on load and dropped on the next save.
/// </summary>
public sealed class WidgetSettings
{
    public static readonly int[] PollChoices = [300, 600, 1800, 3600];
    public static readonly int[] RangeChoices = [60, 300, 720, 1440, 4320, ChartRanges.All];

    public int SettingsVersion { get; set; } = 1;
    public int PollIntervalSeconds { get; set; } = 300;
    public bool CollectorEnabled { get; set; } = true;
    public string Monitoring { get; set; } = "both";
    public string Language { get; set; } = "auto";
    // Existing settings keep their working connections. App startup marks only a new installation unconfigured.
    public bool SetupCompleted { get; set; } = true;
    public bool ClaudeConnected { get; set; } = true;
    public bool CodexConnected { get; set; } = true;
    public bool Connected(ChatPlatform platform) => platform == ChatPlatform.Claude ? ClaudeConnected : CodexConnected;
    public bool Listens(ChatPlatform platform) => SetupCompleted && Connected(platform);
    public bool Collects(ChatPlatform platform) => CollectorEnabled && Listens(platform);
    // Legacy setting name: this is the visible platform selection, not connection intent.
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
    public int RangeMinutes { get; set; } = 300;
    public bool TotalVisible { get; set; } = true;
    public bool FableVisible { get; set; } = true;
    public bool CodexVisible { get; set; } = true;
    public bool CompactMode { get; set; }
    public bool ClaudeChartCollapsed { get; set; }
    public bool CodexChartCollapsed { get; set; }

    public double Width { get; set; } = 300;
    public double? Height { get; set; }
    public double CompactWidth { get; set; } = 240;
    public double? CompactHeight { get; set; }
    public const double MinWidth = 240, MaxWidth = 1200, MinHeight = 80, MaxHeight = 2400;
    public const double MinUiScale = .8, MaxUiScale = 2;
    public double UiScale { get; set; } = 1;
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
            // Pre-connection settings used the selector to opt out of a provider.
            // Preserve that choice once, while retaining every explicit modern flag.
            using var json=JsonDocument.Parse(text,new(){AllowTrailingCommas=true,CommentHandling=JsonCommentHandling.Skip});
            if(json.RootElement.ValueKind==JsonValueKind.Object)
            {
                if(!json.RootElement.TryGetProperty("claudeConnected",out _))s.ClaudeConnected=s.Monitors(ChatPlatform.Claude);
                if(!json.RootElement.TryGetProperty("codexConnected",out _))s.CodexConnected=s.Monitors(ChatPlatform.Codex);
            }
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
        if (Language is not ("auto" or "en" or "zh-CN")) Language = "auto";
        if (Monitoring is not ("both" or "claude" or "codex")) Monitoring = "both";
        if (PollIntervalSeconds < 60) PollIntervalSeconds = 60;
        if (PollIntervalSeconds > 86400) PollIntervalSeconds = 86400;
        if (!RangeChoices.Contains(RangeMinutes)) RangeMinutes = 300;
        if (Display is not ("used" or "remaining")) Display = "remaining";
        if (ChartMode is not ("rate" or "cumulative")) ChartMode = "rate";
        if (ClaudeChartMode is not ("rate" or "cumulative")) ClaudeChartMode = ChartMode;
        if (CodexChartMode is not ("rate" or "cumulative")) CodexChartMode = ChartMode;
        if (TrendMinutes is not (60 or 90 or 120 or 150)) TrendMinutes = 120;
        if (!double.IsFinite(Width) || Width < MinWidth || Width > MaxWidth) Width = 300;
        if (!double.IsFinite(CompactWidth) || CompactWidth < MinWidth || CompactWidth > MaxWidth) CompactWidth = 240;
        static double? NormalizeHeight(double? height) => height is not {} h || !double.IsFinite(h) || h <= 0 ? null : Math.Clamp(h, MinHeight, MaxHeight);
        Height = NormalizeHeight(Height); CompactHeight = NormalizeHeight(CompactHeight);
        UiScale = double.IsFinite(UiScale) ? Math.Clamp(UiScale, MinUiScale, MaxUiScale) : 1;
        if (string.IsNullOrWhiteSpace(FableModelName)) FableModelName = "Fable";
    }

    public string ResolveClaudeConfigDir(DataPaths paths) =>
        string.IsNullOrWhiteSpace(ClaudeConfigDir) ? paths.DefaultClaudeConfigDir : Environment.ExpandEnvironmentVariables(ClaudeConfigDir);
}
