using System.Collections.Concurrent;
using BetterDns.Core.Configuration;

namespace BetterDns.Core.Routing;

public sealed class UpstreamHealthTracker
{
    private readonly ConcurrentDictionary<string, MutableStatus> states = new(StringComparer.Ordinal);

    public sealed record Attempt(string Key, long Generation, bool Recovery);

    public Attempt? TryBeginAttempt(UpstreamDefinition upstream, DateTimeOffset now)
    {
        var key = Key(upstream);
        var state = states.GetOrAdd(key, static _ => new());
        lock (state)
        {
            if (state.CircuitOpenUntil is { } until)
            {
                if (until > now || state.RecoveryInProgress) return null;
                state.RecoveryInProgress = true;
                return new(key, state.Generation, true);
            }
            return new(key, state.Generation, false);
        }
    }

    public bool CanTry(UpstreamDefinition upstream, FailoverChain chain, DateTimeOffset now)
    {
        var state = states.GetOrAdd(Key(upstream), static _ => new());
        lock (state) return !state.RecoveryInProgress && (state.CircuitOpenUntil is null || state.CircuitOpenUntil <= now);
    }

    public void Succeed(Attempt attempt, TimeSpan latency, DateTimeOffset now, string response)
    {
        var state = states[attempt.Key];
        lock (state)
        {
            if (state.Generation != attempt.Generation) return;
            state.Failures = 0;
            state.CircuitOpenUntil = null;
            state.RecoveryInProgress = false;
            state.LastLatencyMilliseconds = latency.TotalMilliseconds;
            state.LastChecked = now;
            state.FailureCode = null;
            state.LastDnsResponse = response;
            if (attempt.Recovery) state.Generation++;
        }
    }

    public void Fail(Attempt attempt, FailoverChain chain, string code, DateTimeOffset now)
    {
        var state = states[attempt.Key];
        lock (state)
        {
            if (state.Generation != attempt.Generation) return;
            state.Failures++;
            state.LastLatencyMilliseconds = null;
            state.LastChecked = now;
            state.FailureCode = code;
            state.LastDnsResponse = null;
            if (attempt.Recovery || state.Failures >= Math.Max(1, chain.FailureThreshold))
            {
                state.CircuitOpenUntil = now.AddSeconds(Math.Max(1, chain.CooldownSeconds));
                state.RecoveryInProgress = false;
                state.Generation++; // Completions from pre-failure requests cannot end this cooldown.
            }
        }
    }

    public void Cancel(Attempt attempt)
    {
        var state = states[attempt.Key];
        lock (state)
            if (state.Generation == attempt.Generation && attempt.Recovery) state.RecoveryInProgress = false;
    }

    // Convenience methods for callers without concurrent routing leases.
    public void RecordSuccess(UpstreamDefinition upstream, TimeSpan latency)
    {
        if (TryBeginAttempt(upstream, DateTimeOffset.UtcNow) is { } attempt)
            Succeed(attempt, latency, DateTimeOffset.UtcNow, "NOERROR");
    }

    public void RecordFailure(UpstreamDefinition upstream, FailoverChain chain, Exception error, DateTimeOffset now)
    {
        if (TryBeginAttempt(upstream, now) is { } attempt) Fail(attempt, chain, ResolverFailure.Classify(error), now);
    }

    public IReadOnlyList<UpstreamStatus> Snapshot(IEnumerable<UpstreamDefinition> upstreams)
    {
        return upstreams.Select(upstream =>
        {
            var state = states.GetOrAdd(Key(upstream), static _ => new());
            lock (state)
                return new UpstreamStatus(upstream.Id, upstream.Name,
                    state.CircuitOpenUntil is null && !state.RecoveryInProgress,
                    state.Failures, state.CircuitOpenUntil, state.LastLatencyMilliseconds, state.FailureCode,
                    state.LastChecked, state.FailureCode, state.RecoveryInProgress, state.LastDnsResponse);
        }).ToArray();
    }

    public static string Key(UpstreamDefinition upstream) => System.Text.Json.JsonSerializer.Serialize(upstream, JsonSettings.Wire);

    private sealed class MutableStatus
    {
        public long Generation;
        public int Failures;
        public DateTimeOffset? CircuitOpenUntil;
        public bool RecoveryInProgress;
        public double? LastLatencyMilliseconds;
        public DateTimeOffset? LastChecked;
        public string? FailureCode;
        public string? LastDnsResponse;
    }
}
