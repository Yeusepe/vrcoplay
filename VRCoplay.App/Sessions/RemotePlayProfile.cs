// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
namespace VRCoplay;
internal sealed class RemotePlayProfile : IDisposable
{
    internal string DirectoryPath { get; }
    private RemotePlayProfile(string directory) => DirectoryPath = directory;
    internal static async Task<RemotePlayProfile> CreateAsync(RemotePlayCredentials identity, string host, CancellationToken stop)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || identity.Port is < 1024 or > 65500 || identity.AppId <= 0
            || !Guid.TryParse(identity.ServerId, out _) || identity.UniqueId.Length != 16
            || identity.Certificate.Length > 4096 || identity.PrivateKey.Length > 4096 || identity.ServerCertificate.Length > 4096)
            throw new InvalidDataException("The host sent invalid remote play settings.");
        var directory = PrivateSessionDirectory.Create("Players");
        var profile = new RemotePlayProfile(directory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "portable.dat"), [], stop).ConfigureAwait(false);
            var settings = Directory.CreateDirectory(Path.Combine(directory, "Moonlight Game Streaming Project")).FullName;
            var ini = $"""
                [General]
                certificate={Bytes(identity.Certificate)}
                key={Bytes(identity.PrivateKey)}
                uniqueid={Text(identity.UniqueId)}

                [hosts]
                size=1
                1\hostname=VRCoplay
                1\uuid={Text(identity.ServerId)}
                1\customname=false
                1\manualaddress={Text(host)}
                1\manualport={identity.Port}
                1\srvcert={Bytes(identity.ServerCertificate)}
                1\nvidiasw=false
                1\apps\size=1
                1\apps\1\name=VRCoplay
                1\apps\1\id={identity.AppId}
                1\apps\1\hdr=false
                """;
            await File.WriteAllTextAsync(Path.Combine(settings, "Moonlight.ini"), ini + "\n", new UTF8Encoding(false), stop).ConfigureAwait(false);
            return profile;
        }
        catch { profile.Dispose(); throw; }
    }
    private static string Text(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    private static string Bytes(string value) => Text("@ByteArray(" + value + ")");
    public void Dispose() => PrivateSessionDirectory.Delete(DirectoryPath);
}
internal static class PrivateSessionDirectory
{
    internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCoplay", "RemotePlay");
    internal static string Create(string category)
    {
        var directory = new DirectoryInfo(Path.Combine(Root, category, Guid.NewGuid().ToString("N")));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.Create(security);
        return directory.FullName;
    }
    internal static void Delete(string directory)
    {
        var path = Path.GetFullPath(directory);
        var prefix = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid remote play directory.");
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
    }
}
