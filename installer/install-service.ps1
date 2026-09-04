#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallRoot,

    [string]$LogPath = (Join-Path $env:ProgramData 'BetterDNS\installer.log')
)

$ErrorActionPreference = 'Stop'
$serviceRoot = Join-Path $InstallRoot 'Service'
$serviceExe = Join-Path $serviceRoot 'BetterDns.Service.exe'
$driverPackage = Join-Path $serviceRoot 'native.windivert.2.2.2.nupkg'
$driverArchive = Join-Path $serviceRoot 'native.windivert.2.2.2.zip'
$driverDirectory = Join-Path $serviceRoot 'WinDivert-2.2.2'
$nativeDirectory = Join-Path $driverDirectory 'runtimes\win-x64\native'
$driverDll = Join-Path $nativeDirectory 'WinDivert.dll'
$driverSys = Join-Path $nativeDirectory 'WinDivert64.sys'

New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null
Set-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) BetterDNS service installation started."

function Write-InstallLog([string]$Message) {
    Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) $Message"
}

trap {
    Write-InstallLog ("FAILED: " + ($_ | Out-String).Trim())
    Write-InstallLog ("STACK: " + $_.ScriptStackTrace)
    exit 1
}

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

if (-not (Test-Path -LiteralPath $driverDll) -or -not (Test-Path -LiteralPath $driverSys)) {
    Copy-Item -LiteralPath $driverPackage -Destination $driverArchive -Force
    Expand-Archive -LiteralPath $driverArchive -DestinationPath $driverDirectory -Force
    Remove-Item -LiteralPath $driverArchive -Force
}

if (-not (Test-Path -LiteralPath $driverDll) -or -not (Test-Path -LiteralPath $driverSys)) {
    throw 'The WinDivert package did not contain the expected x64 driver files.'
}
Write-InstallLog "WinDivert was extracted in place; no endpoint-protection-sensitive driver copy was attempted."

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
Write-InstallLog 'Windows service registration is configured.'

& sc.exe description BetterDNS 'Encrypted DNS routing, failover, rules, and kernel DNS interception.' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not set the service description.' }
& sc.exe failure BetterDNS reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery.' }
& sc.exe failureflag BetterDNS 1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not enable service recovery.' }

Start-Service -Name 'BetterDNS'
(Get-Service -Name 'BetterDNS').WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
Write-InstallLog 'BetterDNS service is running.'
