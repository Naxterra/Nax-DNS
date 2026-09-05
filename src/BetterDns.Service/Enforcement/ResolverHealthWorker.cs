using BetterDns.Core.Routing;
using BetterDns.Service.Configuration;

namespace BetterDns.Service.Enforcement;

public sealed class ResolverHealthWorker(ConfigurationStore configuration, DnsRouter router, ILogger<ResolverHealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await router.ConfirmPendingFailuresAsync(configuration.Current, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Resolver failure confirmation did not finish."); }
        }
    }
}
