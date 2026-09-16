// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Vanara.PInvoke.User32;
namespace VRCoplay;
internal sealed record CaptureWindow(int Pid, nint Hwnd, string Title, string ProcessName) : CaptureSource
{
    public string ApplicationName { get; init; } = ProcessName;
    internal string ApplicationId { get; init; } = ProcessName;
    public override string Label => Title;
    public override string CaptureMode => "Window Capture";
    internal override nint CurrentHandle() => CurrentHandle(Pid, Hwnd);
    internal override (int Width, int Height) CaptureSize() => CaptureSize(CurrentHandle());
    internal override bool IsSameSource(CaptureSource? other) => other is CaptureWindow window && Hwnd == window.Hwnd && Pid == window.Pid;
    internal override Task WaitForChange(nint handle, CancellationToken stop) => WaitForWindowChange(Pid, handle, stop);
    public override string ToString() => Label;
    internal static IEnumerable<CaptureWindow> Enumerate(string? name = null)
    {
        var windows = new List<CaptureWindow>();
        var processes = new Dictionary<int, (string Process, string Id, string Label)>();
        var applications = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowThreadProcessId(hwnd, out var pid) == 0
                || pid == Environment.ProcessId)
                return true;
            try
            {
                GetWindowText(hwnd, out var text);
                if (string.IsNullOrWhiteSpace(text)) return true;
                var title = text.Trim();
                if (!processes.TryGetValue((int)pid, out var app))
                {
                    using var process = Process.GetProcessById((int)pid);
                    var processName = process.ProcessName;
                    app = (processName, processName, processName);
                    try
                    {
                        if (process.MainModule?.FileName is { } path)
                        {
                            if (!applications.TryGetValue(path, out var label))
                            {
                                var info = FileVersionInfo.GetVersionInfo(path);
                                label = processName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                                    ? "File Explorer" : info.FileDescription?.Trim();
                                applications[path] = label = string.IsNullOrWhiteSpace(label) ? processName : label;
                            }
                            app = (processName, path, label);
                        }
                    }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
                    processes[(int)pid] = app;
                }
                if (app.Process.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                    app = (app.Process, $"{app.Process}:{hwnd}", title);
                if (name is null || StringComparer.OrdinalIgnoreCase.Equals(name, app.Process))
                    windows.Add(new((int)pid, (nint)hwnd, title, app.Process) { ApplicationId = app.Id, ApplicationName = app.Label });
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        return windows;
    }
    internal static nint CurrentHandle(int pid, nint preferred = 0)
    {
        using var process = Process.GetProcessById(pid);
        var hwnd = preferred != 0 ? preferred : process.MainWindowHandle;
        if (process.HasExited || hwnd == 0 || !IsWindow(hwnd)
            || GetWindowThreadProcessId(hwnd, out var owner) == 0 || owner != pid)
            throw new InvalidOperationException("The selected window is no longer available.");
        return hwnd;
    }
    internal static async Task WaitForWindowChange(int pid, nint hwnd, CancellationToken stop)
    {
        var size = CaptureSize(hwnd);
        while (CurrentHandle(pid, hwnd) == hwnd)
        {
            await Task.Delay(250, stop);
            if (!IsIconic(hwnd) && IsWindowVisible(hwnd) && CaptureSize(hwnd) != size)
                return;
        }
    }
    internal static (int Width, int Height) CaptureSize(nint hwnd)
    {
        if (!IsIconic(hwnd) && GetClientRect(hwnd, out var client) && client.Width > 0 && client.Height > 0)
            return (client.Width, client.Height);
        var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (
            GetWindowPlacement(hwnd, ref placement)
            && placement.rcNormalPosition.Width > 0
            && placement.rcNormalPosition.Height > 0
        )
            return (placement.rcNormalPosition.Width, placement.rcNormalPosition.Height);
        throw new InvalidOperationException("The selected window is unavailable.");
    }
}
