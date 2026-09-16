// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
namespace VRCoplay;
internal sealed record VideoEncoder(string Name, string Factory, Func<int, int?, string> Options, bool Software = false)
{
    internal string Pipeline(int fps, int? quality = null, string properties = "")
    {
        if (!Regex.IsMatch(properties, @"\A\s*(?:[a-z][a-z0-9-]*=[a-zA-Z0-9_.-]+\s*)*\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Encoder properties must be space-separated name=value pairs.");
        return (Software ? "d3d11download ! video/x-raw,format=NV12 ! " : "") +
            $"{Factory} {Options(Math.Max(1, (fps + 2) / 4), quality)} {properties} ! video/x-h264,profile=constrained-baseline";
    }
    internal static readonly VideoEncoder[] All =
    [
        new("NVIDIA NVENC", "nvd3d11h264enc", (g, q) => $"preset=p1 tune=ultra-low-latency rc-mode=constqp qp-const-i={q ?? 25} qp-const-p={q ?? 25} gop-size={g} bframes=0 zerolatency=true rc-lookahead=0"),
        new("AMD AMF", "amfh264enc", (g, q) => $"usage=ultra-low-latency preset=speed gop-size={g} b-frames=0 pre-analysis=false pre-encode=false " + (q is null ? "rate-control=cbr bitrate=8000 max-bitrate=8000" : $"rate-control=cqp qp-i={q} qp-p={q}")),
        new("Intel Quick Sync", "qsvh264enc", (g, q) => $"target-usage=7 low-latency=true rate-control=cqp qp-i={q ?? 25} qp-p={q ?? 25} gop-size={g} b-frames=0"),
        new("x264 software", "x264enc", (g, q) => $"speed-preset=ultrafast tune=zerolatency pass=quant quantizer={q ?? 25} key-int-max={g} bframes=0 rc-lookahead=0", true),
    ];
    internal static Task<VideoEncoder> PickAsync(int choice) => Task.Run(() =>
    {
        GStreamerRuntime.Initialize();
        if (choice < 0 || choice > All.Length) throw new ArgumentOutOfRangeException(nameof(choice));
        string lastError = "";
        foreach (var profile in choice == 0 ? All : All.Skip(choice - 1).Take(1))
        {
            Gst.Element? pipeline = null;
            try
            {
                pipeline = Gst.Parse.Launch("d3d11testsrc num-buffers=1 ! video/x-raw(memory:D3D11Memory),format=NV12,width=256,height=256,framerate=60/1 ! " + profile.Pipeline(60) + " ! fakesink sync=false");
                pipeline.SetState(Gst.State.Playing);
                using var bus = pipeline.Bus;
                using var message = bus.TimedPopFiltered(3_000_000_000, Gst.MessageType.Error | Gst.MessageType.Eos);
                if (message?.Type == Gst.MessageType.Eos) return profile;
                if (message?.Type == Gst.MessageType.Error) { message.ParseError(out var error, out _); lastError = error.Message; }
                else lastError = "The encoder did not produce a frame within three seconds.";
            }
            catch (Exception error) { lastError = error.Message; }
            finally { pipeline?.SetState(Gst.State.Null); pipeline?.Dispose(); }
        }
        throw new InvalidOperationException($"{(choice == 0 ? "No H.264 encoder is" : All[choice - 1].Name + " is not")} available. {lastError}");
    });
}
