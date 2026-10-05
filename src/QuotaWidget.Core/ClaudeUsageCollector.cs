using System.Globalization;
using System.Text.Json;

namespace QuotaWidget.Core;

/// <summary>
/// Polls plan usage through an <see cref="IUsageSource"/> (the official CLI) and writes
/// latest.json atomically. One attempt at a time; failures back off; nothing is attempted
/// before the earliest allowed time, whether the trigger is the timer, a click or a restart.
/// Verified missing/incompatible local CLI repairs can clear that local-only wait;
/// server/network waits and explicit retry floors cannot be cleared by dependency recovery.
/// </summary>
public enum CollectTrigger { Timer, Manual }

public sealed class ClaudeUsageCollector : IDisposable
{
    public const string SourceId = "claude-code-usage";
    static readonly TimeSpan MinManualSpacing = TimeSpan.FromSeconds(60);
    static readonly TimeSpan MaxErrorBackoff = TimeSpan.FromHours(1);

    readonly DataPaths _paths;
    readonly Func<WidgetSettings> _settings;
    readonly IUsageSource _source;
    readonly SimpleLog _log;
    readonly Func<DateTimeOffset> _clock;
    readonly SemaphoreSlim _gate = new(1, 1);

    DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    DateTimeOffset _notBefore = DateTimeOffset.MinValue;
    DateTimeOffset _lastObserved = DateTimeOffset.MinValue;
    string? _lastStatus;
    string? _lastError;
    int? _serverRetryAfter;
    DateTimeOffset _nextLocalCheck;
    int _failStreak;
    (DateTime, long)? _credStampAtFailure;
    volatile bool _wake;
    string _profileKey = "unknown";
    string? _planLabel;

    public ClaudeUsageCollector(DataPaths paths, Func<WidgetSettings> settings, IUsageSource source, Func<DateTimeOffset>? clock = null)
    {
        _paths = paths;
        _settings = settings;
        _source = source;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _log = new SimpleLog(Path.Combine(paths.LogsDir, "collector.log"));
        RestoreFromLatest();
    }

    public string? LastStatus => _lastStatus;
    public DateTimeOffset NotBefore => _notBefore;

    string ConfigDir => _settings().ResolveClaudeConfigDir(_paths);
    TimeSpan Poll => TimeSpan.FromSeconds(Math.Max(60, _settings().PollIntervalSeconds));

    /// <summary>A restart keeps the previous wait; local dependency recovery is checked separately.</summary>
    void RestoreFromLatest()
    {
        var text = AtomicFile.TryReadAllText(_paths.Latest);
        if (text is null) return;
        var env = SnapshotJson.ParseEnvelope(text, new List<string>());
        if (env is null || env.SourceId != SourceId || env.AttemptedAt > _clock()) return;
        _profileKey = env.ProfileKey;
        _planLabel = env.PlanLabel;
        _lastAttempt = env.AttemptedAt;
        if (env.Snapshot is not null) _lastObserved = env.Snapshot.ObservedAt;
        if (env.Status == Statuses.AuthRequired) return; // re-check the login right away
        _lastStatus = env.Status;
        _lastError = env.ErrorCode;
        _serverRetryAfter = env.RetryAfterSeconds;
        if (!Statuses.HasSnapshot(env.Status)) _failStreak = 1;
        var wait = TimeSpan.FromSeconds(Math.Max(env.EffectivePollIntervalSeconds, env.RetryAfterSeconds ?? 0));
        _notBefore = env.AttemptedAt + wait;
    }

    TimeSpan DelayAfter(string status, int? retryAfterSeconds)
    {
        var poll = Poll;
        if (Statuses.HasSnapshot(status) || status == Statuses.AuthRequired) return poll;
        // Local back-off doubles and is capped; a server's Retry-After is a floor and never shortened.
        var local = TimeSpan.FromSeconds(Math.Min(MaxErrorBackoff.TotalSeconds, poll.TotalSeconds * Math.Pow(2, Math.Max(0, _failStreak - 1))));
        if (local < poll) local = poll;
        var server = TimeSpan.FromSeconds(retryAfterSeconds ?? 0);
        return server > local ? server : local;
    }

    /// <summary>Asks for an attempt now; refused inside a back-off window or within 60 s of the last attempt.</summary>
    public bool TriggerNow()
    {
        var now = _clock();
        var backingOff = _lastStatus is not null && !Statuses.HasSnapshot(_lastStatus) && _lastStatus != Statuses.AuthRequired && now < _notBefore;
        if (backingOff || now - _lastAttempt < MinManualSpacing) return false;
        _wake = true;
        return true;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_settings().CollectorEnabled || !_settings().Monitors(ChatPlatform.Claude))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                var now = _clock();
                bool go;
                if (_lastStatus == Statuses.AuthRequired)
                    go = _wake || !Equals(ClaudeConfigFiles.CredentialStamp(ConfigDir), _credStampAtFailure);
                else if (_lastStatus is not null && Statuses.HasSnapshot(_lastStatus))
                    go = now >= _lastAttempt + Poll || _wake; // the interval setting may change at any time
                else
                    go = now >= _notBefore || CanCheckLocalRepair(now);
                if (go)
                {
                    var trigger = _wake ? CollectTrigger.Manual : CollectTrigger.Timer;
                    _wake = false;
                    if (await CollectOnceAsync(ct, trigger) is not null) continue;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _log.Write("loop error " + e.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    bool InBackoff(DateTimeOffset now) =>
        _lastStatus is not null && !Statuses.HasSnapshot(_lastStatus) && _lastStatus != Statuses.AuthRequired && now < _notBefore;

    bool CanCheckLocalRepair(DateTimeOffset now) => _source is ILocalUsageRecovery && _lastStatus == Statuses.Error &&
        _lastError is ("cli_missing" or "cli_incompatible") && _serverRetryAfter is null &&
        now >= _nextLocalCheck && now - _lastAttempt >= MinManualSpacing;

    // Only the two failures that occur before launching the CLI can recover this way.
    // A server/network/auth failure, an explicit wait floor, or an untrusted CLI cannot.
    bool TryLocalRepair(DateTimeOffset now)
    {
        if (!CanCheckLocalRepair(now)) return false;
        _nextLocalCheck = now + Poll;
        try
        {
            if (!((ILocalUsageRecovery)_source).IsLocalDependencyReady()) return false;
            _notBefore = now;
            _failStreak = 0;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The only path that runs the source. Every caller — timer, click, --collect-once — is
    /// checked here, under the lock: inside a back-off window nothing runs and nothing is
    /// written (the stored deadline stays as it is), and manual calls keep 60 s apart.
    /// Returns null when the attempt was refused.
    /// </summary>
    public async Task<LatestEnvelope?> CollectOnceAsync(CancellationToken ct, CollectTrigger trigger = CollectTrigger.Manual)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_settings().Monitors(ChatPlatform.Claude)) return null;
            var now = _clock();
            if (InBackoff(now) && !TryLocalRepair(now)) return null;
            if (trigger == CollectTrigger.Manual && now - _lastAttempt < MinManualSpacing) return null;
            var result = await AttemptAsync(ct);
            _lastAttempt = result.AttemptedAt;
            _lastStatus = result.Status;
            _lastError = result.ErrorCode;
            _serverRetryAfter = result.RetryAfterSeconds;
            if (Statuses.HasSnapshot(result.Status))
            {
                _failStreak = 0;
                _lastObserved = result.Snapshot!.ObservedAt;
            }
            else if (result.Status == Statuses.AuthRequired) _credStampAtFailure = ClaudeConfigFiles.CredentialStamp(ConfigDir);
            else _failStreak++;
            var delay = DelayAfter(result.Status, result.RetryAfterSeconds);
            _notBefore = result.AttemptedAt + delay;
            var env = result with { EffectivePollIntervalSeconds = (int)Math.Max(60, Math.Min(int.MaxValue, Math.Round(delay.TotalSeconds))) };
            AtomicFile.WriteAllText(_paths.Latest, SnapshotJson.WriteEnvelope(env));
            _log.Write($"{env.Status}{(env.ErrorCode is null ? "" : " " + env.ErrorCode)} next={env.EffectivePollIntervalSeconds}s");
            return env;
        }
        finally
        {
            _gate.Release();
        }
    }

    LatestEnvelope Fail(DateTimeOffset at, string status, string code) =>
        new(SnapshotJson.SchemaVersion, SourceId, _profileKey, _planLabel, at, status, 60, null, code, null);

    async Task<LatestEnvelope> AttemptAsync(CancellationToken ct)
    {
        var s = _settings();
        var configDir = ConfigDir;
        var attemptedAt = _clock();
        if (ClaudeConfigFiles.CredentialStamp(configDir) is null) return Fail(attemptedAt, Statuses.AuthRequired, "not_logged_in");

        var fetch = await _source.FetchAsync(configDir, ct);
        switch (fetch.Kind)
        {
            case UsageFetchKind.NotLoggedIn: return Fail(attemptedAt, Statuses.AuthRequired, "not_logged_in");
            case UsageFetchKind.CliMissing: return Fail(attemptedAt, Statuses.Error, "cli_missing");
            case UsageFetchKind.CliUntrusted: return Fail(attemptedAt, Statuses.Error, "cli_untrusted");
            case UsageFetchKind.CliIncompatible: return Fail(attemptedAt, Statuses.Error, "cli_incompatible");
            case UsageFetchKind.Timeout: return Fail(attemptedAt, Statuses.Error, "timeout");
            case UsageFetchKind.Unavailable: return Fail(attemptedAt, Statuses.Error, "unavailable");
            case UsageFetchKind.NotFresh: return Fail(attemptedAt, Statuses.Error, "not_fresh");
            case UsageFetchKind.BadOutput: return Fail(attemptedAt, Statuses.Error, "bad_output");
            case UsageFetchKind.CliFailed: return Fail(attemptedAt, Statuses.Error, "cli_failed");
        }

        // Identity first: without a known account nothing may enter a shared history.
        var profile = ClaudeProfiles.Resolve(ClaudeConfigFiles.ReadIdentity(configDir), fetch.SubscriptionType);
        if (profile is null) return Fail(attemptedAt, Statuses.AuthRequired, "identity_unknown");
        _profileKey = profile.ProfileKey;
        _planLabel = profile.PlanLabel;

        var observedAt = fetch.FetchedAt!.Value;
        if (observedAt <= _lastObserved) return Fail(attemptedAt, Statuses.Error, "not_fresh");
        var parsed = UsageParser.Parse(fetch.RateLimits!.Value, s.FableModelName);
        var limits = parsed.Limits;
        var count = (limits.FiveHour is null ? 0 : 1) + (limits.AllWeek is null ? 0 : 1) + (limits.FableWeek is null ? 0 : 1);
        if (count == 0) return Fail(attemptedAt, Statuses.Error, parsed.OutOfRange ? "value_out_of_range" : "no_known_limits");
        var id = "cc-" + observedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss.fff'Z'", CultureInfo.InvariantCulture);
        return new LatestEnvelope(SnapshotJson.SchemaVersion, SourceId, _profileKey, _planLabel, attemptedAt,
            count == 3 ? Statuses.Ok : Statuses.Partial, 60, null, parsed.OutOfRange ? "value_out_of_range" : null,
            new QuotaSnapshot(id, observedAt, limits));
    }

    public void Dispose() => _gate.Dispose();
}
