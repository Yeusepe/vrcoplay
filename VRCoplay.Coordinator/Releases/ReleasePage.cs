// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
public sealed partial class ReleasePage(ReleaseStore store)
{
    private static readonly string Template = LoadTemplate();
    private readonly object gate = new();
    private string? cachedPath;
    private byte[] page = [], checksums = [];
    public byte[]? Get(string name)
    {
        var snapshot = store.Resolve("index.html");
        if (snapshot is null) return null;
        lock (gate)
        {
            if (cachedPath != snapshot)
            {
                var folder = Path.GetDirectoryName(snapshot)!;
                var version = ReleaseStore.ParseVersion(Path.GetFileName(folder)) ?? throw new InvalidDataException("Invalid release version.");
                var instructions = ReadText(folder, "INSTALL.txt");
                if (Field(instructions, "VRCoplay ") != version.ToString()) throw new InvalidDataException("Installation metadata does not match the release.");
                var source = Field(instructions, "Matching source: ");
                if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) || sourceUri.Scheme != "https" || sourceUri.UserInfo.Length != 0)
                    throw new InvalidDataException("Invalid corresponding-source address.");
                var sourceHash = Field(instructions, "Source ZIP SHA-256: ");
                if (!Digest().IsMatch(sourceHash)) throw new InvalidDataException("Invalid corresponding-source checksum.");
                var fingerprint = Convert.ToHexString(SHA256.HashData(ReadBytes(folder, "VRCoplay.Testers.cer")));
                var archivedPage = ReadText(folder, "index.html");
                var unityVersion = UnityMetadata().Match(archivedPage);
                if (!unityVersion.Success) unityVersion = LegacyUnityMetadata().Match(archivedPage);
                if (!unityVersion.Success) throw new InvalidDataException("Missing Unity version metadata.");
                var unity = new FileInfo(Path.Combine(folder, ReleaseStore.UnityPackageName));
                if (!unity.Exists || (unity.Attributes & FileAttributes.ReparsePoint) != 0 || unity.Length is <= 0 or > 67108864)
                    throw new InvalidDataException("Invalid Unity package.");
                var size = unity.Length < 1048576 ? $"{Math.Ceiling(unity.Length / 1000d).ToString(CultureInfo.InvariantCulture)} KB" :
                    $"{(unity.Length / 1000000d).ToString("0.#", CultureInfo.InvariantCulture)} MB";
                var html = Template.Replace("{{VERSION}}", version.ToString())
                    .Replace("{{SOURCE_URI}}", WebUtility.HtmlEncode(sourceUri.AbsoluteUri))
                    .Replace("{{SOURCE_SHA256}}", sourceHash.ToLowerInvariant())
                    .Replace("{{FINGERPRINT}}", fingerprint)
                    .Replace("{{UNITY_VERSION}}", unityVersion.Groups[1].Value)
                    .Replace("{{UNITY_SIZE}}", size);
                var rendered = Encoding.UTF8.GetBytes(html);
                var hash = Convert.ToHexString(SHA256.HashData(rendered)).ToLowerInvariant();
                var originalChecksums = ReadText(folder, "SHA256SUMS.txt");
                if (PageChecksum().Matches(originalChecksums).Count != 1) throw new InvalidDataException("Missing page checksum.");
                var renderedChecksums = Encoding.UTF8.GetBytes(PageChecksum().Replace(originalChecksums, hash + "  index.html"));
                page = rendered;
                checksums = renderedChecksums;
                cachedPath = snapshot;
            }
            return name == "index.html" ? page : checksums;
        }
    }
    private static string Field(string text, string prefix)
    {
        var values = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (values.Length != 1) throw new InvalidDataException("Missing or ambiguous release metadata.");
        return values[0][prefix.Length..];
    }
    private static string ReadText(string folder, string name) => Encoding.UTF8.GetString(ReadBytes(folder, name)).TrimStart('\uFEFF');
    private static byte[] ReadBytes(string folder, string name)
    {
        var file = new FileInfo(Path.Combine(folder, name));
        if (!file.Exists || file.Length > 262144 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Missing or invalid release metadata.");
        return File.ReadAllBytes(file.FullName);
    }
    private static string LoadTemplate()
    {
        using var stream = typeof(ReleasePage).Assembly.GetManifestResourceStream("VRCoplay.ReleasePage.html")
            ?? throw new InvalidOperationException("The download page template is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    [GeneratedRegex(@"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();
    [GeneratedRegex("<meta name=\"vrcoplay:unity-version\" content=\"([0-9]+\\.[0-9]+\\.[0-9]+)\">", RegexOptions.CultureInvariant)]
    private static partial Regex UnityMetadata();
    [GeneratedRegex(@"Version ([0-9]+\.[0-9]+\.[0-9]+) · Unity package ·", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyUnityMetadata();
    [GeneratedRegex(@"^[a-fA-F0-9]{64}  index\.html(?=\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PageChecksum();
}
