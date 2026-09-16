// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _notificationTimer;
    private string? _statusNotificationKey;
    private NotificationActivation? _notificationNavigation;
    private void InitializeNotifications()
    {
        _session.Notifications.Raised += notice =>
        {
            if (_closing) return;
            Show(notice.Message, ToInfoBar(notice.Severity));
            _statusNotificationKey = notice.Key;
            if (notice.Actions.FirstOrDefault() is { } primary)
            {
                var button = new Button { Content = primary.Text };
                button.Click += (_, _) => OpenNotification(new(Guid.NewGuid().ToString("N"), notice.Target,
                    notice.RoomId, notice.ParticipantId, primary.Command, notice.Key));
                StatusBar.ActionButton = button;
            }
            _ = App.Notifications.ShowAsync(notice,
                () => !_closing && _session.Notifications.IsCurrent(notice));
        };
        _session.Notifications.Cleared += key =>
        {
            _ = App.Notifications.RemoveAsync(key);
            if (_statusNotificationKey == key)
            {
                StatusBar.IsOpen = false;
                StatusBar.ActionButton = null;
                _statusNotificationKey = null;
            }
        };
        _notificationTimer = DispatcherQueue.CreateTimer();
        _notificationTimer.Interval = TimeSpan.FromSeconds(1);
        _notificationTimer.Tick += (_, _) => _session.Notifications.Tick();
        _notificationTimer.Start();
        RootPage.Loaded += (_, _) =>
        {
            if (_notificationNavigation is not { } navigation) return;
            _notificationNavigation = null;
            OpenNotification(navigation);
        };
    }
    internal void OpenNotification(NotificationActivation activation)
    {
        if (_closing) return;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        if (!RootPage.IsLoaded)
        {
            _notificationNavigation = activation;
            return;
        }
        if (activation.RoomId is { } room && room != _session.Notifications.RoomId)
        {
            Show("This notification belongs to a game session that has ended.", InfoBarSeverity.Informational);
            return;
        }
        if (activation.Command != NotificationCommand.Open)
        {
            _ = ExecuteNotificationActionAsync(activation);
            return;
        }
        if (activation.Target == NotificationTarget.Requests)
        {
            if (!IsPendingControllerRequest(activation))
            {
                Show("This controller request is no longer pending.", InfoBarSeverity.Informational);
                return;
            }
            ParticipantsButton.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            ParticipantsButton.Focus(FocusState.Programmatic);
            ParticipantsButton.Flyout.ShowAt(ParticipantsButton);
            if (activation.ParticipantId is { } id) RoomPeople.ScrollTo(id);
        }
        else if (activation.Target == NotificationTarget.Controllers)
        {
            if (RequestControllerButton.Visibility == Visibility.Visible)
            {
                RequestControllerButton.StartBringIntoView();
                RequestControllerButton.Focus(FocusState.Programmatic);
            }
            else
            {
                StreamSettingsExpander.IsExpanded = true;
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (_closing) return;
                    ControllerCard.StartBringIntoView();
                    ControllerToggle.Focus(FocusState.Programmatic);
                });
            }
        }
        else
        {
            StreamControls.StartBringIntoView();
            if (StartButton.Visibility == Visibility.Visible) StartButton.Focus(FocusState.Programmatic);
            else if (PlayButton.Visibility == Visibility.Visible) PlayButton.Focus(FocusState.Programmatic);
        }
    }
    private async Task ExecuteNotificationActionAsync(NotificationActivation activation)
    {
        if (!_session.Notifications.IsCurrentAction(activation))
        {
            Show("This notification is no longer current.", InfoBarSeverity.Informational);
            return;
        }
        switch (activation.Command)
        {
            case NotificationCommand.ApproveController:
            case NotificationCommand.DeclineController:
                if (!IsPendingControllerRequest(activation))
                {
                    Show("This controller request is no longer pending.", InfoBarSeverity.Informational);
                    return;
                }
                var action = activation.Command == NotificationCommand.ApproveController
                    ? PlayerAction.Approve
                    : PlayerAction.Decline;
                var error = ActOnPlayer(activation.ParticipantId!, action);
                if (error is not null)
                    Show(error, InfoBarSeverity.Warning);
                else if (action == PlayerAction.Decline ||
                    _session.GameSession?.Session?.Participants.FirstOrDefault(x => x.Id == activation.ParticipantId)
                        is not { RemotePlayRequested: true })
                    Show(action == PlayerAction.Approve ? "Controller request approved." : "Controller request declined.",
                        action == PlayerAction.Approve ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
                break;
            case NotificationCommand.RequestController:
                await RequestControlAsync();
                break;
            case NotificationCommand.RestartCapture:
                if (_captureState == CaptureState.Idle)
                    StartCapture("notification-retry");
                else
                    Show("Sharing or playing is already active.", InfoBarSeverity.Informational);
                break;
            case NotificationCommand.RetryControllers:
                if (_session.CanRetryPointer)
                    await _session.RetryPointerAsync();
                else
                    Show("Controllers cannot be retried in the current session.", InfoBarSeverity.Informational);
                break;
            case NotificationCommand.RetryTouch:
                if (PointerTouchCard.IsEnabled)
                    PointerTouchToggle.IsOn = true;
                else
                    Show("Touch controls are unavailable in the current session.", InfoBarSeverity.Informational);
                break;
            case NotificationCommand.ChooseCaptureSource:
                WindowPicker.StartBringIntoView();
                WindowPicker.Focus(FocusState.Programmatic);
                WindowPicker.IsDropDownOpen = true;
                break;
        }
    }
    private bool IsPendingControllerRequest(NotificationActivation activation) =>
        _session.IsHost && _session.Notifications.IsPendingRequest(activation.RoomId, activation.ParticipantId);
}
