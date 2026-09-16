// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal static class CaptureCanvas
{
    internal static (int Width, int Height) Size(CaptureSource source, StreamSettings settings)
    {
        if (settings.Width is { } width && settings.Height is { } height)
            return (width, height);
        var bounds = source.CaptureSize();
        var croppedWidth = bounds.Width - (long)settings.CropLeft - settings.CropRight;
        var croppedHeight = bounds.Height - (long)settings.CropTop - settings.CropBottom;
        if (croppedWidth < 2 || croppedHeight < 2)
            throw new InvalidOperationException("The source is smaller than the configured crop.");
        return ((int)(croppedWidth / 2 * 2), (int)(croppedHeight / 2 * 2));
    }
}
