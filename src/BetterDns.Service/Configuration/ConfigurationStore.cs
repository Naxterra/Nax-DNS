using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDns.Core.Configuration;

namespace BetterDns.Service.Configuration;

public sealed class ConfigurationStore
{
    private readonly object gate = new();
    private readonly JsonSerializerOptions jsonOptions = JsonSettings.File;
    private readonly string configurationPath;
    private BetterDnsConfiguration current;

    public ConfigurationStore() : this(DataDirectory) { }

    public ConfigurationStore(string directory)
    {
        Directory.CreateDirectory(directory);
        configurationPath = Path.Combine(directory, "config.json");
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
            var temporaryPath = configurationPath + ".new";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, jsonOptions));
            File.Move(temporaryPath, configurationPath, overwrite: true);
            current = configuration;
        }
    }

    private BetterDnsConfiguration Load()
    {
        if (!File.Exists(configurationPath))
        {
            var defaults = DefaultConfiguration.Create();
            Save(defaults);
            return defaults;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<BetterDnsConfiguration>(
                File.ReadAllText(configurationPath),
                jsonOptions) ?? throw new InvalidDataException("Configuration is empty.");
            Validate(parsed);
            return parsed;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            var invalidPath = configurationPath + ".invalid-" + Guid.NewGuid().ToString("N");
            File.Move(configurationPath, invalidPath, overwrite: false);
            var defaults = DefaultConfiguration.Create();
            Save(defaults);
            return defaults;
        }
    }

    private static void Validate(BetterDnsConfiguration configuration)
    {
        if (configuration.Upstreams is null || configuration.Chains is null || configuration.Rules is null || configuration.Enforcement is null)
            throw new InvalidDataException("Configuration collections and enforcement settings cannot be null.");
        foreach (var upstream in configuration.Upstreams)
        {
            if (upstream is null || string.IsNullOrWhiteSpace(upstream.Id) || string.IsNullOrWhiteSpace(upstream.Name) || string.IsNullOrWhiteSpace(upstream.Endpoint) ||
                !Enum.IsDefined(upstream.Protocol) || upstream.BootstrapAddresses is null || upstream.TimeoutMilliseconds is < 250 or > 30000)
                throw new InvalidDataException("Each resolver needs an ID, name, valid transport, endpoint and a timeout between 250 and 30000 ms.");
            if (upstream.Protocol is DnsProtocol.Doh or DnsProtocol.Doh3 &&
                (!Uri.TryCreate(upstream.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https"))
                throw new InvalidDataException("DoH and DoH3 require HTTPS endpoints.");
            if (upstream.BootstrapAddresses.Any(value => !System.Net.IPAddress.TryParse(value, out _)))
                throw new InvalidDataException("Bootstrap addresses must be IP literals.");
        }
        if (configuration.Chains.Any(chain => chain is null || string.IsNullOrWhiteSpace(chain.Id) || chain.UpstreamIds is null || chain.UpstreamIds.Count == 0))
            throw new InvalidDataException("Each failover chain needs an ID and at least one resolver.");
        foreach (var rule in configuration.Rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Pattern) || !Enum.IsDefined(rule.MatchKind) || !Enum.IsDefined(rule.Action))
                throw new InvalidDataException("Each rule needs a pattern, match mode and action.");
            if (rule.Action == RuleAction.Route && !configuration.Chains.Any(chain => chain.Id == rule.ChainId))
                throw new InvalidDataException("A routing rule references an unknown failover chain.");
            if (rule.MatchKind == DomainMatchKind.Regex)
            {
                try { _ = new System.Text.RegularExpressions.Regex(rule.Pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50)); }
                catch (ArgumentException error) { throw new InvalidDataException("Invalid rule regular expression.", error); }
            }
        }
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
