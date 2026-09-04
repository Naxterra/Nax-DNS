# Run only on the disposable CI machine. This installs the real service and driver.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This test is restricted to the disposable CI runner.' }
$repo = Split-Path -Parent $PSScriptRoot
$logs = Join-Path $repo 'artifacts\InstallerTest'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$setup = Get-ChildItem (Join-Path $repo 'artifacts\Installer') -Filter '*.exe' | Select-Object -First 1
$target = Join-Path $env:ProgramFiles 'BetterDNS'
if (Get-Service BetterDNS -ErrorAction SilentlyContinue) { throw 'Refusing to test over an existing service.' }

try {
    foreach ($phase in 'fresh', 'repair') {
        $setupLog = Join-Path $logs "$phase.log"
        $process = Start-Process $setup.FullName -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="' + $setupLog + '"')) -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "$phase installer timed out." }
        if ($process.ExitCode -ne 0) { throw "$phase installer exited $($process.ExitCode)." }
        & (Join-Path $target 'Service\BetterDns.Service.exe') --check-health
        if ($LASTEXITCODE -ne 0) { throw "$phase service health failed." }
    }
    $uninstall = Start-Process (Join-Path $target 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $logs 'uninstall.log') + '"')) -PassThru -WindowStyle Hidden
    if (-not $uninstall.WaitForExit(60000)) { $uninstall.Kill(); throw 'Uninstall timed out.' }
    if ($uninstall.ExitCode -ne 0) { throw "Uninstaller exited $($uninstall.ExitCode)." }
    if (Get-Service BetterDNS -ErrorAction SilentlyContinue) { throw 'The uninstaller left the service registered.' }
    if (Test-Path -LiteralPath (Join-Path $target 'Service\BetterDns.Service.exe')) { throw 'Uninstaller left the executable.' }
}
finally {
    $lifecycleLog = Join-Path $env:LOCALAPPDATA 'Temp\BetterDNS-installer.log'
    if (Test-Path -LiteralPath $lifecycleLog) { Copy-Item -LiteralPath $lifecycleLog -Destination (Join-Path $logs 'lifecycle.log') }
    Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-15)} -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'BetterDns' } | Format-List TimeCreated,ProviderName,Message |
        Out-File (Join-Path $logs 'events.log')
}
