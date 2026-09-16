// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal enum CaptureState
{
    Idle,
    Starting,
    Running,
    Stopping,
}
internal enum PlayLocation
{
    HostPc,
    MyPc,
    VideoPlayer,
}
internal sealed record CaptureRequest(StreamSettings Preferences, PlayLocation Location, CaptureSource? Source);
internal sealed partial class DesktopSession(
    Uri coordinator,
    Action<Action> dispatch,
    Func<StreamSettings> readSettings,
    Func<bool> animationsEnabled
)
{
    internal readonly StreamOverlay Overlays = new();
    internal readonly SessionNotifications Notifications = new();
    internal readonly Task AudioRecovery = QuietApplicationAudio.RecoverAsync();
    internal GameSession? GameSession;
    internal StreamSettings? ActiveSettings { get; private set; }
    internal PlayLocation? ActivePlayLocation { get; private set; }
    internal bool VideoPlayerOnly => ActivePlayLocation == PlayLocation.VideoPlayer;
    internal StreamSettings? GameSettings { get; private set; }
    internal bool IsHost => GameSession?.IsHost == true;
    internal bool Capturing => _cts is not null;
    internal event Action? FlowChanged,
        OverlayChanged,
        CaptureEnded,
        InputChanged,
        AddressStopped;
    internal event Action<StreamSettings>? CaptureReady;
    internal event Action<CaptureSource>? TargetChosen;
    internal event Action<SessionState>? RoomChanged;
    internal event Action<string, DesktopNotice>? Notice;
    internal event Action<string>? ControllerDescriptionChanged,
        PointerStatusChanged,
        WatchLinkChanged;
    internal event Action? TouchDisabled;
    internal event Action<bool>? AddressAvailabilityChanged;
    internal event Action<bool?, bool>? AddressChanged;
    private string _watchLink = LocalVideo.PlayerUrl;
    internal string WatchLink
    {
        get => _watchLink;
        set
        {
            if (_watchLink == value) return;
            _watchLink = value;
            _latencyMode = null;
            if (VideoPlayerOnly && _cts?.IsCancellationRequested == false)
                GameSession?.SetPlaybackLatency(value, null);
            WatchLinkChanged?.Invoke(value);
        }
    }
    private void Show(string message, DesktopNotice severity) => Notice?.Invoke(message, severity);
    private void UpdateFlow() => FlowChanged?.Invoke();
    private void ResetSession()
    {
        Overlays.Reset();
        EnableTouch(false);
        WatchLink = !IsHost ? GameSession?.Session?.Settings.WatchLink ?? LocalVideo.PlayerUrl : LocalVideo.PlayerUrl;
        CaptureEnded?.Invoke();
    }
}
