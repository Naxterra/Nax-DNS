using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace BetterDns.Gui.Localization;

public static class LocalizationManager
{
    private const string English = "en";
    private const string German = "de";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterDNS",
        "ui-settings.json");

    public static string CurrentLanguage { get; private set; } = English;

    public static void Initialize()
    {
        var savedLanguage = LoadLanguage();
        var detectedLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
            German,
            StringComparison.OrdinalIgnoreCase)
            ? German
            : English;
        Apply(savedLanguage ?? detectedLanguage, persist: false);
    }

    public static void Apply(string language, bool persist = true)
    {
        var normalized = language.Equals(German, StringComparison.OrdinalIgnoreCase) ? German : English;
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("Strings.", StringComparison.OrdinalIgnoreCase) == true);
        if (existing is not null)
        {
            dictionaries.Remove(existing);
        }

        dictionaries.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"/BetterDNS;component/Resources/Strings.{normalized}.xaml", UriKind.Relative)
        });
        CurrentLanguage = normalized;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(normalized);

        if (persist)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new UiSettings(normalized)));
        }
    }

    public static string Get(string key, params object?[] arguments)
    {
        var template = System.Windows.Application.Current?.TryFindResource(key) as string ?? key;
        return arguments.Length == 0
            ? template
            : string.Format(CultureInfo.CurrentUICulture, template, arguments);
    }

    private static string? LoadLanguage()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath))?.Language
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record UiSettings(string Language);
}

public sealed record LanguageOption(string Code, string DisplayName);
