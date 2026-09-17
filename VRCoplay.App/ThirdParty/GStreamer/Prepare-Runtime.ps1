param([string]$CacheDirectory = (Join-Path $env:LOCALAPPDATA 'VRCoplay/build-cache/gstreamer'))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1')
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem
$manifest = Get-Content -Raw (Join-Path $PSScriptRoot 'runtime.json') | ConvertFrom-Json
$destination = Join-Path $PSScriptRoot 'runtime'
New-Item -ItemType Directory -Force -Path $destination, $CacheDirectory | Out-Null
$allowed = @($manifest.packages.files.path)
$scanner = $manifest.packages.files | Where-Object { $_.path -eq 'bin/gst-plugin-scanner.exe' }
foreach ($file in Get-ChildItem -LiteralPath $destination -Recurse -File) {
    $relative = $file.FullName.Substring($destination.Length + 1).Replace('\', '/')
    if ($relative -eq 'libexec/gstreamer-1.0/gst-plugin-scanner.exe' -and $scanner -and
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -eq $scanner.sha256) {
        Remove-Item -LiteralPath $file.FullName
        continue
    }
    if ($relative -notin $allowed) { throw "Unexpected GStreamer runtime file: $relative. Restore a clean runtime directory." }
}
foreach ($package in $manifest.packages) {
    $missing = @($package.files | Where-Object {
        $path = Join-Path $destination $_.path
        !(Test-Path -LiteralPath $path) -or (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -ne $_.sha256
    })
    if ($missing.Count -eq 0) { continue }
    $archive = Join-Path $CacheDirectory $package.file
    if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -UseBasicParsing -Uri $package.url -OutFile $archive }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash -ne $package.sha256) {
        throw "GStreamer archive checksum mismatch: $archive"
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($file in $missing) {
            $path = Join-Path $destination $file.path
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($file.member), $path, $true)
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -ne $file.sha256) { throw "GStreamer file checksum mismatch: $path" }
        }
    } finally { $zip.Dispose() }
}
