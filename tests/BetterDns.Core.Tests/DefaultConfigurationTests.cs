using BetterDns.Core.Configuration;

namespace BetterDns.Core.Tests;

public sealed class DefaultConfigurationTests
{
    [Fact]
    public void Required_public_defaults_are_doh3()
    {
        var configuration = DefaultConfiguration.Create();
        var ids = new[] { "hagezi-root", "cloudflare-security", "quad9-secure", "google-public" };

        foreach (var id in ids)
        {
            var upstream = Assert.Single(configuration.Upstreams, value => value.Id == id);
            Assert.Equal(DnsProtocol.Doh3, upstream.Protocol);
            Assert.NotEmpty(upstream.BootstrapAddresses);
        }
    }

    [Fact]
    public void Control_d_fails_over_to_next_dns()
    {
        var configuration = DefaultConfiguration.Create();
        var chain = Assert.Single(configuration.Chains, value => value.Id == "controld-nextdns");

        Assert.Equal(["controld", "nextdns"], chain.UpstreamIds);
    }
}
