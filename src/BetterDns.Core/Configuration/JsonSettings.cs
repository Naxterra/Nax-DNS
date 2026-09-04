using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterDns.Core.Configuration;

public static class JsonSettings
{
    public static JsonSerializerOptions Wire { get; } = Create(false);
    public static JsonSerializerOptions File { get; } = Create(true);

    private static JsonSerializerOptions Create(bool indented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = indented,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
