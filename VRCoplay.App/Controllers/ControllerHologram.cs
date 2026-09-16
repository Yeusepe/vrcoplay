// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
namespace VRCoplay;
internal sealed record ControllerMesh(string Name, Vector3[] Vertices, int[] Indices)
{
    internal Vector3 Center { get; } = (Vertices.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min) +
        Vertices.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max)) / 2;
}
internal sealed record ControllerHologramScene(ControllerMesh Mesh, PointerPose Current, PointerPose Target,
    bool Matched, float Hold, int Captures, bool Tracking = true);
internal sealed class ControllerHologramGuide
{
    private readonly PointerPose _basis;
    private readonly Quaternion _headYaw;
    private readonly float _hand;
    internal ControllerHologramGuide(PointerPose head, bool leftHanded)
    {
        _hand = leftHanded ? -1 : 1;
        var forward = Vector3.Transform(Vector3.UnitZ, head.Rotation); forward.Y = 0;
        _headYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(forward.X, forward.Z));
        var relative = new Vector3(.13f * _hand,-.23f,.32f);
        _basis = new(head.Position + Vector3.Transform(relative,_headYaw),
            Quaternion.Normalize(_headYaw * Quaternion.CreateFromAxisAngle(Vector3.UnitX,.12f)));
    }
    internal PointerPose Target(int captured)
    {
        var (position, angles) = Math.Clamp(captured, 0, 3) switch {
            0 => (Vector3.Zero, Vector3.Zero),
            1 => (new Vector3(-.04f * _hand, .025f, -.03f), new Vector3(-.52f, .12f, .08f)),
            2 => (new Vector3(.035f * _hand, -.02f, .025f), new Vector3(.10f, -.55f * _hand, -.1f)),
            _ => (new Vector3(-.025f * _hand, .025f, .045f), new Vector3(.35f, .38f * _hand, .45f * _hand)),
        };
        return new(_basis.Position + Vector3.Transform(position, _headYaw),
            Quaternion.Normalize(_basis.Rotation * Quaternion.CreateFromYawPitchRoll(angles.Y, angles.X, angles.Z)));
    }
    internal static bool Matches(PointerPose actual, PointerPose target) => actual.Valid && target.Valid &&
        Vector3.Distance(actual.Position, target.Position) <= .04f && PointerPose.Angle(actual.Rotation, target.Rotation) <= .21f;
    internal static PointerPose Compose(PointerPose a, PointerPose b) => new(a.Position + Vector3.Transform(b.Position, a.Rotation), Quaternion.Normalize(a.Rotation * b.Rotation));
    internal static PointerPose Inverse(PointerPose a) => new(Vector3.Transform(-a.Position, Quaternion.Conjugate(a.Rotation)), Quaternion.Conjugate(a.Rotation));
}
