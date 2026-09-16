// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;
namespace VRCoplay;
internal sealed partial class VrControllerBridge
{
    private ulong _alignmentTipAction;
    private uint _alignmentController = OpenVR.k_unTrackedDeviceIndexInvalid;
    private string _alignmentControllerId = "";
    private string _alignmentModelName = "";
    private readonly VrControllerHologram? _hologram;
    private ControllerHologramGuide? _hologramGuide;
    private PointerPose _hologramTipToRaw;
    private ulong _hologramDevicePath;
    private long _hologramMatchAt;
    private int _hologramGuideVersion;
    private ulong _alignmentComponentOrigin;
    private string _alignmentComponent = "";
    private long _alignmentComponentCheckedAt;
    internal ControllerHologramScene? ControllerHologram => _hologram?.Scene;
    private static readonly uint AlignmentPoseSize = (uint)Marshal.SizeOf<InputPoseActionData_t>();
    internal PointerAlignmentStatus PointerAlignment => _pointer?.Current?.Alignment ?? default;
    internal void AlignPointer() => _pointer?.Current?.StartAlignment();
    internal void SkipPointerAlignment() => _pointer?.Current?.SkipAlignment();
    internal void ResetPointerAlignment() => _pointer?.Current?.ResetAlignment();
    internal void TrimPointer(float x, float y) => _pointer?.Current?.SetAlignmentTrim(x, y);
    private void PollAlignment(CVRInput input, TrackedDevicePose_t[] poses)
    {
        var pointer = _pointer?.Current;
        if (pointer?.Alignment.Available != true)
        { _hologram?.Update(null); _hologramGuide = null; _hologramMatchAt = 0; return; }
        uint index = _system!.GetTrackedDeviceIndexForControllerRole(_layout.MainRole);
        if (index != OpenVR.k_unTrackedDeviceIndexInvalid && (index != _alignmentController || _alignmentModelName.Length == 0))
        {
            if (index != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                string Property(ETrackedDeviceProperty property)
                {
                    var text = new StringBuilder(256); var propertyError = ETrackedPropertyError.TrackedProp_Success;
                    _system.GetStringTrackedDeviceProperty(index, property, text, 256, ref propertyError);
                    return propertyError == ETrackedPropertyError.TrackedProp_Success ? text.ToString() : "";
                }
                string model = Property(ETrackedDeviceProperty.Prop_RenderModelName_String);
                string serial = Property(ETrackedDeviceProperty.Prop_SerialNumber_String);
                if (model.Length > 0 && serial.Length > 0)
                {
                    string identity = serial + ":" + model;
                    if (_alignmentControllerId.Length > 0 && _alignmentControllerId != identity)
                    { _hologramGuide = null; _hologramMatchAt = 0; }
                    _alignmentController = index; _alignmentModelName = model; _alignmentControllerId = identity;
                    input.GetInputSourceHandle(_layout.LeftHanded ? "/user/hand/left" : "/user/hand/right", ref _hologramDevicePath);
                }
            }
        }
        var status = pointer.Alignment;
        var data = default(InputPoseActionData_t);
        var error = _alignmentTipAction == 0 ? EVRInputError.InvalidHandle : input.GetPoseActionDataRelativeToNow(_alignmentTipAction,
            ETrackingUniverseOrigin.TrackingUniverseStanding, 0, ref data, AlignmentPoseSize, _hologramDevicePath);
        PointerPose? pose = error == EVRInputError.None && data.bActive && data.pose.bDeviceIsConnected && data.pose.bPoseIsValid &&
            data.pose.eTrackingResult == ETrackingResult.Running_OK ? SteamVrControllerModel.Pose(data.pose.mDeviceToAbsoluteTracking) : null;
        PointerPose? rawToTip = null;
        string reference = _alignmentModelName + ": action pose (component unavailable)";
        if (status.Phase is 1 or 2 && pose is not null && index < poses.Length && poses[index].bPoseIsValid && poses[index].bDeviceIsConnected &&
            poses[index].eTrackingResult == ETrackingResult.Running_OK)
        {
            if (_alignmentComponentOrigin != data.activeOrigin || status.GuideVersion != _hologramGuideVersion ||
                _alignmentComponent.Length == 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_alignmentComponentCheckedAt).TotalSeconds > .5)
            {
                _alignmentComponentOrigin = data.activeOrigin;
                _alignmentComponentCheckedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                _alignmentComponent = SteamVrControllerModel.BoundPoseComponent(input, _alignmentTipAction, _layout.LeftHanded ? "/user/hand/left" : "/user/hand/right");
            }
            var origin = default(InputOriginInfo_t);
            var originError = input.GetOriginTrackedDeviceInfo(data.activeOrigin, ref origin, (uint)Marshal.SizeOf<InputOriginInfo_t>());
            if (originError == EVRInputError.None && origin.trackedDeviceIndex != index) pose = null;
            else
            {
                if (SteamVrControllerModel.TryComponentLocal(OpenVR.RenderModels, _alignmentModelName,
                    _alignmentComponent.Length > 0 ? _alignmentComponent : origin.rchRenderModelComponentName, _hologramDevicePath, out var local))
                {
                    rawToTip = local;
                    reference = _alignmentModelName + ": " + (_alignmentComponent.Length > 0 ? _alignmentComponent : origin.rchRenderModelComponentName) + " (SDK component)";
                    pose = ControllerHologramGuide.Compose(SteamVrControllerModel.Pose(poses[index].mDeviceToAbsoluteTracking), local);
                }
            }
        }
        pointer.AlignmentReference(reference);
        if (status.GuideVersion != _hologramGuideVersion)
        {
            _hologramGuideVersion=status.GuideVersion;
            _hologramGuide=null; _hologramMatchAt=0; _hologram?.Update(null);
        }
        bool matched = false;
        if (status.Phase is 1 or 2 && pose is { Valid: true } tip && index < poses.Length &&
            poses[index].bPoseIsValid && poses[index].bDeviceIsConnected && poses[0].bPoseIsValid && poses[0].bDeviceIsConnected && _alignmentModelName.Length > 0)
        {
            var raw = SteamVrControllerModel.Pose(poses[index].mDeviceToAbsoluteTracking);
            var head = SteamVrControllerModel.Pose(poses[0].mDeviceToAbsoluteTracking);
            if (!raw.Valid || !head.Valid)
            {
                _hologram?.TrackingLost(); _hologramMatchAt = 0;
                pointer.HologramGuidance(false, false, false, tracking: false);
                pointer.TrackAlignment(null, _alignmentControllerId, false); return;
            }
            if (_hologramGuide is null)
            {
                _hologramGuide = new(head, _layout.LeftHanded);
                _hologramTipToRaw = rawToTip is { } local ? ControllerHologramGuide.Inverse(local) :
                    ControllerHologramGuide.Compose(ControllerHologramGuide.Inverse(tip), raw);
            }
            var targetTip = _hologramGuide.Target(status.Poses);
            bool modelReady = _hologram?.Scene?.Mesh.Name == _alignmentModelName && _hologram.Error is null;
            matched = status.Phase is 1 or 2 && modelReady && ControllerHologramGuide.Matches(tip, targetTip);
            if (!matched) _hologramMatchAt = 0;
            else if (_hologramMatchAt == 0) _hologramMatchAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (status.Phase == 1 && matched && System.Diagnostics.Stopwatch.GetElapsedTime(_hologramMatchAt).TotalSeconds >= .2)
                pointer.StartAlignment(automatic: true);
            float hold = matched ? status.Progress : 0;
            _hologram?.Update(new(_alignmentModelName, _hologramDevicePath,
                ControllerHologramGuide.Compose(targetTip, _hologramTipToRaw), matched, hold, status.Poses, DeviceIndex:index));
            pointer.HologramGuidance(modelReady, matched, _hologram?.Error is not null);
        }
        else
        {
            _hologramMatchAt = 0;
            if (status.Phase is not (1 or 2))
            { _hologram?.Update(null); _hologramGuide = null; }
            else
            {
                _hologram?.TrackingLost();
                pointer.HologramGuidance(false, false, false, tracking: false);
            }
        }
        pointer.TrackAlignment(pose, _alignmentControllerId, matched);
    }
}
