// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private bool _openJoinWhenLoaded;
    private bool _focusJoinedRoom;
    private Border[] _roomCodeFocus = [];
    private TextBlock[] _roomCodeCharacters = [];
    private string? _joinErrorInput;
    private void InitializeJoin()
    {
        _roomCodeFocus = [RoomCodeFocus0, RoomCodeFocus1, RoomCodeFocus2, RoomCodeFocus3, RoomCodeFocus4, RoomCodeFocus5];
        _roomCodeCharacters = [RoomCodeCharacter0, RoomCodeCharacter1, RoomCodeCharacter2, RoomCodeCharacter3, RoomCodeCharacter4, RoomCodeCharacter5];
        _session.JoinStarting += code =>
        {
            RoomCodeBox.Text = code;
            JoinFeedback.Visibility = Visibility.Collapsed;
            UpdateJoinControls();
            ShowJoinFlyout();
            AudiencePicker.SelectedIndex = 1;
        };
        _session.JoinLocationChosen += mode => PlayLocationPicker.SelectedIndex = mode == SessionMode.LocalGame ? 1 : 0;
        _session.JoinFailed += (message, open) =>
        {
            if (open) ShowJoinFlyout();
            JoinError(message);
        };
        JoinLauncher.Loaded += (_, _) =>
        {
            if (_openJoinWhenLoaded)
                ShowJoinFlyout();
        };
        var presenterStyle = JoinFlyout.FlyoutPresenterStyle;
        void SizeJoinFlyout()
        {
            if (JoinLauncher.ActualWidth <= 0) return;
            JoinFlyout.FlyoutPresenterStyle = new(typeof(FlyoutPresenter))
            {
                BasedOn = presenterStyle,
                Setters =
                {
                    new Setter(FrameworkElement.MinWidthProperty, JoinLauncher.ActualWidth),
                    new Setter(FrameworkElement.MaxWidthProperty, JoinLauncher.ActualWidth),
                },
            };
        }
        JoinFlyout.Opening += (_, _) =>
        {
            DismissSharingTip();
            SizeJoinFlyout();
        };
        JoinLauncher.SizeChanged += (_, e) =>
        {
            if (JoinFlyout.IsOpen && e.NewSize.Width != e.PreviousSize.Width) SizeJoinFlyout();
        };
        JoinFlyout.Opened += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (JoinFlyout.IsOpen && !_session.Joining)
                RoomCodeBox.Focus(FocusState.Programmatic);
        });
        JoinFlyout.Closed += (_, _) =>
        {
            if (_focusJoinedRoom)
                FocusJoinedRoom();
        };
        RoomCodeBox.TextChanged += (_, _) =>
        {
            var value = RoomCodeBox.Text.Trim();
            var formatted = GameInvite.RoomCode(value, App.CoordinatorBaseUri)
                ?? (value.Length <= 6 ? string.Concat(value.Where(char.IsAsciiLetterOrDigit)).ToUpperInvariant() : value);
            if (formatted != RoomCodeBox.Text)
            {
                RoomCodeBox.Text = formatted;
                RoomCodeBox.SelectionStart = formatted.Length;
                return;
            }
            if (RoomCodeBox.Text != _joinErrorInput)
                JoinFeedback.Visibility = Visibility.Collapsed;
            UpdateJoinControls();
        };
        RoomCodeBox.SelectionChanged += (_, _) => UpdateRoomCodePresentation();
        RoomCodeBox.GotFocus += (_, _) => UpdateRoomCodePresentation();
        RoomCodeBox.LostFocus += (_, _) => UpdateRoomCodePresentation();
    }
    private void UpdateJoinControls()
    {
        var joining = _session.Joining;
        var available = _session.GameSession is null && !_session.Capturing && _captureState == CaptureState.Idle;
        JoinLauncher.IsEnabled = joining || available;
        JoinLauncher.Content = joining ? "Joining…" : "Join room";
        RoomCodeBox.IsEnabled = available && !joining;
        PasteInviteButton.IsEnabled = available && !joining;
        JoinRoomButton.IsEnabled = available && !joining && GameInvite.RoomCode(RoomCodeBox.Text, App.CoordinatorBaseUri) is not null;
        JoinRoomButton.Content = joining ? "Joining…" : "Join room";
        SetVisible(joining, CancelJoinButton);
        UpdateRoomCodePresentation();
    }
    private void UpdateRoomCodePresentation()
    {
        var code = GameInvite.RoomCode(RoomCodeBox.Text, App.CoordinatorBaseUri);
        var shown = code ?? (RoomCodeBox.Text.Length <= 6 ? RoomCodeBox.Text : "");
        var ready = code is not null;
        for (var i = 0; i < _roomCodeCharacters.Length; i++)
        {
            var character = i < shown.Length ? shown[i].ToString() : "";
            _roomCodeCharacters[i].Text = character.Length > 0 && _settings.StreamerMode ? "•" : character;
            _roomCodeFocus[i].Visibility = ready || RoomCodeBox.FocusState != FocusState.Unfocused
                && i == Math.Min(RoomCodeBox.SelectionStart, shown.Length) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private async void PasteInvite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)
                || GameInvite.RoomCode(await content.GetTextAsync(), App.CoordinatorBaseUri) is not { } code)
            {
                JoinError("The clipboard doesn't contain a room code or invite link.");
                return;
            }
            RoomCodeBox.Text = code;
            RoomCodeBox.SelectionStart = code.Length;
            RoomCodeBox.Focus(FocusState.Programmatic);
        }
        catch (Exception error)
        {
            JoinError($"The clipboard couldn't be read. {error.Message}");
        }
    }
    private void ShowJoinFlyout()
    {
        if (_windowClosed || _closing)
            return;
        if (_sharingExplanation is not null || _firstLaunchFlow || _controllerSetupDialog is not null)
        {
            _openJoinWhenLoaded = true;
            return;
        }
        _openJoinWhenLoaded = !JoinLauncher.IsLoaded;
        if (!_openJoinWhenLoaded && !JoinFlyout.IsOpen && JoinLauncher.IsEnabled)
        {
            JoinLauncher.StartBringIntoView(new() { AnimationDesired = false });
            JoinFlyout.ShowAt(JoinLauncher);
        }
    }
    private void JoinError(string message)
    {
        _joinErrorInput = RoomCodeBox.Text;
        JoinFeedback.Text = message;
        JoinFeedback.Visibility = Visibility.Visible;
        if (!JoinFlyout.IsOpen && !_openJoinWhenLoaded)
            Show(message, InfoBarSeverity.Warning);
    }
    private async void Join_Click(object sender, RoutedEventArgs e) => await JoinRoomAsync(RoomCodeBox.Text);
    private async void RoomCode_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        if (!_session.Joining && JoinRoomButton.IsEnabled)
            await JoinRoomAsync(RoomCodeBox.Text);
    }
    private void CancelJoin_Click(object sender, RoutedEventArgs e)
    {
        _session.CancelJoin();
        _openJoinWhenLoaded = false;
        JoinFlyout.Hide();
    }
    internal void OpenInvite(Uri uri)
    {
        if (GameInvite.RoomCode(uri.AbsoluteUri, App.CoordinatorBaseUri) is { } code)
            _ = JoinRoomAsync(code);
        else if (uri.AbsoluteUri is "vrcoplay://open" or "vrcoplay://open/" && !_session.Capturing && _session.GameSession is null)
            ShowJoinFlyout();
    }
    internal async Task JoinRoomAsync(string invite)
    {
        var joined = await _session.JoinRoomAsync(invite);
        if (!joined)
            return;
        _openJoinWhenLoaded = false;
        _focusJoinedRoom = true;
        if (JoinFlyout.IsOpen)
            JoinFlyout.Hide();
        else
            FocusJoinedRoom();
        Show("Joined the game session. Choose Play to connect your game and controller.", InfoBarSeverity.Success);
    }
    private void FocusJoinedRoom()
    {
        if (_sharingExplanation is not null || _firstLaunchFlow || _controllerSetupDialog is not null)
            return;
        _focusJoinedRoom = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_windowClosed && _session.GameSession?.Session is not null)
                PlayButton.Focus(FocusState.Programmatic);
        });
    }
}
