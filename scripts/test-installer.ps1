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
        if ($phase -eq 'fresh') {
            $probe = Join-Path $repo 'tools\BetterDns.InterceptionProbe\bin\Release\net11.0-windows\win-x64\BetterDns.InterceptionProbe.exe'
            # CI has no user's provider preference. Select a measured, responding strict-DoH3
            # endpoint before testing kernel interception; production defaults are untouched.
            & $probe --ci-select-primary (Join-Path $logs 'ci-primary-selection.json')
            if ($LASTEXITCODE -ne 0) { throw 'No usable strict-DoH3 primary on this CI network.' }
            # The resolver hostname is answered from configured bootstrap IPs, avoiding
            # dependence on external UDP/443 availability on hosted CI machines.
            & $probe 127.0.0.1 (Join-Path $logs 'loopback-interception.json') root.hagezi.org
            if ($LASTEXITCODE -ne 0) { throw 'Loopback DNS interception failed.' }
            $gui = Start-Process (Join-Path $target 'App\BetterDNS.exe') -PassThru
            try {
                $deadline = (Get-Date).AddSeconds(20)
                do {
                    Start-Sleep -Milliseconds 250
                    $gui.Refresh()
                    if ($gui.HasExited) { throw "Published GUI crashed with exit code $($gui.ExitCode)." }
                } while ($gui.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline)
                if ($gui.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Published GUI did not create its window.' }
                Start-Sleep -Seconds 4
                $gui.Refresh()
                if ($gui.HasExited) { throw 'Published GUI exited after creating its window.' }
            }
            finally {
                if (-not $gui.HasExited) {
                    $gui.CloseMainWindow() | Out-Null
                    if (-not $gui.WaitForExit(5000)) { $gui.Kill() }
                }
            }
        }
    }
    # Exercise the exact helper used by the GUI, never on a user's live machine.
    $configurationPath = Join-Path $env:ProgramData 'BetterDNS\config.json'
    $beforeServiceActions = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    foreach ($action in 'Stop', 'Start', 'Uninstall', 'Install') {
        $manageScript = Join-Path $target 'Installer\manage-service.ps1'
        $actionLog = Join-Path $logs "service-$action.log"
        $manager = Start-Process (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $manageScript + '"'),
            '-InstallRoot', ('"' + $target + '"'), '-Action', $action, '-LogPath', ('"' + $actionLog + '"')
        ) -PassThru -WindowStyle Hidden
        if (-not $manager.WaitForExit(120000)) { $manager.Kill(); throw "Service $action timed out." }
        if ($manager.ExitCode -ne 0) { throw "Service $action failed with exit $($manager.ExitCode)." }
        $state = Get-Service BetterDNS -ErrorAction SilentlyContinue
        try {
            if ($action -eq 'Uninstall') {
                if ($state) { throw 'Service registration remains after service uninstall.' }
            } elseif ($action -eq 'Stop') {
                if (-not $state -or $state.Status -ne 'Stopped') { throw 'Service did not stop.' }
            } elseif (-not $state -or $state.Status -ne 'Running') { throw "Service is not running after $action." }
        } finally { if ($state) { $state.Dispose() } }
        if (-not (Test-Path -LiteralPath (Join-Path $target 'App\BetterDNS.exe'))) { throw 'Service action removed the GUI.' }
        $afterServiceAction = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
        if ($afterServiceAction.Active) { throw 'A service action unexpectedly enabled routing.' }
        foreach ($property in 'Upstreams', 'Chains', 'Rules') {
            if (($beforeServiceActions.$property | ConvertTo-Json -Depth 30 -Compress) -cne
                ($afterServiceAction.$property | ConvertTo-Json -Depth 30 -Compress)) { throw "Service action changed $property." }
        }
        if ($action -in 'Stop', 'Uninstall') {
            if (Get-NetFirewallRule -ErrorAction Stop | Where-Object Group -eq 'BetterDNS') { throw 'BetterDNS firewall policy remains after stopping.' }
        }
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
