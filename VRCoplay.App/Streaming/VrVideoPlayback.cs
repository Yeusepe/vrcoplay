// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal sealed class VrVideoPlayback
{
    private readonly HashSet<ulong> _engines = [];
    private ulong? _candidate;
    private (ulong Context, ulong Engine, string Url)? _resolving;
    private string? _link, _localPath;
    internal bool Started { get; private set; }
    internal void SetStream(string link, string localPath)
    {
        if (_link == link && _localPath == localPath) return;
        _link = link;
        _localPath = localPath;
        _candidate = null;
        _resolving = null;
        Started = false;
    }
    internal void Engine(int id, ulong instance, int result = 0)
    {
        var discovered = (id == 105 || id == 101 && result == 0) && _engines.Add(instance);
        if (id == 113) _engines.Remove(instance);
        if (discovered || id is 105 or 113 or 137)
        {
            _candidate = null;
            _resolving = null;
            Started = false;
        }
        if (id == 101 && result == 0 && _engines.Count == 1 && _candidate == instance)
            Started = true;
    }
    internal void Source(int id, ulong context, string url, int result = 0)
    {
        if (id == 200)
        {
            _candidate = null;
            Started = false;
            _resolving = _engines.Count == 1 && Matches(_link, _localPath, url)
                ? (context, _engines.Single(), url) : null;
        }
        else if (id == 205 && _resolving is { } source && source.Context == context && source.Url == url)
        {
            _candidate = result == 0 ? source.Engine : null;
            _resolving = null;
        }
    }
    internal static bool Matches(string? link, string? localPath, string source)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var expected) ||
            !Uri.TryCreate(source, UriKind.Absolute, out var actual) ||
            actual.Scheme is not ("rtsp" or "rtspt") || actual.UserInfo.Length != 0 ||
            actual.Query.Length != 0 || actual.Fragment.Length != 0 ||
            expected.UserInfo.Length != 0 || expected.Query.Length != 0 || expected.Fragment.Length != 0)
            return false;
        if (!string.IsNullOrEmpty(localPath) && actual.IsLoopback && actual.Port == 8554 && actual.AbsolutePath == "/" + localPath)
            return true;
        if (expected.Host != actual.Host)
            return false;
        if (expected.Scheme is "rtsp" or "rtspt")
            return expected.Port == actual.Port && expected.AbsolutePath == actual.AbsolutePath;
        var parts = actual.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return StreamLink.IsServerUri(expected) &&
            expected.AbsolutePath.StartsWith("/watch/", StringComparison.Ordinal) &&
            actual.Port == StreamLink.ResolverPort &&
            parts.Length == 3 && parts[0] == "t" && parts[1].Length == 48 &&
            parts[1].All(Uri.IsHexDigit) && parts[2] == expected.AbsolutePath[7..];
    }
}
