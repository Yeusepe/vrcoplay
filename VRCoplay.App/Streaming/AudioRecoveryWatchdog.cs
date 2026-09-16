// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using CliWrap;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
namespace VRCoplay;
internal static class AudioRecoveryWatchdog
{
    private const string Mode = "--recover-audio-on-exit";
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly Dictionary<string, CommandTask<CommandResult>> Watchers = new(StringComparer.OrdinalIgnoreCase);
    internal static async Task EnsureStartedAsync(string path, CancellationToken stop)
    {
        await Gate.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            if (Watchers.TryGetValue(path, out var existing) && !existing.Task.IsCompleted)
                return;
            var pipeName = "VRCoplay.AudioRecovery." + Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
            );
            using var current = Process.GetCurrentProcess();
            var executable = Environment.ProcessPath!;
            var watcher = Cli.Wrap(executable)
                .WithArguments(args =>
                {
                    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                        args.Add(Assembly.GetEntryAssembly()!.Location);
                    args.Add(Mode).Add(current.Id).Add(current.StartTime.ToUniversalTime().Ticks).Add([path, pipeName]);
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            var ready = new byte[1];
            await pipe.ReadExactlyAsync(ready, timeout.Token).ConfigureAwait(false);
            if (ready[0] != 1)
                throw new InvalidOperationException("Audio recovery did not become ready.");
            Watchers[path] = watcher;
        }
        finally
        {
            Gate.Release();
        }
    }
    internal static bool RunIfRequested(string[] args)
    {
        if (args.Length != 5 || args[0] != Mode)
            return false;
        if (!int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var started))
            return true;
        try
        {
            using var parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks != started)
                return true;
            try
            {
                using var pipe = new NamedPipeClientStream(".", args[4], PipeDirection.Out);
                pipe.Connect(10000);
                pipe.WriteByte(1);
                pipe.Flush();
            }
            catch (IOException) when (parent.HasExited) { }
            parent.WaitForExit();
        }
        catch (ArgumentException)
        {
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            return true;
        }
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var recovery = new AudioVolumeRecovery(args[3]);
                recovery.Recover();
                if (!recovery.HasPending)
                    break;
            }
            catch (Exception error)
            {
                Debug.WriteLine(error);
            }
            Thread.Sleep(500);
        }
        return true;
    }
}
