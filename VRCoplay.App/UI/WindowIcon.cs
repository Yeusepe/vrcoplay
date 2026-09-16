// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
namespace VRCoplay;
internal static class WindowIcon
{
    internal static byte[] Load(CaptureWindow window)
    {
        try
        {
            if (IsWindow(window.Hwnd)
                && GetWindowThreadProcessId(window.Hwnd, out var pid) != 0 && pid == window.Pid)
            {
                var handle = GetWindowIcon(window.Hwnd);
                if (handle != 0)
                {
                    using var copy = CopyIcon(new HICON(handle));
                    if (!copy.IsInvalid)
                    {
                        using var icon = Icon.FromHandle(copy.DangerousGetHandle());
                        return Encode(icon);
                    }
                }
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Window icon lookup failed: {error.Message}");
        }
        try
        {
            using var process = Process.GetProcessById(window.Pid);
            if (process.MainModule?.FileName is { } path)
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                    return Encode(icon);
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Application icon lookup failed: {error.Message}");
        }
        return Encode(SystemIcons.Application);
    }
    private static nint GetWindowIcon(nint hwnd)
    {
        foreach (var kind in new nint[] { 1 , 2 , 0  })
        {
            nint icon = 0;
            if (SendMessageTimeout(hwnd, (uint)WindowMessage.WM_GETICON, kind, 0,
                SMTO.SMTO_ABORTIFHUNG | SMTO.SMTO_ERRORONEXIT, 100, ref icon) == 0)
                break;
            if (icon != 0)
                return icon;
        }
        var classIcon = GetClassLong(hwnd, GetClassLongFlag.GCLP_HICON);
        return classIcon != 0 ? classIcon : GetClassLong(hwnd, GetClassLongFlag.GCLP_HICONSM);
    }
    private static byte[] Encode(Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
