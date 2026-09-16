#requires -Version 7.3
param([Parameter(Mandatory)][string]$BuildRoot, [switch]$GenerateBindings)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
$pathMap = '/experimental:deterministic /pathmap:"' + $BuildRoot + '"=/_'
$pin = Get-Content -LiteralPath "$PSScriptRoot/PIN.json" -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null
foreach ($dependency in @(@{Name='source'; Url=$pin.source; Commit=$pin.commit}, @{Name='vcpkg'; Url='https://github.com/microsoft/vcpkg'; Commit=$pin.vcpkg})) {
    $path = Join-Path $BuildRoot $dependency.Name
    if (!(Test-Path -LiteralPath $path)) { git clone $dependency.Url $path }
    if (git -C $path status --porcelain) { throw "Uncommitted changes in $path" }
    git -C $path checkout --detach $dependency.Commit
    git -C $path submodule update --init --recursive
}
git -C "$BuildRoot/source" apply (Join-Path $PSScriptRoot $pin.patch)
& "$BuildRoot/vcpkg/bootstrap-vcpkg.bat" -disableMetrics
$triplets = Join-Path $BuildRoot 'triplets'
[IO.Directory]::CreateDirectory($triplets) | Out-Null
$triplet = Get-Content -LiteralPath "$BuildRoot/vcpkg/triplets/x64-windows-static.cmake" -Raw
$triplet += "`nset(VCPKG_C_FLAGS [[$pathMap]])`nset(VCPKG_CXX_FLAGS [[$pathMap]])`n"
Set-Content -LiteralPath "$triplets/x64-windows-static.cmake" -Value $triplet
$ports = Join-Path $BuildRoot 'ports'
[IO.Directory]::CreateDirectory($ports) | Out-Null
Copy-Item -LiteralPath "$BuildRoot/vcpkg/ports/openssl" -Destination $ports -Recurse -Force
Copy-Item -LiteralPath "$PSScriptRoot/openssl-build-info.patch" -Destination "$ports/openssl/"
$portfile = Get-Content -LiteralPath "$ports/openssl/portfile.cmake" -Raw
$portfile = $portfile.Replace('    PATCHES', "    PATCHES`n        openssl-build-info.patch")
Set-Content -LiteralPath "$ports/openssl/portfile.cmake" -Value $portfile
& "$BuildRoot/vcpkg/vcpkg.exe" install openssl:x64-windows-static "--overlay-triplets=$triplets" "--overlay-ports=$ports" --disable-metrics
cmake -S "$BuildRoot/source" -B "$BuildRoot/build" -A x64 -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_POLICY_DEFAULT_CMP0141=NEW -DCMAKE_MSVC_DEBUG_INFORMATION_FORMAT=Embedded -DBUILD_SHARED_LIBS=ON -DBUILD_SHARED_DEPS_LIBS=OFF -DNO_MEDIA=ON -DNO_WEBSOCKET=ON -DNO_EXAMPLES=ON -DNO_TESTS=ON -DOPENSSL_USE_STATIC_LIBS=ON "-DOPENSSL_ROOT_DIR=$BuildRoot/vcpkg/installed/x64-windows-static" -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded -U CMAKE_C_FLAGS -U CMAKE_CXX_FLAGS -U CMAKE_C_FLAGS_DEBUG -U CMAKE_CXX_FLAGS_DEBUG "-DCMAKE_C_FLAGS_INIT=$pathMap" "-DCMAKE_CXX_FLAGS_INIT=$pathMap"
cmake --build "$BuildRoot/build" --config Release --target datachannel --parallel
if ($GenerateBindings) {
    if (!(Test-Path -LiteralPath "$BuildRoot/tools/ClangSharpPInvokeGenerator.cmd")) { dotnet tool install ClangSharpPInvokeGenerator --version $pin.generator --tool-path "$BuildRoot/tools" }
    & "$BuildRoot/tools/ClangSharpPInvokeGenerator.cmd" "@$PSScriptRoot/generate.rsp" -f "$BuildRoot/source/include/rtc/rtc.h" -o "$PSScriptRoot/Rtc.g.cs"
}
Copy-Item -LiteralPath "$BuildRoot/build/Release/datachannel.dll" -Destination "$PSScriptRoot/datachannel.dll"
Get-FileHash -LiteralPath "$PSScriptRoot/datachannel.dll", "$PSScriptRoot/Rtc.g.cs" -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } | Set-Content -LiteralPath "$PSScriptRoot/SHA256SUMS.txt"
