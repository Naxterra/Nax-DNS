using BetterDns.Core.Configuration;

namespace BetterDns.Core.Transports;

public interface IDnsTransport
{
    DnsProtocol Protocol { get; }

    Task<byte[]> QueryAsync(
        UpstreamDefinition upstream,
        ReadOnlyMemory<byte> query,
        CancellationToken cancellationToken);
}
