// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
namespace VRCoplay;
internal enum PointerCalibrationIssue { None, Moving, NoSignal, Miss, OutsidePlane, Duplicate, InvalidGeometry }
internal readonly record struct PointerCalibrationProgress(int Step = 0, bool Retry = false,
    PointerCalibrationIssue Issue = PointerCalibrationIssue.None);
internal sealed class PointerCalibration
{
    internal const float Inset = .08f;
    internal static Vector2 Target(int index) => index switch
    {
        0 => new(1 - Inset, 1 - Inset),
        1 => new(Inset, 1 - Inset),
        2 => new(Inset, Inset),
        3 => new(1 - Inset, Inset),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
    private readonly double[] _h;
    private readonly Vector2 _center;
    private readonly double _scale;
    private PointerCalibration(double[] h, Vector2 center, double scale) => (_h, _center, _scale) = (h, center, scale);
    internal Vector2 Map(Vector2 point)
    {
        double x = (point.X - (double)_center.X) * _scale, y = (point.Y - (double)_center.Y) * _scale;
        var d = _h[6] * x + _h[7] * y + 1;
        if (!double.IsFinite(d) || Math.Abs(d) < 1e-8) return new(float.NaN);
        return new((float)((_h[0] * x + _h[1] * y + _h[2]) / d),
            (float)((_h[3] * x + _h[4] * y + _h[5]) / d));
    }
    internal static bool TryCreate(ReadOnlySpan<Vector2> points, out PointerCalibration? calibration)
    {
        calibration = null;
        if (points.Length != 4) return false;
        double winding = 0;
        for (int i = 0; i < 4; i++)
        {
            var p = points[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) return false;
            var a = points[(i + 1) % 4] - p;
            var b = points[(i + 2) % 4] - points[(i + 1) % 4];
            double cross = (double)a.X * b.Y - (double)a.Y * b.X;
            if (a.LengthSquared() < .000025f || Math.Abs(cross) < .000025 ||
                i > 0 && cross * winding <= 0) return false;
            winding = cross;
        }
        var center = (points[0] + points[1] + points[2] + points[3]) / 4;
        double meanDistance = 0;
        foreach (var point in points) meanDistance += Vector2.Distance(point, center) / 4d;
        double normalization = Math.Sqrt(2) / meanDistance;
        var matrix = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = (points[i].X - (double)center.X) * normalization, y = (points[i].Y - (double)center.Y) * normalization;
            var uv = Target(i);
            int r = i * 2;
            matrix[r, 0] = x; matrix[r, 1] = y; matrix[r, 2] = 1;
            matrix[r, 6] = -uv.X * x; matrix[r, 7] = -uv.X * y; matrix[r, 8] = uv.X;
            matrix[r + 1, 3] = x; matrix[r + 1, 4] = y; matrix[r + 1, 5] = 1;
            matrix[r + 1, 6] = -uv.Y * x; matrix[r + 1, 7] = -uv.Y * y; matrix[r + 1, 8] = uv.Y;
        }
        if (!LinearSolve.Solve(matrix, out var h)) return false;
        double sign = 0;
        foreach (var uv in new[] { Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY })
        {
            double d = (h[3] * h[7] - h[4] * h[6]) * uv.X +
                (h[1] * h[6] - h[0] * h[7]) * uv.Y + h[0] * h[4] - h[1] * h[3];
            if (!double.IsFinite(d) || Math.Abs(d) < 1e-8 || sign != 0 && sign * d <= 0) return false;
            sign = d;
        }
        var result = new PointerCalibration(h, center, normalization);
        for (int i = 0; i < 4; i++)
            if (Vector2.Distance(result.Map(points[i]), Target(i)) > .0001f) return false;
        calibration = result;
        return true;
    }
}
internal sealed class PointerSamples
{
    private readonly Queue<(Vector2 Point, double Time)> _samples = new();
    internal void Clear() => _samples.Clear();
    internal void Add(Vector2 point, double seconds)
    {
        _samples.Enqueue((point, seconds));
        while (_samples.Count > 0 && seconds - _samples.Peek().Time > .12) _samples.Dequeue();
    }
    internal bool TryRead(double seconds, out Vector2 point)
    {
        point = default;
        if (_samples.Count < 4 || seconds - _samples.Last().Time > .1 ||
            _samples.Last().Time - _samples.Peek().Time < .06) return false;
        foreach (var sample in _samples) point += sample.Point;
        point /= _samples.Count;
        var mean = point;
        return _samples.All(s => Vector2.DistanceSquared(s.Point, mean) <= .000004f);
    }
}
