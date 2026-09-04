using BetterDns.Core.Configuration;

namespace BetterDns.Gui.ViewModels;

public sealed class UpstreamEditor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New resolver";
    public DnsProtocol Protocol { get; set; } = DnsProtocol.Doh3;
    public string Endpoint { get; set; } = "https://example.net/dns-query";
    public string BootstrapAddresses { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int TimeoutMilliseconds { get; set; } = 3500;

    public static UpstreamEditor From(UpstreamDefinition value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        Protocol = value.Protocol,
        Endpoint = value.Endpoint,
        BootstrapAddresses = string.Join(", ", value.BootstrapAddresses),
        Enabled = value.Enabled,
        TimeoutMilliseconds = value.TimeoutMilliseconds
    };

    public UpstreamDefinition ToModel() => new()
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        Protocol = Protocol,
        Endpoint = Endpoint.Trim(),
        BootstrapAddresses = BootstrapAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Enabled = Enabled,
        TimeoutMilliseconds = TimeoutMilliseconds
    };
}

public sealed class ChainEditor
{
    public string Id { get; set; } = "new-chain";
    public string Name { get; set; } = "New failover chain";
    public string UpstreamIds { get; set; } = string.Empty;
    public int FailureThreshold { get; set; } = 2;
    public int CooldownSeconds { get; set; } = 30;

    public static ChainEditor From(FailoverChain value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        UpstreamIds = string.Join(", ", value.UpstreamIds),
        FailureThreshold = value.FailureThreshold,
        CooldownSeconds = value.CooldownSeconds
    };

    public FailoverChain ToModel() => new()
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        UpstreamIds = UpstreamIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        FailureThreshold = FailureThreshold,
        CooldownSeconds = CooldownSeconds
    };
}

public sealed class RuleEditor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New rule";
    public string Pattern { get; set; } = "example.com";
    public DomainMatchKind MatchKind { get; set; } = DomainMatchKind.Suffix;
    public RuleAction Action { get; set; } = RuleAction.Route;
    public string? ChainId { get; set; }
    public bool Enabled { get; set; } = true;

    public static RuleEditor From(DnsRule value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        Pattern = value.Pattern,
        MatchKind = value.MatchKind,
        Action = value.Action,
        ChainId = value.ChainId,
        Enabled = value.Enabled
    };

    public DnsRule ToModel() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        Pattern = Pattern.Trim(),
        MatchKind = MatchKind,
        Action = Action,
        ChainId = string.IsNullOrWhiteSpace(ChainId) ? null : ChainId.Trim(),
        Enabled = Enabled
    };
}

public sealed record UpstreamStatusView(string Name, bool Available, string LatencyText, string? LastError);
public sealed record QueryView(string TimeText, string Domain, string? UpstreamName, string Result);
