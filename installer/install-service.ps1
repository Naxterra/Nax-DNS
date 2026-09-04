#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallRoot,

    [string]$LogPath = (Join-Path $env:TEMP 'BetterDNS-installer.log')
)

$ErrorActionPreference = 'Stop'
$serviceRoot = Join-Path $InstallRoot 'Service'
$serviceExe = Join-Path $serviceRoot 'BetterDns.Service.exe'
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

if (-not (Test-Path -LiteralPath $driverDll) -or -not (Test-Path -LiteralPath $driverSys)) {
    throw 'Setup did not deploy the expected pre-extracted WinDivert x64 driver files.'
}
Write-InstallLog 'The pre-extracted WinDivert files are present in their final directory.'

if ($existing) {
    $registration = Get-CimInstance Win32_Service -Filter "Name='BetterDNS'"
    $changed = Invoke-CimMethod -InputObject $registration -MethodName Change -Arguments @{
        PathName = '"' + $serviceExe + '"'; StartMode = 'Automatic'; StartName = 'LocalSystem'; DisplayName = 'BetterDNS Resolver'
    }
    if ($changed.ReturnValue -ne 0) { throw "Could not update the service: $($changed.ReturnValue)." }
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
$healthOutput = & $serviceExe --check-health 2>&1
$healthExit = $LASTEXITCODE
Write-InstallLog ($healthOutput | Out-String)
if ($healthExit -ne 0) {
    Stop-Service -Name 'BetterDNS' -ErrorAction SilentlyContinue
    throw 'Service startup verification failed. See the health-check error above.'
}
Write-InstallLog 'BetterDNS stayed responsive; configuration, GUI connection and kernel driver are ready.'
