// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Valve.VR;
namespace VRCoplay;
internal static class SteamVrRuntime
{
    private static readonly uint EventSize = (uint)Marshal.SizeOf<VREvent_t>();
    internal static bool QuitRequested(CVRSystem system)
    {
        var current = default(VREvent_t);
        while (system.PollNextEvent(ref current, EventSize))
        {
            if ((EVREventType)current.eventType != EVREventType.VREvent_Quit)
                continue;
            system.AcknowledgeQuit_Exiting();
            return true;
        }
        return false;
    }
}
