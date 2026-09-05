using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Routing;
using BetterDns.Core.Transports;

namespace BetterDns.Core.Tests;

public sealed class KnownResolverBootstrapTests
{
    [Theory]
    [InlineData(DnsProtocol.Doh3, "https://dns.controld.com/test-profile", "76.76.2.22")]
    [InlineData(DnsProtocol.Doh, "https://freedns.controld.com/p2", "76.76.2.11")]
    [InlineData(DnsProtocol.Dot, "test-profile.dns.controld.com", "76.76.2.22")]
    [InlineData(DnsProtocol.Doq, "quic://test-profile.dns.controld.com", "76.76.2.22")]
    [InlineData(DnsProtocol.Doh3, "https://dns.nextdns.io/test-profile", "45.90.28.0")]
    public void Known_resolvers_get_automatic_bootstrap_without_rewriting_profiles(DnsProtocol protocol, string endpoint, string expected)
    {
        var provider = new UpstreamDefinition { Id = "test", Name = "Personal", Protocol = protocol, Endpoint = endpoint };
        Assert.Contains(provider.ParsedBootstrapAddresses, address => address.ToString() == expected);
        Assert.Empty(provider.BootstrapAddresses);
        Assert.Equal(endpoint, provider.Endpoint);
    }

    [Fact]
    public void Explicit_addresses_are_not_overwritten()
    {
        var provider = new UpstreamDefinition { Id = "test", Name = "Personal", Protocol = DnsProtocol.Doh3, Endpoint = "https://dns.controld.com/test", BootstrapAddresses = ["192.0.2.1"] };
        Assert.Equal("192.0.2.1", Assert.Single(provider.ParsedBootstrapAddresses).ToString());
    }

    [Theory]
    [InlineData("dns.controld.com.evil.example")]
    [InlineData("evildns.controld.com")]
    [InlineData("dns.nextdns.io.evil.example")]
    public void Lookalike_hosts_do_not_get_provider_addresses(string host) => Assert.Empty(KnownResolverBootstrap.ForHost(host));

    [Fact]
    public async Task Existing_Control_D_profile_without_IPs_bootstraps_without_upstream_recursion()
    {
        var provider = new UpstreamDefinition { Id = "personal", Name = "Control D", Protocol = DnsProtocol.Doh3, Endpoint = "https://dns.controld.com/test-profile" };
        var config = new BetterDnsConfiguration { DefaultChainId = "default", Upstreams = [provider], Chains = [new() { Id = "default", Name = "Default", UpstreamIds = [provider.Id] }] };
        var transport = new NoNetworkTransport();
        var log = new QueryLog();
        using var router = new DnsRouter(new UpstreamHealthTracker(), log, [transport]);
        var query = DnsWire.CreateQuery("dns.controld.com");
        var response = await router.ResolveAsync(query, config, CancellationToken.None);
        Assert.True(DnsWire.MatchesQuestion(query, response));
        Assert.True(DnsWire.IsUsableResponse(response));
        Assert.Equal(0, transport.Calls);
        Assert.Equal("Local bootstrap", Assert.Single(log.Snapshot()).UpstreamName);
    }

    private sealed class NoNetworkTransport : IDnsTransport
    {
        public DnsProtocol Protocol => DnsProtocol.Doh3;
        public int Calls { get; private set; }
        public Task<byte[]> QueryAsync(UpstreamDefinition upstream, ReadOnlyMemory<byte> query, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Bootstrap must not call an upstream resolver."); }
    }
}
