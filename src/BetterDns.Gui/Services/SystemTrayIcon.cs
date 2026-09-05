using System.Drawing;
using System.Windows.Forms;
using BetterDns.Gui.Localization;

namespace BetterDns.Gui.Services;

public interface ISystemTrayIcon : IDisposable
{
    event EventHandler? OpenRequested;
    event EventHandler? ExitRequested;
    bool Visible { get; set; }
}

public sealed class SystemTrayIcon : ISystemTrayIcon
{
    private readonly NotifyIcon icon;
    private readonly Icon ownedIcon;
    private readonly ToolStripMenuItem openItem;
    private readonly ToolStripMenuItem exitItem;

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    public SystemTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/BetterDNS;component/Assets/BetterDNS.ico"))
            ?? throw new InvalidOperationException("The BetterDNS tray icon resource is missing.");
        using (resource.Stream)
        using (var source = new Icon(resource.Stream))
            ownedIcon = (Icon)source.Clone();

        openItem = new ToolStripMenuItem();
        exitItem = new ToolStripMenuItem();
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        var menu = new ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        menu.Opening += (_, _) => UpdateLabels();
        icon = new NotifyIcon
        {
            Icon = ownedIcon,
            Text = "BetterDNS",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left) OpenRequested?.Invoke(this, EventArgs.Empty);
        };
        UpdateLabels();
    }

    public bool Visible { get => icon.Visible; set => icon.Visible = value; }

    private void UpdateLabels()
    {
        openItem.Text = LocalizationManager.Get("Tray.Open");
        exitItem.Text = LocalizationManager.Get("Tray.Exit");
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.ContextMenuStrip?.Dispose();
        icon.Dispose();
        ownedIcon.Dispose();
    }
}
