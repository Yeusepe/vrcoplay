// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private bool _loadingSettings = true;
    private async void Overlay_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
            return;
        await Task.Yield();
        ApplyOverlaySettings(ReferenceEquals(sender, WarningToggle));
    }
    private async void InviteDuration_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loadingSettings)
            return;
        if (double.IsNaN(e.NewValue))
        {
            sender.Value = 15;
            return;
        }
        await Task.Yield();
        ApplyOverlaySettings(false);
    }
    private void ApplyOverlaySettings(bool warningOnly)
    {
        SaveSettings(ReadSettings());
        _session.ApplyOverlaySettings(ReadSettings(), warningOnly);
    }
    private void ShowInvite_Click(object sender, RoutedEventArgs e)
    {
        if (!ShowInviteButton.IsEnabled || !InviteToggle.IsOn)
            return;
        _session.ShowInvite(ReadSettings());
    }
    private void UpdateOverlayControls(bool starting = false)
    {
        var editable = !_session.Capturing && !starting || _captureState == CaptureState.Running;
        InviteToggle.IsEnabled = editable && (_session.IsHost || _session.GameSession is null);
        WarningToggle.IsEnabled = editable;
        InviteKeepVisibleToggle.IsEnabled = InviteToggle.IsOn && InviteToggle.IsEnabled;
        InviteDurationBox.IsEnabled = InviteKeepVisibleToggle.IsEnabled && !InviteKeepVisibleToggle.IsOn;
        InvitePeriodicToggle.IsEnabled = InviteDurationBox.IsEnabled;
        ShowInviteButton.IsEnabled = InviteToggle.IsOn && _session.IsHost && _session.GameSession?.Session?.Code is { Length: 6 };
    }
}
