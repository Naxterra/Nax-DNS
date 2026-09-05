using BetterDns.Core.Ipc;

namespace BetterDns.Service.LocalDns;

public sealed class LocalDnsState
{
    private LocalDnsSnapshot current = new(false, [], "starting");
    public LocalDnsSnapshot Snapshot() => Volatile.Read(ref current);
    public void Update(bool ready, IReadOnlyList<string> endpoints, string? errorCode = null)
        => Volatile.Write(ref current, new(ready, endpoints, errorCode));
}
