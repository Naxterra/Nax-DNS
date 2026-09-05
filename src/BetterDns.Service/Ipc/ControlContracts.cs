using System.Text.Json;
using BetterDns.Core.Configuration;

namespace BetterDns.Service.Ipc;

public sealed record ControlRequest(string Command, JsonElement Payload);

public sealed record ControlResponse(bool Success, object? Data = null, string? Error = null, string? ErrorCode = null);
