// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace VRCoplay;
internal static class ProcessAudio
{
    internal const int SampleRate = 44100;
    internal static async Task CaptureAsync(int? pid, Action<ReadOnlySpan<byte>, long> output, CancellationToken stop,
        QuietApplicationAudio? quiet = null)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
        await using var recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)(pid ?? Environment.ProcessId), pid is null
                ? ProcessLoopbackMode.ExcludeTargetProcessTree : ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(format)
            .WithBufferLength(20)
            .WithMmcssThreadPriority()
            .BuildAsync();
        Exception? fault = null;
        var compensated = Array.Empty<byte>();
        recorder.DataAvailable += (data, flags, _, timestamp) =>
        {
            if ((flags & AudioClientBufferFlags.TimestampError) != 0) timestamp = 0;
            if (quiet is null)
            {
                output(data, timestamp);
                return;
            }
            if (compensated.Length < data.Length)
                compensated = new byte[data.Length];
            var packet = compensated.AsSpan(0, data.Length);
            quiet.Compensate(data, packet);
            output(packet, timestamp);
        };
        recorder.RecordingStopped += (_, e) => Volatile.Write(ref fault, e.Exception);
        stop.ThrowIfCancellationRequested();
        recorder.StartRecording();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await timer.WaitForNextTickAsync(stop))
        {
            quiet?.ThrowIfFailed();
            if (Volatile.Read(ref fault) is { } error)
                throw new InvalidOperationException("Audio capture stopped.", error);
        }
    }
}
