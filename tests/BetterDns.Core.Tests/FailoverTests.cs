using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Routing;
using BetterDns.Core.Transports;

namespace BetterDns.Core.Tests;

public sealed class FailoverTests
{
    [Fact]
    public async Task Disabled_primary_is_skipped_without_an_attempt_and_retains_its_place()
    {
        var primary = new UpstreamDefinition { Id = "primary", Name = "Primary", Endpoint = "https://primary.invalid/dns-query", Protocol = DnsProtocol.Doh, Enabled = false };
        var secondary = primary with { Id = "secondary", Name = "Secondary", Enabled = true };
        var chain = new FailoverChain { Id = "default", Name = "Default", UpstreamIds = ["primary", "secondary"] };
        var config = new BetterDnsConfiguration { DefaultChainId = "default", Upstreams = [primary, secondary], Chains = [chain] };
        var transport = new RecordingTransport();
        using var router = new DnsRouter(new UpstreamHealthTracker(), new QueryLog(), [transport]);
        var query = DnsWire.CreateQuery("example.com");
        var response = await router.ResolveAsync(query, config, CancellationToken.None);
        Assert.True(DnsWire.IsUsableResponse(response));
        Assert.Equal(["secondary"], transport.Calls);
        Assert.Equal(["primary", "secondary"], chain.UpstreamIds);
    }

    private sealed class RecordingTransport : IDnsTransport
    {
        public DnsProtocol Protocol => DnsProtocol.Doh;
        public List<string> Calls { get; } = [];
        public Task<byte[]> QueryAsync(UpstreamDefinition upstream, ReadOnlyMemory<byte> query, CancellationToken cancellationToken)
        { Calls.Add(upstream.Id); return Task.FromResult(DnsWire.CreateErrorResponse(query.Span, 0)); }
    }

    [Fact]
    public async Task Failed_primary_uses_secondary_and_opens_primary_circuit()
    {
        var primary = new UpstreamDefinition
        {
            Id = "primary",
            Name = "Primary",
            Protocol = DnsProtocol.Doh,
            Endpoint = "https://primary.invalid/dns-query",
            TimeoutMilliseconds = 1000
        };
        var secondary = primary with { Id = "secondary", Name = "Secondary" };
        var configuration = new BetterDnsConfiguration
        {
            DefaultChainId = "default",
            Upstreams = [primary, secondary],
            Chains =
            [
                new FailoverChain
                {
                    Id = "default",
                    Name = "Default",
                    UpstreamIds = ["primary", "secondary"],
                    FailureThreshold = 1,
                    FailoverAfterSeconds = 0,
                    CooldownSeconds = 60
                }
            ]
        };
        var health = new UpstreamHealthTracker();
        var queryLog = new QueryLog();
        using var router = new DnsRouter(health, queryLog, [new FakeTransport()]);
        var query = DnsWire.CreateQuery("example.com");

        var response = await router.ResolveAsync(query, configuration, CancellationToken.None);

        Assert.Equal("NOERROR", DnsWire.ResponseCodeName(response));
        Assert.Equal("Secondary", Assert.Single(queryLog.Snapshot()).UpstreamName);
        var primaryStatus = Assert.Single(health.Snapshot(configuration.Upstreams), value => value.Id == "primary");
        Assert.False(primaryStatus.Available);
        Assert.Equal(1, primaryStatus.ConsecutiveFailures);
    }

    private sealed class FakeTransport : IDnsTransport
    {
        public DnsProtocol Protocol => DnsProtocol.Doh;

        public Task<byte[]> QueryAsync(UpstreamDefinition upstream, ReadOnlyMemory<byte> query, CancellationToken cancellationToken)
        {
            if (upstream.Id == "primary")
            {
                throw new IOException("Primary is down.");
            }

            return Task.FromResult(DnsWire.CreateErrorResponse(query.Span, 0));
        }
    }
}
