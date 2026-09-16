// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathVector = MathNet.Numerics.LinearAlgebra.Vector<double>;
namespace VRCoplay;
internal readonly record struct PointerPose(Vector3 Position, Quaternion Rotation)
{
    internal bool Valid => Finite(Position) && float.IsFinite(Rotation.LengthSquared()) && Math.Abs(Rotation.LengthSquared() - 1) < .01f;
    internal static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    internal static float Angle(Quaternion a, Quaternion b)
    {
        var delta = Quaternion.Conjugate(a) * b;
        return 2 * MathF.Atan2(new Vector3(delta.X, delta.Y, delta.Z).Length(), Math.Abs(delta.W));
    }
    internal static bool TryMarkers(Vector3 origin, Vector3 right, Vector3 up, out PointerPose pose)
    {
        pose = default;
        if (!Finite(origin) || !Finite(right) || !Finite(up)) return false;
        var x = right - origin; var y = up - origin;
        if (Math.Abs(x.Length() - .25f) > .003f || Math.Abs(y.Length() - .25f) > .003f) return false;
        x = Vector3.Normalize(x); y = Vector3.Normalize(y);
        if (Math.Abs(Vector3.Dot(x, y)) > .02f) return false;
        var z = Vector3.Normalize(Vector3.Cross(x, y)); y = Vector3.Cross(z, x);
        pose = new(origin, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new(
            x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1))));
        return pose.Valid;
    }
    internal static bool TryContactMarkers(Vector3 origin, Vector3 right, Vector3 up, out PointerPose pose)
    {
        pose = default;
        float scale = (Vector3.Distance(origin, right) + Vector3.Distance(origin, up)) / .5f;
        if (!float.IsFinite(scale) || scale is < .98f or > 20f) return false;
        var radius = new Vector3(.0001f);
        return TryMarkers(origin / scale - radius, right / scale - radius, up / scale - radius, out pose);
    }
}
internal readonly record struct PointerAlignmentSample(PointerPose Hand, PointerPose Tip);
internal readonly record struct PointerAlignmentFit(PointerPose Offset, float Scale, float PositionError, float AngleError,
    float OriginSpread = 0, float AngleSpread = 0, float ScaleSpread = 0);
internal static class PointerAlignmentSolver
{
    internal const float MaximumPositionError = .003f, MaximumOriginSpread = .004f;
    internal const float MaximumAngleError = .0105f, MaximumScaleSpread = .015f;
    private readonly record struct Model(PointerAlignmentFit Fit, Quaternion World, Vector3 Origin)
    {
        internal (float Position, float Angle) Error(PointerAlignmentSample p) => (
            Vector3.Distance(p.Hand.Position + Vector3.Transform(Fit.Offset.Position, p.Hand.Rotation),
                Origin + Fit.Scale * Vector3.Transform(p.Tip.Position, World)) / Fit.Scale,
            PointerPose.Angle(p.Hand.Rotation * Fit.Offset.Rotation, World * p.Tip.Rotation));
    }
    internal static bool TrySolve(IReadOnlyList<PointerAlignmentSample> samples, out PointerAlignmentFit fit, out string issue)
    {
        fit = default;
        issue = "More independent measurements are needed.";
        if (samples.Count < 4 || samples.Any(p => !p.Hand.Valid || !p.Tip.Valid)) return false;
        Model best = default; int[] inliers = []; float bestError = float.MaxValue;
        var all = samples.ToArray();
        var random = new Random(374);
        for (int attempt = 0; attempt < 96; attempt++)
        {
            var subset = attempt == 0 ? all : all.OrderBy(_ => random.Next()).Take(3).ToArray();
            if (!TryFit(subset, out var candidate)) continue;
            var errors = all.Select(candidate.Error).ToArray();
            var good = Enumerable.Range(0, all.Length).Where(i => errors[i].Position <= MaximumPositionError && errors[i].Angle <= MaximumAngleError).ToArray();
            float error = good.Sum(i => errors[i].Position + errors[i].Angle * .1f);
            if (good.Length > inliers.Length || good.Length == inliers.Length && error < bestError)
                (best, inliers, bestError) = (candidate, good, error);
        }
        if (inliers.Length < Math.Max(4, (int)Math.Ceiling(all.Length * .75))) return false;
        var accepted = inliers.Select(i => all[i]).ToArray();
        if (!TryFit(accepted, out best)) return false;
        int verified = 0, unconstrained = 0;
        float originSpread = 0, angleSpread = 0, scaleSpread = 0;
        for (int i = 0; i < accepted.Length; i++)
        {
            if (!TryFit(accepted.Where((_, j) => i != j).ToArray(), out var check)) { unconstrained++; continue; }
            var error = check.Error(accepted[i]);
            float originDifference = Vector3.Distance(check.Fit.Offset.Position, best.Fit.Offset.Position) / best.Fit.Scale;
            float angleDifference = PointerPose.Angle(check.Fit.Offset.Rotation, best.Fit.Offset.Rotation);
            float scaleDifference = Math.Abs(check.Fit.Scale / best.Fit.Scale - 1);
            originSpread = Math.Max(originSpread, originDifference);
            angleSpread = Math.Max(angleSpread, angleDifference);
            scaleSpread = Math.Max(scaleSpread, scaleDifference);
            if (error.Position > MaximumPositionError || error.Angle > MaximumAngleError ||
                originDifference > MaximumOriginSpread || angleDifference > MaximumAngleError || scaleDifference > MaximumScaleSpread) continue;
            verified++;
        }
        if (verified < 3 || verified + unconstrained != accepted.Length)
        { issue = $"The controller offset is not yet stable: {verified}/{accepted.Length} independent checks, {unconstrained} unconstrained; origin spread {originSpread * 1000:F2} mm, angle spread {angleSpread * 180 / MathF.PI:F3} degrees, scale spread {scaleSpread:P2}."; return false; }
        var residuals = accepted.Select(best.Error).ToArray();
        float position = residuals.Max(e => e.Position), angle = residuals.Max(e => e.Angle);
        if (position > MaximumPositionError || angle > MaximumAngleError) return false;
        fit = best.Fit with { PositionError = position, AngleError = angle,
            OriginSpread = originSpread, AngleSpread = angleSpread, ScaleSpread = scaleSpread };
        issue = "";
        return true;
    }
    private static bool TryFit(PointerAlignmentSample[] train, out Model model)
    {
        model = default;
        if (train.Length < 3) return false;
        var normal = new double[4, 4];
        for (int i = 0; i < train.Length; i++) for (int j = i + 1; j < train.Length; j++)
        {
            var a = Quaternion.Normalize(Quaternion.Conjugate(train[j].Hand.Rotation) * train[i].Hand.Rotation);
            var b = Quaternion.Normalize(Quaternion.Conjugate(train[j].Tip.Rotation) * train[i].Tip.Rotation);
            if (a.W < 0) a = -a; if (b.W < 0) b = -b;
            if (PointerPose.Angle(a, Quaternion.Identity) < .15f) continue;
            var columns = new Quaternion[4];
            for (int k = 0; k < 4; k++)
            {
                var basis = k switch { 0 => new Quaternion(1, 0, 0, 0), 1 => new(0, 1, 0, 0), 2 => new(0, 0, 1, 0), _ => new(0, 0, 0, 1) };
                columns[k] = a * basis - basis * b;
            }
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) normal[r, c] += Quaternion.Dot(columns[r], columns[c]);
        }
        Eigen(normal, out var eigenvalues, out var eigenvectors);
        var order = Enumerable.Range(0, 4).OrderBy(i => eigenvalues[i]).ToArray();
        if (eigenvalues[order[1]] < .02 || eigenvalues[order[1]] < eigenvalues[order[3]] * .025) return false;
        var rotation = Quaternion.Normalize(new((float)eigenvectors[0, order[0]], (float)eigenvectors[1, order[0]],
            (float)eigenvectors[2, order[0]], (float)eigenvectors[3, order[0]]));
        var worldRotations = train.Select(p => Quaternion.Normalize(p.Hand.Rotation * rotation * Quaternion.Conjugate(p.Tip.Rotation))).ToArray();
        var world = Average(worldRotations);
        var meanHand = train.Aggregate(Vector3.Zero, (sum, p) => sum + p.Hand.Position) / train.Length;
        var meanTip = train.Aggregate(Vector3.Zero, (sum, p) => sum + p.Tip.Position) / train.Length;
        var meanAxes = new Vector3[3];
        for (int k = 0; k < 3; k++) meanAxes[k] = train.Aggregate(Vector3.Zero, (sum, p) => sum + Vector3.Transform(Axis(k), p.Hand.Rotation)) / train.Length;
        normal = new double[4, 4]; var rhs = new double[4];
        foreach (var p in train)
        {
            var columns = new Vector3[4];
            for (int k = 0; k < 3; k++) columns[k] = Vector3.Transform(Axis(k), p.Hand.Rotation) - meanAxes[k];
            columns[3] = -Vector3.Transform(p.Tip.Position - meanTip, world);
            var target = -(p.Hand.Position - meanHand);
            for (int r = 0; r < 4; r++)
            {
                rhs[r] += Vector3.Dot(columns[r], target);
                for (int c = 0; c < 4; c++) normal[r, c] += Vector3.Dot(columns[r], columns[c]);
            }
        }
        Eigen(normal, out eigenvalues, out _);
        if (eigenvalues.Min() < 1e-5 || eigenvalues.Min() < eigenvalues.Max() * 1e-5 || !Solve(normal, rhs, out var solution)) return false;
        var offset = new Vector3((float)solution[0], (float)solution[1], (float)solution[2]);
        float scale = (float)solution[3];
        if (!float.IsFinite(scale) || scale is < .05f or > 20f || !PointerPose.Finite(offset) || offset.Length() / scale > .6f ||
            Math.Max(Math.Abs(offset.X), Math.Max(Math.Abs(offset.Y), Math.Abs(offset.Z))) > .5f) return false;
        var translation = meanHand + meanAxes[0] * offset.X + meanAxes[1] * offset.Y + meanAxes[2] * offset.Z - scale * Vector3.Transform(meanTip, world);
        model = new(new(new(offset, rotation), scale, 0, 0), world, translation);
        return true;
    }
    internal static Quaternion Average(IReadOnlyList<Quaternion> values)
    {
        var sum = new Quaternion(0, 0, 0, 0); var first = values[0];
        foreach (var value in values) sum += Quaternion.Dot(first, value) < 0 ? -value : value;
        return Quaternion.Normalize(sum);
    }
    private static Vector3 Axis(int k) => k switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
    private static bool Solve(double[,] a, double[] b, out double[] x)
    {
        var m = new double[4, 5];
        for (int r = 0; r < 4; r++) { for (int c = 0; c < 4; c++) m[r, c] = a[r, c]; m[r, 4] = b[r]; }
        return LinearSolve.Solve(m, out x);
    }
    private static void Eigen(double[,] input, out double[] values, out double[,] vectors)
    {
        var decomposition = Matrix<double>.Build.DenseOfArray(input).Evd(Symmetricity.Symmetric);
        values = decomposition.EigenValues.Select(value => value.Real).ToArray();
        vectors = decomposition.EigenVectors.ToArray();
    }
    internal static Vector3 Euler(Quaternion q)
    {
        var right = Vector3.Transform(Vector3.UnitX, q); var up = Vector3.Transform(Vector3.UnitY, q); var forward = Vector3.Transform(Vector3.UnitZ, q);
        float x = MathF.Asin(Math.Clamp(-forward.Y, -1, 1));
        float y = Math.Abs(MathF.Cos(x)) > .0001f ? MathF.Atan2(forward.X, forward.Z) : MathF.Atan2(-right.Z, right.X);
        float z = Math.Abs(MathF.Cos(x)) > .0001f ? MathF.Atan2(right.Y, up.Y) : 0;
        return new Vector3(x, y, z) * (180 / MathF.PI);
    }
}
internal static class LinearSolve
{
    internal static bool Solve(double[,] matrix, out double[] solution)
    {
        var size = matrix.GetLength(0);
        var coefficients = new double[size, size];
        for (var row = 0; row < size; row++)
            for (var column = 0; column < size; column++)
                coefficients[row, column] = matrix[row, column];
        solution = new double[size];
        try
        {
            var factors = Matrix<double>.Build.DenseOfArray(coefficients).LU();
            if (factors.U.Diagonal().Any(value => Math.Abs(value) < 1e-10)) return false;
            solution = factors.Solve(MathVector.Build.Dense(size, row => matrix[row, size])).ToArray();
            return solution.All(double.IsFinite);
        }
        catch
        {
            return false;
        }
    }
}
