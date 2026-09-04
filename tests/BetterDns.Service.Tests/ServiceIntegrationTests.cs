using BetterDns.Core.Configuration;
using BetterDns.Core.Ipc;
using BetterDns.Core.Routing;
using BetterDns.Service.Configuration;
using BetterDns.Service.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BetterDns.Service.Tests;

public sealed class ServiceIntegrationTests
{
    [Fact]
    public async Task Fresh_service_starts_serves_GUI_saves_config_and_restarts()
    {
        var directory = Directory.CreateTempSubdirectory("BetterDns-test-").FullName;
        var pipe = "BetterDns-test-" + Guid.NewGuid().ToString("N");
        try
        {
            using var host = CreateHost(directory, pipe);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await host.StartAsync(timeout.Token);
            var client = new ControlClient(pipe); // The same client used by the GUI.
            var state = await client.SendAsync<ServiceSnapshot>("getState", false, timeout.Token);
            Assert.False(state.Configuration.Active);
            Assert.Equal(6, state.Configuration.Upstreams.Count);
            Assert.Equal(4, host.Services.GetRequiredService<DnsRouter>().SupportedProtocols.Count);
            var edited = state.Configuration with { DefaultChainId = "controld-nextdns" };
            await client.SendAsync<BetterDnsConfiguration>("saveConfiguration", edited, timeout.Token);
            Assert.Equal("controld-nextdns", (await client.SendAsync<ServiceSnapshot>("getState", false, timeout.Token)).Configuration.DefaultChainId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync<BetterDnsConfiguration>("setActive", true, timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync<BetterDnsConfiguration>("saveConfiguration", edited with { Active = true }, timeout.Token));
            await host.StopAsync(timeout.Token);
            using var restarted = CreateHost(directory, pipe);
            await restarted.StartAsync(timeout.Token);
            Assert.Equal("controld-nextdns", (await client.SendAsync<ServiceSnapshot>("getState", false, timeout.Token)).Configuration.DefaultChainId);
            await restarted.StopAsync(timeout.Token);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Corrupt_configuration_is_retained_and_replaced_with_inactive_defaults()
    {
        var directory = Directory.CreateTempSubdirectory("BetterDns-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "config.json"), "{ corrupt");
            var store = new ConfigurationStore(directory);
            Assert.False(store.Current.Active);
            Assert.Equal("{ corrupt", File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "*.invalid-*"))));
            Assert.False(new ConfigurationStore(directory).Current.Active);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static IHost CreateHost(string directory, string pipe)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddBetterDns(directory, pipe, diagnostic: true);
        return builder.Build();
    }
}
