namespace BetterDns.Core.Configuration;

/// <summary>Operator-published bootstrap addresses; never discovered through intercepted DNS.</summary>
public static class KnownResolverBootstrap
{
    public static IReadOnlyList<string> ForHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        // Control D documents the premium (.22) and free (.11) addresses separately:
        // https://docs.controld.com/docs/control-d-ip-ranges
        if (host == "dns.controld.com" || host.EndsWith(".dns.controld.com", StringComparison.Ordinal))
            return ["76.76.2.22", "76.76.10.22", "2606:1a40::22", "2606:1a40:1::22"];
        if (host == "freedns.controld.com" || host.EndsWith(".freedns.controld.com", StringComparison.Ordinal))
            return ["76.76.2.11", "76.76.10.11", "2606:1a40::11", "2606:1a40:1::11"];
        if (host == "dns.nextdns.io" || host.EndsWith(".dns.nextdns.io", StringComparison.Ordinal))
            return ["45.90.28.0", "45.90.30.0", "2a07:a8c0::", "2a07:a8c1::"];
        return DefaultConfiguration.Create().Upstreams.FirstOrDefault(provider => provider.HostName.Equals(host, StringComparison.OrdinalIgnoreCase))?.BootstrapAddresses ?? [];
    }
}
