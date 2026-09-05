using System.Windows;
using System.Windows.Controls;
using BetterDns.Core.Configuration;
using BetterDns.Gui.Localization;
using BetterDns.Gui.ViewModels;

namespace BetterDns.Gui;

public partial class ProviderEditorWindow : Window
{
    public UpstreamEditor Editor { get; }
    public string UseAs { get; set; }
    public IReadOnlyList<LanguageOption> Uses { get; } = [
        new("primary", LocalizationManager.Get("Setup.Primary")),
        new("backup", LocalizationManager.Get("Setup.Backup")),
        new("library", LocalizationManager.Get("Provider.LibraryOnly"))];
    public IReadOnlyList<ProtocolOption> ProtocolOptions { get; } = Enum.GetValues<DnsProtocol>().Select(protocol => new ProtocolOption(protocol, MainViewModel.ProtocolName(protocol))).ToArray();

    public ProviderEditorWindow(UpstreamEditor? existing = null)
    {
        Editor = existing is null ? new() { Endpoint = string.Empty, Name = string.Empty } : UpstreamEditor.From(existing.ToModel());
        if (existing is not null) Editor.Name = existing.DisplayName;
        UseAs = existing is null ? "primary" : "library";
        InitializeComponent();
        WindowAppearance.Attach(this);
        DataContext = this;
        PresetBox.ItemsSource = new[] { LocalizationManager.Get("Provider.Custom"), "Control D", "NextDNS", "HaGeZi", "Cloudflare", "Quad9", "Google" };
        PresetBox.SelectedIndex = 0;
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        var preset = PresetBox.SelectedItem as string;
        if (preset is null || PresetBox.SelectedIndex == 0) return;
        Editor.Name = preset;
        Editor.Protocol = DnsProtocol.Doh3;
        Editor.Endpoint = preset switch
        {
            "Control D" => "https://dns.controld.com/",
            "NextDNS" => "https://dns.nextdns.io/",
            "HaGeZi" => "https://root.hagezi.org/dns-query",
            "Cloudflare" => "https://cloudflare-dns.com/dns-query",
            "Quad9" => "https://dns.quad9.net/dns-query",
            "Google" => "https://dns.google/dns-query",
            _ => string.Empty
        };
        Editor.BootstrapAddresses = string.Empty;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (HasValidationError(this)) { ErrorText.Text = LocalizationManager.Get("Provider.TimeoutInvalid"); return; }
        if (ProviderValidation.Problem(Editor.ToModel()) is { } key) { ErrorText.Text = LocalizationManager.Get(key); return; }
        if (UseAs != "library" && !Editor.Enabled) { ErrorText.Text = LocalizationManager.Get("Setup.DisabledProvider"); return; }
        DialogResult = true;
    }

    private static bool HasValidationError(DependencyObject parent)
    {
        if (Validation.GetHasError(parent)) return true;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (HasValidationError(System.Windows.Media.VisualTreeHelper.GetChild(parent, i))) return true;
        return false;
    }

    public sealed record ProtocolOption(DnsProtocol Code, string DisplayName);
}
