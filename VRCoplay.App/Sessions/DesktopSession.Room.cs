// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
namespace VRCoplay;
internal sealed partial class DesktopSession
{
    internal async Task CreateGameRoomAsync(StreamSettings? captureSettings = null)
    {
        var settings = captureSettings ?? ActiveSettings ?? readSettings();
        if (GameSession is not null || !settings.Games)
            return;
        GameSettings = settings;
        await ConnectGameSessionAsync(
            async (session, stop) =>
            {
                await session.CreateSessionAsync(settings.Mode, settings.RequirePin, settings.Controller, stop);
                var invitation = readSettings();
                session.Configure(
                    new(
                        MoonlightHost: settings.MoonlightHost.Length == 0 ? null : settings.MoonlightHost,
                        MoonlightApp: settings.MoonlightApp.Length == 0 ? "Desktop" : settings.MoonlightApp,
                        InviteEnabled: invitation.InviteOverlay,
                        InvitePeriodic: invitation.InvitePeriodic,
                        InviteSeconds: invitation.InviteSeconds,
                        InviteKeepVisible: invitation.InviteKeepVisible
                    )
                );
                if (GameSession != session)
                    return;
                PublishWatchLink();
                if (State == CaptureState.Running)
                    RefreshControllers(settings, false);
                Show(settings.Mode == SessionMode.HostGame && !settings.AllowRequests
                    ? "Game room ready. Friends can join and watch."
                    : "Game room ready. Friends can join and request controllers.", DesktopNotice.Success);
            }
        );
    }
    private async Task<bool> ConnectGameSessionAsync(
        Func<GameSession, CancellationToken, Task> connect,
        CancellationToken cancel = default
    )
    {
        var session = NewGameSession();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            _cts?.Token ?? CancellationToken.None,
            cancel
        );
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await connect(session, deadline.Token);
            cancel.ThrowIfCancellationRequested();
            return GameSession == session && session.Session is not null;
        }
        catch (Exception error)
        {
            if (GameSession != session)
                return false;
            await CloseGameSessionAsync();
            if (!cancel.IsCancellationRequested && _cts?.IsCancellationRequested != true)
            {
                if (Joining)
                {
                    Debug.WriteLine(error);
                    JoinFailed?.Invoke(error is OperationCanceledException
                        ? "The room didn’t respond. Ask the host to keep VRCoplay open, then try again."
                        : "Couldn’t join this room. Check the code and your connection, then try again.", false);
                }
                else
                    Show(
                        $"The game room could not connect: {error.GetBaseException().Message}. Video can run without a game room.",
                        DesktopNotice.Warning
                    );
            }
            return false;
        }
        finally
        {
            UpdateFlow();
        }
    }
    private GameSession NewGameSession()
    {
        Notifications.BeginRoom();
        var session = GameSession = new(coordinator.AbsoluteUri, readSettings().PlayerName);
        UpdateFlow();
        void OnUi(Action action) =>
            dispatch(() =>
            {
                if (GameSession == session)
                    action();
            });
        session.Notice += (message, severity) => OnUi(() => Show(message, severity));
        session.SessionEnded += reason =>
            OnUi(() =>
            {
                Show($"{reason} Video continues independently.", DesktopNotice.Warning);
                Notifications.HostDisconnected();
                _ = CloseGameSessionAsync();
            });
        session.InviteRequested += validForMilliseconds =>
        {
            var received = Stopwatch.GetTimestamp();
            OnUi(() =>
            {
                PrepareOverlays(readSettings());
                var remaining =
                    validForMilliseconds
                    - (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(received).TotalMilliseconds);
                Overlays.RequestInvite(remaining);
            });
        };
        session.PlaybackLatencyChanged += () => OnUi(() => PrepareOverlays(readSettings()));
        session.ControllerReport += (slot, report) =>
        {
            if (GameSession == session)
                _input?.SetRemote(slot, report);
        };
        session.StateChanged += room =>
        {
            if (GameSession != session)
                return;
            _input?.SetRemoteSlots(session.IsHost ? room.ControllerSlots : 0);
            OnUi(() =>
            {
                GameSession?.SetPointerState(_localPointer);
                PrepareOverlays(readSettings());
                if (!IsHost)
                {
                    if (ActivePlayLocation == PlayLocation.HostPc && AutomaticMoonlight)
                    {
                        if (VRCoplay.GameSession.CanUseRemotePlay(room.Participants.FirstOrDefault(p => p.Id == session.ParticipantId)))
                            _remotePlayApproved = true;
                        else if (_remotePlayApproved) _cts?.Cancel();
                    }
                    WatchLink = room.Settings.WatchLink ?? LocalVideo.PlayerUrl;
                    _localRoute?.SetFeed(room.Settings.WatchLink, "game");
                }
                RoomChanged?.Invoke(room);
                Notifications.UpdateRoom(room, IsHost, session.ParticipantId,
                    (GameSettings ?? ActiveSettings ?? readSettings()).AllowRequests);
                if (IsHost && (GameSettings ?? ActiveSettings)?.AllowRequests == false && GameSession is not null)
                    foreach (var person in room.Participants.Where(x => x.WantsController && x.Slot is null))
                        GameSession.DecideController(person.Id, false);
            });
        };
        return session;
    }
    internal async Task CloseGameSessionAsync()
    {
        var end = IsHost;
        var gameSession = Interlocked.Exchange(ref GameSession, null);
        PrepareOverlays(readSettings());
        GameSettings = null;
        if (gameSession is null)
            return;
        Notifications.EndRoom();
        _input?.SetRemoteSlots(0);
        await gameSession.DisposeAsync();
        if (!end && (VideoPlayerOnly || MoonlightStarted || ActivePlayLocation == PlayLocation.HostPc))
        {
            await StopCaptureAsync("game-room-closed");
            return;
        }
        if (_cts is null || !end && MoonlightStarted)
            StopInput();
        if (_cts is null)
            ResetSession();
        else
            UpdateFlow();
    }
    internal void ParticipantAction(string id, string? action)
    {
        if (GameSession is null)
            return;
        if (action == "Remove")
            GameSession.RemoveParticipant(id);
        else
        {
            if (action == "Approve" && _input is null)
            {
                StartControllers(GameSettings ?? readSettings(), false);
                if (_input is null)
                    throw new InvalidOperationException(ControllerDescription);
            }
            GameSession.DecideController(id, action == "Approve");
        }
    }
    internal async Task<bool> RequestControlAsync(Func<Task<string?>> askPin)
    {
        try
        {
            var pin =
                GameSession?.Session?.PinRequired == true
                    ? await askPin()
                    : "";
            if (pin is null || GameSession is null)
            {
                Show("Control request canceled.", DesktopNotice.Informational);
                return false;
            }
            GameSession.RequestController(pin, ActivePlayLocation == PlayLocation.HostPc && AutomaticMoonlight);
            Show("Controller request sent to the host.", DesktopNotice.Success);
            return true;
        }
        catch (Exception error)
        {
            Show($"Could not request control: {error.GetBaseException().Message}", DesktopNotice.Error);
            return false;
        }
    }
}
