// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private StreamSettings _settings = new();
    private StreamSettings ReadSettings() => _settings.Snapshot();
    private bool CanEditSetting(bool settingsEnabled, bool available) => settingsEnabled && available;
    private void StreamerMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.StreamerMode = ((ToggleSwitch)sender).IsOn;
        UpdatePrivacy();
        SaveSettings(ReadSettings());
    }
    private void UpdatePrivacy()
    {
        UpdateRoomCodePresentation();
        UpdateStreamLink();
        if (_session.GameSession?.Session is { } room)
            ShowSession(room);
    }
    private void SetPrivateText(TextBlock control, string value, string label)
    {
        var hidden = _settings.StreamerMode && value.Length > 0;
        control.Text = hidden ? "••••••" : value;
        control.IsTextSelectionEnabled = !hidden;
        ToolTipService.SetToolTip(control, hidden ? "Hidden in streamer mode" : value);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control,
            hidden ? $"{label}, hidden in streamer mode" : $"{label} {value}".TrimEnd());
    }
    private async void DirectIpLink_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.UseDirectIp = ((ToggleSwitch)sender).IsOn;
        DirectIpLinkToggle.IsEnabled = false;
        try
        {
            await _session.SetUseDirectIpAsync(_settings.UseDirectIp);
        }
        catch (Exception error)
        {
            Show($"Could not change the stream connection: {error.GetBaseException().Message}", InfoBarSeverity.Error);
        }
        finally { DirectIpLinkToggle.IsEnabled = true; }
        DismissSharingTip();
        UpdateStreamLink();
        if (_captureState == CaptureState.Running)
            UpdateAddress(_addressReady, _addressConnecting);
        SaveSettings(ReadSettings());
    }
    private void LoadSettings()
    {
        _settings = SettingsStore.Load();
        RootPage.DataContext = _settings;
        Bindings.Update();
        UpdatePrivacy();
    }
    private bool SaveSettings(StreamSettings settings)
    {
        try
        {
            SettingsStore.Save(settings);
            return true;
        }
        catch (Exception error)
        {
            Show($"Could not save settings: {error.Message}", InfoBarSeverity.Warning);
            return false;
        }
    }
}
public sealed class OptionalNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int number ? (double)number : double.NaN;
    public object? ConvertBack(object value, Type targetType, object parameter, string language) =>
        double.IsNaN((double)value)
            ? targetType == typeof(int)
                ? int.Parse(parameter as string ?? "0")
                : null
            : (int)Math.Round((double)value);
}
