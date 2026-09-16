// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private void RecalibratePointer_Click(object sender, RoutedEventArgs e) => _session.Input?.RecalibratePointer();
    private async void RetryPointer_Click(object sender, RoutedEventArgs e) => await _session.RetryPointerAsync();
    private void StartPointerCalibration_Click(object sender, RoutedEventArgs e) => _session.ConfirmVideoPlaying();
    private void UpdatePointerButtons()
    {
        UpdatePointerAlignment();
        var available = CanCaptureControllers && _session.CanRetryPointer;
        SetVisible(CanCaptureControllers && _session.CanConfirmVideoPlaying, StartPointerCalibrationButton);
        var retryOsc = _session.Input?.CanRetryPointer == true;
        RetryPointerButton.Content = _session.RetryingPointer ? "Retrying…" : retryOsc ? "Retry OSC" : "Retry controllers";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RetryPointerButton,
            retryOsc ? "Retry VRChat OSC connection" : "Retry VR controllers");
        ToolTipService.SetToolTip(RetryPointerButton, retryOsc
            ? "Restart OSC discovery and the pointer connection"
            : "Reconnect VR controllers without restarting the stream");
        RetryPointerButton.IsEnabled = available;
        RecalibratePointerButton.IsEnabled = available && (PointerStatusText.Text == "Ready" || PointerStatusText.Text.StartsWith("Point ", StringComparison.Ordinal));
    }
    private void PointerTouch_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            _session.EnableTouch(PointerTouchToggle.IsOn);
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            PointerTouchToggle.IsOn = false;
            Show($"Touch controls could not start: {error.Message}", InfoBarSeverity.Warning);
        }
    }
    private void PointerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _session.EnablePointer(PointerToggle.IsOn);
    }
    private void PointerStatus(string status)
    {
        if (PointerStatusText is null) return;
        PointerStatusText.Text = status == "Waiting for video in VRChat"
            ? "Waiting for video in VRChat. Reload the video, or select Start calibration if your stream is already visible."
            : status;
        if (_session.Input is not null && CanCaptureControllers &&
            (status is "Off" or "Ready" || status.StartsWith("Point ", StringComparison.Ordinal)))
            PointerToggle.IsOn = status != "Off";
        UpdatePointerButtons();
    }
}
