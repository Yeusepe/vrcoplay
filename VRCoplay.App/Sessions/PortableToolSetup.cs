// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
namespace VRCoplay;
internal sealed record PortableTool(string Executable, string WorkingDirectory);
internal sealed record PortablePackage(string Version, Uri Url, string Sha256,
    string Executable = "Moonlight.exe", string Name = "Moonlight");
internal sealed class PortableToolSetup(HttpClient http, string root, Func<string?> findInstalled,
    PortablePackage package, bool portableProfile = true)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = Path.GetFullPath(root);
    private const long MaxArchiveBytes = 64 * 1024 * 1024;
    private const long MaxExpandedBytes = 256 * 1024 * 1024;
    private const string ManifestName = "vrcoplay-install.json";
    private sealed record InstalledFile(string Path, long Length);
    private sealed record Installation(string Sha256, InstalledFile[] Files);
    internal async Task<PortableTool> EnsureAsync(IProgress<string>? progress, CancellationToken stop)
    {
        await _gate.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            stop.ThrowIfCancellationRequested();
            if (findInstalled() is { } existing && File.Exists(existing))
                return new(existing, Path.GetDirectoryName(Path.GetFullPath(existing))!);
            var destination = ChildPath(_root, package.Version);
            if (!Complete(destination))
                await InstallAsync(destination, progress, stop).ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            if (!portableProfile) return new(ChildPath(destination, package.Executable), Path.GetDirectoryName(ChildPath(destination, package.Executable))!);
            var profile = Directory.CreateDirectory(ChildPath(_root, "Profile")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(profile, "portable.dat"), [], stop).ConfigureAwait(false);
            return new(ChildPath(destination, package.Executable), profile);
        }
        finally { _gate.Release(); }
    }
    private bool Complete(string directory)
    {
        try
        {
            var installation = JsonSerializer.Deserialize<Installation>(File.ReadAllText(Path.Combine(directory, ManifestName)));
            return installation?.Sha256 == package.Sha256 && installation.Files is { Length: > 0 } &&
                installation.Files.Any(file => file is { Length: > 0 } && file.Path == package.Executable) &&
                installation.Files.All(file => file is not null &&
                    new FileInfo(ChildPath(directory, file.Path)) is { Exists: true } info && info.Length == file.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }
    private async Task InstallAsync(string destination, IProgress<string>? progress, CancellationToken stop)
    {
        Directory.CreateDirectory(_root);
        var staging = ChildPath(_root, ".setup-" + Guid.NewGuid().ToString("N"));
        var archive = staging + ".zip";
        try
        {
            progress?.Report($"Downloading {package.Name}…");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                using var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                if (total > MaxArchiveBytes) throw new InvalidDataException($"The {package.Name} download is larger than expected.");
                await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long received = 0;
                var lastPercent = -1;
                int length;
                while ((length = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    received += length;
                    if (received > MaxArchiveBytes) throw new InvalidDataException($"The {package.Name} download is larger than expected.");
                    hash.AppendData(buffer, 0, length);
                    await output.WriteAsync(buffer.AsMemory(0, length), deadline.Token).ConfigureAwait(false);
                    if (total is > 0)
                    {
                        var percent = (int)Math.Min(100, received * 100 / total.Value);
                        if (lastPercent < 0 || percent / 5 != lastPercent / 5)
                        {
                            lastPercent = percent;
                            progress?.Report($"Downloading Moonlight… {percent}%");
                        }
                    }
                }
                progress?.Report($"Checking {package.Name}…");
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The {package.Name} download could not be verified. Try Play again to download a fresh copy.");
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                throw new IOException($"The {package.Name} download timed out. Check your connection, then try Play again.");
            }
            catch (HttpRequestException error)
            {
                System.Diagnostics.Debug.WriteLine(error);
                throw new IOException($"Couldn’t download {package.Name}. Check your connection, then try Play again.");
            }
            progress?.Report($"Preparing {package.Name}…");
            Directory.CreateDirectory(staging);
            var files = new List<InstalledFile>();
            using (var zip = ZipFile.OpenRead(archive))
            {
                long expanded = 0;
                foreach (var entry in zip.Entries)
                {
                    stop.ThrowIfCancellationRequested();
                    var path = ChildPath(staging, entry.FullName);
                    if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                        throw new InvalidDataException($"The {package.Name} package contains an unsupported link.");
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }
                    expanded = checked(expanded + entry.Length);
                    if (expanded > MaxExpandedBytes) throw new InvalidDataException($"The {package.Name} package is larger than expected.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using var source = entry.Open();
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await source.CopyToAsync(output, stop).ConfigureAwait(false);
                    if (output.Length != entry.Length) throw new InvalidDataException($"The {package.Name} package is incomplete.");
                    files.Add(new(entry.FullName, entry.Length));
                }
            }
            if (!files.Any(file => file.Path == package.Executable && file.Length > 0))
                throw new InvalidDataException($"The {package.Name} package is missing its application.");
            await File.WriteAllTextAsync(Path.Combine(staging, ManifestName),
                JsonSerializer.Serialize(new Installation(package.Sha256, files.ToArray())), stop).ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            DeleteOwnedDirectory(destination);
            Directory.Move(staging, destination);
        }
        finally
        {
            DeleteOwnedDirectory(staging);
            if (File.Exists(archive)) File.Delete(archive);
        }
    }
    private static string ChildPath(string parent, string relative)
    {
        var prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative));
        if (relative.Contains(':') || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The portable package contains an invalid path.");
        return path;
    }
    private void DeleteOwnedDirectory(string directory)
    {
        var path = ChildPath(_root, Path.GetRelativePath(_root, directory));
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
