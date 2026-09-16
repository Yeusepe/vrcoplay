// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace VRCoplay;
public static class StreamLink
{
    public const string DefaultServer = "https://vrco.pl/";
    public static Uri Server
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("VRCOPLAY_SERVER");
            if (string.IsNullOrEmpty(value)) return new(DefaultServer);
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && uri.Host.Length > 0
                && uri.AbsolutePath == "/" && uri.Query.Length == 0
                && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0)
                return uri;
            throw new InvalidOperationException("VRCOPLAY_SERVER must be an HTTP or HTTPS origin without a path, credentials, query, or fragment.");
        }
    }
    public static string ServerUrl(string path) => new Uri(Server, path).AbsoluteUri;
    public static string ResolverUrl(string path) => new UriBuilder("rtspt", Server.Host, ResolverPort, path).Uri.AbsoluteUri;
    public static bool IsServerUri(Uri uri)
    {
        var server = Server;
        return uri.IsAbsoluteUri && uri.Scheme == server.Scheme && uri.Host == server.Host && uri.Port == server.Port
            && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0;
    }
    public static int ResolverPort =>
        int.TryParse(Environment.GetEnvironmentVariable("VRCOPLAY_RTSP_PORT"), out var port) && port is > 0 and <= 65535
            ? port
            : 32270;
    public static string Local => ResolverUrl("/local");
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record Address(string Ip, int Port, string Path);
    internal static readonly SearchValues<char> PathChars = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_"
    );
    private static readonly SearchValues<char> TokenChars = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-"
    );
    public const int LocalProbePort = 8556;
    internal const string QuestCodeAlphabet = "23456789abcdefghjkmnpqrstuvwxyz";
    private static readonly SearchValues<char> QuestCodeChars = SearchValues.Create(
        QuestCodeAlphabet + QuestCodeAlphabet.ToUpperInvariant());
    internal static bool IsQuestCode(ReadOnlySpan<char> code) =>
        code.Length == 8 && !code.ContainsAnyExcept(QuestCodeChars);
    public static string? QuestCode(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && IsServerUri(uri)
        && uri.AbsolutePath.StartsWith("/q/", StringComparison.OrdinalIgnoreCase)
        && IsQuestCode(uri.AbsolutePath.AsSpan(3))
            ? uri.AbsolutePath[3..].ToLowerInvariant() : null;
    public static string? StreamId(string watchLink) =>
        Uri.TryCreate(watchLink, UriKind.Absolute, out var uri)
        && IsServerUri(uri)
        && uri.AbsolutePath.StartsWith("/watch/", StringComparison.Ordinal)
            ? WatchStreamId(uri.AbsolutePath.AsSpan(7))
            : null;
    internal static bool IsWatchKey(ReadOnlySpan<char> key) =>
        key.Length == 22 && !key.ContainsAnyExcept(TokenChars) && "AQgw".Contains(key[^1]);
    internal static string? WatchStreamId(ReadOnlySpan<char> key) =>
        IsWatchKey(key) ? "watch_" + Convert.ToHexString(Base64Url.DecodeFromChars(key)) : null;
    internal static string Encode(IPAddress ip, int port, string path)
    {
        var address = new Address(ip.ToString(), port, path);
        if (!Valid(address))
            throw new ArgumentException("Invalid direct stream address.");
        return Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(address));
    }
    internal static string ResolveLocal() => "rtsp://127.0.0.1:8554/game";
    internal static string? ResolveAddress(string encoded)
    {
        if (Decode(encoded) is not { } address)
            return null;
        var target = new UriBuilder(
            "rtsp",
            address.Ip,
            address.Port,
            address.Path
        );
        return target.Uri.AbsoluteUri;
    }
    internal static Address? Decode(ReadOnlySpan<char> data)
    {
        if (data.Length is < 1 or > 1021 || data.ContainsAnyExcept(TokenChars))
            return null;
        try
        {
            var address = JsonSerializer.Deserialize<Address>(Base64Url.DecodeFromChars(data));
            return address is not null && Valid(address) ? address : null;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or JsonException)
        {
            return null;
        }
    }
    private static bool Valid(Address value) =>
        IPAddress.TryParse(value.Ip, out var ip)
        && !ip.Equals(IPAddress.Any)
        && !ip.Equals(IPAddress.IPv6Any)
        && value.Port is > 0 and <= 65535
        && value.Path is { Length: > 0 and <= 96 }
        && !value.Path.AsSpan().ContainsAnyExcept(PathChars);
}
