namespace BetterDns.Service.Enforcement;

public sealed class FirewallLeakGuard
{
    public Task EnsureEnabledAsync(CancellationToken cancellationToken)
    {
        const string script = "$name = 'BetterDNS Leak Guard TCP 53'; $nonLoopback = @('0.0.0.0-127.0.0.0','127.0.0.2-255.255.255.255','::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff'); if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) { New-NetFirewallRule -DisplayName $name -Group 'BetterDNS' -Direction Outbound -Action Block -Protocol TCP -RemotePort 53 -RemoteAddress $nonLoopback -Profile Any | Out-Null }";
        return PowerShellRunner.RunAsync(script, cancellationToken);
    }

    public Task DisableAsync(CancellationToken cancellationToken)
    {
        // Enumerate then filter: querying a missing group directly makes PowerShell exit 1,
        // even with SilentlyContinue. No matching rules is a successful cleanup.
        const string script = "$ErrorActionPreference = 'Stop'; Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.Group -eq 'BetterDNS' } | Remove-NetFirewallRule -ErrorAction Stop; exit 0";
        return PowerShellRunner.RunAsync(script, cancellationToken);
    }
}
