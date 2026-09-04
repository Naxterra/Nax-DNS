#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallRoot
)

$ErrorActionPreference = 'Stop'
$serviceExe = Join-Path $InstallRoot 'Service\BetterDns.Service.exe'
$service = Get-Service -Name 'BetterDNS' -ErrorAction SilentlyContinue

if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name 'BetterDNS' -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
}

if (Test-Path -LiteralPath $serviceExe) {
    & $serviceExe --restore
    if ($LASTEXITCODE -ne 0) { throw 'BetterDNS could not remove its firewall policy.' }
}

if ($service) {
    & sc.exe delete BetterDNS | Out-Null
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1072) {
        throw 'Could not delete the BetterDNS service registration.'
    }
}
