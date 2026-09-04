using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDns.Core.Configuration;

namespace BetterDns.Service.Configuration;

public sealed class ConfigurationStore
{
    private readonly object gate = new();
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private BetterDnsConfiguration current;

    public ConfigurationStore()
    {
        Directory.CreateDirectory(DataDirectory);
        current = Load();
    }

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BetterDNS");

    public static string ConfigurationPath => Path.Combine(DataDirectory, "config.json");

    public BetterDnsConfiguration Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public JsonSerializerOptions JsonOptions => jsonOptions;

    public void Save(BetterDnsConfiguration configuration)
    {
        Validate(configuration);
        lock (gate)
        {
            var temporaryPath = ConfigurationPath + ".new";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, jsonOptions));
            File.Move(temporaryPath, ConfigurationPath, overwrite: true);
            current = configuration;
        }
    }

    private BetterDnsConfiguration Load()
    {
        if (!File.Exists(ConfigurationPath))
        {
            var defaults = DefaultConfiguration.Create();
            File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(defaults, jsonOptions));
            return defaults;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<BetterDnsConfiguration>(
                File.ReadAllText(ConfigurationPath),
                jsonOptions) ?? throw new InvalidDataException("Configuration is empty.");
            Validate(parsed);
            return parsed;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            var invalidPath = ConfigurationPath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(ConfigurationPath, invalidPath, overwrite: false);
            var defaults = DefaultConfiguration.Create();
            File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(defaults, jsonOptions));
            return defaults;
        }
    }

    private static void Validate(BetterDnsConfiguration configuration)
    {
        if (configuration.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported configuration schema {configuration.SchemaVersion}.");
        }

        if (configuration.Upstreams.Select(static value => value.Id).Distinct(StringComparer.Ordinal).Count() != configuration.Upstreams.Count)
        {
            throw new InvalidDataException("Upstream IDs must be unique.");
        }

        if (configuration.Chains.Select(static value => value.Id).Distinct(StringComparer.Ordinal).Count() != configuration.Chains.Count)
        {
            throw new InvalidDataException("Chain IDs must be unique.");
        }

        if (!configuration.Chains.Any(value => value.Id == configuration.DefaultChainId))
        {
            throw new InvalidDataException("The default failover chain does not exist.");
        }

        var upstreamIds = configuration.Upstreams.Select(static value => value.Id).ToHashSet(StringComparer.Ordinal);
        if (configuration.Chains.SelectMany(static value => value.UpstreamIds).Any(id => !upstreamIds.Contains(id)))
        {
            throw new InvalidDataException("A failover chain references an unknown upstream.");
        }
    }
}
