using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaWidget.Core;

public interface ICodexUsageSource
{
    Task<LatestEnvelope> FetchAsync(CancellationToken ct);
}

/// <summary>Only the official process handles the existing ChatGPT login. No credential files are read here.</summary>
public sealed class CodexUsageSource(string workDir) : ICodexUsageSource
{
    public const string SourceId = "codex-usage";
    public const string Signer = "OpenAI OpCo, LLC";
    public bool? LastCreditDetailsPresent { get; private set; }

    public static string? Resolve()
    {
        if (!OperatingSystem.IsWindows()) return null;
        // Desktop-managed binaries track the app protocol; the npm wrapper may be much older.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "OpenAI", "Codex", "bin");
        if (!Directory.Exists(root)) return null;
        foreach (var path in Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
            if (Authenticode.IsSignedBy(path, Signer)) return path;
        return null;
    }

    public static LatestEnvelope Failure(DateTimeOffset at, string code, string status = Statuses.Error) =>
        new(1, SourceId, "unknown", null, at, status, 300, null, code, null);

    public static LatestEnvelope Parse(JsonElement result, DateTimeOffset at)
    {
        try { return ParseResult(result, at); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or OverflowException)
        { return Failure(at, "bad_output"); }
    }

    static LatestEnvelope ParseResult(JsonElement result, DateTimeOffset at)
    {
        if (result.ValueKind != JsonValueKind.Object) return Failure(at, "bad_output");
        if (!result.TryGetProperty("accountId", out var identity) || identity.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(identity.GetString()))
            return Failure(at, "identity_unknown");
        JsonElement bucket;
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
        {
            // Other buckets (e.g. base_model_inference) have different allowances.
            if (!buckets.TryGetProperty("codex", out bucket)) return Failure(at, "no_codex_bucket");
        }
        else if (!result.TryGetProperty("rateLimits", out bucket) ||
                 !bucket.TryGetProperty("limitId", out var id) || id.GetString() != "codex")
            return Failure(at, "no_codex_bucket");
        if (bucket.ValueKind != JsonValueKind.Object) return Failure(at, "bad_output");
        QuotaLimit? ReadWindow(int wanted,string label,out string? error)
        {
            error=null;QuotaLimit? limit=null;var seen=false;
            foreach(var name in new[]{"primary","secondary"})
            {
                if(!bucket.TryGetProperty(name,out var window)||window.ValueKind!=JsonValueKind.Object||
                    !window.TryGetProperty("windowDurationMins",out var duration)||duration.ValueKind!=JsonValueKind.Number||!duration.TryGetInt32(out var minutes)||minutes!=wanted)continue;
                if(seen){error="ambiguous_"+label+"_window";return null;}seen=true;
                if(!window.TryGetProperty("usedPercent",out var used)||used.ValueKind!=JsonValueKind.Number||!used.TryGetDouble(out var pct)||!double.IsFinite(pct)||pct<0||pct>100||
                    !window.TryGetProperty("resetsAt",out var reset)||reset.ValueKind!=JsonValueKind.Number||!reset.TryGetInt64(out var seconds)) {error="invalid_"+label+"_window";continue;}
                try {limit=UsageParser.Limit(pct,DateTimeOffset.FromUnixTimeSeconds(seconds));}
                catch(ArgumentOutOfRangeException){error="invalid_"+label+"_window";}
            }
            return error is null?limit:null;
        }
        // Slot order is not a contract: match the actual duration, never the plan name.
        var week=ReadWindow(10080,"weekly",out var weekError);
        var five=ReadWindow(300,"five_hour",out var fiveError);
        if(week is null&&five is null)return Failure(at,weekError??fiveError??"no_supported_window");
        var plan = bucket.TryGetProperty("planType", out var planValue) && planValue.ValueKind == JsonValueKind.String ? planValue.GetString() : "unknown";
        // No email/account ID is persisted. Account + plan changes get separate histories.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.GetString() + "|" + plan))).ToLowerInvariant()[..24];
        return new(1, SourceId, "codex-" + hash, "Codex " + plan, at, Statuses.Partial, 300, null, weekError??fiveError,
            new QuotaSnapshot("cx-" + at.ToUnixTimeMilliseconds(), at, new QuotaLimits(five, week, null)));
    }

    public async Task<LatestEnvelope> FetchAsync(CancellationToken ct)
    {
        var exe = Resolve();
        if (exe is null) return Failure(DateTimeOffset.Now, "cli_missing_or_untrusted");
        using var executable = Authenticode.OpenVerified(exe, Signer);
        if (executable is null) return Failure(DateTimeOffset.Now, "cli_missing_or_untrusted");
        Directory.CreateDirectory(workDir);
        var home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workDir };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("OPENAI", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase)).ToArray()) psi.Environment.Remove(key);
        psi.Environment["CODEX_HOME"] = home;
        foreach (var arg in new[] { "app-server", "--listen", "stdio://", "-c", "analytics.enabled=false" }) psi.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            // Drain and discard, never log raw CLI output (could contain private diagnostics).
            _ = Task.Run(async () => { try { var buf = new char[4096]; while (await process.StandardError.ReadAsync(buf, timeout.Token) > 0) { } } catch { } });
            using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            async Task<JsonElement> Request(int id, string method, object? parameters = null)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
                for (var messages = 0; messages < 128; messages++)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    if (line is null || line.Length > 1024 * 1024) throw new InvalidDataException();
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var value) || value != id) continue;
                    if (root.TryGetProperty("error", out _)) throw new InvalidDataException();
                    return root.GetProperty("result").Clone();
                }
                throw new InvalidDataException();
            }
            await Request(1, "initialize", new { clientInfo = new { name = "quota_widget", title = "Quota Widget", version = "0.4.0" }, capabilities = new { experimentalApi = true } });
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}".AsMemory(), timeout.Token);
            var account = await Request(2, "account/read", new { refreshToken = false });
            if (!account.TryGetProperty("account", out var a) || a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("type", out var type) || type.GetString() != "chatgpt")
                return Failure(DateTimeOffset.Now, "not_logged_in", Statuses.AuthRequired);
            // Verified in the bundled 0.159.2 protocol: omit the separate reset-credit detail
            // lookup. The widget consumes no reset credits and reads quota windows only.
            var limits = await Request(3, "account/rateLimits/read", new { excludeResetCreditDetails = true });
            LastCreditDetailsPresent = limits.TryGetProperty("rateLimitResetCredits", out var credits) && credits.ValueKind == JsonValueKind.Object &&
                credits.TryGetProperty("credits", out var details) && details.ValueKind == JsonValueKind.Array;
            // This API has no server fetchedAt; observedAt is the completed query time.
            return Parse(limits, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure(DateTimeOffset.Now, "timeout"); }
        catch { return Failure(DateTimeOffset.Now, "cli_failed"); }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
            try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
    }
}

/// <summary>Independent provider cadence and persisted backoff. Never signs in or signs out.</summary>
public sealed class CodexUsageCollector : IDisposable
{
    readonly DataPaths _paths;
    readonly Func<WidgetSettings> _settings;
    readonly ICodexUsageSource _source;
    readonly Func<DateTimeOffset> _clock;
    readonly SemaphoreSlim _gate = new(1, 1);
    int _failures;
    LatestEnvelope? _last;
    public DateTimeOffset NotBefore { get; private set; }
    public CodexUsageCollector(DataPaths paths, Func<WidgetSettings> settings, ICodexUsageSource source, Func<DateTimeOffset>? clock = null)
    {
        _paths = paths; _settings = settings; _source = source; _clock = clock ?? (() => DateTimeOffset.Now);
        if (AtomicFile.TryReadAllText(paths.Latest) is { } text) _last = SnapshotJson.ParseEnvelope(text, []);
        if (_last is { } last && last.SourceId == CodexUsageSource.SourceId && last.AttemptedAt <= _clock())
        {
            NotBefore = last.AttemptedAt.AddSeconds(Math.Max(last.EffectivePollIntervalSeconds, last.RetryAfterSeconds ?? 0));
            _failures = Statuses.HasSnapshot(last.Status) ? 0 : 1;
        }
    }
    public async Task<LatestEnvelope?> CollectOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_settings().Monitors(ChatPlatform.Codex) || _clock() < NotBefore) return null;
            LatestEnvelope result;
            try { result = await _source.FetchAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { result = CodexUsageSource.Failure(_clock(), "source_failed"); }
            var success = Statuses.HasSnapshot(result.Status);
            _failures = success ? 0 : _failures + 1;
            var poll = Math.Max(60, _settings().PollIntervalSeconds);
            var delay = success ? poll : Math.Max(poll, (int)Math.Min(3600, poll * Math.Pow(2, Math.Min(6, _failures - 1))));
            delay = Math.Max(delay, result.RetryAfterSeconds ?? 0);
            if (!success && _last is { } old) result = result with { ProfileKey = old.ProfileKey, PlanLabel = old.PlanLabel };
            _last = result with { EffectivePollIntervalSeconds = delay };
            NotBefore = result.AttemptedAt.AddSeconds(delay);
            AtomicFile.WriteAllText(_paths.Latest, SnapshotJson.WriteEnvelope(_last));
            return _last;
        }
        finally { _gate.Release(); }
    }
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { if (_settings().CollectorEnabled) await CollectOnceAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public void Dispose() => _gate.Dispose();
}
