// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private static InfoBarSeverity ToInfoBar(DesktopNotice notice) => notice switch
    {
        DesktopNotice.Success => InfoBarSeverity.Success,
        DesktopNotice.Warning => InfoBarSeverity.Warning,
        DesktopNotice.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };
    private StreamSettings ReadSessionSettings() =>
        ReadAvailableSettings() with
        {
            InviteOverlay = InviteToggle.IsOn,
            InvitePeriodic = InvitePeriodicToggle.IsOn,
        };
    private void InitializeSession()
    {
        _session.FlowChanged += UpdateFlow;
        _session.OverlayChanged += () => UpdateOverlayControls();
        _session.Notice += (text, severity) => Show(text, ToInfoBar(severity));
        _session.WatchLinkChanged += _ => UpdateStreamLink();
        _session.QuestStreamChanged += UpdateQuestStreamLink;
        _session.TargetChosen += target =>
        {
            RefreshWindows();
            WindowPicker.SelectedItem = WindowPicker.Sources.FirstOrDefault(target.IsSameSource);
        };
        _session.CaptureReady += _ =>
        {
            _choosingPlay = false;
            SaveSettings(ReadSettings());
        };
        _session.CaptureEnded += () =>
        {
            ResetSessionUi();
            if (_session.GameSession?.Session is { } room)
                ShowSession(room);
        };
        _session.RoomChanged += UpdateRoom;
        _session.ControllerDescriptionChanged += _ => UpdateControllerAvailability();
        _session.InputChanged += UpdatePointerButtons;
        _session.PointerStatusChanged += PointerStatus;
        _session.TouchDisabled += () => PointerTouchToggle.IsOn = false;
        _session.AddressChanged += UpdateAddress;
        _session.AddressAvailabilityChanged += _ =>
        {
            UpdateStreamLink();
            RotateStreamLinkButton.IsEnabled = _captureState == CaptureState.Running;
        };
        _session.AddressStopped += () =>
        {
            AddressProgress.IsActive = false;
            SetVisible(false, AddressProgress, AddressStateIcon);
        };
    }
}
