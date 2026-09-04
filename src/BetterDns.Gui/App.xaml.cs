using BetterDns.Gui.Localization;

namespace BetterDns.Gui;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        LocalizationManager.Initialize();
        base.OnStartup(e);
    }
}
