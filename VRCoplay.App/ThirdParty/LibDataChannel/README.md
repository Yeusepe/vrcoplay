# libdatachannel build

Requirements: PowerShell 7.3+, .NET 10, Git, CMake 3.25+, and Visual Studio C++ tools.

Run `./build.ps1 -BuildRoot <build-directory>` from this directory.
Use a new build directory.
The script builds `datachannel.dll` and updates `SHA256SUMS.txt`.
Use `-GenerateBindings` to update `Rtc.g.cs`.

`PIN.json` gives the source revisions and compiler tool versions.
`generate.rsp` selects the declarations from `include/rtc/rtc.h`.
Use the generator to change these declarations.

Native source: libdatachannel 0.24.5 with `dtls-signaling.patch`.
The patch queues DTLS initialization and waits for signaling to finish before
reading the remote fingerprint. Certificate fingerprint verification stays enabled.
Static dependencies: libjuice, OpenSSL, usrsctp, and plog.
OpenSSL source: https://github.com/openssl/openssl/tree/openssl-3.6.4.
OpenSSL patch: `openssl-build-info.patch`.
Licenses and attribution: `LICENSES.txt`.
