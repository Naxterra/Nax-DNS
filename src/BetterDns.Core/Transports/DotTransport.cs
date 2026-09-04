using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using BetterDns.Core.Configuration;

namespace BetterDns.Core.Transports;

public sealed class DotTransport : IDnsTransport
{
    public DnsProtocol Protocol => DnsProtocol.Dot;

    public async Task<byte[]> QueryAsync(
        UpstreamDefinition upstream,
        ReadOnlyMemory<byte> query,
        CancellationToken cancellationToken)
    {
        var (host, port) = EndpointParser.Parse(upstream.Endpoint, 853);
        using var socket = await BootstrapConnector
            .ConnectTcpAsync(upstream, host, port, cancellationToken)
            .ConfigureAwait(false);
        await using var network = new NetworkStream(socket, ownsSocket: false);
        await using var tls = new SslStream(network, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online
            },
            cancellationToken).ConfigureAwait(false);

        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)query.Length));
        await tls.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await tls.WriteAsync(query, cancellationToken).ConfigureAwait(false);
        await tls.FlushAsync(cancellationToken).ConfigureAwait(false);

        await tls.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        var responseLength = BinaryPrimitives.ReadUInt16BigEndian(length);
        var response = new byte[responseLength];
        await tls.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }
}
