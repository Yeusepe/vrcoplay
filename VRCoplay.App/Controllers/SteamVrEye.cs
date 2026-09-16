// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using Valve.VR;
namespace VRCoplay;
internal readonly record struct SteamVrEye(HmdMatrix34_t Camera, Matrix4x4 ViewProjection)
{
    internal static SteamVrEye ThroughSurface(Matrix4x4 eyeToWorld, SteamVrSurface surface)
    {
        Matrix4x4.Invert(surface.Transform, out var worldToSurface);
        var eye = Vector3.Transform(eyeToWorld.Translation, worldToSurface);
        var camera = surface.Transform; camera.Translation = eyeToWorld.Translation;
        Matrix4x4.Invert(camera, out var view);
        const float near = .01f;
        float k = near / eye.Z, half = surface.Width / 2;
        var projection = Matrix4x4.CreatePerspectiveOffCenter(
            (-half-eye.X)*k,(half-eye.X)*k,(-half-eye.Y)*k,(half-eye.Y)*k,near,20);
        return new(Native(camera),view*projection);
    }
    internal static Matrix4x4 Matrix(HmdMatrix34_t m) => new(m.m0,m.m4,m.m8,0, m.m1,m.m5,m.m9,0,
        m.m2,m.m6,m.m10,0, m.m3,m.m7,m.m11,1);
    internal static HmdMatrix34_t Native(Matrix4x4 m) => new() { m0=m.M11,m1=m.M21,m2=m.M31,m3=m.M41,
        m4=m.M12,m5=m.M22,m6=m.M32,m7=m.M42,m8=m.M13,m9=m.M23,m10=m.M33,m11=m.M43 };
}
internal readonly record struct SteamVrSurface(Matrix4x4 Transform, float Width)
{
    internal static SteamVrSurface Around(Vector3 center, float radius, Vector3 head)
    {
        var toHead = head-center;
        float distance=toHead.Length();
        if(distance<radius+.08f) return default;
        var z=toHead/distance;
        var x=Vector3.Normalize(Vector3.Cross(Math.Abs(z.Y)>.98f?Vector3.UnitZ:Vector3.UnitY,z));
        var y=Vector3.Cross(z,x);
        var transform=new Matrix4x4(x.X,x.Y,x.Z,0, y.X,y.Y,y.Z,0, z.X,z.Y,z.Z,0, center.X,center.Y,center.Z,1);
        return new(transform,2*(radius+.04f)*distance/(distance-radius));
    }
}
