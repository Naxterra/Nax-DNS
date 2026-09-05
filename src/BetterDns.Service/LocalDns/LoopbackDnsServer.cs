using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BetterDns.Core.Dns;

namespace BetterDns.Service.LocalDns;

// Socket-bound DNS for VPN clients. Never bind wildcard/LAN addresses and never
// use system DNS here: the caller supplies the same encrypted router as WinDivert.
public sealed class LoopbackDnsServer(
    Func<ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> resolve,
    int port = 53,
    bool enableIpv6 = true,
    TimeSpan? clientTimeout = null)
{
    public int Port { get; private set; }
    private readonly TimeSpan timeout = clientTimeout ?? TimeSpan.FromSeconds(30);

    public async Task RunAsync(Action<IReadOnlyList<string>> ready, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var queries = new SemaphoreSlim(128, 128);
        using var clients = new SemaphoreSlim(64, 64);
        var udpSockets = new List<UdpClient>();
        var tcpSockets = new List<TcpListener>();
        var loops = new List<Task>();
        try
        {
            Port = port;
            foreach (var address in enableIpv6 && Socket.OSSupportsIPv6
                ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : [IPAddress.Loopback])
            {
                var tcp = new TcpListener(address, Port);
                tcpSockets.Add(tcp);
                tcp.Server.ExclusiveAddressUse = true;
                if (address.AddressFamily == AddressFamily.InterNetworkV6) tcp.Server.DualMode = false;
                tcp.Start(64);
                Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                var udp = new UdpClient(address.AddressFamily);
                udpSockets.Add(udp);
                udp.ExclusiveAddressUse = true;
                if (address.AddressFamily == AddressFamily.InterNetworkV6) udp.Client.DualMode = false;
                udp.Client.Bind(new IPEndPoint(address, Port));
                // ICMP errors from a closed client socket must not terminate the UDP listener.
                if (OperatingSystem.IsWindows()) udp.Client.IOControl((IOControlCode)(-1744830452), new byte[4], null);
            }
            // Publish readiness only after all bindings succeeded (no partial listener).
            ready(tcpSockets.Select(socket => socket.LocalEndpoint.ToString()!).ToArray());
            loops.AddRange(udpSockets.Select(socket => ReceiveUdpAsync(socket, queries, lifetime.Token)));
            loops.AddRange(tcpSockets.Select(socket => AcceptTcpAsync(socket, queries, clients, lifetime.Token)));
            var finished = await Task.WhenAny(loops).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("A local DNS receive loop stopped unexpectedly.");
        }
        finally
        {
            lifetime.Cancel();
            foreach (var socket in udpSockets) socket.Dispose();
            foreach (var socket in tcpSockets) socket.Stop();
            try { await Task.WhenAll(loops).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }

    private async Task ReceiveUdpAsync(UdpClient socket, SemaphoreSlim queries, CancellationToken token)
    {
        var pending = new List<Task>();
        try
        {
            while (true)
            {
                var packet = await socket.ReceiveAsync(token).ConfigureAwait(false);
                // A VPN DNS client may bind its source to the tunnel address before sending
                // to 127.0.0.1. The destination binding is the security boundary; rejecting
                // a non-loopback source here breaks that valid local delivery.
                if (!IsQuery(packet.Buffer)) continue;
                // Under overload, let clients retry rather than growing an unbounded queue.
                if (!queries.Wait(0)) continue;
                pending.RemoveAll(task => task.IsCompleted);
                pending.Add(AnswerUdpAsync(socket, packet, queries, token));
            }
        }
        finally { await Task.WhenAll(pending).ConfigureAwait(false); }
    }

    private async Task AnswerUdpAsync(UdpClient socket, UdpReceiveResult packet, SemaphoreSlim queries, CancellationToken token)
    {
        try
        {
            var response = await ResolveSafeAsync(packet.Buffer, token).ConfigureAwait(false);
            if (response.Length > UdpResponseLimit(packet.Buffer))
            {
                response = DnsWire.CreateErrorResponse(packet.Buffer, (byte)(response[3] & 15));
                response[2] |= 2; // TC: retry the complete answer over TCP.
            }
            await socket.SendAsync(response, packet.RemoteEndPoint, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally { queries.Release(); }
    }

    private async Task AcceptTcpAsync(TcpListener socket, SemaphoreSlim queries, SemaphoreSlim clients, CancellationToken token)
    {
        var pending = new List<Task>();
        try
        {
            while (true)
            {
                var client = await socket.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (!clients.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                pending.RemoveAll(task => task.IsCompleted);
                pending.Add(AnswerTcpAsync(client, queries, clients, token));
            }
        }
        finally { await Task.WhenAll(pending).ConfigureAwait(false); }
    }

    private async Task AnswerTcpAsync(TcpClient client, SemaphoreSlim queries, SemaphoreSlim clients, CancellationToken token)
    {
        using (client)
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var prefix = new byte[2];
            // Reuse a TCP connection for sequential or pipelined requests; frame boundaries
            // are independent of socket reads. Bound idle/partial-frame clients.
            while (!token.IsCancellationRequested)
            {
                using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                frameTimeout.CancelAfter(timeout);
                var frameToken = frameTimeout.Token;
                await stream.ReadExactlyAsync(prefix, frameToken).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
                if (length < DnsWire.HeaderLength) return;
                var query = new byte[length];
                await stream.ReadExactlyAsync(query, frameToken).ConfigureAwait(false);
                if (!IsQuery(query)) return;
                await queries.WaitAsync(frameToken).ConfigureAwait(false);
                byte[] response;
                try { response = await ResolveSafeAsync(query, token).ConfigureAwait(false); }
                finally { queries.Release(); }
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                writeTimeout.CancelAfter(timeout);
                BinaryPrimitives.WriteUInt16BigEndian(prefix, checked((ushort)response.Length));
                await stream.WriteAsync(prefix, writeTimeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(response, writeTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { clients.Release(); }
    }

    private async Task<byte[]> ResolveSafeAsync(byte[] query, CancellationToken token)
    {
        try
        {
            using var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            queryTimeout.CancelAfter(timeout);
            var answer = await resolve(query, queryTimeout.Token).ConfigureAwait(false);
            return answer.Length is >= 12 and <= ushort.MaxValue ? answer : DnsWire.CreateErrorResponse(query, 2);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return DnsWire.CreateErrorResponse(query, 2); }
    }

    private static bool IsQuery(byte[] packet) => packet.Length >= 12 && (packet[2] & 0x80) == 0;

    private static int UdpResponseLimit(ReadOnlySpan<byte> query)
    {
        try
        {
            var offset = DnsWire.ReadFirstQuestion(query).QuestionEndOffset;
            var preceding = BinaryPrimitives.ReadUInt16BigEndian(query[6..]) + BinaryPrimitives.ReadUInt16BigEndian(query[8..]);
            var count = preceding + BinaryPrimitives.ReadUInt16BigEndian(query[10..]);
            for (var record = 0; record < count; record++)
            {
                while (true)
                {
                    var label = query[offset++];
                    if (label == 0) break;
                    if ((label & 0xC0) == 0xC0) { offset++; break; }
                    if (label > 63) return 512;
                    offset += label;
                }
                var type = BinaryPrimitives.ReadUInt16BigEndian(query[offset..]);
                var size = BinaryPrimitives.ReadUInt16BigEndian(query[(offset + 2)..]);
                var dataLength = BinaryPrimitives.ReadUInt16BigEndian(query[(offset + 8)..]);
                offset += 10 + dataLength;
                if (offset > query.Length) return 512;
                if (record >= preceding && type == 41) return Math.Clamp((int)size, 512, 4096);
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) { }
        return 512;
    }
}
