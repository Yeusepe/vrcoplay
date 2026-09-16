// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using VRCoplay;
sealed class RoomDirectory : IDisposable
{
    private sealed record Registration(string Connection, string? WatchLink = null);
    internal sealed record Attempt(string Host)
    {
        internal TaskCompletionSource<string> Offer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    readonly ConcurrentDictionary<string, Registration> _hosts = new(StringComparer.OrdinalIgnoreCase);
    readonly MemoryCache _pending = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private sealed record StreamRegistration(string Connection, string Encoded, string? QuestCode = null);
    private readonly Lock _streamGate = new();
    private readonly Dictionary<string, StreamRegistration> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _questStreams = new(StringComparer.OrdinalIgnoreCase);
    internal string RegisterStream(string connection, string? key, string encoded)
    {
        if (StreamLink.Decode(encoded) is not { } address)
            throw new HubException("Invalid stream address.");
        lock (_streamGate)
        {
            string? questCode = null;
            if (key is not null)
            {
                if (!_streams.TryGetValue(key, out var existing) || existing.Connection != connection)
                    throw new HubException("Only the sharing app can update its stream.");
                questCode = existing.QuestCode;
                if (questCode is not null && !IsPublicShare(address.Path))
                    throw new HubException("Quest viewing requires a public shared video feed.");
            }
            else
            {
                if (_streams.Count >= 4096)
                    throw new HubException("Too many active streams. Try again shortly.");
                do key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
                while (_streams.ContainsKey(key));
            }
            _streams[key] = new(connection, encoded, questCode);
            return key;
        }
    }
    internal string? StreamAddress(string key)
    {
        lock (_streamGate)
            return _streams.GetValueOrDefault(key)?.Encoded;
    }
    internal string RegisterQuestStream(string connection, string key)
    {
        lock (_streamGate)
        {
            if (!_streams.TryGetValue(key, out var stream) || stream.Connection != connection)
                throw new HubException("Register your stream before creating a Quest link.");
            if (StreamLink.Decode(stream.Encoded) is not { } address || !IsPublicShare(address.Path))
                throw new HubException("Quest viewing requires a public shared video feed.");
            if (stream.QuestCode is { } existing) return existing;
            string code;
            do code = RandomNumberGenerator.GetString(StreamLink.QuestCodeAlphabet, 8);
            while (_questStreams.ContainsKey(code));
            _questStreams[code] = key;
            _streams[key] = stream with { QuestCode = code };
            return code;
        }
    }
    internal string? QuestAddress(string code)
    {
        if (!StreamLink.IsQuestCode(code)) return null;
        lock (_streamGate)
        {
            if (!_questStreams.TryGetValue(code, out var key) || !_streams.TryGetValue(key, out var stream)
                || StreamLink.Decode(stream.Encoded) is not { } address)
                return null;
            return StreamLink.Encode(System.Net.IPAddress.Parse(address.Ip), address.Port, address.Path + "_quest");
        }
    }
    private static bool IsPublicShare(string path) => path.Length == 38
        && path.StartsWith("share_", StringComparison.Ordinal) && path.AsSpan(6).IndexOfAnyExcept("0123456789ABCDEF") < 0;
    internal string Create(string host)
    {
        string code;
        do code = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6);
        while (!_hosts.TryAdd(code, new(host)));
        return code;
    }
    internal bool Exists(string code) => _hosts.ContainsKey(code);
    internal void SetWatchLink(string code, string connection, string? link)
    {
        if (link is not null && (StreamLink.StreamId(link) is null || !new Uri(link).IsDefaultPort))
            throw new HubException("Invalid Video Player link.");
        if (
            !_hosts.TryGetValue(code, out var host)
            || host.Connection != connection
            || !_hosts.TryUpdate(code, host with { WatchLink = link }, host)
        )
            throw new HubException("Only this room’s host can update its Video Player link.");
    }
    internal string? RoomForWatchLink(string link) =>
        _hosts.Where(x => x.Value.WatchLink == link).Take(2).ToArray() is [var room] ? room.Key : null;
    internal Attempt Join(string code, string guest)
    {
        if (!_hosts.TryGetValue(code.Trim(), out var host))
            throw new HubException("Room not found.");
        return _pending.Set(
            guest,
            new Attempt(host.Connection),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30), Size = 1 }
        );
    }
    internal Attempt Pending(string guest) =>
        _pending.Get<Attempt>(guest) ?? throw new HubException("Connection attempt expired. Retry joining.");
    internal void Complete(string guest) => _pending.Remove(guest);
    internal void Disconnect(string connection, string? code, string? stream = null)
    {
        lock (_streamGate)
            if (
                stream is not null
                && _streams.TryGetValue(stream, out var registration)
                && registration.Connection == connection
            )
            {
                if (registration.QuestCode is { } questCode) _questStreams.Remove(questCode);
                _streams.Remove(stream);
            }
        if (code is not null && _hosts.TryGetValue(code, out var host) && host.Connection == connection)
            _hosts.TryRemove(code, out _);
        foreach (var key in _pending.Keys.OfType<string>())
            if (key == connection || _pending.Get<Attempt>(key)?.Host == connection)
                _pending.Remove(key);
    }
    public void Dispose() => _pending.Dispose();
}
