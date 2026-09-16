# Internet Hosting Tool build

Requirements: Git, Visual Studio C++ tools, and CMake 3.28+.

Run these commands from this directory:

```powershell
$patch = (Resolve-Path ./changes.patch).Path
$pin = Get-Content ./PIN.json | ConvertFrom-Json
$work = Read-Host 'Build directory'
$source = Join-Path $work 'source'
git clone --no-checkout $pin.project $source
git -C $source checkout $pin.baseCommit
git -C $source apply $patch
cmake -S $source -B "$work/build" -A x64
cmake --build "$work/build" --config Release --target iht
```

`PIN.json` gives the base revision, dependency revisions, patch, hashes, and build options.
Source hashes use LF line endings. Patch and DLL hashes use exact file bytes.
Output: `<build-directory>/build/Release/iht.dll`.
`mapping.h` defines the C interface.

License: GPL-3.0. Read `LICENSE`.
Dependency notices: `miniupnpc.txt` and `libnatpmp.txt`.
