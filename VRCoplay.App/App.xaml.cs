// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
global using static Vanara.PInvoke.User32;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using ProtocolActivatedEventArgs = Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs;
namespace VRCoplay;
public partial class App : Application
{
    private MainWindow? _window;
    private static readonly ConcurrentQueue<Uri?> Pending = new();
    private static DispatcherQueue? _queue;
    private static App? _app;
    private string? _lastNotificationId;
    internal static WindowsNotifications Notifications { get; private set; } = null!;
    internal static Uri CoordinatorBaseUri => StreamLink.Server;
    public App() => InitializeComponent();
    [STAThread]
    private static void Main(string[] args)
    {
        if (AudioRecoveryWatchdog.RunIfRequested(args))
            return;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var current = AppInstance.GetCurrent();
        var activation = current.GetActivatedEventArgs();
        Pending.Enqueue(ActivationUri(activation));
        current.Activated += (_, incoming) =>
        {
            Pending.Enqueue(ActivationUri(incoming));
            _queue?.TryEnqueue(() => _app?.ActivatePending());
        };
        var main = AppInstance.FindOrRegisterForKey("VRCoplay");
        if (!main.IsCurrent)
        {
            RedirectActivation(main, activation);
            return;
        }
        if (!Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess().IsPackaged)
            ActivationRegistrationManager.RegisterForProtocolActivation("vrcoplay", "", "VRCoplay", "");
        using var notifications = Notifications = new WindowsNotifications();
        Application.Start(_ =>
        {
            _queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(_queue));
            _app = new App();
        });
        try { QuietApplicationAudio.RestoreOnExit(); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = Notifications.ClearAsync();
        _window ??= new MainWindow();
        ActivatePending();
    }
    private static Uri? ActivationUri(AppActivationArguments activation) =>
        activation.Data is ProtocolActivatedEventArgs args ? args.Uri : null;
    private static void RedirectActivation(AppInstance main, AppActivationArguments activation)
    {
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        var redirect = Task.Run(async () =>
        {
            try { await main.RedirectActivationToAsync(activation); }
            finally { completed.Set(); }
        });
        Marshal.ThrowExceptionForHR(CoWaitForMultipleObjects(0, uint.MaxValue, 1,
            [completed.SafeWaitHandle.DangerousGetHandle()], out _));
        redirect.GetAwaiter().GetResult();
    }
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint count,
        [In] nint[] handles, out uint index);
    private void ActivatePending()
    {
        if (_window is null || Pending.IsEmpty)
            return;
        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        _window.Activate();
        while (Pending.TryDequeue(out var uri))
        {
            if (uri is null) continue;
            if (NotificationActivation.IsNotificationUri(uri))
            {
                if (NotificationActivation.From(uri) is { } notification && notification.Id != _lastNotificationId)
                {
                    _lastNotificationId = notification.Id;
                    _window.OpenNotification(notification);
                }
            }
            else _window.OpenInvite(uri);
        }
    }
}
