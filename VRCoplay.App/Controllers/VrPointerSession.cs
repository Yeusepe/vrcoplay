// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using VRC.OSCQuery;
namespace VRCoplay;
internal sealed class VrPointerSession : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _calibrationChanged;
    private readonly Action<PointerCalibrationProgress>? _progressChanged;
    private readonly Func<IDiscovery>? _discoveryFactory;
    private readonly int _fallbackPort;
    private VrPointerOsc? _current;
    private Task? _retry;
    private bool _enabled, _disposed, _videoReady;
    private int _generation;
    private string _lastStatus = "Off";
    internal VrPointerOsc? Current => Volatile.Read(ref _current);
    internal string Status => Volatile.Read(ref _lastStatus);
    internal VrPointerSession(Action<string> status, bool enabled, Action<bool>? calibrationChanged,
        Func<IDiscovery>? discoveryFactory = null, Action<PointerCalibrationProgress>? progressChanged = null, int fallbackPort = 9001, bool videoReady = true)
    {
        _status = status;
        _enabled = enabled;
        _calibrationChanged = calibrationChanged;
        _progressChanged = progressChanged;
        _discoveryFactory = discoveryFactory;
        _fallbackPort = fallbackPort;
        _videoReady = videoReady;
        Restart();
    }
    internal Task RetryAsync()
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (!_videoReady) { Publish(_generation, _enabled ? "Waiting for video in VRChat" : "Off"); return Task.CompletedTask; }
            if (_retry is { IsCompleted: false }) return _retry;
            return _retry = Task.Run(Restart);
        }
    }
    internal Task VideoStartedAsync()
    {
        lock (_gate)
        {
            if (_disposed || _videoReady) return Task.CompletedTask;
            _videoReady = true;
            return RetryAsync();
        }
    }
    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = enabled;
            _current?.SetEnabled(enabled);
            if (!_videoReady) Publish(_generation, enabled ? "Waiting for video in VRChat" : "Off");
        }
    }
    private void Restart()
    {
        VrPointerOsc? previous;
        int generation;
        bool enabled;
        lock (_gate)
        {
            if (_disposed) return;
            generation = ++_generation;
            enabled = _enabled;
            previous = Interlocked.Exchange(ref _current, null);
            _calibrationChanged?.Invoke(false);
            _progressChanged?.Invoke(default);
            if (!_videoReady)
            {
                Publish(generation, enabled ? "Waiting for video in VRChat" : "Off");
                return;
            }
            Publish(generation, "Restarting OSC");
        }
        previous?.Dispose();
        VrPointerOsc? next = null;
        try
        {
            next = new(status => Publish(generation, status), enabled,
                calibrated => Publish(generation, calibrated), _discoveryFactory?.Invoke(), _gate,
                progress => Publish(generation, progress), _fallbackPort);
            lock (_gate)
            {
                if (_disposed || generation != _generation) return;
                next.SetEnabled(_enabled);
                Volatile.Write(ref _current, next);
                next = null;
            }
        }
        catch (Exception error) { Publish(generation, $"OSC could not start: {error.Message}. Retry OSC."); }
        finally { next?.Dispose(); }
    }
    private void Publish(int generation, string status)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            if (_current is not null && (status is "Off" or "Ready" || status.StartsWith("Point ", StringComparison.Ordinal)))
                _enabled = status != "Off";
            Volatile.Write(ref _lastStatus, status);
            _status(status);
        }
    }
    private void Publish(int generation, bool calibrated)
    {
        lock (_gate)
            if (!_disposed && generation == _generation) _calibrationChanged?.Invoke(calibrated);
    }
    private void Publish(int generation, PointerCalibrationProgress progress)
    {
        lock (_gate)
            if (!_disposed && generation == _generation) _progressChanged?.Invoke(progress);
    }
    public void Dispose()
    {
        VrPointerOsc? current;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            current = Interlocked.Exchange(ref _current, null);
            _calibrationChanged?.Invoke(false);
            _progressChanged?.Invoke(default);
        }
        current?.Dispose();
    }
}
