using System.Net;
using BetterDns.Core.Configuration;

namespace BetterDns.Gui.ViewModels;

public static class ProviderValidation
{
    public static string? Problem(UpstreamDefinition provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Name)) return "Provider.NameRequired";
        if (provider.TimeoutMilliseconds is < 250 or > 30000) return "Provider.TimeoutInvalid";
        if (provider.Protocol is DnsProtocol.Doh or DnsProtocol.Doh3)
        {
            if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment)) return "Provider.EndpointInvalid";
            if ((endpoint.Host == "dns.controld.com" || endpoint.Host == "dns.nextdns.io") && endpoint.AbsolutePath.Trim('/').Length == 0)
                return "Provider.ProfileRequired";
        }
        else
        {
            try
            {
                if (string.IsNullOrWhiteSpace(provider.HostName)) return "Provider.EndpointInvalid";
                if (!IPAddress.TryParse(provider.Endpoint, out _))
                {
                    var address = provider.Endpoint.Contains("://", StringComparison.Ordinal) ? provider.Endpoint : "tls://" + provider.Endpoint;
                    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                        uri.Scheme is not ("tls" or "dot" or "quic" or "doq") ||
                        uri.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo) ||
                        !string.IsNullOrEmpty(uri.Fragment) || Uri.CheckHostName(uri.DnsSafeHost) == UriHostNameType.Unknown)
                        return "Provider.EndpointInvalid";
                }
            }
            catch (Exception error) when (error is ArgumentException or FormatException) { return "Provider.EndpointInvalid"; }
        }
        if (provider.BootstrapAddresses.Any(address => !IPAddress.TryParse(address, out _))) return "Provider.BootstrapInvalid";
        if (provider.ParsedBootstrapAddresses.Count == 0 && !IPAddress.TryParse(provider.HostName, out _)) return "Provider.BootstrapRequired";
        return null;
    }

    public static string SuggestedBootstrap(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return string.Empty;
        return string.Join(", ", KnownResolverBootstrap.ForHost(uri.Host));
    }
}
