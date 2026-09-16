// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private ControllerReadiness _controllerReadiness = ControllerReadiness.Checking;
    private DispatcherQueueTimer? _controllerCheckTimer;
    private Task? _controllerCheck;
    private ControllerSetupDialog? _controllerSetupDialog;
    private bool _firstLaunchFlow;
    private bool SendsControllersToHost => _session.GameSession?.Session is not null && !_session.IsHost &&
        (_session.ActivePlayLocation ?? SelectedPlayLocation) is PlayLocation.HostPc or PlayLocation.VideoPlayer;
    private bool CanOutputControllers => _controllerReadiness.CanOutput(_settings.Cemuhook, _settings.DualShock);
    private bool CanCaptureControllers => _controllerReadiness.CanCapture(SendsControllersToHost, _settings.Cemuhook, _settings.DualShock);
    private StreamSettings ReadAvailableSettings()
    {
        var preferences = ReadSettings();
        var controller = preferences.Controller && _controllerReadiness.CanCapture(SendsControllersToHost, preferences.Cemuhook, preferences.DualShock);
        return preferences with
        {
            Controller = controller,
            AllowRequests = preferences.AllowRequests && _controllerReadiness.CanOutput(preferences.Cemuhook, preferences.DualShock),
            DualShock = preferences.DualShock && _controllerReadiness.CanCreateGamepads,
            Pointer = preferences.Pointer && controller,
        };
    }
    private void InitializeControllerSetup()
    {
        _controllerCheckTimer = DispatcherQueue.CreateTimer();
        _controllerCheckTimer.Interval = TimeSpan.FromSeconds(3);
        _controllerCheckTimer.Tick += async (_, _) =>
        {
            if (_windowActive || _controllerSetupDialog is not null) await RefreshControllerReadinessAsync();
        };
        RootPage.Loaded += async (_, _) =>
        {
            _controllerCheckTimer.Start();
            await RefreshControllerReadinessAsync();
        };
        Activated += async (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                await RefreshControllerReadinessAsync();
        };
        Closed += (_, _) => _controllerCheckTimer.Stop();
    }
    private Task RefreshControllerReadinessAsync() => _controllerCheck is { IsCompleted: false }
        ? _controllerCheck : _controllerCheck = CheckControllerReadinessAsync();
    private async Task CheckControllerReadinessAsync()
    {
        try
        {
            var readiness = await Task.Run(ControllerDependencyProbe.Read);
            if (_windowClosed || _closing) return;
            if (_controllerReadiness != readiness)
            {
                _controllerReadiness = readiness;
                UpdateControllerAvailability();
            }
            _controllerSetupDialog?.UpdateStatus(readiness);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
            if (_windowClosed || _closing) return;
            _controllerReadiness = new(ControllerDriverState.Unavailable, false);
            UpdateControllerAvailability();
            _controllerSetupDialog?.UpdateStatus(_controllerReadiness);
        }
    }
    private void UpdateControllerAvailability()
    {
        if (_loadingSettings || ControllerSetupCard is null) return;
        var canCapture = CanCaptureControllers;
        var driverReady = _controllerReadiness.CanCreateGamepads;
        var help = _controllerReadiness.Help(SendsControllersToHost, _settings.Cemuhook, _settings.DualShock);
        var restartForInput = canCapture && ControllerToggle.IsOn && _session.ActiveSettings is { Controller: false };
        if (restartForInput) help = "Stop sharing, then start again to use your VR controllers.";
        ControllerSetupCard.Description = driverReady
            ? "Install SteamVR to use your VR controllers." : _controllerReadiness.DriverDescription;
        SetVisible(_controllerReadiness.Driver != ControllerDriverState.Checking &&
            ((_settings.DualShock && !driverReady) || !_controllerReadiness.SteamVrInstalled), ControllerSetupCard);
        UpdatePeople();
        ControllerCard.IsEnabled = SettingsSheet.IsEnabled && canCapture;
        ControllerCard.Description = canCapture && !restartForInput ? _session.ControllerDescription ?? help : help;
        AllowRequestsCard.IsEnabled = CanOutputControllers;
        RequirePinCard.IsEnabled = CanOutputControllers && AllowRequestsToggle.IsOn;
        var canPoint = canCapture && ControllerToggle.IsOn && !restartForInput;
        PointerCard.IsEnabled = canPoint;
        var canTouch = canPoint && !(_session.GameSession?.Session is not null && !_session.IsHost &&
            (_session.ActivePlayLocation ?? SelectedPlayLocation) == PlayLocation.VideoPlayer);
        PointerTouchCard.IsEnabled = canTouch;
        if (!canTouch && PointerTouchToggle.IsOn) PointerTouchToggle.IsOn = false;
        PointerAvailabilityText.Text = !canCapture || restartForInput ? help : "Enable VR controllers to use pointing and touch controls.";
        SetVisible(!canPoint, PointerAvailabilityText);
        var games = _session.GameSession?.Session is not null || ActivityPicker.SelectedIndex == 1;
        ControllerSetupNotice.Message = help;
        ControllerSetupNotice.IsOpen = games && (!canCapture || restartForInput);
        SetVisible(ControllerSetupNotice.IsOpen, ControllerSetupNotice);
        UpdatePointerButtons();
        if (_session.GameSession?.Session is { } room)
        {
            var self = room.Participants.FirstOrDefault(x => x.Id == _session.GameSession.ParticipantId);
            RequestControllerButton.IsEnabled = canCapture && self is not { WantsController: true } && self?.Slot is null;
        }
    }
    private void ControllerPreference_Changed(object sender, RoutedEventArgs e) => UpdateControllerAvailability();
    private void ControllerOutput_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        if (ReferenceEquals(sender, CemuhookToggle)) _settings.Cemuhook = CemuhookToggle.IsOn;
        if (ReferenceEquals(sender, DualShockToggle)) _settings.DualShock = DualShockToggle.IsOn;
        UpdateControllerAvailability();
    }
    private async void ControllerSetup_Click(object sender, RoutedEventArgs e)
    {
        try { await ShowControllerSetupAsync(); }
        catch (Exception error) { Show($"Could not open controller setup. {error.Message}", InfoBarSeverity.Warning); }
    }
    private async Task ShowControllerSetupAsync()
    {
        if (_controllerSetupDialog is not null || _closing || _windowClosed) return;
        DismissSharingTip();
        if (JoinFlyout.IsOpen) { _openJoinWhenLoaded = true; JoinFlyout.Hide(); }
        await RefreshControllerReadinessAsync();
        if (_controllerSetupDialog is not null || _closing || _windowClosed) return;
        var dialog = _controllerSetupDialog = new(_controllerReadiness, RefreshControllerReadinessAsync)
        {
            RequestedTheme = RootPage.ActualTheme,
        };
        try
        {
            try { SettingsStore.MarkControllerSetupSeen(); }
            catch (Exception error) { Show($"Setup preference could not be saved. {error.Message}", InfoBarSeverity.Warning); }
            await ShowDialogAsync(dialog);
        }
        finally
        {
            _controllerSetupDialog = null;
            if (!_firstLaunchFlow && _openJoinWhenLoaded) ShowJoinFlyout();
            if (!_firstLaunchFlow && _focusJoinedRoom) FocusJoinedRoom();
        }
    }
}
