using System.Net;
using System.Text.Json.Serialization;
using BetterDns.Core.Transports;

namespace BetterDns.Core.Configuration;

public enum DnsProtocol
{
    Doh,
    Doh3,
    Dot,
    Doq
}

public enum DomainMatchKind
{
    Exact,
    Suffix,
    Wildcard,
    Regex
}

public enum RuleAction
{
    Route,
    Block,
    Allow
}

public sealed record UpstreamDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DnsProtocol Protocol { get; init; }
    public required string Endpoint { get; init; }
    public IReadOnlyList<string> BootstrapAddresses { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public int TimeoutMilliseconds { get; init; } = 3500;

    [JsonIgnore]
    public string HostName
    {
        get
        {
            if (Protocol is DnsProtocol.Doh or DnsProtocol.Doh3)
            {
                return Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
                    ? uri.DnsSafeHost
                    : string.Empty;
            }

            return EndpointParser.Parse(Endpoint, 853).Host;
        }
    }

    [JsonIgnore]
    public IReadOnlyList<IPAddress> ParsedBootstrapAddresses => BootstrapAddresses
        .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
        .Where(static address => address is not null)
        .Cast<IPAddress>()
        .ToArray();
}

public sealed record FailoverChain
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> UpstreamIds { get; init; } = [];
    public int FailureThreshold { get; init; } = 2;
    public int CooldownSeconds { get; init; } = 30;
}

public sealed record DnsRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required string Pattern { get; init; }
    public DomainMatchKind MatchKind { get; init; } = DomainMatchKind.Suffix;
    public RuleAction Action { get; init; } = RuleAction.Route;
    public string? ChainId { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record EnforcementConfiguration
{
    public bool Enabled { get; init; } = true;
    public bool BlockPlaintextDns { get; init; } = true;
    public int WatchdogSeconds { get; init; } = 15;
}

public sealed record BetterDnsConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public bool Active { get; init; }
    public string DefaultChainId { get; init; } = "privacy-default";
    public IReadOnlyList<UpstreamDefinition> Upstreams { get; init; } = [];
    public IReadOnlyList<FailoverChain> Chains { get; init; } = [];
    public IReadOnlyList<DnsRule> Rules { get; init; } = [];
    public EnforcementConfiguration Enforcement { get; init; } = new();
}

public sealed record UpstreamStatus(
    string Id,
    string Name,
    bool Available,
    int ConsecutiveFailures,
    DateTimeOffset? CircuitOpenUntil,
    double? LastLatencyMilliseconds,
    string? LastError);

public sealed record QueryLogEntry(
    DateTimeOffset Timestamp,
    string Domain,
    string? RuleName,
    string? UpstreamName,
    string Result,
    double ElapsedMilliseconds,
    string? UpstreamId = null,
    string? ChainId = null,
    DnsProtocol? Protocol = null);
