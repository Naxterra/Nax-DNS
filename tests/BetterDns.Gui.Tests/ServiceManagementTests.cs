using System.IO;
using BetterDns.Core.Ipc;
using BetterDns.Gui.Localization;
using BetterDns.Gui.Services;
using BetterDns.Gui.ViewModels;
using Xunit;

namespace BetterDns.Gui.Tests;

public sealed class ServiceManagementTests
{
    [Theory]
    [InlineData(WindowsServiceState.Stopped, ServiceOperation.Start, WindowsServiceState.Running)]
    [InlineData(WindowsServiceState.Running, ServiceOperation.Stop, WindowsServiceState.Stopped)]
    [InlineData(WindowsServiceState.Missing, ServiceOperation.Install, WindowsServiceState.Running)]
    [InlineData(WindowsServiceState.Running, ServiceOperation.Uninstall, WindowsServiceState.Missing)]
    [InlineData(WindowsServiceState.Stopped, ServiceOperation.Uninstall, WindowsServiceState.Missing)]
    public async Task Actions_work_without_IPC_and_refresh_actual_state(WindowsServiceState before, ServiceOperation action, WindowsServiceState after)
    {
        var manager = new FakeServiceManager { State = before };
        using var vm = new MainViewModel(new OfflineClient(), manager);
        vm.ConfirmServiceOperation = requested => requested == action;
        await vm.ManageServiceAsync(action);
        Assert.False(vm.IsConnected);
        Assert.Equal([action], manager.Operations);
        Assert.Equal(after, manager.State);
        Assert.Equal(LocalizationManager.Get("Service.State." + after), vm.WindowsServiceStatus);
        Assert.Equal(LocalizationManager.Get("Service.Done." + action), vm.ServiceMessage);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Missing_or_declined_confirmation_never_mutates_service(bool? confirmed)
    {
        var manager = new FakeServiceManager();
        using var vm = new MainViewModel(new OfflineClient(), manager);
        if (confirmed.HasValue) vm.ConfirmServiceOperation = _ => confirmed.Value;
        await vm.ManageServiceAsync(ServiceOperation.Uninstall);
        Assert.Empty(manager.Operations);
    }

    [Fact]
    public async Task Failed_operation_keeps_controls_usable_and_exposes_diagnostics()
    {
        var manager = new FakeServiceManager { FailOperation = true };
        using var vm = new MainViewModel(new OfflineClient(), manager);
        vm.ConfirmServiceOperation = _ => true;
        await vm.ManageServiceAsync(ServiceOperation.Stop);
        Assert.Equal(LocalizationManager.Get("Service.Failed"), vm.ServiceMessage);
        Assert.Contains("test failure", vm.ServiceErrorDetails);
        Assert.False(vm.IsBusy);
        Assert.True(vm.StopServiceCommand.CanExecute(null));
    }

    [Fact]
    public async Task Missing_payload_and_invalid_draft_prevent_actions()
    {
        var manager = new FakeServiceManager { CanManage = false };
        using var vm = new MainViewModel(new OfflineClient(), manager);
        vm.ConfirmServiceOperation = _ => throw new Exception("Should not ask for confirmation.");
        await vm.ManageServiceAsync(ServiceOperation.Stop);
        Assert.Empty(manager.Operations);
        Assert.False(vm.StopServiceCommand.CanExecute(null));
        manager.CanManage = true;
        vm.HasInputErrors = true;
        await vm.ManageServiceAsync(ServiceOperation.Stop);
        Assert.Empty(manager.Operations);
        Assert.Equal(LocalizationManager.Get("Service.SaveFirst"), vm.ServiceMessage);
    }

    [Fact]
    public async Task Unsaved_provider_is_not_lost_or_saved_by_service_action()
    {
        var manager = new FakeServiceManager();
        using var vm = new MainViewModel(new OfflineClient(), manager);
        vm.Upstreams.Add(new UpstreamEditor { Id = "draft", Name = "Private draft" });
        Assert.True(vm.HasUnsavedChanges);
        vm.ConfirmServiceOperation = _ => throw new Exception("Should not ask for confirmation.");
        await vm.ManageServiceAsync(ServiceOperation.Uninstall);
        Assert.Empty(manager.Operations);
        Assert.Equal("Private draft", vm.Upstreams.Single().Name);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Stale_command_state_is_rechecked_before_confirmation()
    {
        var manager = new FakeServiceManager { State = WindowsServiceState.Stopped };
        using var vm = new MainViewModel(new OfflineClient(), manager);
        vm.RefreshServiceState();
        Assert.True(vm.StartServiceCommand.CanExecute(null));
        manager.State = WindowsServiceState.Running;
        vm.ConfirmServiceOperation = _ => throw new Exception("Should not ask for confirmation.");
        await vm.ManageServiceAsync(ServiceOperation.Start);
        Assert.Empty(manager.Operations);
        Assert.False(vm.StartServiceCommand.CanExecute(null));
    }

    private sealed class OfflineClient : IControlClient
    {
        public Task<T> SendAsync<T>(string command, object? payload, CancellationToken cancellationToken = default)
        {
            Assert.Equal("getState", command); // No config writes or activation through IPC.
            throw new IOException("Offline for test.");
        }
    }
}

internal sealed class FakeServiceManager : IWindowsServiceManager
{
    public bool CanManage { get; set; } = true;
    public WindowsServiceState State { get; set; } = WindowsServiceState.Running;
    public bool FailOperation { get; set; }
    public List<ServiceOperation> Operations { get; } = [];
    public WindowsServiceState GetState() => State;
    public Task ExecuteAsync(ServiceOperation operation)
    {
        Operations.Add(operation);
        if (FailOperation) throw new IOException("test failure; log path");
        State = operation switch {
            ServiceOperation.Stop => WindowsServiceState.Stopped,
            ServiceOperation.Uninstall => WindowsServiceState.Missing,
            _ => WindowsServiceState.Running
        };
        return Task.CompletedTask;
    }
}
