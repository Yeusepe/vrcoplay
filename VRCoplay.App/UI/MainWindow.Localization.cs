// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings)
            return;
        _settings.DisplayLanguage = LanguagePicker.SelectedIndex;
        if (!SaveSettings(ReadSettings()))
            return;
        AppStrings.ApplyDisplayLanguage(_settings.DisplayLanguage);
        AppInstance.Restart("");
        Show(AppStrings.Get("Language.RestartFailed", "The display language will apply the next time VRCoplay opens."),
            InfoBarSeverity.Warning);
    }
}
