using System.Net;
using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Transports;

namespace BetterDns.Core.Tests;

public sealed class DohAddressTransportTests
{
    private static UpstreamDefinition Server(params string[] addresses) => new() { Id = "test", Name = "Test", Protocol = DnsProtocol.Doh3, Endpoint = "https://resolver.example/test-profile?device=pc&dns=old", BootstrapAddresses = addresses };

    [Fact]
    public async Task Strict_HTTP3_pins_connection_address_but_preserves_Host_profile_and_DNS_ID()
    {
        using var transport = new DohTransport(DnsProtocol.Doh3, () => new Handler(async (request, token) =>
        {
            Assert.Equal("192.0.2.1", request.RequestUri!.Host);
            Assert.Equal("resolver.example", request.Headers.Host);
            Assert.Equal("/test-profile", request.RequestUri.AbsolutePath);
            Assert.Contains("device=pc", request.RequestUri.Query);
            Assert.DoesNotContain("dns=old", request.RequestUri.Query);
            Assert.Equal(HttpVersion.Version30, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Equal(HttpMethod.Get, request.Method);
            var query = await ReadQuery(request, token);
            Assert.Equal(0, DnsWire.GetId(query));
            return Reply(request, query);
        }));
        var original = DnsWire.WithId(DnsWire.CreateQuery("example.com"), 1234);
        var result = await transport.QueryAsync(Server("192.0.2.1"), original, CancellationToken.None);
        Assert.Equal(1234, DnsWire.GetId(result));
        Assert.True(DnsWire.MatchesResponseQuestion(original, result));
    }

    [Fact]
    public async Task A_stalled_address_does_not_prevent_another_address_of_same_provider_answering()
    {
        var firstCanceled = false;
        using var transport = new DohTransport(DnsProtocol.Doh3, () => new Handler(async (request, token) =>
        {
            Assert.Equal("resolver.example", request.Headers.Host);
            if (request.RequestUri!.Host == "192.0.2.1")
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { firstCanceled = true; throw; }
            }
            return Reply(request, await ReadQuery(request, token));
        }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var query = DnsWire.CreateQuery("example.com");
        var answer = await transport.QueryAsync(Server("192.0.2.1", "192.0.2.2"), query, deadline.Token);
        Assert.True(DnsWire.MatchesResponseQuestion(query, answer));
        Assert.True(firstCanceled);
    }

    [Fact]
    public async Task Large_queries_use_POST_without_protocol_downgrade()
    {
        using var transport = new DohTransport(DnsProtocol.Doh3, () => new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Equal("application/dns-message", request.Content!.Headers.ContentType!.MediaType);
            return Reply(request, await ReadQuery(request, token));
        }));
        var query = DnsWire.CreateQuery("example.com").Concat(new byte[1100]).ToArray();
        var response = await transport.QueryAsync(Server("192.0.2.1"), query, CancellationToken.None);
        Assert.True(DnsWire.MatchesResponseQuestion(query, response));
    }

    [Fact]
    public async Task Lower_HTTP_version_is_rejected_in_strict_mode()
    {
        using var transport = new DohTransport(DnsProtocol.Doh3, () => new Handler(async (request, token) =>
        {
            var response = Reply(request, await ReadQuery(request, token)); response.Version = HttpVersion.Version20; return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.QueryAsync(Server("192.0.2.1"), DnsWire.CreateQuery("example.com"), CancellationToken.None));
    }

    [Fact]
    public async Task DNS_refusal_is_returned_unchanged_not_retried_as_transport_failure()
    {
        var calls = 0;
        using var transport = new DohTransport(DnsProtocol.Doh3, () => new Handler(async (request, token) =>
        { Interlocked.Increment(ref calls); return Reply(request, await ReadQuery(request, token), 5); }));
        var response = await transport.QueryAsync(Server("192.0.2.1", "192.0.2.2"), DnsWire.CreateQuery("example.com"), CancellationToken.None);
        Assert.Equal("REFUSED", DnsWire.ResponseCodeName(response)); Assert.Equal(1, calls);
    }

    [Fact]
    public void Only_the_exact_legacy_free_Control_D_preset_uses_corrected_addresses()
    {
        var legacy = new UpstreamDefinition { Id = "controld", Name = "Control D (replace with your resolver URL)", Endpoint = "https://freedns.controld.com/p2", Protocol = DnsProtocol.Doh3,
            BootstrapAddresses = ["76.76.2.2", "76.76.10.2", "2606:1a40::2", "2606:1a40:1::2"] };
        Assert.Contains(legacy.ParsedBootstrapAddresses, address => address.ToString() == "76.76.2.11");
        var custom = legacy with { Id = "custom", Name = "Custom override" };
        Assert.Contains(custom.ParsedBootstrapAddresses, address => address.ToString() == "76.76.2.2");
    }

    private static async Task<byte[]> ReadQuery(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method == HttpMethod.Post) return await request.Content!.ReadAsByteArrayAsync(token);
        var value = request.RequestUri!.Query.TrimStart('?').Split('&').Single(parameter => parameter.StartsWith("dns=", StringComparison.Ordinal))[4..].Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
    }
    private static HttpResponseMessage Reply(HttpRequestMessage request, byte[] query, byte code = 0) => new(HttpStatusCode.OK)
    { Version = request.Version, Content = new ByteArrayContent(DnsWire.CreateErrorResponse(query, code)) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
