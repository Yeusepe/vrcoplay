# Win2D public-source build

VRCoplay uses `VRCoplay.Win2D` `1.4.0-public.25680382.3`, a local Windows x64
package built from Microsoft Win2D commit
[`25680382dd2136779e10ea6084f0c5ba437ae288`](https://github.com/microsoft/Win2D/tree/25680382dd2136779e10ea6084f0c5ba437ae288).
It is not an official Microsoft binary. The upstream MIT license is retained in
the source archive, local package, and `../../Tools/Win2D-LICENSE.txt`.

## Build

Requires PowerShell 7, Git, .NET SDK 10, Visual Studio 2022 C++ x64 tools (v143),
and Windows SDK 10.0.26100.0. From the repository root:

```powershell
pwsh -File VRCoplay.App/ThirdParty/Win2D/build.ps1
```

`tools/Restore-Native.ps1` also runs this recipe. It verifies the source archive
and NuGet tool against `PIN.json`, applies `changes.patch`, then builds only the
Release x64 native DLL and the .NET 8 Windows projection. The public
`winrt/inc/MicrosoftTelemetry.h` is verified and never replaced by the internal
Microsoft telemetry package. The stub does not disable Win2D's ordinary ETW events.

The patch adapts the old UWP build to desktop C++ tools: explicit WinRT metadata
generation, ABI namespace and scoped enums, metadata/include paths, and removal
of the unused UWP VCLibs SDK packaging reference. The recipe retains upstream's
hybrid CRT linkage, uses Unicode, and disables UWP app-container packaging.
The native DLL embeds only its PDB filename. The managed projection omits Release symbols and maps source paths. The recipe rejects profile paths or absolute PDB references before packing.
The managed projection targets Windows 22621/.NET 8, consumes the freshly built
metadata, restores the required Windows App SDK components, and uses SDK 10.
Win2D's C++/IDL implementation and public telemetry header are unchanged.

Output: `artifacts/VRCoplay.Win2D.1.4.0-public.25680382.3.nupkg` and a `build.json`
receipt with input recipe, tool versions and output hashes. Source and
build output stay in `%LOCALAPPDATA%/VRCoplay/source-build/`. Source mapping in
`../../NuGet.Config` restricts this package ID to the local feed. App restore has
no fallback to the official Win2D package. Only x64 is supplied.

To modify Win2D, update the patch and increment the version in `PIN.json`, the
nuspec and the app project before rebuilding. Do not overwrite a version already
restored into NuGet's global cache. `-Rebuild` reruns the recipe in a fresh folder;
`-CacheDirectory` chooses its parent directory. No files are published.

## Source distribution

Keep this recipe, pin, patch and nuspec with the app source. Supply the pinned
upstream archive alongside the app source when distributing binaries that use
this package; it contains Win2D's source, project files, interfaces and notices.
Review the final payload's other dependency-source obligations separately.
