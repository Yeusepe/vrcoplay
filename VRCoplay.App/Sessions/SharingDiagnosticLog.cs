// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace VRCoplay;
internal sealed class SharingDiagnosticLog(string directory, int maxFileBytes = 256 * 1024)
{
    internal static SharingDiagnosticLog Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCoplay", "Logs")
    );
    private readonly object _gate = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    internal void Write(
        string session,
        string action,
        string? reason = null,
        string? task = null,
        int? childPid = null,
        int? exitCode = null,
        bool? canceled = null,
        Exception? error = null,
        IEnumerable<string>? stderr = null
    )
    {
        try
        {
            var row = new
            {
                utc = DateTimeOffset.UtcNow,
                qpc = Stopwatch.GetTimestamp(),
                qpcFrequency = Stopwatch.Frequency,
                appPid = Environment.ProcessId,
                session,
                action,
                reason = reason is null ? null : KnownReason(reason),
                task,
                childPid,
                exitCode,
                canceled,
                errors = Errors(error),
                stderr = stderr?.TakeLast(12).Select(Summarize).ToArray(),
            };
            var bytes = Utf8.GetBytes(JsonSerializer.Serialize(row) + "\n");
            if (bytes.Length > maxFileBytes)
                return;
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                using var lease = new FileStream(
                    Path.Combine(directory, "sharing.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None
                );
                var path = Path.Combine(directory, "sharing.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > maxFileBytes)
                {
                    if (File.Exists(path + ".1"))
                        File.Move(path + ".1", path + ".2", overwrite: true);
                    File.Move(path, path + ".1", overwrite: true);
                }
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(bytes);
            }
        }
        catch (Exception)
        {
        }
    }
    private static string KnownReason(string reason) => reason switch
    {
        "capture-api" or "start-button" or "stop-button" or "link-rotation" or "play-button"
            or "watch-button" or "window-close" or "sharing-declined" or "control-declined"
            or "completed" or "canceled" or "failed" => reason,
        _ => "unspecified",
    };
    private static object[] Errors(Exception? error)
    {
        var result = new List<object>();
        for (var current = error; current is not null && result.Count < 8; current = current.InnerException)
            result.Add(new
            {
                type = current.GetType().FullName,
                hresult = $"0x{current.HResult:X8}",
                summary = Summarize(current.Message),
            });
        return result.ToArray();
    }
    internal static string Summarize(string text)
    {
        var input = text.AsSpan(0, Math.Min(text.Length, 8192));
        var found = new List<string>();
        foreach (var phrase in DiagnosticPhrases)
            if (input.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                found.Add(phrase);
        return found.Count == 0 ? "Unrecognized diagnostic text omitted" : string.Join("; ", found.Take(6));
    }
    private static readonly string[] DiagnosticPhrases =
    [
        "Unable to load DLL",
        "gstreamer-1.0-0.dll",
        "glib-2.0-0.dll",
        "gobject-2.0-0.dll",
        "The GStreamer runtime is missing",
        "The GStreamer DLL search path could not be configured",
        "The specified module could not be found",
        "The application's Windows volume changed",
        "The application changed its audio level",
        "Quiet local audio stopped",
        "Application audio capture stopped",
        "Some application volumes could not be restored",
        "Another VRCoplay instance is managing application audio",
        "The saved application volumes could not be read",
        "The saved application volumes are invalid",
        "Choose an application first",
        "Controller bridge could not start",
        "Another controller bridge is running",
        "Hmd Not Found",
        "SteamVR closed",
        "OpenVR input error",
        "NoSteam",
        "InvalidHandle",
        "MismatchedActionManifest",
        "Could not start libVIIPER",
        "Could not start Cemuhook",
        "Could not create the controller bus",
        "Could not create a DualShock controller",
        "Could not initialize a DualShock controller",
        "Could not update the DualShock controller",
        "Could not update a remote DualShock controller",
        "Port 8554 is in use",
        "MediaMTX did not start",
        "MediaMTX stopped",
        "The video publisher exited",
        "The video publisher closed its input",
        "No H.264 encoder is",
        "The operation was canceled",
        "A task was canceled",
        "Access is denied",
        "Access to the path",
        "The system cannot find the file",
        "The process cannot access the file",
        "The pipe has been ended",
        "Pipe is broken",
        "Broken pipe",
        "Connection reset",
        "Connection refused",
        "Connection timed out",
        "I/O error",
        "Invalid argument",
        "End of file",
        "Cannot allocate memory",
        "Out of memory",
        "No space left on device",
        "Resource temporarily unavailable",
        "Error during demuxing",
        "Error submitting a packet",
        "Error while opening encoder",
        "Error initializing output stream",
        "Error writing trailer",
        "Error muxing a packet",
        "Conversion failed",
        "Cannot load nvcuda.dll",
        "No capable devices found",
        "No CUDA capable device",
        "OpenEncodeSessionEx failed",
        "InitializeEncoder failed",
        "EncodePicture failed",
        "Device removed",
        "Device lost",
        "DXGI_ERROR_DEVICE_REMOVED",
        "DXGI_ERROR_DEVICE_RESET",
        "DXGI_ERROR_DEVICE_HUNG",
        "Ignoring maximum wav data size",
        "Guessed Channel Layout",
    ];
}
