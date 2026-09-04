namespace BetterDns.Core.Configuration;

/// <summary>Edits the default route without rewriting profiles or rule-specific chains.</summary>
public static class DefaultRouteEditor
{
    public static BetterDnsConfiguration Apply(BetterDnsConfiguration configuration, IReadOnlyList<string> orderedIds)
    {
        if (orderedIds.Count == 0) throw new InvalidDataException("Choose a primary DNS provider.");
        if (orderedIds.Distinct(StringComparer.Ordinal).Count() != orderedIds.Count)
            throw new InvalidDataException("A DNS provider can only appear once in the fallback order.");
        foreach (var id in orderedIds)
        {
            var upstream = configuration.Upstreams.SingleOrDefault(value => value.Id == id);
            if (upstream is null || !upstream.Enabled)
                throw new InvalidDataException("Every selected DNS provider must exist and be enabled.");
        }

        var current = configuration.Chains.Single(value => value.Id == configuration.DefaultChainId);
        if (current.UpstreamIds.SequenceEqual(orderedIds, StringComparer.Ordinal)) return configuration;
        // A default-route edit must not silently change a domain rule that uses this chain.
        var sharedWithRule = configuration.Rules.Any(rule => rule.ChainId == current.Id);
        var edited = current with
        {
            Id = sharedWithRule ? "home-" + Guid.NewGuid().ToString("N") : current.Id,
            Name = sharedWithRule ? "Default DNS" : current.Name,
            UpstreamIds = orderedIds.ToArray()
        };
        return configuration with
        {
            DefaultChainId = edited.Id,
            Chains = sharedWithRule
                ? [.. configuration.Chains, edited]
                : configuration.Chains.Select(chain => chain.Id == current.Id ? edited : chain).ToArray()
        };
    }
}
