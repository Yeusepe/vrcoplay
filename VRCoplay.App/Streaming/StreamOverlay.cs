// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
namespace VRCoplay;
internal enum OverlayKind
{
    None,
    Invite,
    Warning,
    Calibration,
    CalibrationParty,
    PointerCalibration,
}
internal sealed record OverlayOptions(
    string? RoomCode = null,
    bool InviteEnabled = false,
    bool InvitePeriodic = true,
    int InviteSeconds = 15,
    bool InviteKeepVisible = false,
    bool Warning = false,
    OverlayKind Calibration = OverlayKind.None,
    bool Animate = true,
    int PointerStep = 0,
    bool PointerRetry = false,
    PointerCalibrationIssue PointerIssue = PointerCalibrationIssue.None
);
internal readonly record struct OverlayPresentation(OverlayKind Kind, string? Code, double Opacity, double Compact,
    int Step = 0, bool Retry = false, double Motion = 0, double Time = 0,
    PointerCalibrationIssue Issue = PointerCalibrationIssue.None);
internal sealed class StreamOverlay
{
    internal const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly Lock _gate = new();
    private OverlayOptions _options = new();
    private bool _requested,
        _wasReady;
    private double _inviteAge,
        _periodAge;
    private readonly double[] _noticeAges = new double[5];
    private long _lastTick,
        _requestExpires;
    private OverlayKind _lastKind;
    private double _pointerAge, _pointerStepAge;
    internal bool AnimationsEnabled { get { lock (_gate) return _options.Animate; } }
    internal void Configure(OverlayOptions options)
    {
        if (options.RoomCode is { } code && (code.Length != 6 || code.AsSpan().ContainsAnyExcept(Alphabet)))
            options = options with { RoomCode = null };
        options = options with { InviteSeconds = Math.Clamp(options.InviteSeconds, 5, 120) };
        lock (_gate)
        {
            var previous = _options;
            _options = options;
            if (options.PointerStep != previous.PointerStep)
            {
                _pointerStepAge = 0;
                if (options.PointerStep == 0 || previous.PointerStep == 0) _pointerAge = 0;
            }
            if (_requested && previous.Animate != options.Animate)
                _inviteAge = options.Animate ? _inviteAge + .25 : Math.Max(0, _inviteAge - .25);
            if (!options.Warning)
                _noticeAges[(int)OverlayKind.Warning] = 0;
            if (options.Calibration != previous.Calibration)
                _noticeAges[(int)OverlayKind.Calibration] = _noticeAges[(int)OverlayKind.CalibrationParty] = 0;
            if (!options.Animate)
                _noticeAges.AsSpan((int)OverlayKind.Warning).Fill(5.05);
            if (previous.InviteKeepVisible && !options.InviteKeepVisible)
                _inviteAge = options.Animate ? .25 : 0;
            if (options.RoomCode != previous.RoomCode)
            {
                _periodAge = 0;
                _wasReady = false;
            }
            if (
                options.InviteEnabled
                && options.RoomCode is not null
                && !_requested
                && (
                    !previous.InviteEnabled
                    || previous.RoomCode != options.RoomCode
                    || options.InvitePeriodic && !previous.InvitePeriodic
                    || options.InviteKeepVisible && !previous.InviteKeepVisible
                )
                && (options.InvitePeriodic || options.InviteKeepVisible)
            )
                RequestCore();
            if (!options.InviteEnabled)
                _requested = false;
        }
    }
    internal void RequestInvite(int validForMilliseconds = 120000)
    {
        lock (_gate)
            if (_options.InviteEnabled && _options.RoomCode is not null && validForMilliseconds > 0)
                RequestCore(Stopwatch.GetTimestamp(), Math.Min(120000, validForMilliseconds));
    }
    private void RequestCore(long now = 0, int validForMilliseconds = 120000)
    {
        _inviteAge = _requested && _options.Animate && _inviteAge >= .25 ? .25 : 0;
        _requested = true;
        _requestExpires =
            (now == 0 ? Stopwatch.GetTimestamp() : now) + Stopwatch.Frequency * validForMilliseconds / 1000;
        _periodAge = 0;
    }
    internal OverlayPresentation Present(long now, bool ready, int fps)
    {
        lock (_gate)
        {
            var elapsed = _lastTick == 0 ? 0 : Math.Max(0, (now - _lastTick) / (double)Stopwatch.Frequency);
            var dt = ready && _wasReady ? Math.Min(elapsed, 1.5 / fps) : 0;
            _lastTick = now;
            _wasReady = ready;
            var o = _options;
            var pointer = o.PointerStep is >= 1 and <= 5 && (o.PointerStep != 5 || _pointerStepAge < 2.5);
            if (pointer)
            {
                if (_lastKind != OverlayKind.PointerCalibration) dt = 0;
                _lastKind = OverlayKind.PointerCalibration;
                _pointerAge += dt;
                _pointerStepAge += dt;
                var fade = o.Animate ? Math.Clamp(_pointerAge / .3, 0, 1) : 1;
                if (o.PointerStep == 5 && o.Animate) fade *= Math.Clamp((2.5 - _pointerStepAge) / .3, 0, 1);
                return new(OverlayKind.PointerCalibration, null, fade, 0, o.PointerStep, o.PointerRetry,
                    o.Animate ? _pointerStepAge : -1, _pointerAge, o.PointerIssue);
            }
            var calibration = o.Calibration is OverlayKind.Calibration or OverlayKind.CalibrationParty;
            if (!calibration)
                _periodAge += dt;
            if (o.InviteEnabled && o.RoomCode is not null && o.InvitePeriodic && !_requested && _periodAge >= 90)
                RequestCore(now);
            if (_requested && _inviteAge == 0 && now > _requestExpires && !o.InviteKeepVisible)
                _requested = false;
            var kind =
                calibration ? o.Calibration
                : _requested && o.RoomCode is not null ? OverlayKind.Invite
                : o.Warning ? OverlayKind.Warning
                : OverlayKind.None;
            if (kind != _lastKind)
            {
                _lastKind = kind;
                dt = 0;
            }
            if (kind == OverlayKind.Invite)
            {
                _inviteAge += dt;
                var entrance = o.Animate ? .25 : 0;
                var end = entrance + o.InviteSeconds;
                if (!o.InviteKeepVisible && _inviteAge >= end + (o.Animate ? .25 : 0))
                {
                    _requested = false;
                    _lastKind = OverlayKind.None;
                    _periodAge = 0;
                    return default;
                }
                var opacity =
                    !o.Animate ? 1
                    : _inviteAge < entrance ? _inviteAge / entrance
                    : o.InviteKeepVisible || _inviteAge < end ? 1
                    : 1 - (_inviteAge - end) / .25;
                return new(kind, o.RoomCode, Math.Clamp(opacity, 0, 1), 0);
            }
            if (kind == OverlayKind.None)
                return default;
            var age = _noticeAges[(int)kind] += dt;
            return new(
                kind,
                null,
                o.Animate ? Math.Clamp(age / .25, 0, 1) : 1,
                o.Animate ? Math.Clamp((age - 4.25) / .8, 0, 1) : 1
            );
        }
    }
    internal void Reset()
    {
        lock (_gate)
        {
            _options = new();
            _requested = false;
            _inviteAge = _periodAge = 0;
            Array.Clear(_noticeAges);
            _lastTick = 0;
            _lastKind = OverlayKind.None;
            _wasReady = false;
            _pointerAge = _pointerStepAge = 0;
        }
    }
}
