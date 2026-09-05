using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Routing;
using BetterDns.Core.Transports;
using BetterDns.Service.Configuration;
using BetterDns.Service.LocalDns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BetterDns.Service.Tests;

public sealed class LoopbackDnsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_udp_and_tcp_sockets_preserve_questions_and_support_multiple_tcp_frames(bool ipv6)
    {
        if (ipv6 && !Socket.OSSupportsIPv6) return;
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        await using var server = await RunningServer.Create((query, _) =>
            Task.FromResult(DnsWire.CreateAddressResponse(query.Span, [IPAddress.Parse("192.0.2.42")])));
        Assert.All(server.Endpoints, endpoint => Assert.True(endpoint.StartsWith("127.0.0.1:") || endpoint.StartsWith("[::1]:")));
        var query = DnsWire.CreateQuery("listener.example");
        using var udp = new UdpClient(address.AddressFamily);
        udp.Connect(address, server.Port); // Connected UDP, like c-ares.
        await udp.SendAsync(query);
        var answer = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(DnsWire.MatchesQuestion(query, answer.Buffer));

        using var tcp = new TcpClient(address.AddressFamily);
        await tcp.ConnectAsync(address, server.Port);
        var stream = tcp.GetStream();
        var second = DnsWire.CreateQuery("second.example");
        var firstFrame = Frame(query);
        // Split the length prefix across reads, then pipeline a second query.
        await stream.WriteAsync(firstFrame.AsMemory(0, 1));
        await stream.WriteAsync(firstFrame.AsMemory(1));
        await stream.WriteAsync(Frame(second));
        var firstAnswer = await ReadFrame(stream);
        Assert.True(DnsWire.MatchesQuestion(query, firstAnswer));
        var secondAnswer = await ReadFrame(stream);
        Assert.True(DnsWire.MatchesQuestion(second, secondAnswer));
    }

    [Fact]
    public async Task Large_udp_answers_are_truncated_and_tcp_returns_complete_answer()
    {
        var addresses = Enumerable.Range(1, 80).Select(n => IPAddress.Parse("192.0.2." + n)).ToArray();
        await using var server = await RunningServer.Create((query, _) =>
            Task.FromResult(DnsWire.CreateAddressResponse(query.Span, addresses)));
        var query = DnsWire.CreateQuery("large.example");
        using var udp = new UdpClient();
        await udp.SendAsync(query, new IPEndPoint(IPAddress.Loopback, server.Port));
        var shortReply = (await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer;
        Assert.True(shortReply.Length <= 512);
        Assert.NotEqual(0, shortReply[2] & 2);
        Assert.Equal(DnsWire.GetId(query), DnsWire.GetId(shortReply));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await tcp.GetStream().WriteAsync(Frame(query));
        var full = await ReadFrame(tcp.GetStream());
        Assert.True(full.Length > 512);
        Assert.Equal(0, full[2] & 2);
        Assert.True(DnsWire.MatchesQuestion(query, full));
        // EDNS clients advertising sufficient space receive the complete UDP answer.
        var edns = query.Concat(new byte[] { 0, 0, 41, 16, 0, 0, 0, 0, 0, 0, 0 }).ToArray();
        edns[11] = 1;
        await udp.SendAsync(edns, new IPEndPoint(IPAddress.Loopback, server.Port));
        Assert.Equal(full.Length, (await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer.Length);
    }

    [Fact]
    public async Task Listener_uses_same_DoH3_router_rules_and_logs_and_refuses_when_routing_off()
    {
        var directory = Directory.CreateTempSubdirectory("BetterDns-listener-");
        try
        {
            var store = new ConfigurationStore(directory.FullName);
            store.Save(store.Current with { Active = true });
            var transport = new TestTransport();
            var log = new QueryLog();
            using var router = new DnsRouter(new UpstreamHealthTracker(), log, [transport]);
            var worker = new LocalDnsWorker(store, router, new LocalDnsState(), NullLogger<LocalDnsWorker>.Instance);
            await using var server = await RunningServer.Create(worker.ResolveAsync);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            var query = DnsWire.CreateQuery("routing.example");
            await tcp.GetStream().WriteAsync(Frame(query));
            var answer = await ReadFrame(tcp.GetStream());
            Assert.True(DnsWire.MatchesQuestion(query, answer));
            Assert.Equal(1, transport.Calls);
            Assert.Contains(log.Snapshot(), item => item.Domain == "routing.example" && item.Protocol == DnsProtocol.Doh3);
            store.Save(store.Current with { Active = false });
            await tcp.GetStream().WriteAsync(Frame(query));
            Assert.Equal("REFUSED", DnsWire.ResponseCodeName(await ReadFrame(tcp.GetStream())));
            Assert.Equal(1, transport.Calls);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Occupied_port_never_reports_ready_and_partial_binding_is_released()
    {
        using var owner = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)owner.Client.LocalEndPoint!).Port;
        var ready = false;
        var server = new LoopbackDnsServer((q, _) => Task.FromResult(q.ToArray()), port, false);
        await Assert.ThrowsAsync<SocketException>(() => server.RunAsync(_ => ready = true, CancellationToken.None));
        Assert.False(ready);
        var tcp = new TcpListener(IPAddress.Loopback, port);
        try { tcp.Start(); } // The failed start released its earlier TCP binding.
        finally { tcp.Stop(); }
        Assert.Equal(port, ((IPEndPoint)owner.Client.LocalEndPoint!).Port);
    }

    [Fact]
    public async Task Bad_frames_and_upstream_errors_do_not_kill_listener_and_idle_clients_are_closed()
    {
        await using var server = await RunningServer.Create((_, _) => throw new IOException("test upstream failure"),
            TimeSpan.FromMilliseconds(250));
        using var udp = new UdpClient();
        var endpoint = new IPEndPoint(IPAddress.Loopback, server.Port);
        await udp.SendAsync(new byte[] { 1, 2 }, endpoint);
        var query = DnsWire.CreateQuery("failure.example");
        await udp.SendAsync(query, endpoint);
        Assert.Equal("SERVFAIL", DnsWire.ResponseCodeName((await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await tcp.GetStream().WriteAsync(new byte[] { 0 }); // Incomplete length prefix.
        Assert.Equal(0, await tcp.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Stopping_listener_cancels_inflight_queries_and_releases_sockets()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = await RunningServer.Create(async (q, token) => {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return q.ToArray();
        });
        using var udp = new UdpClient();
        await udp.SendAsync(DnsWire.CreateQuery("pending.example"), new IPEndPoint(IPAddress.Loopback, server.Port));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await server.DisposeAsync();
        using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, server.Port));
        Assert.NotNull(rebound.Client.LocalEndPoint);
    }

    private static byte[] Frame(byte[] message)
    {
        var frame = new byte[message.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)message.Length);
        message.CopyTo(frame, 2);
        return frame;
    }
    private static async Task<byte[]> ReadFrame(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, timeout.Token);
        var message = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
        await stream.ReadExactlyAsync(message, timeout.Token);
        return message;
    }
    private sealed class TestTransport : IDnsTransport
    {
        public DnsProtocol Protocol => DnsProtocol.Doh3;
        public int Calls { get; private set; }
        public Task<byte[]> QueryAsync(UpstreamDefinition upstream, ReadOnlyMemory<byte> query, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(DnsWire.CreateAddressResponse(query.Span, [IPAddress.Parse("192.0.2.1")]));
        }
    }
    private sealed class RunningServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private Task running = Task.CompletedTask;
        public int Port { get; private set; }
        public IReadOnlyList<string> Endpoints { get; private set; } = [];
        public static async Task<RunningServer> Create(Func<ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> resolve,
            TimeSpan? timeout = null, bool enableIpv6 = true)
        {
            var wrapper = new RunningServer();
            var server = new LoopbackDnsServer(resolve, 0, enableIpv6, timeout);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wrapper.running = server.RunAsync(endpoints => { wrapper.Endpoints = endpoints; ready.SetResult(); }, wrapper.stop.Token);
            try
            {
                var first = await Task.WhenAny(ready.Task, wrapper.running).WaitAsync(TimeSpan.FromSeconds(3));
                await first;
                wrapper.Port = server.Port;
                return wrapper;
            }
            catch { await wrapper.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            finally { stop.Dispose(); }
        }
    }
}
