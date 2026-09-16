// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
namespace VRCoplay;
internal sealed class AudioVolumeRecovery : IDisposable
{
    internal sealed record Entry(string Device, string Session, float Original, float Applied, string? Instance = null);
    internal static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRCoplay",
            "audio-volume-recovery.json"
        );
    private readonly string _path;
    private readonly List<Entry> _entries;
    private readonly Mutex _lease;
    internal bool HasPending => _entries.Count != 0;
    internal AudioVolumeRecovery(string path)
    {
        _path = path;
        var key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))
        );
        _lease = new Mutex(false, @"Local\VRCoplay.AudioVolumes." + key);
        try
        {
            if (!_lease.WaitOne(TimeSpan.FromSeconds(15)))
                throw new InvalidOperationException("Another VRCoplay instance is managing application audio.");
        }
        catch (AbandonedMutexException)
        {
        }
        catch
        {
            _lease.Dispose();
            throw;
        }
        try
        {
            _entries = File.Exists(path)
                ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("The saved application volumes could not be read.")
                : [];
            if (_entries.Any(e => !(e.Original is >= 0 and <= 1 && e.Applied is >= 0 and <= 1)))
                throw new InvalidDataException("The saved application volumes are invalid.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }
    public void Dispose()
    {
        _lease.ReleaseMutex();
        _lease.Dispose();
    }
    internal Entry Remember(string device, AudioSessionControl session)
    {
        var id = session.GetSessionIdentifier;
        var instance = session.GetSessionInstanceIdentifier;
        var volume = session.SimpleAudioVolume.Volume;
        var previous = _entries.FirstOrDefault(e =>
            e.Device == device && e.Session == id && (e.Instance == instance || Matches(volume, e.Applied))
        );
        var original = previous is not null && Matches(volume, previous.Applied) ? previous.Original : volume;
        var entry = new Entry(device, id, original, original * QuietApplicationAudio.Attenuation, instance);
        _entries.RemoveAll(e =>
            e.Device == device && e.Session == id && (e.Instance == instance || e.Instance is null)
        );
        _entries.Add(entry);
        Save();
        return entry;
    }
    internal void Restore(AudioSessionControl session, Entry entry)
    {
        if (Matches(session.SimpleAudioVolume.Volume, entry.Applied))
            session.SimpleAudioVolume.Volume = entry.Original;
    }
    internal void Forget(IEnumerable<Entry> restored)
    {
        foreach (var entry in restored)
            _entries.Remove(entry);
        Save();
    }
    internal void Recover()
    {
        if (_entries.Count == 0)
            return;
        using var enumerator = new MMDeviceEnumerator();
        var restored = new HashSet<Entry>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    var matching = _entries.Where(e =>
                        e.Device == device.ID && e.Session == session.GetSessionIdentifier
                    );
                    var entry =
                        matching.FirstOrDefault(e => e.Instance == session.GetSessionInstanceIdentifier)
                        ?? matching.FirstOrDefault(e =>
                            e.Instance is null || Matches(session.SimpleAudioVolume.Volume, e.Applied)
                        );
                    if (entry is null)
                        continue;
                    Restore(session, entry);
                    restored.Add(entry);
                }
            }
        }
        Forget(restored);
    }
    internal static bool Matches(float actual, float expected) =>
        actual == expected || Math.Abs(actual - expected) <= Math.Abs(expected) * .00001f;
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
        File.Move(temporary, _path, overwrite: true);
    }
}
