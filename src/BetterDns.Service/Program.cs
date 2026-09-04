using BetterDns.Core.Routing;
using BetterDns.Service.Configuration;
using BetterDns.Service.Enforcement;
using BetterDns.Service.Ipc;
using BetterDns.Service.Kernel;

if (args.Contains("--restore", StringComparer.OrdinalIgnoreCase))
{
    var store = new ConfigurationStore();
    await new FirewallLeakGuard().DisableAsync(CancellationToken.None).ConfigureAwait(false);
    await new AdapterDnsManager().RestoreAsync(CancellationToken.None).ConfigureAwait(false);
    store.Save(store.Current with { Active = false });
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "BetterDNS");
builder.Services.AddSingleton<ConfigurationStore>();
builder.Services.AddSingleton<UpstreamHealthTracker>();
builder.Services.AddSingleton<QueryLog>();
builder.Services.AddSingleton<DnsRouter>();
builder.Services.AddSingleton<AdapterDnsManager>();
builder.Services.AddSingleton<FirewallLeakGuard>();
builder.Services.AddSingleton<EnforcementState>();
builder.Services.AddHostedService<KernelDnsInterceptorWorker>();
builder.Services.AddHostedService<EnforcementWorker>();
builder.Services.AddHostedService<ControlPipeWorker>();

await builder.Build().RunAsync().ConfigureAwait(false);
