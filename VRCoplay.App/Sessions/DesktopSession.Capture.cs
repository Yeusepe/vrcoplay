// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using static System.Threading.Tasks.ConfigureAwaitOptions;
namespace VRCoplay;
internal sealed partial class DesktopSession
{
    internal CaptureState State =>
        _cts is null ? CaptureState.Idle
        : _cts.IsCancellationRequested ? CaptureState.Stopping
        : ActiveSettings is null ? CaptureState.Starting
        : CaptureState.Running;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _diagnosticSession = "";
    internal void StartCapture(
        Func<CaptureRequest> request,
        Func<Task<bool>> confirmSharing,
        Func<Task<bool>> requestControl,
        string reason = "capture-api"
    )
    {
        if (State != CaptureState.Idle)
            return;
        Notifications.BeginCapture();
        _diagnosticSession = Guid.NewGuid().ToString("N");
        SharingDiagnosticLog.Default.Write(_diagnosticSession, "start-requested", reason: reason);
        _cts = new();
        _loop = CaptureAsync(request, confirmSharing, requestControl, _cts.Token);
        UpdateFlow();
    }
    internal async Task StopCaptureAsync(string reason = "capture-api")
    {
        if (_loop is not { } loop)
            return;
        SharingDiagnosticLog.Default.Write(_diagnosticSession, "stop-requested", reason: reason);
        if (!IsHost) Notifications.ReleasingController();
        _cts!.Cancel();
        UpdateFlow();
        await loop;
    }
    private async Task CaptureAsync(
        Func<CaptureRequest> request,
        Func<Task<bool>> confirmSharing,
        Func<Task<bool>> requestControl,
        CancellationToken stop
    )
    {
        await Task.Yield();
        var guest = GameSession?.Session is not null && !IsHost;
        var message = guest ? "Stopped playing. You are still in the game session." : "Stopped.";
        var severity = DesktopNotice.Informational;
        var latency = Task.CompletedTask;
        var roomSetup = Task.CompletedTask;
        var addressCheck = Task.CompletedTask;
        var questStreaming = Task.CompletedTask;
        var diagnosticSession = _diagnosticSession;
        var terminalReason = "completed";
        var notifyFailure = false;
        try
        {
            var input = request();
            var preferences = input.Preferences;
            var room = GameSession?.Session;
            ActivePlayLocation = guest ? input.Location : null;
            var mode = guest
                ? input.Location == PlayLocation.MyPc ? SessionMode.LocalGame : SessionMode.HostGame
                : room?.Mode ?? preferences.Mode;
            var settings = preferences with
            {
                Games = mode != SessionMode.ScreenSharing,
                GameMode = mode == SessionMode.LocalGame ? 1 : 0,
                MoonlightHost =
                    preferences.MoonlightHost.Length > 0 ? preferences.MoonlightHost : GameSession?.GameHost ?? "",
                MoonlightApp =
                    preferences.MoonlightApp.Length > 0 ? preferences.MoonlightApp : room?.Settings.MoonlightApp ?? "",
            };
            if (guest)
            {
                Notifications.ReleasingController();
                GameSession!.SetPlayLocation(mode);
            }
            if (VideoPlayerOnly)
            {
                Target = null;
                EnableTouch(false);
                if (settings.Controller && !await requestControl())
                {
                    terminalReason = "control-declined";
                    return;
                }
                stop.ThrowIfCancellationRequested();
                WatchLink = room!.Settings.WatchLink ?? LocalVideo.PlayerUrl;
                StartControllers(settings, true);
                _latencyMode = null;
                PrepareOverlays(settings);
                GameSession?.SetPlaybackLatency(WatchLink, null);
                latency = PlayerLatency.WatchAsync(mode => dispatch(() => PlayerLatencyChanged(mode, stop)), stop,
                    () => WatchLink, () => "", () => dispatch(() => VideoStarted(stop)),
                    (link, mode) => dispatch(() =>
                    {
                        if (!stop.IsCancellationRequested && VideoPlayerOnly && WatchLink == link)
                            GameSession?.SetPlaybackLatency(link, mode);
                    }));
                ActiveSettings = settings;
                CaptureReady?.Invoke(preferences);
                UpdateFlow();
                SharingDiagnosticLog.Default.Write(diagnosticSession, "controllers-only-started");
                await Task.Delay(Timeout.Infinite, stop);
                return;
            }
            if (guest && mode == SessionMode.HostGame && (settings.Controller || AutomaticMoonlight) && !await requestControl())
            {
                terminalReason = "control-declined";
                return;
            }
            var target =
                guest && mode == SessionMode.HostGame
                    ? await OpenMoonlightAsync(
                        settings,
                        stop,
                        () =>
                            Show(
                                "Connecting to the host…",
                                DesktopNotice.Informational
                            )
                    )
                    : input.Source ?? throw new InvalidOperationException("Choose a window or display first.");
            settings.Validate(target);
            if (!guest)
            {
                if (!await SettingsStore.ConfirmSharingAsync(confirmSharing))
                {
                    terminalReason = "sharing-declined";
                    return;
                }
            }
            stop.ThrowIfCancellationRequested();
            if (LocalPorts.InUse(8554))
                throw new InvalidOperationException("Port 8554 is in use. Stop the other local stream first.");
            WatchLink = guest ? room!.Settings.WatchLink ?? LocalVideo.PlayerUrl
                : readSettings().UseDirectIp ? LocalVideo.PublishUrl : LocalVideo.PlayerUrl;
            if (!guest && settings.Audience == 1)
            {
                DirectShare = new(coordinator, quest: settings.QuestStreaming, useDirectIp: readSettings().UseDirectIp);
                WatchLink = await DirectShare.PrepareAsync(stop, settings.DirectAddress, settings.DirectPort);
            }
            if (!guest && settings.Games && GameSession is null)
                roomSetup = CreateGameRoomAsync(settings);
            Show("Checking the encoder and starting the local server...", DesktopNotice.Informational);
            Encoder ??= await PickEncoderAsync(settings.Encoder);
            stop.ThrowIfCancellationRequested();
            Target = target;
            if (guest)
                TargetChosen?.Invoke(target);
            await StartServerAsync(DirectShare, stop);
            if (DirectShare is not null && settings.QuestStreaming)
            {
                var quest = QuestShare = new DirectQuestStream(DirectShare.PublishUrl, settings.Audio);
                quest.Changed += () => dispatch(() =>
                {
                    if (QuestShare == quest && !stop.IsCancellationRequested)
                        QuestStreamChanged?.Invoke();
                });
                questStreaming = Task.Run(() => quest.RunAsync(stop));
                QuestStreamChanged?.Invoke();
            }
            if (DirectShare?.Link is { } link)
                WatchLink = link;
            if (settings.Games)
            {
                StartLocalRoute(WatchLink, "game");
                PublishWatchLink();
            }
            StartControllers(settings, guest);
            _latencyMode = null;
            PrepareOverlays(settings);
            if (settings.World == 0 || settings.Games)
                latency = PlayerLatency.WatchAsync(mode => dispatch(() => PlayerLatencyChanged(mode, stop)), stop,
                    () => WatchLink, () => settings.Games ? "game" : DirectShare?.Path ?? "game", () => dispatch(() => VideoStarted(stop)));
            stop.ThrowIfCancellationRequested();
            settings = settings with { UseDirectIp = DirectShare?.UseDirectIp ?? readSettings().UseDirectIp };
            ActiveSettings = settings;
            CaptureReady?.Invoke(preferences);
            UpdateFlow();
            addressCheck = BindStreamAddressAsync(stop);
            SharingDiagnosticLog.Default.Write(diagnosticSession, "capture-started");
            await RunMediaAsync(settings, DirectShare?.PublishUrl ?? LocalVideo.PublishUrl, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            terminalReason = "canceled";
        }
        catch (Exception error)
        {
            notifyFailure = !stop.IsCancellationRequested;
            terminalReason = "failed";
            SharingDiagnosticLog.Default.Write(diagnosticSession, "capture-failed", error: error);
            message = $"{(VideoPlayerOnly || guest ? "Playing" : "Capture")} stopped: {error.GetBaseException().Message}";
            severity = DesktopNotice.Error;
        }
        finally
        {
            if (VideoPlayerOnly) GameSession?.SetPlaybackLatency(null, null);
            SharingDiagnosticLog.Default.Write(
                diagnosticSession, "cleanup-started", reason: terminalReason, canceled: stop.IsCancellationRequested
            );
            try
            {
                StopLocalRoute();
                if (!guest && GameSession is { IsHost: true } hostedRoom)
                    await hostedRoom.StopRemoteHostingAsync();
                await StreamProcess.StopAsync(_cts!, [roomSetup, addressCheck, latency, questStreaming]);
                if (guest || !IsHost || GameSession?.Session is null)
                    StopInput();
                await Task.WhenAll(_server?.Task ?? Task.CompletedTask, _moonlight?.Task ?? Task.CompletedTask)
                    .ConfigureAwait(ContinueOnCapturedContext | SuppressThrowing);
                if (DirectShare is not null)
                    await DirectShare.DisposeAsync();
                if (guest && GameSession is { } gameSession)
                    try
                    {
                        Notifications.ReleasingController();
                        gameSession.ReleaseController();
                    }
                    catch { }
                _server = null;
                _moonlight = null;
                _remotePlayProfile?.Dispose();
                _remotePlayProfile = null;
                _remotePlayApproved = false;
                DirectShare = null;
                QuestShare = null;
                QuestStreamChanged?.Invoke();
                if (!guest)
                    PublishWatchLink();
                NetworkNotice = null;
                ActiveSettings = null;
                ActivePlayLocation = null;
                _loop = null;
                _cts!.Dispose();
                _cts = null;
                ResetSession();
                Notifications.CaptureStopped();
                Show(message, severity);
                if (notifyFailure)
                    Notifications.CaptureFailed(guest);
                SharingDiagnosticLog.Default.Write(diagnosticSession, "capture-ended", reason: terminalReason);
            }
            catch (Exception error)
            {
                SharingDiagnosticLog.Default.Write(diagnosticSession, "cleanup-failed", error: error);
                throw;
            }
        }
    }
}
