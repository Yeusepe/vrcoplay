// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
namespace VRCoplay;
internal readonly record struct PointerAlignmentMeasurement(PointerAlignmentSample Sample, int Readings,
    float HandPositionNoise, float TipPositionNoise, float HandAngleNoise, float TipAngleNoise);
internal sealed class PointerAlignmentCapture
{
    internal const double SettleSeconds = .2, MeasureSeconds = .45;
    internal const double HoldSeconds = SettleSeconds + MeasureSeconds;
    private readonly List<(double Time, PointerAlignmentSample Sample)> _readings = [];
    private PointerAlignmentSample? _baseline;
    private double _started, _last = double.NegativeInfinity;
    internal float Progress { get; private set; }
    internal void Clear()
    {
        _readings.Clear(); _baseline = null; _last = double.NegativeInfinity; Progress = 0;
    }
    internal bool Add(PointerAlignmentSample sample, double time, out PointerAlignmentMeasurement measurement)
    {
        measurement = default;
        if (!sample.Hand.Valid || !sample.Tip.Valid || !double.IsFinite(time)) { Clear(); return false; }
        if (time <= _last) return false;
        if (time - _last > .15) Clear();
        _last = time;
        if (_baseline is not { } baseline || Vector3.Distance(baseline.Tip.Position, sample.Tip.Position) > .005f ||
            Vector3.Distance(baseline.Hand.Position, sample.Hand.Position) > .03f ||
            PointerPose.Angle(baseline.Tip.Rotation, sample.Tip.Rotation) > .025f ||
            PointerPose.Angle(baseline.Hand.Rotation, sample.Hand.Rotation) > .025f)
        {
            _readings.Clear(); _baseline = sample; _started = time;
        }
        Progress = Math.Clamp((float)((time - _started) / HoldSeconds), 0, .95f);
        if (time - _started < SettleSeconds) return false;
        _readings.Add((time, sample));
        while (_readings.Count > 8 && _readings[0].Time < time - MeasureSeconds - .025) _readings.RemoveAt(0);
        if (time - _started < HoldSeconds || _readings.Count < 8 || _readings[^1].Time - _readings[0].Time < .35) return false;
        var values = _readings.Select(r => r.Sample).ToArray();
        var center = Mean(values);
        float[] Deviations(Func<PointerAlignmentSample, float> error) => values.Select(error).Order().ToArray();
        float Limit(float[] deviations, float floor) => Math.Max(floor, deviations[deviations.Length / 2] * 3);
        float hp = Limit(Deviations(p => Vector3.Distance(p.Hand.Position, center.Hand.Position)), .0005f);
        float tp = Limit(Deviations(p => Vector3.Distance(p.Tip.Position, center.Tip.Position)), .0005f);
        float ha = Limit(Deviations(p => PointerPose.Angle(p.Hand.Rotation, center.Hand.Rotation)), .0015f);
        float ta = Limit(Deviations(p => PointerPose.Angle(p.Tip.Rotation, center.Tip.Rotation)), .0015f);
        var accepted = values.Where(p => Vector3.Distance(p.Hand.Position, center.Hand.Position) <= hp &&
            Vector3.Distance(p.Tip.Position, center.Tip.Position) <= tp &&
            PointerPose.Angle(p.Hand.Rotation, center.Hand.Rotation) <= ha &&
            PointerPose.Angle(p.Tip.Rotation, center.Tip.Rotation) <= ta).ToArray();
        if (accepted.Length < 8 || accepted.Length < values.Length * .8) return false;
        var first = Mean(values.Take(values.Length / 2).ToArray());
        var last = Mean(values.Skip(values.Length / 2).ToArray());
        float handSpread = MathF.Sqrt(values.Average(p => Vector3.DistanceSquared(p.Hand.Position, center.Hand.Position)));
        if (Vector3.Distance(first.Tip.Position, last.Tip.Position) > .0008f ||
            Vector3.Distance(first.Hand.Position, last.Hand.Position) > Math.Max(.0003f, handSpread * .8f) ||
            PointerPose.Angle(first.Tip.Rotation, last.Tip.Rotation) > .0035f ||
            PointerPose.Angle(first.Hand.Rotation, last.Hand.Rotation) > .0035f) return false;
        center = Mean(accepted);
        float Rms(Func<PointerAlignmentSample, float> error) => MathF.Sqrt(accepted.Average(p => MathF.Pow(error(p), 2)));
        measurement = new(center, accepted.Length,
            Rms(p => Vector3.Distance(p.Hand.Position, center.Hand.Position)), Rms(p => Vector3.Distance(p.Tip.Position, center.Tip.Position)),
            Rms(p => PointerPose.Angle(p.Hand.Rotation, center.Hand.Rotation)), Rms(p => PointerPose.Angle(p.Tip.Rotation, center.Tip.Rotation)));
        if (measurement.TipPositionNoise > .0015f || measurement.HandAngleNoise > .005f || measurement.TipAngleNoise > .005f) return false;
        return true;
    }
    private static PointerAlignmentSample Mean(PointerAlignmentSample[] samples) => new(
        MeanPose(samples.Select(p => p.Hand).ToArray()), MeanPose(samples.Select(p => p.Tip).ToArray()));
    private static PointerPose MeanPose(PointerPose[] poses) => new(poses.Aggregate(Vector3.Zero, (sum, p) => sum + p.Position) / poses.Length,
        PointerAlignmentSolver.Average(poses.Select(p => p.Rotation).ToArray()));
}
