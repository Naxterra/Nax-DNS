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
    // Local-to-local traffic belongs to local sockets and must not be intercepted twice.
    // VPN DNS addresses are remote tunnel peers, so they remain covered by this filter.
    // Replies generated here are QR=1 and cannot enter this interception path again.
    internal const string QueryFilter =
        "outbound and not loopback and udp.DstPort == 53 and udp.PayloadLength >= 12 and udp.Payload[2] < 128";

    private bool CaptureEnabled => configurationStore.Current is { Active: true, Enforcement.Enabled: true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunModeAsync(CaptureEnabled, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Kernel DNS interception driver is unavailable.");
                enforcementState.Update(false, "Kernel interception driver unavailable", error.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        enforcementState.Update(false, "Kernel interception stopped");
    }

    private async Task RunModeAsync(bool active, CancellationToken stoppingToken)
    {
        // A no-match handle verifies driver readiness without diverting or reinjecting any
        // traffic while disabled. This avoids loops with other WFP injection drivers.
        using var divert = new DivertService((DivertFilter)(active ? QueryFilter : "false"),
            DivertLayer.Network, DivertService.HighestPriority, DivertFlags.None,
            runContinuationsAsynchronously: true);
        enforcementState.Update(active,
            active ? $"WinDivert {divert.Version} kernel DNS interception active"
                   : $"WinDivert {divert.Version} driver ready; protection disabled",
            driverReady: true);

        if (!active)
        {
            while (!CaptureEnabled) await Task.Delay(100, stoppingToken).ConfigureAwait(false);
            return;
        }

        using var mode = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = MonitorModeAsync(mode, stoppingToken);
        using var concurrency = new SemaphoreSlim(256, 256);
        var pending = new List<Task>();
        try
        {
            while (!mode.IsCancellationRequested)
            {
                var packet = new byte[ushort.MaxValue];
                var addresses = new DivertAddress[1];
                var (length, count) = await divert.ReceiveAsync(packet, addresses, mode.Token).ConfigureAwait(false);
                if (length <= 0 || count != 1) continue;
                await concurrency.WaitAsync(mode.Token).ConfigureAwait(false);
                pending.RemoveAll(task => task.IsCompleted);
                pending.Add(ProcessWithSlotAsync(divert, packet.AsMemory(0, length), addresses[0], concurrency, mode.Token));
            }
        }
        catch (OperationCanceledException) when (mode.IsCancellationRequested) { }
        finally
        {
            mode.Cancel();
            await monitor.ConfigureAwait(false);
            // Keep the native handle and semaphore alive until every pending query completes.
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private async Task MonitorModeAsync(CancellationTokenSource mode, CancellationToken stoppingToken)
    {
        try
        {
            while (CaptureEnabled && !mode.IsCancellationRequested)
                await Task.Delay(100, mode.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (mode.IsCancellationRequested || stoppingToken.IsCancellationRequested) { }
        finally { mode.Cancel(); }
    }

    private async Task ProcessWithSlotAsync(DivertService divert, ReadOnlyMemory<byte> packet,
        DivertAddress address, SemaphoreSlim concurrency, CancellationToken cancellationToken)
    {
        try
        {
            if (!UdpPacketRewriter.TryGetDnsPayload(packet.Span, out var payload))
                throw new InvalidDataException("Intercepted packet has no complete DNS payload.");
            var query = payload.ToArray();
            var answer = await router.ResolveAsync(query, configurationStore.Current, cancellationToken).ConfigureAwait(false);
            var response = UdpPacketRewriter.CreateResponse(packet.Span, answer);
            // Windows classifies local-to-local packets as outbound, including the reply.
            address.IsOutbound = address.IsLoopback;
            if (!DivertHelper.CalculateChecksums(response, ref address, DivertHelperFlags.None))
                throw new InvalidDataException("Failed to calculate DNS response checksums.");
            await divert.SendAsync(response, new[] { address }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { logger.LogWarning(error, "An intercepted DNS query could not be answered."); }
        finally { concurrency.Release(); }
    }
}
