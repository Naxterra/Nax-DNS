[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [switch]$BuildInstaller
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot 'artifacts\BetterDNS'

Push-Location $repositoryRoot
try {
    dotnet test 'BetterDns.slnx' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

    if (Test-Path -LiteralPath $artifactRoot) {
        $resolvedArtifacts = [IO.Path]::GetFullPath($artifactRoot)
        $resolvedRepository = [IO.Path]::GetFullPath($repositoryRoot)
        if (-not $resolvedArtifacts.StartsWith($resolvedRepository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Artifact path escaped the repository.'
        }
        Remove-Item -LiteralPath $resolvedArtifacts -Recurse -Force
    }

    dotnet publish 'src\BetterDns.Service\BetterDns.Service.csproj' -c Release -r $Runtime --self-contained true -o (Join-Path $artifactRoot 'Service')
    if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }

    $winDivertPackage = Join-Path $env:USERPROFILE '.nuget\packages\native.windivert\2.2.2\native.windivert.2.2.2.nupkg'
    if (-not (Test-Path -LiteralPath $winDivertPackage)) { throw 'Native.WinDivert 2.2.2 was not restored.' }
    Copy-Item -LiteralPath $winDivertPackage -Destination (Join-Path $artifactRoot 'Service')
    $winDivertOutput = Join-Path $artifactRoot 'Service\WinDivert-2.2.2'
    [System.IO.Compression.ZipFile]::ExtractToDirectory($winDivertPackage, $winDivertOutput)
    $expectedDriver = Join-Path $winDivertOutput 'runtimes\win-x64\native\WinDivert64.sys'
    $expectedLibrary = Join-Path $winDivertOutput 'runtimes\win-x64\native\WinDivert.dll'
    if (-not (Test-Path -LiteralPath $expectedDriver) -or -not (Test-Path -LiteralPath $expectedLibrary)) {
        throw 'The build-time WinDivert extraction did not produce the expected x64 files.'
    }

    dotnet publish 'src\BetterDns.Gui\BetterDns.Gui.csproj' -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $artifactRoot 'App')
    if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed.' }

    Copy-Item -LiteralPath 'scripts\install-service.ps1' -Destination $artifactRoot
    Copy-Item -LiteralPath 'scripts\uninstall-service.ps1' -Destination $artifactRoot
    Copy-Item -LiteralPath 'README.md' -Destination $artifactRoot
    Copy-Item -LiteralPath 'THIRD_PARTY_NOTICES.md' -Destination $artifactRoot
    Write-Host "Package created at $artifactRoot" -ForegroundColor Green

    if ($BuildInstaller) {
        $compilerCandidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        )
        $compiler = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
        if (-not $compiler) {
            throw 'Inno Setup 7 (or 6.6+) is required to build the Setup executable.'
        }

        & $compiler 'installer\BetterDNS.iss'
        if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
        Write-Host "Installer created in $(Join-Path $repositoryRoot 'artifacts\Installer')" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
