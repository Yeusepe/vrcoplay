// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using VRCoplay;
sealed class SignalingHub(RoomDirectory rooms) : Hub
{
    public override Task OnConnectedAsync()
    {
        Context.Items["limit"] = new FixedWindowRateLimiter(
            new() { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }
        );
        return base.OnConnectedAsync();
    }
    public string CreateRoom()
    {
        Unbound();
        var code = rooms.Create(Context.ConnectionId);
        Context.Items["room"] = code;
        return code;
    }
    public async Task<string> JoinRoom(string code)
    {
        Unbound();
        var attempt = rooms.Join(code, Context.ConnectionId);
        Context.Items["joining"] = true;
        await Clients.Client(attempt.Host).SendAsync("JoinRequested", Context.ConnectionId, Context.ConnectionAborted);
        return await attempt.Offer.Task.WaitAsync(TimeSpan.FromSeconds(30), Context.ConnectionAborted);
    }
    public void SetWatchLink(string? link)
    {
        Permit(link ?? "");
        if (Context.Items.TryGetValue("room", out var code) && code is string room)
            rooms.SetWatchLink(room, Context.ConnectionId, link);
        else
            throw new HubException("Create a game room first.");
    }
    public string RegisterStream(string encoded)
    {
        Permit(encoded);
        var key = rooms.RegisterStream(
            Context.ConnectionId,
            Context.Items.TryGetValue("stream", out var existing) ? existing as string : null,
            encoded
        );
        Context.Items["stream"] = key;
        return StreamLink.ServerUrl("/watch/" + key);
    }
    public string RegisterQuestStream()
    {
        Permit("");
        if (!Context.Items.TryGetValue("stream", out var stream) || stream is not string key)
            throw new HubException("Register your stream before creating a Quest link.");
        return StreamLink.ServerUrl("/q/" + rooms.RegisterQuestStream(Context.ConnectionId, key));
    }
    public void Offer(string guest, string sdp)
    {
        Permit(sdp);
        var attempt = rooms.Pending(guest);
        if (attempt.Host != Context.ConnectionId || !attempt.Offer.TrySetResult(sdp))
            throw new HubException("This offer does not belong to this connection.");
    }
    public async Task Answer(string sdp)
    {
        Permit(sdp);
        var attempt = rooms.Pending(Context.ConnectionId);
        if (!attempt.Offer.Task.IsCompletedSuccessfully)
            throw new HubException("No offer is pending.");
        rooms.Complete(Context.ConnectionId);
        await Clients.Client(attempt.Host).SendAsync("Answer", Context.ConnectionId, sdp, Context.ConnectionAborted);
    }
    void Unbound()
    {
        Permit("");
        if (Context.Items.ContainsKey("room") || Context.Items.ContainsKey("joining"))
            throw new HubException("This signaling connection is already in use.");
    }
    void Permit(string sdp)
    {
        using var lease = ((FixedWindowRateLimiter)Context.Items["limit"]!).AttemptAcquire();
        if (!lease.IsAcquired || sdp is null || sdp.Length > 30000)
            throw new HubException("Signaling limit exceeded.");
    }
    public override Task OnDisconnectedAsync(Exception? error)
    {
        rooms.Disconnect(
            Context.ConnectionId,
            Context.Items.TryGetValue("room", out var code) ? code as string : null,
            Context.Items.TryGetValue("stream", out var stream) ? stream as string : null
        );
        (Context.Items["limit"] as IDisposable)?.Dispose();
        return base.OnDisconnectedAsync(error);
    }
}
