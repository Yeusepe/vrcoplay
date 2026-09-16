// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Globalization.NumberFormatting;
using WinRT.Interop;
using Windows.UI.ViewManagement;
namespace VRCoplay;
public sealed partial class MainWindow : Window
{
    private static string Url => LocalVideo.PlayerUrl;
    private string AppDetails
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
            var developmentBuild = true;
            if (Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess().IsPackaged)
            {
                var installed = Windows.ApplicationModel.Package.Current.Id.Version;
                version = new Version(installed.Major, installed.Minor, installed.Build, installed.Revision);
                developmentBuild = false;
            }
            return $"VRCoplay alpha · Version {version}" + (developmentBuild ? " · Development build" : "");
        }
    }
    private readonly DesktopSession _session;
    private bool _windowClosed;
    private bool _windowActive;
    private readonly DispatcherQueueTimer _sharingTipTimer;
    private bool _closing;
    private bool _allowClose;
    private readonly UISettings _uiSettings = new();
    private LayoutMotion? _layoutMotion;
    private SettingsExpanderMotion[] _settingsMotion = [];
    public MainWindow()
    {
        _session = new(App.CoordinatorBaseUri, action => DispatcherQueue.TryEnqueue(() => action()),
            ReadSessionSettings, () => _uiSettings.AnimationsEnabled);
        InitializeComponent();
        InitializeSession();
        InitializeNotifications();
        InitializeMedia();
        InitializeJoin();
        InitializePreview();
        _sharingTipTimer = DispatcherQueue.CreateTimer();
        _sharingTipTimer.Interval = TimeSpan.FromSeconds(6);
        _sharingTipTimer.IsRepeating = false;
        _sharingTipTimer.Tick += (_, _) => DismissSharingTip();
        SharingTip.Closed += (_, _) =>
        {
            if (!SharingTip.IsOpen)
                _sharingTipTimer.Stop();
        };
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "VRCoplay.ico"));
        _session.WatchLink = Url;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.ResizeClient(
            new((int)Math.Min(1180 * scale, work.Width * .9), (int)Math.Min(780 * scale, work.Height * .9))
        );
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(480 * scale);
            presenter.PreferredMinimumHeight = (int)(480 * scale);
        }
        foreach (
            var box in (NumberBox[])
                [FpsBox, WidthBox, HeightBox, CropLeftBox, CropTopBox, CropRightBox, CropBottomBox, QualityBox, InviteDurationBox]
        )
            box.NumberFormatter = new DecimalFormatter
            {
                FractionDigits = 0,
                NumberRounder = new IncrementNumberRounder { Increment = 1 },
            };
        LoadSettings();
        InitializePeople();
        InitializeControllerSetup();
        RootPage.Loaded += ShowWelcomeOnFirstLaunch;
        RootPage.Loaded += async (_, _) =>
        {
            _layoutMotion ??= new LayoutMotion(RootPage, StreamControls.Children.Concat(SessionChoices.Children)
                .Concat(PageSections.Children.Where(element => element != AppFooter)), _uiSettings);
            if (_settingsMotion.Length == 0)
                _settingsMotion = SettingsExpanderMotion.Attach(SettingsPanel, _uiSettings).ToArray();
            try { await _session.AudioRecovery; }
            catch (Exception error)
            {
                Show($"Saved application volumes could not be restored. Check Windows Volume mixer. {error.GetBaseException().Message}", InfoBarSeverity.Warning);
            }
        };
        RootPage.Loaded += async (_, _) => await CheckForUpdatesOnLaunchAsync();
        _uiSettings.AnimationsEnabledChanged += AnimationsChanged;
        _loadingSettings = false;
        DesktopSession.PrepareNetwork(_settings);
        RefreshWindows();
        UpdateFlow();
        PreviewHost.Loaded += (_, _) =>
        {
            ResizePreview();
            UpdatePreview();
        };
        PreviewHost.SizeChanged += (_, _) => ResizePreview();
        RootPage.SizeChanged += (_, _) =>
        {
            DismissSharingTip();
            ResizePreview();
        };
        Scroller.ViewChanged += (_, _) =>
        {
            DismissSharingTip();
            UpdatePreview();
        };
        var dismissSharingTip = new PointerEventHandler((_, _) => DismissSharingTip());
        RootPage.AddHandler(UIElement.PointerPressedEvent, dismissSharingTip, true);
        RootPage.AddHandler(UIElement.PointerWheelChangedEvent, dismissSharingTip, true);
        RootPage.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape)
                DismissSharingTip();
        }), true);
        Activated += (_, args) =>
        {
            _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
            if (!_windowActive)
                DismissSharingTip();
            else
            {
                _previewFailedSource = null;
                _ = App.Notifications.ClearAsync();
            }
            UpdatePreview();
        };
        AppWindow.Closing += async (_, args) =>
        {
            if (_allowClose)
                return;
            args.Cancel = true;
            if (_closing)
                return;
            _closing = true;
            _controllerCheckTimer?.Stop();
            _controllerSetupDialog?.Hide();
            _updateDialog?.Hide();
            _notificationTimer?.Stop();
            _session.Notifications.Stop();
            PointerTouchToggle.IsOn = false;
            RootPage.IsEnabled = false;
            _session.CancelJoin();
            _openJoinWhenLoaded = false;
            JoinFlyout.Hide();
            DismissSharingTip();
            SaveSettings(ReadSettings());
            try
            {
                await App.Notifications.ClearAsync();
                ClosePreview();
                await _previewCleanup;
                await StopCaptureAsync("window-close");
                await _session.CloseGameSessionAsync();
            }
            catch (Exception error) { Debug.WriteLine(error); }
            finally
            {
                _allowClose = true;
                DispatcherQueue.TryEnqueue(() => Close());
            }
        };
        Closed += (_, _) =>
        {
            _windowClosed = true;
            _uiSettings.AnimationsEnabledChanged -= AnimationsChanged;
            _layoutMotion?.Dispose();
            foreach (var motion in _settingsMotion)
                motion.Dispose();
            ClosePreview();
        };
    }
    private void AnimationsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_windowClosed)
                return;
            _layoutMotion?.ApplySettings();
            foreach (var motion in _settingsMotion)
                motion.ApplySettings();
            _session.PrepareOverlays(ReadSettings());
        });
    private void Flow_Changed(object sender, object e)
    {
        DismissSharingTip();
        UpdateFlow();
        if (!_loadingSettings)
            DesktopSession.PrepareNetwork(_settings);
    }
    private void UseWindow_Click(object sender, RoutedEventArgs e)
    {
        PlayLocationPicker.SelectedIndex = 1;
        _choosingPlay = true;
        UpdateFlow();
        WindowPicker.Focus(FocusState.Programmatic);
    }
    private static void SetVisible(bool show, params UIElement[] controls)
    {
        foreach (var control in controls)
            control.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateFlow()
    {
        if (_loadingSettings)
            return;
        _updateDialog?.SetSessionActive(UpdateSessionActive);
        var game = _session.GameSession?.Session is not null;
        var guest = game && !_session.IsHost;
        var idle = _captureState == CaptureState.Idle;
        var busy = _captureState is CaptureState.Starting or CaptureState.Stopping;
        var connecting = _session.GameSession is { Session: null };
        SettingsSheet.IsEnabled = idle && !connecting;
        ActivityPicker.IsEnabled = idle && !game && !connecting;
        StartButton.IsEnabled = (!connecting || !idle) && _captureState != CaptureState.Stopping;
        CreateGameRoomButton.IsEnabled = !connecting && !busy;
        LeaveGameRoomItem.IsEnabled = !busy;
        PlayButton.IsEnabled = !busy;
        UpdateOverlayControls(!idle);
        SetVisible(!guest && AudiencePicker.SelectedIndex == 1, DirectNetworkSettings);
        SetVisible(guest || ActivityPicker.SelectedIndex == 1, GameSessionSettings);
        UpdateJoinControls();
        var sharing = !guest;
        var playing = guest && _captureState is CaptureState.Running or CaptureState.Stopping;
        var preview = sharing || playing && !_session.VideoPlayerOnly || guest && _choosingPlay && SelectedPlayLocation == PlayLocation.MyPc;
        SetVisible(sharing, ActivityPicker, AudienceSettings);
        SetVisible(sharing && ActivityPicker.SelectedIndex == 1, GameModePicker);
        SetVisible(preview, PreviewPanel);
        SetVisible(guest && !playing && !_choosingPlay && !busy, UseWindowButton);
        SetVisible(guest && _choosingPlay, PlaySetup);
        PlayHint.Text = SelectedPlayLocation switch
        {
            PlayLocation.HostPc => "Connects automatically after the host approves.",
            PlayLocation.MyPc => "Use the game's multiplayer or Netplay. Your controller stays on this PC.",
            _ => "Use your controllers while watching the Video Player.",
        };
        SetVisible(SelectedPlayLocation == PlayLocation.HostPc && _session.GameSession?.AutomaticRemotePlay != true, MoonlightHelp);
        SetVisible(guest, PlayActions);
        SetVisible(!playing, PlayButton);
        PlayButton.Content = _choosingPlay ? "Start playing" : "Play";
        SetVisible(playing || _choosingPlay, WatchButton);
        WatchButton.Content = playing ? "Stop playing" : "Cancel";
        Grid.SetColumn(WatchButton, playing ? 0 : 1);
        Grid.SetColumnSpan(WatchButton, playing ? 2 : 1);
        Grid.SetColumnSpan(PlayButton, _choosingPlay ? 1 : 2);
        PlayLocationPicker.IsEnabled = !busy && _session.GameSession?.Session?.Mode != SessionMode.LocalGame;
        SetVisible(sharing, StartButton);
        StartButton.Content = !_session.Capturing ? "Start sharing" : "Stop sharing";
        SetVisible(_captureState == CaptureState.Running &&
            (!_session.VideoPlayerOnly || !string.IsNullOrWhiteSpace(_session.GameSession?.Session?.Settings.WatchLink)), StreamUrlRow);
        if (StreamUrlRow.Visibility != Visibility.Visible)
            DismissSharingTip();
        UpdateStreamLink();
        SetVisible(_session.DirectShare is not null && _captureState == CaptureState.Running, RotateStreamLinkButton);
        RotateStreamLinkButton.IsEnabled = _captureState == CaptureState.Running;
        SetVisible(!game && _captureState == CaptureState.Running && ActivityPicker.SelectedIndex == 1, CreateGameRoomButton);
        SetVisible(game, RoomHeader);
        SetVisible(playing && SendsControllersToHost && _session.ActiveSettings?.Controller == true, RequestControllerButton);
        SetVisible(game, SessionBar);
        if (!game) PeopleFlyout.Hide();
        SetVisible(game && _session.IsHost, ShareButton);
        UpdateControllerAvailability();
        ResizePreview();
        if (preview)
            UpdatePreview();
        else
            ClosePreview();
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        DismissSharingTip();
        SettingsActionsButton.Focus(FocusState.Programmatic);
        SettingsPanel.StartBringIntoView(
            new()
            {
                VerticalAlignmentRatio = 0,
                AnimationDesired = _uiSettings.AnimationsEnabled,
            }
        );
    }
    private void BackToStream_Click(object sender, RoutedEventArgs e)
    {
        DismissSharingTip();
        SettingsNavigationButton.Focus(FocusState.Programmatic);
        Scroller.ScrollTo(
            0,
            0,
            new(
                _uiSettings.AnimationsEnabled
                    ? ScrollingAnimationMode.Auto
                    : ScrollingAnimationMode.Disabled,
                ScrollingSnapPointsMode.Ignore
            )
        );
    }
    private void EncoderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => _session.Encoder = null;
    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        UpdateStreamLink();
        if (_streamLink.Length == 0 || !Copy(_streamLink, "Video Player link"))
            return;
        StatusBar.IsOpen = false;
        ShowSharingTip("Video Player link copied", "Paste it into a VRChat® Video Player. Keep VRCoplay and the shared window open while you watch.");
    }
    private void SharingHelp_Click(object sender, RoutedEventArgs e) => ShowSharingTip(
        "Your video comes from your PC",
        "The Video Player link connects viewers to your computer. VRCoplay’s servers never receive or store your video. Viewers can discover your IP address, so share with people you trust and avoid public worlds.",
        autoDismiss: false);
    private void ShowSharingTip(string title, string text, bool autoDismiss = true)
    {
        if (_windowClosed || !_windowActive)
            return;
        _sharingTipTimer.Stop();
        SharingTip.Title = title;
        SharingTip.Subtitle = text;
        SharingTip.IsOpen = true;
        if (autoDismiss)
            _sharingTipTimer.Start();
    }
    private void DismissSharingTip()
    {
        _sharingTipTimer?.Stop();
        if (SharingTip is not null)
            SharingTip.IsOpen = false;
    }
    private void CopyShare_Click(object sender, RoutedEventArgs e) => Copy(_session.GameSession?.ShareUrl ?? "", "Invite link");
    private void CopyRoomCode_Click(object sender, RoutedEventArgs e) => Copy(_session.GameSession?.Session?.Code ?? "", "Room code");
    private void CopyPin_Click(object sender, RoutedEventArgs e) =>
        Copy(_session.GameSession?.Pin ?? "", "Controller PIN");
    private bool Copy(string value, string label)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(value);
            var copied = Clipboard.SetContentWithOptions(data, new());
            Show(
                copied ? $"{label} copied." : "Clipboard is busy; try again.",
                copied ? InfoBarSeverity.Success : InfoBarSeverity.Warning
            );
            return copied;
        }
        catch (Exception ex)
        {
            Show($"Could not copy: {ex.Message}", InfoBarSeverity.Warning);
            return false;
        }
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var s = new StreamSettings();
        _loadingSettings = true;
        _settings = s;
        RootPage.DataContext = _settings;
        Bindings.Update();
        _loadingSettings = false;
        UpdatePrivacy();
        UpdateFlow();
        if (SaveSettings(s))
            Show("Settings reset to automatic.", InfoBarSeverity.Success);
    }
    private ProfileCenterDialog? _profileCenter;
    private async void BrowseProfiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShowDialogAsync(_profileCenter ??= new ProfileCenterDialog());
        }
        catch (Exception error)
        {
            Show($"Could not open profiles: {error.Message}", InfoBarSeverity.Error);
        }
    }
    private void ResetSessionUi()
    {
        PointerTouchToggle.IsOn = false;
        _choosingPlay = false;
        if (_session.GameSession?.Session is null)
            RoomCodeText.Text = "";
        AddressStatus.Text = "";
        AddressProgress.IsActive = false;
        SetVisible(false, AddressProgress, AddressStateIcon);
        DismissSharingTip();
        PointerToggle_Toggled(PointerToggle, new RoutedEventArgs());
        UpdateFlow();
    }
    private void Show(string message, InfoBarSeverity severity)
    {
        _statusNotificationKey = null;
        StatusBar.ActionButton = null;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
