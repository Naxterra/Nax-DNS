using BetterDns.Gui.Services;

namespace BetterDns.Gui.ViewModels;

public sealed partial class MainViewModel
{
    private IWindowsServiceManager serviceManager = null!;
    private WindowsServiceState serviceState;
    private string serviceMessageKey = "Service.Ready", serviceErrorDetails = "";
    public Func<ServiceOperation, bool>? ConfirmServiceOperation { get; set; }
    public string WindowsServiceStatus => L("Service.State." + serviceState);
    public string ServiceMessage => L(serviceMessageKey);
    public string ServiceErrorDetails => serviceErrorDetails;
    public AsyncCommand StartServiceCommand { get; private set; } = null!;
    public AsyncCommand StopServiceCommand { get; private set; } = null!;
    public AsyncCommand InstallServiceCommand { get; private set; } = null!;
    public AsyncCommand UninstallServiceCommand { get; private set; } = null!;
    public AsyncCommand RefreshServiceCommand { get; private set; } = null!;

    private void InitializeServiceManager(IWindowsServiceManager manager)
    {
        serviceManager = manager;
        StartServiceCommand = new(() => ManageServiceAsync(ServiceOperation.Start), () => CanManageService(ServiceOperation.Start));
        StopServiceCommand = new(() => ManageServiceAsync(ServiceOperation.Stop), () => CanManageService(ServiceOperation.Stop));
        InstallServiceCommand = new(() => ManageServiceAsync(ServiceOperation.Install), () => CanManageService(ServiceOperation.Install));
        UninstallServiceCommand = new(() => ManageServiceAsync(ServiceOperation.Uninstall), () => CanManageService(ServiceOperation.Uninstall));
        RefreshServiceCommand = new(() => { RefreshServiceState(); return Task.CompletedTask; }, () => !IsBusy);
    }

    private bool CanManageService(ServiceOperation operation) => !IsBusy && serviceManager.CanManage && operation switch
    {
        ServiceOperation.Start => serviceState == WindowsServiceState.Stopped,
        ServiceOperation.Stop => serviceState == WindowsServiceState.Running,
        ServiceOperation.Install => serviceState == WindowsServiceState.Missing,
        ServiceOperation.Uninstall => serviceState is WindowsServiceState.Running or WindowsServiceState.Stopped,
        _ => false
    };

    public void RefreshServiceState()
    {
        try
        {
            serviceState = serviceManager.GetState();
            if (!serviceManager.CanManage) serviceMessageKey = "Service.PayloadMissing";
        }
        catch (Exception error)
        {
            serviceState = WindowsServiceState.Unknown;
            serviceMessageKey = "Service.StatusFailed";
            serviceErrorDetails = error.Message;
        }
        RaiseServicePresentation();
    }

    private void RaiseServicePresentation()
    {
        Raise(nameof(WindowsServiceStatus)); Raise(nameof(ServiceMessage)); Raise(nameof(ServiceErrorDetails));
        StartServiceCommand?.RaiseCanExecuteChanged(); StopServiceCommand?.RaiseCanExecuteChanged();
        InstallServiceCommand?.RaiseCanExecuteChanged(); UninstallServiceCommand?.RaiseCanExecuteChanged();
        RefreshServiceCommand?.RaiseCanExecuteChanged();
    }

    public async Task ManageServiceAsync(ServiceOperation operation)
    {
        RefreshServiceState();
        if (!CanManageService(operation)) return;
        if (HasUnsavedChanges || HasInputErrors) { serviceMessageKey = "Service.SaveFirst"; RaiseServicePresentation(); return; }
        // No confirmation callback means no authority to change the Windows service.
        if (ConfirmServiceOperation?.Invoke(operation) != true) return;
        IsBusy = true;
        await stateGate.WaitAsync();
        serviceMessageKey = "Service.Working"; serviceErrorDetails = "";
        RaiseServicePresentation();
        try
        {
            await serviceManager.ExecuteAsync(operation);
            serviceMessageKey = "Service.Done." + operation;
        }
        catch (Exception error)
        {
            serviceMessageKey = "Service.Failed";
            serviceErrorDetails = error.Message;
        }
        finally
        {
            stateGate.Release(); IsBusy = false;
            RefreshServiceState();
        }
        await RefreshAsync();
    }
}
