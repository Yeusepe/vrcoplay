// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.Win32;
namespace VRCoplay;
internal static class DolphinProfiles
{
    public static void Install() =>
        Install(Path.Combine(AppContext.BaseDirectory, "Input", "Dolphin"), FindUserDirectory());
    public static void InstallNunchuk() =>
        Install(Path.Combine(AppContext.BaseDirectory, "Input", "Dolphin"), FindUserDirectory(), nunchuk: true);
    internal static void Install(string sourceDirectory, string userDirectory, bool nunchuk = false)
    {
        var files = Enumerable
            .Range(1, 4)
            .Select(player => $"VRCoplay Player {player}{(nunchuk ? " + Nunchuk" : "")}.ini")
            .Select(name => (Name: name, Bytes: File.ReadAllBytes(Path.Combine(sourceDirectory, name))))
            .ToArray();
        var target = Directory.CreateDirectory(Path.Combine(userDirectory, "Config", "Profiles", "Wiimote")).FullName;
        foreach (var file in files)
        {
            var destination = Path.Combine(target, file.Name);
            if (File.Exists(destination))
            {
                if (File.ReadAllBytes(destination).AsSpan().SequenceEqual(file.Bytes))
                    continue;
                File.Copy(destination, $"{destination}.{Guid.NewGuid():N}.bak");
            }
            File.WriteAllBytes(destination, file.Bytes);
        }
    }
    private static string FindUserDirectory()
    {
        foreach (var process in Process.GetProcessesByName("Dolphin"))
            using (process)
                try
                {
                    var folder = Path.GetDirectoryName(process.MainModule?.FileName);
                    if (folder is not null && File.Exists(Path.Combine(folder, "portable.txt")))
                        return Path.Combine(folder, "User");
                }
                catch { }
        var root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Dolphin Emulator", "UserConfigPath", null) as string;
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dolphin Emulator")
            : Environment.ExpandEnvironmentVariables(root);
    }
}
