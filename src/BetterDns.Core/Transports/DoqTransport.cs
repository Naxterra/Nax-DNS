using System.Buffers.Binary;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;

namespace BetterDns.Core.Transports;

public sealed class DoqTransport : IDnsTransport
{
    private static readonly SslApplicationProtocol DoqProtocol = new("doq");

    public DnsProtocol Protocol => DnsProtocol.Doq;

    public async Task<byte[]> QueryAsync(
        UpstreamDefinition upstream,
        ReadOnlyMemory<byte> query,
        CancellationToken cancellationToken)
    {
        if (!QuicConnection.IsSupported)
        {
            throw new PlatformNotSupportedException("DNS-over-QUIC requires MsQuic support.");
        }

        var originalId = DnsWire.GetId(query.Span);
        var doqQuery = DnsWire.WithId(query.Span, 0);
        var (host, port) = EndpointParser.Parse(upstream.Endpoint, 853);
        var addresses = await BootstrapConnector.GetAddressesAsync(upstream, host, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (var address in addresses.OrderBy(static value => value.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1))
        {
            try
            {
                await using var connection = await QuicConnection.ConnectAsync(
                    new QuicClientConnectionOptions
                    {
                        RemoteEndPoint = new IPEndPoint(address, port),
                        DefaultCloseErrorCode = 0,
                        DefaultStreamErrorCode = 0,
                        ClientAuthenticationOptions = new SslClientAuthenticationOptions
                        {
                            TargetHost = host,
                            ApplicationProtocols = [DoqProtocol]
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                await using var stream = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional,
                    cancellationToken).ConfigureAwait(false);

                var length = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)doqQuery.Length));
                await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(doqQuery, cancellationToken).ConfigureAwait(false);
                stream.CompleteWrites();

                await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
                var responseLength = BinaryPrimitives.ReadUInt16BigEndian(length);
                var response = new byte[responseLength];
                await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
                if (DnsWire.GetId(response) != 0) throw new InvalidDataException("DoQ response ID must be zero.");
                return DnsWire.WithId(response, originalId);
            }
            catch (Exception error) when (error is QuicException or AuthenticationException or IOException)
            {
                lastError = error;
            }
        }

        throw new IOException($"Unable to establish a DoQ connection to {host}:{port}.", lastError);
    }
}
