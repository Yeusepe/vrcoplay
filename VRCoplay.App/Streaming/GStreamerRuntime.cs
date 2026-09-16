// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
namespace VRCoplay;
internal static class GStreamerRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;
    internal static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            var root = Path.Combine(AppContext.BaseDirectory, "GStreamer");
            var bin = Path.Combine(root, "bin");
            if (!File.Exists(Path.Combine(bin, "gstreamer-1.0-0.dll")))
                throw new FileNotFoundException("The GStreamer runtime is missing. Reinstall VRCoplay.");
            Environment.SetEnvironmentVariable("PATH", bin + ";" + Environment.GetEnvironmentVariable("PATH"));
            Environment.SetEnvironmentVariable("GST_PLUGIN_SYSTEM_PATH_1_0", Path.Combine(root, "lib", "gstreamer-1.0"));
            Environment.SetEnvironmentVariable("GST_PLUGIN_PATH_1_0", Path.Combine(root, "lib", "gstreamer-1.0"));
            Environment.SetEnvironmentVariable("GST_PLUGIN_SCANNER_1_0", Path.Combine(root, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe"));
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCoplay", "gstreamer");
            Directory.CreateDirectory(cache);
            Environment.SetEnvironmentVariable("GST_REGISTRY_1_0", Path.Combine(cache, "registry-1.28.7-x64.bin"));
            Gst.Application.Init();
            _initialized = true;
        }
    }
    internal static Gst.Pipeline CreatePipeline(string graph)
    {
        Initialize();
        var pipeline = (Gst.Pipeline)Gst.Parse.Launch(graph);
        pipeline.DeepElementAdded += (_, args) =>
        {
            if (args.Element.Factory?.Name != "appsink") return;
            using var value = new GLib.Value((ulong)0);
            args.Element.SetProperty("processing-deadline", value);
        };
        return pipeline;
    }
    internal sealed class TimerResolution : IDisposable
    {
        private readonly bool _active = timeBeginPeriod(1) == 0;
        public void Dispose() { if (_active) timeEndPeriod(1); }
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);
    }
}
