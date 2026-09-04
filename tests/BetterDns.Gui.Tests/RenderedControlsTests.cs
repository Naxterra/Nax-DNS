using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

                var dialog = new ProviderEditorWindow();
                var dialogContent = (FrameworkElement)dialog.Content;
                Layout(dialogContent, 640, 650);
                var protocol = Descendants<ComboBox>(dialogContent).Single(combo => combo.SelectedValue is DnsProtocol);
                AssertLabel(protocol, "DoH3");
                var role = Descendants<ComboBox>(dialogContent).Single(combo => Equals(combo.SelectedValue, "primary"));
                AssertLabel(role, LocalizationManager.Get("Setup.Primary"));
                Render(dialogContent, "provider-" + language);
                dialog.Close();
                window.Close();
            }
        }
        finally { app.Shutdown(); }
    }

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
