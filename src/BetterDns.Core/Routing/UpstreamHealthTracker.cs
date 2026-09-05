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

    public void Succeed(Attempt attempt, TimeSpan latency, DateTimeOffset now, string response, string source = "traffic")
    {
        var state = states[attempt.Key];
        lock (state)
        {
            if (state.Generation != attempt.Generation) return;
            state.Failures = 0;
            state.FailureStartedAt = null;
            state.CircuitOpenUntil = null;
            state.RecoveryInProgress = false;
            state.LastLatencyMilliseconds = latency.TotalMilliseconds;
            state.LastChecked = now;
            state.FailureCode = null;
            state.LastDnsResponse = response;
            state.MeasurementSource = source;
            if (attempt.Recovery) state.Generation++;
        }
    }

    public bool Fail(Attempt attempt, FailoverChain chain, string code, DateTimeOffset now, string source = "traffic")
    {
        var state = states[attempt.Key];
        lock (state)
        {
            if (state.Generation != attempt.Generation) return state.CircuitOpenUntil is not null;
            // A long observation gap (sleep, idle service, etc.) is not proof of a continuous outage.
            if (!attempt.Recovery && (state.FailureStartedAt is null ||
                state.LastChecked is { } last && now - last > TimeSpan.FromSeconds(45)))
            {
                state.FailureStartedAt = now;
                state.Failures = 0;
            }
            state.Failures++;
            state.LastLatencyMilliseconds = null;
            state.LastChecked = now;
            state.FailureCode = code;
            state.LastDnsResponse = null;
            state.MeasurementSource = source;
            if (attempt.Recovery || (state.Failures >= Math.Max(1, chain.FailureThreshold) &&
                now - state.FailureStartedAt!.Value >= TimeSpan.FromSeconds(Math.Max(0, chain.FailoverAfterSeconds))))
            {
                state.CircuitOpenUntil = now.AddSeconds(Math.Max(1, chain.CooldownSeconds));
                state.RecoveryInProgress = false;
                state.Generation++; // Completions from pre-failure requests cannot end this cooldown.
            }
            return state.CircuitOpenUntil is not null;
        }
    }

    public bool NeedsConfirmation(UpstreamDefinition upstream, DateTimeOffset now)
    {
        if (!states.TryGetValue(Key(upstream), out var state)) return false;
        lock (state)
        {
            if (state.RecoveryInProgress || state.FailureStartedAt is null) return false;
            if (state.CircuitOpenUntil is { } until) return until <= now;
            return state.LastChecked is { } last && now - last >= TimeSpan.FromSeconds(10);
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
                    state.LastChecked, state.FailureCode, state.RecoveryInProgress, state.LastDnsResponse,
                    state.FailureStartedAt, state.MeasurementSource);
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
        public DateTimeOffset? FailureStartedAt;
        public string MeasurementSource = "traffic";
    }
}
