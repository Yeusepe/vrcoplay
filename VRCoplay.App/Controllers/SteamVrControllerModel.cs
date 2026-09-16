// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;
namespace VRCoplay;
internal sealed class SteamVrControllerModel(string name, ulong devicePath)
{
    private sealed record Part(string Name, PointerPose Transform);
    private readonly Queue<Part> _parts = new();
    private readonly List<Vector3> _vertices = [];
    private readonly List<int> _indices = [];
    private bool _started;
    private int _polls;
    internal ControllerMesh? Mesh { get; private set; }
    internal string? Error { get; private set; }
    internal bool Finished => Mesh is not null || Error is not null;
    internal static string BoundPoseComponent(CVRInput input, ulong action, string device)
    {
        var bindings = new InputBindingInfo_t[16]; uint count = 0;
        if (input.GetActionBindingInfo(action, bindings, (uint)Marshal.SizeOf<InputBindingInfo_t>(), ref count) != EVRInputError.None || count > bindings.Length) return "";
        var components = bindings.Take((int)count).Select(b => PoseComponent(b.rchDevicePathName, b.rchInputPathName, device))
            .Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return components.Length == 1 ? components[0] : "";
    }
    internal static string PoseComponent(string boundDevice, string inputPath, string device)
    {
        string prefix = device + "/pose/";
        string component = inputPath.StartsWith(prefix, StringComparison.Ordinal) ? inputPath[prefix.Length..] :
            boundDevice == device && inputPath.StartsWith("/pose/", StringComparison.Ordinal) ? inputPath[6..] : "";
        return component.Contains('/') ? "" : component;
    }
    internal static bool TryComponentLocal(CVRRenderModels models, string model, string component, ulong devicePath, out PointerPose pose)
    {
        pose = default;
        if (component.Length == 0) return false;
        if (component == "raw") { pose = new(Vector3.Zero, Quaternion.Identity); return true; }
        var mode = default(RenderModel_ControllerMode_State_t); var state = default(RenderModel_ComponentState_t);
        if (!models.GetComponentStateForDevicePath(model, component, devicePath, ref mode, ref state)) return false;
        pose = Pose(state.mTrackingToComponentLocal);
        return pose.Valid;
    }
    internal void Poll(CVRRenderModels models)
    {
        if (Finished) return;
        try
        {
            if (!_started)
            {
                _started = true;
                uint count = models.GetComponentCount(name);
                if (count > 64) throw new InvalidDataException("Too many controller components.");
                for (uint i = 0; i < count; i++)
                {
                    var component = new StringBuilder(1024); var model = new StringBuilder(1024);
                    if (models.GetComponentName(name, i, component, 1024) is 0 or > 1024) continue;
                    if (models.GetComponentRenderModelName(name, component.ToString(), model, 1024) is 0 or > 1024) continue;
                    var mode = default(RenderModel_ControllerMode_State_t); var state = default(RenderModel_ComponentState_t);
                    if (!models.GetComponentStateForDevicePath(name, component.ToString(), devicePath, ref mode, ref state))
                    {
                        var neutral = default(VRControllerState_t);
                        if (!models.GetComponentState(name, component.ToString(), ref neutral, ref mode, ref state))
                            throw new InvalidDataException("Controller component transform unavailable.");
                    }
                    if ((state.uProperties & (uint)EVRComponentProperty.IsVisible) == 0) continue;
                    _parts.Enqueue(new(model.ToString(), Pose(state.mTrackingToComponentRenderModel)));
                }
                if (_parts.Count == 0) _parts.Enqueue(new(name, new(Vector3.Zero, Quaternion.Identity)));
            }
            if (++_polls > 600) throw new TimeoutException("Controller model loading timed out.");
            var part = _parts.Peek(); nint native = 0;
            var result = models.LoadRenderModel_Async(part.Name, ref native);
            try
            {
                if (result == EVRRenderModelError.Loading) return;
                if (result != EVRRenderModelError.None || native == 0) throw new InvalidDataException("Controller model: " + result);
                var model = Marshal.PtrToStructure<RenderModel_t>(native);
                if (model.unVertexCount is 0 or > 200000 || model.unTriangleCount is 0 or > 200000 ||
                    model.rVertexData == 0 || model.rIndexData == 0 || _vertices.Count + model.unVertexCount > 300000)
                    throw new InvalidDataException("Invalid controller mesh size.");
                int start = _vertices.Count, stride = Marshal.SizeOf<RenderModel_Vertex_t>();
                for (int i = 0; i < model.unVertexCount; i++)
                {
                    var v = Marshal.PtrToStructure<RenderModel_Vertex_t>(model.rVertexData + i * stride).vPosition;
                    var p = part.Transform.Position + Vector3.Transform(new Vector3(v.v0, v.v1, -v.v2), part.Transform.Rotation);
                    if (!PointerPose.Finite(p) || p.Length() > 2) throw new InvalidDataException("Invalid controller vertex.");
                    _vertices.Add(p);
                }
                for (int i = 0; i < model.unTriangleCount * 3; i++)
                {
                    int index = (ushort)Marshal.ReadInt16(model.rIndexData, i * 2);
                    if (index >= model.unVertexCount) throw new InvalidDataException("Invalid controller index.");
                    _indices.Add(start + index);
                }
                _parts.Dequeue();
                if (_parts.Count == 0) Mesh = new(name, _vertices.ToArray(), _indices.ToArray());
            }
            finally { if (native != 0) models.FreeRenderModel(native); }
        }
        catch (Exception error) { Error = error.Message; }
    }
    internal static PointerPose Pose(HmdMatrix34_t m) => new(new(m.m3, m.m7, -m.m11),
        Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new(m.m0,m.m4,-m.m8,0, m.m1,m.m5,-m.m9,0,
            -m.m2,-m.m6,m.m10,0, 0,0,0,1))));
    internal static HmdMatrix34_t Matrix(PointerPose pose)
    {
        var r = Matrix4x4.CreateFromQuaternion(pose.Rotation);
        return new() { m0=r.M11,m1=r.M21,m2=-r.M31,m3=pose.Position.X,
            m4=r.M12,m5=r.M22,m6=-r.M32,m7=pose.Position.Y,
            m8=-r.M13,m9=-r.M23,m10=r.M33,m11=-pose.Position.Z };
    }
}
