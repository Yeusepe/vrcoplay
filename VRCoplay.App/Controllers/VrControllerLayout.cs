// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using Valve.VR;
namespace VRCoplay;
internal readonly record struct VrControllerLayout(bool LeftHanded)
{
    internal ETrackedControllerRole MainRole => LeftHanded ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand;
    internal ETrackedControllerRole SecondaryRole => LeftHanded ? ETrackedControllerRole.RightHand : ETrackedControllerRole.LeftHand;
    internal string MainTipAction => LeftHanded ? "/actions/vrcoplay/in/left_tip" : "/actions/vrcoplay/in/right_tip";
    internal string HapticAction => LeftHanded ? "/actions/vrcoplay/out/calibration_haptic_left" : "/actions/vrcoplay/out/calibration_haptic";
    internal void Apply(ref DualShockState main, Vector2 left, Vector2 right,
        float leftTrigger, float rightTrigger, ushort buttons)
    {
        if (LeftHanded)
        {
            (left, right) = (right, left);
            (leftTrigger, rightTrigger) = (rightTrigger, leftTrigger);
            buttons = Swap(buttons, DualShockState.Cross, DualShockState.Square);
            buttons = Swap(buttons, DualShockState.Circle, DualShockState.Triangle);
            buttons = Swap(buttons, DualShockState.L1, DualShockState.R1);
            buttons = Swap(buttons, DualShockState.L3, DualShockState.R3);
        }
        main.SetControls(left, right, leftTrigger, rightTrigger, buttons);
    }
    private static ushort Swap(ushort buttons, ushort first, ushort second) =>
        (ushort)((buttons & ~(first | second)) | ((buttons & first) != 0 ? second : 0) | ((buttons & second) != 0 ? first : 0));
}
