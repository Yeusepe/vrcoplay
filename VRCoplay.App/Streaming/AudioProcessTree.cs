// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using static Vanara.PInvoke.Kernel32;
namespace VRCoplay;
internal sealed class AudioProcessTree
{
    private readonly Dictionary<uint, long> _members = new();
    internal AudioProcessTree(int pid)
    {
        using var process = Process.GetProcessById(pid);
        _members.Add((uint)pid, process.StartTime.ToUniversalTime().Ticks);
    }
    internal bool Contains(uint pid) => _members.ContainsKey(pid);
    internal void Refresh()
    {
        using var snapshot = CreateToolhelp32Snapshot(TH32CS.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
            throw new Win32Exception();
        var parents = snapshot.EnumProcess32().ToDictionary(p => p.th32ProcessID, p => p.th32ParentProcessID);
        foreach (var (pid, started) in _members.ToArray())
            if (!parents.ContainsKey(pid) || StartTime(pid) != started)
                _members.Remove(pid);
        var children = parents.ToLookup(pair => pair.Value, pair => pair.Key);
        var pending = new Queue<uint>(_members.Keys);
        while (pending.TryDequeue(out var parent))
            foreach (var pid in children[parent])
                if (!_members.ContainsKey(pid) && StartTime(pid) is { } started && started >= _members[parent])
                {
                    _members.Add(pid, started);
                    pending.Enqueue(pid);
                }
    }
    private static long? StartTime(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
