using System.Net;

namespace BetterDns.Core.Transports;

internal static class EndpointParser
{
    public static (string Host, int Port) Parse(string endpoint, int defaultPort)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return (uri.DnsSafeHost, uri.IsDefaultPort ? defaultPort : uri.Port);
        }

        if (endpoint.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = endpoint.IndexOf(']');
            if (closingBracket < 0)
            {
                throw new FormatException($"Invalid IPv6 endpoint: {endpoint}");
            }

            var host = endpoint[1..closingBracket];
            var port = endpoint.Length > closingBracket + 1 && endpoint[closingBracket + 1] == ':'
                ? int.Parse(endpoint[(closingBracket + 2)..], System.Globalization.CultureInfo.InvariantCulture)
                : defaultPort;
            return (host, port);
        }

        if (IPAddress.TryParse(endpoint, out _))
        {
            return (endpoint, defaultPort);
        }

        var lastColon = endpoint.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(endpoint[(lastColon + 1)..], out var parsedPort))
        {
            return (endpoint[..lastColon], parsedPort);
        }

        return (endpoint, defaultPort);
    }
}
