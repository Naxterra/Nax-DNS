using BetterDns.Core.Configuration;

namespace BetterDns.Core.Ipc;

public sealed record ServiceSnapshot(
    string Version,
    BetterDnsConfiguration Configuration,
    IReadOnlyList<UpstreamStatus> Upstreams,
    IReadOnlyList<QueryLogEntry> Queries,
    EnforcementSnapshot Enforcement);

public sealed record EnforcementSnapshot(bool Active, string Status, string? LastError, DateTimeOffset? LastChecked, bool DriverReady);
