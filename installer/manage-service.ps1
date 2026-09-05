#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallRoot,
    [Parameter(Mandatory)][ValidateSet('Start', 'Stop', 'Install', 'Uninstall')][string]$Action,
    [string]$LogPath = (Join-Path $env:TEMP 'BetterDNS-service.log')
)
$ErrorActionPreference = 'Stop'
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$serviceExe = Join-Path $InstallRoot 'Service\BetterDns.Service.exe'
try {
    if (-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)) { throw 'Service payload missing. Run the BetterDNS installer first.' }
    # Never operate on an unrelated registration with the same name.
    $registration = Get-CimInstance Win32_Service -Filter "Name='BetterDNS'"
    if ($registration -and $registration.PathName.Trim('"') -ne $serviceExe) {
        throw 'BetterDNS is registered to another installation. Use that installation to manage it.'
    }
    Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) Service action: $Action"
    switch ($Action) {
        'Install' {
            & (Join-Path $PSScriptRoot 'install-service.ps1') -InstallRoot $InstallRoot -LogPath $LogPath
        }
        'Uninstall' {
            & (Join-Path $PSScriptRoot 'uninstall-service.ps1') -InstallRoot $InstallRoot -LogPath $LogPath
        }
        'Start' {
            Start-Service -Name BetterDNS
            $service = Get-Service -Name BetterDNS
            try { $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(20)) }
            finally { $service.Dispose() }
            & $serviceExe --check-health
            if ($LASTEXITCODE -ne 0) { throw 'Service started, but the health check failed. DNS protection is not confirmed.' }
        }
        'Stop' {
            $service = Get-Service -Name BetterDNS
            try {
                if ($service.Status -ne 'Stopped') { Stop-Service -Name BetterDNS }
                $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
            } finally { $service.Dispose() }
            & $serviceExe --restore
            if ($LASTEXITCODE -ne 0) { throw 'Service stopped, but restoring BetterDNS network policy failed.' }
        }
    }
    Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) Service action completed."
    exit 0
} catch {
    Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) FAILED: $($_.Exception.Message)"
    exit 1
}
