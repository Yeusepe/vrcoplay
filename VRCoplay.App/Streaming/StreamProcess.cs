// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using CliWrap;
using Meziantou.Framework.Win32;
namespace VRCoplay;
internal static class StreamProcess
{
    private static readonly JobObject Job = CreateJob();
    private static JobObject CreateJob()
    {
        var job = new JobObject();
        try
        {
            job.SetLimits(new JobObjectLimits { Flags = JobObjectLimitFlags.KillOnJobClose });
        }
        catch
        {
            job.Dispose();
            throw;
        }
        return job;
    }
    internal static async Task StopAsync(CancellationTokenSource stop, IEnumerable<Task?> tasks)
    {
        stop.Cancel();
        await Task.WhenAll(tasks.OfType<Task>()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
    internal static CommandTask<CommandResult> ExecuteOwnedAsync(this Command command, CancellationToken stop)
    {
        var job = Job;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Exception? assignmentError = null;
        CommandTask<CommandResult> running;
        try
        {
            running = command.ExecuteAsync(null, process =>
            {
                try
                {
                    if (!process.HasExited) job.AssignProcess(process);
                }
                catch (Win32Exception) when (process.HasExited) { }
                catch (Exception error)
                {
                    assignmentError = error;
                    lifetime.Cancel();
                }
            }, lifetime.Token);
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }
        return new(ObserveAsync(), running.ProcessId);
        async Task<CommandResult> ObserveAsync()
        {
            using (lifetime)
            {
                await ((Task)running.Task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (assignmentError is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(assignmentError);
                return await running.Task.ConfigureAwait(false);
            }
        }
    }
}
