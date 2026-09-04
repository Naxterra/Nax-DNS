using System.Collections.Concurrent;
using BetterDns.Core.Configuration;

namespace BetterDns.Core.Routing;

public sealed class UpstreamHealthTracker
{
    private readonly ConcurrentDictionary<string, MutableStatus> states = new(StringComparer.Ordinal);

    public bool CanTry(UpstreamDefinition upstream, FailoverChain chain, DateTimeOffset now)
    {
        var state = states.GetOrAdd(Key(upstream), static _ => new());
        lock (state)
        {
            return state.CircuitOpenUntil is null || state.CircuitOpenUntil <= now;
        }
    }

    public void RecordSuccess(UpstreamDefinition upstream, TimeSpan latency)
    {
        var state = states.GetOrAdd(Key(upstream), static _ => new());
        lock (state)
        {
            state.Failures = 0;
            state.CircuitOpenUntil = null;
            state.LastLatencyMilliseconds = latency.TotalMilliseconds;
            state.LastError = null;
        }
    }

    public void RecordFailure(UpstreamDefinition upstream, FailoverChain chain, Exception error, DateTimeOffset now)
    {
        var state = states.GetOrAdd(Key(upstream), static _ => new());
        lock (state)
        {
            state.Failures++;
            state.LastError = error.Message;
            if (state.Failures >= Math.Max(1, chain.FailureThreshold))
            {
                state.CircuitOpenUntil = now.AddSeconds(Math.Max(1, chain.CooldownSeconds));
            }
        }
    }

    public IReadOnlyList<UpstreamStatus> Snapshot(IEnumerable<UpstreamDefinition> upstreams)
    {
        var now = DateTimeOffset.UtcNow;
        return upstreams.Select(upstream =>
        {
            var state = states.GetOrAdd(Key(upstream), static _ => new());
            lock (state)
            {
                return new UpstreamStatus(
                    upstream.Id,
                    upstream.Name,
                    state.CircuitOpenUntil is null || state.CircuitOpenUntil <= now,
                    state.Failures,
                    state.CircuitOpenUntil,
                    state.LastLatencyMilliseconds,
                    state.LastError);
            }
        }).ToArray();
    }

    private static string Key(UpstreamDefinition upstream) => System.Text.Json.JsonSerializer.Serialize(upstream, JsonSettings.Wire);

    private sealed class MutableStatus
    {
        public int Failures { get; set; }
        public DateTimeOffset? CircuitOpenUntil { get; set; }
        public double? LastLatencyMilliseconds { get; set; }
        public string? LastError { get; set; }
    }
}
