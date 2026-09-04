using System.Net;
using System.Net.Sockets;
using BetterDns.Core.Configuration;

namespace BetterDns.Core.Transports;

internal static class BootstrapConnector
{
    public static async Task<Socket> ConnectTcpAsync(
        UpstreamDefinition upstream,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var addresses = await GetAddressesAsync(upstream, host, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (var address in PreferIpv4(addresses))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException)
            {
                lastError = error;
                socket.Dispose();
                if (error is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new IOException($"Unable to connect to {host}:{port}.", lastError);
    }

    public static async Task<IReadOnlyList<IPAddress>> GetAddressesAsync(
        UpstreamDefinition upstream,
        string host,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        var configured = upstream.ParsedBootstrapAddresses;
        if (configured.Count > 0)
        {
            return configured;
        }

        return await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<IPAddress> PreferIpv4(IEnumerable<IPAddress> addresses) => addresses
        .OrderBy(static address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);
}
