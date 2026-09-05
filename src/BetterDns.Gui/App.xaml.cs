using BetterDns.Gui.Localization;

namespace BetterDns.Gui;

public partial class App : System.Windows.Application
{
    internal static Services.PrivilegedControlSession? ControlSession { get; private set; }
    private Services.GuiInstanceCoordinator? instanceCoordinator;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        LogStartup(e.Args.Length > 0 && e.Args[0] == "--control-broker" ? "control helper started" : "standard UI started");
        LocalizationManager.Initialize();
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--control-broker")
        {
            try
            {
                var result = e.Args.Length == 3 && int.TryParse(e.Args[2], out var parentId)
                    ? await Services.PrivilegedControlSession.RunBrokerAsync(e.Args[1], parentId)
                    : 87;
                Shutdown(result);
            }
            catch (Exception error) { LogStartup("control helper failed: " + error.Message); Shutdown(1); }
            return;
        }
        instanceCoordinator = new Services.GuiInstanceCoordinator();
        var requestedCommand = e.Args.Contains("--exit-for-update", StringComparer.OrdinalIgnoreCase)
            ? Services.GuiInstanceCoordinator.ExitForUpdateCommand
            : Services.GuiInstanceCoordinator.ActivateCommand;
        if (!instanceCoordinator.TryOwnInstance())
        {
            await instanceCoordinator.SendAsync(requestedCommand);
            Shutdown();
            return;
        }
        if (requestedCommand == Services.GuiInstanceCoordinator.ExitForUpdateCommand)
        {
            Shutdown();
            return;
        }
        instanceCoordinator.StartListening(command => Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is not MainWindow window) return;
            if (command == Services.GuiInstanceCoordinator.ExitForUpdateCommand) window.RequestExitForUpdate();
            else if (command == Services.GuiInstanceCoordinator.ActivateCommand) window.RestoreFromTray();
        }));
        try
        {
            ControlSession = await Services.PrivilegedControlSession.StartAsync();
            MainWindow = new MainWindow();
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            MainWindow.Show();
            LogStartup("standard UI shown");
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            Shutdown(); // User cancelled the administrative control session.
        }
        catch (Exception error)
        {
            LogStartup("UI startup failed: " + error.Message);
            System.Windows.MessageBox.Show(error.Message, "BetterDNS", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        ControlSession?.Dispose();
        ControlSession = null;
        instanceCoordinator?.Dispose();
        instanceCoordinator = null;
        base.OnExit(e);
    }

    internal static void LogStartup(string message)
    {
        try
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterDNS");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "ui-startup.log"),
                $"{DateTimeOffset.Now:O} pid {Environment.ProcessId}: {message}{Environment.NewLine}");
        }
        catch { }
    }
}
