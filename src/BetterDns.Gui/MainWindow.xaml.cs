using BetterDns.Gui.ViewModels;

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
        Closing += (_, args) =>
        {
            if (viewModel.IsBusy) { args.Cancel = true; return; }
            if (viewModel.HasUnsavedChanges && System.Windows.MessageBox.Show(this,
                Localization.LocalizationManager.Get("Setup.CloseDraft"), "BetterDNS",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes) args.Cancel = true;
        };
        WindowAppearance.Attach(this);
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

}
