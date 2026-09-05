using BetterDns.Core.Configuration;

namespace BetterDns.Gui.ViewModels;

public sealed class UpstreamEditor : ObservableObject
{
    private string id = Guid.NewGuid().ToString("N");
    public string Id { get => id; set => Set(ref id, value); }
    private string name = "New resolver";
    public string Name { get => name; set { if (Set(ref name, value)) Raise(nameof(DisplayName)); } }
    private DnsProtocol protocol = DnsProtocol.Doh3;
    public DnsProtocol Protocol { get => protocol; set { if (Set(ref protocol, value)) { Raise(nameof(ProtocolDisplayName)); Raise(nameof(BootstrapStatus)); } } }
    private string endpoint = "https://example.net/dns-query";
    public string Endpoint { get => endpoint; set { if (Set(ref endpoint, value)) { Raise(nameof(DisplayName)); Raise(nameof(BootstrapStatus)); } } }
    private string bootstrapAddresses = string.Empty;
    public string BootstrapAddresses { get => bootstrapAddresses; set { if (Set(ref bootstrapAddresses, value)) Raise(nameof(BootstrapStatus)); } }
    private bool enabled = true;
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    private int timeoutMilliseconds = 3500;
    public int TimeoutMilliseconds { get => timeoutMilliseconds; set => Set(ref timeoutMilliseconds, value); }

    public string DisplayName => ProviderNames.Display(Name, Endpoint);
    public string ProtocolDisplayName => MainViewModel.ProtocolName(Protocol);
    public string BootstrapStatus
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(BootstrapAddresses)) return Localization.LocalizationManager.Get("Provider.BootstrapManual");
            try { return Localization.LocalizationManager.Get(KnownResolverBootstrap.ForHost(ToModel().HostName).Count > 0 ? "Provider.BootstrapAutomatic" : "Provider.BootstrapCustom"); }
            catch (Exception error) when (error is ArgumentException or FormatException) { return Localization.LocalizationManager.Get("Provider.BootstrapCustom"); }
        }
    }

    public void RefreshDisplayName() => Raise(nameof(DisplayName));

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

public sealed class ChainEditor : ObservableObject
{
    private string id = "new-chain";
    public string Id { get => id; set => Set(ref id, value); }
    private string name = "New failover chain";
    public string Name { get => name; set { if (Set(ref name, value)) { Raise(nameof(DisplayName)); Raise(nameof(EditableName)); } } }
    public string DisplayName => Name is "Privacy defaults" or "Default DNS" ? Localization.LocalizationManager.Get("Chain.DefaultName") : Name;
    public string EditableName { get => DisplayName; set => Name = value; }
    public void RefreshDisplayName() { Raise(nameof(DisplayName)); Raise(nameof(EditableName)); }
    private string upstreamIds = string.Empty;
    public string UpstreamIds { get => upstreamIds; set => Set(ref upstreamIds, value); }
    private int failureThreshold = 2;
    public int FailureThreshold { get => failureThreshold; set => Set(ref failureThreshold, value); }
    private int cooldownSeconds = 30;
    public int CooldownSeconds { get => cooldownSeconds; set => Set(ref cooldownSeconds, value); }

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

public sealed class RuleEditor : ObservableObject
{
    private string id = Guid.NewGuid().ToString("N");
    public string Id { get => id; set => Set(ref id, value); }
    private string name = "New rule";
    public string Name { get => name; set => Set(ref name, value); }
    private string pattern = "example.com";
    public string Pattern { get => pattern; set => Set(ref pattern, value); }
    private DomainMatchKind matchKind = DomainMatchKind.Suffix;
    public DomainMatchKind MatchKind { get => matchKind; set => Set(ref matchKind, value); }
    private RuleAction action = RuleAction.Route;
    public RuleAction Action { get => action; set => Set(ref action, value); }
    private string? chainId;
    public string? ChainId { get => chainId; set => Set(ref chainId, value); }
    private bool enabled = true;
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }

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

public sealed record UpstreamStatusView(string Name, string StateText, string LatencyText, string? LastError,
    string Protocol = "", string CheckedText = "—", string SourceText = "");
public sealed record QueryView(string TimeText, string Domain, string? UpstreamName, string Result, string Details = "");
public sealed record SelectionOption<T>(T Value, string DisplayName);
