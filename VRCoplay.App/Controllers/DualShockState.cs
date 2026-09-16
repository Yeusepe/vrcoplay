// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using System.Runtime.InteropServices;
namespace VRCoplay;
[StructLayout(LayoutKind.Explicit, Size = Size)]
internal struct DualShockState
{
    internal const int Size = 34;
    internal const ushort Square = 0x0010, Cross = 0x0020, Circle = 0x0040, Triangle = 0x0080,
        L1 = 0x0100, R1 = 0x0200, L2Button = 0x0400, R2Button = 0x0800, L3 = 0x4000, R3 = 0x8000;
    [FieldOffset(0)] public sbyte LX;
    [FieldOffset(1)] public sbyte LY;
    [FieldOffset(2)] public sbyte RX;
    [FieldOffset(3)] public sbyte RY;
    [FieldOffset(4)] public ushort Buttons;
    [FieldOffset(6)] public byte DPad;
    [FieldOffset(7)] public byte L2;
    [FieldOffset(8)] public byte R2;
    [FieldOffset(10)] public ushort Touch1X;
    [FieldOffset(12)] public ushort Touch1Y;
    [FieldOffset(14)] public byte Touch1Active;
    [FieldOffset(16)] public ushort Touch2X;
    [FieldOffset(18)] public ushort Touch2Y;
    [FieldOffset(20)] public byte Touch2Active;
    [FieldOffset(22)] public short GyroX;
    [FieldOffset(24)] public short GyroY;
    [FieldOffset(26)] public short GyroZ;
    [FieldOffset(28)] public short AccelX;
    [FieldOffset(30)] public short AccelY;
    [FieldOffset(32)] public short AccelZ;
    internal static DualShockState Neutral => new() { AccelZ = -8192 };
    internal void SetControls(Vector2 left, Vector2 right, float leftTrigger, float rightTrigger, ushort buttons)
    {
        LX = Stick(left.X);
        LY = Stick(-left.Y);
        RX = Stick(right.X);
        RY = Stick(-right.Y);
        L2 = byte.CreateSaturating(leftTrigger * byte.MaxValue);
        R2 = byte.CreateSaturating(rightTrigger * byte.MaxValue);
        Buttons = (ushort)(buttons | (L2 > 0 ? L2Button : 0) | (R2 > 0 ? R2Button : 0));
    }
    internal void ReleaseRightTrigger()
    {
        R2 = 0;
        Buttons &= unchecked((ushort)~R2Button);
    }
    private static sbyte Stick(float value) =>
        sbyte.CreateSaturating(MathF.Round(value * (value < 0 ? 128 : 127)));
}
