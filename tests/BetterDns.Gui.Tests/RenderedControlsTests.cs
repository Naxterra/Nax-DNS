using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using BetterDns.Core.Configuration;
using BetterDns.Core.Ipc;
using BetterDns.Gui.Localization;
using BetterDns.Gui.ViewModels;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BetterDns.Gui.Tests;

public sealed class RenderedControlsTests
{
    [Fact]
    public void Actual_windows_render_provider_names_protocols_and_roles_in_both_languages()
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            try { RenderAndCheck(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Off-screen WPF render timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RenderAndCheck()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            var theme = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "UiSource", "App.xaml"));
            var resources = theme.Descendants().First(element => element.Name.LocalName == "ResourceDictionary");
            resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
            app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString().Replace("\"Resources/", "\"/BetterDNS;component/Resources/"));
            foreach (var language in new[] { "de", "en" })
            {
                LocalizationManager.Apply(language, persist: false);
                using var model = new MainViewModel(new RenderClient());
                model.RefreshAsync().GetAwaiter().GetResult();
                var window = new MainWindow(model);
                var content = (FrameworkElement)window.Content;
                Layout(content, 1244, 860);
                var primary = Descendants<ComboBox>(content).Single(combo => ReferenceEquals(combo.SelectedItem, model.PrimaryProvider));
                var backup = Descendants<ComboBox>(content).Single(combo => ReferenceEquals(combo.SelectedItem, model.BackupProvider));
                AssertLabel(primary, "HaGeZi Full Protection (Germany)");
                AssertLabel(backup, "Cloudflare Public DNS");
                Render(content, "home-" + language);

                primary.SetCurrentValue(Selector.SelectedItemProperty, model.Upstreams.Single(provider => provider.Id == "google-public"));
                Layout(content, 1244, 860);
                Assert.Equal("google-public", model.PrimaryProvider?.Id);
                AssertLabel(primary, "Google Public DNS");
                model.SaveAsync().GetAwaiter().GetResult();
                Layout(content, 1244, 860);
                Assert.False(model.HasUnsavedChanges);

                var extras = Descendants<Expander>(content).Single();
                extras.IsExpanded = true;
                Layout(content, 1244, 860);
                var extraList = Descendants<ListBox>(content).Single();
                Assert.Contains(Descendants<TextBlock>(extraList), text => text.Text == "Quad9 Secure");
                Assert.DoesNotContain(Descendants<TextBlock>(extraList), text => text.Text.Contains("UpstreamEditor", StringComparison.Ordinal));

                model.SelectedTabIndex = 1;
                Layout(content, 1244, 860);
                var providerGrid = Descendants<DataGrid>(content).Single();
                Assert.True(providerGrid.IsReadOnly); // Only the explicit toggle control edits a row.
                var checkbox = Descendants<CheckBox>(providerGrid).First(check => check.DataContext is UpstreamEditor);
                Assert.True(checkbox.IsEnabled);
                Assert.True(checkbox.IsHitTestVisible);
                var server = (UpstreamEditor)checkbox.DataContext;
                var serverId = server.Id;
                var toggle = (IToggleProvider)new CheckBoxAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle);
                toggle.Toggle();
                Layout(content, 1244, 860);
                Assert.False(server.Enabled);
                Assert.True(model.HasUnsavedChanges);
                Render(content, "servers-" + language);
                model.SaveAsync().GetAwaiter().GetResult();
                Assert.False(model.Upstreams.Single(item => item.Id == serverId).Enabled);
                Layout(content, 1244, 860);
                var savedCheckbox = Descendants<CheckBox>(providerGrid).First(check => check.DataContext is UpstreamEditor item && item.Id == serverId);
                ((IToggleProvider)new CheckBoxAutomationPeer(savedCheckbox).GetPattern(PatternInterface.Toggle)).Toggle();
                model.SaveAsync().GetAwaiter().GetResult();
                Assert.True(model.Upstreams.Single(item => item.Id == serverId).Enabled);
                Layout(content, 1244, 860);

                var displayedNames = Descendants<TextBlock>(providerGrid).Select(text => text.Text).ToArray();
                Assert.DoesNotContain(displayedNames, name => name.Contains("replace with", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain("Doh3", displayedNames);
                Assert.Contains("DoH3", displayedNames);

                model.SelectedTabIndex = 3;
                Layout(content, 1244, 860);
                var selectedTabs = Descendants<TabItem>(content).Where(tab => tab.IsSelected).ToArray();
                Assert.All(selectedTabs, tab => Assert.Equal("#FF43E6C3", tab.Foreground.ToString()));
                Render(content, "advanced-" + language);

                var dialog = new ProviderEditorWindow();
                Assert.IsType<LinearGradientBrush>(dialog.Background);
                var dialogContent = (FrameworkElement)dialog.Content;
                Layout(dialogContent, 640, 650);
                var protocol = Descendants<ComboBox>(dialogContent).Single(combo => combo.SelectedValue is DnsProtocol);
                AssertLabel(protocol, "DoH3");
                var role = Descendants<ComboBox>(dialogContent).Single(combo => Equals(combo.SelectedValue, "primary"));
                AssertLabel(role, LocalizationManager.Get("Setup.Primary"));
                Render(dialogContent, "provider-" + language);
                Descendants<Expander>(dialogContent).Single().IsExpanded = true;
                Layout(dialogContent, 640, 650);
                Render(dialogContent, "provider-expanded-" + language);
                var handle = new WindowInteropHelper(dialog).EnsureHandle();
                var dark = 0;
                if (DwmGetWindowAttribute(handle, 20, ref dark, sizeof(int)) == 0) Assert.Equal(1, dark);
                dialog.Close();
                window.Close();
            }
        }
        finally { app.Shutdown(); }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    private static void AssertLabel(DependencyObject control, string expected)
    {
        var labels = Descendants<TextBlock>(control).Select(text => text.Text).ToArray();
        Assert.Contains(expected, labels);
        Assert.DoesNotContain(labels, label => label.Contains("BetterDns.", StringComparison.Ordinal));
    }

    private static void Layout(FrameworkElement content, double width, double height)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        content.UpdateLayout();
    }

    private static void Render(FrameworkElement content, string name)
    {
        var output = Path.Combine(AppContext.BaseDirectory, "TestResults", "ui");
        Directory.CreateDirectory(output);
        var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        image.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var item in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return item;
    }

    private sealed class RenderClient : IControlClient
    {
        private BetterDnsConfiguration configuration = DefaultConfiguration.Create();
        public Task<T> SendAsync<T>(string command, object? payload, CancellationToken cancellationToken = default)
        {
            if (command == "getState") return Task.FromResult((T)(object)new ServiceSnapshot("render test", configuration, [], [], new(false, "ready", null, null, true)));
            if (command == "saveConfiguration") { configuration = (BetterDnsConfiguration)payload!; return Task.FromResult((T)(object)configuration); }
            throw new InvalidOperationException("Off-screen rendering never enables real DNS.");
        }
    }
}
