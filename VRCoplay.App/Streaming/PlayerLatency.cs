// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
namespace VRCoplay;
internal sealed class PlayerLatency
{
    internal static readonly Guid MediaEngine = new("8f2048e0-f260-4f57-a8d1-932376291682");
    internal static readonly Guid MediaFoundation = new("a7364e1a-894f-4b3d-a930-2ed9c8c4c811");
    internal static readonly Guid SourceResolver = new("bc97b970-d001-482f-8745-b8d7d5759f99");
    private readonly VrVideoPlayback _playback = new();
    private readonly HashSet<ulong> _engines = [];
    private readonly Dictionary<(uint Component, uint Setting), bool> _components = [];
    internal bool? Mode { get; private set; }
    private string? _link, _localPath;
    internal bool? PlaybackMode => _playback.Started ? Mode : null;
    internal void SetStream(string link, string localPath)
    {
        if (_link == link && _localPath == localPath) return;
        _link = link;
        _localPath = localPath;
        _playback.SetStream(link, localPath);
        _components.Clear();
        Mode = null;
    }
    internal void Observe(bool engine, int id, ulong instance = 0, uint component = 0, uint setting = 0, int result = 0)
    {
        var discovered = engine && (id == 105 || id == 101 && result == 0) && _engines.Add(instance);
        if (engine && (discovered || id is 105 or 113 or 137))
        {
            if (id == 113) _engines.Remove(instance);
            _components.Clear();
            Mode = null;
        }
        if (engine) _playback.Engine(id, instance, result);
        if (engine || _engines.Count != 1 || id is not (1200 or 1201) || result < 0)
            return;
        _components[(component, setting)] = id == 1200;
        Mode = _components.Values.Distinct().Count() == 1 ? id == 1200 : null;
    }
    internal static bool ShouldWarn(int world, bool enabled, bool? mode) =>
        enabled && (world == 2 || world == 0 && mode != true);
    internal static Task WatchAsync(Action<bool?> changed, CancellationToken stop,
        Func<string>? videoLink = null, Func<string>? localPath = null, Action? videoStarted = null,
        Action<string, bool?>? playbackChanged = null) => Task.Run(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            var processes = Process.GetProcessesByName("VRChat");
            try
            {
                if (processes.Length == 1) Capture(processes[0], changed, stop, videoLink: videoLink, localPath: localPath,
                    videoStarted: videoStarted, playbackChanged: playbackChanged);
            }
            catch (Exception error) { Trace.TraceWarning($"Player latency unavailable: {error.Message}"); }
            finally
            {
                foreach (var process in processes) process.Dispose();
                changed(null);
                playbackChanged?.Invoke(videoLink?.Invoke() ?? "", null);
            }
            if (stop.WaitHandle.WaitOne(2000)) break;
        }
    });
    internal static void Capture(Process process, Action<bool?> changed, CancellationToken stop, Action? ready = null,
        Func<string>? videoLink = null, Func<string>? localPath = null, Action? videoStarted = null,
        Action<string, bool?>? playbackChanged = null)
    {
        using var owner = new Mutex(false, $"Local\\VRCoplay-PlayerLatency-{process.Id}");
        try { if (!owner.WaitOne(0)) return; }
        catch (AbandonedMutexException) { }
        try { CaptureOwned(process, changed, stop, ready, videoLink, localPath, videoStarted, playbackChanged); }
        finally { owner.ReleaseMutex(); }
    }
    private static void CaptureOwned(Process process, Action<bool?> changed, CancellationToken stop, Action? ready,
        Func<string>? videoLink, Func<string>? localPath, Action? videoStarted, Action<string, bool?>? playbackChanged)
    {
        var directory = Directory.CreateTempSubdirectory("VRCoplay-latency-");
        var file = Path.Combine(directory.FullName, "player.etl");
        int targetPid = process.Id;
        try
        {
            using var session = new TraceEventSession($"VRCoplay-PlayerLatency-{targetPid}", file, TraceEventSessionOptions.PrivateLogger)
            {
                StopOnDispose = true,
                CircularBufferMB = 4,
            };
            var options = new TraceEventProviderOptions { ProcessIDFilter = [targetPid] };
            session.EnableProvider(MediaEngine, TraceEventLevel.Verbose, 0x8000000000000000, options);
            session.EnableProvider(MediaFoundation, TraceEventLevel.Informational, 0x2000000000000000, options);
            if (videoStarted is not null || playbackChanged is not null)
                session.EnableProvider(SourceResolver, TraceEventLevel.Informational, 0x4000000000000000, options);
            var evidence = new PlayerLatency();
            var last = DateTime.MinValue;
            ready?.Invoke();
            while (!stop.WaitHandle.WaitOne(2000) && !process.HasExited)
            {
                session.Flush();
                if (videoLink is not null && localPath is not null)
                    evidence.SetStream(videoLink(), localPath());
                last = evidence.Read(directory.FullName, targetPid, last);
                changed(evidence.Mode);
                if (evidence._link is { } observedLink)
                    playbackChanged?.Invoke(observedLink, evidence.PlaybackMode);
                if (evidence._playback.Started) videoStarted?.Invoke();
            }
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 4201 && process.HasExited) { }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(directory.FullName)) File.Delete(path);
            directory.Delete();
        }
    }
    private DateTime Read(string directory, int pid, DateTime last)
    {
        var files = Directory.GetFiles(directory, "*.etl*");
        if (files.Length == 0) return last;
        var since = last;
        using var source = new ETWTraceEventSource(files, TraceEventSourceType.MergeAll);
        source.Dynamic.All += e =>
        {
            if (e.ProcessID != pid || e.TimeStamp <= since) return;
            if (e.ProviderGuid == MediaEngine && (int)e.ID is 101 or 105 or 113 or 137)
            {
                var id = (int)e.ID;
                var instance = Convert.ToUInt64(e.PayloadValue(0));
                Observe(true, id, instance, result: id == 101 ? unchecked((int)Convert.ToInt64(e.PayloadByName("hr"))) : 0);
            }
            else if (e.ProviderGuid == SourceResolver && (int)e.ID is 200 or 205)
            {
                ObserveSource((int)e.ID, Convert.ToUInt64(e.PayloadByName("Context")),
                    Convert.ToString(e.PayloadByName("url")) ?? "", unchecked((int)Convert.ToInt64(e.PayloadByName("hr"))));
            }
            else if (e.ProviderGuid == MediaFoundation && (int)e.ID is 1200 or 1201)
                Observe(false, (int)e.ID, component: Convert.ToUInt32(e.PayloadByName("ComponentType")),
                    setting: Convert.ToUInt32(e.PayloadByName("SettingType")), result: Convert.ToInt32(e.PayloadByName("Result")));
            else return;
            last = e.TimeStamp;
        };
        source.Process();
        if (source.EventsLost != 0) throw new IOException("Latency trace lost events.");
        return last;
    }
    internal void ObserveSource(int id, ulong context, string url, int result = 0) =>
        _playback.Source(id, context, url, result);
}
