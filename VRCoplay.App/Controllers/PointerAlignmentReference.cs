// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
namespace VRCoplay;
internal readonly record struct PointerAlignmentReference(PointerAlignmentFit Baseline, PointerPose Correction)
{
    internal bool Valid => Baseline.Offset.Valid && float.IsFinite(Baseline.Scale) && Baseline.Scale is >= .05f and <= 20f &&
        Correction.Valid && Correction.Position.Length() <= .6f;
    internal PointerPose Apply(PointerAlignmentFit fit) => new(
        fit.Offset.Position + Vector3.Transform(Correction.Position * fit.Scale, fit.Offset.Rotation),
        Quaternion.Normalize(fit.Offset.Rotation * Correction.Rotation));
    internal PointerAlignmentReference Observe(float[] parameters)
    {
        if (parameters.Length != 8 || parameters.Any(p => !float.IsFinite(p) || p is < 0 or > 1)) return this;
        var position = new Vector3(parameters[0], parameters[1], parameters[2]) - new Vector3(.5f);
        var angles = (new Vector3(parameters[3], parameters[4], parameters[5]) - new Vector3(.5f)) * (2 * MathF.PI);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angles.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, angles.X) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angles.Z);
        var inverse = Quaternion.Conjugate(Baseline.Offset.Rotation);
        return this with { Correction = new(Vector3.Transform(position - Baseline.Offset.Position, inverse) / Baseline.Scale,
            Quaternion.Normalize(inverse * rotation)) };
    }
}
