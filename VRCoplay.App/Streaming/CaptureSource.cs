// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal abstract record CaptureSource
{
    public abstract string Label { get; }
    public abstract string CaptureMode { get; }
    public string Description => $"{CaptureMode}: {Label}";
    internal abstract nint CurrentHandle();
    internal abstract (int Width, int Height) CaptureSize();
    internal abstract bool IsSameSource(CaptureSource? other);
    internal abstract Task WaitForChange(nint handle, CancellationToken stop);
    internal string CaptureElement(nint handle, StreamSettings settings)
    {
        var size = CaptureSize();
        int width = size.Width - settings.CropLeft - settings.CropRight, height = size.Height - settings.CropTop - settings.CropBottom;
        if (width < 1 || height < 1) throw new InvalidOperationException("The resized source is smaller than the crop.");
        var property = this is CaptureMonitor ? "monitor-handle" : "window-handle";
        return $"d3d11screencapturesrc name=capture capture-api=wgc {property}={unchecked((ulong)handle)} " +
            $"window-capture-mode=client show-cursor={settings.Cursor.ToString().ToLowerInvariant()} " +
            $"crop-x={settings.CropLeft} crop-y={settings.CropTop} crop-width={width} crop-height={height}";
    }
    public override string ToString() => Label;
}
