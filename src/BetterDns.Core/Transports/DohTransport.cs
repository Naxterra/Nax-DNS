using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using BetterDns.Core.Configuration;

namespace BetterDns.Core.Transports;

public sealed class DohTransport : IDnsTransport, IDisposable
{
    private readonly ConcurrentDictionary<string, HttpClient> clients = new(StringComparer.Ordinal);

    public DohTransport(DnsProtocol protocol)
    {
        if (protocol is not (DnsProtocol.Doh or DnsProtocol.Doh3))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol));
        }

        Protocol = protocol;
    }

    public DnsProtocol Protocol { get; }

    public async Task<byte[]> QueryAsync(
        UpstreamDefinition upstream,
        ReadOnlyMemory<byte> query,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(upstream.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new FormatException("DoH endpoints must be absolute HTTPS URLs.");
        }

        var client = clients.GetOrAdd(upstream.Id + "|" + upstream.Endpoint, static _ => CreateClient());
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = Protocol == DnsProtocol.Doh3 ? HttpVersion.Version30 : HttpVersion.Version20,
            VersionPolicy = Protocol == DnsProtocol.Doh3
                ? HttpVersionPolicy.RequestVersionExact
                : HttpVersionPolicy.RequestVersionOrLower,
            Content = new ByteArrayContent(query.ToArray())
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        request.Headers.UserAgent.ParseAdd("BetterDNS/0.1");

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (payload.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("DoH response exceeds the DNS message size limit.");
        }

        return payload;
    }

    public void Dispose()
    {
        foreach (var client in clients.Values)
        {
            client.Dispose();
        }

        clients.Clear();
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseCookies = false,
            UseProxy = false
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
