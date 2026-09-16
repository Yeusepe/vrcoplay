// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
namespace VRCoplay;
internal sealed class QuietApplicationAudio : IAsyncDisposable
{
    internal const float Attenuation = .000001f;
    private static QuietApplicationAudio? _active;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly ConcurrentQueue<(string Device, AudioSessionControl Control)> _newSessions = new();
    private readonly Dictionary<string, MMDevice> _devices = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private Exception? _failure;
    private int _epoch;
    private int _verifiedEpoch = -1;
    private long _resumeAt = long.MaxValue;
    private QuietApplicationAudio(int pid, string path) =>
        _worker = new Task(() => Run(pid, path), CancellationToken.None, TaskCreationOptions.LongRunning);
    internal static async Task<QuietApplicationAudio> StartAsync(
        int pid,
        CancellationToken stop,
        string? recoveryPath = null
    )
    {
        stop.ThrowIfCancellationRequested();
        recoveryPath ??= AudioVolumeRecovery.DefaultPath;
        await AudioRecoveryWatchdog.EnsureStartedAsync(recoveryPath, stop).ConfigureAwait(false);
        var owner = new QuietApplicationAudio(pid, recoveryPath);
        if (Interlocked.CompareExchange(ref _active, owner, null) is not null)
            throw new InvalidOperationException("Application audio is already being adjusted.");
        owner._worker.Start(TaskScheduler.Default);
        try
        {
            await owner._started.Task.WaitAsync(stop).ConfigureAwait(false);
            return owner;
        }
        catch
        {
            await owner.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
    internal static Task RecoverAsync() =>
        Task.Run(() =>
        {
            using var recovery = new AudioVolumeRecovery(AudioVolumeRecovery.DefaultPath);
            recovery.Recover();
        });
    internal static void RestoreOnExit() =>
        Volatile.Read(ref _active)?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    internal void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } failure)
            throw new InvalidOperationException("Quiet local audio stopped. " + failure.Message, failure);
    }
    internal void Compensate(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var epoch = Volatile.Read(ref _epoch);
        if (
            epoch != Volatile.Read(ref _verifiedEpoch)
            || Stopwatch.GetTimestamp() < Volatile.Read(ref _resumeAt)
            || Volatile.Read(ref _failure) is not null
            || _stop.IsCancellationRequested
        )
        {
            output.Clear();
            return;
        }
        var source = MemoryMarshal.Cast<byte, float>(input);
        var destination = MemoryMarshal.Cast<byte, float>(output);
        if (source.ContainsAnyExceptInRange(-Attenuation * 4, Attenuation * 4))
        {
            Fail(new InvalidOperationException(
                "The application changed its audio level. Turn off Quiet local audio or restart sharing."));
            output.Clear();
            return;
        }
        TensorPrimitives.Divide<float>(source, Attenuation, destination);
        TensorPrimitives.Min<float>(destination, 1f, destination);
        TensorPrimitives.Max<float>(destination, -1f, destination);
        if (epoch != Volatile.Read(ref _epoch) || Volatile.Read(ref _failure) is not null)
            output.Clear();
    }
    private void Pause() => Interlocked.Increment(ref _epoch);
    private void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref _failure, error, null);
        Pause();
    }
    private void SessionCreated(string device, AudioSessionControl session)
    {
        Pause();
        _newSessions.Enqueue((device, session));
    }
    private void Run(int pid, string path)
    {
        AudioVolumeRecovery? recovery = null;
        try
        {
            recovery = new(path);
            recovery.Recover();
            var tree = new AudioProcessTree(pid);
            using var enumerator = new MMDeviceEnumerator();
            using var notifications = enumerator.CreateNotificationClient(useSynchronizationContext: false);
            notifications.DeviceAdded += (_, _) => Pause();
            notifications.DeviceRemoved += (_, _) => Pause();
            notifications.DeviceStateChanged += (_, _) => Pause();
            notifications.DefaultDeviceChanged += (_, _) => Pause();
            do
            {
                var epoch = Volatile.Read(ref _epoch);
                tree.Refresh();
                RefreshDevices(enumerator);
                while (_newSessions.TryPeek(out var created))
                {
                    var key = created.Device + "|" + created.Control.GetSessionInstanceIdentifier;
                    var retained = _sessions.TryAdd(key, new Session(this, created.Device, created.Control));
                    _newSessions.TryDequeue(out _);
                    if (!retained) created.Control.Dispose();
                }
                foreach (var (key, session) in _sessions)
                {
                    var control = session.Control;
                    if (session.Entry is null && control.State == AudioSessionState.AudioSessionStateExpired)
                    {
                        _sessions.Remove(key);
                        control.Dispose();
                        continue;
                    }
                    if (!tree.Contains(control.GetProcessID) || control.IsSystemSoundsSession
                        || control.State != AudioSessionState.AudioSessionStateActive)
                        continue;
                    if (session.Entry is null)
                    {
                        Pause();
                        session.Entry = recovery.Remember(session.Device, control);
                        control.RegisterEventClient(session);
                        control.SimpleAudioVolume.Volume = session.Entry.Applied;
                    }
                    session.CheckVolume(control.SimpleAudioVolume.Volume);
                }
                ThrowIfFailed();
                if (epoch != _verifiedEpoch)
                {
                    Volatile.Write(ref _resumeAt, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4);
                    Volatile.Write(ref _verifiedEpoch, epoch);
                }
                _started.TrySetResult();
            } while (!_stop.Token.WaitHandle.WaitOne(100));
        }
        catch (Exception error)
        {
            Fail(error);
            _started.TrySetException(error);
        }
        finally
        {
            Pause();
            var restored = new List<AudioVolumeRecovery.Entry>();
            var restoreErrors = new List<Exception>();
            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception error) { restoreErrors.Add(error); }
            }
            foreach (var session in _sessions.Values)
            {
                if (session.Entry is { } entry)
                    Attempt(() =>
                    {
                        recovery!.Restore(session.Control, entry);
                        restored.Add(entry);
                    });
                Attempt(session.Control.Dispose);
            }
            _sessions.Clear();
            foreach (var device in _devices.Values)
                Attempt(device.Dispose);
            _devices.Clear();
            while (_newSessions.TryDequeue(out var created))
                Attempt(created.Control.Dispose);
            try { Attempt(() => recovery?.Forget(restored)); }
            finally { recovery?.Dispose(); }
            Interlocked.CompareExchange(ref _active, null, this);
            if (restoreErrors.Count > 0)
                throw new InvalidOperationException(
                    "Some application volumes could not be restored. Reopen VRCoplay with the audio device connected to retry, or adjust the app in Windows Volume mixer.",
                    new AggregateException(restoreErrors));
        }
    }
    private void RefreshDevices(MMDeviceEnumerator enumerator)
    {
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (_devices.ContainsKey(device.ID))
            {
                device.Dispose();
                continue;
            }
            Pause();
            var id = device.ID;
            _devices.Add(id, device);
            var manager = device.AudioSessionManager;
            manager.OnSessionCreated += (_, session) => SessionCreated(id, session);
            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
                _newSessions.Enqueue((id, sessions[i]));
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        Pause();
        await _worker.ConfigureAwait(false);
    }
    private sealed class Session(
        QuietApplicationAudio owner,
        string device,
        AudioSessionControl control
    ) : IAudioSessionEventsHandler
    {
        internal AudioSessionControl Control => control;
        internal string Device => device;
        internal AudioVolumeRecovery.Entry? Entry { get; set; }
        internal void CheckVolume(float volume)
        {
            if (!AudioVolumeRecovery.Matches(volume, Entry!.Applied))
                owner.Fail(
                    new InvalidOperationException(
                        "The application's Windows volume changed. Restart sharing to use the new volume."
                    )
                );
        }
        public void OnVolumeChanged(float volume, bool isMuted)
        {
            if (!AudioVolumeRecovery.Matches(volume, Entry!.Applied))
                owner.Pause();
        }
        public void OnSessionDisconnected(AudioSessionDisconnectReason reason) => owner.Pause();
        public void OnStateChanged(AudioSessionState state) => owner.Pause();
        public void OnChannelVolumeChanged(uint count, IntPtr volumes, uint index) { }
        public void OnDisplayNameChanged(string name) { }
        public void OnIconPathChanged(string path) { }
        public void OnGroupingParamChanged(ref Guid id) { }
    }
}
