// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
namespace VRCoplay;
internal readonly record struct ControllerMotion(Vector3 Acceleration, Vector3 AngularVelocity)
{
    internal static ControllerMotion Neutral => new(new(0, VrMotionState.Gravity, 0), Vector3.Zero);
    internal bool IsFinite => Finite(Acceleration) && Finite(AngularVelocity);
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    internal void Apply(ref DualShockState state)
    {
        state.AccelX = Accel(Acceleration.X); state.AccelY = Accel(Acceleration.Y); state.AccelZ = Accel(Acceleration.Z);
        state.GyroX = Gyro(AngularVelocity.X); state.GyroY = Gyro(AngularVelocity.Y); state.GyroZ = Gyro(AngularVelocity.Z);
    }
    internal static ControllerMotion FromDualShock(in DualShockState state) => new(
        new Vector3(state.AccelX, state.AccelY, state.AccelZ) * (VrMotionState.Gravity / 8192),
        new Vector3(state.GyroX, state.GyroY, state.GyroZ) * (MathF.PI / (180 * 16)));
    private static short Accel(float value) => Raw(value / VrMotionState.Gravity * 8192d);
    private static short Gyro(float value) => Raw(value * (180d / Math.PI * 16));
    private static short Raw(double value) => double.IsFinite(value) ? short.CreateSaturating(Math.Round(value)) : (short)0;
}
internal readonly record struct ControllerFrame(DualShockState Controls, ControllerMotion Primary,
    ControllerMotion Secondary, ulong Timestamp, bool PrimaryIsLeft)
{
    internal static ControllerFrame Neutral => new(DualShockState.Neutral, ControllerMotion.Neutral,
        ControllerMotion.Neutral, 0, false);
    internal DualShockState ToDualShock(int motionHand)
    {
        var state = Controls;
        var motion = motionHand == 0 ? ControllerMotion.FromDualShock(DualShockState.Neutral)
            : (motionHand == 1) == PrimaryIsLeft ? Primary : Secondary;
        motion.Apply(ref state);
        return state;
    }
}
internal static class ControllerReport
{
    internal const int Size = 98;
    internal static void Write(Span<byte> destination, in ControllerFrame frame)
    {
        var bytes = destination[..Size];
        bytes.Clear();
        "VRC2"u8.CopyTo(bytes);
        bytes[4] = frame.PrimaryIsLeft ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], frame.Timestamp);
        var controls = frame.Controls;
        MemoryMarshal.Write(bytes[16..], in controls);
        WriteMotion(bytes[50..], frame.Primary);
        WriteMotion(bytes[74..], frame.Secondary);
    }
    internal static bool TryRead(ReadOnlySpan<byte> bytes, out ControllerFrame frame)
    {
        frame = ControllerFrame.Neutral;
        if (bytes.Length != Size || !bytes[..4].SequenceEqual("VRC2"u8) || bytes[4] > 1 ||
            bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0)
            return false;
        frame = new(MemoryMarshal.Read<DualShockState>(bytes[16..]), ReadMotion(bytes[50..]),
            ReadMotion(bytes[74..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), bytes[4] != 0);
        return frame.Primary.IsFinite && frame.Secondary.IsFinite;
    }
    private static void WriteMotion(Span<byte> bytes, ControllerMotion motion)
    {
        WriteVector(bytes, motion.Acceleration);
        WriteVector(bytes[12..], motion.AngularVelocity);
    }
    private static void WriteVector(Span<byte> bytes, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], v.Z);
    }
    private static ControllerMotion ReadMotion(ReadOnlySpan<byte> bytes) => new(ReadVector(bytes), ReadVector(bytes[12..]));
    private static Vector3 ReadVector(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));
}
