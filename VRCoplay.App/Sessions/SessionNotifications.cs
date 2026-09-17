// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal enum NotificationTarget { Stream, Controllers, Session, Requests }
internal enum NotificationCommand
{
    Open,
    ApproveController,
    DeclineController,
    RequestController,
    RestartCapture,
    RetryControllers,
    RetryTouch,
    ChooseCaptureSource,
}
internal sealed record NotificationAction(string Text, NotificationCommand Command);
internal sealed record SessionNotification(
    string Key, string Title, string Message, NotificationTarget Target,
    DesktopNotice Severity = DesktopNotice.Warning, string? RoomId = null, string? ParticipantId = null)
{
    internal NotificationAction[] Actions => Key switch
    {
        _ when Key.StartsWith("request:", StringComparison.Ordinal) =>
            [new("Approve", NotificationCommand.ApproveController), new("Decline", NotificationCommand.DeclineController)],
        "controller-access" when Title is "Controller request declined" or "Controller access removed" =>
            [new("Request again", NotificationCommand.RequestController)],
        "capture-stopped" => [new("Try again", NotificationCommand.RestartCapture)],
        "controllers-stopped" => [new("Retry controllers", NotificationCommand.RetryControllers)],
        "touch-stopped" => [new("Turn touch back on", NotificationCommand.RetryTouch)],
        "standby" => [new("Choose another source", NotificationCommand.ChooseCaptureSource)],
        _ => [],
    };
}
internal sealed class SessionNotifications(TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan InterruptionDelay = TimeSpan.FromSeconds(20);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, SessionNotification> _active = [];
    private readonly Dictionary<string, (long Since, SessionNotification Notice)> _delayed = [];
    private Participant? _self;
    private bool _localReleasePending, _hadVideo, _hadRemoteLink, _stopped;
    internal string? RoomId { get; private set; }
    internal event Action<SessionNotification>? Raised;
    internal event Action<string>? Cleared;
    internal bool IsCurrent(SessionNotification notice) =>
        _active.TryGetValue(notice.Key, out var current) && ReferenceEquals(current, notice);
    internal bool IsCurrentAction(NotificationActivation activation) =>
        activation.NotificationKey is { } key && _active.TryGetValue(key, out var notice) &&
        notice.RoomId == activation.RoomId && notice.ParticipantId == activation.ParticipantId &&
        notice.Actions.Any(action => action.Command == activation.Command);
    internal void BeginRoom()
    {
        EndRoom();
        Clear("session-ended");
        RoomId = Guid.NewGuid().ToString("N");
    }
    internal void EndRoom()
    {
        foreach (var key in _active.Keys.Where(x => x.StartsWith("request:", StringComparison.Ordinal)).ToArray())
            Clear(key);
        Clear("controller-access");
        RoomId = null;
        _self = null;
        _localReleasePending = false;
    }
    internal void HostDisconnected()
    {
        EndRoom();
        Raise(new("session-ended", "Game session disconnected",
            "The connection to the host ended. Video may continue independently.", NotificationTarget.Session));
    }
    internal void UpdateRoom(SessionState room, bool host, string participantId, bool allowRequests)
    {
        if (_stopped || RoomId is null) return;
        if (host)
        {
            var requests = room.Participants.Where(x => allowRequests && x.WantsController).ToArray();
            var keys = requests.Select(x => "request:" + x.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _active.Keys.Where(x => x.StartsWith("request:", StringComparison.Ordinal)).ToArray())
                if (!keys.Contains(key)) Clear(key);
            foreach (var person in requests)
                Raise(new("request:" + person.Id, "Controller requested", $"{person.Name} wants a controller.",
                    NotificationTarget.Requests, DesktopNotice.Informational, RoomId, person.Id));
            return;
        }
        var self = room.Participants.FirstOrDefault(x => x.Id == participantId);
        if (_localReleasePending)
        {
            if (self?.Controller is not null) return;
            _localReleasePending = false;
            _self = self;
            return;
        }
        var previous = _self;
        _self = self;
        if (previous is null || self is null) return;
        if (self.Controller is ControllerGranted grant && previous.Slot != grant.Slot)
            Replace(new("controller-access", "Controller assigned", $"Controller {grant.Slot + 1} assigned. You can play now.",
                NotificationTarget.Controllers, DesktopNotice.Success, RoomId));
        else if (self.Controller is null && previous.Controller is ControllerRequested)
            Replace(new("controller-access", "Controller request declined", "The host declined your controller request.",
                NotificationTarget.Controllers, RoomId: RoomId));
        else if (self.Controller is null && previous.Controller is ControllerGranted)
            Replace(new("controller-access", "Controller access removed", "The host removed your controller access. You can still watch.",
                NotificationTarget.Controllers, RoomId: RoomId));
        else if (self.Controller is ControllerRequested)
            Clear("controller-access");
    }
    internal void ReleasingController()
    {
        _localReleasePending = _self?.Controller is not null;
        Clear("controller-access");
    }
    internal bool IsPendingRequest(string? roomId, string? participantId) =>
        roomId is not null && roomId == RoomId && participantId is not null && _active.ContainsKey("request:" + participantId);
    internal void BeginCapture()
    {
        CaptureStopped();
        Clear("capture-stopped");
    }
    internal void CaptureStopped()
    {
        _hadVideo = _hadRemoteLink = false;
        Clear("standby");
        Clear("remote-link");
    }
    internal void CaptureFailed(bool guest, Exception? error = null) => Raise(new("capture-stopped", guest ? "Playing stopped" : "Sharing stopped",
        error?.GetBaseException() is DllNotFoundException or BadImageFormatException or FileNotFoundException
            ? "A required streaming component could not load. Update or reinstall VRCoplay."
        : guest ? "Playing stopped unexpectedly. Open VRCoplay to try again."
              : "Sharing stopped unexpectedly. Open VRCoplay to restart.",
        NotificationTarget.Stream, DesktopNotice.Error));
    internal void ControllersFailed() => Raise(new("controllers-stopped", "Controllers unavailable",
        "Controllers stopped or could not start. Video runs independently.", NotificationTarget.Controllers));
    internal void ClearControllersStopped() => Clear("controllers-stopped");
    internal void TouchFailed() => Raise(new("touch-stopped", "Touch controls stopped",
        "Touch controls are unavailable. Video runs independently.", NotificationTarget.Controllers));
    internal void TouchReady() => Clear("touch-stopped");
    internal void VideoWaiting(bool waiting)
    {
        if (!waiting) _hadVideo = true;
        Delay("standby", waiting && _hadVideo, new("standby", "Shared window unavailable",
            "Your stream is showing the standby screen. Restore the shared window or choose another application.",
            NotificationTarget.Stream));
    }
    internal void RemoteLinkAvailable(bool available)
    {
        if (available) _hadRemoteLink = true;
        Delay("remote-link", !available && _hadRemoteLink, new("remote-link", "Video Player link unavailable",
            "Your Video Player link is unavailable. VRCoplay is reconnecting automatically.", NotificationTarget.Stream));
    }
    private void Delay(string key, bool active, SessionNotification notice)
    {
        if (!active) Clear(key);
        else if (!_stopped && !_active.ContainsKey(key))
            _delayed.TryAdd(key, (_clock.GetTimestamp(), notice));
    }
    internal void Tick()
    {
        foreach (var (key, pending) in _delayed.ToArray())
            if (_clock.GetElapsedTime(pending.Since) >= InterruptionDelay)
            {
                _delayed.Remove(key);
                Raise(pending.Notice);
            }
    }
    private void Replace(SessionNotification notice)
    {
        Clear(notice.Key);
        Raise(notice);
    }
    private void Raise(SessionNotification notice)
    {
        if (!_stopped && _active.TryAdd(notice.Key, notice)) Raised?.Invoke(notice);
    }
    private void Clear(string key)
    {
        _delayed.Remove(key);
        if (_active.Remove(key)) Cleared?.Invoke(key);
    }
    internal void Stop()
    {
        _stopped = true;
        _delayed.Clear();
        foreach (var key in _active.Keys.ToArray()) Clear(key);
    }
}
