@echo off
rem Builds Nax-DNSManager into .\dist (service, tray app and the WinDivert driver files).
setlocal
cd /d "%~dp0"
if not exist dist\.gotmp mkdir dist\.gotmp
rem Link inside dist\ so an antivirus exclusion for this folder also covers the build.
set GOTMPDIR=%~dp0dist\.gotmp
go build -trimpath -ldflags "-s -w" -o dist\Nax-DNSService.exe .\cmd\naxdns-service || exit /b 1
go build -trimpath -ldflags "-s -w -H=windowsgui" -o dist\Nax-DNSManager.exe .\cmd\naxdns || exit /b 1
copy /y third_party\windivert\WinDivert-2.2.2-A\x64\WinDivert.dll dist >nul
copy /y third_party\windivert\WinDivert-2.2.2-A\x64\WinDivert64.sys dist >nul 2>&1
echo Built dist\. Install from an elevated terminal: dist\Nax-DNSService.exe install
