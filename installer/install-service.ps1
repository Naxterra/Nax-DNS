#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallRoot
)

$ErrorActionPreference = 'Stop'
$serviceRoot = Join-Path $InstallRoot 'Service'
$serviceExe = Join-Path $serviceRoot 'BetterDns.Service.exe'
$driverPackage = Join-Path $serviceRoot 'native.windivert.2.2.2.nupkg'
$driverArchive = Join-Path $serviceRoot 'native.windivert.2.2.2.zip'
$driverExtraction = Join-Path $serviceRoot '.windivert-package'

if (-not (Test-Path -LiteralPath $serviceExe)) {
    throw "Service executable not found at $serviceExe"
}

$existing = Get-Service -Name 'BetterDNS' -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Stop-Service -Name 'BetterDNS' -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
}

if (-not (Test-Path -LiteralPath $driverPackage)) {
    throw 'The signed WinDivert driver package is missing.'
}

Copy-Item -LiteralPath $driverPackage -Destination $driverArchive -Force
Expand-Archive -LiteralPath $driverArchive -DestinationPath $driverExtraction -Force
Copy-Item -LiteralPath (Join-Path $driverExtraction 'runtimes\win-x64\native\WinDivert.dll') -Destination $serviceRoot -Force
Copy-Item -LiteralPath (Join-Path $driverExtraction 'runtimes\win-x64\native\WinDivert64.sys') -Destination $serviceRoot -Force
Copy-Item -LiteralPath (Join-Path $driverExtraction 'LICENSE') -Destination (Join-Path $serviceRoot 'WinDivert-LICENSE.txt') -Force
Remove-Item -LiteralPath $driverExtraction -Recurse -Force
Remove-Item -LiteralPath $driverArchive -Force

if ($existing) {
    $quotedBinary = '"' + $serviceExe + '"'
    $arguments = @(
        'config', 'BetterDNS',
        'binPath=', $quotedBinary,
        'start=', 'auto',
        'obj=', 'LocalSystem',
        'DisplayName=', 'BetterDNS Resolver'
    )
    & sc.exe @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not update the BetterDNS service registration.' }
}
else {
    New-Service -Name 'BetterDNS' -BinaryPathName ('"' + $serviceExe + '"') -DisplayName 'BetterDNS Resolver' -Description 'Encrypted DNS routing, failover, rules, and kernel DNS interception.' -StartupType Automatic | Out-Null
}

& sc.exe description BetterDNS 'Encrypted DNS routing, failover, rules, and kernel DNS interception.' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not set the service description.' }
& sc.exe failure BetterDNS reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery.' }
& sc.exe failureflag BetterDNS 1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not enable service recovery.' }

Start-Service -Name 'BetterDNS'
(Get-Service -Name 'BetterDNS').WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
