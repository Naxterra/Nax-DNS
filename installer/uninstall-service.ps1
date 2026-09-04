#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallRoot,

    [string]$LogPath = (Join-Path $env:TEMP 'BetterDNS-installer.log')
)

$ErrorActionPreference = 'Stop'
$serviceExe = Join-Path $InstallRoot 'Service\BetterDns.Service.exe'
$service = Get-Service -Name 'BetterDNS' -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null
Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) BetterDNS service removal started."

trap {
    Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) UNINSTALL FAILED: $(($_ | Out-String).Trim())"
    exit 1
}

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

Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) BetterDNS service removal completed."
