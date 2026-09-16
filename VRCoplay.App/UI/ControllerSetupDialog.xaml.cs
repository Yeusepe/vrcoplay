// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
namespace VRCoplay;
public sealed partial class ControllerSetupDialog : ContentDialog
{
    internal static readonly Uri DriverDownload = new("https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.7/USBip-0.9.7.7-x64.exe");
    private static readonly Uri SteamVrDownload = new("https://store.steampowered.com/app/250820/SteamVR/");
    private readonly Func<Task> _refresh;
    private readonly Func<Uri, Task<bool>> _openUri;
    private ControllerReadiness _readiness = ControllerReadiness.Checking;
    private bool _driverDownloadOpened;
    private bool _steamVrDownloadOpened;
    private bool _busy;
    private bool _openingDownload;
    private bool Complete => _readiness.CanCreateGamepads && _readiness.SteamVrInstalled;
    private bool WaitingForInstall => _readiness.CanCreateGamepads ? _steamVrDownloadOpened : _driverDownloadOpened;
    internal ControllerSetupDialog(ControllerReadiness readiness, Func<Task> refresh, Func<Uri, Task<bool>>? openUri = null)
    {
        InitializeComponent();
        _refresh = refresh;
        _openUri = openUri ?? (async uri => await Windows.System.Launcher.LaunchUriAsync(uri));
        UpdateStatus(readiness);
        Opened += (_, _) => { ResizeBody(); XamlRoot.Changed += Root_Changed; };
        Closed += (_, _) => XamlRoot.Changed -= Root_Changed;
    }
    internal void UpdateStatus(ControllerReadiness readiness)
    {
        _readiness = readiness;
        var checking = readiness.Driver == ControllerDriverState.Checking;
        var unavailable = readiness.Driver == ControllerDriverState.Unavailable;
        var driverReady = readiness.CanCreateGamepads;
        SetupHeading.Text = Complete ? "Controller setup complete"
            : unavailable ? "Check controller setup"
            : driverReady ? "Use VR controllers"
            : _driverDownloadOpened ? "Install the driver"
            : "Play with controllers";
        SetupDescription.Text = Complete ? "Start SteamVR when you’re ready to use your VR controllers."
            : unavailable ? "The controller driver isn’t responding. If you just installed it, restart your PC."
            : driverReady ? _steamVrDownloadOpened
                ? "In Steam, install SteamVR. Return here when installation finishes."
                : "Install SteamVR to use your VR controllers with VRCoplay."
            : _driverDownloadOpened ? "Open the downloaded installer and follow its steps. Return here when you’re done."
            : "Use VR controllers in your games, or let friends play on this PC.";
        SetupNote.Text = Complete || driverReady ? "Friends can already control games on this PC."
            : _driverDownloadOpened ? "Windows may ask for permission or a restart."
            : "Screen sharing works without controller setup.";
        SetupIcon.Glyph = WaitingForInstall ? "\uE896" : "\uE7FC";
        SetupIcon.Visibility = Complete || unavailable ? Visibility.Collapsed : Visibility.Visible;
        WarningIcon.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
        ReadyIcon.Visibility = Complete ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButtonText = Complete ? "Done" : _openingDownload ? "Opening…" : _busy ? "Checking…"
            : checking ? "Checking…" : unavailable ? "Check again"
            : WaitingForInstall ? "Check installation" : driverReady ? "Get SteamVR" : "Download driver";
        CloseButtonText = Complete ? "" : "Skip for now";
        IsPrimaryButtonEnabled = !_busy && !_openingDownload && !checking;
        SetupNote.Visibility = checking ? Visibility.Collapsed : Visibility.Visible;
        var showStatus = !Complete && (checking || WaitingForInstall || _busy);
        StatusRow.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
        CheckingRing.IsActive = checking || _busy;
        CheckingRing.Visibility = CheckingRing.IsActive ? Visibility.Visible : Visibility.Collapsed;
        SetupStatus.Text = checking || _busy ? "Checking this PC…" : "Waiting for installation…";
        SetupDetailsButton.Visibility = Complete ? Visibility.Collapsed : Visibility.Visible;
        SetupDetails.Text = driverReady
            ? "SteamVR reads your headset and VR controllers. Install it from Steam, then start it before playing.\n\nThe controller driver is ready on this PC. You can receive friends’ controller input even without SteamVR."
            : WaitingForInstall || unavailable
                ? "Open USBip-0.9.7.7-x64.exe from your downloads and allow Windows to make changes. Restart if the installer asks.\n\nVRCoplay checks for the driver automatically while setup is open. You can return to Controller setup in Settings."
                : "The usbip-win2 driver lets games on this PC receive controller input. SteamVR is also needed to read VR controllers.\n\nIf you’re controlling a friend’s game, only their PC needs the driver. You can return to Controller setup in Settings at any time.";
        DownloadAgainButton.Visibility = WaitingForInstall || unavailable ? Visibility.Visible : Visibility.Collapsed;
        DownloadAgainButton.Content = driverReady ? "Open SteamVR in Steam" : "Download driver again";
        if (Complete) SetupError.Visibility = Visibility.Collapsed;
        ResizeBody();
    }
    private async void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (Complete) return;
        args.Cancel = true;
        if (_busy || _openingDownload) return;
        SetupError.Visibility = Visibility.Collapsed;
        if (WaitingForInstall || _readiness.Driver == ControllerDriverState.Unavailable)
        {
            _busy = true;
            UpdateStatus(_readiness);
            try { await _refresh(); }
            catch { ShowError("Couldn’t check controller setup. Try again in a moment."); }
            finally { _busy = false; UpdateStatus(_readiness); }
        }
        else await OpenDownloadAsync();
    }
    private async Task OpenDownloadAsync()
    {
        if (_openingDownload) return;
        _openingDownload = true;
        var driverReady = _readiness.CanCreateGamepads;
        var errorMessage = driverReady ? "Couldn’t open SteamVR. Check your default browser, then try again."
            : "Couldn’t start the download. Check your default browser, then try again.";
        UpdateStatus(_readiness);
        try
        {
            if (!await _openUri(driverReady ? SteamVrDownload : DriverDownload))
            {
                ShowError(errorMessage);
                return;
            }
            if (driverReady) _steamVrDownloadOpened = true; else _driverDownloadOpened = true;
            SetupError.Visibility = Visibility.Collapsed;
        }
        catch { ShowError(errorMessage); }
        finally { _openingDownload = false; UpdateStatus(_readiness); }
    }
    private void SetupDetails_Click(object sender, RoutedEventArgs e) => FlyoutBase.ShowAttachedFlyout(SetupDetailsButton);
    private async void DownloadAgain_Click(object sender, RoutedEventArgs e)
    {
        FlyoutBase.GetAttachedFlyout(SetupDetailsButton).Hide();
        await OpenDownloadAsync();
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
            primaryMargin: false
        );
    }
    private void ShowError(string message) { SetupError.Text = message; SetupError.Visibility = Visibility.Visible; }
    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeBody();
    private void ResizeBody()
    {
        if (XamlRoot is null) return;
        DialogChrome.Resize(SetupScroll, XamlRoot.Size.Width, XamlRoot.Size.Height,
            GetTemplateChild("Title") as FrameworkElement, GetTemplateChild("CommandSpace") as FrameworkElement,
            372, 150, 112, 88);
    }
}
