using BetterDns.Core.Configuration;
using BetterDns.Core.Ipc;
using BetterDns.Gui.ViewModels;
using BetterDns.Gui.Localization;
using Xunit;

namespace BetterDns.Gui.Tests;

public sealed class SetupWorkflowTests
{
    [Fact]
    public async Task Editing_failover_timing_does_not_reset_unsaved_primary_selection()
    {
        using var vm = new MainViewModel(new FakeClient());
        await vm.RefreshAsync();
        vm.PrimaryProvider = vm.Upstreams.Single(provider => provider.Id == "google-public");
        vm.Chains.First(chain => chain.Id == vm.DefaultChainId).CooldownSeconds = 45;
        Assert.Equal("google-public", vm.PrimaryProvider?.Id);
        Assert.Equal("google-public", vm.BuildConfiguration(false).Chains.First(chain => chain.Id == vm.DefaultChainId).UpstreamIds[0]);
    }

    [Fact]
    public async Task Loads_existing_route_without_editing_or_losing_extra_backups()
    {
        var client = new FakeClient();
        using var vm = new MainViewModel(client);
        await vm.RefreshAsync();
        Assert.Equal("hagezi-root", vm.PrimaryProvider?.Id);
        Assert.Equal("cloudflare-security", vm.BackupProvider?.Id);
        Assert.Equal(["quad9-secure", "google-public"], vm.AdditionalFallbacks.Select(provider => provider.Id));
        Assert.False(vm.HasUnsavedChanges);
        Assert.All(vm.UpstreamStatuses, provider => Assert.Equal(LocalizationManager.Get("Health.Untested"), provider.StateText));
    }

    [Fact]
    public async Task Save_and_enable_persists_visible_choices_before_activation()
    {
        var client = new FakeClient();
        using var vm = new MainViewModel(client);
        await vm.RefreshAsync();
        vm.PrimaryProvider = vm.Upstreams.Single(provider => provider.Id == "google-public");
        await vm.ToggleProtectionAsync();
        Assert.Equal(["saveConfiguration", "setActive"], client.Writes);
        Assert.Equal(["google-public", "cloudflare-security", "quad9-secure", "hagezi-root"], client.Config.Chains[0].UpstreamIds);
        Assert.True(vm.IsActive);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Save_failure_does_not_activate_and_error_survives_refresh()
    {
        var client = new FakeClient { FailSave = true };
        using var vm = new MainViewModel(client);
        await vm.RefreshAsync();
        vm.PrimaryProvider = vm.Upstreams[1];
        await vm.ToggleProtectionAsync();
        await vm.RefreshAsync();
        Assert.False(client.Config.Active);
        Assert.Equal(["saveConfiguration"], client.Writes);
        Assert.Equal("save failed", vm.StatusMessage);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Activation_failure_keeps_saved_draft_off_and_explains_failure()
    {
        var client = new FakeClient { FailActivation = true };
        using var vm = new MainViewModel(client);
        await vm.RefreshAsync();
        await vm.ToggleProtectionAsync();
        Assert.False(vm.IsActive);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal("activation failed", vm.StatusMessage);
    }

    [Fact]
    public async Task Turning_off_does_not_save_invalid_edits()
    {
        var client = new FakeClient { Config = DefaultConfiguration.Create() with { Active = true } };
        using var vm = new MainViewModel(client);
        await vm.RefreshAsync();
        vm.PrimaryProvider = null;
        await vm.ToggleProtectionAsync();
        Assert.Equal(["setActive"], client.Writes);
        Assert.False(client.Config.Active);
        Assert.Null(vm.PrimaryProvider);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Refresh_does_not_overwrite_or_mislabel_an_unsaved_provider_edit()
    {
        var client = new FakeClient();
        using var vm = new MainViewModel(client);
        await vm.RefreshAsync();
        vm.Upstreams[0].Name = "My DNS";
        await vm.RefreshAsync();
        Assert.Equal("My DNS", vm.Upstreams[0].Name);
        Assert.Equal(LocalizationManager.Get("Setup.Unsaved"), vm.StatusMessage);
    }

    [Fact]
    public async Task External_changes_are_not_overwritten_by_a_stale_window()
    {
        var client = new FakeClient();
        using var vm = new MainViewModel(client);
        await vm.RefreshAsync();
        vm.Upstreams[0].Name = "Draft";
        client.Config = client.Config with { DefaultChainId = "controld-nextdns" };
        await vm.SaveAsync();
        Assert.Empty(client.Writes);
        Assert.Equal(LocalizationManager.Get("Setup.Conflict"), vm.StatusMessage);
        Assert.Equal("controld-nextdns", client.Config.DefaultChainId);
    }

    [Fact]
    public async Task Guided_add_assigns_new_provider_as_primary_without_changing_saved_config()
    {
        var client = new FakeClient();
        using var vm = new MainViewModel(client);
        await vm.RefreshAsync();
        var provider = UpstreamEditor.From(client.Config.Upstreams[0] with { Id = "my-profile", Name = "Personal DNS" });
        vm.AcceptProvider(provider, "primary");
        Assert.Same(provider, vm.PrimaryProvider);
        Assert.Equal(7, vm.Upstreams.Count);
        Assert.Equal(6, client.Config.Upstreams.Count);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(0, vm.SelectedTabIndex);
    }

    [Fact]
    public async Task Live_status_identifies_backup_protocol_and_ignores_bootstrap_and_other_routes()
    {
        var client = new FakeClient { Config = DefaultConfiguration.Create() with { Active = true } };
        client.Queries = [
            new(DateTimeOffset.UtcNow, "bootstrap.example", "Bootstrap", "Local bootstrap", "NOERROR", 1),
            new(DateTimeOffset.UtcNow, "work.example", "Work", "Control D", "NOERROR", 2, "controld", "controld-nextdns", DnsProtocol.Doh3),
            new(DateTimeOffset.UtcNow, "example.com", null, "Cloudflare", "NOERROR", 33, "cloudflare-security", "privacy-default", DnsProtocol.Doh3)];
        using var vm = new MainViewModel(client);
        await vm.RefreshAsync();
        Assert.Equal("Cloudflare · DoH3", vm.LiveResolverText);
        Assert.True(vm.LastResponseUsedBackup);
    }

    [Fact]
    public async Task Off_state_never_presents_old_query_as_current_routing()
    {
        var client = new FakeClient();
        client.Queries = [new(DateTimeOffset.UtcNow, "example.com", null, "Old provider", "NOERROR", 4, "hagezi-root", "privacy-default", DnsProtocol.Doh3)];
        using var vm = new MainViewModel(client);
        await vm.RefreshAsync();
        Assert.Equal(LocalizationManager.Get("Setup.RoutingOff"), vm.LiveResolverText);
    }

    [Fact]
    public async Task Required_profile_and_bootstrap_are_checked_before_saving()
    {
        var client = new FakeClient();
        using var vm = new MainViewModel(client);
        await vm.RefreshAsync();
        vm.PrimaryProvider = vm.Upstreams.Single(provider => provider.Id == "nextdns");
        await vm.ToggleProtectionAsync();
        Assert.Empty(client.Writes);
        Assert.False(client.Config.Active);
    }

    private sealed class FakeClient : IControlClient
    {
        public BetterDnsConfiguration Config { get; set; } = DefaultConfiguration.Create();
        public List<string> Writes { get; } = [];
        public bool FailSave { get; init; }
        public bool FailActivation { get; init; }
        public IReadOnlyList<QueryLogEntry> Queries { get; set; } = [];
        public Task<T> SendAsync<T>(string command, object? payload, CancellationToken cancellationToken = default)
        {
            if (command == "getState") return Task.FromResult((T)(object)new ServiceSnapshot("test", Config,
                Config.Upstreams.Select(provider => new UpstreamStatus(provider.Id, provider.Name, true, 0, null, null, null)).ToArray(), Queries,
                new(Config.Active, "ready", null, DateTimeOffset.UtcNow, true)));
            Writes.Add(command);
            if (command == "saveConfiguration")
            {
                if (FailSave) throw new InvalidOperationException("save failed");
                Config = (BetterDnsConfiguration)payload!;
            }
            else if (command == "setActive")
            {
                if (FailActivation) throw new InvalidOperationException("activation failed");
                Config = Config with { Active = (bool)payload! };
            }
            else throw new InvalidOperationException(command);
            return Task.FromResult((T)(object)Config);
        }
    }
}
