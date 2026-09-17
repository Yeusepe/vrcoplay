// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal sealed class StreamPipelineException(string? element, string? domain, int code, string message)
    : InvalidOperationException(message)
{
    internal string Element { get; } = element switch
    {
        "appsrc" or "appsink" or "audiomixer" or "audioconvert" or "audioresample"
        or "d3d11testsrc" or "d3d11screencapturesrc" or "d3d11compositor" or "d3d11convert"
        or "d3d11overlay" or "d3d11download" or "nvd3d11h264enc" or "amfh264enc" or "qsvh264enc"
        or "x264enc" or "h264parse" or "rtspclientsink" or "rtspsrc" or "rtpbin" or "rtpsession"
        or "rtph264pay" or "rtpL16pay" or "queue" or "tee" or "capsfilter" => element,
        _ => "other",
    };
    internal string Domain { get; } = domain switch
    {
        "gst-core-error-quark" => "core",
        "gst-library-error-quark" => "library",
        "gst-resource-error-quark" => "resource",
        "gst-stream-error-quark" => "stream",
        _ => "other",
    };
    internal int Code { get; } = code;
}
