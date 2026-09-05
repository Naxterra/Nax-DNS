using System.Diagnostics;
using System.Collections.Concurrent;
using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Transports;

namespace BetterDns.Core.Routing;

public sealed class DnsRouter : IDisposable
{
    private readonly DomainRuleMatcher matcher = new();
    private readonly TimeProvider time;
    private DateTimeOffset Now => time.GetUtcNow();
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
        IEnumerable<IDnsTransport> availableTransports, TimeProvider? timeProvider = null)
    {
        this.time = timeProvider ?? TimeProvider.System;
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
            queryLog.Add(new(Now, domain, "Bootstrap", "Local bootstrap", "NOERROR", stopwatch.Elapsed.TotalMilliseconds));
            return bootstrap;
        }

        var rule = matcher.Match(domain, configuration.Rules);
        if (rule?.Action == RuleAction.Block)
        {
            queryLog.Add(new(Now, domain, rule.Name, null, "BLOCKED", stopwatch.Elapsed.TotalMilliseconds));
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
        var failoverPending = false;
        foreach (var upstream in upstreams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = health.TryBeginAttempt(upstream, Now);
            if (lease is null)
            {
                attempts.Add(new(upstream.Id, upstream.Name, upstream.Protocol, Now, null, "cooldown", null));
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
                health.Succeed(lease, attempt.Elapsed, Now, responseCode);
                attempts.Add(new(upstream.Id, upstream.Name, upstream.Protocol, Now, attempt.Elapsed.TotalMilliseconds, null, responseCode));
                queryLog.Add(new(
                    Now,
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
                var mayFailOver = health.Fail(lease, ConfirmationPolicy(configuration, chain, upstream.Id), code, Now);
                attempts.Add(new(upstream.Id, upstream.Name, upstream.Protocol, Now, attempt.Elapsed.TotalMilliseconds, code, null));
                if (!mayFailOver) { failoverPending = true; break; }
            }
        }

        queryLog.Add(new(
            Now,
            domain,
            rule?.Name,
            null,
            failoverPending ? "FAILOVER PENDING" : attempts.Count == 0 ? "NO UPSTREAM" : "FAILOVER EXHAUSTED",
            stopwatch.Elapsed.TotalMilliseconds,
            ChainId: chain.Id,
            Attempts: attempts));
        return DnsWire.CreateErrorResponse(query.Span, 2);
    }

    public async Task ConfirmPendingFailuresAsync(BetterDnsConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!configuration.Active) return;
        var candidates = configuration.Chains.OrderBy(chain => chain.Id == configuration.DefaultChainId ? 0 : 1)
            .SelectMany(chain => chain.UpstreamIds.Select(id => (Chain: chain, Server: configuration.Upstreams.FirstOrDefault(server => server.Id == id))))
            .Where(item => item.Server is { Enabled: true }).DistinctBy(item => item.Server!.Id)
            .Where(item => health.NeedsConfirmation(item.Server!, Now)).ToArray();
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (item, token) =>
        {
            var server = item.Server!;
            var lease = health.TryBeginAttempt(server, Now);
            if (lease is null) return;
            var elapsed = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(server.TimeoutMilliseconds, 250, 30000)));
            try
            {
                var query = DnsWire.CreateQuery("example.com");
                var response = await transports[server.Protocol].QueryAsync(server, query, timeout.Token).ConfigureAwait(false);
                if (!DnsWire.MatchesResponseQuestion(query, response)) throw new InvalidDataException("Invalid health-check response.");
                health.Succeed(lease, elapsed.Elapsed, Now, DnsWire.ResponseCodeName(response), "automatic");
            }
            catch (Exception error)
            {
                if (token.IsCancellationRequested) { health.Cancel(lease); token.ThrowIfCancellationRequested(); }
                health.Fail(lease, ConfirmationPolicy(configuration, item.Chain, server.Id), ResolverFailure.Classify(error), Now, "automatic");
            }
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var disposable in transports.Values.OfType<IDisposable>())
        {
            disposable.Dispose();
        }
    }

    private static FailoverChain ConfirmationPolicy(BetterDnsConfiguration configuration, FailoverChain chain, string serverId) => chain with
    {
        // Health is provider-scoped. A shorter delay in another group must not bypass
        // the confirmation window of a group using the same provider.
        FailoverAfterSeconds = configuration.Chains.Where(group => group.UpstreamIds.Contains(serverId)).Max(group => group.FailoverAfterSeconds)
    };

    public IReadOnlyList<ResolverProbeResult> ProbeSnapshot(IEnumerable<UpstreamDefinition> upstreams) => upstreams
        .Select(upstream => probes.GetValueOrDefault(UpstreamHealthTracker.Key(upstream)))
        .Where(result => result is not null).Cast<ResolverProbeResult>().ToArray();

    public async Task<ResolverProbeResult> ProbeAsync(UpstreamDefinition upstream, CancellationToken cancellationToken)
    {
        ResolverProbeResult result;
        var clock = Stopwatch.StartNew();
        try
        {
            if (!upstream.Enabled) result = new(upstream.Id, upstream.Name, upstream.Protocol, Now, null, "disabled", null);
            else if (upstream.Protocol is DnsProtocol.Doh or DnsProtocol.Doh3 && upstream.HostName is "dns.nextdns.io" or "dns.controld.com" && new Uri(upstream.Endpoint).AbsolutePath.Trim('/').Length == 0)
                result = new(upstream.Id, upstream.Name, upstream.Protocol, Now, null, "profile-required", null);
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(upstream.TimeoutMilliseconds, 250, 30000)));
                var query = DnsWire.CreateQuery("example.com");
                var response = await transports[upstream.Protocol].QueryAsync(upstream, query, timeout.Token).ConfigureAwait(false);
                if (!DnsWire.MatchesResponseQuestion(query, response)) throw new InvalidDataException("Invalid response.");
                result = new(upstream.Id, upstream.Name, upstream.Protocol, Now, clock.Elapsed.TotalMilliseconds, null, DnsWire.ResponseCodeName(response));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = new(upstream.Id, upstream.Name, upstream.Protocol, Now, null, ResolverFailure.Classify(error), null);
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
