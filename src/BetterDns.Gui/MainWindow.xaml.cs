using BetterDns.Gui.ViewModels;
using System.Runtime.InteropServices;

namespace BetterDns.Gui;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel viewModel;

    public MainWindow() : this(new MainViewModel()) { }

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.EditProviderRequested += OnEditProvider;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Closing += (_, args) =>
        {
            if (viewModel.IsBusy) { args.Cancel = true; return; }
            if (viewModel.HasUnsavedChanges && System.Windows.MessageBox.Show(this,
                Localization.LocalizationManager.Get("Setup.CloseDraft"), "BetterDNS",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes) args.Cancel = true;
        };
        SourceInitialized += OnSourceInitialized;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        await viewModel.InitializeAsync().ConfigureAwait(true);
    }

    private void OnClosed(object? sender, EventArgs e) => viewModel.Dispose();

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

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var enabled = 1;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
