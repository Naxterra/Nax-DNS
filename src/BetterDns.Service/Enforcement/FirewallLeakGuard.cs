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
        const string script = "Get-NetFirewallRule -Group 'BetterDNS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue";
        return PowerShellRunner.RunAsync(script, cancellationToken);
    }
}
