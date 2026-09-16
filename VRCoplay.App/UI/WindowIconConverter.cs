// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;
namespace VRCoplay;
public static class WindowIconImage
{
    private static readonly ConditionalWeakTable<CaptureWindow, BitmapImage> Images = new();
    public static BitmapImage? For(object? value) =>
        value is CaptureWindow window ? Images.GetValue(window, CreateImage) : null;
    private static BitmapImage CreateImage(CaptureWindow window)
    {
        var image = new BitmapImage();
        _ = LoadImageAsync(image, window);
        return image;
    }
    private static async Task LoadImageAsync(BitmapImage image, CaptureWindow window)
    {
        try
        {
            var png = await Task.Run(() => WindowIcon.Load(window));
            using var stream = new MemoryStream(png);
            await image.SetSourceAsync(stream.AsRandomAccessStream());
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Window icon unavailable: {error.Message}");
        }
    }
}
