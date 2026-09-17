// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Windows.ApplicationModel;
using Windows.Storage;
namespace VRCoplay;
internal sealed class AppUpdates : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    internal UpdateIdentity? Identity { get; }
    internal Uri? Source { get; }
    internal bool IsStoreManaged { get; }
    internal string CurrentVersion => Identity?.Version.ToString() ?? "Development build";
    internal AppUpdates()
    {
        if (!Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess().IsPackaged) return;
        var package = Package.Current;
        var id = package.Id;
        Identity = new(id.Name, id.Publisher, id.Architecture.ToString().ToLowerInvariant(),
            new(id.Version.Major, id.Version.Minor, id.Version.Build, id.Version.Revision));
        IsStoreManaged = package.SignatureKind == PackageSignatureKind.Store ||
            id.FamilyName == "YUCPStudio.VRCoplay_w1zj3ynvdat22";
        if (IsStoreManaged) return;
        try { Source = package.GetAppInstallerInfo()?.Uri; }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        if (Source is not null) return;
        var path = Path.Combine(AppContext.BaseDirectory, "update-source.json");
        if (!File.Exists(path)) return;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (json.RootElement.TryGetProperty("appInstallerUri", out var value) &&
            Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == "https") Source = uri;
    }
    internal Task<UpdateRelease?> CheckAsync(CancellationToken cancellation) =>
        Source is null || Identity is null ? Task.FromResult<UpdateRelease?>(null) : new UpdateFeed(_http).CheckAsync(Source, Identity, cancellation);
    internal static async Task<bool> OpenInstallerAsync(UpdateRelease release)
    {
        var folder = await SettingsStore.Open().LocalCacheFolder.CreateFolderAsync("Updates", CreationCollisionOption.OpenIfExists);
        var file = await folder.CreateFileAsync("VRCoplay.appinstaller", CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteTextAsync(file, release.InstallerXml);
        return await Windows.System.Launcher.LaunchFileAsync(file);
    }
    internal static string InstalledNotes()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "release-notes.json")));
            return json.RootElement.GetProperty("notes").GetString() ?? "";
        }
        catch { return "Release notes aren’t available for this build."; }
    }
    public void Dispose() => _http.Dispose();
}
