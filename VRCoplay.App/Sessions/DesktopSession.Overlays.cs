// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal sealed partial class DesktopSession
{
    private bool? _latencyMode;
    private CalibrationNotice PointerNotice =>
        CalibrationGuidance.For(GameSession?.Session, IsHost, GameSession?.ParticipantId ?? "host", _localPointer);
    internal void ApplyInvitePolicy()
    {
        var invitation = readSettings();
        if (IsHost && GameSession?.Session is { } room)
            GameSession.Configure(
                room.Settings with
                {
                    InviteEnabled = invitation.InviteOverlay,
                    InvitePeriodic = invitation.InvitePeriodic,
                    InviteSeconds = invitation.InviteSeconds,
                    InviteKeepVisible = invitation.InviteKeepVisible,
                }
            );
    }
    internal void PrepareOverlays(StreamSettings settings)
    {
        var room = GameSession?.Session;
        var policy = !IsHost ? room?.Settings : null;
        var sharedVideo = room is not null && (IsHost || (GameSettings ?? ActiveSettings ?? settings).Mode == SessionMode.HostGame);
        var pointerStep = CalibrationGuidance.SetupStep(room, sharedVideo, _pointerProgress.Step);
        var latencyMode = _latencyMode;
        if (IsHost && DirectShare is not null && GameSession!.TryGetRemotePlaybackLatency(out var remoteLatency))
            latencyMode = remoteLatency;
        Volatile.Write(ref _pointerTarget, sharedVideo ? room!.CalibrationTarget is >= 1 and <= 4 ? room.CalibrationTarget : -1 : 0);
        Overlays.Configure(
            new(
                room?.Code,
                room is not null && (policy?.InviteEnabled ?? settings.InviteOverlay),
                policy?.InvitePeriodic ?? settings.InvitePeriodic,
                policy?.InviteSeconds ?? settings.InviteSeconds,
                policy?.InviteKeepVisible ?? settings.InviteKeepVisible,
                PlayerLatency.ShouldWarn(settings.World, settings.WarningOverlay, latencyMode),
                PointerNotice switch
                {
                    CalibrationNotice.Party => OverlayKind.CalibrationParty,
                    CalibrationNotice.None => OverlayKind.None,
                    _ => OverlayKind.Calibration,
                },
                animationsEnabled(),
                pointerStep,
                _pointerProgress.Retry && pointerStep == _pointerProgress.Step,
                _pointerProgress.Issue
            )
        );
        OverlayChanged?.Invoke();
    }
    private void PlayerLatencyChanged(bool? mode, CancellationToken stop)
    {
        if (stop.IsCancellationRequested)
            return;
        _latencyMode = mode;
        PrepareOverlays(readSettings());
    }
    internal void ApplyOverlaySettings(StreamSettings settings, bool warningOnly)
    {
        try
        {
            if (!warningOnly)
                ApplyInvitePolicy();
            PrepareOverlays(settings);
        }
        catch (Exception error)
        {
            Show(error.Message, DesktopNotice.Warning);
            OverlayChanged?.Invoke();
        }
    }
    internal void ShowInvite(StreamSettings settings)
    {
        try
        {
            ApplyInvitePolicy();
            GameSession?.ShowInvite();
            PrepareOverlays(settings);
            Overlays.RequestInvite();
            Show(
                "Invite requested. It appears when the shared picture is ready and calibration is complete.",
                DesktopNotice.Success
            );
        }
        catch (Exception error)
        {
            Show(error.Message, DesktopNotice.Warning);
        }
    }
}
