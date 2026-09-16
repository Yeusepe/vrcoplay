// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal sealed partial class GameSession
{
    private sealed class RemotePlayLease(Guid requestId, CancellationToken stop)
    {
        internal Guid RequestId { get; } = requestId;
        internal CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(stop);
        internal Task Lifetime = Task.CompletedTask;
    }
    private readonly List<Task> _remoteWork = [];
    private Task<SunshineHost>? _remoteHost;
    private CancellationTokenSource? _remoteHostStop;
    private Task? _remoteShutdown;
    private TaskCompletionSource? _remoteApproval;
    private bool _remoteRequestSeen;
    private Guid _remoteRequestId;
    private TaskCompletionSource<RemotePlayReady>? _remoteResponse;
    internal string? RemotePlayDisplay { get; private set; }
    internal bool AutomaticRemotePlay => Session is { Settings.MoonlightHost: null };
    internal Func<string, IProgress<string>, CancellationToken, Task<SunshineHost>> StartRemotePlayHost { private get; init; } =
        (display, progress, stop) => SunshineHost.StartAsync(display, progress, stop);
    internal bool MapRemotePlayPorts { private get; init; } = true;
    internal void SelectRemotePlayDisplay(string display)
    {
        lock (_gate)
        {
            Host();
            if (_remoteShutdown is not null) throw new InvalidOperationException("Remote play is stopping. Try again in a moment.");
            if (string.IsNullOrWhiteSpace(display) || display.Length > 128 || display.Any(char.IsControl))
                throw new ArgumentException("Choose a display for remote play.");
            if (_remoteHost is not null && RemotePlayDisplay != display)
                throw new InvalidOperationException("End this room before changing the remote play display.");
            RemotePlayDisplay = display;
        }
    }
    internal static bool CanUseRemotePlay(Participant? person) => person is
        { Mode: SessionMode.HostGame, RemotePlayRequested: true, Controller: ControllerGranted };
    private void BeginRemotePlay(Member member, PairRemotePlay request)
    {
        if (request.RequestId == Guid.Empty || !AutomaticRemotePlay || !CanUseRemotePlay(member.State)
            || RemotePlayDisplay is null || member.RemotePlay is not null || _stop.IsCancellationRequested || _remoteShutdown is not null)
        {
            Send(member, new RemotePlayReady(request.RequestId, null, "Ask the host to approve remote play, then try Play again."));
            return;
        }
        var lease = member.RemotePlay = new(request.RequestId, _stop.Token);
        lease.Lifetime = Task.Run(() => ServeRemotePlayAsync(member, lease));
        _remoteWork.RemoveAll(task => task.IsCompleted);
        _remoteWork.Add(lease.Lifetime);
    }
    private async Task ServeRemotePlayAsync(Member member, RemotePlayLease lease)
    {
        SunshineHost? host = null;
        SunshinePlayer? player = null;
        try
        {
            Task<SunshineHost> setup;
            lock (_gate)
            {
                lease.Stop.Token.ThrowIfCancellationRequested();
                if (_remoteHost is null)
                {
                    var display = RemotePlayDisplay!;
                    var lifetime = _remoteHostStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    _remoteHost = Task.Run(async () =>
                    {
                        var server = await StartRemotePlayHost(display, new HostProgress(this), lifetime.Token).ConfigureAwait(false);
                        lock (_gate)
                            if (!lifetime.IsCancellationRequested && MapRemotePlayPorts) OpenGamePorts(server.Port);
                        return server;
                    });
                }
                setup = _remoteHost;
            }
            host = await setup.WaitAsync(lease.Stop.Token).ConfigureAwait(false);
            player = await host.PairAsync(lease.Stop.Token).ConfigureAwait(false);
            lock (_gate)
            {
                lease.Stop.Token.ThrowIfCancellationRequested();
                if (member.RemotePlay != lease || !CanUseRemotePlay(member.State))
                    throw new OperationCanceledException(lease.Stop.Token);
                Send(member, new RemotePlayReady(lease.RequestId, player.Credentials));
            }
            await host.Completion.WaitAsync(lease.Stop.Token).ConfigureAwait(false);
            throw new IOException("Remote play stopped on the host. Try joining the room again.");
        }
        catch (Exception error)
        {
            if (!lease.Stop.IsCancellationRequested)
            {
                lock (_gate)
                {
                    Send(member, new RemotePlayReady(lease.RequestId, null,
                        error is RemotePlayHostException hostError ? hostError.PlayerMessage
                            : error is OperationCanceledException ? "Remote play setup timed out on the host. Try Play again." : error.GetBaseException().Message));
                    if (member.RemotePlay == lease && member.State is { } person)
                    {
                        member.State = person with { Controller = null, RemotePlayRequested = false };
                        Publish(Session!);
                    }
                }
                Notice?.Invoke(error is RemotePlayHostException ? error.Message
                    : "Remote play could not connect. " + error.GetBaseException().Message, DesktopNotice.Warning);
            }
        }
        finally
        {
            if (player is not null && host is not null)
                try { await host.RevokeAsync(player.Id).ConfigureAwait(false); }
                catch (IOException error) { Notice?.Invoke(error.Message, DesktopNotice.Warning); }
            lock (_gate)
            {
                if (member.RemotePlay == lease) member.RemotePlay = null;
                if (_remoteHost is { IsFaulted: true } || _remoteHost is { IsCompletedSuccessfully: true, Result.Running: false })
                    _ = StopRemoteHostingAsync();
            }
            lease.Stop.Dispose();
        }
    }
    internal async Task<RemotePlayCredentials> PrepareRemotePlayAsync(CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(7));
        await WaitForRemoteApprovalAsync(deadline.Token).ConfigureAwait(false);
        Task<RemotePlayReady> result;
        lock (_gate)
        {
            if (!AutomaticRemotePlay) throw new InvalidOperationException("The host has not enabled automatic remote play.");
            _remoteRequestId = Guid.NewGuid();
            _remoteResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
            result = _remoteResponse.Task;
            Command(new PairRemotePlay(_remoteRequestId));
        }
        try
        {
            var ready = await result.WaitAsync(deadline.Token).ConfigureAwait(false);
            return ready.Credentials ?? throw new IOException(ready.Error ?? "Remote play setup failed. Try Play again.");
        }
        finally { lock (_gate) { _remoteResponse = null; _remoteRequestId = Guid.Empty; } }
    }
    private Task WaitForRemoteApprovalAsync(CancellationToken stop)
    {
        lock (_gate)
        {
            if (_remoteApproval is null) throw new InvalidOperationException("Request remote play from the host first.");
            return _remoteApproval.Task.WaitAsync(stop);
        }
    }
    private void RemoteApprovalChanged(SessionState state)
    {
        var person = state.Participants.FirstOrDefault(p => p.Id == ParticipantId);
        if (CanUseRemotePlay(person)) _remoteApproval?.TrySetResult();
        else if (person?.WantsController == true) _remoteRequestSeen = true;
        else
        {
            if (_remoteRequestSeen) _remoteApproval?.TrySetException(new IOException("The host declined or ended remote play."));
            _remoteResponse?.TrySetException(new IOException("The host ended remote play."));
        }
    }
    internal Task StopRemoteHostingAsync()
    {
        lock (_gate) return _remoteShutdown ??= Task.Run(StopRemoteHostingCoreAsync);
    }
    private async Task StopRemoteHostingCoreAsync()
    {
        Task<SunshineHost>? setup;
        Task[] work;
        DirectPortMapping? mapping;
        lock (_gate)
        {
            RemotePlayDisplay = null;
            _remoteHostStop?.Cancel();
            foreach (var member in _peers.Values)
            {
                member.RemotePlay?.Stop.Cancel();
                if (member.State is { RemotePlayRequested: true } person)
                    member.State = person with { Controller = null, RemotePlayRequested = false };
            }
            if (IsHost && Session is not null) Publish(Session);
            setup = _remoteHost;
            work = _remoteWork.ToArray();
            mapping = _gameMapping;
            _gameMapping = null;
        }
        try
        {
            if (setup is not null)
            {
                await ((Task)setup).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (setup.IsCompletedSuccessfully) await setup.Result.DisposeAsync().ConfigureAwait(false);
            }
            await Task.WhenAll(work).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (mapping is not null) await mapping.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _remoteHost = null;
                _remoteHostStop?.Dispose();
                _remoteHostStop = null;
                _remoteShutdown = null;
            }
        }
    }
    private sealed class HostProgress(GameSession session) : IProgress<string>
    {
        public void Report(string message) => session.Notice?.Invoke(message, DesktopNotice.Informational);
    }
}
