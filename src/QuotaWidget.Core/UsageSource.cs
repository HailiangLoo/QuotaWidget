using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace QuotaWidget.Core;

public enum UsageFetchKind { Ok, NotLoggedIn, Unavailable, NotFresh, CliMissing, CliUntrusted, CliIncompatible, CliFailed, Timeout, BadOutput }

/// <summary>
/// One answer from the usage source. RateLimits is the plan-usage body as the official client
/// returned it; FetchedAt is when that client actually got it from the server.
/// The CLI's control channel does not expose HTTP status or Retry-After: a server-side 429
/// arrives here only as Unavailable (or NotFresh), so the scheduler can only back off locally.
/// </summary>
public sealed record UsageFetch(UsageFetchKind Kind, JsonElement? RateLimits = null, string? SubscriptionType = null, DateTimeOffset? FetchedAt = null);

public interface IUsageSource
{
    Task<UsageFetch> FetchAsync(string configDir, CancellationToken ct);
}

/// <summary>Local filesystem/signature check only. Must never make an authenticated or network usage request.</summary>
public interface ILocalUsageRecovery
{
    bool IsLocalDependencyReady();
}

/// <summary>
/// Asks the unmodified, Anthropic-signed Claude Code CLI for plan usage through its stream-json
/// control channel ("get_usage", the request it offers for usage meters). The CLI keeps and
/// refreshes its own login; this process never reads, stores or sends a token, and sends no
/// prompt, so no model inference happens.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClaudeCliUsageSource : IUsageSource, ILocalUsageRecovery
{
    const string RequestId = "quota-widget-usage";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    readonly Func<string?> _configuredExe;
    readonly string _workDir;
    readonly bool _diagnostics;
    readonly HashSet<string> _diagnosticClasses = new();
    public string[] DiagnosticClasses { get { lock (_diagnosticClasses) return _diagnosticClasses.ToArray(); } }
    public bool? LastRateLimitsAvailable { get; private set; }
    public double? LastSessionCost { get; private set; }
    public int? LastModelUsageCount { get; private set; }
    public double? LastFetchMilliseconds { get; private set; }
    public long? LastPeakWorkingSetBytes { get; private set; }

    public ClaudeCliUsageSource(Func<string?> configuredExe, string workDir, bool diagnostics = false)
    {
        _configuredExe = configuredExe;
        _workDir = workDir;
        _diagnostics = diagnostics;
    }

    /// <summary>The CLI chosen on the last fetch (shown in the settings panel).</summary>
    public CliChoice? LastChoice { get; private set; }
    public bool IsLocalDependencyReady() => ClaudeCli.Resolve(_configuredExe()).Usable;

    public async Task<UsageFetch> FetchAsync(string configDir, CancellationToken ct)
    {
        var choice = ClaudeCli.Resolve(_configuredExe());
        var fetchWatch = Stopwatch.StartNew();
        LastChoice = choice;
        switch (choice.Problem)
        {
            case "cli_missing": return new UsageFetch(UsageFetchKind.CliMissing);
            case "cli_untrusted": return new UsageFetch(UsageFetchKind.CliUntrusted);
            case "cli_incompatible": return new UsageFetch(UsageFetchKind.CliIncompatible);
        }
        var exe = choice.Path!;
        using var executable = Authenticode.OpenVerified(exe);
        if (executable is null) return new UsageFetch(UsageFetchKind.CliUntrusted);
        if (!ClaudeCli.EnsurePrivateDirectory(configDir)) return new UsageFetch(UsageFetchKind.CliFailed);
        Directory.CreateDirectory(_workDir);

        var before = ClaudeConfigFiles.CachedUsageFetchedAtMs(configDir);
        var started = DateTimeOffset.Now;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = _workDir,
        };
        foreach (var a in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose" }) psi.ArgumentList.Add(a);
        if (_diagnostics)
            foreach (var a in new[] { "--debug-to-stderr", "--debug-file", "NUL" }) psi.ArgumentList.Add(a);
        ClaudeCli.PrepareEnvironment(psi, configDir, allowUsage: true);

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception) { return new UsageFetch(UsageFetchKind.CliFailed); }
        // Exit must stop the credential-using child before running auth logout.
        using var cancelChild = ct.Register(() => Kill(proc));
        proc.ErrorDataReceived += (_, e) =>
        {
            // Fixed labels only. Auth output, URLs, request bodies and headers are never retained.
            if (_diagnostics && DiagnosticClass(e.Data) is { } label)
                lock (_diagnosticClasses) _diagnosticClasses.Add(label);
        };
        proc.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        string? answer = null;
        try
        {
            var request = "{\"type\":\"control_request\",\"request_id\":\"" + RequestId + "\",\"request\":{\"subtype\":\"get_usage\",\"skip_behaviors\":true}}\n";
            await proc.StandardInput.WriteAsync(request.AsMemory(), timeout.Token);
            await proc.StandardInput.FlushAsync(timeout.Token);
            while (await proc.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (IsOurResponse(line))
                {
                    answer = line;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(proc);
            return new UsageFetch(UsageFetchKind.Timeout);
        }
        catch (IOException)
        {
            // the CLI exited early; handled below as missing output
        }
        finally
        {
            try { proc.Refresh(); LastPeakWorkingSetBytes = proc.PeakWorkingSet64; } catch { }
            try { proc.StandardInput.Close(); } catch { }
            try
            {
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await proc.WaitForExitAsync(exit.Token);
            }
            catch { Kill(proc); }
            LastFetchMilliseconds = fetchWatch.Elapsed.TotalMilliseconds;
        }
        ct.ThrowIfCancellationRequested();
        if (answer is null) return new UsageFetch(UsageFetchKind.BadOutput);

        var parsed = ParseControlResponse(answer);
        try
        {
            using var doc = JsonDocument.Parse(answer);
            var body = doc.RootElement.GetProperty("response").GetProperty("response");
            if (body.TryGetProperty("rate_limits_available", out var available) && available.ValueKind is JsonValueKind.True or JsonValueKind.False)
                LastRateLimitsAvailable = available.GetBoolean();
            if (body.TryGetProperty("session", out var session))
            {
                if (session.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDouble(out var dollars)) LastSessionCost = dollars;
                if (session.TryGetProperty("model_usage", out var models) && models.ValueKind == JsonValueKind.Object) LastModelUsageCount = models.EnumerateObject().Count();
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
        if (parsed.Kind != UsageFetchKind.Ok) return parsed;
        var after = ClaudeConfigFiles.CachedUsageFetchedAtMs(configDir);
        var fetchedAt = FreshFetchTime(before, after, started);
        return fetchedAt is null ? parsed with { Kind = UsageFetchKind.NotFresh } : parsed with { FetchedAt = fetchedAt };
    }

    public static string? DiagnosticClass(string? line)
    {
        if (line is null) return null;
        if (line.Contains("fetchUtilization: GET /api/oauth/usage", StringComparison.Ordinal)) return "usage_requested";
        if (line.Contains("fetchUtilization: 200", StringComparison.Ordinal)) return "usage_http_200";
        foreach (var status in new[] { "401", "403", "429", "500", "502", "503" })
            if (line.Contains("status code " + status, StringComparison.Ordinal) || line.Contains("fetchUtilization: " + status + " remembered", StringComparison.Ordinal)) return "http_" + status;
        if (line.Contains("Auth error: no-auth", StringComparison.Ordinal)) return "auth_context_missing";
        if (line.Contains("Auth error: essential-traffic-only", StringComparison.Ordinal)) return "usage_blocked_locally";
        if (line.Contains("Usage fetch returned a fieldless", StringComparison.Ordinal)) return "usage_invalid_body";
        if (line.Contains("ECONN", StringComparison.Ordinal) || line.Contains("ETIMEDOUT", StringComparison.Ordinal)) return "network_error";
        return null;
    }

    static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    static bool Is(JsonElement o, string name, JsonValueKind kind, out JsonElement value) =>
        o.TryGetProperty(name, out value) && value.ValueKind == kind;

    static bool IsString(JsonElement o, string name, string expected) =>
        Is(o, name, JsonValueKind.String, out var v) && v.GetString() == expected;

    /// <summary>
    /// Maps the CLI's control_response line; pure, so it can be tested without a CLI. Every level
    /// is type-checked: a changed shape becomes BadOutput (shown as "数据格式变化"), never an exception.
    /// </summary>
    public static UsageFetch ParseControlResponse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !IsString(root, "type", "control_response") ||
                !Is(root, "response", JsonValueKind.Object, out var r) || !IsString(r, "request_id", RequestId) ||
                !Is(r, "subtype", JsonValueKind.String, out var st))
                return new UsageFetch(UsageFetchKind.BadOutput);
            if (st.GetString() != "success") return new UsageFetch(UsageFetchKind.CliFailed);
            if (!Is(r, "response", JsonValueKind.Object, out var body)) return new UsageFetch(UsageFetchKind.BadOutput);

            bool available;
            if (Is(body, "rate_limits_available", JsonValueKind.True, out _)) available = true;
            else if (Is(body, "rate_limits_available", JsonValueKind.False, out _)) available = false;
            else return new UsageFetch(UsageFetchKind.BadOutput);

            string? subscription = null;
            if (body.TryGetProperty("subscription_type", out var sub))
            {
                if (sub.ValueKind == JsonValueKind.String) subscription = sub.GetString();
                else if (sub.ValueKind != JsonValueKind.Null) return new UsageFetch(UsageFetchKind.BadOutput);
            }
            if (!available) return new UsageFetch(subscription is null ? UsageFetchKind.NotLoggedIn : UsageFetchKind.Unavailable, null, subscription);
            if (!body.TryGetProperty("rate_limits", out var rl) || rl.ValueKind == JsonValueKind.Null)
                return new UsageFetch(UsageFetchKind.Unavailable, null, subscription);
            if (rl.ValueKind != JsonValueKind.Object) return new UsageFetch(UsageFetchKind.BadOutput);
            return new UsageFetch(UsageFetchKind.Ok, rl.Clone(), subscription);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            return new UsageFetch(UsageFetchKind.BadOutput);
        }
    }

    /// <summary>Cheap filter for the read loop: is this line the answer to our request?</summary>
    static bool IsOurResponse(string line) =>
        line.Contains(RequestId, StringComparison.Ordinal) && line.Contains("control_response", StringComparison.Ordinal);

    /// <summary>
    /// The CLI stamps its cached usage body when it really asked the server. If the stamp did
    /// not move during this run, the answer came from a cache or a fallback: not a new observation.
    /// </summary>
    public static DateTimeOffset? FreshFetchTime(long? beforeMs, long? afterMs, DateTimeOffset started)
    {
        if (afterMs is not { } after) return null;
        if (beforeMs is { } b && after <= b) return null;
        if (after < 0 || after > 253_402_300_799_999) return null; // outside DateTimeOffset's range
        var t = DateTimeOffset.FromUnixTimeMilliseconds(after).ToLocalTime();
        if (t < started - TimeSpan.FromSeconds(5) || t > DateTimeOffset.Now + TimeSpan.FromMinutes(2)) return null;
        return t;
    }
}
