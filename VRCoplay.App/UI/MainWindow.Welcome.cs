// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private Task<bool>? _sharingExplanation;
    private async void ShowWelcomeOnFirstLaunch(object sender, RoutedEventArgs e)
    {
        RootPage.Loaded -= ShowWelcomeOnFirstLaunch;
        _firstLaunchFlow = true;
        try
        {
            await SettingsStore.ConfirmSharingAsync(ConfirmSharingAsync);
            if (!_closing && !SettingsStore.ControllerSetupSeen)
                await ShowControllerSetupAsync();
        }
        catch (Exception error)
        {
            Show($"Could not finish setup. You can return to it in Settings. {error.GetBaseException().Message}", InfoBarSeverity.Warning);
        }
        finally
        {
            _firstLaunchFlow = false;
            if (_openJoinWhenLoaded) ShowJoinFlyout();
            if (_focusJoinedRoom) FocusJoinedRoom();
        }
    }
    private Task<bool> ConfirmSharingAsync() => _sharingExplanation ??= ExplainSharingAsync();
    private async Task<bool> ExplainSharingAsync()
    {
        DismissSharingTip();
        if (JoinFlyout.IsOpen)
        {
            _openJoinWhenLoaded = true;
            JoinFlyout.Hide();
        }
        try
        {
            return await ShowDialogAsync(new SharingWelcomeDialog()) == ContentDialogResult.Primary;
        }
        finally
        {
            _sharingExplanation = null;
            if (!_firstLaunchFlow && _openJoinWhenLoaded)
                ShowJoinFlyout();
            if (!_firstLaunchFlow && _focusJoinedRoom)
                FocusJoinedRoom();
        }
    }
    private async void SharingIntroduction_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await ConfirmSharingAsync())
                SettingsStore.RememberSharingExplained();
        }
        catch (Exception error)
        {
            Show($"Could not open the sharing introduction: {error.GetBaseException().Message}", InfoBarSeverity.Error);
        }
    }
}
