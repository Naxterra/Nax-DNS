#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [switch]$RemoveConfiguration
)

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:ProgramFiles 'BetterDNS'
$serviceExe = Join-Path $installRoot 'Service\BetterDns.Service.exe'
$programDataRoot = Join-Path $env:ProgramData 'BetterDNS'

$service = Get-Service -Name 'BetterDNS' -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name 'BetterDNS' -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
}

if (Test-Path -LiteralPath $serviceExe) {
    & $serviceExe --restore
    if ($LASTEXITCODE -ne 0) { throw 'BetterDNS could not restore adapter and firewall state.' }
}

if ($service) {
    & sc.exe delete BetterDNS | Out-Null
}

Remove-Item -LiteralPath (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\BetterDNS') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'BetterDNS.lnk') -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $installRoot) {
    $resolvedInstall = [IO.Path]::GetFullPath($installRoot)
    $resolvedProgramFiles = [IO.Path]::GetFullPath($env:ProgramFiles)
    if (-not $resolvedInstall.StartsWith($resolvedProgramFiles + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Install path escaped Program Files.'
    }
    Remove-Item -LiteralPath $resolvedInstall -Recurse -Force
}

if ($RemoveConfiguration -and (Test-Path -LiteralPath $programDataRoot)) {
    $resolvedData = [IO.Path]::GetFullPath($programDataRoot)
    $resolvedProgramData = [IO.Path]::GetFullPath($env:ProgramData)
    if (-not $resolvedData.StartsWith($resolvedProgramData + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Configuration path escaped ProgramData.'
    }
    Remove-Item -LiteralPath $resolvedData -Recurse -Force
}

Write-Host 'BetterDNS was removed and captured adapter DNS settings were restored.' -ForegroundColor Green
