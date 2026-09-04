using System.Diagnostics;
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

        Exception? lastError = null;
        foreach (var upstream in OrderByCircuit(upstreams, chain))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(upstream.TimeoutMilliseconds, 250, 30_000)));
            var attempt = Stopwatch.StartNew();
            try
            {
                var response = await transports[upstream.Protocol]
                    .QueryAsync(upstream, query, timeout.Token)
                    .ConfigureAwait(false);
                if (!DnsWire.MatchesQuestion(query.Span, response))
                {
                    throw new InvalidDataException($"Resolver returned {DnsWire.ResponseCodeName(response)}.");
                }

                health.RecordSuccess(upstream, attempt.Elapsed);
                queryLog.Add(new(
                    DateTimeOffset.UtcNow,
                    domain,
                    rule?.Name,
                    upstream.Name,
                    DnsWire.ResponseCodeName(response),
                    stopwatch.Elapsed.TotalMilliseconds));
                return response;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                lastError = error;
                health.RecordFailure(upstream, chain, error, DateTimeOffset.UtcNow);
            }
        }

        queryLog.Add(new(
            DateTimeOffset.UtcNow,
            domain,
            rule?.Name,
            null,
            lastError is null ? "NO UPSTREAM" : "FAILOVER EXHAUSTED",
            stopwatch.Elapsed.TotalMilliseconds));
        return DnsWire.CreateErrorResponse(query.Span, 2);
    }

    public void Dispose()
    {
        foreach (var disposable in transports.Values.OfType<IDisposable>())
        {
            disposable.Dispose();
        }
    }

    private IEnumerable<UpstreamDefinition> OrderByCircuit(
        IReadOnlyList<UpstreamDefinition> upstreams,
        FailoverChain chain)
    {
        var now = DateTimeOffset.UtcNow;
        var healthy = upstreams.Where(value => health.CanTry(value, chain, now)).ToArray();
        return healthy.Length > 0 ? healthy : upstreams.Take(1);
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
