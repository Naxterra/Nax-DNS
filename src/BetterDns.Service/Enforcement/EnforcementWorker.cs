using BetterDns.Service.Configuration;

namespace BetterDns.Service.Enforcement;

public sealed class EnforcementWorker(
    ConfigurationStore configurationStore,
    AdapterDnsManager adapterDnsManager,
    FirewallLeakGuard firewallLeakGuard,
    ILogger<EnforcementWorker> logger) : BackgroundService
{
    private bool wasActive;
    private bool legacyAdapterStateRestored;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var configuration = configurationStore.Current;
            var shouldProtect = configuration.Active && configuration.Enforcement.Enabled;

            try
            {
                if (!legacyAdapterStateRestored)
                {
                    await adapterDnsManager.RestoreAsync(stoppingToken).ConfigureAwait(false);
                    legacyAdapterStateRestored = true;
                }

                if (shouldProtect)
                {
                    if (configuration.Enforcement.BlockPlaintextDns)
                    {
                        await firewallLeakGuard.EnsureEnabledAsync(stoppingToken).ConfigureAwait(false);
                    }

                    wasActive = true;
                }
                else if (wasActive)
                {
                    await firewallLeakGuard.DisableAsync(stoppingToken).ConfigureAwait(false);
                    wasActive = false;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "DNS enforcement pass failed.");
            }

            var delay = TimeSpan.FromSeconds(Math.Clamp(configuration.Enforcement.WatchdogSeconds, 5, 300));
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
