// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
namespace VRCoplay;
internal static class GStreamerRuntime
{
    private const uint LoadLibrarySearchDefaultDirs = 0x1000;
    private static readonly object Gate = new();
    private static bool _initialized;
    private static nint _dllDirectory;
    internal static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            var root = Path.Combine(AppContext.BaseDirectory, "GStreamer");
            var bin = Path.Combine(root, "bin");
            if (!File.Exists(Path.Combine(bin, "gstreamer-1.0-0.dll")))
                throw new FileNotFoundException("The GStreamer runtime is missing. Reinstall VRCoplay.");
            if (_dllDirectory == 0)
            {
                if (!SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs) || (_dllDirectory = AddDllDirectory(bin)) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The GStreamer DLL search path could not be configured.");
            }
            Environment.SetEnvironmentVariable("GST_PLUGIN_SYSTEM_PATH_1_0", Path.Combine(root, "lib", "gstreamer-1.0"));
            Environment.SetEnvironmentVariable("GST_PLUGIN_PATH_1_0", Path.Combine(root, "lib", "gstreamer-1.0"));
            Environment.SetEnvironmentVariable("GST_PLUGIN_SCANNER_1_0", Path.Combine(bin, "gst-plugin-scanner.exe"));
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCoplay", "gstreamer");
            Directory.CreateDirectory(cache);
            var installation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root))))[..16];
            Environment.SetEnvironmentVariable("GST_REGISTRY_1_0", Path.Combine(cache, $"registry-1.28.7-x64-{installation}.bin"));
            Gst.Application.Init();
            _initialized = true;
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AddDllDirectory(string path);
    internal static string? ErrorDomain(int domain) => Marshal.PtrToStringUTF8(g_quark_to_string(domain));
    [DllImport("glib-2.0-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint g_quark_to_string(int quark);
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
