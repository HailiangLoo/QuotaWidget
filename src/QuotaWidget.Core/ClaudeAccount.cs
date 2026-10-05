using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaWidget.Core;

/// <summary>
/// Read-only access to the non-secret files of the widget's Claude Code config directory.
/// The credential file (.credentials.json) is only ever stat'ed, never opened: the official
/// CLI alone holds and refreshes the login.
/// </summary>
public static class ClaudeConfigFiles
{
    public static string CredentialsPath(string configDir) => Path.Combine(configDir, ".credentials.json");
    public static string GlobalConfigPath(string configDir) => Path.Combine(configDir, ".claude.json");

    /// <summary>Change detector for "has the user logged in / out" without reading the secret.</summary>
    public static (DateTime, long)? CredentialStamp(string configDir)
    {
        var fi = new FileInfo(CredentialsPath(configDir));
        return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : null;
    }

    static JsonElement? ReadGlobal(string configDir)
    {
        var text = AtomicFile.TryReadAllText(GlobalConfigPath(configDir));
        if (text is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// When the CLI last fetched usage from the server (it caches the body with this stamp).
    /// Used as the observation time and to tell a fresh fetch from a cached answer.
    /// </summary>
    public static long? CachedUsageFetchedAtMs(string configDir)
    {
        if (ReadGlobal(configDir) is not { } root) return null;
        if (!root.TryGetProperty("cachedUsageUtilization", out var c) || c.ValueKind != JsonValueKind.Object) return null;
        return c.TryGetProperty("fetchedAtMs", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt64(out var ms) ? ms : null;
    }

    public static ClaudeIdentity? ReadIdentity(string configDir)
    {
        if (ReadGlobal(configDir) is not { } root) return null;
        if (!root.TryGetProperty("oauthAccount", out var a) || a.ValueKind != JsonValueKind.Object) return null;
        var account = Str(a, "accountUuid");
        if (string.IsNullOrWhiteSpace(account)) return null;
        string? tier = null;
        foreach (var p in a.EnumerateObject())
            if (p.Name.Contains("RateLimitTier", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                tier = p.Value.GetString();
        return new ClaudeIdentity(account, Str(a, "organizationUuid"), tier);
    }
}

public sealed record ClaudeIdentity(string AccountUuid, string? OrganizationUuid, string? RateLimitTier);

public sealed record ClaudeProfile(string ProfileKey, string? PlanLabel);

public static class ClaudeProfiles
{
    /// <summary>
    /// Stable local alias: plan plus a short hash of the account/organisation ids. Without a
    /// known account there is no profile at all, so two accounts can never share a history.
    /// </summary>
    public static ClaudeProfile? Resolve(ClaudeIdentity? identity, string? subscriptionType)
    {
        if (identity is null) return null;
        var (slug, label) = Plan(identity.RateLimitTier, subscriptionType);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.AccountUuid + "|" + identity.OrganizationUuid)))[..10].ToLowerInvariant();
        return new ClaudeProfile($"claude-{slug}-{hash}", label);
    }

    public static (string Slug, string? Label) Plan(string? tier, string? subscription)
    {
        var t = (tier ?? "").ToLowerInvariant();
        if (t.Contains("max_20x") || t.Contains("max20x")) return ("max20x", "Max (20x)");
        if (t.Contains("max_5x") || t.Contains("max5x")) return ("max5x", "Max (5x)");
        return (subscription ?? "").ToLowerInvariant() switch
        {
            "max" => ("max", "Max"),
            "pro" => ("pro", "Pro"),
            "team" => ("team", "Team"),
            "enterprise" => ("enterprise", "Enterprise"),
            "free" => ("free", "Free"),
            "" => ("unknown", null),
            var other => (new string(other.Where(char.IsLetterOrDigit).Take(20).ToArray()), null),
        };
    }
}
