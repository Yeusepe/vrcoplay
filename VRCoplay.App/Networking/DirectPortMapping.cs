// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using static System.Threading.Tasks.ConfigureAwaitOptions;
using static VRCoplay.InternetHosting;
namespace VRCoplay;
internal sealed class DirectPortMapping : IAsyncDisposable
{
    internal sealed record Status(IPEndPoint? Endpoint, string? Error = null);
    private const int Lifetime = 120;
    private readonly TimeProvider _time;
    private readonly Task _run;
    private CancellationTokenSource? _stop;
    internal Status State { get; private set; } = new(null);
    internal bool Active => _stop?.IsCancellationRequested == false && State.Endpoint is not null;
    internal DirectPortMapping(
        Port[] ports,
        CancellationToken stop,
        Action<Status>? changed = null,
        Func<Port[], string, int, Task<Result>>? update = null,
        TimeProvider? time = null
    )
    {
        if (
            ports.Length is < 1 or > 16
            || ports.Any(p =>
                p.Protocol is not (6 or 17) || p.PublicPort is < 1 or > 65535 || p.PrivatePort is < 0 or > 65535
            )
        )
            throw new ArgumentException("Invalid TCP/UDP mapping.", nameof(ports));
        _time = time ?? TimeProvider.System;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _run = RunAsync(ports.ToArray(), _stop.Token, changed, update);
    }
    private async Task RunAsync(
        Port[] ports,
        CancellationToken stop,
        Action<Status>? changed,
        Func<Port[], string, int, Task<Result>>? update
    )
    {
        var owner = Guid.NewGuid().ToString("N");
        var delay = 5;
        var assigned = false;
        var conflicts = 0;
        void Publish(Status state)
        {
            if (State == state)
                return;
            State = state;
            changed?.Invoke(state);
        }
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var opened = false;
                var expires = 0L;
                var routerError = 0;
                Task<Result>? pending = null;
                Func<int, Task<Result>>? apply = update is null ? null : life => update(ports, owner, life);
                try
                {
                    var port = ports[0].PublicPort;
                    if (apply is null)
                    {
                        if (IsPublic(LocalAddress()))
                        {
                            port = ports[0].PrivatePort == 0 ? port : ports[0].PrivatePort;
                            apply = _ => Task.FromResult(new Result(LocalAddress()));
                        }
                        else
                            apply = life => UpdateAsync(ports, owner, life);
                    }
                    while (true)
                    {
                        var started = _time.GetTimestamp();
                        var deadline = State.Endpoint is null
                            ? started + Lifetime * _time.TimestampFrequency * 4 / 5
                            : expires;
                        if (deadline <= started)
                            throw new IOException("The direct connection expired.");
                        pending = apply(Lifetime);
                        var result = await pending.WaitAsync(_time.GetElapsedTime(started, deadline), _time, stop);
                        var address = result.Address;
                        opened |= address is not null;
                        stop.ThrowIfCancellationRequested();
                        if (result.Error is { } rejected)
                        {
                            routerError = result.ErrorCode;
                            Publish(new(null, rejected));
                            break;
                        }
                        if (
                            address is null
                            || !IsPublic(address)
                            || deadline <= _time.GetTimestamp()
                            || State.Endpoint is { } previous && !previous.Address.Equals(address)
                        )
                            throw new IOException("The router did not provide a valid public mapping.");
                        expires = started + Lifetime * _time.TimestampFrequency * 4 / 5;
                        Publish(new(new(address, port)));
                        assigned = true;
                        delay = 5;
                        conflicts = 0;
                        await Task.Delay(TimeSpan.FromSeconds(35), _time, stop);
                    }
                }
                catch (Exception failure)
                {
                    var error = failure.GetBaseException();
                    if (!stop.IsCancellationRequested)
                        Publish(
                            new(
                                null,
                                error is TimeoutException
                                    ? assigned
                                        ? "The router did not renew the direct connection in time."
                                        : "The router took too long to open the direct connection."
                                    : error.Message
                            )
                        );
                }
                finally
                {
                    if (pending is not null)
                        await ((Task)pending).ConfigureAwait(SuppressThrowing);
                    opened |= pending is { IsCompletedSuccessfully: true, Result.Address: not null };
                    if (opened)
                        try
                        {
                            await apply!(0);
                        }
                        catch { }
                }
                if (stop.IsCancellationRequested)
                    break;
                if (!assigned && ports is [{ PrivatePort: > 0 }] && routerError is 718 or 729)
                {
                    var previous = ports[0].PublicPort;
                    int next;
                    do next = RandomNumberGenerator.GetInt32(30000, 60000);
                    while (next == previous);
                    ports[0] = ports[0] with { PublicPort = next };
                    if (++conflicts <= 3)
                    {
                        Publish(new(null));
                        continue;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(delay), _time, stop).ConfigureAwait(SuppressThrowing);
                delay = Math.Min(delay * 2, 60);
            }
        }
        finally
        {
            State = new(null, "Direct connection stopped.");
        }
    }
    public async ValueTask DisposeAsync()
    {
        using var stop = Interlocked.Exchange(ref _stop, null);
        stop?.Cancel();
        await _run;
    }
    internal static IPAddress LocalAddress()
    {
        using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        route.Connect(IPAddress.Parse("1.1.1.1"), 53);
        return ((IPEndPoint)route.LocalEndPoint!).Address;
    }
    private static readonly IPNetwork[] NonPublicNetworks =
        "0.0.0.0/8 10.0.0.0/8 127.0.0.0/8 224.0.0.0/3 100.64.0.0/10 169.254.0.0/16 172.16.0.0/12 192.168.0.0/16 192.0.0.0/24 192.0.2.0/24 198.18.0.0/15 198.51.100.0/24 203.0.113.0/24"
            .Split(' ')
            .Select(IPNetwork.Parse)
            .ToArray();
    internal static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        return ip.AddressFamily == AddressFamily.InterNetwork
            && !NonPublicNetworks.Any(network => network.Contains(ip));
    }
}
