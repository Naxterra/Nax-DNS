using System.Xml.Linq;

namespace BetterDns.Core.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void English_and_German_resources_have_identical_nonempty_keys()
    {
        var english = Load("Strings.en.xaml");
        var german = Load("Strings.de.xaml");

        Assert.True(english.Count >= 60, "The UI localization catalog unexpectedly lost entries.");
        Assert.Equal(english.Keys.Order(), german.Keys.Order());
        Assert.All(english, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), $"English resource {entry.Key} is empty."));
        Assert.All(german, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), $"German resource {entry.Key} is empty."));
        Assert.NotEqual(english["Tab.Dashboard"], german["Tab.Dashboard"]);
    }

    private static IReadOnlyDictionary<string, string> Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Localization", fileName);
        var document = XDocument.Load(path);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return document.Root!
            .Elements()
            .Where(element => element.Attribute(xaml + "Key") is not null)
            .ToDictionary(
                element => element.Attribute(xaml + "Key")!.Value,
                element => element.Value,
                StringComparer.Ordinal);
    }
}
