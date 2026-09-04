using System.Text.Json;
using BetterDns.Core.Configuration;

namespace BetterDns.Service.Ipc;

public sealed record ControlRequest(string Command, JsonElement Payload);

public sealed record ControlResponse(bool Success, object? Data = null, string? Error = null);

public sealed record ServiceSnapshot(
    string Version,
    BetterDnsConfiguration Configuration,
    IReadOnlyList<UpstreamStatus> Upstreams,
    IReadOnlyList<QueryLogEntry> Queries,
    EnforcementSnapshot Enforcement);

public sealed record EnforcementSnapshot(bool Active, string Status, string? LastError, DateTimeOffset? LastChecked, bool DriverReady);
