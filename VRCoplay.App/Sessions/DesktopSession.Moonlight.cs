// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using CliWrap;
namespace VRCoplay;
internal sealed partial class DesktopSession
{
    private CommandTask<CommandResult>? _moonlight;
    private RemotePlayProfile? _remotePlayProfile;
    private bool _remotePlayApproved;
    private bool AutomaticMoonlight => GameSession?.AutomaticRemotePlay == true && readSettings().MoonlightHost.Length == 0;
    internal bool MoonlightStarted => _moonlight is not null;
    private async Task<CaptureWindow> OpenMoonlightAsync(StreamSettings settings, CancellationToken stop, Action started)
    {
        if (string.IsNullOrWhiteSpace(settings.MoonlightHost) || string.IsNullOrWhiteSpace(settings.MoonlightApp))
            throw new InvalidOperationException(
                "Set the host address and Sunshine application under Settings, Game session."
            );
        var progress = new Progress<string>(message =>
        {
            if (!stop.IsCancellationRequested && _moonlight is null)
                Show(message, DesktopNotice.Informational);
        });
        var client = await MoonlightSetup.Default.EnsureAsync(progress, stop);
        var host = settings.MoonlightHost;
        var app = settings.MoonlightApp;
        if (AutomaticMoonlight && GameSession is { } room)
        {
            Show("Waiting for the host to approve remote play…", DesktopNotice.Informational);
            var identity = await room.PrepareRemotePlayAsync(stop);
            stop.ThrowIfCancellationRequested();
            _remotePlayProfile = await RemotePlayProfile.CreateAsync(identity, host, stop);
            client = client with { WorkingDirectory = _remotePlayProfile.DirectoryPath };
            host = host.Contains(':') ? $"[{host}]:{identity.Port}" : $"{host}:{identity.Port}";
            app = SunshineHost.Application;
        }
        stop.ThrowIfCancellationRequested();
        var existingWindows = CaptureWindow.Enumerate("Moonlight").Select(x => x.Hwnd).ToHashSet();
        _moonlight = Cli.Wrap(client.Executable)
            .WithWorkingDirectory(client.WorkingDirectory)
            .WithArguments(args =>
                args.Add(["stream", host, app])
                    .Add("--display-mode windowed --no-quit-after --no-background-gamepad --audio-on-host --no-game-optimization", escape: false)
            )
            .WithValidation(CommandResultValidation.None)
            .ExecuteOwnedAsync(stop);
        started();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (_moonlight.Task.IsCompleted)
                {
                    await _moonlight.Task;
                    throw new InvalidOperationException("Moonlight closed before connecting. Check the host’s connection, then try Play again.");
                }
                if (
                    CaptureWindow
                        .Enumerate("Moonlight")
                        .FirstOrDefault(x =>
                            (x.Pid == _moonlight.ProcessId || !existingWindows.Contains(x.Hwnd))
                            && x.Title.EndsWith(" - Moonlight", StringComparison.Ordinal)
                        ) is
                    { } window
                )
                    return window;
                await Task.Delay(250, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Moonlight did not connect. Check that the host is online and the network allows remote play, then try Play again."
            );
        }
    }
}
