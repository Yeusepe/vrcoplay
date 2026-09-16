// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private void InitializeMedia()
    {
        const string standby = "Showing the standby screen while the capture source is unavailable.";
        _session.FallbackChanged += (waiting, streamToken) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (streamToken.IsCancellationRequested)
                    return;
                _session.Notifications.VideoWaiting(waiting);
                if (waiting)
                    Show(standby, InfoBarSeverity.Informational);
                else if (StatusBar.Message is standby or "Switching capture source...")
                    StatusBar.IsOpen = false;
            });
        _session.OverlayFailed += (error, streamToken) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!streamToken.IsCancellationRequested)
                    Show($"Overlay artwork could not be loaded: {error.Message}", InfoBarSeverity.Warning);
            });
        _session.MediaStarted += () =>
        {
            if (StatusBar.Message == "Checking the encoder and starting the local server...")
                StatusBar.IsOpen = false;
        };
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_captureState == CaptureState.Idle)
            StartCapture("start-button");
        else
            await StopCaptureAsync("stop-button");
    }
    private void StartCapture(string reason = "capture-api")
    {
        if (_closing || _windowClosed || _captureState != CaptureState.Idle)
            return;
        _session.StartCapture(
            () =>
                new(
                    ReadAvailableSettings(),
                    SelectedPlayLocation,
                    WindowPicker.SelectedSource
                ),
            ConfirmSharingAsync,
            RequestControlAsync,
            reason
        );
    }
    private Task StopCaptureAsync(string reason = "capture-api") => _session.StopCaptureAsync(reason);
    private CaptureState _captureState => _session.State;
    private bool _choosingPlay;
    private PlayLocation SelectedPlayLocation => PlayLocationPicker.SelectedIndex switch
    {
        1 => PlayLocation.MyPc,
        2 => PlayLocation.VideoPlayer,
        _ => PlayLocation.HostPc,
    };
    private async void RotateStreamLink_Click(object sender, RoutedEventArgs e)
    {
        if (_captureState != CaptureState.Running || _session.DirectShare is null)
            return;
        RotateStreamLinkButton.IsEnabled = false;
        await StopCaptureAsync("link-rotation");
        StartCapture("link-rotation");
    }
    private bool? _addressReady;
    private bool _addressConnecting;
    private string _streamLink = "";
    private string _questStreamLink = "";
    private void UpdateStreamLink()
    {
        var share = _session.DirectShare;
        var link = _session.VideoPlayerOnly ? _session.GameSession?.Session?.Settings.WatchLink ?? ""
            : _settings.UseDirectIp
            ? share is null ? LocalVideo.PublishUrl : share.DirectLink ?? ""
            : share is null ? _session.WatchLink : share.InternetMapped ? share.Link ?? "" : "";
        _streamLink = _settings.UseDirectIp ? link.Replace("rtsp://", "rtspt://", StringComparison.Ordinal) : link;
        SetPrivateText(StreamUrlBox, _streamLink, "Video Player link");
        CopyStreamLinkButton.IsEnabled = _streamLink.Length > 0;
        UpdateQuestStreamLink();
    }
    private void UpdateQuestStreamLink()
    {
        var quest = _session.QuestShare;
        var state = quest?.State;
        SetVisible(quest is not null, QuestStreamUrlRow);
        var link = state?.Ready == true ? _session.DirectShare?.QuestLink(_settings.UseDirectIp) : null;
        _questStreamLink = link ?? "";
        SetPrivateText(QuestStreamUrlBox, _questStreamLink, "Quest Video Player link");
        CopyQuestStreamLinkButton.IsEnabled = link is not null;
        QuestStreamStatus.Text = link is not null
            ? "Use this in the Video Player’s Quest or alternate URL field. Video comes directly from this PC."
            : state?.Ready == true
                ? !_settings.UseDirectIp && _session.DirectShare?.InternetMapped == true
                    ? "Preparing the short Quest link…"
                    : _session.DirectShare?.State?.Error ?? "Opening the direct Quest connection…"
                : state?.Message ?? "";
    }
    private void CopyQuestUrl_Click(object sender, RoutedEventArgs e)
    {
        UpdateQuestStreamLink();
        if (_questStreamLink.Length > 0)
            Copy(_questStreamLink, "Quest Video Player link");
    }
    private void UpdateAddress(bool? ready, bool connecting)
    {
        _addressReady = ready;
        _addressConnecting = connecting;
        var notice = _session.NetworkNotice;
        if (_settings.UseDirectIp)
        {
            UpdateStreamLink();
            ready = _streamLink.Length > 0;
            notice = ready == true ? null : _session.DirectShare?.State?.Error;
            connecting = ready != true && notice is null;
        }
        var text =
            notice
            ?? (
                _settings.UseDirectIp
                    ? ready == true
                        ? "Direct IP link ready. Copy it to a VRChat® Video Player."
                        : "Preparing the direct IP link…"
                    : ready switch
                    {
                        null => "Checking the Video Player link…",
                        true => "Ready to share. Copy the link to a VRChat® Video Player.",
                        false =>
                            "Your video is running, but the server does not respond. Try the Video Player link again shortly.",
                    }
            );
        if (AddressStatus.Text != text && notice is null && ready == true)
            ShowSharingTip(
                "Your Video Player link is ready",
                "Copy this link and paste it into a VRChat® Video Player. Share it with people you trust."
            );
        var animate = connecting && _uiSettings.AnimationsEnabled;
        AddressProgress.IsActive = animate;
        SetVisible(animate, AddressProgress);
        AddressStateIcon.Symbol =
            connecting ? Symbol.Clock
            : notice is null && ready == true ? Symbol.Accept
            : Symbol.Important;
        SetVisible(!animate, AddressStateIcon);
        AddressStatus.Text = text;
    }
}
