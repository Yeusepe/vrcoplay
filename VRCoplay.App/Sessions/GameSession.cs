// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.SignalR.Client;
namespace VRCoplay;
internal sealed partial class GameSession : IAsyncDisposable, IRetryPolicy
{
    TimeSpan? IRetryPolicy.NextRetryDelay(RetryContext _) => IsHost ? TimeSpan.FromSeconds(5) : null;
    private sealed class Member(GamePeer peer)
    {
        internal GamePeer Peer { get; } = peer;
        internal Participant? State;
        internal uint Sequence;
        internal Task Lifetime = Task.CompletedTask;
        internal RemotePlayLease? RemotePlay;
        internal SetPlaybackLatency? Playback;
        internal long PlaybackReceived;
        internal FixedWindowRateLimiter PinLimit { get; } =
            new(
                new()
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                }
            );
    }
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Member> _peers = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<SessionState> _initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HubConnection _hub;
    private readonly Uri _coordinator;
    private DirectPortMapping? _gameMapping;
    private uint _grant,
        _sequence;
    private int _firstRemoteSlot;
    private PointerState _pointer;
    private Task? _disposal;
    private long _inviteUntil, _seenInvite;
    private string _playerName;
    public event Action<SessionState>? StateChanged;
    public event Action<int, ReadOnlySpan<byte>>? ControllerReport;
    public event Action<string, DesktopNotice>? Notice;
    public event Action<string>? SessionEnded;
    public event Action<int>? InviteRequested;
    public event Action? PlaybackLatencyChanged;
    public bool IsHost { get; private set; }
    public string ParticipantId { get; private set; } = "host";
    public string? Pin { get; private set; }
    public SessionState? Session { get; private set; }
    public bool HasAvailableControllerSlot
    {
        get
        {
            lock (_gate)
                return IsHost && Session is not null && ((15u << _firstRemoteSlot) & 15u & ~(uint)Session.ControllerSlots) != 0;
        }
    }
    public string ShareUrl =>
        Session is { Code.Length: > 0 } state ? new Uri(_coordinator, $"/join/{state.Code}").AbsoluteUri : "";
    public string? GameHost
    {
        get
        {
            lock (_gate)
                return Session?.Settings.MoonlightHost
                    ?? (
                        _peers.TryGetValue("host", out var host)
                        && IPEndPoint.TryParse(host.Peer.RemoteAddress, out var address)
                            ? address.Address.ToString()
                            : null
                    );
        }
    }
    public GameSession(string coordinatorBaseUri, string playerName = "")
    {
        _playerName = PlayerNames.Normalize(playerName);
        _coordinator = new(coordinatorBaseUri);
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_coordinator, "/v1/signaling"))
            .WithAutomaticReconnect(this)
            .Build();
    }
    public async Task<SessionState> CreateSessionAsync(
        SessionMode mode,
        bool pinRequired,
        bool localController,
        CancellationToken cancel = default
    )
    {
        if (mode is not (SessionMode.HostGame or SessionMode.LocalGame))
            throw new ArgumentOutOfRangeException(nameof(mode));
        IsHost = true;
        _firstRemoteSlot = localController ? 1 : 0;
        Pin =
            mode == SessionMode.HostGame && pinRequired
                ? RandomNumberGenerator.GetInt32(1_000_000).ToString("D6")
                : null;
        Session = new("", mode, Pin is not null, [])
        {
            HostName = PlayerNames.ForHost(_playerName),
        };
        _hub.Reconnecting += _ =>
        {
            lock (_gate)
                Publish(Session! with { Code = "" });
            Notice?.Invoke("Room discovery is unavailable. Established peer connections continue.", DesktopNotice.Warning);
            return Task.CompletedTask;
        };
        _hub.Reconnected += async _ =>
        {
            try
            {
                await RegisterAsync(_stop.Token);
            }
            catch (Exception error)
            {
                Notice?.Invoke($"Could not register a new room: {error.Message}", DesktopNotice.Warning);
            }
        };
        _hub.On<string>(
            "JoinRequested",
            id =>
            {
                lock (_gate)
                {
                    if (_stop.IsCancellationRequested || _peers.Count >= 16)
                        return;
                    var member = AddPeer(id);
                    member.Lifetime = RunPeerAsync(id, member);
                }
            }
        );
        _hub.On<string, string>(
            "Answer",
            (id, sdp) =>
            {
                lock (_gate)
                    _peers.GetValueOrDefault(id)?.Peer.AcceptAnswer(sdp);
            }
        );
        await _hub.StartAsync(cancel);
        await RegisterAsync(cancel);
        return Session;
    }
    private async Task RegisterAsync(CancellationToken stop)
    {
        var code = await _hub.InvokeAsync<string>("CreateRoom", stop);
        lock (_gate)
            Publish(Session! with { Code = code });
        if (Session.Settings.WatchLink is not null)
            await PublishWatchLinkAsync();
    }
    private async Task PublishWatchLinkAsync()
    {
        try
        {
            if (_hub.State == HubConnectionState.Connected)
                await _hub.InvokeAsync("SetWatchLink", Session!.Settings.WatchLink, _stop.Token);
        }
        catch (Exception error)
        {
            if (!_stop.IsCancellationRequested)
                Notice?.Invoke($"The Video Player link couldn’t update its game invite: {error.Message}", DesktopNotice.Warning);
        }
    }
    private async Task RunPeerAsync(string id, Member member)
    {
        try
        {
            if (IsHost)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var offer = await member.Peer.DescribeAsync(null, deadline.Token);
                await _hub.InvokeAsync("Offer", id, offer, deadline.Token);
                await member.Peer.Ready.WaitAsync(deadline.Token);
                lock (_gate)
                {
                    member.State = new(id, $"Player {id[..4]}", Session!.Mode);
                    Publish(Session);
                }
            }
            var error = await member.Peer.Closed.WaitAsync(_stop.Token);
            if (!IsHost)
                SessionEnded?.Invoke(error.Message);
        }
        catch (Exception error) when (!_stop.IsCancellationRequested)
        {
            Notice?.Invoke($"Direct connection failed: {error.Message}. The guest can retry joining.", DesktopNotice.Warning);
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate)
            {
                _peers.Remove(id);
                member.RemotePlay?.Stop.Cancel();
                if (member.State is not null)
                    Publish(Session!);
            }
            await member.Peer.DisposeAsync();
            member.PinLimit.Dispose();
        }
    }
    public async Task<SessionState> JoinAsync(string code, CancellationToken cancel = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        Member member;
        lock (_gate)
            member = AddPeer("host");
        await _hub.StartAsync(deadline.Token);
        ParticipantId = _hub.ConnectionId!;
        var offer = await _hub.InvokeAsync<string>("JoinRoom", code, deadline.Token);
        var answer = await member.Peer.DescribeAsync(offer, deadline.Token);
        await _hub.InvokeAsync("Answer", answer, deadline.Token);
        await Task.WhenAll(member.Peer.Ready, _initial.Task).WaitAsync(deadline.Token);
        Command(new SetPlayerName(_playerName));
        SetPlaybackLatency(null, null);
        await _hub.StopAsync(deadline.Token);
        member.Lifetime = RunPeerAsync("host", member);
        return Session!;
    }
    private Member AddPeer(string id)
    {
        var member = new Member(new GamePeer());
        _peers.Add(id, member);
        member.Peer.SessionMessage += bytes => ReceiveCommand(id, bytes);
        member.Peer.ControllerReport += bytes => ReceiveController(id, bytes);
        return member;
    }
    private void ReceiveCommand(string id, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            var command = JsonSerializer.Deserialize<SessionCommand>(bytes, JsonSerializerOptions.Strict);
            if (!IsHost)
            {
                switch (command)
                {
                    case SessionState { Participants.Length: <= 16 } state:
                        if (Session?.Participants.FirstOrDefault(x => x.Id == ParticipantId)?.Controller
                            != state.Participants.FirstOrDefault(x => x.Id == ParticipantId)?.Controller)
                            _sequence = 0;
                        Session = state;
                        RemoteApprovalChanged(state);
                        StateChanged?.Invoke(state);
                        if (state.InviteRevision > _seenInvite)
                        {
                            _seenInvite = state.InviteRevision;
                            if (state.Settings.InviteEnabled && state.InviteRequestTtlMs is > 0 and <= 120000)
                                InviteRequested?.Invoke(state.InviteRequestTtlMs);
                        }
                        _initial.TrySetResult(state);
                        break;
                    case SessionNotice notice:
                        _remoteApproval?.TrySetException(new IOException(notice.Message));
                        Notice?.Invoke(notice.Message, DesktopNotice.Warning);
                        break;
                    case RemotePlayReady ready:
                        if (_remoteRequestId == ready.RequestId) _remoteResponse?.TrySetResult(ready);
                        break;
                    case ShowInvite _: InviteRequested?.Invoke(120000); break;
                    default: throw new InvalidDataException("Unexpected host command.");
                }
                return;
            }
            if (!_peers.TryGetValue(id, out var member) || member.State is not { } person)
                return;
            switch (command)
            {
                case SetPlayerName name:
                    try
                    {
                        member.State = person with { Name = PlayerNames.ForGuest(id, PlayerNames.Normalize(name.Name)) };
                    }
                    catch (ArgumentException)
                    {
                        Send(member, new SessionNotice("Use up to 32 characters on one line for your name."));
                        return;
                    }
                    break;
                case RequestController request when person.Mode == SessionMode.HostGame:
                    using (var permit = member.PinLimit.AttemptAcquire())
                    {
                        var error =
                            !permit.IsAcquired ? "Too many PIN attempts. Try again in a minute."
                            : Pin is not null
                            && !CryptographicOperations.FixedTimeEquals(
                                MemoryMarshal.AsBytes(Pin.AsSpan()),
                                MemoryMarshal.AsBytes(request.Pin.AsSpan())
                            )
                                ? "The controller PIN is incorrect."
                            : null;
                        if (error is not null)
                        {
                            Send(member, new SessionNotice(error));
                            return;
                        }
                    }
                    if (person.Controller is not null)
                        return;
                    member.State = person with { Controller = new ControllerRequested(), RemotePlayRequested = request.RemotePlay };
                    break;
                case ReleaseController _:
                    member.State = person with { Controller = null, RemotePlayRequested = false };
                    break;
                case SetPlayLocation location
                    when location.Mode == Session!.Mode || location.Mode == SessionMode.LocalGame:
                    member.State = person with { Mode = location.Mode, Controller = null, RemotePlayRequested = false };
                    break;
                case SetPointerState pointer:
                    member.State = person with { Pointer = Normalize(pointer.Pointer) };
                    break;
                case SetPlaybackLatency playback:
                    if (playback.WatchLink is null && playback.LowLatency is not null)
                        throw new InvalidDataException("Inactive playback cannot report latency.");
                    if (playback.WatchLink is not null && playback.WatchLink != Session!.Settings.WatchLink)
                        return;
                    var refreshed = Stopwatch.GetElapsedTime(member.PlaybackReceived) > TimeSpan.FromSeconds(10);
                    var playbackChanged = member.Playback != playback;
                    member.Playback = playback;
                    member.PlaybackReceived = Stopwatch.GetTimestamp();
                    if (playbackChanged || refreshed) PlaybackLatencyChanged?.Invoke();
                    return;
                case PairRemotePlay pair:
                    BeginRemotePlay(member, pair);
                    return;
                default:
                    throw new InvalidDataException("This command is not allowed for a guest.");
            }
            if (member.State != person)
                Publish(Session!);
        }
    }
    public void DecideController(string participantId, bool allow)
    {
        lock (_gate)
        {
            Host();
            if (
                !_peers.TryGetValue(participantId, out var member)
                || member.State is not { } person
                || allow && person.Controller is not ControllerRequested
            )
                throw new InvalidOperationException("No controller request from that participant.");
            var free = (15u << _firstRemoteSlot) & 15u & ~(uint)Session!.ControllerSlots;
            if (allow && free == 0)
                throw new InvalidOperationException("All four controller slots are in use.");
            if (allow && person.RemotePlayRequested && RemotePlayDisplay is null)
                throw new InvalidOperationException("Choose the display to share with this player first.");
            member.State = person with
            {
                Controller = allow
                    ? new ControllerGranted(BitOperations.TrailingZeroCount(free), checked(++_grant))
                    : null,
            };
            member.Sequence = 0;
            if (member.State != person)
                Publish(Session!);
        }
    }
    private void ReceiveController(string id, ReadOnlySpan<byte> wire)
    {
        if (wire.Length < 8 || !VRCoplay.ControllerReport.TryRead(wire[8..], out _))
            throw new InvalidDataException("Invalid controller report size.");
        lock (_gate)
        {
            if (
                !_peers.TryGetValue(id, out var member)
                || member.State?.Controller is not ControllerGranted grant
                || BinaryPrimitives.ReadUInt32LittleEndian(wire) != grant.Id
            )
                return;
            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(wire[4..]);
            if (unchecked((int)(sequence - member.Sequence)) <= 0)
                return;
            member.Sequence = sequence;
            ControllerReport?.Invoke(grant.Slot, wire[8..]);
        }
    }
    public bool SendControllerReport(ReadOnlySpan<byte> report)
    {
        if (!VRCoplay.ControllerReport.TryRead(report, out _))
            throw new ArgumentException("A controller report must contain a valid VRC2 controller frame.", nameof(report));
        lock (_gate)
        {
            if (
                IsHost
                || !_peers.TryGetValue("host", out var host)
                || Session?.Participants.FirstOrDefault(x => x.Id == ParticipantId)?.Controller
                    is not ControllerGranted grant
            )
                return false;
            Span<byte> wire = stackalloc byte[8 + report.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(wire, grant.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(wire[4..], ++_sequence);
            report.CopyTo(wire[8..]);
            return host.Peer.SendController(wire);
        }
    }
    public void RemoveParticipant(string id)
    {
        lock (_gate)
        {
            Host();
            if (!_peers.TryGetValue(id, out var member))
                return;
            member.State = null;
            Publish(Session!);
            _ = member.Peer.DisposeAsync();
        }
    }
    public void RequestController(string? pin, bool remotePlay = false)
    {
        lock (_gate)
        {
            if (Session?.Participants.FirstOrDefault(x => x.Id == ParticipantId)?.Mode != SessionMode.HostGame)
                throw new InvalidOperationException("This play location keeps controllers local.");
            if (remotePlay)
            {
                _remoteApproval = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _remoteRequestSeen = false;
            }
            Command(new RequestController(pin, remotePlay));
        }
    }
    public void RenewControllerGrant(int slot)
    {
        lock (_gate)
        {
            if (!IsHost || _stop.IsCancellationRequested)
                return;
            var member = _peers.Values.FirstOrDefault(x => x.State?.Slot == slot);
            if (member?.State is not { } person)
                return;
            member.State = person with { Controller = new ControllerGranted(slot, checked(++_grant)) };
            member.Sequence = 0;
            Publish(Session!);
        }
    }
    public void ReleaseController() => Command(new ReleaseController());
    public void SetPlayerName(string name)
    {
        lock (_gate)
        {
            _playerName = PlayerNames.Normalize(name);
            if (_stop.IsCancellationRequested || Session is null) return;
            if (IsHost)
                Publish(Session with { HostName = PlayerNames.ForHost(_playerName) });
            else
                Command(new SetPlayerName(_playerName));
        }
    }
    public void SetPlayLocation(SessionMode mode) => Command(new SetPlayLocation(mode));
    public void SetPlaybackLatency(string? watchLink, bool? lowLatency)
    {
        lock (_gate)
        {
            if (IsHost || _stop.IsCancellationRequested || Session is null ||
                !_peers.TryGetValue("host", out var host))
                return;
            if (watchLink is not null && watchLink != Session.Settings.WatchLink) return;
            Send(host, new SetPlaybackLatency(watchLink, watchLink is null ? null : lowLatency));
        }
    }
    public bool TryGetRemotePlaybackLatency(out bool? mode)
    {
        lock (_gate)
        {
            mode = true;
            var viewers = false;
            foreach (var member in _peers.Values)
            {
                if (member.State is null || member.Playback is { WatchLink: null }) continue;
                viewers = true;
                var observed = member.Playback is { WatchLink: not null } report &&
                    report.WatchLink == Session?.Settings.WatchLink &&
                    Stopwatch.GetElapsedTime(member.PlaybackReceived) <= TimeSpan.FromSeconds(10)
                    ? report.LowLatency : null;
                if (observed == false) mode = false;
                else if (observed is null && mode == true) mode = null;
            }
            if (!viewers) mode = null;
            return viewers;
        }
    }
    public void SetPointerState(PointerState pointer)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || Session is null)
                return;
            pointer = Normalize(pointer);
            if (_pointer == pointer)
                return;
            _pointer = pointer;
            if (IsHost)
                Publish(Session with { HostPointerCalibrated = pointer.Calibrated });
            else if (_peers.TryGetValue("host", out var host))
                Send(host, new SetPointerState(pointer));
        }
    }
    private static PointerState Normalize(PointerState pointer) => pointer.Active
        ? pointer with { CalibrationStep = !pointer.Calibrated && pointer.CalibrationStep is >= 1 and <= 4 ? pointer.CalibrationStep : 0 }
        : default;
    private void Command(SessionCommand command)
    {
        lock (_gate)
            Send(_peers["host"], command);
    }
    public void Configure(SessionSettings settings)
    {
        if (
            settings.MoonlightHost is { } host
                && (host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            || string.IsNullOrWhiteSpace(settings.MoonlightApp)
            || settings.MoonlightApp.Length > 256
            || settings.MoonlightApp.Any(char.IsControl)
            || settings.WatchLink is { } link && (link.Length > 900 || StreamLink.StreamId(link) is null)
            || settings.InviteSeconds is < 5 or > 120
        )
            throw new ArgumentException("Invalid game session settings.");
        lock (_gate)
        {
            Host();
            var changed = Session!.Settings.WatchLink != settings.WatchLink;
            if (changed)
                foreach (var member in _peers.Values)
                    if (member.Playback is { WatchLink: not null }) member.Playback = null;
            Publish(Session! with { Settings = settings });
            if (changed)
                _ = PublishWatchLinkAsync();
        }
    }
    public void ShowInvite()
    {
        lock (_gate)
        {
            Host();
            if (!Session!.Settings.InviteEnabled) return;
            _inviteUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 120;
            Publish(Session with { InviteRevision = Session.InviteRevision + 1 });
        }
    }
    private void Host()
    {
        if (!IsHost || Session is null || _stop.IsCancellationRequested)
            throw new InvalidOperationException("Only the active host can do that.");
    }
    private void Publish(SessionState state)
    {
        foreach (var member in _peers.Values)
            if (!CanUseRemotePlay(member.State)) member.RemotePlay?.Stop.Cancel();
        var roster = _peers.Values.Select(x => x.State).OfType<Participant>().ToArray();
        state = state with { CalibrationTarget = CalibrationGuidance.SharedTarget(roster, _pointer.CalibrationStep) };
        if (Session == state && Session.Participants.SequenceEqual(roster))
            return;
        Session = state with
        {
            Participants = roster,
            InviteRequestTtlMs = (int)Math.Clamp((_inviteUntil - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency, 0, 120000),
        };
        StateChanged?.Invoke(Session);
        Broadcast(Session);
    }
    private void Broadcast(SessionCommand command)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(command);
        foreach (var member in _peers.Values.Where(x => x.State is not null))
            Send(member, bytes);
    }
    private static void Send(Member member, SessionCommand command) =>
        Send(member, JsonSerializer.SerializeToUtf8Bytes(command));
    private static void Send(Member member, ReadOnlySpan<byte> bytes)
    {
        try
        {
            member.Peer.SendSession(bytes);
        }
        catch (IOException)
        {
            _ = member.Peer.DisposeAsync();
        }
    }
    internal bool OpenGamePorts(int port = 47989)
    {
        if (!IsHost || Session?.Mode != SessionMode.HostGame)
            return false;
        _gameMapping ??= new(
            [new(6, port - 5), new(6, port), new(6, port + 21), new(17, port + 9), new(17, port + 10), new(17, port + 11)],
            _stop.Token,
            state =>
                Notice?.Invoke(
                    state.Endpoint is null
                        ? $"{state.Error} Remote video needs six free Sunshine mappings. Retrying; the game room and local play remain available."
                        : "The direct Sunshine connection is ready. Players can connect.",
                    state.Endpoint is null ? DesktopNotice.Warning : DesktopNotice.Success
                )
        );
        return true;
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
            return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        await StopRemoteHostingAsync();
        var peers = _peers.Values.ToArray();
        await _hub.DisposeAsync();
        await Task.WhenAll(peers.Select(x => x.Peer.DisposeAsync().AsTask()));
        await Task.WhenAll(peers.Select(x => x.Lifetime));
        if (_gameMapping is not null)
            await _gameMapping.DisposeAsync();
        foreach (var member in peers)
            member.PinLimit.Dispose();
        _stop.Dispose();
    }
}
