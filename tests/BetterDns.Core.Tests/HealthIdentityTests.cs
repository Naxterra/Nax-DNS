using BetterDns.Core.Configuration;
using BetterDns.Core.Routing;

namespace BetterDns.Core.Tests;

public sealed class HealthIdentityTests
{
    [Fact]
    public void Changed_endpoint_is_not_presented_as_successfully_tested()
    {
        var provider = DefaultConfiguration.Create().Upstreams[0];
        var health = new UpstreamHealthTracker();
        health.RecordSuccess(provider, TimeSpan.FromMilliseconds(20));
        Assert.NotNull(Assert.Single(health.Snapshot([provider])).LastLatencyMilliseconds);
        var edited = provider with { Endpoint = "https://new.example/dns-query" };
        Assert.Null(Assert.Single(health.Snapshot([edited])).LastLatencyMilliseconds);
    }
}
