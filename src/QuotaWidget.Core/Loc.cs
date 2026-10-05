using System.Globalization;
using System.Text.Json;

namespace QuotaWidget.Core;

/// <summary>Presentation text only. Identifiers, stored data and calculations never depend on language.</summary>
public static class Loc
{
    static readonly IReadOnlyDictionary<string,string> English = Load();
    static volatile bool _isEnglish;
    public static bool IsEnglish => _isEnglish;
    public static IEnumerable<string> Keys => English.Keys;
    public static string Language => IsEnglish ? "en" : "zh-CN";

    public static string Resolve(string? language, CultureInfo? system = null) => language switch
    {
        "en" => "en",
        "zh-CN" => "zh-CN",
        _ => (system ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en"
    };

    public static void Configure(string? language, CultureInfo? system = null) => _isEnglish = Resolve(language,system) == "en";
    public static string T(string text, bool? english = null) => (english ?? IsEnglish) && English.TryGetValue(text,out var translation) ? translation : text;
    public static string F(FormattableString text, bool? english = null) => string.Format(CultureInfo.InvariantCulture,T(text.Format,english),text.GetArguments());

    static IReadOnlyDictionary<string,string> Load()
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream("QuotaWidget.Core.Strings.en.json")
            ?? throw new InvalidOperationException("Missing English language resources.");
        return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;
    }
}
