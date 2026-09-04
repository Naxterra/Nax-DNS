using BetterDns.Core.Dns;
using BetterDns.Core.Routing;
using BetterDns.Service.Configuration;
using BetterDns.Service.Enforcement;
using Divert.Windows;

namespace BetterDns.Service.Kernel;

public sealed class KernelDnsInterceptorWorker(
    ConfigurationStore configurationStore,
    DnsRouter router,
    EnforcementState enforcementState,
    ILogger<KernelDnsInterceptorWorker> logger) : BackgroundService
{
    private const int PacketBufferSize = ushort.MaxValue;
    private readonly SemaphoreSlim concurrency = new(256, 256);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunInterceptionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Kernel DNS interception driver is unavailable.");
                enforcementState.Update(false, "Kernel interception driver unavailable", error.Message, driverReady: false);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public override void Dispose()
    {
        concurrency.Dispose();
        base.Dispose();
    }

    private async Task RunInterceptionAsync(CancellationToken cancellationToken)
    {
        DivertFilter filter = "outbound and !loopback and !impostor and udp.DstPort == 53";
        using var divert = new DivertService(
            filter,
            DivertLayer.Network,
            DivertService.HighestPriority,
            DivertFlags.None,
            runContinuationsAsynchronously: true);
        enforcementState.Update(
            configurationStore.Current.Active,
            $"WinDivert {divert.Version} kernel interception driver ready",
            driverReady: true);
        logger.LogInformation("WinDivert {Version} is intercepting outbound UDP DNS at kernel priority {Priority}.", divert.Version, DivertService.HighestPriority);

        while (!cancellationToken.IsCancellationRequested)
        {
            var buffer = new byte[PacketBufferSize];
            var addresses = new DivertAddress[1];
            var (packetLength, addressLength) = await divert
                .ReceiveAsync(buffer, addresses, cancellationToken)
                .ConfigureAwait(false);
            if (packetLength <= 0 || addressLength != 1)
            {
                continue;
            }

            var packet = buffer.AsMemory(0, packetLength).ToArray();
            var address = addresses[0];
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            _ = ProcessPacketAsync(divert, packet, address, cancellationToken).ContinueWith(
                _ => concurrency.Release(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ProcessPacketAsync(
        DivertService divert,
        byte[] packet,
        DivertAddress address,
        CancellationToken cancellationToken)
    {
        try
        {
            var configuration = configurationStore.Current;
            if (!configuration.Active || !configuration.Enforcement.Enabled)
            {
                await divert.SendAsync(packet, new[] { address }, cancellationToken).ConfigureAwait(false);
                enforcementState.Update(false, $"WinDivert {divert.Version} driver ready; protection disabled", driverReady: true);
                return;
            }

            if (!UdpPacketRewriter.TryGetDnsPayload(packet, out var querySpan))
            {
                throw new InvalidDataException("Intercepted packet did not contain a complete DNS/UDP payload.");
            }

            var query = querySpan.ToArray();
            var dnsResponse = await router.ResolveAsync(query, configuration, cancellationToken).ConfigureAwait(false);
            var responsePacket = UdpPacketRewriter.CreateResponse(packet, dnsResponse);
            address.IsOutbound = false;
            if (!DivertHelper.CalculateChecksums(responsePacket, ref address, DivertHelperFlags.None))
            {
                throw new InvalidDataException("WinDivert could not calculate response packet checksums.");
            }

            await divert.SendAsync(responsePacket, new[] { address }, cancellationToken).ConfigureAwait(false);
            enforcementState.Update(true, $"WinDivert {divert.Version} kernel DNS interception active", driverReady: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "An intercepted DNS packet was dropped fail-closed.");
        }
    }
}
