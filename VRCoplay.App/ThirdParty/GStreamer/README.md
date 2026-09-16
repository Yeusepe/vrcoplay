# GStreamer runtime

VRCoplay uses GStreamer 1.28.7 for Windows Graphics Capture, GPU conversion,
H.264 encoding, audio mixing and RTSP publishing. It uses the system Direct2D
renderer for overlays and NAudio for process loopback audio.

`runtime.json` pins the official GStreamer Python wheels from PyPI, their SHA-256
checksums, and the exact files shipped. The wheels are archives only; Python is
not required to build or run VRCoplay. `Prepare-Runtime.ps1` restores the selected
files with Windows PowerShell 5.1. MSBuild invokes it through its absolute Windows
path, including when Visual Studio has no PowerShell 7 on PATH. The generated `runtime`
directory is excluded from source control and source exports.

The bundle contains the core, app, Direct3D11, NVIDIA, AMD, Intel, x264, H.264
parser, RTSP/RTP, TCP/UDP, audio conversion/mixer/resampler plugins and their
native dependencies. The Media Foundation plugin uses Windows' AAC encoder for
Quest playback; the RTSP plugin receives the public stream without another video
encode. It does not load plugins from a machine-wide installation.

Upstream: https://gstreamer.freedesktop.org/

Source and build recipes: https://gitlab.freedesktop.org/gstreamer/cerbero/-/tree/1.28.7

The managed bindings are GstSharpBundle 1.24.3-preview6 (restored from NuGet).
The native runtime is restored separately, rather than using the older native
runtime from that package family.

## Dependencies and distribution

The selected native files also include GLib 2.82.4 (LGPL-2.0-or-later),
proxy-libintl 0.5 (LGPL-2.0-or-later), libffi 3.2.9999.5 (MIT), PCRE2 10.42
(BSD-3-Clause), Orc 0.4.42 (BSD-3-Clause), zlib 1.3.1 (zlib), and x264
0.164.3108 at 31e19f9 (GPL-2.0-or-later). Build headers include DirectX-Headers
1.611.0 and DirectXMath 3.1.9/feb2024 (MIT). License texts are in `LICENSES`.
The MSVC runtime DLLs come from the upstream Windows wheels; Microsoft runtime
redistribution terms apply. Exact dependency recipes and patches are pinned by
the Cerbero 1.28.7 source tree linked above.

Release source archives must contain the corresponding GStreamer/component
sources and build recipes required by their licenses. The source-export checks
require the GStreamer source group. The manifest alone does not replace sources.

## Media behavior

RTSP TCP appsinks use `processing-deadline=0`, retaining clock synchronization.
Public and private overlay branches receive separate GPU textures before drawing.
Window resizing refreshes video capture without reopening process audio. Audio
uses WASAPI sample timestamps; GStreamer's live mixer maintains silence while a
source is inactive. Capture input is bounded at 60 ms. Output audio queues allow
two video frame intervals plus 60 ms, so low frame rates do not discard audio
while RTSP waits for the shared presentation clock. Queue capacity adds no
deliberate playback delay.

The video compositor retains its default inactive-pad handling. Skipping inactive
pads could leave a newly attached, static WGC source black at 60 fps despite its
first captured frame containing the picture. The live compositor keeps standby
running while waiting for capture.

Quest output keeps H.264 video compressed and uses Media Foundation AAC with
the MPEG4-GENERIC RTP payloader. AVC caps provide H.264 headers before RTSP
ANNOUNCE. The ready signal follows the RTSP sink's internal streaming bin entering
PLAYING after a successful RECORD response; this is tested against the pinned
1.28.7 runtime.

NAudio remains responsible for float process capture and Windows audio-session
volume restoration. Its capture delay can also occur before Windows delivers the
first packet; GStreamer's silence output does not cure that Windows startup delay.
