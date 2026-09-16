// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private UpdateDialog? _updateDialog;
    private bool _automaticUpdateCheckStarted;
    private bool UpdateSessionActive => _captureState != CaptureState.Idle || _session.GameSession is not null || _session.Joining;
    private UpdateDialog CreateUpdateDialog(AppUpdates updates, UpdateRelease? initialRelease = null) =>
        new(updates.Identity?.Version, AppUpdates.InstalledNotes(), updates.Source is not null,
            updates.CheckAsync, async release =>
            {
                if (UpdateSessionActive || _closing || _windowClosed) return false;
                if (!SaveSettings(ReadSettings())) return false;
                return await AppUpdates.OpenInstallerAsync(release);
            }, initialRelease, SettingsStore.SkipUpdateVersion, SettingsStore.AutomaticUpdatePrompts,
            enabled => SettingsStore.AutomaticUpdatePrompts = enabled);
    private async Task CheckForUpdatesOnLaunchAsync()
    {
        if (_automaticUpdateCheckStarted || _closing || _windowClosed)
            return;
        _automaticUpdateCheckStarted = true;
        try
        {
            if (!SettingsStore.AutomaticUpdatePrompts)
                return;
            while (_firstLaunchFlow && !_closing && !_windowClosed)
                await Task.Delay(100);
            if (_closing || _windowClosed || _updateDialog is not null || !SettingsStore.AutomaticUpdatePrompts)
                return;
            using var updates = new AppUpdates();
            var release = await updates.CheckAsync(CancellationToken.None);
            if (_closing || _windowClosed || _updateDialog is not null || updates.Identity is not { } identity ||
                release is null || release.Version <= identity.Version || !SettingsStore.ShouldPromptForUpdate(release.Version))
                return;
            _updateDialog = CreateUpdateDialog(updates, release);
            _updateDialog.SetSessionActive(UpdateSessionActive);
            DismissSharingTip();
            JoinFlyout.Hide();
            await ShowDialogAsync(_updateDialog);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
        finally { _updateDialog = null; }
    }
    private async void Updates_Click(object sender, RoutedEventArgs e)
    {
        if (_updateDialog is not null || _previewSuspended || _closing || _windowClosed) return;
        try
        {
            using var updates = new AppUpdates();
            _updateDialog = CreateUpdateDialog(updates);
            _updateDialog.SetSessionActive(UpdateSessionActive);
            DismissSharingTip();
            JoinFlyout.Hide();
            await ShowDialogAsync(_updateDialog);
        }
        catch { Show("Couldn’t open updates. Try again in a moment.", InfoBarSeverity.Warning); }
        finally { _updateDialog = null; }
    }
}
