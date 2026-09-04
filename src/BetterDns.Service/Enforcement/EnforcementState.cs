using BetterDns.Service.Ipc;

namespace BetterDns.Service.Enforcement;

public sealed class EnforcementState
{
    private readonly object gate = new();
    private EnforcementSnapshot snapshot = new(false, "Kernel interception driver is starting", null, null, false);

    public EnforcementSnapshot Snapshot()
    {
        lock (gate)
        {
            return snapshot;
        }
    }

    public void Update(bool active, string status, string? lastError = null, bool driverReady = false)
    {
        lock (gate)
        {
            snapshot = new(active, status, lastError, DateTimeOffset.UtcNow, driverReady);
        }
    }
}
