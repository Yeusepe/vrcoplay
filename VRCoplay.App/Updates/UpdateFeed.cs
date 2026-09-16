// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
namespace VRCoplay;
internal sealed record UpdateRelease(Version Version, Uri InstallerUri, string InstallerXml, string Notes);
internal sealed record UpdateIdentity(string Name, string Publisher, string Architecture, Version Version);
internal sealed class UpdateFeed(HttpClient http)
{
    private const int MaximumBytes = 256 * 1024;
    internal async Task<UpdateRelease?> CheckAsync(Uri source, UpdateIdentity identity, CancellationToken cancellation)
    {
        var xml = await ReadAsync(source, cancellation);
        var release = Parse(xml, source, identity);
        var notes = "Release notes aren’t available for this version.";
        try
        {
            var json = await ReadAsync(new Uri(source, "release-notes.json"), cancellation);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.GetProperty("version").GetString() == release.Version.ToString() &&
                document.RootElement.GetProperty("notes").GetString() is { Length: > 0 and <= 20000 } text)
                notes = text;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
        return release with { Notes = notes };
    }
    internal static UpdateRelease Parse(string xml, Uri source, UpdateIdentity identity)
    {
        ValidateUri(source, source);
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes
        });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Missing installer.");
        if (root.Name.LocalName != "AppInstaller" || root.Name.NamespaceName != "http://schemas.microsoft.com/appx/appinstaller/2021")
            throw new InvalidDataException("Unsupported installer format.");
        var ns = root.Name.Namespace;
        if (!Uri.TryCreate((string?)root.Attribute("Uri"), UriKind.Absolute, out var installer) || installer != source)
            throw new InvalidDataException("The installer address changed.");
        if (root.Descendants().Any(e => e.Name.LocalName is "MainBundle" or "OptionalPackages" or "RelatedPackages"))
            throw new InvalidDataException("Unexpected package configuration.");
        var packages = root.Elements(ns + "MainPackage").ToArray();
        if (packages.Length != 1) throw new InvalidDataException("Expected one app package.");
        var package = packages[0];
        if ((string?)package.Attribute("Name") != identity.Name ||
            (string?)package.Attribute("Publisher") != identity.Publisher ||
            (string?)package.Attribute("ProcessorArchitecture") != identity.Architecture)
            throw new InvalidDataException("This update belongs to a different app.");
        if (!Version.TryParse((string?)package.Attribute("Version"), out var version) || version.Revision < 0 ||
            new[] { version.Major, version.Minor, version.Build, version.Revision }.Any(v => v > ushort.MaxValue))
            throw new InvalidDataException("Invalid package version.");
        foreach (var attribute in root.DescendantsAndSelf().Attributes("Uri"))
        {
            if (!Uri.TryCreate(attribute.Value, UriKind.Absolute, out var uri)) throw new InvalidDataException("Invalid download address.");
            ValidateUri(uri, source);
        }
        if (package.Attribute("Uri") is null) throw new InvalidDataException("Missing package address.");
        return new(version, installer, xml, "");
    }
    private static void ValidateUri(Uri uri, Uri source)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != "https" && !(source.IsFile && uri.IsFile)) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Updates require HTTPS or a local tester folder.");
    }
    private async Task<string> ReadAsync(Uri uri, CancellationToken cancellation)
    {
        ValidateUri(uri, uri);
        if (uri.IsFile)
        {
            if (new FileInfo(uri.LocalPath).Length > MaximumBytes) throw new InvalidDataException("Update information is too large.");
            return await File.ReadAllTextAsync(uri.LocalPath, cancellation);
        }
        for (var redirect = 0; redirect < 6; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                ValidateUri(uri, new Uri("https://updates.invalid/"));
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Update information is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellation);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
            {
                if (output.Length + count > MaximumBytes) throw new InvalidDataException("Update information is too large.");
                output.Write(buffer, 0, count);
            }
            return System.Text.Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF');
        }
        throw new HttpRequestException("Too many update redirects.");
    }
}
