// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
public sealed partial class UpdateDialog : ContentDialog
{
    private readonly Func<CancellationToken, Task<UpdateRelease?>> _check;
    private readonly Func<UpdateRelease, Task<bool>> _install;
    private readonly Version? _current;
    private readonly string _installedNotes;
    private readonly bool _connected;
    private readonly Action<Version>? _skipVersion;
    private readonly Action<bool>? _setAutomaticPrompts;
    private readonly CancellationTokenSource _lifetime = new();
    private UpdateRelease? _release;
    private bool _checking;
    private bool _opening;
    private bool _failed;
    private bool _sessionActive;
    private bool _closed;
    private bool _automaticPromptsEnabled;
    private bool Available => _release is not null && _current is not null && _release.Version > _current;
    internal UpdateDialog(Version? current, string installedNotes, bool connected,
        Func<CancellationToken, Task<UpdateRelease?>> check, Func<UpdateRelease, Task<bool>> install,
        UpdateRelease? initialRelease = null, Action<Version>? skipVersion = null,
        bool automaticPromptsEnabled = true, Action<bool>? setAutomaticPrompts = null)
    {
        InitializeComponent();
        _current = current;
        _installedNotes = installedNotes;
        _connected = connected;
        _check = check;
        _install = install;
        _release = initialRelease;
        _skipVersion = skipVersion;
        _automaticPromptsEnabled = automaticPromptsEnabled;
        _setAutomaticPrompts = setAutomaticPrompts;
        Render();
        Opened += async (_, _) => { ResizeBody(); XamlRoot.Changed += Root_Changed; if (_connected && _release is null) await CheckAsync(); };
        Closed += (_, _) => { _closed = true; UpdateOptionsMenu.Hide(); _lifetime.Cancel(); XamlRoot.Changed -= Root_Changed; };
    }
    internal void SetSessionActive(bool active) { _sessionActive = active; Render(); }
    private async Task CheckAsync()
    {
        if (_checking || _opening || _closed) return;
        _checking = true;
        _failed = false;
        UpdateError.Visibility = Visibility.Collapsed;
        Render();
        try { _release = await _check(_lifetime.Token); }
        catch (OperationCanceledException) when (_closed) { }
        catch { _failed = true; }
        finally { _checking = false; if (!_closed) Render(); }
    }
    private async void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_checking || _opening || _closed) return;
        if (!Available) { await CheckAsync(); return; }
        if (_sessionActive) return;
        _opening = true;
        UpdateError.Visibility = Visibility.Collapsed;
        Render();
        try
        {
            if (await _install(_release!)) Hide();
            else ShowError(AppStrings.Get("Update.OpenInstallerFailed", "Couldn’t open Windows App Installer. Make sure App Installer is installed, then try again."));
        }
        catch { ShowError(AppStrings.Get("Update.OpenFailed", "Couldn’t open the update. Check your connection, then try again.")); }
        finally { _opening = false; if (!_closed) Render(); }
    }
    private void Render()
    {
        UpdateHeading.Text = _checking ? AppStrings.Get("Update.Checking", "Checking for updates")
            : _opening ? AppStrings.Get("Update.Opening", "Opening the update")
            : _failed ? AppStrings.Get("Update.CheckFailed", "Couldn’t check for updates")
            : !_connected ? AppStrings.Get("Update.Title", "VRCoplay updates")
            : Available ? AppStrings.Get("Update.Available", "Update available")
            : AppStrings.Get("Update.UpToDate", "You’re up to date");
        UpdateIcon.Glyph = Available ? "\uE896" : _failed || !_connected || _checking ? "\uE895" : "\uE930";
        VersionText.Text = Available
            ? AppStrings.Format("Update.VersionInstalled", "Version {0} · Installed {1}", _release!.Version, _current)
            : _current is null ? AppStrings.Get("Update.DevelopmentBuild", "Development build")
            : AppStrings.Format("Update.Version", "Version {0}", _current);
        UpdateDescription.Text = _checking ? AppStrings.Get("Update.Looking", "Looking for the latest version of VRCoplay.")
            : _opening ? AppStrings.Get("Update.ContinueInstaller", "Continue in Windows App Installer.")
            : _failed ? AppStrings.Get("Update.ConnectionRetry", "Check your connection, then try again. You can keep using VRCoplay.")
            : !_connected ? _current is null ? AppStrings.Get("Update.InstallTester", "Install a tester release to receive updates from Windows.")
                : AppStrings.Get("Update.ConnectInstaller", "Install using VRCoplay.appinstaller to connect this copy to updates.")
            : Available ? _sessionActive ? AppStrings.Get("Update.StopSession", "Stop sharing and leave your game room before updating.")
                : AppStrings.Get("Update.WindowsInstalls", "Windows will install the update. If asked, close VRCoplay to continue.")
            : AppStrings.Get("Update.Latest", "You have the latest version of VRCoplay.");
        PrimaryButtonText = !_connected ? "" : _checking ? AppStrings.Get("Update.CheckingAction", "Checking…")
            : _opening ? AppStrings.Get("Update.OpeningAction", "Opening…")
            : Available ? AppStrings.Get("Update.Action", "Update")
            : _failed ? AppStrings.Get("Update.CheckAgain", "Check again") : "";
        IsPrimaryButtonEnabled = !_checking && !_opening && !(Available && _sessionActive);
        CloseButtonText = Available || _checking ? AppStrings.Get("Update.NotNow", "Not now") : AppStrings.Get("Update.Done", "Done");
        DefaultButton = PrimaryButtonText.Length > 0 && IsPrimaryButtonEnabled ? ContentDialogButton.Primary : ContentDialogButton.Close;
        UpdateProgress.Visibility = _checking || _opening ? Visibility.Visible : Visibility.Collapsed;
        NotesText.Text = Available ? _release!.Notes : _installedNotes;
        CheckAgainItem.Visibility = _connected && !Available && !_failed ? Visibility.Visible : Visibility.Collapsed;
        SkipVersionItem.Visibility = Available && _skipVersion is not null ? Visibility.Visible : Visibility.Collapsed;
        AutomaticPromptsItem.Visibility = _setAutomaticPrompts is not null ? Visibility.Visible : Visibility.Collapsed;
        AutomaticPromptsItem.IsChecked = _automaticPromptsEnabled;
        UpdateOptionsButton.Visibility = CheckAgainItem.Visibility == Visibility.Visible || SkipVersionItem.Visibility == Visibility.Visible ||
            AutomaticPromptsItem.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateOptionsButton.IsEnabled = !_checking && !_opening;
        ResizeBody();
    }
    private async void CheckAgain_Click(object sender, RoutedEventArgs e)
    {
        if (_connected) await CheckAsync();
    }
    private void SkipVersion_Click(object sender, RoutedEventArgs e)
    {
        if (!Available || _skipVersion is null || _checking || _opening || _closed) return;
        try
        {
            _skipVersion(_release!.Version);
            Hide();
        }
        catch { ShowError(AppStrings.Get("Update.SavePreferenceFailed", "Couldn’t save that preference. Try again in a moment.")); }
    }
    private void AutomaticPrompts_Click(object sender, RoutedEventArgs e)
    {
        if (_setAutomaticPrompts is null || _checking || _opening || _closed) return;
        try
        {
            var enabled = AutomaticPromptsItem.IsChecked;
            _setAutomaticPrompts(enabled);
            _automaticPromptsEnabled = enabled;
            Render();
        }
        catch
        {
            AutomaticPromptsItem.IsChecked = _automaticPromptsEnabled;
            ShowError(AppStrings.Get("Update.SavePreferenceFailed", "Couldn’t save that preference. Try again in a moment."));
        }
    }
    private void ShowError(string text)
    {
        if (_closed) return;
        UpdateError.Text = text;
        UpdateError.Visibility = Visibility.Visible;
    }
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        DialogChrome.Apply(
            GetTemplateChild("Title") as ContentControl,
            GetTemplateChild("CommandSpace") as Grid,
            GetTemplateChild("PrimaryColumn") as ColumnDefinition,
            ResizeBody,
            commandsBottom: 16,
            twoRows: true,
            primary: GetTemplateChild("PrimaryButton") as Button,
            close: GetTemplateChild("CloseButton") as Button,
            pinButtons: true,
            primaryMargin: true
        );
    }
    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeBody();
    private void ResizeBody()
    {
        if (XamlRoot is null) return;
        DialogChrome.Resize(UpdateScroll, XamlRoot.Size.Width, XamlRoot.Size.Height,
            GetTemplateChild("Title") as FrameworkElement, GetTemplateChild("CommandSpace") as FrameworkElement,
            412, 140, 112, 88);
    }
}
