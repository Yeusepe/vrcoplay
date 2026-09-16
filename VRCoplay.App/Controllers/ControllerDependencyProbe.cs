// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using Vanara.PInvoke;
using Valve.VR;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;
namespace VRCoplay;
internal static class ControllerDependencyProbe
{
    private static readonly Guid DriverInterface = new("B4030C06-DC5F-4FCC-87EB-E5515A0935C0");
    internal static ControllerReadiness Read()
    {
        var steamVr = false;
        try { steamVr = OpenVR.IsRuntimeInstalled(); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
        try { return new(ReadDriver(), steamVr); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        { return new(ControllerDriverState.Unavailable, steamVr); }
    }
    private static unsafe ControllerDriverState ReadDriver()
    {
        var guid = DriverInterface;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (PInvoke.CM_Get_Device_Interface_List_Size(out var length, guid, default, 0) != CONFIGRET.CR_SUCCESS || length > 65536)
                return ControllerDriverState.Unavailable;
            if (length <= 1) return ControllerDriverState.Missing;
            var buffer = new char[length];
            fixed (char* pointer = buffer)
            {
                var result = PInvoke.CM_Get_Device_Interface_List(&guid, default, pointer, length, 0);
                if (result == CONFIGRET.CR_BUFFER_SMALL) continue;
                if (result != CONFIGRET.CR_SUCCESS) return ControllerDriverState.Unavailable;
            }
            var paths = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in paths)
            {
                using var handle = Kernel32.CreateFile(path,
                    Kernel32.FileAccess.GENERIC_READ | Kernel32.FileAccess.GENERIC_WRITE,
                    FileShare.ReadWrite, null, FileMode.Open, 0, default);
                if (!handle.IsInvalid) return ControllerDriverState.Ready;
            }
            return paths.Length == 0 ? ControllerDriverState.Missing : ControllerDriverState.Unavailable;
        }
        return ControllerDriverState.Unavailable;
    }
}
