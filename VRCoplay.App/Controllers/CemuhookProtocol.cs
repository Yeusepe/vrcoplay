// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
namespace VRCoplay;
internal static class CemuhookProtocol
{
    internal const ushort Version = 1001;
    internal const uint VersionMessage = 0x100000, PortsMessage = 0x100001, DataMessage = 0x100002;
    internal static bool TryRequest(ReadOnlySpan<byte> bytes, out uint type, out uint clientId)
    {
        type = clientId = 0;
        if (bytes.Length < 20 || !bytes[..4].SequenceEqual("DSUC"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != bytes.Length - 16 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != Crc32(bytes, packet: true)) return false;
        type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        clientId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        return true;
    }
    internal static void Header(Span<byte> bytes, uint type, uint serverId)
    {
        bytes.Clear();
        "DSUS"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], (ushort)(bytes.Length - 16));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], serverId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], type);
    }
    internal static void Finish(Span<byte> bytes) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], Crc32(bytes, packet: true));
    internal static void Port(Span<byte> bytes, int slot, int hand, bool connected)
    {
        bytes[..12].Clear();
        bytes[0] = (byte)slot;
        bytes[1] = connected ? (byte)2 : (byte)0;
        bytes[2] = 2;
        bytes[3] = 1;
        Mac(bytes[4..], slot, hand);
        bytes[10] = 0xef;
    }
    internal static void Mac(Span<byte> bytes, int slot, int hand)
    {
        bytes[0] = 0x02; bytes[1] = 0x56; bytes[2] = 0x52; bytes[3] = 0x43;
        bytes[4] = (byte)hand; bytes[5] = (byte)(slot + 1);
    }
    internal static void Data(Span<byte> bytes, uint serverId, int slot, int hand, uint sequence,
        ulong timestamp, in DualShockState controls, in ControllerMotion motion)
    {
        Header(bytes, DataMessage, serverId);
        Port(bytes[20..], slot, hand, true);
        bytes[31] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[32..], sequence);
        var b = controls.Buttons;
        var d = controls.DPad;
        bytes[36] = (byte)(((d & 4) != 0 ? 0x80 : 0) | ((d & 2) != 0 ? 0x40 : 0) |
            ((d & 8) != 0 ? 0x20 : 0) | ((d & 1) != 0 ? 0x10 : 0) |
            ((b & 0x2000) != 0 ? 8 : 0) | ((b & 0x8000) != 0 ? 4 : 0) |
            ((b & 0x4000) != 0 ? 2 : 0) | ((b & 0x1000) != 0 ? 1 : 0));
        bytes[37] = (byte)((b & 0xf0) | ((b & DualShockState.R1) != 0 ? 8 : 0) |
            ((b & DualShockState.L1) != 0 ? 4 : 0) | ((b & DualShockState.R2Button) != 0 ? 2 : 0) |
            ((b & DualShockState.L2Button) != 0 ? 1 : 0));
        bytes[38] = (byte)(b & 1); bytes[39] = (byte)((b >> 1) & 1);
        bytes[40] = (byte)(controls.LX + 128); bytes[41] = InvertAxis(controls.LY);
        bytes[42] = (byte)(controls.RX + 128); bytes[43] = InvertAxis(controls.RY);
        for (var i = 0; i < 4; i++) bytes[44 + i] = (bytes[36] & (0x80 >> i)) != 0 ? (byte)255 : (byte)0;
        for (var i = 0; i < 6; i++) bytes[48 + i] = (bytes[37] & (0x80 >> i)) != 0 ? (byte)255 : (byte)0;
        bytes[54] = controls.R2; bytes[55] = controls.L2;
        Touch(bytes[56..], controls.Touch1Active, 0, controls.Touch1X, controls.Touch1Y);
        Touch(bytes[62..], controls.Touch2Active, 1, controls.Touch2X, controls.Touch2Y);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[68..], timestamp);
        WriteFloat(bytes[76..], -motion.Acceleration.X / VrMotionState.Gravity);
        WriteFloat(bytes[80..], -motion.Acceleration.Y / VrMotionState.Gravity);
        WriteFloat(bytes[84..], -motion.Acceleration.Z / VrMotionState.Gravity);
        WriteFloat(bytes[88..], motion.AngularVelocity.X * (180d / Math.PI));
        WriteFloat(bytes[92..], -motion.AngularVelocity.Y * (180d / Math.PI));
        WriteFloat(bytes[96..], -motion.AngularVelocity.Z * (180d / Math.PI));
        Finish(bytes);
    }
    private static byte InvertAxis(sbyte axis) => (byte)Math.Round(128 - axis * (axis < 0 ? 127d / 128 : 128d / 127));
    private static void WriteFloat(Span<byte> bytes, double value) =>
        BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)Math.Clamp(value, -float.MaxValue, float.MaxValue));
    private static void Touch(Span<byte> bytes, byte active, byte id, ushort x, ushort y)
    {
        bytes[0] = active != 0 ? (byte)1 : (byte)0; bytes[1] = id;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], x);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], y);
    }
    internal static uint Crc32(ReadOnlySpan<byte> bytes, bool packet = false)
    {
        if (!packet || bytes.Length <= 8) return System.IO.Hashing.Crc32.HashToUInt32(bytes);
        var crc = new System.IO.Hashing.Crc32();
        ReadOnlySpan<byte> checksum = [0, 0, 0, 0];
        crc.Append(bytes[..8]);
        crc.Append(checksum[..Math.Min(4, bytes.Length - 8)]);
        if (bytes.Length > 12) crc.Append(bytes[12..]);
        return crc.GetCurrentHashAsUInt32();
    }
}
