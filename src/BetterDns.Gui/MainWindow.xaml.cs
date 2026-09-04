using BetterDns.Gui.ViewModels;

namespace BetterDns.Gui;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        await viewModel.InitializeAsync().ConfigureAwait(true);
    }

    private void OnClosed(object? sender, EventArgs e) => viewModel.Dispose();
}
