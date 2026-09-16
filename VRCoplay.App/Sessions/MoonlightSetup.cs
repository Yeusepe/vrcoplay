// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;
namespace VRCoplay;
internal static class MoonlightSetup
{
    internal static readonly PortablePackage Package = new("6.1.0",
        new("https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/MoonlightPortable-x64-6.1.0.zip"),
        "95f4d0853a31c7fced4b6d233ddf55ee41720963f2e2620a9cb49a21d112aed1");
    private static readonly HttpClient DownloadClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    internal static readonly PortableToolSetup Default = new(DownloadClient,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCoplay", "Moonlight"),
        FindInstalled, Package);
    private static string? FindInstalled()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Moonlight.exe");
                    if (key?.GetValue(null) is string value && Existing(value.Trim('"')) is { } exe) return exe;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        foreach (var directory in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Moonlight Game Streaming"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Moonlight Game Streaming"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Moonlight Game Streaming"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moonlight Game Streaming"),
            Path.Combine(AppContext.BaseDirectory, "Moonlight"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "Moonlight"),
        })
            if (Existing(Path.Combine(directory, "Moonlight.exe")) is { } exe) return exe;
        return null;
        static string? Existing(string path) => Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
    }
}
