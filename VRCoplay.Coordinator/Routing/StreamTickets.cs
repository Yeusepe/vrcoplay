// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using VRCoplay;
sealed class StreamTickets(RoomDirectory rooms, int probePort = StreamLink.LocalProbePort) : IDisposable
{
    private sealed record LocalRoute(string Path, long Until);
    private sealed record Ticket(string Encoded, string Stream)
    {
        internal LocalRoute? Local;
    }
    private readonly MemoryCache _tickets = new(new MemoryCacheOptions { SizeLimit = 4096 });
    internal string? Page(string encoded, string? code = null)
    {
        if (
            !StreamLink.IsWatchKey(encoded)
            || rooms.StreamAddress(encoded) is not { } address
            || StreamLink.Decode(address) is null
            || StreamLink.WatchStreamId(encoded) is not { } stream
        )
            return null;
        var nonce = RandomNumberGenerator.GetHexString(48);
        _tickets.Set(
            nonce,
            new Ticket(encoded, stream),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60), Size = 1 }
        );
        var data = JsonSerializer.Serialize(
            new
            {
                sources = new object[]
                {
                    new { file = $"http://127.0.0.1:{probePort}/v1/probe/{stream}/{nonce}.smil" },
                    new { file = StreamLink.ResolverUrl($"/t/{nonce}/{encoded}"), type = "video/mp4" },
                },
            }
        );
        return InvitePage.Watch(encoded, data, code);
    }
    internal bool Claim(string nonce, string stream, string path)
    {
        if (path.Length is < 1 or > 96 || path.AsSpan().ContainsAnyExcept(StreamLink.PathChars))
            return false;
        if (!_tickets.TryGetValue<Ticket>(nonce, out var ticket) || ticket!.Stream != stream)
            return false;
        Volatile.Write(ref ticket.Local, new(path, Environment.TickCount64 + 5000));
        return true;
    }
    internal string? QuestPage(string code)
    {
        if (rooms.QuestAddress(code) is null) return null;
        code = code.ToLowerInvariant();
        var data = JsonSerializer.Serialize(new
        {
            sources = new[] { new { file = StreamLink.ResolverUrl($"/q/{code}"), type = "video/mp4" } },
        });
        return InvitePage.Watch(code, data, watchLink: StreamLink.ServerUrl("/q/" + code));
    }
    internal string? Resolve(string path)
    {
        if (path == "/local") return StreamLink.ResolveLocal();
        var parts = path.Split('/');
        if (parts.Length == 3 && parts[1].Equals("q", StringComparison.OrdinalIgnoreCase))
            return rooms.QuestAddress(parts[2]) is { } quest ? StreamLink.ResolveAddress(quest) : null;
        if (parts.Length != 4 || parts[1] != "t" || parts[2].Length != 48 || !parts[2].All(char.IsAsciiHexDigit))
            return null;
        if (!StreamLink.IsWatchKey(parts[3]) || rooms.StreamAddress(parts[3]) is not { } encoded)
            return null;
        if (
            _tickets.TryGetValue<Ticket>(parts[2], out var ticket)
            && ticket!.Encoded == parts[3]
            && Volatile.Read(ref ticket.Local) is { } local
            && local.Until > Environment.TickCount64
        )
            return $"rtsp://127.0.0.1:8554/{local.Path}";
        return StreamLink.ResolveAddress(encoded);
    }
    public void Dispose() => _tickets.Dispose();
    internal static void Map(WebApplication app)
    {
        app.MapGet("/q/{code}", (string code, HttpContext context, StreamTickets tickets) =>
        {
            context.Response.Headers.CacheControl = "no-store, max-age=0";
            var page = tickets.QuestPage(code);
            return page is null ? Results.NotFound() : Results.Content(page, "text/html");
        }).RequireRateLimiting("view");
        app.MapGet(
            "/watch/{encoded}",
            (string encoded, HttpContext context, StreamTickets tickets, RoomDirectory rooms) =>
            {
                context.Response.Headers.CacheControl = "no-store, max-age=0";
                var page = tickets.Page(encoded, rooms.RoomForWatchLink(StreamLink.ServerUrl("/watch/" + encoded)));
                return page is null ? Results.NotFound() : Results.Content(page, "text/html");
            }
        );
        app.MapPost(
            "/v1/view/{nonce}/{stream}/{path}",
            (string nonce, string stream, string path, HttpContext context, StreamTickets tickets) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
                    return Results.StatusCode(413);
                return tickets.Claim(nonce, stream, path) ? Results.NoContent() : Results.NotFound();
            }
        );
    }
}
