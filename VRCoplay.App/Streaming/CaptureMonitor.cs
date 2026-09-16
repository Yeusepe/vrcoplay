// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
namespace VRCoplay;
internal sealed record CaptureMonitor(string DeviceName, nint Handle, RECT Bounds, bool Primary) : CaptureSource
{
    public override string CaptureMode => "Fullscreen Capture";
    public override string Label => $"Display {DeviceName.Replace(@"\\.\DISPLAY", "", StringComparison.OrdinalIgnoreCase)} · {Bounds.Width} × {Bounds.Height}{(Primary ? " · Main display" : "")}";
    public override string ToString() => Label;
    internal static IReadOnlyList<CaptureMonitor> Enumerate()
    {
        var displays = new List<CaptureMonitor>();
        EnumDisplayMonitors(default, default, (monitor, dc, rect, data) =>
        {
            var info = MONITORINFOEX.Default;
            if (GetMonitorInfo(monitor, ref info) && info.rcMonitor.Width > 0 && info.rcMonitor.Height > 0)
                displays.Add(new(info.szDevice, (nint)monitor, info.rcMonitor, (uint)info.dwFlags == 1));
            return true;
        }, 0);
        return displays.OrderByDescending(x => x.Primary).ThenBy(x => x.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal override bool IsSameSource(CaptureSource? other) =>
        other is CaptureMonitor monitor && StringComparer.OrdinalIgnoreCase.Equals(DeviceName, monitor.DeviceName);
    internal CaptureMonitor Resolve() => Enumerate().FirstOrDefault(IsSameSource)
        ?? throw new InvalidOperationException("The selected display is disconnected. Reconnect it or choose another source.");
    internal static CaptureMonitor FromSource(CaptureSource? source)
    {
        if (source is CaptureMonitor monitor) return monitor.Resolve();
        if (source is not CaptureWindow window)
            throw new InvalidOperationException("Choose a window or display before approving remote play.");
        var handle = MonitorFromWindow(window.CurrentHandle(), MonitorFlags.MONITOR_DEFAULTTONULL);
        return Enumerate().FirstOrDefault(display => display.Handle == (nint)handle)
            ?? throw new InvalidOperationException("The selected window is not on a connected display. Move it onto a display before approving remote play.");
    }
    internal override nint CurrentHandle() => Resolve().Handle;
    internal override (int Width, int Height) CaptureSize()
    {
        var current = Resolve();
        return (current.Bounds.Width, current.Bounds.Height);
    }
    internal override async Task WaitForChange(nint handle, CancellationToken stop)
    {
        var bounds = Resolve().Bounds;
        while (true)
        {
            await Task.Delay(250, stop);
            var current = Resolve();
            if (current.Handle != handle || !current.Bounds.Equals(bounds))
                return;
        }
    }
}
