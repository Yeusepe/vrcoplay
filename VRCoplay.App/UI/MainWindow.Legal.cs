// Copyright (c) 2026 YUCP Studio.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace VRCoplay;
public sealed partial class MainWindow
{
    private const string SourceRepository = "https://github.com/Yeusepe/vrcoplay";
    private async void Legal_Click(object sender, RoutedEventArgs e)
    {
        await ShowDialogAsync(new LegalDialog(AppContext.BaseDirectory));
    }
    private async void Source_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "source-info.json");
            if (System.IO.File.Exists(path))
            {
                using var info = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(path));
                if (Uri.TryCreate(info.RootElement.GetProperty("uri").GetString(), UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
                    && await Windows.System.Launcher.LaunchUriAsync(uri)) return;
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }
        try
        {
            if (await Windows.System.Launcher.LaunchUriAsync(new Uri(SourceRepository))) return;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
        await ShowDialogAsync(new ContentDialog
        {
            Title = "VRCoplay source code",
            Content = $"Browse the source at {SourceRepository}. Matching source downloads are provided with distributed releases.",
            CloseButtonText = "Close",
        });
    }
}
