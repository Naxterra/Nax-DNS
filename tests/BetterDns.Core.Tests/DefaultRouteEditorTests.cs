using BetterDns.Core.Configuration;

namespace BetterDns.Core.Tests;

public sealed class DefaultRouteEditorTests
{
    [Fact]
    public void Unchanged_route_preserves_entire_configuration()
    {
        var config = DefaultConfiguration.Create();
        Assert.Same(config, DefaultRouteEditor.Apply(config, config.Chains[0].UpstreamIds));
    }

    [Fact]
    public void Editing_default_route_does_not_change_rule_specific_routes_or_profiles()
    {
        var original = DefaultConfiguration.Create();
        var config = original with { Rules = [new() { Name = "Work", Pattern = "work.example", ChainId = original.DefaultChainId }] };
        var edited = DefaultRouteEditor.Apply(config, ["google-public", "quad9-secure"]);
        Assert.NotEqual(config.DefaultChainId, edited.DefaultChainId);
        Assert.Equal(config.Chains, edited.Chains.Take(config.Chains.Count));
        Assert.Same(config.Upstreams, edited.Upstreams);
        Assert.Same(config.Rules, edited.Rules);
        Assert.Equal(["google-public", "quad9-secure"], edited.Chains.Last().UpstreamIds);
    }

    [Fact]
    public void Editing_unshared_route_preserves_thresholds_and_other_chains()
    {
        var config = DefaultConfiguration.Create();
        var edited = DefaultRouteEditor.Apply(config, ["google-public", "quad9-secure"]);
        Assert.Equal(config.DefaultChainId, edited.DefaultChainId);
        Assert.Equal(config.Chains[0].FailureThreshold, edited.Chains[0].FailureThreshold);
        Assert.Equal(config.Chains[1], edited.Chains[1]);
    }

    [Fact]
    public void Rejects_empty_duplicate_missing_providers_but_preserves_disabled_positions()
    {
        var config = DefaultConfiguration.Create();
        Assert.Throws<InvalidDataException>(() => DefaultRouteEditor.Apply(config, []));
        Assert.Throws<InvalidDataException>(() => DefaultRouteEditor.Apply(config, ["hagezi-root", "hagezi-root"]));
        Assert.Throws<InvalidDataException>(() => DefaultRouteEditor.Apply(config, ["missing"]));
        config = config with { Upstreams = config.Upstreams.Select(provider => provider with { Enabled = false }).ToArray() };
        Assert.Equal(["hagezi-root"], DefaultRouteEditor.Apply(config, ["hagezi-root"]).Chains[0].UpstreamIds);
    }
}
