// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
namespace VRCoplay;
internal sealed partial class DesktopSession
{
    private VrControllerBridge? _input;
    private object? _retryingPointer;
    private StreamSettings? _controllerSettings;
    private SessionPlan? _inputPlan;
    private bool _controllerGuest, _pointerEnabled;
    private bool _videoStarted;
    private string? _controllerError;
    internal bool RetryingPointer => _retryingPointer is not null;
    internal bool CanRetryPointer => !RetryingPointer &&
        (_input?.CanRetryPointer == true || ControllerPlan is { LocalController: true } or { SendController: true });
    internal VrControllerBridge? Input => _input;
    internal bool CanConfirmVideoPlaying => _cts is { IsCancellationRequested: false } &&
        !_videoStarted && _pointerEnabled && _input?.CanRetryPointer == true;
    internal void ConfirmVideoPlaying()
    {
        if (CanConfirmVideoPlaying) VideoStarted(_cts!.Token);
    }
    internal string? ControllerDescription { get; private set; }
    private SessionPlan? ControllerPlan => _controllerSettings is { } settings
        ? SessionPlan.For(settings.Mode, _controllerGuest, settings.Controller, settings.AllowRequests,
            GameSession?.Session is not null)
        : null;
    internal void EnablePointer(bool enabled)
    {
        _pointerEnabled = enabled;
        if (_controllerSettings is { } settings)
            _controllerSettings = settings with { Pointer = enabled };
        _input?.EnablePointer(enabled);
        if (_input?.CanRetryPointer != true) PublishUnavailablePointer();
    }
    private void PublishUnavailablePointer()
    {
        var status = !_pointerEnabled ? "Off"
            : _controllerSettings is null ? "Start streaming to use VR pointing"
            : _controllerSettings.Mode == SessionMode.ScreenSharing ? "Choose Games to use VR pointing"
            : !_controllerSettings.Controller ? "Enable VR controllers, then restart streaming"
            : _controllerError is { } error ? $"VR controllers unavailable: {error.TrimEnd('.')}. Select Retry controllers."
            : "Local VR controllers are unavailable";
        PointerStatusChanged?.Invoke(status);
        InputChanged?.Invoke();
    }
    private void DescribeControllers(string message)
    {
        ControllerDescription = message;
        ControllerDescriptionChanged?.Invoke(message);
    }
    private WindowTouch? _pointerTouch;
    private PointerState _localPointer;
    private PointerCalibrationProgress _pointerProgress;
    private int _inputGeneration;
    private int _pointerTarget;
    private void PublishPointerState()
    {
        GameSession?.SetPointerState(_localPointer);
        PrepareOverlays(readSettings());
    }
    internal void StartControllers(StreamSettings settings, bool guest)
    {
        StopInput();
        RefreshControllers(settings, guest);
    }
    internal void RefreshControllers(StreamSettings settings, bool guest)
    {
        var plan = SessionPlan.For(settings.Mode, guest, settings.Controller, settings.AllowRequests,
            GameSession?.Session is not null);
        var reuse = _input is not null && _inputPlan == plan && _controllerGuest == guest &&
            _controllerSettings is { } previous && previous.Cemuhook == settings.Cemuhook &&
            previous.CemuhookPort == settings.CemuhookPort && previous.DualShock == settings.DualShock &&
            previous.DualShockMotionHand == settings.DualShockMotionHand && previous.ControllerHand == settings.ControllerHand;
        if (_controllerSettings is not null) settings = settings with { Pointer = _pointerEnabled };
        _controllerSettings = settings with { };
        _controllerGuest = guest;
        _pointerEnabled = settings.Pointer;
        if (reuse)
        {
            _input!.SetRemoteSlots(RemoteSlots);
            return;
        }
        _retryingPointer = null;
        StopControllerBridge();
        StartControllerBridge();
    }
    private void StartControllerBridge()
    {
        var settings = _controllerSettings!;
        var plan = ControllerPlan!;
        _controllerError = null;
        if (!plan.LocalController && !plan.SendController && !plan.ReceiveControllers)
        {
            DescribeControllers("Controllers are not needed for this activity.");
            PublishUnavailablePointer();
            return;
        }
        ReadOnlySpan<bool> captureAttempts =
            plan.LocalController && plan.ReceiveControllers
                ? [true, false]
                : [plan.LocalController || plan.SendController];
        foreach (var capture in captureAttempts)
        {
            try
            {
                SharingDiagnosticLog.Default.Write(_diagnosticSession, "controller-starting", task: capture ? "local-input" : "receive-only");
                if (settings.DualShock && (plan.LocalController || plan.ReceiveControllers) && LocalPorts.InUse(32341))
                    throw new InvalidOperationException(
                        "Another controller bridge is running. Close the other VRCoplay instance first."
                    );
                var generation = ++_inputGeneration;
                _input = new VrControllerBridge(
                    capture: capture,
                    virtualPads: plan.LocalController || plan.ReceiveControllers,
                    report: plan.SendController ? report => GameSession?.SendControllerReport(report) : null,
                    padCount: plan.ReceiveControllers ? 4 : 1,
                    dualShock: settings.DualShock,
                    dualShockMotionHand: settings.DualShockMotionHand,
                    cemuhook: settings.Cemuhook,
                    cemuhookPort: settings.CemuhookPort,
                    leftHanded: settings.ControllerHand == 1,
                    pointerStatus: status => PointerStatus(status, generation),
                    pointerEnabled: settings.Pointer,
                    pointerInput: UpdatePointerTouch,
                    calibrationChanged: calibrated =>
                        dispatch(() =>
                        {
                            if (generation != _inputGeneration || _input is null)
                                return;
                            _localPointer = _localPointer with { Calibrated = calibrated };
                            PublishPointerState();
                        }),
                    calibrationTarget: () => Volatile.Read(ref _pointerTarget),
                    calibrationProgress: progress => dispatch(() =>
                    {
                        if (generation != _inputGeneration || _input is null) return;
                        _pointerProgress = progress;
                        _localPointer = _localPointer with { CalibrationStep = progress.Step is >= 1 and <= 4 ? progress.Step : 0 };
                        PublishPointerState();
                    })
                );
                _inputPlan = plan;
                _localPointer = new(capture, false);
                if (_videoStarted) _ = _input.VideoStartedAsync();
                Notifications.ClearControllersStopped();
                InputChanged?.Invoke();
                PublishPointerState();
                _input.SetRemoteSlots(RemoteSlots);
                _input.ControllerTimedOut += slot =>
                {
                    if (generation == Volatile.Read(ref _inputGeneration)) GameSession?.RenewControllerGrant(slot);
                };
                if (!capture && plan.LocalController)
                {
                    DescribeControllers("Local SteamVR input is unavailable. Friends can still request controllers.");
                    PublishUnavailablePointer();
                }
                else if (capture)
                {
                    _controllerError = null;
                    DescribeControllers("VR controllers connected.");
                    if (_input.PointerStatus is { } status) PointerStatusChanged?.Invoke(status);
                }
                else
                {
                    DescribeControllers("Receiving friends' controllers.");
                    PublishUnavailablePointer();
                }
                SharingDiagnosticLog.Default.Write(_diagnosticSession, "controller-started", task: capture ? "local-input" : "receive-only");
                _ = WatchInputAsync(_input);
                return;
            }
            catch (Exception error)
            {
                StopControllerBridge();
                _controllerError = error.GetBaseException().Message;
                DescribeControllers(_controllerError);
                SharingDiagnosticLog.Default.Write(_diagnosticSession, "controller-start-failed", task: capture ? "local-input" : "receive-only", error: error);
            }
        }
        PublishUnavailablePointer();
        Show($"Controllers could not start: {ControllerDescription}. Video runs independently.", DesktopNotice.Warning);
        Notifications.ControllersFailed();
    }
    private async Task WatchInputAsync(VrControllerBridge input)
    {
        try
        {
            var error = await input.Fault;
            if (_input != input)
                return;
            StopControllerBridge();
            _controllerError = error.GetBaseException().Message;
            DescribeControllers(_controllerError);
            SharingDiagnosticLog.Default.Write(_diagnosticSession, "controller-stopped", error: error);
            PublishUnavailablePointer();
            Show(
                $"Controllers stopped: {error.GetBaseException().Message}. Video is independent of the controller connection.",
                DesktopNotice.Warning
            );
            Notifications.ControllersFailed();
        }
        catch (OperationCanceledException) { }
    }
    internal async Task RetryPointerAsync()
    {
        if (!CanRetryPointer)
            return;
        var operation = _retryingPointer = new object();
        var input = _input;
        InputChanged?.Invoke();
        try
        {
            if (input?.CanRetryPointer == true)
                await input.RetryPointerAsync();
            else
            {
                await Task.Yield();
                if (_retryingPointer != operation) return;
                StopControllerBridge();
                StartControllerBridge();
            }
        }
        catch (Exception error)
        {
            if (_retryingPointer == operation)
                PointerStatusChanged?.Invoke($"OSC retry failed: {error.Message}");
        }
        finally
        {
            if (_retryingPointer == operation)
            {
                _retryingPointer = null;
                InputChanged?.Invoke();
            }
        }
    }
    internal void StopInput()
    {
        _videoStarted = false;
        _controllerSettings = null;
        _controllerError = null;
        _retryingPointer = null;
        StopControllerBridge();
        PublishUnavailablePointer();
    }
    private int RemoteSlots => GameSession is { IsHost: true, Session: { } state } ? state.ControllerSlots : 0;
    private void VideoStarted(CancellationToken stop)
    {
        if (stop.IsCancellationRequested || _controllerSettings is null || _videoStarted) return;
        _videoStarted = true;
        _ = _input?.VideoStartedAsync();
        InputChanged?.Invoke();
    }
    private void StopControllerBridge()
    {
        Notifications.ClearControllersStopped();
        ++_inputGeneration;
        _inputPlan = null;
        Interlocked.Exchange(ref _input, null)?.Dispose();
        InputChanged?.Invoke();
        UpdatePointerTouch(default, 1);
        _localPointer = default;
        _pointerProgress = default;
        PublishPointerState();
    }
    internal void EnableTouch(bool enabled)
    {
        Interlocked.Exchange(ref _pointerTouch, null)?.Dispose();
        if (enabled)
        {
            Volatile.Write(ref _pointerTouch, new WindowTouch());
            Notifications.TouchReady();
        }
    }
    private bool UpdatePointerTouch(VrPointerOsc.Touch touch, float trigger)
    {
        var controls = Volatile.Read(ref _pointerTouch);
        if (controls is null)
            return false;
        try
        {
            if (GetAsyncKeyState(0x1B) < 0)
            {
                controls.Dispose();
                dispatch(() =>
                {
                    if (ReferenceEquals(_pointerTouch, controls))
                        TouchDisabled?.Invoke();
                });
            }
            else
            {
                var target = Target as CaptureWindow;
                controls.Update(target?.Hwnd ?? 0, PointerScreenPoint(touch, target), trigger);
            }
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            dispatch(() =>
            {
                if (!ReferenceEquals(_pointerTouch, controls))
                    return;
                TouchDisabled?.Invoke();
                Show($"Touch controls stopped: {error.Message}", DesktopNotice.Warning);
                Notifications.TouchFailed();
            });
        }
        return true;
    }
    private POINT? PointerScreenPoint(VrPointerOsc.Touch touch, CaptureWindow? target)
    {
        if (
            !touch.Active
            || target is null
            || _cts is not { IsCancellationRequested: false }
            || ActiveSettings is not { } s
            || IsIconic(target.Hwnd)
            || !GetClientRect(target.Hwnd, out var rect)
        )
            return null;
        if (
            WindowTouch.Map(
                touch.X,
                touch.Y,
                new(s.CropLeft, s.CropTop, rect.Width - s.CropRight, rect.Height - s.CropBottom),
                s.Width,
                s.Height
            )
            is not { } point
        )
            return null;
        return
            ClientToScreen(target.Hwnd, ref point)
            && GetAncestor(WindowFromPoint(point), GetAncestorFlag.GA_ROOTOWNER)
                == GetAncestor(target.Hwnd, GetAncestorFlag.GA_ROOTOWNER)
            ? point
            : null;
    }
    private void PointerStatus(string status, int generation) =>
        dispatch(() =>
        {
            if (generation != _inputGeneration || _input is null)
                return;
            PointerStatusChanged?.Invoke(status);
        });
}
