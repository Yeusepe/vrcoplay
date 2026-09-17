[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][uri]$SourceUri,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$SourceSha256,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$NotesPath
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($Version -cnotmatch '^[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.0$' -or
    $Version.Split('.').Where({ [long]$_ -gt 65535 }).Count) { throw 'Store versions require A.B.C.0, with A positive and each part <= 65535.' }
$expectedUri = "https://github.com/Yeusepe/vrcoplay/releases/download/corresponding-source-store-v$Version/VRCoplay_${Version}_source.zip"
if ($SourceUri.AbsoluteUri -cne $expectedUri) { throw 'The Store source URL must match this exact Store version.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Store output already exists; use a fresh output directory.' }
$identity = Get-Content -LiteralPath (Join-Path $repo 'VRCoplay.App/StoreIdentity.json') -Raw | ConvertFrom-Json
$notes = (Get-Content -LiteralPath $NotesPath -Raw).Trim()
if (!$notes -or $notes.Length -gt 20000) { throw 'Release notes must contain 1–20000 characters.' }
$work = Join-Path $repo ('Private/artifacts/store-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
[xml]$manifest = Get-Content -LiteralPath (Join-Path $repo 'VRCoplay.App/Package.appxmanifest') -Raw
$manifest.Package.Identity.Name = $identity.name
$manifest.Package.Identity.Publisher = $identity.publisher
$manifest.Package.Identity.Version = $Version
$manifest.Package.Properties.PublisherDisplayName = $identity.publisherDisplayName
foreach ($phone in @($manifest.SelectNodes("//*[local-name()='PhoneIdentity']"))) { $null = $phone.ParentNode.RemoveChild($phone) }
$manifestFile = Join-Path $work 'Package.Store.appxmanifest'
$manifest.Save($manifestFile)
$sourceFile = Join-Path $work 'source-info.json'
@{ version = $Version; uri = $expectedUri; sha256 = $SourceSha256 } | ConvertTo-Json | Set-Content -LiteralPath $sourceFile -Encoding utf8
$notesFile = Join-Path $work 'release-notes.json'
@{ version = $Version; notes = $notes } | ConvertTo-Json | Set-Content -LiteralPath $notesFile -Encoding utf8
$msbuild = $env:VRCOPLAY_MSBUILD
if (!$msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/Current/Bin/amd64/MSBuild.exe'
}
if (!$msbuild -or !(Test-Path -LiteralPath $msbuild)) { throw 'Windows MSBuild is required for Store packaging.' }
$packages = Join-Path $work 'packages'
& $msbuild (Join-Path $repo 'VRCoplay.App/VRCoplay.csproj') -restore -target:Build -nologo -nr:false -verbosity:minimal `
    -p:Configuration=Release -p:Platform=x64 -p:StorePackage=true -p:PythonExe=NO_ARTWORK_GENERATORS `
    "-p:Version=$Version" "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version" "-p:InformationalVersion=$Version" `
    "-p:StoreManifest=$manifestFile" "-p:StoreReleaseNotes=$notesFile" "-p:CorrespondingSourceInfo=$sourceFile" "-p:AppxPackageDir=$packages/"
if ($LASTEXITCODE -ne 0) { throw 'Store package build failed.' }
$uploads = @(Get-ChildItem -LiteralPath $packages -Filter '*.msixupload' -File)
if ($uploads.Count -ne 1 -or $uploads[0].Name -cne "VRCoplay_${Version}_x64.msixupload") { throw 'Expected exactly one matching Store upload.' }
New-Item -ItemType Directory -Path (Join-Path $output 'checks') -Force | Out-Null
Copy-Item -LiteralPath $uploads[0].FullName -Destination $output
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($uploads[0].FullName)
try {
    $packageName = "VRCoplay_${Version}_x64.msix"
    if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -cne $packageName) { throw 'Unexpected upload contents.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.Entries[0], (Join-Path $output "checks/$packageName"))
} finally { $archive.Dispose() }
Copy-Item -LiteralPath $sourceFile -Destination $output
$assembly = Join-Path $repo 'VRCoplay.App/bin/x64/Release/net10.0-windows10.0.22621.0/win-x64/VRCoplay.dll'
$fileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
if ([Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString() -cne $Version -or
    $fileInfo.FileVersion -cne $Version -or $fileInfo.ProductVersion -cne $Version) { throw 'Store assembly versions differ from the package version.' }
Write-Output "Store package created locally: $output. Run all release validation gates before upload or distribution."
