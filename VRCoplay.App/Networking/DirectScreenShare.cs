// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Sockets;
using System.Security.Cryptography;
using CliWrap;
namespace VRCoplay;
internal sealed class DirectScreenShare : IAsyncDisposable
{
    internal string Path { get; } = "share_" + RandomNumberGenerator.GetHexString(32);
    internal int Port { get; }
    private DirectPortMapping? _mapping;
    private readonly Uri _coordinator;
    private readonly bool _questEnabled;
    private readonly SemaphoreSlim _lifecycle = new(1);
    private Registration? _registration;
    private bool _useDirectIp, _disposed;
    private Task? _disposal;
    private string? _encoded;
    internal bool UseDirectIp => Volatile.Read(ref _useDirectIp);
    internal string? RegisteredLink => UseDirectIp ? null : _registration?.Link;
    internal string? Link => UseDirectIp ? DirectLink : RegisteredLink;
    internal string? DirectLink =>
        InternetMapped && Volatile.Read(ref _encoded) is { } encoded ? StreamLink.ResolveAddress(encoded) : null;
    internal string? QuestLink(bool useDirectIp)
    {
        if (!InternetMapped) return null;
        return useDirectIp ? DirectLink is { } direct ? direct + "_quest" : null
            : UseDirectIp ? null : _registration?.QuestLink;
    }
    private bool _manualReady;
    internal event Action? Changed;
    internal DirectPortMapping.Status? State => _mapping?.State;
    internal bool InternetMapped => _mapping?.Active ?? _manualReady;
    internal string PublishUrl => $"rtsp://127.0.0.1:{Port}/{Path}";
    internal DirectScreenShare(Uri coordinator, int port = 8554, bool quest = false, bool useDirectIp = false)
    {
        Port = port;
        _coordinator = coordinator;
        _questEnabled = quest;
        _useDirectIp = useDirectIp;
        if (!useDirectIp) StartRegistration();
    }
    private void StartRegistration()
    {
        var registration = new Registration(_coordinator, _questEnabled);
        registration.Changed += () => Changed?.Invoke();
        _registration = registration;
        if (Volatile.Read(ref _encoded) is { } encoded) registration.Update(encoded);
    }
    internal async Task SetUseDirectIpAsync(bool useDirectIp)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed || UseDirectIp == useDirectIp) return;
            Volatile.Write(ref _useDirectIp, useDirectIp);
            var previous = Interlocked.Exchange(ref _registration, null);
            Changed?.Invoke();
            if (previous is not null) await previous.DisposeAsync();
            if (!useDirectIp) StartRegistration();
            Changed?.Invoke();
        }
        finally { _lifecycle.Release(); }
    }
    internal async Task<string> PrepareAsync(
        CancellationToken stop,
        string host = "",
        int publicPort = 8554
    )
    {
        void Register(IPAddress address, int port) =>
            Update(StreamLink.Encode(address, port, Path));
        if (!string.IsNullOrWhiteSpace(host))
        {
            if (publicPort is < 1 or > 65535 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                throw new ArgumentException("Enter a host address and a port from 1 to 65535.");
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, stop);
            var ip =
                addresses.FirstOrDefault() ?? throw new ArgumentException("The host needs a reachable IPv4 address.");
            Register(ip, publicPort);
            _manualReady = true;
            Changed?.Invoke();
            return Link ?? "";
        }
        _mapping ??= new(
            [new(6, RandomNumberGenerator.GetInt32(30000, 60000), Port)],
            stop,
            state =>
            {
                if (state.Endpoint is { } endpoint)
                    Register(endpoint.Address, endpoint.Port);
                Changed?.Invoke();
            }
        );
        return Link ?? "";
    }
    internal Command MediaCommand(string tools) => LocalVideo.MediaCommand(tools, Port, share: true);
    internal void Update(string encoded)
    {
        Volatile.Write(ref _encoded, encoded);
        Volatile.Read(ref _registration)?.Update(encoded);
        Changed?.Invoke();
    }
    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            _disposed = true;
            var registration = Interlocked.Exchange(ref _registration, null);
            if (registration is not null) await registration.DisposeAsync();
            await (_mapping?.DisposeAsync() ?? ValueTask.CompletedTask);
            _mapping = null;
            _manualReady = false;
            Changed?.Invoke();
        }
        finally { _lifecycle.Release(); }
    }
    private sealed class Registration : IAsyncDisposable
    {
        private readonly HubConnection _hub;
        private readonly bool _questEnabled;
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<string> _updates = Channel.CreateBounded<string>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }
        );
        private readonly Task _loop;
        private string? _encoded, _link, _questLink;
        internal string? Link => Volatile.Read(ref _link);
        internal string? QuestLink => Volatile.Read(ref _questLink);
        internal event Action? Changed;
        internal Registration(Uri coordinator, bool quest)
        {
            _questEnabled = quest;
            _hub = new HubConnectionBuilder().WithUrl(new Uri(coordinator, "/v1/signaling")).Build();
            _hub.Closed += _ =>
            {
                Volatile.Write(ref _link, null);
                Volatile.Write(ref _questLink, null);
                Changed?.Invoke();
                if (!_stop.IsCancellationRequested && Volatile.Read(ref _encoded) is { } encoded)
                    _updates.Writer.TryWrite(encoded);
                return Task.CompletedTask;
            };
            _loop = RunAsync();
        }
        internal void Update(string encoded)
        {
            Volatile.Write(ref _encoded, encoded);
            _updates.Writer.TryWrite(encoded);
        }
        private async Task RunAsync()
        {
            var stop = _stop.Token;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var encoded = await _updates.Reader.ReadAsync(stop);
                    while (_updates.Reader.TryRead(out var newer))
                        encoded = newer;
                    try
                    {
                        if (_hub.State == HubConnectionState.Disconnected)
                            await _hub.StartAsync(stop);
                        var link = await _hub.InvokeAsync<string>("RegisterStream", encoded, stop);
                        stop.ThrowIfCancellationRequested();
                        if (_hub.State != HubConnectionState.Connected || StreamLink.StreamId(link) is null)
                            throw new IOException("The Video Player link registration was interrupted.");
                        Volatile.Write(ref _link, link);
                        Changed?.Invoke();
                        if (_questEnabled) await RegisterQuestAsync(stop);
                    }
                    catch (Exception) when (!stop.IsCancellationRequested)
                    {
                        Volatile.Write(ref _link, null);
                        Volatile.Write(ref _questLink, null);
                        Changed?.Invoke();
                        await _hub.StopAsync(stop);
                        await Task.Delay(TimeSpan.FromSeconds(3), stop);
                        if (Volatile.Read(ref _encoded) is { } latest)
                            _updates.Writer.TryWrite(latest);
                    }
                }
            }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
        private async Task RegisterQuestAsync(CancellationToken stop)
        {
            var link = await _hub.InvokeAsync<string>("RegisterQuestStream", stop);
            stop.ThrowIfCancellationRequested();
            if (_hub.State != HubConnectionState.Connected || StreamLink.QuestCode(link) is null)
                throw new IOException("The short Quest link registration was interrupted.");
            Volatile.Write(ref _questLink, link);
            Changed?.Invoke();
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                await _loop;
            }
            finally
            {
                await _hub.DisposeAsync();
                _stop.Dispose();
            }
            Volatile.Write(ref _link, null);
            Volatile.Write(ref _questLink, null);
        }
    }
}
