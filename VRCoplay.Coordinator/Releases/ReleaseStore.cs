// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
public sealed partial class ReleaseStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public const string PackageIdentity = "9A28BE70-8187-4B43-9829-CA935ADB1B00";
    public const string Publisher = "CN=VRCoplay";
    public const string UnityPackageName = "VRCoplay.Pointer.unitypackage";
    public static Version? ParseVersion(string text) => VersionPattern().IsMatch(text) &&
        Version.TryParse(text, out var version) && version.Major <= 65535 && version.Minor <= 65535 &&
        version.Build <= 65535 && version.Revision <= 65535 ? version : null;
    public static string[] Files(Version version) =>
        [$"VRCoplay_{version}_x64.msix", "VRCoplay.appinstaller", "VRCoplay.Testers.cer", "release-notes.json", "INSTALL.txt", "index.html", "SHA256SUMS.txt", UnityPackageName];
    public static bool IsPublicFile(string name)
    {
        if (name is "VRCoplay.appinstaller" or "VRCoplay.Testers.cer" or "release-notes.json" or "INSTALL.txt" or "index.html" or "SHA256SUMS.txt" or UnityPackageName) return true;
        var match = PackagePattern().Match(name);
        return match.Success && ParseVersion(match.Groups[1].Value) is not null;
    }
    public string? Resolve(string name)
    {
        if (!FileNamePattern().IsMatch(name) || !IsPublicFile(name)) return null;
        var pointer = Path.Combine(DirectoryPath, "current.txt");
        if (!RegularFile(pointer) || new FileInfo(pointer).Length > 64) return null;
        if (ParseVersion(File.ReadAllText(pointer).Trim()) is not { } version ||
            !Files(version).Contains(name, StringComparer.Ordinal)) return null;
        var versions = Path.Combine(DirectoryPath, "versions");
        var folder = Path.Combine(versions, version.ToString());
        var candidate = Path.Combine(folder, name);
        return PlainDirectory(versions) && PlainDirectory(folder) &&
            RegularFile(Path.Combine(folder, UnityPackageName)) && RegularFile(candidate) ? candidate : null;
    }
    public Version? CurrentVersion()
    {
        var feed = Resolve("VRCoplay.appinstaller");
        return feed is null ? null : ParseVersion((string?)ReadXml(feed).Root?.Attribute("Version") ?? "");
    }
    public void ValidateAndPromote(string staging, Version version, Uri baseUri)
    {
        if (ParseVersion(version.ToString()) is null) throw new InvalidDataException("Invalid MSIX version.");
        var expected = Files(version);
        var actual = Directory.GetFiles(staging).Select(Path.GetFileName).ToArray();
        if (actual.Length != expected.Length || actual.Except(expected, StringComparer.Ordinal).Any())
            throw new InvalidDataException("The release contains unexpected or missing assets.");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(staging, "SHA256SUMS.txt")))
        {
            var match = HashPattern().Match(line);
            if (!match.Success || !hashes.TryAdd(match.Groups[2].Value, match.Groups[1].Value))
                throw new InvalidDataException("Invalid release checksums.");
        }
        if (hashes.Count != expected.Length - 1) throw new InvalidDataException("Missing release checksums.");
        foreach (var name in expected.Where(n => n != "SHA256SUMS.txt"))
        {
            var file = Path.Combine(staging, name);
            if (!RegularFile(file) || !hashes.TryGetValue(name, out var hash) || !Hash(file).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release asset checksum mismatch.");
        }
        var installer = ReadXml(Path.Combine(staging, "VRCoplay.appinstaller"));
        XNamespace ns = "http://schemas.microsoft.com/appx/appinstaller/2021";
        var root = installer.Root;
        var package = root?.Element(ns + "MainPackage");
        if (root?.Name != ns + "AppInstaller" || (string?)root.Attribute("Version") != version.ToString() ||
            (string?)root.Attribute("Uri") != new Uri(baseUri, "VRCoplay.appinstaller").AbsoluteUri ||
            package is null || (string?)package.Attribute("Name") != PackageIdentity ||
            (string?)package.Attribute("Publisher") != Publisher || (string?)package.Attribute("Version") != version.ToString() ||
            (string?)package.Attribute("ProcessorArchitecture") != "x64" ||
            (string?)package.Attribute("Uri") != new Uri(baseUri, $"VRCoplay_{version}_x64.msix").AbsoluteUri ||
            root.Elements().Any(e => e.Name != ns + "MainPackage") ||
            root.Elements(ns + "MainPackage").Count() != 1)
            throw new InvalidDataException("The release feed has an unexpected identity, address or update policy.");
        using var notes = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "release-notes.json")));
        if (notes.RootElement.GetProperty("version").GetString() != version.ToString() ||
            notes.RootElement.GetProperty("notes").GetString() is not { Length: > 0 and <= 20000 })
            throw new InvalidDataException("Release notes do not match this version.");
        var current = CurrentVersion();
        if (current is not null && current >= version) throw new InvalidDataException("A release must have a higher version.");
        var currentFeed = Resolve("VRCoplay.appinstaller");
        if (currentFeed is not null)
        {
            var previous = ReadXml(currentFeed).Root!;
            var previousPackage = previous.Element(ns + "MainPackage");
            if ((string?)previous.Attribute("Uri") != (string?)root.Attribute("Uri") ||
                (string?)previousPackage?.Attribute("Name") != PackageIdentity ||
                (string?)previousPackage?.Attribute("Publisher") != Publisher)
                throw new InvalidDataException("Do not change the installed app identity or feed address.");
        }
        var currentCert = Resolve("VRCoplay.Testers.cer");
        if (currentCert is not null && Hash(currentCert) != Hash(Path.Combine(staging, "VRCoplay.Testers.cer")))
            throw new InvalidDataException("Signing certificate changed. Plan tester trust migration before publishing.");
        var versions = Path.Combine(DirectoryPath, "versions");
        Directory.CreateDirectory(versions);
        if (!PlainDirectory(versions)) throw new InvalidDataException("The release directory cannot be a symbolic link.");
        var destination = Path.Combine(versions, version.ToString());
        if (Directory.Exists(destination))
        {
            if (!PlainDirectory(destination) || expected.Any(n => !RegularFile(Path.Combine(destination, n)) || Hash(Path.Combine(destination, n)) != Hash(Path.Combine(staging, n))))
                throw new InvalidDataException("This version already exists with different contents.");
        }
        else Directory.Move(staging, destination);
        var temporary = Path.Combine(DirectoryPath, ".current-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(temporary, version.ToString());
            File.Move(temporary, Path.Combine(DirectoryPath, "current.txt"), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static XDocument ReadXml(string file)
    {
        using var reader = XmlReader.Create(file, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 });
        return XDocument.Load(reader);
    }
    private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool RegularFile(string file) => File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0;
    private static bool PlainDirectory(string folder) => Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0;
    [GeneratedRegex(@"\A(?:0|[1-9][0-9]{0,4})(?:\.(?:0|[1-9][0-9]{0,4})){3}\z")]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"\AVRCoplay_([0-9.]+)_x64\.msix\z")]
    private static partial Regex PackagePattern();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,150}\z")]
    private static partial Regex FileNamePattern();
    [GeneratedRegex(@"\A([a-fA-F0-9]{64})  ([A-Za-z0-9][A-Za-z0-9._-]{0,150})\z")]
    private static partial Regex HashPattern();
}
