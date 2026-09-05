using System.Diagnostics;
using System.Collections.Concurrent;
using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Transports;

namespace BetterDns.Core.Routing;

public sealed class DnsRouter : IDisposable
{
    private readonly DomainRuleMatcher matcher = new();
    private readonly UpstreamHealthTracker health;
    private readonly QueryLog queryLog;
    private readonly IReadOnlyDictionary<DnsProtocol, IDnsTransport> transports;
    private readonly ConcurrentDictionary<string, ResolverProbeResult> probes = new(StringComparer.Ordinal);

    public IReadOnlyCollection<DnsProtocol> SupportedProtocols => transports.Keys.ToArray();

    public DnsRouter(UpstreamHealthTracker health, QueryLog queryLog)
        : this(
            health,
            queryLog,
            [
                new DohTransport(DnsProtocol.Doh),
                new DohTransport(DnsProtocol.Doh3),
                new DotTransport(),
                new DoqTransport()
            ])
    {
    }

    public DnsRouter(
        UpstreamHealthTracker health,
        QueryLog queryLog,
        IEnumerable<IDnsTransport> availableTransports)
    {
        this.health = health;
        this.queryLog = queryLog;
        transports = availableTransports.ToDictionary(static value => value.Protocol);
    }

    public async Task<byte[]> ResolveAsync(
        ReadOnlyMemory<byte> query,
        BetterDnsConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string domain;
        try
        {
            domain = DnsWire.ReadFirstQuestion(query.Span).Name;
        }
        catch (InvalidDataException)
        {
            return DnsWire.CreateErrorResponse(query.Span, 1);
        }

        var bootstrap = FindBootstrapAnswer(domain, query.Span, configuration.Upstreams);
        if (bootstrap is not null)
        {
            queryLog.Add(new(DateTimeOffset.UtcNow, domain, "Bootstrap", "Local bootstrap", "NOERROR", stopwatch.Elapsed.TotalMilliseconds));
            return bootstrap;
        }

        var rule = matcher.Match(domain, configuration.Rules);
        if (rule?.Action == RuleAction.Block)
        {
            queryLog.Add(new(DateTimeOffset.UtcNow, domain, rule.Name, null, "BLOCKED", stopwatch.Elapsed.TotalMilliseconds));
            return DnsWire.CreateErrorResponse(query.Span, 5);
        }

        var chainId = rule is { Action: RuleAction.Route, ChainId: not null }
            ? rule.ChainId
            : configuration.DefaultChainId;
        var chain = configuration.Chains.FirstOrDefault(value => value.Id == chainId);
        if (chain is null)
        {
            return DnsWire.CreateErrorResponse(query.Span, 2);
        }

        var upstreams = chain.UpstreamIds
            .Select(id => configuration.Upstreams.FirstOrDefault(value => value.Id == id))
            .Where(static value => value is { Enabled: true })
            .Cast<UpstreamDefinition>()
            .ToArray();

        var attempts = new List<ResolverAttempt>();
        foreach (var upstream in upstreams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = health.TryBeginAttempt(upstream, DateTimeOffset.UtcNow);
            if (lease is null)
            {
                attempts.Add(new(upstream.Id, upstream.Name, upstream.Protocol, DateTimeOffset.UtcNow, null, "cooldown", null));
                continue;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(upstream.TimeoutMilliseconds, 250, 30_000)));
            var attempt = Stopwatch.StartNew();
            try
            {
                var response = await transports[upstream.Protocol]
                    .QueryAsync(upstream, query, timeout.Token)
                    .ConfigureAwait(false);
                if (!DnsWire.MatchesResponseQuestion(query.Span, response))
                {
                    throw new InvalidDataException($"Resolver returned {DnsWire.ResponseCodeName(response)}.");
                }

                var responseCode = DnsWire.ResponseCodeName(response);
                health.Succeed(lease, attempt.Elapsed, DateTimeOffset.UtcNow, responseCode);
                attempts.Add(new(upstream.Id, upstream.Name, upstream.Protocol, DateTimeOffset.UtcNow, attempt.Elapsed.TotalMilliseconds, null, responseCode));
                queryLog.Add(new(
                    DateTimeOffset.UtcNow,
                    domain,
                    rule?.Name,
                    upstream.Name,
                    DnsWire.ResponseCodeName(response),
                    stopwatch.Elapsed.TotalMilliseconds,
                    upstream.Id,
                    chain.Id,
                    upstream.Protocol,
                    attempts));
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                health.Cancel(lease);
                throw;
            }
            catch (Exception error)
            {
                if (cancellationToken.IsCancellationRequested) { health.Cancel(lease); cancellationToken.ThrowIfCancellationRequested(); }
                var code = ResolverFailure.Classify(error);
                health.Fail(lease, chain, code, DateTimeOffset.UtcNow);
                attempts.Add(new(upstream.Id, upstream.Name, upstream.Protocol, DateTimeOffset.UtcNow, attempt.Elapsed.TotalMilliseconds, code, null));
            }
        }

        queryLog.Add(new(
            DateTimeOffset.UtcNow,
            domain,
            rule?.Name,
            null,
            attempts.Count == 0 ? "NO UPSTREAM" : "FAILOVER EXHAUSTED",
            stopwatch.Elapsed.TotalMilliseconds,
            ChainId: chain.Id,
            Attempts: attempts));
        return DnsWire.CreateErrorResponse(query.Span, 2);
    }

    public void Dispose()
    {
        foreach (var disposable in transports.Values.OfType<IDisposable>())
        {
            disposable.Dispose();
        }
    }

    public IReadOnlyList<ResolverProbeResult> ProbeSnapshot(IEnumerable<UpstreamDefinition> upstreams) => upstreams
        .Select(upstream => probes.GetValueOrDefault(UpstreamHealthTracker.Key(upstream)))
        .Where(result => result is not null).Cast<ResolverProbeResult>().ToArray();

    public async Task<ResolverProbeResult> ProbeAsync(UpstreamDefinition upstream, CancellationToken cancellationToken)
    {
        ResolverProbeResult result;
        var clock = Stopwatch.StartNew();
        try
        {
            if (!upstream.Enabled) result = new(upstream.Id, upstream.Name, upstream.Protocol, DateTimeOffset.UtcNow, null, "disabled", null);
            else if (upstream.Protocol is DnsProtocol.Doh or DnsProtocol.Doh3 && upstream.HostName is "dns.nextdns.io" or "dns.controld.com" && new Uri(upstream.Endpoint).AbsolutePath.Trim('/').Length == 0)
                result = new(upstream.Id, upstream.Name, upstream.Protocol, DateTimeOffset.UtcNow, null, "profile-required", null);
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(upstream.TimeoutMilliseconds, 250, 30000)));
                var query = DnsWire.CreateQuery("example.com");
                var response = await transports[upstream.Protocol].QueryAsync(upstream, query, timeout.Token).ConfigureAwait(false);
                if (!DnsWire.MatchesResponseQuestion(query, response)) throw new InvalidDataException("Invalid response.");
                result = new(upstream.Id, upstream.Name, upstream.Protocol, DateTimeOffset.UtcNow, clock.Elapsed.TotalMilliseconds, null, DnsWire.ResponseCodeName(response));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = new(upstream.Id, upstream.Name, upstream.Protocol, DateTimeOffset.UtcNow, null, ResolverFailure.Classify(error), null);
        }
        // A manual latency test does not switch routes, clear cooldowns or contaminate the traffic log.
        probes[UpstreamHealthTracker.Key(upstream)] = result;
        return result;
    }

    private static byte[]? FindBootstrapAnswer(
        string domain,
        ReadOnlySpan<byte> query,
        IEnumerable<UpstreamDefinition> upstreams)
    {
        var addresses = upstreams
            .Where(upstream => upstream.Enabled && upstream.HostName.Equals(domain, StringComparison.OrdinalIgnoreCase))
            .SelectMany(static upstream => upstream.ParsedBootstrapAddresses)
            .Distinct()
            .ToArray();
        return addresses.Length == 0 ? null : DnsWire.CreateAddressResponse(query, addresses);
    }
}
