using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;

namespace BetterDns.Core.Transports;

public sealed class DohTransport : IDnsTransport, IDisposable
{
    private readonly ConcurrentDictionary<string, HttpClient> clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IPAddress> preferredAddresses = new(StringComparer.Ordinal);
    private readonly Func<HttpMessageHandler> handlerFactory;

    public DohTransport(DnsProtocol protocol, Func<HttpMessageHandler>? handlerFactory = null)
    {
        if (protocol is not (DnsProtocol.Doh or DnsProtocol.Doh3)) throw new ArgumentOutOfRangeException(nameof(protocol));
        Protocol = protocol;
        this.handlerFactory = handlerFactory ?? CreateHandler;
    }

    public DnsProtocol Protocol { get; }

    public async Task<byte[]> QueryAsync(UpstreamDefinition upstream, ReadOnlyMemory<byte> query, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(upstream.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0)
            throw new FormatException("DoH endpoints must be absolute HTTPS URLs without credentials or fragments.");

        var originalId = DnsWire.GetId(query.Span);
        var wireQuery = DnsWire.WithId(query.Span, 0); // RFC 8484: HTTP correlation replaces DNS transaction IDs.
        var useGet = Protocol == DnsProtocol.Doh3 && wireQuery.Length <= 1024;

        var key = upstream.Id + "|" + upstream.Endpoint + "|" + string.Join(",", upstream.BootstrapAddresses);
        var configured = IPAddress.TryParse(endpoint.DnsSafeHost.Trim('[', ']'), out var literal)
            ? new[] { literal } : upstream.ParsedBootstrapAddresses.ToArray();
        if (configured.Length == 0) throw new InvalidDataException("A custom DNS endpoint needs bootstrap addresses.");
        preferredAddresses.TryGetValue(key, out var preferred);
        var addresses = configured.Distinct().OrderBy(address => address.Equals(preferred) ? 0 :
            address.AddressFamily == AddressFamily.InterNetwork ? 1 : 2).Take(8).ToArray();
        var perAddress = TimeSpan.FromMilliseconds(addresses.Length <= 2 ? Math.Clamp(upstream.TimeoutMilliseconds, 250, 30000) : Math.Clamp(upstream.TimeoutMilliseconds / 2.0, 100, 2000));
        var stagger = TimeSpan.FromMilliseconds(Math.Min(250, perAddress.TotalMilliseconds / 2));
        using var attemptsCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<(byte[] Answer, IPAddress Address)>>();
        var index = 0;
        Exception? lastError = null;
        pending.Add(SendAddressAsync(addresses[index++]));
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task completed;
                if (index < addresses.Length && pending.Count < 2)
                {
                    var delay = Task.Delay(stagger, attemptsCancellation.Token);
                    completed = await Task.WhenAny(pending.Cast<Task>().Append(delay)).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (completed == delay) { pending.Add(SendAddressAsync(addresses[index++])); continue; }
                }
                else completed = await Task.WhenAny(pending).ConfigureAwait(false);

                var request = (Task<(byte[] Answer, IPAddress Address)>)completed;
                pending.Remove(request);
                try
                {
                    var result = await request.ConfigureAwait(false);
                    preferredAddresses[key] = result.Address;
                    return result.Answer;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) { lastError = error; }
                if (index < addresses.Length) pending.Add(SendAddressAsync(addresses[index++]));
            }
            throw lastError ?? new IOException("No DNS endpoint address could be reached.");
        }
        finally
        {
            attemptsCancellation.Cancel();
            // Observe and dispose losing requests before returning; never abandon background DNS tasks.
            foreach (var task in pending)
                try { await task.ConfigureAwait(false); } catch (Exception) { }
        }

        async Task<(byte[] Answer, IPAddress Address)> SendAddressAsync(IPAddress address)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(attemptsCancellation.Token);
            deadline.CancelAfter(perAddress);
            var addressBuilder = new UriBuilder(endpoint) { Host = address.ToString() };
            if (useGet)
            {
                var parameters = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Where(parameter => Uri.UnescapeDataString(parameter.Split('=', 2)[0]) != "dns").ToList();
                parameters.Add("dns=" + Convert.ToBase64String(wireQuery).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
                addressBuilder.Query = string.Join("&", parameters);
            }
            var addressUri = addressBuilder.Uri;
            var client = clients.GetOrAdd(key + "|" + address, _ => new HttpClient(this.handlerFactory(), true) { Timeout = Timeout.InfiniteTimeSpan });
            using var request = new HttpRequestMessage(useGet ? HttpMethod.Get : HttpMethod.Post, addressUri)
            {
                Version = Protocol == DnsProtocol.Doh3 ? HttpVersion.Version30 : HttpVersion.Version20,
                VersionPolicy = Protocol == DnsProtocol.Doh3 ? HttpVersionPolicy.RequestVersionExact : HttpVersionPolicy.RequestVersionOrLower,
                Content = useGet ? null : new ByteArrayContent(wireQuery)
            };
            // .NET uses Host for TLS SNI/name validation (including HTTP/3 since .NET 7).
            // Only the socket destination changes; the provider hostname and profile path do not.
            request.Headers.Host = endpoint.IsDefaultPort ? endpoint.IdnHost : endpoint.Authority;
            if (request.Content is not null) request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
            request.Headers.UserAgent.ParseAdd("BetterDNS/" + typeof(DohTransport).Assembly.GetName().Version?.ToString(3));
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (Protocol == DnsProtocol.Doh3 && response.Version.Major != 3)
                    throw new InvalidDataException("A strict DoH3 request did not receive HTTP/3.");
                var payload = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
                if (payload.Length > ushort.MaxValue || !DnsWire.MatchesResponseQuestion(wireQuery, payload))
                    throw new InvalidDataException("DNS response is invalid or does not match the request.");
                return (DnsWire.WithId(payload, originalId), address);
            }
            catch (OperationCanceledException) when (!attemptsCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException("The DNS endpoint address did not respond in time.");
            }
        }
    }

    public void Dispose()
    {
        foreach (var client in clients.Values) client.Dispose();
        clients.Clear();
    }

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        EnableMultipleHttp2Connections = true,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        UseCookies = false,
        UseProxy = false
    };
}
