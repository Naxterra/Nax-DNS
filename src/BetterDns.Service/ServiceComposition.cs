using BetterDns.Core.Routing;
using BetterDns.Service.Configuration;
using BetterDns.Service.Enforcement;
using BetterDns.Service.Ipc;
using BetterDns.Service.Kernel;

namespace BetterDns.Service;

public static class ServiceComposition
{
    public static void AddBetterDns(this IServiceCollection services, string? directory = null,
        string pipeName = ControlPipeWorker.PipeName, bool diagnostic = false)
    {
        services.AddSingleton(new ControlPipeOptions(pipeName, diagnostic));
        services.AddSingleton(_ => new ConfigurationStore(directory ?? ConfigurationStore.DataDirectory));
        services.AddSingleton<UpstreamHealthTracker>();
        services.AddSingleton<QueryLog>();
        // Explicit factory: DI otherwise chooses the overload with an empty IEnumerable<IDnsTransport>.
        services.AddSingleton(provider => new DnsRouter(
            provider.GetRequiredService<UpstreamHealthTracker>(), provider.GetRequiredService<QueryLog>()));
        services.AddSingleton<AdapterDnsManager>();
        services.AddSingleton<FirewallLeakGuard>();
        services.AddSingleton<EnforcementState>();
        if (!diagnostic)
        {
            services.AddHostedService<KernelDnsInterceptorWorker>();
            services.AddHostedService<EnforcementWorker>();
            services.AddHostedService<ResolverHealthWorker>();
        }
        services.AddHostedService<ControlPipeWorker>();
    }
}
