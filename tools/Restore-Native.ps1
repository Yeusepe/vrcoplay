# Copyright (c) 2026 YUCP Studio.
# SPDX-License-Identifier: GPL-3.0-or-later
#Requires -Version 7.0
[CmdletBinding()]
param([string]$CacheDirectory = (Join-Path $env:LOCALAPPDATA 'VRCoplay/source-build'))
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
[IO.Directory]::CreateDirectory($CacheDirectory) | Out-Null
function Download([string]$Uri, [string]$Path, [string]$Hash) {
    if (!(Test-Path -LiteralPath $Path)) { Invoke-WebRequest -Uri $Uri -OutFile $Path }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Hash) { throw "Checksum mismatch: $Path" }
}
function Restore-ZipFile([string]$Uri, [string]$ArchiveHash, [string]$Member, [string]$Destination, [string]$FileHash) {
    $path = Join-Path $repo $Destination
    if (Test-Path -LiteralPath $path) { return }
    $archive = Join-Path $CacheDirectory ([IO.Path]::GetFileName(([uri]$Uri).AbsolutePath))
    Download $Uri $archive $ArchiveHash
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = @($zip.Entries | Where-Object { $_.FullName -eq $Member -or $_.FullName.EndsWith('/' + $Member) })
        if ($entry.Count -ne 1) { throw "Expected one $Member in $archive" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry[0], $path, $false)
    } finally { $zip.Dispose() }
    if ((Get-FileHash -LiteralPath $path).Hash -ine $FileHash) { throw "Restored file checksum mismatch: $path" }
}
Restore-ZipFile 'https://github.com/bluenviron/mediamtx/releases/download/v1.20.0/mediamtx_v1.20.0_windows_amd64.zip' `
    '7364e7672e6b4420e986ec4b56e2cc32ec7b4085f69b56ec224d596d0fa8b19f' 'mediamtx.exe' 'VRCoplay.App/Tools/mediamtx.exe' `
    '6149b1854800295cc2578bcfc20dfb965f4b2fd5acfe7b3d3d41fe2f5cbd38df'
$native = Join-Path $repo 'VRCoplay.App/ThirdParty'
$viiper = Get-Content -LiteralPath (Join-Path $native 'VIIPER/PIN.json') -Raw | ConvertFrom-Json
Restore-ZipFile "$($viiper.repository)/releases/download/$($viiper.tag)/$($viiper.releaseAsset)" `
    $viiper.sha256.($viiper.releaseAsset) 'libVIIPER.dll' 'VRCoplay.App/ThirdParty/VIIPER/libVIIPER.dll' $viiper.sha256.'libVIIPER.dll'
$openvr = Get-Content -LiteralPath (Join-Path $native 'OpenVR/PIN.json') -Raw | ConvertFrom-Json
foreach ($file in $openvr.files | Where-Object { $_.path.StartsWith('bin/win64/') }) {
    $destination = Join-Path $native ('OpenVR/' + [IO.Path]::GetFileName($file.path))
    if (!(Test-Path -LiteralPath $destination)) {
        Download "https://raw.githubusercontent.com/ValveSoftware/openvr/$($openvr.commit)/$($file.path)" $destination $file.sha256
    }
}
if (!(Test-Path -LiteralPath (Join-Path $native 'InternetHostingTool/iht.dll'))) {
    $pin = Get-Content -LiteralPath (Join-Path $native 'InternetHostingTool/PIN.json') -Raw | ConvertFrom-Json
    $work = Join-Path $CacheDirectory ('iht-' + [guid]::NewGuid().ToString('N'))
    $source = Join-Path $work 'source'
    git clone --no-checkout $pin.project $source
    git -C $source checkout --detach $pin.baseCommit
    git -C $source apply (Join-Path $native 'InternetHostingTool/changes.patch')
    cmake -S $source -B "$work/build" -A x64 -DBUILD_TESTING=OFF
    cmake --build "$work/build" --config Release --target iht --parallel
    Copy-Item -LiteralPath "$work/build/Release/iht.dll" -Destination (Join-Path $native 'InternetHostingTool/iht.dll')
}
if (!(Test-Path -LiteralPath (Join-Path $native 'LibDataChannel/datachannel.dll'))) {
    & (Join-Path $native 'LibDataChannel/build.ps1') -BuildRoot (Join-Path $CacheDirectory ('datachannel-' + [guid]::NewGuid().ToString('N')))
}
& (Join-Path $native 'Win2D/build.ps1') -CacheDirectory $CacheDirectory
Write-Output 'Native build inputs are ready. Obtain the Satoshi font as described in BUILD.txt, then build the projects.'
