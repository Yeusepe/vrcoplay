
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][uri] $BaseUri,
    [string] $NotesPath,
    [string] $CertificateThumbprint,
    [uri] $TimestampUri = 'http://timestamp.digicert.com',
    [Parameter(Mandatory)][uri] $SourceUri,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string] $SourceSha256,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $WorkDirectory,
    [switch] $ReuseRepositoryBuild,
    [string] $RepositoryBuildReceipt
)
$ErrorActionPreference = 'Stop'
if ($env:CI -eq 'true' -and !$CertificateThumbprint) {
    throw 'CI must provide an existing signing certificate. Never generate a new key for each release.'
}
$releaseVersion = [version]::Parse($Version)
if ($releaseVersion.Revision -lt 0 -or @($releaseVersion.Major, $releaseVersion.Minor, $releaseVersion.Build, $releaseVersion.Revision).Where({ $_ -gt 65535 }).Count) {
    throw 'Use a four-part MSIX version, with each number between 0 and 65535.'
}
if ($BaseUri.Scheme -ne 'https' -or $BaseUri.Query -or $BaseUri.Fragment -or $BaseUri.UserInfo -or !$BaseUri.AbsolutePath.EndsWith('/')) {
    throw 'BaseUri must be a stable HTTPS directory ending in /, without credentials, query or fragment.'
}
if (!$TimestampUri.IsAbsoluteUri -or $TimestampUri.Scheme -notin @('http','https') -or $TimestampUri.UserInfo -or $TimestampUri.Query -or $TimestampUri.Fragment) {
    throw 'TimestampUri must be an HTTP(S) RFC 3161 timestamp service without credentials, query or fragment.'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $repo 'VRCoplay.App/VRCoplay.csproj'
if ($ReuseRepositoryBuild) {
    if (!$RepositoryBuildReceipt) { throw 'Packaging without compilation requires this run''s repository build receipt.' }
    $receipt = Get-Content -LiteralPath $RepositoryBuildReceipt -Raw | ConvertFrom-Json -AsHashtable
    if ($receipt.status -ne 'passed' -or $receipt.build_input -ne 'repository-checkout' -or
        $receipt.release_version -cne $Version -or $receipt.source_commit -cne (& git -C $repo rev-parse HEAD) -or
        !$receipt.build_outputs -or $receipt.build_outputs.Count -eq 0) {
        throw 'The repository build receipt does not match this release.'
    }
    if ($env:GITHUB_ACTIONS -eq 'true' -and (!$env:GITHUB_RUN_ID -or !$env:GITHUB_RUN_ATTEMPT -or
        $receipt.workflow_run_id -cne $env:GITHUB_RUN_ID -or $receipt.workflow_run_attempt -cne $env:GITHUB_RUN_ATTEMPT -or
        $receipt.source_dirty -ne $false -or (& git -C $repo status --porcelain))) {
        throw 'CI packaging must use a clean repository build from this workflow attempt.'
    }
    foreach ($relative in $receipt.build_outputs.Keys) {
        if ($relative -notmatch '^VRCoplay\.App/(bin|obj)/' -or $relative.Split('/') -contains '..') {
            throw 'Unexpected repository build output path.'
        }
        $path = Join-Path $repo $relative
        $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            [IO.FileShare]::Read, 1MB, [IO.FileOptions]::SequentialScan)
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -ine $receipt.build_outputs[$relative]) {
            throw "Repository build output changed after validation: $relative"
        }
    }
    $files = @(foreach ($folder in @('bin', 'obj')) {
        $directory = Join-Path $repo "VRCoplay.App/$folder"
        if ([IO.Directory]::Exists($directory)) {
            foreach ($file in [IO.Directory]::EnumerateFiles($directory, '*', [IO.SearchOption]::AllDirectories)) {
                [IO.Path]::GetRelativePath($repo, $file).Replace('\', '/')
            }
        }
    })
    if (Compare-Object @($receipt.build_outputs.Keys) $files) {
        throw 'Repository build output inventory changed after validation.'
    }
}
if ($SourceUri.Scheme -ne 'https' -or $SourceUri.UserInfo -or $SourceUri.Query -or $SourceUri.Fragment) {
    throw 'SourceUri must be a stable HTTPS URL without credentials, query, or fragment.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output already exists. Choose a new version or an empty output directory: $output" }
$work = if ($WorkDirectory) { [IO.Path]::GetFullPath($WorkDirectory) } else { Join-Path ([IO.Path]::GetTempPath()) ("vrcoplay-build-" + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $work) { throw "Build directory already exists: $work" }
[IO.Directory]::CreateDirectory($work) | Out-Null
$notes = if ($NotesPath) { (Get-Content -LiteralPath $NotesPath -Raw).Trim() } else { "Version $Version." }
if (!$notes -or $notes.Length -gt 20000) { throw 'Release notes must contain 1–20000 characters.' }

[xml]$manifest = Get-Content -LiteralPath (Join-Path $repo 'VRCoplay.App/Package.appxmanifest') -Raw
$manifest.Package.Identity.Version = $Version
$publisher = $manifest.Package.Identity.Publisher
$identity = $manifest.Package.Identity.Name
$manifestPath = Join-Path $work 'Package.appxmanifest'
$manifest.Save($manifestPath)
$notesFile = Join-Path $work 'release-notes.json'
@{ version = $Version; notes = $notes } | ConvertTo-Json | Set-Content -LiteralPath $notesFile -Encoding utf8
$sourceInfo = Join-Path $work 'source-info.json'
@{ version = $Version; uri = $SourceUri.AbsoluteUri; sha256 = $SourceSha256.ToLowerInvariant() } | ConvertTo-Json | Set-Content -LiteralPath $sourceInfo -Encoding utf8
$sourceFile = Join-Path $work 'update-source.json'
$installerUri = [uri]::new($BaseUri, 'VRCoplay.appinstaller').AbsoluteUri
@{ appInstallerUri = $installerUri } | ConvertTo-Json | Set-Content -LiteralPath $sourceFile -Encoding utf8


$signingFolder = Join-Path $env:LOCALAPPDATA 'VRCoplay/Packaging'
$signingRecord = Join-Path $signingFolder 'tester-certificate.json'
if (!$CertificateThumbprint -and (Test-Path -LiteralPath $signingRecord)) {
    $CertificateThumbprint = (Get-Content -LiteralPath $signingRecord -Raw | ConvertFrom-Json).thumbprint
}
if ($CertificateThumbprint) {
    $certificate = Get-Item -LiteralPath "Cert:/CurrentUser/My/$CertificateThumbprint"
} else {
    $certificate = New-SelfSignedCertificate -Type Custom -Subject $publisher -FriendlyName 'VRCoplay tester signing' `
        -CertStoreLocation Cert:/CurrentUser/My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}CA=false')
    [IO.Directory]::CreateDirectory($signingFolder) | Out-Null
    @{ thumbprint = $certificate.Thumbprint } | ConvertTo-Json | Set-Content -LiteralPath $signingRecord -Encoding utf8
}
if (!$certificate.HasPrivateKey -or $certificate.Subject -ne $publisher -or $certificate.NotAfter -lt (Get-Date).AddDays(7)) {
    throw 'The signing certificate must match the manifest publisher, have a private key and remain valid for at least seven days.'
}
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$signTool = Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object { $_.Name -match '^10\.' } |
    Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$signTool) { throw 'Install the Windows SDK signing tools.' }

$packageFolder = Join-Path $work 'packages'
$buildArgs = @('build', $project, '-c', 'Release', '--nologo', '-v:minimal',
    "-p:Version=$Version", "-p:AssemblyVersion=$Version", "-p:FileVersion=$Version", "-p:InformationalVersion=$Version",
    '-p:Platform=x64', '-p:RuntimeIdentifier=win-x64', '-p:SelfContained=true', '-p:WindowsAppSDKSelfContained=true',
    '-p:WindowsPackageType=MSIX', '-p:EnableMsixTooling=true', '-p:GenerateAppxPackageOnBuild=true',
    '-p:AppxBundle=Never', '-p:UapAppxPackageBuildMode=SideloadOnly', '-p:AppxPackageSigningEnabled=false', '-p:AppxSymbolPackageEnabled=false',
    '-p:PythonExe=NO_ARTWORK_GENERATORS',
    "-p:AppxPackageDir=$packageFolder/", "-p:TesterManifest=$manifestPath", "-p:TesterReleaseNotes=$notesFile", "-p:TesterUpdateSource=$sourceFile", "-p:CorrespondingSourceInfo=$sourceInfo")
if (!$ReuseRepositoryBuild) {
    $buildArgs += @("-p:BaseIntermediateOutputPath=$work/obj/", "-p:MSBuildProjectExtensionsPath=$work/obj/", "-p:OutputPath=$work/bin/")
}
else {
    $buildArgs[0] = 'publish'
    $buildArgs += @('--no-build', '-p:PublishAppxPackage=true')
}
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "MSIX build failed. Build files: $work" }
$packages = @(Get-ChildItem -LiteralPath $packageFolder -Recurse -Filter '*.msix' | Where-Object { $_.FullName -notmatch '[\\/]Dependencies[\\/]' })
if ($packages.Count -ne 1) { throw "Expected one self-contained MSIX; found $($packages.Count)." }
[IO.Directory]::CreateDirectory($output) | Out-Null
$packageName = "VRCoplay_$($Version)_x64.msix"
$packagePath = Join-Path $output $packageName
Copy-Item -LiteralPath $packages[0].FullName -Destination $packagePath
& $signTool sign /fd SHA256 /tr $TimestampUri.AbsoluteUri /td SHA256 /s My /sha1 $certificate.Thumbprint $packagePath
if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }
$signature = Get-AuthenticodeSignature -LiteralPath $packagePath
if (!$signature.TimeStamperCertificate) { throw 'The MSIX signature has no timestamp.' }


Export-Certificate -Cert $certificate -FilePath (Join-Path $output 'VRCoplay.Testers.cer') -Type CERT | Out-Null
Copy-Item -LiteralPath $notesFile -Destination $output

$packageUri = [uri]::new($BaseUri, $packageName).AbsoluteUri
$xml = [xml]@"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2021" Version="$Version" Uri="$([Security.SecurityElement]::Escape($installerUri))">
  <MainPackage Name="$identity" Publisher="$([Security.SecurityElement]::Escape($publisher))" Version="$Version" ProcessorArchitecture="x64" Uri="$([Security.SecurityElement]::Escape($packageUri))" />
</AppInstaller>
"@
$xml.Save((Join-Path $output 'VRCoplay.appinstaller'))
$fingerprint = $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
@"
VRCoplay $Version
Windows 11, version 22H2 or later, x64.

Install
1. Download VRCoplay.Testers.cer from $($BaseUri.AbsoluteUri).
2. Open the certificate file.
3. Select Install Certificate, then Local Machine.
4. Select the Trusted People certificate store.
5. Complete the certificate import.
6. Download $installerUri.
7. Open VRCoplay.appinstaller.
8. Select Install.

Certificate SHA-256: $fingerprint
Certificate expiry: $($certificate.NotAfter.ToUniversalTime().ToString('u'))

For updates, open Settings > Updates in VRCoplay.
If Windows cannot open the installer, install Microsoft App Installer:
https://learn.microsoft.com/en-us/windows/msix/app-installer/install-update-app-installer

Copyright (c) 2026 YUCP Studio and contributors.
GPL version 3 or later. No warranty.
Matching source: $($SourceUri.AbsoluteUri)
Source repository: https://github.com/Yeusepe/vrcoplay
Source ZIP SHA-256: $($SourceSha256.ToLowerInvariant())
License texts are in the app and source archive.
"@ | Set-Content -LiteralPath (Join-Path $output 'INSTALL.txt') -Encoding utf8
$sourceLink = [Net.WebUtility]::HtmlEncode($SourceUri.AbsoluteUri)
@"
<!doctype html>
<html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>VRCoplay $Version</title><h1>VRCoplay $Version</h1>
<p>Windows 11 x64. Read the <a href="INSTALL.txt">installation steps</a>.</p>
<ul><li><a href="VRCoplay.Testers.cer">Certificate</a></li>
<li><a href="VRCoplay.appinstaller">Installer</a></li>
<li><a href="$sourceLink">Matching source and licenses</a></li>
<li><a href="https://github.com/Yeusepe/vrcoplay">Source repository</a></li></ul>
<p>Copyright (c) 2026 YUCP Studio and contributors. GPL v3 or later. No warranty.</p>
<p>Source SHA-256: $($SourceSha256.ToLowerInvariant())</p></html>
"@ | Set-Content -LiteralPath (Join-Path $output 'index.html') -Encoding utf8
Get-ChildItem -LiteralPath $output -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Write-Output "Tester release: $output"
