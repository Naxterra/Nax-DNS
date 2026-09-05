using BetterDns.Core.Configuration;

namespace BetterDns.Core.Ipc;

public sealed record ServiceSnapshot(
    string Version,
    BetterDnsConfiguration Configuration,
    IReadOnlyList<UpstreamStatus> Upstreams,
    IReadOnlyList<QueryLogEntry> Queries,
    EnforcementSnapshot Enforcement,
    IReadOnlyList<ResolverProbeResult>? ProbeResults = null,
    LocalDnsSnapshot? LocalDns = null);

public sealed record LocalDnsSnapshot(bool Ready, IReadOnlyList<string> Endpoints, string? ErrorCode = null);

public sealed record EnforcementSnapshot(bool Active, string Status, string? LastError, DateTimeOffset? LastChecked, bool DriverReady);
