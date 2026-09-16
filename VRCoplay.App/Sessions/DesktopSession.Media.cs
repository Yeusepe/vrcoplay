// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using CliWrap;
using static Vanara.PInvoke.User32;
namespace VRCoplay;
internal sealed partial class DesktopSession
{
    private readonly string _tools = Path.Combine(AppContext.BaseDirectory, "Tools");
    private CommandTask<CommandResult>? _server;
    private CancellationTokenSource? _captureStop;
    private string _lastServerLine = "";
    private string _lastCaptureLine = "";
    internal CaptureSource? Target { get; private set; }
    internal VideoEncoder? Encoder { get; set; }
    internal event Action<bool, CancellationToken>? FallbackChanged;
    internal event Action<Exception, CancellationToken>? OverlayFailed;
    internal event Action? MediaStarted;
    internal void SelectSource(CaptureSource? next)
    {
        if (!Capturing || _server is null || next is null || IsCurrent(next))
            return;
        SharingDiagnosticLog.Default.Write(_diagnosticSession, "source-switch-requested");
        Target = next;
        Show("Switching capture source...", DesktopNotice.Informational);
        _captureStop?.Cancel();
    }
    private bool IsCurrent(CaptureSource target) => target.IsSameSource(Target);
    private async Task StartServerAsync(DirectScreenShare? share, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        var start = share?.MediaCommand(_tools) ?? LocalVideo.MediaCommand(_tools);
        _lastServerLine = "";
        _server = start
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => RememberOutput(line, ref _lastServerLine)))
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line => RememberOutput(line, ref _lastServerLine)))
            .ExecuteOwnedAsync(stop);
        SharingDiagnosticLog.Default.Write(_diagnosticSession, "server-started", childPid: _server.ProcessId);
        for (var waited = 0; !LocalPorts.InUse(8554); waited += 50)
        {
            if (_server.Task.IsCompleted || waited >= 5000)
                throw new InvalidOperationException($"MediaMTX did not start. {_lastServerLine}");
            await Task.Delay(50, stop);
        }
    }
    private async Task RunMediaAsync(StreamSettings settings, string output, CancellationToken stop)
    {
        var (width, height) = CaptureCanvas.Size(Target!, settings);
        ActiveSettings = settings with { Width = width, Height = height };
        var fps = settings.Fps ?? 60;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var stream = new StreamPublisher(_tools, width, height, fps, settings, Encoder!, output, Overlays,
            () => {
                if (!settings.Games) return default;
                var alignment = _input?.PointerAlignment ?? default;
                return alignment.Available ? (alignment.Hint, alignment.Progress) : default;
            }, () => settings.Games ? _input?.ControllerHologram : null,
            DirectShare is not null && settings.Games ? LocalVideo.PublishUrl : null);
        stream.FallbackChanged += waiting => FallbackChanged?.Invoke(waiting, lifetime.Token);
        stream.OverlayFailed += error => OverlayFailed?.Invoke(error, lifetime.Token);
        stream.Frame += frame => _localRoute?.Frame(frame);
        stream.Log += line => RememberOutput(line, ref _lastCaptureLine);
        var publisher = stream.RunAsync(lifetime.Token);
        var capture = CaptureWindowsAsync(stream, settings, lifetime.Token);
        SharingDiagnosticLog.Default.Write(_diagnosticSession, "publisher-started");
        MediaStarted?.Invoke();
        try
        {
            var ended = await Task.WhenAny(publisher, capture, _server!.Task);
            stop.ThrowIfCancellationRequested();
            await ended;
            throw new InvalidOperationException(ended == _server.Task
                ? $"MediaMTX stopped. {_lastServerLine}" : $"The media pipeline stopped. {_lastCaptureLine}");
        }
        catch (Exception error)
        {
            SharingDiagnosticLog.Default.Write(_diagnosticSession, "media-ended",
                canceled: stop.IsCancellationRequested, error: error, stderr: [_lastCaptureLine]);
            throw;
        }
        finally
        {
            await StreamProcess.StopAsync(lifetime, [publisher, capture]);
            _localRoute?.ResetFrames();
        }
    }
    private async Task CaptureWindowsAsync(
        StreamPublisher stream,
        StreamSettings settings,
        CancellationToken stop
    )
    {
        QuietApplicationAudio? quiet = null;
        int quietPid = 0;
        string? lastFailure = null;
        try
        {
            if (settings.Audio && settings.QuietLocalAudio)
                await AudioRecovery;
            while (!stop.IsCancellationRequested)
            {
                var target = Target!;
                using var iteration = _captureStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var source = stream.BeginSource();
                Task? capturing = null, audio = null;
                var startingQuietAudio = false;
                _lastCaptureLine = "";
                try
                {
                    var handle = target.CurrentHandle();
                    var window = target as CaptureWindow;
                    if (window is not null && (IsIconic(handle) || !IsWindowVisible(handle)))
                    {
                        await Task.Delay(250, iteration.Token);
                        continue;
                    }
                    if (settings.Audio && settings.QuietLocalAudio && window is not null && quietPid != window.Pid)
                    {
                        startingQuietAudio = true;
                        if (quiet is not null)
                            await quiet.DisposeAsync();
                        quiet = await QuietApplicationAudio.StartAsync(window.Pid, iteration.Token);
                        quietPid = window.Pid;
                        startingQuietAudio = false;
                    }
                    if (settings.Audio)
                        audio = ProcessAudio.CaptureAsync(
                            window?.Pid,
                            (bytes, timestamp) => stream.AddAudio(bytes, timestamp, source),
                            iteration.Token,
                            quiet
                        );
                    capturing = stream.CaptureAsync(
                        target, handle, settings, source, iteration.Token
                    );
                    var ended = await Task.WhenAny(capturing, audio ?? capturing);
                    await ended;
                }
                catch (OperationCanceledException) when (iteration.IsCancellationRequested) { }
                catch (Exception error) when (!startingQuietAudio && audio?.IsFaulted != true)
                {
                    var failure = error.GetBaseException().Message;
                    if (failure != lastFailure)
                    {
                        Trace.TraceWarning($"Capture source unavailable; standby continues: {failure}");
                        SharingDiagnosticLog.Default.Write(
                            _diagnosticSession, "window-capture-unavailable", error: error, stderr: [_lastCaptureLine]
                        );
                    }
                    lastFailure = failure;
                }
                finally
                {
                    iteration.Cancel();
                    stream.BeginSource();
                    await StreamProcess.StopAsync(iteration, [capturing, audio]);
                    _captureStop = null;
                    if (
                        quiet is not null
                        && (
                            !IsCurrent(target)
                            || target is not CaptureWindow currentWindow
                            || !IsWindow(currentWindow.Hwnd)
                            || IsIconic(currentWindow.Hwnd)
                            || audio?.IsFaulted == true
                            || stop.IsCancellationRequested
                        )
                    )
                    {
                        await quiet.DisposeAsync();
                        quiet = null;
                        quietPid = 0;
                    }
                }
                if (!stop.IsCancellationRequested)
                    await Task.Delay(250, stop);
            }
        }
        finally
        {
            if (quiet is not null)
                await quiet.DisposeAsync();
        }
    }
    private Task<VideoEncoder> PickEncoderAsync(int choice) => VideoEncoder.PickAsync(choice);
    private static void RememberOutput(string line, ref string lastLine)
    {
        if (!string.IsNullOrWhiteSpace(line) && !line.Contains(" INF ", StringComparison.Ordinal))
            lastLine = line.Trim();
    }
}
