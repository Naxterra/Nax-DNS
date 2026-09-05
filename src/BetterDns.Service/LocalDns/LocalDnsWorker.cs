using System.Net.Sockets;
using BetterDns.Core.Dns;
using BetterDns.Core.Routing;
using BetterDns.Service.Configuration;

namespace BetterDns.Service.LocalDns;

public sealed class LocalDnsWorker(ConfigurationStore configuration, DnsRouter router,
    LocalDnsState state, ILogger<LocalDnsWorker> logger) : BackgroundService
{
    // Routing off must mean no upstream queries, including clients explicitly using localhost.
    public Task<byte[]> ResolveAsync(ReadOnlyMemory<byte> query, CancellationToken token)
    {
        var snapshot = configuration.Current;
        return snapshot.Active && snapshot.Enforcement.Enabled
            ? router.ResolveAsync(query, snapshot, token)
            : Task.FromResult(DnsWire.CreateErrorResponse(query.Span, 5));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var server = new LoopbackDnsServer(ResolveAsync);
                    await server.RunAsync(endpoints => state.Update(true, endpoints), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    var code = error is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
                        ? "port-unavailable" : "listener-failed";
                    state.Update(false, [], code);
                    logger.LogError(error, "Local DNS listener unavailable ({Code}); no other DNS service will be stopped.", code);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { state.Update(false, [], "stopped"); }
    }
}
