using BetterDns.Core.Routing;
using BetterDns.Service.Configuration;
using BetterDns.Service.Enforcement;
using BetterDns.Service.Ipc;
using BetterDns.Service.Kernel;
using BetterDns.Service;
using BetterDns.Core.Ipc;

if (args.Contains("--check-health", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var expected = typeof(ServiceComposition).Assembly.GetName().Version!.ToString(3);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var state = await new ControlClient().SendAsync<ServiceSnapshot>("getState", false, timeout.Token);
            if (state.Version != expected) throw new InvalidDataException($"Expected service {expected}; received {state.Version}.");
            if (!state.Enforcement.DriverReady)
                throw new InvalidOperationException(state.Enforcement.LastError ?? state.Enforcement.Status);
            if (state.LocalDns?.Ready != true)
                throw new InvalidOperationException("Local UDP/TCP DNS listener is not ready: " + state.LocalDns?.ErrorCode);
            if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(2));
        }
        Console.WriteLine("Service configuration, IPC, kernel and local UDP/TCP DNS readiness checks passed.");
    }
    catch (Exception error)
    {
        Console.WriteLine("Health check failed: " + error.Message);
        Environment.ExitCode = 1;
    }
    return;
}

WinDivertNativeLoader.Configure();

if (args.Contains("--restore", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var store = new ConfigurationStore();
        await new FirewallLeakGuard().DisableAsync(CancellationToken.None).ConfigureAwait(false);
        await new AdapterDnsManager().RestoreAsync(CancellationToken.None).ConfigureAwait(false);
        store.Save(store.Current with { Active = false });
    }
    catch (Exception error)
    {
        Console.WriteLine("Cleanup failed: " + error.Message);
        Environment.ExitCode = 1;
    }
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "BetterDNS");
builder.Services.AddBetterDns();

await builder.Build().RunAsync().ConfigureAwait(false);
