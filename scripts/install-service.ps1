#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
$sourceService = Join-Path $packageRoot 'Service'
$sourceApp = Join-Path $packageRoot 'App'
$installRoot = Join-Path $env:ProgramFiles 'BetterDNS'
$serviceRoot = Join-Path $installRoot 'Service'
$appRoot = Join-Path $installRoot 'App'
$serviceExe = Join-Path $serviceRoot 'BetterDns.Service.exe'
$appExe = Join-Path $appRoot 'BetterDNS.exe'

if (-not (Test-Path -LiteralPath (Join-Path $sourceService 'BetterDns.Service.exe')) -or -not (Test-Path -LiteralPath (Join-Path $sourceApp 'BetterDNS.exe'))) {
    throw 'This installer must be run from the self-contained artifacts\BetterDNS package created by build.ps1.'
}

$existing = Get-Service -Name 'BetterDNS' -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name 'BetterDNS' -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
    & sc.exe delete BetterDNS | Out-Null
    Start-Sleep -Milliseconds 750
}

New-Item -ItemType Directory -Path $serviceRoot -Force | Out-Null
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
Copy-Item -Path (Join-Path $sourceService '*') -Destination $serviceRoot -Recurse -Force
Copy-Item -Path (Join-Path $sourceApp '*') -Destination $appRoot -Recurse -Force

$driverDirectory = Join-Path $serviceRoot 'WinDivert-2.2.2'
$driverDll = Join-Path $driverDirectory 'runtimes\win-x64\native\WinDivert.dll'
$driverSys = Join-Path $driverDirectory 'runtimes\win-x64\native\WinDivert64.sys'
if (-not (Test-Path -LiteralPath $driverDll) -or -not (Test-Path -LiteralPath $driverSys)) {
    throw 'The build package is missing its pre-extracted WinDivert x64 driver files.'
}

New-Service -Name 'BetterDNS' -BinaryPathName ('"' + $serviceExe + '"') -DisplayName 'BetterDNS Resolver' -Description 'Encrypted DNS routing, failover, rules, and Windows DNS leak protection.' -StartupType Automatic | Out-Null
& sc.exe failure BetterDNS reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
& sc.exe failureflag BetterDNS 1 | Out-Null

$shell = New-Object -ComObject WScript.Shell
$startMenuDirectory = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\BetterDNS'
New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
$startShortcut = $shell.CreateShortcut((Join-Path $startMenuDirectory 'BetterDNS.lnk'))
$startShortcut.TargetPath = $appExe
$startShortcut.WorkingDirectory = $appRoot
$startShortcut.Save()
$desktopShortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'BetterDNS.lnk'))
$desktopShortcut.TargetPath = $appExe
$desktopShortcut.WorkingDirectory = $appRoot
$desktopShortcut.Save()

Start-Service -Name 'BetterDNS'
Start-Process -FilePath $appExe
Write-Host 'BetterDNS is installed. Review the resolver chain, then enable protection in the GUI.' -ForegroundColor Green
