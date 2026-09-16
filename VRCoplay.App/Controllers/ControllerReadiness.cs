// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
namespace VRCoplay;
internal enum ControllerDriverState { Checking, Missing, Unavailable, Ready }
internal sealed record ControllerReadiness(ControllerDriverState Driver, bool SteamVrInstalled)
{
    internal static ControllerReadiness Checking { get; } = new(ControllerDriverState.Checking, false);
    internal bool CanCreateGamepads => Driver == ControllerDriverState.Ready;
    internal bool CanOutput(bool cemuhook, bool dualShock) => cemuhook || (dualShock && CanCreateGamepads);
    internal bool CanCapture(bool sendsToHost, bool cemuhook = false, bool dualShock = true) =>
        SteamVrInstalled && (sendsToHost || CanOutput(cemuhook, dualShock));
    internal string DriverDescription => Driver switch
    {
        ControllerDriverState.Checking => "Checking this PC…",
        ControllerDriverState.Ready => "Controller driver ready",
        ControllerDriverState.Missing => "Controller driver not detected",
        _ => "Controller driver unavailable",
    };
    internal string Help(bool sendsToHost, bool cemuhook = false, bool dualShock = true) => Driver == ControllerDriverState.Checking
        ? "Checking controller support. You can still share your screen."
        : !sendsToHost && !CanOutput(cemuhook, dualShock)
            ? !dualShock ? "Enable Cemuhook or DualShock in Controls to use controllers on this PC."
                : Driver == ControllerDriverState.Missing
                ? "Set up the optional controller driver to use gamepads on this PC."
                : "Restart this PC after installing the controller driver, then check again."
            : !SteamVrInstalled
                ? "Install SteamVR to use your VR controllers."
                : "Start SteamVR before playing with your VR controllers.";
}
