// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using static System.Threading.Tasks.ConfigureAwaitOptions;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private void InitializePeople()
    {
        RoomPeople.SetSavedName(_settings.PlayerName);
        RoomPeople.SaveName = name =>
        {
            if (!SaveSettings(ReadSettings() with { PlayerName = name }))
                return "Your name couldn’t be saved. Try again.";
            _settings.PlayerName = name;
            _session.GameSession?.SetPlayerName(name);
            return null;
        };
        RoomPeople.ActOnPlayer = ActOnPlayer;
    }
    private void PeopleFlyout_Opening(object sender, object e)
    {
        RoomPeople.Width = Math.Min(348, Math.Max(240, RootPage.ActualWidth - 64));
        UpdatePeople();
    }
    private void UpdatePeople()
    {
        if (_session.GameSession is not { Session: { } room } game) return;
        var count = room.Participants.Length + 1;
        ParticipantCountText.Text = count.ToString();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ParticipantsButton,
            $"People, {count} {(count == 1 ? "player" : "players")} in your game");
        var hint = !CanOutputControllers ? "Set up controllers in Settings to approve requests."
            : !(_session.GameSettings ?? _session.ActiveSettings ?? _settings).AllowRequests ? "Controller requests are turned off in Settings."
            : !game.HasAvailableControllerSlot ? "All controller slots are in use. Revoke control to free a slot."
            : "";
        RoomPeople.Update(room, game.ParticipantId, game.IsHost, hint.Length == 0, hint);
    }
    private async void CreateGameRoom_Click(object sender, RoutedEventArgs e) => await _session.CreateGameRoomAsync();
    private async void LeaveGameRoom_Click(object sender, RoutedEventArgs e)
    {
        await _session.CloseGameSessionAsync();
        Show("Left the game session. Any running video continues.", InfoBarSeverity.Informational);
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_session.GameSession?.Session is null || _captureState != CaptureState.Idle)
            return;
        if (_choosingPlay)
            StartCapture("play-button");
        else
        {
            _choosingPlay = true;
            UpdateFlow();
        }
    }
    private async void Watch_Click(object sender, RoutedEventArgs e)
    {
        await StopCaptureAsync("watch-button");
        _choosingPlay = false;
        UpdateFlow();
    }
    private async void RequestController_Click(object sender, RoutedEventArgs e) => await RequestControlAsync();
    private Task<bool> RequestControlAsync() =>
        (_session.ActivePlayLocation == PlayLocation.HostPc && _session.GameSession?.AutomaticRemotePlay == true)
            || CanCaptureControllers
            ? _session.RequestControlAsync(() => AskAsync("Play on the host's PC", "Controller PIN"))
            : Task.FromResult(false);
    private string? ActOnPlayer(string id, PlayerAction action)
    {
        if (_session.GameSession is not { IsHost: true, Session: { } room } game)
            return "Only the host can manage players.";
        var person = room.Participants.FirstOrDefault(x => x.Id == id);
        if (person is null) return "This player has left the game.";
        if (action is PlayerAction.Approve or PlayerAction.Decline && !person.WantsController)
            return "This controller request is no longer pending.";
        if (action == PlayerAction.Approve && (!CanOutputControllers ||
            !(_session.GameSettings ?? _session.ActiveSettings ?? _settings).AllowRequests))
            return "Enable controller requests in Settings and finish controller setup first.";
        if (action == PlayerAction.Approve && !game.HasAvailableControllerSlot)
            return "All controller slots are in use. Revoke control to free a slot.";
        if (action == PlayerAction.Approve && person.RemotePlayRequested)
        {
            if (_remoteApprovalOpen) return "Finish the current remote play request first.";
            _ = ApproveRemotePlayerAsync(game, person);
            return null;
        }
        try
        {
            _session.ParticipantAction(id, action switch
            {
                PlayerAction.Approve => "Approve",
                PlayerAction.Remove => "Remove",
                _ => "Deny",
            });
            UpdatePeople();
            return null;
        }
        catch (Exception error)
        {
            return $"Couldn’t update this player: {error.GetBaseException().Message}";
        }
    }
    private bool _remoteApprovalOpen;
    private async Task ApproveRemotePlayerAsync(GameSession game, Participant person)
    {
        _remoteApprovalOpen = true;
        try
        {
            ParticipantsButton.Flyout.Hide();
            CaptureSource? CurrentSource() => _session.ActiveSettings is not null
                ? _session.Target : WindowPicker.SelectedSource;
            var source = CurrentSource();
            var selected = CaptureMonitor.FromSource(source);
            if (game.RemotePlayDisplay is { } current && !StringComparer.OrdinalIgnoreCase.Equals(selected.DeviceName, current))
                throw new InvalidOperationException("The selected source is on a different display from the current remote play session. Stop sharing before changing the remote play display.");
            var content = new StackPanel { Spacing = 16 };
            content.Children.Add(new TextBlock
            {
                Text = $"Selected source: {source!.Label}",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"Remote play display: {selected.Label}",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = (source is CaptureWindow
                    ? "Remote play shares the entire display containing this window, including other apps. "
                    : "Remote play shares the entire selected display. ") +
                    $"{person.Name} can see everything on it and use a controller. " +
                    "Close private windows before sharing. You can revoke access in People.",
                TextWrapping = TextWrapping.Wrap,
            });
            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = "Approve remote play?", Content = content, PrimaryButtonText = "Share display",
                CloseButtonText = "Decline", DefaultButton = ContentDialogButton.Close,
            });
            if (_session.GameSession != game || game.Session?.Participants.FirstOrDefault(x => x.Id == person.Id)
                is not { WantsController: true, RemotePlayRequested: true }) return;
            if (result == ContentDialogResult.Primary)
            {
                var currentSource = CurrentSource();
                if (!source.IsSameSource(currentSource))
                    throw new InvalidOperationException("The selected source changed. Review the remote play request again.");
                var monitor = CaptureMonitor.FromSource(currentSource);
                if (!selected.IsSameSource(monitor))
                    throw new InvalidOperationException("The window moved to another display. Review the remote play request again.");
                game.SelectRemotePlayDisplay(monitor.DeviceName);
                _session.ParticipantAction(person.Id, "Approve");
            }
            else _session.ParticipantAction(person.Id, "Deny");
            UpdatePeople();
        }
        catch (Exception error) { Show(error.GetBaseException().Message, InfoBarSeverity.Error); }
        finally { _remoteApprovalOpen = false; }
    }
    private async Task<string?> AskAsync(string title, string header)
    {
        var input = new PasswordBox
        {
            Header = header,
            MaxLength = 32,
            PasswordRevealMode = _settings.StreamerMode ? PasswordRevealMode.Hidden : PasswordRevealMode.Visible,
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary ? input.Password.Trim() : null;
    }
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot = RootPage.XamlRoot;
        dialog.FontFamily = RootPage.FontFamily;
        _previewSuspended = true;
        UpdatePreview();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _previewSuspended = false;
            UpdatePreview();
        }
    }
    private void UpdateRoomHeader(SessionState room)
    {
        var discoverable = room.Code.Length > 0;
        RoomHeading.Text = discoverable ? "Room code" : "Game session";
        SetPrivateText(RoomCodeText, room.Code, "Room code");
        SetVisible(discoverable, RoomCodeRow);
        SetVisible(!discoverable, RoomDiscoveryStatus);
        ShareButton.IsEnabled = _session.GameSession?.ShareUrl.Length > 0;
    }
    private string? _shareQrUrl;
    private async void ShowSession(SessionState room)
    {
        if (_session.GameSession is not { } gameSession)
            return;
        SetPrivateText(ShareUrlText, gameSession.ShareUrl, "Room invite URL");
        UpdateRoomHeader(room);
        var pin = gameSession.Pin;
        SetPrivateText(ControlPinText, pin is { Length: 6 } ? pin[..3] + " " + pin[3..] : pin ?? "", "Controller PIN");
        CopyPinButton.IsEnabled = pin is not null;
        SetVisible(room.Mode == SessionMode.HostGame, ControllerPinSection);
        SetVisible(pin is not null, ControllerPinRow);
        ControllerPinHint.Text = pin is not null
            ? "Share this PIN with players who need a controller. You approve each request."
            : "No PIN is required. You still approve each controller request.";
        ShareHint.Text = _settings.StreamerMode ? "Copy the invite link to share it. Details are hidden in streamer mode."
            : "Share the link or scan the code to join.";
        SetVisible(!_settings.StreamerMode, ShareQrPanel);
        var qrUrl = _settings.StreamerMode ? "" : gameSession.ShareUrl;
        if (_shareQrUrl == qrUrl)
            return;
        _shareQrUrl = qrUrl;
        ShareQrImage.Source = null;
        if (qrUrl.Length == 0)
            return;
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(
            QRCoder.PngByteQRCodeHelper.GetQRCode(qrUrl, QRCoder.QRCodeGenerator.ECCLevel.Q, 8)
        );
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        if (!_settings.StreamerMode && _shareQrUrl == qrUrl && _session.GameSession == gameSession)
            ShareQrImage.Source = bitmap;
    }
    private void UpdateRoom(SessionState room)
    {
        ShowSession(room);
        UpdatePeople();
        var self = room.Participants.FirstOrDefault(x => x.Id == _session.GameSession?.ParticipantId);
        RequestControllerButton.Content =
            self?.Slot is int assigned ? $"Assigned controller {assigned + 1}"
            : self?.WantsController == true ? "Waiting for host"
            : "Request control";
        RequestControllerButton.IsEnabled = CanCaptureControllers && self is not { WantsController: true } && self?.Slot is null;
    }
}
