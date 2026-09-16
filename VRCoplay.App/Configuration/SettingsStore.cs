// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
namespace VRCoplay;
internal static class SettingsStore
{
    internal static bool ControllerSetupSeen =>
        Open().LocalSettings.Values.TryGetValue("controllerSetupSeenV1", out var value) && value is true;
    internal static void MarkControllerSetupSeen() => Open().LocalSettings.Values["controllerSetupSeenV1"] = true;
    private const string SharingExplainedKey = "sharingExplainedV2";
    private const string AutomaticUpdatePromptsKey = "automaticUpdatePromptsV1";
    private const string SkippedUpdateVersionKey = "skippedUpdateVersionV1";
    internal static bool AutomaticUpdatePrompts
    {
        get => !Open().LocalSettings.Values.TryGetValue(AutomaticUpdatePromptsKey, out var value) || value is not false;
        set => Open().LocalSettings.Values[AutomaticUpdatePromptsKey] = value;
    }
    internal static bool ShouldPromptForUpdate(Version version) =>
        AutomaticUpdatePrompts &&
        (!Open().LocalSettings.Values.TryGetValue(SkippedUpdateVersionKey, out var value) ||
            value is not string skipped || !string.Equals(skipped, version.ToString(), StringComparison.Ordinal));
    internal static void SkipUpdateVersion(Version version) =>
        Open().LocalSettings.Values[SkippedUpdateVersionKey] = version.ToString();
    internal static async Task<bool> ConfirmSharingAsync(Func<Task<bool>> confirm)
    {
        var values = Open().LocalSettings.Values;
        if (values.TryGetValue(SharingExplainedKey, out var explained) && explained is true)
            return true;
        if (!await confirm())
            return false;
        RememberSharingExplained();
        return true;
    }
    internal static void RememberSharingExplained() => Open().LocalSettings.Values[SharingExplainedKey] = true;
    internal static Microsoft.Windows.Storage.ApplicationData Open() =>
        Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess().IsPackaged
            ? Microsoft.Windows.Storage.ApplicationData.GetDefault()
            : Microsoft.Windows.Storage.ApplicationData.GetForUnpackaged("VRCoplay", "VRCoplay");
    internal static StreamSettings Load()
    {
        try
        {
            return Open().LocalSettings.Values.TryGetValue("streamSettings", out var value) && value is string json
                ? JsonSerializer.Deserialize<StreamSettings>(json) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }
    internal static void Save(StreamSettings settings) =>
        Open().LocalSettings.Values["streamSettings"] = JsonSerializer.Serialize(settings);
}
