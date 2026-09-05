using BetterDns.Gui.ViewModels;
using BetterDns.Gui.Services;
using System.ComponentModel;

namespace BetterDns.Gui;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel viewModel;
    private readonly ISystemTrayIcon trayIcon;
    private bool exitRequested;
    private bool sessionEnding;
    private bool hidingToTray;

    public MainWindow() : this(new MainViewModel(), new SystemTrayIcon()) { }

    public MainWindow(MainViewModel viewModel) : this(viewModel, new SystemTrayIcon()) { }

    public MainWindow(MainViewModel viewModel, ISystemTrayIcon trayIcon)
    {
        this.viewModel = viewModel;
        this.trayIcon = trayIcon;
        InitializeComponent();
        DataContext = viewModel;
        var inputErrors = new HashSet<System.Windows.Controls.ValidationError>();
        System.Windows.Controls.Validation.AddErrorHandler(this, (_, args) =>
        {
            if (args.Action == System.Windows.Controls.ValidationErrorEventAction.Added) inputErrors.Add(args.Error);
            else inputErrors.Remove(args.Error);
            viewModel.HasInputErrors = inputErrors.Count > 0;
        });
        viewModel.EditProviderRequested += OnEditProvider;
        viewModel.ConfirmServiceOperation = operation => System.Windows.MessageBox.Show(this,
            Localization.LocalizationManager.Get("Service.Confirm." + operation), "BetterDNS",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        trayIcon.OpenRequested += OnTrayOpen;
        trayIcon.ExitRequested += OnTrayExit;
        if (System.Windows.Application.Current is { } application) application.SessionEnding += OnSessionEnding;
        WindowAppearance.Attach(this);
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        await viewModel.InitializeAsync().ConfigureAwait(true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current is { } application) application.SessionEnding -= OnSessionEnding;
        trayIcon.OpenRequested -= OnTrayOpen;
        trayIcon.ExitRequested -= OnTrayExit;
        trayIcon.Dispose();
        viewModel.Dispose();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == System.Windows.WindowState.Minimized) HideToTray();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!exitRequested && !sessionEnding)
        {
            args.Cancel = true;
            HideToTray();
            return;
        }
        if (sessionEnding) return;
        if (viewModel.IsBusy)
        {
            args.Cancel = true;
            exitRequested = false;
            RestoreFromTray();
            return;
        }
        if (viewModel.HasUnsavedChanges && System.Windows.MessageBox.Show(this,
            Localization.LocalizationManager.Get("Setup.CloseDraft"), "BetterDNS",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
        {
            args.Cancel = true;
            exitRequested = false;
        }
    }

    private void HideToTray()
    {
        if (hidingToTray) return;
        hidingToTray = true;
        try
        {
            ShowInTaskbar = false;
            Hide();
            WindowState = System.Windows.WindowState.Normal;
            trayIcon.Visible = true;
        }
        finally { hidingToTray = false; }
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    private void OnTrayOpen(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RestoreFromTray);

    private void OnTrayExit(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        exitRequested = true;
        Close();
    });

    private void OnSessionEnding(object sender, System.Windows.SessionEndingCancelEventArgs e)
    {
        sessionEnding = true;
        exitRequested = true;
    }

    private void OnEditProvider(UpstreamEditor? existing)
    {
        viewModel.IsProviderEditorOpen = true;
        try
        {
            var dialog = new ProviderEditorWindow(existing) { Owner = this };
            if (dialog.ShowDialog() == true) viewModel.AcceptProvider(dialog.Editor, dialog.UseAs);
        }
        finally { viewModel.IsProviderEditorOpen = false; }
    }

    private void OnCopySelectedDomains(object sender, System.Windows.RoutedEventArgs e) => CopySelectedDomains();

    private void OnActivityGridPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.C ||
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0 ||
            e.OriginalSource is System.Windows.Controls.TextBox) return;
        e.Handled = CopySelectedDomains();
    }

    private bool CopySelectedDomains()
    {
        var selected = ActivityQueryGrid.SelectedItems.Cast<QueryView>().ToHashSet();
        if (selected.Count == 0 && ActivityQueryGrid.SelectedItem is QueryView current) selected.Add(current);
        var text = FormatDomains(ActivityQueryGrid.Items.Cast<QueryView>().Where(selected.Contains));
        if (text.Length == 0) return false;
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    public static string FormatDomains(IEnumerable<QueryView> queries) => string.Join(
        Environment.NewLine,
        queries.Select(query => query.Domain.Trim().TrimEnd('.'))
            .Where(domain => domain.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));

}
