# Copyright (c) 2026 YUCP Studio.
# SPDX-License-Identifier: GPL-3.0-or-later
#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$CacheDirectory = (Join-Path $env:LOCALAPPDATA 'VRCoplay/source-build'),
    [string]$MSBuildPath,
    [switch]$Rebuild
)
$ErrorActionPreference = 'Stop'
$pin = Get-Content (Join-Path $PSScriptRoot 'PIN.json') -Raw | ConvertFrom-Json
$feed = Join-Path $PSScriptRoot 'artifacts'
$package = Join-Path $feed "$($pin.packageId).$($pin.packageVersion).nupkg"
$receipt = Join-Path $feed 'build.json'
$recipe = (@('PIN.json','changes.patch','build.ps1','VRCoplay.Win2D.nuspec') | ForEach-Object {
    (Get-FileHash (Join-Path $PSScriptRoot $_)).Hash
}) -join ':'
if (!$Rebuild -and (Test-Path $package) -and (Test-Path $receipt)) {
    $prior = Get-Content $receipt -Raw | ConvertFrom-Json
    if ($prior.recipe -eq $recipe -and $prior.packageSha256 -eq (Get-FileHash $package).Hash) {
        Write-Output "Verified local Win2D package: $package"
        return
    }
    throw 'Win2D build inputs changed. Increment the package version, then run with -Rebuild.'
}
function Run([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed ($LASTEXITCODE)." }
}
function Download([string]$Uri, [string]$Path, [string]$Hash) {
    if (!(Test-Path $Path)) { Invoke-WebRequest $Uri -OutFile $Path }
    if ((Get-FileHash $Path).Hash -ine $Hash) { throw "Checksum mismatch: $Path" }
}
if (!$MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = (& $vswhere -version '[17.0,19.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath) |
        Where-Object { Get-ChildItem (Join-Path $_ 'VC/Tools/MSVC') -Directory -Filter '14.44.*' -ErrorAction SilentlyContinue } | Select-Object -First 1
    if (!$vs) { throw 'Install Visual Studio 2022 or 2026 with C++ x64 tools (v143), or supply -MSBuildPath.' }
    $MSBuildPath = Join-Path $vs 'MSBuild/Current/Bin/amd64/MSBuild.exe'
}
$vsRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path $MSBuildPath) '../../../..'))
$msvc = Get-ChildItem (Join-Path $vsRoot 'VC/Tools/MSVC') -Directory |
    Where-Object { $_.Name -like '14.44.*' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$msvc) { throw 'Install the v143 14.44 C++ x64 toolset.' }
$sdk = '10.0.26100.0'
$sdkBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/bin/$sdk/x64"
if (!(Test-Path "$sdkBin/midlrt.exe")) { throw "Install Windows SDK $sdk." }
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
[IO.Directory]::CreateDirectory($CacheDirectory) | Out-Null
[IO.Directory]::CreateDirectory($feed) | Out-Null
$archive = Join-Path $CacheDirectory "win2d-$($pin.commit).tar.gz"
$nuget = Join-Path $CacheDirectory 'nuget-6.14.0.exe'
Download $pin.archive $archive $pin.archiveSha256
Download $pin.nugetUrl $nuget $pin.nugetSha256
$work = Join-Path $CacheDirectory ('win2d-public-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
Run 'tar' @('-xzf', $archive, '-C', $work, '--strip-components=1')
Run 'git' @('-C', $work, 'init', '-q')
Run 'git' @('-C', $work, 'fetch', '--depth=1', $pin.repository, $pin.commit)
Run 'git' @('-C', $work, 'reset', '--mixed', 'FETCH_HEAD')
Run 'git' @('-C', $work, 'apply', '--check', (Join-Path $PSScriptRoot 'changes.patch'))
Run 'git' @('-C', $work, 'apply', (Join-Path $PSScriptRoot 'changes.patch'))
if ((Get-FileHash "$work/winrt/inc/MicrosoftTelemetry.h").Hash -ine $pin.telemetryHeaderSha256) { throw 'Public telemetry header changed.' }
$oldPath = $env:PATH
Push-Location $work
try {
    $env:PATH = $sdkBin + ';' + $oldPath
    Run $nuget @('restore', 'winrt/lib/packages.config', '-PackagesDirectory', 'packages', '-Source', 'https://api.nuget.org/v3/index.json', '-NonInteractive', '-Verbosity', 'quiet')
    Run $MSBuildPath @('Win2D.proj', '/t:PrepareVersionInfo', '/p:BuildTests=false', '/p:BuildTools=false', '/p:RunTests=false', '/nr:false', '/v:m')
    Run $MSBuildPath @('winrt/dll/winrt.dll.uap.vcxproj', '/p:Configuration=Release', '/p:Platform=x64', '/p:PlatformToolset=v143', "/p:VCToolsVersion=$($msvc.Name)", "/p:WindowsTargetPlatformVersion=$sdk", '/p:PreferredToolArchitecture=x64', '/p:IncludeVersionInfo=true', '/p:ApplicationType=', '/p:AppContainerApplication=false', '/p:UseCrtSDKReference=false', '/p:WinUISDKReferences=false', '/p:CharacterSet=Unicode', '/m:4', '/nr:false', '/v:m')
    $managed = @('winrt/projection/winrt.projection.csproj', '-p:TargetPlatformVersion=10.0.22621.0', '-p:Configuration=Release', '-p:Platform=AnyCPU', '-p:DebugType=None', '-p:DebugSymbols=false', "-p:PathMap=$work=/_")
    Run 'dotnet' (@('restore') + $managed + @('--source', 'https://api.nuget.org/v3/index.json'))
    Run 'dotnet' (@('build') + $managed + @('--no-restore', '--verbosity', 'minimal'))
    foreach ($dll in @('bin/uapx64/release/winrt.dll.uap/Microsoft.Graphics.Canvas.dll', 'bin/anycpu/release/winrt.projection/Microsoft.Graphics.Canvas.Interop.dll')) {
        $bytes = [IO.File]::ReadAllBytes((Join-Path $work $dll))
        $ascii = [Text.Encoding]::ASCII.GetString($bytes)
        foreach ($record in [regex]::Matches($ascii, '(?s)RSDS.{20}([^\x00]+)')) {
            if ($record.Groups[1].Value -match '[\\/:]') { throw "PDB build path leaked into $dll" }
        }
        foreach ($text in @($ascii, [Text.Encoding]::Unicode.GetString($bytes))) {
            if ($text -match '(?i)[A-Z]:[\\/]+Users[\\/]+|/(Users|home)/') { throw "User profile path leaked into $dll" }
        }
    }
    Run $nuget @('pack', (Join-Path $PSScriptRoot 'VRCoplay.Win2D.nuspec'), '-BasePath', $work, '-OutputDirectory', $feed, '-NonInteractive', '-NoPackageAnalysis')
    @{
        commit = $pin.commit; recipe = $recipe
        packageSha256 = (Get-FileHash $package).Hash
        nativeSha256 = (Get-FileHash 'bin/uapx64/release/winrt.dll.uap/Microsoft.Graphics.Canvas.dll').Hash
        projectionSha256 = (Get-FileHash 'bin/anycpu/release/winrt.projection/Microsoft.Graphics.Canvas.Interop.dll').Hash
        telemetryHeaderSha256 = (Get-FileHash 'winrt/inc/MicrosoftTelemetry.h').Hash
        msbuildVersion = (Get-Item $MSBuildPath).VersionInfo.FileVersion; msvcVersion = $msvc.Name; windowsSdk = $sdk; dotnetSdk = (& dotnet --version)
    } | ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding utf8NoBOM
} finally {
    $env:PATH = $oldPath
    Pop-Location
}
Write-Output "Built local Win2D package: $package"
