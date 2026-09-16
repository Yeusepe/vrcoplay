// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Numerics;
using Valve.VR;
namespace VRCoplay;
internal sealed class VrMotionState
{
    internal const float Gravity = 9.80665f;
    private Vector3 _previousVelocity;
    private long _previousTime;
    private uint _device = OpenVR.k_unTrackedDeviceIndexInvalid;
    private bool _haveVelocity;
    internal DualShockState Read(uint device, in TrackedDevicePose_t pose, long now)
    {
        var state = DualShockState.Neutral;
        ReadMotion(device, pose, now).Apply(ref state);
        return state;
    }
    internal ControllerMotion ReadMotion(uint device, in TrackedDevicePose_t pose, long now)
    {
        if (!pose.bDeviceIsConnected || !pose.bPoseIsValid)
        {
            _haveVelocity = false;
            _device = OpenVR.k_unTrackedDeviceIndexInvalid;
            return ControllerMotion.Neutral;
        }
        var velocity = Vector(pose.vVelocity);
        var velocityOk = pose.eTrackingResult == ETrackingResult.Running_OK && Finite(velocity);
        var dt = Stopwatch.GetElapsedTime(_previousTime, now).TotalSeconds;
        var acceleration = velocityOk && _haveVelocity && device == _device && dt is >= .002 and <= .1
            ? (velocity - _previousVelocity) / (float)dt
            : Vector3.Zero;
        _haveVelocity = velocityOk;
        _previousVelocity = velocity;
        _previousTime = now;
        _device = device;
        var matrix = pose.mDeviceToAbsoluteTracking;
        var gyro = Local(matrix, Vector(pose.vAngularVelocity));
        var accel = Local(matrix, acceleration + Vector3.UnitY * Gravity);
        return new(Finite(accel) ? accel : ControllerMotion.Neutral.Acceleration,
            Finite(gyro) ? gyro : Vector3.Zero);
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static Vector3 Vector(HmdVector3_t value) => new(value.v0, value.v1, value.v2);
    private static Vector3 Local(HmdMatrix34_t matrix, Vector3 value) => new(
        matrix.m0 * value.X + matrix.m4 * value.Y + matrix.m8 * value.Z,
        matrix.m1 * value.X + matrix.m5 * value.Y + matrix.m9 * value.Z,
        matrix.m2 * value.X + matrix.m6 * value.Y + matrix.m10 * value.Z);
}
